import json
from pathlib import Path

import numpy as np
import pytest

pytest.importorskip(
    "pycolmap"
)  # pipeline.run needs the `pipeline` extra (uv sync --extra pipeline)
pytest.importorskip("cv2")  # the ArUco calibration tests below need it too
pytest.importorskip("pymeshlab")  # pipeline.fast (imported below, for the stage-dispatch tests)
# pulls in pipeline.mesh, which needs it

from test_aruco import _render_views  # reuse the synthetic ArUco rendering rig, per assignment

import pipeline.fast as fast_module
from pipeline.aruco import BoardSpec
from pipeline.calibrate import ScaleEstimate, apply_similarity, similarity_matrix
from pipeline.package import Camera
from pipeline.run import (
    RawPose,
    _align_full_to_preview,
    _frame_timestamp,
    _full_res_cameras,
    _next_revision_and_frame_id,
    _parse_stages,
    _resolve_board_specs,
    _run_sfm_with_vggt_fallback,
    calibrate_scale,
    gravity_residual_deg,
    poses_to_cameras,
    run,
)
from pipeline.sfm import SfmResult
from pipeline.telemetry import TelemetryRecord


def _random_rotation(rng):
    q, _ = np.linalg.qr(rng.normal(size=(3, 3)))
    if np.linalg.det(q) < 0:
        q[:, -1] *= -1
    return q


# --- poses_to_cameras (pycolmap -> cameras.json pose conversion, synthetic poses) ---------------


def test_poses_to_cameras_identity_alignment_round_trips_pose():
    R_wc = np.eye(3)
    t_wc = np.array([0.0, 0.0, -5.0])
    raw = RawPose(
        name="0001.jpg",
        R_wc=R_wc,
        t_wc=t_wc,
        fx=1000.0,
        fy=1000.0,
        cx=960.0,
        cy=540.0,
        w=1920,
        h=1080,
    )
    identity = similarity_matrix(1.0, np.eye(3), np.zeros(3))

    cameras = poses_to_cameras([raw], identity)

    assert len(cameras) == 1
    cam = cameras[0]
    assert cam.id == "0001"
    assert cam.thumb == "thumbs/0001.jpg"
    assert cam.R == pytest.approx(R_wc)
    assert cam.t == pytest.approx(t_wc)
    assert cam.position == pytest.approx([0.0, 0.0, 5.0])  # -R^T @ t
    assert cam.fx == 1000.0 and cam.w == 1920


def test_poses_to_cameras_applies_similarity_consistently():
    """A world point at the camera's optical axis, transformed by the same similarity matrix used
    to align the poses, must still project consistently (calibrate.transform_camera's own
    contract) -- exercised here through the public `poses_to_cameras` conversion."""
    rng = np.random.default_rng(0)
    R_wc = _random_rotation(rng)
    t_wc = rng.normal(size=3)
    raw = RawPose(
        name="0007.jpg", R_wc=R_wc, t_wc=t_wc, fx=800.0, fy=800.0, cx=480.0, cy=270.0, w=960, h=540
    )
    s, t_sim = 2.0, np.array([1.0, -1.0, 3.0])
    R_sim = _random_rotation(rng)
    M = similarity_matrix(s, R_sim, t_sim)

    cam = poses_to_cameras([raw], M)[0]

    assert cam.R @ cam.R.T == pytest.approx(np.eye(3), abs=1e-8)  # still a proper rotation
    # position transforms exactly like a world point under the same similarity
    old_position = -R_wc.T @ t_wc
    expected_new_position = s * (R_sim @ old_position) + t_sim
    assert cam.position == pytest.approx(expected_new_position, abs=1e-8)


def test_poses_to_cameras_preserves_order_and_uses_stem_as_id():
    raws = [
        RawPose("0010.jpg", np.eye(3), np.zeros(3), 1, 1, 0, 0, 10, 10),
        RawPose("0002.jpg", np.eye(3), np.zeros(3), 1, 1, 0, 0, 10, 10),
    ]
    identity = similarity_matrix(1.0, np.eye(3), np.zeros(3))
    cameras = poses_to_cameras(raws, identity)
    assert [c.id for c in cameras] == ["0010", "0002"]  # order preserved, no re-sorting here


# --- gravity_residual_deg ------------------------------------------------------------------------


def test_gravity_residual_deg_zero_for_perfectly_level_orbit():
    n = 24
    angles = np.linspace(0, 2 * np.pi, n, endpoint=False)
    up = np.array([0.0, 1.0, 0.0])
    rotations = []
    for a in angles:
        pos = np.array([np.cos(a), 0.0, np.sin(a)])
        forward = -pos
        right = np.cross(forward, up)
        right /= np.linalg.norm(right)
        down = np.cross(forward, right)
        rotations.append(np.stack([right, down, forward], axis=1))
    rotations = np.array(rotations)

    residual = gravity_residual_deg(rotations, up)

    assert residual == pytest.approx(0.0, abs=1e-6)


def test_gravity_residual_deg_positive_when_roll_present():
    up = np.array([0.0, 1.0, 0.0])
    # a single camera rolled 30deg off level -- right axis tilted out of the horizontal plane
    tilt = np.radians(30.0)
    right = np.array([np.cos(tilt), np.sin(tilt), 0.0])
    forward = np.array([0.0, 0.0, 1.0])
    down = np.cross(forward, right)
    R = np.stack([right, down, forward], axis=1)[None, :, :]

    residual = gravity_residual_deg(R, up)

    assert residual == pytest.approx(30.0, abs=1e-6)


# --- _frame_timestamp ------------------------------------------------------------------------


def test_frame_timestamp_no_start_offset():
    assert _frame_timestamp("0001.jpg", fps=2.0, start_s=None) == pytest.approx(0.0)
    assert _frame_timestamp("0003.jpg", fps=2.0, start_s=None) == pytest.approx(1.0)


def test_frame_timestamp_with_start_offset():
    assert _frame_timestamp("0001.jpg", fps=2.0, start_s=10.0) == pytest.approx(10.0)
    assert _frame_timestamp("0005.jpg", fps=2.0, start_s=10.0) == pytest.approx(12.0)


# --- _resolve_board_specs --------------------------------------------------------------------


def test_resolve_board_specs_auto_tries_board_then_scalebar():
    specs = _resolve_board_specs("auto", None, None, None, None, None)
    assert [type(s).__name__ for s in specs] == ["BoardSpec", "ScaleBarSpec"]


def test_resolve_board_specs_none_is_empty():
    assert _resolve_board_specs("none", None, None, None, None, None) == []


def test_resolve_board_specs_board_applies_size_overrides():
    (spec,) = _resolve_board_specs("board", 0.5, 0.1, 4, 3, None)
    assert (spec.marker_length_m, spec.gap_m, spec.cols, spec.rows) == (0.5, 0.1, 4, 3)


def test_resolve_board_specs_scalebar_applies_distance_override():
    (spec,) = _resolve_board_specs("scalebar", None, None, None, None, 2.5)
    assert spec.center_distance_m == 2.5


def test_resolve_board_specs_defaults_unchanged_without_overrides():
    (spec,) = _resolve_board_specs("board", None, None, None, None, None)
    assert spec == BoardSpec()


# --- _full_res_cameras (intrinsics rescale for full-resolution ArUco detection) ---------------


def test_full_res_cameras_rescales_intrinsics_and_extracts_once_per_pose(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(
        "pipeline.run.frames.extract_frames_at",
        lambda video, items: calls.extend((video, out_path, ts) for ts, out_path in items),
    )
    raw = RawPose(
        name="0005.jpg",
        R_wc=np.eye(3),
        t_wc=np.zeros(3),
        fx=500.0,
        fy=500.0,
        cx=480.0,
        cy=270.0,
        w=960,
        h=540,
    )

    frames_full_dir, cameras = _full_res_cameras(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=[raw],
        full_w=1920,
        full_h=1080,
        extract_fps=2.0,
        start_s=None,
        force=True,
    )

    assert frames_full_dir == tmp_path / "frames_full"
    assert len(calls) == 1  # one extraction per raw pose
    assert len(cameras) == 1
    cam = cameras[0]
    scale = 1920 / 960  # == 1080 / 540 here, both axes scale identically
    assert cam.fx == pytest.approx(500.0 * scale)
    assert cam.fy == pytest.approx(500.0 * scale)
    assert cam.cx == pytest.approx(480.0 * scale)
    assert cam.cy == pytest.approx(270.0 * scale)
    assert (cam.w, cam.h) == (1920, 1080)
    assert cam.R is raw.R_wc and cam.t is raw.t_wc  # pose itself is resolution-independent


def test_full_res_cameras_skips_extraction_when_already_present(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(
        "pipeline.run.frames.extract_frames_at", lambda video, items: calls.extend(items)
    )
    (tmp_path / "frames_full").mkdir()
    (tmp_path / "frames_full" / "0001.jpg").write_bytes(b"")
    raw = RawPose(
        name="0001.jpg",
        R_wc=np.eye(3),
        t_wc=np.zeros(3),
        fx=1.0,
        fy=1.0,
        cx=0.0,
        cy=0.0,
        w=10,
        h=10,
    )

    _full_res_cameras(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=[raw],
        full_w=20,
        full_h=20,
        extract_fps=2.0,
        start_s=None,
        force=False,
    )

    assert calls == []  # already on disk -- not re-extracted


def test_full_res_cameras_frame_timestamps_override_keys_by_stem(tmp_path, monkeypatch):
    """Regression test: caught on a real hfbox run -- frame_timestamps must be looked up by
    Path(raw.name).stem (e.g. "p0001"), not the raw filename with extension ("p0001.jpg"), since
    that's the convention frame_times.json/Camera.id both use (poses_to_cameras' own `stem =
    Path(raw.name).stem`), which _align_full_to_preview relies on downstream."""
    calls = []
    monkeypatch.setattr(
        "pipeline.run.frames.extract_frames_at",
        lambda video, items: calls.extend(ts for ts, _ in items),
    )
    raw = RawPose(
        name="p0001.jpg",
        R_wc=np.eye(3),
        t_wc=np.zeros(3),
        fx=500.0,
        fy=500.0,
        cx=480.0,
        cy=270.0,
        w=960,
        h=540,
    )

    _full_res_cameras(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=[raw],
        full_w=1920,
        full_h=1080,
        extract_fps=2.0,
        start_s=None,
        force=True,
        frame_timestamps={"p0001": 3.75},  # stem-keyed, no ".jpg"
    )

    assert calls == [3.75]  # would KeyError (or silently use the wrong timestamp) if mis-keyed


# --- _run_sfm_with_vggt_fallback (decision logic, fully mocked) -------------------------------


def _fake_sfm_result(num_registered, num_images, mapper_used="global"):
    return SfmResult(
        reconstruction_path=Path("x"),
        num_registered=num_registered,
        num_images=num_images,
        mean_reprojection_error=0.5,
        num_points3d=100,
        mapper_used=mapper_used,
        timings_s={},
    )


def test_sfm_fallback_forced_by_mapper_vggt(monkeypatch, tmp_path):
    monkeypatch.setattr(
        "pipeline.run.sfm.run_sfm",
        lambda *a, **k: pytest.fail("sfm.run_sfm must not run when mapper='vggt'"),
    )
    monkeypatch.setattr(
        "pipeline.run.vggt.run_vggt", lambda *a, **k: _fake_sfm_result(10, 10, "vggt")
    )

    result = _run_sfm_with_vggt_fallback(tmp_path, tmp_path, mapper="vggt", num_threads=4)

    assert result.mapper_used == "vggt"


def test_sfm_fallback_not_triggered_on_good_registration(monkeypatch, tmp_path):
    monkeypatch.setattr("pipeline.run.sfm.run_sfm", lambda *a, **k: _fake_sfm_result(95, 100))
    monkeypatch.setattr(
        "pipeline.run.vggt.run_vggt", lambda *a, **k: pytest.fail("should not fall back")
    )

    result = _run_sfm_with_vggt_fallback(tmp_path, tmp_path, mapper="global", num_threads=4)

    assert result.num_registered == 95
    assert result.mapper_used == "global"


def test_sfm_fallback_triggered_on_low_registration_when_env_set(monkeypatch, tmp_path):
    monkeypatch.setattr("pipeline.run.sfm.run_sfm", lambda *a, **k: _fake_sfm_result(10, 100))
    monkeypatch.setattr("pipeline.run.vggt.env_python", lambda: Path("/fake/python"))
    monkeypatch.setattr(
        "pipeline.run.vggt.run_vggt", lambda *a, **k: _fake_sfm_result(10, 10, "vggt")
    )

    result = _run_sfm_with_vggt_fallback(tmp_path, tmp_path, mapper="global", num_threads=4)

    assert result.mapper_used == "vggt"


def test_sfm_fallback_disabled_without_env_var_keeps_pycolmap_result(monkeypatch, tmp_path):
    monkeypatch.setattr("pipeline.run.sfm.run_sfm", lambda *a, **k: _fake_sfm_result(10, 100))
    monkeypatch.setattr("pipeline.run.vggt.env_python", lambda: None)
    monkeypatch.setattr(
        "pipeline.run.vggt.run_vggt", lambda *a, **k: pytest.fail("must not be called")
    )

    result = _run_sfm_with_vggt_fallback(tmp_path, tmp_path, mapper="global", num_threads=4)

    assert result.num_registered == 10  # degraded, but didn't crash and didn't call VGGT


def test_sfm_fallback_on_pycolmap_exception_with_env_set(monkeypatch, tmp_path):
    def raise_sfm(*a, **k):
        raise RuntimeError("SfM mapping produced zero reconstructions")

    monkeypatch.setattr("pipeline.run.sfm.run_sfm", raise_sfm)
    monkeypatch.setattr("pipeline.run.vggt.env_python", lambda: Path("/fake/python"))
    monkeypatch.setattr(
        "pipeline.run.vggt.run_vggt", lambda *a, **k: _fake_sfm_result(5, 5, "vggt")
    )

    result = _run_sfm_with_vggt_fallback(tmp_path, tmp_path, mapper="global", num_threads=4)

    assert result.mapper_used == "vggt"


def test_sfm_fallback_reraises_on_pycolmap_exception_without_env(monkeypatch, tmp_path):
    def raise_sfm(*a, **k):
        raise RuntimeError("SfM mapping produced zero reconstructions")

    monkeypatch.setattr("pipeline.run.sfm.run_sfm", raise_sfm)
    monkeypatch.setattr("pipeline.run.vggt.env_python", lambda: None)

    with pytest.raises(RuntimeError, match="zero reconstructions"):
        _run_sfm_with_vggt_fallback(tmp_path, tmp_path, mapper="global", num_threads=4)


# --- calibrate_scale: competing estimates + the ArUco path end to end -------------------------


def _raw_pose_from_camera(name, cam):
    return RawPose(
        name=name,
        R_wc=cam.R,
        t_wc=cam.t,
        fx=cam.fx,
        fy=cam.fy,
        cx=cam.cx,
        cy=cam.cy,
        w=cam.w,
        h=cam.h,
    )


def test_calibrate_scale_prefers_known_distance_over_gps(monkeypatch, tmp_path):
    """`choose_scale` ranking (known_dimension > gps) must actually change which scale gets baked
    into `align_matrix`, not just which `ScaleEstimate` gets returned."""
    monkeypatch.setattr("pipeline.run._full_res_cameras", lambda *a, **k: (tmp_path, []))
    monkeypatch.setattr("pipeline.run._aruco_scale", lambda *a, **k: None)  # no board in frame

    raw_poses = [
        RawPose(
            f"{i:04d}.jpg",
            np.eye(3),
            np.array([0.0, 0.0, -float(i + 1)]),
            1000,
            1000,
            500,
            500,
            1000,
            1000,
        )
        for i in range(3)
    ]
    cam_positions_raw = np.array([-p.R_wc.T @ p.t_wc for p in raw_poses])
    cam_to_world_rotations = np.array([p.R_wc.T for p in raw_poses])

    # a real TelemetryRecord (not a bespoke stub) so calibrate_scale's caption-based candidates
    # (altitude/odometry) see the real "no H/H.S reading" defaults and cleanly produce nothing,
    # rather than crashing on missing attributes -- only lat/lon/abs_alt_m are exercised here,
    # `telemetry.resample`/`telemetry.to_enu` themselves are mocked below.
    records = [TelemetryRecord(t_s=0.0, lat=33.0, lon=-84.0, abs_alt_m=300.0)]
    monkeypatch.setattr(
        "pipeline.run.telemetry.resample", lambda records, times_s: records * len(times_s)
    )
    monkeypatch.setattr(
        "pipeline.run.telemetry.to_enu",
        # a horizontal spread, so the GPS track is wide enough to fit (_MIN_GPS_EXTENT_STEPS)
        lambda resampled: np.array([[0.0, 0.0, 1.0], [30.0, 0.0, 2.0], [0.0, 30.0, 3.0]]),
    )

    kd_estimate_scale = 3.0
    monkeypatch.setattr(
        "pipeline.run.known_distance_scale",
        lambda path, cams: ScaleEstimate("known_dimension", kd_estimate_scale, 0.01),
    )

    result = calibrate_scale(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=raw_poses,
        cam_positions_raw=cam_positions_raw,
        cam_to_world_rotations=cam_to_world_rotations,
        records=records,
        extract_fps=2.0,
        start_s=None,
        probe_info={"width": 1000, "height": 1000},
        board="auto",
        board_marker_m=None,
        board_gap_m=None,
        board_cols=None,
        board_rows=None,
        board_distance_m=None,
        known_distance_json=tmp_path / "kd.json",
        known_height_m=None,
        force=False,
    )

    methods = [e.method for e in result.scale_estimates]
    assert methods == ["gps", "known_dimension"]
    # the applied scale (recovered from align_matrix's determinant, similarity_matrix's own
    # contract: det(s*R) = s**3) must be the known_dimension winner's scale, not GPS's ~10x
    applied_scale = float(np.linalg.det(result.align_matrix[:3, :3])) ** (1 / 3)
    assert applied_scale == pytest.approx(kd_estimate_scale, rel=1e-6)


def _orbit_rotations(n=24, radius=1.0):
    """cam-to-world rotations for a level circular orbit around Y-up -- same construction as
    test_calibrate.py's `_orbit_cam_to_world_rotations`, inlined here to keep this test standalone."""
    world_up = np.array([0.0, 1.0, 0.0])
    rotations = []
    for a in np.linspace(0, 2 * np.pi, n, endpoint=False):
        pos = np.array([radius * np.cos(a), 0.0, radius * np.sin(a)])
        forward = -pos / np.linalg.norm(pos)
        right = np.cross(forward, world_up)
        right = right / np.linalg.norm(right)
        down = np.cross(forward, right)
        rotations.append(np.stack([right, down, forward], axis=1))
    return np.array(rotations)


def test_calibrate_scale_skips_gps_with_no_fix_and_falls_back_to_altitude(tmp_path):
    """Assignment: a clip like the real DJI_0095.srt corpus clip -- every telemetry record has H
    but zero GPS fixes. `calibrate_scale` must not crash (previously: NameError on `s_gps`/`R_gps`,
    referenced unconditionally further down whenever `records` was non-empty), must skip 'gps'
    cleanly (falling back to `up_from_cameras`), and must still produce an 'altitude' candidate."""
    n = 24
    raw_poses = [
        RawPose(f"{i + 1:04d}.jpg", np.eye(3), np.zeros(3), 1000, 1000, 500, 500, 1000, 1000)
        for i in range(n)  # dummy R/t/intrinsics -- board="none" means _full_res_cameras never
        # touches them; only `.name` (for frame timestamps) matters here
    ]
    cam_to_world_rotations = _orbit_rotations(n)
    heights = np.linspace(0.0, 4.0, n)  # reconstruction (unitless) camera height, up-axis = Y
    cam_positions_raw = np.stack(
        [np.cos(np.linspace(0, 2 * np.pi, n, endpoint=False)), heights, np.zeros(n)], axis=1
    )

    true_scale = 2.5  # metres per reconstruction unit
    records = [
        TelemetryRecord(t_s=float(i), lat=float("nan"), lon=float("nan"), rel_alt_m=true_scale * h)
        for i, h in enumerate(heights)  # matches _frame_timestamp(name, fps=1.0, start_s=0.0)
    ]

    result = calibrate_scale(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=raw_poses,
        cam_positions_raw=cam_positions_raw,
        cam_to_world_rotations=cam_to_world_rotations,
        records=records,
        extract_fps=1.0,
        start_s=0.0,
        probe_info={"width": 1000, "height": 1000},
        board="none",
        board_marker_m=None,
        board_gap_m=None,
        board_cols=None,
        board_rows=None,
        board_distance_m=None,
        known_distance_json=None,
        known_height_m=None,
        force=False,
    )

    methods = [e.method for e in result.scale_estimates]
    assert "gps" not in methods  # 0/24 finite fixes -- below _MIN_GPS_FIXES, skipped cleanly
    assert "altitude" in methods
    altitude_est = next(e for e in result.scale_estimates if e.method == "altitude")
    assert altitude_est.scale == pytest.approx(true_scale, rel=0.1)
    assert result.origin_gps is None  # no GPS path ran -- no GPS origin to report
    assert result.north_field is None


def test_calibrate_scale_skips_gps_quantised_coarser_than_the_track(tmp_path):
    """gt-lcc DJI_0097 (DJI Mini 4K): the caption writes lon/lat to 4 decimals (~10 m), and a close
    pass around a pavilion spans one or two such steps. Fitting the camera path to that staircase
    gives an arbitrary scale, up and north, and GPS outranks the altitude fit, so it must be
    skipped. The same pass with full-precision GPS still takes the GPS path."""
    n = 24
    a = np.linspace(0, 2 * np.pi, n, endpoint=False)
    heights = np.linspace(0.0, 0.8, n)
    cam_positions_raw = np.stack([np.cos(a), heights, np.sin(a)], axis=1)  # up-axis = Y
    raw_poses = [
        RawPose(f"{i + 1:04d}.jpg", np.eye(3), np.zeros(3), 1000, 1000, 500, 500, 1000, 1000)
        for i in range(n)
    ]
    true_scale = 5.0  # a 5 m radius pass, 4 m of climb
    lat0, lon0, r_earth = 33.7784, -84.4023, 6_378_137.0
    lat = lat0 - np.degrees(true_scale * np.sin(a) / r_earth)  # z -> south keeps it right-handed
    lon = lon0 + np.degrees(true_scale * np.cos(a) / (r_earth * np.cos(np.radians(lat0))))

    def calibrate(decimals):
        records = [
            TelemetryRecord(
                t_s=float(i),
                lat=float(np.round(lat[i], decimals)),
                lon=float(np.round(lon[i], decimals)),
                rel_alt_m=true_scale * heights[i],
            )
            for i in range(n)
        ]
        return calibrate_scale(
            video=Path("v.mp4"),
            work_dir=tmp_path,
            raw_poses=raw_poses,
            cam_positions_raw=cam_positions_raw,
            cam_to_world_rotations=_orbit_rotations(n),
            records=records,
            extract_fps=1.0,
            start_s=0.0,
            probe_info={"width": 1000, "height": 1000},
            board="none",
            board_marker_m=None,
            board_gap_m=None,
            board_cols=None,
            board_rows=None,
            board_distance_m=None,
            known_distance_json=None,
            known_height_m=None,
            force=False,
        )

    coarse = calibrate(4)
    methods = [e.method for e in coarse.scale_estimates]
    assert "gps" not in methods and coarse.north_field is None and coarse.origin_gps is None
    altitude_est = next(e for e in coarse.scale_estimates if e.method == "altitude")
    applied_scale = float(np.linalg.det(coarse.align_matrix[:3, :3])) ** (1 / 3)
    assert applied_scale == pytest.approx(altitude_est.scale) == pytest.approx(true_scale, rel=0.1)

    fine = calibrate(9)
    gps_est = next(e for e in fine.scale_estimates if e.method == "gps")
    assert gps_est.scale == pytest.approx(true_scale, rel=0.01)


def test_calibrate_scale_altitude_with_preview_frame_timestamps(tmp_path):
    """The preview stage's frames are "p0001.jpg"... at arbitrary times, keyed by stem in
    frame_times.json -- the caption altitude fit must look timestamps up by stem (it used to KeyError
    on the ".jpg" name, masked because the preview passed no caption records at all)."""
    n = 24
    raw_poses = [
        RawPose(f"p{i + 1:04d}.jpg", np.eye(3), np.zeros(3), 1000, 1000, 500, 500, 1000, 1000)
        for i in range(n)
    ]
    heights = np.linspace(0.0, 4.0, n)
    cam_positions_raw = np.stack(
        [np.cos(np.linspace(0, 2 * np.pi, n, endpoint=False)), heights, np.zeros(n)], axis=1
    )
    times = {f"p{i + 1:04d}": 0.5 + 3.0 * i for i in range(n)}  # not a fixed 1 fps grid
    records = [
        TelemetryRecord(t_s=t, lat=float("nan"), lon=float("nan"), rel_alt_m=2.5 * h)
        for t, h in zip(times.values(), heights)
    ]

    result = calibrate_scale(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=raw_poses,
        cam_positions_raw=cam_positions_raw,
        cam_to_world_rotations=_orbit_rotations(n),
        records=records,
        extract_fps=1.0,
        start_s=None,
        probe_info={"width": 1000, "height": 1000},
        board="none",
        board_marker_m=None,
        board_gap_m=None,
        board_cols=None,
        board_rows=None,
        board_distance_m=None,
        known_distance_json=None,
        known_height_m=None,
        force=False,
        frame_timestamps=times,
    )

    altitude_est = next(e for e in result.scale_estimates if e.method == "altitude")
    assert altitude_est.scale == pytest.approx(2.5, rel=0.05)


def test_calibrate_scale_aruco_path_recovers_metric_scale_end_to_end(tmp_path, monkeypatch):
    """Assignment: synthetic end-to-end scale test for the ArUco path inside `run`'s calibration
    code -- a reconstruction at an arbitrary scale must come out metric within 1% via
    `calibrate_scale`'s own `align_matrix`, not just via `aruco.estimate_board_scale` in isolation
    (already covered by test_aruco.py)."""
    spec = BoardSpec()
    true_cameras = _render_views(spec, tmp_path, n_views=8)  # real metres, true (unscaled) rig
    recon_scale = 0.083  # arbitrary -- simulates an unscaled SfM reconstruction

    raw_poses = [
        RawPose(
            name=name,
            R_wc=cam.R,
            t_wc=cam.t * recon_scale,  # only translation scales -- the classic SfM scale ambiguity
            fx=cam.fx,
            fy=cam.fy,
            cx=cam.cx,
            cy=cam.cy,
            w=cam.w,
            h=cam.h,
        )
        for name, cam in true_cameras.items()
    ]
    cam_positions_raw = np.array([-p.R_wc.T @ p.t_wc for p in raw_poses])
    cam_to_world_rotations = np.array([p.R_wc.T for p in raw_poses])

    full_cameras = [
        Camera(
            id=p.name,
            file=p.name,
            thumb="",
            R=p.R_wc,
            t=p.t_wc,
            fx=p.fx,
            fy=p.fy,
            cx=p.cx,
            cy=p.cy,
            w=p.w,
            h=p.h,
        )
        for p in raw_poses
    ]
    # the real detect/triangulate/scale pipeline runs unmocked; only the full-res re-extraction is
    # stubbed out (the synthetic frames are already "full resolution" -- there's no video to re-cut)
    monkeypatch.setattr("pipeline.run._full_res_cameras", lambda *a, **k: (tmp_path, full_cameras))

    result = calibrate_scale(
        video=Path("v.mp4"),
        work_dir=tmp_path,
        raw_poses=raw_poses,
        cam_positions_raw=cam_positions_raw,
        cam_to_world_rotations=cam_to_world_rotations,
        records=[],
        extract_fps=2.0,
        start_s=None,
        probe_info={"width": 1280, "height": 960},
        board="board",
        board_marker_m=None,
        board_gap_m=None,
        board_cols=None,
        board_rows=None,
        board_distance_m=None,
        known_distance_json=None,
        known_height_m=None,
        force=False,
    )

    assert [e.method for e in result.scale_estimates] == ["scalebar_aruco"]

    # apply the SAME align_matrix run() would apply to raw poses, and check recovered camera-to-
    # camera distances against the TRUE (pre-recon_scale) rig -- not just the raw ScaleEstimate.
    names = list(true_cameras.keys())
    true_positions = {n: -true_cameras[n].R.T @ true_cameras[n].t for n in names}
    raw_positions = {p.name: -p.R_wc.T @ p.t_wc for p in raw_poses}

    a, b = names[0], names[len(names) // 2]
    true_dist = np.linalg.norm(true_positions[a] - true_positions[b])
    transformed = apply_similarity(
        np.array([raw_positions[a], raw_positions[b]]), result.align_matrix
    )
    recovered_dist = np.linalg.norm(transformed[0] - transformed[1])

    assert recovered_dist == pytest.approx(true_dist, rel=0.01)  # within 1%, plan's bar


# --- _parse_stages (plan §1a: `--stages preview,full`, either or both) --------------------------


def test_parse_stages_default_both():
    assert _parse_stages("preview,full") == ["preview", "full"]


def test_parse_stages_single_stage():
    assert _parse_stages("preview") == ["preview"]
    assert _parse_stages("full") == ["full"]


def test_parse_stages_strips_whitespace():
    assert _parse_stages(" preview , full ") == ["preview", "full"]


def test_parse_stages_rejects_unknown_stage():
    with pytest.raises(ValueError, match="unknown stage"):
        _parse_stages("preview,bogus")


def test_parse_stages_rejects_empty_string():
    with pytest.raises(ValueError):
        _parse_stages("")


# --- _next_revision_and_frame_id (plan §1a: revision 2 onto a preview, else revision 1) ---------


def test_next_revision_and_frame_id_no_preview_is_revision_1_no_alignment(tmp_path):
    # fallback when there's no published preview at all (e.g. no VGGT env on this box)
    revision, frame_id, needs_alignment = _next_revision_and_frame_id(None, tmp_path, "site")
    assert revision == 1
    assert needs_alignment is False
    assert frame_id  # a fresh frame id was minted


def test_next_revision_and_frame_id_existing_preview_bumps_to_revision_2(tmp_path):
    existing_scene = {"quality": "preview", "revision": 1, "frame": {"id": "site-abcd"}}
    revision, frame_id, needs_alignment = _next_revision_and_frame_id(
        existing_scene, tmp_path, "site"
    )
    assert revision == 2
    assert needs_alignment is True
    assert frame_id == "site-abcd"  # reuses the preview's frame id, doesn't mint a new one


def test_next_revision_and_frame_id_existing_full_scene_stays_revision_1(tmp_path):
    # a currently-published *full* scene (not preview) doesn't trigger alignment bookkeeping
    existing_scene = {"quality": "full", "revision": 1, "frame": {"id": "site-abcd"}}
    revision, _frame_id, needs_alignment = _next_revision_and_frame_id(
        existing_scene, tmp_path, "site"
    )
    assert revision == 1
    assert needs_alignment is False


# --- _align_full_to_preview (plan §1a: rigid-align full onto the preview's frame) ----------------


def test_align_full_to_preview_recovers_known_rotation_translation(tmp_path):
    work_dir = tmp_path / "work"
    out_dir = tmp_path / "out"
    (work_dir / "preview").mkdir(parents=True)
    out_dir.mkdir()

    extract_fps, start_s = 2.0, 0.0
    # Stage B's raw camera path: R_wc = I, so camera centre = -t_wc; a straight line so linear
    # interpolation at the preview's timestamps is exact, not an approximation the test has to
    # replicate by hand.
    full_ts = [0.0, 0.5, 1.0, 1.5, 2.0]
    raw_poses = [
        RawPose(
            name=f"{i + 1:04d}.jpg",
            R_wc=np.eye(3),
            t_wc=-np.array([2 * t, 0.0, 5.0]),
            fx=1000.0,
            fy=1000.0,
            cx=960.0,
            cy=540.0,
            w=1920,
            h=1080,
        )
        for i, t in enumerate(full_ts)
    ]
    cam_positions_raw = np.array([-p.R_wc.T @ p.t_wc for p in raw_poses])
    align_matrix = similarity_matrix(1.0, np.eye(3), np.zeros(3))  # identity -- keep the fixture
    # simple, only _align_full_to_preview's own fix-up rotation/translation is under test

    angle = np.radians(15.0)
    R_true = np.array(
        [[np.cos(angle), -np.sin(angle), 0], [np.sin(angle), np.cos(angle), 0], [0, 0, 1]]
    )
    t_true = np.array([3.0, -1.0, 0.5])

    preview_times_by_id = {"p0001": 0.25, "p0002": 0.75, "p0003": 1.25}
    preview_cams = []
    for pid, t in preview_times_by_id.items():
        src_point = np.array([2 * t, 0.0, 5.0])  # exact point on the line at time t
        preview_cams.append({"id": pid, "position": (R_true @ src_point + t_true).tolist()})

    (work_dir / "preview" / "frame_times.json").write_text(json.dumps(preview_times_by_id))
    (out_dir / "cameras.r1.json").write_text(json.dumps(preview_cams))
    existing_scene = {"cameras": "cameras.r1.json"}

    total_matrix, residual, fit_scale = _align_full_to_preview(
        work_dir,
        out_dir,
        raw_poses,
        cam_positions_raw,
        align_matrix,
        extract_fps,
        start_s,
        existing_scene,
    )

    assert residual < 1e-6  # noise-free synthetic fixture -- should recover the fit almost exactly
    assert fit_scale == pytest.approx(1.0)

    # "mesh follows": an arbitrary point in Stage B's world must land where the known transform
    # says it should once carried through the composed total_matrix (mesh/cameras use the same
    # align matrix, so this stands in for "the mesh transforms consistently with the cameras")
    probe = np.array([2 * 1.0, 0.0, 5.0])
    expected = R_true @ probe + t_true
    got = apply_similarity(probe[None, :], total_matrix)[0]
    assert got == pytest.approx(expected, abs=1e-6)


def test_align_full_to_preview_falls_back_when_preview_files_missing(tmp_path):
    work_dir = tmp_path / "work"
    out_dir = tmp_path / "out"
    work_dir.mkdir()
    out_dir.mkdir()
    align_matrix = similarity_matrix(1.0, np.eye(3), np.zeros(3))

    total_matrix, residual, fit_scale = _align_full_to_preview(
        work_dir,
        out_dir,
        raw_poses=[],
        cam_positions_raw=np.zeros((0, 3)),
        align_matrix=align_matrix,
        extract_fps=2.0,
        start_s=0.0,
        existing_scene={"cameras": "cameras.r1.json"},  # points at a file that doesn't exist
    )

    assert residual is None and fit_scale is None
    np.testing.assert_array_equal(total_matrix, align_matrix)  # unchanged, unaligned fallback


def test_align_full_to_preview_adopts_preview_units_when_not_metric(tmp_path):
    """No scale reference: VGGT and COLMAP land in unrelated units (6-80x apart on the corpus), so
    the full revision must take the preview's scale too or the swap jumps. A metric full revision
    keeps its own scale and only reports the disagreement."""
    work_dir, out_dir = tmp_path / "work", tmp_path / "out"
    (work_dir / "preview").mkdir(parents=True)
    out_dir.mkdir()
    angles = np.linspace(0, np.pi, 6)
    raw_poses = [
        RawPose(
            name=f"{i + 1:04d}.jpg",
            R_wc=np.eye(3),
            t_wc=-np.array([4 * np.cos(a), 4 * np.sin(a), 1.0]),
            fx=1000.0,
            fy=1000.0,
            cx=960.0,
            cy=540.0,
            w=1920,
            h=1080,
        )
        for i, a in enumerate(angles)
    ]
    cam_positions_raw = np.array([-p.R_wc.T @ p.t_wc for p in raw_poses])
    times = {f"p{i}": i * 0.5 for i in range(len(angles))}  # same timestamps as Stage B at 2 fps
    t_true = np.array([1.0, 2.0, 3.0])
    preview = [
        {"id": k, "position": (0.1 * cam_positions_raw[i] + t_true).tolist()}
        for i, k in enumerate(times)
    ]
    (work_dir / "preview" / "frame_times.json").write_text(json.dumps(times))
    (out_dir / "cameras.r1.json").write_text(json.dumps(preview))
    identity = similarity_matrix(1.0, np.eye(3), np.zeros(3))
    args = (
        work_dir,
        out_dir,
        raw_poses,
        cam_positions_raw,
        identity,
        2.0,
        0.0,
        {"cameras": "cameras.r1.json"},
    )

    total, residual, fit_scale = _align_full_to_preview(*args, full_is_metric=False)
    assert residual < 1e-6 and fit_scale == pytest.approx(0.1)
    probe = cam_positions_raw[2]
    assert apply_similarity(probe[None, :], total)[0] == pytest.approx(0.1 * probe + t_true)

    total, residual, fit_scale = _align_full_to_preview(*args, full_is_metric=True)
    assert fit_scale == pytest.approx(0.1) and residual > 1.0  # honest misfit, scale kept
    assert np.linalg.det(total[:3, :3]) == pytest.approx(1.0)


# --- run() stage dispatcher (plan §1a: preview then full by default; either alone via --stages) -


def test_run_stages_preview_only_skips_full(monkeypatch, tmp_path):
    monkeypatch.setattr(fast_module, "run_preview", lambda *a, **k: {"revision": 1})

    def _boom(*a, **k):
        raise AssertionError("_run_full must not run when --stages preview")

    monkeypatch.setattr("pipeline.run._run_full", _boom)

    report = run(tmp_path / "v.mp4", "site", tmp_path / "out", tmp_path / "work", stages="preview")

    assert report == {
        "site": "site",
        "video": str(tmp_path / "v.mp4"),
        "stages": ["preview"],
        "preview": {"revision": 1},
    }


def test_run_stages_full_only_skips_preview(monkeypatch, tmp_path):
    def _boom(*a, **k):
        raise AssertionError("preview must not run when --stages full")

    monkeypatch.setattr(fast_module, "run_preview", _boom)
    monkeypatch.setattr("pipeline.run._run_full", lambda *a, **k: {"revision": 1, "site": "site"})

    report = run(tmp_path / "v.mp4", "site", tmp_path / "out", tmp_path / "work", stages="full")

    assert report["stages"] == ["full"]
    assert report["preview"] is None
    assert report["revision"] == 1  # _run_full's report merged straight through


def test_run_stages_default_runs_preview_then_full(monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(
        fast_module, "run_preview", lambda *a, **k: calls.append("preview") or {"revision": 1}
    )
    monkeypatch.setattr(
        "pipeline.run._run_full",
        lambda *a, **k: calls.append("full") or {"revision": 2, "site": "site"},
    )

    report = run(tmp_path / "v.mp4", "site", tmp_path / "out", tmp_path / "work")

    assert calls == ["preview", "full"]  # preview must publish before full starts (plan §1a)
    assert report["stages"] == ["preview", "full"]
    assert report["preview"] == {"revision": 1}
    assert report["revision"] == 2
