"""Runs INSIDE `AIRTOOLS_VGGT_ENV` (torch/open3d/vggt only live there) -- plan §1a/§2.2 Stage A's
GPU-side work: VGGT forward pass (poses+depth) -> Open3D TSDF fusion -> mesh -> seed sparse points
-> full-resolution pycolmap Reconstruction -> undistort -> OpenMVS `InterfaceCOLMAP` ->
`TextureMesh`. Adapted from the measured-working prototype
(`pipeline/experiments/fast/vggt_tsdf_texture.py`, `docs/research/p1-fast-recon.md` §7.1) into a
reusable `process_job` so both `once` (cold, one process per call -- `pipeline.fast`'s default) and
`worker` (warm, loads the model once and serves jobs from a spool dir -- kept alive by hand, e.g.
in a tmux session, across repeated preview runs during a demo) can call it.

Standalone by design: no `pipeline.*` imports (this venv doesn't have pymeshlab/pycolmap-the-same-
build/etc guaranteed) -- talks to the orchestrator (`pipeline.fast`) purely via JSON job/result
files on disk. `MIOPEN_USER_DB_PATH`/`MIOPEN_CUSTOM_CACHE_DIR`/`TORCH_HOME` are pinned under
/workspace (not the container's ephemeral home) BEFORE `import torch`, so a fresh container doesn't
recompile ROCm kernels or re-download the 4.68GB checkpoint every cold start.
"""

import argparse
import glob
import json
import os
import shutil
import sys
import time
import traceback
from pathlib import Path

# Running this file directly (`python .../pipeline/fast_worker.py`) puts its own directory
# (pipeline/) at sys.path[0]. This script is standalone by design (no pipeline.* imports, see the
# module docstring), so that directory serves no purpose here -- but its presence is actively
# harmful: the VGGT repo's own `vggt` package (added to sys.path in main(), below) has no
# `__init__.py` (a PEP 420 namespace package), and a namespace package always loses to a *regular*
# module of the same name found anywhere else on sys.path, regardless of search order -- so
# `import vggt` would silently resolve to our unrelated pipeline/vggt.py instead. Dropping our own
# directory removes that regular-module competitor before it can shadow anything.
if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)

os.environ.setdefault("TORCH_HOME", "/workspace/.cache/torch")
os.environ.setdefault("MIOPEN_USER_DB_PATH", "/workspace/.cache/miopen")
os.environ.setdefault("MIOPEN_CUSTOM_CACHE_DIR", "/workspace/.cache/miopen")
for _cache_dir in (os.environ["MIOPEN_USER_DB_PATH"], os.environ["MIOPEN_CUSTOM_CACHE_DIR"]):
    Path(_cache_dir).mkdir(parents=True, exist_ok=True)

import numpy as np
import torch

DEVICE = "cuda"  # ROCm's torch build also uses the "cuda" device string
_VOXEL_RESOLUTION = 320  # 320^3 voxels -- matches the prototype's "fast preview" budget
_SEED_POINTS_PER_FRAME = 150  # prototype: enough for InterfaceCOLMAP's view-selection, OpenMVS
# does its own (more robust) outlier fusion downstream -- see pipeline/vggt.py's identical choice
_HEARTBEAT_INTERVAL_S = 5.0
_POLL_INTERVAL_S = 0.3


def _write_json_atomic(path, obj) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f".{path.name}.tmp")
    tmp.write_text(json.dumps(obj))
    tmp.replace(path)


def load_model():
    from vggt.models.vggt import VGGT

    model = VGGT.from_pretrained("facebook/VGGT-1B").to(DEVICE)
    model.eval()
    return model


def _forward_pass(model, image_paths: list[str]):
    from vggt.utils.load_fn import load_and_preprocess_images
    from vggt.utils.pose_enc import pose_encoding_to_extri_intri

    images = load_and_preprocess_images(image_paths, mode="crop").to(DEVICE)
    Hm, Wm = images.shape[-2:]
    # vggt/models/vggt.py's own forward() runs the aggregator under bf16 autocast but wraps
    # camera_head/depth_head in fp32 (`torch.autocast(..., enabled=False)`) -- running the heads
    # under bf16 produces NaN depth (exp() activation overflow), confirmed in the prototype pass;
    # match the reference call sequence exactly.
    if os.environ.get("AIRTOOLS_VGGT_LOWVRAM"):
        # harry7557558/vggt-low-vram (AIRTOOLS_VGGT_REPO points at it): whole model in bf16, its
        # forward() does the mixed precision itself and returns fp32; ~6x less VRAM, so all
        # ~175 frames of a clip fit in one pass on 16 GB (bench E1, experimental).
        model = model.to(torch.bfloat16)
        with torch.no_grad():
            pred = model(images.to(torch.bfloat16))
        extrinsic, intrinsic = pose_encoding_to_extri_intri(pred["pose_enc"], images.shape[-2:])
        depth_map, depth_conf = pred["depth"], pred["depth_conf"]
    else:
        with torch.no_grad():
            with torch.autocast(device_type="cuda", dtype=torch.bfloat16):
                aggregated_tokens_list, ps_idx = model.aggregator(images[None])
            with torch.autocast(device_type="cuda", enabled=False):
                pose_enc = model.camera_head(aggregated_tokens_list)[-1]
                extrinsic, intrinsic = pose_encoding_to_extri_intri(pose_enc, images.shape[-2:])
                depth_map, depth_conf = model.depth_head(
                    aggregated_tokens_list, images[None], ps_idx
                )
    torch.cuda.synchronize()
    extrinsic = extrinsic.squeeze(0).float().cpu().numpy()  # (N,3,4) cam-from-world
    intrinsic = intrinsic.squeeze(0).float().cpu().numpy()  # (N,3,3), in Wm x Hm model space
    depth_map = depth_map.squeeze(0).float().cpu().numpy()
    depth_conf = depth_conf.squeeze(0).float().cpu().numpy()
    if depth_map.ndim == 4:
        depth_map = depth_map[..., 0]
    if depth_conf.ndim == 4:
        depth_conf = depth_conf[..., 0]
    rgb_lowres = (images.permute(0, 2, 3, 1).float().cpu().numpy() * 255).astype(np.uint8)
    return extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres, Hm, Wm


def _tsdf_fuse(extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres, Hm, Wm):
    import open3d as o3d

    n = len(extrinsic)
    # open3d==0.20.0's ScalableTSDFVolume.integrate() was confirmed (p1-fast-recon.md §7.1) to
    # silently produce 0 points/vertices on any input in this pip wheel -- UniformTSDFVolume (same
    # package) doesn't have that bug, used here instead.
    depth_valid = (depth_conf > np.percentile(depth_conf, 40)) & (depth_map > 1e-6)
    med_depth = (
        float(np.median(depth_map[depth_valid]))
        if depth_valid.any()
        else float(np.median(depth_map))
    )
    cam_centers = np.array([-extrinsic[k][:3, :3].T @ extrinsic[k][:3, 3] for k in range(n)])
    max_depth = float(np.percentile(depth_map[depth_valid], 99)) if depth_valid.any() else med_depth
    margin = max_depth * 1.2
    bbox_min = cam_centers.min(axis=0) - margin
    bbox_max = cam_centers.max(axis=0) + margin
    extent = float((bbox_max - bbox_min).max())
    voxel = extent / _VOXEL_RESOLUTION
    sdf_trunc = voxel * 6

    volume = o3d.pipelines.integration.UniformTSDFVolume(
        length=extent,
        resolution=_VOXEL_RESOLUTION,
        sdf_trunc=sdf_trunc,
        color_type=o3d.pipelines.integration.TSDFVolumeColorType.RGB8,
        origin=bbox_min,
    )
    intr_o3d = o3d.camera.PinholeCameraIntrinsic()
    for i in range(n):
        d = depth_map[i].copy()
        d[~depth_valid[i]] = 0.0  # zero-out low-confidence/invalid depth so TSDF skips it
        depth_o3d = o3d.geometry.Image(np.ascontiguousarray(d.astype(np.float32)))
        color_o3d = o3d.geometry.Image(np.ascontiguousarray(rgb_lowres[i]))
        rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
            color_o3d,
            depth_o3d,
            depth_scale=1.0,
            depth_trunc=med_depth * 4,
            convert_rgb_to_intensity=False,
        )
        fx, fy, cx, cy = (
            intrinsic[i, 0, 0],
            intrinsic[i, 1, 1],
            intrinsic[i, 0, 2],
            intrinsic[i, 1, 2],
        )
        intr_o3d.set_intrinsics(Wm, Hm, fx, fy, cx, cy)
        extr_4x4 = np.eye(4)
        extr_4x4[:3, :4] = extrinsic[i]  # cam_from_world
        volume.integrate(rgbd, intr_o3d, extr_4x4)
    o3d_mesh = volume.extract_triangle_mesh()
    o3d_mesh.compute_vertex_normals()
    if len(o3d_mesh.triangles) == 0:
        raise RuntimeError("fast_worker: TSDF fusion produced an empty mesh")

    # drop tiny disconnected junk (sky/noise blobs) before handing to OpenMVS
    tri_clusters, cluster_n_tris, _ = o3d_mesh.cluster_connected_triangles()
    tri_clusters = np.asarray(tri_clusters)
    cluster_n_tris = np.asarray(cluster_n_tris)
    keep = cluster_n_tris[tri_clusters] > (cluster_n_tris.max() * 0.02)
    o3d_mesh.remove_triangles_by_mask(~keep)
    o3d_mesh.remove_unreferenced_vertices()
    return o3d_mesh, depth_valid


def _seed_points(n: int, extrinsic, intrinsic, depth_map, depth_valid, rgb_lowres, seed: int = 0):
    """~150 valid pixels/frame, unprojected to world space -- an empty/near-empty points3D makes
    OpenMVS's `InterfaceCOLMAP` crash with no error text (p1-fast-recon.md §7.1's own finding);
    OpenMVS does its own (more robust) outlier fusion downstream, point *quantity* is what matters
    here (same reasoning as `pipeline/vggt.py`'s `_VGGT_CONF_THRES`/`_VGGT_VIS_THRES`)."""
    rng = np.random.default_rng(seed)
    pts3d_list, xyf_list, rgb_list = [], [], []
    for i in range(n):
        ys, xs = np.where(depth_valid[i])
        if len(ys) == 0:
            continue
        pick = rng.choice(len(ys), size=min(_SEED_POINTS_PER_FRAME, len(ys)), replace=False)
        ys, xs = ys[pick], xs[pick]
        zs = depth_map[i, ys, xs]
        fx, fy, cx, cy = (
            intrinsic[i, 0, 0],
            intrinsic[i, 1, 1],
            intrinsic[i, 0, 2],
            intrinsic[i, 1, 2],
        )
        X = (xs - cx) / fx * zs
        Y = (ys - cy) / fy * zs
        p_cam = np.stack([X, Y, zs], axis=1)
        R, t = extrinsic[i][:3, :3], extrinsic[i][:3, 3]
        p_world = (p_cam - t) @ R  # cam_from_world inverse: p_world = R^T (p_cam - t)
        pts3d_list.append(p_world)
        xyf_list.append(np.stack([xs, ys, np.full(len(xs), i)], axis=1))
        rgb_list.append(rgb_lowres[i, ys, xs])
    if not pts3d_list:
        raise RuntimeError("fast_worker: no valid depth pixels to seed InterfaceCOLMAP's points")
    return (
        np.concatenate(pts3d_list, axis=0),
        np.concatenate(xyf_list, axis=0).astype(np.float64),
        np.concatenate(rgb_list, axis=0),
    )


def process_job(model, job: dict, vggt_repo: Path) -> dict:
    """`job`: `{"small_image_paths", "full_image_paths", "names"` (parallel lists, model-input /
    texturing / basename), `"work_dir"` (this job's own scratch dir), `"out_textured_glb"`,
    `"out_raw_poses_json"` (written on success: a JSON list of `{name, R_wc, t_wc, fx, fy, cx, cy,
    w, h}` at FULL resolution -- `pipeline.run.RawPose`'s exact fields), `"openmvs_bin_dir"`,
    `"max_texture_size"}`. Returns `{"status": "ok", "timings_s": {...}}` or `{"status": "error",
    "error": "..."}` -- never raises, so a `worker` loop can report one failed job without dying.
    """
    timings: dict[str, float] = {}
    try:
        import pycolmap
        from PIL import Image
        from vggt.dependency.np_to_pycolmap import batch_np_matrix_to_pycolmap_wo_track

        names = job["names"]
        small_paths = job["small_image_paths"]
        full_paths = job["full_image_paths"]
        # .resolve() matters: OpenMVS's InterfaceCOLMAP re-interprets a *relative* -i against its
        # own -w (working dir), not the process's actual cwd -- a relative work_dir here doubled
        # into "mvs/work/<site>/preview/vggt/sfm/dense/..." (confirmed on a real hfbox run; same
        # class of bug already fixed for Stage B, see pipeline/mvs.py's identical comment).
        work_dir = Path(job["work_dir"]).resolve()
        work_dir.mkdir(parents=True, exist_ok=True)

        t0 = time.time()
        extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres, Hm, Wm = _forward_pass(
            model, small_paths
        )
        timings["vggt_forward_s"] = time.time() - t0
        torch.cuda.empty_cache()

        t0 = time.time()
        o3d_mesh, depth_valid = _tsdf_fuse(
            extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres, Hm, Wm
        )
        timings["tsdf_fuse_s"] = time.time() - t0

        t0 = time.time()
        import open3d as o3d

        tsdf_mesh_path = work_dir / "tsdf_mesh.ply"
        o3d.io.write_triangle_mesh(str(tsdf_mesh_path), o3d_mesh)
        timings["mesh_export_s"] = time.time() - t0

        t0 = time.time()
        points3d, points_xyf, points_rgb = _seed_points(
            len(names), extrinsic, intrinsic, depth_map, depth_valid, rgb_lowres
        )
        recon = batch_np_matrix_to_pycolmap_wo_track(
            points3d=points3d,
            points_xyf=points_xyf,
            points_rgb=points_rgb,
            extrinsics=extrinsic,
            intrinsics=intrinsic,
            image_size=np.array([Wm, Hm]),
            shared_camera=False,
            camera_type="PINHOLE",
        )
        raw_images_dir = work_dir / "sfm" / "raw_images"
        raw_images_dir.mkdir(parents=True, exist_ok=True)
        full_sizes = {}
        for idx, (_image_id, image) in enumerate(
            sorted(recon.images.items(), key=lambda kv: kv[0])
        ):
            name = names[idx]
            image.name = name
            cam = recon.cameras[image.camera_id]
            with Image.open(full_paths[idx]) as im:
                W, H = im.size
            full_sizes[name] = (W, H)
            sx, sy = W / Wm, H / Hm
            fx, fy, cx, cy = cam.params
            cam.params = np.array([fx * sx, fy * sy, cx * sx, cy * sy])
            cam.width, cam.height = W, H
            dst = raw_images_dir / name
            if not dst.exists():
                os.symlink(Path(full_paths[idx]).resolve(), dst)
        raw_sparse_dir = work_dir / "sfm" / "raw_sparse"
        raw_sparse_dir.mkdir(parents=True, exist_ok=True)
        recon.write(str(raw_sparse_dir))
        timings["build_recon_s"] = time.time() - t0

        t0 = time.time()
        dense_dir = work_dir / "sfm" / "dense"
        # a re-run (e.g. `run_preview(..., force=True)`, a warm worker's spool serving the same
        # site twice) leaves a populated dense_dir from last time -- pycolmap.undistort_images
        # refuses to overwrite an existing images/ dir ("boost::filesystem::copy_file: File
        # exists"), confirmed on a real hfbox rerun. Clear it first so this is idempotent.
        if dense_dir.exists():
            shutil.rmtree(dense_dir)
        pycolmap.undistort_images(
            output_path=str(dense_dir),
            input_path=str(raw_sparse_dir),
            image_path=str(raw_images_dir),
            output_type="COLMAP",
        )
        timings["undistort_s"] = time.time() - t0

        import subprocess

        bin_dir = Path(job["openmvs_bin_dir"])
        mvs_dir = work_dir / "mvs"
        mvs_dir.mkdir(exist_ok=True)

        t0 = time.time()
        r = subprocess.run(
            [
                str(bin_dir / "InterfaceCOLMAP"),
                "-i",
                str(dense_dir),
                "-o",
                "scene.mvs",
                "--image-folder",
                str(dense_dir / "images"),
                "-w",
                str(mvs_dir),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        timings["interface_colmap_s"] = time.time() - t0
        if r.returncode != 0:
            raise RuntimeError(f"InterfaceCOLMAP failed: {r.stdout[-2000:]} {r.stderr[-2000:]}")

        t0 = time.time()
        out_glb = Path(job["out_textured_glb"])
        r = subprocess.run(
            [
                str(bin_dir / "TextureMesh"),
                "-i",
                "scene.mvs",
                "-m",
                str(tsdf_mesh_path.resolve()),
                "-o",
                str(out_glb.resolve()),
                "--max-texture-size",
                str(job.get("max_texture_size", 4096)),
                "--export-type",
                "glb",
                "--global-seam-leveling",
                "0",
                "--local-seam-leveling",
                "0",
                "-w",
                str(mvs_dir),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        timings["texture_mesh_s"] = time.time() - t0
        if r.returncode != 0:
            raise RuntimeError(f"TextureMesh failed: {r.stdout[-2000:]} {r.stderr[-2000:]}")

        raw_poses_out = []
        for idx, name in enumerate(names):
            W, H = full_sizes[name]
            sx, sy = W / Wm, H / Hm
            fx, fy, cx, cy = (
                intrinsic[idx, 0, 0],
                intrinsic[idx, 1, 1],
                intrinsic[idx, 0, 2],
                intrinsic[idx, 1, 2],
            )
            raw_poses_out.append(
                {
                    "name": name,
                    "R_wc": extrinsic[idx][:3, :3].tolist(),
                    "t_wc": extrinsic[idx][:3, 3].tolist(),
                    "fx": float(fx * sx),
                    "fy": float(fy * sy),
                    "cx": float(cx * sx),
                    "cy": float(cy * sy),
                    "w": int(W),
                    "h": int(H),
                }
            )
        _write_json_atomic(job["out_raw_poses_json"], raw_poses_out)

        return {"status": "ok", "timings_s": timings}
    except Exception as exc:  # noqa: BLE001 -- a job failure must be reported, not crash the worker
        return {
            "status": "error",
            "error": f"{exc}\n{traceback.format_exc()[-4000:]}",
            "timings_s": timings,
        }


def _run_once(job_path: Path, vggt_repo: Path) -> None:
    job = json.loads(Path(job_path).read_text())
    t0 = time.time()
    model = load_model()
    load_s = time.time() - t0
    result = process_job(model, job, vggt_repo)
    result.setdefault("timings_s", {})["model_load_s"] = load_s
    _write_json_atomic(job["result_path"], result)


def _run_worker(spool_dir: Path, vggt_repo: Path) -> None:
    spool_dir = Path(spool_dir)
    spool_dir.mkdir(parents=True, exist_ok=True)
    heartbeat = spool_dir / "heartbeat"
    print(f"fast_worker: loading VGGT once, serving jobs from {spool_dir}", flush=True)
    model = load_model()
    print("fast_worker: model loaded, warm and polling", flush=True)
    last_beat = 0.0
    while True:
        now = time.time()
        if now - last_beat >= _HEARTBEAT_INTERVAL_S:
            heartbeat.write_text(str(now))
            last_beat = now
        for job_path_str in sorted(glob.glob(str(spool_dir / "*.job.json"))):
            job_path = Path(job_path_str)
            try:
                job = json.loads(job_path.read_text())
            except (json.JSONDecodeError, OSError):
                continue  # writer hasn't finished its atomic rename yet -- pick it up next loop
            print(f"fast_worker: processing {job_path.name}", flush=True)
            result = process_job(model, job, vggt_repo)
            _write_json_atomic(job["result_path"], result)
            job_path.unlink(missing_ok=True)
        time.sleep(_POLL_INTERVAL_S)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--vggt-repo",
        type=Path,
        default=Path(os.environ.get("AIRTOOLS_VGGT_REPO", "/workspace/vggt")),
    )
    sub = parser.add_subparsers(dest="mode", required=True)
    p_once = sub.add_parser("once", help="load the model, run one job, exit (cold path)")
    p_once.add_argument("job_json", type=Path)
    p_worker = sub.add_parser("worker", help="load the model once, serve jobs from a spool dir")
    p_worker.add_argument("spool_dir", type=Path)
    args = parser.parse_args()

    # must happen before load_model()'s `from vggt.models.vggt import VGGT` -- inserted at index 0
    # so it wins over sys.path[0] (this script's own directory, `pipeline/`, which shadows the
    # real third-party `vggt` package with our unrelated same-named `pipeline/vggt.py`)
    sys.path.insert(0, str(args.vggt_repo))

    if args.mode == "once":
        _run_once(args.job_json, args.vggt_repo)
    else:
        _run_worker(args.spool_dir, args.vggt_repo)


if __name__ == "__main__":
    main()
