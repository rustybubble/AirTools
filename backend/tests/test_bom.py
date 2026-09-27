"""Fully offline tests for the "what else do I need?" BOM pipeline: llm/sellers are
monkeypatched, no HTTP anywhere."""

from pathlib import Path

import pytest

from server import bom
from server.config import get_settings
from server.models import Dims, Part

SCREW_LISTING = {
    "title": "Gutter screws (100-pack)",
    "source": "Home Depot",
    "price_usd": 12.0,
    "delivery": "Free delivery",
    "rating": 4.7,
    "reviews": 500,
    "product_link": "https://homedepot.example/screws",
    "thumbnail": None,
    "immersive_token": None,
}

SEALANT_LISTING = {
    "title": "Gutter sealant",
    "source": "Ace Hardware",
    "price_usd": 6.5,
    "delivery": "+ $2.00",
    "rating": 4.3,
    "reviews": 80,
    "product_link": "https://ace.example/sealant",
    "thumbnail": None,
    "immersive_token": None,
}


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield tmp_path
    get_settings.cache_clear()


def _part(part_id: str = "gutter-hanger-1", name: str = "Amerimax hidden gutter hanger") -> Part:
    return Part(id=part_id, name=name, dims_mm=Dims(w=127, d=38, h=45))


def _plan(*items: bom.BomItem) -> bom.Plan:
    return bom.Plan(items=list(items))


# --- happy path ----------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_what_else_builds_lines_seller_and_total(monkeypatch):
    part = _part()

    async def fake_extract(role, messages, model_cls):
        assert role == "extract"
        assert model_cls is bom.Plan
        return _plan(
            bom.BomItem(
                name="Gutter screws", qty=2, reason="fasten the hanger", search_query="q-screws"
            ),
            bom.BomItem(
                name="Gutter sealant", qty=1, reason="seal the seam", search_query="q-seal"
            ),
        )

    async def fake_shopping(query):
        return [SCREW_LISTING] if query == "q-screws" else [SEALANT_LISTING]

    monkeypatch.setattr(bom.llm, "extract", fake_extract)
    monkeypatch.setattr(bom.sellers, "shopping", fake_shopping)

    result = await bom.what_else([part], {part.id: 10})

    assert result.id
    assert result.part_ids == [part.id]
    assert len(result.lines) == 2
    screws, sealant = result.lines
    assert (screws.idx, screws.name, screws.qty) == (0, "Gutter screws", 2)
    assert screws.seller.name == "Home Depot"
    assert screws.seller.unit_price_usd == pytest.approx(0.12)  # $12 / 100-pack
    assert sealant.seller.name == "Ace Hardware"
    assert result.total_usd == round(12.0 * 2 + 6.5 * 1, 2)

    # persisted to disk, loadable by id
    loaded = bom.load_bom(result.id)
    assert loaded == result
    assert (Path(get_settings().DATA_DIR) / "boms" / f"{result.id}.json").exists()


@pytest.mark.asyncio
async def test_item_without_a_priced_listing_has_no_seller(monkeypatch):
    part = _part()

    async def fake_extract(role, messages, model_cls):
        return _plan(bom.BomItem(name="Mystery part", qty=1, reason="", search_query="q"))

    async def fake_shopping(query):
        return []

    monkeypatch.setattr(bom.llm, "extract", fake_extract)
    monkeypatch.setattr(bom.sellers, "shopping", fake_shopping)

    result = await bom.what_else([part], {})
    assert len(result.lines) == 1
    assert result.lines[0].seller is None
    assert result.total_usd == 0.0


# --- hard cap of 4 items (SerpApi quota) ----------------------------------------------------


@pytest.mark.asyncio
async def test_hard_caps_at_four_items(monkeypatch):
    part = _part()
    calls = []

    async def fake_extract(role, messages, model_cls):
        return _plan(
            *[
                bom.BomItem(name=f"item{i}", qty=1, reason="", search_query=f"q{i}")
                for i in range(6)
            ]
        )

    async def fake_shopping(query):
        calls.append(query)
        return [SCREW_LISTING]

    monkeypatch.setattr(bom.llm, "extract", fake_extract)
    monkeypatch.setattr(bom.sellers, "shopping", fake_shopping)

    result = await bom.what_else([part], {})
    assert len(result.lines) == 4
    assert len(calls) == 4


# --- whole-result cache ----------------------------------------------------------------------


@pytest.mark.asyncio
async def test_cache_hit_skips_llm_and_sellers(monkeypatch):
    part = _part()

    async def fake_extract(role, messages, model_cls):
        return _plan(bom.BomItem(name="Screws", qty=10, reason="", search_query="screws"))

    async def fake_shopping(query):
        return [SCREW_LISTING]

    monkeypatch.setattr(bom.llm, "extract", fake_extract)
    monkeypatch.setattr(bom.sellers, "shopping", fake_shopping)
    first = await bom.what_else([part], {part.id: 10})

    async def fail_extract(*args, **kwargs):
        raise AssertionError("cache hit must not call the LLM")

    async def fail_shopping(*args, **kwargs):
        raise AssertionError("cache hit must not call sellers.shopping")

    monkeypatch.setattr(bom.llm, "extract", fail_extract)
    monkeypatch.setattr(bom.sellers, "shopping", fail_shopping)
    second = await bom.what_else([part], {part.id: 10})

    assert second == first


# --- OFFLINE ---------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_offline_miss_returns_empty_without_calling_llm(monkeypatch):
    part = _part()

    async def fail_extract(*args, **kwargs):
        raise AssertionError("OFFLINE miss must never call the LLM")

    monkeypatch.setattr(bom.llm, "extract", fail_extract)
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    try:
        result = await bom.what_else([part], {part.id: 8})
    finally:
        get_settings.cache_clear()

    assert result.lines == []
    assert result.total_usd == 0.0


@pytest.mark.asyncio
async def test_offline_cache_hit_still_returns_the_warmed_bom(monkeypatch):
    part = _part()

    async def fake_extract(role, messages, model_cls):
        return _plan(bom.BomItem(name="Screws", qty=8, reason="", search_query="screws"))

    async def fake_shopping(query):
        return [SCREW_LISTING]

    monkeypatch.setattr(bom.llm, "extract", fake_extract)
    monkeypatch.setattr(bom.sellers, "shopping", fake_shopping)
    warmed = await bom.what_else([part], {part.id: 8})

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    try:
        result = await bom.what_else([part], {part.id: 8})
    finally:
        get_settings.cache_clear()

    assert result == warmed


# --- never cache an empty BOM ------------------------------------------------------------------


@pytest.mark.asyncio
async def test_empty_plan_never_cached(monkeypatch):
    part = _part()
    calls = []

    async def fake_extract(role, messages, model_cls):
        calls.append(1)
        return bom.Plan(items=[])

    monkeypatch.setattr(bom.llm, "extract", fake_extract)

    await bom.what_else([part], {})
    await bom.what_else([part], {})
    assert len(calls) == 2  # never cached -- retried both times


@pytest.mark.asyncio
async def test_no_parts_returns_empty_without_calling_llm(monkeypatch):
    async def fail_extract(*args, **kwargs):
        raise AssertionError("must not call the LLM with no placed parts")

    monkeypatch.setattr(bom.llm, "extract", fail_extract)
    result = await bom.what_else([], {})
    assert result == bom.Bom(id="", part_ids=[], lines=[], total_usd=0.0)
