"""Grok Imagine image edits: the thin xAI client plus "Reimagine this view"
(docs/research/grok-ideas/g1-imagine.md §1 and #1).

`edit()` sends capture frames + a prompt to xAI `POST /v1/images/edits` as plain JSON (the
OpenAI SDK's `images.edit()` sends multipart, which xAI rejects). Every call goes through
`cache.cached("imagine", ...)`, so OFFLINE replays rehearsed prompts. The JSON cache only holds
the metadata; the JPEG itself lands at `<DATA_DIR>/imagine/<id>.jpg`, which is also what
`GET /scene/reimagine/{id}.jpg` serves.

`load_frame()` loads one frame of a scene package plus its camera from `cameras.r<rev>.json`.
Edits come back pixel-aligned with their source (0 px shift measured), so the headset can show
them on a quad at that camera's pose. Callers: `reimagine()` below and the "see it installed"
postcard (server/postcard.py).

`refine()` chains voice refinements ("darker blue", "no, lighter") onto a reimagine: each step
sends the latest edit plus the original frame, measures `drift()` against the original and, past
`DRIFT_MAX`, retries once from the original with every step merged into one prompt
(docs/research/grok-ideas/f18-refine.md). The per-session stack lives in `agent.Session`.
"""

import base64
import hashlib
import io
import json
import logging
import re
import time
from pathlib import Path
from typing import Any

import httpx
import numpy as np
from PIL import Image, ImageFilter

from server import cache
from server.config import get_settings

logger = logging.getLogger(__name__)

EDITS_URL = "https://api.x.ai/v1/images/edits"
MODEL = "grok-imagine-image-2.0"
LABEL = "AI preview, not to scale"
MAX_IMAGES = 5  # xAI multi-image edit cap
MAX_PROMPT_LEN = 500
TIMEOUT_S = 120.0
TICKS_PER_USD = 1e10
# The clause that kept sign text, fridge magnets and framing unchanged in G1's run.
KEEP_CLAUSE = " Keep the camera, framing and everything else in the photo unchanged."
# Image 1 = the latest edit (sets the output aspect), image 2 = the original capture frame.
REFINE_PROMPT = (
    "Image 1 is an edited version of image 2, the original photo. Edit image 1: {change}. Change "
    "only that and keep the earlier edits. Keep everything else identical to image 2, with the "
    "same camera and framing."
)
DRIFT_SIZE = (160, 90)  # drift is measured on this grey thumbnail (JPEG noise averages out)
REGION_DIFF = 30  # /255: a pixel the first edit changed by more than this is "edited region"
# /255 median outside that region. Measured (f18-refine.md): a fresh edit 2.2, chains with the
# original 3.6-7.6, without it 31 once a step relit the room. Between those, with margin.
DRIFT_MAX = 12.0

_ID_RE = re.compile(r"^[0-9a-f]{40}$")
_SITE_RE = re.compile(r"^[a-z0-9-]+$")
_MODERATION_RE = re.compile(r"moderat|safety|policy|blocked|not allowed", re.IGNORECASE)


class ImagineError(Exception):
    """Any failed edit (bad key, 5xx, malformed response)."""


class ModerationBlocked(ImagineError):
    """xAI's moderation rejected the prompt or the image."""


class RateLimited(ImagineError):
    """xAI answered 429."""


def image_path(image_id: str) -> Path | None:
    """`<DATA_DIR>/imagine/<id>.jpg` for a well-formed id, else None (no path tricks)."""
    if not _ID_RE.match(image_id):
        return None
    return Path(get_settings().DATA_DIR) / "imagine" / f"{image_id}.jpg"


def _data_uri(jpg: bytes) -> str:
    return "data:image/jpeg;base64," + base64.b64encode(jpg).decode()


async def _call_xai(body: dict[str, Any]) -> dict[str, Any]:
    key = get_settings().GROK_API_KEY
    if not key:
        raise ImagineError("GROK_API_KEY not set")
    async with httpx.AsyncClient(timeout=TIMEOUT_S) as client:
        resp = await client.post(EDITS_URL, json=body, headers={"Authorization": f"Bearer {key}"})
    if resp.status_code == 429:
        raise RateLimited("xAI rate limit")
    if resp.status_code >= 400:
        text = resp.text[:300]
        if resp.status_code in (400, 403, 422) and _MODERATION_RE.search(text):
            raise ModerationBlocked(text)
        raise ImagineError(f"xAI {resp.status_code}: {text}")
    return resp.json()


async def edit(
    images: list[bytes], prompt: str, *, model: str = MODEL, resolution: str = "1k"
) -> tuple[bytes, dict[str, Any]]:
    """Edit `images` (JPEG bytes, 1-5; the first sets the output aspect) with `prompt`.

    Returns (jpeg bytes, meta). meta: `id` (content key, names the stored JPEG), `cost_usd` and
    `latency_s` of the live call (0.0 on a cache hit), `cached`.
    """
    if not 1 <= len(images) <= MAX_IMAGES:
        raise ValueError(f"need 1-{MAX_IMAGES} images")
    key = {
        "model": model,
        "resolution": resolution,
        "prompt": prompt,
        "images": [hashlib.sha1(img).hexdigest() for img in images],
    }
    image_id = hashlib.sha1(json.dumps(key, sort_keys=True).encode()).hexdigest()
    fresh = False

    async def call() -> dict[str, Any]:
        nonlocal fresh
        refs = [{"url": _data_uri(img)} for img in images]
        body: dict[str, Any] = {
            "model": model,
            "prompt": prompt,
            "resolution": resolution,
            "n": 1,
            "response_format": "b64_json",
        }
        if len(refs) == 1:
            body["image"] = refs[0]
        else:
            body["images"] = refs
        t0 = time.monotonic()
        data = await _call_xai(body)
        latency_s = round(time.monotonic() - t0, 2)
        item = (data.get("data") or [{}])[0]
        if item.get("respect_moderation") is False:
            raise ModerationBlocked("image filtered by moderation")
        if not item.get("b64_json"):
            raise ImagineError("xAI returned no image")
        ticks = (data.get("usage") or {}).get("cost_in_usd_ticks") or 0
        cost_usd = round(ticks / TICKS_PER_USD, 4)
        out = image_path(image_id)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(base64.b64decode(item["b64_json"]))
        logger.info("imagine: %s edit %s $%.4f %.1f s", model, image_id[:8], cost_usd, latency_s)
        fresh = True
        return {"id": image_id, "cost_usd": cost_usd, "latency_s": latency_s}

    meta = dict(await cache.cached("imagine", key, call))
    # cache keys are normalize()d (case/punctuation-insensitive), so a hit may carry another
    # prompt spelling's id: always read the file the cached meta names.
    path = image_path(meta["id"])
    if not path.exists():  # cache entry survived but the JPEG was deleted
        raise ImagineError(f"cached image {meta['id']} missing on disk")
    meta["cached"] = not fresh
    if not fresh:
        meta["cost_usd"] = 0.0
        meta["latency_s"] = 0.0
    return path.read_bytes(), meta


def load_frame(site: str, frame_id: str) -> tuple[bytes, dict[str, Any], str]:
    """(jpeg bytes, camera entry, package-relative image path) for one capture frame.

    Uses the full-res `file` when the package ships it, else the 640 px `thumb`. Raises
    ValueError for an unknown site or frame.
    """
    if not _SITE_RE.match(site):
        raise ValueError("invalid site")
    base = (Path(get_settings().SCENE_DIR) / site).resolve()
    try:
        scene = json.loads((base / "scene.json").read_text())
        cams_name = scene.get("cameras") or "cameras.json"
        cameras = json.loads((base / cams_name).read_text())
    except (OSError, json.JSONDecodeError, AttributeError) as exc:
        raise ValueError(f"unknown site {site!r}") from exc
    cam = next((c for c in cameras if str(c.get("id")) == frame_id), None)
    if cam is None:
        raise ValueError(f"unknown frame {frame_id!r}")
    for rel in (cam.get("file"), cam.get("thumb")):
        if not rel:
            continue
        p = (base / rel).resolve()
        if p.is_relative_to(base) and p.is_file():
            return p.read_bytes(), cam, rel
    raise ValueError(f"frame {frame_id!r} has no image in the package")


async def reimagine(
    site: str, frame_id: str, prompt: str, *, resolution: str = "1k"
) -> dict[str, Any]:
    """Edit one capture frame. The result registers with the frame's camera (pose+intrinsics)."""
    prompt = prompt.strip()[:MAX_PROMPT_LEN]
    if not prompt:
        raise ValueError("empty prompt")
    jpg, cam, rel = load_frame(site, frame_id)
    _, meta = await edit([jpg], prompt + KEEP_CLAUSE, resolution=resolution)
    camera = {
        k: cam[k] for k in ("R", "t", "position", "fx", "fy", "cx", "cy", "w", "h") if k in cam
    }
    return {
        "id": meta["id"],
        "image_url": f"/scene/reimagine/{meta['id']}.jpg",
        "before_url": f"/scenes/{site}/{rel}",
        "frame_id": frame_id,
        "camera": camera,
        "label": LABEL,
        "cost_usd": meta["cost_usd"],
        "latency_s": meta["latency_s"],
        "cached": meta["cached"],
    }


def _grey(jpg: bytes) -> np.ndarray:
    img = Image.open(io.BytesIO(jpg)).convert("L").convert("F")  # float: no uint8 wrap/rounding
    return np.asarray(img.resize(DRIFT_SIZE, Image.BILINEAR))


def drift(original: bytes, first: bytes, current: bytes) -> float:
    """Median |current - original| (/255) outside the region the first edit changed.

    ponytail: the region is whatever the first edit (the reimagine) changed, grown 2 px at
    160x90. A refinement that adds something outside it ("and LED strips") counts as drift; the
    median shrugs that off while it covers well under half the frame. A per-step region needs
    the user's words mapped to pixels (segmentation), which nothing here does yet.
    """
    orig = _grey(original)
    changed = (np.abs(_grey(first) - orig) > REGION_DIFF).astype(np.uint8) * 255
    region = np.asarray(Image.fromarray(changed).filter(ImageFilter.MaxFilter(5))) > 0
    outside = np.abs(_grey(current) - orig)[~region]
    return round(float(np.median(outside)), 1) if outside.size else 0.0


def merged_prompt(steps: list[str]) -> str:
    """Every step of a chain as one instruction ("navy cabinets; then darker blue")."""
    return "; then ".join(s.strip().rstrip(".") for s in steps)


async def refine(
    site: str, frame_id: str, steps: list[str], image_ids: list[str], *, with_original: bool = True
) -> dict[str, Any]:
    """Apply `steps[-1]` to the latest edit `image_ids[-1]` (to the frame itself when there is
    none yet). `steps` and `image_ids` are the chain so far, oldest first; `image_ids[0]` sets the
    edited region for `drift()`. `with_original=False` sends the latest edit alone (the naive
    chain, kept for the f18 comparison).

    Returns edit()'s meta plus `drift` (None on a first step) and `retried`. Past `DRIFT_MAX`, one
    retry edits the original frame with `merged_prompt(steps)`; the lower-drift image wins and
    `cost_usd` counts both calls.
    """
    change = steps[-1].strip()[:MAX_PROMPT_LEN]
    if not change:
        raise ValueError("empty prompt")
    jpg, _, _ = load_frame(site, frame_id)
    if not image_ids:
        _, meta = await edit([jpg], change + KEEP_CLAUSE)
        return {**meta, "drift": None, "retried": False}
    current = image_path(image_ids[-1]).read_bytes()
    first = image_path(image_ids[0]).read_bytes()
    if with_original:
        out, meta = await edit([current, jpg], REFINE_PROMPT.format(change=change.rstrip(".")))
    else:
        out, meta = await edit([current], change + KEEP_CLAUSE)
    meta = {**meta, "drift": drift(jpg, first, out), "retried": False}
    if meta["drift"] <= DRIFT_MAX:
        return meta
    try:
        out2, meta2 = await edit([jpg], merged_prompt(steps) + "." + KEEP_CLAUSE)
    except (ImagineError, cache.OfflineMiss) as exc:  # keep the drifted step over nothing
        logger.warning("imagine: drift retry failed: %s", exc)
        return meta
    retry_drift = drift(jpg, first, out2)
    logger.info("imagine: drift %.1f > %.1f, retry %.1f", meta["drift"], DRIFT_MAX, retry_drift)
    cost = round(meta["cost_usd"] + meta2["cost_usd"], 4)
    if retry_drift < meta["drift"]:
        return {**meta2, "drift": retry_drift, "retried": True, "cost_usd": cost}
    return {**meta, "retried": True, "cost_usd": cost}
