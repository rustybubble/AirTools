import numpy as np
import pytest

from pipeline.depthfusion_worker import (
    _CLOUD_PROPS,
    apply_alignment,
    fill_views,
    fit_alignment,
    geometry_mask,
    hybrid_depth,
    read_dmap,
    read_mvs_cloud,
    refit_voxel,
    sparse_prompt,
    tsdf_params,
    tsdf_passes,
    write_union_cloud,
)


@pytest.mark.parametrize("method", ["scale", "affine", "plane"])
def test_fit_alignment_recovers_scale_despite_outliers(method):
    rng = np.random.default_rng(0)
    d = rng.uniform(1.0, 4.0, 300)
    uv = rng.uniform(-1, 1, (300, 2))
    z = 5.4 * d * (1 + rng.normal(0, 0.005, 300))
    z[:30] *= 1.5  # 10% gross outliers (bad SfM points / depth edges)
    params, fit = fit_alignment(d, z, uv, method, disparity=False)
    rel = np.abs(fit - z) / z
    assert np.median(rel) < 0.01
    assert params[0] == pytest.approx(5.4, rel=0.02)


def test_fit_alignment_disparity_affine_and_apply_roundtrip():
    rng = np.random.default_rng(1)
    z_true = rng.uniform(1.0, 5.0, (20, 30))
    disp = (1.0 / z_true - 0.05) / 2.0  # relative model: 1/z = 2*disp + 0.05
    iy, ix = rng.integers(0, 20, 200), rng.integers(0, 30, 200)
    uv = np.stack([ix, iy], 1) / np.array([29, 19]) * 2 - 1
    params, _ = fit_alignment(disp[iy, ix], z_true[iy, ix], uv, "affine", disparity=True)
    assert params == pytest.approx([2.0, 0.05], rel=1e-3)
    z = apply_alignment(disp, params, "affine", True, 30, 20)
    assert np.allclose(z, z_true, rtol=1e-3)


def test_geometry_mask_drops_depth_edges_and_grazing_surfaces():
    H, W = 40, 60
    K = np.array([[50.0, 0, 30], [0, 50.0, 20], [0, 0, 1]])
    z = np.full((H, W), 2.0)
    z[:, 30:] = 3.0  # a step edge at x=30
    m = geometry_mask(z, K, grazing_deg=80, edge_rel=0.04)
    assert not m[:, 28:32].any() and m[:, :27].all() and m[:, 33:].all()
    # a plane seen almost edge-on: depth grows fast along x
    _, u = np.mgrid[0:H, 0:W].astype(float)
    x = (u - 30) / 50
    z_graze = 2.0 / (1 - 8.0 * x).clip(0.05)
    m2 = geometry_mask(np.where(8.0 * x < 0.95, z_graze, 0), K, grazing_deg=80, edge_rel=10)
    assert m2[:, :10].all() and not m2[:, 31:33].any()


def test_sparse_prompt_interpolates_a_plane_and_fills_outside_the_hull():
    rng = np.random.default_rng(2)
    uv = rng.uniform([200, 100], [1700, 950], (300, 2))
    z = 1.0 + uv[:, 0] / 1920  # a tilted plane
    p = sparse_prompt(uv, z, 1920, 1080)
    assert p.shape == (144, 256) and np.isfinite(p).all()
    gx = (np.arange(256) + 0.5) * 1920 / 256
    assert np.allclose(p[72, 40:220], 1.0 + gx[40:220] / 1920, atol=1e-3)


@pytest.mark.parametrize("correct", ["plane", "field"])
def test_hybrid_depth_keeps_mvs_inliers_and_fills_holes_with_corrected_mono(correct):
    rng = np.random.default_rng(3)
    H, W = 60, 80
    _, u = np.mgrid[0:H, 0:W].astype(np.float32)
    z_true = 2.0 + u / W  # a tilted wall
    z_mono = z_true / 1.1 * (1 + 0.05 * u / W)  # wrong scale plus a tilted bias
    z_mvs = z_true * (1 + rng.normal(0, 0.002, (H, W)))
    z_mvs[:, 60:] = 0  # textureless: no PatchMatch estimate
    z_mvs[5:10, 5:10] *= 1.3  # gross outliers
    conf = np.full((H, W), 0.9, np.float32)
    z, inl, _ = hybrid_depth(
        z_mono.astype(np.float32), z_mvs.astype(np.float32), conf,
        conf_min=0.5, tol=0.03, correct=correct, sigma_px=10,
    )  # fmt: skip
    assert not inl[5:10, 5:10].any() and inl[20:, :55].all() and not inl[:, 60:].any()
    assert np.allclose(z[inl], z_mvs[inl])
    assert np.abs(z / z_true - 1)[:, 60:].max() < 0.01  # the hole is filled at the MVS scale


def test_read_dmap_parses_the_openmvs_raw_format(tmp_path):
    import struct

    dw, dh = 4, 3
    name = b"../sfm/dense/images/0496.jpg"
    depth = np.arange(12, dtype="<f4").reshape(dh, dw)
    conf = np.full((dh, dw), 0.5, "<f4")
    b = struct.pack("<HBBIIIIff", 0x5244, 7, 0, dw, dh, dw, dh, 1.0, 2.0)
    b += struct.pack("<H", len(name)) + name + struct.pack("<I2I", 2, 5, 6)
    b += np.zeros(21, "<f8").tobytes() + depth.tobytes() + np.zeros(3 * 12, "<f4").tobytes()
    b += conf.tobytes()
    (tmp_path / "depth0001.dmap").write_bytes(b)
    nm, d, c = read_dmap(tmp_path / "depth0001.dmap")
    assert nm == "0496.jpg" and np.array_equal(d, depth) and np.array_equal(c, conf)


def test_union_cloud_roundtrip_and_fill_views(tmp_path):
    K = np.array([[50.0, 0, 40], [0, 50.0, 30], [0, 0, 1]])
    frames = {7: (np.full((60, 80), 2.0, np.float32), K, np.eye(4))}  # a wall at z=2
    X = np.array([[0.0, 0.0, 2.0], [0.0, 0.0, 3.0], [0.0, 0.0, -2.0]])
    assert fill_views(X, frames) == [[7], [], []]
    props = _CLOUD_PROPS.replace(" property", "\nproperty")
    header = ["ply", "format binary_little_endian 1.0", "element vertex 0", props, "end_header"]
    base = tmp_path / "base.ply"
    base.write_text("\n".join(header) + "\n")
    h, body, _ = read_mvs_cloud(base)
    write_union_cloud(tmp_path / "u.ply", h, body, 0, X[:2], np.zeros((2, 3)), [[7], [1, 2]])
    h2, _, xyz2 = read_mvs_cloud(tmp_path / "u.ply")
    assert "element vertex 2" in h2 and np.allclose(xyz2, X[:2])


def test_tsdf_params_keeps_room_defaults_and_grows_for_a_building():
    room = {0: np.full((10, 10), 2.0, np.float32)}  # 2 m away, 1 unit = 1 m
    assert tsdf_params(room, 1.0, 0.01, 4.0, 0.02) == pytest.approx((0.01, 4.0, 0.02))
    far = {0: np.linspace(20.0, 60.0, 400, dtype=np.float32).reshape(20, 20)}  # a drone orbit
    voxel, depth_max, fill = tsdf_params(far, 1.0, 0.01, 4.0, 0.02)
    assert depth_max > 60.0  # nothing cut, so TSDF blocks get touched
    assert voxel == pytest.approx(0.04, rel=0.05)  # median 40 m / 1000
    assert fill == pytest.approx(2 * voxel)


def test_refit_voxel_keeps_the_tsdf_under_open3ds_extract_limit():
    assert refit_voxel(0.01, 262_144) is None  # the largest grid open3d 0.20 extracts
    coarser = refit_voxel(0.01, 356_283)  # the hospital facade that segfaulted
    assert coarser > 0.01 * np.sqrt(356_283 / 262_144)  # blocks ~ 1/voxel^2, with margin
    assert 356_283 * (0.01 / coarser) ** 2 < 262_144


def test_tsdf_passes_drops_a_frame_with_no_depth_under_the_cut_off():
    z = np.full((4, 4), 9.0, np.float32)  # gt-lcc: a frame looking past the pavilion
    assert tsdf_passes([z, z], z < 4.0) == []  # open3d aborts the whole TSDF on an empty image
    near = z.copy()
    near[0, 0] = 2.0
    out = tsdf_passes([near], near < 4.0)
    assert len(out) == 1 and out[0].dtype == np.float32 and out[0].sum() == 2.0
