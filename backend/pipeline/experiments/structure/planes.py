"""S1 baseline structure layer: Manhattan planes from the hybrid dense cloud -> creases -> corners.

    python -m pipeline.experiments.structure.planes <work_dir> <scene_dir> --out structure.json

1. The union cloud (`mvs/depthfusion/union.ply`) is read; by default only its OpenMVS points
   (the first `union_mvs_points`, R5: fill points carry a 2-10 mm MoGe bias). It is brought into
   the scene frame by the exact Sim(3) between the SfM and `cameras.json` camera centres.
2. Axes: +Y (the calibration's up) and the dominant horizontal normal direction (yaw histogram
   mod 90 deg). Planes are only looked for along these three axes, so every plane is Manhattan-
   regularized by construction (`regularized.delta_deg` records the free-fit deviation).
3. Per signed axis: peaks of the offset histogram -> inliers within `eps` -> connected
   components on a `cell` grid in the plane -> one plane per component (convex-hull polygon).
4. Creases: every pair of planes on different axes is intersected; the crease is the part of the
   line where both planes have inliers within `line_r` (`kind: crease`).
5. Corners: plane triples (one per axis) whose three creases pass within `corner_r` of their
   common point (`kind: 3plane`).

All distances are in scene units (the scene's metric calibration).
"""

import json
import time
from pathlib import Path

import numpy as np
from scipy import ndimage
from scipy.spatial import ConvexHull

# --- IO -----------------------------------------------------------------------------------------


def read_union_ply(path: Path) -> tuple[np.ndarray, np.ndarray]:
    """xyz, normals of an OpenMVS-style binary ply with trailing per-point lists (view indices,
    weights); the variable-length records need a sequential offset walk (a few s for 1M)."""
    raw = Path(path).read_bytes()
    end = raw.index(b"end_header\n") + len(b"end_header\n")
    header = raw[:end].decode()
    n = int(header.split("element vertex ")[1].split()[0])
    props = [ln.split()[-1] for ln in header.splitlines() if ln.startswith("property ")]
    if props[:9] != ["x", "y", "z", "red", "green", "blue", "nx", "ny", "nz"]:
        raise ValueError(f"unexpected ply layout {props}")
    fixed = 12 + 3 + 12
    body = np.frombuffer(raw, np.uint8, offset=end)
    offs = np.empty(n, np.int64)
    o = 0
    n_lists = sum(ln.startswith("property list uint8") for ln in header.splitlines())
    for i in range(n):  # ponytail: python walk over variable-length rows; numba if it ever matters
        offs[i] = o
        o += fixed
        for _ in range(n_lists):  # trailing `list uint8 <4-byte>` props (view indices, weights)
            o += 1 + 4 * int(body[o])
    rows = np.stack([body[offs + k] for k in range(fixed)], 1).copy()
    xyz = rows[:, :12].view("<f4").reshape(n, 3).astype(np.float64)
    nrm = rows[:, 15:27].view("<f4").reshape(n, 3).astype(np.float64)
    return xyz, nrm


def scene_similarity(sparse_dir: Path, scene_dir: Path) -> tuple[float, np.ndarray, np.ndarray]:
    """Exact Sim(3) SfM -> scene frame from the shared camera centres."""
    import pycolmap

    from pipeline.calibrate import umeyama
    from pipeline.qa import _load_cameras

    cams = _load_cameras(scene_dir)
    rec = pycolmap.Reconstruction(str(sparse_dir))
    src, dst = [], []
    for im in rec.images.values():
        cid = Path(im.name).stem
        if cid in cams:
            src.append(im.projection_center())
            dst.append(cams[cid].position)
    s, R, t, _ = umeyama(np.array(src), np.array(dst))
    return s, R, t


# --- geometry -----------------------------------------------------------------------------------


def dominant_axes(normals: np.ndarray, up=(0.0, 1.0, 0.0)) -> np.ndarray:
    """[x', up, z']: up plus the dominant horizontal direction (yaw histogram mod 90 deg)."""
    up = np.asarray(up, np.float64)
    h = normals[np.abs(normals @ up) < 0.3]
    yaw = np.degrees(np.arctan2(h[:, 2], h[:, 0])) % 90
    hist, edges = np.histogram(yaw, bins=180, range=(0, 90))
    hist = ndimage.uniform_filter1d(hist.astype(float), 5, mode="wrap")
    peak = (edges[np.argmax(hist)] + 0.25) % 90
    d = (yaw - peak + 45) % 90 - 45
    peak = peak + float(np.mean(d[np.abs(d) < 5]))
    th = np.radians(peak)
    x = np.array([np.cos(th), 0.0, np.sin(th)])
    return np.array([x, up, np.cross(x, up)])


def refine_yaw(P: np.ndarray, planes: list[dict], axes: np.ndarray, top: int = 10) -> np.ndarray:
    """Area-weighted mean yaw (mod 90 deg) of the free-fit normals of the `top` largest vertical
    planes; returns the re-rotated axes."""
    vert = sorted(
        (p for p in planes if p["regularized"]["axis"] != 1), key=lambda p: -p["area_m2"]
    )[:top]
    yaws, w = [], []
    for p in vert:
        pts = P[p["_idx"]]
        nf = np.linalg.svd(pts - pts.mean(0), full_matrices=False)[2][-1]
        yaws.append(np.degrees(np.arctan2(nf[2], nf[0])) % 90)
        w.append(p["area_m2"])
    y0 = np.degrees(np.arctan2(axes[0][2], axes[0][0])) % 90
    d = (np.array(yaws) - y0 + 45) % 90 - 45
    th = np.radians(y0 + float(np.average(d, weights=w)))
    x = np.array([np.cos(th), 0.0, np.sin(th)])
    return np.array([x, axes[1], np.cross(x, axes[1])])


def _basis(n: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    u = np.cross(n, [0.0, 1.0, 0.0]) if abs(n[1]) < 0.9 else np.cross(n, [1.0, 0.0, 0.0])
    u /= np.linalg.norm(u)
    return u, np.cross(n, u)


def detect_planes(
    P: np.ndarray,
    N: np.ndarray,
    axes: np.ndarray,
    eps: float = 0.005,
    ang_deg: float = 12.0,
    bin_w: float = 0.001,
    cell: float = 0.01,
    min_pts: int = 300,
    min_area: float = 0.002,
) -> list[dict]:
    planes = []
    cos_t = np.cos(np.radians(ang_deg))
    for k, a in enumerate(axes):
        for sgn in (1.0, -1.0):
            n = sgn * a
            sel = np.flatnonzero(N @ n > cos_t)
            if len(sel) < min_pts:
                continue
            d = P[sel] @ n
            lo, hi = d.min(), d.max()
            hist, edges = np.histogram(d, bins=max(1, int((hi - lo) / bin_w)), range=(lo, hi))
            sm = ndimage.uniform_filter1d(hist.astype(float), max(1, int(eps / bin_w)))
            win = int(2 * eps / bin_w)
            peaks = np.flatnonzero((sm == ndimage.maximum_filter1d(sm, 2 * win + 1)) & (sm > 0))
            peaks = peaks[np.argsort(-sm[peaks])]
            taken = np.zeros(len(sel), bool)
            u, v = _basis(n)
            for pk in peaks:
                off = (edges[pk] + edges[pk + 1]) / 2
                inl = (~taken) & (np.abs(d - off) < eps)
                if inl.sum() < min_pts:
                    continue
                off = float(np.median(d[inl]))
                inl = (~taken) & (np.abs(d - off) < eps)
                taken |= inl
                idx = sel[inl]
                uv = np.stack([P[idx] @ u, P[idx] @ v], 1)
                ij = np.floor((uv - uv.min(0)) / cell).astype(int)
                grid = np.zeros(ij.max(0) + 1, bool)
                grid[ij[:, 0], ij[:, 1]] = True
                lab, nlab = ndimage.label(ndimage.binary_closing(grid, iterations=1) | grid)
                comp = lab[ij[:, 0], ij[:, 1]]
                for c in range(1, nlab + 1):
                    m = comp == c
                    area = (lab == c).sum() * cell * cell
                    if m.sum() < min_pts or area < min_area:
                        continue
                    pts = P[idx[m]]
                    planes.append(_plane_record(pts, idx[m], n, k, u, v, area))
    return planes


def _plane_record(pts, idx, n, axis_k, u, v, area) -> dict:
    off = float(np.median(pts @ n))
    c = pts.mean(0)
    c = c - (c @ n - off) * n  # origin on the plane
    ctr = pts - c
    nf = np.linalg.svd(ctr, full_matrices=False)[2][-1]
    uv = np.stack([ctr @ u, ctr @ v], 1)
    hull = uv[ConvexHull(uv).vertices] if len(uv) >= 3 else uv
    return {
        "normal": n,
        "offset": -off,
        "origin": c,
        "u": u,
        "polygon": hull,
        "area_m2": float(area),
        "support": len(pts),
        "rms_m": float(np.sqrt(np.mean((pts @ n - off) ** 2))),
        "regularized": {
            "axis": int(axis_k),
            "delta_deg": float(np.degrees(np.arccos(min(1.0, abs(float(nf @ n)))))),
        },
        "_idx": idx,
    }


def creases(
    P: np.ndarray, planes: list[dict], line_r: float = 0.015, t_bin: float = 0.005, min_len=0.03
) -> list[dict]:
    """Plane-pair intersection lines, clipped to where both planes have inliers within
    `line_r`; one edge per contiguous supported run."""
    out = []
    boxes = [(P[p["_idx"]].min(0) - line_r, P[p["_idx"]].max(0) + line_r) for p in planes]
    for i in range(len(planes)):
        for j in range(i + 1, len(planes)):
            pi, pj = planes[i], planes[j]
            if pi["regularized"]["axis"] == pj["regularized"]["axis"]:
                continue
            (lo_i, hi_i), (lo_j, hi_j) = boxes[i], boxes[j]
            if np.any(np.maximum(lo_i, lo_j) > np.minimum(hi_i, hi_j)):
                continue
            ni, nj = pi["normal"], pj["normal"]
            dvec = np.cross(ni, nj)
            dvec /= np.linalg.norm(dvec)
            A = np.array([ni, nj, dvec])
            x0 = np.linalg.solve(A, [-pi["offset"], -pj["offset"], 0.0])
            ts = []
            for p in (pi, pj):
                rel = P[p["_idx"]] - x0
                t = rel @ dvec
                near = np.linalg.norm(rel - t[:, None] * dvec, axis=1) < line_r
                ts.append(t[near])
            if min(len(ts[0]), len(ts[1])) < 5:
                continue
            lo, hi = max(ts[0].min(), ts[1].min()), min(ts[0].max(), ts[1].max())
            if hi - lo < min_len:
                continue
            nb = int(np.ceil((hi - lo) / t_bin))
            h0 = np.histogram(ts[0], nb, (lo, hi))[0] > 0
            h1 = np.histogram(ts[1], nb, (lo, hi))[0] > 0
            both = ndimage.binary_closing(h0 & h1, iterations=2) | (h0 & h1)
            lab, nl = ndimage.label(both)
            for c in range(1, nl + 1):
                ks = np.flatnonzero(lab == c)
                a_t, b_t = lo + ks[0] * t_bin, lo + (ks[-1] + 1) * t_bin
                if b_t - a_t < min_len:
                    continue
                # convex if each plane's support lies behind the other plane
                wi = (
                    np.median(P[pi["_idx"]][np.abs((P[pi["_idx"]] - x0) @ nj) < 0.1] @ nj)
                    + pj["offset"]
                )
                out.append(
                    {
                        "a": x0 + a_t * dvec,
                        "b": x0 + b_t * dvec,
                        "planes": [i, j],
                        "kind": "crease",
                        "dihedral_deg": float(np.degrees(np.arccos(abs(ni @ nj)))),
                        "convex": bool(wi < 0),
                        "src": ["plane_intersection"],
                        "support": float((h0 & h1)[ks].mean()),
                    }
                )
    return out


def corners(
    P: np.ndarray, planes: list[dict], edges: list[dict], corner_r: float = 0.015
) -> list[dict]:
    """Corners where a crease (planes i, j) meets a plane k on the third axis: the triple point
    must lie within `corner_r` of the crease segment and have plane-k inliers within `corner_r`.
    `kind: 3plane` when all three pairs are creased, else `crease_plane`. `residual_m` is the
    larger of the two gaps."""
    from scipy.spatial import cKDTree

    from pipeline.experiments.structure.score import closest_on_segments

    trees = [cKDTree(P[p["_idx"]]) for p in planes]
    pairs = {tuple(sorted(e["planes"])) for e in edges}
    best: dict[tuple, dict] = {}
    for e in edges:
        i, j = e["planes"]
        for k in range(len(planes)):
            if len({planes[m]["regularized"]["axis"] for m in (i, j, k)}) != 3:
                continue
            A = np.array([planes[m]["normal"] for m in (i, j, k)])
            X = np.linalg.solve(A, [-planes[m]["offset"] for m in (i, j, k)])
            d_e = closest_on_segments(X[None], e["a"][None], e["b"][None])[0][0, 0]
            if d_e > corner_r:
                continue
            d_k = trees[k].query(X)[0]
            if d_k > corner_r:
                continue
            key = tuple(sorted((i, j, k)))
            three = all(tuple(sorted(q)) in pairs for q in ((i, j), (i, k), (j, k)))
            c = {
                "p": X,
                "kind": "3plane" if three else "crease_plane",
                "planes": list(key),
                "residual_m": float(max(d_e, d_k)),
            }
            if key not in best or c["residual_m"] < best[key]["residual_m"]:
                best[key] = c
    return list(best.values())


def snap_ends(edges: list[dict], cs: list[dict], extend: float, corner_r: float) -> None:
    """Support runs end short of the real crease end (mesh rounding, occlusion), and a clamped
    edge snap then drags the tip along the line. Extend every crease by `extend` each way, then
    move an endpoint onto a corner of the same planes when one lies within reach."""
    for e in edges:
        d = e["b"] - e["a"]
        d = d / np.linalg.norm(d)
        e["a"], e["b"] = e["a"] - extend * d, e["b"] + extend * d
        for c in cs:
            if not set(e["planes"]) <= set(c["planes"]):
                continue
            for end in ("a", "b"):
                if np.linalg.norm(c["p"] - e[end]) <= extend + corner_r:
                    e[end] = c["p"].copy()


# --- driver ---------------------------------------------------------------------------------------


def build(a) -> dict:
    t0 = time.time()
    work, scene = Path(a.work), Path(a.scene)
    df = work / "mvs" / "depthfusion"
    if a.source == "cloud":
        xyz, nrm = read_union_ply(df / "union.ply")
        n_mvs = json.loads((df / "stats.json").read_text())["timings_s"].get("union_mvs_points")
        if not a.with_fill and n_mvs:
            xyz, nrm = xyz[:n_mvs], nrm[:n_mvs]
        s, R, t = scene_similarity(work / "sfm" / "dense" / "sparse", scene)
        P = s * xyz @ R.T + t
        N = nrm @ R.T
    else:  # the scene's own textured mesh: graph-cut closes blank faces MVS leaves empty
        import trimesh

        from pipeline.qa import _resolve_package_files

        mesh = trimesh.load(
            a.mesh or scene / _resolve_package_files(scene)[0], force="scene", process=False
        ).to_geometry()
        P, fi = trimesh.sample.sample_surface(mesh, a.samples, seed=0)
        P, N = np.asarray(P), np.asarray(mesh.face_normals[fi])
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-12)
    t_load = time.time() - t0
    axes = dominant_axes(N)
    if a.refine_axes:  # yaw from the large vertical planes' own normals (mesh normals are noisy)
        planes = detect_planes(P, N, axes, eps=a.eps, ang_deg=a.ang, cell=a.cell, min_pts=a.min_pts)
        axes = refine_yaw(P, planes, axes)
    planes = detect_planes(
        P, N, axes, eps=a.eps, ang_deg=a.ang, cell=a.cell, min_pts=a.min_pts, min_area=a.min_area
    )
    edges = creases(P, planes, line_r=a.line_r)
    edges = [e for e in edges if e["convex"] or a.concave]
    cs = corners(P, planes, edges, corner_r=a.corner_r)
    snap_ends(edges, cs, a.extend, a.corner_r)
    doc = {
        "schema": "airtools.structure/1",
        "frame": "scene",
        "method": "s1-manhattan-planes",
        "params": {k: v for k, v in vars(a).items() if k not in ("func",)},
        "axes": axes.tolist(),
        "cost": {"runtime_s": time.time() - t0, "load_s": t_load, "cores": 1, "points": len(P)},
        "planes": [],
        "edges": [],
        "corners": [],
    }
    for i, p in enumerate(planes):
        doc["planes"].append(
            {
                "id": f"p{i}",
                **{
                    k: (v.tolist() if isinstance(v, np.ndarray) else v)
                    for k, v in p.items()
                    if k != "_idx"
                },
            }
        )
    for i, e in enumerate(edges):
        doc["edges"].append(
            {
                "id": f"e{i}",
                **{k: (v.tolist() if isinstance(v, np.ndarray) else v) for k, v in e.items()},
                "planes": [f"p{m}" for m in e["planes"]],
            }
        )
    for i, c in enumerate(cs):
        doc["corners"].append(
            {
                "id": f"c{i}",
                "p": c["p"].tolist(),
                "kind": c["kind"],
                "planes": [f"p{m}" for m in c["planes"]],
                "residual_m": c["residual_m"],
            }
        )
    Path(a.out).write_text(json.dumps(doc))
    print(
        f"{len(P)} pts, {len(planes)} planes, {len(edges)} edges, {len(cs)} corners, "
        f"{time.time() - t0:.1f}s -> {a.out}"
    )
    return doc


def main(argv=None) -> None:
    import argparse

    p = argparse.ArgumentParser(prog="python -m pipeline.experiments.structure.planes")
    p.add_argument("work", help="the run's work dir (mvs/depthfusion/union.ply, sfm/dense/sparse)")
    p.add_argument("scene", help="the scene package (cameras.json defines the frame)")
    p.add_argument("--out", required=True)
    p.add_argument("--source", choices=("cloud", "mesh"), default="mesh")
    p.add_argument("--mesh", default=None, help="--source mesh: this glb instead of the scene's")
    p.add_argument("--samples", type=int, default=800_000, help="--source mesh: surface samples")
    p.add_argument("--with-fill", action="store_true", help="cloud: also use the MoGe fill points")
    p.add_argument("--ang", type=float, default=15.0, help="normal-to-axis tolerance (deg)")
    p.add_argument("--eps", type=float, default=0.005, help="plane inlier distance")
    p.add_argument("--cell", type=float, default=0.01, help="in-plane connectivity grid")
    p.add_argument("--min-pts", type=int, default=300)
    p.add_argument("--min-area", type=float, default=0.002)
    p.add_argument("--line-r", type=float, default=0.015, help="crease support radius")
    p.add_argument("--corner-r", type=float, default=0.015)
    p.add_argument("--concave", action="store_true", help="keep concave creases too")
    p.add_argument("--no-refine-axes", dest="refine_axes", action="store_false")
    p.add_argument("--extend", type=float, default=0.02, help="crease extension each end")
    build(p.parse_args(argv))


if __name__ == "__main__":
    main()
