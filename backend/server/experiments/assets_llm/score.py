"""Score a model.glb against its part.json (docs/research/assets-llm/r3-toolchain.md §5).

Geometry checks need only trimesh. Photo metrics need pyrender + scipy; CLIP needs torch +
open_clip (clip_sim is None when they are not importable).

    uv run --with pyrender --with scipy --with open_clip_torch \
        --index https://download.pytorch.org/whl/cpu \
        python -m server.experiments.assets_llm.score data/parts/<id> [--glb candidate.glb]
(`--no-clip` skips torch; the CPU index keeps torch to the ~200 MB CPU wheel.)
"""

import argparse
import json
from functools import cache
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image

MASK = 128  # silhouette compare resolution
TOL_ORIGIN = 0.02  # origin checks: fraction of the matching dim
TOL_DIMS_PCT = 2.0  # bbox_ok when every axis is within this


def geometry_scores(glb: Path, dims_mm: dict) -> dict:
    """Load, watertight, bbox vs dims (X=w, Y=h, Z=d), origin at mount-face centre, size."""
    try:
        scene = trimesh.load(str(glb), force="scene")
        geoms = [g for g in scene.dump() if isinstance(g, trimesh.Trimesh) and len(g.faces)]
    except Exception as exc:  # noqa: BLE001 -- any parser error means "does not load"
        return {"loads": False, "error": str(exc)[:200]}
    if not geoms:
        return {"loads": False, "error": "no triangles"}
    # glTF splits vertices at normal/UV seams; merge on position only before topology checks.
    # Checked per primitive: touching parts (knob on a plate) share edges once concatenated.
    watertight = []
    for g in geoms:
        g = g.copy()
        g.merge_vertices(merge_tex=True, merge_norm=True)
        watertight.append(bool(g.is_watertight))
    target = np.array([dims_mm["w"], dims_mm["h"], dims_mm["d"]]) / 1000.0
    lo, hi = scene.bounds
    ext = hi - lo
    err_pct = (ext - target) / target * 100
    centre = (lo + hi) / 2
    origin_off = np.array([centre[0] / target[0], centre[1] / target[1], lo[2] / target[2]])
    return {
        "loads": True,
        "primitives": len(geoms),
        "watertight": all(watertight),
        "watertight_frac": round(sum(watertight) / len(watertight), 2),
        "triangles": int(sum(len(g.faces) for g in geoms)),
        "file_kb": round(glb.stat().st_size / 1024, 1),
        "bbox_mm": [round(float(v) * 1000, 1) for v in ext],
        "dims_err_pct": [round(float(v), 2) for v in err_pct],
        "bbox_ok": bool(np.all(np.abs(err_pct) <= TOL_DIMS_PCT)),
        # X/Y centred and the part starting at Z=0 (mount face), as fractions of each dim
        "origin_off": [round(float(v), 3) for v in origin_off],
        "origin_ok": bool(np.all(np.abs(origin_off) <= TOL_ORIGIN)),
    }


def photo_mask(photo: Path, tol: int = 12) -> np.ndarray:
    """Foreground of a plain-background product photo: flood the border colour in from the edges
    (so a white product on white keeps its body), keep the largest blob (drops badges and
    accessories), fill holes. ponytail: no learned matting; add rembg if lifestyle photos matter."""
    from scipy import ndimage

    img = np.asarray(Image.open(photo).convert("RGB"), dtype=np.int16)
    border = np.concatenate([img[0], img[-1], img[:, 0], img[:, -1]])
    near_bg = np.abs(img - np.median(border, axis=0)).max(axis=2) <= tol
    lab, _ = ndimage.label(near_bg)
    edge = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
    fg = ndimage.binary_opening(~np.isin(lab, edge[edge > 0]))
    lab, n = ndimage.label(fg)
    if n > 1:
        fg = lab == (np.argmax(np.bincount(lab.ravel())[1:]) + 1)
    return ndimage.binary_fill_holes(fg)


def _norm_mask(mask: np.ndarray) -> np.ndarray:
    """Crop to the foreground bbox, pad to square (keeps aspect), resize to MASK x MASK."""
    ys, xs = np.nonzero(mask)
    if not len(ys):
        return np.zeros((MASK, MASK), bool)
    m = mask[ys.min() : ys.max() + 1, xs.min() : xs.max() + 1]
    (h, w), s = m.shape, max(m.shape)
    sq = np.zeros((s, s), bool)
    y0, x0 = (s - h) // 2, (s - w) // 2
    sq[y0 : y0 + h, x0 : x0 + w] = m
    return np.asarray(Image.fromarray(sq).resize((MASK, MASK), Image.NEAREST))


def iou(a: np.ndarray, b: np.ndarray) -> float:
    a, b = _norm_mask(a), _norm_mask(b)
    return float((a & b).sum() / max((a | b).sum(), 1))


# Photo viewpoints are unknown: compare against a ring of yaw angles (front, 3/4s, side) at a
# slight elevation and keep the best. ponytail: 7 fixed views; a real pose search if this is noisy.
PHOTO_VIEWS = {
    f"yaw{yaw}": (np.sin(np.radians(yaw)), 0.25, np.cos(np.radians(yaw)))
    for yaw in (0, 30, 45, 60, 90, -30, -45)
}


@cache
def _clip():
    import open_clip
    import torch

    model, _, preprocess = open_clip.create_model_and_transforms(
        "ViT-B-32", pretrained="laion2b_s34b_b79k"
    )
    model.eval()
    return model, preprocess, torch


def clip_similarity(photo: Path, renders: list[np.ndarray]) -> float:
    """Max cosine similarity between the photo and any render (ViT-B/32, CPU)."""
    model, preprocess, torch = _clip()
    imgs = [Image.open(photo).convert("RGB")] + [Image.fromarray(r) for r in renders]
    with torch.no_grad():
        f = model.encode_image(torch.stack([preprocess(i) for i in imgs]))
    f = f / f.norm(dim=-1, keepdim=True)
    return float((f[1:] @ f[0]).max())


def photo_scores(glb: Path, photo: Path, use_clip: bool = True) -> dict:
    from server.experiments.assets_llm.render import load_scene, render_views

    views = render_views(load_scene(glb), size=256, views=PHOTO_VIEWS)
    pm = photo_mask(photo)
    ious = {k: iou(m, pm) for k, (_, m) in views.items()}
    best = max(ious, key=ious.get)
    out = {"sil_iou": round(ious[best], 3), "sil_view": best}
    if use_clip:
        try:
            out["clip_sim"] = round(clip_similarity(photo, [rgb for rgb, _ in views.values()]), 3)
        except ImportError:
            out["clip_sim"] = None
    return out


def score_part(part_dir: Path, glb: Path | None = None, use_clip: bool = True) -> dict:
    part = json.loads((part_dir / "part.json").read_text())
    tier = "candidate" if glb else part["asset"]["tier"]
    glb = glb or part_dir / "model.glb"
    out = {"id": part["id"], "tier": tier, **geometry_scores(glb, part["dims_mm"])}
    photo = part_dir / "image.jpg"
    if out["loads"] and photo.exists():
        out.update(photo_scores(glb, photo, use_clip))
    return out


def _selfcheck() -> None:
    box = trimesh.creation.box(extents=(0.1, 0.2, 0.3))
    box.apply_translation((0, 0, 0.15))  # mount face at Z=0
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        p = Path(tmp) / "b.glb"
        box.export(p)
        s = geometry_scores(p, {"w": 100, "h": 200, "d": 300})
        assert s["watertight"] and s["bbox_ok"] and s["origin_ok"], s
        s = geometry_scores(p, {"w": 100, "h": 300, "d": 200})  # h/d swapped
        assert not s["bbox_ok"] and s["origin_ok"], s
    m = np.zeros((50, 80), bool)
    m[10:40, 20:60] = True
    assert iou(m, np.pad(m, 30)) == 1.0  # translation/scale invariant
    print("selfcheck ok")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("part_dirs", nargs="*", type=Path)
    ap.add_argument("--glb", type=Path, help="score this GLB instead of <part_dir>/model.glb")
    ap.add_argument("--no-clip", action="store_true")
    ap.add_argument("--selfcheck", action="store_true")
    a = ap.parse_args()
    if a.selfcheck:
        _selfcheck()
    for d in a.part_dirs:
        print(json.dumps(score_part(d, a.glb, not a.no_clip)))
