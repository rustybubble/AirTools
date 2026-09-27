"""Rules and money check: permits, code editions, rebates (G5 pick 1,
docs/research/grok-ideas/g5-round4.md).

Facts come from deterministic sources; Grok only researches and cites:
- jurisdiction: the free Census geocoder. A "Decatur, GA 30032" mailing address is
  unincorporated DeKalb County, so the county issues the permit, not the City of Decatur;
- code editions: `CODE_TABLE`, checked in from DCA's notice. The model's `code_basis` is asked
  for and dropped (G5 call #3 dated the 2024 codes a year early);
- program status: each program's own status page (`PROGRAMS`), plus the fixed IRS rule that 25C
  ended after 2025 (G5 call #4 called the closed HEAR program active);
- efficiency: ENERGY STAR open data, including the certified indoor/outdoor pair;
- eligibility: a program's requirement (`Requires`) read from its own page when the page states
  it, else from the checked-in `Requires` on `PROGRAMS`, checked against that ENERGY STAR result.
  A system that fails it makes the program `not_eligible`; a failed lookup makes it `unverified`.
  Only `counted` rows add up to `rebates_usd`, the one "after rebates" figure.

Grok makes one Responses call for the permit and one for incentives, `web_search` limited to
government and program domains. Every item's URL must be in that call's own citations (F3's
rule), its quote is checked against the page we fetch ourselves, and a fee or rebate amount only
survives if its number is in a verified quote. Pages that 403 a script (atlantaga.gov) keep their
item, marked "unverified (site blocks automated checks)". Every source is cached; OFFLINE serves
the cache.
"""

import asyncio
import html
import logging
import re
import unicodedata
from dataclasses import dataclass
from datetime import UTC, date, datetime
from typing import Annotated, Literal
from urllib.parse import urlparse

import httpx
import pydantic

from server import cache, jobs, llm
from server.models import Job, Part

logger = logging.getLogger(__name__)

MODEL = "grok-4.20-0309-non-reasoning"  # G5 calls #3/#4: $0.115 / $0.070
PERMIT_TURNS = 4
MONEY_TURNS = 3
DAY_S = 86400
CENSUS_URL = "https://geocoding.geo.census.gov/geocoder/geographies/onelineaddress"
ENERGY_STAR_URL = "https://data.energystar.gov/resource/{}.json"
DCA_NOTICE = "https://dca.georgia.gov/announcement/2025-12-09/new-codes-jan-2027"
BLOCKED = "unverified (site blocks automated checks)"
UNLOADED = "unverified (page didn't load)"
NOT_HTML = "unverified (not a web page we can read, e.g. a PDF)"
DISCLAIMER = "Not legal advice; confirm with the permitting office."
LABEL = f"Research with sources, not a permit determination or tax advice. {DISCLAIMER}"
_CITE_RE = re.compile(r"\s*\[\[\d+\]\]\([^)]*\)")  # inline citation markup: [[1]](https://...)
_CODE_WORD_RE = re.compile(r"\b(?:IRC|IMC|IPC|IFGC|IBC|NEC|codes?|editions?)\b", re.IGNORECASE)


@dataclass(frozen=True)
class JobSpec:
    label: str
    scope: str  # the job sentence sent to Grok (G5 call #3's wording)
    match: str  # regex on a part name or a spoken command
    codes: tuple[str, ...]  # CODE_TABLE rows that govern it
    energy_star: str | None = None  # data.energystar.gov dataset id
    # Set when the job needs a licensed trade whatever the permit office says; the install coach
    # (server/coach.py) refuses it with this reason, spoken after "I won't coach this one: ".
    licensed: str | None = None


JOBS = {
    "minisplit_install": JobSpec(
        "mini-split install",
        "Install a ductless mini-split heat pump (about 12,000 BTU): a new indoor wall unit, a new "
        "outdoor unit on a ground pad, a new dedicated 240 V 20 A circuit, and a refrigerant line "
        "set through an exterior wall. Single-family house.",
        r"mini[- ]?split|ductless",
        ("IRC", "IMC", "NEC"),
        "akti-mt5s",  # ENERGY STAR Certified Mini-Split Heat Pumps
        "its refrigerant lines need an EPA-certified HVAC tech, and its new 240 volt circuit "
        "needs an electrician",
    ),
    "water_heater_swap": JobSpec(
        "water heater replacement",
        "Replace an existing 40-50 gallon tank water heater with a new one in the same location, "
        "same fuel, reusing the existing gas line and vent or electric circuit. Single-family "
        "house.",
        r"water heater",
        ("IRC", "IPC", "IFGC"),
        "pbpq-swnu",  # ENERGY STAR Certified Water Heaters
    ),
    "window_ac": JobSpec(
        "window AC",
        "Install a plug-in window air conditioner on an existing 120 V outlet. Single-family "
        "house.",
        r"window (?:air|a/?c|unit)|room air",
        ("NEC",),
        "5xn2-dv4h",  # ENERGY STAR Certified Room Air Conditioners
    ),
}


def job_for(text: str) -> str | None:
    """The JOBS key whose pattern matches a part name or a command, else None."""
    return next((k for k, s in JOBS.items() if re.search(s.match, text or "", re.IGNORECASE)), None)


@dataclass(frozen=True)
class Office:
    name: str
    domains: tuple[str, ...]  # web_search allow-list, max 5
    phone: str | None = None  # only one we read on the office's own page
    phone_source: str | None = None


OFFICES = {
    # atlantaga.gov 403s scripts, so its phone number stays unread (None) rather than guessed.
    ("GA", "Atlanta city"): Office(
        "City of Atlanta Office of Buildings",
        ("atlantaga.gov", "library.municode.com", "sos.ga.gov", "dca.georgia.gov"),
    ),
    ("GA", "DeKalb County"): Office(
        "DeKalb County Planning and Sustainability Department",
        ("dekalbcountyga.gov", "library.municode.com", "sos.ga.gov", "dca.georgia.gov"),
        "404-371-2155",
        "https://www.dekalbcountyga.gov/planning-and-sustainability/planning-sustainability",
    ),
}

# From DCA's 2025-12-09 notice (fetched 2026-09-26): "voted to adopt the following codes with an
# effective date of January 1, 2027". Before that date the prior editions with Georgia amendments
# apply; DCA's own list doesn't name them, so neither do we. Codes office: (404) 679-3118.
CODE_TABLE = {
    "GA": [
        ("IRC", "International Residential Code", "2024 with Georgia Amendments"),
        ("IMC", "International Mechanical Code", "2024 with Georgia Amendments"),
        ("IPC", "International Plumbing Code", "2024 with Georgia Amendments"),
        ("IFGC", "International Fuel Gas Code", "2024 with Georgia Amendments"),
        ("NEC", "National Electrical Code", "2023 with 2026 Georgia Amendments"),
    ]
}
CODE_EFFECTIVE = {"GA": "2027-01-01"}
PRIOR = "prior edition with Georgia amendments"


def codes(state: str, keys: tuple[str, ...], as_of: date | None = None) -> list[dict]:
    """The code rows for `keys` in `state`, as of `as_of` (default today). Never from the model."""
    effective = CODE_EFFECTIVE.get(state)
    in_effect = (
        effective is not None and (as_of or datetime.now(UTC).date()).isoformat() >= effective
    )
    return [
        {
            "code": code,
            "name": name,
            "applies": edition if in_effect else PRIOR,
            "next_edition": None if in_effect else edition,
            "effective": effective,
            "source_url": DCA_NOTICE,
        }
        for code, name, edition in CODE_TABLE.get(state, [])
        if code in keys
    ]


@dataclass(frozen=True)
class Requires:
    """What the equipment must meet for a program to pay. `source_url` backs it."""

    source_url: str
    energy_star: bool = False  # ENERGY STAR certified; for a split system, the whole pair
    seer2_min: float | None = None
    hspf2_min: float | None = None
    basis: str | None = None  # how we read it, when the source isn't word for word


@dataclass(frozen=True)
class Program:
    name: str
    provider: str
    short: str  # spoken, e.g. "the state HEAR rebate"
    url: str  # the program's own status page
    jobs: tuple[str, ...]
    closed: str | None = None  # regex on that page: not taking applications
    open: str | None = None  # regex on that page: running
    fixed: str | None = None  # a status no page or model can change
    note: str | None = None
    requires: Requires | None = None  # used when the status page doesn't state a requirement


_HEIP_REQS = (
    "https://www.georgiapower.com/content/dam/georgia-power/pdfs/programs/heip/"
    "HEIP_Preconditions_Requirements_2026.pdf"
)
_IRS_25C = "https://www.irs.gov/credits-deductions/energy-efficient-home-improvement-credit"
PROGRAMS = {
    "*": {
        "fed_25c": Program(
            "Federal 25C energy efficient home improvement credit",
            "IRS",
            "the federal tax credit",
            _IRS_25C,
            ("minisplit_install", "water_heater_swap"),  # 25C never covered a window AC
            fixed="ended",
            note="only for property placed in service through December 31, 2025",
        ),
    },
    "GA": {
        "georgia_power_heip": Program(
            "Georgia Power Home Energy Improvement Program",
            "Georgia Power",
            "Georgia Power",
            "https://www.georgiapower.com/residential/solutions/home-solutions/heip.html",
            ("minisplit_install", "water_heater_swap"),
            closed=r"no longer accepting|program (?:is|has) (?:closed|ended|paused)",
            open=r"submit your application|apply for rebate",
            note="Georgia Power customers only",
            # heip.html names no tier. The 2026 preconditions PDF (read 2026-09-26) says "ENERGY
            # STAR® certified" for its equipment rebates, and for heat pumps "Installed
            # indoor/outdoor unit combination must be listed on the AHRI directory". Its ductless
            # row is an installer (midstream) rebate the PDF doesn't list, so this is our reading.
            requires=Requires(
                _HEIP_REQS,
                energy_star=True,
                basis="Georgia Power's HEIP requirements ask for ENERGY STAR certified equipment "
                "and a listed indoor/outdoor combination",
            ),
        ),
        "ga_hear": Program(
            "Georgia Home Energy Rebates (HEAR)",
            "Georgia Environmental Finance Authority",
            "the state HEAR rebate",
            "https://energyrebates.georgia.gov/",
            ("minisplit_install", "water_heater_swap"),
            closed=r"not accepting new applications",
            open=r"now accepting (?:new )?applications",
            note="income limits apply",
            # the same page, read 2026-09-26: "Qualifying households can get rebates on certain
            # ENERGY STAR® appliances, such as heat pumps"; kept for when the page won't load
            requires=Requires("https://energyrebates.georgia.gov/", energy_star=True),
        ),
    },
}
MONEY_DOMAINS = {
    "GA": (
        "georgiapower.com",
        "energyrebates.georgia.gov",
        "gefa.georgia.gov",
        "irs.gov",
        "energystar.gov",
    ),
    "*": ("irs.gov", "energystar.gov", "energy.gov"),
}


def _programs(state: str, job: str) -> dict[str, Program]:
    both = {**PROGRAMS["*"], **PROGRAMS.get(state, {})}
    return {k: p for k, p in both.items() if job in p.jobs}


# --- jurisdiction -------------------------------------------------------------------------


class NoMatch(ValueError):
    """Census couldn't match the address and there's no location text to fall back on."""


class Juris(pydantic.BaseModel):
    state: str
    county: str | None = None
    place: str | None = None  # incorporated place, e.g. "Atlanta city"; None = unincorporated
    authority: str  # who issues permits: the place, else the county
    source: Literal["census", "location_text"]
    confidence: Literal["address", "city_only"]
    matched_address: str | None = None


async def _census(address: str) -> dict:
    params = {
        "address": address,
        "benchmark": "Public_AR_Current",
        "vintage": "Current_Current",
        "layers": "all",
        "format": "json",
    }
    async with httpx.AsyncClient(timeout=15) as client:
        resp = await client.get(CENSUS_URL, params=params)
    resp.raise_for_status()
    matches = resp.json()["result"]["addressMatches"]
    if not matches:
        raise NoMatch(f"census has no match for {address!r}")  # not cached: a typo can be fixed
    geo = matches[0]["geographies"]
    place = (geo.get("Incorporated Places") or [{}])[0].get("NAME")
    return {
        "matched_address": matches[0]["matchedAddress"],
        "state": geo["States"][0]["STUSAB"],
        "county": geo["Counties"][0]["NAME"],
        "place": place,
    }


def _from_text(text: str) -> Juris:
    """ "Atlanta, GA" or "..., Decatur, GA 30032" -> that city, marked city_only: a mailing city
    isn't always the permitting jurisdiction."""
    parts = [p.strip() for p in text.split(",") if p.strip()]
    m = re.match(r"([A-Za-z]{2})\b", parts[-1]) if len(parts) >= 2 else None
    if not m:
        raise NoMatch(f"can't read a 'City, ST' from {text!r}")
    city = f"{parts[-2]} city"
    return Juris(
        state=m.group(1).upper(),
        place=city,
        authority=city,
        source="location_text",
        confidence="city_only",
    )


async def jurisdiction(address: str | None, location: str | None) -> Juris:
    """Census for a street address (cached forever), else "City, ST" text. Census down falls back
    to the text; an unmatched address with no location raises NoMatch; OFFLINE with no cached
    address raises cache.OfflineMiss."""
    if address:
        try:
            geo = await cache.cached(
                "rules_census", cache.normalize(address), lambda: _census(address)
            )
            return Juris(
                **geo,
                authority=geo["place"] or geo["county"],
                source="census",
                confidence="address",
            )
        except NoMatch:
            if not location:
                raise
        except (httpx.HTTPError, KeyError, IndexError, ValueError) as exc:
            logger.warning("rules: census failed, using the address text: %s", exc)
    return _from_text(location or address or "")


# --- page checks ----------------------------------------------------------------------------


def _norm_url(url: str) -> str:
    return url.strip().rstrip("/").removeprefix("https://").removeprefix("http://").lower()


def _alnum(text: str) -> str:
    """Letters and digits only, lowercased, NFKC (F9's quote normaliser)."""
    return re.sub(r"[^a-z0-9]", "", unicodedata.normalize("NFKC", text).lower())


def _visible(page: str) -> str:
    page = re.sub(r"(?is)<(script|style)\b.*?</\1>", " ", page)
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", page))).strip()


def _public_https(url: str) -> bool:
    """Only fetch public https hosts: the URL came from a model, even if it was cited."""
    u = urlparse(url)
    host = u.hostname or ""
    return u.scheme == "https" and "." in host and not re.fullmatch(r"[\d.]+|.*:.*", host)


async def page_text(url: str) -> dict:
    """{"status": http status (0 = didn't load), "text": visible text}; cached a day. A plain
    httpx GET, no browser headers: a site that walls scripts gets marked, not worked around."""

    async def fetch() -> dict:
        async with httpx.AsyncClient(timeout=15, follow_redirects=True) as client:
            resp = await client.get(url)
        html_page = "html" in resp.headers.get("content-type", "")
        text = _visible(resp.text) if resp.is_success and html_page else ""
        return {"status": resp.status_code, "text": text}

    if not _public_https(url):
        return {"status": 0, "text": ""}
    try:
        return await cache.cached("rules_page", url, fetch, ttl_s=DAY_S)
    except (httpx.HTTPError, cache.OfflineMiss) as exc:
        logger.info("rules: page %s didn't load: %s", url, exc)
        return {"status": 0, "text": ""}


def quote_check(quote: str | None, page: dict) -> tuple[str, str | None]:
    """(quote_status, note). verified: every "..."-separated piece of the quote is on the page;
    unreadable: the page 403'd or didn't load; not_found: otherwise."""
    if page["status"] in (401, 403, 429):
        return "unreadable", BLOCKED
    if not 200 <= page["status"] < 300:
        return "unreadable", UNLOADED
    if not page["text"]:  # a PDF form or other non-HTML file
        return "unreadable", NOT_HTML
    pieces = [_alnum(p) for p in re.split(r"\.\.\.|…", quote or "")]
    pieces = [p for p in pieces if len(p) >= 8]
    body = _alnum(page["text"])
    if pieces and max(map(len, pieces)) >= 12 and all(p in body for p in pieces):
        return "verified", None
    return "not_found", None


def _numbers(text: str | None) -> list[float]:
    return [float(n.replace(",", "")) for n in re.findall(r"\d[\d,]*(?:\.\d+)?", text or "")]


def backed_amount(amount: str | None, quote: str | None, status: str) -> float | None:
    """The largest number in `amount`, only if the quote is verified and holds every number of
    `amount` (so "$8,000" survives only a verified quote saying 8,000)."""
    nums = _numbers(amount)
    if status != "verified" or not nums or not set(nums) <= set(_numbers(quote)):
        return None
    return max(nums)


# --- Grok research ----------------------------------------------------------------------------


# the model sometimes writes the string "null" for a null field
Text = Annotated[
    str | None,
    pydantic.BeforeValidator(
        lambda v: None if str(v).strip().lower() in ("null", "none", "") else v
    ),
]


class _Permit(pydantic.BaseModel):
    name: str
    office: str
    fee_note: Text
    url: str
    quote: Text


class _WhoCanPull(pydantic.BaseModel):
    homeowner_allowed: Literal["yes", "no", "unclear"]
    licence: Text


class _PermitReply(pydantic.BaseModel):
    permit_required: Literal["yes", "no", "depends", "unknown"]
    permits: list[_Permit]
    who_can_pull: _WhoCanPull
    inspections: list[str]
    code_basis: list[str]  # asked for, never shown: editions come from CODE_TABLE
    caveats: list[str]
    summary: str


class _Incentive(pydantic.BaseModel):
    name: str
    provider: str
    program: str  # a PROGRAMS key, or "other"
    amount: Text
    how: Text
    status: Literal["active", "ended", "closed", "unknown"]  # never shown: probes decide
    url: str
    quote: Text


class _MoneyReply(pydantic.BaseModel):
    incentives: list[_Incentive]


PERMIT_INSTRUCTIONS = (
    "You research building permits for one home-improvement job in one jurisdiction, using only "
    "pages your web search returns in this request. permits: each permit the job needs, with "
    "the issuing office; fee_note is the fee only if a page states it, else null; url is the "
    "exact page that says the permit is needed; quote is a verbatim excerpt (max 30 words) from "
    "that page that supports it, else null. who_can_pull: homeowner_allowed is 'unclear' unless "
    "a page says so; licence is the licence type needed or null. inspections: short labels. "
    "code_basis: the code editions that apply. caveats: short. summary: one sentence. Never "
    "invent URLs, fees or phone numbers."
)
MONEY_INSTRUCTIONS = (
    "You find rebates and tax credits for one home-improvement job, using only pages your web "
    "search returns in this request. For each incentive: program is one of {keys} when it is "
    "that program, else 'other'; amount is the amount as the page states it, or null; how is "
    "how it's paid (instant, mail-in, tax credit) or null; status is what the page says; url is "
    "the program page you read; quote is a verbatim excerpt (max 30 words) from that page with "
    "the amount, else null. Check each program's own site for whether it is taking "
    "applications. Never invent programs, URLs or amounts."
)


def _response_format(name: str, model_cls: type[pydantic.BaseModel]) -> dict:
    schema = llm._strict_schema(model_cls.model_json_schema())
    return {"format": {"type": "json_schema", "name": name, "schema": schema, "strict": True}}


def _web_search(domains: tuple[str, ...] | None) -> dict:
    tool: dict = {"type": "web_search"}
    if domains:
        tool["filters"] = {"allowed_domains": list(domains)[:5]}
    return tool


def permit_request(juris: Juris, job: str) -> dict:
    """The `llm.responses` call for one permit lookup: the job and the jurisdiction only, so it
    caches per (jurisdiction, job) and no street address leaves the server."""
    office = OFFICES.get((juris.state, juris.authority))
    where = ", ".join(dict.fromkeys(x for x in (juris.authority, juris.county, juris.state) if x))
    return {
        "model": MODEL,
        "instructions": PERMIT_INSTRUCTIONS,
        "input": f"Job: {JOBS[job].scope} Permitting authority: {where}"
        f"{' (unincorporated county)' if juris.place is None else ''}. Which permits does this "
        "job need there, from which office, who may pull them, and which inspections follow?",
        "tools": [_web_search(office.domains if office else None)],
        "text": _response_format("permit", _PermitReply),
        "include": ["no_inline_citations"],
        "max_turns": PERMIT_TURNS,
    }


def money_request(state: str, job: str) -> dict:
    """The incentive lookup, per (state, job); the product's own facts come from ENERGY STAR."""
    programs = _programs(state, job)
    known = "; ".join(f"'{k}' ({p.name})" for k, p in programs.items())
    return {
        "model": MODEL,
        "instructions": MONEY_INSTRUCTIONS.format(keys=known),
        "input": f"Job: {JOBS[job].scope} State: {state}. Which rebates or tax credits "
        "apply to this job for an owner-occupied home, and are they taking applications now?",
        "tools": [_web_search(MONEY_DOMAINS.get(state, MONEY_DOMAINS["*"]))],
        "text": _response_format("incentives", _MoneyReply),
        "include": ["no_inline_citations"],
        "max_turns": MONEY_TURNS,
    }


async def _research(namespace: str, key: dict, kwargs: dict, reply_cls, ttl_s: float) -> dict:
    """One Grok call, cached as {reply, citations, cost_usd}; a bad reply raises (not cached)."""

    async def fetch() -> dict:
        result = await llm.responses(**kwargs, timeout_s=90)
        reply = reply_cls.model_validate_json(_CITE_RE.sub("", result.text))
        logger.info("rules: %s cost_usd=%s tools=%s", namespace, result.cost_usd, result.tool_usage)
        return {
            "reply": reply.model_dump(),
            "citations": result.citations,
            "cost_usd": result.cost_usd,
        }

    return await cache.cached(namespace, key, fetch, ttl_s=ttl_s)


# --- sections ---------------------------------------------------------------------------------


def _permit_key(juris: Juris, job: str) -> dict:
    return {"state": juris.state, "county": juris.county, "authority": juris.authority, "job": job}


def _money_key(juris: Juris, job: str) -> dict:
    return {"state": juris.state, "job": job}


def is_cached(juris: Juris, job: str) -> bool:
    """Both Grok researches cached: a check now spends nothing (its `cost_usd` is still what
    they cost when fetched)."""
    return (
        cache.get("rules_permit", _permit_key(juris, job)) is not None
        and cache.get("rules_money", _money_key(juris, job)) is not None
    )


async def permit(juris: Juris, job: str) -> dict:
    office = OFFICES.get((juris.state, juris.authority))
    key = _permit_key(juris, job)
    data = await _research("rules_permit", key, permit_request(juris, job), _PermitReply, 7 * DAY_S)
    reply = _PermitReply.model_validate(data["reply"])
    seen = {_norm_url(u) for u in data["citations"]}
    cited = [p for p in reply.permits if _norm_url(p.url) in seen]
    pages = await asyncio.gather(*(page_text(p.url) for p in cited))
    items = []
    for p, page in zip(cited, pages, strict=True):
        status, note = quote_check(p.quote, page)
        fee_ok = backed_amount(p.fee_note, p.quote, status) is not None
        items.append(
            {
                "name": p.name,
                "office": p.office,
                "url": p.url,
                "quote": p.quote,
                "quote_status": status,
                "quote_note": note,
                "fee": p.fee_note if fee_ok else None,
            }
        )
    required = reply.permit_required
    if required == "yes" and not items:
        required = "unknown"  # a "yes" with no cited page behind it
    who = reply.who_can_pull
    readable = any(i["quote_status"] == "verified" for i in items)
    return {
        "required": required,
        "office": {
            "name": office.name if office else (items[0]["office"] if items else juris.authority),
            "phone": office.phone if office else None,
            "phone_source": office.phone_source if office else None,
        },
        "items": items,
        # Nothing on the page backs who may pull it unless we could read one of its quotes; the
        # model's yes/no stays visible as model_said, but the answer is "unclear".
        "who_can_pull": {
            "homeowner_allowed": who.homeowner_allowed if readable else "unclear",
            "model_said": who.homeowner_allowed,
            "licence": who.licence,
        },
        "inspections": reply.inspections,
        # code facts come only from CODE_TABLE, so caveats about codes go too
        "caveats": [c for c in reply.caveats if not _CODE_WORD_RE.search(c)],
        "dropped": len(reply.permits) - len(cited),
        "sources_label": None if office else "unverified jurisdiction sources",
        "cost_usd": data["cost_usd"],
    }


_ES_REQ_RE = re.compile(
    r"ENERGY STAR\W{0,3}(?:certified|qualified|appliances|equipment|products)", re.IGNORECASE
)


def _min(metric: str, text: str) -> float | None:
    """A stated minimum, e.g. "minimum 16 SEER2" or "HSPF2 of at least 8.1"."""
    least, n = r"(?:minimum(?: of)?|at least|≥|>=)\s*", r"(\d{1,2}(?:\.\d+)?)"
    pattern = rf"{least}{n}\s*{metric}\b|\b{metric}\s*(?:of\s*)?{least}{n}"
    m = re.search(pattern, text, re.IGNORECASE)
    return float(m.group(1) or m.group(2)) if m else None


def page_requires(url: str, text: str) -> Requires | None:
    """The equipment requirement the program's own page states, else None."""
    seer2, hspf2 = _min("SEER2", text), _min("HSPF2", text)
    energy_star = bool(_ES_REQ_RE.search(text))
    if not (energy_star or seer2 or hspf2):
        return None
    return Requires(url, energy_star=energy_star, seer2_min=seer2, hspf2_min=hspf2)


async def program_status(prog: Program) -> tuple[str, str | None, Requires | None]:
    """(status, note, requires) from the program's own page, or its fixed rule. The page's
    stated requirement wins over the checked-in one."""
    if prog.fixed:
        return prog.fixed, prog.note, prog.requires
    page = await page_text(prog.url)
    requires = page_requires(prog.url, page["text"]) or prog.requires
    if page["status"] in (401, 403, 429):
        return "unknown", BLOCKED, requires
    if prog.closed and re.search(prog.closed, page["text"], re.IGNORECASE):
        return "closed", prog.note, requires
    if prog.open and re.search(prog.open, page["text"], re.IGNORECASE):
        return "active", prog.note, requires
    return "unknown", prog.note if page["text"] else UNLOADED, requires


NOT_LISTED = "needs an ENERGY STAR certified system; this unit isn't listed"
NOT_CHECKED = "needs an ENERGY STAR certified system; we couldn't check this unit's listing"


def eligibility(req: Requires | None, es: dict | None) -> tuple[str | None, str | None]:
    """(eligibility, plain reason) of the BOM for a requirement: eligible, not_eligible, or
    unverified when the ENERGY STAR lookup failed or gave no rating to compare. None: no
    requirement on file."""
    if req is None:
        return None, None
    if es is None:
        return "unverified", NOT_CHECKED
    if req.energy_star and not es["certified"]:
        return "not_eligible", NOT_LISTED
    if req.energy_star and es.get("missing"):
        return (
            "not_eligible",
            f"needs the whole ENERGY STAR certified pair; add the {es['missing']}",
        )
    for metric, least in (("seer2", req.seer2_min), ("hspf2", req.hspf2_min)):
        if least is None:
            continue
        if es.get(metric) is None:
            return "unverified", f"needs at least {least:g} {metric.upper()}; no rating on file"
        if es[metric] < least:
            return (
                "not_eligible",
                f"needs at least {least:g} {metric.upper()}; this system is {es[metric]:g}",
            )
    return "eligible", None


def _eligible_row(status: str, req: Requires | None, es: dict | None) -> dict:
    """The eligibility fields of an incentive row; a failed requirement overrides a live or
    unknown status (a closed or ended program stays closed or ended)."""
    verdict, reason = eligibility(req, es)
    if verdict == "not_eligible" and status in ("active", "unknown"):
        status = "not_eligible"
    return {
        "status": status,
        "eligibility": verdict,
        "eligibility_reason": reason,
        "requires": None
        if req is None
        else {
            "energy_star": req.energy_star,
            "seer2_min": req.seer2_min,
            "hspf2_min": req.hspf2_min,
            "source_url": req.source_url,
            "basis": req.basis,
        },
    }


def counted(row: dict) -> bool:
    """Whether a row's amount comes off the price: live, with an amount, and not unverified."""
    return (
        row["status"] == "active"
        and bool(row.get("amount_usd"))
        and row.get("eligibility") in ("eligible", None)
    )


def _model_key(text: str | None) -> str:
    return re.sub(r"[\s-]", "", (text or "").upper())


async def energy_star(model_no: str, dataset: str, bom_models: list[str]) -> dict:
    """ENERGY STAR certification for `model_no`, and for a split system whether the BOM holds
    both halves of the certified pair (rebates need the pair)."""

    async def fetch() -> list[dict]:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.get(ENERGY_STAR_URL.format(dataset), params={"$q": model_no})
        resp.raise_for_status()
        return resp.json()

    rows = await cache.cached(
        "rules_energystar", {"dataset": dataset, "model": model_no}, fetch, ttl_s=DAY_S
    )
    want = _model_key(model_no)
    row = next(
        (
            r
            for r in rows
            if want
            in (_model_key(r.get("model_number")), _model_key(r.get("indoor_unit_model_number")))
        ),
        None,
    )
    out: dict = {
        "model": model_no,
        "certified": row is not None,
        "source_url": f"{ENERGY_STAR_URL.format(dataset)}?$q={model_no}",
    }
    if row is None:
        return out
    for name, field in (
        ("seer2", "seer2_btu_wh"),
        ("hspf2", "hspf2_btu_wh"),
        ("uef", "uniform_energy_factor_uef"),
        ("ceer", "combined_energy_efficiency_ratio_ceer"),
        ("cooling_btu", "cooling_capacity_btu_h"),
    ):
        if row.get(field):
            out[name] = float(row[field])
    if row.get("indoor_unit_model_number"):
        pair = {"indoor": row["indoor_unit_model_number"], "outdoor": row["model_number"]}
        have = {_model_key(m) for m in bom_models}
        missing = [m for m in pair.values() if _model_key(m) not in have]
        out |= {
            "pair": pair,
            "bom_has_pair": not missing,
            "missing": missing[0] if missing else None,
        }
    return out


async def money(juris: Juris, job: str, parts: list[Part]) -> dict:
    """ENERGY STAR for the part, then Grok's cited incentives, each program's status from its own
    page; known programs the model missed are still listed (the probes answer without Grok)."""
    part = parts[0] if parts else None
    programs = _programs(juris.state, job)
    es = None
    if part and part.model_no and JOBS[job].energy_star:
        try:
            es = await energy_star(
                part.model_no, JOBS[job].energy_star, [p.model_no for p in parts if p.model_no]
            )
        except (httpx.HTTPError, ValueError, cache.OfflineMiss) as exc:
            logger.warning("rules: energy star failed: %s", exc)

    key = _money_key(juris, job)
    research = _research("rules_money", key, money_request(juris.state, job), _MoneyReply, DAY_S)
    data = await _section("incentive research", research)
    reply = _MoneyReply.model_validate(data["reply"]) if data else _MoneyReply(incentives=[])
    seen = {_norm_url(u) for u in (data or {}).get("citations", [])}
    cited = [i for i in reply.incentives if _norm_url(i.url) in seen]
    pages = await asyncio.gather(*(page_text(i.url) for i in cited))
    rows, matched = [], set()
    for inc, page in zip(cited, pages, strict=True):
        quote_status, quote_note = quote_check(inc.quote, page)
        amount = backed_amount(inc.amount, inc.quote, quote_status)
        # the model's program tag, else the program whose status page it cited (call #4 tagged
        # Georgia Power's own HEIP page "other")
        by_url = (k for k, p in programs.items() if _norm_url(p.url) == _norm_url(inc.url))
        pkey = inc.program if inc.program in programs else next(by_url, None)
        prog = programs.get(pkey) if pkey else None
        status, note, req = await program_status(prog) if prog else ("unknown", None, None)
        matched.add(pkey)
        rows.append(
            {
                "name": inc.name,
                "provider": inc.provider,
                "program": pkey,
                "amount_usd": amount,
                "amount_note": None if amount else "see program page",
                "how": inc.how,
                **_eligible_row(status, req, es),
                "status_source": prog.url if prog else None,
                "note": note,
                "url": inc.url,
                "quote": inc.quote,
                "quote_status": quote_status,
                "quote_note": quote_note,
            }
        )
    for key, prog in programs.items():  # known programs the model missed still get checked
        if key not in matched:
            status, note, req = await program_status(prog)
            rows.append(
                {
                    "name": prog.name,
                    "provider": prog.provider,
                    "program": key,
                    "amount_usd": None,
                    "amount_note": "see program page",
                    "how": None,
                    **_eligible_row(status, req, es),
                    "status_source": prog.url,
                    "note": note,
                    "url": prog.url,
                    "quote": None,
                    "quote_status": None,
                    "quote_note": None,
                }
            )
    for row in rows:
        row["counted"] = counted(row)
    return {
        "energy_star": es,
        "incentives": rows,
        # the only "after rebates" figure: live, eligible amounts (never unverified ones)
        "rebates_usd": sum(r["amount_usd"] for r in rows if r["counted"]),
        "dropped": len(reply.incentives) - len(cited),
        "research": "done" if data else "unavailable",
        "cost_usd": (data or {}).get("cost_usd"),
    }


# --- whole check ------------------------------------------------------------------------------


def _permit_phrase(name: str) -> str:
    name = re.sub(r"\b[A-Z][a-z]+\b", lambda m: m.group().lower(), name.strip())
    return f"{'an' if name[:1] in 'aeiou' else 'a'} {name}"


def spoken(permit_res: dict | None, money_res: dict | None, job: str | None) -> str:
    """Template line (F6's pattern): only permit names, the office and checked facts."""
    bits = []
    if job is None:
        bits.append("I have no permit rules on file for this kind of job.")
    elif permit_res is None:
        bits.append("I couldn't research the permit rules right now.")
    else:
        office = permit_res["office"]["name"]
        office = office if office.lower().startswith("the ") else f"the {office}"
        names = [_permit_phrase(i["name"]) for i in permit_res["items"][:2]]
        if permit_res["required"] == "yes":
            what = " and ".join(names) if names else "a permit"
            bits.append(f"You'll need {what} from {office}.")
            who = permit_res["who_can_pull"]["homeowner_allowed"]
            it = "them" if len(names) > 1 else "it"
            if who == "no":
                bits.append(f"A licensed contractor has to pull {it}.")
            elif who == "unclear":
                bits.append(f"Whether you can pull {it} yourself is unclear.")
        elif permit_res["required"] == "no":
            bits.append(f"Pages from {office} suggest no permit is needed.")
        else:
            bits.append(f"Whether this needs a permit is unclear; ask {office}.")

    es = (money_res or {}).get("energy_star") or {}
    rows = (money_res or {}).get("incentives", [])
    if any(r["status"] == "not_eligible" for r in rows):
        pass  # that row's reason says it
    elif es.get("missing"):
        bits.append(f"Rebates need the certified pair: add the {es['missing']}.")
    elif es and not es.get("certified"):
        bits.append(f"The {es['model']} isn't ENERGY STAR certified.")
    progs = {k: p for by_state in PROGRAMS.values() for k, p in by_state.items()}
    said = set()
    for row in rows:
        prog = progs.get(row.get("program") or "")
        name = row["name"]
        if not name.lower().startswith(row["provider"].lower()):
            name = f"{row['provider']} {name}"
        upto = f"up to {row['amount_usd']:,.0f} dollars" if row["amount_usd"] else None
        if row["status"] == "active" and upto and "active" not in said:
            said.add("active")
            if row.get("eligibility") == "unverified":
                bits.append(f"{name}: {upto}, but it {row['eligibility_reason']}.")
            else:
                bits.append(f"{name}: {upto}.")
        elif row["status"] == "not_eligible" and "not_eligible" not in said:
            said.add("not_eligible")
            bits.append(f"{name} doesn't apply: it {row['eligibility_reason']}.")
        elif prog and row["status"] == "ended" and row["program"] not in said:
            said.add(row["program"])
            bits.append(f"{prog.short[0].upper()}{prog.short[1:]} ended after 2025.")
        elif prog and row["status"] == "closed" and row["program"] not in said:
            said.add(row["program"])
            bits.append(f"{prog.short[0].upper()}{prog.short[1:]} isn't taking applications.")
    bits.append(DISCLAIMER)
    return " ".join(bits)


async def _section(name: str, coro) -> dict | None:
    """A failed source is None, never a raise: the check still answers with the rest."""
    try:
        return await coro
    except Exception as exc:  # noqa: BLE001 - every failure degrades the same way
        logger.warning("rules: %s failed: %s", name, exc)
        return None


STAGE = "rules check"  # the jobs.py stage that marks a rules check


def start(juris: Juris, job: str | None, parts: list[Part]) -> Job:
    """Run `check` as a background job (a cold check takes ~15-40 s; a warm one is instant)."""
    return jobs.start_task(STAGE, check(juris, job, parts))


def body(job: Job) -> dict:
    """`GET /rules/check/{id}` and the `show_rules` action's args."""
    out = {"check_id": job.id, "status": job.status, **(job.result or {})}
    return out | ({"error": job.error} if job.error else {})


def where(juris: Juris) -> str:
    """ "Atlanta city" -> "Atlanta"; a county stays "DeKalb County"."""
    return re.sub(r" (?:city|town|village)$", "", juris.authority)


async def check(
    juris: Juris, job: str | None, parts: list[Part], as_of: date | None = None
) -> dict:
    """The whole rules-and-money result for one job at one jurisdiction."""
    if job is None:  # unknown kind of job: say so, spend nothing
        permit_res = {"required": "unknown", "items": [], "office": None}
        money_res = None
        code_rows: list[dict] = []
    else:
        permit_res, money_res = await asyncio.gather(
            _section("permit", permit(juris, job)),
            _section("money", money(juris, job, parts)),
        )
        code_rows = codes(juris.state, JOBS[job].codes, as_of)
    costs = [s.get("cost_usd") for s in (permit_res, money_res) if s and s.get("cost_usd")]
    return {
        "job": job,
        "jurisdiction": juris.model_dump(),
        "permit": permit_res,
        "codes": code_rows,
        "money": money_res,
        "spoken": spoken(permit_res, money_res, job),
        "label": LABEL,
        "disclaimer": DISCLAIMER,
        "cost_usd": round(sum(costs), 4) if costs else None,
        "part_ids": [p.id for p in parts],  # the job packet matches a check to its parts
    }
