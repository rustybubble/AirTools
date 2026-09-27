"""Subprocess wrappers around the OpenMVS v2.4.0 prebuilt CLI (research doc §2.1, §5 PRIMARY):
`InterfaceCOLMAP -> DensifyPointCloud -> ReconstructMesh -> [auto-crop] -> TextureMesh
--export-type glb`. Each OpenMVS step's stdout/stderr goes to `work_dir/logs/<step>.log`; a
nonzero exit raises with the log tail attached (OpenMVS's own message is the useful part, not the
Python traceback). Resumable per step: a step is skipped if its output file already exists, unless
`force=True`.

The one non-OpenMVS step (`--crop`) runs in Python, between `ReconstructMesh` and `TextureMesh`,
on the *untextured* dense mesh -- so `TextureMesh`'s 4096px atlas and `_auto_decimate_ratio`'s
triangle budget are both spent on the cropped subject, not wasted on background it (and the
final `mesh.py::process_mesh` decimation pass afterward) would otherwise have to throw away.
"""

import logging
import os
import subprocess
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import trimesh

from pipeline import mesh as mesh_ops
from pipeline.openmvs import ensure_installed

logger = logging.getLogger(__name__)

_LOG_TAIL_LINES = 40
_MIN_DECIMATE_RATIO = 0.02  # floor against a pathological (near-zero) target/huge-mesh ratio
_CROP_MODES = ("auto", "none", "orbit")
DENSIFY_MODES = ("openmvs", "depthfusion", "hybrid")
# `--densify hybrid` (docs/research/bench/e5-hybrid.md): a cheaper PatchMatch pass than the
# default res-0 one; its cloud plus TSDF vertices where the cloud is empty go to ReconstructMesh
HYBRID_MVS_ARGS = (
    "--resolution-level", "1", "--number-views", "3",
)  # fmt: skip


def _ply_face_count(path: Path) -> int:
    """Triangle count from a PLY file's header (`element face N`) -- enough to size TextureMesh's
    `--decimate` ratio without loading the whole (potentially much larger, pre-decimation) mesh."""
    with open(path, "rb") as f:
        for raw_line in f:
            line = raw_line.decode("ascii", errors="ignore").strip()
            if line.startswith("element face"):
                return int(line.split()[-1])
            if line == "end_header":
                break
    raise ValueError(f"_ply_face_count: no 'element face' line in {path} header")


def _auto_decimate_ratio(mesh_ply: Path, target_triangles: int) -> float:
    """TextureMesh's own `--decimate` is a fixed fraction (its default 0.2 always throws away 80%
    regardless of the mesh's actual size -- crushes a small mesh, barely touches a huge one).
    Compute the fraction that actually lands near `target_triangles` instead, from the
    just-reconstructed mesh's real triangle count (`mesh.py`'s exact-count decimation pass still
    runs afterward -- this only has to get TextureMesh's own pre-refinement decimate in the right
    ballpark so it isn't wastefully texturing triangles about to be thrown away, or crushing detail
    TextureMesh is about to bake in)."""
    face_count = _ply_face_count(mesh_ply)
    if face_count <= 0:
        return 1.0
    ratio = target_triangles / face_count
    return max(_MIN_DECIMATE_RATIO, min(1.0, ratio))


@dataclass
class MvsResult:
    scene_mvs: Path
    dense_mvs: Path
    mesh_ply: Path
    textured_glb: Path
    timings_s: dict[str, float] = field(default_factory=dict)
    densify: str = "openmvs"  # the mode that actually ran (`hybrid` can fall back to `openmvs`)


def _run(cmd: list[str], cwd: Path, log_path: Path) -> float:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    t0 = time.monotonic()
    result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, check=False)
    elapsed = time.monotonic() - t0
    log_text = (result.stdout or "") + (result.stderr or "")
    log_path.write_text(log_text)
    if result.returncode != 0:
        tail = "\n".join(log_text.splitlines()[-_LOG_TAIL_LINES:])
        raise RuntimeError(
            f"{Path(cmd[0]).name} failed (exit {result.returncode}); log tail:\n{tail}"
        )
    return elapsed


def _with_extra(cmd: list[str], extra: tuple[str, ...]) -> list[str]:
    """Append `extra` flags to an OpenMVS command, dropping any `--flag value` pair of `cmd` that
    `extra` sets again (OpenMVS rejects a repeated option). Every flag this module passes takes
    exactly one value."""
    given = {a for a in extra if a.startswith("--")}
    out, i = [], 0
    while i < len(cmd):
        if cmd[i] in given:
            i += 2
            continue
        out.append(cmd[i])
        i += 1
    return out + list(extra)


def _step(output: Path, force: bool, run_fn) -> float:
    """Skip `run_fn()` (and return 0.0) if `output` already exists and `force` is False --
    resumability for the slowest, most crash-prone stage (`DensifyPointCloud` especially)."""
    if output.exists() and not force:
        logger.info("mvs: %s already exists, skipping", output.name)
        return 0.0
    return run_fn()


def run_mvs(
    dense_dir: Path,
    work_dir: Path,
    *,
    resolution_level: int = 1,
    number_views: int = 3,
    densify: str = "hybrid",
    units_per_m: float | None = None,
    densify_args: tuple[str, ...] = (),
    mesh_args: tuple[str, ...] = (),
    texture_args: tuple[str, ...] = (),
    decimate: float | None = None,
    target_triangles: int = 200_000,
    texture_resolution_level: int = 0,
    max_texture_size: int = 4096,
    cost_smoothness_ratio: float = 0.1,
    global_seam_leveling: bool = False,
    local_seam_leveling: bool = False,
    crop: str = "none",
    crop_cam_positions: np.ndarray | None = None,
    crop_cam_rotations: np.ndarray | None = None,
    crop_cam_intrinsics: np.ndarray | None = None,
    crop_up: np.ndarray | None = None,
    bin_dir: Path | None = None,
    force: bool = False,
    threads: int | None = None,
    on_densified: Callable[[], None] | None = None,
) -> MvsResult:
    """Run the 4-step OpenMVS chain on `dense_dir` (pycolmap's undistorted PINHOLE COLMAP output,
    `sfm.undistort`'s return value). `resolution_level`/`number_views` are `DensifyPointCloud`'s
    two real time-budget levers (research doc §7 pitfall 10); `resolution_level` defaults to
    `1` and `number_views` to `3` (bench E1, docs/research/bench/e1-experiments.md §4): on the
    kitchen clip that is 3.2x faster end to end than `0`/`5` with higher held-out coverage, PSNR and
    SSIM; level 0 leaves holes on dark, untextured surfaces. An earlier note here blamed level 1 for
    speckled textures; that speckle was the QA renderer (fixed in `34cc339`), not the mesh.
    `decimate` is the pre-texture
    fractional decimation `TextureMesh --decimate` applies (0..1, `1` = disabled -- the exact-count
    pass is still `mesh.py`'s job afterward); default `None` computes it from `target_triangles`
    against the mesh `ReconstructMesh` actually produced (`_auto_decimate_ratio`) instead of
    OpenMVS's fixed-fraction default, which crushes a small mesh and barely touches a huge one
    (quality-tuning finding, research doc: the default 0.2 starved a 142m spire of detail).
    `texture_resolution_level` defaults to `0` (full-res photos) -- texture quality is what people
    actually see; it's independent of `resolution_level`'s *dense point cloud* budget.
    `densify_args`/`mesh_args`/`texture_args` are extra raw flags appended to `DensifyPointCloud`/
    `ReconstructMesh`/`TextureMesh`
    (e.g. `--sub-resolution-levels 1`), for sweeps; a flag this function already passes is replaced.
    `cost_smoothness_ratio` is OpenMVS's own default. Seam leveling is OFF, unlike OpenMVS's
    default: in the v2.4.0 prebuilt binary it wrecks the atlas (patches go black with colour
    blobs). Measured on strasbourg, texture-vs-photo colour correlation per view: off 0.78-0.94,
    local only ~0.22, global only ~0.02 (research doc p1-quality-runs.md). `threads` defaults to
    `os.cpu_count()`; lower it on a memory-constrained box -- each DensifyPointCloud thread holds
    its own depth-map working set.

    `crop` (`"auto"|"none"|"orbit"`) runs `mesh.py`'s auto-crop on `ReconstructMesh`'s raw output,
    before `TextureMesh` -- default `"none"` here (this function has no camera data of its own);
    the pipeline-level default is `"auto"`, set by `run.py`/the CLI, which also supplies the
    `crop_cam_*`/`crop_up` arrays this needs (raw, un-aligned reconstruction-frame camera poses --
    `crop_cam_positions` `(N,3)`, `crop_cam_rotations` `(N,3,3)` world-to-camera R, `crop_cam_
    intrinsics` `(N,6)` fx/fy/cx/cy/w/h, `crop_up` `(3,)`; see `mesh.crop_to_subject_auto`). If
    `crop != "none"` but that data isn't given, the crop step is skipped with a warning rather than
    raising -- callers that don't care about cropping (most existing tests, `bin_dir=...` smoke
    runs) don't have to supply it.

    `densify="depthfusion"` replaces `DensifyPointCloud` + `ReconstructMesh` with
    `pipeline.depthfusion` (mono depth aligned to the sparse points, TSDF-fused; `densify_args`
    then go to `depthfusion_worker.py`, `units_per_m` sets its voxel size) and textures against
    `scene.mvs`. The crop and texture steps are unchanged. `densify="hybrid"` runs
    `DensifyPointCloud` with `HYBRID_MVS_ARGS` (ignoring `resolution_level`/`number_views`) while
    the worker's depth inference runs on the GPU, then the worker fuses the OpenMVS depth maps with
    the mono depth and writes `union.ply` (the OpenMVS cloud plus TSDF vertices in its holes), which
    `ReconstructMesh` meshes. `hybrid` is the default (bench E1 §6: 0.996 held-out coverage vs
    0.777 for plain OpenMVS at the same wall time); if `AIRTOOLS_DEPTH_ENV` is unset or the depth
    worker fails it falls back to plain OpenMVS with a warning, reusing the hybrid's own
    `DensifyPointCloud` pass. `MvsResult.densify` records the mode that actually ran.

    `on_densified`, if given, is called once the OpenMVS depth maps (`depth*.dmap` in `work_dir`)
    exist, so a caller can start work on them while the mesh steps run (the structure layer)."""
    # `.resolve()` matters: `_run` below sets `cwd=work_dir` for each OpenMVS subprocess, so any
    # *relative* dense_dir/work_dir (e.g. a relative --work on the CLI) would otherwise get
    # re-interpreted relative to that new cwd instead of the caller's cwd -- confirmed the hard way
    # (InterfaceCOLMAP: "unable to open file 'work/<site>/sfm/dense/sparse/cameras.bin'", doubled
    # under work/<site>/mvs/ because the arg strings built below were relative).
    dense_dir, work_dir = Path(dense_dir).resolve(), Path(work_dir).resolve()
    work_dir.mkdir(parents=True, exist_ok=True)
    bin_dir = Path(bin_dir) if bin_dir is not None else ensure_installed()
    logs_dir = work_dir / "logs"
    threads = str(threads if threads is not None else (os.cpu_count() or 1))

    timings: dict[str, float] = {}
    scene_mvs = work_dir / "scene.mvs"
    dense_mvs = work_dir / "scene_dense.mvs"
    mesh_ply = work_dir / "scene_dense_mesh.ply"
    textured_glb = work_dir / "textured.glb"

    timings["interface_colmap_s"] = _step(
        scene_mvs,
        force,
        lambda: _run(
            [
                str(bin_dir / "InterfaceCOLMAP"),
                "-i",
                str(dense_dir),
                "-o",
                str(scene_mvs),
                "--image-folder",
                str(dense_dir / "images"),
                "--max-threads",
                threads,
            ],
            work_dir,
            logs_dir / "interface_colmap.log",
        ),
    )

    if densify not in DENSIFY_MODES:
        raise ValueError(f"densify must be one of {DENSIFY_MODES}, got {densify!r}")
    if densify == "hybrid":
        from pipeline import depthfusion

        if depthfusion.env_python() is None:
            logger.warning(
                "mvs: --densify hybrid needs AIRTOOLS_DEPTH_ENV (MoGe-2 depth venv); it is not "
                "set, so falling back to plain OpenMVS densify"
            )
            densify = "openmvs"
    if densify in ("depthfusion", "hybrid"):
        from pipeline import depthfusion

        t0 = time.monotonic()
        df_dir, worker_args = work_dir / "depthfusion", densify_args
        if densify == "hybrid":
            # mono/multi-view depth on the GPU while OpenMVS PatchMatch runs on the CPU
            def _prefetch_depth() -> None:
                try:
                    depthfusion.run_depthfusion(
                        dense_dir,
                        df_dir,
                        units_per_m=units_per_m,
                        extra_args=densify_args,
                        depth_only=True,
                    )
                except (RuntimeError, OSError) as e:  # the fuse call below retries, then falls back
                    logger.warning("mvs: depth prefetch failed: %s", str(e).splitlines()[0])

            prefetch = threading.Thread(target=_prefetch_depth)
            prefetch.start()
            timings["densify_mvs_s"] = _step(
                dense_mvs,
                force,
                lambda: _run(
                    [
                        str(bin_dir / "DensifyPointCloud"),
                        str(scene_mvs),
                        "-o", str(dense_mvs),
                        *HYBRID_MVS_ARGS,
                        "--max-threads", threads,
                    ],
                    work_dir,
                    logs_dir / "densify.log",
                ),
            )  # fmt: skip
            prefetch.join()
            if on_densified:  # the OpenMVS depth maps exist from here on
                on_densified()
            worker_args = (
                "--mvs-depth", str(work_dir),
                "--mvs-cloud", str(work_dir / "scene_dense.ply"),
                *densify_args,
            )  # fmt: skip
        try:
            df_mesh, df_timings = depthfusion.run_depthfusion(
                dense_dir,
                df_dir,
                units_per_m=units_per_m,
                extra_args=worker_args,
                force=force,
            )
        except (RuntimeError, OSError) as e:
            if densify != "hybrid":
                raise
            logger.warning(
                "mvs: hybrid depth fill failed (%s); falling back to plain OpenMVS on the "
                "DensifyPointCloud pass that already ran",
                str(e).splitlines()[0],
            )
            densify, df_mesh = "openmvs", None
        timings["densify_s"] = time.monotonic() - t0
    if densify in ("depthfusion", "hybrid"):
        mesh_ply = df_mesh
        timings.update({f"depthfusion_{k}": v for k, v in df_timings.items()})
        dense_mvs = scene_mvs  # TextureMesh reads cameras from here; there's no dense cloud
        union = mesh_ply.parent / "union.ply"  # worker `--mvs-cloud`: mesh it with graph-cut
        if union.exists():
            union_mesh = work_dir / "union_mesh.ply"
            timings["reconstruct_mesh_s"] = _step(
                union_mesh,
                force,
                lambda: _run(
                    _with_extra(
                        [
                            str(bin_dir / "ReconstructMesh"),
                            "-i",
                            str(scene_mvs),
                            "-p",
                            str(union),
                            "-o",
                            str(union_mesh),
                            "--max-threads",
                            threads,
                        ],
                        mesh_args,
                    ),
                    work_dir,
                    logs_dir / "reconstruct_mesh.log",
                ),
            )
            mesh_ply = union_mesh
    else:
        # after a hybrid fallback the DensifyPointCloud pass already exists: reuse it, even on force
        timings["densify_s"] = timings.get("densify_s", 0.0) + _step(
            dense_mvs,
            force and "densify_mvs_s" not in timings,
            lambda: _run(
                _with_extra(
                    [
                        str(bin_dir / "DensifyPointCloud"),
                        str(scene_mvs),
                        "-o",
                        str(dense_mvs),
                        "--resolution-level",
                        str(resolution_level),
                        "--number-views",
                        str(number_views),
                        "--max-threads",
                        threads,
                    ],
                    densify_args,
                ),
                work_dir,
                logs_dir / "densify.log",
            ),
        )
        if on_densified:
            on_densified()

        timings["reconstruct_mesh_s"] = _step(
            mesh_ply,
            force,
            lambda: _run(
                _with_extra(
                    [
                        str(bin_dir / "ReconstructMesh"),
                        str(dense_mvs),
                        "-o",
                        str(mesh_ply),
                        "--max-threads",
                        threads,
                    ],
                    mesh_args,
                ),
                work_dir,
                logs_dir / "reconstruct_mesh.log",
            ),
        )

    # Auto-crop (Python, not an OpenMVS binary): trims ReconstructMesh's raw output to the subject
    # *before* TextureMesh spends its atlas/decimate budget on it. `texture_source_ply` is what
    # TextureMesh actually reads -- the cropped ply if cropping ran and produced one, else the
    # uncropped `mesh_ply` (crop="none", or crop requested without camera data, or the crop itself
    # emptied out -- `mesh.py`'s crop functions never return an empty mesh, but this stays a plain
    # fallback rather than assuming that).
    cropped_mesh_ply = work_dir / "scene_dense_mesh_cropped.ply"
    texture_source_ply = mesh_ply

    if crop not in _CROP_MODES:
        raise ValueError(f"crop must be one of {_CROP_MODES}, got {crop!r}")

    def _crop_mesh() -> float:
        if crop_cam_positions is None or crop_up is None:
            logger.warning("mvs: crop=%r requested but no camera poses given, skipping", crop)
            return 0.0
        t0 = time.monotonic()
        loaded = trimesh.load(mesh_ply, process=False, force="mesh")
        if crop == "orbit":
            forwards = np.asarray(crop_cam_rotations)[:, 2, :]  # camera +Z (forward) in world
            cropped = mesh_ops.crop_to_subject_orbit(loaded, crop_cam_positions, forwards)
        else:  # "auto"
            cropped = mesh_ops.crop_to_subject_auto(
                loaded, crop_cam_positions, crop_cam_rotations, crop_cam_intrinsics, crop_up
            )
        cropped.export(cropped_mesh_ply)
        return time.monotonic() - t0

    if crop != "none":
        timings["crop_mesh_s"] = _step(cropped_mesh_ply, force, _crop_mesh)
        if cropped_mesh_ply.exists():
            texture_source_ply = cropped_mesh_ply

    # TextureMesh needs the *scene* (camera poses, `-i`) and the *mesh* (`-m`) separately --
    # `ReconstructMesh` only ever writes the mesh geometry file, never a re-usable `.mvs` project
    # for it (confirmed hands-on: `-o <name>.mvs` on `ReconstructMesh` silently still writes a
    # `.ply`, there is no mesh-carrying `.mvs` to pass positionally here).
    def _texture_mesh_cmd() -> list[str]:
        # computed lazily (only when this step actually runs, i.e. isn't resumed/skipped) --
        # `texture_source_ply` may only hold a placeholder in tests, and there's no reason to read
        # a multi-million-triangle PLY's header just to then skip the step anyway.
        eff_decimate = (
            decimate
            if decimate is not None
            else _auto_decimate_ratio(texture_source_ply, target_triangles)
        )
        return _with_extra(
            [
                str(bin_dir / "TextureMesh"),
                "-i",
                str(dense_mvs),
                "-m",
                str(texture_source_ply),
                "-o",
                str(textured_glb),
                "--decimate",
                str(eff_decimate),
                "--resolution-level",
                str(texture_resolution_level),
                "--max-texture-size",
                str(max_texture_size),
                "--cost-smoothness-ratio",
                str(cost_smoothness_ratio),
                "--global-seam-leveling",
                str(int(global_seam_leveling)),
                "--local-seam-leveling",
                str(int(local_seam_leveling)),
                "--export-type",
                "glb",
                "--max-threads",
                threads,
            ],
            texture_args,
        )

    timings["texture_mesh_s"] = _step(
        textured_glb,
        force,
        lambda: _run(_texture_mesh_cmd(), work_dir, logs_dir / "texture_mesh.log"),
    )

    return MvsResult(
        scene_mvs=scene_mvs,
        dense_mvs=dense_mvs,
        mesh_ply=mesh_ply,
        textured_glb=textured_glb,
        timings_s=timings,
        densify=densify,
    )
