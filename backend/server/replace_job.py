"""One sentence, the whole replace, autonomously (the app's e2e hand-off p4-e2e, patch 0004):

  "replace the dishwasher with a new one that fits"
    1. remove   remove_component {component_id}       (named, or the part the pointer is on)
    2. measure  measure_cavity {component_id}         (the headset tapes the gap)
    3. scale    scale_gap {component_id, axis, real_m} when the gap is implausible for its kind
    4. search   search_started {job_id}               a search fitted to the gap's W x H x D
    5. pick     the best fit (fits > tight > least over), within the purchase limit
    6. model    the pick's GLB (and the next two's) resolved, a proxy box upgraded once
    7. place    place_part {part_id, model_url, component_id, fits, clearance_mm, cycle}
    job_done    {kind: "replace", spoken, summary, part_id, fits, total_usd, ...}

Each step lands in the headset as it finishes, through the run table and poll of "do the whole
job" (runjob.register; `GET /job/run/{run_id}?after=n`: job_started, job_step, the step's own
actions, job_done); a long search adds `job_step {status: "running"}` progress lines. A fixed
chain, not an LLM planner: the LLM (or the fast path, `parse`) only decides that this is a
replace, which part, and what to search for (agent.py `replace_component`). The follow-ups
("next one", "option 2", "put it back", "undo") are agent._replace_route's, on the session
state this chain leaves behind (removed, candidate_ids, gap_index, scale).

Scale sanity (step 3). The kitchen scan reads ~1.47x small indoors (its scale comes from the
drone's altitude caption). When the headset's scale isn't calibrated yet (context `scale` 1.0)
and the gap is implausible for its kind (PRIORS `plausible_in`, e.g. a dishwasher opening
narrower than 20 in), the job scales the scene from the kind's standard opening: a 34.5 in
dishwasher opening height, a 30 in range, a 30/33/36 in fridge opening, a 34.5 in base cabinet.
Of a kind's standard sizes it takes the first that leaves every other axis plausible too (the
kitchen fridge scaled to 36 in wide would stand 86 in tall; 30 in gives 72 in). On the headset
scale_gap is ScaleCalibration from the gap's own tape: one undoable edit. The job says so.
"""

import asyncio
import logging
import re
import time
from dataclasses import dataclass, field
from typing import Any

from server import assets, jobs, mandate, replace, report, runjob
from server.config import get_settings
from server.models import Cavity, Fit, Part, SearchRequest

logger = logging.getLogger(__name__)

STEPS = ["remove", "measure", "scale", "search", "pick", "model", "place"]
KIND = "replace"
SEARCH_CAP_S = 150.0  # a fresh search (SerpApi limited -> web fallback) can take 60-90 s
MODEL_CAP_S = 60.0  # the pick's GLB: one LLM call + template build, or a queued Groq call
UPGRADE_CAP_S = 25.0  # one more try at a better model than the proxy box
MORE_MODELS_CAP_S = 45.0  # the next two candidates' GLBs, after the pick is in the gap
PROGRESS_EVERY_S = 4.0
FALLBACK_AFTER_S = 45.0  # a named search still running: start the plain kind alongside

# --- the phrases (the fast path; free phrasing goes to the LLM's replace_component tool) ------

_LEAD, _TAIL, _DET, _thing = replace._LEAD, replace._TAIL, replace._DET, replace._thing
_I = re.IGNORECASE
_SPACE = r"(?:gap|space|spot|opening|hole|slot)"
_REPLACE_RE = re.compile(
    _LEAD
    + r"(?:replace|swap\s+out|swap|change\s+out|switch\s+out|upgrade|trade\s+out|trade\s+in)\s+"
    + r"(?:(?:it|this|that)(?:\s+one)?|"
    + _DET
    + _thing("t1")
    + r")(?:\s+out)?(?:\s+(?:with|for)\s+(?P<with>[^.?!]{1,80}?))?"
    + _TAIL,
    _I,
)
_BROKEN_RE = re.compile(
    _LEAD
    + r"(?:the|my|our|this|that)\s+"
    + _thing("t1")
    + r"\s+(?:is|'s|seems|looks)\s+(?:(?:totally|completely|really|so|pretty|kind\s+of)\s+)?"
    + r"(?:broken|broke|dead|busted|shot|leaking|leaky|not\s+working|done\s+for|dying|failing|"
    + r"toast|too\s+(?:old|small|big|loud|noisy))"
    + r"\W+(?P<rest>.*\b(?:new|replace|replacement|another|swap)\b.*)$",
    _I,
)
_NEW_ONE_RE = re.compile(
    _LEAD
    + r"(?:put|install|fit|get|find|give|order|stick|drop|pop)\s+(?:me\s+|us\s+)?(?:in\s+)?"
    + r"(?:a|an|some)\s+(?P<new>(?:new|replacement|different|better|bigger|smaller)\s+)?"
    + _thing("t1")
    + r"(?P<where>\s+(?:in\s+here|in\s+there|in\s+its\s+place|in\s+place|here|there|"
    + r"in\s+(?:the\s+)?"
    + _SPACE
    + r"))?"
    + r"(?:\s+(?:that|which)\s+(?:will\s+|would\s+)?fits?(?:\s+(?:in\s+)?(?:there|here|the\s+"
    + _SPACE
    + r"))?)?(?:\s+and\s+put\s+it\s+in(?:\s+there)?)?"
    + _TAIL,
    _I,
)
_OUT_AND_NEW_RE = re.compile(
    _LEAD
    + r"(?:take\s+out|remove|pull\s+out|rip\s+out|yank\s+out|get\s+rid\s+of)\s+"
    + _DET
    + _thing("t1")
    + r"(?:\s+out)?\s*(?:,\s*|\s+)(?:and\s+)?(?:then\s+)?"
    + r"(?:put|install|fit|get|find|stick|drop|pop|give)\s+(?:me\s+)?(?:in\s+)?"
    + r"(?P<rest>(?:a|an|some)?\s*(?:new|replacement|different|another)\b.*)$",
    _I,
)
# Words in "with a new stainless one that fits" that name no feature of the part.
_FILLER = set(
    re.findall(
        r"\S+",
        "a an the some one ones new replacement different another other better same kind type "
        "similar that which will would can could fits fit to in into here there it its for of "
        "gap space spot opening hole please me us and then put install get find",
    )
)


def _clean_words(words: str | None) -> str:
    """ "a 30-inch induction one that fits" -> "30 inch induction"."""
    t = replace._norm(words)
    t = re.sub(r"(\d+(?:\.\d+)?)\s*(?:-\s*)?(?:inch(?:es)?|in\b|\"|″)", r"\1 inch", t)
    t = re.sub(r"[^a-z0-9 .\-]", " ", t)
    return " ".join(w for w in t.split() if w not in _FILLER)


def query_for(label: str, words: str | None) -> str:
    """The search query: the features the user named, and the part's own noun when they didn't
    name one ("30 inch induction" + range -> "30 inch induction range")."""
    feats = _clean_words(words)
    if not feats:
        return replace._norm(label)
    if replace.kind_of(feats) or re.search(rf"\b{re.escape(replace._norm(label))}s?\b", feats):
        return feats
    return f"{feats} {replace._norm(label)}"


def parse(text: str) -> dict[str, Any] | None:
    """{thing, words, strict} for a replace sentence, else None. `thing` is what to take out (None:
    "it"/"this", the pointer's part), `words` the features to search for; `strict`: only when
    the part is still in (a "find a new X that fits" with its gap open is the step flow's)."""
    t = re.sub(r"\s+", " ", (text or "").strip())
    if not t or len(t) > 200:
        return None
    if m := _REPLACE_RE.match(t):
        return {"thing": replace._thing_of(m), "words": m.group("with"), "strict": False}
    if m := _OUT_AND_NEW_RE.match(t):
        return {"thing": replace._thing_of(m), "words": _features(m.group("rest")), "strict": False}
    if m := _BROKEN_RE.match(t):
        return {"thing": replace._thing_of(m), "words": None, "strict": False}
    if (m := _NEW_ONE_RE.match(t)) and (m.group("new") or m.group("where")):
        thing = replace._thing_of(m)
        if thing is None or replace.kind_of(thing) is None:
            return None
        return {"thing": thing, "words": thing, "strict": True}
    return None


def _features(rest: str | None) -> str | None:
    """ "a new stainless one that fits and put it in" -> "a new stainless one that fits"."""
    if not rest:
        return None
    return re.split(r"\s+and\s+(?:then\s+)?|,", rest, maxsplit=1)[0]


# --- which part --------------------------------------------------------------------------------


def _box_distance(p: list[float], bbox: dict[str, Any]) -> float:
    lo, hi = bbox.get("min"), bbox.get("max")
    if not (isinstance(lo, list) and isinstance(hi, list) and len(lo) == len(hi) == 3):
        return float("inf")
    d2 = sum(max(lo[k] - p[k], 0.0, p[k] - hi[k]) ** 2 for k in range(3))
    return d2**0.5


def at_pointer(comps: list[dict[str, Any]], pointer: Any, margin_m: float = 0.15):
    """The removable part the headset's pointer (context `pointer`, the structure file's frame,
    the parts file's frame) is on or within `margin_m` of; the nearest when several."""
    if not (isinstance(pointer, list | tuple) and len(pointer) == 3):
        return None
    try:
        p = [float(v) for v in pointer]
    except (TypeError, ValueError):
        return None
    near = [
        (_box_distance(p, c.get("bbox") or {}), i, c)
        for i, c in enumerate(comps)
        if c.get("removable", True)
    ]
    near = [n for n in near if n[0] <= margin_m]
    return min(near)[2] if near else None


def resolve(comps: list[dict[str, Any]], thing: str | None, pointer: Any = None):
    """The scene part `thing` names (id, label, class, or a synonym: "stove" -> the range), or
    the pointer's part when it names none ("replace this")."""
    if thing:
        c = replace.find(comps, thing)
        if c is None:
            kind = replace.kind_of(thing)
            c = (
                next((x for x in comps if replace.component_kind(x) == kind), None)
                if kind
                else None
            )
        return c
    return at_pointer(comps, pointer)


# --- the chain -----------------------------------------------------------------------------


@dataclass
class Want:
    component: dict[str, Any]
    query: str
    max_usd: float | None = None


@dataclass
class State:
    want: Want
    kind: str | None
    label: str
    cavity: Cavity | None = None
    scale_note: str | None = None
    pick_note: str = ""  # " It's over your $1,200; nothing under it fits."
    job: Any = None  # the search Job used
    search_note: str = ""
    index: int = -1
    part: Part | None = None
    fit: Fit | None = None
    others: list[asyncio.Task] = field(default_factory=list)
    run: Any = None  # job-cancel: the run (its tasks list)
    mode: str = "auto"  # asset-mode: the session's generator preference (its context asset_mode)


def start(session: Any, site: str, want: Want, context: dict[str, Any]) -> runjob.Run:
    """Register the run (runjob.Busy while one runs) and start the chain in the background."""
    label = replace.label(want.component)
    title = f"Replace the {label}"
    run = runjob.register(session, site, want.query, context, list(STEPS), kind=KIND, title=title)
    state = State(want=want, kind=replace.component_kind(want.component), label=label)
    state.mode = assets.normalize_mode(getattr(session, "asset_mode", "auto"))  # asset-mode
    state.run = run  # job-cancel: the next models' builds join run.tasks
    run.tasks.append(asyncio.create_task(_chain(run, session, state)))
    return run


async def _chain(run: runjob.Run, session: Any, st: State) -> None:
    by = {s["name"]: s for s in run.steps}
    work = {
        "remove": (lambda: _remove(session, st), 5.0),
        "measure": (lambda: _measure(session, st, run.context), 5.0),
        "scale": (lambda: _scale(session, st, run.context), 5.0),
        "search": (lambda: _search(run, by["search"], session, st), 2 * SEARCH_CAP_S + 15),
        "pick": (lambda: _pick(session, st), 5.0),
        "model": (lambda: _model(st), MODEL_CAP_S + UPGRADE_CAP_S + 5),
        "place": (lambda: _place(session, st), 5.0),
    }
    try:
        for name in STEPS:
            make, cap = work[name]
            await runjob._step(run, by[name], make(), cap_s=cap)
            if runjob.cancelled(run):  # job-cancel: a step that swallowed the cancel
                return
            if by[name]["status"] == "stopped" or (
                name in ("search", "pick", "model") and by[name]["status"] != "done"
            ):
                run.status = "stopped"
                break
        if run.status != "stopped" and st.others:
            await asyncio.wait(st.others, timeout=MORE_MODELS_CAP_S)
    except asyncio.CancelledError:
        for task in st.others:  # job-cancel: the next models' builds stop too
            task.cancel()
        raise
    except Exception:
        logger.exception("replace job %s crashed", run.id)
        if not runjob.cancelled(run):
            run.status = "stopped"
    if runjob.cancelled(run):  # job-cancel: cancel() already closed the run
        return
    if run.status != "stopped":
        run.status = "done"
    run.actions.append({"name": "job_done", "args": _done_args(run, st, by)})
    logger.info(
        "replace job %s %s in %.1f s: %s",
        run.id,
        run.status,
        time.monotonic() - run.started,
        ", ".join(f"{s['name']}={s['status']}" for s in run.steps),
    )


def _progress(run: runjob.Run, step: dict[str, Any], spoken: str) -> None:
    """A long step's progress line: job_step {status: "running"} (the rail's caption)."""
    if runjob.cancelled(run):  # job-cancel
        return
    step.update(status="running", spoken=spoken)
    run.actions.append({"name": "job_step", "args": dict(step)})


def _plural(noun: str) -> str:
    return noun if noun.endswith("s") else jobs._pluralize(noun)


def _gap_words(cav: Cavity) -> str:
    """ "65 × 88 × 66 cm" (W × H × D)."""
    w, h, d = cav.size_m()
    return f"{w * 100:.0f} × {h * 100:.0f} × {d * 100:.0f} cm"


def _a(name: str) -> str:
    """ "an LG ...", "an Asko ...", "a GE ...", "a Frigidaire ..." (by the first letter's sound)."""
    word = (name or "x").split()[0]
    vowel_sound = word[0] in "AEIOUaeiou" or (word.isupper() and word[0] in "AEFHILMNORSX")
    return "an" if vowel_sound else "a"


def _count(n: int) -> str:
    """ "three" (so "Found three 30 inch ranges", not "3 30 inch")."""
    return ("no", "one", "two", "three", "four", "five")[n] if 0 <= n <= 5 else str(n)


def _price(part: Part) -> float | None:
    return runjob._part_total(part)


def fit_phrase(f: Fit | None) -> str:
    """ "3 mm to spare" / "an exact fit" / "tight on height" / "13 mm too tall"."""
    if f is None or f.spare_mm is None:
        return "fit unknown"
    word = replace.fits_word(f)
    if word == "fits":
        return "an exact fit" if f.spare_mm < 2 else f"{f.spare_mm:.0f} mm to spare"
    if word == "tight":
        return f"tight on {({'w': 'width', 'h': 'height', 'd': 'depth'}).get(f.axis or '', 'size')}"
    too = {"w": "wide", "h": "tall", "d": "deep"}.get(f.axis or "", "big")
    return f"{abs(f.spare_mm):.0f} mm too {too}"


async def _remove(session: Any, st: State):
    c = st.want.component
    cid = c["id"]
    session.gap_index = None
    session.cavity = None
    if cid in session.removed:
        return f"The {st.label}'s already out.", [], None
    session.removed.append(cid)
    return (
        f"Took out the {st.label}.",
        [{"name": "remove_component", "args": {"component_id": cid}}],
        None,
    )


async def _measure(session: Any, st: State, context: dict[str, Any]):
    c = st.want.component
    ctx_cav = context.get("cavity") if isinstance(context.get("cavity"), dict) else None
    st.cavity = replace.cavity_for(c, session.scale, ctx_cav)
    if st.cavity is None:
        raise runjob.Stop(f"The scan has no gap size for the {st.label}; can't fit a new one.")
    spoken = f"Taped the gap: {_gap_words(st.cavity)} on the scan."
    return spoken, [{"name": "measure_cavity", "args": {"component_id": c["id"]}}], None


# Context `scale_source` values that mean the headset's scale is deliberate even at 1.0.
CALIBRATED_SOURCES = {"user", "site_default", "calibrated", "tape", "gap"}


def calibrated(scale: float | None, context: dict[str, Any] | None = None) -> bool:
    """The headset's scale is set: not 1.0 (the kitchen starts at its site default, x1.63), or
    its context says where a 1.0 came from (`scale_source`)."""
    source = str((context or {}).get("scale_source") or "").lower()
    return abs((scale or 1.0) - 1.0) > 0.005 or source in CALIBRATED_SOURCES


async def _scale(session: Any, st: State, context: dict[str, Any]):
    scale = session.scale or 1.0
    if calibrated(scale, context):
        raise runjob.Skip(f"The scale's already set (×{scale:.2f}); kept it.")
    gap = st.cavity.size_m()
    prior = replace.scale_prior(st.kind, gap)
    if prior is None:
        if st.kind is None:
            raise runjob.Skip(f"No standard size on file for a {st.label}; kept the scan's scale.")
        raise runjob.Skip(f"The gap reads right for a {st.label}; kept the scan's scale.")
    session.scale = scale * prior.factor
    session.cavity = None
    c = st.want.component
    st.cavity = replace.cavity_for(c, session.scale)
    reads = "small" if prior.factor > 1 else "large"
    st.scale_note = (
        f"The scan reads {reads}; scaled it from a standard {prior.said} (×{prior.factor:.2f})."
    )
    action = {
        "name": "scale_gap",
        "args": {"component_id": c["id"], "axis": prior.axis, "real_m": prior.real_m},
    }
    return st.scale_note, [action], None


async def _await_search(run, step, job, what: str, t0: float, cap: float, tick=None):
    """A search job, polled with progress lines (job_step running) until it's done or `cap`;
    `tick(elapsed)` runs on every progress line."""
    while True:
        cur = jobs.get(job.id) or job
        if cur.status in ("done", "failed") or time.monotonic() - t0 >= cap:
            return cur
        await jobs.wait(cur.id, min(PROGRESS_EVERY_S, max(0.01, cap - (time.monotonic() - t0))))
        cur = jobs.get(cur.id) or cur
        if cur.status == "running":
            stage = cur.stage or "searching"
            _progress(run, step, f"Searching for {what} · {stage} · {time.monotonic() - t0:.0f} s")
            if tick is not None:
                tick(time.monotonic() - t0)


def _fitting(job, cav: Cavity) -> bool:
    return any(
        replace.fits_word(replace.fit(p.dims_mm, cav)) in ("fits", "tight") for p in job.candidates
    )


async def _search(run: runjob.Run, step: dict[str, Any], session: Any, st: State):
    """The user's (or the kind's) query fitted to the gap; the plain kind alongside as a fallback
    for a fresh query that comes back empty; and when nothing that came back fits, one more search
    for parts of the gap's size ("30 inch fridge")."""
    cav = st.cavity
    plain = replace._norm(st.label)
    query = st.want.query or plain
    what = _plural(query)
    searches = [jobs.start_search(SearchRequest(query=query, cavity=cav, asset_mode=st.mode))]
    fallback_ok = replace._norm(query) != plain  # "range" for "30 inch induction range"

    def fallback(elapsed: float = FALLBACK_AFTER_S) -> None:
        """The plain kind, started only once the named search is slow (SerpApi quota is tight)."""
        if fallback_ok and len(searches) == 1 and elapsed >= FALLBACK_AFTER_S:
            searches.append(
                jobs.start_search(SearchRequest(query=plain, cavity=cav, asset_mode=st.mode))
            )

    t0 = time.monotonic()
    _progress(run, step, f"Searching for {what} that fit {_gap_words(cav)}.")
    job = await _await_search(run, step, searches[0], what, t0, SEARCH_CAP_S, tick=fallback)
    if not (job.status == "done" and job.candidates):
        fallback()
    if not (job.status == "done" and job.candidates) and len(searches) > 1:
        fallback = await jobs.wait(searches[1].id, max(1.0, SEARCH_CAP_S - (time.monotonic() - t0)))
        if fallback is not None and fallback.status == "done" and fallback.candidates:
            st.search_note = f" No {what} came back in time; these are plain {_plural(plain)}."
            job, what = fallback, _plural(plain)
    if job.status == "done" and job.candidates and not _fitting(job, cav):
        sized = replace.sized_query(st.kind, plain, cav)
        if replace._norm(sized) != replace._norm(query):
            _progress(run, step, f"None of those fit; searching for {_plural(sized)}.")
            retry = jobs.start_search(SearchRequest(query=sized, cavity=cav, asset_mode=st.mode))
            t1 = time.monotonic()
            again = await _await_search(run, step, retry, _plural(sized), t1, SEARCH_CAP_S)
            if again.status == "done" and again.candidates and _fitting(again, cav):
                st.search_note = f" None of the {what} fit; these are {_plural(sized)}."
                job, what = again, _plural(sized)
    if not (job.status == "done" and job.candidates):
        if job.status == "running":
            raise runjob.Stop(f"The search for {what} took too long; say find one that fits.")
        raise runjob.Stop(f"Couldn't find {what} for that gap.")
    st.job = job
    session.job_id = job.id
    session.candidate_ids = [p.id for p in job.candidates]
    session.gap_index = None
    fits = [replace.fits_word(replace.fit(p.dims_mm, cav)) for p in job.candidates]
    n = len(fits)
    said = {"fits": ("fits", "fit"), "tight": ("tight", "tight"), "too_big": ("too big", "too big")}
    counts = ", ".join(
        f"{_count(fits.count(w))} {said[w][fits.count(w) > 1]}"
        for w in ("fits", "tight", "too_big")
        if fits.count(w)
    )
    spoken = f"Found {_count(n)} {what} for the {_gap_words(cav)} gap: {counts}.{st.search_note}"
    return spoken, [{"name": "search_started", "args": {"job_id": job.id}}], None


async def _pick(session: Any, st: State):
    """The best fit (the search's order: the first that fits, else the tightest), preferring ones
    within the purchase limit. Only a part that fits or is tight goes in: when none does, the job
    stops here and says how far off the closest is (and, at an uncalibrated scale, how to set
    it)."""
    parts: list[Part] = st.job.candidates
    cav = st.cavity
    limit = st.want.max_usd
    if limit is None:
        intent = mandate.current_intent(session.id)
        limit = intent.max_total_usd if intent is not None else None
    fits = [replace.fit(p.dims_mm, cav) for p in parts]
    words = [replace.fits_word(f) for f in fits]
    fitting = [i for i, w in enumerate(words) if w in ("fits", "tight")]
    if not fitting:
        closest = min(range(len(parts)), key=lambda i: -(fits[i].spare_mm or 0))
        line = (
            f"None of the {len(parts)} fits the {_gap_words(cav)} gap; the closest, the "
            f"{replace.short_name(parts[closest])}, is {fit_phrase(fits[closest])}."
        )
        hint = replace.scale_hint(cav, words, session.scale)
        raise runjob.Stop(f"{line} {hint}" if hint else line)
    within = [
        i for i in fitting if limit is None or _price(parts[i]) is None or _price(parts[i]) <= limit
    ]
    pool = set(within or fitting)
    note = (
        "" if within or limit is None else f" It's over your ${limit:,.0f}; nothing under it fits."
    )
    index = replace.best([p.dims_mm if i in pool else None for i, p in enumerate(parts)], cav)
    part = parts[index]
    st.index, st.part, st.fit = index, part, fits[index]
    session.selected_part_id = part.id
    price = _price(part)
    cost = f", ${price:,.0f}" if price is not None else ""
    st.pick_note = note
    spoken = f"Picked the {replace.short_name(part)}: {fit_phrase(st.fit)}{cost}.{note}"
    return spoken, [], None


_TIER_WORDS = {
    "cad": "the maker's CAD model",
    "llm": "built to size from the product photo",
    "scad": "Grok's OpenSCAD model",
    "ai_mesh": "an AI mesh from the photo",
    "proxy": "a photo box at true size",
}


async def _ensure(part: Part, mode: str = "auto") -> Part:
    """The part's GLB, built (or awaited) once: resolve_shared; a proxy box then gets one more
    try at a generated model (assets.upgrade_proxy, once per part per run)."""
    part = await (
        assets.resolve_shared(part, mode=mode) if mode != "auto" else assets.resolve_shared(part)
    )
    if part.asset.tier == "proxy" and not get_settings().OFFLINE:
        part = await assets.upgrade_proxy(part)
    return part


async def _model(st: State):
    """The pick's GLB before it goes in (a new candidate's is generated here: its LLM template
    call + build, or awaited when the search's background worker is already on it); the next
    two's start now and finish before job_done, so "next one" is instant."""
    parts: list[Part] = st.job.candidates
    t0 = time.monotonic()
    try:
        shared = (  # asset-mode: the session's generator
            assets.resolve_shared(st.part, mode=st.mode)
            if st.mode != "auto"
            else assets.resolve_shared(st.part)
        )
        part = await asyncio.wait_for(shared, MODEL_CAP_S)
    except TimeoutError as exc:
        raise runjob.Stop(
            "Its 3D model didn't finish in time; say put it in to try again."
        ) from exc
    upgraded = ""
    if part.asset.tier == "proxy" and not get_settings().OFFLINE:
        upgrade = asyncio.ensure_future(assets.upgrade_proxy(part))
        try:  # shielded: a late upgrade still lands on disk for the next time
            better = await asyncio.wait_for(asyncio.shield(upgrade), UPGRADE_CAP_S)
            if better.asset.tier != "proxy":
                part, upgraded = better, " (rebuilt from the photo box)"
        except TimeoutError:
            pass
    st.part = part
    n = len(parts)
    nxt = [parts[(st.index + k) % n] for k in (1, 2) if n > k]
    st.others = [asyncio.create_task(_ensure(p, st.mode)) for p in nxt]
    if st.run is not None:
        st.run.tasks.extend(st.others)  # job-cancel
    took = time.monotonic() - t0
    spoken = f"3D model ready: {_TIER_WORDS.get(part.asset.tier, part.asset.tier)}{upgraded}."
    if took >= 1:
        spoken = spoken[:-1] + f", {took:.0f} s."
    return spoken, [], None


async def _place(session: Any, st: State):
    c, cav, part = st.want.component, st.cavity, st.part
    n = len(st.job.candidates)
    f = st.fit
    args = {
        "part_id": part.id,
        "model_url": assets.model_url(part.id, st.mode),  # texture: the session's generator
        "name": part.name,
        "component_id": c["id"],
        "fits": replace.fits_word(f),
        "clearance_mm": replace.clearance_mm(part.dims_mm, cav),
        "cycle": {"index": st.index, "count": n},
    }
    session.gap_index = st.index
    entry = {"type": "placement", "part_id": part.id, "count": 1, "source": "replace_job"}
    report.append_notebook(session.id, [entry | ({"site": session.site} if session.site else {})])
    spoken = f"Put the {replace.short_name(part)} in. {replace.spoken_fit(f)}"
    return spoken, [{"name": "place_part", "args": args}], None


def _done_args(run: runjob.Run, st: State, by: dict[str, dict[str, Any]]) -> dict[str, Any]:
    placed = by["place"]["status"] == "done" and st.part is not None
    ready = sum(1 for t in st.others if t.done() and not t.cancelled() and t.exception() is None)
    if placed:
        short = replace.short_name(st.part)
        phrase = fit_phrase(st.fit)
        lines = [st.scale_note] if st.scale_note else []
        lines.append(f"Put {_a(short)} {short} in the {st.label} gap, {phrase}.{st.pick_note}")
        if ready:
            lines.append(
                f"Say next one to see the other {'two' if ready == 2 else 'one'}."
                if ready in (1, 2)
                else "Say next one to see the others."
            )
        spoken = " ".join(lines)
        summary = f"{short} in the {st.label} gap · {phrase}"
    else:
        stopped = next((s for s in run.steps if s["status"] == "stopped"), None)
        stopped = stopped or next((s for s in run.steps if s["status"] == "skipped"), None)
        spoken = (stopped or {}).get("spoken") or f"Couldn't replace the {st.label}."
        summary = f"Stopped: {spoken.rstrip('.')}"
    cost = round(sum(s.get("cost_usd") or 0 for s in run.steps), 4)
    return {
        "run_id": run.id,
        "status": run.status,
        "kind": KIND,
        "title": run.title,
        "spoken": spoken,
        "summary": summary,
        "component_id": st.want.component["id"],
        "part_id": st.part.id if placed else None,
        "fits": replace.fits_word(st.fit) if placed and st.fit else None,
        "models_ready": (1 if placed else 0) + ready,
        "total_usd": _price(st.part) if placed else None,
        "after_rebates_usd": None,
        "packet_url": None,
        "cost_usd": cost,
    }
