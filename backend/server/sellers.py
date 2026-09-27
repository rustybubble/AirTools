"""SerpApi: google_shopping -> google_immersive_product stores, home_depot -> home_depot_product
dims + ETA. Every call goes through `cache.cached`; the api_key never enters the cache key or a
log line.
"""

import logging
import math
import re
from datetime import UTC, date, datetime, timedelta
from typing import Any, Literal

import httpx

from server.cache import cached
from server.keys import KeyPool, KeysExhausted
from server.models import Dims, Seller

logger = logging.getLogger(__name__)

BASE_URL = "https://serpapi.com/search.json"


class SerpApiError(RuntimeError):
    """A SerpApi request failed. Message carries only {engine, status} -- never the request
    URL, which carries `api_key` as a query param (see `_serpapi_get`)."""

    def __init__(self, message: str, status: int | None = None):
        super().__init__(message)
        self.status = status


# 429 = out of monthly searches or over the hourly throughput cap: rotate to the next key and
# retry this one after an hour (a still-empty account just costs one quick 429).
SERPAPI_KEYS = KeyPool(
    "SERP_API_KEY",
    is_limited=lambda exc: isinstance(exc, SerpApiError) and exc.status == 429,
    cooldown_s=3600,
)


_WEEKDAYS = {"mon": 0, "tue": 1, "wed": 2, "thu": 3, "fri": 4, "sat": 5, "sun": 6}
_MONTHS = {
    "jan": 1,
    "feb": 2,
    "mar": 3,
    "apr": 4,
    "may": 5,
    "jun": 6,
    "jul": 7,
    "aug": 8,
    "sep": 9,
    "oct": 10,
    "nov": 11,
    "dec": 12,
}


async def _serpapi_get(params: dict) -> dict | None:
    """GET serpapi.com with `params`, cached on {engine, params} (api_key excluded).

    Never lets an httpx error escape as-is: `httpx.HTTPStatusError.__str__` embeds the full
    request URL, and `api_key` rides in that URL's query string -- callers only ever see
    `SerpApiError` (engine + status code), and `from None` keeps the raw exception (and its
    traceback, which `logger.exception` would otherwise print in full) out of the log too.
    """
    engine = params.get("engine")
    if not SERPAPI_KEYS.keys():
        logger.warning("SERP_API_KEY missing; skipping serpapi engine=%s", engine)
        return None

    async def fetch_with(api_key: str | None) -> dict:
        async with httpx.AsyncClient(timeout=20) as client:
            try:
                resp = await client.get(BASE_URL, params={**params, "api_key": api_key})
                resp.raise_for_status()
            except httpx.HTTPError as exc:
                status = (
                    exc.response.status_code if isinstance(exc, httpx.HTTPStatusError) else None
                )
                raise SerpApiError(
                    f"serpapi request failed: engine={engine} status={status}", status
                ) from None
            return resp.json()

    async def fetch() -> dict:
        try:
            return await SERPAPI_KEYS.call(fetch_with)
        except KeysExhausted:
            raise SerpApiError(
                f"serpapi request failed: engine={engine} status=429 (all keys rate limited)", 429
            ) from None

    return await cached(namespace="serpapi", key={"engine": engine, "params": params}, fn=fetch)


async def shopping(query: str) -> list[dict]:
    """Condensed google_shopping listings."""
    data = await _serpapi_get({"engine": "google_shopping", "q": query, "gl": "us", "hl": "en"})
    if not data:
        return []
    return [
        {
            "title": r.get("title"),
            "source": r.get("source"),
            "price_usd": r.get("extracted_price"),
            "delivery": r.get("delivery"),
            "rating": r.get("rating"),
            "reviews": r.get("reviews"),
            "product_link": r.get("product_link"),
            "thumbnail": r.get("thumbnail"),
            "immersive_token": r.get("immersive_product_page_token"),
        }
        for r in data.get("shopping_results", [])
    ]


def parse_shipping(text: str | None) -> float | None:
    """ "Free" -> 0.0, "+ $20.00" -> 20.0, None/unparseable -> None."""
    if not text:
        return None
    if text.strip().lower() == "free":
        return 0.0
    m = re.search(r"[\d,]+\.?\d*", text)
    return float(m.group(0).replace(",", "")) if m else None


def _to_date(
    token: str,
    today: date,
    weekday_ref: date | None = None,
    month_ref: int | None = None,
    year_ref: int | None = None,
) -> date | None:
    token = token.strip().strip(",")
    if "," in token:
        token = token.split(",")[-1].strip()
    low = token.lower()
    if low == "today":
        return today
    if low[:3] in _WEEKDAYS:
        base = weekday_ref or today
        delta = (_WEEKDAYS[low[:3]] - base.weekday()) % 7
        return base + timedelta(days=delta)
    m = re.match(r"^([a-zA-Z]{3,9})\.?\s+(\d{1,2})$", token)
    if m:
        month = _MONTHS.get(m.group(1).lower()[:3])
        if month:
            year = today.year
            d = date(year, month, int(m.group(2)))
            if d < today - timedelta(days=180):
                d = date(year + 1, month, int(m.group(2)))
            return d
    if re.match(r"^\d{1,2}$", token) and month_ref:
        return date(year_ref or today.year, month_ref, int(token))
    return None


def eta_days(eta: str | None, today: date) -> int | None:
    """Days until the latest date of a Home Depot arrival text ("Sep 26" / "Sep 26 - Sep 29")."""
    if not eta:
        return None
    d = _to_date(eta.split(" - ")[-1], today)
    return (d - today).days if d else None


def parse_eta(text: str, today: date) -> tuple[str, int] | None:
    """Pull an ETA out of a free-text `details_and_offers` line.

    Handles "Delivery between Sep 25 - Oct 1 $20" (en-dash or hyphen, day-name or month-day
    on either side, trailing "$amount" stripped), "Free delivery by Fri", "Get it by Sep 26".
    Returns (human text, days from `today`) or None if no ETA is present.
    """
    if not text:
        return None
    norm = text.replace(" ", " ").replace("–", "-").replace("—", "-")
    norm = re.sub(r"\s*\$[\d,]+(\.\d+)?\s*$", "", norm).strip()

    m = re.search(r"between\s+(.+?)\s*-\s*(.+)$", norm, re.IGNORECASE)
    if m:
        left, right = m.group(1).strip(), m.group(2).strip()
        d1 = _to_date(left, today)
        d2 = _to_date(
            right,
            today,
            weekday_ref=d1,
            month_ref=d1.month if d1 else None,
            year_ref=d1.year if d1 else None,
        )
        if d2:
            return f"{left} - {right}", (d2 - today).days
        return None

    m = re.search(r"(?:get it by|delivery by|by)\s+(.+)$", norm, re.IGNORECASE)
    if m:
        token = m.group(1).strip()
        d = _to_date(token, today)
        if d:
            return token, (d - today).days
    return None


async def stores(immersive_token: str) -> list[Seller]:
    """google_immersive_product stores for a shopping result's immersive token."""
    data = await _serpapi_get(
        {"engine": "google_immersive_product", "page_token": immersive_token, "more_stores": 1}
    )
    if not data:
        return []
    today = datetime.now(UTC).date()
    out = []
    for s in data.get("product_results", {}).get("stores", []):
        details = s.get("details_and_offers") or []
        eta, eta_days = None, None
        for line in details:
            parsed = parse_eta(line, today)
            if parsed:
                eta, eta_days = parsed
                break
        shipping_text = s.get("shipping")
        title = s.get("title")
        pack_qty, unit_price = pack_pricing(title, s.get("extracted_price"))
        out.append(
            Seller(
                name=s.get("name", ""),
                title=title,
                price_usd=s.get("extracted_price"),
                pack_qty=pack_qty,
                unit_price_usd=unit_price,
                shipping_usd=parse_shipping(shipping_text),
                shipping=shipping_text,
                total_usd=s.get("extracted_total"),
                eta=eta,
                eta_days=eta_days,
                rating=s.get("rating"),
                reviews=s.get("reviews"),
                in_stock="in stock" in " ".join(details).lower(),
                url=s.get("link"),
                verified=True,
            )
        )
    return out


# Long, unambiguous keywords take a hyphen/space separator; short ones (pk/ct/pcs) collide with
# SKU tails like "21-030pk" or "29016PK", so they require a real space and a short (<=3 digit)
# count -- a plain size number never sits next to any of these words, so "5 in." / "#8 x 3/8-in"
# never match at all.
_PACK_PATTERNS = [
    re.compile(r"\bbox\s+of\s+(\d{1,4})\b", re.IGNORECASE),
    re.compile(r"\bcase\s+of\s+(\d{1,4})\b", re.IGNORECASE),
    re.compile(r"\bpack\s+of\s+(\d{1,4})\b", re.IGNORECASE),
    re.compile(r"\b(\d{1,4})\s*/\s*box\b", re.IGNORECASE),
    re.compile(r"\b(\d{1,4})[\s-]*pack\b", re.IGNORECASE),
    re.compile(r"\b(\d{1,4})\s*count\b", re.IGNORECASE),
    re.compile(r"\b(\d{1,3})\s+pk\b", re.IGNORECASE),
    re.compile(r"\b(\d{1,3})\s+ct\b", re.IGNORECASE),
    re.compile(r"\b(\d{1,3})\s*pcs?\b", re.IGNORECASE),
]


def parse_pack_qty(title: str | None) -> int:
    """How many units a listing title is selling, e.g. "(50-Pack)" -> 50, "Box of 100" -> 100,
    "100/Box" -> 100, "Case of 50" -> 50, "50 Count"/"50 ct" -> 50, "Pack of 10" -> 10,
    "10 pcs" -> 10. A plain size ("5 in.", "5-in", "#8 x 3/8-in") never matches -> 1."""
    if not title:
        return 1
    for pattern in _PACK_PATTERNS:
        m = pattern.search(title)
        if m:
            qty = int(m.group(1))
            if qty > 0:
                return qty
    return 1


def pack_pricing(title: str | None, price: float | None) -> tuple[int, float | None]:
    """(pack_qty, unit_price_usd) for a listing/store title and its total price."""
    qty = parse_pack_qty(title)
    return qty, (price / qty if price is not None else None)


def unit_cost(s: Seller) -> float:
    """Effective landed cost per unit, for ranking/dedupe -- total_usd (price+shipping for the
    whole pack) is the most complete source when known; otherwise unit_price_usd plus shipping
    spread over the pack; otherwise the raw price. `math.inf` when nothing is known."""
    pack_qty = max(s.pack_qty, 1)
    if s.total_usd is not None:
        return s.total_usd / pack_qty
    if s.unit_price_usd is not None:
        return s.unit_price_usd + (s.shipping_usd or 0.0) / pack_qty
    if s.price_usd is not None:
        return s.price_usd / pack_qty
    return math.inf


def _parse_inches(value: str) -> float | None:
    """ "5 in" -> 5.0, "0.75 in" -> 0.75, "1-1/2 in" -> 1.5."""
    s = value.strip().lower().replace("in.", "").replace("in", "").strip()
    if not s:
        return None
    m = re.match(r"^(\d+)-(\d+)/(\d+)$", s)
    if m:
        whole, num, den = (int(g) for g in m.groups())
        return whole + num / den
    m = re.match(r"^(\d+)/(\d+)$", s)
    if m:
        num, den = (int(g) for g in m.groups())
        return num / den
    try:
        return float(s)
    except ValueError:
        return None


# Sizes printed in a listing title, for search's last-resort dims ("12 in. x 8 in.", "3-3/4 in.",
# "3 in. (76 mm)", "36 in. W x 24 in. D x 34.5 in. H"). A number counts only with a length unit
# (a bare "in" only before x/(/,/end, so "2 in 1" isn't a size); an "x" chain shares its last unit.
_SIZE_NUM = r"\d+[-\s]\d+/\d+|\d+/\d+|\d+(?:\.\d+)?"
_SIZE_UNIT = r"mm\b|cm\b|in\.|inch(?:es)?\b|in\b(?=\s*(?:[x×(),]|$))|\"|”|ft\b\.?|feet\b"
_SIZE_ITEM_RE = re.compile(
    rf"(?P<num>{_SIZE_NUM})\s*-?\s*(?P<unit>{_SIZE_UNIT})?"
    r"(?:\s*\(\s*(?P<metric>\d+(?:\.\d+)?)\s*(?P<metric_unit>mm|cm)\s*\))?"
    r"(?:\s*(?P<axis>(?-i:[WDH]))\b\.?)?",
    re.IGNORECASE,
)
_SIZE_ITEM = re.sub(r"\?P<\w+>", "?:", _SIZE_ITEM_RE.pattern)  # same item, no named groups
_SIZE_CHAIN_RE = re.compile(rf"{_SIZE_ITEM}(?:\s*[x×]\s*{_SIZE_ITEM}){{0,2}}", re.IGNORECASE)
# Numbers that aren't the part's own size: screw spacing ("3 in. Center-to-Center", "3 Inch
# Hole Center"), a radius, a hinge's overlay or cup, a screw gauge ("#8 x 1 in.").
_NOT_SIZE_AFTER = re.compile(
    r"^\s*[-,]?\s*(?:cent(?:er|re)[-\s]*to[-\s]*cent(?:er|re)|cent(?:er|re)s\b|c-?c\b|"
    r"on\s+cent|o\.c\.|hole|(?:corner\s+)?radius|overlay|cup|bore)",
    re.IGNORECASE,
)
_NOT_SIZE_BEFORE = re.compile(
    r"(?:#|cent(?:er|re)[-\s]*to[-\s]*cent(?:er|re)\s*:?|hole\s+(?:spacing|cent(?:er|re))\s*:?)\s*$",
    re.IGNORECASE,
)
_ALTERNATIVE_SIZES = re.compile(
    r"\d\s*(?:in\.|mm|\")?\s+or\s+\d|\d{2,}\s*/\s*\d{2,}\s*mm", re.IGNORECASE
)
_TO_MM = {"mm": 1.0, "cm": 10.0, "ft": 304.8, "fe": 304.8}  # by unit[:2]; anything else is inches


def parse_title_sizes(title: str | None) -> list[tuple[float, str | None]]:
    """The one size mention in a title as [(mm, axis "w"/"d"/"h" or None), ...] in title order,
    e.g. "10 in. x12 in. Shelf Bracket" -> [(254, None), (304.8, None)], "3 in. (76 mm) Pull"
    -> [(76, None)] (the printed metric wins). Conservative: [] when there's no unit-tagged size,
    when sizes are alternatives ("3 or 3-3/4 in. (76/96 mm)"), or when the title has more than one
    separate size mention (which one is the part?); centre-to-centre spacing, radii, overlays and
    screw gauges are never sizes."""
    if not title or _ALTERNATIVE_SIZES.search(title):
        return []
    mentions = []
    for chain in _SIZE_CHAIN_RE.finditer(title):
        items = list(_SIZE_ITEM_RE.finditer(chain.group(0)))
        last_unit = items[-1].group("unit") or items[-1].group("metric_unit")
        if not last_unit:
            continue  # "(25-Pack)", "110-Degree", "2 x 4": no length unit
        if _NOT_SIZE_AFTER.match(title[chain.end() :]) or _NOT_SIZE_BEFORE.search(
            title[: chain.start()]
        ):
            continue
        sizes = []
        for item in items:
            if item.group("metric"):
                mm = float(item.group("metric")) * _TO_MM[item.group("metric_unit").lower()]
            else:
                unit = (item.group("unit") or last_unit).lower()
                value = _parse_inches(item.group("num").replace(" ", "-"))
                if value is None:
                    break
                mm = value * _TO_MM.get(unit[:2], 25.4)
            axis = item.group("axis")
            sizes.append((round(mm, 2), axis.lower() if axis else None))
        else:
            mentions.append(sizes)
    return mentions[0] if len(mentions) == 1 else []


# assetgen: the depth of a window unit that publishes only its width and height: the standard
# jamb depth of a vinyl replacement window (Home Depot's "Jamb Depth (in.)" when it's there).
WINDOW_DEPTH_IN = 3.25


def _parse_dims(specs: list[dict]) -> Dims | None:
    for group in specs:
        if group.get("key") != "Dimensions":
            continue
        w = d = h = length = None
        pull = False
        for item in group.get("value", []):
            name = (item.get("name") or "").lower()
            if "window" in name:
                continue  # "Window opening min/max width/height" is a fit range, not a dim
            if "rough opening" in name or " x " in name:
                # assetgen: a window's / door's rough opening is the hole it goes in, not its
                # size; "Width (in.) x Height (in.)" repeats the two sizes as one string
                continue
            inches = _parse_inches(item.get("value", ""))
            if inches is None:
                continue
            # Directional names ("Sink Left to Right Length") say which axis outright.
            if "left to right" in name:
                w = inches
            elif "top to bottom" in name:
                h = inches
            elif "front to back" in name:
                d = inches
            # Cabinet hardware has no Width/Depth/Height at all: a knob publishes its diameter
            # and projection (off the door), a pull its length and projection -- its
            # "Center to Center Measurement (mm)" is the screw spacing, not a size.
            elif "knob diameter" in name:
                w = h = inches
            elif "projection" in name:
                d = inches
            elif "pull length" in name:
                w, pull = inches, True
            elif "width" in name:
                w = inches
            elif "depth" in name:
                d = inches
            elif "height" in name:
                h = inches
            elif "length" in name:
                length = inches
        if pull and h is None and d is not None:
            # A pull's thickness isn't published: use its projection (a bar pull is thinner than
            # it projects, so its proxy box comes out a little tall rather than too thin).
            h = d
        if w is not None and d is not None and h is not None:
            return Dims(w=w * 25.4, d=d * 25.4, h=h * 25.4)
        known = [v for v in (w, d, h) if v is not None]
        if len(known) == 2 and length is not None:
            # Flat goods ("Panel Width/Height/length", labels often swapped): mounted flush, so
            # the thinnest side is depth off the mounting surface, the longest runs vertical.
            thin, mid, long_ = sorted([*known, length])
            return Dims(w=mid * 25.4, d=thin * 25.4, h=long_ * 25.4)
    return None


def _first_inches(items: list[tuple[str, str]], *starts: str) -> float | None:
    """The first spec (lower-case name, value) whose name starts with one of `starts`, in inches."""
    for name, value in items:
        if name.startswith(starts):
            inches = _parse_inches(value)
            if inches:
                return inches
    return None


def _window_dims(specs: list[dict]) -> tuple[Dims | None, bool]:
    """assetgen: a window or door unit (it publishes a rough opening) whose width and height are
    published but not its depth: (dims with the jamb depth, else WINDOW_DEPTH_IN, and whether the
    depth is that typical one). (None, False) for anything else."""
    for group in specs:
        if group.get("key") != "Dimensions":
            continue
        items = [
            ((i.get("name") or "").lower(), i.get("value", "")) for i in group.get("value", [])
        ]
        if not any("rough opening" in name for name, _ in items):
            continue

        w = _first_inches(items, "product width", "actual width")
        h = _first_inches(items, "product height", "actual height")
        d = _first_inches(items, "jamb depth", "product depth", "frame depth")
        if w and h:
            typical = d is None
            depth = WINDOW_DEPTH_IN if typical else d
            return Dims(w=w * 25.4, d=depth * 25.4, h=h * 25.4), typical
    return None, False


def _parse_fit_range(specs: list[dict]) -> dict | None:
    """Published window-opening min/max width, e.g. a window AC's "Window opening minimum/
    maximum width (in.)" spec fields -> {"min": mm, "max": mm}; else a sink's "Minimum Cabinet
    Size (in.)" as an open-ended range. Distinct from the plain
    "Product Width" field `_parse_dims` reads, so neither steals the other's number."""
    for group in specs:
        if group.get("key") != "Dimensions":
            continue
        lo = hi = None
        for item in group.get("value", []):
            name = (item.get("name") or "").lower()
            if "window" not in name or "width" not in name:
                continue
            inches = _parse_inches(item.get("value", ""))
            if inches is None:
                continue
            if "minimum" in name or "min" in name:
                lo = inches
            elif "maximum" in name or "max" in name:
                hi = inches
        if lo is not None and hi is not None:
            return {"min": round(lo * 25.4, 1), "max": round(hi * 25.4, 1)}
    for group in specs:
        for item in group.get("value", []):
            if "minimum cabinet size" in (item.get("name") or "").lower():
                inches = _parse_inches(item.get("value", ""))
                if inches is not None:
                    # A sink's minimum base cabinet width; any wider cabinet still takes it.
                    # ponytail: open-ended max, the fit note then reports spare above the min.
                    return {"min": round(inches * 25.4, 1), "max": 10_000.0}
    return None


def _parse_details(specs: list[dict]) -> tuple[str | None, str | None]:
    color = material = None
    for group in specs:
        if group.get("key") != "Details":
            continue
        for item in group.get("value", []):
            name = (item.get("name") or "").lower()
            if "color" in name and color is None:
                color = item.get("value")
            elif "material" in name and material is None:
                material = item.get("value")
    return color, material


def _hd_eta(fulfillment: dict) -> str | None:
    for opt in (fulfillment or {}).get("options", []):
        if opt.get("type") == "Ship to Home":
            times = opt.get("arrival_time") or []
            uniq = list(dict.fromkeys(times))
            if not uniq:
                return None
            return uniq[-1] if len(uniq) == 1 else " - ".join(uniq)
    return None


def _words(text: str | None) -> set[str]:
    return set(re.findall(r"[a-z0-9]+", (text or "").lower())) - {"in", "with", "for", "the", "and"}


def _best_product(products: list[dict], query: str) -> dict | None:
    """Home Depot search is fuzzy (a model number can return paint). Keep the product sharing the
    most words with the query; exact model number wins; fewer than 2 shared words = no match."""
    q = _words(query)
    best, best_score = None, 1
    for p in products:
        if (p.get("model_number") or "").lower() == query.strip().lower():
            return p
        score = len(q & _words(f"{p.get('brand')} {p.get('model_number')} {p.get('title')}"))
        if score > best_score:
            best, best_score = p, score
    return best


async def home_depot_search(query: str) -> list[dict]:
    """Condensed `home_depot` engine results, for candidate discovery (one cached SerpApi call).

    Kept separate from `home_depot_product` so a caller that already knows which listing is the
    right product (the LLM tied it via `listing_indexes`) can skip straight to the product call
    instead of paying for a search it doesn't need.
    """
    data = await _serpapi_get({"engine": "home_depot", "q": query})
    if not data:
        return []
    out = []
    for p in data.get("products") or []:
        thumbnails = p.get("thumbnails") or []
        out.append(
            {
                "title": p.get("title"),
                "brand": p.get("brand"),
                "model_number": p.get("model_number"),
                "price_usd": p.get("price"),
                "product_id": p.get("product_id"),
                "link": p.get("link"),
                "thumbnail": thumbnails[0][-1] if thumbnails and thumbnails[0] else None,
                "rating": p.get("rating"),
                "reviews": p.get("reviews"),
            }
        )
    return out


async def home_depot_product(product_id: str) -> dict | None:
    """`home_depot_product` engine for a known `product_id` -> dims + price + ETA etc."""
    data = await _serpapi_get({"engine": "home_depot_product", "product_id": product_id})
    if not data:
        return None
    pr = data.get("product_results") or {}
    if not pr:
        return None

    specs = pr.get("specifications") or []
    color, material = _parse_details(specs)
    images = pr.get("images") or []
    image = images[0][-1] if images and images[0] else None
    dims, depth_typical = _parse_dims(specs), False
    if dims is None:  # assetgen: a window / door with no published depth
        dims, depth_typical = _window_dims(specs)

    return {
        "dims": dims,
        "depth_typical": depth_typical,
        "fit_range_mm": _parse_fit_range(specs),
        "title": pr.get("title"),
        "brand": (pr.get("brand") or {}).get("name"),
        "model_number": pr.get("model_number"),
        "image": image,
        "link": pr.get("link"),
        "price": pr.get("price"),
        "rating": pr.get("rating"),
        "reviews": pr.get("reviews"),
        "eta": _hd_eta(pr.get("fulfillment") or {}),
        "color": color,
        "material": material,
    }


async def home_depot_lookup(query_or_model_no: str) -> dict | None:
    """home_depot search -> best-matching product_id -> home_depot_product.

    Composition of `home_depot_search` + `_best_product` + `home_depot_product`, kept for
    callers that only have a name/model number to go on (no listing already tied to a
    product_id).
    """
    products = await home_depot_search(query_or_model_no)
    product = _best_product(products, query_or_model_no)
    product_id = product and product.get("product_id")
    if not product_id:
        return None
    return await home_depot_product(product_id)


def _sort_key(s: Seller, sort: str) -> tuple[Any, ...]:
    if sort == "cheapest":
        return (unit_cost(s), s.name)
    if sort == "fastest":
        return (s.eta_days if s.eta_days is not None else math.inf,)
    # "best": in-stock first, then a single unit / small pack over a bulk case (we don't know
    # how many the caller wants), then rating weighted by review volume (a lone 5-star review
    # shouldn't outrank a 4.6 with 3700 reviews), ties broken by name for determinism.
    weight = (s.rating or 0) * math.log1p(s.reviews or 0)
    small_pack = 0 if s.pack_qty <= 4 else 1
    return (0 if s.in_stock else 1, small_pack, -weight, s.name)


def rank_sellers(
    sellers: list[Seller], sort: Literal["cheapest", "fastest", "best"]
) -> list[Seller]:
    return sorted(sellers, key=lambda s: _sort_key(s, sort))


def _reason(s: Seller, sort: str) -> str:
    if sort == "cheapest":
        stock = "in stock" if s.in_stock else "stock unknown"
        if s.pack_qty > 1 and s.unit_price_usd is not None:
            total = f"${s.total_usd:.2f}" if s.total_usd is not None else f"${s.price_usd:.2f}"
            return (
                f"cheapest per unit {stock}, ${s.unit_price_usd:.2f} each in a pack of "
                f"{s.pack_qty} ({total} total)"
            )
        price = f"${s.total_usd:.2f}" if s.total_usd is not None else "price unknown"
        ship = (
            "with free delivery"
            if s.shipping_usd == 0
            else (f"+ ${s.shipping_usd:.2f} shipping" if s.shipping_usd else "")
        )
        return f"cheapest {stock}, " + " ".join(p for p in (price, ship) if p)
    if sort == "fastest":
        return f"arrives soonest ({s.eta})" if s.eta else "arrives soonest (eta unknown)"
    stock = "in stock" if s.in_stock else "stock unknown"
    rating = f", {s.rating:.1f}★ ({s.reviews} reviews)" if s.rating else ""
    return f"best match, {stock}{rating}"


def recommend(
    sellers: list[Seller], sort: Literal["cheapest", "fastest", "best"]
) -> tuple[int, str] | None:
    if not sellers:
        return None
    idx = min(range(len(sellers)), key=lambda i: _sort_key(sellers[i], sort))
    return idx, _reason(sellers[idx], sort)
