"""Laptop-side driver for a remote reconstruction run (plan §1a "Getting it to the laptop", §2.4):
rsync the video to `hfbox`, start `pipeline.cli run` there inside a detached tmux session, then
poll the remote scene dir and pull each newly published revision into the local scene package as
soon as it lands -- revision files first, `scene.json` last, so a reader of the local `scene/`
tree never sees a torn revision either (mirrors the publish-side guarantee, §1a).

`hfbox`'s ssh (`~/.ssh/config`) goes through `ProxyCommand openssl s_client ...`, which prints TLS
handshake noise ("depth=0 ...", "verify return:1 ...") to stderr on every connection --
`filter_proxy_noise` drops it so real errors aren't buried.

Only `--upload video` is implemented: `pipeline.run.run()` calls `frames.probe`/`extract_frames`
directly on a video path, it has no frames-dir entry point yet, so there's nothing for
`--upload frames` to hand off to. `--upload frames` raises `NotImplementedError` rather than
silently doing the wrong thing.
"""

import json
import logging
import re
import shlex
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

logger = logging.getLogger(__name__)

DEFAULT_HOST = "hfbox"
DEFAULT_REMOTE_ROOT = "/workspace/airtools"
DEFAULT_POLL_INTERVAL_S = 5.0
_LOG_TAIL_LINES = 40

_SSH_BASE = ["ssh", "-o", "BatchMode=yes"]  # BatchMode: never hang on a credential prompt

_PROXY_NOISE_PREFIXES = ("depth=", "verify return:")


class _Runner(Protocol):
    def __call__(self, cmd: list[str], **kwargs: Any) -> subprocess.CompletedProcess: ...


# --- pure helpers: command construction, noise filtering, revision detection -----------------


def filter_proxy_noise(text: str) -> str:
    """Drop hfbox's ProxyCommand TLS handshake lines ("depth=0 ...", "verify return:1 ...") from
    ssh/rsync stderr, so whatever's left is an actual error."""
    return "\n".join(
        line for line in text.splitlines() if not line.strip().startswith(_PROXY_NOISE_PREFIXES)
    )


def ssh_command(host: str, remote_command: str) -> list[str]:
    return [*_SSH_BASE, host, remote_command]


def rsync_upload_command(local_path: Path, host: str, remote_path: str) -> list[str]:
    """Resumable upload: `--partial` keeps a partial transfer on disk so a dropped connection
    resumes a 1-2 GB video instead of restarting; `--mkpath` creates the remote directory."""
    return [
        "rsync",
        "-av",
        "--partial",
        "--mkpath",
        "-e",
        " ".join(_SSH_BASE),
        str(local_path),
        f"{host}:{remote_path}",
    ]


def rsync_pull_bulk_command(host: str, remote_dir: str, local_dir: Path) -> list[str]:
    """Pull everything under the remote scene dir except `scene.json` -- the revision-named
    mesh/collision/cameras files, `thumbs/`, `path.json` (plan §1a: revision files land first)."""
    return [
        "rsync",
        "-av",
        "--partial",
        "--exclude=scene.json",
        "-e",
        " ".join(_SSH_BASE),
        f"{host}:{remote_dir}/",
        f"{local_dir}/",
    ]


def rsync_pull_scene_json_command(host: str, remote_dir: str, local_dir: Path) -> list[str]:
    """Pull `scene.json` last. rsync's default (non `--inplace`) transfer writes to a temp file
    in `local_dir` and renames it over `scene.json` on completion -- atomic, so a reader on the
    laptop side never sees a torn `scene.json` either (mirrors the remote's own commit point)."""
    return [
        "rsync",
        "-av",
        "--partial",
        "-e",
        " ".join(_SSH_BASE),
        f"{host}:{remote_dir}/scene.json",
        f"{local_dir}/scene.json",
    ]


def remote_run_command(
    remote_root: str, remote_video: str, site: str, extra_args: list[str] | None = None
) -> str:
    """The command run inside the remote tmux session (plan §2.5: `AIRTOOLS_VGGT_ENV` points at
    the VGGT fallback venv). Bare `python` on hfbox resolves to `/opt/venv/bin/python` (the
    system ROCm venv, no pipeline deps) -- confirmed hands-on -- so this runs through `uv run
    --extra pipeline`, same as every other pipeline invocation in this repo."""
    cmd = (
        f"cd {shlex.quote(remote_root)} && uv run --extra pipeline python -m pipeline.cli run "
        f"{shlex.quote(remote_video)} --site {shlex.quote(site)} --out {shlex.quote(f'scene/{site}')}"
    )
    if extra_args:
        cmd += " " + " ".join(shlex.quote(a) for a in extra_args)
    return f"source /workspace/env.sh && export AIRTOOLS_VGGT_ENV=/workspace/envs/vggt && {cmd}"


def tmux_launch_command(session: str, inner_command: str, log_path: str) -> str:
    """Detached tmux session so a dropped laptop connection doesn't kill the remote run; logs to
    `log_path` and appends `EXIT:<code>` at the end so the poll loop can tell success from
    failure once the session ends."""
    logged = f"( {inner_command} ) > {shlex.quote(log_path)} 2>&1; echo EXIT:$? >> {shlex.quote(log_path)}"
    return f"tmux new-session -d -s {shlex.quote(session)} {shlex.quote(logged)}"


def tmux_has_session_command(session: str) -> str:
    return f"tmux has-session -t {shlex.quote(session)}"


def remote_log_tail_command(remote_log: str, lines: int = _LOG_TAIL_LINES) -> str:
    return f"tail -n {lines} {shlex.quote(remote_log)}"


@dataclass
class SceneStatus:
    revision: int | None
    quality: str


def read_scene_status(scene_json_path: Path) -> SceneStatus | None:
    """Parse a locally-pulled `scene.json` for its revision/quality (plan §1a). `None` if the
    file doesn't exist yet, or isn't valid JSON -- rsync's atomic rename (see
    `rsync_pull_scene_json_command`) means a reader here only ever sees a complete file or none,
    but an empty/missing pull directory still needs to read as "nothing published yet"."""
    if not scene_json_path.exists():
        return None
    try:
        data = json.loads(scene_json_path.read_text())
    except (json.JSONDecodeError, OSError):
        return None
    return SceneStatus(revision=data.get("revision"), quality=data.get("quality", "full"))


def _format_elapsed(seconds: float) -> str:
    return f"{seconds:.0f} s" if seconds < 60 else f"{seconds / 60:.0f} m"


# --- subprocess plumbing ----------------------------------------------------------------------


#  rsync's "the remote scene dir doesn't exist" error -- expected on every poll cycle before the
# first revision publishes (confirmed hands-on against hfbox: this fired ~once a second and
# buried the actually-useful log lines). Downgraded to DEBUG instead of dropped outright, so
# `--upload`/launch problems that print *alongside* it are never silently swallowed.
_RSYNC_MISSING_REMOTE_DIR_RE = re.compile(r"change_dir .* failed: No such file or directory")


def _run(runner: _Runner, cmd: list[str], **kwargs: Any) -> subprocess.CompletedProcess:
    kwargs.setdefault("capture_output", True)
    kwargs.setdefault("text", True)
    result = runner(cmd, **kwargs)
    noise_free = filter_proxy_noise(result.stderr or "")
    if noise_free.strip():
        if _RSYNC_MISSING_REMOTE_DIR_RE.search(noise_free):
            logger.debug(noise_free)
        else:
            logger.info(noise_free)
    return result


def pull_once(
    host: str, remote_dir: str, local_dir: Path, runner: _Runner = subprocess.run
) -> SceneStatus | None:
    """One pull cycle: bulk files first, `scene.json` last (see the two command builders above),
    then report whatever `scene.json` says now. Order of the two `_run` calls is the whole point
    -- tested with a mocked `runner` that records call order."""
    _run(runner, rsync_pull_bulk_command(host, remote_dir, local_dir))
    _run(runner, rsync_pull_scene_json_command(host, remote_dir, local_dir))
    return read_scene_status(local_dir / "scene.json")


def _remote_log_tail(host: str, remote_log: str, runner: _Runner) -> str:
    result = _run(runner, ssh_command(host, remote_log_tail_command(remote_log)), check=False)
    return filter_proxy_noise(result.stdout or "")


# --- orchestration -----------------------------------------------------------------------------


def remote_run(
    video: Path,
    site: str,
    out_dir: Path,
    *,
    host: str = DEFAULT_HOST,
    remote_root: str = DEFAULT_REMOTE_ROOT,
    upload: str = "video",
    extra_args: list[str] | None = None,
    poll_interval_s: float = DEFAULT_POLL_INTERVAL_S,
    runner: _Runner = subprocess.run,
) -> int:
    """Upload `video`, start the remote run, pull each revision into `out_dir` as it publishes.
    Returns 0 once the full revision lands, 1 if the remote job ends without publishing one."""
    if upload != "video":
        raise NotImplementedError(
            "--upload frames isn't implemented: pipeline.run.run() only accepts a video path "
            "(frames.probe/extract_frames run directly on it), not a pre-extracted frames dir -- "
            "use --upload video for now"
        )

    video = Path(video)
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    remote_video = f"{remote_root}/uploads/{site}/{video.name}"
    remote_scene_dir = f"{remote_root}/scene/{site}"
    remote_log = f"{remote_root}/logs/{site}.log"
    session = f"airtools-{site}"

    t0 = time.monotonic()
    logger.info("uploading %s to %s:%s", video, host, remote_video)
    _run(runner, ssh_command(host, f"mkdir -p {shlex.quote(f'{remote_root}/logs')}"), check=True)
    _run(runner, rsync_upload_command(video, host, remote_video), check=True)
    logger.info("upload finished in %s", _format_elapsed(time.monotonic() - t0))

    inner = remote_run_command(remote_root, remote_video, site, extra_args)
    _run(runner, ssh_command(host, tmux_launch_command(session, inner, remote_log)), check=True)
    logger.info("remote run started in tmux session %r", session)

    def _note_landing(status: SceneStatus, last_status: SceneStatus | None) -> bool:
        """Catch-up pull + "landed" print for a newly-seen `status` (compares the *whole*
        revision+quality pair, not just revision -- a pre-§1a scene.json never sets "revision" at
        all, so comparing only `.revision` would compare `None != None` forever and never fire,
        confirmed hands-on: a real single-publish run finished successfully but was reported as
        failed because of exactly this). Returns whether `status` is new."""
        if status == last_status:
            return False
        # Catch-up pull: scene.json may have committed on the remote between this cycle's two
        # pulls in pull_once, in which case the bulk pull just before could have caught a file
        # mid-write. Pulling again now that scene.json confirms the status closes that race.
        _run(runner, rsync_pull_bulk_command(host, remote_scene_dir, out_dir))
        print(f"{status.quality} landed at +{_format_elapsed(time.monotonic() - t0)}")
        return True

    last_status: SceneStatus | None = None
    while True:
        status = pull_once(host, remote_scene_dir, out_dir, runner)
        if status is not None:
            if _note_landing(status, last_status):
                last_status = status
            if status.quality == "full":
                return 0

        alive = _run(runner, ssh_command(host, tmux_has_session_command(session)), check=False)
        if alive.returncode != 0:  # tmux session ended
            # One last pull: the final publish and this poll cycle may have raced (confirmed
            # hands-on -- a real run's last regular poll missed the just-published mesh/collision
            # files by a hair). Only declare failure if a full revision genuinely never landed.
            status = pull_once(host, remote_scene_dir, out_dir, runner)
            if status is not None and status.quality == "full":
                _note_landing(status, last_status)
                return 0
            tail = _remote_log_tail(host, remote_log, runner)
            print(f"remote job ended without publishing a full revision. Log tail:\n{tail}")
            return 1

        time.sleep(poll_interval_s)
