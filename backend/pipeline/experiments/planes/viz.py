"""S3 figures: PxwPlanar masks (coloured by fused scene plane) and the 3D plane layer (planes,
edges, corners of a structure.json in the COLMAP frame) re-projected into undistorted frames with a
z-buffer over the planes.

    python viz.py --dense work/dji0095/sfm/dense --structure s.json [--cache pxw_cache] \
        --frames 0001.jpg,0301.jpg --out fig.jpg
"""

import argparse
import json
import sys
from pathlib import Path

if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)

import numpy as np


def colours(n: int, seed: int = 3) -> np.ndarray:
    import matplotlib

    rng = np.random.default_rng(seed)
    return np.array(matplotlib.colormaps["tab20"](rng.permutation(max(n, 1)) % 20))[:, :3] * 255


def render_layer(img, K, R, t, doc, alpha=0.45):
    import cv2

    h, w = img.shape[:2]
    C = -R.T @ t
    v, u = np.mgrid[0:h, 0:w].astype(np.float32)
    rays_w = np.stack([(u - K[0, 2]) / K[0, 0], (v - K[1, 2]) / K[1, 1], np.ones_like(u)], -1) @ R
    zbuf = np.full((h, w), np.inf, np.float32)
    idx = np.full((h, w), -1, np.int32)
    col = colours(len(doc["planes"]))
    for i, p in enumerate(doc["planes"]):
        P = np.array(p["polygon"])
        Xc = P @ R.T + t
        if (Xc[:, 2] < 0.05).any():
            continue
        uv = Xc[:, :2] / Xc[:, 2:] * [K[0, 0], K[1, 1]] + K[:2, 2]
        m = np.zeros((h, w), np.uint8)
        cv2.fillPoly(m, [np.round(uv).astype(np.int32)], 1)
        m = m.astype(bool)
        if not m.any():
            continue
        n, d = np.array(p["normal"]), p["offset"]
        lam = -(n @ C + d) / (rays_w[m] @ n)
        zb = zbuf[m]
        closer = (lam > 0) & (lam < zb)
        zb[closer] = lam[closer]
        zbuf[m] = zb
        ix = idx[m]
        ix[closer] = i
        idx[m] = ix
    out = img.astype(np.float32)
    has = idx >= 0
    out[has] = (1 - alpha) * out[has] + alpha * col[idx[has]]
    out = out.astype(np.uint8)
    for e in doc["edges"]:
        X = np.array([e["a"], e["b"]]) @ R.T + t
        if (X[:, 2] < 0.05).any():
            continue
        uv = X[:, :2] / X[:, 2:] * [K[0, 0], K[1, 1]] + K[:2, 2]
        cv2.line(out, *[tuple(map(int, np.round(q))) for q in uv], (255, 0, 0), 3, cv2.LINE_AA)
    for c in doc["corners"]:
        X = np.array(c["p"]) @ R.T + t
        if X[2] > 0.05:
            q = X[:2] / X[2] * [K[0, 0], K[1, 1]] + K[:2, 2]
            cv2.circle(out, tuple(map(int, np.round(q))), 9, (255, 255, 0), -1, cv2.LINE_AA)
    return out


def mask_panel(img, cache_npz, fclusters, name):
    """PxwPlanar segments coloured by the scene cluster they fused into (grey = dropped)."""
    import cv2

    c = np.load(cache_npz)
    lab = cv2.resize(c["labels"], img.shape[1::-1], interpolation=cv2.INTER_NEAREST)
    k = max(fclusters.values()) + 1 if fclusters else 1
    col = colours(k, seed=5)
    out = img.astype(np.float32) * 0.45
    for L in np.unique(lab[lab > 0]):
        cl = fclusters.get(f"{name}:{L}")
        m = lab == L
        out[m] += 0.55 * (col[cl] if cl is not None else np.array([128, 128, 128]))
    edges = cv2.Canny(lab.astype(np.uint8), 0, 1) > 0
    out[edges] = 255
    return out.astype(np.uint8)


def main() -> None:
    import cv2
    import pycolmap

    p = argparse.ArgumentParser()
    p.add_argument("--dense", required=True)
    p.add_argument("--structure", required=True)
    p.add_argument("--cache", default=None)
    p.add_argument("--frames", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--width", type=int, default=960)
    a = p.parse_args()
    doc = json.loads(Path(a.structure).read_text())
    fc_path = Path(a.structure).with_suffix(".frames.json")
    fc = json.loads(fc_path.read_text()) if fc_path.exists() else {}
    rec = pycolmap.Reconstruction(Path(a.dense) / "sparse")
    by_name = {im.name: im for im in rec.images.values()}
    rows = []
    for nm in a.frames.split(","):
        im = by_name[nm]
        cam = rec.cameras[im.camera_id]
        img = cv2.cvtColor(cv2.imread(str(Path(a.dense) / "images" / nm)), cv2.COLOR_BGR2RGB)
        T = im.cam_from_world().matrix()
        panels = [render_layer(img, cam.calibration_matrix(), T[:, :3], T[:, 3], doc)]
        if a.cache:
            panels.insert(0, mask_panel(img, Path(a.cache) / f"{Path(nm).stem}.npz", fc, nm))
        row = np.hstack(panels)
        s = a.width * len(panels) / row.shape[1]
        rows.append(cv2.resize(row, None, fx=s, fy=s, interpolation=cv2.INTER_AREA))
    cv2.imwrite(
        a.out, cv2.cvtColor(np.vstack(rows), cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 85]
    )


if __name__ == "__main__":
    main()
