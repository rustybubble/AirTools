"""Core "find real parts that fit" pipeline (plan §4b.1-4b.2).

`find_parts(SearchRequest)`:
  whole-result cache -> discovery (SerpApi shopping + SerpApi home_depot search + web.search,
  parallel, cached, each failure -> empty; `llm.web_search` only as a last resort if all three
  come back empty) -> one LLM `extract` call (role "extract") turns the condensed, interleaved
  listings + web text into a `Plan` of candidates -> per candidate, bounded-concurrent: dims
  enrichment (Home Depot wins > page fetch on role "cheap" > sizes in the title > drop), image,
  sellers + recommendation, spec_url liveness, fit against the measurement -> `Part`.

Token budget (Groq free tier, 8K tokens/min on gpt-oss-120b): one search makes exactly one
role="extract" call with a condensed, capped prompt (7 shopping + 5 Home Depot listings,
~350 chars/web hit, whole prompt kept under ~4K tokens); dims fallback extraction runs on
role="cheap" (gpt-oss-20b, separate bucket) with a small page-text slice. `llm.web_search`
(~50K prompt tokens) is the discovery fallback of last resort only. A candidate the LLM already
tied to a numbered Home Depot listing resolves dims with a single `home_depot_product` call (no
search); an untied candidate falls back to at most one Home Depot search+product pair.
"""

import asyncio
import hashlib
import itertools
import logging
import math
import re
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Literal

from pydantic import BaseModel, Field

from server import cache, llm, meshgen, replace, sellers, vision, web
from server.config import get_settings
from server.models import (
    Cavity,
    Dims,
    Finish,
    Fit,
    Measurement,
    Opening,
    Part,
    SearchRequest,
    Seller,
)

logger = logging.getLogger(__name__)

_MAX_SHOPPING_IN_PROMPT = 7
_MAX_HD_IN_PROMPT = 5
_MAX_LISTINGS_IN_PROMPT = _MAX_SHOPPING_IN_PROMPT + _MAX_HD_IN_PROMPT
_WEB_HIT_CHARS = 350
_PAGE_FETCH_CHARS = 3000  # cheap-role budget: comfortably under ~3K tokens with room to spare
_CANDIDATE_CONCURRENCY = 3  # bounded fan-out so per-candidate "cheap" calls don't spike the bucket


# --- LLM output schema (role "extract") ----------------------------------------------------
# Strict-schema friendly for Groq/xAI: no dict/Any fields, every optional field a nullable
# scalar or nested model (server.llm._strict_schema walks nested $defs too).


class DimsMm(BaseModel):
    w: float | None = None
    d: float | None = None
    h: float | None = None


class FitRangeMm(BaseModel):
    """e.g. a window AC unit that "fits windows 23-36 in wide" -> min=584, max=914."""

    min: float
    max: float


class FinishOut(BaseModel):
    name: str
    hex: str | None = None


class Candidate(BaseModel):
    name: str
    manufacturer: str | None = None
    model_no: str | None = None
    dims_mm: DimsMm = Field(default_factory=DimsMm)
    dims_evidence: str | None = None  # short quote the dims came from, for provenance
    fit_range_mm: FitRangeMm | None = None
    color_name: str | None = None
    color_hex: str | None = None
    material: str | None = None
    finishes: list[FinishOut] = []
    listing_indexes: list[int] = []  # 1-based, into the numbered shopping-listings block
    spec_url: str | None = None
    mount_surface: str | None = None
    spacing_mm: float | None = None


class Plan(BaseModel):
    candidates: list[Candidate] = []
    measure_axis: Literal["w", "d", "h", "length"] | None = None
    note: str = ""


# --- prompt building -----------------------------------------------------------------------


def _hd_as_listing(hd: dict) -> dict:
    """Shape a `sellers.home_depot_search` result like a shopping listing, tagged with
    `product_id` so dims resolution can skip straight to `home_depot_product` once a candidate
    is tied to it via `listing_indexes` -- no second search, no title matching needed."""
    return {
        "title": hd.get("title"),
        "source": "Home Depot",
        "price_usd": hd.get("price_usd"),
        "delivery": None,
        "rating": hd.get("rating"),
        "reviews": hd.get("reviews"),
        "product_link": hd.get("link"),
        "thumbnail": hd.get("thumbnail"),
        "immersive_token": None,
        "product_id": hd.get("product_id"),
    }


def _merge_listings(shopping_listings: list[dict], hd_listings: list[dict]) -> list[dict]:
    """Cap shopping/Home Depot listings separately (priced first) then interleave them so the
    numbered prompt block shows both discovery sources instead of one crowding out the other."""

    def _priced_first(listings: list[dict]) -> list[dict]:
        return sorted(listings, key=lambda listing: listing.get("price_usd") is None)

    shopping_top = _priced_first(shopping_listings)[:_MAX_SHOPPING_IN_PROMPT]
    hd_top = [_hd_as_listing(hd) for hd in _priced_first(hd_listings)[:_MAX_HD_IN_PROMPT]]
    paired = itertools.zip_longest(shopping_top, hd_top)
    return [listing for listing in itertools.chain.from_iterable(paired) if listing is not None]


def _condense_listings(
    listings: list[dict], limit: int = _MAX_LISTINGS_IN_PROMPT
) -> tuple[str, list[dict]]:
    """Numbered block for the prompt, priced listings first, capped to `limit`.

    Returns (block_text, the_capped_listings_in_prompt_order) -- callers need the second to
    resolve `Candidate.listing_indexes` back to real listing dicts.
    """
    prioritized = sorted(listings, key=lambda listing: listing.get("price_usd") is None)[:limit]
    lines = []
    for i, listing in enumerate(prioritized, 1):
        price = (
            f"${listing['price_usd']:.2f}"
            if listing.get("price_usd") is not None
            else "price unknown"
        )
        bits = [
            listing.get("title") or "",
            listing.get("source") or "",
            price,
            listing.get("delivery") or "",
        ]
        lines.append(f"[{i}] " + " — ".join(b for b in bits if b))
    return "\n".join(lines), prioritized


def _measurement_text(measurement: Measurement | None) -> str:
    if measurement is None:
        return "no measurement provided"
    mm = measurement.value_m * 1000.0
    label = measurement.label or "unlabeled"
    return f"{label} = {measurement.value_m:.3f} m ({mm:.0f} mm)" + (
        f", axis hint: {measurement.axis}" if measurement.axis else ""
    )


_SPARE_CANDIDATES = 2  # planned beyond max_candidates, built only to replace dropped picks

_SYSTEM_PROMPT = (
    "You find real, buyable construction/hardware parts. Your job: pick up to {n} DISTINCT "
    "real products of the type asked for in the query -- never an accessory for it (a pad, "
    "cover, bracket or install kit for the asked-for product) in its place. Strongly prefer "
    "the numbered listings below (shopping and Home Depot) over the web text -- whenever a "
    "candidate is one of those listings, you MUST set listing_indexes to its bracket number(s) "
    "([n]); leave it empty only if no listing matches. Never invent a product that isn't in the "
    "listings or the web text. The web text is supporting evidence for a listed product's specs "
    "(dimensions, fit range, material) -- not a product source of its own, unless there are no "
    "listings at all. Fill dims_mm ONLY from numbers actually published in the text -- null any "
    "dimension you don't have; never guess, the server looks up missing dims itself afterwards. "
    "Convert inches to millimetres. w = width, side-to-side as installed; h = height, vertical; "
    "d = depth, out from the mounting surface. fit_range_mm is null unless one of two things "
    "is stated for THAT product: (1) a published fit range (e.g. \"fits windows 23-36 in "
    "wide\"); (2) the size of the thing it holds, accepts or connects to, stated in its own "
    "title or specs, when the measurement is of that thing (a '5 in.' gutter hanger holds a "
    "127 mm gutter; a 2x6 joist hanger accepts 1-1/2 in (38 mm) lumber; min = max for one "
    "size). Never infer a fit range from general knowledge or from the measurement itself -- "
    "for anything that simply has to fit into the measured space (a panel on a roof, a heater "
    "in a closet) leave it null; the server compares the product's dims. List the best "
    "candidates first. Whether a candidate's size plausibly fits the given measurement is a "
    "soft preference only, never a requirement for including it -- the server computes the "
    "real fit afterwards from verified dims. Set measure_axis to whichever part dimension the "
    "measurement constrains, inferred from the query and the measurement label."
)


def _opening_text(opening: Opening | None) -> str:
    """assetgen: the taped opening for the planner, e.g. "1500 x 1200 mm (59 x 47.2 in), W x H"."""
    if opening is None:
        return ""
    w, h = opening.size_mm()
    size = f"{w:.0f} x {h:.0f} mm ({w / 25.4:.1f} x {h / 25.4:.1f} in), width x height"
    depth = f", {opening.d_m * 1000:.0f} mm deep" if opening.d_m else ""
    return (
        f"Opening (taped by the headset): {size}{depth}. The product goes INTO this opening: "
        "prefer products made for it -- just under it on both width and height (a replacement "
        "window is ordered about 1/2 in. under the rough opening); never much bigger or much "
        "smaller.\n"
    )


def _build_prompt(
    req: SearchRequest, listings_block: str, web_block: str, photo: str | None = None
) -> list[dict]:
    system = _SYSTEM_PROMPT.format(n=req.max_candidates + _SPARE_CANDIDATES)
    user = (
        f"Query: {req.query}\n"
        + (f"Photo of the existing part (from the headset): {photo}\n" if photo else "")
        + f"Measurement: {_measurement_text(req.measurement)}\n"
        + _opening_text(req.opening)
        + f"Max candidates: {req.max_candidates + _SPARE_CANDIDATES}\n\n"
        f"Listings (shopping + Home Depot):\n{listings_block or '(none)'}\n\n"
        f"Web search:\n{web_block or '(none)'}\n"
    )
    return [{"role": "system", "content": system}, {"role": "user", "content": user}]


# --- tiny helpers ----------------------------------------------------------------------------


def _tokens(text: str | None) -> set[str]:
    return set(re.findall(r"[a-z0-9]+", (text or "").lower()))


def _title_matches(
    candidate: Candidate, brand: str | None, model_no: str | None, title: str
) -> bool:
    """Same product? Model number match, or same brand (compared against the brand field only,
    so a generic word like "aluminum" in a title can't match) AND every size number in the
    candidate's name appears in the title (a 6 in. hanger never lends its dims to a 5 in. one)."""
    if candidate.model_no and model_no and _tokens(candidate.model_no) == _tokens(model_no):
        return True
    cand_brand = {t for t in _tokens(candidate.manufacturer) if len(t) > 2}
    if not (cand_brand and cand_brand & _tokens(brand)):
        return False
    return {t for t in _tokens(candidate.name) if t.isdigit()} <= _tokens(title)


def _listing_matches_part(part: Part, title: str) -> bool:
    """Same product? Mirrors `_title_matches`, but for a plain shopping-listing title with no
    separate brand/model_no field of its own to compare -- model_no token(s) found in the title,
    or the manufacturer plus 2+ words of the part's name, with every size number in the part's
    name in the title (a title with no numbers at all can't name a conflicting size)."""
    tokens = _tokens(title)
    if part.model_no and _tokens(part.model_no) <= tokens:
        return True
    brand = {t for t in _tokens(part.manufacturer) if len(t) > 2}
    if not (brand and brand & tokens):
        return False
    name = _tokens(part.name)
    if len({t for t in name - brand if len(t) > 3 and not t.isdigit()} & tokens) < 2:
        return False  # same brand, different product ("Amerimax Downspout Extension")
    return {t for t in name if t.isdigit()} <= tokens or not any(t.isdigit() for t in tokens)


def _make_id(candidate: Candidate) -> str:
    if candidate.manufacturer and candidate.model_no:
        base = f"{candidate.manufacturer} {candidate.model_no}"
    else:
        base = candidate.name
    slug = re.sub(r"[^a-z0-9]+", "-", base.lower()).strip("-") or "part"
    digest = hashlib.sha1(base.lower().encode()).hexdigest()[:6]
    return f"{slug}-{digest}"


def _is_home_depot(listing: dict) -> bool:
    return "home depot" in (listing.get("source") or "").lower()


# --- pure fit computation ---------------------------------------------------------------------


# Headset measurement error: a 126 mm reading of a 5 in. (127 mm) gutter still fits a 5 in.
# hanger. Applies to fit ranges only (sold-by-size parts); overall dims compare exactly.
FIT_TOLERANCE_MM = 5.0


def _fit_note(spare: float) -> str:
    return "exact fit" if abs(spare) < 2 else f"fits, {spare:.0f} mm spare"


def compute_fit(
    dims: Dims,
    measurement: Measurement | None,
    measure_axis: str | None,
    fit_range: FitRangeMm | None,
) -> Fit:
    """Fit vs `measurement`. `fit_range` wins when present; else the axis dimension vs the
    measurement; "length" axis (a run, not a size constraint) always fits.

    A fit_range miss below `min` means the part can't shrink enough for the opening (too_big);
    a miss above `max` means the part can't stretch enough to span it (too_small).
    """
    if measurement is None or measure_axis is None:
        return Fit(status="unknown", axis=measure_axis, note="no measurement to fit against")

    meas_mm = measurement.value_m * 1000.0

    if fit_range is not None:
        if fit_range.min - FIT_TOLERANCE_MM <= meas_mm <= fit_range.max + FIT_TOLERANCE_MM:
            spare = max(0.0, min(meas_mm - fit_range.min, fit_range.max - meas_mm))
            return Fit(
                status="fits", spare_mm=round(spare, 1), axis=measure_axis, note=_fit_note(spare)
            )
        if meas_mm < fit_range.min:
            miss = fit_range.min - meas_mm
            return Fit(
                status="too_big",
                spare_mm=round(-miss, 1),
                axis=measure_axis,
                note=f"too big, needs {miss:.0f} mm more opening",
            )
        miss = meas_mm - fit_range.max
        return Fit(
            status="too_small",
            spare_mm=round(-miss, 1),
            axis=measure_axis,
            note=f"too small for the opening by {miss:.0f} mm",
        )

    if measure_axis == "length":
        return Fit(
            status="fits",
            spare_mm=None,
            axis="length",
            note="fits (length run, no size constraint)",
        )

    axis_dim = getattr(dims, measure_axis, None)
    if axis_dim is None:
        return Fit(status="unknown", axis=measure_axis, note="no published dimension on this axis")
    spare = meas_mm - axis_dim
    if spare >= 0:
        return Fit(
            status="fits", spare_mm=round(spare, 1), axis=measure_axis, note=_fit_note(spare)
        )
    word = {"w": "wide", "h": "tall", "d": "deep"}.get(measure_axis, "big")
    return Fit(
        status="too_big",
        spare_mm=round(spare, 1),
        axis=measure_axis,
        note=f"too {word} by {abs(spare):.0f} mm",
    )


# --- discovery ---------------------------------------------------------------------------------


def discovery_query(req: SearchRequest) -> str:
    """assetgen: what the shops are searched for. With a taped opening its size goes along
    ("window frame 59 x 47 in"): stores list windows by size, so the listings are the ones made
    for it, not every frame or screen."""
    if req.opening is None:
        return req.query
    w, h = (round(v / 25.4 * 2) / 2 for v in req.opening.size_mm())
    return f"{req.query} {w:g} x {h:g} in"


async def _discover(req: SearchRequest) -> tuple[list[dict], list[dict], list[web.Hit]]:
    query = discovery_query(req)
    raw_listings, raw_hd, raw_hits = await asyncio.gather(
        sellers.shopping(query),
        sellers.home_depot_search(query),
        web.search(f"{query} dimensions specifications"),
        return_exceptions=True,
    )
    listings = raw_listings if isinstance(raw_listings, list) else []
    hd_listings = raw_hd if isinstance(raw_hd, list) else []
    hits = raw_hits if isinstance(raw_hits, list) else []
    if isinstance(raw_listings, Exception):
        logger.info("find_parts: sellers.shopping failed: %s", raw_listings)
    if isinstance(raw_hd, Exception):
        logger.info("find_parts: sellers.home_depot_search failed: %s", raw_hd)
    if isinstance(raw_hits, Exception):
        logger.info("find_parts: web.search failed: %s", raw_hits)

    if not listings and not hd_listings and not hits:
        # Last resort: Groq browser_search costs ~50K prompt tokens, so only when every cheap
        # discovery path (SerpApi shopping + SerpApi Home Depot + web.search) came back empty.
        try:
            result = await llm.web_search(f"{req.query} dimensions specifications price")
        except Exception as exc:  # noqa: BLE001 -- any provider failure just means no fallback hits
            logger.info("find_parts: web_search fallback failed: %s", exc)
        else:
            hits = [
                web.Hit(title=s.title, url=s.url, text=s.content, source="groq_browser_search")
                for s in result.sources
            ]
            if not hits and result.text:
                hits = [web.Hit(title="", url="", text=result.text, source="groq_browser_search")]
    return listings, hd_listings, hits


# --- per-candidate build --------------------------------------------------------------------


def _hd_listing_product_id(cand_listings: list[dict]) -> str | None:
    """product_id of an HD listing the LLM already tied this candidate to via listing_indexes,
    if any -- lets dims resolution skip straight to `home_depot_product` (1 call, no search, no
    title matching needed since the LLM did the matching)."""
    return next(
        (listing["product_id"] for listing in cand_listings if listing.get("product_id")), None
    )


async def _dims_from_home_depot(candidate: Candidate) -> tuple[Dims | None, dict | None]:
    """One HD query (model number if present, else name) -- at most one search+product pair per
    candidate to save quota. Only a matching product counts."""
    query = candidate.model_no or candidate.name
    hd = await sellers.home_depot_lookup(query)
    if not hd:
        return None, None
    if _title_matches(candidate, hd.get("brand"), hd.get("model_number"), hd.get("title") or ""):
        return hd.get("dims"), hd
    return None, None


def _retry_role_ready(role: str, first: str) -> bool:
    """assetgen: `role` resolves (its LLM_<ROLE> setting is on) to a provider with a key, other
    than `first`'s provider:model."""
    try:
        retry, primary = llm.resolve_role(role), llm.resolve_role(first)
    except ValueError:
        return False
    key = llm.PROVIDERS[retry[0]]["api_key_field"]
    return retry != primary and bool(getattr(get_settings(), key, None))


async def _extract_dims(candidate: Candidate, text: str) -> Dims | None:
    messages = [
        {
            "role": "system",
            "content": "Extract only this product's published width, depth and height in "
            "millimetres from the text below. Convert inches to mm. Null any "
            "dimension that isn't explicitly published for this exact product -- never "
            "guess.",
        },
        {
            "role": "user",
            "content": f"Product: {candidate.name}\n\nText:\n{text[:_PAGE_FETCH_CHARS]}",
        },
    ]
    # assetgen: the cheap model first; when it finds no complete size (or fails), one retry on
    # `extract_retry` (LLM_EXTRACT_RETRY, default Grok 4.20 non-reasoning).
    roles = ["cheap"] + (["extract_retry"] if _retry_role_ready("extract_retry", "cheap") else [])
    for role in roles:
        try:
            page_dims = await llm.extract(role, messages, DimsMm)
        except Exception as exc:  # noqa: BLE001 -- bad/unparseable text, fall through to "drop"
            logger.info(
                "find_parts: dims extract (%s) failed for %r: %s", role, candidate.name, exc
            )
            continue
        if page_dims.w is not None and page_dims.d is not None and page_dims.h is not None:
            if role != "cheap":
                logger.info("find_parts: %r dims from the %s retry", candidate.name, role)
            return Dims(w=page_dims.w, d=page_dims.d, h=page_dims.h)
    return None


async def _dims_from_page(candidate: Candidate, cand_listings: list[dict]) -> Dims | None:
    """The product page first; else web-search text for "<brand> <model> dimensions" (spec
    sheets, retailer pages the fetchers can't render), both read by the cheap role."""
    fetch_url = candidate.spec_url or next(
        (
            listing.get("product_link")
            for listing in cand_listings
            if not _is_home_depot(listing) and listing.get("product_link")
        ),
        None,
    )
    page_text = await web.fetch(fetch_url) if fetch_url else None
    if page_text:
        dims = await _extract_dims(candidate, page_text)
        if dims is not None:
            return dims

    subject = (
        f"{candidate.manufacturer} {candidate.model_no}"
        if candidate.manufacturer and candidate.model_no
        else candidate.name
    )
    try:
        hits = await web.search(f"{subject} dimensions", n=4)
    except Exception as exc:  # noqa: BLE001 -- search is a last resort, never fatal
        logger.info("find_parts: dims web search failed for %r: %s", candidate.name, exc)
        return None
    text = web.condense(hits, per_hit_chars=800)
    return await _extract_dims(candidate, text) if text else None


async def _resolve_dims(
    candidate: Candidate, cand_listings: list[dict]
) -> tuple[Dims | None, str, dict | None]:
    """Home Depot wins outright when it matches (even over already-complete LLM dims), else
    the LLM's own dims if complete, else a page-fetch fallback, else sizes printed in the
    title (`_dims_from_title`), else give up (drop).

    An HD listing the LLM already tied via listing_indexes skips straight to
    `home_depot_product(product_id)` (1 call); otherwise at most one HD search+product pair as a
    fallback (model number, else name) -- never both, to save SerpApi quota.
    """
    needs_hd = any(
        v is None for v in (candidate.dims_mm.w, candidate.dims_mm.d, candidate.dims_mm.h)
    )
    hd_product_id = _hd_listing_product_id(cand_listings)
    is_hd_listing = any(_is_home_depot(listing) for listing in cand_listings)

    hd_result = None
    if hd_product_id:
        hd_result = await sellers.home_depot_product(hd_product_id)
    elif needs_hd or is_hd_listing:
        _, hd_result = await _dims_from_home_depot(candidate)

    if hd_result and hd_result.get("dims") is not None:
        typical = hd_result.get("depth_typical")  # assetgen: a window's standard jamb depth
        return hd_result["dims"], "home_depot_typical_depth" if typical else "home_depot", hd_result

    if not needs_hd:
        dims = candidate.dims_mm
        return Dims(w=dims.w, d=dims.d, h=dims.h), "llm", hd_result

    page_dims = await _dims_from_page(candidate, cand_listings)
    if page_dims is not None:
        return page_dims, "page", hd_result

    title_dims = _dims_from_title(candidate, cand_listings, hd_result)
    if title_dims is not None:
        return title_dims, "title", hd_result

    window_dims = _window_dims_from_title(candidate, cand_listings, hd_result)  # assetgen
    if window_dims is not None:
        return window_dims, "title_typical_depth", hd_result

    return None, "", hd_result


def _dims_from_title(
    candidate: Candidate, cand_listings: list[dict], hd: dict | None
) -> Dims | None:
    """Last resort: sizes printed in the product's own name or listing titles
    (`sellers.parse_title_sizes`). Only completes a box, never guesses a side: W/D/H-labelled
    sizes take their axis, three unlabelled ones follow `_parse_dims`' flat-goods rule (thinnest
    = depth, longest = height), and a single unlabelled size may only fill the one side the LLM's
    published dims left empty. Anything else stays unknown (dropped)."""
    titles = [candidate.name, (hd or {}).get("title"), *(li.get("title") for li in cand_listings)]
    sizes = next((s for s in map(sellers.parse_title_sizes, titles) if s), [])
    known = candidate.dims_mm.model_dump()  # sides the LLM filled from published text
    for mm, axis in sizes:
        if axis and known[axis] is None:
            known[axis] = mm
    missing = [axis for axis, v in known.items() if v is None]
    loose = [
        mm
        for mm, axis in sizes
        if axis is None and all(v is None or abs(v - mm) > 2 for v in known.values())
    ]
    if len(missing) == 3 and len(loose) == 3:
        thin, mid, long_ = sorted(loose)
        known = {"w": mid, "d": thin, "h": long_}
    elif len(missing) == 1 and len(loose) == 1:
        known[missing[0]] = loose[0]
    if any(v is None for v in known.values()):
        return None
    return Dims(**known)


# --- per-candidate sellers ------------------------------------------------------------------

_MAX_SELLERS = 8
_MAX_IMMERSIVE_CALLS = 2


def _window_dims_from_title(
    candidate: Candidate, cand_listings: list[dict], hd: dict | None
) -> Dims | None:
    """assetgen: a window sold by its size ("59.5 in. x 47.5 in. ... Sliding Window"): windows
    and doors are listed width x height, so two unlabelled title sizes are W then H, and the depth
    is the standard jamb depth (`sellers.WINDOW_DEPTH_IN`, dims_source "title_typical_depth").
    None for anything that isn't a window, or any other title."""
    if not re.search(meshgen.WINDOW_NAME, (candidate.name or "").lower()):
        return None
    titles = [candidate.name, (hd or {}).get("title"), *(li.get("title") for li in cand_listings)]
    sizes = next((s for s in map(sellers.parse_title_sizes, titles) if s), [])
    labelled = {axis: mm for mm, axis in sizes if axis}
    loose = [mm for mm, axis in sizes if axis is None]
    w, h = labelled.get("w"), labelled.get("h")
    if w is None and h is None and len(loose) == 2:
        w, h = loose
    if not w or not h or len(sizes) > 3:
        return None
    return Dims(w=w, h=h, d=labelled.get("d") or sellers.WINDOW_DEPTH_IN * 25.4)


def _hd_seller(hd: dict | None) -> Seller | None:
    """The matched Home Depot product itself, as a verified seller."""
    if not hd or hd.get("price") is None:
        return None
    pack_qty, unit_price = sellers.pack_pricing(hd.get("title"), hd["price"])
    return Seller(
        name="Home Depot",
        title=hd.get("title"),
        price_usd=hd["price"],
        total_usd=hd["price"],
        pack_qty=pack_qty,
        unit_price_usd=unit_price,
        eta=hd.get("eta"),
        eta_days=sellers.eta_days(hd.get("eta"), datetime.now(UTC).date()),
        rating=hd.get("rating"),
        reviews=hd.get("reviews"),
        in_stock=True,
        url=hd.get("link"),
        verified=True,
    )


def _seller_from_listing(listing: dict) -> Seller:
    title = listing.get("title")
    price = listing.get("price_usd")
    pack_qty, unit_price = sellers.pack_pricing(title, price)
    return Seller(
        name=listing.get("source") or "unknown",
        title=title,
        price_usd=price,
        pack_qty=pack_qty,
        unit_price_usd=unit_price,
        shipping=listing.get("delivery"),
        shipping_usd=sellers.parse_shipping(listing.get("delivery")),
        rating=listing.get("rating"),
        reviews=listing.get("reviews"),
        url=listing.get("product_link"),
        verified=True,
    )


def _merge_sellers(*seller_lists: list[Seller]) -> list[Seller]:
    """Union sellers across any number of lists, deduped by lowercased name, cheapest unit cost
    wins -- the one dedupe rule every seller-merging caller (`_build_sellers`,
    `_sellers_from_stores`, `expand_sellers`) shares."""
    merged: dict[str, Seller] = {}
    for seller in itertools.chain.from_iterable(seller_lists):
        key = seller.name.lower()
        existing = merged.get(key)
        if existing is None or sellers.unit_cost(seller) < sellers.unit_cost(existing):
            merged[key] = seller
    return list(merged.values())


async def _sellers_from_stores(cand_listings: list[dict]) -> list[Seller]:
    """stores() for up to `_MAX_IMMERSIVE_CALLS` of the candidate's listings, deduped by seller
    name keeping the cheapest unit cost."""
    tokens = list(
        dict.fromkeys(
            listing["immersive_token"]
            for listing in cand_listings
            if listing.get("immersive_token")
        )
    )[:_MAX_IMMERSIVE_CALLS]
    if not tokens:
        return []
    results = await asyncio.gather(*(sellers.stores(t) for t in tokens), return_exceptions=True)
    ok_results = []
    for result in results:
        if isinstance(result, Exception):
            logger.info("find_parts: sellers.stores failed: %s", result)
            continue
        ok_results.append(result)
    return _merge_sellers(*ok_results)


async def _build_sellers(
    hd: dict | None, cand_listings: list[dict], stores: bool = True
) -> list[Seller]:
    """Union of: the matched Home Depot product itself, stores() from the candidate's listings,
    deduped by seller name (cheapest unit cost wins); falls back to the candidate's own shopping
    listings only when both of those are empty. Capped at `_MAX_SELLERS`. `stores=False` (a
    catalog browse search, `SearchRequest.lite`) makes no stores() calls."""
    hd_seller = _hd_seller(hd)
    from_stores = await _sellers_from_stores(cand_listings) if stores else []
    merged = _merge_sellers([hd_seller] if hd_seller else [], from_stores)
    result = merged or [_seller_from_listing(listing) for listing in cand_listings]
    return result[:_MAX_SELLERS]


async def _build_candidate(
    candidate: Candidate,
    req: SearchRequest,
    measure_axis: str | None,
    listings_prompt: list[dict],
    progress: Callable[[str], None],
) -> Part | None:
    cand_listings = [
        listings_prompt[i - 1] for i in candidate.listing_indexes if 1 <= i <= len(listings_prompt)
    ]

    dims, dims_source, hd = await _resolve_dims(candidate, cand_listings)
    if dims is None:
        logger.info(
            "find_parts: dropping %r (%s) -- no dims from LLM, Home Depot, page, or title",
            candidate.name,
            candidate.model_no,
        )
        return None
    if dims_source == "llm":
        logger.info(
            "find_parts: %r dims from LLM, evidence=%r", candidate.name, candidate.dims_evidence
        )

    if hd:
        # Home Depot's own record beats the LLM for name/brand/model (the LLM sometimes names
        # the retailer as manufacturer, or a web-text product as the listing) and fills what it left blank; brand/model drive seller
        # expansion and the part id, and the HD page is a real spec page.
        candidate = candidate.model_copy(
            update={
                "name": hd.get("title") or candidate.name,
                "manufacturer": hd.get("brand") or candidate.manufacturer,
                "model_no": hd.get("model_number") or candidate.model_no,
                "material": candidate.material or hd.get("material"),
                "color_name": candidate.color_name or hd.get("color"),
                "spec_url": candidate.spec_url or hd.get("link"),
            }
        )

    image_url = None
    if hd and hd.get("image"):
        image_url = hd["image"]
    else:
        image_url = next(
            (listing.get("thumbnail") for listing in cand_listings if listing.get("thumbnail")),
            None,
        )

    seller_list = await _build_sellers(hd, cand_listings, stores=not req.lite)
    if not seller_list:
        logger.info("find_parts: dropping %r -- nowhere to buy it", candidate.name)
        return None
    rec = sellers.recommend(seller_list, "best")
    recommended_seller, recommendation_reason = rec if rec else (None, None)

    spec_url = candidate.spec_url
    llm_url = spec_url != (hd or {}).get("link")  # Home Depot's own link needs no check
    if spec_url and llm_url and not await web.url_alive(spec_url):
        spec_url = None

    # A published spec (Home Depot's own min/max window opening) beats the LLM's guess.
    fit_range = candidate.fit_range_mm
    if fit_range is None and hd and hd.get("fit_range_mm"):
        fit_range = FitRangeMm(**hd["fit_range_mm"])
    fit = compute_fit(dims, req.measurement, measure_axis, fit_range)
    if fit.axis == "length" and candidate.spacing_mm and req.measurement:
        # ponytail: naive fence-post (+1); swap for a real layout model if spacing accuracy
        # ever matters more than a spoken "about N" estimate.
        qty = max(1, round((req.measurement.value_m * 1000.0) / candidate.spacing_mm) + 1)
        fit = fit.model_copy(
            update={"note": f"{fit.note}, qty {qty} at {candidate.spacing_mm:.0f} mm spacing"}
        )

    citations = []
    for url in [spec_url, *[listing.get("product_link") for listing in cand_listings]]:
        if url and url not in citations:
            citations.append(url)

    progress("checking the spec sheets")

    return Part(
        id=_make_id(candidate),
        name=candidate.name,
        manufacturer=candidate.manufacturer,
        model_no=candidate.model_no,
        dims_mm=dims,
        dims_source=dims_source,
        color_hex=candidate.color_hex,
        finish=candidate.color_name,
        material=candidate.material,
        finishes=[Finish(name=f.name, hex=f.hex) for f in candidate.finishes],
        mount={"face": "-z", "surface": candidate.mount_surface},
        spacing_mm=candidate.spacing_mm,
        fit_range_mm=fit_range.model_dump() if fit_range else None,
        image_url=image_url,
        spec_url=spec_url,
        citations=citations,
        sellers=seller_list,
        recommended_seller=recommended_seller,
        recommendation_reason=recommendation_reason,
        fit=fit,
        fetched_at=datetime.now(UTC),
    )


# --- sort + top-level pipeline ----------------------------------------------------------------


def _seller_total(part: Part) -> float:
    if part.recommended_seller is None or not (0 <= part.recommended_seller < len(part.sellers)):
        return math.inf
    seller = part.sellers[part.recommended_seller]
    total = seller.total_usd if seller.total_usd is not None else seller.price_usd
    return total if total is not None else math.inf


async def _describe_photo(req: SearchRequest) -> str | None:
    """The headset's frame of the existing part, described by the vision model (its own token
    bucket, runs alongside discovery). A bad frame never sinks the search."""
    if not req.frame_jpg_b64:
        return None
    try:
        return await vision.identify_part(req.frame_jpg_b64, hint=req.query)
    except Exception as exc:  # noqa: BLE001 -- vision is a bonus, never a blocker
        logger.info("find_parts: frame description failed: %s", exc)
        return None


async def _search_uncached(req: SearchRequest, progress: Callable[[str], None]) -> list[Part]:
    progress("searching the supply shops")
    (listings, hd_listings, hits), photo = await asyncio.gather(
        _discover(req), _describe_photo(req)
    )

    progress("charting the candidates")
    listings_block, listings_prompt = _condense_listings(_merge_listings(listings, hd_listings))
    web_block = web.condense(hits, per_hit_chars=_WEB_HIT_CHARS)
    plan = await llm.extract("extract", _build_prompt(req, listings_block, web_block, photo), Plan)
    if plan.note:
        logger.info("find_parts: plan note=%r", plan.note)

    measure_axis = None
    if req.measurement is not None:
        measure_axis = req.measurement.axis or plan.measure_axis

    sem = asyncio.Semaphore(_CANDIDATE_CONCURRENCY)

    async def _bounded(candidate: Candidate) -> Part | None:
        async with sem:
            try:
                return await _build_candidate(
                    candidate, req, measure_axis, listings_prompt, progress
                )
            except Exception as exc:  # noqa: BLE001 -- one bad candidate shouldn't sink the search
                logger.warning("find_parts: candidate %r failed: %s", candidate.name, exc)
                return None

    # The plan carries spares; they're only built (more lookups) when a first pick is dropped.
    n = req.max_candidates
    first, spares = plan.candidates[:n], plan.candidates[n:]
    parts = _dedupe([p for p in await asyncio.gather(*[_bounded(c) for c in first]) if p])
    if len(parts) < n and spares:
        spare_parts = await asyncio.gather(*[_bounded(c) for c in spares[: n - len(parts)]])
        parts = _dedupe(parts + [p for p in spare_parts if p])[:n]

    progress("plotting the best fit")
    parts.sort(key=lambda p: (p.fit.status != "fits", _seller_total(p)))
    return parts


def _dedupe(parts: list[Part]) -> list[Part]:
    """Two plan entries can resolve to one product (same brand + model = same part id)."""
    first: dict[str, Part] = {}
    for part in parts:
        first.setdefault(part.id, part)
    return list(first.values())


async def find_parts(
    req: SearchRequest, progress: Callable[[str], None] = lambda s: None
) -> list[Part]:
    """Whole-result cache -> discovery -> LLM plan -> per-candidate build -> sort. Never raises
    for a single candidate's failure; only the LLM `extract` call itself is allowed to bubble."""
    key = {
        "q": cache.normalize(req.query),
        "m": req.measurement.model_dump() if req.measurement else None,
    }
    if req.opening is not None:  # assetgen: its size shapes discovery and the planner's picks
        key["o"] = [round(v / 10) for v in req.opening.size_mm()]  # to the centimetre
    raw = cache.get("search", key)
    refit = False
    if not raw and get_settings().OFFLINE:
        # A live headset never repeats the warmed measurement to the millimetre: reuse any
        # cached answer for this query and recompute fit against the new measurement.
        raw, refit = cache.get("search_by_query", key["q"]), True
    if raw:
        parts = [Part.model_validate(p) for p in raw]
        for part in parts:
            part.cached = True
            if refit:
                axis = (req.measurement and req.measurement.axis) or part.fit.axis
                fit_range = FitRangeMm(**part.fit_range_mm) if part.fit_range_mm else None
                part.fit = compute_fit(part.dims_mm, req.measurement, axis, fit_range)
        if refit:
            parts.sort(key=lambda p: (p.fit.status != "fits", _seller_total(p)))
        return fit_to_opening(fit_to_cavity(parts, req.cavity), req.opening)

    if get_settings().OFFLINE:
        # No cached answer and the uplink is off-limits: never touch discovery or the LLM.
        return []

    parts = await _search_uncached(req, progress)
    if parts:  # an empty result is worth retrying, not remembering
        dumped = [p.model_dump(mode="json") for p in parts]
        cache.put("search", key, dumped)
        cache.put("search_by_query", key["q"], dumped)
    return fit_to_opening(fit_to_cavity(parts, req.cavity), req.opening)


def fit_to_cavity(parts: list[Part], cavity: Cavity | None) -> list[Part]:
    """e2e: a search for a removed part's gap -- every candidate's fit is against the gap's three
    axes (replace.fit: the tightest clearance decides), fitting ones first, then cheapest. The
    cache keeps the measurement's fit; this runs on every answer, cached or not."""
    if cavity is None:
        return parts
    for part in parts:
        part.fit = replace.fit(part.dims_mm, cavity)
    parts.sort(key=lambda p: (p.fit.status != "fits", _seller_total(p)))
    return parts


# assetgen: a part for a taped opening (a window frame): its width and height against the
# opening's. Over by more than OPENING_TIGHT_MM is too big; more than OPENING_LOOSE_MM under on
# an axis is too small (a gap to fill, not a product made for it). The headset uses the same.
OPENING_TIGHT_MM = 3.0
OPENING_LOOSE_MM = 51.0  # 2 in.


def opening_fit(dims: Dims, opening: Opening) -> Fit:
    """A part against the opening's width and height (its depth only when the opening has one):
    the tightest clearance decides "fits" / "too_big"; a part far smaller is "too_small"."""
    w, h = opening.size_mm()
    clearance = {"w": w - dims.w, "h": h - dims.h}
    if opening.d_m:
        clearance["d"] = opening.d_m * 1000.0 - dims.d
    axis = min(clearance, key=lambda k: clearance[k])
    spare = clearance[axis]
    word = {"w": "wide", "h": "tall", "d": "deep"}[axis]
    if spare < -OPENING_TIGHT_MM:
        note = f"too {word} for the opening by {abs(spare):.0f} mm"
        return Fit(status="too_big", spare_mm=round(spare, 1), axis=axis, note=note)
    loose_axis = max(("w", "h"), key=lambda k: clearance[k])
    loose = clearance[loose_axis]
    if loose > OPENING_LOOSE_MM:
        word = {"w": "narrow", "h": "short"}[loose_axis]
        note = f"too {word} for the opening by {loose:.0f} mm"
        return Fit(status="too_small", spare_mm=round(-loose, 1), axis=loose_axis, note=note)
    note = (
        "exact fit in the opening" if abs(spare) < 2 else f"fits the opening, {spare:.0f} mm spare"
    )
    return Fit(status="fits", spare_mm=round(spare, 1), axis=axis, note=note)


def fit_to_opening(parts: list[Part], opening: Opening | None) -> list[Part]:
    """assetgen: every candidate's fit against a taped opening; the ones made for it first (the
    least spare first), then the rest by how far off they are. Runs on cached answers too."""
    if opening is None:
        return parts
    for part in parts:
        part.fit = opening_fit(part.dims_mm, opening)
    parts.sort(key=lambda p: (p.fit.status != "fits", abs(p.fit.spare_mm or 0.0)))
    return parts


_MIN_SELLERS_BEFORE_EXPAND = 3


async def expand_sellers(part: Part) -> Part:
    """Lazy seller expansion for the one part a user actually asked sellers for -- SerpApi quota
    is tight, so this is a one-shot attempt per part: `sellers_expanded` latches True even on a
    failure or a no-match, so a part is never retried. At most 2 SerpApi calls: one `shopping`
    search, then `stores` on the first matching listing's immersive token (if any)."""
    if part.sellers_expanded or len(part.sellers) >= _MIN_SELLERS_BEFORE_EXPAND:
        return part
    part.sellers_expanded = True  # set first: any failure below must still count as "tried"

    query = (
        f"{part.manufacturer} {part.model_no}" if part.manufacturer and part.model_no else part.name
    )
    try:
        listings = await sellers.shopping(query)
    except Exception as exc:  # noqa: BLE001 -- keep the sellers we already have
        logger.info("expand_sellers: shopping failed for %r: %s", part.name, exc)
        return part

    matching = [
        listing for listing in listings if _listing_matches_part(part, listing.get("title") or "")
    ]
    token = next(
        (listing["immersive_token"] for listing in matching if listing.get("immersive_token")), None
    )
    new_sellers: list[Seller] = []
    if token:
        try:
            new_sellers = await sellers.stores(token)
        except Exception as exc:  # noqa: BLE001 -- keep the sellers we already have
            logger.info("expand_sellers: stores failed for %r: %s", part.name, exc)

    part.sellers = _merge_sellers(part.sellers, new_sellers)[:_MAX_SELLERS]
    rec = sellers.recommend(part.sellers, "best")
    part.recommended_seller, part.recommendation_reason = rec if rec else (None, None)
    return part


async def refine_sellers(part: Part, sort: Literal["cheapest", "fastest", "best"]) -> Part:
    """Expand sellers lazily (one-shot, see `expand_sellers`), then re-rank `part.sellers` and
    recompute the recommendation for a voice command like "sellers, cheapest first"."""
    part = await expand_sellers(part)
    part.sellers = sellers.rank_sellers(part.sellers, sort)
    rec = sellers.recommend(part.sellers, sort)
    part.recommended_seller, part.recommendation_reason = rec if rec else (None, None)
    return part
