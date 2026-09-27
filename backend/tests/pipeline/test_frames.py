import json
import shutil
import subprocess
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

from pipeline import frames

pytestmark = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not on PATH")

_TESTSRC = "testsrc=duration=2:size=320x240:rate=10"


@pytest.fixture
def plain_video(tmp_path):
    """2s/10fps synthetic video, no subtitle track."""
    video = tmp_path / "plain.mp4"
    subprocess.run(
        ["ffmpeg", "-y", "-f", "lavfi", "-i", _TESTSRC, "-c:v", "mpeg4", str(video)],
        check=True,
        capture_output=True,
    )
    return video


@pytest.fixture
def subtitled_video(tmp_path):
    """Same clip, plus a 2-cue subtitle track muxed in (DJI SRT stands in for the real telemetry)."""
    srt = tmp_path / "subs.srt"
    srt.write_text(
        "1\n00:00:00,000 --> 00:00:01,000\nhello\n\n2\n00:00:01,000 --> 00:00:02,000\nworld\n"
    )
    video = tmp_path / "subtitled.mp4"
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-f",
            "lavfi",
            "-i",
            _TESTSRC,
            "-f",
            "srt",
            "-i",
            str(srt),
            "-c:v",
            "mpeg4",
            "-c:s",
            "mov_text",
            "-shortest",
            str(video),
        ],
        check=True,
        capture_output=True,
    )
    return video


# --- probe --------------------------------------------------------------------


def test_probe_plain_video(plain_video):
    info = frames.probe(plain_video)
    assert info["duration"] == pytest.approx(2.0, abs=0.2)
    assert info["width"] == 320
    assert info["height"] == 240
    assert info["fps"] == pytest.approx(10.0, abs=0.1)
    assert info["has_subtitle_stream"] is False


def test_probe_subtitled_video(subtitled_video):
    info = frames.probe(subtitled_video)
    assert info["has_subtitle_stream"] is True


def test_probe_prefers_video_stream_duration_over_container(monkeypatch):
    """DJI_0095: the subtitle/meta tracks run to 88.0 s but the video ends at 87.25 s."""
    fake = {
        "format": {"duration": "88.000000"},
        "streams": [
            {
                "codec_type": "video",
                "duration": "87.253833",
                "width": 3840,
                "height": 2160,
                "r_frame_rate": "30000/1001",
            },
            {"codec_type": "subtitle", "duration": "88.000000"},
        ],
    }
    monkeypatch.setattr(
        frames.subprocess,
        "run",
        lambda *a, **k: subprocess.CompletedProcess(a, 0, stdout=json.dumps(fake), stderr=""),
    )
    assert frames.probe(Path("x.mp4"))["duration"] == pytest.approx(87.253833)


# --- extract_frames -------------------------------------------------------------


def test_extract_frames_count_and_long_edge(plain_video, tmp_path):
    out_dir = tmp_path / "frames"
    paths = frames.extract_frames(plain_video, out_dir, fps=3.0, long_edge=160)

    assert len(paths) == pytest.approx(6, abs=1)  # 2s * 3fps
    assert all(p.exists() for p in paths)
    assert paths == sorted(paths)

    with Image.open(paths[0]) as img:
        assert max(img.size) <= 160


def test_extract_frames_start_end_window(plain_video, tmp_path):
    out_dir = tmp_path / "frames_window"
    paths = frames.extract_frames(plain_video, out_dir, fps=5.0, start_s=0.5, end_s=1.5)
    assert len(paths) == pytest.approx(5, abs=1)  # 1s window * 5fps


# --- extract_frame_at -------------------------------------------------------------


def test_extract_frame_at_native_resolution(plain_video, tmp_path):
    out_path = tmp_path / "full" / "0001.jpg"
    result = frames.extract_frame_at(plain_video, out_path, timestamp_s=0.5)

    assert result == out_path
    assert out_path.exists()
    with Image.open(out_path) as img:
        assert img.size == (320, 240)  # _TESTSRC's native size, no --long-edge downscale


# --- extract_frames_at (one decode pass instead of per-frame seeks) -------------------


def test_seek_frame_indices_match_accurate_seek_on_the_pipeline_grid():
    """29.97 fps video, the full stage's 10 fps timestamp grid: `-ss T` outputs the first frame
    at or after T, so frame 600 (20.02 s) for T = 20.0 s, not the nearer frame 599 (19.99 s)."""
    pts = np.arange(2615) * 1001 / 30000
    grid = [(n - 1) / 10 for n in (1, 2, 11, 201, 872)]
    assert frames.seek_frame_indices(pts, grid) == [0, 3, 30, 600, 2611]
    assert frames.seek_frame_indices(pts, [99.0]) == [2614]  # past the end: clamp to the last


def test_extract_frames_at_gives_the_same_frames_as_per_frame_seeks(plain_video, tmp_path):
    times = [0.0, 0.5, 0.52, 1.5]
    items = [(t, tmp_path / "new" / f"{i:04d}.jpg") for i, t in enumerate(times, start=1)]

    frames.extract_frames_at(plain_video, items)

    for t, new in items:
        old = frames.extract_frame_at(plain_video, tmp_path / "old" / new.name, timestamp_s=t)
        assert new.read_bytes() == old.read_bytes()
    assert not (tmp_path / "new" / "_select").exists()


# --- extract_srt ----------------------------------------------------------------


def test_extract_srt_returns_none_without_subtitle_stream(plain_video, tmp_path):
    assert frames.extract_srt(plain_video, tmp_path / "out.srt") is None
    assert not (tmp_path / "out.srt").exists()


def test_extract_srt_extracts_the_track(subtitled_video, tmp_path):
    out = frames.extract_srt(subtitled_video, tmp_path / "telemetry.srt")
    assert out is not None
    text = out.read_text()
    assert "hello" in text
    assert "world" in text


# --- sharpness / select_sharpest -------------------------------------------------


def _save_gray(path, arr):
    Image.fromarray(arr.astype("uint8"), mode="L").save(path)


def test_sharpness_ranks_sharp_above_blurred(tmp_path):
    rng = np.random.default_rng(0)
    noise = rng.integers(0, 256, size=(64, 64), dtype=np.uint8).astype(np.float64)

    sharp_path = tmp_path / "sharp.jpg"
    _save_gray(sharp_path, noise)

    blurred = noise.copy()
    for _ in range(6):  # crude box-blur smoothing pass
        blurred[1:-1, 1:-1] = (
            blurred[:-2, 1:-1] + blurred[2:, 1:-1] + blurred[1:-1, :-2] + blurred[1:-1, 2:]
        ) / 4
    blurred_path = tmp_path / "blurred.jpg"
    _save_gray(blurred_path, blurred)

    assert frames.sharpness(sharp_path) > frames.sharpness(blurred_path)


def test_select_sharpest_keeps_one_per_window(tmp_path):
    rng = np.random.default_rng(1)
    paths = []
    # window 0: frame 1 sharpest; window 1: frame 3 sharpest (index within window)
    levels = [0.2, 0.9, 0.3, 0.1, 0.8, 0.4]
    for i, level in enumerate(levels):
        base = rng.integers(100, 156, size=(32, 32), dtype=np.uint8).astype(np.float64)
        noise = rng.integers(-40, 40, size=(32, 32)).astype(np.float64) * level
        arr = np.clip(base + noise, 0, 255)
        p = tmp_path / f"{i:04d}.jpg"
        _save_gray(p, arr)
        paths.append(p)

    kept = frames.select_sharpest(paths, keep_every_window=3)
    assert kept == [paths[1], paths[4]]
