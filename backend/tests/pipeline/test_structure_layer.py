import json
from pathlib import Path

import numpy as np
import pytest
from scipy.spatial.transform import Rotation

from pipeline import structure
from pipeline.calibrate import similarity_matrix


def _doc(frame="cameras"):
    return {
        "schema": "airtools.structure/1",
        "frame": frame,
        "cameras": "x",
        "edges": [{"a": [0.1, 0.2, 3.0], "b": [0.4, 0.2, 3.0], "kind": "line"}],
        "corners": [{"p": [0.1, 0.2, 3.0]}],
        "planes": [{"id": "p0", "normal": [0, 0, 1], "offset": -3.0}],
    }


def test_published_structure_projects_like_the_raw_model():
    """A raw-frame point moved by to_scene(cameras_matrix) lands on the same pixel through the
    published camera (poses_to_cameras with the same matrix) as through the raw SfM pose."""
    from pipeline.experiments.lines.frame import to_scene
    from pipeline.run import RawPose, poses_to_cameras

    r_wc = Rotation.from_euler("xyz", [5, -20, 10], degrees=True).as_matrix()
    raw = RawPose("0007.jpg", r_wc, np.array([0.1, -0.2, 0.3]), 900, 900, 640, 360, 1280, 720)
    align = similarity_matrix(
        3.8, Rotation.from_euler("xyz", [90, 0, 30], degrees=True).as_matrix(), np.ones(3)
    )
    m = similarity_matrix(1.02, np.eye(3), np.zeros(3)) @ align  # run.py's cameras_matrix
    cam = poses_to_cameras([raw], m)[0]

    def pixel(r, t, x):
        xc = r @ x + t
        return np.array([raw.fx * xc[0] / xc[2] + raw.cx, raw.fy * xc[1] / xc[2] + raw.cy])

    x_raw = np.array(_doc()["corners"][0]["p"])
    x_scene = np.array(to_scene(_doc(), m)["corners"][0]["p"])
    assert np.allclose(pixel(r_wc, raw.t_wc, x_raw), pixel(cam.R, cam.t, x_scene), atol=1e-6)


def test_prepare_skips_with_a_reason(monkeypatch, tmp_path):
    kw = {"m_per_unit": 0.26, "up_cameras": tmp_path / "up.json"}
    monkeypatch.delenv("AIRTOOLS_LIMAP_ENV", raising=False)
    monkeypatch.delenv("AIRTOOLS_PXW_ENV", raising=False)
    assert structure.prepare(False, tmp_path, tmp_path, tmp_path, **kw) == (
        None,
        {"skipped": "--structure off"},
    )
    job, rep = structure.prepare(True, tmp_path, tmp_path, tmp_path, **kw)
    assert job is None and "AIRTOOLS_LIMAP_ENV" in rep["skipped"]

    # a LIMAP venv without LIMAP's cfgs/ counts as missing; no metric scale skips it all
    env = tmp_path / "limap"
    (env / "bin").mkdir(parents=True)
    (env / "bin" / "python").touch()
    monkeypatch.setenv("AIRTOOLS_LIMAP_ENV", str(env))
    assert structure.env_pythons() == (None, None)
    (env / structure._LIMAP_CONFIG).parent.mkdir(parents=True)
    (env / structure._LIMAP_CONFIG).touch()
    assert structure.env_pythons()[0] == env / "bin" / "python"
    kw["m_per_unit"] = None
    job, rep = structure.prepare(True, tmp_path, tmp_path, tmp_path, **kw)
    assert job is None and "metric" in rep["skipped"]


def _job(monkeypatch, tmp_path, fake_run):
    for var in ("AIRTOOLS_LIMAP_ENV", "AIRTOOLS_PXW_ENV"):
        env = tmp_path / var
        (env / "bin").mkdir(parents=True)
        (env / "bin" / "python").touch()
        (env / structure._LIMAP_CONFIG).parent.mkdir(parents=True)
        (env / structure._LIMAP_CONFIG).touch()
        monkeypatch.setenv(var, str(env))
    monkeypatch.setattr(structure, "_run", fake_run)
    job, _ = structure.prepare(
        True, tmp_path / "dense", tmp_path / "mvs", tmp_path / "work",
        m_per_unit=0.26, up_cameras=tmp_path / "up.json",
    )  # fmt: skip
    return job


def _out(cmd):
    return Path(cmd[cmd.index("--out") + 1])


def test_lines_only_when_planes_fail(monkeypatch, tmp_path):
    seen = []

    def fake_run(cmd, log, *a, **k):
        if "pipeline.experiments.lines.limap_run" in cmd:
            seen.append(cmd)
            _out(cmd).mkdir(parents=True, exist_ok=True)
            (_out(cmd) / "structure.json").write_text(json.dumps(_doc()))
        else:  # PxwPlanar (and so S4) fail
            raise RuntimeError("no GPU")

    job = _job(monkeypatch, tmp_path, fake_run)
    job.start()
    m = similarity_matrix(2.0, np.eye(3), np.array([0.0, 1.0, 0.0]))
    entry = job.finish(
        m, tmp_path, 2, mesh_file="mesh.r2.glb", cameras_file="cameras.r2.json",
        frames_dir=tmp_path, sparse_raw=tmp_path,
    )  # fmt: skip
    assert entry["file"] == "structure.r2.json" and entry["edges"] == 1
    doc = json.loads((tmp_path / "structure.r2.json").read_text())
    assert doc["frame"] == "scene" and np.allclose(doc["corners"][0]["p"], [0.2, 1.4, 6.0])
    assert job.report["ran"] == ["limap"] and "pxw_infer" in job.report["failed"]
    assert all(a in seen[0] for a in structure.LIMAP_ARGS)  # line cap reaches LIMAP's CLI
    assert "s4_rects" not in job.report["timings_s"]  # no planes, no rects


def test_nothing_published_when_every_step_fails(monkeypatch, tmp_path):
    def fake_run(cmd, log, *a, **k):
        raise RuntimeError("boom")

    job = _job(monkeypatch, tmp_path, fake_run)
    entry = job.finish(  # finish starts the job itself when the densify hook never fired
        np.eye(4), tmp_path, 2, mesh_file="m", cameras_file="c",
        frames_dir=tmp_path, sparse_raw=tmp_path,
    )  # fmt: skip
    assert entry is None and not (tmp_path / "structure.r2.json").exists()
    assert set(job.report["failed"]) == {"limap", "pxw_infer"}


@pytest.mark.parametrize("corner, ok", [([0.5, 0.5, 0.5], True), ([50.0, 0, 0], False)])
def test_validate_package_checks_the_structure_frame(tmp_path, corner, ok):
    import trimesh

    from pipeline.package import _validate_structure

    trimesh.creation.box((1, 1, 1)).export(tmp_path / "mesh.glb")
    doc = dict(_doc("scene"), edges=[], corners=[{"p": corner}])
    (tmp_path / "s.json").write_text(json.dumps(doc))
    assert (_validate_structure(tmp_path / "s.json", tmp_path / "mesh.glb") == []) == ok


def test_a_hung_step_is_killed_with_its_workers(tmp_path):
    """A step whose pool workers hang (S4's fork deadlock) is killed as a process group."""
    import sys
    import time

    pid_file = tmp_path / "worker.pid"
    code = (
        "import subprocess, sys, time;"
        "w = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)']);"
        f"open({str(pid_file)!r}, 'w').write(str(w.pid)); time.sleep(60)"
    )
    t0 = time.monotonic()
    with pytest.raises(RuntimeError, match="timed out"):
        structure._run([sys.executable, "-c", code], tmp_path / "log.txt", timeout=1.5)
    assert time.monotonic() - t0 < 10 and not structure._LIVE
    stat = Path(f"/proc/{pid_file.read_text()}/stat")
    time.sleep(0.2)
    assert not stat.exists() or stat.read_text().split()[2] == "Z"  # gone or a reaped-later zombie


def test_publish_does_not_wait_on_a_hung_background_part(monkeypatch, tmp_path):
    import time

    def fake_run(cmd, log, *a, **k):
        time.sleep(30)

    job = _job(monkeypatch, tmp_path, fake_run)
    monkeypatch.setattr(structure, "_WAIT_S", 0.3)
    monkeypatch.setattr(structure, "_GRACE_S", 0.3)
    job.start()
    t0 = time.monotonic()
    entry = job.finish(
        np.eye(4), tmp_path, 2, mesh_file="m", cameras_file="c",
        frames_dir=tmp_path, sparse_raw=tmp_path,
    )  # fmt: skip
    assert entry is None and time.monotonic() - t0 < 5
    assert "wait" in job.report["failed"] and not (tmp_path / "structure.r2.json").exists()


def test_a_late_layer_is_killed_and_the_finished_one_published(monkeypatch, tmp_path):
    """gt-lcc: LIMAP ran past the wait while the other layer was long done; the publish kills
    the late step and keeps the finished layer instead of dropping both."""
    import time

    def fake_run(cmd, log, *a, **k):
        if "pipeline.experiments.lines.limap_run" in cmd:
            _out(cmd).mkdir(parents=True, exist_ok=True)
            (_out(cmd) / "structure.json").write_text(json.dumps(_doc()))
        else:  # PxwPlanar hangs until the publish kills it
            structure._ABORT.wait(10)
            raise RuntimeError("exit -9")

    job = _job(monkeypatch, tmp_path, fake_run)
    monkeypatch.setattr(structure, "_WAIT_S", 0.3)
    job.start()
    t0 = time.monotonic()
    entry = job.finish(
        np.eye(4), tmp_path, 2, mesh_file="m", cameras_file="c",
        frames_dir=tmp_path, sparse_raw=tmp_path,
    )  # fmt: skip
    assert time.monotonic() - t0 < 5 and not structure._ABORT.is_set()
    assert entry["file"] == "structure.r2.json" and entry["edges"] == 1
    assert "wait" in job.report["failed"] and "pxw_infer" in job.report["failed"]
