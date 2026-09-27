"""Recall and defect radar: is this part recalled, or known-bad? (G3 pick 1,
docs/research/grok-ideas/g3-round2.md.)

Two sources run in parallel:
- CPSC's free recalls API, queried by manufacturer, then matched on the model number in the
  record text (a `ProductModel=` query returns nothing: model numbers live in the free text).
  Deterministic, so it's the evidence.
- One xAI Responses call (`web_search` + `x_search`, strict json_schema) that catches recalls
  CPSC words differently and adds owner complaints.

The LLM never sets the verdict: `merge()` applies fixed rules, and a Grok recall or complaint
only counts when its URL is in that call's own citations (the F3 honesty rule, server/intel.py).
Both sources are cached 24 h, and OFFLINE serves whatever is cached.
"""

import asyncio
import json
import logging
import re
from datetime import UTC, datetime, timedelta
from datetime import date as date_cls
from typing import Literal
from urllib.parse import urlparse

import httpx
import pydantic

from server import cache, llm
from server.models import Part

logger = logging.getLogger(__name__)

CPSC_URL = "https://www.saferproducts.gov/RestWebServices/Recall"
MODEL = "grok-4.20-0309-non-reasoning"  # G3 call #1: found the Midea recall in 11.4 s, $0.116
MAX_TURNS = 3
TTL_S = 86400
LOOKBACK_DAYS = 730  # x_search window, and the oldest brand-level recall that raises caution
ALLOWED_DOMAINS = ("cpsc.gov", "saferproducts.gov", "recalls.gov")
_CITE_RE = re.compile(r"\s*\[\[\d+\]\]\([^)]*\)")  # inline citation markup: [[1]](https://...)
_STOP = {"with", "from", "that", "this", "inch", "pack", "recall", "recalls", "recalled"}

INSTRUCTIONS = (
    "You check a home-improvement product for safety recalls and owner-reported defects. Search "
    "the web (cpsc.gov, the manufacturer's site, Health Canada, retailer reviews, forums) AND run "
    "one X search for owner posts. recalls: only official recalls you saw in this request's "
    "search results; url is the recall page; date is YYYY-MM-DD or null; applies_to_model is "
    "'yes' only if the recall lists this exact model number, 'no' if it lists only other models, "
    "else 'unknown'. complaints: at most 5 owner complaints about a defect or safety problem, "
    "each from a different URL in your results; theme is a 2-5 word label, and reuse the "
    "identical label when complaints describe the same problem; quote is a short verbatim "
    "excerpt (max 25 words) or null; source is 'x' for an X post, else 'web'. Never invent URLs. "
    "Empty lists are fine."
)

Verdict = Literal["recalled", "caution", "clear", "unknown"]


class Recall(pydantic.BaseModel):
    source: Literal["cpsc", "web"]
    number: str | None = None
    title: str
    date: str | None = None  # YYYY-MM-DD
    url: str
    applies: Literal["yes", "brand", "unknown"]  # brand = same maker and product type, not model


class Complaint(pydantic.BaseModel):
    theme: str
    source: Literal["web", "x"] = "web"
    url: str
    quote: str | None = None


class SafetyReport(pydantic.BaseModel):
    part_id: str
    verdict: Verdict
    headline: str
    recalls: list[Recall] = []
    complaints: list[Complaint] = []
    spoken: str
    sources: list[Literal["cpsc", "grok"]] = []  # which sources answered
    checked_at: str
    cost_usd: float | None = None  # the Grok call's cost when it was fetched


class _GrokRecall(pydantic.BaseModel):
    title: str
    date: str | None
    url: str
    applies_to_model: Literal["yes", "no", "unknown"]


class _Reply(pydantic.BaseModel):
    """What the model is asked for (strict json_schema)."""

    recalls: list[_GrokRecall]
    complaints: list[Complaint]


# --- CPSC -------------------------------------------------------------------------------


async def cpsc_recalls(manufacturer: str) -> list[dict]:
    async def fetch() -> list[dict]:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.get(
                CPSC_URL, params={"format": "json", "Manufacturer": manufacturer}
            )
        resp.raise_for_status()
        return resp.json()

    return await cache.cached("cpsc", manufacturer, fetch, ttl_s=TTL_S)


def _model_key(text: str) -> str:
    return re.sub(r"[\s-]", "", text.upper())


def match_model(record: dict, model_no: str) -> bool:
    """`model_no` appears as a whole token anywhere in the record (upper-cased, dashes and
    spaces dropped). ponytail: whole-token, not substring, so LU26 doesn't match LU26Z and a
    5-digit model can't match inside a date; a model written with inner spaces is missed."""
    want = _model_key(model_no)
    if len(want) < 4:
        return False
    tokens = re.findall(r"[A-Z0-9][A-Z0-9-]*", json.dumps(record).upper())
    return any(_model_key(t) == want for t in tokens)


def _words(text: str, drop: set[str]) -> set[str]:
    return {w.rstrip("s") for w in re.findall(r"[a-z]{4,}", text.lower())} - _STOP - drop


def _brand_related(part: Part, record: dict) -> bool:
    """A recent recall from the same maker for the same kind of product (a word of the part
    name in the recall title or product name). LG's range recall shouldn't flag an LG AC.
    ponytail: word overlap, not a product taxonomy."""
    brand = part.manufacturer or ""
    if not re.search(rf"\b{re.escape(brand)}\b", json.dumps(record), re.IGNORECASE):
        return False
    cutoff = (datetime.now(UTC) - timedelta(days=LOOKBACK_DAYS)).date().isoformat()
    if (record.get("RecallDate") or "")[:10] < cutoff:
        return False
    names = " ".join(
        [record.get("Title") or ""] + [p.get("Name") or "" for p in record.get("Products") or []]
    )
    brand_words = _words(brand, set())
    return bool(_words(part.name, brand_words) & _words(names, brand_words))


def _cpsc_recall(record: dict, applies: Literal["yes", "brand"]) -> Recall:
    return Recall(
        source="cpsc",
        number=record.get("RecallNumber"),
        title=record.get("Title") or "",
        date=(record.get("RecallDate") or "")[:10] or None,
        url=record.get("URL")
        or f"https://www.cpsc.gov/Recalls?search={record.get('RecallNumber')}",
        applies=applies,
    )


# --- Grok -------------------------------------------------------------------------------


def _norm_url(url: str) -> str:
    return url.strip().rstrip("/").removeprefix("https://").removeprefix("http://").lower()


def strip_citations(text: str) -> str:
    return _CITE_RE.sub("", text)


def request_kwargs(part: Part) -> dict:
    """The `llm.responses` call for one part (split out so tests can assert its shape)."""
    from_date = (datetime.now(UTC) - timedelta(days=LOOKBACK_DAYS)).date().isoformat()
    product = " ".join(x for x in (part.manufacturer, part.model_no) if x)
    return {
        "model": MODEL,
        "instructions": INSTRUCTIONS,
        "input": f"Product: {product} ({part.name}). Is there a recall covering this exact "
        "model, and what defects or safety problems do owners report?",
        "tools": [{"type": "web_search"}, {"type": "x_search", "from_date": from_date}],
        "text": {
            "format": {
                "type": "json_schema",
                "name": "safety",
                "schema": llm._strict_schema(_Reply.model_json_schema()),
                "strict": True,
            }
        },
        "include": ["no_inline_citations"],  # G3 saw [[1]](url) inside JSON strings without it
        "max_turns": MAX_TURNS,
    }


async def _grok_lookup(part: Part) -> dict:
    result = await llm.responses(**request_kwargs(part), timeout_s=60)
    # Strip markup from the raw JSON so no string field (theme, quote, title) carries it.
    reply = _Reply.model_validate_json(strip_citations(result.text))  # bad JSON = source failed
    seen = {_norm_url(u) for u in result.citations}
    recalls = [
        Recall(source="web", title=r.title, date=r.date, url=r.url, applies=r.applies_to_model)
        for r in reply.recalls
        if r.applies_to_model != "no" and _norm_url(r.url) in seen
    ]
    complaints = [c for c in reply.complaints if _norm_url(c.url) in seen]
    dropped = len(reply.recalls) + len(reply.complaints) - len(recalls) - len(complaints)
    if dropped:
        logger.info("safety: dropped %d uncited/irrelevant item(s) for %s", dropped, part.id)
    return {
        "recalls": [r.model_dump() for r in recalls],
        "complaints": [c.model_dump() for c in complaints],
        "cost_usd": result.cost_usd,
        "checked_at": datetime.now(UTC).isoformat(timespec="seconds"),
    }


async def grok_check(part: Part) -> dict:
    return await cache.cached("safety_grok", part.id, lambda: _grok_lookup(part), ttl_s=TTL_S)


# --- verdict ----------------------------------------------------------------------------


def _allowed(url: str, brand: str | None) -> bool:
    """cpsc.gov / saferproducts.gov / recalls.gov, or the manufacturer's own domain."""
    host = (urlparse(url).hostname or "").lower()
    if any(host == d or host.endswith("." + d) for d in ALLOWED_DOMAINS):
        return True
    labels = host.split(".")
    words = re.findall(r"[a-z0-9]+", (brand or "").lower())
    return len(labels) >= 2 and bool(words) and labels[-2] in {"".join(words), words[0]}


def _month(date: str | None) -> str:
    try:
        return date_cls.fromisoformat((date or "")[:10]).strftime("%B %Y")
    except ValueError:
        return ""


def _hazard(title: str) -> str:
    """ "Midea Recalls ... Due to Risk of Mold Exposure" -> "risk of mold exposure"."""
    tail = re.split(r"\bdue to\b", title, maxsplit=1, flags=re.IGNORECASE)[-1].strip(" .;")
    return tail.lower()[:90]


def merge(part: Part, cpsc: list[dict] | None, grok: dict | None) -> SafetyReport:
    """Fixed rules (the LLM never sets the verdict):
    recalled: a CPSC record lists the model, or a cited Grok recall that applies to the model
      comes from an allowed domain;
    caution: a recent same-brand, same-type CPSC recall, any other cited recall claim, or 2+
      cited complaints sharing one theme;
    clear: otherwise; unknown: both sources failed."""
    recalls: list[Recall] = []
    for record in cpsc or []:
        if part.model_no and match_model(record, part.model_no):
            recalls.append(_cpsc_recall(record, "yes"))
        elif _brand_related(part, record):
            recalls.append(_cpsc_recall(record, "brand"))
    known = {_norm_url(r.url) for r in recalls}
    for r in (grok or {}).get("recalls", []):
        if _norm_url(r["url"]) not in known:
            recalls.append(Recall(**r))
    complaints = [Complaint(**c) for c in (grok or {}).get("complaints", [])]

    by_theme: dict[str, list[Complaint]] = {}
    for c in complaints:
        by_theme.setdefault(cache.normalize(c.theme), []).append(c)
    theme = max(by_theme.values(), key=len, default=[])
    confirmed = [
        r
        for r in recalls
        if r.applies == "yes" and (r.source == "cpsc" or _allowed(r.url, part.manufacturer))
    ]

    if cpsc is None and grok is None:
        verdict: Verdict = "unknown"
        headline, spoken = "Safety check unavailable", "Couldn't run the safety check right now."
    elif confirmed or recalls:
        r = (confirmed or recalls)[0]
        hazard, month = _hazard(r.title), _month(r.date)
        when, in_when = month or "recently", f"in {month}" if month else "recently"
        if confirmed:
            verdict = "recalled"
            headline = f"Recalled {when}: {hazard}" + (f" (CPSC #{r.number})" if r.number else "")
            spoken = f"Heads up: this exact model was recalled {in_when} for {hazard}."
        elif r.applies == "brand":
            verdict = "caution"
            headline = f"{part.manufacturer} recall {when}, not this model: {hazard}"
            spoken = (
                f"Caution: {part.manufacturer} recalled a similar product {in_when} for "
                f"{hazard}, but this model isn't listed."
            )
        else:
            verdict = "caution"
            headline = f"Unconfirmed recall report ({when}): {hazard}"
            spoken = f"Caution: a recall report mentions {hazard}, but I couldn't confirm it."
    elif len({_norm_url(c.url) for c in theme}) >= 2:
        verdict = "caution"
        headline = f"Owners report {theme[0].theme} ({len(theme)} sources)"
        spoken = f"Caution: several owners report {theme[0].theme}."
    else:
        verdict = "clear"
        headline = "No recalls or complaint patterns found"
        spoken = "No recalls or complaint patterns on this one."

    return SafetyReport(
        part_id=part.id,
        verdict=verdict,
        headline=headline,
        recalls=recalls,
        complaints=complaints,
        spoken=spoken,
        sources=[s for s, v in (("cpsc", cpsc), ("grok", grok)) if v is not None],
        checked_at=(grok or {}).get("checked_at")
        or datetime.now(UTC).isoformat(timespec="seconds"),
        cost_usd=(grok or {}).get("cost_usd"),
    )


# --- entry points -----------------------------------------------------------------------


async def _source(name: str, coro) -> dict | list | None:
    """A failed source (HTTP error, bad JSON, no key, OFFLINE miss) is None, never a raise:
    the endpoint answers `unknown` instead of a 5xx."""
    try:
        return await coro
    except Exception as exc:  # noqa: BLE001 - every failure degrades the same way
        logger.warning("safety: %s failed: %s", name, exc)
        return None


async def _cpsc_for(part: Part) -> list[dict] | None:
    return await cpsc_recalls(part.manufacturer) if part.manufacturer else None


async def _check(part: Part) -> SafetyReport:
    cpsc, grok = await asyncio.gather(
        _source("cpsc", _cpsc_for(part)), _source("grok", grok_check(part))
    )
    report = merge(part, cpsc, grok)
    logger.info("safety part=%s verdict=%s sources=%s", part.id, report.verdict, report.sources)
    return report


_inflight: dict[str, asyncio.Task] = {}


def start(part: Part) -> asyncio.Task:
    """Kick the check off in the background (agent: on select and on show_sellers). A second
    caller for the same part joins the running task, so a part is never paid for twice."""
    task = _inflight.get(part.id)
    if task is None:
        task = _inflight[part.id] = asyncio.create_task(_check(part))
        task.add_done_callback(lambda _t, pid=part.id: _inflight.pop(pid, None))
    return task


async def check(part: Part) -> SafetyReport:
    return await asyncio.shield(start(part))


def peek(part: Part) -> SafetyReport | None:
    """The report from cache only (no network, TTL ignored); None if nothing was ever fetched.
    For checkout, which must not stall on a live check."""
    cpsc = cache.get("cpsc", part.manufacturer) if part.manufacturer else None
    grok = cache.get("safety_grok", part.id)
    return None if cpsc is None and grok is None else merge(part, cpsc, grok)
