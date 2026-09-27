"""ffmpeg/ffprobe wrappers: video -> frames + telemetry track (plan §2.2 step 1).

Tool-independent: nothing here knows which pose estimator (COLMAP/GLOMAP/VGGT) consumes the
frames. `ffmpeg`/`ffprobe` are assumed on PATH (repo convention, see the assignment).
"""

import json
import logging
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image

logger = logging.getLogger(__name__)

# Downscale before the Laplacian: sharpness only needs to rank frames against each other, not
# measure absolute blur, and a 480px edge is ~15x fewer pixels than a 4K frame for the same rank.
_SHARPNESS_MAX_EDGE = 480


def probe(video: Path) -> dict:
    """ffprobe summary: duration (s), width, height, fps, has_subtitle_stream."""
    cmd = [
        "ffprobe",
        "-v",
        "error",
        "-print_format",
        "json",
        "-show_format",
        "-show_streams",
        str(video),
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, check=True)
    data = json.loads(result.stdout)
    streams = data.get("streams", [])
    video_stream = next((s for s in streams if s.get("codec_type") == "video"), {})
    # The video stream's own duration, not the container's: DJI files carry subtitle/meta tracks
    # that outlast the video (DJI_0095: 88.0 s container, 87.25 s video), and seeking past the last
    # video frame silently extracts nothing.
    duration = float(video_stream.get("duration") or data.get("format", {}).get("duration", 0.0))
    return {
        "duration": duration,
        "width": int(video_stream.get("width", 0)),
        "height": int(video_stream.get("height", 0)),
        "fps": _parse_rate(video_stream.get("r_frame_rate", "0/1")),
        "has_subtitle_stream": any(s.get("codec_type") == "subtitle" for s in streams),
    }


def _parse_rate(rate: str) -> float:
    """ffprobe reports frame rate as a "num/den" string (e.g. "30000/1001")."""
    num, _, den = rate.partition("/")
    try:
        num_f, den_f = float(num), float(den or 1)
        return num_f / den_f if den_f else 0.0
    except ValueError:
        return 0.0


def extract_frames(
    video: Path,
    out_dir: Path,
    fps: float = 3.0,
    long_edge: int = 1920,
    start_s: float | None = None,
    end_s: float | None = None,
) -> list[Path]:
    """Extract frames at `fps`, longest edge capped to `long_edge`px, as `out_dir/0001.jpg`...

    `-ss` before `-i` (fast seek) resets output timestamps to 0 at the seek point, so `end_s` is
    converted to a `-t` duration when `start_s` is also given.
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    cmd = ["ffmpeg", "-y"]
    if start_s is not None:
        cmd += ["-ss", str(start_s)]
    cmd += ["-i", str(video)]
    if end_s is not None:
        cmd += ["-t", str(end_s - start_s)] if start_s is not None else ["-to", str(end_s)]
    # force_original_aspect_ratio=decrease: scale so both dims fit in long_edge x long_edge,
    # i.e. the *longer* side becomes long_edge (or stays smaller) -- exactly "cap the long edge".
    vf = f"fps={fps},scale={long_edge}:{long_edge}:force_original_aspect_ratio=decrease"
    cmd += ["-vf", vf, "-q:v", "2", str(out_dir / "%04d.jpg")]
    subprocess.run(cmd, capture_output=True, text=True, check=True)
    return sorted(out_dir.glob("*.jpg"))


def extract_frame_at(
    video: Path, out_path: Path, timestamp_s: float, long_edge: int | None = None
) -> Path:
    """Extract a single frame at `timestamp_s`, native resolution by default (no scale filter) --
    for ArUco detection at full resolution (`run.py`'s SfM frames are downscaled to `--long-edge`,
    but marker decoding wants every real pixel, plan §2's "detect on full-resolution frames").
    `-ss` before `-i` (fast seek, same convention as `extract_frames`). `long_edge`, if given, caps
    the longest side the same way `extract_frames` does -- `pipeline.fast`'s preview stage wants a
    small downscaled frame for VGGT's own forward pass (cheaper disk I/O/JPEG decode than loading
    a full-res frame just to downsize it in Python) alongside the native-res one it also needs for
    texturing, both via this same seek-based single-frame extraction."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    cmd = ["ffmpeg", "-y", "-ss", str(timestamp_s), "-i", str(video), "-frames:v", "1"]
    if long_edge is not None:
        cmd += ["-vf", f"scale={long_edge}:{long_edge}:force_original_aspect_ratio=decrease"]
    cmd += ["-q:v", "2", str(out_path)]
    subprocess.run(cmd, capture_output=True, text=True, check=True)
    return out_path


def frame_pts(video: Path) -> np.ndarray:
    """Presentation timestamps (s) of every video frame, relative to the first, in display order.
    From packet headers (`ffprobe -show_entries packet=pts_time`), so nothing is decoded."""
    cmd = ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time"]
    cmd += ["-of", "csv=p=0", str(video)]
    out = subprocess.run(cmd, capture_output=True, text=True, check=True).stdout
    pts = np.sort([float(x) for x in out.split() if x.strip() not in ("", "N/A")])
    return pts - pts[0]


def seek_frame_indices(pts: np.ndarray, timestamps_s: list[float]) -> list[int]:
    """The display-order frame index `ffmpeg -ss T -i` (accurate seek) outputs for each `T`: the
    first frame whose timestamp is >= T (1 us slack for float rounding), clamped to the last."""
    idx = np.searchsorted(pts, np.asarray(timestamps_s, float) - 1e-6, side="left")
    return np.minimum(idx, len(pts) - 1).tolist()


def extract_frames_at(video: Path, items: list[tuple[float, Path]]) -> list[Path]:
    """Native-resolution frames at many timestamps in ONE decode pass: the same frames
    `extract_frame_at` would give one `-ss` seek at a time (`seek_frame_indices`), picked with a
    `select` filter on frame numbers. Per-frame seeks re-decode a GOP each; on the 175-frame kitchen
    clip at 4K that was 87 s."""
    if not items:
        return []
    idx = seek_frame_indices(frame_pts(video), [t for t, _ in items])
    tmp = items[0][1].parent / "_select"
    tmp.mkdir(parents=True, exist_ok=True)
    wanted = sorted(set(idx))
    expr = "+".join(f"eq(n\\,{i})" for i in wanted)
    cmd = ["ffmpeg", "-y", "-i", str(video), "-vf", f"select={expr}", "-fps_mode", "passthrough"]
    cmd += ["-q:v", "2", str(tmp / "%06d.jpg")]
    subprocess.run(cmd, capture_output=True, text=True, check=True)
    got = sorted(tmp.glob("*.jpg"))
    if len(got) != len(wanted):
        raise RuntimeError(
            f"extract_frames_at: wanted {len(wanted)} frames, ffmpeg wrote {len(got)}"
        )
    by_index = dict(zip(wanted, got, strict=True))
    for i, (_, out) in zip(idx, items, strict=True):
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(by_index[i].read_bytes())
    for f in got:
        f.unlink()
    tmp.rmdir()
    return [out for _, out in items]


def extract_srt(video: Path, out_path: Path) -> Path | None:
    """Extract the subtitle/telemetry track (`ffmpeg -map 0:s:0`). None (never raises) when the
    video has no subtitle stream -- older/non-DJI clips, or `Video Subtitles` left off."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    cmd = ["ffmpeg", "-y", "-i", str(video), "-map", "0:s:0", "-c:s", "srt", str(out_path)]
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        out_path.unlink(missing_ok=True)  # ffmpeg can leave a 0-byte file behind on failure
        return None
    return out_path


def sharpness(path: Path) -> float:
    """Variance of the Laplacian (higher = sharper), a standard motion-blur proxy. No OpenCV
    dependency: the 3x3 Laplacian [[0,1,0],[1,-4,1],[0,1,0]] is one shifted-array sum in numpy."""
    with Image.open(path) as img:
        img = img.convert("L")
        img.thumbnail((_SHARPNESS_MAX_EDGE, _SHARPNESS_MAX_EDGE))
        arr = np.asarray(img, dtype=np.float64)
    lap = -4 * arr[1:-1, 1:-1] + arr[:-2, 1:-1] + arr[2:, 1:-1] + arr[1:-1, :-2] + arr[1:-1, 2:]
    return float(lap.var())


def select_sharpest(paths: list[Path], keep_every_window: int) -> list[Path]:
    """Keep the sharpest frame in each consecutive window of `keep_every_window` frames --
    rejects motion-blurred frames while keeping roughly even coverage along the flight path."""
    kept = []
    for i in range(0, len(paths), keep_every_window):
        window = paths[i : i + keep_every_window]
        kept.append(max(window, key=sharpness))
    return kept
