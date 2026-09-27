import json

import numpy as np
import pytest
import trimesh.transformations as tf

from pipeline.experiments.bench.harness import (
    _append_row,
    _undistort_simple_radial,
    detail_scores,
    erode,
    geometry_metrics,
    id_times,
    laplacian_var,
    nn_dist,
    robust_sim3,
    voxel_downsample,
)


def test_id_times_fixed_fps_and_explicit_map():
    assert id_times(["0001", "0011"], None, 10.0) == pytest.approx([0.0, 1.0])
    assert id_times(["p0002"], {"p0002": 3.25}, None) == pytest.approx([3.25])


def test_robust_sim3_recovers_known_similarity_despite_an_outlier():
    rng = np.random.default_rng(0)
    src = rng.normal(size=(40, 3))
    R = tf.random_rotation_matrix(rng.random(3))[:3, :3]
    s, t = 0.37, np.array([1.0, -2.0, 0.5])
    dst = s * (R @ src.T).T + t + rng.normal(scale=1e-3, size=(40, 3))
    dst[7] += 5.0  # one mis-registered camera

    s_fit, R_fit, t_fit, resid, keep = robust_sim3(src, dst)

    assert not keep[7] and keep.sum() == 39
    assert s_fit == pytest.approx(s, rel=1e-2)
    assert np.allclose(R_fit, R, atol=1e-2)
    assert np.allclose(t_fit, t, atol=1e-2)
    assert resid < 5e-3


def test_nn_dist_matches_brute_force():
    rng = np.random.default_rng(1)
    pts, q = rng.random((20_000, 3)), rng.random((300, 3))
    brute = np.sqrt(((q[:, None] - pts[None]) ** 2).sum(-1)).min(1)
    assert nn_dist(pts, q) == pytest.approx(brute, abs=1e-6)


def test_geometry_metrics_on_offset_planes():
    """A plane vs the same plane shifted 3 cm: every distance is 3 cm, so F@2cm = 0, F@5cm = 1."""
    g = np.linspace(0, 1, 60)
    xx, yy = np.meshgrid(g, g)
    ref = np.stack([xx.ravel(), yy.ravel(), np.zeros(xx.size)], 1)
    cand = ref + [0, 0, 0.03]

    m = geometry_metrics(cand, ref)

    assert m["chamfer_m"] == pytest.approx(0.03, abs=1e-6)
    assert m["f_2cm"] == 0.0 and m["f_5cm"] == 1.0


def test_geometry_metrics_drops_candidate_points_outside_reference_box():
    ref = np.random.default_rng(2).random((5000, 3))
    cand = np.vstack([ref[:100], [[10.0, 10.0, 10.0]]])  # far-away point the reference never saw
    m = geometry_metrics(cand, ref)
    assert m["n_cand"] == 100 and m["precision_2cm"] == 1.0


def test_laplacian_var_zero_on_flat_and_positive_on_texture():
    mask = np.ones((50, 50), bool)
    assert laplacian_var(np.full((50, 50), 7.0), mask) == 0.0
    checker = (np.indices((50, 50)).sum(0) % 2) * 255.0
    assert laplacian_var(checker, mask) > 0


def test_erode_shrinks_mask_by_radius():
    m = np.zeros((20, 20), bool)
    m[5:15, 5:15] = True
    assert erode(m, 2).sum() == 6 * 6


def test_voxel_downsample_one_point_per_cell():
    pts = np.array([[0.001, 0, 0], [0.002, 0, 0], [0.02, 0, 0]])
    assert len(voxel_downsample(pts, 0.01)) == 2


def test_undistort_simple_radial_identity_when_k_zero():
    img = (np.random.default_rng(3).random((40, 60, 3)) * 255).astype(np.uint8)
    out = _undistort_simple_radial(img, 50.0, 29.5, 19.5, 0.0)
    assert np.array_equal(out, img)


def test_append_row_writes_header_once(tmp_path):
    r = {
        "name": "x",
        "geometry": {"chamfer_m": 0.012, "f_2cm": 0.5, "f_5cm": 0.8},
        "speed": {"wall_s": 100, "est_total_s": 200},
        "alignment": {"residual_m": 0.01, "cand_size_error_pct": 1.5},
        "all": {
            "coverage": 0.5,
            "psnr": 20.0,
            "ssim": 0.7,
            "sharpness_ratio": 0.6,
            "lap_corr": 0.4,
        },
        "held_out": {"psnr": 19.0},
        "registration_coverage": 1.0,
        "quest": {"triangles": 1, "texture_px": 4096, "glb_mb": 5.0, "ok": True},
    }
    path = tmp_path / "results.md"
    _append_row(path, r)
    _append_row(path, json.loads(json.dumps(r)))
    lines = path.read_text().splitlines()
    assert len(lines) == 4 and "| x | 100 | 200 | 0.500 |" in lines[2] and "1.20" in lines[2]


def test_detail_scores_identical_vs_noise():
    rng = np.random.default_rng(4)
    photo = (rng.random((60, 80, 3)) * 255).astype(np.uint8)
    cov = np.ones((60, 80), bool)
    ratio, corr = detail_scores(photo, photo, cov)
    assert ratio == pytest.approx(1.0) and corr == pytest.approx(1.0)
    noise = (rng.random((60, 80, 3)) * 255).astype(np.uint8)
    assert abs(detail_scores(noise, photo, cov)[1]) < 0.2
