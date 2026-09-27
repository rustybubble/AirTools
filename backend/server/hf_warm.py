"""asset-mode: the HF warm queue -- Hunyuan3D meshes for searched and catalog parts made ahead of
need, so switching the headset's 3D models to "HF" is instant (docs/api.md "Asset modes").

One worker, one generation at a time (the HF ZeroGPU quota is per token / per IP, and
`assets.ai_mesh` holds a semaphore anyway). A part is queued once; an on-demand "hf" request moves
it to the front. When every HF token's and the IP's quota is spent (`assets.warm_hf` says
"quota"), the worker backs off -- BACKOFF_START_S, doubling to BACKOFF_MAX_S -- and retries the same
part; a success resets the backoff. Nothing runs OFFLINE or with HF_WARM off. A part whose
Hunyuan mesh exists is never queued (`assets.hf_final`): HF results are made once and kept.

    enqueue(part_or_id, front=False)   # jobs._resolve_assets, GET/POST ?mode=hf, the catalog
    status()                           # GET /assets/hf-warm
"""

import asyncio
import collections
import logging
import time

from server import assets, jobs
from server.config import get_settings
from server.models import Part

logger = logging.getLogger(__name__)

BACKOFF_START_S = 300.0
BACKOFF_MAX_S = 3600.0
PAUSE_S = 1.0  # between generations: be gentle with the shared Space
RECENT = 20

_queue: collections.deque[str] = collections.deque()
_queued: set[str] = set()
_worker: asyncio.Task | None = None
_state: dict = {}


def reset() -> None:
    """Forget the queue and counters (tests)."""
    global _worker
    if _worker is not None and not _worker.done():
        _worker.cancel()
    _worker = None
    _queue.clear()
    _queued.clear()
    _state.clear()
    _state.update(
        current=None,
        made=0,
        mismatch=0,
        failed=0,
        no_photo=0,
        quota_hits=0,
        backoff_s=0.0,
        resume_at=0.0,
        recent=[],
    )


reset()


def enabled() -> bool:
    s = get_settings()
    return bool(s.HF_WARM) and not s.OFFLINE


def enqueue(part: Part | str, *, front: bool = False) -> bool:
    """Queue the part's Hunyuan mesh (front: next up). False when there's nothing to do: warm off
    or offline, the mesh already exists, or the part has no photo to make it from."""
    pid = part if isinstance(part, str) else part.id
    if not enabled() or not pid or assets.hf_final(pid):
        return False
    if not isinstance(part, str):
        photo = assets.part_dir(pid) / "image.jpg"
        if not part.image_url and not photo.exists():
            return False
    if pid in _queued:
        if front and _queue and _queue[0] != pid and _state["current"] != pid:
            _queue.remove(pid)
            _queue.appendleft(pid)
        _ensure_worker()
        return True
    _queued.add(pid)
    if front:
        _queue.appendleft(pid)
    else:
        _queue.append(pid)
    _ensure_worker()
    return True


def enqueue_many(parts: list[Part]) -> int:
    return sum(enqueue(p) for p in parts)


def _ensure_worker() -> None:
    global _worker
    if _worker is not None and not _worker.done():
        return
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:  # no loop (a sync caller): the next async enqueue starts it
        return
    _worker = loop.create_task(_run())


def _record(pid: str, outcome: str, seconds: float) -> None:
    key = {"ready": "made"}.get(outcome, outcome)
    if key in _state and isinstance(_state[key], int):
        _state[key] += 1
    _state["recent"].append({"part_id": pid, "outcome": outcome, "seconds": round(seconds, 1)})
    del _state["recent"][:-RECENT]


async def _run() -> None:
    while _queue:
        wait = _state["resume_at"] - time.time()
        if wait > 0:
            await asyncio.sleep(wait)
        if not enabled():
            await asyncio.sleep(5.0)
            continue
        pid = _queue[0]
        part = jobs.load_part(pid)
        if part is None or assets.hf_final(pid):
            _pop(pid)
            continue
        _state["current"] = pid
        t0 = time.monotonic()
        outcome = await assets.warm_hf(part)
        _state["current"] = None
        if outcome == "quota":
            _state["quota_hits"] += 1
            backoff = min(max(_state["backoff_s"] * 2, BACKOFF_START_S), BACKOFF_MAX_S)
            _state["backoff_s"] = backoff
            _state["resume_at"] = time.time() + backoff
            logger.info("hf warm: ZeroGPU quota spent at %s; backing off %.0f s", pid, backoff)
            continue
        _pop(pid)
        _record(pid, outcome, time.monotonic() - t0)
        _state["backoff_s"] = 0.0
        logger.info(
            "hf warm: %s %s in %.1f s (%d queued)", pid, outcome, time.monotonic() - t0, len(_queue)
        )
        await asyncio.sleep(PAUSE_S)


def _pop(pid: str) -> None:
    if _queue and _queue[0] == pid:
        _queue.popleft()
    elif pid in _queue:
        _queue.remove(pid)
    _queued.discard(pid)


def status() -> dict:
    """The queue for GET /assets/hf-warm: what's queued and running, counts, the backoff, and
    the last ZeroGPU quota messages the Space sent."""
    return {
        "enabled": enabled(),
        "queued": list(_queue),
        "current": _state["current"],
        "made": _state["made"],
        "mismatch": _state["mismatch"],
        "failed": _state["failed"],
        "no_photo": _state["no_photo"],
        "quota_hits": _state["quota_hits"],
        "backoff_s": _state["backoff_s"],
        "resume_in_s": max(0.0, round(_state["resume_at"] - time.time(), 1)),
        "recent": list(_state["recent"]),
        "last_ai_mesh": dict(assets.AI_MESH_LAST),
        "quota_messages": list(assets.HF_QUOTA_SEEN),
        "hf_keys_cooling_s": round(assets.HF_KEYS.cooling_s(), 1),
    }


async def drain(timeout_s: float = 10.0) -> None:
    """Wait for the queue to empty (tests)."""
    t0 = time.monotonic()
    while (
        _queue or (_worker is not None and not _worker.done())
    ) and time.monotonic() - t0 < timeout_s:
        await asyncio.sleep(0.01)
