"""E2: Qwen writes OpenSCAD or CadQuery against a frame-safe helper library (server/scad_lib.scad,
e2_cq.py), with compile-error retries, bbox feedback and one render-critique pass.
Notes and results: docs/research/assets-llm/e2-code.md.

    uv run --with pyrender --with scipy --with open_clip_torch --with manifold3d \
        --index https://download.pytorch.org/whl/cpu \
        python -m server.experiments.assets_llm.e2_run --lang scad [--parts id,id]

Per part: work/assets-llm/e2/<lang>/<id>/ (code, GLBs, sheets, log.json); one row per variant
appended to work/assets-llm/results.jsonl.
`--text`: gpt-oss-120b on an agent-written photo description (DESCRIPTIONS), numeric feedback
instead of the render critique; outputs under e2/<lang>-txt/.
All LLM calls go through llm.chat (tag e2/<variant>/<id>/<step>).
"""

import argparse
import io
import json
import re
import subprocess
import tempfile
import time
from pathlib import Path

import numpy as np
import trimesh
from openai import APIConnectionError, APITimeoutError
from PIL import Image, ImageDraw

from server.experiments.assets_llm.groq_smoke import _photo_data_url
from server.experiments.assets_llm.llm import (
    GROQ_MODEL,
    GROQ_TEXT_MODEL,
    WORK,
    DailyQuotaExceeded,
    chat,
)
from server.experiments.assets_llm.render import contact_sheet, load_scene, render_views
from server.experiments.assets_llm.score import score_part

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
OPENSCAD = WORK / "tools" / "openscad.AppImage"
CQ_PY = WORK / "envs" / "cq" / "bin" / "python"
MAX_TOKENS = 2500  # lean: code answers run 0.7-1.3K tokens; the brief caps at 2500
RESCALE_MAX_PCT = 5.0
LANGS = {
    "scad": {
        "name": "OpenSCAD",
        "fence": "openscad",
        "api": re.search(
            r"// ---- API.*?\n(.*?)\n\n", (ROOT / "server" / "scad_lib.scad").read_text(), re.DOTALL
        )
        .group(1)
        .replace("// ", ""),
        "rules": "Plain OpenSCAD 2021+ syntax; the library is already included. Use difference()"
        " / union() / hull() for booleans. OpenSCAD has no object variables (`b = box();` is a"
        " syntax error): call modules directly, or wrap a group in `module name() { ... }`."
        ' Colours are strings: paint("#RRGGBB").',
    },
    "cq": {
        "name": "CadQuery (Python)",
        "fence": "python",
        "api": None,  # filled lazily: e2_cq imports cadquery, so read the API text from the file
        "rules": "Python; the library functions and cq are already imported; no other imports,"
        " no file I/O.",
    },
}
LANGS["cq"]["api"] = re.search(
    r'API = """\\\n(.*?)"""', (HERE / "e2_cq.py").read_text(), re.DOTALL
).group(1)

PROMPT = """You are a CAD modeller. Write {lang} code that builds a recognisable 3D model of the \
product, using ONLY the helper library below. Model the overall shape and the main \
visible features (body, doors, panels, grilles, vents, handles, bowls, flanges, holes, knobs, \
pipes); skip text and logos.

Product: {name}
{desc}
Category: {category}. Material: {material}. Colour: {color}. Mounted on: {surface}.
Envelope (mm), fixed: W={w} along X, H={h} along Y, D={d} along Z. The model must fill exactly \
this box.

Frame (mm): X across, -W/2..W/2 (+X = right when facing the front). Y up, 0..H (floor/bottom \
at Y=0). Z depth, 0..D: Z=0 is the back / mounting face (against the wall, floor or \
structure), Z=D is the front the viewer sees (the side shown in product photos). W, H, D are \
predefined; write sizes as expressions of them.

Library:
{api}

Rules: {rules} Every solid must be painted with a hex colour matching the product. Build \
everything with the helpers (world coordinates, no rotations). At most 70 lines. Reply with \
one ```{fence} code block only."""

# Qwen vision quota gone (Groq caps it at 200K tokens/day): the text-model path gets a short
# description of the photo, written by the E2 agent from the JPEG, instead of the image.
DESCRIPTIONS = {
    "whirlpool-what101-1bw-cdcda4": "White through-the-wall air conditioner seen straight from the "
    "front. Front is a flat white panel. Top ~30%: a black control panel at the upper left "
    "(about 20% of the width) and two side-by-side air outlets with white horizontal/vertical "
    "louvres. Bottom ~65%: a large intake grille of ~14 thin horizontal slats with dark gaps, "
    "split into 4 columns. A thin horizontal lip separates the two zones.",
    "kraus-ke1us32-ec0e5f": "Single-bowl undermount kitchen sink in brushed stainless steel "
    "(#B8BABC), seen from above at an angle. A large rectangular bowl with small-radius rounded "
    "corners, thin rim/flange around the top, vertical walls, flat bottom with a round drain "
    "(~90 mm) near the centre-back. Underside has dark grey sound-deadening coating (#3A3D42). "
    "The top is open (it is a bowl).",
    "delta-75641-d491ed": "Round chrome shower head (#D8DADC). A disc-shaped face ~111 mm "
    "diameter facing the front, ~25 mm thick with a bevelled chrome rim; the face has a grey "
    "(#8A8C8E) centre hub and rings of black rubber nozzles (#2A2A2A). Behind the head a short "
    "chrome ball-joint neck (~35 mm diameter) runs back to the wall connector at the back. "
    "A small lever tab sticks out on the right edge.",
    "aciq-14-3-seer2-central-air-conditioner-condenser-a0436b": "Outdoor central-AC condenser: a "
    "square dark grey (#3E4448) box with slightly rounded vertical corners. All four sides are "
    "louvred coil guards (many narrow vertical slots in 3 horizontal bands). Flat top with a "
    "large round fan grille (black, concentric wire rings) nearly as wide as the top. A small "
    "blue logo badge on the front; service valves at the lower right side.",
}

NUMERIC = """Automatic check of your model (no image available):
- bounding box error per axis (X, Y, Z) %: {bbox}
- connected pieces: {pieces} ({floating} not touching the main body)
- silhouette IoU vs the product photo (best of 7 views, 1.0 = identical outline): {iou} ({view})
- CLIP image similarity vs the photo (0.25 poor .. 0.85 excellent): {clip}
Improve the model with these numbers and the description: fix any size/frame error, attach floating pieces, and add or reshape features so the outline and look match the product better. Reply with the full revised code in one ```{fence} block."""

CRITIQUE = """Image 1 is the product photo. Image 2 is a render of your model: front view \
(camera on +Z) and 3/4 view (from +X, +Y, +Z). Target W={w} H={h} D={d} mm.

Checklist:
1. Overall shape and proportions match the photo?
2. Right way up, and the photo's visible front on the +Z face?
3. Main features present, on the right face, right size (doors, grilles, vents, handles, bowl, \
holes, pipes)?
4. Floating, disconnected or missing pieces?
5. Colours match the photo?

Your current code:
```{fence}
{code}
```
List the problems in at most 5 short bullets, then give the full revised code in one \
```{fence} block that fixes them (same library and frame)."""


def extract_code(text: str) -> str:
    m = re.search(r"```[a-zA-Z]*\n(.*?)```", text, re.DOTALL) or re.search(
        r"```[a-zA-Z]*\n(.*)", text, re.DOTALL
    )
    return (m.group(1) if m else text).strip()


# ---------- build: code -> [(mesh_mm, hex)] in the product frame ----------


def _scad_run(src: Path, out: Path, dims: tuple, only: str) -> subprocess.CompletedProcess:
    w, h, d = dims
    args = [str(OPENSCAD), "--backend=manifold", "-o", str(out), "-D", f"W={w}", "-D", f"H={h}"]
    args += ["-D", f"D={d}", "-D", f'ONLY="{only}"', str(src)]
    return subprocess.run(args, capture_output=True, text=True, timeout=180, check=False)


def build_scad(code: str, work: Path, dims: tuple) -> tuple[list, str | None]:
    """Returns (pieces, error). One OpenSCAD run lists the colours (and catches unpainted
    solids), then one run per colour. The library is appended so model line numbers stay true."""
    src = work / "model.scad"
    src.write_text(code + f"\ninclude <{ROOT / 'server' / 'scad_lib.scad'}>\n")
    p = _scad_run(src, work / "none.stl", dims, "__none__")
    log = p.stdout + p.stderr
    bad = [
        ln
        for ln in log.splitlines()
        if "ERROR" in ln
        or "unknown" in ln.lower()
        or "undefined" in ln.lower()
        or "Assertion" in ln
    ]
    if bad or (p.returncode and "top level object is empty" not in log):
        err = "\n".join(bad or log.splitlines()[-8:])[:1500]
        err = re.sub(r"in file [^,]*, ", "in model.scad, ", err)
        lines = code.splitlines()
        for n in dict.fromkeys(int(m) for m in re.findall(r"line (\d+)", err)):
            if 0 < n <= len(lines):  # show the offending source line, not just its number
                err += f"\nline {n}: {lines[n - 1].strip()}"
        return [], err
    colors = list(dict.fromkeys(re.findall(r"E2COLOR=(#[0-9A-Fa-f]{6})", log)))
    pieces = []
    if (work / "none.stl").exists() and p.returncode == 0:
        pieces.append((trimesh.load(work / "none.stl", force="mesh"), "#B0B0B0"))
    for i, c in enumerate(colors):
        stl = work / f"c{i}.stl"
        q = _scad_run(src, stl, dims, c)
        if q.returncode == 0 and stl.exists():
            pieces.append((trimesh.load(stl, force="mesh"), c))
    if not pieces:
        return (
            [],
            'The model is empty: nothing was painted. Wrap every solid in paint("#hex") { ... }.',
        )
    return pieces, None


def build_cq(code: str, work: Path, dims: tuple) -> tuple[list, str | None]:
    src, out = work / "model.py", work / "cqout"
    src.write_text(code)
    p = subprocess.run(
        [
            str(CQ_PY),
            "-m",
            "server.experiments.assets_llm.e2_cq",
            str(src),
            str(out),
            *map(str, dims),
        ],
        capture_output=True,
        text=True,
        timeout=300,
        check=False,
        cwd=ROOT,
    )
    if p.returncode:
        tb = p.stderr.strip().splitlines()
        # keep the frames inside the model code plus the exception line
        keep = [ln for ln in tb if "<llm>" in ln] + tb[-3:]
        return [], "\n".join(keep)[-1500:]
    colors = json.loads((out / "colors.json").read_text())
    return [(trimesh.load(out / f"{i}.stl", force="mesh"), c) for i, c in enumerate(colors)], None


# ---------- assemble: paint order, envelope clip, pieces, rescale, GLB ----------


def _err(lo, hi, dims) -> list[float]:
    """Per-axis worst of low/high face offset from the envelope, % of that dim."""
    t_lo, t_hi, t = (
        np.array([-dims[0] / 2, 0, 0]),
        np.array([dims[0] / 2, dims[1], dims[2]]),
        np.array(dims),
    )
    return [round(float(v), 1) for v in np.maximum(abs(lo - t_lo), abs(hi - t_hi)) / t * 100]


def _bool(op, meshes):
    try:
        out = getattr(trimesh.boolean, op)(meshes, engine="manifold")
        return out if len(out.faces) else None
    except Exception:  # noqa: BLE001 -- non-manifold input: keep the unmodified mesh
        return meshes[0] if op != "union" else trimesh.util.concatenate(meshes)


def _linear(hex_color: str) -> list[float]:
    s = [int(hex_color.lstrip("#")[i : i + 2], 16) / 255 for i in (0, 2, 4)]
    return [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in s] + [1.0]


def assemble(pieces: list, dims: tuple, glb: Path) -> dict:
    w, h, d = dims
    allv = np.vstack([m.vertices for m, _ in pieces])
    raw_lo, raw_hi = allv.min(0), allv.max(0)
    raw_err = _err(raw_lo, raw_hi, dims)
    env = trimesh.creation.box(extents=(w, h, d))
    env.apply_translation((0, h / 2, d / 2))
    # merge same-colour pieces, then later paints win: subtract every later colour, clip to envelope
    by_color: dict[str, list] = {}
    for m, c in pieces:
        by_color.setdefault(c.upper(), []).append(m)
    order = list(by_color)
    merged = [(_bool("union", ms) if len(ms) > 1 else ms[0], c) for c, ms in by_color.items()]
    final = []
    for i, (m, c) in enumerate(merged):
        m = _bool("intersection", [m, env])
        later = [mm for mm, _ in merged[i + 1 :]]
        if m is not None and later:
            m = _bool("difference", [m, *later])
        if m is not None and len(m.faces):
            final.append((m, c))
    union = _bool("union", [m for m, _ in final])
    comps = union.split(only_watertight=False) if union is not None else []
    vols = sorted((abs(cp.volume) for cp in comps), reverse=True)
    allv = np.vstack([m.vertices for m, _ in final])
    lo, hi = allv.min(0), allv.max(0)
    clip_err = _err(lo, hi, dims)
    rescaled = max(clip_err) > 0.05 and max(clip_err) <= RESCALE_MAX_PCT
    t_lo, t_hi = np.array([-w / 2, 0, 0]), np.array([w / 2, h, d])
    scene = trimesh.Scene()
    for i, (m, c) in enumerate(final):
        m = m.copy()
        if rescaled:  # per-axis map of the bbox onto the envelope
            m.vertices = t_lo + (m.vertices - lo) / (hi - lo) * (t_hi - t_lo)
        m.vertices = (m.vertices - [0, h / 2, 0]) / 1000.0  # product frame -> glTF contract
        m.visual = trimesh.visual.TextureVisuals(
            material=trimesh.visual.material.PBRMaterial(
                baseColorFactor=_linear(c), metallicFactor=0.1, roughnessFactor=0.6
            )
        )
        scene.add_geometry(m, node_name=f"c{i}")
    scene.export(glb)
    return {
        "bbox_raw_mm": [[round(float(v), 1) for v in raw_lo], [round(float(v), 1) for v in raw_hi]],
        "bbox_err_raw": raw_err,
        "bbox_err_clip": clip_err,
        "rescaled": bool(rescaled),
        "colors": order,
        "pieces": len(comps),
        "floating": max(len(comps) - 1, 0),
        "small_piece_frac": round(sum(vols[1:]) / max(sum(vols), 1e-9), 4) if vols else None,
    }


# ---------- loop ----------


def two_view(glb: Path) -> Image.Image:
    views = render_views(load_scene(glb), 384, {"front": (0, 0, 1), "3/4": (1, 0.75, 1.25)})
    img = Image.new("RGB", (768, 384))
    for i, (name, (rgb, _)) in enumerate(views.items()):
        tile = Image.fromarray(rgb)
        ImageDraw.Draw(tile).text((6, 4), name, fill=(20, 20, 20))
        img.paste(tile, (384 * i, 0))
    return img


def _data_url(img: Image.Image) -> str:
    import base64

    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=85)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


class Run:
    def __init__(self, lang: str, part: dict, text_model: bool = False):
        self.lang, self.part, self.text = lang, part, text_model
        self.model = GROQ_TEXT_MODEL if text_model else None  # None = llm default (qwen vision)
        self.variant = f"{lang}-txt" if text_model else lang
        self.cfg = LANGS[lang]
        self.dims = (part["dims_mm"]["w"], part["dims_mm"]["h"], part["dims_mm"]["d"])
        self.dir = WORK / "e2" / self.variant / part["id"]
        self.dir.mkdir(parents=True, exist_ok=True)
        self.calls: list[dict] = []
        self.log: list[dict] = []
        self.n = 0  # build counter
        w, h, d = self.dims
        self.prompt = PROMPT.format(
            lang=self.cfg["name"],
            name=part["name"],
            category=part["category"],
            material=part.get("material") or "?",
            color=part.get("color_hex") or "see " + ("description" if text_model else "photo"),
            desc=f"Description (from the product photo): {DESCRIPTIONS[part['id']]}"
            if text_model
            else "The product photo is attached.",
            surface=(part.get("mount") or {}).get("surface") or "floor/structure",
            w=w,
            h=h,
            d=d,
            api=self.cfg["api"],
            rules=self.cfg["rules"],
            fence=self.cfg["fence"],
        )

    def ask(self, messages: list, step: str) -> str:
        tag = f"e2/{self.variant}/{self.part['id']}/{step}"
        if self.text:  # gpt-oss: keep hidden reasoning short, it counts as output tokens
            messages = [{"role": "system", "content": "Reasoning: low"}, *messages]
        for attempt in range(3):  # network blips (APIConnectionError) killed two runs
            try:
                text, rec = chat(
                    messages, tag=tag, model=self.model, max_tokens=MAX_TOKENS, temperature=0.2
                )
                break
            except (APIConnectionError, APITimeoutError):
                if attempt == 2:
                    raise
                time.sleep(30)
        self.calls.append(rec)
        (self.dir / f"{len(self.calls):02d}_{step}.txt").write_text(text)
        return text

    def build(self, code: str) -> tuple[dict | None, str | None]:
        self.n += 1
        work = self.dir / f"b{self.n}"
        work.mkdir(exist_ok=True)
        fn = build_scad if self.lang == "scad" else build_cq
        try:
            pieces, err = fn(code, work, self.dims)
        except subprocess.TimeoutExpired:
            pieces, err = [], "Timed out (over 3 minutes). Avoid minkowski and huge loops."
        if err:
            return None, err
        glb = work / "model.glb"
        try:
            info = assemble(pieces, self.dims, glb)
        except Exception as exc:  # noqa: BLE001
            return None, f"Mesh assembly failed: {exc}"
        return {"code": code, "glb": glb, **info}, None

    def generate(self, messages: list, step: str) -> tuple[dict | None, int]:
        """One LLM answer + up to 2 compile-error retries. Returns (build or None, attempts)."""
        text = self.ask(messages, step)
        for attempt in range(3):
            code = extract_code(text)
            res, err = self.build(code)
            self.log.append({"step": step, "attempt": attempt + 1, "error": err})
            if res:
                return res, attempt + 1
            if attempt == 2:
                return None, 3
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
                f"{step}-fix{attempt + 1}",
            )
        return None, 3

    def bbox_feedback(self, res: dict) -> str:
        (lo, hi), (w, h, d) = res["bbox_raw_mm"], self.dims
        return (
            f"The model's bounding box is off (mm):\n"
            f"X [{lo[0]}, {hi[0]}], should be [{-w / 2}, {w / 2}]\n"
            f"Y [{lo[1]}, {hi[1]}], should be [0, {h}]\n"
            f"Z [{lo[2]}, {hi[2]}], should be [0, {d}]\n"
            "Anything outside the envelope is cut off. Check the frame (Y up, back face at Z=0, "
            "front at Z=D) and fix the sizes/positions. Reply with the full corrected code in one "
            f"```{self.cfg['fence']} block."
        )

    def run(self) -> list[dict]:
        t0 = time.time()
        photo = ROOT / self.part["photo"]
        purl = None if self.text else _photo_data_url(photo)
        content = [{"type": "text", "text": self.prompt}]
        if purl:
            content.append({"type": "image_url", "image_url": {"url": purl}})
        msgs = [{"role": "user", "content": content if purl else self.prompt}]
        base, attempts = self.generate(msgs, "gen")
        compile_attempts = attempts
        notes = []
        if base and max(base["bbox_err_raw"]) > 2.0:
            fence = self.cfg["fence"]
            fixed, a2 = self.generate(
                [
                    {"role": "user", "content": self.prompt},
                    {"role": "assistant", "content": f"```{fence}\n{base['code']}\n```"},
                    {"role": "user", "content": self.bbox_feedback(base)},
                ],
                "bbox",
            )
            compile_attempts += a2
            if fixed and max(fixed["bbox_err_raw"]) < max(base["bbox_err_raw"]):
                notes.append(f"bbox fix {max(base['bbox_err_raw'])}->{max(fixed['bbox_err_raw'])}%")
                base = fixed
            else:
                notes.append("bbox fix not better, kept first")
        rows = []
        if base:
            rows.append(self.finish(base, "", t0, compile_attempts, notes))
        else:
            rows.append(self.fail("", t0, compile_attempts, "no compiling code after 2 retries"))
            return self.write(rows)
        if self.text:
            return self.numeric_pass(base, rows, t0, compile_attempts, notes)
        # render critique: photo + 2-view render
        two = two_view(base["glb"])
        two.save(self.dir / "render_base.jpg", quality=85)
        w, h, d = self.dims
        crit_msgs = [
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": self.prompt},
                    {"type": "image_url", "image_url": {"url": purl}},
                    {"type": "image_url", "image_url": {"url": _data_url(two)}},
                    {
                        "type": "text",
                        "text": CRITIQUE.format(
                            w=w, h=h, d=d, code=base["code"], fence=self.cfg["fence"]
                        ),
                    },
                ],
            }
        ]
        crit, a3 = self.generate(crit_msgs, "crit")
        if crit:
            rows.append(self.finish(crit, "-crit", t0, compile_attempts + a3, notes))
        else:
            rows.append(
                self.fail("-crit", t0, compile_attempts + a3, "critique code never compiled")
            )

        return self.keep_best(rows)

    def keep_best(self, rows: list[dict]) -> list[dict]:
        # keep the better: valid first, then iou + clip
        def key(r):
            return (r["valid"], (r["iou"] or 0) + (r["clip"] or 0))

        best = max(rows, key=key)
        for r in rows:
            r["kept"] = r is best
        return self.write(rows)

    def numeric_pass(self, base, rows, t0, attempts, notes) -> list[dict]:
        r0, fence = rows[0], self.cfg["fence"]
        fb = NUMERIC.format(
            bbox=base["bbox_err_raw"],
            pieces=r0["pieces"],
            floating=r0["floating"],
            iou=r0["iou"],
            view=r0.get("sil_view"),
            clip=r0["clip"],
            fence=fence,
        )
        num, a3 = self.generate(
            [
                {"role": "user", "content": self.prompt},
                {"role": "assistant", "content": f"```{fence}\n{base['code']}\n```"},
                {"role": "user", "content": fb},
            ],
            "num",
        )
        if num:
            rows.append(self.finish(num, "-num", t0, attempts + a3, notes))
        else:
            rows.append(
                self.fail("-num", t0, attempts + a3, "numeric-feedback code never compiled")
            )
        return self.keep_best(rows)

    def _cost(self, upto: int | None = None) -> dict:
        cs = self.calls[:upto]
        return {
            "calls": len(cs),
            "in_tokens": sum(c["in_tokens"] or 0 for c in cs),
            "out_tokens": sum(c["out_tokens"] or 0 for c in cs),
            "llm_s": round(sum(c["latency_s"] for c in cs), 1),
        }

    def finish(self, res: dict, suffix: str, t0: float, attempts: int, notes: list) -> dict:
        glb = self.dir / f"model{suffix or '-base'}.glb"
        glb.write_bytes(res["glb"].read_bytes())
        (
            self.dir / f"code{suffix or '-base'}.{'scad' if self.lang == 'scad' else 'py'}"
        ).write_text(res["code"])
        s = score_part(ROOT / "data" / "parts" / self.part["id"], glb)
        contact_sheet(glb, self.dir / f"sheet{suffix or '-base'}.jpg", ROOT / self.part["photo"])
        n = list(notes)
        if self.text:
            n.append("text path: agent-written photo description, no image")
        if res["rescaled"]:
            n.append(f"rescaled (clip err {max(res['bbox_err_clip'])}%)")
        return {
            "method": f"e2-{self.variant}{suffix}",
            "part": self.part["id"],
            "model": self.model or "qwen/qwen3.8-27b",
            "valid": bool(s.get("loads") and s.get("bbox_ok") and s.get("origin_ok")),
            "bbox_err_pct": max(res["bbox_err_raw"]),
            "pieces": res["pieces"],
            "floating": res["floating"],
            "iou": s.get("sil_iou"),
            "clip": s.get("clip_sim"),
            "sil_view": s.get("sil_view"),
            **self._cost(),
            "wall_s": round(time.time() - t0, 1),
            "notes": "; ".join(n),
            "compile_attempts": attempts,
            "bbox_err_raw": res["bbox_err_raw"],
            "bbox_err_clip": res["bbox_err_clip"],
            "rescaled": res["rescaled"],
            "small_piece_frac": res["small_piece_frac"],
            "colors": len(res["colors"]),
            "triangles": s.get("triangles"),
            "file_kb": s.get("file_kb"),
        }

    def fail(self, suffix: str, t0: float, attempts: int, note: str) -> dict:
        return {
            "method": f"e2-{self.variant}{suffix}",
            "part": self.part["id"],
            "model": self.model or "qwen/qwen3.8-27b",
            "valid": False,
            "bbox_err_pct": None,
            "pieces": None,
            "floating": None,
            "iou": None,
            "clip": None,
            **self._cost(),
            "wall_s": round(time.time() - t0, 1),
            "notes": note,
            "compile_attempts": attempts,
        }

    def write(self, rows: list[dict]) -> list[dict]:
        (self.dir / "log.json").write_text(
            json.dumps({"builds": self.log, "calls": self.calls, "rows": rows}, indent=1)
        )
        with open(WORK / "results.jsonl", "a") as f:
            for r in rows:
                f.write(json.dumps(r) + "\n")
        return rows


# E2's share of each model's daily Groq quota (E1 and the live server share the keys)
BUDGET = {GROQ_TEXT_MODEL: 52_000, GROQ_MODEL: 150_000}


def _e2_tokens(model: str) -> int:
    recs = [json.loads(ln) for ln in (WORK / "calls.jsonl").read_text().splitlines()]
    return sum(
        (r.get("in_tokens") or 0) + (r.get("out_tokens") or 0)
        for r in recs
        if r.get("model") == model and r.get("tag", "").startswith("e2/")
    )


def _selfcheck() -> None:
    """Library frame check without the LLM: a box + front panel lands in the contract frame."""
    dims = (600.0, 400.0, 500.0)
    code = {
        "scad": 'paint("#EEEEEE") box(z1=D-20);\npaint("#333333") panel("front", [0, H/2], [W/2, H/2], 20);',
        "cq": 'paint(box(z1=D-20), "#EEEEEE")\npaint(panel("front", (0, H/2), (W/2, H/2), 20), "#333333")',
    }
    for lang, fn in (("scad", build_scad), ("cq", build_cq)):
        with tempfile.TemporaryDirectory() as tmp:
            pieces, err = fn(code[lang], Path(tmp), dims)
            assert not err, err
            info = assemble(pieces, dims, Path(tmp) / "m.glb")
            b = load_scene(Path(tmp) / "m.glb").bounds * 1000
            assert np.allclose(b, [[-300, -200, 0], [300, 200, 500]], atol=0.5), b
            assert info["pieces"] == 1 and max(info["bbox_err_raw"]) == 0, info
            print(lang, "selfcheck ok", info["colors"])


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--lang", choices=list(LANGS))
    ap.add_argument("--parts", default="all")
    ap.add_argument("--text", action="store_true", help="gpt-oss-120b + text description")
    ap.add_argument("--selfcheck", action="store_true")
    a = ap.parse_args()
    if a.selfcheck:
        _selfcheck()
    else:
        testset = json.loads((WORK / "testset.json").read_text())
        ids = [p["id"] for p in testset] if a.parts == "all" else a.parts.split(",")
        by_id = {p["id"]: p for p in testset}
        for part in (by_id[i] for i in ids):  # in the order given
            model = GROQ_TEXT_MODEL if a.text else GROQ_MODEL
            if _e2_tokens(model) > BUDGET[model]:
                print(f"{model} budget {BUDGET[model]} used, stopping", flush=True)
                break
            run = Run(a.lang, part, a.text)
            if (run.dir / "log.json").exists():
                continue  # resume: already done
            try:
                rows = run.run()
            except DailyQuotaExceeded as exc:
                print(f"daily quota exceeded, stopping: {exc}", flush=True)
                break
            for r in rows:
                print(
                    json.dumps(
                        {
                            k: r.get(k)
                            for k in (
                                "method",
                                "part",
                                "valid",
                                "bbox_err_pct",
                                "iou",
                                "clip",
                                "calls",
                                "out_tokens",
                                "compile_attempts",
                            )
                        }
                    ),
                    flush=True,
                )
