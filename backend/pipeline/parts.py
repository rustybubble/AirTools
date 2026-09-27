"""Parts stage (docs/research/p1-parts/r1-segment-remove.md §4): split the scene mesh into named,
removable components and model the cavity each one leaves. Runs after the structure layer.

"Geometry proposes, the VLM names, SAM draws the pixels, the faces vote, the planes cut":
  1. keyframes: evenly spaced posed thumbs;
  2. name: Groq vision (`server.llm`, role `vision`) boxes every appliance in each square-padded
     thumb (cached per image hash); boxes are matched to the projected S4 rectangles by IoU;
  3. masks: SAM 2.1 box -> mask in `AIRTOOLS_SAM_PYTHON` (`pipeline/parts_sam.py`). Without it the
     stage reuses masks already in `<work>/masks/<frame>_<class>.png`, else the VLM box itself;
  4. vote: faces seen in >= 2 masked views with >= 50 % mask share go to the best component, then
     its largest connected piece over the vertex-welded mesh (the atlas seams split it otherwise);
  5. bound: floor, back, front, sides and top from the structure layer, snapped to gravity and
     the Manhattan axes: an unobserved surface is only ever extrapolated from a snapped plane;
  6. cut: faces inside the box that are voted to the component or claimed by nobody; faces other
     components claim and unvoted faces in the 2 cm under the top stay (the countertop sag, R1
     §3.4). A horizontal plane just above the top (the countertop) is re-capped when the cut opens
     a hole in it, measured top-down;
  7. fill: flat-coloured quads on the box's floor, back, sides and top, coloured from the
     neighbouring surfaces, flagged `estimated`.

Writes, beside `mesh.r<rev>.glb` (left untouched): `mesh.parts.r<rev>.glb` (nodes `background` and
`part_<id>`, one shared material), `collision.parts.r<rev>.glb` (same node names),
`cavity.r<rev>.glb` (nodes `cavity_<id>`), `parts.r<rev>.json` and a `parts` entry in scene.json.
Node names use `_`, not R1's `/`: three.js strips `/` from names and Unity's `Transform.Find`
reads it as a path.

    python -m pipeline.parts scene/kitchen --components dishwasher,fridge,range,sink_cabinet
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import functools
import hashlib
import io
import json
import logging
import os
import re
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image
from scipy import ndimage
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree

from pipeline.package import write_json_atomic

logger = logging.getLogger(__name__)

SCHEMA = "airtools.parts/1"
SAM_MODEL = "facebook/sam2.1-hiera-tiny"
CELL = 0.01  # top-down hole grid, m
COS10, COS15, SIN15 = np.cos(np.radians(10)), np.cos(np.radians(15)), np.sin(np.radians(15))
ALIASES = {
    "dishwasher": "dishwasher", "fridge": "fridge", "refrigerator": "fridge",
    "range": "range", "stove": "range", "oven": "range", "cooktop": "range",
    "microwave": "microwave", "base cabinet": "base_cabinet", "sink cabinet": "base_cabinet",
    "sink base": "base_cabinet", "sink base cabinet": "base_cabinet",
}  # fmt: skip
PREFIX = {
    "dishwasher": "dw",
    "fridge": "fr",
    "range": "rg",
    "microwave": "mw",
    "base_cabinet": "bc",
}
KIND = {"base_cabinet": "cabinet"}  # everything else here is an appliance
PROMPT = (
    "This is a frame from a video of a kitchen. List every appliance, fixture and cabinet unit "
    "visible (e.g. dishwasher, range, fridge, microwave, sink, base cabinet, wall cabinet, "
    "countertop). For each give a short lowercase label and its bounding box [x0,y0,x1,y1] as "
    "integers 0..1000 relative to the image width/height (top-left origin). The photo occupies the "
    "top part; the black band below is padding. Only list things you can actually see; include "
    "partly visible ones."
)


def canonical(label: str) -> str | None:
    return ALIASES.get(label.lower().replace("_", " ").strip())


# ---------------------------------------------------------------- cameras and frames


def project(P: np.ndarray, cam: dict, W: int) -> tuple[np.ndarray, np.ndarray]:
    """Scene points -> thumb pixels (intrinsics scaled by W / cam['w']) and camera depth."""
    X = np.asarray(P, float) @ np.asarray(cam["R"]).T + np.asarray(cam["t"])
    s, z = W / cam["w"], X[:, 2]
    with np.errstate(divide="ignore", invalid="ignore"):
        uv = np.c_[
            (cam["fx"] * X[:, 0] / z + cam["cx"]) * s, (cam["fy"] * X[:, 1] / z + cam["cy"]) * s
        ]
    return uv, z


@dataclass
class Frame:
    """Component frame: r along the run (the viewer's right), up = gravity, d out of the cavity."""

    r: np.ndarray
    up: np.ndarray
    d: np.ndarray

    def to(self, P) -> np.ndarray:
        P = np.asarray(P, float)
        return np.stack([P @ self.r, P @ self.up, P @ self.d], -1)

    def world(self, rud) -> np.ndarray:
        q = np.asarray(rud, float)
        return q[..., :1] * self.r + q[..., 1:2] * self.up + q[..., 2:3] * self.d


def component_frame(hint: np.ndarray, axes: list, up: np.ndarray) -> Frame:
    """Gravity-snapped frame: d = the horizontal Manhattan axis closest to `hint` (component ->
    cameras), so the cavity walls are vertical and square to the run whatever the plane fits say."""
    h = hint - (hint @ up) * up
    cands = [np.asarray(a, float) for a in axes if abs(np.asarray(a) @ up) < 0.3]
    cands = [c - (c @ up) * up for c in cands] or [h]
    cands = [c / np.linalg.norm(c) for c in cands]
    a = max(cands, key=lambda c: abs(c @ h))
    d = a * np.sign(a @ h)
    return Frame(np.cross(up, d), up, d)


# ---------------------------------------------------------------- 2. name


def pad_square(path: Path) -> bytes:
    im = Image.open(path).convert("RGB")
    sq = Image.new("RGB", (max(im.size),) * 2)
    sq.paste(im, (0, 0))
    buf = io.BytesIO()
    sq.save(buf, "JPEG", quality=90)
    return buf.getvalue()


def vlm_detect(thumbs: dict[str, Path], cache: Path) -> tuple[dict[str, list[dict]], dict]:
    """{frame: [{label, box (thumb px)}]} from Groq vision, one call per square-padded thumb,
    cached on disk by image + prompt hash. Returns (detections, stats)."""
    import pydantic

    from server import keys, llm

    class Item(pydantic.BaseModel):
        label: str
        box: list[int]

    class Items(pydantic.BaseModel):
        items: list[Item]

    provider, model = llm.resolve_role("vision")
    cache.mkdir(parents=True, exist_ok=True)
    stats = {"model": f"{provider}:{model}", "calls": 0, "cached": 0, "failed": 0, "call_s": []}
    sem = asyncio.Semaphore(1)  # Groq free tier: ~8K tokens/min per key, ~2K per call

    async def one(fid: str, path: Path):
        jpg = pad_square(path)
        side = max(Image.open(path).size)
        w, h = Image.open(path).size
        key = hashlib.sha1(jpg + PROMPT.encode() + model.encode()).hexdigest()[:16]
        hit = cache / f"{fid}-{key}.json"
        if hit.exists():
            stats["cached"] += 1
            items = json.loads(hit.read_text())
        else:
            b64 = base64.b64encode(jpg).decode()
            msg = [{"role": "user", "content": [
                {"type": "text", "text": PROMPT},
                {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{b64}"}}]}]  # fmt: skip
            async with sem:
                t = time.time()
                r = None
                for _ in range(6):
                    try:
                        r = await llm.extract("vision", msg, Items)
                        break
                    except keys.KeysExhausted:  # per-minute 429s: the pool cools keys for 15 min
                        await asyncio.sleep(30)
                        keys.reset_all()
                    except Exception as exc:  # noqa: BLE001 -- one bad frame must not stop the stage
                        logger.warning("vlm %s failed: %r", fid, exc)
                        break
                if r is None:
                    stats["failed"] += 1
                    return fid, []
                stats["calls"] += 1
                stats["call_s"].append(round(time.time() - t, 1))
            items = [i.model_dump() for i in r.items]
            hit.write_text(json.dumps(items))
        out = []
        for i in items:
            if len(i["box"]) != 4:
                continue
            x0, y0, x1, y1 = (v / 1000 * side for v in i["box"])
            box = [max(0, x0), max(0, y0), min(w, x1), min(h, y1)]
            if box[2] - box[0] > 2 and box[3] - box[1] > 2:
                out.append({"label": i["label"], "box": box})
        return fid, out

    async def main():
        return await asyncio.gather(*(one(f, p) for f, p in thumbs.items()))

    return dict(asyncio.run(main())), stats


# ---------------------------------------------------------------- 3. proposals


def box_iou(a, b) -> float:
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    u = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - ix * iy
    return ix * iy / u if u > 0 else 0.0


@dataclass
class Component:
    cls: str
    id: str
    boxes: dict[str, list[float]]  # frame -> VLM box (thumb px)
    rect: dict | None = None  # the matched S4 object
    rect_iou: float = 0.0
    masks: dict[str, np.ndarray] = field(default_factory=dict)


def proposals(dets: dict, cams: dict, objects: list, wanted: list[str], W: int, H: int):
    """One component per wanted class (the largest box of that class per frame), named by the VLM
    and matched to the S4 rectangle whose projected bbox overlaps its boxes best (mean IoU >= 0.4).
    ponytail: one instance per class; repeated units (cabinet doors) need 3D instance merging."""
    comps = []
    for cls in wanted:
        boxes = {}
        for fid, items in dets.items():
            mine = [i["box"] for i in items if canonical(i["label"]) == cls]
            if mine:
                boxes[fid] = max(mine, key=lambda b: (b[2] - b[0]) * (b[3] - b[1]))
        if not boxes:
            logger.warning("no VLM detection for %s", cls)
            continue
        best, best_iou = None, 0.0
        for o in objects:
            ious = []
            for fid, b in boxes.items():
                uv, z = project(o["corners3d"], cams[fid], W)
                if (z <= 0).any():
                    ious.append(0.0)
                    continue
                r = [*np.clip(uv.min(0), 0, [W, H]), *np.clip(uv.max(0), 0, [W, H])]
                ious.append(box_iou(b, r))
            if np.mean(ious) > best_iou:
                best, best_iou = o, float(np.mean(ious))
        comps.append(Component(cls, f"{PREFIX.get(cls, cls[:2])}1", boxes,
                               best if best_iou >= 0.4 else None, best_iou))  # fmt: skip
    return comps


# ---------------------------------------------------------------- 4. masks


def make_masks(comps, thumbs: dict[str, Path], mask_dir: Path, sam_python: str | None) -> dict:
    """Fill `comp.masks`. Reuses `<mask_dir>/<frame>_<class>.png`; runs SAM for the missing ones
    when `sam_python` is set; falls back to the VLM box as the mask."""
    mask_dir.mkdir(parents=True, exist_ok=True)
    todo = [
        {"image": str(thumbs[f]), "box": b, "out": str(mask_dir / f"{f}_{c.cls}.png")}
        for c in comps
        for f, b in c.boxes.items()
        if not (mask_dir / f"{f}_{c.cls}.png").exists()
    ]
    stats = {"source": "precomputed" if not todo else None, "sam_images": 0}
    if todo and sam_python:
        job = mask_dir / "job.json"
        job.write_text(json.dumps({"model": SAM_MODEL, "items": todo}))
        worker = Path(__file__).with_name("parts_sam.py")
        try:
            out = subprocess.run([sam_python, str(worker), str(job)], capture_output=True,
                                 text=True, timeout=3600, check=True)  # fmt: skip
            stats.update(json.loads(out.stdout.strip().splitlines()[-1]), source=SAM_MODEL)
            stats["sam_images"] = len(todo)
        except (subprocess.SubprocessError, OSError, ValueError, IndexError) as exc:
            logger.warning("SAM worker failed, falling back to VLM boxes: %s", exc)
    for c in comps:
        for f, b in c.boxes.items():
            p = mask_dir / f"{f}_{c.cls}.png"
            if p.exists():
                c.masks[f] = np.array(Image.open(p)) > 0
                stats["source"] = stats["source"] or SAM_MODEL
            else:
                w, h = Image.open(thumbs[f]).size
                m = np.zeros((h, w), bool)
                m[int(b[1]) : int(np.ceil(b[3])), int(b[0]) : int(np.ceil(b[2]))] = True
                c.masks[f] = m
                stats["source"] = "vlm-box"
    return stats


# ---------------------------------------------------------------- 5. vote


def visible_faces(C, V, cam, W, H):
    """Faces whose centroid passes a splat z-buffer of centroids + vertices (3x3 min filter), as
    (face idx, u, v). ponytail: splat, not a rasterizer; pipeline/preview.render could return a
    face-id buffer if thin structures ever leak through."""
    uvf, zf = project(C, cam, W)
    uvv, zv = project(V, cam, W)
    zb = np.full((H, W), np.inf)
    for uv, z in ((uvf, zf), (uvv, zv)):
        ok = (z > 0.05) & (uv[:, 0] >= 0) & (uv[:, 0] < W) & (uv[:, 1] >= 0) & (uv[:, 1] < H)
        np.minimum.at(zb, (uv[ok, 1].astype(int), uv[ok, 0].astype(int)), z[ok])
    zb = ndimage.minimum_filter(zb, 3)
    ok = (zf > 0.05) & (uvf[:, 0] >= 0) & (uvf[:, 0] < W) & (uvf[:, 1] >= 0) & (uvf[:, 1] < H)
    idx = np.nonzero(ok)[0]
    u, v = uvf[idx, 0].astype(int), uvf[idx, 1].astype(int)
    keep = zf[idx] <= zb[v, u] * 1.01 + 0.005
    return idx[keep], u[keep], v[keep]


def welded_faces(V: np.ndarray, F: np.ndarray) -> np.ndarray:
    """Faces re-indexed onto vertices merged by position (UV seams duplicate them)."""
    _, inv = np.unique(np.round(V, 6), axis=0, return_inverse=True)
    return inv.reshape(-1)[F]


def largest_piece(Fw: np.ndarray, sel: np.ndarray) -> np.ndarray:
    """The largest vertex-connected piece of the selected faces (bool mask over all faces)."""
    li = np.nonzero(sel)[0]
    if len(li) == 0:
        return sel
    n = int(Fw.max()) + 1
    inc = coo_matrix((np.ones(3 * len(li)), (np.repeat(np.arange(len(li)), 3), Fw[li].ravel())),
                     shape=(len(li), n)).tocsr()  # fmt: skip
    _, cc = connected_components(inc @ inc.T, directed=False)
    out = np.zeros_like(sel)
    out[li[cc == np.bincount(cc).argmax()]] = True
    return out


def vote(C, V, Fw, cams, comps, W, H, min_views=2, min_share=0.5) -> tuple[np.ndarray, dict]:
    """Per-face component index (-1 = none) by multi-view mask voting, largest piece per component."""
    K = len(comps)
    seen, hit = np.zeros((K, len(C))), np.zeros((K, len(C)))
    frames = sorted({f for c in comps for f in c.masks})
    for f in frames:
        idx, u, v = visible_faces(C, V, cams[f], W, H)
        for k, c in enumerate(comps):
            if f in c.masks:
                seen[k, idx] += 1
                hit[k, idx] += c.masks[f][v, u]
    share = hit / np.maximum(seen, 1)
    ok = (seen >= min_views) & (share >= min_share)
    raw = np.where(ok.any(0), np.where(ok, share, -1).argmax(0), -1)
    labels = np.full(len(C), -1)
    for k in range(K):
        labels[largest_piece(Fw, raw == k)] = k
    return labels, {"frames": frames, "views_per_comp": [len(c.masks) for c in comps]}


# ---------------------------------------------------------------- 6. bound


def poly3d(p: dict) -> np.ndarray | None:
    pts = np.asarray(p.get("polygon3d") or p.get("polygon") or [], float)
    if pts.ndim != 2 or len(pts) < 3:
        return None
    if pts.shape[1] == 3:
        return pts
    n, u = np.asarray(p["normal"], float), np.asarray(p["u"], float)
    return np.asarray(p["origin"]) + pts[:, :1] * u + pts[:, 1:2] * np.cross(n, u)


def plane_coord(p: dict, fr: Frame, r, y=None, d=None):
    """The fitted (unsnapped) plane's missing coordinate: d at (r, y) or y at (r, d)."""
    n = np.asarray(p["normal"], float)
    nr, nu, nd = n @ fr.r, n @ fr.up, n @ fr.d
    if d is None:
        return -(p["offset"] + nr * np.asarray(r) + nu * np.asarray(y)) / nd
    return -(p["offset"] + nr * np.asarray(r) + nd * np.asarray(d)) / nu


@dataclass
class Extent:
    r0: float
    r1: float
    y0: float
    y1: float
    d1: float  # the component's own front


def _overlaps(lo, hi, a, b) -> bool:
    return hi >= a and lo <= b


def _plane_cands(planes, fr: Frame, horizontal: bool):
    for p in planes:
        q = poly3d(p)
        if q is None:
            continue
        n = np.asarray(p["normal"], float)
        nu = abs(n @ fr.up)
        if horizontal and nu < COS10:
            continue
        if not horizontal:
            nh = n - (n @ fr.up) * fr.up
            if nu > SIN15 or abs(nh @ fr.d) / np.linalg.norm(nh) < COS15:
                continue
        yield p, fr.to(q)


def _tilt_deg(p: dict, axis: np.ndarray) -> float:
    return float(np.degrees(np.arccos(min(1.0, abs(np.asarray(p["normal"]) @ axis)))))


def _plane_bound(p, value, pred, sigma_fit=None) -> dict:
    """Bound from a snapped plane: sigma = hypot(plane rms, half the snap delta over the face)."""
    delta = np.asarray(pred) - value
    rms = p.get("rms_m") or 0.005
    return {"value": float(value), "plane": p["id"], "observed": False,
            "snap_delta_m": [float(delta.min()), float(delta.max())],
            "sigma": float(np.hypot(sigma_fit or rms, np.abs(delta).max() / 2))}  # fmt: skip


def _edge_cluster(hits: list[tuple[float, float, str]], pick: str) -> dict | None:
    """hits = (sort key, coordinate, edge id); chain-cluster at 1 cm and take the first or last."""
    if not hits:
        return None
    hits.sort()
    groups, cur = [], [hits[0]]
    for h in hits[1:]:
        if h[0] - cur[-1][0] > 0.01:
            groups.append(cur)
            cur = []
        cur.append(h)
    groups.append(cur)
    g = groups[0] if pick == "first" else groups[-1]
    vals = np.array([h[1] for h in g])
    return {"value": float(vals.mean()), "edges": sorted(h[2] for h in g), "observed": False,
            "sigma": float(max(vals.std(), 0.002))}  # fmt: skip


def cavity_bounds(
    S: dict, fr: Frame, ext: Extent, own: set[str], front_pid: str | None, d_seen: float | None
) -> dict:
    """Floor, top, left, right, back, front of the opening, each from structure snapped to the
    frame. A side with no structure falls back to the component's own extent (`source`).
    `d_seen`: the deepest the component's own faces were seen (the back lies behind it)."""
    planes = S.get("planes", [])
    P = {p["id"]: p for p in planes}
    E = []
    for e in S.get("edges", []):
        m = re.match(r"^(o\d+)e\d+$", e["id"])
        if m and m.group(1) in own:
            continue
        a, b = fr.to(e["a"]), fr.to(e["b"])
        L = np.linalg.norm(b - a)
        if L > 0.05:
            E.append((e["id"], a, b, np.abs(b - a) / L))
    rc = (ext.r0 + ext.r1) / 2
    out: dict[str, dict] = {}

    # sides: the outermost vertical-edge cluster within 1.5 cm inside to 4 cm outside each end
    for name, r_end, sgn in (("left", ext.r0, -1), ("right", ext.r1, 1)):
        hits = []
        for eid, a, b, dirn in E:
            m = (a + b) / 2
            ov = min(max(a[1], b[1]), ext.y1) - max(min(a[1], b[1]), ext.y0)
            if dirn[1] < COS10 or abs(m[2] - ext.d1) > 0.08 or ov < 0.4 * (ext.y1 - ext.y0):
                continue
            off = (m[0] - r_end) * sgn
            if -0.015 <= off <= 0.04:
                hits.append((off, m[0], eid))
        out[name] = _edge_cluster(hits, "last") or {
            "value": r_end, "sigma": 0.01, "observed": False, "source": "component"}  # fmt: skip

    # top: the lowest long horizontal line (a counter lip) at or just above the component's top
    hits = []
    for eid, a, b, dirn in E:
        m, lo, hi = (a + b) / 2, min(a[0], b[0]), max(a[0], b[0])
        cover = min(hi, ext.r1) - max(lo, ext.r0)
        if dirn[0] < COS10 or abs(m[2] - ext.d1) > 0.08 or cover < 0.5 * (ext.r1 - ext.r0):
            continue
        if not (lo < ext.r0 - 0.05 or hi > ext.r1 + 0.05):
            continue  # no longer than the component: its own detail, not a neighbour's
        if ext.y1 - 0.015 <= m[1] <= ext.y1 + 0.06:
            hits.append((m[1], m[1], eid))
    out["top"] = _edge_cluster(hits, "first") or {
        "value": ext.y1, "sigma": 0.01, "observed": False, "source": "component"}  # fmt: skip
    # ... or a lower horizontal plane over the footprint there (a cabinet underside)
    for p, q in _plane_cands(planes, fr, horizontal=True):
        yq = q[:, 1].mean()
        over = _overlaps(q[:, 0].min(), q[:, 0].max(), ext.r0, ext.r1) and _overlaps(
            q[:, 2].min(), q[:, 2].max(), ext.d1 - 0.6, ext.d1
        )
        low = yq < out["top"]["value"] or out["top"].get("source")
        if over and low and ext.y1 - 0.015 <= yq <= ext.y1 + 0.06:
            out["top"] = {"value": float(yq), "plane": p["id"], "observed": False,
                          "sigma": float(p.get("rms_m") or 0.005)}  # fmt: skip
    top = out["top"]["value"]

    # floor: the largest horizontal plane below, near the footprint; height = its outline's mean
    best = None
    for p, q in _plane_cands(planes, fr, horizontal=True):
        if not _overlaps(q[:, 0].min(), q[:, 0].max(), ext.r0 - 0.3, ext.r1 + 0.3):
            continue
        near = q[(q[:, 0] > ext.r0 - 0.3) & (q[:, 0] < ext.r1 + 0.3)]
        near = near if len(near) else q  # an outline with no vertex beside the footprint
        if near[:, 1].mean() > ext.y0 + 0.02:
            continue
        if not _overlaps(q[:, 2].min(), q[:, 2].max(), ext.d1 - 1.0, ext.d1 + 1.0):
            continue
        if best is None or (p.get("area_m2") or 0) > (best[0].get("area_m2") or 0):
            best = (p, near[:, 1].mean())

    # back: a vertical plane facing d, 0.2-1.5 m behind the front, beside the footprint; the wall
    # reaching down to within 10 cm of the top (a backsplash) beats a bigger one higher up
    # (the wall-cabinet fronts), then the largest
    back, back_key = None, None
    for p, q in _plane_cands(planes, fr, horizontal=False):
        dm = q[:, 2].mean()
        if not (ext.d1 - 1.5 < dm < min(ext.d1 - 0.2, (d_seen or np.inf) - 0.01)):
            continue
        if not _overlaps(q[:, 0].min(), q[:, 0].max(), ext.r0 - 0.3, ext.r1 + 0.3):
            continue
        key = (q[:, 1].min() - top <= 0.1, p.get("area_m2") or 0)
        if back is None or key > back_key:
            back, back_key = (p, dm), key

    # front: the plane under the S4 rectangle (its parent for a relief plane), else a vertical
    # plane facing d within 10 cm of the component's front; evaluated at the opening's centre
    fp = P.get(front_pid) if front_pid else None
    if fp is not None and fp.get("parent") in P:
        fp = P[fp["parent"]]
    if fp is None:
        for p, q in _plane_cands(planes, fr, horizontal=False):
            near = abs(q[:, 2].mean() - ext.d1) < 0.1
            beside = _overlaps(q[:, 0].min(), q[:, 0].max(), ext.r0 - 0.5, ext.r1 + 0.5)
            if (
                near
                and beside
                and (fp is None or (p.get("area_m2") or 0) > (fp.get("area_m2") or 0))
            ):
                fp = p
    rl, rr = out["left"]["value"], out["right"]["value"]
    y0 = best[1] if best else ext.y0
    if fp is not None:
        front = float(plane_coord(fp, fr, rc, (y0 + top) / 2))
        out["front"] = {"value": front, "plane": fp["id"], "observed": True,
                        "sigma": float(fp.get("rms_m") or 0.005)}  # fmt: skip
    else:
        out["front"] = {"value": ext.d1, "sigma": 0.01, "observed": True, "source": "component"}
    front = out["front"]["value"]
    back_v = back[1] if back else None

    rs, ds = np.array([rl, rr, rl, rr]), np.array([front, front, back_v or front, back_v or front])
    if best:
        p = best[0]
        out["floor"] = _plane_bound(p, best[1], plane_coord(p, fr, rs, d=ds))
        out["floor"]["tilt_deg"] = _tilt_deg(p, fr.up)
    else:
        out["floor"] = {"value": ext.y0, "sigma": 0.01, "observed": False, "source": "component"}
    if back:
        p = back[0]
        ys = np.array([out["floor"]["value"], out["floor"]["value"], top, top])
        out["back"] = _plane_bound(p, back_v, plane_coord(p, fr, np.array([rl, rr, rl, rr]), ys))
        out["back"]["tilt_deg"] = _tilt_deg(p, fr.d)
    else:  # ponytail: no wall found -> a nominal 60 cm deep opening, flagged
        out["back"] = {"value": front - 0.6, "sigma": 0.1, "observed": False, "source": "nominal"}

    # cap: the nearest horizontal plane over the footprint up to 10 cm above the top
    cap = None
    for p, q in _plane_cands(planes, fr, horizontal=True):
        yq = q[:, 1].mean()
        if not (top < yq <= top + 0.1):
            continue
        over = _overlaps(q[:, 0].min(), q[:, 0].max(), min(rl, rr), max(rl, rr)) and _overlaps(
            q[:, 2].min(), q[:, 2].max(), out["back"]["value"], front
        )
        if over and (cap is None or yq < cap[1]):
            cap = (p, yq)
    if cap:
        out["cap"] = {"plane": cap[0]["id"], "value": float(cap[1])}
    return out


def sizes(b: dict) -> tuple[dict, dict]:
    w = abs(b["right"]["value"] - b["left"]["value"])
    h = b["top"]["value"] - b["floor"]["value"]
    d = b["front"]["value"] - b["back"]["value"]
    sg = {k: float(np.hypot(b[x]["sigma"], b[y]["sigma"]))
          for k, x, y in (("w", "left", "right"), ("h", "floor", "top"), ("d", "front", "back"))}  # fmt: skip
    return {"w": float(w), "h": float(h), "d": float(d)}, sg


# ---------------------------------------------------------------- 7. cut


def cut_faces(rud, b: dict, d_front: float, own: np.ndarray, other: np.ndarray, taken: np.ndarray):
    """Faces to move into the part: inside the plane box, and voted to this component or claimed
    by no other, except unvoted faces in the 2 cm under the top (a neighbour's sagging underside).
    Returns (cut mask, stats)."""
    rl, rr = sorted((b["left"]["value"], b["right"]["value"]))
    top = b["top"]["value"]
    box = (
        (rud[:, 0] > rl + 0.003) & (rud[:, 0] < rr - 0.003)
        & (rud[:, 1] > b["floor"]["value"] + 0.01) & (rud[:, 1] < top)
        & (rud[:, 2] > b["back"]["value"] - 0.01) & (rud[:, 2] < max(b["front"]["value"], d_front) + 0.03)
    )  # fmt: skip
    band = rud[:, 1] > top - 0.02
    cut = box & ~taken & (own | (~other & ~band))
    return cut, {
        "faces": int(cut.sum()),
        "voted": int((cut & own).sum()),
        "unclaimed": int((cut & ~own).sum()),
        "protected_claimed": int((box & other & ~own).sum()),
        "protected_band": int((box & band & ~own & ~other).sum()),
    }


def top_holes(rud_before, rud_after, b: dict, y_max: float, cell=CELL) -> np.ndarray:
    """Top-down: the (r, d) grid of 1 cm cells over the footprint, from (left, back), whose highest
    surface below `y_max` dropped by > 3 cm after the cut -- holes opened in the counter on top."""
    rl, rr = sorted((b["left"]["value"], b["right"]["value"]))
    d0, d1 = b["back"]["value"], b["front"]["value"] - 0.01
    nr, nd = max(1, int((rr - rl) / cell)), max(1, int((d1 - d0) / cell))

    def hmap(q):
        q = q[
            (q[:, 1] < y_max) & (q[:, 0] >= rl) & (q[:, 0] < rr) & (q[:, 2] >= d0) & (q[:, 2] < d1)
        ]
        i = np.minimum(((q[:, 0] - rl) / cell).astype(int), nr - 1)
        j = np.minimum(((q[:, 2] - d0) / cell).astype(int), nd - 1)
        h = np.full((nr, nd), -np.inf)
        np.maximum.at(h, (i, j), q[:, 1])
        return h

    hb, ha = hmap(rud_before), hmap(rud_after)
    return np.isfinite(hb) & (ha < hb - 0.03)


# ---------------------------------------------------------------- 8. fill


def cavity_mesh(fr: Frame, b: dict, colors: dict, cap_cells=None) -> trimesh.Trimesh:
    """Floor, back, left, right, top as flat-coloured quads, both windings, plus a cap quad on the
    counter plane over each holed cell (`top_holes`, dilated)."""
    rl, rr = b["left"]["value"], b["right"]["value"]
    y0, y1, d0, d1 = b["floor"]["value"], b["top"]["value"], b["back"]["value"], b["front"]["value"]
    quads = {
        "floor": [(rl, y0, d0), (rr, y0, d0), (rr, y0, d1), (rl, y0, d1)],
        "back": [(rl, y0, d0), (rr, y0, d0), (rr, y1, d0), (rl, y1, d0)],
        "left": [(rl, y0, d0), (rl, y0, d1), (rl, y1, d1), (rl, y1, d0)],
        "right": [(rr, y0, d0), (rr, y0, d1), (rr, y1, d1), (rr, y1, d0)],
        "top": [(rl, y1, d0), (rr, y1, d0), (rr, y1, d1), (rl, y1, d1)],
    }
    if b["top"].get("source"):  # nothing above (a freestanding range): the opening is open-topped
        quads.pop("top")
    quads = {k: [v] for k, v in quads.items()}
    if cap_cells is not None and cap_cells.any():
        i, j = np.nonzero(cap_cells)
        r, d, yc = min(rl, rr) + i * CELL, d0 + j * CELL, np.full(len(i), b["cap"]["value"])
        quads["cap"] = np.stack([np.c_[r, yc, d], np.c_[r + CELL, yc, d],
                                 np.c_[r + CELL, yc, d + CELL], np.c_[r, yc, d + CELL]], 1)  # fmt: skip
    tri = np.array([[0, 1, 2], [0, 2, 3], [0, 2, 1], [0, 3, 2]])
    parts = []
    for name, q in quads.items():
        q = np.asarray(q, float)
        faces = (tri[None] + 4 * np.arange(len(q))[:, None, None]).reshape(-1, 3)
        m = trimesh.Trimesh(fr.world(q.reshape(-1, 3)), faces, process=False)
        m.visual = trimesh.visual.ColorVisuals(m, face_colors=np.r_[linear(colors[name]), 255])
        parts.append(m)
    return trimesh.util.concatenate(parts)


def linear(srgb) -> np.ndarray:
    """sRGB 0-255 -> linear 0-255 uint8: glTF COLOR_0 is linear (the atlas texture is sRGB)."""
    c = np.asarray(srgb, float) / 255
    c = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    return np.round(c * 255).astype(np.uint8)


def fill_colors(rud, fc, nup, b: dict) -> dict:
    """Median colour of the surfaces next to each cavity face (the floor in front, the backsplash
    above the counter, the neighbours' fronts beside it, the counter top)."""
    rl, rr = sorted((b["left"]["value"], b["right"]["value"]))
    y0, y1 = b["floor"]["value"], b["top"]["value"]
    d0, d1 = b["back"]["value"], b["front"]["value"]
    r, y, d = rud[:, 0], rud[:, 1], rud[:, 2]
    inr = (r > rl) & (r < rr)
    side = (np.abs(d - d1) < 0.03) & (y > y0 + 0.1) & (y < y1 - 0.05)
    yc = b.get("cap", {}).get("value", y1)
    sel = {
        "floor": inr & (np.abs(y - y0) < 0.02) & (d > d1) & (d < d1 + 0.3),
        "back": (r > rl - 0.2)
        & (r < rr + 0.2)
        & (np.abs(d - d0) < 0.03)
        & (y > y1)
        & (y < y1 + 0.3),
        "left": side & (r < rl - 0.03) & (r > rl - 0.15),
        "right": side & (r > rr + 0.03) & (r < rr + 0.15),
        "cap": ~inr
        & (r > rl - 0.3)
        & (r < rr + 0.3)
        & (np.abs(y - yc) < 0.01)
        & (d > d0)
        & (d < d1)
        & (nup > 0.8),
    }
    out = {k: (np.median(fc[s], 0) if s.sum() >= 20 else np.full(3, 160.0)) for k, s in sel.items()}
    out["top"] = out["cap"] * 0.75  # the counter's underside, in shadow
    return {k: np.round(v).clip(0, 255) for k, v in out.items()}


# ---------------------------------------------------------------- 9. run


def _obb(fr: Frame, rud: np.ndarray) -> dict:
    lo, hi = rud.min(0), rud.max(0)
    return {"center": fr.world((lo + hi) / 2).tolist(), "axes": [fr.r.tolist(), fr.up.tolist(),
            fr.d.tolist()], "size": (hi - lo).tolist()}  # fmt: skip


def run(
    scene_dir: Path,
    components: list[str],
    work: Path,
    n_frames: int = 16,
    sam_python: str | None = None,
    detections: dict | None = None,
) -> dict:
    t_start, stages = time.time(), {}
    scene_dir = Path(scene_dir)
    scene = json.loads((scene_dir / "scene.json").read_text())
    rev = scene["revision"]
    S = json.loads((scene_dir / scene["structure"]["file"]).read_text())
    cam_list = json.loads((scene_dir / scene["cameras"]).read_text())
    cams = {c["id"]: c for c in cam_list}
    up = np.asarray(scene.get("up", [0, 1, 0]), float)
    mesh = trimesh.load(scene_dir / scene["mesh"]["file"], force="mesh", process=False)
    C, V, F = mesh.triangles_center, mesh.vertices, mesh.faces
    W, H = Image.open(scene_dir / cam_list[0]["thumb"]).size

    # 1. keyframes: evenly spaced. ponytail: greedy face coverage when orbits get uneven
    ids = [c["id"] for c in cam_list]
    keys = [
        ids[i] for i in np.linspace(0, len(ids) - 1, min(n_frames, len(ids))).round().astype(int)
    ]
    thumbs = {f: scene_dir / cams[f]["thumb"] for f in keys}

    # 2-3. name and propose
    t = time.time()
    vlm = {"model": "given"}
    if detections is None:
        detections, vlm = vlm_detect(thumbs, work / "vlm")
    (work / "detections.json").write_text(json.dumps(detections, indent=1))
    comps = proposals(detections, cams, S.get("objects", []), components, W, H)
    comps.sort(key=lambda c: c.rect is None)  # rectangle-bounded first: they set shared sides
    stages["name_s"] = time.time() - t

    # 4. masks
    t = time.time()
    mstats = make_masks(comps, {f: scene_dir / cams[f]["thumb"] for f in detections}, work / "masks",
                        sam_python)  # fmt: skip
    stages["masks_s"] = time.time() - t

    # 5. vote
    t = time.time()
    Fw = welded_faces(V, F)
    labels, vstats = vote(C, V, Fw, cams, comps, W, H)
    np.save(work / "labels.npy", labels.astype(np.int16))
    stages["vote_s"] = time.time() - t

    # 6-8. bound, cut, fill
    t = time.time()
    fc = mesh.visual.to_color().vertex_colors[:, :3].astype(float)[F].mean(1)
    owner = np.full(len(F), -1)
    docs, cavities, placed = [], [], []
    objects = {o["id"]: o for o in S.get("objects", [])}
    for k, c in enumerate(comps):
        voted = labels == k
        if c.rect is None and voted.sum() < 50:
            logger.warning(
                "%s: no S4 rectangle and only %d voted faces, skipped", c.id, voted.sum()
            )
            continue
        centre = np.mean(c.rect["corners3d"], 0) if c.rect is not None else C[voted].mean(0)
        cam_mean = np.mean([cams[f]["position"] for f in c.boxes], 0)
        fr = component_frame(cam_mean - centre, S.get("axes") or [], up)
        rud = fr.to(C)
        if c.rect is not None:
            q = fr.to(c.rect["corners3d"])
            ext = Extent(q[:, 0].min(), q[:, 0].max(), q[:, 1].min(), q[:, 1].max(), q[:, 2].max())
            own = {o["id"] for o in objects.values() if _inside(fr.to(o["corners3d"]), ext)}
        else:
            lo, hi = np.percentile(rud[voted], 2, 0), np.percentile(rud[voted], 98, 0)
            ext, own = Extent(lo[0], hi[0], lo[1], hi[1], hi[2]), set()
        d_seen = float(np.percentile(rud[voted][:, 2], 2)) if voted.any() else None
        b = cavity_bounds(S, fr, ext, own, c.rect["plane"] if c.rect is not None else None, d_seen)
        share_sides(b, fr, placed)
        placed.append((c.id, fr, b))
        size, sigma = sizes(b)
        other = (labels >= 0) & (labels != k)
        cut, cstats = cut_faces(rud, b, ext.d1, voted, other, owner >= 0)
        y_max = b["cap"]["value"] + 0.03 if "cap" in b else b["top"]["value"] + 0.1
        keep = owner < 0
        pts_before = fr.to(V[F[keep]].reshape(-1, 3))
        pts_after = fr.to(V[F[keep & ~cut]].reshape(-1, 3))
        # holes only count in a surface seen from above just over the opening (a countertop: the
        # cap plane); an underside is seen from inside the cavity, which its top quad covers
        under = "cap" in b
        holes = top_holes(pts_before, pts_after, b, y_max) if under else None
        cstats["holes_cm2"] = int(holes.sum()) if under else None
        if not cstats["holes_cm2"]:
            b.pop("cap", None)
        cap_cells = ndimage.binary_dilation(holes) if "cap" in b else None
        cstats["cap_cm2"] = int(cap_cells.sum()) if cap_cells is not None else 0
        rl, rr = sorted((b["left"]["value"], b["right"]["value"]))
        core = ((rud[:, 0] > rl + 0.01) & (rud[:, 0] < rr - 0.01)
                & (rud[:, 1] > b["floor"]["value"] + 0.02) & (rud[:, 1] < b["top"]["value"] - 0.02)
                & (rud[:, 2] > b["back"]["value"] + 0.01) & (rud[:, 2] < b["front"]["value"] - 0.01))  # fmt: skip
        cstats["leftover_faces"] = int((core & ~cut & (owner < 0)).sum())
        owner[cut] = k
        colors = fill_colors(rud, fc, mesh.face_normals @ up, b)
        cav = cavity_mesh(fr, b, colors, cap_cells)
        cav.metadata = {"component": c.id, "estimated": True, "fill": "flat"}
        cavities.append((c.id, cav))
        inb = rud[voted]
        tol = ((inb[:, 0] > rl - 0.01) & (inb[:, 0] < rr + 0.01)
               & (inb[:, 1] > b["floor"]["value"] - 0.01) & (inb[:, 1] < b["top"]["value"] + 0.01)
               & (inb[:, 2] > b["back"]["value"] - 0.01) & (inb[:, 2] < max(b["front"]["value"], ext.d1) + 0.04))  # fmt: skip
        planes_used = sorted({v["plane"] for v in b.values() if "plane" in v})
        docs.append({
            "id": c.id, "label": c.cls.replace("_", " "), "class": KIND.get(c.cls, "appliance"),
            "removable": True, "label_source": "vlm+s4" if c.rect is not None else "vlm",
            "node": f"part_{c.id}", "collision_node": f"part_{c.id}",
            "faces": int(cut.sum()),
            "bbox": {"min": C[cut].min(0).tolist(), "max": C[cut].max(0).tolist()} if cut.any() else None,
            "obb": _obb(fr, rud[cut]) if cut.any() else None,
            "structure_objects": sorted(own, key=lambda s: int(s[1:])),
            "evidence": {"frames": sorted(c.boxes), "vlm": vlm.get("model"), "masks": mstats["source"],
                         "views_voted": len(c.masks), "voted_faces": int(voted.sum()),
                         "rect_iou": round(c.rect_iou, 3),
                         "purity_est": round(float(tol.mean()), 3) if len(tol) else None},
            "cut": cstats,
            "cavity": {
                "node": f"cavity_{c.id}", "estimated": True, "fill": "flat",
                "size_m": size, "sigma_m": sigma,
                "insert": {"p": fr.world([(rl + rr) / 2, b["floor"]["value"], b["front"]["value"]]).tolist(),
                           "axes": [fr.r.tolist(), fr.up.tolist(), fr.d.tolist()]},
                "box": {"min_rud": [rl, b["floor"]["value"], b["back"]["value"]],
                        "max_rud": [rr, b["top"]["value"], b["front"]["value"]]},
                "bounds": b, "planes": planes_used,
                "colors": {k2: v.tolist() for k2, v in colors.items()},
            },
        })  # fmt: skip
    stages["bound_cut_fill_s"] = time.time() - t

    # 9. publish: revision files first, scene.json last
    t = time.time()
    names = {k: f"part_{c.id}" for k, c in enumerate(comps)}
    shared_jpeg_material(mesh)
    out_mesh = trimesh.Scene()
    for n, sel in [("background", owner < 0)] + [(n, owner == k) for k, n in names.items()]:
        if sel.any():
            sub = mesh.submesh([np.nonzero(sel)[0]], append=True)
            sub.visual.material = mesh.visual.material  # one material and atlas for every node
            out_mesh.add_geometry(sub, node_name=n, geom_name=n)
    files = {"mesh": f"mesh.parts.r{rev}.glb", "cavities": f"cavity.r{rev}.glb",
             "collision": f"collision.parts.r{rev}.glb", "file": f"parts.r{rev}.json"}  # fmt: skip
    out_mesh.export(scene_dir / files["mesh"])
    cav_scene = trimesh.Scene()
    for cid, cav in cavities:
        cav_scene.add_geometry(cav, node_name=f"cavity_{cid}", geom_name=f"cavity_{cid}")
    cav_scene.export(scene_dir / files["cavities"])
    col = trimesh.load(scene_dir / scene["collision"]["file"], force="mesh", process=False)
    col_owner = owner[cKDTree(C).query(col.triangles_center)[1]]  # nearest render face's label
    col_scene = trimesh.Scene()
    for n, sel in [("background", col_owner < 0)] + [(n, col_owner == k) for k, n in names.items()]:
        if sel.any():
            col_scene.add_geometry(col.submesh([np.nonzero(sel)[0]], append=True), node_name=n,
                                   geom_name=n)  # fmt: skip
    col_scene.export(scene_dir / files["collision"])
    stages["publish_s"] = time.time() - t
    doc = {
        "schema": SCHEMA, "frame": "scene", "revision": rev, "method": "s4+vlm+sam2+vote",
        "mesh": files["mesh"], "cavities": files["cavities"], "collision": files["collision"],
        "scale": {"method": scene.get("scale_method"), "residual_m": scene.get("scale_residual_m"),
                  "note": "sizes are in scene metres; sigma_m is geometric and excludes the "
                          "scene's scale error"},
        "cost": {"runtime_s": time.time() - t_start, "stages_s": stages,
                 "vlm": {k: v for k, v in vlm.items() if k != "call_s"},
                 "vlm_call_s": vlm.get("call_s"), "masks": mstats, "vote": vstats},
        "components": docs,
    }  # fmt: skip
    write_json_atomic(scene_dir / files["file"], json.dumps(doc, indent=1))
    scene["parts"] = {"file": files["file"], "schema": SCHEMA, "components": len(docs),
                      "mesh": files["mesh"], "cavities": files["cavities"],
                      "collision": files["collision"]}  # fmt: skip
    write_json_atomic(scene_dir / "scene.json", json.dumps(scene, indent=2))
    return doc


def share_sides(b: dict, fr: Frame, placed: list) -> None:
    """A side that falls inside an already placed cavity facing the same way (and overlapping it
    in height) snaps to that cavity's facing side: neighbours share one boundary."""
    for cid, pfr, pb in placed:
        if pfr.d @ fr.d < 0.99:
            continue
        if not _overlaps(pb["floor"]["value"], pb["top"]["value"], b["floor"]["value"],
                         b["top"]["value"]):  # fmt: skip
            continue
        pl, pr = pb["left"]["value"], pb["right"]["value"]
        if pl < b["left"]["value"] < pr:
            b["left"] = {**pb["right"], "shared_with": cid}
        if pl < b["right"]["value"] < pr:
            b["right"] = {**pb["left"], "shared_with": cid}


def shared_jpeg_material(mesh: trimesh.Trimesh) -> None:
    """Make the atlas a JPEG that trimesh re-saves at its own quantisation (trimesh embeds a
    JPEG-format image as JPEG but at PIL's default q75, and anything else as PNG: 4.5 MB)."""
    buf = io.BytesIO()
    mesh.visual.material.baseColorTexture.convert("RGB").save(buf, "JPEG", quality=92)
    jpg = Image.open(buf)
    jpg.load()
    jpg.save = functools.partial(jpg.save, quality="keep")  # ponytail: instance patch, see above
    mesh.visual.material.baseColorTexture = jpg


def _inside(q: np.ndarray, ext: Extent, tol=0.01) -> bool:
    return bool(
        (q[:, 0] >= ext.r0 - tol).all() and (q[:, 0] <= ext.r1 + tol).all()
        and (q[:, 1] >= ext.y0 - tol).all() and (q[:, 1] <= ext.y1 + tol).all()
        and (np.abs(q[:, 2] - ext.d1) < 0.06).all()
    )  # fmt: skip


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(
        prog="python -m pipeline.parts", description=__doc__.split("\n")[0]
    )
    ap.add_argument("scene_dir", type=Path)
    ap.add_argument("--components", default="dishwasher",
                    help="comma list: dishwasher, fridge, range, microwave, sink_cabinet")  # fmt: skip
    ap.add_argument(
        "--work", type=Path, help="cache dir (VLM replies, masks); default work/parts/<scene>"
    )
    ap.add_argument("--frames", type=int, default=16, help="keyframes sent to the VLM")
    ap.add_argument("--sam-python", default=os.environ.get("AIRTOOLS_SAM_PYTHON"),
                    help="python with torch+transformers for SAM 2.1 (default $AIRTOOLS_SAM_PYTHON)")  # fmt: skip
    a = ap.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    wanted = [canonical(c) or c for c in a.components.split(",") if c]
    work = a.work or Path("work/parts") / a.scene_dir.resolve().name
    work.mkdir(parents=True, exist_ok=True)
    doc = run(a.scene_dir, wanted, work, a.frames, a.sam_python)
    for c in doc["components"]:
        s, g = c["cavity"]["size_m"], c["cavity"]["sigma_m"]
        print(f"{c['id']:4} {c['label']:13} faces {c['faces']:6}  cavity W {s['w']:.3f}±{g['w']:.3f} "
              f"H {s['h']:.3f}±{g['h']:.3f} D {s['d']:.3f}±{g['d']:.3f} m  planes {c['cavity']['planes']}")  # fmt: skip
    print(f"runtime {doc['cost']['runtime_s']:.1f} s -> {a.scene_dir / doc['mesh']}")


if __name__ == "__main__":
    sys.exit(main())
