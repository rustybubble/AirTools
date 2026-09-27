"""'Do the whole job' (docs/research/grok-ideas/g5-round4.md pick 3): one sentence runs the Grok
features as a fixed chain, each step landing in the headset as it finishes.

1. `survey`: F10's condition survey of the loaded scene, when the user named no need. Its worst
   pin with a `part_query` becomes the need.
2. `part`: a parts search for the need; the top candidate is selected.
3. `safety`, `rules`, `postcard`: F6, F13 and F5, in parallel.
4. `packet`: F14's job packet (public link unless OFFLINE).
5. `checkout`: opens the hold-to-pay panel (`start_checkout`). It never pays: the user's own
   pinch does. A recalled part stops the chain here.

A fixed chain, not a Grok planner: xAI's multi-agent client tools are a gated beta (G5 call #1),
and six LLM tool picks in a row are slow and fragile. A step that fails, times out or has
nothing to work on is skipped with a spoken note, and the chain goes on.

Each finished step appends `job_step {i, name, status, spoken, seconds, cost_usd}` and then the
step's own actions (`show_survey`, `select_candidate`, `show_safety`, ...) to the run, which
`GET /job/run/{run_id}?after=n` pages through. Runs live in memory, like jobs.

`POST /job/run/{run_id}/cancel` (`cancel`): the headset stopped following the run (the user
switched to another scanned model mid-run). The run's chain task is cancelled at the await it is
in (a step's wait on a search, a model or an LLM call; a replace run's extra model builds too),
the run is marked `cancelled` and ends with `job_done {status: "cancelled"}`, and the session is
free for a new job at once. Background jobs a step started (a search in the jobs table) finish on
their own: other runs and the cache may use them.
"""

import asyncio
import logging
import time
import uuid
from collections import OrderedDict
from dataclasses import dataclass, field
from typing import Any

from server import assets, cache, jobs, packet, postcard, report, rules, safety, survey
from server.config import get_settings
from server.models import Part, SearchRequest

logger = logging.getLogger(__name__)

STEPS = ["survey", "part", "safety", "rules", "postcard", "packet", "checkout"]
STEP_CAP_S = 45.0  # per step; a slower step is skipped (its background job still finishes)
PART_CAP_S = 90.0  # a cold parts search (search + verify + sellers) can take a minute
MAX_RUNS = 64
STOP_LINE = "This model is recalled; I stopped before checkout."
PAY_LINE = "Pay panel's open. Hold to pay when you're ready."


class Skip(Exception):
    """A step with nothing to do or that failed; the message is spoken."""


class Stop(Exception):
    """A step the rest of the chain can't go on without (replace: no such part, nothing found);
    the step is `stopped`, the message spoken, and the chain ends there."""


@dataclass
class Run:
    id: str
    session_id: str
    site: str | None
    goal: str | None
    context: dict[str, Any]
    status: str = "running"  # running | done | stopped | cancelled
    # "job" (this chain) or "replace" (server/replace_job.py); title = the rail's heading
    kind: str = "job"
    title: str | None = None
    started: float = field(default_factory=time.monotonic)
    steps: list[dict[str, Any]] = field(default_factory=list)
    actions: list[dict[str, Any]] = field(default_factory=list)
    # what the steps hand each other
    pin: dict[str, Any] | None = None
    part: Part | None = None
    recalled: bool = False
    rebates_usd: float = 0.0
    packet_url: str | None = None
    # job-cancel: the chain's task and any it spawned (a replace run's next models): cancel() stops them
    tasks: list[asyncio.Task] = field(default_factory=list, repr=False)
    cancel_reason: str | None = None


_runs: OrderedDict[str, Run] = OrderedDict()

CANCELLED = "cancelled"


class Busy(Exception):
    """A run is already going for this session (HTTP 409)."""


def get(run_id: str) -> Run | None:
    return _runs.get(run_id)


def body(run: Run, after: int = 0) -> dict[str, Any]:
    """`GET /job/run/{run_id}?after=n`: the actions from index n, and `next` to ask for."""
    after = max(0, after)
    return {
        "run_id": run.id,
        "status": run.status,
        "steps": run.steps,
        "actions": run.actions[after:],
        "next": len(run.actions),
    }


def plan_steps(site: str | None, goal: str | None) -> list[str]:
    """The step names this run will try (the survey only when there's a scene and no need)."""
    return [s for s in STEPS if s != "survey" or (site and not goal)]


def start(session: Any, site: str | None, goal: str | None, context: dict[str, Any]) -> Run:
    """Start the chain in the background. `session` is the agent session (duck-typed): the
    chosen part becomes its selection, so "is it recalled?" and checkout work after."""
    goal = (goal or "").strip()[:200] or None
    if not goal and not site:
        raise ValueError("name what to fix, or load a scanned scene")
    run = register(session, site, goal, context, plan_steps(site, goal))
    run.tasks.append(asyncio.create_task(_chain(run, session)))
    return run


def register(
    session: Any,
    site: str | None,
    goal: str | None,
    context: dict[str, Any],
    names: list[str],
    kind: str = "job",
    title: str | None = None,
) -> Run:
    """A new run in the table with `job_started` as its action 0 (Busy while the session has one
    running). The caller starts its own chain; `GET /job/run/{run_id}` serves every kind."""
    if any(r.session_id == session.id and r.status == "running" for r in _runs.values()):
        raise Busy("a job is already running for this session")
    run = Run(uuid.uuid4().hex[:12], session.id, site, goal, dict(context), kind=kind, title=title)
    run.steps = [{"i": i, "name": n, "status": "pending"} for i, n in enumerate(names)]
    started: dict[str, Any] = {"run_id": run.id, "steps": names}
    if kind != "job":
        started |= {"kind": kind, "title": title}
    run.actions.append({"name": "job_started", "args": started})
    _runs[run.id] = run
    while len(_runs) > MAX_RUNS:
        oldest = next(iter(_runs.values()))
        if oldest.status == "running":
            break
        _runs.popitem(last=False)
    return run


def cancel(run: Run, reason: str | None = None) -> bool:
    """Stop a running run now (`POST /job/run/{run_id}/cancel`): its tasks are cancelled, every
    step not finished yet is `cancelled`, `job_done {status: "cancelled"}` closes its actions, and
    the session may start another job at once. False (and nothing changes) when it had already
    ended: done, stopped or cancelled."""
    if run.status != "running":
        return False
    run.status = CANCELLED
    run.cancel_reason = (reason or "").strip()[:200] or None
    for step in run.steps:
        if step.get("status") in ("pending", "running"):
            step["status"] = CANCELLED
    args: dict[str, Any] = {"run_id": run.id, "status": CANCELLED, "kind": run.kind}
    if run.cancel_reason:
        args["reason"] = run.cancel_reason
    run.actions.append({"name": "job_done", "args": args})
    current = asyncio.current_task()
    for task in run.tasks:
        if task is not current and not task.done():
            task.cancel()
    logger.info(
        "run %s (%s) cancelled after %.1f s%s",
        run.id,
        run.kind,
        time.monotonic() - run.started,
        f": {run.cancel_reason}" if run.cancel_reason else "",
    )
    return True


def cancelled(run: Run) -> bool:
    """The run was cancelled: its chain writes nothing more."""
    return run.status == CANCELLED


# --- the chain -----------------------------------------------------------------------------


async def _chain(run: Run, session: Any) -> None:
    by_name = {s["name"]: s for s in run.steps}
    try:
        if "survey" in by_name:
            await _step(run, by_name["survey"], _survey(run))
        await _step(run, by_name["part"], _part(run, session), part=True)
        await asyncio.gather(
            _step(run, by_name["safety"], _safety(run)),
            _step(run, by_name["rules"], _rules(run)),
            _step(run, by_name["postcard"], _postcard(run)),
        )
        await _step(run, by_name["packet"], _packet(run))
        await _step(run, by_name["checkout"], _checkout(run))
    except Exception:
        logger.exception("run_job %s crashed", run.id)
    if cancelled(run):  # job-cancel: cancel() already closed the run
        return
    run.status = "stopped" if run.recalled else "done"
    cost = round(sum(s.get("cost_usd") or 0 for s in run.steps), 4)
    total = _part_total(run.part)
    run.actions.append(
        {
            "name": "job_done",
            "args": {
                "run_id": run.id,
                "status": run.status,
                "total_usd": total,
                "after_rebates_usd": max(round(total - run.rebates_usd, 2), 0)
                if total is not None and run.rebates_usd
                else None,
                "packet_url": run.packet_url,
                "cost_usd": cost,
            },
        }
    )
    logger.info(
        "run_job %s %s in %.1f s, $%.4f: %s",
        run.id,
        run.status,
        time.monotonic() - run.started,
        cost,
        ", ".join(f"{s['name']}={s['status']}" for s in run.steps),
    )


async def _step(
    run: Run, step: dict[str, Any], work, part: bool = False, cap_s: float | None = None
) -> None:
    """Run one step under its cap; record `job_step`, then the step's own actions."""
    t0 = time.monotonic()
    actions: list[dict[str, Any]] = []
    cost = None
    cap = cap_s if cap_s is not None else PART_CAP_S if part else STEP_CAP_S
    try:
        spoken, actions, cost = await asyncio.wait_for(work, cap)
        status = "stopped" if step["name"] == "checkout" and run.recalled else "done"
    except Skip as exc:
        status, spoken = "skipped", str(exc)
    except Stop as exc:
        status, spoken = "stopped", str(exc)
    except cache.OfflineMiss:
        status, spoken = "skipped", f"The {step['name']} step needs the uplink; skipped it."
    except TimeoutError:
        status, spoken = "skipped", f"The {step['name']} step took too long; skipped it."
    except Exception as exc:  # noqa: BLE001 -- any failure skips the step, the chain goes on
        logger.warning("run_job %s: %s failed: %s", run.id, step["name"], exc)
        status, spoken = "skipped", f"The {step['name']} step failed; skipped it."
    # job-cancel: a step that finished anyway (it swallowed the cancel) adds nothing
    if cancelled(run):
        return
    step.update(
        status=status,
        spoken=spoken,
        seconds=round(time.monotonic() - t0, 1),
        cost_usd=round(cost, 4) if cost else None,
    )
    run.actions.append({"name": "job_step", "args": dict(step)})
    run.actions.extend(actions)
    logger.info(
        "run_job %s step %s %s %.1f s $%s", run.id, step["name"], status, step["seconds"], cost
    )


def _part_total(part: Part | None) -> float | None:
    if part is None or not part.sellers:
        return None
    seller = part.sellers[part.recommended_seller or 0]
    if seller.price_usd is None:
        return None
    return round(seller.price_usd + (seller.shipping_usd or 0), 2)


# --- steps: each returns (spoken, actions, cost_usd) or raises Skip ----------------------------


async def _survey(run: Run):
    try:
        plan = survey.plan(run.site, focus="all")
    except (LookupError, survey.NotReady) as exc:
        raise Skip(f"No survey: {exc}.") from exc
    if get_settings().OFFLINE and not survey.is_cached(plan):
        raise Skip("No survey: it needs the uplink.")
    job = jobs.start_task("survey", survey.run(plan))  # in the jobs table: the packet reads it
    job = await jobs.wait(job.id, STEP_CAP_S)
    if job is None or job.status != "done":
        raise Skip("The survey didn't finish; skipped it.")
    result = job.result or {}
    run.pin = next((p for p in result.get("pins") or [] if p.get("part_query")), None)
    actions = [
        {"name": "survey_started", "args": {"survey_id": job.id}},
        {
            "name": "show_survey",
            "args": {"survey_id": job.id, "pins": result["pins"], "label": result["label"]},
        },
    ]
    return result["spoken"], actions, result.get("cost_usd")


async def _part(run: Run, session: Any):
    query = run.goal or (run.pin or {}).get("part_query")
    if not query:
        raise Skip("Nothing in the survey needs a part.")
    job = jobs.start_search(SearchRequest(query=query))
    job = await jobs.wait(job.id, PART_CAP_S)
    if job is None or job.status != "done" or not job.candidates:
        raise Skip(f"Couldn't find a part for {query}.")
    run.part = part = job.candidates[0]
    session.candidate_ids = [c.id for c in job.candidates]
    session.selected_part_id = part.id
    # The chosen part joins the notebook, so the packet and the report list it.
    entry = {"type": "placement", "part_id": part.id, "count": 1, "source": "run_job"}
    report.append_notebook(run.session_id, [entry | ({"site": run.site} if run.site else {})])
    actions = [
        {"name": "search_started", "args": {"job_id": job.id}},
        {"name": "select_candidate", "args": {"index": 0}},
    ]
    return f"Picked the {part.name}.", actions, None


def _need_part(run: Run, what: str) -> Part:
    if run.part is None:
        raise Skip(f"No part, so no {what}.")
    return run.part


async def _safety(run: Run):
    part = _need_part(run, "recall check")
    # ponytail: cache.get ignores the TTL, so a refetch of an expired entry is logged as $0
    fresh = cache.get("safety_grok", part.id) is None
    verdict = await safety.check(part)
    run.recalled = verdict.verdict == "recalled"
    action = {
        "name": "show_safety",
        "args": {"part_id": part.id, "verdict": verdict.verdict, "headline": verdict.headline},
    }
    return verdict.spoken, [action], verdict.cost_usd if fresh else 0.0


async def _rules(run: Run):
    part = _need_part(run, "rules check")
    kind = rules.job_for(part.name)
    if kind is None:
        raise Skip("No permit rules on file for this kind of job.")
    address = str(run.context.get("address") or "")[:200] or None
    location = run.context.get("location") or (None if address else get_settings().DEFAULT_LOCATION)
    try:
        juris = await rules.jurisdiction(address, location)
    except (cache.OfflineMiss, rules.NoMatch) as exc:
        raise Skip("Couldn't place the address for the rules check.") from exc
    fresh = not rules.is_cached(juris, kind)
    check = rules.start(juris, kind, [part])  # in the jobs table: the packet reads it
    check = await jobs.wait(check.id, STEP_CAP_S)
    if check is None or check.status != "done":
        raise Skip("The rules check didn't finish; skipped it.")
    result = check.result or {}
    incentives = (result.get("money") or {}).get("incentives") or []
    # F13's counted rows only: an unverified or not-eligible amount never comes off the total
    run.rebates_usd = sum(i["amount_usd"] for i in incentives if rules.counted(i))
    return (
        result["spoken"],
        [{"name": "show_rules", "args": rules.body(check)}],
        result.get("cost_usd") if fresh else 0.0,
    )


async def _postcard(run: Run):
    part = _need_part(run, "preview")
    ctx, pin = run.context, run.pin or {}
    frame_id = ctx.get("frame_id") or pin.get("frame_id")
    b64 = ctx.get("frame_jpg_b64")
    if not b64 and not (run.site and frame_id):
        raise Skip("No camera view for the preview.")
    photo = assets.part_dir(part.id) / "image.jpg"
    if not photo.is_file() and part.image_url:  # asset resolution may not have got there yet
        await assets.fetch_image(part.image_url, photo)
    try:
        jpg, before_url = postcard.load_site(run.site, frame_id, b64)
        # the survey pin's box is where the problem is: draw the part there
        box = None if b64 or ctx.get("frame_id") else pin.get("box")
        where = f"replacing the damaged {pin['element'].replace('_', ' ')}" if pin else ""
        out = await postcard.make_postcard(
            part, jpg, placement=where, box=box, before_url=before_url
        )
    except ValueError as exc:
        raise Skip(f"No preview: {exc}.") from exc
    args = {k: out[k] for k in ("part_id", "image_url", "before_url", "label")}
    cost = 0.0 if out.get("cached") else out.get("cost_usd")
    return "Here's how it'll look installed.", [{"name": "show_postcard", "args": args}], cost


async def _packet(run: Run):
    try:
        record = await packet.make(run.session_id)
        spoken = "The job packet's up: scan the code. The link is public for 7 days."
    except packet.PublishError as exc:
        record, spoken = exc.record, "Couldn't post the packet publicly; it's on the local network."
    if record is None:
        raise Skip("Nothing in the notebook for a packet.")
    if not record["public"] and "error" not in record:
        spoken = "The job packet is on the local network only (offline)."
    run.packet_url = record["url"] or record["local_url"]
    return spoken, [packet.show_action(record)], record.get("cost_usd")


async def _checkout(run: Run):
    part = _need_part(run, "checkout")
    if run.recalled:
        return STOP_LINE, [], None
    # The same action as the start_checkout tool: the panel opens, the user's pinch pays.
    args = {"seller_index": part.recommended_seller or 0, "part_id": part.id}
    return PAY_LINE, [{"name": "start_checkout", "args": args}], None
