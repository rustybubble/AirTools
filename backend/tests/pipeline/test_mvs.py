from pathlib import Path
from unittest.mock import patch

import numpy as np
import pytest
import trimesh

from pipeline import mvs


@pytest.fixture(autouse=True)
def _no_depth_env(monkeypatch):
    """`densify="hybrid"` (the default) falls back to OpenMVS without a depth venv; pin that."""
    monkeypatch.delenv("AIRTOOLS_DEPTH_ENV", raising=False)


def _fake_bin_dir(tmp_path):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    for name in ["InterfaceCOLMAP", "DensifyPointCloud", "ReconstructMesh", "TextureMesh"]:
        (bin_dir / name).write_text("#!/bin/sh\n")
    return bin_dir


def _mock_run_that_writes_output(*args, **kwargs):
    """Stand-in for `subprocess.run`: OpenMVS's real effect is "read some input, write some output
    file(s)" -- write the `-o`/positional output path the fake command was given, so `run_mvs`'s
    per-step existence checks (and the next step's input) see a plausible on-disk result.
    `ReconstructMesh`'s `.ply` output gets a real (tiny) PLY header, not just placeholder bytes --
    `run_mvs`'s auto `--decimate` computation reads it."""
    cmd = args[0]
    if "-o" in cmd:
        out_path = cmd[cmd.index("-o") + 1]
    else:
        out_path = cmd[-1]

    if out_path.endswith(".ply"):
        Path(out_path).write_text(
            "ply\nformat ascii 1.0\nelement vertex 0\nelement face 100\nend_header\n"
        )
    else:
        Path(out_path).write_bytes(b"fake output")

    class _Result:
        returncode = 0
        stdout = "ok"
        stderr = ""

    return _Result()


def test_run_mvs_calls_all_four_steps_in_order(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        result = mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir)

    assert mock_run.call_count == 4
    called_bins = [call.args[0][0] for call in mock_run.call_args_list]
    assert called_bins == [
        str(bin_dir / "InterfaceCOLMAP"),
        str(bin_dir / "DensifyPointCloud"),
        str(bin_dir / "ReconstructMesh"),
        str(bin_dir / "TextureMesh"),
    ]
    assert result.textured_glb.exists()
    assert all(v >= 0.0 for v in result.timings_s.values())


def test_run_mvs_skips_steps_whose_output_already_exists(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    work_dir.mkdir()
    bin_dir = _fake_bin_dir(tmp_path)

    # Pre-seed every expected output -- a full resume of an already-finished run.
    for name in ["scene.mvs", "scene_dense.mvs", "scene_dense_mesh.ply", "textured.glb"]:
        (work_dir / name).write_bytes(b"already done")

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        result = mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir)

    assert mock_run.call_count == 0  # nothing re-run
    assert all(v == 0.0 for v in result.timings_s.values())


def test_run_mvs_force_reruns_every_step_even_if_outputs_exist(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    work_dir.mkdir()
    bin_dir = _fake_bin_dir(tmp_path)
    for name in ["scene.mvs", "scene_dense.mvs", "scene_dense_mesh.ply", "textured.glb"]:
        (work_dir / name).write_bytes(b"stale")

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir, force=True)

    assert mock_run.call_count == 4


def test_run_mvs_texture_command_includes_auto_decimate_and_seam_flags(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir, target_triangles=50)

    texture_cmd = mock_run.call_args_list[-1].args[0]
    assert texture_cmd[0] == str(bin_dir / "TextureMesh")
    ratio = float(texture_cmd[texture_cmd.index("--decimate") + 1])
    assert ratio == pytest.approx(0.5)  # target_triangles=50 / the mock's 100-face ply
    assert texture_cmd[texture_cmd.index("--cost-smoothness-ratio") + 1] == "0.1"
    # seam leveling off: the v2.4.0 binary's leveling blackens the atlas (see run_mvs docstring)
    assert texture_cmd[texture_cmd.index("--global-seam-leveling") + 1] == "0"
    assert texture_cmd[texture_cmd.index("--local-seam-leveling") + 1] == "0"
    assert texture_cmd[texture_cmd.index("--resolution-level") + 1] == "0"  # full-res by default


def test_run_mvs_explicit_decimate_overrides_auto_computation(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir, decimate=0.75)

    texture_cmd = mock_run.call_args_list[-1].args[0]
    assert texture_cmd[texture_cmd.index("--decimate") + 1] == "0.75"


# --- _ply_face_count / _auto_decimate_ratio -----------------------------------------------


def test_ply_face_count_reads_header(tmp_path):
    path = tmp_path / "mesh.ply"
    path.write_text("ply\nformat ascii 1.0\nelement vertex 10\nelement face 12345\nend_header\n")
    assert mvs._ply_face_count(path) == 12345


def test_ply_face_count_raises_without_header(tmp_path):
    path = tmp_path / "mesh.ply"
    path.write_bytes(b"not a ply file")
    with pytest.raises(ValueError):
        mvs._ply_face_count(path)


def test_auto_decimate_ratio_targets_the_budget(tmp_path):
    path = tmp_path / "mesh.ply"
    path.write_text("ply\nformat ascii 1.0\nelement face 1000000\nend_header\n")
    assert mvs._auto_decimate_ratio(path, target_triangles=200_000) == pytest.approx(0.2)


def test_auto_decimate_ratio_clips_to_one_when_mesh_already_under_budget(tmp_path):
    path = tmp_path / "mesh.ply"
    path.write_text("ply\nformat ascii 1.0\nelement face 100\nend_header\n")
    assert mvs._auto_decimate_ratio(path, target_triangles=200_000) == 1.0


def test_auto_decimate_ratio_floors_at_min_ratio(tmp_path):
    path = tmp_path / "mesh.ply"
    path.write_text("ply\nformat ascii 1.0\nelement face 100000000\nend_header\n")
    assert mvs._auto_decimate_ratio(path, target_triangles=1) == mvs._MIN_DECIMATE_RATIO


def test_run_mvs_raises_with_log_tail_on_nonzero_exit(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)

    class _FailResult:
        returncode = 1
        stdout = "some progress\n"
        stderr = "fatal: something broke\n"

    with (
        patch("subprocess.run", return_value=_FailResult()),
        pytest.raises(RuntimeError, match="something broke"),
    ):
        mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir)


# --- crop (Python step between ReconstructMesh and TextureMesh) -----------------------------


def _mock_run_reconstruct_writes_a_real_box(*args, **kwargs):
    """Like `_mock_run_that_writes_output`, but `ReconstructMesh`'s `.ply` is a real,
    trimesh-loadable box -- needed to exercise the crop step, which actually loads it."""
    cmd = args[0]
    out_path = cmd[cmd.index("-o") + 1] if "-o" in cmd else cmd[-1]
    if Path(cmd[0]).name == "ReconstructMesh":
        trimesh.creation.box(extents=(2.0, 2.0, 2.0)).export(out_path)
    elif out_path.endswith(".ply"):
        Path(out_path).write_text(
            "ply\nformat ascii 1.0\nelement vertex 0\nelement face 100\nend_header\n"
        )
    else:
        Path(out_path).write_bytes(b"fake output")

    class _Result:
        returncode = 0
        stdout = "ok"
        stderr = ""

    return _Result()


def _cam_looking_at_origin(n=6, radius=10.0):
    angles = np.linspace(0, 2 * np.pi, n, endpoint=False)
    positions = np.stack([radius * np.cos(angles), np.zeros(n), radius * np.sin(angles)], axis=1)
    forwards = -positions / np.linalg.norm(positions, axis=1, keepdims=True)
    right = np.cross(forwards, np.array([0.0, 1.0, 0.0]))
    right /= np.linalg.norm(right, axis=1, keepdims=True)
    down = np.cross(forwards, right)
    rotations = np.stack([right, down, forwards], axis=1)  # rows = camera axes in world
    intrinsics = np.tile([1400.0, 1400.0, 960.0, 540.0, 1920.0, 1080.0], (n, 1))
    return positions, rotations, intrinsics


def test_run_mvs_crop_auto_textures_the_cropped_ply(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)
    positions, rotations, intrinsics = _cam_looking_at_origin()

    with patch("subprocess.run", side_effect=_mock_run_reconstruct_writes_a_real_box) as mock_run:
        result = mvs.run_mvs(
            dense_dir,
            work_dir,
            bin_dir=bin_dir,
            crop="auto",
            crop_cam_positions=positions,
            crop_cam_rotations=rotations,
            crop_cam_intrinsics=intrinsics,
            crop_up=np.array([0.0, 1.0, 0.0]),
        )

    assert mock_run.call_count == 4  # crop itself doesn't spawn a subprocess
    cropped_ply = work_dir / "scene_dense_mesh_cropped.ply"
    assert cropped_ply.exists()
    texture_cmd = mock_run.call_args_list[-1].args[0]
    assert texture_cmd[texture_cmd.index("-m") + 1] == str(cropped_ply)
    assert result.textured_glb.exists()


def test_run_mvs_crop_auto_without_camera_data_falls_back_to_uncropped(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)

    with patch("subprocess.run", side_effect=_mock_run_reconstruct_writes_a_real_box) as mock_run:
        mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir, crop="auto")

    assert not (work_dir / "scene_dense_mesh_cropped.ply").exists()
    texture_cmd = mock_run.call_args_list[-1].args[0]
    assert texture_cmd[texture_cmd.index("-m") + 1] == str(work_dir / "scene_dense_mesh.ply")


def test_run_mvs_crop_none_never_touches_the_mesh(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)
    positions, rotations, intrinsics = _cam_looking_at_origin()

    with patch("subprocess.run", side_effect=_mock_run_reconstruct_writes_a_real_box):
        mvs.run_mvs(
            dense_dir,
            work_dir,
            bin_dir=bin_dir,
            crop="none",
            crop_cam_positions=positions,
            crop_cam_rotations=rotations,
            crop_cam_intrinsics=intrinsics,
            crop_up=np.array([0.0, 1.0, 0.0]),
        )

    assert not (work_dir / "scene_dense_mesh_cropped.ply").exists()


def test_run_mvs_rejects_unknown_crop_mode(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)

    with (
        patch("subprocess.run", side_effect=_mock_run_reconstruct_writes_a_real_box),
        pytest.raises(ValueError, match="crop"),
    ):
        mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir, crop="bogus")


def test_run_mvs_appends_extra_densify_and_texture_flags(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    bin_dir = _fake_bin_dir(tmp_path)

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        mvs.run_mvs(
            dense_dir,
            tmp_path / "work",
            bin_dir=bin_dir,
            densify_args=("--sub-resolution-levels", "1"),
            mesh_args=("--free-space-support", "1"),
            texture_args=("--outlier-threshold", "0", "--global-seam-leveling", "1"),
        )

    densify_cmd, texture_cmd = (
        mock_run.call_args_list[1].args[0],
        mock_run.call_args_list[3].args[0],
    )
    assert densify_cmd[-2:] == ["--sub-resolution-levels", "1"]
    assert texture_cmd[-4:] == ["--outlier-threshold", "0", "--global-seam-leveling", "1"]
    assert texture_cmd.count("--global-seam-leveling") == 1  # OpenMVS rejects a repeated option
    assert mock_run.call_args_list[2].args[0][-2:] == ["--free-space-support", "1"]


def test_run_mvs_depthfusion_skips_densify_and_textures_the_fused_mesh(tmp_path):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)
    fused = tmp_path / "fused.ply"
    _mock_run_that_writes_output(["x", "-o", str(fused)])

    with (
        patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run,
        patch("pipeline.depthfusion.run_depthfusion", return_value=(fused, {"worker_s": 1.0})),
    ):
        result = mvs.run_mvs(dense_dir, work_dir, bin_dir=bin_dir, densify="depthfusion")

    called = [call.args[0] for call in mock_run.call_args_list]
    assert [c[0] for c in called] == [
        str(bin_dir / "InterfaceCOLMAP"),
        str(bin_dir / "TextureMesh"),
    ]
    tex = called[1]
    assert tex[tex.index("-i") + 1] == str(result.scene_mvs)
    assert tex[tex.index("-m") + 1] == str(fused)
    assert result.timings_s["depthfusion_worker_s"] == 1.0


def test_run_mvs_hybrid_densifies_fast_then_fuses_with_the_depth_maps(tmp_path, monkeypatch):
    monkeypatch.setenv("AIRTOOLS_DEPTH_ENV", str(tmp_path / "venv"))
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    work_dir = tmp_path / "work"
    bin_dir = _fake_bin_dir(tmp_path)
    fused = tmp_path / "fused.ply"
    _mock_run_that_writes_output(["x", "-o", str(fused)])

    with (
        patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run,
        patch("pipeline.depthfusion.run_depthfusion", return_value=(fused, {})) as mock_df,
    ):
        mvs.run_mvs(
            dense_dir, work_dir, bin_dir=bin_dir, densify="hybrid", densify_args=("--x", "1")
        )

    called = [call.args[0] for call in mock_run.call_args_list]
    assert [Path(c[0]).name for c in called] == [
        "InterfaceCOLMAP",
        "DensifyPointCloud",
        "TextureMesh",
    ]
    assert "--resolution-level" in called[1]
    prefetch, fuse = mock_df.call_args_list
    assert prefetch.kwargs["depth_only"] and prefetch.kwargs["extra_args"] == ("--x", "1")
    w = work_dir.resolve()
    assert fuse.kwargs["extra_args"] == (
        "--mvs-depth", str(w), "--mvs-cloud", str(w / "scene_dense.ply"), "--x", "1",
    )  # fmt: skip


def test_run_mvs_default_hybrid_falls_back_to_openmvs_without_a_depth_env(tmp_path, caplog):
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    bin_dir = _fake_bin_dir(tmp_path)

    with patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run:
        result = mvs.run_mvs(dense_dir, tmp_path / "work", bin_dir=bin_dir)

    names = [Path(c.args[0][0]).name for c in mock_run.call_args_list]
    assert names == ["InterfaceCOLMAP", "DensifyPointCloud", "ReconstructMesh", "TextureMesh"]
    assert result.densify == "openmvs"
    assert "AIRTOOLS_DEPTH_ENV" in caplog.text


def test_run_mvs_hybrid_falls_back_to_its_own_densify_pass_when_depth_fails(
    tmp_path, monkeypatch, caplog
):
    monkeypatch.setenv("AIRTOOLS_DEPTH_ENV", str(tmp_path / "venv"))
    dense_dir = tmp_path / "dense"
    (dense_dir / "images").mkdir(parents=True)
    bin_dir = _fake_bin_dir(tmp_path)

    with (
        patch("subprocess.run", side_effect=_mock_run_that_writes_output) as mock_run,
        patch("pipeline.depthfusion.run_depthfusion", side_effect=RuntimeError("worker OOM")),
    ):
        result = mvs.run_mvs(dense_dir, tmp_path / "work", bin_dir=bin_dir, force=True)

    called = [c.args[0] for c in mock_run.call_args_list]
    names = [Path(c[0]).name for c in called]
    assert names == ["InterfaceCOLMAP", "DensifyPointCloud", "ReconstructMesh", "TextureMesh"]
    assert called[2][1] == str(result.dense_mvs)  # meshes the hybrid's DensifyPointCloud cloud
    assert result.densify == "openmvs"
    assert "worker OOM" in caplog.text
