"""Evaluate a structure.json against held-out frames (S2, docs/research/structure/s2-lines.md).

For each held-out frame (a registered frame the line map never saw): project every 3D edge with
the COLMAP pose, detect LSD lines in the image, and match by angle, overlap and perpendicular
distance. Reports the median reprojection distance of matched edges, the fraction of visible
edges with a match, and the recall of strong image lines. Also renders overlay figures.

ponytail: no occlusion test (an edge behind a cabinet still projects); add a mesh depth test if
precision numbers matter more than the median residual.

    python -m pipeline.experiments.lines.evaluate --lines structure.json \
        --model <sfm>/dense/sparse --images <sfm>/dense/images --out eval_dir
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import cv2
import numpy as np
import pycolmap

AXIS_BGR = [(60, 60, 255), (60, 220, 60), (255, 120, 40), (0, 220, 255)]  # x, y, z, off-axis


def project_segment(p0, p1, cam_from_world, k, wh, near=1e-3):
    """World segment -> clipped 2D segment ((2,), (2,)) or None when not visible."""
    r, t = cam_from_world[:, :3], cam_from_world[:, 3]
    a, b = r @ p0 + t, r @ p1 + t
    if a[2] < near and b[2] < near:
        return None
    if a[2] < near or b[2] < near:  # clip at the near plane
        s = (near - a[2]) / (b[2] - a[2])
        c = a + s * (b - a)
        a, b = (c, b) if a[2] < near else (a, c)
    ua, ub = (k @ a)[:2] / a[2], (k @ b)[:2] / b[2]
    # Liang-Barsky clip to the image rectangle
    d = ub - ua
    t0, t1 = 0.0, 1.0
    for p, q in ((-d[0], ua[0]), (d[0], wh[0] - ua[0]), (-d[1], ua[1]), (d[1], wh[1] - ua[1])):
        if p == 0:
            if q < 0:
                return None
            continue
        r_ = q / p
        if p < 0:
            t0 = max(t0, r_)
        else:
            t1 = min(t1, r_)
    if t0 >= t1:
        return None
    return ua + t0 * d, ua + t1 * d


def seg_distance(ref, other, ang_tol_deg=3.0, min_overlap=0.5):
    """Mean perpendicular distance (px) from `other` to `ref`'s line over their overlap, or inf
    if the angle exceeds the tolerance or the overlap is under `min_overlap` of the shorter."""
    a, b = ref
    c, d = other
    u = b - a
    lr = np.linalg.norm(u)
    lo = np.linalg.norm(d - c)
    if lr < 1 or lo < 1:
        return np.inf
    u /= lr
    v = (d - c) / lo
    if abs(u @ v) < np.cos(np.radians(ang_tol_deg)):
        return np.inf
    tc, td = (c - a) @ u, (d - a) @ u
    lo_t, hi_t = max(min(tc, td), 0.0), min(max(tc, td), lr)
    if hi_t - lo_t < min_overlap * min(lr, lo):
        return np.inf
    n = np.array([-u[1], u[0]])

    def perp(tt):  # point on `other` whose projection on ref is tt
        s = (tt - tc) / (td - tc) if td != tc else 0.0
        return abs((c + s * (d - c) - a) @ n)

    return (perp(lo_t) + perp(hi_t)) / 2


def lsd(gray, min_len):
    det = cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD)
    lines = det.detect(gray)[0]
    if lines is None:
        return []
    lines = lines.reshape(-1, 4)
    keep = np.hypot(lines[:, 2] - lines[:, 0], lines[:, 3] - lines[:, 1]) >= min_len
    return [(ln[:2], ln[2:]) for ln in lines[keep]]


def evaluate(
    lines_json,
    model_dir,
    images_dir,
    out_dir,
    names=None,
    n_figs=4,
    match_px=10.0,
    recall_px=5.0,
    strong_len=60.0,
    min_vis_len=20.0,
):
    data = json.loads(Path(lines_json).read_text())
    names = names or data["heldout"]
    edges = [(np.array(e["a"]), np.array(e["b"]), e.get("axis", -1)) for e in data["edges"]]
    corners = [np.array(c["p"]) for c in data.get("corners", [])]
    recon = pycolmap.Reconstruction(str(model_dir))
    by_name = {im.name: im for im in recon.images.values()}
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    dists, vis_total, matched, recalled, strong_total, per_frame = [], 0, 0, 0, 0, []
    for fi, name in enumerate(names):
        im = by_name[name]
        cam = im.camera
        k = cam.calibration_matrix()
        pose = im.cam_from_world().matrix()
        wh = (cam.width, cam.height)
        img = cv2.imread(str(Path(images_dir) / name))
        det = lsd(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), 20)
        proj = []
        for p0, p1, ax in edges:
            s = project_segment(p0, p1, pose, k, wh)
            if s is not None and np.linalg.norm(s[1] - s[0]) >= min_vis_len:
                proj.append((s, ax))
        fd = []
        for s, _ in proj:
            best = min((seg_distance(s, d) for d in det), default=np.inf)
            if best <= match_px:
                fd.append(best)
        strong = [d for d in det if np.linalg.norm(d[1] - d[0]) >= strong_len]
        rec = sum(
            min((seg_distance(d, s) for s, _ in proj), default=np.inf) <= recall_px for d in strong
        )
        dists += fd
        vis_total += len(proj)
        matched += len(fd)
        recalled += rec
        strong_total += len(strong)
        per_frame.append(
            {
                "name": name,
                "visible": len(proj),
                "matched": len(fd),
                "median_px": float(np.median(fd)) if fd else None,
                "strong": len(strong),
                "recalled": int(rec),
            }
        )
        if fi % max(1, len(names) // n_figs) == 0:
            vis = img.copy()
            for a, b in det:
                cv2.line(vis, tuple(map(int, a)), tuple(map(int, b)), (200, 200, 200), 1)
            for (a, b), ax in proj:
                cv2.line(vis, tuple(map(int, a)), tuple(map(int, b)), AXIS_BGR[ax], 3)
            for c in corners:
                s = project_segment(c, c + 1e-6, pose, k, wh)
                if s is not None:
                    cv2.circle(vis, tuple(map(int, s[0])), 9, (255, 0, 255), 3)
            cv2.imwrite(
                str(out_dir / f"overlay_{Path(name).stem}.jpg"),
                cv2.resize(vis, None, fx=0.5, fy=0.5),
                [cv2.IMWRITE_JPEG_QUALITY, 80],
            )

    summary = {
        "frames": len(names),
        "median_px": float(np.median(dists)) if dists else None,
        "p90_px": float(np.percentile(dists, 90)) if dists else None,
        "visible_edges": vis_total,
        "matched_frac": matched / max(vis_total, 1),
        "strong_image_lines": strong_total,
        "strong_recall": recalled / max(strong_total, 1),
        "match_px": match_px,
        "recall_px": recall_px,
        "per_frame": per_frame,
    }
    (out_dir / "eval.json").write_text(json.dumps(summary, indent=1))
    return summary


def plot_views(lines_json, out_png, px=900):
    """Orthographic views of the line map in its Manhattan frame, side by side: axis0 x axis1,
    axis0 x axis2, axis2 x axis1. Edges coloured by axis (x red, y green, z blue, off-axis
    yellow), corners magenta."""
    data = json.loads(Path(lines_json).read_text())
    r = np.array(data["axes"])  # rows = axes
    e = np.array([[e["a"], e["b"]] for e in data["edges"]]) @ r.T
    ax = [e_["axis"] for e_ in data["edges"]]
    c = np.array([c["p"] for c in data["corners"]]).reshape(-1, 3) @ r.T
    lo, hi = np.percentile(e.reshape(-1, 3), [1, 99], axis=0)
    panels = []
    for i, j in ((0, 1), (0, 2), (2, 1)):
        span = max(hi[i] - lo[i], hi[j] - lo[j])
        img = np.full((px, px, 3), 255, np.uint8)

        def uv(p, i=i, j=j, span=span):
            return (
                int((p[i] - lo[i]) / span * (px - 40) + 20),
                int(px - 20 - (p[j] - lo[j]) / span * (px - 40)),
            )

        for (a, b), k in zip(e, ax):
            cv2.line(img, uv(a), uv(b), AXIS_BGR[k], 1, cv2.LINE_AA)
        for p in c:
            cv2.circle(img, uv(p), 3, (255, 0, 255), 1)
        panels.append(img)
    cv2.imwrite(str(out_png), np.hstack(panels))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lines", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--images", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    s = evaluate(a.lines, a.model, a.images, a.out)
    print(json.dumps({k: v for k, v in s.items() if k != "per_frame"}, indent=1))


if __name__ == "__main__":
    main()
