"""S3: PxwPlanar per-frame planarity, depth, normals and plane segments (docs/research/structure/
s3-learned-planes.md). Runs INSIDE its own venv (`/workspace/envs/pxw`: the planarity MoGe fork
shadows other MoGe installs).

    python pxw_infer.py --dense work/dji0095/sfm/dense --out /workspace/s3/pxw_cache [--long-edge 960]

Per undistorted COLMAP image: resize (aspect kept) to `--long-edge`, MoGe 4-head `infer` with our
COLMAP horizontal FoV, then PxwPlanar's region growing with the paper's canonical parameters
(planarity > 0.3, 5 deg, 2.5 % depth, 8 neighbours). One `<name>.npz` per frame: `labels` (int16,
0 = none), `depth` (m, float16), `normal` (camera frame, float16), `planarity` (uint8, x/255),
`mask`. Resumable: frames with an npz are skipped.
"""

import argparse
import json
import sys
import time
from pathlib import Path

if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)

import numpy as np


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--dense", required=True, help="COLMAP undistorted dir (images/, sparse/)")
    p.add_argument("--out", required=True)
    p.add_argument("--long-edge", type=int, default=960)
    p.add_argument("--num-tokens", type=int, default=1600)
    p.add_argument("--model", default="alpayozkan/pxwplanar-moge2-planarity")
    p.add_argument("--limit", type=int, default=0, help="first N frames only (smoke)")
    a = p.parse_args()

    import cv2
    import pycolmap
    import torch
    from pxwplanar.inference.planarity.moge_inference import MoGePlanarityInference
    from pxwplanar.shared.segmentation import compute_planar_segments
    from pxwplanar.shared.utils.label_utils import remap_labels

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    rec = pycolmap.Reconstruction(Path(a.dense) / "sparse")
    ims = sorted(rec.images.values(), key=lambda im: im.name)
    if a.limit:
        ims = ims[: a.limit]
    t0 = time.time()
    net = MoGePlanarityInference.from_pretrained(a.model).model.eval()
    t_load = time.time() - t0
    per = []
    for im in ims:
        dst = out / f"{Path(im.name).stem}.npz"
        if dst.exists():
            continue
        t1 = time.time()
        cam = rec.cameras[im.camera_id]
        fov_x = float(np.degrees(2 * np.arctan(cam.width / 2 / cam.calibration_matrix()[0, 0])))
        rgb = cv2.cvtColor(cv2.imread(str(Path(a.dense) / "images" / im.name)), cv2.COLOR_BGR2RGB)
        s = a.long_edge / max(rgb.shape[:2])
        w, h = round(rgb.shape[1] * s), round(rgb.shape[0] * s)
        x = torch.from_numpy(cv2.resize(rgb, (w, h), interpolation=cv2.INTER_AREA))
        x = (x.permute(2, 0, 1).float() / 255)[None].cuda()
        with torch.no_grad():
            o = net.infer(x, num_tokens=a.num_tokens, fov_x=fov_x, force_projection=True,
                          apply_mask=True)  # fmt: skip
        depth = np.nan_to_num(o["depth"][0].float().cpu().numpy(), posinf=0.0).astype(np.float32)
        normal = np.nan_to_num(o["normal"][0].float().cpu().numpy()).astype(np.float32)
        plan = o["planarity"][0].float().cpu().numpy()
        mask = o["mask"][0].cpu().numpy().astype(bool)
        labels, _ = compute_planar_segments(
            ((plan > 0.3) & mask).astype(np.int16), normal, depth, np.deg2rad(5.0), 0.025,
            neighbor_match_count_thresh=8,
        )  # fmt: skip
        labels, _ = remap_labels(labels)
        np.savez_compressed(
            dst,
            labels=labels.astype(np.int16),
            depth=depth.astype(np.float16),
            normal=normal.astype(np.float16),
            planarity=(np.clip(plan, 0, 1) * 255).astype(np.uint8),
            mask=mask,
        )
        per.append(time.time() - t1)
        print(f"{im.name}: {labels.max()} segments, {per[-1]:.2f}s", flush=True)
    stats = {
        "frames": len(per),
        "load_s": t_load,
        "infer_seg_s_total": float(np.sum(per)),
        "infer_seg_s_median": float(np.median(per)) if per else None,
        "long_edge": a.long_edge,
        "num_tokens": a.num_tokens,
    }
    (out / "infer_stats.json").write_text(json.dumps(stats, indent=2))
    print(json.dumps(stats))


if __name__ == "__main__":
    main()
