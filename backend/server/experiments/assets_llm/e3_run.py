"""E3 runner: photo-decal proxies (+ Groq view detection) for the test set, scored and logged.

    uv run --with pyrender --with scipy --with open_clip_torch \
        --index https://download.pytorch.org/whl/cpu \
        python -m server.experiments.assets_llm.e3_run [part ids...] [--mesh-demo id ...]

Writes work/assets-llm/e3/<part>/{decal,decal_view,hunyuan_decal}.glb + .jpg sheets and view.json,
appends rows to work/assets-llm/results.jsonl. The view answer is cached in view.json, so a rerun
costs no Groq calls.
"""

import argparse
import ctypes
import json
import time
from pathlib import Path

import numpy as np
import trimesh

from server.experiments.assets_llm import decal
from server.experiments.assets_llm.llm import WORK
from server.experiments.assets_llm.render import contact_sheet
from server.experiments.assets_llm.score import score_part


def _patch_pyrender_textures() -> None:
    """pyrender pins PyOpenGL 3.1.0, whose glGenTextures wrapper breaks on Python 3.13 ("No
    array-type handler"); untextured meshes never hit it. Route through the raw entry points.
    Imported late so render.py has set PYOPENGL_PLATFORM=egl first."""
    import pyrender.texture as pt
    from OpenGL.raw.GL.VERSION.GL_1_1 import glDeleteTextures, glGenTextures

    def gen(n: int) -> int:
        ids = (ctypes.c_uint * n)()
        glGenTextures(n, ids)
        return ids[0]

    pt.glGenTextures = gen
    pt.glDeleteTextures = lambda ids: glDeleteTextures(len(ids), (ctypes.c_uint * len(ids))(*ids))


OUT = WORK / "e3"
RESULTS = WORK / "results.jsonl"


def pieces(glb: Path) -> tuple[int, int]:
    """(connected components, components whose bbox misses the largest one's bbox)."""
    m = trimesh.load(str(glb), force="scene").to_geometry()
    m.merge_vertices(merge_tex=True, merge_norm=True)
    parts = m.split(only_watertight=False)
    if len(parts) <= 1:
        return len(parts), 0
    main = max(parts, key=lambda p: len(p.faces))
    lo, hi = main.bounds
    floating = sum(
        1 for p in parts if p is not main and (np.any(p.bounds[0] > hi) or np.any(p.bounds[1] < lo))
    )
    return len(parts), floating


def row(method: str, pid: str, glb: Path, wall_s: float, rec: dict | None, notes: str) -> dict:
    pdir = Path("data/parts") / pid
    s = score_part(pdir, glb)
    n, floating = pieces(glb)
    r = {
        "method": method,
        "part": pid,
        "valid": bool(s["loads"] and s["bbox_ok"] and s["origin_ok"]),
        "bbox_err_pct": max(abs(v) for v in s["dims_err_pct"]),
        "pieces": n,
        "floating": floating,
        "iou": s.get("sil_iou"),
        "clip": s.get("clip_sim"),
        "calls": 1 if rec else 0,
        "in_tokens": rec["in_tokens"] if rec else 0,
        "out_tokens": rec["out_tokens"] if rec else 0,
        "wall_s": round(wall_s, 2),
        "notes": f"{notes}; {s['file_kb']} KB, {s['triangles']} tris",
    }
    with open(RESULTS, "a") as f:
        f.write(json.dumps(r) + "\n")
    contact_sheet(glb, glb.with_suffix(".jpg"), pdir / "image.jpg")
    print(json.dumps(r))
    return r


def run_part(part: dict) -> None:
    pid = part["id"]
    pdir = Path("data/parts") / pid
    photo = pdir / "image.jpg"
    out = OUT / pid
    out.mkdir(parents=True, exist_ok=True)

    t0 = time.time()
    glb = out / "decal.glb"
    decal.decal_proxy(part["dims_mm"], photo).export(glb)
    row("e3-decal", pid, glb, time.time() - t0, None, "photo on front (+Z)")

    view_file = out / "view.json"
    if view_file.exists():
        view = json.loads(view_file.read_text())
    else:
        ans, rec = decal.detect_view(part, photo, tag=f"e3/{pid}/view")
        view = {
            "answer": ans,
            "rec": rec,
            "aspect_guess": decal.aspect_guess(part["dims_mm"], photo),
        }
        view_file.write_text(json.dumps(view, indent=1))
    ans, rec = view["answer"], view["rec"]
    print(pid, "view:", ans, "aspect guess:", view["aspect_guess"])
    if (ans["face"], ans["rotate_cw"]) != ("front", 0):
        t0 = time.time()
        glb = out / "decal_view.glb"
        decal.decal_proxy(part["dims_mm"], photo, ans["face"], ans["rotate_cw"]).export(glb)
        wall = time.time() - t0 + rec["latency_s"]
        row("e3-decal-view", pid, glb, wall, rec, f"view {ans['face']} rot {ans['rotate_cw']}")


def mesh_demo(part: dict) -> None:
    pid = part["id"]
    out = OUT / pid
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    glb = out / "hunyuan_decal.glb"
    decal.textured_glb(Path(part["baseline_glb"]), Path(part["photo"])).export(glb)
    row("e3-hunyuan-decal", pid, glb, time.time() - t0, None, "photo projected on Hunyuan mesh")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("ids", nargs="*")
    ap.add_argument("--mesh-demo", nargs="*", default=[])
    a = ap.parse_args()
    _patch_pyrender_textures()
    testset = {p["id"]: p for p in json.loads((WORK / "testset.json").read_text())}
    for pid in a.ids or ([] if a.mesh_demo else list(testset)):
        run_part(testset[pid])
    for pid in a.mesh_demo:
        mesh_demo(testset[pid])
