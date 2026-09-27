"""Mesh-independent ground truth for the structure bench (docs/research/p2-structure-bench.md §2).

Kitchen corners are picked coarsely in one anchor frame (`picks.json`), refined to sub-pixel at
the intersection of two LSD line families, KLT-tracked through the 10 fps frame grid and
re-refined in every SfM frame they reach, then triangulated with the COLMAP poses. No mesh or
dense cloud is touched anywhere, so the GT can judge any of them.

Frames are the pipeline's own `frames/%04d.jpg` (fps 10, 1920 long edge, SfM uses every 5th:
ids 0001, 0006, ...). Observations are stored in those raw (distorted) pixels; `build` maps them
through the SfM camera's distortion onto the undistorted pinhole of the scene's `cameras.json`.
"""

import json
from pathlib import Path

import numpy as np

# --- 2D: sub-pixel corner at the intersection of LSD lines -----------------------------------


def _lsd(gray: np.ndarray) -> np.ndarray:
    """(N, 4) LSD segments x0 y0 x1 y1 (empty array when none)."""
    import cv2

    segs = cv2.createLineSegmentDetector().detect(gray)[0]
    return np.zeros((0, 4)) if segs is None else segs.reshape(-1, 4).astype(np.float64)


def _fit_line(segs: np.ndarray) -> tuple[np.ndarray, float]:
    """Total-least-squares line through the segments' endpoints, weighted by segment length.
    Returns (unit normal n, c) with n.x + c = 0."""
    pts = np.vstack([segs[:, :2], segs[:, 2:]])
    w = np.tile(np.hypot(segs[:, 2] - segs[:, 0], segs[:, 3] - segs[:, 1]), 2)
    mu = (pts * w[:, None]).sum(0) / w.sum()
    d = pts - mu
    _, vecs = np.linalg.eigh((d * w[:, None]).T @ d)
    n = vecs[:, 0]
    return n, -float(n @ mu)


def refine_corner(
    gray: np.ndarray,
    p: np.ndarray,
    radius: int = 40,
    near_px: float = 4.0,
    min_len: float = 10.0,
    max_move: float = 8.0,
    min_angle_deg: float = 25.0,
) -> tuple[np.ndarray | None, dict]:
    """Sub-pixel corner near `p`: the intersection of the two strongest line families (by total
    segment length) among LSD segments whose supporting line passes within `near_px` of `p`.
    Within a family only the offset cluster nearest `p` is kept (a door seam gives two parallel
    lines 2-4 px apart). Returns (point or None, info)."""
    h, w = gray.shape
    x0, y0 = int(max(0, p[0] - radius)), int(max(0, p[1] - radius))
    x1, y1 = int(min(w, p[0] + radius + 1)), int(min(h, p[1] + radius + 1))
    segs = _lsd(np.ascontiguousarray(gray[y0:y1, x0:x1]))
    if len(segs) < 2:
        return None, {"why": "few segments"}
    segs = segs + [x0, y0, x0, y0]
    d = segs[:, 2:] - segs[:, :2]
    length = np.hypot(d[:, 0], d[:, 1])
    ok = length >= min_len
    segs, d, length = segs[ok], d[ok], length[ok]
    if len(segs) < 2:
        return None, {"why": "short segments"}
    n = np.stack([-d[:, 1], d[:, 0]], 1) / length[:, None]
    off = np.einsum("ij,ij->i", n, segs[:, :2] - p)  # signed distance of p's line to each seg
    near = np.abs(off) <= near_px
    segs, length, off, ang = (
        segs[near],
        length[near],
        off[near],
        np.degrees(np.arctan2(d[near, 1], d[near, 0])) % 180,
    )
    if len(segs) < 2:
        return None, {"why": "no lines through p"}

    def adiff(a, b):
        x = np.abs(a - b) % 180
        return np.minimum(x, 180 - x)

    families = []
    used = np.zeros(len(segs), bool)
    for _ in range(2):
        best, best_len = None, 0.0
        for i in np.flatnonzero(~used):
            m = (~used) & (adiff(ang, ang[i]) < 6)
            if families and adiff(ang[i], families[0][1]) < min_angle_deg:
                continue
            if length[m].sum() > best_len:
                best, best_len = i, length[m].sum()
        if best is None:
            return None, {"why": "one line family"}
        fam = (~used) & (adiff(ang, ang[best]) < 6)
        # nearest-offset cluster (within 1.5 px of the member closest to p)
        o = off * np.sign(np.cos(np.radians(ang - ang[best])))  # consistent sign in the family
        ref = o[fam][np.argmin(np.abs(o[fam]))]
        fam &= np.abs(o - ref) <= 1.5
        used |= fam
        families.append((fam, ang[best], length[fam].sum()))
    (n1, c1), (n2, c2) = _fit_line(segs[families[0][0]]), _fit_line(segs[families[1][0]])
    q = np.linalg.solve(np.array([n1, n2]), -np.array([c1, c2]))
    info = {
        "angles_deg": [float(families[0][1]), float(families[1][1])],
        "support_px": [float(families[0][2]), float(families[1][2])],
    }
    if np.linalg.norm(q - p) > max_move:
        return None, {**info, "why": f"moved {np.linalg.norm(q - p):.1f}px"}
    return q, info


# --- tracking through the 10 fps grid --------------------------------------------------------


def _gray(frames_dir: Path, fid: int, cache: dict) -> np.ndarray:
    import cv2

    if fid not in cache:
        if len(cache) > 64:
            cache.pop(next(iter(cache)))
        cache[fid] = cv2.imread(str(frames_dir / f"{fid:04d}.jpg"), cv2.IMREAD_GRAYSCALE)
    return cache[fid]


def track_point(
    frames_dir: Path,
    anchor: int,
    p: np.ndarray,
    span: int = 60,
    sfm_every: int = 5,
    fb_max: float = 0.7,
    cache: dict | None = None,
) -> dict[int, dict]:
    """KLT-track `p` (already refined in `anchor`) forward and backward up to `span` 10 fps
    frames, with a forward-backward check per step; at every SfM frame (id % sfm_every == 1)
    re-refine with `refine_corner` and keep it as an observation (re-seeding the tracker there,
    so KLT drift never accumulates past one SfM interval). Stops a direction when tracking or
    refinement fails twice in a row, or the point leaves the frame."""
    import cv2

    cache = {} if cache is None else cache
    lk = {"winSize": (31, 31), "maxLevel": 3}
    obs: dict[int, dict] = {}
    if anchor % sfm_every == 1:
        obs[anchor] = {"xy": p.tolist(), "src": "anchor"}
    n = len(list(frames_dir.glob("*.jpg")))
    for step in (1, -1):
        cur, q, misses = anchor, p.astype(np.float32).reshape(1, 1, 2), 0
        while abs(cur - anchor) < span:
            nxt = cur + step
            if not 1 <= nxt <= n:
                break
            g0, g1 = _gray(frames_dir, cur, cache), _gray(frames_dir, nxt, cache)
            q1, st, _ = cv2.calcOpticalFlowPyrLK(g0, g1, q, None, **lk)
            qb, stb, _ = cv2.calcOpticalFlowPyrLK(g1, g0, q1, None, **lk)
            if not (st[0, 0] and stb[0, 0]) or np.linalg.norm(qb - q) > fb_max:
                break
            h, w = g1.shape
            x, y = q1[0, 0]
            if not (20 <= x < w - 20 and 20 <= y < h - 20):
                break
            cur, q = nxt, q1
            if cur % sfm_every == 1:
                r, _ = refine_corner(g1, q[0, 0].astype(np.float64))
                if r is None:
                    misses += 1
                    if misses >= 2:
                        break
                    continue
                misses = 0
                obs[cur] = {"xy": r.tolist(), "src": "track"}
                q = r.astype(np.float32).reshape(1, 1, 2)
    return dict(sorted(obs.items()))


# --- 3D: undistort + triangulate + residuals ---------------------------------------------------


def undistort_simple_radial(xy: np.ndarray, f: float, cx: float, cy: float, k: float) -> np.ndarray:
    """COLMAP SIMPLE_RADIAL pixel -> normalised undistorted (x, y): invert x_d = x_u(1 + k r_u^2)
    by fixed-point iteration (|k r^2| << 1 for this lens)."""
    xd = (np.asarray(xy, np.float64) - [cx, cy]) / f
    xu = xd.copy()
    for _ in range(20):
        xu = xd / (1 + k * (xu**2).sum(-1, keepdims=True))
    return xu


def triangulate(P: np.ndarray, uv: np.ndarray) -> np.ndarray:
    """N-view DLT then Gauss-Newton on reprojection error. P (N,3,4), uv (N,2) pinhole px."""
    A = np.concatenate([uv[:, :1] * P[:, 2] - P[:, 0], uv[:, 1:] * P[:, 2] - P[:, 1]])
    X = np.linalg.svd(A)[2][-1]
    X = X[:3] / X[3]
    for _ in range(10):
        x = P @ np.append(X, 1)  # (N,3)
        r = (x[:, :2] / x[:, 2:] - uv).ravel()
        J = (P[:, :2, :3] * x[:, 2, None, None] - x[:, :2, None] * P[:, 2, None, :3]) / (
            x[:, 2, None, None] ** 2
        )
        dX = np.linalg.lstsq(J.reshape(-1, 3), -r, rcond=None)[0]
        X = X + dX
        if np.linalg.norm(dX) < 1e-10:
            break
    return X


def reproj(P: np.ndarray, uv: np.ndarray, X: np.ndarray) -> np.ndarray:
    x = P @ np.append(X, 1)
    return np.linalg.norm(x[:, :2] / x[:, 2:] - uv, axis=1)


def robust_triangulate(
    P: np.ndarray, uv: np.ndarray, max_px: float = 1.5, min_views: int = 3, iters: int = 400
) -> tuple[np.ndarray | None, np.ndarray]:
    """RANSAC over view pairs (so a track that jumped to a neighbouring corner can't win on
    count alone), Gauss-Newton on the consensus set, re-count once. Returns (X or None, inliers)."""
    n = len(uv)
    if n < min_views:
        return None, np.zeros(n, bool)
    rng = np.random.default_rng(0)
    pairs = rng.integers(0, n, size=(iters, 2))
    best, best_keep = -1, None
    for i, j in pairs:
        if i == j:
            continue
        X = triangulate(P[[i, j]], uv[[i, j]])
        x = P @ np.append(X, 1)
        if (x[:, 2] <= 0).any():
            continue
        keep = reproj(P, uv, X) <= max_px
        if keep.sum() > best:
            best, best_keep = int(keep.sum()), keep
    if best < min_views:
        return None, np.zeros(n, bool)
    keep = best_keep
    for _ in range(2):
        X = triangulate(P[keep], uv[keep])
        keep = reproj(P, uv, X) <= max_px
    if keep.sum() < min_views:
        return None, keep
    return triangulate(P[keep], uv[keep]), keep


def triangulation_angle_deg(centres: np.ndarray, X: np.ndarray) -> float:
    """Largest angle at X between any two camera rays (baseline quality)."""
    v = centres - X
    v /= np.linalg.norm(v, axis=1, keepdims=True)
    return float(np.degrees(np.arccos(np.clip((v @ v.T).min(), -1, 1))))


def point_precision_m(P: np.ndarray, uv: np.ndarray, X: np.ndarray, sigma_px: float) -> float:
    """1-sigma 3D position uncertainty (sqrt of the largest covariance eigenvalue) of X for an
    isotropic `sigma_px` image noise, from the reprojection Jacobian."""
    x = P @ np.append(X, 1)
    J = (P[:, :2, :3] * x[:, 2, None, None] - x[:, :2, None] * P[:, 2, None, :3]) / (
        x[:, 2, None, None] ** 2
    )
    J = J.reshape(-1, 3)
    cov = sigma_px**2 * np.linalg.inv(J.T @ J)
    return float(np.sqrt(np.linalg.eigvalsh(cov)[-1]))


def _split_half(P: np.ndarray, uv: np.ndarray) -> float | None:
    """Distance between the points triangulated from even and odd views (a precision check)."""
    if len(uv) < 6:
        return None
    return float(np.linalg.norm(triangulate(P[0::2], uv[0::2]) - triangulate(P[1::2], uv[1::2])))


def load_json(path: Path) -> dict:
    return json.loads(Path(path).read_text())


# --- CLI steps ----------------------------------------------------------------------------------


def observe(picks_path: Path, frames_dir: Path, out: Path, span: int = 150) -> dict:
    """picks.json (coarse anchors) -> observations.json: refined anchor + tracked SfM-frame
    observations per point (OpenCV pixel convention of frames/%04d.jpg). Anchors of one point are
    merged (later anchors fill frames the earlier ones did not reach)."""
    picks = load_json(picks_path)
    cache: dict = {}
    out_pts = {}
    for name, spec in picks["points"].items():
        obs: dict[int, dict] = {}
        notes = []
        for fid, x, y in spec["anchors"]:
            g = _gray(frames_dir, fid, cache)
            q, info = refine_corner(g, np.array([x, y], float))
            if q is None:
                notes.append(f"anchor {fid}: {info.get('why')}")
                continue
            for k, v in track_point(frames_dir, fid, q, span=span, cache=cache).items():
                obs.setdefault(k, v)
        out_pts[name] = {
            "desc": spec.get("desc", ""),
            "obs": {f"{k:04d}": v["xy"] for k, v in sorted(obs.items())},
            "notes": notes,
        }
        print(f"{name:10s} {len(obs):3d} obs {notes if notes else ''}", flush=True)
    res = {"frames_dir": str(frames_dir), "pixel_convention": "opencv", "points": out_pts}
    out.write_text(json.dumps(res, indent=1))
    return res


def _pinhole_mapper(sparse_raw: Path, cams: dict):
    """OpenCV px in the raw SfM frames -> px on the scene cameras' undistorted pinhole."""
    import pycolmap

    rec = pycolmap.Reconstruction(str(sparse_raw))
    (cam,) = [c for c in rec.cameras.values()]
    f, cx, cy, k = (float(v) for v in cam.params)
    c0 = next(iter(cams.values()))

    def to_pinhole(xy: np.ndarray) -> np.ndarray:
        xn = undistort_simple_radial(np.asarray(xy) + 0.5, f, cx, cy, k)  # COLMAP px centres
        return xn * [c0.fx, c0.fy] + [c0.cx, c0.cy]

    def to_raw(uv: np.ndarray) -> np.ndarray:
        xn = (np.asarray(uv) - [c0.cx, c0.cy]) / [c0.fx, c0.fy]
        return xn * (1 + k * (xn**2).sum(-1, keepdims=True)) * f + [cx, cy] - 0.5

    return to_pinhole, to_raw, {"model": str(cam.model), "params": [f, cx, cy, k]}


def build(
    picks_path: Path,
    obs_path: Path,
    scene_dir: Path,
    sparse_raw: Path,
    out: Path,
    max_px: float = 1.5,
    id_fps: float = 10.0,
    densify: bool = True,
    max_sigma_m: float = 0.005,
    scene_label: str | None = None,
) -> dict:
    """Triangulate every point with the scene's cameras (metric scene frame, same as mesh.glb)
    and write gt.json with per-point residuals, pairs, groups, planes and edges from picks.json."""
    from pipeline.qa import _load_cameras, _resolve_package_files

    picks, obs = load_json(picks_path), load_json(obs_path)
    cams = _load_cameras(scene_dir)
    to_pinhole, to_raw, raw_cam = _pinhole_mapper(sparse_raw, cams)
    frames_dir, cache = Path(obs["frames_dir"]), {}
    Pof = {
        f: np.array([[c.fx, 0, c.cx], [0, c.fy, c.cy], [0, 0, 1.0]])
        @ np.hstack([c.R, c.t[:, None]])
        for f, c in cams.items()
    }
    points, dropped = {}, {}
    for name, rec in obs["points"].items():
        if name not in picks["points"]:
            continue
        raw = {f: np.array(xy) for f, xy in rec["obs"].items() if f in cams}
        X = None
        for rnd in range(2 if densify else 1):
            if rnd == 1:  # re-find the point in every SfM frame it projects into (tight gate)
                for f, c in cams.items():
                    if f in raw:
                        continue
                    x = Pof[f] @ np.append(X, 1)
                    if x[2] <= 0:
                        continue
                    q0 = to_raw(x[:2] / x[2])
                    if not (30 <= q0[0] < c.w - 30 and 30 <= q0[1] < c.h - 30):
                        continue
                    q, _ = refine_corner(_gray(frames_dir, int(f), cache), q0, max_move=3.0)
                    if q is not None:
                        raw[f] = q
            fids = sorted(raw)
            if len(fids) < 3:
                break
            P = np.array([Pof[f] for f in fids])
            uv = to_pinhole(np.array([raw[f] for f in fids]))
            X, keep = robust_triangulate(P, uv, max_px=max_px)
            if X is None:
                break
        if X is None:
            dropped[name] = f"no consistent >=3-view subset under {max_px}px ({len(raw)} views)"
            continue
        r = reproj(P[keep], uv[keep], X)
        C = np.array([cams[f].position for f, k in zip(fids, keep) if k])
        sigma = point_precision_m(P[keep], uv[keep], X, 0.5)
        if sigma > max_sigma_m:
            dropped[name] = f"1-sigma {sigma * 1000:.1f} mm at 0.5 px > {max_sigma_m * 1000:.0f} mm"
            continue
        spec = picks["points"].get(name, {})
        points[name] = {
            "xyz": X.tolist(),
            "desc": rec["desc"],
            "kind": spec.get("kind", "corner"),
            "n_obs": int(keep.sum()),
            "n_rejected": int((~keep).sum()),
            "frames": [f for f, k in zip(fids, keep) if k],
            "uv_pinhole": uv[keep].round(3).tolist(),
            "reproj_rms_px": float(np.sqrt(np.mean(r**2))),
            "reproj_max_px": float(r.max()),
            "tri_angle_deg": triangulation_angle_deg(C, X),
            "sigma_m_at_0.5px": sigma,
            "split_half_m": _split_half(P[keep], uv[keep]),
            "grade": "A" if sigma < 0.001 and keep.sum() >= 10 else "B",
        }
        if "grade" in spec:  # manual downgrade after cross-checks (picks.json "grade_reason")
            points[name]["grade"] = spec["grade"]
            points[name]["grade_reason"] = spec.get("grade_reason", "")
    scene = load_json(scene_dir / "scene.json") if (scene_dir / "scene.json").exists() else {}
    ids = sorted(cams)
    gt = {
        "schema": "airtools.structure-gt/1",
        "frame": {
            "scene": scene_label or str(scene_dir),
            "frame_id": scene.get("frame", {}).get("id"),
            "cameras": _resolve_package_files(scene_dir)[1],
            "units": "m (scene calibration)",
            "scale_method": scene.get("scale_method"),
            "scale_candidates": scene.get("scale_candidates"),
            "scale_note": "absolute scale is uncertain (caption altitude ~31% small indoors per "
            "p1-recon-bench §5.2; MoGe-2/object cues differ ~6%); prefer ratios and angles.",
        },
        "track": {
            "id_fps": id_fps,
            "ids": ids,
            "positions": [cams[i].position.tolist() for i in ids],
            "R": [cams[i].R.tolist() for i in ids],
            "t": [cams[i].t.tolist() for i in ids],
            "intrinsics": {k: getattr(cams[ids[0]], k) for k in ("fx", "fy", "cx", "cy", "w", "h")},
        },
        "provenance": {
            "method": "coarse picks by eye in anchor frames -> LSD line-intersection sub-pixel "
            "refinement -> KLT tracking through the 10 fps frames, re-refined at every SfM frame "
            "-> undistort (SfM SIMPLE_RADIAL) onto the scene pinhole -> N-view DLT + Gauss-Newton "
            f"with greedy outlier-view removal (max {max_px}px); then the point is projected into "
            "every other SfM frame, re-refined there within 3 px and re-triangulated. No mesh or "
            "dense cloud is used anywhere.",
            "raw_camera": raw_cam,
            "code": "pipeline/experiments/structure/gt.py",
        },
        "points": points,
        "dropped": dropped,
    }
    X = {k: np.array(v["xyz"]) for k, v in points.items()}
    dist = lambda a, b: float(np.linalg.norm(X[a] - X[b]))
    gt["pairs"] = [
        {**p, "dist_m": dist(p["a"], p["b"])}
        for p in picks.get("pairs", [])
        if p["a"] in X and p["b"] in X
    ]
    gt["ratio_reference"] = picks.get("ratio_reference")
    gt["same_length_groups"] = picks.get("same_length_groups", [])
    gt["angle_pairs"] = picks.get("angle_pairs", [])
    gt["planes"] = [p for p in picks.get("planes", []) if all(q in X for q in p["points"])]
    gt["edges"] = [e for e in picks.get("edges", []) if e["a"] in X and e["b"] in X]
    out.write_text(json.dumps(gt, indent=1))
    return gt


def main(argv: list[str] | None = None) -> None:
    import argparse

    ap = argparse.ArgumentParser(prog="python -m pipeline.experiments.structure.gt")
    sub = ap.add_subparsers(dest="cmd", required=True)
    o = sub.add_parser("observe", help="picks.json -> observations.json (2D, mesh-free)")
    o.add_argument("picks")
    o.add_argument("--frames", required=True, help="the run's frames/ (10 fps grid)")
    o.add_argument("--out", required=True)
    o.add_argument("--span", type=int, default=150, help="max 10 fps frames tracked each way")
    b = sub.add_parser("build", help="observations -> gt.json (triangulated, scene frame)")
    b.add_argument("picks")
    b.add_argument("--obs", required=True)
    b.add_argument("--scene", required=True, help="scene dir whose cameras define the frame")
    b.add_argument("--sparse-raw", required=True, help="that run's sfm/sparse/0 (distortion)")
    b.add_argument("--out", required=True)
    b.add_argument("--max-px", type=float, default=1.5)
    b.add_argument("--scene-label", default=None, help="provenance name for --scene")
    a = ap.parse_args(argv)
    if a.cmd == "observe":
        observe(Path(a.picks), Path(a.frames), Path(a.out), a.span)
    else:
        gt = build(
            Path(a.picks),
            Path(a.obs),
            Path(a.scene),
            Path(a.sparse_raw),
            Path(a.out),
            a.max_px,
            scene_label=a.scene_label,
        )
        for k, v in gt["points"].items():
            print(
                f"{k:10s} {v['grade']} n={v['n_obs']:2d} rej={v['n_rejected']:2d} rms={v['reproj_rms_px']:.2f}"
                f" max={v['reproj_max_px']:.2f} ang={v['tri_angle_deg']:5.1f}"
                f" sig={v['sigma_m_at_0.5px'] * 1000:.2f}mm split={(v['split_half_m'] or 0) * 1000:.2f}mm"
            )
        print("dropped:", gt["dropped"])
        for p in gt["pairs"]:
            print(f"pair {p['a']}-{p['b']}: {p['dist_m'] * 1000:.1f} mm")


if __name__ == "__main__":
    main()
