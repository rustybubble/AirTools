""" "What else do I need?" bill of materials (plan §4b.6): the placed parts -> a short list of
install accessories (screws, sealant, end caps, ...), each priced with a seller, all summed into
one total so the headset can add them to the same checkout.

`what_else(parts, counts)`: whole-result cache (like `search.find_parts`) -> one LLM `extract`
call (role "extract") plans up to `_MAX_ITEMS` accessories -> per item, one `sellers.shopping`
call (SerpApi quota is tight, hence the hard cap) -> best priced listing -> `Seller`. Persisted to
disk by id (`data/boms/<id>.json`) so `/checkout` can load it back later without trusting
whatever the client claims a line costs.
"""

import asyncio
import logging
import uuid
from pathlib import Path

from pydantic import BaseModel

from server import cache, llm, search, sellers
from server.config import get_settings
from server.models import Part, Seller

logger = logging.getLogger(__name__)

_MAX_ITEMS = 4  # SerpApi quota is tight: at most one `sellers.shopping` call per item
_MAX_QTY = 50


# --- LLM output schema (role "extract") ----------------------------------------------------
# Strict-schema friendly (see server.llm._strict_schema): every field a scalar, nothing optional
# left dict/Any.


class BomItem(BaseModel):
    name: str
    qty: int = 1
    reason: str = ""
    search_query: str


class Plan(BaseModel):
    items: list[BomItem] = []


# --- output models ---------------------------------------------------------------------------


class BomLine(BaseModel):
    idx: int
    name: str
    qty: int
    reason: str
    seller: Seller | None = None  # None when no priced listing was found for this item


class Bom(BaseModel):
    id: str  # "" for an empty/uncacheable BOM (nothing worth persisting)
    part_ids: list[str]
    lines: list[BomLine] = []
    total_usd: float = 0.0


_SYSTEM_PROMPT = (
    f"You are a hardware quartermaster. Given the parts a customer just placed and how many of "
    f"each, list up to {_MAX_ITEMS} small accessories or consumables actually needed to INSTALL "
    "them -- fasteners, sealant, end caps, brackets, etc. Never list the parts themselves, and "
    "never list anything that isn't needed to install them or already comes with them (a part "
    "sold 'with Screw' needs no screws). Scale qty to the placed count (e.g. "
    "roughly 2 end caps per gutter run, one screw pack usually covers several hangers). "
    "qty counts packages as sold (one box of 100 screws = qty 1), not loose pieces. "
    "search_query must be a short, buyable product search (brand/generic name), not a sentence."
)


def _build_prompt(parts: list[Part], counts: dict[str, int]) -> list[dict]:
    listed = "\n".join(f"- {part.name} x{counts.get(part.id, 1)}" for part in parts)
    user = f"Parts just placed:\n{listed}"
    return [{"role": "system", "content": _SYSTEM_PROMPT}, {"role": "user", "content": user}]


def _pick_seller(listings: list[dict]) -> Seller | None:
    """Best priced listing for a BOM item -- unpriced listings are useless for the cart total,
    so they're dropped before ranking; `sellers.recommend`'s "best" sort then prefers in-stock,
    small-pack, well-rated among what's left (see server/sellers.py's `_sort_key`)."""
    priced = [listing for listing in listings if listing.get("price_usd") is not None]
    if not priced:
        return None
    candidates = [search._seller_from_listing(listing) for listing in priced]
    rec = sellers.recommend(candidates, "best")
    return candidates[rec[0]] if rec else None


async def _build_line(idx: int, item: BomItem) -> BomLine:
    listings = await sellers.shopping(item.search_query)
    qty = min(max(item.qty, 1), _MAX_QTY)  # LLM output: never 0/negative, never a pallet
    return BomLine(
        idx=idx, name=item.name, qty=qty, reason=item.reason, seller=_pick_seller(listings)
    )


def line_cost(line: BomLine) -> float:
    """listing price x qty (qty counts packages as sold, like part checkout) for a priced line,
    else 0.0 -- the one place `checkout.py` and `what_else`'s own total both go through, so they
    can never disagree."""
    if line.seller is None or line.seller.price_usd is None:
        return 0.0
    return round(line.seller.price_usd * line.qty, 2)


def _bom_path(bom_id: str) -> Path:
    return Path(get_settings().DATA_DIR) / "boms" / f"{bom_id}.json"


def _persist(a_bom: Bom) -> None:
    cache.write_json_atomic(_bom_path(a_bom.id), a_bom.model_dump_json(indent=2))


def load_bom(bom_id: str) -> Bom | None:
    path = _bom_path(bom_id)
    if not path.exists():
        return None
    try:
        return Bom.model_validate_json(path.read_text())
    except ValueError:  # pydantic ValidationError / json decode error, both ValueError
        return None


async def what_else(parts: list[Part], counts: dict[str, int]) -> Bom:
    """Whole-result cache -> LLM plan -> per-item seller lookup -> `Bom`. Never raises for a
    single item's seller lookup failing; only the LLM `extract` call itself is allowed to bubble.
    """
    part_ids = sorted({p.id for p in parts})
    if not part_ids:
        return Bom(id="", part_ids=[], lines=[], total_usd=0.0)

    key = {"parts": part_ids, "counts": {pid: counts.get(pid, 1) for pid in part_ids}}
    raw = cache.get("bom", key)
    if raw:
        return Bom.model_validate(raw)

    if get_settings().OFFLINE:
        # No cached answer and the uplink is off-limits: never touch the LLM or SerpApi.
        return Bom(id="", part_ids=part_ids, lines=[], total_usd=0.0)

    plan = await llm.extract("extract", _build_prompt(parts, counts), Plan)
    items = plan.items[:_MAX_ITEMS]
    if not items:
        return Bom(id="", part_ids=part_ids, lines=[], total_usd=0.0)

    built = await asyncio.gather(
        *(_build_line(i, item) for i, item in enumerate(items)), return_exceptions=True
    )
    lines: list[BomLine] = []
    for result in built:
        if isinstance(result, Exception):
            logger.info("what_else: seller lookup failed: %s", result)
            continue
        lines.append(result)
    if not lines:  # an empty BOM is worth retrying, not remembering
        return Bom(id="", part_ids=part_ids, lines=[], total_usd=0.0)

    total = round(sum(line_cost(line) for line in lines), 2)
    a_bom = Bom(id=f"bom-{uuid.uuid4().hex[:12]}", part_ids=part_ids, lines=lines, total_usd=total)
    cache.put("bom", key, a_bom.model_dump(mode="json"))
    _persist(a_bom)
    return a_bom
