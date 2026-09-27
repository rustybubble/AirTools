"""End-to-end CLI pipeline (assignment §7, plan §1a/§2.2): video -> scene package, in two stages.
Each stage's own output on disk is its resume marker -- rerunning skips whatever's already there
unless `force=True`.

`run()` dispatches `--stages` (default `"preview,full"`): Stage A (`pipeline.fast.run_preview`,
<1 min target) publishes revision 1 BEFORE Stage B starts, so a watcher can pick it up; Stage B
(`_run_full`, this module's original body) is the existing chain -- probe -> extract frames (+
sharpest-per-window selection) -> SRT telemetry if present (GPS scale via `calibrate.scale_from_
gps`, which also gives up/north) else `calibrate.up_from_cameras` -> SfM -> undistort ->
calibration (needs only the SfM poses, computed here before OpenMVS so its raw up/camera-pose
estimate is ready for `mvs.run_mvs`'s auto-crop step) -> OpenMVS (dense + crop + texture) -> mesh
post-process with calibration -> rigid-align onto the preview if one is published (plan §1a) ->
revision-named publish -> validate_package. Writes `work_dir/report.json` with timings, counts,
residuals.
"""

import json
import logging
import time
from dataclasses import dataclass, replace
from pathlib import Path

import numpy as np
import pycolmap

from pipeline import aruco, frames, mesh, mvs, qa, sfm, telemetry, vggt
from pipeline import structure as structure_layer
from pipeline.calibrate import (
    ScaleEstimate,
    align_to_gltf,
    apply_similarity,
    choose_scale,
    interpolate_positions_at_times,
    known_distance_scale,
    scale_agreement,
    scale_from_caption_altitude,
    scale_from_caption_odometry,
    scale_from_gps,
    similarity_matrix,
    transform_camera,
    umeyama,
    up_from_cameras,
)
from pipeline.package import (
    Camera,
    load_or_create_frame_id,
    make_thumbs,
    publish_revision,
    recommended_spawn,
    revision_filenames,
    validate_package,
    write_cameras_json,
    write_json_atomic,
)

logger = logging.getLogger(__name__)

# QA renders/compares each id it's given (preview.render is ~seconds/frame, but not free) -- cap
# an auto-wired run()'s QA pass so it doesn't multiply wall time by the frame count. A full sweep
# is `pipeline qa` run by hand afterward.
_AUTO_QA_MAX_FRAMES = 12

_VALID_STAGES = ("preview", "full")
_DEFAULT_STAGES = "preview,full"
_DEFAULT_PREVIEW_FRAMES = 48

# Minimum finite (resampled) GPS fixes before trusting `calibrate.scale_from_gps`'s similarity fit
# -- matches `calibrate._MIN_UMEYAMA_POINTS`, the fit's own hard minimum. Below this, skip the GPS
# path cleanly (fall back to `up_from_cameras`) rather than let `scale_from_gps` raise.
_MIN_GPS_FIXES = 3
# The GPS track must also span several of its own position steps, or the Umeyama fit's scale, up
# and north are arbitrary. The DJI Mini 4K caption (FC7703) writes lon/lat to 4 decimals (~10 m),
# so a close pass around one building sits on 2-3 distinct positions (gt-lcc DJI_0097). GPS is
# good to 1-3 m at best, hence the 1 m floor on the step.
_MIN_GPS_EXTENT_STEPS = 5.0
_MIN_GPS_STEP_M = 1.0


def _gps_step_m(records: list) -> float:
    """The caption's horizontal GPS resolution in metres: the smallest nonzero change of lat or
    lon between consecutive fixes, floored at `_MIN_GPS_STEP_M`."""
    ll = np.array([(r.lat, r.lon) for r in records], dtype=np.float64)
    ll = ll[np.all(np.isfinite(ll), axis=1)]
    d = np.abs(np.diff(ll, axis=0))
    steps = np.radians(np.concatenate([d[:, 0], d[:, 1] * np.cos(np.radians(ll[0, 0]))]))
    steps = steps[steps > 1e-12] * telemetry._WGS84_RADIUS_M
    return max(_MIN_GPS_STEP_M, float(steps.min()) if steps.size else 0.0)


@dataclass
class RawPose:
    """One camera's pose+intrinsics straight out of a reconstruction, before calibration.
    `R_wc`/`t_wc`: world-to-camera, `x_cam = R_wc @ x_world + t_wc` (OpenCV convention, matches
    `package.Camera`)."""

    name: str
    R_wc: np.ndarray
    t_wc: np.ndarray
    fx: float
    fy: float
    cx: float
    cy: float
    w: int
    h: int


def poses_to_cameras(
    raw_poses: list[RawPose], align_matrix: np.ndarray, thumb_dir: str = "thumbs"
) -> list[Camera]:
    """Reconstruction-space poses -> `package.Camera`, after `align_matrix` (calibration
    similarity + glTF up-alignment, `calibrate.similarity_matrix`) re-expresses the world.
    `calibrate.transform_camera` keeps each pose's rotation exactly orthonormal."""
    cameras = []
    for raw in raw_poses:
        R_new, t_new = transform_camera(raw.R_wc, raw.t_wc, align_matrix)
        stem = Path(raw.name).stem
        cameras.append(
            Camera(
                id=stem,
                file=f"frames/{raw.name}",
                thumb=f"{thumb_dir}/{stem}.jpg",
                R=R_new,
                t=t_new,
                fx=raw.fx,
                fy=raw.fy,
                cx=raw.cx,
                cy=raw.cy,
                w=raw.w,
                h=raw.h,
            )
        )
    return cameras


def gravity_residual_deg(cam_to_world_rotations: np.ndarray, up: np.ndarray) -> float:
    """RMS degrees the cameras' right axes deviate from perpendicular to `up` -- how much the
    "roll ~= 0" assumption `calibrate.up_from_cameras` relies on actually held across the flight
    (0 for a perfectly level orbit)."""
    right_axes = np.asarray(cam_to_world_rotations, dtype=np.float64)[:, :, 0]
    dots = np.clip(np.abs(right_axes @ np.asarray(up, dtype=np.float64)), 0.0, 1.0)
    angles_deg = np.degrees(np.arcsin(dots))
    return float(np.sqrt(np.mean(angles_deg**2)))


def _frame_timestamp(name: str, fps: float, start_s: float | None) -> float:
    """Extracted frames are named `%04d.jpg` at a fixed `fps` (`frames.extract_frames`) -- frame
    N's video timestamp is `(N-1)/fps` offset by wherever `-ss` started."""
    index = int(Path(name).stem)
    return (index - 1) / fps + (start_s or 0.0)


def _raw_poses_from_reconstruction(recon: pycolmap.Reconstruction) -> list[RawPose]:
    poses = []
    for image in recon.images.values():
        cfw = image.cam_from_world()
        cam = image.camera
        poses.append(
            RawPose(
                name=image.name,
                R_wc=cfw.rotation.matrix(),
                t_wc=np.asarray(cfw.translation, dtype=np.float64),
                fx=cam.focal_length_x,
                fy=cam.focal_length_y,
                cx=cam.principal_point_x,
                cy=cam.principal_point_y,
                w=cam.width,
                h=cam.height,
            )
        )
    return sorted(poses, key=lambda p: p.name)


def _run_sfm_with_vggt_fallback(
    image_dir: Path,
    sfm_work: Path,
    *,
    mapper: str,
    num_threads: int,
    camera_model: str = "SIMPLE_RADIAL",
) -> sfm.SfmResult:
    """`sfm.run_sfm`, with a VGGT fallback (plan §2.5 point 3, `pipeline.vggt`) when pycolmap
    registers too few frames or raises outright, or when `mapper == "vggt"` forces it directly
    (`run`'s `--mapper vggt`, for testing the fallback on demand). Falls back to whatever pycolmap
    produced, with a warning, when `AIRTOOLS_VGGT_ENV` isn't set -- the ROCm venv only exists on
    hfbox; if pycolmap raised outright and there's nothing to fall back to, the original exception
    propagates rather than silently continuing with no poses at all.
    """
    if mapper == "vggt":
        return vggt.run_vggt(image_dir, sfm_work)

    try:
        sfm_result = sfm.run_sfm(
            image_dir,
            sfm_work,
            matcher="sequential",
            overlap=10,
            camera_model=camera_model,
            single_camera=True,
            mapper=mapper,
            num_threads=num_threads,
        )
    except Exception:
        if vggt.env_python() is None:
            raise
        logger.exception("run: pycolmap SfM raised -- falling back to VGGT")
        return vggt.run_vggt(image_dir, sfm_work)

    if vggt.should_fallback(sfm_result.num_registered, sfm_result.num_images):
        fraction = sfm_result.num_registered / max(sfm_result.num_images, 1)
        if vggt.env_python() is None:
            logger.warning(
                "run: pycolmap registered only %d/%d frames (%.0f%%) but AIRTOOLS_VGGT_ENV is not "
                "set -- VGGT fallback disabled, keeping pycolmap's result",
                sfm_result.num_registered,
                sfm_result.num_images,
                fraction * 100,
            )
            return sfm_result
        logger.warning(
            "run: pycolmap registered only %d/%d frames (%.0f%%) -- falling back to VGGT",
            sfm_result.num_registered,
            sfm_result.num_images,
            fraction * 100,
        )
        return vggt.run_vggt(image_dir, sfm_work)
    return sfm_result


def _resolve_board_specs(
    board: str,
    marker_m: float | None,
    gap_m: float | None,
    cols: int | None,
    rows: int | None,
    distance_m: float | None,
) -> list:
    """`--board {auto,none,board,scalebar}` -> the `aruco.Spec`(s) to try, in order. `"auto"`
    (default) tries `BoardSpec` then `ScaleBarSpec` -- whichever's actually in frame wins, silently
    skipping the other; `"board"`/`"scalebar"` force one. Size overrides (`None` = that spec's own
    default) apply to whichever spec(s) get built."""
    board_spec = aruco.BoardSpec()
    if marker_m is not None:
        board_spec = replace(board_spec, marker_length_m=marker_m)
    if gap_m is not None:
        board_spec = replace(board_spec, gap_m=gap_m)
    if cols is not None:
        board_spec = replace(board_spec, cols=cols)
    if rows is not None:
        board_spec = replace(board_spec, rows=rows)

    bar_spec = aruco.ScaleBarSpec()
    if marker_m is not None:
        bar_spec = replace(bar_spec, marker_length_m=marker_m)
    if distance_m is not None:
        bar_spec = replace(bar_spec, center_distance_m=distance_m)

    return {
        "board": [board_spec],
        "scalebar": [bar_spec],
        "none": [],
        "auto": [board_spec, bar_spec],
    }[board]


def _full_res_cameras(
    video: Path,
    work_dir: Path,
    raw_poses: list[RawPose],
    full_w: int,
    full_h: int,
    extract_fps: float,
    start_s: float | None,
    force: bool,
    frame_timestamps: dict[str, float] | None = None,
) -> tuple[Path, list[Camera]]:
    """Extract a native-resolution frame at each registered pose's own timestamp into
    `work_dir/frames_full/` (skipped if already there, unless `force`) and build full-resolution
    `Camera`s for ArUco detection -- SfM frames are downscaled to `--long-edge`, but marker
    decoding needs every real pixel (plan §2's "detect on full-resolution frames"). Intrinsics
    rescale by the per-axis ratio the extraction does (`full_w/raw.w`, `full_h/raw.h` -- normally
    equal, computed separately in case of rounding); pose (`R`/`t`) is resolution-independent.

    `frame_timestamps` (id -> video timestamp_s), if given, overrides the `(extract_fps, start_s)`
    fixed-fps formula -- `pipeline.fast`'s preview stage picks frames at arbitrary, non-fixed-fps
    timestamps (`fast.select_preview_frame_times`) and already extracted them at those exact
    timestamps into a `frames_full/` of its own, so this both computes the *same* timestamp (no
    mismatch) and, since the file already exists there, skips re-extracting it entirely."""
    frames_full_dir = work_dir / "frames_full"
    cameras = []
    to_extract: list[tuple[float, Path]] = []
    for raw in raw_poses:
        full_path = frames_full_dir / raw.name
        ts = (
            frame_timestamps[Path(raw.name).stem]  # frame_times.json/Camera.id are both stems
            # (no extension, e.g. "p0001") -- matches poses_to_cameras' own `stem = Path(raw.name)
            # .stem` convention, which `_align_full_to_preview` also relies on downstream
            if frame_timestamps is not None
            else _frame_timestamp(raw.name, extract_fps, start_s)
        )
        if force or not full_path.exists():
            to_extract.append((ts, full_path))
        fx_scale, fy_scale = full_w / raw.w, full_h / raw.h
        cameras.append(
            Camera(
                id=raw.name,
                file=raw.name,
                thumb="",
                R=raw.R_wc,
                t=raw.t_wc,
                fx=raw.fx * fx_scale,
                fy=raw.fy * fy_scale,
                cx=raw.cx * fx_scale,
                cy=raw.cy * fy_scale,
                w=full_w,
                h=full_h,
            )
        )
    frames.extract_frames_at(video, to_extract)  # one decode pass, same frames as per-pose seeks
    return frames_full_dir, cameras


def _aruco_scale(
    frames_full_dir: Path, full_cameras: list[Camera], specs: list
) -> ScaleEstimate | None:
    """Try each spec in order, returning the first hit -- `None` (never raises) if the board/bar
    wasn't seen in enough views under any spec (`aruco.estimate_board_scale`'s own contract), or if
    detection itself raised (a corrupt/unreadable extracted frame shouldn't fail the whole run)."""
    for spec in specs:
        try:
            estimate = aruco.estimate_board_scale(frames_full_dir, full_cameras, spec)
        except Exception:
            logger.exception("run: aruco detection raised for spec %r, skipping", spec.name)
            continue
        if estimate is not None:
            return estimate
    return None


@dataclass
class CalibrationResult:
    align_matrix: np.ndarray
    scale_estimates: list[ScaleEstimate]
    known_height_for_mesh: float | None
    up_for_residual: np.ndarray
    north_field: list[float] | None
    origin_gps: list[float] | None
    grav_residual_deg: float
    board_normal_gravity_angle_deg: float | None


def calibrate_scale(
    *,
    video: Path,
    work_dir: Path,
    raw_poses: list[RawPose],
    cam_positions_raw: np.ndarray,
    cam_to_world_rotations: np.ndarray,
    records: list,
    extract_fps: float,
    start_s: float | None,
    probe_info: dict,
    board: str,
    board_marker_m: float | None,
    board_gap_m: float | None,
    board_cols: int | None,
    board_rows: int | None,
    board_distance_m: float | None,
    known_distance_json: Path | None,
    known_height_m: float | None,
    force: bool,
    frame_timestamps: dict[str, float] | None = None,
) -> CalibrationResult:
    """Everything between raw SfM poses and the final `align_matrix` (plan §2.3): up/rotation (GPS
    Umeyama fit, or the roll~=0 fallback), every available metric scale source in order of
    evidence (GPS, caption altitude/odometry, ArUco board/scale-bar, known-distance-json), and the
    winning similarity transform. Pulled out of `run()` so the scale-source competition (plan item
    3/5) is unit-testable with a synthetic ArUco rig + mocks, without a real video/SfM/OpenMVS run.
    Shared by both pipeline stages (plan §1a item 1: "don't duplicate") -- `pipeline.fast`'s
    preview stage calls this too, passing its own `frame_timestamps` (its frames aren't at a fixed
    `extract_fps` grid, see `_full_res_cameras`) and the same caption records, so a GPS-less
    preview is metric from the caption altitude too.

    `records` may have GPS-less entries (`telemetry.parse_srt`'s NaN lat/lon, e.g. flown indoors
    with no fix): the GPS Umeyama fit only runs with `_MIN_GPS_FIXES`+ finite fixes, cleanly
    falling back to `up_from_cameras` otherwise; the caption-based "altitude"/"odometry" scale
    candidates run independently of GPS fix state (they use H/H.S, not lat/lon).
    """
    scale_estimates: list[ScaleEstimate] = []

    def _timestamp(name: str) -> float:
        return (
            frame_timestamps[Path(name).stem]  # keyed by stem ("p0001"), like _full_res_cameras
            if frame_timestamps is not None
            else _frame_timestamp(name, extract_fps, start_s)
        )

    # registered frames' filenames are numbered in the dense (`extract_fps`) sequence, not the
    # nominal `fps` -- must use the fps that actually matches their on-disk numbering.
    times_s = [_timestamp(p.name) for p in raw_poses] if records else []
    resampled = telemetry.resample(records, times_s) if records else []
    enu = telemetry.to_enu(resampled) if records else None
    # only lat/lon (columns 0/1, East/North) indicate an actual GPS fix -- Up (column 2) comes
    # from H (barometer/VPS), which reports with or without one (telemetry.to_enu's own contract).
    finite_gps = (
        int(np.count_nonzero(np.all(np.isfinite(enu[:, :2]), axis=1)))
        if enu is not None and len(enu)
        else 0
    )

    used_gps_path = bool(records) and finite_gps >= _MIN_GPS_FIXES
    if used_gps_path:
        extent_m = float(np.hypot(*np.ptp(enu[np.all(np.isfinite(enu[:, :2]), axis=1), :2], 0)))
        step_m = _gps_step_m(records)
        if extent_m < _MIN_GPS_EXTENT_STEPS * step_m:
            logger.warning(
                "run: the GPS track spans %.1f m at a %.1f m position step (need %.0fx) -- too "
                "coarse to fit, skipping the GPS scale path",
                extent_m,
                step_m,
                _MIN_GPS_EXTENT_STEPS,
            )
            used_gps_path = False
    if used_gps_path:
        s_gps, R_gps, _t_gps, residual = scale_from_gps(cam_positions_raw, enu)
        scale_estimates.append(
            ScaleEstimate(
                "gps",
                s_gps,
                residual,
                notes=f"camera path fit to GPS ENU track ({finite_gps}/{len(enu)} frames had a fix)",
            )
        )
        # fix, confirmed via TypeError: align_to_gltf's params are up_vec/north_vec, not up/north --
        # this call has always raised whenever `records` was truthy (never exercised: no corpus
        # clip has telemetry, plan §2.6), caught while wiring calibrate_scale's own GPS-path tests.
        R_gltf = align_to_gltf(np.array([0.0, 0.0, 1.0]), np.array([0.0, 1.0, 0.0]))
        up_for_residual = R_gps.T @ np.array([0.0, 0.0, 1.0])
        north_field = [0.0, 0.0, -1.0]
        gps_fix_records = [r for r in records if np.isfinite(r.lat) and np.isfinite(r.lon)]
        origin_gps = [
            gps_fix_records[0].lat,
            gps_fix_records[0].lon,
            gps_fix_records[0].abs_alt_m or 0.0,
        ]
    else:
        if records and finite_gps < _MIN_GPS_FIXES:
            logger.info(
                "run: %d/%d frame(s) had a finite GPS fix (need >=%d) -- skipping the GPS scale "
                "path, falling back to up_from_cameras",
                finite_gps,
                len(enu) if enu is not None else 0,
                _MIN_GPS_FIXES,
            )
        up = up_from_cameras(cam_to_world_rotations)
        R_gltf = align_to_gltf(up)
        up_for_residual = up
        north_field = None
        origin_gps = None

    grav_residual = gravity_residual_deg(cam_to_world_rotations, up_for_residual)

    # --- caption-based scale candidates (plan/assignment task 2): "altitude" (camera-up-axis fit
    # to H) and "odometry" (H.S-integrated path length vs the reconstruction's own) -- only ever
    # candidates, not requirements: both return None (no crash, no estimate appended) when there's
    # not enough signal (e.g. near-constant H, too little caption/camera data). Independent of
    # whether the GPS path above ran: H/H.S report with or without a GPS fix (bug (b)).
    if records:
        up_cam = cam_positions_raw @ up_for_residual
        h_caption = np.array(
            [r.rel_alt_m if r.rel_alt_m is not None else float("nan") for r in resampled]
        )
        altitude_estimate = scale_from_caption_altitude(up_cam, h_caption)
        if altitude_estimate is not None:
            scale_estimates.append(altitude_estimate)

        raw_t = np.array([r.t_s for r in records], dtype=np.float64)
        raw_hspeed = np.array(
            [r.h_speed_mps if r.h_speed_mps is not None else float("nan") for r in records]
        )
        odometry_estimate = scale_from_caption_odometry(
            np.array(times_s),
            cam_positions_raw,
            up_for_residual,
            raw_t,
            raw_hspeed,
            reference_scale=(altitude_estimate.scale if altitude_estimate is not None else None),
        )
        if odometry_estimate is not None:
            scale_estimates.append(odometry_estimate)

    # --- ArUco board/scale-bar scale (plan §2.1/§2.3), on full-resolution re-extracted frames ---
    board_normal_gravity_angle_deg = None
    if board != "none":
        full_w, full_h = probe_info["width"], probe_info["height"]
        frames_full_dir, full_cameras = _full_res_cameras(
            video,
            work_dir,
            raw_poses,
            full_w,
            full_h,
            extract_fps,
            start_s,
            force,
            frame_timestamps=frame_timestamps,
        )
        specs = _resolve_board_specs(
            board, board_marker_m, board_gap_m, board_cols, board_rows, board_distance_m
        )
        aruco_estimate = _aruco_scale(frames_full_dir, full_cameras, specs)
        if aruco_estimate is not None:
            scale_estimates.append(aruco_estimate)
            if aruco_estimate.board_normal is not None:
                # gravity cross-check only (plan: "don't override up") -- board laid flat means its
                # normal is a second, independent up estimate; sign is ambiguous (a flat board looks
                # the same from either side), so compare the angle between the *lines*, not vectors.
                cos_angle = np.clip(
                    abs(np.dot(aruco_estimate.board_normal, up_for_residual)), 0.0, 1.0
                )
                board_normal_gravity_angle_deg = float(np.degrees(np.arccos(cos_angle)))

    # --- known-distance-json scale: a two-point stand-in for the board -------------------------
    if known_distance_json is not None:
        raw_cameras_by_name = {
            raw.name: Camera(
                id=raw.name,
                file=raw.name,
                thumb="",
                R=raw.R_wc,
                t=raw.t_wc,
                fx=raw.fx,
                fy=raw.fy,
                cx=raw.cx,
                cy=raw.cy,
                w=raw.w,
                h=raw.h,
            )
            for raw in raw_poses
        }
        scale_estimates.append(known_distance_scale(known_distance_json, raw_cameras_by_name))

    # --- apply the winning scale (plan: "the winner's similarity gets applied") ------------------
    # `choose_scale` ranks scalebar_aruco > known_dimension(known-distance-json) > gps -- whichever
    # of those actually produced an estimate above (never "none": only appended on success).  When
    # GPS is present but a *different* method wins, GPS's rotation/translation (north, position) are
    # kept -- only its scale is swapped for the winner's, with the translation re-solved at the new
    # scale so the mean camera position still lands on the GPS ENU track's mean (Umeyama's own
    # `t = mu_dst - s*R@mu_src`, just re-solved for the new `s`).
    early_best = choose_scale(scale_estimates) if scale_estimates else None
    known_height_for_mesh = None if early_best is not None else known_height_m
    if early_best is not None and known_height_m is not None:
        logger.info(
            "run: ignoring --known-height-m, better scale source available: %s", early_best.method
        )
    if not scale_estimates and known_height_m is None:
        logger.warning(
            "run: no GPS telemetry, no ArUco board/scale-bar detected, no --known-height-m/"
            "--known-distance-json given -- scale_method will be 'none' (scale=1.0, NOT metres) "
            "in scene.json"
        )

    if used_gps_path:
        winning_scale = early_best.scale if early_best is not None else s_gps
        mu_src, mu_dst = cam_positions_raw.mean(axis=0), enu.mean(axis=0)
        t_gps_adj = mu_dst - winning_scale * (R_gps @ mu_src)
        gps_M = similarity_matrix(winning_scale, R_gps, t_gps_adj)
        align_matrix = similarity_matrix(1.0, R_gltf, np.zeros(3)) @ gps_M
    else:
        winning_scale = early_best.scale if early_best is not None else 1.0
        align_matrix = similarity_matrix(winning_scale, R_gltf, np.zeros(3))

    return CalibrationResult(
        align_matrix=align_matrix,
        scale_estimates=scale_estimates,
        known_height_for_mesh=known_height_for_mesh,
        up_for_residual=up_for_residual,
        north_field=north_field,
        origin_gps=origin_gps,
        grav_residual_deg=grav_residual,
        board_normal_gravity_angle_deg=board_normal_gravity_angle_deg,
    )


def _parse_stages(stages: str) -> list[str]:
    parsed = [s.strip() for s in stages.split(",") if s.strip()]
    if not parsed:
        raise ValueError("stages: at least one of 'preview'/'full' required, got an empty string")
    unknown = [s for s in parsed if s not in _VALID_STAGES]
    if unknown:
        raise ValueError(f"stages: unknown stage(s) {unknown!r}, expected any of {_VALID_STAGES}")
    return parsed


def _read_scene_json(out_dir: Path) -> dict | None:
    """The currently-published `scene.json`, or `None` if there isn't one / it's unreadable --
    Stage B's signal for whether a preview already published (plan §1a: rigid-align onto it,
    revision 2) or this run defines the frame itself (no preview, revision 1)."""
    path = Path(out_dir) / "scene.json"
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text())
    except (json.JSONDecodeError, OSError):
        return None


def _next_revision_and_frame_id(
    existing_scene: dict | None, work_dir: Path, site: str
) -> tuple[int, str, bool]:
    """Stage B's revision number + `frame.id` (plan §1a): revision 2 rigid-aligned onto a
    currently-published preview, or revision 1 defining the frame itself if there's no preview
    published (e.g. no VGGT env on the laptop, `pipeline.fast.run_preview` skipped itself).
    Returns `(revision, frame_id, needs_alignment)`."""
    if existing_scene is not None and existing_scene.get("quality") == "preview":
        revision = int(existing_scene.get("revision", 1)) + 1
        frame_id = existing_scene.get("frame", {}).get("id") or load_or_create_frame_id(
            work_dir, site
        )
        return revision, frame_id, True
    return 1, load_or_create_frame_id(work_dir, site), False


def _align_full_to_preview(
    work_dir: Path,
    out_dir: Path,
    raw_poses: list[RawPose],
    cam_positions_raw: np.ndarray,
    align_matrix: np.ndarray,
    extract_fps: float,
    start_s: float | None,
    existing_scene: dict,
    full_is_metric: bool = True,
) -> tuple[np.ndarray, float | None, float | None]:
    """Rigid-align (rotation + translation, no scale -- plan §1a) Stage B's own metric camera
    centres onto the currently-published preview's, on camera centres of frames at the same video
    timestamps -- interpolating Stage B's camera path (denser, different frame ids) at the
    preview's exact timestamps (`work_dir/preview/frame_times.json`, written by
    `pipeline.fast.run_preview`). Returns `(new_align_matrix, alignment_residual_m)`; falls back to
    `(align_matrix, None)` (unaligned) with a warning if the preview's timing/camera data isn't on
    disk (e.g. published from a different work_dir, or too few points for a stable fit).

    Rigid only holds when both stages are in metres. Without a scale reference each stage is in its
    own arbitrary units (VGGT and COLMAP differ by 6-80x on the corpus), so a rigid fit can't
    overlay them -- then (`full_is_metric=False`) the full revision takes the preview's units via a
    similarity fit, so the swap stays in place. Also returns the fitted preview-per-full scale, which
    should be ~1.0 when both are metric (plan §1a's 2% check)."""
    preview_times_path = work_dir / "preview" / "frame_times.json"
    cameras_file = existing_scene.get("cameras")
    preview_cameras_path = out_dir / cameras_file if cameras_file else None
    if (
        preview_cameras_path is None
        or not preview_times_path.exists()
        or not preview_cameras_path.exists()
    ):
        logger.warning(
            "run: scene.json says quality=preview but %s/%s is missing -- publishing unaligned "
            "(revision still advances, aligned_to_preview stays False)",
            preview_times_path,
            preview_cameras_path,
        )
        return align_matrix, None, None

    preview_times: dict = json.loads(preview_times_path.read_text())
    preview_cams = json.loads(preview_cameras_path.read_text())
    preview_pos_by_id = {c["id"]: np.array(c["position"], dtype=np.float64) for c in preview_cams}
    ids = [i for i in preview_times if i in preview_pos_by_id]
    if len(ids) < 3:
        logger.warning(
            "run: only %d preview camera(s) with a known timestamp -- skipping rigid alignment "
            "(need >=3 for a stable fit), publishing unaligned",
            len(ids),
        )
        return align_matrix, None, None

    dst = np.array([preview_pos_by_id[i] for i in ids])
    dst_times = np.array([preview_times[i] for i in ids])
    full_times = np.array([_frame_timestamp(p.name, extract_fps, start_s) for p in raw_poses])
    full_positions_aligned = apply_similarity(cam_positions_raw, align_matrix)
    src = interpolate_positions_at_times(full_times, full_positions_aligned, dst_times)

    fit_scale = umeyama(src, dst, with_scale=True)[0]
    s_fix, R_fix, t_fix, residual = umeyama(src, dst, with_scale=not full_is_metric)
    total_matrix = similarity_matrix(s_fix, R_fix, t_fix) @ align_matrix
    return total_matrix, float(residual), float(fit_scale)


def run(
    video: Path,
    site: str,
    out_dir: Path,
    work_dir: Path,
    *,
    stages: str = _DEFAULT_STAGES,
    preview_frames: int = _DEFAULT_PREVIEW_FRAMES,
    start_s: float | None = None,
    end_s: float | None = None,
    fps: float = 2.0,
    long_edge: int = 1920,
    sharpen_window: int = 1,
    srt_path: Path | None = None,
    known_height_m: float | None = None,
    known_distance_json: Path | None = None,
    board: str = "auto",
    board_marker_m: float | None = None,
    board_gap_m: float | None = None,
    board_cols: int | None = None,
    board_rows: int | None = None,
    board_distance_m: float | None = None,
    camera_model: str = "SIMPLE_RADIAL",
    mapper: str = "global",
    resolution_level: int = 1,
    number_views: int = 3,
    densify: str = "hybrid",
    densify_args: tuple[str, ...] = (),
    mesh_args: tuple[str, ...] = (),
    texture_args: tuple[str, ...] = (),
    texture_decimate: float | None = None,
    texture_resolution_level: int = 0,
    target_triangles: int = 200_000,
    collision_triangles: int = 50_000,
    max_texture_size: int = 4096,
    crop: str = "auto",
    holdout_every: int = 5,  # matches qa.py's own _DEFAULT_HOLDOUT_EVERY; 0 disables held-out
    force: bool = False,
    threads: int | None = None,
    structure: bool = True,
) -> dict:
    """Plan §1a/§2.2: Stage A (preview, publishes revision 1) then Stage B (full, `_run_full`) by
    default -- `--stages preview,full` picks either or both; the preview publish always happens
    before Stage B starts, so a watcher polling `scene.json` sees revision 1 while Stage B is still
    running. Resumable per stage exactly as before (each stage's own work dir is its resume
    marker)."""
    video, out_dir, work_dir = Path(video), Path(out_dir), Path(work_dir)
    work_dir.mkdir(parents=True, exist_ok=True)
    stage_list = _parse_stages(stages)

    preview_report = None
    if "preview" in stage_list:
        from pipeline import fast as fast_module  # lazy: avoids a module-level circular import

        # (pipeline.fast imports calibrate_scale/RawPose/poses_to_cameras from this module)
        preview_report = fast_module.run_preview(
            video,
            site,
            out_dir,
            work_dir,
            max_frames=preview_frames,
            board=board,
            board_marker_m=board_marker_m,
            board_gap_m=board_gap_m,
            board_cols=board_cols,
            board_rows=board_rows,
            board_distance_m=board_distance_m,
            known_distance_json=known_distance_json,
            known_height_m=known_height_m,
            srt_path=srt_path,
            force=force,
            threads=threads,
        )

    if "full" not in stage_list:
        return {"site": site, "video": str(video), "stages": stage_list, "preview": preview_report}

    report = _run_full(
        video,
        site,
        out_dir,
        work_dir,
        start_s=start_s,
        end_s=end_s,
        fps=fps,
        long_edge=long_edge,
        sharpen_window=sharpen_window,
        srt_path=srt_path,
        known_height_m=known_height_m,
        known_distance_json=known_distance_json,
        board=board,
        board_marker_m=board_marker_m,
        board_gap_m=board_gap_m,
        board_cols=board_cols,
        board_rows=board_rows,
        board_distance_m=board_distance_m,
        camera_model=camera_model,
        mapper=mapper,
        resolution_level=resolution_level,
        number_views=number_views,
        densify=densify,
        densify_args=densify_args,
        mesh_args=mesh_args,
        texture_args=texture_args,
        texture_decimate=texture_decimate,
        texture_resolution_level=texture_resolution_level,
        target_triangles=target_triangles,
        collision_triangles=collision_triangles,
        max_texture_size=max_texture_size,
        crop=crop,
        holdout_every=holdout_every,
        force=force,
        threads=threads,
        structure=structure,
    )
    report["stages"] = stage_list
    report["preview"] = preview_report
    return report


def _run_full(
    video: Path,
    site: str,
    out_dir: Path,
    work_dir: Path,
    *,
    start_s: float | None = None,
    end_s: float | None = None,
    fps: float = 2.0,
    long_edge: int = 1920,
    sharpen_window: int = 1,
    srt_path: Path | None = None,
    known_height_m: float | None = None,
    known_distance_json: Path | None = None,
    board: str = "auto",
    board_marker_m: float | None = None,
    board_gap_m: float | None = None,
    board_cols: int | None = None,
    board_rows: int | None = None,
    board_distance_m: float | None = None,
    camera_model: str = "SIMPLE_RADIAL",
    mapper: str = "global",
    resolution_level: int = 1,
    number_views: int = 3,
    densify: str = "hybrid",
    densify_args: tuple[str, ...] = (),
    mesh_args: tuple[str, ...] = (),
    texture_args: tuple[str, ...] = (),
    texture_decimate: float | None = None,
    texture_resolution_level: int = 0,
    target_triangles: int = 200_000,
    collision_triangles: int = 50_000,
    max_texture_size: int = 4096,
    crop: str = "auto",
    holdout_every: int = 5,
    force: bool = False,
    threads: int | None = None,
    structure: bool = True,
) -> dict:
    """Stage B (plan §2.2): the full pycolmap/GLOMAP + OpenMVS chain. Computes its own metric
    calibration (`calibrate_scale`, same as Stage A), then, if a preview is currently published in
    `out_dir`, rigid-aligns onto it and publishes revision 2 with `aligned_to_preview: true`; else
    this revision defines the frame itself (revision 1, `aligned_to_preview: false`, plan §1a)."""
    video, out_dir, work_dir = Path(video), Path(out_dir), Path(work_dir)
    work_dir.mkdir(parents=True, exist_ok=True)
    timings: dict[str, float] = {}

    t0 = time.monotonic()
    probe_info = frames.probe(video)
    timings["probe_s"] = time.monotonic() - t0

    # --- frames: held-out QA needs frames SfM never sees (assignment: QA should measure
    # generalisation, not just how well the mesh reproduces its own texture source). Extract
    # `holdout_every`x denser than `fps` into `frames_dir`, then feed SfM only every
    # `holdout_every`-th one (`frames_nominal`) -- at (very nearly) the same video timestamps a
    # plain `--fps fps` extraction would have produced, so SfM sees the same frames either way.
    # The other `frames_dir` frames are genuine held-out candidates: never given to SfM, but on
    # disk for `qa.default_ids`/`interpolate_camera` (already built for exactly this) to score.
    # `holdout_every=0` disables this and extracts at `fps` directly (old behaviour, no held-out).
    frames_dir = work_dir / "frames"
    extract_fps = fps * holdout_every if holdout_every > 0 else fps
    t0 = time.monotonic()
    if force or not any(frames_dir.glob("*.jpg")):
        raw_frame_paths = frames.extract_frames(
            video, frames_dir, fps=extract_fps, long_edge=long_edge, start_s=start_s, end_s=end_s
        )
    else:
        logger.info("run: %s already has frames, skipping extraction", frames_dir)
        raw_frame_paths = sorted(frames_dir.glob("*.jpg"))
    timings["extract_frames_s"] = time.monotonic() - t0

    if holdout_every > 0:
        nominal_dir = work_dir / "frames_nominal"
        nominal_paths = raw_frame_paths[::holdout_every]
        if force or not any(nominal_dir.glob("*.jpg")):
            nominal_dir.mkdir(parents=True, exist_ok=True)
            for old in nominal_dir.glob("*.jpg"):
                old.unlink()
            for path in nominal_paths:
                (nominal_dir / path.name).symlink_to(path.resolve())
    else:
        nominal_dir, nominal_paths = frames_dir, raw_frame_paths

    if sharpen_window > 1:
        image_dir = work_dir / "frames_selected"
        if force or not any(image_dir.glob("*.jpg")):
            kept = frames.select_sharpest(nominal_paths, sharpen_window)
            image_dir.mkdir(parents=True, exist_ok=True)
            for old in image_dir.glob("*.jpg"):
                old.unlink()
            for path in kept:
                (image_dir / path.name).symlink_to(path.resolve())
    else:
        image_dir = nominal_dir

    # --- telemetry -----------------------------------------------------------------------------
    t0 = time.monotonic()
    if srt_path is not None:
        srt_file = Path(srt_path) if Path(srt_path).exists() else None
    else:
        srt_file = frames.extract_srt(video, work_dir / "telemetry.srt")
    records = telemetry.parse_srt(srt_file.read_text()) if srt_file else []
    timings["telemetry_s"] = time.monotonic() - t0

    # --- SfM -------------------------------------------------------------------------------
    sfm_work = work_dir / "sfm"
    sparse_path = sfm_work / "sparse" / "0"
    t0 = time.monotonic()
    if force or not (sparse_path / "cameras.bin").exists():
        sfm_result = _run_sfm_with_vggt_fallback(
            image_dir,
            sfm_work,
            mapper=mapper,
            num_threads=threads if threads is not None else 4,
            camera_model=camera_model,
        )
        sfm_counts = {
            "num_registered": sfm_result.num_registered,
            "num_images": sfm_result.num_images,
            "mean_reprojection_error": sfm_result.mean_reprojection_error,
            "num_points3d": sfm_result.num_points3d,
            "mapper_used": sfm_result.mapper_used,
        }
        timings.update({f"sfm_{k}": v for k, v in sfm_result.timings_s.items()})
    else:
        logger.info("run: %s already reconstructed, skipping SfM", sparse_path)
        recon = pycolmap.Reconstruction(sparse_path)
        mapper_used = "resumed"
        meta_path = sfm_work / "sfm_meta.json"
        if meta_path.exists():
            try:
                mapper_used = json.loads(meta_path.read_text()).get("mapper_used", "resumed")
            except (json.JSONDecodeError, OSError):
                pass
        sfm_counts = {
            "num_registered": recon.num_reg_images(),
            "num_images": len(sfm.image_names(image_dir)),
            "mean_reprojection_error": recon.compute_mean_reprojection_error(),
            "num_points3d": recon.num_points3D(),
            "mapper_used": mapper_used,
        }
    timings["sfm_total_s"] = time.monotonic() - t0

    dense_dir = sfm_work / "dense"
    t0 = time.monotonic()
    if force or not (dense_dir / "images").exists():
        sfm.undistort(sfm_work)
    else:
        logger.info("run: %s already undistorted, skipping", dense_dir)
    timings["undistort_s"] = time.monotonic() - t0

    # --- calibration: GPS scale if we have telemetry, else rotation-only up-alignment -- computed
    # before run_mvs (moved up from after it): the auto-crop step inside run_mvs needs raw camera
    # poses + an "up" estimate to crop in the un-aligned reconstruction frame, before OpenMVS
    # spends its texture atlas/triangle budget on an uncropped mesh. Everything here only needs
    # `dense_dir/sparse` (written by `sfm.undistort` above), not `mvs_result` -- safe to move.
    dense_recon = pycolmap.Reconstruction(dense_dir / "sparse")
    raw_poses = _raw_poses_from_reconstruction(dense_recon)
    cam_positions_raw = np.array([-p.R_wc.T @ p.t_wc for p in raw_poses])
    cam_rotations_raw = np.array([p.R_wc for p in raw_poses])  # world-to-camera R
    cam_to_world_rotations = np.array([p.R_wc.T for p in raw_poses])
    cam_intrinsics_raw = np.array([[p.fx, p.fy, p.cx, p.cy, p.w, p.h] for p in raw_poses])

    calib = calibrate_scale(
        video=video,
        work_dir=work_dir,
        raw_poses=raw_poses,
        cam_positions_raw=cam_positions_raw,
        cam_to_world_rotations=cam_to_world_rotations,
        records=records,
        extract_fps=extract_fps,
        start_s=start_s,
        probe_info=probe_info,
        board=board,
        board_marker_m=board_marker_m,
        board_gap_m=board_gap_m,
        board_cols=board_cols,
        board_rows=board_rows,
        board_distance_m=board_distance_m,
        known_distance_json=known_distance_json,
        known_height_m=known_height_m,
        force=force,
    )
    align_matrix = calib.align_matrix
    scale_estimates = calib.scale_estimates
    known_height_for_mesh = calib.known_height_for_mesh
    up_for_residual = calib.up_for_residual
    north_field = calib.north_field
    origin_gps = calib.origin_gps
    grav_residual = calib.grav_residual_deg
    board_normal_gravity_angle_deg = calib.board_normal_gravity_angle_deg

    # --- revision + rigid-align onto the preview, if one is already published (plan §1a) ---------
    existing_scene = _read_scene_json(out_dir)
    revision, frame_id, needs_alignment = _next_revision_and_frame_id(
        existing_scene, work_dir, site
    )
    preview_match = None
    if needs_alignment:
        # scale_estimates is empty when nothing gave a scale (choose_scale would raise on it)
        full_is_metric = (
            any(e.method != "none" for e in scale_estimates) or known_height_for_mesh is not None
        )
        align_matrix, alignment_residual_m, preview_fit_scale = _align_full_to_preview(
            work_dir,
            out_dir,
            raw_poses,
            cam_positions_raw,
            align_matrix,
            extract_fps,
            start_s,
            existing_scene,
            full_is_metric=full_is_metric,
        )
        aligned_to_preview = alignment_residual_m is not None
        preview_candidates = existing_scene.get("scale_candidates") or []
        preview_match = next(
            (
                c
                for c in preview_candidates
                if c.get("method") == existing_scene.get("scale_method")
            ),
            None,
        )
    else:
        alignment_residual_m = None
        aligned_to_preview = False
        full_is_metric = False
        preview_fit_scale = None

    # --- OpenMVS (each step independently resumable inside run_mvs); auto-crop (`--crop`) runs
    # inside, on the untextured dense mesh, using the raw poses/up computed just above -----------
    mvs_work = work_dir / "mvs"
    metric_estimates = [e for e in scale_estimates if e.method != "none"]
    units_per_m = 1.0 / choose_scale(metric_estimates).scale if metric_estimates else None
    # structure layer (pipeline/structure.py): starts once the OpenMVS depth maps exist, runs
    # beside the mesh steps, joins before the publish; never fails the run
    structure_job, structure_report = structure_layer.prepare(
        structure,
        dense_dir,
        mvs_work,
        work_dir / "structure",
        m_per_unit=1.0 / units_per_m if units_per_m else None,
        up_cameras=work_dir / "structure" / "up_cameras.json",
        force=force,
    )
    if structure_job:  # gravity for S3's Manhattan snap: the scene's +Y, before any mesh scale
        (work_dir / "structure").mkdir(parents=True, exist_ok=True)
        write_cameras_json(
            poses_to_cameras(raw_poses, align_matrix), work_dir / "structure" / "up_cameras.json"
        )
    mvs_result = mvs.run_mvs(
        dense_dir,
        mvs_work,
        resolution_level=resolution_level,
        number_views=number_views,
        densify=densify,
        units_per_m=units_per_m,
        densify_args=densify_args,
        mesh_args=mesh_args,
        texture_args=texture_args,
        decimate=texture_decimate,
        target_triangles=target_triangles,
        texture_resolution_level=texture_resolution_level,
        max_texture_size=max_texture_size,
        crop=crop,
        crop_cam_positions=cam_positions_raw,
        crop_cam_rotations=cam_rotations_raw,
        crop_cam_intrinsics=cam_intrinsics_raw,
        crop_up=up_for_residual,
        force=force,
        threads=threads,
        on_densified=structure_job.start if structure_job else None,
    )
    timings.update({f"mvs_{k}": v for k, v in mvs_result.timings_s.items()})

    # --- mesh post-process ---------------------------------------------------------------------
    t0 = time.monotonic()
    out_dir.mkdir(parents=True, exist_ok=True)
    filenames = revision_filenames(revision)
    mesh_stats, mesh_scale = mesh.process_mesh(
        mvs_result.textured_glb,
        out_dir / filenames["mesh"],
        out_dir / filenames["collision"],
        align_matrix=align_matrix,
        known_height_m=known_height_for_mesh,
        target_triangles=target_triangles,
        collision_triangles=collision_triangles,
        max_texture_size=max_texture_size,
    )
    timings["mesh_postprocess_s"] = time.monotonic() - t0
    scale_estimates.append(mesh_scale)
    final_scale = choose_scale(scale_estimates)

    # plan §1a: "if the two scale estimates differ by more than 2%, keep the full revision's scale
    # [...] and record both in scale_candidates" -- a metric full stage's own scale always wins
    # (it's aligned rigidly), this only decides whether to flag the disagreement + records
    # the preview's estimate alongside the winner (it never outranks a real method, see
    # calibrate._METHOD_RANK, so choose_scale above already never picked it).
    scale_comparison = None
    if aligned_to_preview and preview_match is not None:
        scale_estimates.append(
            ScaleEstimate(
                method="preview",
                scale=preview_match["scale"],
                residual_m=preview_match.get("residual_m"),
                notes=(
                    f"preview revision {existing_scene.get('revision')}'s winning scale "
                    f"({existing_scene.get('scale_method')}), recorded for comparison -- the full "
                    "revision's own scale always wins (plan §1a)"
                ),
            )
        )
    # The 2% check compares geometry, not the two calibrations' SfM-unit multipliers (those come
    # from different reconstructions, so they're incomparable): the fitted preview-per-full scale
    # is ~1.0 exactly when both stages really are in metres.
    preview_is_metric = needs_alignment and existing_scene.get("scale_method") not in (None, "none")
    if aligned_to_preview and full_is_metric and not preview_is_metric:
        logger.warning(
            "run: the preview was published unscaled (scale_method 'none') but this revision is "
            "metric -- it keeps its own scale, so the scene changes size at the swap (x%.3f)",
            1.0 / preview_fit_scale if preview_fit_scale else float("nan"),
        )
    if aligned_to_preview and full_is_metric and preview_is_metric and preview_fit_scale:
        scale_comparison = scale_agreement(preview_fit_scale, 1.0)
        if not scale_comparison["agrees"]:
            logger.warning(
                "run: preview scale %.4f vs full scale %.4f differ by %.1f%% (>2%%) -- keeping "
                "the full revision's scale (more/sharper observations); both recorded in "
                "scale_candidates",
                scale_comparison["preview_scale"],
                scale_comparison["full_scale"],
                scale_comparison["diff_pct"],
            )

    # cameras.r<rev>.json needs the *same* extra scale mesh.py applied on top of align_matrix
    # (baked in only if known-height scaling ran -- 1.0/no-op on the GPS path, where align_matrix
    # is already metric).
    cameras_matrix = similarity_matrix(mesh_scale.scale, np.eye(3), np.zeros(3)) @ align_matrix
    cameras = poses_to_cameras(raw_poses, cameras_matrix)

    # --- thumbs + cameras.r<rev>.json + scene.json ------------------------------------------------
    t0 = time.monotonic()
    source_images = [dense_dir / "images" / p.name for p in raw_poses]
    make_thumbs(source_images, out_dir / "thumbs")
    timings["thumbs_s"] = time.monotonic() - t0

    write_cameras_json(cameras, out_dir / filenames["cameras"])
    structure_entry = None
    if structure_job:
        t0 = time.monotonic()
        structure_entry = structure_job.finish(
            cameras_matrix,
            out_dir,
            revision,
            mesh_file=filenames["mesh"],
            cameras_file=filenames["cameras"],
            frames_dir=frames_dir,
            sparse_raw=sparse_path,
        )
        timings["structure_added_s"] = time.monotonic() - t0  # on the critical path
    spawn = recommended_spawn(np.array(mesh_stats.bbox), cameras)
    publish_revision(
        out_dir,
        revision=revision,
        quality="full",
        name=site,
        scale=final_scale,
        gravity_residual_deg=grav_residual,
        frame_id=frame_id,
        aligned_to_preview=aligned_to_preview,
        alignment_residual_m=alignment_residual_m,
        origin_gps=origin_gps,
        north=north_field,
        spawn=spawn,
        mesh_stats={
            "triangles": mesh_stats.triangles,
            "texture_px": mesh_stats.texture_px,
            "collision_triangles": mesh_stats.collision_triangles,
        },
        scale_candidates=scale_estimates,
        structure=structure_entry,
    )

    problems = validate_package(out_dir)
    if problems:
        logger.warning("run: validate_package found problems: %s", problems)

    # --- QA: render each (capped) registered/held-out camera against its real photo -- an
    # objective sanity check on texture quality, not just "did it produce a file" (assignment
    # Part 1). `holdout_every` matches the one used for frame extraction above, so this actually
    # finds the held-out frames that exist (or correctly finds none, if disabled). Never fails the
    # run: QA is a diagnostic, not a pipeline stage.
    t0 = time.monotonic()
    qa_result = None
    try:
        qa_ids = qa.default_ids(
            out_dir, frames_dir, holdout_every=holdout_every, max_ids=_AUTO_QA_MAX_FRAMES
        )
        qa_result = qa.photo_consistency(out_dir, frames_dir, qa_ids, out_dir=work_dir / "qa")
        write_json_atomic(work_dir / "qa.json", json.dumps(qa_result, indent=2))
    except Exception:
        logger.exception("run: qa failed, continuing without qa.json")
    timings["qa_s"] = time.monotonic() - t0

    report = {
        "site": site,
        "video": str(video),
        "probe": probe_info,
        "sfm": sfm_counts,
        "revision": revision,
        "quality": "full",
        "densify": mvs_result.densify,  # the mode that ran: `hybrid` falls back to `openmvs`
        "frame_id": frame_id,
        "aligned_to_preview": aligned_to_preview,
        "alignment_residual_m": alignment_residual_m,
        "scale_comparison": scale_comparison,
        "scale_method": final_scale.method,
        "scale": final_scale.scale,
        "scale_residual_m": final_scale.residual_m,
        "scale_candidates": [
            {"method": e.method, "scale": e.scale, "residual_m": e.residual_m}
            for e in scale_estimates
        ],
        "gravity_residual_deg": grav_residual,
        "board_normal_gravity_angle_deg": board_normal_gravity_angle_deg,
        "mesh": {
            "triangles": mesh_stats.triangles,
            "texture_px": mesh_stats.texture_px,
            "bbox": mesh_stats.bbox,
            "mesh_bytes": mesh_stats.mesh_bytes,
            "collision_triangles": mesh_stats.collision_triangles,
            "collision_bytes": mesh_stats.collision_bytes,
        },
        "timings_s": timings,
        "validate_package_problems": problems,
        "structure": structure_report,
        "qa": qa_result,
    }
    write_json_atomic(work_dir / "report.json", json.dumps(report, indent=2))
    return report
