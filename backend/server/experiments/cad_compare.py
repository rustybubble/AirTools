"""cad: side-by-side renders of one part's 3D models -- HF (Hunyuan image-to-3D), CAD (Grok
OpenSCAD, per model) and the LLM template -- with a table row per model: faces, file size, bbox
error against the listed dims, the shape's raw error (CAD: before the snap to the envelope; HF:
the uniform-scale residual), seconds and cost.

    uv run --with pyrender python -m server.experiments.cad_compare <part_id> out.png \
        --data data --model "HF=data/parts/<id>/model.hf.glb" --model "CAD=...scad.glb" ...

Each --model is LABEL=path.glb[:meta.json]; the meta is an asset.<stem>.json or a scad.json
record (seconds, cost_usd, bbox_err_pct / scale_residual_pct, made_by). Rows: 3/4 view, front.
Renders with pyrender (EGL on Linux, pyglet on macOS).
"""

import argparse
import json
import os
import sys
from pathlib import Path

if sys.platform != "darwin":
    os.environ.setdefault("PYOPENGL_PLATFORM", "egl")

import numpy as np
import pyrender
import trimesh
from PIL import Image, ImageDraw, ImageFont

BG = (226, 230, 236)
VIEWS = {"3/4": (1.0, 0.6, 1.3), "front": (0.0, 0.0, 1.0)}


def look_at(eye: np.ndarray, target: np.ndarray) -> np.ndarray:
    fwd = target - eye
    fwd = fwd / np.linalg.norm(fwd)
    up = np.array([0.0, 1.0, 0.0]) if abs(fwd[1]) < 0.99 else np.array([0.0, 0.0, -1.0])
    right = np.cross(fwd, up)
    right /= np.linalg.norm(right)
    pose = np.eye(4)
    pose[:3, 0], pose[:3, 1], pose[:3, 2], pose[:3, 3] = right, np.cross(right, fwd), -fwd, eye
    return pose


_renderers: dict[int, pyrender.OffscreenRenderer] = {}


def _renderer(size: int) -> pyrender.OffscreenRenderer:
    # One GL context per size for the whole process (a second pyglet context fails on macOS).
    if size not in _renderers:
        _renderers[size] = pyrender.OffscreenRenderer(size, size)
    return _renderers[size]


def render(tm: trimesh.Scene, size: int, radius: float) -> dict[str, np.ndarray]:
    scene = pyrender.Scene(bg_color=[c / 255 for c in BG] + [1.0], ambient_light=[0.45] * 3)
    for node in tm.graph.nodes_geometry:
        transform, name = tm.graph[node]
        geom = tm.geometry[name]
        if isinstance(geom, trimesh.Trimesh) and len(geom.faces):
            scene.add(pyrender.Mesh.from_trimesh(geom, smooth=False), pose=transform)
    center = tm.bounds.mean(axis=0)
    cam = pyrender.OrthographicCamera(
        xmag=radius * 1.08, ymag=radius * 1.08, znear=radius * 0.01, zfar=radius * 8
    )
    cam_node = scene.add(cam)
    key = scene.add(pyrender.DirectionalLight(intensity=2.2))
    fill = scene.add(pyrender.DirectionalLight(intensity=0.9))
    renderer = _renderer(size)
    out = {}
    for view, direction in VIEWS.items():
        d = np.asarray(direction, float)
        d /= np.linalg.norm(d)
        scene.set_pose(cam_node, look_at(center + d * radius * 3, center))
        scene.set_pose(key, look_at(center + (d + [-0.4, 0.9, 0.3]) * radius * 3, center))
        scene.set_pose(fill, look_at(center + (d + [0.9, -0.2, 0.0]) * radius * 3, center))
        rgb, _ = renderer.render(scene)
        out[view] = rgb.copy()
    return out


def stats(glb: Path, meta: dict, dims_mm: dict) -> dict:
    tm = trimesh.load(glb, force="scene")
    faces = sum(len(g.faces) for g in tm.geometry.values() if hasattr(g, "faces"))
    target = np.array([dims_mm["w"], dims_mm["h"], dims_mm["d"]])
    ext = tm.extents * 1000.0
    bbox_err = float(np.max(np.abs(ext - target) / target) * 100.0)
    shape_err = meta.get("bbox_err_pct")
    if shape_err is None:
        shape_err = meta.get("first_bbox_err_pct")
    if shape_err is None:
        shape_err = meta.get("scale_residual_pct")
    return {
        "faces": int(faces),
        "kb": round(glb.stat().st_size / 1024, 1),
        "bbox_err_pct": round(bbox_err, 2),
        "shape_err_pct": None if shape_err is None else round(float(shape_err), 1),
        "seconds": meta.get("seconds"),
        "cost_usd": meta.get("cost_usd"),
        "made_by": meta.get("made_by") or "",
        "extents_mm": [round(float(v)) for v in ext],
    }


def sheet(
    part: dict, photo: Path | None, models: list[tuple[str, Path, dict]], out: Path, size=360
):
    scenes = [(label, trimesh.load(glb, force="scene"), meta) for label, glb, meta in models]
    radius = max(float(np.linalg.norm(tm.extents)) / 2 for _, tm, _ in scenes)
    cols = len(scenes) + 1
    head, foot = 46, 118
    img = Image.new("RGB", (cols * size, head + 2 * size + foot), (255, 255, 255))
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 15)
        small = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 12)
    except OSError:
        font = small = ImageFont.load_default()
    d = part["dims_mm"]
    draw.text(
        (8, 6),
        f"{part['name'][:90]}",
        fill=(0, 0, 0),
        font=font,
    )
    draw.text(
        (8, 26),
        f"{part['id']}  listed W {d['w']:.0f} x H {d['h']:.0f} x D {d['d']:.0f} mm",
        fill=(70, 70, 70),
        font=small,
    )
    tile = Image.new("RGB", (size, size), (255, 255, 255))
    if photo and photo.exists():
        p = Image.open(photo).convert("RGB")
        p.thumbnail((size - 10, size - 10))
        tile.paste(p, ((size - p.width) // 2, (size - p.height) // 2))
    img.paste(tile, (0, head))
    draw.text((8, head + 4), "product photo", fill=(60, 60, 60), font=small)
    rows = []
    for i, (label, tm, meta) in enumerate(scenes, start=1):
        views = render(tm, size, radius)
        for r, view in enumerate(VIEWS):
            img.paste(Image.fromarray(views[view]), (i * size, head + r * size))
            draw.text(
                (i * size + 8, head + r * size + 4),
                f"{label} · {view}",
                fill=(20, 20, 20),
                font=font,
            )
        s = meta["_stats"]
        rows.append(s)
        lines = [
            f"{label}",
            f"{s['faces']:,} faces · {s['kb']:.0f} KB",
            f"bbox vs listed: {s['bbox_err_pct']:.1f}%"
            + (f" · shape {s['shape_err_pct']:.0f}%" if s["shape_err_pct"] is not None else ""),
            (f"{s['seconds']:.0f} s" if s["seconds"] is not None else "- s")
            + (f" · ${s['cost_usd']:.3f}" if s["cost_usd"] is not None else " · $0"),
            s["made_by"][:48],
        ]
        for k, line in enumerate(lines):
            draw.text(
                (i * size + 8, head + 2 * size + 6 + k * 21),
                line,
                fill=(0, 0, 0) if k == 0 else (60, 60, 60),
                font=font if k == 0 else small,
            )
    img.save(out)
    return rows


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("part_id")
    ap.add_argument("out", type=Path)
    ap.add_argument("--data", type=Path, required=True)
    ap.add_argument("--model", action="append", default=[], help="LABEL=glb[:meta.json]")
    ap.add_argument("--json", type=Path, help="append the table rows here")
    a = ap.parse_args()
    pdir = a.data / "parts" / a.part_id
    part = json.loads((pdir / "part.json").read_text())
    models = []
    for spec in a.model:
        label, _, rest = spec.partition("=")
        glb, _, meta_path = rest.partition(":")
        if not Path(glb).exists():
            print(f"{a.part_id} | {label} | missing {glb}")
            continue
        meta = json.loads(Path(meta_path).read_text()) if meta_path else {}
        meta["_stats"] = stats(Path(glb), meta, part["dims_mm"])
        models.append((label, Path(glb), meta))
    rows = sheet(part, pdir / "image.jpg", models, a.out)
    for (label, _, _), row in zip(models, rows, strict=True):
        print(f"{a.part_id} | {label} | {json.dumps(row)}")
    if a.json:
        table = json.loads(a.json.read_text()) if a.json.exists() else {}
        table[a.part_id] = {label: row for (label, _, _), row in zip(models, rows, strict=True)}
        a.json.write_text(json.dumps(table, indent=2))


if __name__ == "__main__":
    main()
