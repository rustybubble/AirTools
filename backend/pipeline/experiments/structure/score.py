"""Structure bench scorer (docs/research/p2-structure-bench.md §2).

    python -m pipeline.experiments.structure score <scene_dir> --gt gt.json \
        [--structure structure.json] [--frames <run>/frames] --out results.jsonl --name <row>

Everything is scored in the GT frame (`gt.json`, triangulated from images, see `gt.py`). The scene
(mesh, collision mesh, structure layer) is Sim(3)-aligned onto it by timestamp-matched camera
centres, so mm are always at the GT's one fixed scale. Absolute scale is uncertain (the bench's
open scale issue): ratios, angles and spreads are reported next to the mm.

Snapping model: the pinch tip is exactly at the GT point (perfect aim). Row 0 snaps to the closest
point on the collision mesh (`Collider.ClosestPoint`, today). With a structure layer the priority is
corner > edge > plane > mesh, each within `--radius`.
"""

import json
import time
from pathlib import Path

import numpy as np

from pipeline.calibrate import interpolate_positions_at_times
from pipeline.experiments.bench.harness import id_times, robust_sim3

# --- geometry helpers (unit-tested) ------------------------------------------------------------


def unit(v: np.ndarray) -> np.ndarray:
    v = np.asarray(v, np.float64)
    return v / np.linalg.norm(v, axis=-1, keepdims=True)


def closest_on_segments(q: np.ndarray, a: np.ndarray, b: np.ndarray) -> tuple:
    """For queries q (Q,3) and segments a,b (E,3): (dist (Q,E), points (Q,E,3))."""
    ab = b - a
    L2 = np.maximum((ab**2).sum(-1), 1e-18)
    t = np.clip(np.einsum("qed,ed->qe", q[:, None] - a[None], ab) / L2, 0, 1)
    p = a[None] + t[..., None] * ab[None]
    return np.linalg.norm(q[:, None] - p, axis=-1), p


def point_in_polygon(pts: np.ndarray, poly: np.ndarray) -> np.ndarray:
    """Even-odd rule, pts (N,2), poly (M,2)."""
    x, y = pts[:, 0:1], pts[:, 1:2]
    x0, y0 = poly[:, 0], poly[:, 1]
    x1, y1 = np.roll(x0, -1), np.roll(y0, -1)
    cross = (y0 > y) != (y1 > y)
    xi = x0 + (y - y0) * (x1 - x0) / np.where(y1 == y0, 1e-18, y1 - y0)
    return ((cross & (x < xi)).sum(1) % 2) == 1


def dist_to_polygon_boundary(pts: np.ndarray, poly: np.ndarray) -> np.ndarray:
    a = np.pad(poly, ((0, 0), (0, 1)))
    b = np.roll(a, -1, 0)
    return closest_on_segments(np.pad(pts, ((0, 0), (0, 1))), a, b)[0].min(1)


def fit_plane(pts: np.ndarray) -> tuple[np.ndarray, np.ndarray, float]:
    """(unit normal, centroid, rms) by SVD."""
    c = pts.mean(0)
    n = np.linalg.svd(pts - c, full_matrices=False)[2][-1]
    return n, c, float(np.sqrt(np.mean(((pts - c) @ n) ** 2)))


def angle_deg(n1: np.ndarray, n2: np.ndarray, unsigned: bool = True) -> float:
    c = float(np.dot(unit(n1), unit(n2)))
    return float(np.degrees(np.arccos(np.clip(abs(c) if unsigned else c, -1, 1))))


def stats_mm(x) -> dict:
    x = np.asarray([v for v in x if v is not None and np.isfinite(v)], np.float64) * 1000
    if not len(x):
        return {"n": 0}
    return {
        "n": len(x),
        "median_mm": float(np.median(x)),
        "p90_mm": float(np.percentile(x, 90)),
        "mean_mm": float(x.mean()),
        "max_mm": float(x.max()),
    }


class MeshQuery:
    """Closest point on a triangle mesh without rtree: kd-tree over face centroids gives candidate
    faces, `trimesh.triangles.closest_point` is exact on those."""

    def __init__(self, mesh, k: int = 48):
        from scipy.spatial import cKDTree

        self.mesh, self.k = mesh, min(k, len(mesh.faces))
        self.tri = mesh.triangles
        self.tree = cKDTree(self.tri.mean(1))

    def closest(self, q: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        from trimesh.triangles import closest_point

        _, idx = self.tree.query(q, k=self.k, workers=-1)
        idx = idx.reshape(len(q), -1)
        tri = self.tri[idx.ravel()]
        cp = closest_point(tri, np.repeat(q, idx.shape[1], 0)).reshape(len(q), -1, 3)
        d = np.linalg.norm(cp - q[:, None], axis=-1)
        d[~np.isfinite(d)] = np.inf  # degenerate (collapsed) triangles give NaN
        j = d.argmin(1)
        return cp[np.arange(len(q)), j], d[np.arange(len(q)), j]


# --- structure layer ---------------------------------------------------------------------------


class Structure:
    """Loaded structure.json (schema airtools.structure/1) as flat arrays, in any frame."""

    def __init__(self, doc: dict):
        self.doc = doc
        planes = doc.get("planes", [])
        self.n = np.array([unit(p["normal"]) for p in planes]).reshape(-1, 3)
        self.origin, self.u, self.poly = [], [], []
        for p, n in zip(planes, self.n):
            poly = p.get("polygon") or p.get("polygon3d")
            if poly is not None and len(poly) and len(poly[0]) == 3:  # 3D outline
                P3 = np.asarray(poly, np.float64)
                o = P3.mean(0)
                u = unit(np.cross(n, np.cross(P3[1] - P3[0], n)))  # in-plane
                v = np.cross(n, u)
                self.origin.append(o)
                self.u.append(u)
                self.poly.append(np.stack([(P3 - o) @ u, (P3 - o) @ v], 1))
                continue
            o = np.asarray(p["origin"], np.float64) if "origin" in p else -p["offset"] * n
            u = (
                unit(p["u"])
                if "u" in p
                else unit(np.cross(n, [0.0, 1, 0] if abs(n[1]) < 0.9 else [1.0, 0, 0]))
            )
            self.origin.append(o)
            self.u.append(u)
            self.poly.append(np.asarray(poly, np.float64) if poly is not None else None)
        self.origin = np.array(self.origin).reshape(-1, 3)
        self.u = np.array(self.u).reshape(-1, 3)
        e = doc.get("edges", [])
        self.ea = np.array([x["a"] for x in e], np.float64).reshape(-1, 3)
        self.eb = np.array([x["b"] for x in e], np.float64).reshape(-1, 3)
        self.c = np.array([x["p"] for x in doc.get("corners", [])], np.float64).reshape(-1, 3)

    def transform(self, s: float, R: np.ndarray, t: np.ndarray) -> None:
        f = lambda x: s * x @ R.T + t
        self.n, self.u = self.n @ R.T, self.u @ R.T
        self.origin, self.ea, self.eb, self.c = map(f, (self.origin, self.ea, self.eb, self.c))
        self.poly = [None if p is None else p * s for p in self.poly]

    @property
    def v(self) -> np.ndarray:
        return np.cross(self.n, self.u)

    def plane_hits(self, q: np.ndarray, radius: float) -> tuple[np.ndarray, np.ndarray]:
        """(dist (Q,P), projected points (Q,P,3)); dist=inf where outside the polygon or radius."""
        if not len(self.n):
            return np.full((len(q), 0), np.inf), np.zeros((len(q), 0, 3))
        sd = np.einsum("qpd,pd->qp", q[:, None] - self.origin[None], self.n)
        proj = q[:, None] - sd[..., None] * self.n[None]
        d = np.abs(sd)
        v = self.v
        for j, poly in enumerate(self.poly):
            if poly is None:
                continue
            rel = proj[:, j] - self.origin[j]
            uv = np.stack([rel @ self.u[j], rel @ v[j]], 1)
            d[~point_in_polygon(uv, poly), j] = np.inf
        d[d > radius] = np.inf
        return d, proj

    def snap(
        self,
        q: np.ndarray,
        radius: float,
        fallback,
        corner_r: float | None = None,
        edge_r: float | None = None,
        beat: float | None = None,
    ) -> tuple[list[str], np.ndarray]:
        """Snap corner > edge > plane > fallback(q). Corners within `corner_r`, edges within
        `edge_r`, planes within `radius` (the first two default to `radius`). With `beat`, a
        corner only wins over an edge that is also in range when d_corner <= d_edge + beat
        (R6 §7.3 policy: corner_r=0.025, edge_r=0.02, beat=0.01)."""
        corner_r = radius if corner_r is None else corner_r
        edge_r = radius if edge_r is None else edge_r
        n = len(q)
        dc = np.full(n, np.inf)
        de = np.full(n, np.inf)
        if len(self.c):
            d = np.linalg.norm(q[:, None] - self.c[None], axis=-1)
            jc = d.argmin(1)
            dc = d[np.arange(n), jc]
        if len(self.ea):
            d, pe = closest_on_segments(q, self.ea, self.eb)
            je = d.argmin(1)
            de = d[np.arange(n), je]
        use_c = dc <= corner_r
        use_e = de <= edge_r
        if beat is not None:
            use_c &= ~use_e | (dc <= de + beat)
        use_e &= ~use_c
        kinds, out = ["mesh"] * n, fallback(q).copy()
        if use_c.any():
            out[use_c] = self.c[jc[use_c]]
        if use_e.any():
            out[use_e] = pe[np.flatnonzero(use_e), je[use_e]]
        done = use_c | use_e
        for i in np.flatnonzero(use_c):
            kinds[i] = "corner"
        for i in np.flatnonzero(use_e):
            kinds[i] = "edge"
        if len(self.n):
            d, p = self.plane_hits(q, radius)
            j = d.argmin(1)
            hit = ~done & np.isfinite(d[np.arange(n), j])
            out[hit] = p[np.flatnonzero(hit), j[hit]]
            for i in np.flatnonzero(hit):
                kinds[i] = "plane"
        return kinds, out


# --- GT helpers ----------------------------------------------------------------------------------


def gt_planes(gt: dict, X: dict, cam_centre: np.ndarray) -> dict:
    """GT plane regions: {name: (normal toward the cameras, origin, u, v, polygon2d)}."""
    out = {}
    for p in gt["planes"]:
        P = np.array([X[k] for k in p["points"]])
        if "extrude" in p:
            if p["extrude"] == "back":  # horizontal, perpendicular to the edge, away from cameras
                e = unit(np.cross([0.0, 1.0, 0.0], P[-1] - P[0]))
                e = -e if np.dot(cam_centre - P.mean(0), e) > 0 else e
            else:
                e = unit(p["extrude"])
            n = unit(np.cross(P[-1] - P[0], e))
            ring = np.vstack([P, P[::-1] + e * p["height"]])
        else:
            n, _, _ = fit_plane(P)
            ring = P
        o = ring.mean(0)
        if np.dot(cam_centre - o, n) < 0:
            n = -n
        u = unit(ring[1] - ring[0])
        u = unit(u - np.dot(u, n) * n)
        v = np.cross(n, u)
        out[p["name"]] = (n, o, u, v, np.stack([(ring - o) @ u, (ring - o) @ v], 1))
    return out


def literal_normal(spec: str, planes: dict, a: np.ndarray, b: np.ndarray, cam: np.ndarray):
    if spec in planes:
        return planes[spec][0]
    if spec in ("+y", "-y"):
        return np.array([0.0, 1.0 if spec == "+y" else -1.0, 0.0])
    if spec == "front":  # horizontal, perpendicular to the edge, toward the cameras
        n = unit(np.cross(b - a, [0.0, 1.0, 0.0]))
        return n if np.dot(cam - (a + b) / 2, n) > 0 else -n
    raise ValueError(f"unknown normal spec {spec!r}")


# --- metrics -------------------------------------------------------------------------------------


def region_vertices(verts, tree, plane, margin: float, band: float) -> np.ndarray:
    n, o, u, v, poly = plane
    r = float(np.linalg.norm(poly, axis=1).max())
    idx = np.array(tree.query_ball_point(o, r + band), dtype=np.int64)
    if not len(idx):
        return idx
    rel = verts[idx] - o
    uv = np.stack([rel @ u, rel @ v], 1)
    keep = (np.abs(rel @ n) < band) & point_in_polygon(uv, poly)
    keep &= dist_to_polygon_boundary(uv, poly) >= margin
    return idx[keep]


def planarity(mesh, planes: dict, struct: "Structure | None", margin: float, band: float) -> dict:
    from scipy.spatial import cKDTree

    verts = np.asarray(mesh.vertices)
    tree = cKDTree(verts)
    out = {}
    for name, pl in planes.items():
        idx = region_vertices(verts, tree, pl, margin, band)
        if len(idx) < 20:
            out[name] = {"n_vertices": len(idx)}
            continue
        n, c, rms = fit_plane(verts[idx])
        row = {
            "n_vertices": len(idx),
            "mesh_rms_m": rms,  # planarity of the mesh itself
            "mesh_vs_gt_deg": angle_deg(n, pl[0]),
            "mesh_offset_from_gt_m": float(abs(np.dot(c - pl[1], pl[0]))),
            "fit_normal": (n if np.dot(n, pl[0]) > 0 else -n).tolist(),
        }
        if struct is not None and len(struct.n):
            ang = np.degrees(np.arccos(np.clip(np.abs(struct.n @ pl[0]), -1, 1)))
            off = np.abs(np.einsum("pd,pd->p", struct.origin - pl[1], struct.n))
            ok = (ang < 10) & (off < 0.03)
            if ok.any():
                j = int(np.flatnonzero(ok)[np.argmin(off[ok] + ang[ok] * 0.002)])
                sd = (verts[idx] - struct.origin[j]) @ struct.n[j]
                row.update(
                    struct_plane=j,
                    struct_vs_gt_deg=float(ang[j]),
                    struct_offset_from_gt_m=float(off[j]),
                    mesh_rms_to_struct_m=float(np.sqrt(np.mean(sd**2))),
                    struct_normal=(
                        struct.n[j] if struct.n[j] @ pl[0] > 0 else -struct.n[j]
                    ).tolist(),
                )
        out[name] = row
    return out


def angle_errors(gt: dict, planes: dict, plan: dict) -> list[dict]:
    rows = []
    for p in gt.get("angle_pairs", []):
        a, b, exp = p["a"], p["b"], p["expect_deg"]
        if a not in planes or b not in planes:
            continue
        row = {
            "a": a,
            "b": b,
            "expect_deg": exp,
            "gt_err_deg": abs(angle_deg(planes[a][0], planes[b][0]) - exp),
        }
        for key, fld in (("mesh", "fit_normal"), ("struct", "struct_normal")):
            if fld in plan.get(a, {}) and fld in plan.get(b, {}):
                row[f"{key}_err_deg"] = abs(angle_deg(plan[a][fld], plan[b][fld]) - exp)
        rows.append(row)
    return rows


def edge_sharpness(
    mesh, gt: dict, X: dict, planes: dict, cam: np.ndarray, radius=0.06, bin_w=0.004
) -> dict:
    """Width of the normal-turn band across each GT edge: faces within `radius` of the edge
    interior (10-90 %) are split by side and binned by distance from the edge (turn fraction 0 =
    plane-1 normal, 1 = plane-2 normal, 0.5 on the crease). On each side the band ends where the
    binned mean has gone 75 % of the way from 0.5 to that side's own far-field level. Width =
    side 1 + side 2; a perfect crease scores 0, bin width 4 mm."""
    from scipy.spatial import cKDTree

    fc, fn = np.asarray(mesh.triangles_center), np.asarray(mesh.face_normals)
    tree = cKDTree(fc)
    out = {}
    for e in gt.get("edges", []):
        a, b = X[e["a"]], X[e["b"]]
        n1, n2 = (literal_normal(s, planes, a, b, cam) for s in e["normals"])
        d = unit(b - a)
        w1, w2 = unit(-n2 - np.dot(-n2, d) * d), unit(-n1 - np.dot(-n1, d) * d)
        L = np.linalg.norm(b - a)
        idx = np.array(tree.query_ball_point((a + b) / 2, L / 2 + radius), dtype=np.int64)
        if not len(idx):
            out[e["name"]] = {"n_faces": 0}
            continue
        rel = fc[idx] - a
        t = rel @ d
        perp = rel - t[:, None] * d
        s1, s2 = perp @ w1, perp @ w2
        keep = (t > 0.1 * L) & (t < 0.9 * L) & (np.linalg.norm(perp, axis=1) < radius)
        keep &= (s1 > -0.01) & (s2 > -0.01)  # outside the solid's two faces only
        if keep.sum() < 20:
            out[e["name"]] = {"n_faces": int(keep.sum())}
            continue
        nf = fn[idx][keep]
        a1 = np.degrees(np.arccos(np.clip(np.abs(nf @ n1), 0, 1)))
        a2 = np.degrees(np.arccos(np.clip(np.abs(nf @ n2), 0, 1)))
        f = a1 / np.maximum(a1 + a2, 1e-9)  # 0 on plane 1, 1 on plane 2
        side1 = s1[keep] >= s2[keep]
        widths = []
        for side, dist in ((side1, s1[keep]), (~side1, s2[keep])):
            # flat level = this side's median turn fraction 3-6 cm out (mesh-normal noise keeps it
            # off 0/1); the band ends at the first of two bins 75 % of the way from 0.5 to it
            dist, ff = np.clip(dist[side], 0, None), f[side]
            far = (dist > 0.5 * radius) & (dist < radius)
            if far.sum() < 5:
                widths.append(None)
                continue
            flat = float(np.median(ff[far]))
            thr = 0.5 + 0.75 * (flat - 0.5)
            bins = np.floor(dist / bin_w).astype(int)
            mean = [
                ff[bins == k].mean() if (bins == k).sum() >= 2 else np.nan
                for k in range(int(radius / bin_w))
            ]
            ok = [np.isfinite(m) and (m - thr) * np.sign(flat - 0.5) >= 0 for m in mean]
            width = next((k * bin_w for k in range(len(ok) - 1) if ok[k] and ok[k + 1]), None)
            widths.append(width)
        out[e["name"]] = {
            "n_faces": int(keep.sum()),
            "width_side1_m": widths[0],
            "width_side2_m": widths[1],
            "width_m": None if None in widths else widths[0] + widths[1],
        }
    return out


def lsd_reprojection(
    gt: dict, struct: "Structure", frames_dir: Path, n_frames=12, min_len=60.0
) -> dict:
    """Project structure edges into a fixed subset of SfM frames (exact poses) and compare with
    LSD lines: median px distance from projected edge
    samples to the nearest LSD line of similar direction (<5 deg), and recall of strong LSD lines
    (>= `min_len` px) that have a projected edge within 3 px / 5 deg. No occlusion test: hidden
    structure edges count against precision, so compare rows, not absolute values."""
    import cv2

    from pipeline.experiments.structure.gt import undistort_simple_radial
    from pipeline.package import Camera

    tr = gt["track"]
    if "R" not in tr or not len(struct.ea):
        return {}
    K = tr["intrinsics"]
    cams = {
        i: Camera(
            i, "", "", np.array(R), np.array(t), K["fx"], K["fy"], K["cx"], K["cy"], K["w"], K["h"]
        )
        for i, R, t in zip(tr["ids"], tr["R"], tr["t"])
    }
    # SfM frames with exact poses: interpolating a pose over the 0.5 s SfM gap is off by ~16 px
    # median (leave-one-out on this clip), useless at pixel level. The ids == 1 mod 25 subset
    # never feeds plane-based structure lines; LIMAP-style methods should hold these frames out.
    held = [int(i) for i in sorted(cams) if int(i) % 25 == 1]
    held = held[:: max(1, len(held) // n_frames)][:n_frames]
    f, cx, cy, k = gt["provenance"]["raw_camera"]["params"]
    dists, recalls = [], []
    for fid in held:
        img = cv2.imread(str(frames_dir / f"{fid:04d}.jpg"), cv2.IMREAD_GRAYSCALE)
        if img is None:
            continue
        cam = cams[f"{fid:04d}"]
        segs = cv2.createLineSegmentDetector().detect(img)[0]
        if segs is None:
            continue
        segs = segs.reshape(-1, 2, 2).astype(np.float64)
        segs = undistort_simple_radial(segs + 0.5, f, cx, cy, k) * [cam.fx, cam.fy] + [
            cam.cx,
            cam.cy,
        ]
        seg_len = np.linalg.norm(segs[:, 1] - segs[:, 0], axis=1)
        segs = segs[seg_len >= 15]
        # project structure edges, clip to z > 0.05
        A = struct.ea @ cam.R.T + cam.t
        B = struct.eb @ cam.R.T + cam.t
        ok = (A[:, 2] > 0.05) & (B[:, 2] > 0.05)
        if not ok.any() or not len(segs):
            continue
        pa = A[ok, :2] / A[ok, 2:] * [cam.fx, cam.fy] + [cam.cx, cam.cy]
        pb = B[ok, :2] / B[ok, 2:] * [cam.fx, cam.fy] + [cam.cx, cam.cy]
        inside = lambda p, w=cam.w, h=cam.h: (
            (p[:, 0] > 0) & (p[:, 0] < w) & (p[:, 1] > 0) & (p[:, 1] < h)
        )
        vis = inside(pa) | inside(pb)
        pa, pb = pa[vis], pb[vis]
        if not len(pa):
            continue
        sdir = unit(segs[:, 1] - segs[:, 0])
        edir = unit(pb - pa)
        # samples along projected edges (inside the image) -> nearest similar-direction LSD line
        for j in range(len(pa)):
            ts = np.linspace(0, 1, 11)[:, None]
            smp = pa[j] + ts * (pb[j] - pa[j])
            smp = smp[inside(smp)]
            simdir = np.abs(sdir @ edir[j]) > np.cos(np.radians(5))
            if not len(smp) or not simdir.any():
                continue
            a3 = np.pad(segs[simdir, 0], ((0, 0), (0, 1)))
            b3 = np.pad(segs[simdir, 1], ((0, 0), (0, 1)))
            d = closest_on_segments(np.pad(smp, ((0, 0), (0, 1))), a3, b3)[0].min(1)
            dists.extend(d.tolist())
        strong = np.linalg.norm(segs[:, 1] - segs[:, 0], axis=1) >= min_len
        for s, sd in zip(segs[strong], sdir[strong]):
            simdir = np.abs(edir @ sd) > np.cos(np.radians(5))
            if not simdir.any():
                recalls.append(False)
                continue
            mid = np.pad(((s[0] + s[1]) / 2)[None], ((0, 0), (0, 1)))
            d = closest_on_segments(
                mid, np.pad(pa[simdir], ((0, 0), (0, 1))), np.pad(pb[simdir], ((0, 0), (0, 1)))
            )[0]
            recalls.append(bool(d.min() <= 3.0))
    d = np.array(dists)
    return {
        "frames": held,
        "edge_sample_px_median": float(np.median(d)) if len(d) else None,
        "edge_sample_within_3px": float((d <= 3).mean()) if len(d) else None,
        "strong_lsd_recall_3px": float(np.mean(recalls)) if recalls else None,
        "n_strong_lsd": len(recalls),
    }


# --- driver --------------------------------------------------------------------------------------


def _load_scene(scene_dir: Path):
    import trimesh

    from pipeline.qa import _load_cameras, _resolve_package_files

    mesh_file, _ = _resolve_package_files(scene_dir)
    sj = scene_dir / "scene.json"
    coll = json.loads(sj.read_text()).get("collision", {}).get("file") if sj.exists() else None
    load = lambda p: trimesh.load(p, force="scene", process=False).to_geometry()
    mesh = load(scene_dir / mesh_file)
    col = load(scene_dir / coll) if coll and (scene_dir / coll).exists() else None
    return mesh, col, _load_cameras(scene_dir), mesh_file


def align_to_gt(gt: dict, ids: list[str], positions: np.ndarray, id_fps: float) -> tuple:
    tr = gt["track"]
    gt_t = id_times(tr["ids"], None, tr["id_fps"])
    t = id_times(ids, None, id_fps)
    m = (t >= gt_t.min()) & (t <= gt_t.max())
    return robust_sim3(
        positions[m], interpolate_positions_at_times(gt_t, np.array(tr["positions"]), t[m])
    )


def score(a) -> dict:
    t0 = time.time()
    gt = json.loads(Path(a.gt).read_text())
    scene = Path(a.scene)
    mesh, col, cams, mesh_file = _load_scene(scene)
    if a.mesh:
        import trimesh

        mesh = trimesh.load(a.mesh, force="scene", process=False).to_geometry()
    ids = sorted(cams)
    s, R, t, resid, keep = align_to_gt(gt, ids, np.array([cams[i].position for i in ids]), a.id_fps)
    M = np.eye(4)
    M[:3, :3], M[:3, 3] = s * R, t
    mesh.apply_transform(M)
    if col is not None:
        col.apply_transform(M)
    align = {
        "scale_gt_per_scene": s,
        "residual_m": resid,
        "inliers": int(keep.sum()),
        "n": len(keep),
    }

    struct, struct_info = None, {}
    if a.structure:
        doc = json.loads(Path(a.structure).read_text())
        struct = Structure(doc)
        if (
            doc.get("frame") == "cameras"
            or isinstance(doc.get("frame"), dict)
            and doc["frame"].get("kind") == "cameras"
        ):
            ss, RR, tt, rr, kk = _align_struct_cameras(doc, Path(a.structure).parent, gt)
            struct_info["own_alignment"] = {"scale": ss, "residual_m": rr, "inliers": int(kk.sum())}
        else:
            ss, RR, tt = s, R, t
        struct.transform(ss, RR, tt)
        struct_info.update(
            planes=len(struct.n),
            edges=len(struct.ea),
            corners=len(struct.c),
            bytes=Path(a.structure).stat().st_size,
            cost=doc.get("cost"),
            method=doc.get("method"),
        )

    X = {k: np.array(v["xyz"]) for k, v in gt["points"].items()}
    grade = {k: v.get("grade", "A") for k, v in gt["points"].items()}
    # snap/measurement stats use points of the chosen grades; planes/edges keep every point
    names = sorted(k for k in X if grade[k] in a.grades)
    ok = set(names)
    pairs = [p for p in gt.get("pairs", []) if p["a"] in ok and p["b"] in ok]
    gt_edges = [e for e in gt.get("edges", []) if e["a"] in ok and e["b"] in ok]
    Q = np.array([X[k] for k in names])
    cam_c = np.array(gt["track"]["positions"]).mean(0)
    snap_mesh = MeshQuery(col if col is not None else mesh)
    mesh_q = MeshQuery(mesh)
    fallback = lambda q: snap_mesh.closest(q)[0]
    per_point = {}
    _, d_col = snap_mesh.closest(Q)
    _, d_mesh = mesh_q.closest(Q)
    for i, k in enumerate(names):
        per_point[k] = {
            "kind": gt["points"][k].get("kind", "corner"),
            "grade": grade[k],
            "collision_mm": d_col[i] * 1000,
            "mesh_mm": d_mesh[i] * 1000,
        }
    snap = {"collision": stats_mm(d_col), "mesh": stats_mm(d_mesh)}
    snapped = dict(zip(names, snap_mesh.closest(Q)[0]))
    if struct is not None:
        kinds, P = struct.snap(Q, a.radius, fallback)
        dp = np.linalg.norm(P - Q, axis=1)
        snapped = {k: P[i] for i, k in enumerate(names)}
        dc = (
            np.linalg.norm(Q[:, None] - struct.c[None], axis=-1).min(1)
            if len(struct.c)
            else np.full(len(Q), np.inf)
        )
        de = (
            closest_on_segments(Q, struct.ea, struct.eb)[0].min(1)
            if len(struct.ea)
            else np.full(len(Q), np.inf)
        )
        for i, k in enumerate(names):
            per_point[k].update(
                snap_kind=kinds[i],
                snap_mm=dp[i] * 1000,
                nearest_corner_mm=dc[i] * 1000,
                nearest_edge_mm=de[i] * 1000,
            )
        corner_pts = [i for i, k in enumerate(names) if per_point[k]["kind"] == "corner"]
        snap.update(
            priority=stats_mm(dp),
            priority_kinds={kk: kinds.count(kk) for kk in set(kinds)},
            corner=stats_mm(dc[np.isfinite(dc)]),
            corner_recall_3cm=float((dc[corner_pts] <= 0.03).mean()),
            edge=stats_mm(de[np.isfinite(de)]),
            edge_recall_3cm=float((de <= 0.03).mean()),
        )

    # realistic aim: the pinch tip lands uniformly within `aim` of the GT point; a mesh snap keeps
    # the tangential part of that miss, a corner/edge snap removes it
    rng = np.random.default_rng(0)
    J = (
        unit(rng.normal(size=(len(Q), a.aim_n, 3)))
        * a.aim
        * rng.random((len(Q), a.aim_n, 1)) ** (1 / 3)
    )
    snapfn = (lambda q: struct.snap(q, a.radius, fallback)[1]) if struct is not None else fallback
    Pj = snapfn((Q[:, None] + J).reshape(-1, 3)).reshape(len(Q), a.aim_n, 3)
    err_j = np.linalg.norm(Pj - Q[:, None], axis=-1)
    corner_idx = [i for i, k in enumerate(names) if per_point[k]["kind"] == "corner"]
    snap["aim"] = {
        "aim_m": a.aim,
        "all": stats_mm(err_j.ravel()),
        "corners": stats_mm(err_j[corner_idx].ravel()),
    }
    jit = {k: Pj[i] for i, k in enumerate(names)}

    # GT edges: snap samples along each edge; error = distance of the snapped point to the GT line
    edge_err, edge_err_mesh = [], []
    for e in gt_edges:
        ea, eb = X[e["a"]], X[e["b"]]
        smp = ea + np.linspace(0.1, 0.9, 9)[:, None] * (eb - ea)
        d = unit(eb - ea)
        line_dist = lambda p, ea=ea, d=d: np.linalg.norm(
            (p - ea) - ((p - ea) @ d)[:, None] * d, axis=1
        )
        edge_err_mesh.extend(line_dist(fallback(smp)).tolist())
        if struct is not None:
            edge_err.extend(line_dist(struct.snap(smp, a.radius, fallback)[1]).tolist())
    snap["gt_edges_collision"] = stats_mm(edge_err_mesh)
    if struct is not None:
        snap["gt_edges_priority"] = stats_mm(edge_err)

    # both snap policies side by side; "r6" (R6 §7.3, what P3 ships) is the headline
    A_idx = [i for i, k in enumerate(names) if grade[k] == "A"]
    policies = {"priority": {}, "r6": {"corner_r": 0.025, "edge_r": 0.02, "beat": 0.01}}
    snap["policies"] = {}
    for pname, kw in policies.items():
        if struct is not None:
            fn = lambda q, kw=kw: struct.snap(q, a.radius, fallback, **kw)
        else:
            fn = lambda q: (["mesh"] * len(q), fallback(q))
        kinds_p, Pp = fn(Q)
        dpp = np.linalg.norm(Pp - Q, axis=1)
        Pjp = fn((Q[:, None] + J).reshape(-1, 3))[1].reshape(len(Q), a.aim_n, 3)
        ejp = np.linalg.norm(Pjp - Q[:, None], axis=-1)
        sp = dict(zip(names, Pp))
        jp = {k: Pjp[i] for i, k in enumerate(names)}
        ee = []
        for e in gt_edges:
            ea, eb = X[e["a"]], X[e["b"]]
            smp = ea + np.linspace(0.1, 0.9, 9)[:, None] * (eb - ea)
            d = unit(eb - ea)
            r_ = fn(smp)[1] - ea
            ee.extend(np.linalg.norm(r_ - (r_ @ d)[:, None] * d, axis=1).tolist())
        snap["policies"][pname] = {
            "params": kw or {"radius": a.radius},
            "kinds": {kk: kinds_p.count(kk) for kk in set(kinds_p)},
            "perfect": stats_mm(dpp),
            "perfect_gradeA": stats_mm(dpp[A_idx]),
            "aim": stats_mm(ejp.ravel()),
            "aim_gradeA": stats_mm(ejp[A_idx].ravel()),
            "aim_corners": stats_mm(ejp[corner_idx].ravel()),
            "gt_edges": stats_mm(ee),
            "meas_abs": stats_mm(
                [abs(np.linalg.norm(sp[q["a"]] - sp[q["b"]]) - q["dist_m"]) for q in pairs]
            ),
            "meas_abs_aim": stats_mm(
                [
                    float(
                        np.median(
                            np.abs(np.linalg.norm(jp[q["a"]] - jp[q["b"]], axis=1) - q["dist_m"])
                        )
                    )
                    for q in pairs
                ]
            ),
        }
        for i, k in enumerate(names):
            per_point[k][f"{pname}_kind"] = kinds_p[i]
            per_point[k][f"{pname}_mm"] = dpp[i] * 1000

    # measurements: snapped lengths vs GT, and ratios to the reference pair (scale-free)
    meas = []
    for p in pairs:
        L = float(np.linalg.norm(snapped[p["a"]] - snapped[p["b"]]))
        meas.append(
            {
                "name": p["name"],
                "gt_m": p["dist_m"],
                "snapped_m": L,
                "err_mm": (L - p["dist_m"]) * 1000,
                "err_pct": (L / p["dist_m"] - 1) * 100,
            }
        )
    meas_aim = [
        float(np.median(np.abs(np.linalg.norm(jit[p["a"]] - jit[p["b"]], axis=1) - p["dist_m"])))
        for p in pairs
    ]
    ref = next((m for m in meas if m["name"] == gt.get("ratio_reference")), None)
    if ref:
        for m in meas:
            m["ratio_err_pct"] = (
                (m["snapped_m"] / ref["snapped_m"]) / (m["gt_m"] / ref["gt_m"]) - 1
            ) * 100
    abs_err = [abs(m["err_mm"]) / 1000 for m in meas]
    measurement = {
        "abs": stats_mm(abs_err),
        "abs_aim": stats_mm(meas_aim),
        "abs_pct_median": float(np.median([abs(m["err_pct"]) for m in meas])) if meas else None,
        "ratio_pct_median": float(
            np.median([abs(m["ratio_err_pct"]) for m in meas if m is not ref])
        )
        if ref
        else None,
        "pairs": meas,
    }
    by_name = {m["name"]: m for m in meas}
    repeat = []
    for g in gt.get("same_length_groups", []):
        ms = [by_name[n] for n in g["pairs"] if n in by_name]
        if len(ms) >= 2:
            sl, gl = [m["snapped_m"] for m in ms], [m["gt_m"] for m in ms]
            repeat.append(
                {
                    "name": g["name"],
                    "snapped_spread_mm": (max(sl) - min(sl)) * 1000,
                    "gt_spread_mm": (max(gl) - min(gl)) * 1000,
                }
            )

    planes = gt_planes(gt, X, cam_c)
    plan = planarity(mesh, planes, struct, a.margin, a.band)
    angles = angle_errors(gt, planes, plan)
    sharp = edge_sharpness(mesh, gt, X, planes, cam_c)
    reproj = (
        lsd_reprojection(gt, struct, Path(a.frames)) if (a.frames and struct is not None) else {}
    )

    med = lambda xs: float(np.median(xs)) if len(xs) else None
    result = {
        "name": a.name,
        "scene": str(scene),
        "mesh": a.mesh or mesh_file,
        "structure": a.structure,
        "gt": a.gt,
        "gt_points": len(names),
        "gt_points_gradeA": len(A_idx),
        "grades": a.grades,
        "alignment": align,
        "radius_m": a.radius,
        "structure_info": struct_info,
        "snap": snap,
        "measurement": measurement,
        "repeatability": repeat,
        "planarity": plan,
        "planarity_summary": {
            "mesh_rms_mm_median": med(
                [v["mesh_rms_m"] * 1000 for v in plan.values() if "mesh_rms_m" in v]
            ),
            "mesh_vs_gt_deg_median": med(
                [v["mesh_vs_gt_deg"] for v in plan.values() if "mesh_vs_gt_deg" in v]
            ),
            "struct_matched": sum("struct_plane" in v for v in plan.values()),
            "struct_vs_gt_deg_median": med(
                [v["struct_vs_gt_deg"] for v in plan.values() if "struct_vs_gt_deg" in v]
            ),
        },
        "angles": angles,
        "angle_summary": {
            k: med([r[k] for r in angles if k in r])
            for k in ("gt_err_deg", "mesh_err_deg", "struct_err_deg")
        },
        "sharpness": sharp,
        "sharpness_mm_median": med(
            [v["width_m"] * 1000 for v in sharp.values() if v.get("width_m")]
        ),
        "reprojection": reproj,
        "per_point": per_point,
        "score_s": time.time() - t0,
        "note": a.note,
        "scale_note": gt["frame"].get("scale_note"),
    }
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("a") as fh:
        fh.write(json.dumps(result, default=float) + "\n")
    return result


def _align_struct_cameras(doc: dict, base: Path, gt: dict):
    """Structure in its own frame: Sim(3) of its cameras (pipeline cameras.json or a COLMAP
    sparse dir, ids = %04d frame stems) onto the GT track."""
    p = Path(doc["cameras"])
    p = p if p.is_absolute() else base / p
    if p.is_dir():
        import pycolmap

        rec = pycolmap.Reconstruction(str(p))
        ims = sorted(rec.images.values(), key=lambda im: im.name)
        ids = [Path(im.name).stem for im in ims]
        C = np.array([im.projection_center() for im in ims])
    else:
        rows = json.loads(p.read_text())
        ids = [r["id"] for r in rows]
        C = np.array([-np.array(r["R"]).T @ np.array(r["t"]) for r in rows])
    return align_to_gt(gt, ids, C, doc.get("id_fps", 10.0))


def summary_line(r: dict) -> str:
    f = lambda x, fmt=".1f": "-" if x is None else format(x, fmt)
    sn = r["snap"]
    pr = sn.get("priority", sn["collision"])
    pol = sn.get("policies", {})
    r6, old = pol.get("r6", {}), pol.get("priority", {})
    m = lambda d, k: f"{f(d.get(k, {}).get('median_mm'))}/{f(d.get(k, {}).get('p90_mm'))}"
    head = (
        f"{r['name']}: R6 aim {m(r6, 'aim')} perfect {m(r6, 'perfect')} gradeA-aim "
        f"{m(r6, 'aim_gradeA')} meas-aim {f(r6.get('meas_abs_aim', {}).get('median_mm'))} mm | "
        f"priority3cm aim {m(old, 'aim')} perfect {m(old, 'perfect')} | "
    )
    return head + (
        f"snap {f(pr.get('median_mm'))}/{f(pr.get('p90_mm'))} mm "
        f"(collision {f(sn['collision'].get('median_mm'))}/{f(sn['collision'].get('p90_mm'))}), "
        f"aim±{r['snap']['aim']['aim_m'] * 100:.0f}cm {f(r['snap']['aim']['all'].get('median_mm'))}/{f(r['snap']['aim']['all'].get('p90_mm'))} mm, "
        f"meas-aim {f(r['measurement']['abs_aim'].get('median_mm'))} mm, "
        f"corner recall@3cm {f(sn.get('corner_recall_3cm'), '.2f')}, "
        f"gt-edge {f(sn.get('gt_edges_priority', sn['gt_edges_collision']).get('median_mm'))} mm, "
        f"meas |err| {f(r['measurement']['abs'].get('median_mm'))} mm "
        f"(ratio {f(r['measurement']['ratio_pct_median'], '.2f')} %), "
        f"planarity {f(r['planarity_summary']['mesh_rms_mm_median'])} mm, "
        f"angle mesh/struct {f(r['angle_summary']['mesh_err_deg'], '.2f')}/{f(r['angle_summary']['struct_err_deg'], '.2f')} deg, "
        f"sharpness {f(r['sharpness_mm_median'])} mm, "
        f"LSD {f(r['reprojection'].get('edge_sample_px_median'))} px / recall {f(r['reprojection'].get('strong_lsd_recall_3px'), '.2f')}"
    )
