import json

import numpy as np
import pytest
import trimesh
from PIL import Image

from pipeline.package import Camera, write_cameras_json
from pipeline.preview import render
from pipeline.qa import default_ids, interpolate_camera, photo_consistency, psnr, ssim

_TEXTURE_COLOR = (200, 80, 40)


def _textured_box(extents=(4.0, 2.0, 2.0)) -> trimesh.Trimesh:
    box = trimesh.creation.box(extents=extents)
    image = Image.new("RGB", (8, 8), _TEXTURE_COLOR)
    uv = np.zeros((len(box.vertices), 2))
    material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=image, baseColorFactor=[1, 1, 1, 1]
    )
    box.visual = trimesh.visual.TextureVisuals(uv=uv, image=image, material=material)
    return box


def _camera(frame_id: str, pos: np.ndarray, width=200, height=150) -> Camera:
    forward = -pos / np.linalg.norm(pos)
    up = np.array([0.0, 1.0, 0.0])
    right = np.cross(forward, up)
    down = np.cross(forward, right)
    R_cw = np.stack([right, down, forward], axis=1)
    R_wc = R_cw.T
    t = -R_wc @ pos
    return Camera(
        id=frame_id,
        file=f"frames/{frame_id}.jpg",
        thumb=f"thumbs/{frame_id}.jpg",
        R=R_wc,
        t=t,
        fx=300.0,
        fy=300.0,
        cx=width / 2,
        cy=height / 2,
        w=width,
        h=height,
    )


# --- psnr / ssim -----------------------------------------------------------------------------


def test_psnr_identical_images_is_inf():
    img = np.random.default_rng(0).integers(0, 255, size=(20, 20, 3), dtype=np.uint8)
    assert psnr(img, img) == float("inf")


def test_psnr_finite_and_decreases_with_more_noise():
    rng = np.random.default_rng(1)
    a = rng.integers(0, 255, size=(20, 20, 3), dtype=np.uint8).astype(np.float64)
    small_noise = a + rng.normal(scale=2.0, size=a.shape)
    big_noise = a + rng.normal(scale=40.0, size=a.shape)
    assert psnr(a, small_noise) > psnr(a, big_noise)


def test_psnr_nan_when_mask_empty():
    img = np.zeros((10, 10, 3), dtype=np.uint8)
    mask = np.zeros((10, 10), dtype=bool)
    assert np.isnan(psnr(img, img, mask=mask))


def test_ssim_identical_images_is_one():
    rng = np.random.default_rng(2)
    img = rng.integers(0, 255, size=(40, 40, 3), dtype=np.uint8)
    assert ssim(img, img) == pytest.approx(1.0, abs=1e-9)


def test_ssim_lower_for_dissimilar_images():
    rng = np.random.default_rng(3)
    a = rng.integers(0, 255, size=(40, 40, 3), dtype=np.uint8)
    b = rng.integers(0, 255, size=(40, 40, 3), dtype=np.uint8)
    assert ssim(a, a) > ssim(a, b)


# --- interpolate_camera ------------------------------------------------------------------------


def test_interpolate_camera_midpoint_between_two_neighbours():
    cams = {
        "0001": _camera("0001", np.array([0.0, 0.0, 10.0])),
        "0005": _camera("0005", np.array([4.0, 0.0, 10.0])),
    }

    cam = interpolate_camera(cams, "0003")  # exactly halfway by frame index

    assert cam.position == pytest.approx([2.0, 0.0, 10.0], abs=1e-6)
    assert cam.R @ cam.R.T == pytest.approx(np.eye(3), abs=1e-8)  # still a proper rotation
    assert cam.fx == cams["0001"].fx


def test_interpolate_camera_falls_back_at_sequence_edge():
    cams = {"0005": _camera("0005", np.array([0.0, 0.0, 10.0]))}
    cam = interpolate_camera(cams, "0001")  # nothing before it -- reuse nearest
    assert cam.id == "0005"


# --- default_ids -------------------------------------------------------------------------------


def _write_package(tmp_path, cams: dict[str, Camera]) -> None:
    mesh = _textured_box()
    mesh.export(tmp_path / "mesh.glb")
    write_cameras_json(list(cams.values()), tmp_path / "cameras.json")


def test_default_ids_includes_holdout_frames_not_registered(tmp_path):
    cams = {"0001": _camera("0001", np.array([0.0, 0.0, 10.0]))}
    _write_package(tmp_path, cams)
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()
    for i in range(1, 11):
        Image.new("RGB", (4, 4)).save(frames_dir / f"{i:04d}.jpg")

    ids = default_ids(tmp_path, frames_dir, holdout_every=5)

    assert "0001" in ids  # registered
    assert "0005" in ids and "0010" in ids  # every 5th frame, held out (not registered)
    assert "0002" not in ids  # neither registered nor a holdout stride hit


def test_default_ids_disjoint_from_run_pys_stride_selected_registered_set(tmp_path):
    # mirrors pipeline.run.run()'s own frame partition exactly: it registers (feeds to SfM) every
    # holdout_every-th frame via `raw_frame_paths[::holdout_every]` (0-indexed stride from 0) --
    # confirms qa.default_ids's own holdout stride (1-indexed, `(i+1) % holdout_every == 0`) never
    # picks one of those as "held out" (it would silently defeat the whole point: scoring a frame
    # that was actually part of the training set as if it were unseen).
    holdout_every = 5
    n = 40
    all_ids = [f"{i:04d}" for i in range(1, n + 1)]
    registered_ids = all_ids[::holdout_every]  # exactly pipeline.run's nominal-frame slice
    cams = {rid: _camera(rid, np.array([float(int(rid)), 0.0, 10.0])) for rid in registered_ids}
    _write_package(tmp_path, cams)
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()
    for fid in all_ids:
        Image.new("RGB", (4, 4)).save(frames_dir / f"{fid}.jpg")

    ids = default_ids(tmp_path, frames_dir, holdout_every=holdout_every)
    held_out = set(ids) - set(registered_ids)

    assert held_out, "expected some genuine held-out frames"
    assert held_out.isdisjoint(registered_ids)


def test_default_ids_max_ids_subsamples(tmp_path):
    cams = {f"{i:04d}": _camera(f"{i:04d}", np.array([float(i), 0.0, 10.0])) for i in range(1, 21)}
    _write_package(tmp_path, cams)
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()

    ids = default_ids(tmp_path, frames_dir, holdout_every=0, max_ids=5)

    assert len(ids) <= 5


# --- photo_consistency ---------------------------------------------------------------------


def test_photo_consistency_scores_near_perfect_against_its_own_render(tmp_path):
    cam = _camera("0001", np.array([0.0, 0.0, 10.0]))
    _write_package(tmp_path, {"0001": cam})

    mesh = _textured_box()
    rendered, covered = render(mesh, cam, width=200, height=150)
    assert covered.any()

    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()
    Image.fromarray(rendered).save(frames_dir / "0001.jpg")

    out_dir = tmp_path / "qa_out"
    result = photo_consistency(
        tmp_path, frames_dir, ["0001"], out_dir=out_dir, width=200, height=150
    )

    assert result["per_frame"][0]["held_out"] is False
    assert result["mean_coverage"] == pytest.approx(covered.mean(), abs=1e-6)
    # JPEG re-encoding a small, hard-edged (no antialiasing) render is lossy right at the
    # silhouette boundary -- "near-perfect", not identical/inf, is the honest bar here.
    assert result["mean_psnr"] > 25.0
    assert result["mean_ssim"] > 0.85
    assert (out_dir / "0001.png").exists()


def test_photo_consistency_resolves_revision_named_files_from_scene_json(tmp_path):
    """Regression test: caught on a real hfbox run -- qa.py's mesh.glb/cameras.json used to be
    hardcoded, so it silently failed (run() swallows the exception) on the plan §1a revision-named
    layout (mesh.r<rev>.glb, cameras.r<rev>.json) every published package actually uses now."""
    cam = _camera("0001", np.array([0.0, 0.0, 10.0]))
    mesh = _textured_box()
    mesh.export(tmp_path / "mesh.r2.glb")
    write_cameras_json([cam], tmp_path / "cameras.r2.json")
    (tmp_path / "scene.json").write_text(
        json.dumps({"mesh": {"file": "mesh.r2.glb"}, "cameras": "cameras.r2.json"})
    )

    rendered, covered = render(mesh, cam, width=200, height=150)
    assert covered.any()
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()
    Image.fromarray(rendered).save(frames_dir / "0001.jpg")

    result = photo_consistency(tmp_path, frames_dir, ["0001"], width=200, height=150)

    assert result["per_frame"][0]["held_out"] is False
    assert result["mean_psnr"] > 25.0


def test_photo_consistency_flags_holdout_frames(tmp_path):
    cams = {
        "0001": _camera("0001", np.array([0.0, 0.0, 10.0])),
        "0003": _camera("0003", np.array([0.0, 0.0, 10.0])),
    }
    _write_package(tmp_path, cams)
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()
    for fid in ("0001", "0002", "0003"):
        Image.new("RGB", (200, 150), _TEXTURE_COLOR).save(frames_dir / f"{fid}.jpg")

    result = photo_consistency(tmp_path, frames_dir, ["0001", "0002"], width=200, height=150)

    per_id = {row["id"]: row for row in result["per_frame"]}
    assert per_id["0001"]["held_out"] is False
    assert per_id["0002"]["held_out"] is True


def test_photo_consistency_reports_train_and_held_out_separately(tmp_path):
    cams = {
        "0001": _camera("0001", np.array([0.0, 0.0, 10.0])),
        "0003": _camera("0003", np.array([0.0, 0.0, 10.0])),
    }
    _write_package(tmp_path, cams)
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()
    for fid in ("0001", "0002", "0003"):
        Image.new("RGB", (200, 150), _TEXTURE_COLOR).save(frames_dir / f"{fid}.jpg")

    result = photo_consistency(tmp_path, frames_dir, ["0001", "0002"], width=200, height=150)

    assert result["train"]["n"] == 1  # 0001 only
    assert result["held_out"]["n"] == 1  # 0002 only
    assert result["train"]["mean_psnr"] == result["per_frame"][0]["psnr"]
    # combined top-level aggregate still covers both, unchanged by the split
    assert result["mean_coverage"] == pytest.approx(
        np.mean([row["coverage"] for row in result["per_frame"]])
    )


def test_photo_consistency_warns_and_skips_missing_photo(tmp_path, caplog):
    cam = _camera("0001", np.array([0.0, 0.0, 10.0]))
    _write_package(tmp_path, {"0001": cam})
    frames_dir = tmp_path / "frames"
    frames_dir.mkdir()

    result = photo_consistency(tmp_path, frames_dir, ["0001"], width=200, height=150)

    assert result["per_frame"] == []
    assert result["mean_psnr"] is None
