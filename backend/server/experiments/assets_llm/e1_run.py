"""E1 runner: an LLM picks a parametric template + params (templates.py), then one refinement.

    uv run --with manifold3d --with rtree --with pyrender --with scipy --with open_clip_torch \
        --index https://download.pytorch.org/whl/cpu \
        python -m server.experiments.assets_llm.e1_run [part ids...]

Methods (rows in work/assets-llm/results.jsonl):
  e1       Qwen vision: photo + name + dims + template catalogue -> template/params/colours
  e1b      one Qwen vision critique: photo + 2-view render -> param patch
  e1-text  gpt-oss-120b (text only): a short description the agent wrote from the photo -> same JSON
           (used once Groq's 200K tokens/day cap on the Qwen vision model was spent)
  e1b-num  gpt-oss-120b: numeric feedback (bbox per axis, pieces, IoU, CLIP) -> param patch
A refinement is kept unless IoU or CLIP drop by more than KEEP_*; both rows are reported.

Outputs per part in work/assets-llm/e1/<id>/: *.json (LLM text + ledger records, reused on rerun
so no call is spent twice), e1*.glb and *_sheet.jpg. Rows already in results.jsonl are not
written again. Plain exact-size boxes are scored once for comparison (e1/proxy_scores.jsonl).
"""

import argparse
import io
import json
import re
import tempfile
import time
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image

from server import part_templates as T
from server.experiments.assets_llm.groq_smoke import _photo_data_url
from server.experiments.assets_llm.llm import GROQ_TEXT_MODEL, WORK, DailyQuotaExceeded, chat
from server.experiments.assets_llm.render import contact_sheet, load_scene, render_views
from server.experiments.assets_llm.score import score_part

ROOT = WORK.parents[1]
OUT = WORK / "e1"
RESULTS = WORK / "results.jsonl"
KEEP_IOU_DROP, KEEP_CLIP_DROP = 0.05, 0.03  # E1b is kept unless it drops more than this

GEN_PROMPT = """Map the product in the photo to ONE parametric 3D template and fill its parameters.
Frame: the product's front faces the viewer. W = width (left-right), H = height (up), D = depth
(back/mounting face to front). Product: {name}. Material/finish: {material} / {finish}.
Exact size mm: W {w}, H {h}, D {d}. If the photo shows several items or accessories, model only
the one that matches this size.

Templates (param=default [range] note). Region params are [u0,u1,v0,v1] fractions of the front
face, u from the left, v from the bottom.
{catalogue}

Pick the template whose shape fits best. Set only params that differ from the defaults. Give
colors as sRGB hex taken from the photo for every visible colour role.
Reply JSON only: {{"template": "...", "params": {{...}}, "colors": {{"role": "#RRGGBB"}},
"front_face_note": "<=20 words: which side of the photo is the front and what is on it"}}"""

CRITIQUE_PROMPT = """Image 1: product photo. Image 2: our 3D model (left: front view, looking at
the front face; right: 3/4 view from front-right-above). Product: {name}. Size mm: W {w}, H {h},
D {d} (the size is fixed and correct).
Current model: {model}
Its template: {spec}
Other templates: {others}

Checklist: 1) right template? 2) main features present / missing / extra? 3) feature positions,
counts and proportions? 4) colours?
Reply JSON only: {{"template_ok": true|false, "issues": ["<=4 short items"],
"template": "<only if switching>", "params": {{"<only changed params>": ...}},
"colors": {{"<only changed roles>": "#RRGGBB"}}}}. Empty params/colors if it already matches."""

NUM_PROMPT = """You tune a parametric 3D model of a product. Product: {name}. {desc}
Size mm (fixed, correct): W {w}, H {h}, D {d}. Front faces +Z.
Current model: {model}
Template: {spec}
Automatic scores against the product photo:
- bbox error per axis W/H/D: {bbox} %; pieces {pieces}, floating {floating}
- silhouette IoU {iou} (outline overlap with the photo's product mask at the best of 7 view
  angles, best: {view}; 1 = identical); CLIP similarity {clip} (0..1, higher looks more alike)
- a plain grey box of the same size scores IoU {piou}, CLIP {pclip}
Change a few params or colours that should raise IoU and CLIP (outline, visible features,
colours). Reply JSON only: {{"reason": "<=20 words", "params": {{...changed only}},
"colors": {{...changed only}}}}"""

# Written by the agent from each photo (Read the JPEG) for parts whose Qwen vision call hit the
# daily cap. Kept to what a vision model would have said: shape, features, colours.
DESCRIPTIONS = {
    "kraus-ke1us32-ec0e5f": "Single-bowl stainless undermount kitchen sink seen from above-front."
    " Rectangular basin with near-vertical walls, small corner radii and a narrow flat rim"
    " flange; outside of the bowl is dark grey (sound-deadening coating). Round stainless drain"
    " toward the back of the floor. Bottom grid and strainer in the photo are accessories.",
    "delta-75641-d491ed": "Round chrome wall-mount shower head seen from the front. Circular"
    " face: chrome outer rim, light-grey spray face with about three rings of black rubber"
    " nozzles and a raised grey centre disc; a small side lever. The back tapers to a chrome"
    " ball joint and a short connector toward the wall.",
    "renogy-rng-320dx4-97f5a1": "Monocrystalline solar panel in portrait orientation (the photo"
    " shows a 4-pack stacked). Narrow black aluminium frame; black cells in a 6 column x 10 row"
    " grid with thin light lines between cells; glass front.",
    "whirlpool-what101-1bw-cdcda4": "White through-the-wall air conditioner, front view. Top"
    " third: black control panel with display at the top-left, two louvred outlets with"
    " vertical vanes to its right. Lower two thirds: intake grille of about 18 horizontal slats"
    " over nearly the full width. Grey metal sleeve/cabinet behind the white front.",
    "samsung-rf70f29mer-f13bd2": "Stainless 4-door French-door refrigerator, front view. Two"
    " upper doors split at the centre, about 60% of the height, with slim near-flush vertical"
    " handles at the inner edges; below them two full-width stacked drawers with horizontal"
    " handle lips; black toe-kick. Store stickers are not part of the product.",
    "costway-ghm0542-cf4a3a": "Mini-split outdoor unit (the photo also shows the indoor unit,"
    " remote and line set: model only the outdoor unit). White box housing; large round fan"
    " behind a black wire grille on the left ~65% of the front with blue blades visible; the"
    " right ~25% of the front is a plain white panel; grey valve cover on the right side.",
}


def _parse(text: str) -> dict:
    text = re.sub(r"<think>.*?</think>", "", text, flags=re.DOTALL).strip()
    text = re.sub(r"^```(?:json)?|```$", "", text.strip(), flags=re.MULTILINE).strip()
    obj = json.loads(text)
    if not isinstance(obj, dict):
        raise ValueError("not an object")  # noqa: TRY004 -- the caller retries on ValueError
    return obj


def ask(messages: list[dict], tag: str, cache: Path, model: str | None = None):
    """JSON call with one retry on invalid JSON, cached in `cache` (text + ledger records).
    Returns (obj, records), or (None, []) when the model's daily token cap is spent."""
    if cache.exists():
        c = json.loads(cache.read_text())
        return c["json"], c["records"]
    records, text = [], ""
    max_tokens = 1500 if model == GROQ_TEXT_MODEL else 800  # gpt-oss spends tokens reasoning
    for attempt in range(2):
        try:
            text, rec = chat(messages, tag=tag, model=model, max_tokens=max_tokens, json_mode=True)
        except DailyQuotaExceeded as exc:
            print(f"skip {tag}: {str(exc)[:120]}", flush=True)
            return None, []
        records.append(rec)
        try:
            obj = _parse(text)
            break
        except ValueError:  # json.JSONDecodeError is a ValueError
            obj = {}
            if attempt:
                break
    cache.write_text(json.dumps({"json": obj, "raw": text, "records": records}, indent=1))
    return obj, records


def _img_url(img: Image.Image) -> str:
    import base64

    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=85)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


def _user(text: str, *urls: str) -> list[dict]:
    content = [{"type": "text", "text": text}]
    content += [{"type": "image_url", "image_url": {"url": u}} for u in urls]
    return [{"role": "user", "content": content}]


def pieces_floating(glb: Path, tol: float = 5e-4) -> tuple[int, int]:
    """Connected components, and how many are not linked (touching/overlapping, within tol) to
    the largest one. ponytail: vertex-inside/near test only; two pieces that cross without either
    having a vertex in the other count as apart (not seen with these templates)."""
    comps = []
    for g in load_scene(glb).dump():
        g = g.copy()
        g.merge_vertices(merge_tex=True, merge_norm=True)
        comps += g.split(only_watertight=False)
    n = len(comps)
    parent = list(range(n))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    b = np.array([c.bounds for c in comps])
    for i in range(n):
        for j in range(i + 1, n):
            if find(i) == find(j):
                continue
            if np.any(b[i][0] > b[j][1] + tol) or np.any(b[j][0] > b[i][1] + tol):
                continue
            for a_, b_ in ((comps[i], comps[j]), (comps[j], comps[i])):
                if trimesh.proximity.signed_distance(b_, a_.vertices).max() > -tol:
                    parent[find(i)] = find(j)
                    break
    main = find(int(np.argmax([c.area for c in comps])))
    return n, sum(find(i) != main for i in range(n))


def evaluate(part_dir: Path, glb: Path) -> dict:
    s = score_part(part_dir, glb)
    pieces, floating = pieces_floating(glb)
    return {
        "valid": bool(s["loads"] and s["bbox_ok"] and s["origin_ok"]),
        "bbox_err_pct": round(max(abs(v) for v in s["dims_err_pct"]), 3),
        "pieces": pieces,
        "floating": floating,
        "iou": s.get("sil_iou"),
        "clip": s.get("clip_sim"),
        "triangles": s["triangles"],
        "file_kb": s["file_kb"],
        "watertight": s["watertight"],
    }


def make(part: dict, choice: dict, out: Path, name: str) -> tuple[Path, dict, float]:
    t0 = time.time()
    scene, info = T.build(
        choice.get("template"), part["dims_mm"], choice.get("params"), choice.get("colors")
    )
    glb = out / f"{name}.glb"
    scene.export(glb)
    contact_sheet(glb, out / f"{name}_sheet.jpg", part["_photo"], 384)
    return glb, info, time.time() - t0


def two_views(glb: Path) -> Image.Image:
    v = render_views(load_scene(glb), 384, {"front": (0, 0, 1), "3/4": (1.0, 0.75, 1.25)})
    img = Image.new("RGB", (768, 384))
    img.paste(Image.fromarray(v["front"][0]), (0, 0))
    img.paste(Image.fromarray(v["3/4"][0]), (384, 0))
    return img


def row(method, pid, ev, records, wall, notes, info):
    return {
        "method": method,
        "part": pid,
        **{k: ev[k] for k in ("valid", "bbox_err_pct", "pieces", "floating", "iou", "clip")},
        "calls": len(records),
        "in_tokens": sum(r["in_tokens"] or 0 for r in records),
        "out_tokens": sum(r["out_tokens"] or 0 for r in records),
        "wall_s": round(wall, 2),
        "notes": notes,
        "template": info["template"],
        "triangles": ev["triangles"],
        "file_kb": ev["file_kb"],
    }


def _gen_prompt(part: dict, size: dict, desc: str | None) -> str:
    p = GEN_PROMPT.format(
        name=part["name"],
        material=part.get("material"),
        finish=part.get("finish"),
        catalogue=T.catalogue(),
        **size,
    )
    if desc:  # text-only model: the description stands in for the photo
        p = p.replace("the product in the photo", "the product described below")
        p = p.replace("If the photo shows", "If the description mentions")
        p = p.replace("taken from the photo", "matching the description")
        p = p.replace("which side of the photo is the front", "what is on the front")
        p += f"\nDescription (written from the product photo): {desc}"
    return p


def _patched(info: dict, patch: dict) -> dict:
    return {
        "template": info["template"],
        "params": {**info["params"], **(patch.get("params") or {})},
        "colors": {**info["colors"], **(patch.get("colors") or {})},
    }


def _refine_row(method, pid, part_dir, out, name, info_a, ev_a, patch, recs_a, recs, wall_a, extra):
    """Build the patched variant, score it, and decide keep/revert against the base."""
    switch = patch.get("template") if patch.get("template") in T.TEMPLATES else None
    switch = switch if switch != info_a["template"] else None
    choice = (
        {"template": switch, "params": patch.get("params"), "colors": patch.get("colors")}
        if switch  # a new template starts from its own defaults, not the old params
        else _patched(info_a, patch)
    )
    part = json.loads((part_dir / "part.json").read_text()) | {"_photo": part_dir / "image.jpg"}
    glb, info_b, t_b = make(part, choice, out, name)
    ev_b = evaluate(part_dir, glb)
    keep = (
        ev_b["valid"]
        and (ev_b["iou"] or 0) >= (ev_a["iou"] or 0) - KEEP_IOU_DROP
        and (ev_b["clip"] or 0) >= (ev_a["clip"] or 0) - KEEP_CLIP_DROP
    )
    changed = sorted(k for k in info_b["params"] if info_b["params"][k] != info_a["params"].get(k))
    changed += sorted(
        f"color:{k}" for k in info_b["colors"] if info_b["colors"][k] != info_a["colors"].get(k)
    )
    note = f"{'kept' if keep else 'reverted'}; " + (f"switch->{switch}; " if switch else "")
    note += f"patch {','.join(changed) or 'none'}; {extra}"
    wall = wall_a + sum(r["latency_s"] for r in recs) + t_b
    return row(method, pid, ev_b, recs_a + recs, wall, note[:300], info_b)


def run_part(pid: str) -> list[dict]:
    """Vision path (e1 -> e1b) and, for parts with an agent-written description, the text path
    (e1-text -> e1b-num). e1b-num on a vision base only replays a cached call (none are new)."""
    part_dir = ROOT / "data" / "parts" / pid
    part = json.loads((part_dir / "part.json").read_text())
    part["_photo"] = part_dir / "image.jpg"
    out = OUT / pid
    out.mkdir(parents=True, exist_ok=True)
    size = {k: round(part["dims_mm"][k], 1) for k in ("w", "h", "d")}
    photo_url = _photo_data_url(part["_photo"], 512)  # fewer image tokens (200K/day cap)
    rows = []
    gen, recs = ask(
        _user(_gen_prompt(part, size, None), photo_url), f"e1/{pid}/gen", out / "gen.json"
    )
    if gen is not None:
        rows += _variants(part, part_dir, out, size, photo_url, "e1", gen, recs, None)
    if pid in DESCRIPTIONS:
        desc = DESCRIPTIONS[pid]
        gen, recs = ask(
            _user(_gen_prompt(part, size, desc)),
            f"e1/{pid}/gen-text",
            out / "gen_text.json",
            model=GROQ_TEXT_MODEL,
        )
        if gen is not None:
            rows += _variants(part, part_dir, out, size, photo_url, "e1-text", gen, recs, desc)
    return rows


def _variants(part, part_dir, out, size, photo_url, method, gen, gen_recs, desc) -> list[dict]:
    pid = part["id"]
    name = "e1" if method == "e1" else "e1t"
    glb_a, info_a, t_a = make(part, gen, out, name)
    ev_a = evaluate(part_dir, glb_a)
    wall_a = sum(r["latency_s"] for r in gen_recs) + t_a
    note_a = f"{info_a['template']}: {gen.get('front_face_note', '')}"[:160]
    rows = [row(method, pid, ev_a, gen_recs, wall_a, note_a, info_a)]
    model = json.dumps(
        {"template": info_a["template"], "params": info_a["params"], "colors": info_a["colors"]}
    )
    spec_line = next(
        line
        for line in T.catalogue().split("\n- ")
        if line.lstrip("- ").startswith(info_a["template"] + ":")
    )

    if method == "e1":  # E1b: one vision critique
        views = two_views(glb_a)
        views.save(out / "e1_views.jpg", quality=85)
        others = "; ".join(
            f"{k}: {v[1]}" for k, v in T.TEMPLATES.items() if k != info_a["template"]
        )
        prompt = CRITIQUE_PROMPT.format(
            name=part["name"], model=model, spec=spec_line, others=others, **size
        )
        crit, recs = ask(
            _user(prompt, photo_url, _img_url(views)), f"e1/{pid}/critique", out / "critique.json"
        )
        if crit is not None:
            issues = "issues: " + " | ".join(map(str, crit.get("issues", [])))
            rows.append(
                _refine_row(
                    "e1b",
                    pid,
                    part_dir,
                    out,
                    "e1b",
                    info_a,
                    ev_a,
                    crit,
                    gen_recs,
                    recs,
                    wall_a,
                    issues,
                )
            )

    # E1b-num: numeric feedback to the text model (new calls only on the text path)
    text_part = pid in DESCRIPTIONS  # num.json belongs to the text path on those parts
    num_cache = out / ("num.json" if (method == "e1-text") == text_part else "num_vision.json")
    if method == "e1" and not num_cache.exists():
        return rows
    s = score_part(part_dir, glb_a, use_clip=False)
    proxy = next(
        json.loads(line)
        for line in (OUT / "proxy_scores.jsonl").read_text().splitlines()
        if json.loads(line)["part"] == pid
    )
    prompt = NUM_PROMPT.format(
        name=part["name"],
        desc=f"Description: {desc}" if desc else f"Photo note: {gen.get('front_face_note', '')}",
        model=model,
        spec=spec_line,
        bbox="/".join(f"{v:+.1f}" for v in s["dims_err_pct"]),
        pieces=ev_a["pieces"],
        floating=ev_a["floating"],
        iou=ev_a["iou"],
        view=s.get("sil_view"),
        clip=ev_a["clip"],
        piou=proxy["iou"],
        pclip=proxy["clip"],
        **size,
    )
    patch, recs = ask(_user(prompt), f"e1/{pid}/num", num_cache, model=GROQ_TEXT_MODEL)
    if patch is not None:
        why = f"reason: {patch.get('reason', '')}"
        rows.append(
            _refine_row(
                "e1b-num",
                pid,
                part_dir,
                out,
                "e1n" if method == "e1" else "e1tn",
                info_a,
                ev_a,
                patch,
                gen_recs,
                recs,
                wall_a,
                why,
            )
        )
    return rows


def proxy_scores(ids: list[str]) -> None:
    """Plain exact-size box (production proxy_mesh) per part, for the "vs proxy" comparison."""
    from server.assets import proxy_mesh
    from server.models import Dims

    path = OUT / "proxy_scores.jsonl"
    done = (
        {json.loads(line)["part"] for line in path.read_text().splitlines()}
        if path.exists()
        else set()
    )
    with tempfile.TemporaryDirectory() as tmp, open(path, "a") as f:
        for pid in ids:
            if pid in done:
                continue
            part_dir = ROOT / "data" / "parts" / pid
            part = json.loads((part_dir / "part.json").read_text())
            glb = Path(tmp) / "p.glb"
            proxy_mesh(Dims(**part["dims_mm"]), part.get("color_hex")).export(glb)
            ev = evaluate(part_dir, glb)
            f.write(json.dumps({"part": pid, **ev}) + "\n")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("ids", nargs="*")
    a = ap.parse_args()
    ids = a.ids or [p["id"] for p in json.loads((WORK / "testset.json").read_text())]
    OUT.mkdir(parents=True, exist_ok=True)
    proxy_scores(ids)
    for pid in ids:
        lines = RESULTS.read_text().splitlines() if RESULTS.exists() else []
        done = {(r["method"], r["part"]) for r in map(json.loads, lines) if "method" in r}
        for r in run_part(pid):
            print(json.dumps(r), flush=True)
            if (r["method"], pid) not in done:
                with open(RESULTS, "a") as f:
                    f.write(json.dumps(r) + "\n")
