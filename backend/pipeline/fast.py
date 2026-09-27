"""Stage A: <1 minute feed-forward preview (plan §1a/§2.2, `docs/research/p1-fast-recon.md`).

Evenly pick <=64 frames (seek-based parallel ffmpeg extraction, native res for texturing + a
downscaled copy for VGGT -- never decodes the whole 4K video) -> VGGT poses + depth, in the VGGT
ROCm env (subprocess, like `pipeline.vggt`) -> Open3D TSDF fusion -> mesh -> seed sparse points so
`InterfaceCOLMAP` works -> undistort -> OpenMVS `TextureMesh` against the full-res frames -> the
SAME calibration path as the full run (`pipeline.run.calibrate_scale`, plan §1a item 1: "don't
duplicate") -> publish revision 1.

The GPU-side work (VGGT forward pass, TSDF fusion -- torch/open3d only live in `AIRTOOLS_VGGT_ENV`)
runs in a separate process, `pipeline/fast_worker.py`, dispatched two ways (`dispatch_vggt_job`):
a warm long-running worker (`python fast_worker.py worker <spool_dir>`, kept alive by hand, e.g. in
a tmux session, so the model loads once and repeated preview runs skip model-load + ROCm
kernel-compile) if one is alive under `work_dir/.vggt_spool`, else a cold one-shot subprocess
(`fast_worker.py once`, mirrors `pipeline.vggt`'s existing per-call convention) -- the cold path
always works, the warm path is a pure speedup.
"""

import json
import logging
import os
import subprocess
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from pipeline import frames, mesh, openmvs, telemetry, vggt
from pipeline.calibrate import choose_scale, similarity_matrix
from pipeline.package import (
    load_or_create_frame_id,
    make_thumbs,
    publish_revision,
    recommended_spawn,
    revision_filenames,
    write_cameras_json,
    write_json_atomic,
)
from pipeline.run import RawPose, calibrate_scale, poses_to_cameras

logger = logging.getLogger(__name__)

_DEFAULT_MAX_FRAMES = 64  # plan §2.2's ceiling, matches vggt.py's own chunk ceiling
_DEFAULT_SMALL_LONG_EDGE = 644  # a bit above VGGT's own ~518px internal resize -- avoids a second
# downscale inside load_and_preprocess_images while staying cheap to seek/decode/upload
_DEFAULT_TARGET_TRIANGLES = 40_000  # plan §1: preview mesh is "~20-50k tris"
_DEFAULT_COLLISION_TRIANGLES = 15_000
_DEFAULT_MAX_TEXTURE_SIZE = 4096  # plan: "let it be smaller for a small mesh" -- TextureMesh's own
# atlas packer already shrinks to fit a coarse mesh's actual UV area, so the cap rarely binds
_WORKER_TIMEOUT_S = 180.0
_HEARTBEAT_MAX_AGE_S = 20.0  # a `fast_worker.py worker` touches this every 5s (see fast_worker.py)
_JOB_POLL_INTERVAL_S = 0.5

_WORKER_SCRIPT = Path(__file__).resolve().parent / "fast_worker.py"


# --- frame selection + seek-based parallel extraction (plan: "never decode the whole 4K video") -


def select_preview_frame_times(
    duration_s: float, fps: float, max_frames: int = _DEFAULT_MAX_FRAMES
) -> list[float]:
    """Evenly spaced video timestamps across the clip, endpoints included (small margin off each
    end so a seek doesn't land past EOF). Frame count is `min(max_frames, video's own frame
    count)` -- never requests more distinct timestamps than the source actually has."""
    if duration_s <= 0:
        raise ValueError("select_preview_frame_times: duration_s must be positive")
    available = max(2, round(duration_s * fps)) if fps > 0 else max_frames
    n = max(1, min(max_frames, available))
    if n == 1:
        return [duration_s / 2.0]
    margin = min(0.1, duration_s / (4 * n))
    lo, hi = margin, max(duration_s - margin, margin)
    return [float(t) for t in np.linspace(lo, hi, n)]


@dataclass
class PreviewFrame:
    id: str  # "p0001" etc -- "p" prefix keeps this namespace disjoint from Stage B's plain
    # numeric frame ids (both stages write thumbs into the same shared out_dir/thumbs/)
    t_s: float
    full_path: Path
    small_path: Path


def extract_preview_frames(
    video: Path,
    work_dir: Path,
    *,
    max_frames: int = _DEFAULT_MAX_FRAMES,
    fps: float,
    duration_s: float,
    small_long_edge: int = _DEFAULT_SMALL_LONG_EDGE,
    max_workers: int = 8,
    force: bool = False,
) -> list[PreviewFrame]:
    """Seek-based, parallel single-frame extraction (`frames.extract_frame_at`, one ffmpeg process
    per frame per resolution, run concurrently via a thread pool -- ffmpeg subprocesses release the
    GIL while running) at `select_preview_frame_times`'s timestamps -- never a single continuous
    decode pass over the whole video (`frames.extract_frames`'s own `fps=` filter would do exactly
    that). Writes native-res frames to `work_dir/frames_full/` (for texturing, and reused by
    `calibrate_scale`'s ArUco path -- same directory name it already expects) and downscaled copies
    to `work_dir/frames_small/` (for VGGT). Resumable: skips a frame already on disk unless
    `force`."""
    full_dir = work_dir / "frames_full"
    small_dir = work_dir / "frames_small"
    full_dir.mkdir(parents=True, exist_ok=True)
    small_dir.mkdir(parents=True, exist_ok=True)

    times = select_preview_frame_times(duration_s, fps, max_frames)
    result = []
    to_extract = []
    for i, t in enumerate(times, start=1):
        fid = f"p{i:04d}"
        full_path, small_path = full_dir / f"{fid}.jpg", small_dir / f"{fid}.jpg"
        result.append(PreviewFrame(id=fid, t_s=t, full_path=full_path, small_path=small_path))
        if force or not full_path.exists():
            to_extract.append(("full", t, full_path))
        if force or not small_path.exists():
            to_extract.append(("small", t, small_path))

    if to_extract:
        with ThreadPoolExecutor(max_workers=max_workers) as pool:
            futures = [
                pool.submit(
                    frames.extract_frame_at,
                    video,
                    path,
                    t,
                    small_long_edge if kind == "small" else None,
                )
                for kind, t, path in to_extract
            ]
            for f in futures:
                f.result()  # surface any ffmpeg failure now, not on first use of a missing file
    return result


def preview_known_distance(known_distance_json: Path | None, frame_names: set[str]) -> Path | None:
    """`--known-distance-json` for the preview's calibration, or None when fewer than 2 of its
    observations name a preview frame. The JSON clicks the full stage's SfM frames (`0012.jpg`),
    which the preview (`p0001.jpg`) never has, and `known_distance_scale` raises on that -- so the
    preview skips this cue (falls back to its other scale sources) instead of crashing the run."""
    if known_distance_json is None:
        return None
    obs = json.loads(Path(known_distance_json).read_text()).get("observations", [])
    if sum(o.get("frame") in frame_names for o in obs) >= 2:
        return known_distance_json
    logger.warning(
        "fast: --known-distance-json names no preview frames (it clicks the full stage's SfM "
        "frames) -- the preview skips it; the full stage uses it"
    )
    return None


# --- VGGT dispatch: warm worker (spool dir) or cold one-shot subprocess ------------------------


def _spool_dir(work_root: Path) -> Path:
    return Path(work_root) / ".vggt_spool"


def worker_is_alive(work_root: Path, max_age_s: float = _HEARTBEAT_MAX_AGE_S) -> bool:
    """A `fast_worker.py worker` process serving `work_root/.vggt_spool` is alive if it touched
    its heartbeat file recently -- survives a killed/crashed worker leaving stale spool files
    behind (falls back to the cold path instead of hanging on a dead worker)."""
    heartbeat = _spool_dir(work_root) / "heartbeat"
    if not heartbeat.exists():
        return False
    return (time.time() - heartbeat.stat().st_mtime) <= max_age_s


def _wait_for_result(
    result_path: Path, timeout_s: float, poll_interval_s: float = _JOB_POLL_INTERVAL_S
) -> dict:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if result_path.exists():
            try:
                return json.loads(result_path.read_text())
            except (json.JSONDecodeError, OSError):
                pass  # writer hasn't finished its atomic rename yet -- keep polling
        time.sleep(poll_interval_s)
    raise TimeoutError(f"dispatch_vggt_job: no result at {result_path} after {timeout_s}s")


def dispatch_vggt_job(
    job: dict,
    work_root: Path,
    *,
    python_bin: Path,
    vggt_repo: Path | None = None,
    timeout_s: float = _WORKER_TIMEOUT_S,
) -> dict:
    """Run `job` (schema: see `fast_worker.process_job`'s docstring) via a warm worker if one's
    alive under `work_root/.vggt_spool` (skips model load + ROCm kernel-compile -- plan: "keep the
    model warm"), else a cold one-shot subprocess (`fast_worker.py once`) -- the cold path always
    works, matching `pipeline.vggt`'s existing per-call subprocess convention. Returns the job's
    `timings_s` dict; raises `RuntimeError` if the job itself failed."""
    if worker_is_alive(work_root):
        spool = _spool_dir(work_root)
        spool.mkdir(parents=True, exist_ok=True)
        job_id = uuid.uuid4().hex
        job_path = spool / f"{job_id}.job.json"
        result_path = spool / f"{job_id}.result.json"
        write_json_atomic(job_path, json.dumps({**job, "result_path": str(result_path)}))
        try:
            result = _wait_for_result(result_path, timeout_s)
        finally:
            job_path.unlink(missing_ok=True)
            result_path.unlink(missing_ok=True)
    else:
        result = _run_cold(job, python_bin=python_bin, vggt_repo=vggt_repo)

    if result.get("status") != "ok":
        raise RuntimeError(f"dispatch_vggt_job: VGGT worker job failed: {result.get('error')}")
    return result["timings_s"]


def _run_cold(job: dict, *, python_bin: Path, vggt_repo: Path | None) -> dict:
    vggt_repo = (
        Path(vggt_repo)
        if vggt_repo is not None
        else Path(os.environ.get("AIRTOOLS_VGGT_REPO", "/workspace/vggt"))
    )
    work_dir = Path(job["work_dir"])
    work_dir.mkdir(parents=True, exist_ok=True)
    job_path = work_dir / "job.json"
    result_path = work_dir / "result.json"
    write_json_atomic(job_path, json.dumps({**job, "result_path": str(result_path)}))

    env = os.environ.copy()
    env.setdefault("TORCH_HOME", "/workspace/.cache/torch")  # p1-gpu-rocm.md: default cache is
    # outside /workspace and re-downloads VGGT-1B's 4.68GB checkpoint every container reset
    env.setdefault("MIOPEN_USER_DB_PATH", "/workspace/.cache/miopen")
    env.setdefault("MIOPEN_CUSTOM_CACHE_DIR", "/workspace/.cache/miopen")
    cmd = [
        str(python_bin),
        str(_WORKER_SCRIPT),
        "--vggt-repo",
        str(vggt_repo),
        "once",
        str(job_path),
    ]

    t0 = time.monotonic()
    result = subprocess.run(cmd, capture_output=True, text=True, env=env, check=False)
    elapsed = time.monotonic() - t0
    if result.returncode != 0:
        tail = "\n".join(((result.stdout or "") + (result.stderr or "")).splitlines()[-40:])
        raise RuntimeError(
            f"fast: cold VGGT worker failed (exit {result.returncode}); log tail:\n{tail}"
        )
    if not result_path.exists():
        raise RuntimeError(f"fast: cold VGGT worker exited 0 but wrote no {result_path}")
    data = json.loads(result_path.read_text())
    data.setdefault("timings_s", {})["cold_subprocess_total_s"] = elapsed
    return data


# --- orchestration --------------------------------------------------------------------------


def run_preview(
    video: Path,
    site: str,
    out_dir: Path,
    work_dir: Path,
    *,
    max_frames: int = _DEFAULT_MAX_FRAMES,
    small_long_edge: int = _DEFAULT_SMALL_LONG_EDGE,
    target_triangles: int = _DEFAULT_TARGET_TRIANGLES,
    collision_triangles: int = _DEFAULT_COLLISION_TRIANGLES,
    max_texture_size: int = _DEFAULT_MAX_TEXTURE_SIZE,
    board: str = "auto",
    board_marker_m: float | None = None,
    board_gap_m: float | None = None,
    board_cols: int | None = None,
    board_rows: int | None = None,
    board_distance_m: float | None = None,
    known_distance_json: Path | None = None,
    known_height_m: float | None = None,
    srt_path: Path | None = None,
    force: bool = False,
    threads: int | None = None,
    python_bin: Path | None = None,
    vggt_repo: Path | None = None,
    worker_timeout_s: float = _WORKER_TIMEOUT_S,
) -> dict:
    """Stage A (plan §1a/§2.2): frame selection+extraction -> VGGT+TSDF+TextureMesh (via
    `dispatch_vggt_job`) -> the same calibration path as Stage B (`pipeline.run.calibrate_scale`)
    -> publish revision 1. Records per-step timings and `preview_ready_s` (video-on-disk -> scene
    .json published). No-op (returns `{"skipped": ...}`) if `AIRTOOLS_VGGT_ENV` isn't set -- the
    preview is only available on a box with the VGGT ROCm venv (plan: "cold path must still work
    without the worker" implies the whole preview is itself optional; Stage B always works)."""
    video, out_dir, work_dir = Path(video), Path(out_dir), Path(work_dir)
    preview_work = work_dir / "preview"
    t_start = time.monotonic()
    timings: dict[str, float] = {}

    python_bin = python_bin or vggt.env_python()
    if python_bin is None:
        logger.warning(
            "fast: AIRTOOLS_VGGT_ENV not set -- Stage A preview unavailable on this box, "
            "skipping (Stage B will define the frame, revision 1)"
        )
        return {"skipped": "AIRTOOLS_VGGT_ENV not set", "timings_s": {}}

    preview_work.mkdir(parents=True, exist_ok=True)

    t0 = time.monotonic()
    probe_info = frames.probe(video)
    preview_frames = extract_preview_frames(
        video,
        preview_work,
        max_frames=max_frames,
        fps=probe_info["fps"],
        duration_s=probe_info["duration"],
        small_long_edge=small_long_edge,
        force=force,
    )
    timings["extract_frames_s"] = time.monotonic() - t0
    frame_times = {pf.id: pf.t_s for pf in preview_frames}
    write_json_atomic(preview_work / "frame_times.json", json.dumps(frame_times, indent=2))

    job = {
        "small_image_paths": [str(pf.small_path) for pf in preview_frames],
        "full_image_paths": [str(pf.full_path) for pf in preview_frames],
        "names": [f"{pf.id}.jpg" for pf in preview_frames],
        "work_dir": str(preview_work / "vggt"),
        "out_textured_glb": str(preview_work / "textured.glb"),
        "out_raw_poses_json": str(preview_work / "raw_poses.json"),
        "openmvs_bin_dir": str(openmvs.ensure_installed()),
        "max_texture_size": max_texture_size,
    }
    raw_poses_path = Path(job["out_raw_poses_json"])
    t0 = time.monotonic()
    if force or not raw_poses_path.exists() or not Path(job["out_textured_glb"]).exists():
        vggt_timings = dispatch_vggt_job(
            job, work_dir, python_bin=python_bin, vggt_repo=vggt_repo, timeout_s=worker_timeout_s
        )
        timings.update({f"vggt_{k}": v for k, v in vggt_timings.items()})
    else:
        logger.info("fast: %s already has VGGT/TextureMesh output, skipping", preview_work)
    timings["vggt_and_texture_total_s"] = time.monotonic() - t0

    raw_poses_data = json.loads(raw_poses_path.read_text())
    raw_poses = [
        RawPose(
            name=r["name"],
            R_wc=np.array(r["R_wc"]),
            t_wc=np.array(r["t_wc"]),
            fx=r["fx"],
            fy=r["fy"],
            cx=r["cx"],
            cy=r["cy"],
            w=r["w"],
            h=r["h"],
        )
        for r in raw_poses_data
    ]
    cam_positions_raw = np.array([-p.R_wc.T @ p.t_wc for p in raw_poses])
    cam_to_world_rotations = np.array([p.R_wc.T for p in raw_poses])

    # --- the same calibration path as the full run (plan §1a item 1: "don't duplicate") ---------
    # The caption track is what makes the preview metric on GPS-less footage (caption altitude);
    # without it the preview published in VGGT's arbitrary units and the room grew ~1.45x at the
    # swap to the metric full revision (DJI_0095). Demuxing it costs ~1 s even for a 1 GB clip.
    t0 = time.monotonic()
    if srt_path is not None:
        srt_file = Path(srt_path) if Path(srt_path).exists() else None
    else:
        srt_file = frames.extract_srt(video, preview_work / "telemetry.srt")
    records = telemetry.parse_srt(srt_file.read_text()) if srt_file else []
    timings["telemetry_s"] = time.monotonic() - t0

    t0 = time.monotonic()
    calib = calibrate_scale(
        video=video,
        work_dir=preview_work,
        raw_poses=raw_poses,
        cam_positions_raw=cam_positions_raw,
        cam_to_world_rotations=cam_to_world_rotations,
        records=records,
        extract_fps=1.0,
        start_s=None,  # unused: frame_timestamps overrides the fixed-fps formula below
        probe_info=probe_info,
        board=board,
        board_marker_m=board_marker_m,
        board_gap_m=board_gap_m,
        board_cols=board_cols,
        board_rows=board_rows,
        board_distance_m=board_distance_m,
        known_distance_json=preview_known_distance(
            known_distance_json, {f"{pf.id}.jpg" for pf in preview_frames}
        ),
        known_height_m=known_height_m,
        force=force,
        frame_timestamps=frame_times,
    )
    timings["calibrate_s"] = time.monotonic() - t0
    align_matrix = calib.align_matrix
    scale_estimates = calib.scale_estimates

    # --- mesh post-process (decimate to the preview budget, JPEG texture, collision) -------------
    t0 = time.monotonic()
    out_dir.mkdir(parents=True, exist_ok=True)
    frame_id = load_or_create_frame_id(work_dir, site)
    filenames = revision_filenames(1)
    mesh_stats, mesh_scale = mesh.process_mesh(
        Path(job["out_textured_glb"]),
        out_dir / filenames["mesh"],
        out_dir / filenames["collision"],
        align_matrix=align_matrix,
        known_height_m=calib.known_height_for_mesh,
        target_triangles=target_triangles,
        collision_triangles=collision_triangles,
        max_texture_size=max_texture_size,
    )
    timings["mesh_postprocess_s"] = time.monotonic() - t0
    scale_estimates.append(mesh_scale)
    final_scale = choose_scale(scale_estimates)

    cameras_matrix = similarity_matrix(mesh_scale.scale, np.eye(3), np.zeros(3)) @ align_matrix
    cameras = poses_to_cameras(raw_poses, cameras_matrix)

    t0 = time.monotonic()
    make_thumbs([Path(p) for p in job["full_image_paths"]], out_dir / "thumbs")
    timings["thumbs_s"] = time.monotonic() - t0

    write_cameras_json(cameras, out_dir / filenames["cameras"])
    spawn = recommended_spawn(np.array(mesh_stats.bbox), cameras)

    # never regress a currently-published full revision back to "preview" -- a re-run of Stage A
    # alone (e.g. `--stages preview` after `--stages preview,full` already landed) still recomputes
    # everything above (so the report/timings are real) but skips the publish.
    from pipeline.run import (
        _read_scene_json,
    )  # local import: avoids a module-level cycle with run.py

    existing_scene = _read_scene_json(out_dir)
    if existing_scene is not None and existing_scene.get("quality") == "full" and not force:
        logger.info(
            "fast: %s already has a published full revision -- skipping preview publish (would "
            "regress quality); recomputed preview output stays in %s for inspection",
            out_dir,
            preview_work,
        )
    else:
        publish_revision(
            out_dir,
            revision=1,
            quality="preview",
            name=site,
            scale=final_scale,
            gravity_residual_deg=calib.grav_residual_deg,
            frame_id=frame_id,
            aligned_to_preview=False,
            alignment_residual_m=None,
            origin_gps=calib.origin_gps,
            north=calib.north_field,
            spawn=spawn,
            mesh_stats={
                "triangles": mesh_stats.triangles,
                "texture_px": mesh_stats.texture_px,
                "collision_triangles": mesh_stats.collision_triangles,
            },
            scale_candidates=scale_estimates,
        )
    timings["preview_ready_s"] = time.monotonic() - t_start

    report = {
        "site": site,
        "video": str(video),
        "revision": 1,
        "quality": "preview",
        "frame_id": frame_id,
        "num_frames": len(preview_frames),
        "scale_method": final_scale.method,
        "scale": final_scale.scale,
        "scale_residual_m": final_scale.residual_m,
        "mesh": {
            "triangles": mesh_stats.triangles,
            "texture_px": mesh_stats.texture_px,
            "collision_triangles": mesh_stats.collision_triangles,
        },
        "timings_s": timings,
    }
    write_json_atomic(preview_work / "report.json", json.dumps(report, indent=2))
    return report
