"""S3: lift PxwPlanar per-frame plane segments into 3D with our COLMAP poses, fuse them across
frames and write the plane layer as `airtools.structure/1` (docs/research/structure/
s3-learned-planes.md). CPU only; runs in any env with numpy, cv2 and pycolmap.

    python pxw_lift.py --dense work/dji0095/sfm/dense --mvs work/dji0095/mvs \
        --cache /workspace/s3/pxw_cache --out /workspace/s3/pxw/structure.json

Per frame and segment (PxwPlanar labels): position the plane by least squares on OpenMVS depth
where PatchMatch has consistent pixels inside the (eroded) mask, else on MoGe depth aligned to the
frame's SfM points (E3's `plane` fit). Mono normals only group pixels (R5: 8-10 deg median error).
The mask's pixels are then ray-cast onto that plane, which gives the support. Per-frame planes are
fused by normal, offset and shared support voxels, refit on all members' points (MVS first), split
into connected support components, and intersected pairwise (edges) and in triples (corners).
Output is in COLMAP units (`frame: cameras`); tolerances are metric via MoGe's units-per-metre.
"""

import argparse
import json
import sys
import time
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[3]
if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)
sys.path.insert(0, str(_ROOT))

import numpy as np

from pipeline.depthfusion_worker import apply_alignment, fit_alignment, read_dmap
from pipeline.experiments.planes import geom


def load_frames(dense: Path) -> list[dict]:
    import pycolmap

    rec = pycolmap.Reconstruction(dense / "sparse")
    xyz = {pid: p.xyz for pid, p in rec.points3D.items()}
    frames = []
    for im in sorted(rec.images.values(), key=lambda im: im.name):
        cam = rec.cameras[im.camera_id]
        T = im.cam_from_world().matrix()
        obs = [(p.xy, xyz[p.point3D_id]) for p in im.points2D if p.has_point3D()]
        uv = np.array([o[0] for o in obs])
        Xc = np.array([o[1] for o in obs]) @ T[:, :3].T + T[:, 3]
        frames.append(
            {
                "name": im.name,
                "K": cam.calibration_matrix(),
                "R": T[:, :3],
                "t": T[:, 3],
                "wh": (cam.width, cam.height),
                "uv": uv,
                "z": Xc[:, 2],
            }
        )
    return frames


def lift_frame(f: dict, c: dict, zm: np.ndarray | None, upm: float, a) -> tuple[list[dict], dict]:
    """Per-frame planes from one frame's PxwPlanar cache `c` (see pxw_infer.py)."""
    import cv2

    lab = c["labels"]
    h, w = lab.shape
    W0 = f["wh"][0]
    s = w / W0
    K = f["K"].copy()
    K[:2, :2] *= s
    K[:2, 2] = (K[:2, 2] + 0.5) * s - 0.5
    mono = c["depth"].astype(np.float32)
    # MoGe depth -> COLMAP units: E3's per-frame scale plane + shift, fitted to the SfM points
    px = np.clip(np.round((f["uv"] + 0.5) * s - 0.5).astype(int), 0, [w - 1, h - 1])
    dp = mono[px[:, 1], px[:, 0]]
    ok = (dp > 0) & (f["z"] > 0)
    uvn = np.stack([2 * px[ok, 0] / (w - 1) - 1, 2 * px[ok, 1] / (h - 1) - 1], 1)
    params, fit = fit_alignment(dp[ok], f["z"][ok], uvn, "plane", False)
    rel = np.abs(fit - f["z"][ok]) / f["z"][ok]
    info = {"name": f["name"], "n_sfm": int(ok.sum()), "align_med_rel": float(np.median(rel))}
    if ok.sum() < 30 or info["align_med_rel"] > 0.05:
        info["skipped"] = "alignment"
        return [], info
    za = apply_alignment(mono, params, "plane", False, w, h)
    za[mono <= 0] = 0
    if zm is not None:
        zm = cv2.resize(zm, (w, h), interpolation=cv2.INTER_NEAREST)
        cons = (zm > 0) & (za > 0) & (np.abs(zm - za) < a.mvs_agree * za)
    else:
        cons = np.zeros_like(za, bool)
    v, u = np.mgrid[0:h, 0:w].astype(np.float32)
    rays = np.stack([(u - K[0, 2]) / K[0, 0], (v - K[1, 2]) / K[1, 1], np.ones_like(u)], -1)
    R, t = f["R"], f["t"]
    C = -R.T @ t
    rays_w = rays @ R  # = (R^T r) per pixel
    to_w = lambda z, m: (rays[m] * z[m][:, None] - t) @ R
    k3 = np.ones((3, 3), np.uint8)
    out = []
    for L in range(1, int(lab.max()) + 1):
        m = lab == L
        if m.sum() < a.min_px:
            continue
        mi = cv2.erode(m.astype(np.uint8), k3, iterations=2).astype(bool)
        mv = mi & cons
        src = "mvs" if mv.sum() >= a.min_mvs_px else "mono"
        P = to_w(zm, mv) if src == "mvs" else to_w(za, mi & (za > 0))
        if len(P) < 30:
            continue
        rng = np.random.default_rng(L)
        if len(P) > 4000:
            P = P[rng.choice(len(P), 4000, replace=False)]
        tol = (a.tol_mvs if src == "mvs" else a.tol_mono) * upm
        n, d, rms, inl = geom.ransac_plane(P, tol, iters=100, seed=L)
        if inl.mean() < 0.6:
            continue
        cen = P[inl].mean(0)
        view = C - cen
        if n @ view < 0:
            n, d = -n, -d
        if n @ view / np.linalg.norm(view) < np.cos(np.radians(a.max_grazing_deg)):
            continue
        # support: every mask pixel ray-cast onto the plane, kept where it agrees with mono depth
        ms = m.copy()
        ms[:, 1::2] = False
        ms[1::2, :] = False
        rw = rays_w[ms]
        den = rw @ n
        lam = -(n @ C + d) / np.where(np.abs(den) > 1e-9, den, np.nan)  # = camera z (ray z = 1)
        keep = np.isfinite(lam) & (lam > 0) & (np.abs(lam - za[ms]) < a.support_agree * za[ms])
        if keep.sum() < 20:
            continue
        S = C + lam[keep, None] * rw[keep]
        fitp = P[inl]
        if len(fitp) > 800:
            fitp = fitp[rng.choice(len(fitp), 800, replace=False)]
        out.append(
            {
                "label": L,
                "n": n,
                "d": float(d),
                "rms": float(rms),
                "src": src,
                "fit": fitp.astype(np.float32),
                "support": S.astype(np.float32),
            }
        )
    info["planes"] = len(out)
    return out, info


def units_per_m(frames: list[dict], caches: dict) -> float:
    """COLMAP units per metre: median over frames of the scale-only SfM / MoGe depth log-ratio."""
    lr = []
    for f in frames:
        c = np.load(caches[f["name"]])
        dep = c["depth"].astype(np.float32)
        s = dep.shape[1] / f["wh"][0]
        px = np.clip(
            np.round((f["uv"] + 0.5) * s - 0.5).astype(int), 0, [dep.shape[1] - 1, dep.shape[0] - 1]
        )
        dp = dep[px[:, 1], px[:, 0]]
        ok = (dp > 0) & (f["z"] > 0)
        if ok.sum() >= 30:
            lr.append(np.median(np.log(f["z"][ok] / dp[ok])))
    return float(np.exp(np.median(lr)))


def finish(planes: list[dict], upm: float, a, method: str, stats: dict) -> dict:
    """Plane-pair edges and three-plane corners for `planes` (dicts with n, d, polygon, area,
    cells, rms, inl, src, frames, cluster, snapped; COLMAP units), as an airtools.structure/1 doc."""
    planes.sort(key=lambda p: -p["area"])
    t_int = time.time()
    near, lo_hi = a.near * upm, [(p["cells"].min(0), p["cells"].max(0)) for p in planes]
    edges, adj = [], {}
    for i in range(len(planes)):
        for j in range(i + 1, len(planes)):
            if np.any(lo_hi[i][0] > lo_hi[j][1] + near) or np.any(lo_hi[j][0] > lo_hi[i][1] + near):
                continue
            pa, pb = planes[i], planes[j]
            if min(pa["area"], pb["area"]) < a.edge_min_area * upm**2:
                continue
            seg = geom.plane_edge(
                pa["n"],
                pa["d"],
                pa["cells"],
                pb["n"],
                pb["d"],
                pb["cells"],
                near,
                a.min_edge * upm,
                pct=a.edge_pct,
                extend=a.edge_extend * upm,
            )
            if seg is None:
                continue
            ang = float(np.degrees(np.arccos(np.clip(pa["n"] @ pb["n"], -1, 1))))
            adj.setdefault(i, {})[j] = adj.setdefault(j, {})[i] = len(edges)
            edges.append({"a": seg[0], "b": seg[1], "planes": (i, j), "dihedral": 180 - ang})
    corners, seen = [], set()
    ext = a.corner_ext * upm
    for c, nbr in adj.items():  # any triple with >= 2 of its 3 edges has a centre plane c
        for j in nbr:
            for k in nbr:
                tri = tuple(sorted((c, j, k)))
                if j >= k or tri in seen:
                    continue
                seen.add(tri)
                i, j2, k2 = tri
                es = [e for e in (adj[i].get(j2), adj[j2].get(k2), adj[i].get(k2)) if e is not None]
                ns = np.stack([planes[x]["n"] for x in tri])
                p = geom.three_plane_point(ns, np.array([planes[x]["d"] for x in tri]))
                if p is None:
                    continue
                # edges end where their supports stop, often a few cm short of the true corner
                if sum(geom.seg_dist(p, edges[e]["a"], edges[e]["b"]) < ext for e in es) >= 2:
                    corners.append({"p": p, "planes": tri, "edges": es})
    for c in corners:  # snap the near endpoint of each corner's edges onto the corner
        for e in c["edges"]:
            E = edges[e]
            end = "a" if np.linalg.norm(E["a"] - c["p"]) < np.linalg.norm(E["b"] - c["p"]) else "b"
            if np.linalg.norm(E[end] - c["p"]) < ext:
                E[end] = c["p"]
    t_int = time.time() - t_int

    L = lambda x: [round(float(v), 6) for v in x]
    doc = {
        "schema": "airtools.structure/1",
        "frame": "cameras",
        "cameras": str(Path(a.dense).resolve() / "sparse"),
        "method": method,
        "params": {k: v for k, v in vars(a).items() if k not in ("out",)} | {"units_per_m": upm},
        "cost": {"runtime_s": None, "host": "hfbox", "cores": 4},
        "planes": [
            {
                "id": f"p{i}",
                "normal": L(p["n"]),
                "offset": round(float(p["d"]), 6),
                "polygon": [L(q) for q in p["polygon"]],
                "area_m2": round(p["area"] / upm**2, 4),
                "inliers": p["inl"],
                "rms_m": round(p["rms"] / upm, 5),
                "label": None,
                "source": p["src"],
                "frames": p["frames"],
                "cluster": int(p["cluster"]),
                "manhattan": bool(p["snapped"]),
            }
            for i, p in enumerate(planes)
        ],
        "edges": [
            {
                "id": f"e{i}",
                "a": L(e["a"]),
                "b": L(e["b"]),
                "planes": [f"p{e['planes'][0]}", f"p{e['planes'][1]}"],
                "dihedral_deg": round(e["dihedral"], 2),
            }
            for i, e in enumerate(edges)
        ],
        "corners": [
            {
                "id": f"c{i}",
                "p": L(c["p"]),
                "planes": [f"p{x}" for x in c["planes"]],
                "edges": [f"e{x}" for x in c["edges"]],
            }
            for i, c in enumerate(corners)
        ],
        "stats": stats | {"intersect_s": t_int},
    }
    return doc


def build(a) -> dict:
    t_all = time.time()
    dense, cache = Path(a.dense), Path(a.cache)
    frames = load_frames(dense)
    dmaps = {}
    if a.mvs:
        for p in sorted(Path(a.mvs).glob("depth*.dmap")):
            nm = read_dmap(p)[0]
            dmaps[nm] = p
    caches = {f["name"]: cache / f"{Path(f['name']).stem}.npz" for f in frames}
    frames = [f for f in frames if caches[f["name"]].exists()]
    upm = units_per_m(frames, caches)
    if not a.tol_scale:
        a.tol_scale = tol_scale(frames, upm)
    a = scale_args(a)
    t_lift = time.time()
    per, infos = [], []
    for fi, f in enumerate(frames):
        c = np.load(caches[f["name"]])
        zm = read_dmap(dmaps[f["name"]])[1] if f["name"] in dmaps else None
        planes, info = lift_frame(f, c, zm, upm, a)
        for p in planes:
            p["frame"] = fi
        per += planes
        infos.append(info)
    t_lift = time.time() - t_lift

    # fuse across frames
    t_fuse = time.time()
    N = np.array([p["n"] for p in per])
    D = np.array([p["d"] for p in per])
    sub = []
    for i, p in enumerate(per):
        S = p["support"]
        sub.append(S[np.random.default_rng(i).choice(len(S), min(len(S), 400), replace=False)])
    cl = geom.fuse_planes(N, D, sub, a.fuse_deg, a.fuse_off * upm, a.fuse_voxel * upm)
    clusters = []
    for k in range(cl.max() + 1):
        mem = [per[i] for i in np.flatnonzero(cl == k)]
        fr = {p["frame"] for p in mem}
        if len(fr) < a.min_frames:
            continue
        mv = [p["fit"] for p in mem if p["src"] == "mvs"]
        src = "mvs" if sum(len(x) for x in mv) >= 300 else "mono"
        P = np.concatenate(mv if src == "mvs" else [p["fit"] for p in mem]).astype(np.float64)
        if len(P) > 20000:
            P = P[np.random.default_rng(k).choice(len(P), 20000, replace=False)]
        tol = (a.tol_mvs if src == "mvs" else a.tol_mono) * upm
        n, d, rms, inl = geom.ransac_plane(P, tol, iters=200, seed=k)
        if n @ np.mean([p["n"] for p in mem], 0) < 0:
            n, d = -n, -d
        clusters.append(
            {
                "n": n,
                "d": d,
                "rms": rms,
                "inl": int(inl.sum()),
                "src": src,
                "mem": mem,
                "frames": len(fr),
                "P": P[inl],
                "k": k,
            }
        )
    if a.manhattan and clusters:
        w = np.array([float(len(c["mem"])) for c in clusters])
        up = scene_up(Path(a.dense), Path(a.up_from)) if a.up_from else None
        Ns, snap = geom.manhattan_snap(np.array([c["n"] for c in clusters]), w, a.manhattan, up)
        for c, n2, sn in zip(clusters, Ns, snap):
            if sn:
                c["n"] = n2
                c["d"] = -float(np.median(c["P"] @ n2))
                c["rms"] = float(np.sqrt(np.mean((c["P"] @ n2 + c["d"]) ** 2)))
                c["snapped"] = True

    # support components -> output planes
    planes = []
    for c in clusters:
        S = np.concatenate([p["support"] for p in c["mem"]]).astype(np.float64)
        votes = np.concatenate([np.full(len(p["support"]), p["frame"]) for p in c["mem"]])
        dist = S @ c["n"] + c["d"]
        near = np.abs(dist) < a.fuse_off * upm
        S = S[near] - np.outer(dist[near], c["n"])
        comps = geom.support_components(
            c["n"],
            c["d"],
            S,
            votes[near],
            a.cell * upm,
            min_votes=2 if c["frames"] >= 3 else 1,
            min_area=a.min_area * upm**2,
            simplify=a.simplify * upm,
        )
        for comp in comps:
            planes.append(
                {
                    **comp,
                    "n": c["n"],
                    "d": c["d"],
                    "rms": c["rms"],
                    "inl": c["inl"],
                    "src": c["src"],
                    "frames": c["frames"],
                    "cluster": c["k"],
                    "snapped": c.get("snapped", False),
                }
            )
    planes.sort(key=lambda p: -p["area"])
    t_fuse = time.time() - t_fuse

    stats = {
        "frames": len(frames),
        "frames_skipped": sum("skipped" in x for x in infos),
        "frame_planes": len(per),
        "frame_planes_mvs": sum(p["src"] == "mvs" for p in per),
        "clusters": len(clusters),
        "timings_s": {"lift": t_lift, "fuse": t_fuse, "total_cpu": time.time() - t_all},
    }
    doc = finish(
        planes, upm, a, "pxwplanar+mvs-lift" + ("+manhattan" if a.manhattan else ""), stats
    )
    # (frame, label) -> output plane cluster, for the mask figures
    doc["_frame_clusters"] = {
        f"{frames[p['frame']]['name']}:{p['label']}": int(cl[i]) for i, p in enumerate(per)
    }
    return doc


def add_finish_args(p: argparse.ArgumentParser) -> None:
    """Polygon, edge and corner options shared with psplat_run.py (metric)."""
    p.add_argument("--cell", type=float, default=0.02, help="m")
    p.add_argument("--min-area", type=float, default=0.02, help="m^2")
    p.add_argument("--simplify", type=float, default=0.01, help="m")
    p.add_argument("--near", type=float, default=0.03, help="m")
    p.add_argument("--min-edge", type=float, default=0.05, help="m")
    p.add_argument("--edge-min-area", type=float, default=0.05, help="m^2, both planes")
    p.add_argument("--edge-pct", type=float, default=2, help="edge extent percentile")
    p.add_argument("--edge-extend", type=float, default=0.0, help="m, grow each edge end")
    p.add_argument("--corner-ext", type=float, default=0.10, help="m past an edge's end")


GSD_REF_M = 0.002  # = pipeline.structure.GSD_REF_M (that module needs the pipeline env)


def tol_scale(frames: list[dict], upm: float) -> float:
    """max(1, ground sample distance / GSD_REF_M), the GSD from the frames' median SfM depth over
    the focal length. Above 1 the tolerances come out in pixels at the scene's depth, whatever
    MoGe's metric says (it is 3.6x off on a 110 m hospital; indoors it is the better scale)."""
    gsd = np.median([np.median(f["z"]) / f["K"][0, 0] for f in frames]) / upm
    return max(1.0, float(gsd) / GSD_REF_M)


LENGTH_ARGS = ("tol_mvs", "tol_mono", "fuse_off", "fuse_voxel", "cell", "simplify", "near",
               "min_edge", "edge_extend", "corner_ext")  # fmt: skip
AREA_ARGS = ("min_area", "edge_min_area")


def scale_args(a: argparse.Namespace) -> argparse.Namespace:
    """Multiply the metric options by `a.tol_scale` (areas by its square): the defaults are tuned
    on a kitchen at ~1 mm per pixel (pipeline.structure.tol_scale)."""
    for k in LENGTH_ARGS:
        setattr(a, k, getattr(a, k) * a.tol_scale)
    for k in AREA_ARGS:
        setattr(a, k, getattr(a, k) * a.tol_scale**2)
    return a


def scene_sim3(dense: Path, cameras_json: Path) -> tuple[float, np.ndarray, np.ndarray]:
    """Sim(3) with x_colmap = s R x_scene + t, from the camera centres of a pipeline
    `cameras.r*.json` (scene frame) and the COLMAP model in `dense/sparse` (Umeyama)."""
    import pycolmap

    cams = {c["id"]: c["position"] for c in json.loads(cameras_json.read_text())}
    rec = pycolmap.Reconstruction(dense / "sparse")
    pairs = [(cams[Path(im.name).stem], im.projection_center()) for im in rec.images.values()
             if Path(im.name).stem in cams]  # fmt: skip
    A, B = (np.array(x, float) for x in zip(*pairs))
    a, b = A - A.mean(0), B - B.mean(0)
    U, S, Vt = np.linalg.svd(b.T @ a)
    D = np.diag([1, 1, np.sign(np.linalg.det(U @ Vt))])
    R = U @ D @ Vt
    s = float((S * np.diag(D)).sum() / (a**2).sum())
    return s, R, B.mean(0) - s * R @ A.mean(0)


def scene_up(dense: Path, cameras_json: Path) -> np.ndarray:
    """The scene's gravity (+Y of `cameras.r*.json`, set by the pipeline's own up-axis fit) in the
    COLMAP frame."""
    return scene_sim3(dense, cameras_json)[1][:, 1]


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--dense", required=True)
    p.add_argument("--mvs", default=None, help="OpenMVS dir with depth*.dmap (optional)")
    p.add_argument("--cache", required=True, help="pxw_infer.py output dir")
    p.add_argument("--out", required=True)
    p.add_argument("--min-px", type=int, default=400, help="min segment size at cache res")
    p.add_argument("--min-mvs-px", type=int, default=150)
    p.add_argument("--mvs-agree", type=float, default=0.05, help="MVS vs aligned mono, relative")
    p.add_argument("--support-agree", type=float, default=0.10)
    p.add_argument("--tol-mvs", type=float, default=0.01, help="m")
    p.add_argument("--tol-mono", type=float, default=0.02, help="m")
    p.add_argument("--max-grazing-deg", type=float, default=80)
    p.add_argument("--fuse-deg", type=float, default=8)
    p.add_argument("--fuse-off", type=float, default=0.02, help="m")
    p.add_argument("--fuse-voxel", type=float, default=0.05, help="m")
    p.add_argument("--min-frames", type=int, default=2)
    add_finish_args(p)
    p.add_argument("--manhattan", type=float, default=0, help="snap tolerance deg (0 = off)")
    p.add_argument("--up-from", default=None, help="cameras.r*.json: gravity axis for Manhattan")
    p.add_argument("--tol-scale", type=float, default=0, help="x metric tolerances; 0: auto")
    a = p.parse_args()
    t0 = time.time()
    doc = build(a)
    doc["cost"]["runtime_s"] = round(time.time() - t0, 1)
    fc = doc.pop("_frame_clusters")
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(doc))
    out.with_suffix(".frames.json").write_text(json.dumps(fc))
    print(json.dumps({k: len(doc[k]) for k in ("planes", "edges", "corners")} | doc["stats"]))


if __name__ == "__main__":
    main()
