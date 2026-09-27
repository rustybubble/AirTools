"""VGGT fallback pose estimator (plan §2.5 point 3, §2.6): when pycolmap's SfM fails outright or
registers too few frames, run VGGT's `demo_colmap.py` (no-BA path only -- `docs/research/
p1-gpu-rocm.md` §1.4's verdict: fast, 100% registration, comfortable VRAM; `--use_ba` OOMs below
~16 frames on hfbox's 16GB card and isn't used) in its own ROCm-GPU venv, producing a COLMAP sparse
model that the rest of the pipeline (undistort -> OpenMVS) consumes exactly like a pycolmap
reconstruction -- `run_vggt` writes `sfm_work/sparse/0` + `sfm_work/sfm_meta.json` in the same
layout `pipeline.sfm.run_sfm` does, so `pipeline.sfm.undistort` doesn't need to know which one ran.

The venv only exists on hfbox (`pipeline/tools/setup_gpu_envs.sh` builds it at
`/workspace/envs/vggt`); `AIRTOOLS_VGGT_ENV` points at it. Unset -> the fallback is disabled, with
a clear log line, not a crash (the CPU dev machine has no ROCm venv and never will).

Chunking: VGGT's global attention over all frame tokens is only confirmed comfortable up to 64
frames at bf16 on hfbox's 16GB card (p1-gpu-rocm.md §1.2: 9.6GB/16GB, 6.4GB headroom) -- more than
that is evenly subsampled down to 64 for the fallback call (`select_chunk`), logged, not silently
dropped.

Resolution: `demo_colmap.py` internally resizes to a 518px square for the model and is *supposed*
to rescale its output intrinsics back to the resolution of whatever's in `{scene_dir}/images/`
before writing `cameras.bin` (confirmed by reading its source: `rename_colmap_recons_and_rescale_
camera`) -- since that's exactly the SfM frame resolution here (the images/ farm is a symlink copy
of `image_dir`), this should already line up. `_rescale_reconstruction` verifies it (compares each
camera's stored width/height against the real file on disk) and rescales if it doesn't, rather than
trusting an upstream implementation detail blindly -- a silent resolution mismatch here would
corrupt every downstream reprojection (undistort, OpenMVS, ArUco).
"""

import json
import logging
import os
import shutil
import subprocess
import time
from pathlib import Path

import numpy as np
import pycolmap
from PIL import Image

from pipeline.sfm import _META_NAME, SfmResult, image_names

logger = logging.getLogger(__name__)

_MAX_FRAMES = 64  # p1-gpu-rocm.md §1.2: comfortable headroom to 64 frames at bf16 on a 16GB card
_MIN_REGISTERED_FRACTION = (
    0.6  # matches sfm._MIN_REGISTERED_FRACTION's "did SfM basically work" bar
)
_LOG_TAIL_LINES = 40


def env_python() -> Path | None:
    """`AIRTOOLS_VGGT_ENV`'s python (e.g. `/workspace/envs/vggt/bin/python`), or `None` if the env
    var isn't set -- the caller's signal that the fallback is unavailable here."""
    env_dir = os.environ.get("AIRTOOLS_VGGT_ENV")
    return Path(env_dir) / "bin" / "python" if env_dir else None


def should_fallback(num_registered: int, num_images: int) -> bool:
    """pycolmap registered under `_MIN_REGISTERED_FRACTION` (60%, same bar `sfm.py`'s own
    incremental-mapping retry uses) of the frames it was given -- SfM "basically failed" this
    footage, not just fragmented it a little."""
    if num_images == 0:
        return True
    return (num_registered / num_images) < _MIN_REGISTERED_FRACTION


def select_chunk(paths: list, max_frames: int = _MAX_FRAMES) -> list:
    """Evenly subsample `paths` to at most `max_frames`, endpoints included -- VGGT's confirmed-
    comfortable ceiling on hfbox's 16GB card. No-op under the ceiling."""
    paths = list(paths)
    n = len(paths)
    if n <= max_frames:
        return paths
    idx = sorted(set(np.linspace(0, n - 1, max_frames).round().astype(int).tolist()))
    return [paths[i] for i in idx]


def rescale_camera(
    fx: float, fy: float, cx: float, cy: float, w: float, h: float, target_w: float, target_h: float
) -> tuple[float, float, float, float, float, float]:
    """Rescale a pinhole camera's intrinsics from its own `(w, h)` resolution to `(target_w,
    target_h)` -- `fx, fy, cx, cy` scale by the same per-axis ratio the image dimensions do (the
    same rule `run.py` applies for full-resolution ArUco detection). Identity if already at
    `target_w, target_h`."""
    sx, sy = target_w / w, target_h / h
    return fx * sx, fy * sy, cx * sx, cy * sy, target_w, target_h


def _rescale_reconstruction(recon: pycolmap.Reconstruction, true_w: int, true_h: int) -> None:
    for camera in recon.cameras.values():
        if camera.width == true_w and camera.height == true_h:
            continue
        params = camera.params  # PINHOLE: [fx, fy, cx, cy]
        fx, fy, cx, cy, w, h = rescale_camera(
            params[0], params[1], params[2], params[3], camera.width, camera.height, true_w, true_h
        )
        logger.warning(
            "vggt: camera %d reported %dx%d, expected %dx%d -- rescaling intrinsics (fx %.1f -> "
            "%.1f)",
            camera.camera_id,
            camera.width,
            camera.height,
            true_w,
            true_h,
            params[0],
            fx,
        )
        camera.params = np.array([fx, fy, cx, cy])
        camera.width, camera.height = int(w), int(h)


_VGGT_CONF_THRES = 0.0  # demo_colmap.py's own default (5.0) filters almost every point on real
# footage -- confirmed hands-on on hfbox (strasbourg-cathedral-spire.mp4): the default left only 21
# points total across 33 frames, far too sparse for OpenMVS's DensifyPointCloud view-selection
# ("reference image N has not enough images in view" for nearly every frame -> 0 images densified,
# empty mesh). conf_thres_value=0/vis_thresh=0 (keep every point VGGT proposes) gave 100k points on
# the same clip and let OpenMVS run -- point *quantity* matters more here than VGGT's own confidence
# filter, since OpenMVS's own robust fusion/graph-cut does its own outlier rejection downstream.
_VGGT_VIS_THRES = 0.0


def run_vggt(
    image_dir: Path,
    sfm_work: Path,
    *,
    python_bin: Path | None = None,
    vggt_repo: Path | None = None,
    max_frames: int = _MAX_FRAMES,
    torch_home: str = "/workspace/.cache/torch",
    conf_thres_value: float = _VGGT_CONF_THRES,
    vis_thresh: float = _VGGT_VIS_THRES,
) -> SfmResult:
    """Run VGGT's `demo_colmap.py` (no-BA, `--shared_camera` -- this pipeline is single-camera
    throughout) against (a `select_chunk`ed subset of) `image_dir`, in the `AIRTOOLS_VGGT_ENV`
    venv. Writes `sfm_work/sparse/0` + `sfm_work/sfm_meta.json`, the exact layout `sfm.run_sfm`
    writes, so `sfm.undistort(sfm_work)` works unchanged regardless of which pose estimator ran.

    `conf_thres_value`/`vis_thresh` default to permissive (0.0, keep everything) rather than
    `demo_colmap.py`'s own defaults -- see `_VGGT_CONF_THRES`'s comment; OpenMVS needs a reasonably
    dense sparse point cloud to select stereo view pairs at all, before it ever gets to its own
    (separate, more robust) outlier fusion.

    Raises `RuntimeError` if `AIRTOOLS_VGGT_ENV` isn't set (call `env_python()` first for a
    friendlier caller-side log line -- this is the low-level runner) or if `demo_colmap.py` itself
    fails.
    """
    image_dir, sfm_work = Path(image_dir), Path(sfm_work)
    python_bin = python_bin or env_python()
    if python_bin is None:
        raise RuntimeError(
            "run_vggt: AIRTOOLS_VGGT_ENV is not set -- the VGGT fallback venv only exists on "
            "hfbox (pipeline/tools/setup_gpu_envs.sh vggt); nothing to run"
        )
    vggt_repo = (
        Path(vggt_repo)
        if vggt_repo is not None
        else Path(os.environ.get("AIRTOOLS_VGGT_REPO", "/workspace/vggt"))
    )

    names = image_names(image_dir)
    if len(names) < 2:
        raise ValueError(f"run_vggt: need >=2 images in {image_dir}, found {len(names)}")
    chosen = select_chunk(names, max_frames)
    if len(chosen) < len(names):
        logger.warning(
            "run_vggt: %d frames exceeds the %d-frame VGGT chunk ceiling -- evenly subsampled to "
            "%d frames for the fallback (p1-gpu-rocm.md §1.2)",
            len(names),
            max_frames,
            len(chosen),
        )

    scene_dir = sfm_work / "vggt_scene"
    images_dir = scene_dir / "images"
    if images_dir.exists():
        shutil.rmtree(images_dir)
    images_dir.mkdir(parents=True)
    for name in chosen:
        (images_dir / name).symlink_to((image_dir / name).resolve())

    with Image.open(image_dir / chosen[0]) as img:
        true_w, true_h = img.size

    env = os.environ.copy()
    env.setdefault("TORCH_HOME", torch_home)  # p1-gpu-rocm.md §1.1.6: default cache is outside
    # /workspace and re-downloads VGGT-1B's 4.68GB checkpoint every container reset otherwise
    cmd = [
        str(python_bin),
        str(vggt_repo / "demo_colmap.py"),
        f"--scene_dir={scene_dir}",
        "--shared_camera",
        "--conf_thres_value",
        str(conf_thres_value),
        "--vis_thresh",
        str(vis_thresh),
    ]

    t0 = time.monotonic()
    result = subprocess.run(cmd, capture_output=True, text=True, env=env, check=False)
    elapsed = time.monotonic() - t0
    log_text = (result.stdout or "") + (result.stderr or "")
    if result.returncode != 0:
        tail = "\n".join(log_text.splitlines()[-_LOG_TAIL_LINES:])
        raise RuntimeError(
            f"run_vggt: demo_colmap.py failed (exit {result.returncode}); log tail:\n{tail}"
        )

    vggt_sparse = scene_dir / "sparse"
    if not (vggt_sparse / "cameras.bin").exists():
        raise RuntimeError(
            f"run_vggt: demo_colmap.py exited 0 but wrote no {vggt_sparse}/cameras.bin"
        )

    sparse0 = sfm_work / "sparse" / "0"
    sparse0.mkdir(parents=True, exist_ok=True)
    for fname in ("cameras.bin", "images.bin", "points3D.bin"):
        shutil.copy2(vggt_sparse / fname, sparse0 / fname)

    recon = pycolmap.Reconstruction(sparse0)
    _rescale_reconstruction(recon, true_w, true_h)
    recon.write(sparse0)

    (sfm_work / _META_NAME).write_text(
        json.dumps(
            {"image_dir": str(image_dir), "mapper_used": "vggt", "timings_s": {"vggt_s": elapsed}}
        )
    )

    try:
        mean_err = recon.compute_mean_reprojection_error()
    except Exception:  # noqa: BLE001 -- no BA -> num_points3D()==0, some pycolmap builds raise
        # rather than return nan for a pointless-track reconstruction
        mean_err = float("nan")

    return SfmResult(
        reconstruction_path=sparse0,
        num_registered=recon.num_reg_images(),
        num_images=len(names),
        mean_reprojection_error=mean_err,
        num_points3d=recon.num_points3D(),
        mapper_used="vggt",
        timings_s={"vggt_s": elapsed},
    )
