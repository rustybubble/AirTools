"""Hands-on benchmark: VGGT poses+depth (single forward pass) -> Open3D TSDF fusion -> mesh ->
OpenMVS TextureMesh (full-res photos), timed stage by stage. strasbourg-b's 57 full-res frames
(work/strasbourg-b/frames, 1920x1080) are the input -- same source images the existing OpenMVS
package (work/strasbourg-b) was built from, so texcorr.py / qa numbers are directly comparable.

No pipeline code changed. Output work dir mirrors work/<site>/sfm/dense/{sparse,images} so
texdbg/texcorr.py works unmodified against it.
"""

import glob
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import torch

sys.path.insert(0, "/workspace/vggt")
import open3d as o3d
import pycolmap
from vggt.dependency.np_to_pycolmap import batch_np_matrix_to_pycolmap_wo_track
from vggt.models.vggt import VGGT
from vggt.utils.load_fn import load_and_preprocess_images
from vggt.utils.pose_enc import pose_encoding_to_extri_intri

FRAMES_DIR = "/workspace/airtools/work/strasbourg-b/frames"  # 57 full-res (1920x1080) jpgs
OUT = Path("/workspace/exp/fast/strasbourg_vggt_tsdf")
OPENMVS_BIN = "/workspace/tools/openmvs/bin"
DEVICE = "cuda"

timings = {}


def tic():
    return time.time()


def log(stage, t0):
    dt = time.time() - t0
    timings[stage] = round(dt, 3)
    print(f"[{stage}] {dt:.3f}s", flush=True)
    return dt


CACHE = OUT / "vggt_raw_cache.npz"


def forward_pass(paths):
    t0 = tic()
    model = VGGT.from_pretrained("facebook/VGGT-1B").to(DEVICE)
    model.eval()
    log("model_load", t0)

    t0 = tic()
    images = load_and_preprocess_images(paths, mode="crop").to(DEVICE)  # (N,3,Hm,Wm)
    log("preprocess", t0)
    Hm, Wm = images.shape[-2:]
    print(f"model input resolution: {Wm}x{Hm}", flush=True)

    t0 = tic()
    # vggt/models/vggt.py's own forward() runs the aggregator under bf16 autocast but wraps
    # camera_head/depth_head in `torch.cuda.amp.autocast(enabled=False)` (fp32) -- confirmed by
    # reading VGGT.forward() directly. Running the heads under bf16 (as a first attempt here did)
    # produced NaN depth (exp() activation overflow) -- matching the reference call sequence exactly.
    with torch.no_grad():
        with torch.autocast(device_type="cuda", dtype=torch.bfloat16):
            aggregated_tokens_list, ps_idx = model.aggregator(images[None])  # add batch dim
        with torch.autocast(device_type="cuda", enabled=False):
            pose_enc = model.camera_head(aggregated_tokens_list)[-1]
            extrinsic, intrinsic = pose_encoding_to_extri_intri(pose_enc, images.shape[-2:])
            depth_map, depth_conf = model.depth_head(aggregated_tokens_list, images[None], ps_idx)
    torch.cuda.synchronize()
    extrinsic = extrinsic.squeeze(0).float().cpu().numpy()  # (N,3,4) cam-from-world
    intrinsic = intrinsic.squeeze(0).float().cpu().numpy()  # (N,3,3), in Wm x Hm space
    depth_map = depth_map.squeeze(0).float().cpu().numpy()  # (N,Hm,Wm,1) or (N,Hm,Wm)
    depth_conf = depth_conf.squeeze(0).float().cpu().numpy()
    log("vggt_forward_poses_depth", t0)
    peak_gb = torch.cuda.max_memory_allocated() / 1024**3
    print(f"peak VRAM: {peak_gb:.2f} GB", flush=True)

    if depth_map.ndim == 4:
        depth_map = depth_map[..., 0]
    if depth_conf.ndim == 4:
        depth_conf = depth_conf[..., 0]

    rgb_lowres = (images.permute(0, 2, 3, 1).float().cpu().numpy() * 255).astype(np.uint8)
    np.savez(
        CACHE,
        extrinsic=extrinsic,
        intrinsic=intrinsic,
        depth_map=depth_map,
        depth_conf=depth_conf,
        rgb_lowres=rgb_lowres,
        Hm=Hm,
        Wm=Wm,
    )
    print(f"cached raw VGGT output -> {CACHE}", flush=True)
    return extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres, Hm, Wm


MAX_FRAMES = 48  # hfbox's GPU is shared with another agent this session -- stay well under the
# 16GB card's ceiling (p1-gpu-rocm.md measured 48 frames at 9.07GB solo) to be a good neighbor.


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    paths = sorted(glob.glob(f"{FRAMES_DIR}/*.jpg"))
    if len(paths) > MAX_FRAMES:
        idx = sorted(set(np.linspace(0, len(paths) - 1, MAX_FRAMES).round().astype(int).tolist()))
        paths = [paths[i] for i in idx]
    names = [Path(p).name for p in paths]
    n = len(paths)
    print(
        f"{n} full-res frames from {FRAMES_DIR} (evenly subsampled to MAX_FRAMES={MAX_FRAMES})",
        flush=True,
    )

    if CACHE.exists() and "--refresh" not in sys.argv:
        print(
            f"loading cached VGGT output from {CACHE} (pass --refresh to force a fresh forward pass)",
            flush=True,
        )
        c = np.load(CACHE)
        extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres = (
            c["extrinsic"],
            c["intrinsic"],
            c["depth_map"],
            c["depth_conf"],
            c["rgb_lowres"],
        )
        Hm, Wm = int(c["Hm"]), int(c["Wm"])
    else:
        extrinsic, intrinsic, depth_map, depth_conf, rgb_lowres, Hm, Wm = forward_pass(paths)

    print(
        f"depth stats: min={depth_map.min():.4f} max={depth_map.max():.4f} "
        f"mean={depth_map.mean():.4f} nan={np.isnan(depth_map).sum()} "
        f"conf min/max/mean={depth_conf.min():.3f}/{depth_conf.max():.3f}/{depth_conf.mean():.3f}",
        flush=True,
    )

    # ---- 4. TSDF fusion (Open3D) from VGGT's own low-res depth + the same resized RGB tensor ----
    # NOTE: open3d==0.20.0's ScalableTSDFVolume.integrate() was confirmed hands-on (debug_tsdf3/4.py)
    # to silently produce 0 points/vertices on *any* input in this pip install -- reproduced even on
    # a trivial synthetic flat plane at identity extrinsic (a case that must work if the API is
    # functioning). UniformTSDFVolume (same package, bounded-volume variant) passed that same
    # synthetic test (9801 pts). Root cause not pinned down further (time-boxed); using
    # UniformTSDFVolume for the real fusion below -- a real, if under-documented, Open3D pip-wheel
    # bug, not a VGGT data problem (confirmed separately: per-frame unprojected points landed within
    # noise of the frame's own depth values, i.e. pose+intrinsics+depth are self-consistent).
    t0 = tic()
    depth_valid = (depth_conf > np.percentile(depth_conf, 40)) & (
        depth_map > 1e-6
    )  # drop the least-confident 40% (sky etc.) and non-positive depth
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
    resolution = 320  # 320^3 voxels, ~170MB TSDF+weight+color -- fine for a "fast preview" budget
    voxel = extent / resolution
    sdf_trunc = voxel * 6
    print(
        f"median depth {med_depth:.3f}, bbox extent {extent:.3f}, voxel {voxel:.5f}, "
        f"sdf_trunc {sdf_trunc:.5f}",
        flush=True,
    )
    print(f"mean valid-pixel fraction per frame: {depth_valid.mean():.3f}", flush=True)

    def fuse() -> "o3d.geometry.TriangleMesh":
        volume = o3d.pipelines.integration.UniformTSDFVolume(
            length=extent,
            resolution=resolution,
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
            extr_4x4[:3, :4] = extrinsic[i]  # cam_from_world -- confirmed correct hands-on
            volume.integrate(rgbd, intr_o3d, extr_4x4)
        return volume.extract_triangle_mesh()

    t0 = tic()
    o3d_mesh = fuse()
    o3d_mesh.compute_vertex_normals()
    log("tsdf_fuse_and_extract", t0)
    print(f"TSDF mesh: {len(o3d_mesh.vertices)} verts, {len(o3d_mesh.triangles)} tris", flush=True)
    if len(o3d_mesh.triangles) == 0:
        print(
            "TSDF produced an empty mesh under both extrinsic conventions -- stopping here.",
            flush=True,
        )
        sys.exit(2)

    t0 = tic()
    # drop tiny disconnected junk (sky/noise blobs) before handing to OpenMVS
    tri_clusters, cluster_n_tris, _ = o3d_mesh.cluster_connected_triangles()
    tri_clusters = np.asarray(tri_clusters)
    cluster_n_tris = np.asarray(cluster_n_tris)
    keep = cluster_n_tris[tri_clusters] > (cluster_n_tris.max() * 0.02)
    o3d_mesh.remove_triangles_by_mask(~keep)
    o3d_mesh.remove_unreferenced_vertices()
    mesh_path = OUT / "tsdf_mesh.ply"
    o3d.io.write_triangle_mesh(str(mesh_path), o3d_mesh)
    log("mesh_cleanup_export", t0)
    print(
        f"cleaned TSDF mesh: {len(o3d_mesh.vertices)} verts, {len(o3d_mesh.triangles)} tris",
        flush=True,
    )

    # ---- 5. build a pycolmap Reconstruction at FULL image resolution for OpenMVS texturing -----
    # points3D: an *empty* points3D.bin made InterfaceCOLMAP crash (no error text, ~45s CPU then
    # exit 1, right after "Reading points") -- confirmed hands-on, reproduced 3x (direct write, and
    # again after adding the undistort step run.py's real pipeline always does). Populating a real
    # (if throwaway) sparse point cloud from VGGT's own depth maps -- ~150 valid pixels/frame,
    # unprojected to world -- avoids whatever zero-points code path OpenMVS's binary crashes on.
    t0 = tic()
    rng = np.random.default_rng(0)
    pts3d_list, xyf_list, rgb_list = [], [], []
    for i in range(n):
        ys, xs = np.where(depth_valid[i])
        if len(ys) == 0:
            continue
        pick = rng.choice(len(ys), size=min(150, len(ys)), replace=False)
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
    points3d = np.concatenate(pts3d_list, axis=0)
    points_xyf = np.concatenate(xyf_list, axis=0).astype(np.float64)
    points_rgb = np.concatenate(rgb_list, axis=0)
    print(f"seed point cloud for InterfaceCOLMAP: {len(points3d)} points", flush=True)

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
    # rescale each per-frame camera's intrinsics from (Wm,Hm) model space to the real full-res
    # frame's (W,H) -- same ratio rescale pipeline/vggt.py's rescale_camera does (crop mode has no
    # letterbox offset for this 16:9 footage, confirmed by reading load_and_preprocess_images).
    from PIL import Image as PILImage

    raw_images_dir = OUT / "sfm" / "raw_images"
    raw_images_dir.mkdir(parents=True, exist_ok=True)
    for idx, (image_id, image) in enumerate(sorted(recon.images.items(), key=lambda kv: kv[0])):
        name = names[idx]
        image.name = name
        cam = recon.cameras[image.camera_id]
        with PILImage.open(paths[idx]) as im:
            W, H = im.size
        sx, sy = W / Wm, H / Hm
        fx, fy, cx, cy = cam.params
        cam.params = np.array([fx * sx, fy * sy, cx * sx, cy * sy])
        cam.width, cam.height = W, H
        dst = raw_images_dir / name
        if not dst.exists():
            os.symlink(Path(paths[idx]).resolve(), dst)
    raw_sparse_dir = OUT / "sfm" / "raw_sparse"
    raw_sparse_dir.mkdir(parents=True, exist_ok=True)
    recon.write(str(raw_sparse_dir))
    log("build_colmap_recon_fullres", t0)

    # `pycolmap.undistort_images` (same step run.py's real pipeline always runs, VGGT-fallback or
    # not, p1-toolchain.md pitfall #1) -- rewrites a clean/complete sparse model (incl. rigs.bin,
    # frames.bin the modern COLMAP data model expects) and copies images into dense/{sparse,images}.
    # A manually-built-from-scratch pycolmap.Reconstruction (batch_np_matrix_to_pycolmap_wo_track)
    # written directly, skipping this step, made InterfaceCOLMAP segfault after "Reading points" on
    # this box -- confirmed by testing the direct-write path first (see run.log history); the
    # already-tested real pipeline's own undistort-always convention avoids it.
    t0 = tic()
    dense_dir = OUT / "sfm" / "dense"
    pycolmap.undistort_images(
        output_path=str(dense_dir),
        input_path=str(raw_sparse_dir),
        image_path=str(raw_images_dir),
        output_type="COLMAP",
    )
    sparse_dir = dense_dir / "sparse"
    full_images_dir = dense_dir / "images"
    log("undistort", t0)

    # ---- 6. InterfaceCOLMAP -> scene.mvs (poses + full-res image list only, no dense points) ---
    t0 = tic()
    mvs_dir = OUT / "mvs"
    mvs_dir.mkdir(exist_ok=True)
    cmd = [
        f"{OPENMVS_BIN}/InterfaceCOLMAP",
        "-i",
        str(sparse_dir.parent),  # OpenMVS appends "sparse" itself -- pass dense/, not dense/sparse/
        "-o",
        "scene.mvs",
        "--image-folder",
        str(full_images_dir),
        "-w",
        str(mvs_dir),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, check=False)
    log("interface_colmap", t0)
    if r.returncode != 0:
        print("InterfaceCOLMAP FAILED:\n", r.stdout[-3000:], r.stderr[-3000:], flush=True)
        sys.exit(1)

    # ---- 7. TextureMesh: bake the atlas onto the TSDF mesh from full-res photos, no Densify/Reconstruct ----
    t0 = tic()
    cmd = [
        f"{OPENMVS_BIN}/TextureMesh",
        "-i",
        "scene.mvs",
        "-m",
        str(mesh_path.resolve()),
        "-o",
        "textured.glb",
        "--max-texture-size",
        "4096",
        "--export-type",
        "glb",
        "--global-seam-leveling",
        "0",  # matches pipeline defaults (p1-quality-runs.md: blackens atlas otherwise)
        "--local-seam-leveling",
        "0",
        "-w",
        str(mvs_dir),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, check=False)
    log("texture_mesh", t0)
    if r.returncode != 0:
        print("TextureMesh FAILED:\n", r.stdout[-3000:], r.stderr[-3000:], flush=True)
        sys.exit(1)
    else:
        print(r.stdout[-1500:], flush=True)

    total = sum(timings.values())
    timings["TOTAL"] = round(total, 3)
    (OUT / "timings.json").write_text(json.dumps(timings, indent=2))
    print(json.dumps(timings, indent=2))
    print("DONE", flush=True)


if __name__ == "__main__":
    main()
