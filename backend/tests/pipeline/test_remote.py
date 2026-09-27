"""pipeline.remote (plan §1a "Getting it to the laptop"): laptop-side driver for a remote
reconstruction run. Pure logic only -- every subprocess call is mocked, nothing here touches the
network."""

import json
import subprocess

import pytest

from pipeline import remote


def _completed(
    stdout: str = "", stderr: str = "", returncode: int = 0
) -> subprocess.CompletedProcess:
    return subprocess.CompletedProcess(args=[], returncode=returncode, stdout=stdout, stderr=stderr)


# --- filter_proxy_noise ------------------------------------------------------------------------


def test_filter_proxy_noise_drops_handshake_lines():
    raw = "depth=0 C=US, O=hfbox\nverify return:1\nreal error: connection refused\n"
    assert remote.filter_proxy_noise(raw) == "real error: connection refused"


def test_filter_proxy_noise_keeps_everything_when_no_noise():
    raw = "just a normal line\nanother one"
    assert remote.filter_proxy_noise(raw) == raw


# --- _run: rsync's "remote dir doesn't exist yet" is expected chatter, not a poll-loop error ----
# (confirmed hands-on against hfbox: fires every ~poll cycle before the first revision publishes
# and otherwise buries real errors in the log)


def test_run_downgrades_missing_remote_dir_rsync_error_to_debug(caplog):
    stderr = (
        'rsync: [sender] change_dir "/workspace/airtools/scene/site-a" failed: '
        "No such file or directory (2)\n"
        "rsync error: some files/attrs were not transferred (see previous errors) (code 23)\n"
    )

    def fake_runner(cmd, **kwargs):
        return _completed(stderr=stderr, returncode=23)

    with caplog.at_level("DEBUG", logger="pipeline.remote"):
        remote._run(fake_runner, ["rsync", "..."])

    assert not any(r.levelname == "INFO" for r in caplog.records)
    assert any(
        r.levelname == "DEBUG" and "No such file or directory" in r.message for r in caplog.records
    )


def test_run_logs_unrelated_rsync_errors_at_info(caplog):
    def fake_runner(cmd, **kwargs):
        return _completed(stderr="rsync: connection unexpectedly closed\n", returncode=12)

    with caplog.at_level("DEBUG", logger="pipeline.remote"):
        remote._run(fake_runner, ["rsync", "..."])

    assert any(
        r.levelname == "INFO" and "connection unexpectedly closed" in r.message
        for r in caplog.records
    )


def test_filter_proxy_noise_handles_leading_whitespace():
    raw = "  depth=1 /CN=hfbox\nactual error"
    assert remote.filter_proxy_noise(raw) == "actual error"


# --- command construction ----------------------------------------------------------------------


def test_ssh_command_uses_batch_mode():
    cmd = remote.ssh_command("hfbox", "echo hi")
    assert cmd == ["ssh", "-o", "BatchMode=yes", "hfbox", "echo hi"]


def test_rsync_upload_command_is_resumable_and_creates_remote_dir():
    cmd = remote.rsync_upload_command(
        "/local/clip.mp4", "hfbox", "/workspace/airtools/uploads/site-a/clip.mp4"
    )
    assert cmd[0] == "rsync"
    assert "--partial" in cmd
    assert "--mkpath" in cmd
    assert cmd[-2] == "/local/clip.mp4"
    assert cmd[-1] == "hfbox:/workspace/airtools/uploads/site-a/clip.mp4"


def test_rsync_pull_bulk_command_excludes_scene_json():
    cmd = remote.rsync_pull_bulk_command("hfbox", "/workspace/airtools/scene/site-a", "/local/out")
    assert "--exclude=scene.json" in cmd
    assert cmd[-2] == "hfbox:/workspace/airtools/scene/site-a/"
    assert cmd[-1] == "/local/out/"


def test_rsync_pull_scene_json_command_targets_scene_json_only():
    cmd = remote.rsync_pull_scene_json_command(
        "hfbox", "/workspace/airtools/scene/site-a", "/local/out"
    )
    assert cmd[-2] == "hfbox:/workspace/airtools/scene/site-a/scene.json"
    assert cmd[-1] == "/local/out/scene.json"
    assert not any(a == "--exclude=scene.json" for a in cmd)


def test_remote_run_command_sources_env_and_sets_vggt_env():
    cmd = remote.remote_run_command(
        "/workspace/airtools", "/workspace/airtools/uploads/v.mp4", "site-a"
    )
    assert "source /workspace/env.sh" in cmd
    assert "export AIRTOOLS_VGGT_ENV=/workspace/envs/vggt" in cmd
    assert "cd /workspace/airtools" in cmd
    assert "uv run --extra pipeline python -m pipeline.cli run" in cmd
    assert "--site site-a" in cmd
    assert "--out scene/site-a" in cmd


def test_remote_run_command_appends_extra_args_quoted():
    cmd = remote.remote_run_command(
        "/workspace/airtools", "/w/v.mp4", "site-a", extra_args=["--mapper", "vggt"]
    )
    assert cmd.endswith("--mapper vggt")


def test_remote_run_command_no_extra_args_no_trailing_space():
    cmd = remote.remote_run_command("/workspace/airtools", "/w/v.mp4", "site-a")
    assert not cmd.endswith(" ")


def test_tmux_launch_command_logs_and_records_exit_code():
    cmd = remote.tmux_launch_command("airtools-site-a", "run-the-thing", "/w/logs/site-a.log")
    assert cmd.startswith("tmux new-session -d -s airtools-site-a")
    assert "/w/logs/site-a.log" in cmd
    assert "EXIT:$?" in cmd
    assert "run-the-thing" in cmd


def test_tmux_has_session_command():
    assert (
        remote.tmux_has_session_command("airtools-site-a") == "tmux has-session -t airtools-site-a"
    )


# --- read_scene_status -------------------------------------------------------------------------


def test_read_scene_status_missing_file_returns_none(tmp_path):
    assert remote.read_scene_status(tmp_path / "scene.json") is None


def test_read_scene_status_invalid_json_returns_none(tmp_path):
    p = tmp_path / "scene.json"
    p.write_text("{not json")
    assert remote.read_scene_status(p) is None


def test_read_scene_status_parses_revision_and_quality(tmp_path):
    p = tmp_path / "scene.json"
    p.write_text(json.dumps({"revision": 2, "quality": "full"}))
    status = remote.read_scene_status(p)
    assert status == remote.SceneStatus(revision=2, quality="full")


def test_read_scene_status_defaults_quality_full_when_absent(tmp_path):
    """Pre-§1a scene.json has no "quality"/"revision" field at all -- treat it as a single
    already-published full revision, not as "unknown"."""
    p = tmp_path / "scene.json"
    p.write_text(json.dumps({"name": "legacy-site"}))
    status = remote.read_scene_status(p)
    assert status == remote.SceneStatus(revision=None, quality="full")


# --- pull_once: ordering (revision files before scene.json) ------------------------------------


def test_pull_once_pulls_bulk_before_scene_json(tmp_path):
    calls: list[list[str]] = []

    def fake_runner(cmd, **kwargs):
        calls.append(cmd)
        if cmd[0] == "rsync" and cmd[-1].endswith("scene.json"):
            (tmp_path / "scene.json").write_text(json.dumps({"revision": 1, "quality": "preview"}))
        return _completed()

    status = remote.pull_once(
        "hfbox", "/workspace/airtools/scene/site-a", tmp_path, runner=fake_runner
    )

    assert len(calls) == 2
    assert "--exclude=scene.json" in calls[0]  # bulk pull first
    assert calls[1][-1].endswith("scene.json")  # scene.json pull second
    assert status == remote.SceneStatus(revision=1, quality="preview")


def test_pull_once_returns_none_when_nothing_published_yet(tmp_path):
    def fake_runner(cmd, **kwargs):
        return _completed()

    status = remote.pull_once(
        "hfbox", "/workspace/airtools/scene/site-a", tmp_path, runner=fake_runner
    )
    assert status is None


# --- remote_run orchestration --------------------------------------------------------------------


def test_remote_run_upload_frames_not_implemented(tmp_path):
    with pytest.raises(NotImplementedError):
        remote.remote_run(tmp_path / "clip.mp4", "site-a", tmp_path / "out", upload="frames")


def test_remote_run_legacy_scene_json_without_revision_field_succeeds(tmp_path, capsys):
    """Regression test for a real bug found running against hfbox: a pre-§1a scene.json never
    sets "revision" (or "quality"), so `read_scene_status` returns `revision=None` -- comparing
    only `.revision` against an initial `last_revision=None` never fires (`None != None` is
    False), so the run was reported as failed (exit 1) even though the remote job had actually
    published a complete package."""
    out_dir = tmp_path / "out"

    def fake_runner(cmd, **kwargs):
        if cmd[0] == "rsync" and cmd[-1].endswith("scene.json"):
            out_dir.mkdir(parents=True, exist_ok=True)
            # a real legacy scene.json: no "revision"/"quality" key at all
            (out_dir / "scene.json").write_text(json.dumps({"name": "site-a", "mesh": {}}))
        return _completed()

    exit_code = remote.remote_run(
        tmp_path / "clip.mp4", "site-a", out_dir, runner=fake_runner, poll_interval_s=0
    )

    assert exit_code == 0
    assert "full landed at +" in capsys.readouterr().out


def test_remote_run_catches_publish_that_races_session_death(tmp_path, capsys):
    """Regression test for a real bug found running against hfbox: the remote job's last publish
    can land in the same window the tmux session ends, so the regular per-cycle pull can miss it
    -- the "session ended" path must pull one more time before declaring failure."""
    out_dir = tmp_path / "out"
    has_session_cmd = remote.ssh_command(
        "hfbox", remote.tmux_has_session_command("airtools-site-a")
    )
    published = False

    def fake_runner(cmd, **kwargs):
        nonlocal published
        if cmd == has_session_cmd:
            published = True  # the file "lands" exactly as the session ends
            return _completed(returncode=1)
        if cmd[0] == "rsync" and cmd[-1].endswith("scene.json") and published:
            out_dir.mkdir(parents=True, exist_ok=True)
            (out_dir / "scene.json").write_text(json.dumps({"revision": 1, "quality": "full"}))
        return _completed()

    exit_code = remote.remote_run(
        tmp_path / "clip.mp4", "site-a", out_dir, runner=fake_runner, poll_interval_s=0
    )

    assert exit_code == 0
    assert "full landed at +" in capsys.readouterr().out


def test_remote_run_reports_preview_then_full_and_stops(tmp_path, capsys):
    out_dir = tmp_path / "out"
    scene_states = iter(
        [
            None,  # first pull_once cycle: nothing yet
            {"revision": 1, "quality": "preview"},
            {"revision": 2, "quality": "full"},
        ]
    )
    calls: list[list[str]] = []

    def fake_runner(cmd, **kwargs):
        calls.append(cmd)
        if cmd[0] == "rsync" and cmd[-1].endswith("scene.json"):
            state = next(scene_states, None)
            if state is not None:
                out_dir.mkdir(parents=True, exist_ok=True)
                (out_dir / "scene.json").write_text(json.dumps(state))
        return _completed()

    exit_code = remote.remote_run(
        tmp_path / "clip.mp4",
        "site-a",
        out_dir,
        runner=fake_runner,
        poll_interval_s=0,
    )

    assert exit_code == 0
    out = capsys.readouterr().out
    assert "preview landed at +" in out
    assert "full landed at +" in out
    # upload + mkdir + tmux launch happen before any polling rsync call
    assert any(
        c[0] == "rsync" and c[1:3] == ["-av", "--partial"] and "--mkpath" in c for c in calls
    )


def test_remote_run_returns_1_when_remote_job_dies_without_full(tmp_path, capsys):
    out_dir = tmp_path / "out"
    has_session_cmd = remote.ssh_command(
        "hfbox", remote.tmux_has_session_command("airtools-site-a")
    )
    log_tail_cmd = remote.ssh_command(
        "hfbox", remote.remote_log_tail_command("/workspace/airtools/logs/site-a.log")
    )

    def fake_runner(cmd, **kwargs):
        if cmd == has_session_cmd:
            return _completed(returncode=1)  # session ended
        if cmd == log_tail_cmd:
            return _completed(stdout="depth=0 noise\nERROR: OpenMVS crashed\n")
        return _completed()

    exit_code = remote.remote_run(
        tmp_path / "clip.mp4",
        "site-a",
        out_dir,
        runner=fake_runner,
        poll_interval_s=0,
    )

    assert exit_code == 1
    out = capsys.readouterr().out
    assert "remote job ended without publishing a full revision" in out
    assert "ERROR: OpenMVS crashed" in out
    assert "depth=0 noise" not in out
