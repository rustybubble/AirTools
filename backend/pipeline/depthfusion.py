"""`--densify depthfusion`: monocular depth aligned to the COLMAP sparse points, fused into a TSDF
mesh, instead of OpenMVS `DensifyPointCloud` + `ReconstructMesh` (docs/research/bench/
e3-depth-fusion.md). This side exports the undistorted COLMAP model to `cameras.npz`; the GPU work
runs in `AIRTOOLS_DEPTH_ENV` (ROCm torch + MoGe + open3d) via `depthfusion_worker.py`."""

import json
import logging
import os
import subprocess
import time
from pathlib import Path

import numpy as np
import pycolmap

logger = logging.getLogger(__name__)

_WORKER = Path(__file__).resolve().parent / "depthfusion_worker.py"


def export_cameras(dense_dir: Path, out_npz: Path) -> int:
    """K, cam-from-world 4x4 and every sparse observation's pixel + camera-frame depth, per
    registered image of `dense_dir/sparse` (sorted by name). Returns the image count."""
    recon = pycolmap.Reconstruction(Path(dense_dir) / "sparse")
    images = sorted(recon.images.values(), key=lambda im: im.name)
    xyz = {pid: p.xyz for pid, p in recon.points3D.items()}
    names, ids, Ks, Ts, whs, of, ouv, oz = [], [], [], [], [], [], [], []
    for i, im in enumerate(images):
        cam = recon.cameras[im.camera_id]
        T = np.eye(4)
        T[:3] = im.cam_from_world().matrix()
        pts = [(p.xy, xyz[p.point3D_id]) for p in im.points2D if p.has_point3D()]
        names.append(im.name)
        ids.append(im.image_id)
        Ks.append(cam.calibration_matrix())
        Ts.append(T)
        whs.append((cam.width, cam.height))
        if pts:
            uv = np.array([p[0] for p in pts])
            Xc = np.array([p[1] for p in pts]) @ T[:3, :3].T + T[:3, 3]
            of.append(np.full(len(pts), i))
            ouv.append(uv)
            oz.append(Xc[:, 2])
    np.savez(
        out_npz,
        names=np.array(names),
        image_id=np.array(ids),
        K=np.array(Ks),
        T_cw=np.array(Ts),
        wh=np.array(whs),
        obs_frame=np.concatenate(of),
        obs_uv=np.concatenate(ouv),
        obs_z=np.concatenate(oz),
    )
    return len(names)


def env_python() -> Path | None:
    env = os.environ.get("AIRTOOLS_DEPTH_ENV")
    return Path(env) / "bin" / "python" if env else None


def run_depthfusion(
    dense_dir: Path,
    work_dir: Path,
    *,
    units_per_m: float | None,
    model: str = "moge2",
    align: str = "plane",
    voxel_m: float = 0.01,
    extra_args: tuple[str, ...] = (),
    python_bin: Path | None = None,
    force: bool = False,
    depth_only: bool = False,
) -> tuple[Path, dict]:
    """Returns `(mesh_ply, timings)`; resumable (skips if `work_dir/mesh.ply` exists).
    `depth_only` just fills the depth cache (a prefetch). `units_per_m` is the SfM-units-per-metre from calibration (sets the voxel size); `None` lets
    the worker take it from the metric depth fit itself."""
    work_dir = Path(work_dir).resolve()
    mesh_ply = work_dir / "mesh.ply"
    if mesh_ply.exists() and not force:
        logger.info("depthfusion: %s already exists, skipping", mesh_ply)
        return mesh_ply, {}
    python_bin = python_bin or env_python()
    if python_bin is None:
        raise RuntimeError("depthfusion: set AIRTOOLS_DEPTH_ENV (ROCm torch + MoGe + open3d venv)")
    work_dir.mkdir(parents=True, exist_ok=True)
    timings: dict[str, float] = {}
    t0 = time.monotonic()
    cams = work_dir / "cameras.npz"
    export_cameras(dense_dir, cams)
    timings["export_cameras_s"] = time.monotonic() - t0
    cmd = [
        str(python_bin),
        str(_WORKER),
        "--cameras", str(cams),
        "--images", str(Path(dense_dir).resolve() / "images"),
        "--out", str(work_dir),
        "--cache", str(work_dir / "depth_cache"),
        "--model", model,
        "--align", align,
        "--voxel-m", str(voxel_m),
        *(["--units-per-m", str(units_per_m)] if units_per_m else []),
        *extra_args,
        *(["--depth-only"] if depth_only else []),
    ]  # fmt: skip
    t0 = time.monotonic()
    r = subprocess.run(cmd, capture_output=True, text=True, check=False)
    (work_dir / "worker.log").write_text((r.stdout or "") + (r.stderr or ""))
    if r.returncode != 0:
        tail = "\n".join(((r.stdout or "") + (r.stderr or "")).splitlines()[-40:])
        raise RuntimeError(f"depthfusion worker failed (exit {r.returncode}):\n{tail}")
    timings["worker_s"] = time.monotonic() - t0
    if depth_only:
        return mesh_ply, timings
    stats = json.loads((work_dir / "stats.json").read_text())
    timings.update({f"worker_{k}": v for k, v in stats["timings_s"].items()})
    return mesh_ply, timings
