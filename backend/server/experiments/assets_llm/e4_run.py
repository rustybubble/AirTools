"""E4: grok-4.7 on the two gutter hangers every Groq approach loses to Hunyuan on
(docs/research/assets-llm/e4-grok.md).

    uv run --with manifold3d --with rtree --with pyrender --with scipy --with open_clip_torch \
        --index https://download.pytorch.org/whl/cpu \
        python -m server.experiments.assets_llm.e4_run [strip|scad|photo|gallery ...] [--best k]

Per hanger:
  cat     one Grok vision call, the exact prompt Qwen got in E5 (whole E1 catalogue +
          finishes); Grok picked strap_hanger on both                row e4-grok-cat
  strip   the same prompt with only the bent_strip entry            row e4-grok-strip
  scad    E2's OpenSCAD helper-library loop driven by Grok: gen, bbox feedback (if >2 %),
          one photo + 2-view render critique; one compile-error retry per part
                                                                    rows e4-grok-scad[-crit]
  photo   E5 photo projection (photo mode, E3's cached face) on the best Grok geometry
                                                                    row e4-grok-best-photo
  gallery photo | Hunyuan | E5 strip (Qwen) | Grok strip | Grok scad | Grok + photo

Every Grok call goes through llm.chat(provider="xai", reason=REASON) and bumps a counter file
first; the run refuses to go past GROK_CAP calls. Answers are cached, so reruns are free.
"""

import argparse
import json
import time

import numpy as np
from openai.resources.chat.completions import Completions
from PIL import Image, ImageDraw

from server import part_templates as T
from server.experiments.assets_llm import decal
from server.experiments.assets_llm.e1_run import _gen_prompt, _parse, _user
from server.experiments.assets_llm.e2_run import Run, extract_code
from server.experiments.assets_llm.e5_run import (
    FINISH_NOTE,
    RESULTS,
    STRIP,
    _part,
    _row,
    project,
    view_of,
)
from server.experiments.assets_llm.groq_smoke import _photo_data_url
from server.experiments.assets_llm.llm import GROK_MODEL, WORK, chat
from server.experiments.assets_llm.render import contact_sheet, load_scene

OUT = WORK / "e4"
COUNTER = OUT / "grok_calls.json"
GROK_CAP = 12
MAX_TOKENS = 16000  # grok-4.7 reasons before answering; the reasoning counts as output tokens
REASON = (
    "Groq qwen3.8-27b lost to Hunyuan on thin moulded gutter hangers in E1/E2/E5"
    " (IoU 0.34-0.51 vs 0.46-0.62, vinyl arm slope reversed)"
)
# E2's prompt has no hint for sloped strips (the library is axis-aligned); hull() is allowed.
# The harness appends the library after the model, so a top-level `hw = W/2;` sees no W (cost
# the alu hanger its one compile retry); Qwen never wrote top-level variables in E2.
STRIP_HINT = (
    " Top-level variables must not use W, H or D (define them inside a module instead)."
    " Thin bent or sloped strips: build each straight segment as hull() of two thin boxes or two"
    ' cyl("x", ...) at its ends; hull() of cylinders also makes rounded bends.'
)


_usage: dict = {}
_create = Completions.create


def _create_logged(self, *a, **k):  # keep the usage llm.chat drops (reasoning tokens)
    resp = _create(self, *a, **k)
    _usage["last"] = resp.usage
    return resp


Completions.create = _create_logged


def grok_left() -> int:
    return GROK_CAP - len(json.loads(COUNTER.read_text()) if COUNTER.exists() else [])


def grok(messages: list, tag: str, json_mode: bool = False) -> tuple[str, dict]:
    calls = json.loads(COUNTER.read_text()) if COUNTER.exists() else []
    if len(calls) >= GROK_CAP:
        raise RuntimeError(f"Grok cap of {GROK_CAP} calls reached")
    COUNTER.write_text(json.dumps([*calls, tag]))  # counted before the call: failures count too
    _usage.pop("last", None)
    text, rec = chat(
        messages, tag=tag, provider="xai", reason=REASON, max_tokens=MAX_TOKENS, json_mode=json_mode
    )
    details = getattr(_usage.get("last"), "completion_tokens_details", None)
    rec["reasoning_tokens"] = getattr(details, "reasoning_tokens", None)
    return text, rec


def _grok_extra(recs: list) -> dict:
    return {
        "model": GROK_MODEL,
        "grok_calls": len(recs),
        "grok_in": sum(r.get("in_tokens") or 0 for r in recs),
        "grok_out": sum(r.get("out_tokens") or 0 for r in recs),
        "grok_reasoning": sum(r.get("reasoning_tokens") or 0 for r in recs),
    }


# ---------------------------------------------------------------- 1. bent_strip


def strip(part: dict, catalogue: bool) -> dict:
    """catalogue=True: the exact E5 prompt Qwen got (whole catalogue, row e4-grok-cat).
    False: the same prompt with only the bent_strip entry (row e4-grok-strip)."""
    pid = part["id"]
    out = OUT / pid
    name = "cat" if catalogue else "strip"
    cache = out / f"{name}.json"
    if cache.exists():
        c = json.loads(cache.read_text())
        ans, recs = c["json"], c["records"]
    else:  # same 512 px photo as Qwen
        size = {k: round(part["dims_mm"][k], 1) for k in ("w", "h", "d")}
        prompt = (
            _gen_prompt(part, size, None) + "\n" + FINISH_NOTE.format(names="|".join(T.FINISHES))
        )
        prompt = prompt.replace('"front_face_note"', '"finishes": {...}, "front_face_note"')
        if not catalogue:
            only = next(e for e in T.catalogue().split("\n- ") if "bent_strip:" in e)
            prompt = prompt.replace(T.catalogue(), "- " + only.lstrip("- "))
            prompt = prompt.replace("Pick the template whose shape fits best.", "Use bent_strip.")
        text, rec = grok(
            _user(prompt, _photo_data_url(part["_photo"], 512)), f"e4/{pid}/{name}", True
        )
        ans, recs = _parse(text), [rec]
        cache.write_text(json.dumps({"json": ans, "raw": text, "records": recs}, indent=1))
    t0 = time.time()
    t, p, c = T.validate(ans.get("template"), ans.get("params"), ans.get("colors"))
    scene, _ = T.build(t, part["dims_mm"], p, c, ans.get("finishes"))
    glb = out / f"{name}.glb"
    scene.export(glb)
    contact_sheet(glb, out / f"{name}_sheet.jpg", part["_photo"])
    wall = time.time() - t0 + sum(r["latency_s"] for r in recs)
    note = f"{t}; {ans.get('front_face_note', '')}"
    method = f"e4-grok-{name}"
    return _row(method, part, glb, glb, recs, wall, note, {"template": t, **_grok_extra(recs)})


# ---------------------------------------------------------------- 2. OpenSCAD loop


class GrokRun(Run):
    """E2's OpenSCAD run with Grok in place of Qwen, one compile retry per part."""

    def __init__(self, part: dict):
        super().__init__("scad", part)
        (WORK / "e2" / "scad" / part["id"]).rmdir()  # Run made E2's dir; ours is under e4/
        self.dir = OUT / part["id"] / "scad"
        self.dir.mkdir(parents=True, exist_ok=True)
        self.prompt = self.prompt.replace(" At most 70 lines.", STRIP_HINT + " At most 70 lines.")
        self.fixes_left = 1

    def ask(self, messages: list, step: str) -> str:
        text, rec = grok(messages, f"e4/{self.part['id']}/scad/{step}")
        self.calls.append(rec)
        (self.dir / f"{len(self.calls):02d}_{step}.txt").write_text(text)
        return text

    def generate(self, messages: list, step: str) -> tuple[dict | None, int]:
        if not grok_left() or (step == "bbox" and grok_left() < 2):  # keep one for the critique
            self.log.append({"step": step, "attempt": 0, "error": "skipped: Grok cap"})
            return None, 0
        text = self.ask(messages, step)
        for attempt in range(2):
            code = extract_code(text)
            res, err = self.build(code)
            self.log.append({"step": step, "attempt": attempt + 1, "error": err})
            if res or attempt or not self.fixes_left or not grok_left():
                return res, attempt + 1
            self.fixes_left -= 1
            fence = self.cfg["fence"]
            text = self.ask(
                [
                    {"role": "user", "content": self.prompt},
                    {"role": "assistant", "content": f"```{fence}\n{code}\n```"},
                    {
                        "role": "user",
                        "content": f"It failed with:\n{err}\n\nFix it. Reply with the"
                        f" full corrected code in one ```{fence} block.",
                    },
                ],
                f"{step}-fix",
            )
        return None, 2

    def finish(self, res, suffix, t0, attempts, notes) -> dict:
        r = super().finish(res, suffix, t0, attempts, notes)
        return r | {"method": f"e4-grok-scad{suffix}"} | _grok_extra(self.calls)

    def fail(self, suffix, t0, attempts, note) -> dict:
        r = super().fail(suffix, t0, attempts, note)
        return r | {"method": f"e4-grok-scad{suffix}"} | _grok_extra(self.calls)


def scad(tpart: dict) -> list[dict]:
    run = GrokRun(tpart)
    if (run.dir / "log.json").exists():  # resume: rows already written
        return json.loads((run.dir / "log.json").read_text())["rows"]
    return run.run()


# ---------------------------------------------------------------- 3. photo on the best


def _rows() -> dict:
    return {
        (r["method"], r["part"]): r
        for r in map(json.loads, RESULTS.read_text().splitlines())
        if "method" in r
    }


def best_geometry(pid: str, pick: str | None) -> tuple[str, dict, object]:
    """(label, row, geometry glb) of the best Grok result: IoU + CLIP, or `pick` (strip|scad)."""
    rows = _rows()
    cands = {"strip": (rows.get(("e4-grok-strip", pid)), OUT / pid / "strip.glb")}
    log = OUT / pid / "scad" / "log.json"
    kept = [r for r in json.loads(log.read_text())["rows"] if r.get("kept")] if log.exists() else []
    if kept and kept[0]["valid"]:
        suffix = kept[0]["method"].removeprefix("e4-grok-scad") or "-base"
        cands["scad"] = (kept[0], OUT / pid / "scad" / f"model{suffix}.glb")
    if pick:
        return pick, *cands[pick]
    k = max(cands, key=lambda c: (cands[c][0]["iou"] or 0) + (cands[c][0]["clip"] or 0))
    return k, *cands[k]


def photo(part: dict, pick: str | None) -> dict:
    pid = part["id"]
    label, row, geo = best_geometry(pid, pick)
    t0 = time.time()
    template = "bent_strip" if label == "strip" else "scad"
    view, vrec, vnote = view_of(part, template)
    textured, pinfo = project(
        load_scene(geo), part["_photo"], view["face"], view.get("rotate_cw", 0)
    )
    glb = OUT / pid / "best_photo.glb"
    textured.export(glb)
    grok_recs = _grok_recs(pid, label)
    wall = time.time() - t0 + sum(r["latency_s"] for r in grok_recs) + vrec["latency_s"]
    note = f"{label} ({row['method']}); {vnote}{' flipped' if pinfo['flip'] else ''}"
    return _row(
        "e4-grok-best-photo", part, glb, geo, grok_recs + [vrec], wall, note, _grok_extra(grok_recs)
    )


def _grok_recs(pid: str, label: str) -> list[dict]:
    if label == "strip":
        return json.loads((OUT / pid / "strip.json").read_text())["records"]
    return json.loads((OUT / pid / "scad" / "log.json").read_text())["calls"]


# ---------------------------------------------------------------- gallery


def gallery(part: dict, testpart: dict) -> None:
    from server.experiments.assets_llm.render import BG, render_views

    pid, size = part["id"], 256
    rows = _rows()
    hy = WORK.parents[1] / testpart["baseline_glb"]
    base = json.loads(
        next(ln for ln in (WORK / "baseline_scores.jsonl").read_text().splitlines() if pid in ln)
    )
    label = best_geometry(pid, None)[0]
    scad_glb = OUT / pid / "scad" / "model-base.glb"
    crit = rows.get(("e4-grok-scad-crit", pid))
    if crit and crit.get("kept"):
        scad_glb = OUT / pid / "scad" / "model-crit.glb"
    cols = [
        ("Hunyuan", hy, {"iou": base["sil_iou"], "clip": base["clip_sim"]}),
        ("e5-strip (Qwen)", WORK / "e5" / pid / "e5-strip.glb", rows.get(("e5-strip", pid))),
        ("e4-grok-strip", OUT / pid / "strip.glb", rows.get(("e4-grok-strip", pid))),
        (
            "e4-grok-scad" + ("-crit" if "crit" in scad_glb.name else ""),
            scad_glb,
            rows.get(("e4-grok-scad-crit" if "crit" in scad_glb.name else "e4-grok-scad", pid)),
        ),
        ("e4-grok-best-photo", OUT / pid / "best_photo.glb", rows.get(("e4-grok-best-photo", pid))),
    ]
    face = view_of(part, "bent_strip")[0]["face"]
    n = np.array(decal.FACES[face][0], float)
    views = {"photo side": n + [0.35, 0.45, 0.35], "3/4": (1.0, 0.75, 1.25)}
    sheet = Image.new("RGB", (size * (len(cols) + 1), size * 2 + 40), (255, 255, 255))
    d = ImageDraw.Draw(sheet)
    d.text((6, 4), f"{pid}  {part['dims_mm']}", fill=(0, 0, 0))
    p = Image.open(part["_photo"]).convert("RGB")
    p.thumbnail((size, size * 2))
    sheet.paste(p, ((size - p.width) // 2, 40 + (2 * size - p.height) // 2))
    d.text((6, 22), "photo", fill=(0, 0, 0))
    for i, (lbl, glb, r) in enumerate(cols, start=1):
        score = f" IoU {r['iou']:.2f} CLIP {r['clip']:.2f}" if r and r.get("clip") else ""
        d.text((i * size + 4, 22), lbl + score, fill=(0, 0, 0))
        if not glb.exists():
            sheet.paste(Image.new("RGB", (size, 2 * size), BG), (i * size, 40))
            continue
        vs = render_views(load_scene(glb), size, views)
        for j, k in enumerate(views):
            sheet.paste(Image.fromarray(vs[k][0]), (i * size, 40 + j * size))
    sheet.save(OUT / f"{pid}.jpg", quality=85)
    print("gallery", OUT / f"{pid}.jpg", "best:", label)


if __name__ == "__main__":
    from server.experiments.assets_llm.e3_run import _patch_pyrender_textures

    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("steps", nargs="*", default=["strip", "scad", "photo", "gallery"])
    ap.add_argument("--best", choices=["strip", "scad"], help="override the photo step's pick")
    ap.add_argument("--parts", default=",".join(STRIP))
    a = ap.parse_args()
    _patch_pyrender_textures()
    testset = {p["id"]: p for p in json.loads((WORK / "testset.json").read_text())}
    for pid in a.parts.split(","):
        part = _part(pid)
        (OUT / pid).mkdir(parents=True, exist_ok=True)
        if "cat" in a.steps:
            strip(part, catalogue=True)
        if "strip" in a.steps:
            strip(part, catalogue=False)
        if "scad" in a.steps:
            for r in scad(testset[pid]):
                print(
                    json.dumps(
                        {
                            k: r.get(k)
                            for k in (
                                "method",
                                "valid",
                                "iou",
                                "clip",
                                "pieces",
                                "grok_calls",
                                "grok_out",
                                "kept",
                            )
                        }
                    )
                )
        if "photo" in a.steps:
            photo(part, a.best)
        if "gallery" in a.steps:
            gallery(part, testset[pid])
    calls = json.loads(COUNTER.read_text()) if COUNTER.exists() else []
    print(f"Grok calls so far: {len(calls)}/{GROK_CAP}")
