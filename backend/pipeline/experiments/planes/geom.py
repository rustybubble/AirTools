"""Plane-layer geometry for S3 (docs/research/structure/s3-learned-planes.md): robust plane fits,
cross-frame plane fusion, support polygons, plane-pair edges and three-plane corners.

Pure numpy (+ cv2 for the polygon raster). Planes are `(n, d)` with `n.x + d = 0`, |n| = 1. All
tolerances are in the caller's units: pass metric ones times units-per-metre.
"""

from __future__ import annotations

import numpy as np


def fit_plane(pts: np.ndarray, w: np.ndarray | None = None) -> tuple[np.ndarray, float, float]:
    """Weighted total-least-squares plane. Returns (n, d, rms)."""
    w = np.ones(len(pts)) if w is None else w
    mu = (pts * w[:, None]).sum(0) / w.sum()
    q = pts - mu
    _, vec = np.linalg.eigh((q * w[:, None]).T @ q)
    n = vec[:, 0]
    d = -float(n @ mu)
    return n, d, float(np.sqrt(np.average((q @ n) ** 2, weights=w)))


def ransac_plane(
    pts: np.ndarray, tol: float, iters: int = 200, seed: int = 0
) -> tuple[np.ndarray, float, float, np.ndarray]:
    """RANSAC (3-point) then a TLS refit on the inliers, twice. Returns (n, d, rms, inlier mask)."""
    rng = np.random.default_rng(seed)
    best = np.zeros(len(pts), bool)
    for _ in range(iters):
        a, b, c = pts[rng.choice(len(pts), 3, replace=False)]
        n = np.cross(b - a, c - a)
        if np.linalg.norm(n) < 1e-12:
            continue
        n /= np.linalg.norm(n)
        inl = np.abs((pts - a) @ n) < tol
        if inl.sum() > best.sum():
            best = inl
    if best.sum() < 3:
        best[:] = True
    for _ in range(2):
        n, d, _ = fit_plane(pts[best])
        best = np.abs(pts @ n + d) < tol
        if best.sum() < 3:
            best[:] = True
    n, d, rms = fit_plane(pts[best])
    return n, d, rms, best


def plane_basis(n: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Two unit vectors spanning the plane with normal n."""
    a = np.eye(3)[int(np.argmin(np.abs(n)))]
    u = np.cross(n, a)
    u /= np.linalg.norm(u)
    return u, np.cross(n, u)


class _DSU:
    def __init__(self, n: int):
        self.p = list(range(n))

    def find(self, i: int) -> int:
        while self.p[i] != i:
            self.p[i] = self.p[self.p[i]]
            i = self.p[i]
        return i

    def union(self, i: int, j: int) -> None:
        self.p[self.find(i)] = self.find(j)


def fuse_planes(
    normals: np.ndarray,
    offsets: np.ndarray,
    supports: list[np.ndarray],
    ang_deg: float,
    off_tol: float,
    voxel: float,
) -> np.ndarray:
    """Cluster per-frame planes into scene planes. Two planes join when their normals are within
    `ang_deg` (orientation matters: normals face the camera that saw them), each one's support lies
    within `off_tol` of the other (median point-to-plane distance) and their supports share a
    `voxel`-sized cell. Union-find, so clusters are transitive. Returns a cluster id per plane.
    ponytail: candidate pairs come from shared voxels, O(pairs); fine for ~10^4 per-frame planes."""
    cells: dict[tuple, set[int]] = {}
    for i, s in enumerate(supports):
        for k in set(map(tuple, np.floor(s / voxel).astype(np.int64))):
            cells.setdefault(k, set()).add(i)
    pairs = set()
    for ids in cells.values():
        ids = sorted(ids)
        pairs.update((a, b) for x, a in enumerate(ids) for b in ids[x + 1 :])
    cos_t = np.cos(np.radians(ang_deg))
    dsu = _DSU(len(normals))
    for a, b in pairs:
        if normals[a] @ normals[b] < cos_t:
            continue
        da = np.median(np.abs(supports[b] @ normals[a] + offsets[a]))
        db = np.median(np.abs(supports[a] @ normals[b] + offsets[b]))
        if max(da, db) < off_tol:
            dsu.union(a, b)
    roots = [dsu.find(i) for i in range(len(normals))]
    _, lab = np.unique(roots, return_inverse=True)
    return lab


def support_components(
    n: np.ndarray,
    d: float,
    pts: np.ndarray,
    votes: np.ndarray,
    cell: float,
    min_votes: int,
    min_area: float,
    close_cells: int = 2,
    simplify: float | None = None,
) -> list[dict]:
    """Rasterise support points (projected onto the plane) into `cell`-sized cells, keep cells with
    at least `min_votes` distinct votes (e.g. frames), close small holes and split into connected
    components. Per component with area >= `min_area`: {"polygon" (3D, outer contour simplified to
    `simplify`), "area", "cells" (3D centres of its occupied cells)}."""
    import cv2

    u, v = plane_basis(n)
    o = -d * n  # plane point closest to the origin
    uv = np.stack([(pts - o) @ u, (pts - o) @ v], 1)
    lo = uv.min(0) - (close_cells + 1) * cell
    ij = np.floor((uv - lo) / cell).astype(np.int64)
    H, W = ij[:, 1].max() + close_cells + 2, ij[:, 0].max() + close_cells + 2
    key = np.unique(np.stack([ij[:, 1], ij[:, 0], votes], 1), axis=0)  # one vote per voter per cell
    grid = np.zeros((H, W), np.int32)
    np.add.at(grid, (key[:, 0], key[:, 1]), 1)
    occ = (grid >= min_votes).astype(np.uint8)
    if close_cells:
        k = np.ones((2 * close_cells + 1,) * 2, np.uint8)
        occ = cv2.morphologyEx(occ, cv2.MORPH_CLOSE, k)
    ncomp, lab = cv2.connectedComponents(occ, connectivity=4)
    out = []
    for c in range(1, ncomp):
        m = (lab == c).astype(np.uint8)
        area = float(m.sum()) * cell * cell
        if area < min_area:
            continue
        cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        cnt = max(cs, key=cv2.contourArea)
        cnt = cv2.approxPolyDP(cnt, (simplify or cell) / cell, True)[:, 0].astype(float)
        # contour runs through pixel centres; +0.5 maps them to the cell centre in plane coords
        to3 = lambda a: (
            o
            + np.outer(lo[0] + (a[:, 0] + 0.5) * cell, u)
            + np.outer(lo[1] + (a[:, 1] + 0.5) * cell, v)
        )
        yy, xx = np.nonzero(m)
        out.append(
            {
                "polygon": to3(cnt),
                "area": area,
                "cells": to3(np.stack([xx, yy], 1).astype(float)),
            }
        )
    return out


def plane_edge(
    na: np.ndarray,
    da: float,
    sa: np.ndarray,
    nb: np.ndarray,
    db: float,
    sb: np.ndarray,
    near: float,
    min_len: float,
    min_pts: int = 3,
    pct: float = 2.0,
    extend: float = 0.0,
) -> tuple[np.ndarray, np.ndarray] | None:
    """Segment of the line where planes a and b meet, limited to where both supports reach it:
    support points of each plane within `near` of the other plane are projected onto the line and
    the segment is the overlap of the two [pct, 100 - pct] percentile ranges, grown by `extend` at
    both ends (supports stop short of the true end: cell centres, mask erosion). None if they don't
    meet or the overlap is shorter than `min_len`."""
    t = np.cross(na, nb)
    if np.linalg.norm(t) < np.sin(np.radians(15)):
        return None
    t /= np.linalg.norm(t)
    A = np.stack([na, nb, t])
    p0 = np.linalg.solve(A, np.array([-da, -db, 0.0]))
    ra = sa[np.abs(sa @ nb + db) < near]
    rb = sb[np.abs(sb @ na + da) < near]
    if len(ra) < min_pts or len(rb) < min_pts:
        return None
    ta, tb = (ra - p0) @ t, (rb - p0) @ t
    lo = max(np.percentile(ta, pct), np.percentile(tb, pct))
    hi = min(np.percentile(ta, 100 - pct), np.percentile(tb, 100 - pct))
    if hi - lo < min_len:
        return None
    return p0 + (lo - extend) * t, p0 + (hi + extend) * t


def seg_dist(p: np.ndarray, a: np.ndarray, b: np.ndarray) -> float:
    ab = b - a
    s = np.clip((p - a) @ ab / (ab @ ab), 0, 1)
    return float(np.linalg.norm(a + s * ab - p))


def three_plane_point(ns: np.ndarray, ds: np.ndarray, min_det: float = 0.3) -> np.ndarray | None:
    """Intersection of three planes (rows of ns), or None when they are near-degenerate."""
    if abs(np.linalg.det(ns)) < min_det:
        return None
    return np.linalg.solve(ns, -ds)


def manhattan_snap(
    normals: np.ndarray, weights: np.ndarray, tol_deg: float, up: np.ndarray | None = None
) -> tuple[np.ndarray, np.ndarray]:
    """Dominant orthogonal frame from area-weighted normals (greedy, then a weighted Procrustes
    refit over the snapped normals) and the normals snapped to +-axis when within `tol_deg`.
    `up` (e.g. the scene's gravity) fixes the first axis. Returns (snapped normals, mask)."""
    cos_t = np.cos(np.radians(tol_deg))
    sim = np.abs(normals @ normals.T) > cos_t
    a0 = up / np.linalg.norm(up) if up is not None else normals[int(np.argmax(sim @ weights))]
    orth = np.abs(normals @ a0) < np.sin(np.radians(tol_deg))
    if not orth.any():
        return normals.copy(), np.zeros(len(normals), bool)
    s1 = (np.abs(normals @ normals[orth].T) > cos_t).astype(float).T @ weights
    a1 = normals[orth][int(np.argmax(s1))]
    a1 = a1 - (a1 @ a0) * a0
    a1 /= np.linalg.norm(a1)
    axes = np.stack([a0, a1, np.cross(a0, a1)])
    for _ in range(2):
        c = normals @ axes.T
        k = np.argmax(np.abs(c), 1)
        snap = np.abs(c[np.arange(len(c)), k]) > cos_t
        M = sum(w * np.sign(ci[ki]) * np.outer(np.eye(3)[ki], n)
                for n, w, ci, ki, sn in zip(normals, weights, c, k, snap) if sn)  # fmt: skip
        u, _, vt = np.linalg.svd(M)
        axes = u @ np.diag([1, 1, np.linalg.det(u @ vt)]) @ vt  # rows = refitted axes
        if up is not None:  # keep gravity exact, re-orthogonalise the horizontal axis
            h = axes[1] - (axes[1] @ a0) * a0
            h /= np.linalg.norm(h)
            axes = np.stack([a0, h, np.cross(a0, h)])
    c = normals @ axes.T
    k = np.argmax(np.abs(c), 1)
    snap = np.abs(c[np.arange(len(c)), k]) > cos_t
    out = normals.copy()
    out[snap] = axes[k[snap]] * np.sign(c[np.arange(len(c)), k][snap])[:, None]
    return out, snap
