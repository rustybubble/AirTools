"""In-memory search jobs + background asset resolution (plan §1b, §4b.3).

No DB: `_jobs` is a plain dict, cleared on restart -- part.json on disk is the durable copy.
"""

import asyncio
import logging
import re
import uuid
from collections import OrderedDict
from collections.abc import Awaitable, Callable

from server import assets, cache, catalog_index, replace, search
from server.config import get_settings
from server.models import Cavity, Job, Part, SearchRequest

logger = logging.getLogger(__name__)

MAX_JOBS = 512  # ponytail: simple LRU cap keyed on creation order, not last-access

_jobs: "OrderedDict[str, Job]" = OrderedDict()
_done_events: dict[str, asyncio.Event] = {}
_cavities: dict[str, Cavity] = {}  # a gap-fitted search's gap (the summary's scale hint)

_NUMBER_WORDS = {1: "One", 2: "Two", 3: "Three", 4: "Four", 5: "Five"}

# Spoken when OFFLINE and a search came back empty (no cached parts matched the query) -- see
# server/warm.py, which pre-caches this line's TTS audio too.
OFFLINE_NO_CANDIDATES = "The supply lines are down; only the cached parts are aboard."


def _evict_old_jobs() -> None:
    """Cap `_jobs`/`_done_events` at `MAX_JOBS`, oldest first -- but a `status == "running"`
    job is never evicted (its background task still holds a reference to it and will `set()`
    its event), so a long burst of running jobs can still push the dict past the cap; that's
    an acceptable ceiling for "simple LRU eviction", not a hard limit."""
    if len(_jobs) <= MAX_JOBS:
        return
    for job_id, job in list(_jobs.items()):
        if len(_jobs) <= MAX_JOBS:
            break
        if job.status == "running":
            continue
        del _jobs[job_id]
        _done_events.pop(job_id, None)
        _cavities.pop(job_id, None)


def start_search(req: SearchRequest) -> Job:
    job = Job(
        id=uuid.uuid4().hex[:12],
        status="running",
        stage="searching the supply shops",
        query=req.query,
    )
    _jobs[job.id] = job
    _done_events[job.id] = asyncio.Event()
    if req.cavity is not None:
        _cavities[job.id] = req.cavity
    asyncio.create_task(_run_search(job, req))
    _evict_old_jobs()
    return job


async def _run_search(job: Job, req: SearchRequest) -> None:
    def progress(stage: str) -> None:
        job.stage = stage

    try:
        candidates = await search.find_parts(req, progress)
        for part in candidates:
            part.asset.status = "pending"
            save_part(part)
        _index(candidates, req.query)
        job.candidates = candidates
        job.status = "done"
        job.stage = "done"
    except Exception as exc:
        logger.exception("search job %s failed", job.id)
        job.status = "failed"
        job.error = str(exc)
    finally:
        _done_events[job.id].set()

    if job.status == "done" and job.candidates:
        asyncio.create_task(_resolve_assets(job.candidates, req.asset_mode))


def _index(parts: list[Part], query: str) -> None:
    """catalog: the catalog's lazy search sees a finished search's parts at once."""
    try:
        catalog_index.INDEX.add_parts(
            [p.model_dump(mode="json") for p in parts], query=query, from_part_json=True
        )
    except Exception:
        logger.exception("catalog index update failed")


def start_task(stage: str, work: Awaitable[dict], result: dict | None = None) -> Job:
    """A non-search background job in the same table (same eviction, same `wait()`), e.g. a
    flythrough clip or a rules check; `stage` names its kind and stays put. `result` is what
    pollers see while it runs; `work`'s dict is merged into it when it finishes. A raise marks
    the job failed with `error` and keeps `result`."""
    job = Job(id=uuid.uuid4().hex[:12], status="running", stage=stage, result=result or {})
    _jobs[job.id] = job
    _done_events[job.id] = asyncio.Event()
    asyncio.create_task(_run_task(job, work))
    _evict_old_jobs()
    return job


async def _run_task(job: Job, work: Awaitable[dict]) -> None:
    try:
        job.result = {**(job.result or {}), **await work}
        job.status = "done"
    except Exception as exc:
        logger.exception("%s job %s failed", job.stage, job.id)
        job.status = "failed"
        job.error = str(exc)
    finally:
        _done_events[job.id].set()


async def _resolve_assets(candidates: list[Part], mode: str = "auto") -> None:
    """One sequential worker per job -- assets.py rate-limits the AI-mesh tier globally
    (anon HF ZeroGPU quota), but this also keeps disk/CPU work off the request path in order.
    asset-mode: the search's `asset_mode` picks the generator; afterwards every candidate's
    Hunyuan mesh is queued on the HF warm queue (made ahead of need, within the quota)."""
    mode = assets.normalize_mode(mode)
    for part in candidates:
        try:
            if mode == "auto":
                await assets.resolve_shared(part)  # the replace job may be awaiting this one too
            else:
                await assets.resolve_shared(part, mode=mode)
        except Exception:
            logger.exception("asset resolution failed for part %s", part.id)
            failed = load_part(part.id) or part
            failed.asset.status = "failed"
            save_part(failed)
    from server import hf_warm  # asset-mode (hf_warm imports jobs)

    hf_warm.enqueue_many(candidates)


def get(job_id: str) -> Job | None:
    return _jobs.get(job_id)


def latest(stage: str, match: Callable[[dict], bool] = lambda _r: True) -> dict | None:
    """The result of the newest finished `stage` job that `match`es, e.g. the last survey of a
    site (the job packet reads other features' results this way)."""
    return next(
        (
            j.result
            for j in reversed(_jobs.values())
            if j.stage == stage and j.status == "done" and match(j.result or {})
        ),
        None,
    )


async def wait(job_id: str, timeout_s: float) -> Job | None:
    """Poll (via an Event) until `job_id` reaches done/failed, or `timeout_s` elapses."""
    event = _done_events.get(job_id)
    if event is not None:
        try:
            await asyncio.wait_for(event.wait(), timeout=timeout_s)
        except TimeoutError:
            pass
    return _jobs.get(job_id)


def save_part(part: Part) -> None:
    """Write part.json. A re-search of an already-resolved part keeps the ready asset on disk,
    so `resolve_asset` stays idempotent instead of replacing a good mesh with a proxy."""
    path = assets.part_dir(part.id) / "part.json"
    if part.asset.status == "pending" and (path.parent / "model.glb").exists():
        old = load_part(part.id)
        if old and old.asset.status == "ready":
            part = part.model_copy(update={"asset": old.asset})
    cache.write_json_atomic(path, part.model_dump_json(indent=2))


def load_part(part_id: str) -> Part | None:
    path = assets.part_dir(part_id) / "part.json"
    if not path.exists():
        return None
    try:
        return Part.model_validate_json(path.read_text())
    except ValueError:  # pydantic ValidationError / json decode error, both ValueError
        return None


def _pluralize(word: str) -> str:
    """Naive suffix rules, no irregulars.
    ponytail: good enough for spoken part names; swap in a real inflector if it ever mangles one."""
    if word.endswith(("s", "x", "z", "ch", "sh")):
        return word + "es"
    if word.endswith("y") and word[-2:-1] not in "aeiou":
        return word[:-1] + "ies"
    return word + "s"


_NOT_NOUNS = {"a", "an", "the", "some", "me", "new", "replacement", "spare"}


def summary(job: Job) -> str:
    """Deterministic, <=2 short sentences, spoken by the quartermaster voice."""
    if job.status == "failed" or not job.candidates:
        if get_settings().OFFLINE and not job.candidates:
            return OFFLINE_NO_CANDIDATES
        return "No parts found that fit; try describing it differently."

    count = len(job.candidates)
    first = job.candidates[0]
    # the query names the thing ("joist hanger for a 2x6"); product names end in noise ("with
    # Screw"). Head noun = last plain word before any "for/with/..." clause.
    head = re.split(r"\b(?:for|with|to|that|in|on|of)\b", job.query.lower())[0]
    words = [w for w in re.findall(r"\b[a-z]+\b", head) if w not in _NOT_NOUNS]
    last_word = (words or ["part"])[-1]
    noun = last_word if count == 1 else _pluralize(last_word)
    count_word = _NUMBER_WORDS.get(count, str(count))
    subject = "It" if count == 1 else "The first"

    fit = first.fit
    if fit.status == "fits" and fit.spare_mm is not None and abs(fit.spare_mm) < 2:
        second = f"{subject} is an exact fit."
    elif fit.status == "fits" and fit.spare_mm is not None:
        second = f"{subject} fits with {fit.spare_mm:.0f} millimetres to spare."
    elif fit.status == "too_big" and fit.spare_mm is not None:
        second = f"{subject} is too big by {abs(fit.spare_mm):.0f} millimetres."
    elif fit.status == "too_small" and fit.spare_mm is not None:
        second = f"{subject} is too small by {abs(fit.spare_mm):.0f} millimetres."
    elif fit.status == "fits":  # no spare_mm: a "length" run constrains no part dimension
        second = f"{subject} fits the run." if fit.axis == "length" else f"{subject} fits."
    else:
        second = "Fit against the measurement is unknown."

    # A gap-fitted search where nothing fits a gap no real one could fill: the scale is off.
    cavity = _cavities.get(job.id)
    words = [replace.fits_word(p.fit) for p in job.candidates]
    hint = replace.scale_hint(cavity, words) if cavity is not None else None
    return f"{count_word} {noun}. {second}" + (f" {hint}" if hint else "")
