import json

import numpy as np
import pytest

from pipeline.calibrate import (
    ScaleEstimate,
    align_to_gltf,
    apply_similarity,
    choose_scale,
    interpolate_positions_at_times,
    known_distance_scale,
    reprojection_error_px,
    rigid_align_points,
    scale_agreement,
    scale_from_caption_altitude,
    scale_from_caption_odometry,
    scale_from_gps,
    scale_from_known_distance,
    similarity_matrix,
    transform_camera,
    triangulate_dlt,
    umeyama,
    up_from_cameras,
)
from pipeline.package import Camera


def _random_rotation(rng):
    q, _ = np.linalg.qr(rng.normal(size=(3, 3)))
    if np.linalg.det(q) < 0:
        q[:, -1] *= -1
    return q


# --- umeyama ----------------------------------------------------------------------


def test_umeyama_recovers_known_similarity_exactly():
    rng = np.random.default_rng(0)
    src = rng.normal(size=(20, 3))
    true_s, true_t = 2.3, np.array([1.0, -2.0, 0.5])
    true_R = _random_rotation(rng)
    dst = true_s * (true_R @ src.T).T + true_t

    s, R, t, rms = umeyama(src, dst)

    assert s == pytest.approx(true_s, abs=1e-8)
    assert R == pytest.approx(true_R, abs=1e-8)
    assert t == pytest.approx(true_t, abs=1e-8)
    assert rms == pytest.approx(0.0, abs=1e-8)
    assert np.linalg.det(R) == pytest.approx(1.0)


def test_umeyama_degrades_gracefully_with_noise():
    rng = np.random.default_rng(1)
    src = rng.normal(size=(50, 3))
    true_s, true_t = 1.7, np.array([0.2, 0.4, -0.1])
    true_R = _random_rotation(rng)
    dst = true_s * (true_R @ src.T).T + true_t + rng.normal(scale=0.01, size=(50, 3))

    s, _R, _t, rms = umeyama(src, dst)

    assert s == pytest.approx(true_s, abs=0.01)
    assert 0.0 < rms < 0.1  # noisy but small, not exact


def test_umeyama_never_returns_a_reflection():
    """If the best unconstrained-least-squares fit would be a mirror (det -1), Umeyama must still
    return a proper rotation (det +1) -- a reflected camera path is physically meaningless."""
    rng = np.random.default_rng(2)
    src = rng.normal(size=(30, 3))
    true_R = _random_rotation(rng)
    reflect = np.diag([1.0, 1.0, -1.0]) @ true_R  # det = -1
    dst = 1.5 * (reflect @ src.T).T + np.array([1.0, 0.0, 0.0])

    _, R, _, _ = umeyama(src, dst)

    assert np.linalg.det(R) == pytest.approx(1.0)
    assert R @ R.T == pytest.approx(np.eye(3), abs=1e-8)  # still orthonormal


def test_umeyama_without_scale_fixes_s_to_one():
    rng = np.random.default_rng(3)
    src = rng.normal(size=(10, 3))
    true_R = _random_rotation(rng)
    dst = 4.0 * (true_R @ src.T).T  # a real scale of 4x

    s, _, _, _ = umeyama(src, dst, with_scale=False)

    assert s == 1.0


# --- scale_from_known_distance ------------------------------------------------------


def test_scale_from_known_distance():
    p_a, p_b = np.array([0.0, 0.0, 0.0]), np.array([2.0, 0.0, 0.0])
    assert scale_from_known_distance(p_a, p_b, true_len_m=5.0) == pytest.approx(2.5)


def test_scale_from_known_distance_rejects_coincident_points():
    with pytest.raises(ValueError):
        scale_from_known_distance(np.zeros(3), np.zeros(3), true_len_m=1.0)


# --- triangulate_dlt / reprojection_error_px -----------------------------------------


def _camera(pos, target, fx=1000.0, fy=1000.0, cx=320.0, cy=240.0, w=640, h=480, name="c.jpg"):
    forward = (target - pos) / np.linalg.norm(target - pos)
    right = np.cross(forward, np.array([0.0, 1.0, 0.0]))
    right = right / np.linalg.norm(right)
    down = np.cross(forward, right)
    R_wc = np.stack([right, down, forward], axis=1).T
    t_wc = -R_wc @ pos
    return Camera(
        id=name, file=name, thumb="", R=R_wc, t=t_wc, fx=fx, fy=fy, cx=cx, cy=cy, w=w, h=h
    )


def _project(cam, X):
    K = np.array([[cam.fx, 0.0, cam.cx], [0.0, cam.fy, cam.cy], [0.0, 0.0, 1.0]])
    x = cam.R @ X + cam.t
    return (K @ x)[:2] / x[2]


def _proj_matrix(cam):
    K = np.array([[cam.fx, 0.0, cam.cx], [0.0, cam.fy, cam.cy], [0.0, 0.0, 1.0]])
    return K @ np.hstack([cam.R, cam.t.reshape(3, 1)])


def test_triangulate_dlt_recovers_known_point():
    X_true = np.array([0.3, -0.2, 5.0])
    cams = [
        _camera(np.array([-1.0, 0.0, 0.0]), np.array([0.0, 0.0, 5.0])),
        _camera(np.array([1.0, 0.5, -0.5]), np.array([0.0, 0.0, 5.0])),
        _camera(np.array([0.0, 1.5, 0.5]), np.array([0.0, 0.0, 5.0])),
    ]
    views = [(_proj_matrix(c), _project(c, X_true)) for c in cams]

    X = triangulate_dlt(views)

    assert X == pytest.approx(X_true, abs=1e-6)
    assert reprojection_error_px(X, views) == pytest.approx(0.0, abs=1e-6)


# --- known_distance_scale -------------------------------------------------------------


def test_known_distance_scale_recovers_scale_from_two_observations(tmp_path):
    p_a, p_b = np.array([0.0, 0.0, 5.0]), np.array([1.0, 0.0, 5.0])  # 1.0 unitless apart
    cams = {
        "0001.jpg": _camera(np.array([-1.5, 0.2, 0.0]), np.array([0.5, 0.0, 5.0]), name="0001.jpg"),
        "0002.jpg": _camera(np.array([1.5, -0.3, 1.0]), np.array([0.5, 0.0, 5.0]), name="0002.jpg"),
    }
    data = {
        "length_m": 3.3,
        "observations": [
            {
                "frame": name,
                "a": _project(cam, p_a).tolist(),
                "b": _project(cam, p_b).tolist(),
            }
            for name, cam in cams.items()
        ],
    }
    path = tmp_path / "known_distance.json"
    path.write_text(json.dumps(data))

    estimate = known_distance_scale(path, cams)

    assert estimate.method == "known_dimension"
    assert estimate.scale == pytest.approx(3.3, rel=1e-4)  # true_len_m / (unitless dist == 1.0)
    assert "2 observations" in estimate.notes


def test_known_distance_scale_ignores_unregistered_frames(tmp_path):
    cam = _camera(np.array([-1.5, 0.2, 0.0]), np.array([0.5, 0.0, 5.0]), name="0001.jpg")
    p_a, p_b = np.array([0.0, 0.0, 5.0]), np.array([1.0, 0.0, 5.0])
    data = {
        "length_m": 1.0,
        "observations": [
            {
                "frame": "0001.jpg",
                "a": _project(cam, p_a).tolist(),
                "b": _project(cam, p_b).tolist(),
            },
            {"frame": "not-registered.jpg", "a": [0, 0], "b": [1, 1]},
        ],
    }
    path = tmp_path / "known_distance.json"
    path.write_text(json.dumps(data))

    with pytest.raises(ValueError, match=">=2"):
        known_distance_scale(path, {"0001.jpg": cam})


# --- scale_from_gps -----------------------------------------------------------------


def test_scale_from_gps_recovers_scale_and_reports_residual():
    rng = np.random.default_rng(4)
    cam_centers = rng.normal(size=(15, 3))
    true_s = 3.0
    true_R = _random_rotation(rng)
    true_t = np.array([10.0, 0.0, -5.0])
    enu = true_s * (true_R @ cam_centers.T).T + true_t + rng.normal(scale=0.02, size=(15, 3))

    s, _R, _t, residual_m = scale_from_gps(cam_centers, enu)

    assert s == pytest.approx(true_s, abs=0.05)
    assert residual_m < 0.1


def test_scale_from_gps_drops_non_finite_rows_and_still_fits():
    """Bug (b)/tolerate-missing-GPS: rows with NaN `enu` (no fix that frame) must be dropped, not
    poison the whole Umeyama fit -- the recovered scale should still match the finite subset."""
    rng = np.random.default_rng(10)
    cam_centers = rng.normal(size=(6, 3))
    true_s = 2.0
    true_R = _random_rotation(rng)
    true_t = np.array([1.0, 2.0, 3.0])
    enu = true_s * (true_R @ cam_centers.T).T + true_t
    enu[1] = np.nan  # no GPS fix that frame
    enu[4] = np.nan

    s, _R, _t, residual_m = scale_from_gps(cam_centers, enu)

    assert s == pytest.approx(true_s, abs=1e-6)
    assert residual_m == pytest.approx(0.0, abs=1e-8)


def test_scale_from_gps_rejects_too_few_finite_fixes():
    cam_centers = np.zeros((5, 3))
    enu = np.full((5, 3), np.nan)
    enu[0] = [0.0, 0.0, 0.0]
    enu[1] = [1.0, 0.0, 0.0]  # only 2 finite rows -- below _MIN_UMEYAMA_POINTS (3)

    with pytest.raises(ValueError, match=">=3"):
        scale_from_gps(cam_centers, enu)


# --- scale_from_caption_altitude (assignment task 2: "altitude") --------------------------------


def test_scale_from_caption_altitude_recovers_known_scale_with_noise_and_quantisation():
    rng = np.random.default_rng(11)
    n = 40
    true_scale = 2.5  # metres per reconstruction unit
    up_cam = np.linspace(0.0, 4.0, n) + rng.normal(scale=0.01, size=n)  # reconstruction units
    h_noisy = true_scale * up_cam + rng.normal(scale=0.02, size=n)  # sensor noise
    h_caption = np.round(h_noisy, 1)  # SRT's own 0.1m quantisation

    est = scale_from_caption_altitude(up_cam, h_caption)

    assert est is not None
    assert est.method == "altitude"
    assert est.scale == pytest.approx(true_scale, rel=0.05)
    assert est.residual_m < 0.2
    assert "quantised" in est.notes


def test_scale_from_caption_altitude_rejects_near_constant_height():
    """A near-hover clip (H barely moves, like most of DJI_0095.srt) can't be fit against
    quantisation noise -- must return None, not a spurious scale."""
    rng = np.random.default_rng(12)
    n = 40
    up_cam = np.linspace(0.0, 4.0, n)
    h_caption = np.round(1.1 + rng.normal(scale=0.02, size=n), 1)  # ~constant H

    assert scale_from_caption_altitude(up_cam, h_caption) is None


def test_scale_from_caption_altitude_rejects_too_few_finite_samples():
    assert scale_from_caption_altitude(np.array([0.0, 1.0]), np.array([0.0, 1.0])) is None


# --- scale_from_caption_odometry (assignment task 2: "odometry") --------------------------------


def test_scale_from_caption_odometry_recovers_known_scale():
    true_scale = 2.5  # metres per reconstruction unit
    cam_times = np.linspace(0.0, 20.0, 21)
    up = np.array([0.0, 0.0, 1.0])
    horiz_speed_units = 0.4  # reconstruction units/s, straight horizontal path
    cam_positions = np.stack(
        [cam_times * horiz_speed_units, np.zeros_like(cam_times), np.zeros_like(cam_times)], axis=1
    )

    caption_times = np.arange(0.0, 21.0, 1.0)  # 1Hz, like the real SRT
    true_speed_mps = horiz_speed_units * true_scale
    caption_hspeed = np.full_like(caption_times, true_speed_mps)

    est = scale_from_caption_odometry(cam_times, cam_positions, up, caption_times, caption_hspeed)

    assert est is not None
    assert est.method == "odometry"
    assert est.scale == pytest.approx(true_scale, rel=1e-6)
    assert est.residual_m is None  # single ratio, no independent redundancy to check it against


def test_scale_from_caption_odometry_reports_agreement_with_reference_scale():
    cam_times = np.linspace(0.0, 10.0, 11)
    up = np.array([0.0, 0.0, 1.0])
    cam_positions = np.stack(
        [cam_times * 1.0, np.zeros_like(cam_times), np.zeros_like(cam_times)], axis=1
    )
    caption_times = np.arange(0.0, 11.0, 1.0)
    caption_hspeed = np.full_like(caption_times, 2.0)  # 20m over 10s

    est = scale_from_caption_odometry(
        cam_times, cam_positions, up, caption_times, caption_hspeed, reference_scale=2.0
    )

    assert est is not None
    assert "altitude estimate's scale" in est.notes


def test_scale_from_caption_odometry_rejects_static_camera():
    cam_times = np.linspace(0.0, 10.0, 11)
    cam_positions = np.zeros((11, 3))  # no motion -- can't calibrate an odometry scale
    caption_times = np.arange(0.0, 11.0, 1.0)
    caption_hspeed = np.full_like(caption_times, 1.0)

    est = scale_from_caption_odometry(
        cam_times, cam_positions, np.array([0.0, 0.0, 1.0]), caption_times, caption_hspeed
    )
    assert est is None


# --- up_from_cameras ------------------------------------------------------------------


def _orbit_cam_to_world_rotations(tilt=None, n=24, radius=15.0, height=8.0):
    """Cameras on a circle of `radius` at `height`, looking at the origin, roll 0. Returns (N,3,3)
    cam-to-world rotations (columns = world-frame directions of the camera's x/y/z axes,
    OpenCV convention x=right, y=down, z=forward), optionally rotated by `tilt` (world rotation)."""
    world_up = np.array([0.0, 1.0, 0.0])
    angles = np.linspace(0, 2 * np.pi, n, endpoint=False)
    rotations = []
    for a in angles:
        pos = np.array([radius * np.cos(a), height, radius * np.sin(a)])
        forward = -pos / np.linalg.norm(pos)  # target is the origin
        right = np.cross(forward, world_up)
        right = right / np.linalg.norm(right)
        down = np.cross(forward, right)
        R_cw = np.stack([right, down, forward], axis=1)  # columns = world-frame axis directions
        if tilt is not None:
            R_cw = tilt @ R_cw
        rotations.append(R_cw)
    return np.array(rotations)


def _angle_deg(a, b):
    cos_theta = np.clip(abs(np.dot(a, b)), -1.0, 1.0)  # abs: up sign is disambiguated separately
    return np.degrees(np.arccos(cos_theta))


def test_up_from_cameras_recovers_world_up():
    rotations = _orbit_cam_to_world_rotations()
    up = up_from_cameras(rotations)
    assert _angle_deg(up, np.array([0.0, 1.0, 0.0])) < 1.0
    assert (
        np.dot(up, [0, 1, 0]) > 0
    )  # sign disambiguation picked "actually up", not "actually down"


def test_up_from_cameras_recovers_up_under_random_world_tilt():
    rng = np.random.default_rng(5)
    tilt = _random_rotation(rng)
    rotations = _orbit_cam_to_world_rotations(tilt=tilt)
    true_up = tilt @ np.array([0.0, 1.0, 0.0])

    up = up_from_cameras(rotations)

    assert _angle_deg(up, true_up) < 1.0
    assert np.dot(up, true_up) > 0


# --- similarity_matrix / apply_similarity / transform_camera ------------------------


def test_similarity_and_camera_transform_are_consistent():
    """A point and a camera pose transformed by the same similarity matrix must still agree:
    projecting the transformed point through the transformed pose reproduces the old camera-frame
    coordinates, scaled by s (scale drops out of a pinhole projection's x/z, y/z ratios)."""
    rng = np.random.default_rng(6)
    s, t = 2.5, np.array([1.0, 2.0, -3.0])
    R_sim = _random_rotation(rng)
    M = similarity_matrix(s, R_sim, t)

    R_wc_old = _random_rotation(rng)
    t_wc_old = rng.normal(size=3)
    point = rng.normal(size=3)

    x_cam_old = R_wc_old @ point + t_wc_old

    point_new = apply_similarity(point[None, :], M)[0]
    R_wc_new, t_wc_new = transform_camera(R_wc_old, t_wc_old, M)
    x_cam_new = R_wc_new @ point_new + t_wc_new

    assert x_cam_new == pytest.approx(s * x_cam_old, abs=1e-8)
    assert R_wc_new @ R_wc_new.T == pytest.approx(np.eye(3), abs=1e-8)  # still a proper rotation


def test_similarity_matrix_shape_and_identity():
    M = similarity_matrix(1.0, np.eye(3), np.zeros(3))
    assert M.shape == (4, 4)
    assert M == pytest.approx(np.eye(4))


# --- align_to_gltf --------------------------------------------------------------------


def test_align_to_gltf_maps_up_and_north():
    up = np.array([0.1, 0.9, 0.2])
    up = up / np.linalg.norm(up)
    north = np.array([1.0, 0.0, 0.3])

    R = align_to_gltf(up, north)

    assert R @ up == pytest.approx([0.0, 1.0, 0.0], abs=1e-8)
    north_horiz = north - np.dot(north, up) * up
    north_horiz /= np.linalg.norm(north_horiz)
    assert R @ north_horiz == pytest.approx([0.0, 0.0, -1.0], abs=1e-8)
    assert np.linalg.det(R) == pytest.approx(1.0)


def test_align_to_gltf_deterministic_without_north():
    up = np.array([0.2, 0.8, -0.3])
    up = up / np.linalg.norm(up)
    R1 = align_to_gltf(up)
    R2 = align_to_gltf(up)
    assert R1 == pytest.approx(R2)
    assert R1 @ up == pytest.approx([0.0, 1.0, 0.0], abs=1e-8)


# --- ScaleEstimate / choose_scale --------------------------------------------------------


def test_choose_scale_ranks_by_method_quality():
    estimates = [
        ScaleEstimate("gps", scale=1.0, residual_m=2.0),
        ScaleEstimate("known_dimension", scale=1.1, residual_m=0.01),
        ScaleEstimate("none", scale=1.0, residual_m=None),
    ]
    assert choose_scale(estimates).method == "known_dimension"


def test_choose_scale_prefers_scalebar_over_everything():
    estimates = [
        ScaleEstimate("known_dimension", scale=1.1, residual_m=0.01),
        ScaleEstimate("scalebar_aruco", scale=1.05, residual_m=0.005),
    ]
    assert choose_scale(estimates).method == "scalebar_aruco"


def test_choose_scale_empty_raises():
    with pytest.raises(ValueError):
        choose_scale([])


# --- interpolate_positions_at_times (plan §1a: "interpolate if timestamps differ") -------------


def test_interpolate_positions_at_times_linear():
    times_known = np.array([0.0, 1.0, 2.0])
    positions_known = np.array([[0.0, 0.0, 0.0], [1.0, 2.0, 0.0], [2.0, 4.0, 0.0]])
    out = interpolate_positions_at_times(times_known, positions_known, np.array([0.5, 1.5]))
    np.testing.assert_allclose(out, [[0.5, 1.0, 0.0], [1.5, 3.0, 0.0]])


def test_interpolate_positions_at_times_clamps_outside_range():
    times_known = np.array([1.0, 0.0, 2.0])  # deliberately unsorted -- must sort internally
    positions_known = np.array([[10.0, 0.0, 0.0], [0.0, 0.0, 0.0], [20.0, 0.0, 0.0]])
    out = interpolate_positions_at_times(times_known, positions_known, np.array([-5.0, 50.0]))
    np.testing.assert_allclose(out, [[0.0, 0.0, 0.0], [20.0, 0.0, 0.0]])  # clamped to endpoints


# --- rigid_align_points (plan §1a: rotation + translation only, Umeyama without scale) ----------


def test_rigid_align_points_recovers_known_rotation_translation_with_noise():
    rng = np.random.default_rng(0)
    src = rng.normal(scale=5.0, size=(20, 3))
    angle = np.radians(20.0)
    R_true = np.array(
        [[np.cos(angle), 0, np.sin(angle)], [0, 1, 0], [-np.sin(angle), 0, np.cos(angle)]]
    )
    t_true = np.array([1.5, -0.5, 2.0])
    noise = rng.normal(scale=0.01, size=src.shape)
    dst = (R_true @ src.T).T + t_true + noise

    R, t, residual = rigid_align_points(src, dst)

    assert R == pytest.approx(R_true, abs=0.01)
    assert t == pytest.approx(t_true, abs=0.02)
    assert residual < 0.02  # small, from the noise only
    # a proper rotation, not a reflection or a scaled fit
    assert R @ R.T == pytest.approx(np.eye(3), abs=1e-6)
    assert np.linalg.det(R) == pytest.approx(1.0, abs=1e-6)


def test_rigid_align_points_never_introduces_scale():
    """A pure scale mismatch (no rotation/translation) must NOT be absorbed into R/t -- the
    residual should stay large, honestly reporting the disagreement, since rigid_align_points has
    no scale term by construction (plan §1a: "Umeyama without scale")."""
    rng = np.random.default_rng(1)
    src = rng.normal(scale=5.0, size=(20, 3))
    dst = src * 1.5  # pure scale, no rotation/translation

    _, _, residual = rigid_align_points(src, dst)

    assert residual > 1.0  # a rigid-only fit can't explain a 50% scale mismatch


# --- scale_agreement (plan §1a's 2% rule) -------------------------------------------------------


def test_scale_agreement_within_threshold():
    result = scale_agreement(preview_scale=1.00, full_scale=1.01, threshold_pct=2.0)
    assert result["agrees"] is True
    assert result["diff_pct"] == pytest.approx(1.0)


def test_scale_agreement_beyond_threshold():
    result = scale_agreement(preview_scale=1.00, full_scale=1.05, threshold_pct=2.0)
    assert result["agrees"] is False
    assert result["diff_pct"] == pytest.approx(5.0)


def test_scale_agreement_exactly_at_threshold_agrees():
    # construct threshold_pct == diff_pct exactly (float-exact by re-using the computed value) so
    # the boundary check isn't at the mercy of floating-point rounding of the input scales
    diff_pct = scale_agreement(preview_scale=1.00, full_scale=1.05)["diff_pct"]
    result = scale_agreement(preview_scale=1.00, full_scale=1.05, threshold_pct=diff_pct)
    assert result["agrees"] is True  # "differ by more than 2%" -- an exact-equal diff still agrees


def test_scale_agreement_records_both_scales():
    result = scale_agreement(preview_scale=2.0, full_scale=2.5)
    assert result["preview_scale"] == 2.0
    assert result["full_scale"] == 2.5


def test_scale_from_caption_altitude_rejects_a_fit_that_does_not_explain_h():
    """Camera height uncorrelated with H (wrong up axis, drifting poses) must not yield a scale."""
    rng = np.random.default_rng(13)
    up_cam = rng.normal(size=60)
    h_caption = np.round(1.0 + 0.4 * np.sin(np.linspace(0, 6, 60)), 1)

    assert scale_from_caption_altitude(up_cam, h_caption) is None
