"""S3: PlanarSplatting (ant-research, CVPR 2025) on our COLMAP poses with MoGe-2 depth and normal
priors instead of Metric3D-v2, then its planar mesh -> airtools.structure/1 (docs/research/
structure/s3-learned-planes.md). Runs INSIDE `/workspace/envs/psplat` (gfx1201 build of
diff-rect-rasterization + quaternion-utils: see psplat_hip.sh).

    python psplat_run.py --dense work/dji0095/sfm/dense --cache /workspace/s3/pxw_cache \
        --repo /workspace/src/psplat --out /workspace/s3/psplat [--iters 5000]

Priors come from the PxwPlanar cache (pxw_infer.py: its MoGe-2 depth and camera-frame normals at
960 px): depth gets E3's per-frame scale-plane fit to the SfM points, and poses and depth are
divided by MoGe's units-per-metre so PlanarSplatting's metric thresholds hold. `--convert-only`
re-reads an earlier run's planar mesh.
"""

import argparse
import json
import os
import sys
import time
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[3]
if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)
sys.path.insert(0, str(_ROOT))

import numpy as np

from pipeline.depthfusion_worker import apply_alignment, fit_alignment
from pipeline.experiments.planes import geom
from pipeline.experiments.planes.pxw_lift import (
    add_finish_args,
    finish,
    load_frames,
    units_per_m,
)


def priors(frames, caches, upm):
    import cv2

    data = {k: [] for k in ("color", "depth", "normal", "image_paths", "extrinsics", "intrinsics")}
    for f in frames:
        c = np.load(caches[f["name"]])
        mono = c["depth"].astype(np.float32)
        h, w = mono.shape
        s = w / f["wh"][0]
        px = np.clip(np.round((f["uv"] + 0.5) * s - 0.5).astype(int), 0, [w - 1, h - 1])
        dp = mono[px[:, 1], px[:, 0]]
        ok = (dp > 0) & (f["z"] > 0)
        uvn = np.stack([2 * px[ok, 0] / (w - 1) - 1, 2 * px[ok, 1] / (h - 1) - 1], 1)
        params, _ = fit_alignment(dp[ok], f["z"][ok], uvn, "plane", False)
        z = apply_alignment(mono, params, "plane", False, w, h) / upm
        z[(mono <= 0) | ~c["mask"]] = 0
        K = f["K"].copy()
        K[:2, :2] *= s
        K[:2, 2] = (K[:2, 2] + 0.5) * s - 0.5
        T = np.eye(4)
        T[:3, :3], T[:3, 3] = f["R"], f["t"] / upm
        rgb = cv2.cvtColor(cv2.imread(str(f["path"])), cv2.COLOR_BGR2RGB)
        n = c["normal"].astype(np.float32)  # OpenCV camera frame, facing the camera (as Metric3D)
        data["color"].append(cv2.resize(rgb, (w, h), interpolation=cv2.INTER_AREA))
        data["depth"].append(z.astype(np.float32))
        data["normal"].append(((n.transpose(2, 0, 1) + 1) / 2).astype(np.float32))
        data["image_paths"].append(str(f["path"]))
        data["extrinsics"].append(np.linalg.inv(T).astype(np.float32))
        data["intrinsics"].append(K.astype(np.float32))
    return data


def mesh_to_planes(ply: Path, upm: float, a) -> list[dict]:
    """PlanarSplatting's merged planar mesh (one vertex colour per plane instance) -> plane dicts
    in COLMAP units: TLS plane per instance, support = area-weighted samples of its triangles."""
    import trimesh

    m = trimesh.load(ply, process=False)
    col = np.asarray(m.visual.vertex_colors)[:, :3]
    fcol = col[m.faces[:, 0]]
    _, inst = np.unique(fcol, axis=0, return_inverse=True)
    inst = inst.ravel()
    planes = []
    for k in range(inst.max() + 1):
        sub = m.submesh([np.flatnonzero(inst == k)], append=True)
        if sub.area < a.min_area:
            continue
        S, _ = trimesh.sample.sample_surface_even(sub, max(200, int(sub.area / a.cell**2 * 4)))
        S = np.asarray(S) * upm
        n, d, rms = geom.fit_plane(np.asarray(sub.vertices) * upm)
        S = S - np.outer(S @ n + d, n)
        for comp in geom.support_components(
            n, d, S, np.zeros(len(S), int), a.cell * upm, 1, a.min_area * upm**2,
            simplify=a.simplify * upm,
        ):  # fmt: skip
            planes.append(
                {
                    **comp,
                    "n": n,
                    "d": d,
                    "rms": rms,
                    "inl": len(sub.vertices),
                    "src": "psplat",
                    "frames": 0,
                    "cluster": k,
                    "snapped": False,
                }
            )
    return planes


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--dense", required=True)
    p.add_argument("--cache", required=True)
    p.add_argument("--repo", default="/workspace/src/psplat")
    p.add_argument("--out", required=True)
    p.add_argument("--iters", type=int, default=5000)
    p.add_argument("--init-planes", type=int, default=3000)
    p.add_argument("--convert-only", action="store_true")
    add_finish_args(p)
    a = p.parse_args()
    out = Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    a.dense = str(Path(a.dense).resolve())  # PlanarSplatting chdirs into its repo
    frames = load_frames(Path(a.dense))
    caches = {f["name"]: Path(a.cache) / f"{Path(f['name']).stem}.npz" for f in frames}
    frames = [f for f in frames if caches[f["name"]].exists()]
    # PlanarSplatting writes mono_mesh.ply next to the images' parent dir: point it at symlinks
    # under --out so the (read-only) SfM cache is never written to
    (out / "images").mkdir(exist_ok=True)
    for f in frames:
        f["path"] = out / "images" / f["name"]
        if not f["path"].exists():
            f["path"].symlink_to(Path(a.dense).resolve() / "images" / f["name"])
    upm = units_per_m(frames, caches)
    timings = {}
    if not a.convert_only:
        data = priors(frames, caches, upm)
        timings["priors_s"] = time.time() - t0
        repo = Path(a.repo)
        os.chdir(repo)
        sys.path[:0] = [str(repo), str(repo / "planarsplat")]
        from pyhocon import ConfigFactory, ConfigTree
        from utils_demo.run_planarSplatting import run_planarSplatting

        conf = ConfigTree.merge_configs(
            ConfigFactory.parse_file("planarsplat/confs/base_conf_planarSplatCuda.conf"),
            ConfigFactory.parse_file("utils_demo/demo.conf"),
        )
        data["out_path"] = str(out)
        conf.put("train.exps_folder_name", str(out))
        conf.put("train.max_total_iters", a.iters)
        conf.put("dataset.img_res", list(data["color"][0].shape[:2]))
        conf.put(
            "dataset.pre_align", False
        )  # our depth is already SfM-aligned; its re-align needs pyrender
        conf.put("dataset.voxel_length", 0.1)
        conf.put("dataset.sdf_trunc", 0.2)
        conf.put("plane_model.init_plane_num", a.init_planes)
        t1 = time.time()
        run_planarSplatting(data=data, conf=conf)
        timings["planarsplatting_s"] = time.time() - t1
    ply = max(out.rglob("*_planar_mesh.ply"), key=lambda q: q.stat().st_mtime)
    t2 = time.time()
    planes = mesh_to_planes(ply, upm, a)
    doc = finish(planes, upm, a, "planarsplatting+moge2-priors", {"mesh": str(ply)})
    timings["convert_s"] = time.time() - t2
    doc["stats"]["timings_s"] = timings
    doc["cost"]["runtime_s"] = round(time.time() - t0, 1)
    (out / "structure.json").write_text(json.dumps(doc))
    print(json.dumps({k: len(doc[k]) for k in ("planes", "edges", "corners")} | timings))


if __name__ == "__main__":
    main()
