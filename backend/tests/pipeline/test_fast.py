import json
import os
import time
from pathlib import Path

import numpy as np
import pytest

pytest.importorskip("pymeshlab")  # pipeline.fast imports pipeline.mesh, which needs it

from pipeline import fast

# --- select_preview_frame_times (plan §1a: "evenly pick <=64 frames") ---------------------------


def test_select_preview_frame_times_caps_at_max_frames():
    times = fast.select_preview_frame_times(duration_s=120.0, fps=30.0, max_frames=64)
    assert len(times) == 64


def test_select_preview_frame_times_evenly_spaced():
    times = fast.select_preview_frame_times(duration_s=10.0, fps=30.0, max_frames=10)
    diffs = np.diff(times)
    assert diffs == pytest.approx(diffs[0], abs=1e-9)
    assert 0.0 < times[0]
    assert times[-1] < 10.0  # margin off both ends, so a seek never lands past EOF


def test_select_preview_frame_times_short_clip_uses_fewer_than_max():
    times = fast.select_preview_frame_times(duration_s=1.0, fps=30.0, max_frames=64)
    assert len(times) == 30  # round(1.0 * 30) < max_frames -- doesn't pad up to 64


def test_select_preview_frame_times_rejects_nonpositive_duration():
    with pytest.raises(ValueError):
        fast.select_preview_frame_times(duration_s=0.0, fps=30.0)


# --- worker_is_alive (heartbeat mtime check) -----------------------------------------------------


def test_worker_is_alive_false_when_no_heartbeat(tmp_path):
    assert fast.worker_is_alive(tmp_path) is False


def test_worker_is_alive_true_for_a_fresh_heartbeat(tmp_path):
    spool = tmp_path / ".vggt_spool"
    spool.mkdir()
    (spool / "heartbeat").write_text("")
    assert fast.worker_is_alive(tmp_path, max_age_s=20.0) is True


def test_worker_is_alive_false_for_a_stale_heartbeat(tmp_path):
    spool = tmp_path / ".vggt_spool"
    spool.mkdir()
    heartbeat = spool / "heartbeat"
    heartbeat.write_text("")
    old = time.time() - 100.0
    os.utime(heartbeat, (old, old))
    assert fast.worker_is_alive(tmp_path, max_age_s=20.0) is False


# --- _wait_for_result -----------------------------------------------------------------------------


def test_wait_for_result_reads_once_written(tmp_path):
    result_path = tmp_path / "r.json"
    result_path.write_text(json.dumps({"status": "ok", "timings_s": {}}))
    assert fast._wait_for_result(result_path, timeout_s=1.0) == {"status": "ok", "timings_s": {}}


def test_wait_for_result_times_out(tmp_path):
    with pytest.raises(TimeoutError):
        fast._wait_for_result(tmp_path / "never.json", timeout_s=0.2, poll_interval_s=0.05)


# --- dispatch_vggt_job (warm worker vs. cold one-shot subprocess, mocked) -------------------------


def test_dispatch_vggt_job_uses_cold_path_when_no_worker(tmp_path, monkeypatch):
    monkeypatch.setattr(fast, "worker_is_alive", lambda work_root, **kw: False)
    calls = []

    def fake_run_cold(job, *, python_bin, vggt_repo):
        calls.append((job, python_bin, vggt_repo))
        return {"status": "ok", "timings_s": {"total_s": 1.0}}

    monkeypatch.setattr(fast, "_run_cold", fake_run_cold)

    timings = fast.dispatch_vggt_job(
        {"work_dir": str(tmp_path)}, tmp_path, python_bin=Path("/fake/python")
    )

    assert timings == {"total_s": 1.0}
    assert len(calls) == 1


def test_dispatch_vggt_job_uses_warm_worker_when_alive(tmp_path, monkeypatch):
    monkeypatch.setattr(fast, "worker_is_alive", lambda work_root, **kw: True)
    written_jobs = []

    def fake_wait_for_result(result_path, timeout_s, poll_interval_s=fast._JOB_POLL_INTERVAL_S):
        # the job file must already be on disk (and named for the worker's spool glob) before a
        # dispatch would start polling for a result
        written_jobs.append(list((tmp_path / ".vggt_spool").glob("*.job.json")))
        return {"status": "ok", "timings_s": {"total_s": 2.0}}

    monkeypatch.setattr(fast, "_wait_for_result", fake_wait_for_result)

    timings = fast.dispatch_vggt_job(
        {"work_dir": str(tmp_path)}, tmp_path, python_bin=Path("/fake/python")
    )

    assert timings == {"total_s": 2.0}
    assert len(written_jobs[0]) == 1
    assert list((tmp_path / ".vggt_spool").glob("*.job.json")) == []  # cleaned up after


def test_dispatch_vggt_job_raises_on_failed_job(tmp_path, monkeypatch):
    monkeypatch.setattr(fast, "worker_is_alive", lambda work_root, **kw: False)
    monkeypatch.setattr(fast, "_run_cold", lambda job, **kw: {"status": "error", "error": "boom"})

    with pytest.raises(RuntimeError, match="boom"):
        fast.dispatch_vggt_job({"work_dir": str(tmp_path)}, tmp_path, python_bin=Path("/fake"))


# --- run_preview: no-op when AIRTOOLS_VGGT_ENV isn't set (plan: "cold path must still work "
# "without the worker" implies the whole preview is itself optional -- Stage B always works) ------


def test_run_preview_skips_without_vggt_env(tmp_path, monkeypatch):
    monkeypatch.setattr(fast.vggt, "env_python", lambda: None)
    report = fast.run_preview(tmp_path / "video.mp4", "site", tmp_path / "out", tmp_path / "work")
    assert report == {"skipped": "AIRTOOLS_VGGT_ENV not set", "timings_s": {}}


# --- preview_known_distance: a JSON clicking SfM frames must not crash the preview ---------------


def test_preview_known_distance_skips_json_without_preview_frames(tmp_path):
    kd = tmp_path / "kd.json"
    obs = [{"frame": f, "a": [0, 0], "b": [1, 1]} for f in ("0012.jpg", "0045.jpg")]
    kd.write_text(json.dumps({"length_m": 1.5, "observations": obs}))
    assert fast.preview_known_distance(kd, {"p0001.jpg", "p0002.jpg"}) is None
    assert fast.preview_known_distance(kd, {"0012.jpg", "0045.jpg"}) == kd
    assert fast.preview_known_distance(None, {"p0001.jpg"}) is None
