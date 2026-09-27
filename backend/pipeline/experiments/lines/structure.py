"""Snap-ready structure from a raw 3D line map (S2, docs/research/structure/s2-lines.md).

Input: 3D segments (from LIMAP or any line triangulator) as endpoint pairs plus the set of views
that support each one. Output: Manhattan axes, merged and axis-snapped edges, and corners where
2-3 edges meet. All tolerances are in the map's units; pass metric ones after scaling.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np


@dataclass
class Seg:
    p0: np.ndarray
    p1: np.ndarray
    views: set[int] = field(default_factory=set)
    axis: int = -1  # index into the Manhattan axes, -1 if off-axis

    @property
    def d(self) -> np.ndarray:
        v = self.p1 - self.p0
        return v / np.linalg.norm(v)

    @property
    def length(self) -> float:
        return float(np.linalg.norm(self.p1 - self.p0))


def manhattan_axes(segs: list[Seg], tol_deg: float = 4.0) -> np.ndarray:
    """3x3 rotation whose columns are the dominant orthogonal line directions (length-weighted).

    Greedy: axis 0 = direction with most length within `tol_deg`; axis 1 = the same among
    directions ~orthogonal to it; axis 2 = their cross product. Then one Procrustes refit over the
    inliers of all three. ponytail: O(n^2) candidate scan, fine for a few thousand segments.
    """
    d = np.array([s.d for s in segs])
    w = np.array([s.length for s in segs])
    cos_tol = np.cos(np.radians(tol_deg))

    def best(mask: np.ndarray) -> np.ndarray:
        cand = d[mask]
        score = (np.abs(cand @ d.T) > cos_tol).astype(float) @ w
        return cand[int(np.argmax(score))]

    a0 = best(np.ones(len(d), bool))
    a1 = best(np.abs(d @ a0) < np.sin(np.radians(tol_deg)))
    a1 = a1 - (a1 @ a0) * a0
    a1 /= np.linalg.norm(a1)
    axes = np.stack([a0, a1, np.cross(a0, a1)], axis=1)
    # Procrustes refit: maximise sum w |d . R e_k| over inliers.
    cos = d @ axes
    k = np.argmax(np.abs(cos), axis=1)
    inl = np.abs(cos[np.arange(len(d)), k]) > cos_tol
    m = np.zeros((3, 3))
    for i in np.flatnonzero(inl):
        m += w[i] * np.sign(cos[i, k[i]]) * np.outer(d[i], np.eye(3)[k[i]])
    u, _, vt = np.linalg.svd(m)
    r = u @ vt
    if np.linalg.det(r) < 0:
        r[:, 2] *= -1
    return r


def snap_to_axes(segs: list[Seg], axes: np.ndarray, tol_deg: float = 5.0) -> None:
    """In place: segments within `tol_deg` of an axis get exactly that direction, pivoting about
    their midpoint (endpoints are projected onto the new line)."""
    cos_tol = np.cos(np.radians(tol_deg))
    for s in segs:
        c = s.d @ axes
        k = int(np.argmax(np.abs(c)))
        if abs(c[k]) < cos_tol:
            s.axis = -1
            continue
        a = axes[:, k] * np.sign(c[k])
        mid = (s.p0 + s.p1) / 2
        s.p0, s.p1 = mid + ((s.p0 - mid) @ a) * a, mid + ((s.p1 - mid) @ a) * a
        s.axis = k


def _fit(points: np.ndarray, weights: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    c = (weights[:, None] * points).sum(0) / weights.sum()
    _, _, vt = np.linalg.svd(np.sqrt(weights)[:, None] * (points - c))
    return c, vt[0]


def merge_collinear(
    segs: list[Seg], dist_tol: float, gap_tol: float, ang_tol_deg: float = 3.0
) -> list[Seg]:
    """Union segments that lie on one line (angle < `ang_tol_deg`, both endpoints within
    `dist_tol` of the other's line) and overlap or leave a gap < `gap_tol`. Each group is refit
    (length-weighted PCA over endpoints) and spans the union of its members' extents.
    Axis-snapped groups keep the exact axis direction."""
    n = len(segs)
    parent = list(range(n))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    cos_tol = np.cos(np.radians(ang_tol_deg))
    d = np.array([s.d for s in segs])
    for i in range(n):  # ponytail: O(n^2) pair scan; a KD-tree on midpoints if n >> 10k
        a = segs[i]
        for j in range(i + 1, n):
            b = segs[j]
            if abs(d[i] @ d[j]) < cos_tol:
                continue
            off = np.array([b.p0 - a.p0, b.p1 - a.p0])
            perp = off - np.outer(off @ d[i], d[i])
            if np.linalg.norm(perp, axis=1).max() > dist_tol:
                continue
            t = off @ d[i]
            gap = max(t.min() - a.length, -t.max(), 0.0)
            if gap < gap_tol:
                parent[find(i)] = find(j)

    groups: dict[int, list[int]] = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(i)
    out = []
    for idx in groups.values():
        members = [segs[i] for i in idx]
        pts = np.array([p for s in members for p in (s.p0, s.p1)])
        wts = np.repeat([s.length for s in members], 2)
        c, u = _fit(pts, wts)
        axis = members[0].axis if len({s.axis for s in members}) == 1 else -1
        if axis >= 0:  # keep the exact snapped direction
            u = members[0].d
        t = (pts - c) @ u
        views = set().union(*(s.views for s in members))
        out.append(Seg(c + t.min() * u, c + t.max() * u, views, axis))
    return out


def _closest(a: Seg, b: Seg) -> tuple[np.ndarray, float, float, float]:
    """Closest points between the infinite lines of a and b: (midpoint, distance, ta, tb) where
    ta/tb are the parameters along a/b from p0 in length units."""
    da, db = a.d, b.d
    w0 = a.p0 - b.p0
    bb = da @ db
    den = 1 - bb * bb
    ta = (bb * (db @ w0) - (da @ w0)) / den
    tb = ((db @ w0) - bb * (da @ w0)) / den
    pa, pb = a.p0 + ta * da, b.p0 + tb * db
    return (pa + pb) / 2, float(np.linalg.norm(pa - pb)), float(ta), float(tb)


def _lsq_point(segs: list[Seg], edges) -> tuple[np.ndarray, float]:
    """Least-squares point nearest all the edges' lines, and its max distance to any of them."""
    a_m, b_v = np.zeros((3, 3)), np.zeros(3)
    projs = []
    for k in edges:
        proj = np.eye(3) - np.outer(segs[k].d, segs[k].d)
        projs.append((proj, segs[k].p0))
        a_m += proj
        b_v += proj @ segs[k].p0
    x = np.linalg.lstsq(a_m, b_v, rcond=None)[0]
    return x, max(float(np.linalg.norm(pr @ (x - p0))) for pr, p0 in projs)


def junctions(
    segs: list[Seg],
    dist_tol: float,
    ext_tol: float,
    min_angle_deg: float = 30.0,
    res_tol: float | None = None,
) -> list[dict]:
    """Corners where 2+ non-parallel segments (nearly) meet, at the line-line closest point (not at
    endpoints, which occlusion cuts short). A pair qualifies when their lines pass within
    `dist_tol` and the meeting point lies on both segments or within `ext_tol` past an end.
    Pairs are merged greedily (closest pairs first) into a corner only if the refit
    least-squares point stays within `res_tol` (default dist_tol/2) of every line and the new
    edge is not parallel to one already there (door-gap twins); otherwise they stay separate
    corners. Returns [{'p', 'edges', 'residual'}]."""
    res_tol = dist_tol / 2 if res_tol is None else res_tol
    sin_min = np.sin(np.radians(min_angle_deg))
    cands = []
    for i in range(len(segs)):
        for j in range(i + 1, len(segs)):
            a, b = segs[i], segs[j]
            if np.linalg.norm(np.cross(a.d, b.d)) < sin_min:
                continue
            p, dist, ta, tb = _closest(a, b)
            if dist > dist_tol:
                continue
            if -ext_tol <= ta <= a.length + ext_tol and -ext_tol <= tb <= b.length + ext_tol:
                cands.append((dist, p, (i, j)))
    cands.sort(key=lambda c: c[0])

    corners: list[dict] = []
    for dist, p, pair in cands:
        for c in corners:
            if np.linalg.norm(c["p"] - p) > dist_tol:
                continue
            new = [k for k in pair if k not in c["edges"]]
            if any(
                np.linalg.norm(np.cross(segs[k].d, segs[m].d)) < sin_min
                for k in new
                for m in c["edges"]
            ):
                continue
            x, res = _lsq_point(segs, c["edges"] + new)
            if res <= res_tol:
                c.update(p=x, edges=c["edges"] + new, residual=res)
                break
        else:
            corners.append({"p": p, "edges": list(pair), "residual": dist / 2})
    for c in corners:
        c["edges"] = sorted(c["edges"])
    return corners


def extend_to_corners(segs: list[Seg], corners: list[dict], ext_tol: float) -> None:
    """In place: move a segment endpoint onto its corner (projected onto the segment's line) when
    the corner is within `ext_tol` of that end, so edges end exactly where they meet."""
    for c in corners:
        for k in c["edges"]:
            s = segs[k]
            t = (c["p"] - s.p0) @ s.d
            q = s.p0 + t * s.d
            near0 = abs(t) <= abs(t - s.length)  # move only the nearer end, never collapse
            if near0 and abs(t) <= ext_tol and np.linalg.norm(s.p1 - q) > 1e-9:
                s.p0 = q
            elif not near0 and abs(t - s.length) <= ext_tol and np.linalg.norm(s.p0 - q) > 1e-9:
                s.p1 = q
