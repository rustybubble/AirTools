"""Plane orthophotos for S4 (docs/research/structure/s4-objects.md).

Two ways to get an image of a plane in its own (u, v) frame at a fixed metric pixel size:

- `render_mesh`: rasterise the textured mesh's triangles near the plane, orthographically along
  the normal (triangle-id raster + barycentric texture lookup, no GL).
- `fuse_frames`: warp the original (distorted) video frames onto the plane through the COLMAP
  poses (plane point -> pinhole -> SIMPLE_RADIAL distortion -> remap), then take the per-pixel
  median over the best-facing frames.

Pixel (col, row) <-> plane (u, v): u = u0 + (col + 0.5) * res, v = v1 - (row + 0.5) * res, so
+v (up) is up in the image.
"""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np


@dataclass
class Grid:
    """An orthophoto raster on a plane: origin/u/v/n in the scene frame, bounds in metres."""

    origin: np.ndarray
    u: np.ndarray
    v: np.ndarray
    n: np.ndarray
    u0: float
    v0: float
    u1: float
    v1: float
    res: float

    @property
    def shape(self) -> tuple[int, int]:
        return int(np.ceil((self.v1 - self.v0) / self.res)), int(
            np.ceil((self.u1 - self.u0) / self.res)
        )

    def px_to_uv(self, px: np.ndarray) -> np.ndarray:
        px = np.asarray(px, float)
        return np.stack(
            [self.u0 + (px[..., 0] + 0.5) * self.res, self.v1 - (px[..., 1] + 0.5) * self.res], -1
        )

    def uv_to_px(self, uv: np.ndarray) -> np.ndarray:
        uv = np.asarray(uv, float)
        return np.stack(
            [(uv[..., 0] - self.u0) / self.res - 0.5, (self.v1 - uv[..., 1]) / self.res - 0.5], -1
        )

    def lift(self, uv: np.ndarray) -> np.ndarray:
        """Plane (u, v) -> scene xyz, exactly on the plane."""
        uv = np.asarray(uv, float)
        return self.origin + uv[..., :1] * self.u + uv[..., 1:2] * self.v

    def to_uv(self, xyz: np.ndarray) -> np.ndarray:
        q = np.asarray(xyz, float) - self.origin
        return np.stack([q @ self.u, q @ self.v], -1)

    def points(self) -> np.ndarray:
        h, w = self.shape
        cc, rr = np.meshgrid(np.arange(w), np.arange(h))
        return self.lift(self.px_to_uv(np.stack([cc, rr], -1)))


def plane_frame(n: np.ndarray, point: np.ndarray, up=(0.0, 1.0, 0.0)):
    """Orthonormal (origin, n, u, v) with v = n x u as close to `up` as possible (u horizontal)."""
    n = np.asarray(n, float) / np.linalg.norm(n)
    u = np.cross(up, n)
    if np.linalg.norm(u) < 1e-6:  # horizontal plane: u along scene x
        u = np.cross(n, np.cross([1.0, 0, 0], n))
    u /= np.linalg.norm(u)
    return np.asarray(point, float), n, u, np.cross(n, u)


def render_mesh(
    grid: Grid,
    verts: np.ndarray,
    faces: np.ndarray,
    uvs: np.ndarray,
    tex: np.ndarray,
    band: float = 0.04,
) -> tuple[np.ndarray, np.ndarray]:
    """Orthographic render of the textured mesh along -n. Only triangles within `band` of the
    plane (all three vertices) and facing +n are drawn, nearest-to-viewer last. Returns
    (BGR image, signed height of the drawn surface above the plane, NaN where empty)."""
    h, w = grid.shape
    d = (verts - grid.origin) @ grid.n
    fd = d[faces]
    tn = np.cross(verts[faces[:, 1]] - verts[faces[:, 0]], verts[faces[:, 2]] - verts[faces[:, 0]])
    keep = (np.abs(fd).max(1) < band) & (tn @ grid.n > 0)
    px = grid.uv_to_px(grid.to_uv(verts))
    f = faces[keep]
    order = np.argsort(fd[keep].mean(1))  # far first, near (larger d) drawn over it
    f = f[order]
    pf = px[f]
    inside = (pf[..., 0].max(1) >= 0) & (pf[..., 0].min(1) < w) & (pf[..., 1].max(1) >= 0)
    inside &= pf[..., 1].min(1) < h
    f, pf = f[inside], pf[inside]
    tid = np.full((h, w), -1, np.int32)
    for i, tri in enumerate(np.round(pf * 16).astype(np.int32)):
        cv2.fillConvexPoly(tid, tri, int(i), lineType=cv2.LINE_8, shift=4)
    rr, cc = np.nonzero(tid >= 0)
    ti = tid[rr, cc]
    a, b, c = pf[ti, 0], pf[ti, 1], pf[ti, 2]
    p = np.stack([cc, rr], -1).astype(float)
    m = np.stack([b - a, c - a], -1)  # (N,2,2) columns
    det = m[:, 0, 0] * m[:, 1, 1] - m[:, 0, 1] * m[:, 1, 0]
    det[np.abs(det) < 1e-12] = 1e-12
    q = p - a
    l1 = (q[:, 0] * m[:, 1, 1] - q[:, 1] * m[:, 0, 1]) / det
    l2 = (m[:, 0, 0] * q[:, 1] - m[:, 1, 0] * q[:, 0]) / det
    bary = np.stack([1 - l1 - l2, l1, l2], -1).clip(0, 1)
    tuv = (bary[..., None] * uvs[f[ti]]).sum(1)
    th, tw = tex.shape[:2]
    mapx = np.full((h, w), -1, np.float32)
    mapy = np.full((h, w), -1, np.float32)
    mapx[rr, cc] = tuv[:, 0] * tw - 0.5
    mapy[rr, cc] = (1 - tuv[:, 1]) * th - 0.5  # trimesh flips glTF v to the GL convention
    img = cv2.remap(tex, mapx, mapy, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
    height = np.full((h, w), np.nan, np.float32)
    height[rr, cc] = (bary * d[f[ti]]).sum(1)
    return img, height


def distort_simple_radial(xn: np.ndarray, f: float, cx: float, cy: float, k: float) -> np.ndarray:
    """Normalised undistorted (x, y) -> COLMAP SIMPLE_RADIAL pixel -> OpenCV pixel (-0.5)."""
    r2 = (xn**2).sum(-1, keepdims=True)
    return xn * (1 + k * r2) * f + [cx - 0.5, cy - 0.5]


def _in_frame(fv: dict, raw: dict, uv: np.ndarray | None = None) -> np.ndarray:
    """Grid points that land inside the raw frame (1 px margin), in front of the camera."""
    if uv is None:
        uv = distort_simple_radial(fv["xn"], raw["f"], raw["cx"], raw["cy"], raw["k"])
    ok = (fv["z"] > 0) & (uv[..., 0] >= 1) & (uv[..., 0] < raw["w"] - 2)
    return ok & (uv[..., 1] >= 1) & (uv[..., 1] < raw["h"] - 2)


def frame_view(grid: Grid, cam: dict, pts: np.ndarray | None = None) -> dict:
    """Per-pixel normalised camera coords of the grid points, plus view quality scalars."""
    pts = grid.points() if pts is None else pts
    R, t = np.asarray(cam["R"]), np.asarray(cam["t"])
    x = pts @ R.T + t
    z = x[..., 2]
    xn = x[..., :2] / np.where(z > 1e-6, z, np.nan)[..., None]
    c = -R.T @ t
    ctr = grid.lift(np.array([(grid.u0 + grid.u1) / 2, (grid.v0 + grid.v1) / 2]))
    ray = c - ctr
    cos = float(ray @ grid.n / np.linalg.norm(ray))
    return {"xn": xn, "z": z, "cos": cos, "dist": float(np.linalg.norm(ray))}


def fuse_frames(
    grid: Grid,
    cams: list[dict],
    load,
    raw: dict,
    k_best: int = 7,
    max_frames: int = 40,
    min_cos: float = 0.35,
    exclude: set[str] = frozenset(),
) -> tuple[np.ndarray, np.ndarray, list[str]]:
    """Per-pixel median of the `k_best` finest-resolution frames that see that pixel. Frames are
    ranked by footprint (distance / cos of the view angle to the plane centre); the best
    `max_frames` are warped. `load(id)` returns the raw BGR frame; `raw` holds SIMPLE_RADIAL
    {f, cx, cy, k, w, h}. Returns (BGR median, seam-free grey, frame ids that contributed)."""
    pts = grid.points()
    h, w = grid.shape
    step = 8  # coverage pre-check on a coarse sub-grid; full-res maps only for the kept frames
    cands = []
    for cam in cams:
        if cam["id"] in exclude:
            continue
        fv = frame_view(grid, cam, pts[::step, ::step])
        if fv["cos"] < min_cos:
            continue
        ok = _in_frame(fv, raw)
        if ok.mean() > 0.02:
            cands.append((fv["dist"] / fv["cos"], cam))
    cands.sort(key=lambda c: c[0])
    cands = cands[:max_frames]
    if not cands:
        return np.zeros((h, w, 3), np.uint8), np.zeros((h, w), np.uint8), []
    # k slots per pixel, filled in rank order: the k finest frames that see each pixel. Only the
    # slots are kept (not every warped frame), so the medians stay small.
    k = k_best
    col = np.full((k, h, w, 3), np.nan, np.float32)
    gxs = np.full((k, h, w - 1), np.nan, np.float32)
    gys = np.full((k, h - 1, w), np.nan, np.float32)
    fid, fx, fy = (np.full(x.shape[:3], -1, np.int32) for x in (col, gxs, gys))
    cnt, cx, cy = (np.zeros(x.shape[1:3], np.int32) for x in (col, gxs, gys))
    used, lo = [], {}
    for i, (_, cam) in enumerate(cands):
        fv = frame_view(grid, cam, pts)
        uv = distort_simple_radial(fv["xn"], raw["f"], raw["cx"], raw["cy"], raw["k"])
        valid = _in_frame(fv, raw, uv)
        take = valid & (cnt < k)
        if not take.any():
            continue
        used.append(cam["id"])
        uv = uv.astype(np.float32)
        img = cv2.remap(load(cam["id"]), uv[..., 0], uv[..., 1], cv2.INTER_LINEAR)
        img = img.astype(np.float32)
        gray = img.mean(-1)
        lo[i] = np.where(valid, gray, np.nan)  # gain is matched on all it sees, not just slots
        for vals, sl, f, c, m in (
            (img, col, fid, cnt, take),
            (np.diff(gray, axis=1), gxs, fx, cx, take[:, 1:] & take[:, :-1]),
            (np.diff(gray, axis=0), gys, fy, cy, take[1:] & take[:-1]),
        ):
            r, q = np.nonzero(m)
            sl[c[r, q], r, q] = vals[r, q]
            f[c[r, q], r, q] = i
            c[r, q] += 1
    gain = np.ones(len(cands) + 1, np.float32)  # [-1] -> empty slot
    lum = col.mean(-1)
    for _ in range(2):  # per-frame gain to the running median, so exposure seams don't make edges
        ref = slot_median(lum * gain[fid])
        for i, li in lo.items():
            m = ~np.isnan(li) & ~np.isnan(ref)
            if m.sum() > 100:
                gain[i] = np.median(ref[m]) / max(np.median(li[m]), 1.0)
    out = slot_median(col * gain[fid][..., None])
    # Seam-free grey: median of per-frame gradients (a frame's own coverage border is not a
    # gradient in it), integrated back with a Neumann Poisson solve.
    gx = slot_median(gxs * gain[fx])
    gy = slot_median(gys * gain[fy])
    seamless = poisson(np.nan_to_num(gx), np.nan_to_num(gy))
    mask = ~np.isnan(out[..., 0])
    if mask.any():  # match the colour median's mean and spread on the covered pixels
        a, b = seamless[mask], out[mask].mean(-1)
        seamless = (seamless - a.mean()) / max(a.std(), 1e-6) * b.std() + b.mean()
    seamless[~mask] = 0
    return (
        np.nan_to_num(out, nan=0).clip(0, 255).astype(np.uint8),
        seamless.clip(0, 255).astype(np.uint8),
        used,
    )


def slot_median(a: np.ndarray) -> np.ndarray:
    """Median over axis 0 ignoring NaN (NaN where all are NaN); fast for a few slots."""
    n = (~np.isnan(a)).sum(0)
    srt = np.sort(a, axis=0)  # NaN sorts last
    lo = np.take_along_axis(srt, np.maximum((n - 1) // 2, 0)[None], 0)[0]
    hi = np.take_along_axis(srt, np.maximum(n // 2, 0)[None], 0)[0]
    return np.where(n > 0, (lo + hi) / 2, np.nan)


def poisson(gx: np.ndarray, gy: np.ndarray) -> np.ndarray:
    """Least-squares image from forward differences gx (H, W-1) and gy (H-1, W), Neumann
    boundary, via the DCT (up to an additive constant)."""
    from scipy.fft import dctn, idctn

    h, w = gy.shape[0] + 1, gx.shape[1] + 1
    div = np.zeros((h, w))
    div[:, :-1] += gx
    div[:, 1:] -= gx
    div[:-1, :] += gy
    div[1:, :] -= gy
    d = (2 * np.cos(np.pi * np.arange(w) / w) - 2)[None, :] + (
        2 * np.cos(np.pi * np.arange(h) / h) - 2
    )[:, None]
    d[0, 0] = 1
    f = dctn(div, norm="ortho") / d
    f[0, 0] = 0
    return idctn(f, norm="ortho")


def sharpness(img: np.ndarray, mask: np.ndarray | None = None) -> float:
    """Variance of the Laplacian over the non-empty pixels (higher = sharper)."""
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if img.ndim == 3 else img
    lap = cv2.Laplacian(g.astype(np.float32), cv2.CV_32F)
    m = (g > 0) if mask is None else mask
    m = cv2.erode(m.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
    return float(lap[m].var()) if m.any() else 0.0
