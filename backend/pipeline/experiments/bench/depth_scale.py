"""Independent metric-scale cue for the bench: MoGe-2 metric depth vs SfM depth at the SfM's own
keypoints (bench E1, the "metric-depth ratio" cue of R1 §9 R2).

    # pipeline env: sample the SfM's triangulated keypoints (u, v, depth) for N evenly spaced frames
    python pipeline/experiments/bench/depth_scale.py export <sfm>/dense <out.npz> [N]
    # depth env (/workspace/envs/depth, torch + moge): per-frame median ratio, then median + MAD
    python pipeline/experiments/bench/depth_scale.py moge <sfm>/dense <out.npz>

Per frame: median(MoGe depth / SfM depth) over its keypoints; across frames: median with MAD
(`pipeline/experiments/metric_scale.py`'s two-stage robust scheme). Result: metres per SfM unit,
to compare against the caption-altitude scale.
"""

import json
import sys
from pathlib import Path

import numpy as np


def export(dense: Path, out: Path, n: int = 30) -> None:
    import pycolmap

    recon = pycolmap.Reconstruction(dense / "sparse")
    ims = sorted(recon.images.values(), key=lambda i: i.name)
    ims = [ims[i] for i in np.linspace(0, len(ims) - 1, n).astype(int)]
    rows = {}
    for im in ims:
        cfw = im.cam_from_world()
        R, t = cfw.rotation.matrix(), np.asarray(cfw.translation)
        uvz = []
        for p2 in im.points2D:
            if p2.has_point3D():
                z = (R @ recon.points3D[p2.point3D_id].xyz + t)[2]
                uvz.append([p2.xy[0], p2.xy[1], z])
        rows[im.name] = np.array(uvz)
    np.savez(out, **rows)
    print(f"exported {len(rows)} frames, {np.mean([len(v) for v in rows.values()]):.0f} pts/frame")


def moge(dense: Path, npz: Path) -> None:
    import torch
    from moge.model import import_model_class_by_version
    from PIL import Image

    model = import_model_class_by_version("v2").from_pretrained("Ruicheng/moge-2-vitl-normal")
    model = model.to("cuda").eval()
    data = np.load(npz)
    per = []
    for name in data.files:
        uvz = data[name]
        img = np.asarray(Image.open(dense / "images" / name).convert("RGB"), np.float32) / 255
        with torch.no_grad():
            out = model.infer(torch.from_numpy(img).permute(2, 0, 1).cuda())
        d = out["depth"].cpu().numpy()
        h, w = d.shape
        u = np.clip(uvz[:, 0].astype(int), 0, w - 1)
        v = np.clip(uvz[:, 1].astype(int), 0, h - 1)
        ratio = d[v, u] / uvz[:, 2]
        ok = np.isfinite(ratio) & (uvz[:, 2] > 0)
        if ok.sum() >= 20:
            per.append(float(np.median(ratio[ok])))
    per = np.array(per)
    med = float(np.median(per))
    mad = float(np.median(np.abs(per - med)))
    print(
        json.dumps(
            {
                "frames": len(per),
                "scale_m_per_unit": med,
                "mad": mad,
                "p10": float(np.percentile(per, 10)),
                "p90": float(np.percentile(per, 90)),
            }
        )
    )


if __name__ == "__main__":
    cmd, dense, out = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
    if cmd == "export":
        export(dense, out, int(sys.argv[4]) if len(sys.argv) > 4 else 30)
    else:
        moge(dense, out)
