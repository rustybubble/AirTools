"""Walk-in renovation clip (docs/research/grok-ideas/g3-round2.md, pick 2).

A real capture frame *moves into* the renovated view: `grok-imagine-video-1.5` with the first
frame pinned to a capture camera ~0.6 m behind the one the user looks through, and the last frame
pinned to that view reimagined by `imagine.edit()` (same prompt + KEEP_CLAUSE as F2's reimagine,
so a view already reimagined reuses its cached edit for $0).

`start()` does the quick, fallible part inline (frame pick, cache, edit, video submit), so a bad
frame / moderation block / OFFLINE miss reaches the caller as an error; polling and the mp4
download run as a `jobs.start_task` background job. xAI's video URL is temporary, so the mp4 is
downloaded the moment it's done to `<DATA_DIR>/imagine/<clip id>.mp4`; the edited end frame
doubles as the poster (`<clip id>.jpg`) and as the still shown if the render fails.
"""

import asyncio
import hashlib
import json
import logging
import time
from pathlib import Path
from typing import Any

import httpx
import numpy as np

from server import cache, imagine, jobs
from server.config import get_settings

logger = logging.getLogger(__name__)

VIDEO_URL = "https://api.x.ai/v1/videos/generations"
POLL_URL = "https://api.x.ai/v1/videos/{}"
MODEL = "grok-imagine-video-1.5"  # classic grok-imagine-video rejects `last_frame`
LABEL = imagine.LABEL  # "AI preview, not to scale" -- Unity always shows it
BACK_M = 0.6  # start camera this far behind the user's view, along its view axis
MIN_FACING = 0.9  # cos of the angle between the two view axes
FALLBACK_STEP = 30  # no camera behind: take the one ~30 entries earlier (0301 is 36 before 0481)
POLL_S = 5.0
POLL_TIMEOUT_S = 300.0
SHARE_EXPIRES_S = 7 * 86400  # public_url lifetime for the QR code (xAI allows 1 h - 30 d)
RENDERING_LINE = "Rendering a walk-in; about a minute."
READY_LINE = "Here's the walk-in."
FAILED_LINE = "I couldn't render that; here's the still."
VIDEO_PROMPT = (
    "Handheld camera moves smoothly from the first view to the last; the room is renovated as "
    "described: {prompt}. Everything else stays the same. No people."
)


class FlythroughError(imagine.ImagineError):
    """The video job came back failed/expired, or never finished."""


def clip_path(clip_id: str, ext: str) -> Path | None:
    """`<DATA_DIR>/imagine/<id>.<ext>` for a well-formed id (imagine's id check), else None."""
    path = imagine.image_path(clip_id)
    return path.with_suffix(f".{ext}") if path else None


def _cameras(site: str) -> list[dict[str, Any]]:
    if not imagine._SITE_RE.match(site):
        raise ValueError("invalid site")
    base = Path(get_settings().SCENE_DIR) / site
    try:
        scene = json.loads((base / "scene.json").read_text())
        return json.loads((base / (scene.get("cameras") or "cameras.json")).read_text())
    except (OSError, json.JSONDecodeError, AttributeError) as exc:
        raise ValueError(f"unknown site {site!r}") from exc


def pick_frames(site: str, to_frame_id: str, back_m: float = BACK_M) -> tuple[str, str]:
    """(start frame id, end frame id). Start = the camera facing the same way whose position is
    closest to `back_m` behind the end camera along its view axis (R is world-to-camera, so row 2
    is the view axis in world space); `FALLBACK_STEP` entries earlier when none is within
    `back_m / 2`."""
    cams = _cameras(site)
    idx = next((i for i, c in enumerate(cams) if str(c.get("id")) == to_frame_id), None)
    if idx is None:
        raise ValueError(f"unknown frame {to_frame_id!r}")
    pos = np.array([c["position"] for c in cams], dtype=float)
    fwd = np.array([c["R"][2] for c in cams], dtype=float)
    d = pos[idx] - pos  # from each camera to the end camera
    along = d @ fwd[idx]
    lateral = np.linalg.norm(d - np.outer(along, fwd[idx]), axis=1)
    err = np.abs(along - back_m) + lateral
    err[(fwd @ fwd[idx]) < MIN_FACING] = np.inf
    err[idx] = np.inf
    best = int(np.argmin(err))
    if err[best] > back_m / 2:
        best = (
            idx - FALLBACK_STEP if idx >= FALLBACK_STEP else min(idx + FALLBACK_STEP, len(cams) - 1)
        )
    return str(cams[best]["id"]), str(cams[idx]["id"])


async def _xai(method: str, url: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
    key = get_settings().GROK_API_KEY
    if not key:
        raise imagine.ImagineError("GROK_API_KEY not set")
    async with httpx.AsyncClient(timeout=imagine.TIMEOUT_S) as client:
        resp = await client.request(
            method, url, json=body, headers={"Authorization": f"Bearer {key}"}
        )
    if resp.status_code == 429:
        raise imagine.RateLimited("xAI rate limit")
    if resp.status_code >= 400:
        text = resp.text[:300]
        if resp.status_code in (400, 403, 422) and imagine._MODERATION_RE.search(text):
            raise imagine.ModerationBlocked(text)
        raise imagine.ImagineError(f"xAI {resp.status_code}: {text}")
    return resp.json()


def video_args(result: dict[str, Any]) -> dict[str, Any]:
    """The `show_video` action's args."""
    keys = ("video_url", "poster_url", "label", "share_url")
    return {k: result[k] for k in keys if result.get(k)}


async def start(
    site: str,
    frame_id: str,
    prompt: str,
    *,
    duration_s: int = 3,
    resolution: str = "480p",
    public: bool = False,
    end_image_id: str | None = None,
) -> dict[str, Any]:
    """Start (or replay) a clip. Returns `{job_id, status: "pending", ...}`, or
    `{job_id: None, status: "done", video_url, ...}` straight from the cache.

    `end_image_id`: an Imagine edit of `frame_id` already made (a refined reimagine, F18) to land
    on instead of editing the frame again; `prompt` then only steers the video.

    Raises ValueError (bad site/frame/prompt), cache.OfflineMiss, imagine.ModerationBlocked,
    imagine.RateLimited, imagine.ImagineError.
    """
    prompt = prompt.strip()[: imagine.MAX_PROMPT_LEN]
    if not prompt:
        raise ValueError("empty prompt")
    from_id, to_id = pick_frames(site, frame_id)
    key = {
        "site": site,
        "from": from_id,
        "to": to_id,
        "prompt": prompt,
        "duration_s": duration_s,
        "resolution": resolution,
        "public": public,
    }
    if end_image_id:  # only then, so the clips cached before F18 keep their keys
        key["end"] = end_image_id
    hit = cache.get("flythrough", key)
    if hit and clip_path(hit["id"], "mp4").is_file():
        return {
            **hit,
            "job_id": None,
            "status": "done",
            "cached": True,
            "cost_usd": 0.0,
            "latency_s": 0.0,
        }
    if get_settings().OFFLINE:
        raise cache.OfflineMiss(f"offline: no cached flythrough for {key!r}")

    t0 = time.monotonic()
    clip_id = hashlib.sha1(json.dumps(key, sort_keys=True).encode()).hexdigest()
    start_jpg, _, _ = imagine.load_frame(site, from_id)
    if end_image_id:
        end_path = imagine.image_path(end_image_id)
        if end_path is None or not end_path.is_file():
            raise ValueError(f"unknown edit {end_image_id!r}")
        edited, edit_cost = end_path.read_bytes(), 0.0
    else:
        end_jpg, _, _ = imagine.load_frame(site, to_id)
        edited, edit_meta = await imagine.edit([end_jpg], prompt + imagine.KEEP_CLAUSE)
        edit_cost = edit_meta["cost_usd"]
    clip_path(clip_id, "jpg").write_bytes(edited)  # poster, and the still if the render fails

    body: dict[str, Any] = {
        "model": MODEL,
        "prompt": VIDEO_PROMPT.format(prompt=prompt.rstrip(".")),
        "image": {"url": imagine._data_uri(start_jpg)},
        "last_frame": {"url": imagine._data_uri(edited)},
        "duration": duration_s,
        "resolution": resolution,
        "aspect_ratio": "16:9",
        "generate_audio": False,
    }
    if public:
        body["storage_options"] = {
            "filename": f"{clip_id}.mp4",
            "public_url": {"expires_after": SHARE_EXPIRES_S},
        }
    request_id = (await _xai("POST", VIDEO_URL, body))["request_id"]
    base = {
        "id": clip_id,
        "from_frame": from_id,
        "to_frame": to_id,
        "poster_url": f"/scene/flythrough/{clip_id}.jpg",
        "label": LABEL,
    }
    work = _finish(request_id, key, base, edit_cost, t0)
    job = jobs.start_task("flythrough", work, result=dict(base))
    logger.info("flythrough: submitted %s (%s -> %s) as job %s", request_id, from_id, to_id, job.id)
    return {**base, "job_id": job.id, "status": "pending"}


async def _finish(
    request_id: str, key: dict[str, Any], base: dict[str, Any], edit_cost: float, t0: float
) -> dict[str, Any]:
    """Poll until done, download the mp4 at once (temporary URL), cache the clip's metadata."""
    deadline = time.monotonic() + POLL_TIMEOUT_S
    while True:
        await asyncio.sleep(POLL_S)
        data = await _xai("GET", POLL_URL.format(request_id))
        status = data.get("status")
        if status == "done":
            break
        if status != "pending":
            raise FlythroughError(f"video {status or 'status missing'}")
        if time.monotonic() > deadline:
            raise FlythroughError(f"video not done after {POLL_TIMEOUT_S:.0f} s")
    video = data.get("video") or {}
    if video.get("respect_moderation") is False:
        raise imagine.ModerationBlocked("video filtered by moderation")
    if not video.get("url"):
        raise FlythroughError("xAI returned no video url")
    async with httpx.AsyncClient(timeout=imagine.TIMEOUT_S) as client:
        resp = await client.get(video["url"])
    resp.raise_for_status()
    clip_path(base["id"], "mp4").write_bytes(resp.content)

    ticks = (data.get("usage") or {}).get("cost_in_usd_ticks") or 0
    video_cost = ticks / imagine.TICKS_PER_USD
    meta = {
        **base,
        "video_url": f"/scene/flythrough/{base['id']}.mp4",
        "share_url": (video.get("file_output") or {}).get("public_url"),
        "cost_usd": round(edit_cost + video_cost, 4),
        "latency_s": round(time.monotonic() - t0, 1),
    }
    cache.put("flythrough", key, meta)
    logger.info(
        "flythrough: %s video $%.4f (+ edit $%.4f), %.1f s, %d bytes",
        base["id"][:8],
        video_cost,
        edit_cost,
        meta["latency_s"],
        len(resp.content),
    )
    return {**meta, "cached": False}
