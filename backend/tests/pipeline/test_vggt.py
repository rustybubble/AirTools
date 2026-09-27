import pytest

pytest.importorskip(
    "pycolmap"
)  # pipeline.vggt needs the `pipeline` extra (uv sync --extra pipeline)

import pycolmap

from pipeline.vggt import (
    _rescale_reconstruction,
    env_python,
    rescale_camera,
    select_chunk,
    should_fallback,
)

# --- should_fallback -----------------------------------------------------------------------


def test_should_fallback_true_below_threshold():
    assert should_fallback(30, 100) is True  # 30% registered


def test_should_fallback_false_at_or_above_threshold():
    assert should_fallback(60, 100) is False  # exactly the 60% bar -- pycolmap's result stands
    assert should_fallback(70, 100) is False


def test_should_fallback_true_on_zero_images():
    assert should_fallback(0, 0) is True


# --- select_chunk ---------------------------------------------------------------------------


def test_select_chunk_noop_under_ceiling():
    paths = list(range(10))
    assert select_chunk(paths, max_frames=64) == paths


def test_select_chunk_evenly_subsamples_and_keeps_endpoints():
    paths = list(range(200))
    chunk = select_chunk(paths, max_frames=64)
    assert len(chunk) <= 64
    assert chunk[0] == 0
    assert chunk[-1] == 199
    assert chunk == sorted(chunk)  # frame order preserved, not shuffled


# --- rescale_camera --------------------------------------------------------------------------


def test_rescale_camera_scales_intrinsics_by_resolution_ratio():
    fx, fy, cx, cy, w, h = rescale_camera(
        fx=500.0, fy=500.0, cx=259.0, cy=259.0, w=518, h=518, target_w=1920, target_h=1080
    )
    assert fx == pytest.approx(500.0 * 1920 / 518)
    assert fy == pytest.approx(500.0 * 1080 / 518)
    assert cx == pytest.approx(259.0 * 1920 / 518)
    assert cy == pytest.approx(259.0 * 1080 / 518)
    assert (w, h) == (1920, 1080)


def test_rescale_camera_identity_when_already_target_size():
    out = rescale_camera(
        fx=1000.0, fy=1000.0, cx=960.0, cy=540.0, w=1920, h=1080, target_w=1920, target_h=1080
    )
    assert out == (1000.0, 1000.0, 960.0, 540.0, 1920, 1080)


# --- env_python ------------------------------------------------------------------------------


def test_env_python_none_when_unset(monkeypatch):
    monkeypatch.delenv("AIRTOOLS_VGGT_ENV", raising=False)
    assert env_python() is None


def test_env_python_from_env_var(monkeypatch, tmp_path):
    monkeypatch.setenv("AIRTOOLS_VGGT_ENV", str(tmp_path))
    assert env_python() == tmp_path / "bin" / "python"


# --- _rescale_reconstruction (real pycolmap Reconstruction, VGGT's own output format) --------


def test_rescale_reconstruction_updates_a_mismatched_camera():
    recon = pycolmap.Reconstruction()
    cam = pycolmap.Camera.create_from_model_id(1, pycolmap.CameraModelId.PINHOLE, 300.0, 518, 518)
    recon.add_camera(cam)

    _rescale_reconstruction(recon, true_w=1920, true_h=1080)

    updated = recon.cameras[1]
    assert (updated.width, updated.height) == (1920, 1080)
    assert updated.params[0] == pytest.approx(300.0 * 1920 / 518)
    assert updated.params[1] == pytest.approx(300.0 * 1080 / 518)


def test_rescale_reconstruction_is_a_noop_when_already_correct():
    recon = pycolmap.Reconstruction()
    cam = pycolmap.Camera.create_from_model_id(
        1, pycolmap.CameraModelId.PINHOLE, 1000.0, 1920, 1080
    )
    recon.add_camera(cam)
    original_params = recon.cameras[1].params.copy()

    _rescale_reconstruction(recon, true_w=1920, true_h=1080)

    assert recon.cameras[1].params == pytest.approx(original_params)
    assert (recon.cameras[1].width, recon.cameras[1].height) == (1920, 1080)
