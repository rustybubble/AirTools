"""E6: MoGe-2 depth + normal priors for the 2DGS trainer (`train.py --priors`).

Runs INSIDE `AIRTOOLS_DEPTH_ENV` (/workspace/envs/df). Per undistorted COLMAP frame: MoGe-2
(`moge-2-vitl-normal`, COLMAP FoV) -> depth fitted to the frame's SfM points with E3's scale plane
+ shift (`depthfusion_worker.fit_alignment`, same frame-rejection thresholds) -> `<out>/<frame>.npz`
with `d` (COLMAP units, 0 = invalid) and `n` (unit normals, OpenCV camera frame, flipped to face
the camera), both at the training size (long edge 960, nearest).
"""

import argparse
import importlib.util
import json
from pathlib import Path

import numpy as np

_spec = importlib.util.spec_from_file_location(
    "depthfusion_worker", Path(__file__).resolve().parents[2] / "depthfusion_worker.py"
)
dfw = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dfw)


def main(a) -> None:
    import torch
    from moge.model import import_model_class_by_version

    cam = np.load(a.cameras)
    names, K, wh = [str(n) for n in cam["names"]], cam["K"], cam["wh"]
    f_obs, uv_obs, z_obs = cam["obs_frame"], cam["obs_uv"], cam["obs_z"]
    W0, H0 = int(wh[0][0]), int(wh[0][1])
    s = min(1.0, a.long_edge / max(W0, H0))
    W, H = round(W0 * s), round(H0 * s)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    net = import_model_class_by_version("v2").from_pretrained("Ruicheng/moge-2-vitl-normal")
    net = net.to("cuda").eval()
    recs = []
    for i, nm in enumerate(names):
        rgb = dfw._load_rgb(Path(a.images) / nm)
        fov_x = float(np.degrees(2 * np.arctan(rgb.shape[1] / (2 * K[i][0, 0]))))
        x = torch.from_numpy(rgb).cuda().permute(2, 0, 1).float() / 255
        with torch.no_grad():
            o = net.infer(x, fov_x=fov_x)
        m = o["mask"].cpu().numpy().astype(bool)
        d = np.where(m, o["depth"].float().cpu().numpy(), 0)
        d = dfw._resize(np.nan_to_num(d), W, H, nearest=True)
        n = o["normal"].float().cpu().numpy()
        n = np.stack([dfw._resize(n[..., c], W, H, nearest=True) for c in range(3)], -1)
        n /= np.linalg.norm(n, axis=-1, keepdims=True) + 1e-9
        n *= np.where(n[..., 2:] > 0, -1.0, 1.0)  # face the camera (+z is forward)

        sel = f_obs == i
        px = np.clip((uv_obs[sel] * s).astype(int), 0, [W - 1, H - 1])
        dp, z = d[px[:, 1], px[:, 0]], z_obs[sel]
        ok = (dp > 0) & (z > 0)
        rec = {"name": nm, "n_pts": int(ok.sum()), "kept": False}
        if ok.sum() >= 20:
            uvn = np.stack([2 * px[ok, 0] / (W - 1) - 1, 2 * px[ok, 1] / (H - 1) - 1], 1)
            params, fit = dfw.fit_alignment(dp[ok], z[ok], uvn, "plane", False)
            rel = np.abs(fit - z[ok]) / z[ok]
            rec.update(med_rel_err=float(np.median(rel)), inlier_frac=float((rel < 0.05).mean()))
            if rec["med_rel_err"] <= 0.08 and rec["inlier_frac"] >= 0.5:
                za = dfw.apply_alignment(d, params, "plane", False, W, H)
                za[d <= 0] = 0
                # sanity: the normal head's convention vs normals of the aligned depth itself
                Kf = K[i].copy()
                Kf[:2] *= s
                v, u = np.mgrid[0:H, 0:W].astype(np.float32)
                P = np.stack(
                    [(u - Kf[0, 2]) / Kf[0, 0], (v - Kf[1, 2]) / Kf[1, 1], np.ones_like(u)], -1
                )
                P *= za[..., None]
                nd = np.cross(np.gradient(P, axis=0), np.gradient(P, axis=1))
                nd /= np.linalg.norm(nd, axis=-1, keepdims=True) + 1e-12
                valid = za > 0
                rec["normal_vs_depth_cos_median"] = float(np.median((nd * n).sum(-1)[valid]))
                np.savez(out / f"{nm}.npz", d=za.astype(np.float32), n=n.astype(np.float16))
                rec["kept"] = True
        recs.append(rec)
        print(json.dumps(rec), flush=True)
    (out / "priors.json").write_text(json.dumps(recs, indent=1))


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--cameras", required=True)
    p.add_argument("--images", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--long-edge", type=int, default=960)
    main(p.parse_args())
