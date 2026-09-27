"""Headless GLB renderer for LLM feedback and scoring (docs/research/assets-llm/r3-toolchain.md).

Contract frame (docs/api.md §2): metres, +Y up, X = width, Z = out of the mounting face.
Views: front (camera on +Z), side (camera on +X), three-quarter (from +X+Y+Z). All views share one
orthographic scale (the bounding-sphere radius), so relative sizes read true across panels.

Backend: pyrender over EGL: the iGPU through Mesa (0.15 s per sheet here), or Mesa llvmpipe on
a GPU-less box (EGL_DEVICE_ID=1 here, 0.3 s per sheet). Not a project dependency:
    uv run --with pyrender python -m server.experiments.assets_llm.render \
        data/parts/<id>/model.glb out.jpg --photo data/parts/<id>/image.jpg
"""

import os

os.environ.setdefault("PYOPENGL_PLATFORM", "egl")

import argparse
from pathlib import Path

import numpy as np
import pyrender
import trimesh
from PIL import Image, ImageDraw

BG = (196, 202, 210)  # blue-grey: white appliances and chrome both separate from it
VIEWS = {  # name -> camera direction (from the object towards the camera)
    "front": (0.0, 0.0, 1.0),
    "side": (1.0, 0.0, 0.0),
    "3/4": (1.0, 0.75, 1.25),
}

_renderers: dict[int, pyrender.OffscreenRenderer] = {}


def _renderer(size: int) -> pyrender.OffscreenRenderer:
    # One GL context per size; creating one costs ~0.3 s.
    if size not in _renderers:
        _renderers[size] = pyrender.OffscreenRenderer(size, size)
    return _renderers[size]


def look_at(eye: np.ndarray, target: np.ndarray) -> np.ndarray:
    """Camera-to-world pose, camera looking down its -Z at `target`, +Y as up where possible."""
    fwd = target - eye
    fwd = fwd / np.linalg.norm(fwd)
    up = np.array([0.0, 1.0, 0.0]) if abs(fwd[1]) < 0.99 else np.array([0.0, 0.0, -1.0])
    right = np.cross(fwd, up)
    right /= np.linalg.norm(right)
    pose = np.eye(4)
    pose[:3, 0], pose[:3, 1], pose[:3, 2], pose[:3, 3] = right, np.cross(right, fwd), -fwd, eye
    return pose


def load_scene(glb: str | Path) -> trimesh.Scene:
    return trimesh.load(str(glb), force="scene")


def _pyrender_scene(tm: trimesh.Scene) -> pyrender.Scene:
    scene = pyrender.Scene(bg_color=[c / 255 for c in BG] + [1.0], ambient_light=[0.35] * 3)
    for node in tm.graph.nodes_geometry:
        transform, geom_name = tm.graph[node]
        geom = tm.geometry[geom_name]
        if isinstance(geom, trimesh.Trimesh) and len(geom.faces):
            # Flat shading: CAD output has hard edges, smoothing across them reads as blobs.
            scene.add(pyrender.Mesh.from_trimesh(geom, smooth=False), pose=transform)
    return scene


def render_views(
    tm: trimesh.Scene, size: int = 384, views: dict | None = None
) -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """{view: (rgb uint8 HxWx3, mask bool HxW)} with a shared orthographic scale."""
    views = views or VIEWS
    scene = _pyrender_scene(tm)
    center = tm.bounds.mean(axis=0)
    radius = max(float(np.linalg.norm(tm.extents)) / 2, 1e-6)
    cam = pyrender.OrthographicCamera(
        xmag=radius * 1.05, ymag=radius * 1.05, znear=radius * 0.01, zfar=radius * 6
    )
    cam_node = scene.add(cam)
    key = scene.add(pyrender.DirectionalLight(intensity=2.5))
    fill = scene.add(pyrender.DirectionalLight(intensity=1.0))
    out = {}
    for name, direction in views.items():
        d = np.asarray(direction, float)
        d /= np.linalg.norm(d)
        pose = look_at(center + d * radius * 3, center)
        scene.set_pose(cam_node, pose)
        # key light from upper-left of the camera, fill from the right, so faces separate
        scene.set_pose(key, look_at(center + (d + [-0.4, 0.8, 0.2]) * radius * 3, center))
        scene.set_pose(fill, look_at(center + (d + [0.8, -0.2, 0.0]) * radius * 3, center))
        rgb, depth = _renderer(size).render(scene)
        out[name] = (rgb.copy(), depth > 0)
    return out


def _panel(img: Image.Image, label: str) -> Image.Image:
    ImageDraw.Draw(img).text((6, 4), label, fill=(40, 40, 40))
    return img


def contact_sheet(
    glb: str | Path,
    out: str | Path | None = None,
    photo: str | Path | None = None,
    size: int = 384,
) -> Image.Image:
    """2x2 sheet: product photo (or blank) | front / side | 3/4. Header shows the bbox in mm."""
    tm = load_scene(glb)
    views = render_views(tm, size)
    w, h, d = (tm.extents * 1000).round().astype(int)
    sheet = Image.new("RGB", (size * 2, size * 2 + 18), (255, 255, 255))
    ImageDraw.Draw(sheet).text(
        (6, 3), f"{Path(glb).parent.name}/{Path(glb).name}  bbox w{w} h{h} d{d} mm", fill=(0, 0, 0)
    )
    tiles = []
    if photo and Path(photo).exists():
        p = Image.open(photo).convert("RGB")
        p.thumbnail((size, size))
        tile = Image.new("RGB", (size, size), (255, 255, 255))
        tile.paste(p, ((size - p.width) // 2, (size - p.height) // 2))
        tiles.append(_panel(tile, "photo"))
    else:
        tiles.append(_panel(Image.new("RGB", (size, size), BG), "no photo"))
    labels = {"front": "front (+Z)", "side": "side (+X)", "3/4": "3/4"}
    tiles += [_panel(Image.fromarray(views[k][0]), labels.get(k, k)) for k in views]
    for i, tile in enumerate(tiles[:4]):
        sheet.paste(tile, ((i % 2) * size, 18 + (i // 2) * size))
    if out:
        sheet.save(out, quality=85)
    return sheet


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("glb")
    ap.add_argument("out")
    ap.add_argument("--photo")
    ap.add_argument("--size", type=int, default=384)
    a = ap.parse_args()
    contact_sheet(a.glb, a.out, a.photo, a.size)
