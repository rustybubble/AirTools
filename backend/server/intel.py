""" "Who installs this near me?" -- local installers for a part or trade, from one xAI Responses
call with server-side `web_search` (location-biased) + `x_search` (last ~12 months).

Honesty rule: an installer is only returned with at least one evidence URL that xAI's own
search tools actually touched this call (the response's `citations`). Anything else is dropped,
so a made-up business -- or a real one with a made-up link -- never reaches the headset.
"""

import logging
import re
from datetime import UTC, datetime, timedelta
from typing import Literal

import pydantic

from server import cache, llm

logger = logging.getLogger(__name__)

MODEL = "grok-4.20-0309-non-reasoning"  # fast + cheap; G2 call #4 ran x_search on it in 5.4 s
MAX_TURNS = 6  # caps the agentic search loop (and the bill)
X_LOOKBACK_DAYS = 365

INSTRUCTIONS = (
    "You find local installers/contractors who do a given job. Search the web (business "
    "sites, Yelp/BBB/Angi/Nextdoor listings, reviews) AND run at least one X search for posts "
    "by locals or businesses about this work in the area. Only list businesses that appear in "
    "the search results you got in this request. Every installer needs evidence_urls: exact "
    "URLs from those results that mention that business. Never invent businesses, URLs or "
    "phone numbers; use null when unknown. quote: a short verbatim excerpt (max 25 words) from "
    "one evidence URL, preferring a customer review or X post over the business's own copy, "
    "else null. sentiment: only from third-party reviews/posts about the business, 'unknown' if "
    "you saw none. source: 'x' if the best evidence is an X post, else 'web'. summary: one "
    "short sentence to be spoken aloud, no URLs."
)


class Installer(pydantic.BaseModel):
    name: str
    website: str | None = None
    phone: str | None = None
    area: str | None = None
    evidence_urls: list[str] = []
    sentiment: Literal["positive", "mixed", "negative", "unknown"] = "unknown"
    quote: str | None = None
    source: Literal["web", "x"] = "web"


class _Reply(pydantic.BaseModel):
    """What the model is asked for (strict json_schema)."""

    installers: list[Installer]
    summary: str


class InstallerIntel(pydantic.BaseModel):
    query: str
    location: str
    installers: list[Installer]
    summary: str  # one spoken sentence
    dropped: int = 0  # installers the model listed without checkable evidence
    cost_usd: float | None = None


def _norm_url(url: str) -> str:
    return url.strip().rstrip("/").removeprefix("https://").removeprefix("http://").lower()


def _user_location(location: str) -> dict[str, str]:
    """ "Atlanta, GA" -> {country, city, region}; anything else ("near Georgia Tech") only biases
    the country -- the free text still rides in the prompt."""
    loc = {"type": "approximate", "country": "US"}
    parts = [p.strip() for p in location.split(",") if p.strip()]
    if len(parts) >= 2:
        loc["city"], loc["region"] = parts[0], parts[1]
    return loc


def request_kwargs(query: str, location: str, max_results: int) -> dict:
    """The `llm.responses` call for one lookup (split out so tests can assert its shape)."""
    from_date = (datetime.now(UTC) - timedelta(days=X_LOOKBACK_DAYS)).date().isoformat()
    return {
        "model": MODEL,
        "instructions": INSTRUCTIONS,
        "input": f"Who installs or does '{query}' near {location}? "
        f"Give at most {max_results} installers, best evidenced first.",
        "tools": [
            {"type": "web_search", "user_location": _user_location(location)},
            {"type": "x_search", "from_date": from_date},
        ],
        "text": {
            "format": {
                "type": "json_schema",
                "name": "installers",
                "schema": llm._strict_schema(_Reply.model_json_schema()),
                "strict": True,
            }
        },
        # inline [[1]](url) citations would land inside our JSON strings
        "include": ["no_inline_citations"],
        "max_turns": MAX_TURNS,
    }


def filter_installers(
    reply: _Reply, citations: list[str], max_results: int
) -> tuple[list[Installer], int]:
    """Keep only evidence URLs the search tools really returned; drop installers left with none;
    dedupe by name. Returns (kept, dropped count)."""
    seen_urls = {_norm_url(u) for u in citations}
    kept: dict[str, Installer] = {}
    dropped = 0
    for inst in reply.installers:
        urls = [u for u in inst.evidence_urls if _norm_url(u) in seen_urls]
        key = cache.normalize(inst.name)
        if not urls or not key:
            dropped += 1
            continue
        if key in kept:  # same business twice (e.g. once from web, once from X): merge evidence
            kept[key].evidence_urls = list(dict.fromkeys(kept[key].evidence_urls + urls))
            continue
        kept[key] = inst.model_copy(update={"evidence_urls": urls})
    return list(kept.values())[:max_results], dropped


def _first_sentence(text: str) -> str:
    """The model sometimes writes three sentences; the headset speaks one.
    ponytail: splits after a lowercase/digit + .!? then a capital, so "R.S. Andrews" survives
    but "Dr. Roof" would split -- swap for a real sentence splitter if that bites."""
    return re.split(r"(?<=[a-z0-9][.!?])\s+(?=[A-Z])", text.strip(), maxsplit=1)[0]


def _fallback_summary(installers: list[Installer], query: str, location: str) -> str:
    if not installers:
        return f"No installers with checkable evidence turned up for {query} near {location}."
    names = [i.name for i in installers[:2]]
    more = f" and {len(installers) - 2} more" if len(installers) > 2 else ""
    return f"Found {len(installers)} with evidence near {location}: {' and '.join(names)}{more}."


async def _lookup(query: str, location: str, max_results: int) -> dict:
    kwargs = request_kwargs(query, location, max_results)
    result = await llm.responses(**kwargs, timeout_s=60)
    try:
        reply = _Reply.model_validate_json(result.text)
    except pydantic.ValidationError:
        logger.warning("intel: unparseable reply for %r near %r", query, location)
        reply = _Reply(installers=[], summary="")
    installers, dropped = filter_installers(reply, result.citations, max_results)
    # The model's own line may name a business we just dropped -- only trust it when none were.
    summary = (
        _first_sentence(reply.summary)
        if installers and not dropped and reply.summary
        else _fallback_summary(installers, query, location)
    )
    if dropped:
        logger.info("intel: dropped %d unevidenced installer(s) for %r", dropped, query)
    return InstallerIntel(
        query=query,
        location=location,
        installers=installers,
        summary=summary,
        dropped=dropped,
        cost_usd=result.cost_usd,
    ).model_dump()


async def find_installers(
    part_or_trade: str, location: str, *, max_results: int = 5
) -> InstallerIntel:
    """Cached per (query, location): repeats and OFFLINE are free; an OFFLINE miss raises
    `cache.OfflineMiss`."""
    key = {"part_or_trade": part_or_trade, "location": location}
    data = await cache.cached("intel", key, lambda: _lookup(part_or_trade, location, max_results))
    return InstallerIntel.model_validate(data)
