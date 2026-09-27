"""Fuse S2's 3D line map with a plane layer (S3 PxwPlanar) into one snap layer (S2 fusion).

1. Tag each line plane-attached (both ends within `att_m` of a plane, parallel to it, and inside
   its polygon grown by `margin_m`) or free. Two planes = crease.
2. Snap attached lines exactly into their plane (a crease onto the plane-plane line).
3. Corners: line x line where both lie on a shared plane, and line x plane where an attached
   line ends near another plane. Free line x line corners are dropped when any edge is
   off-axis ("offaxis", the default) or always (True).
4. Add plane x plane creases where no line runs along them; end each crease at the nearest
   line / third-plane intersection instead of at the planes' support extent.

    python -m pipeline.experiments.lines.fuse --lines structure.json --planes s3.json --out f.json
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from pipeline.experiments.lines import structure as st
from pipeline.experiments.structure.score import (
    Structure,
    dist_to_polygon_boundary,
    point_in_polygon,
)


class Planes:
    def __init__(self, doc: dict):
        s = Structure({"planes": doc["planes"]})
        self.ids = [p["id"] for p in doc["planes"]]
        self.n, self.o, self.u, self.v, self.poly = s.n, s.origin, s.u, s.v, s.poly

    def dist(self, x: np.ndarray) -> np.ndarray:
        """Signed distances (N, P) of points (N, 3)."""
        return np.einsum("npd,pd->np", x[:, None] - self.o[None], self.n)

    def inside(self, j: int, x: np.ndarray, margin: float) -> np.ndarray:
        """Points (N,3), projected onto plane j, inside its polygon or within `margin` of it."""
        rel = x - self.o[j]
        uv = np.stack([rel @ self.u[j], rel @ self.v[j]], 1)
        poly = self.poly[j]
        if poly is None:
            return np.ones(len(x), bool)
        return point_in_polygon(uv, poly) | (dist_to_polygon_boundary(uv, poly) <= margin)

    def project(self, j: int, x: np.ndarray) -> np.ndarray:
        return x - ((x - self.o[j]) @ self.n[j])[..., None] * self.n[j]


def plane_line(pl: Planes, i: int, j: int) -> tuple[np.ndarray, np.ndarray]:
    """Point and unit direction of plane i x plane j."""
    d = np.cross(pl.n[i], pl.n[j])
    d /= np.linalg.norm(d)
    a = np.stack([pl.n[i], pl.n[j], d])
    b = np.array([pl.n[i] @ pl.o[i], pl.n[j] @ pl.o[j], d @ pl.o[i]])
    return np.linalg.solve(a, b), d


def line_plane(p: np.ndarray, d: np.ndarray, pl: Planes, k: int) -> float | None:
    """Parameter t where p + t d meets plane k, or None when (nearly) parallel."""
    den = d @ pl.n[k]
    if abs(den) < 0.2:  # < ~12 deg incidence: intersection is ill-conditioned
        return None
    return float((pl.o[k] - p) @ pl.n[k] / den)


def _solve(segs, pl: Planes, edges, planes, prior: np.ndarray):
    """Least-squares point over line constraints (perpendicular distance) and plane constraints
    (normal distance), weakly pulled to `prior` so under-constrained sets stay put. Returns
    (point, max constraint distance)."""
    rows = [(np.eye(3) - np.outer(segs[k].d, segs[k].d), segs[k].p0) for k in edges]
    rows += [(np.outer(pl.n[j], pl.n[j]), pl.o[j]) for j in planes]
    a_m = sum(m for m, _ in rows) + 1e-3 * np.eye(3)
    b_v = sum(m @ q for m, q in rows) + 1e-3 * prior
    x = np.linalg.solve(a_m, b_v)
    return x, max(float(np.linalg.norm(m @ (x - q))) for m, q in rows)


def merge_corners(segs, pl: Planes, corners: list[dict], dist: float, res_tol: float) -> list:
    """Greedily merge corners closer than `dist` that share a supporting line or plane, placing
    the result at the least-squares point of the union of their constraints; skip a merge whose
    residual exceeds `res_tol` (inconsistent support). Disjoint-support neighbours (counter lip vs
    door face) stay separate."""
    from scipy.spatial import cKDTree

    corners = [dict(c, edges=list(c["edges"]), planes=list(c["planes"])) for c in corners]
    changed = True
    while changed:  # closest pairs first; repeat so merged corners can merge again
        changed = False
        pts = np.array([c["p"] for c in corners]).reshape(-1, 3)
        pairs = sorted(
            cKDTree(pts).query_pairs(dist, output_type="ndarray").tolist(),
            key=lambda ij: np.linalg.norm(pts[ij[0]] - pts[ij[1]]),
        )
        dead = set()
        for i, j in pairs:
            if i in dead or j in dead:
                continue
            a, b = corners[i], corners[j]
            if np.linalg.norm(np.asarray(a["p"]) - b["p"]) > dist:
                continue
            if not (set(a["edges"]) & set(b["edges"]) or set(a["planes"]) & set(b["planes"])):
                continue
            e = sorted(set(a["edges"]) | set(b["edges"]))
            q = sorted(set(a["planes"]) | set(b["planes"]))
            x, res = _solve(segs, pl, e, q, (np.asarray(a["p"]) + b["p"]) / 2)
            if res > res_tol:
                continue
            corners[i] = {"p": x, "edges": e, "planes": q, "residual": res}
            dead.add(j)
            changed = True
        corners = [c for k, c in enumerate(corners) if k not in dead]
    return corners


def fuse(
    lines_doc: dict,
    planes_doc: dict,
    m_per_unit: float,
    att_m: float = 0.01,
    margin_m: float = 0.05,
    ext_m: float = 0.06,
    corner_m: float = 0.01,
    snap_lines: bool = False,
    add_creases: bool = True,
    drop_free_corners: bool | str = False,
    keep_free_edges: bool = True,
    merge_m: float = 0.01,
    tol_scale: float = 1.0,
) -> dict:
    """`tol_scale` multiplies every metric tolerance (pipeline.structure.tol_scale)."""
    u = tol_scale / m_per_unit
    att, margin, ext = att_m * u, margin_m * u, ext_m * u
    pl = Planes(planes_doc)
    sin_par = np.sin(np.radians(8.0))

    # 1-2. tag and snap lines
    segs, tags = [], []
    for e in lines_doc["edges"]:
        s = st.Seg(np.array(e["a"], float), np.array(e["b"], float), set(e.get("view_ids", [])))
        s.axis = e.get("axis", -1)
        dd = np.abs(pl.dist(np.stack([s.p0, s.p1])))  # (2, P)
        par = np.abs(pl.n @ s.d) < sin_par
        cand = [
            j
            for j in np.flatnonzero((dd.max(0) <= att) & par)
            if pl.inside(j, np.stack([s.p0, s.p1, (s.p0 + s.p1) / 2]), margin).all()
        ]
        cand = sorted(cand, key=lambda j: dd[:, j].max())[:2]
        if len(cand) == 2 and abs(pl.n[cand[0]] @ pl.n[cand[1]]) > np.cos(np.radians(30)):
            cand = cand[:1]  # two near-parallel planes: keep the closer one
        if snap_lines and len(cand) == 1:
            s.p0, s.p1 = pl.project(cand[0], s.p0), pl.project(cand[0], s.p1)
        elif snap_lines and len(cand) == 2:
            p, d = plane_line(pl, *cand)
            t0, t1 = sorted([(s.p0 - p) @ d, (s.p1 - p) @ d])
            s.p0, s.p1 = p + t0 * d, p + t1 * d
        if not cand and not keep_free_edges:
            continue
        segs.append(s)
        tags.append(cand)

    # 4. creases where no line runs along them
    n_line = len(segs)
    if add_creases:
        for ce in planes_doc.get("edges", []):
            if len(ce.get("planes", [])) != 2:
                continue
            i, j = (pl.ids.index(x) for x in ce["planes"])
            p, d = plane_line(pl, i, j)
            ta, tb = sorted([(np.array(ce["a"]) - p) @ d, (np.array(ce["b"]) - p) @ d])
            covered = any(
                abs(s.d @ d) > np.cos(np.radians(5))
                and np.linalg.norm(np.cross(s.p0 - p, d)) < att
                and np.linalg.norm(np.cross(s.p1 - p, d)) < att
                for s in segs[:n_line]
            )
            if covered:
                continue
            # candidate stops: third planes and attached lines crossing the crease
            stops = []
            for k in range(len(pl.ids)):
                if k in (i, j):
                    continue
                t = line_plane(p, d, pl, k)
                if t is not None and pl.inside(k, (p + t * d)[None], margin)[0]:
                    stops.append(t)
            for s, tg in zip(segs[:n_line], tags):
                if (i in tg or j in tg) and abs(s.d @ d) < 0.99:
                    c, dist, t_s, t_c = st._closest(s, st.Seg(p, p + d))
                    if dist < att and -ext <= t_s <= s.length + ext:
                        stops.append(t_c)
            stops = np.array(stops)
            for end in (0, 1):
                t = (ta, tb)[end]
                if len(stops):
                    near = stops[np.abs(stops - t) <= ext]
                    if len(near):
                        t = float(near[np.argmin(np.abs(near - t))])
                ta, tb = (t, tb) if end == 0 else (ta, t)
            if tb - ta > 0.05 * u:
                segs.append(st.Seg(p + ta * d, p + tb * d))
                tags.append([i, j])

    # 3. corners: line x line on a shared plane, and line x plane
    all_c = st.junctions(segs, dist_tol=corner_m * u, ext_tol=ext)
    corners = []
    for c in all_c:
        shared = set.intersection(*(set(tags[k]) for k in c["edges"]))
        on_axis = all(segs[k].axis >= 0 for k in c["edges"])
        if not shared and (drop_free_corners is True or (drop_free_corners and not on_axis)):
            continue
        c["planes"] = sorted(shared)
        corners.append(c)
    for k, (s, tg) in enumerate(zip(segs, tags)):
        if not tg:
            continue
        for q in range(len(pl.ids)):
            if q in tg:
                continue
            t = line_plane(s.p0, s.d, pl, q)
            if t is None or not (-ext <= t <= ext or -ext <= t - s.length <= ext):
                continue
            x = s.p0 + t * s.d
            if not pl.inside(q, x[None], margin)[0]:
                continue
            if not merge_m and any(np.linalg.norm(c["p"] - x) < corner_m * u for c in corners):
                continue
            corners.append({"p": x, "edges": [k], "planes": sorted(set(tg) | {q}), "residual": 0})
    if merge_m:
        corners = merge_corners(segs, pl, corners, merge_m * u, res_tol=0.005 * u)
    st.extend_to_corners(segs, corners, ext_tol=ext)

    out = dict(lines_doc)
    out.update(
        method="limap+" + planes_doc.get("method", "planes"),
        params={
            "lines": lines_doc.get("params"),
            "planes": planes_doc.get("method"),
            "att_m": att_m,
            "margin_m": margin_m,
            "ext_m": ext_m,
            "corner_m": corner_m,
            "snap_lines": snap_lines,
            "add_creases": add_creases,
            "drop_free_corners": drop_free_corners,
            "keep_free_edges": keep_free_edges,
            "merge_m": merge_m,
            "tol_scale": tol_scale,
        },
        planes=planes_doc["planes"],
        edges=[
            {
                "id": f"e{k}",
                "a": s.p0.tolist(),
                "b": s.p1.tolist(),
                "kind": "line" if k < n_line else "crease",
                "planes": [pl.ids[j] for j in tg],
                "src": ["limap"] if k < n_line else ["plane_intersection"],
                "axis": s.axis,
                "confidence": "high" if tg else "low",
            }
            for k, (s, tg) in enumerate(zip(segs, tags))
        ],
        corners=[
            {
                "id": f"c{i}",
                "p": np.asarray(c["p"]).tolist(),
                "edges": [f"e{k}" for k in c["edges"]],
                "planes": [pl.ids[j] for j in c["planes"]],
                "kind": "2edge" if len(c["edges"]) > 1 else "line_plane",
                "residual_m": float(c["residual"]) * m_per_unit,
            }
            for i, c in enumerate(corners)
        ],
    )
    out["counts"] = {
        "edges": len(segs),
        "lines_attached": sum(bool(t) for t in tags[:n_line]),
        "lines_free": sum(not t for t in tags[:n_line]),
        "creases_added": len(segs) - n_line,
        "corners": len(corners),
    }
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lines", type=Path, required=True)
    ap.add_argument("--planes", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--m-per-unit", type=float, default=None, help="default: lines' params")
    a = ap.parse_args()
    lines = json.loads(a.lines.read_text())
    mpu = a.m_per_unit or lines["params"]["m_per_unit"]
    out = fuse(lines, json.loads(a.planes.read_text()), mpu)
    a.out.write_text(json.dumps(out, indent=1))
    print(json.dumps(out["counts"]))


if __name__ == "__main__":
    main()
