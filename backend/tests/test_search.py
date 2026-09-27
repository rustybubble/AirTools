"""Fully offline tests for the find-parts pipeline: sellers/web/llm are all monkeypatched, no
HTTP anywhere (respx isn't even imported)."""

import json
from pathlib import Path

import httpx
import pytest

from server import search
from server.config import get_settings
from server.models import Dims, Measurement, Part, SearchRequest, Seller

FIXTURES = Path(__file__).parent / "fixtures" / "search"


@pytest.fixture(autouse=True)
def _cache_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _req(**kw):
    defaults = {
        "query": "5 inch K-style gutter hanger",
        "measurement": Measurement(label="gutter profile", value_m=0.165, axis="w"),
        "max_candidates": 3,
    }
    defaults.update(kw)
    return SearchRequest(**defaults)


HD_LISTING = {
    "title": "Amerimax 150121 5 in. K-style hidden gutter hanger",
    "source": "The Home Depot",
    "price_usd": 4.27,
    "delivery": "Free delivery",
    "rating": 4.6,
    "reviews": 120,
    "product_link": "https://www.homedepot.com/p/amerimax-hanger/123",
    "thumbnail": "https://img.example.com/hd-thumb.jpg",
    "immersive_token": "tok-hd",
}

OTHER_LISTING = {
    "title": "Spectra 5 in K style gutter hanger",
    "source": "Spectra Metals",
    "price_usd": 5.10,
    "delivery": "+ $3.00",
    "rating": 4.2,
    "reviews": 30,
    "product_link": "https://spectra.example.com/hanger",
    "thumbnail": "https://img.example.com/spectra-thumb.jpg",
    "immersive_token": None,
}


def _candidate(**kw):
    defaults = {
        "name": "Amerimax 5 in. K-style hidden gutter hanger",
        "manufacturer": "Amerimax",
        "model_no": "150121",
        "dims_mm": search.DimsMm(w=127.0, d=38.0, h=45.0),
        "dims_evidence": "5 in. W x 1.5 in. D x 1.75 in. H",
        "listing_indexes": [1],
        "spec_url": None,
    }
    defaults.update(kw)
    return search.Candidate(**defaults)


def _plan(candidates, measure_axis="w"):
    return search.Plan(candidates=candidates, measure_axis=measure_axis, note="")


async def _no_stores(_token):
    return []


async def _no_hd(_query):
    return None


async def _dead_url_alive(_url):
    return True


# --- compute_fit -------------------------------------------------------------------------


def test_compute_fit_range_fits():
    dims = Dims(w=760, d=400, h=350)
    meas = Measurement(value_m=0.80, axis="w")
    fit = search.compute_fit(dims, meas, "w", search.FitRangeMm(min=584.0, max=914.0))
    assert fit.status == "fits"
    assert "spare" in fit.note


def test_compute_fit_range_too_big():
    dims = Dims(w=760, d=400, h=350)
    meas = Measurement(value_m=0.40, axis="w")  # narrower than fit_range.min
    fit = search.compute_fit(dims, meas, "w", search.FitRangeMm(min=584.0, max=914.0))
    assert fit.status == "too_big"
    assert fit.spare_mm < 0


def test_compute_fit_range_too_small():
    dims = Dims(w=760, d=400, h=350)
    meas = Measurement(value_m=1.20, axis="w")  # 1200mm wider than fit_range.max
    fit = search.compute_fit(dims, meas, "w", search.FitRangeMm(min=584.0, max=914.0))
    assert fit.status == "too_small"
    assert fit.spare_mm < 0
    assert "too small for the opening by 286 mm" in fit.note


def test_compute_fit_range_tolerates_headset_error():
    dims = Dims(w=19, d=127, h=25)
    meas = Measurement(value_m=0.126, axis="w")  # 5 in. gutter read 1 mm short
    fit = search.compute_fit(dims, meas, "w", search.FitRangeMm(min=127.0, max=127.0))
    assert (fit.status, fit.spare_mm, fit.note) == ("fits", 0.0, "exact fit")
    six_inch = Measurement(value_m=0.152, axis="w")
    assert (
        search.compute_fit(dims, six_inch, "w", search.FitRangeMm(min=127.0, max=127.0)).status
        == "too_small"
    )


def test_compute_fit_axis_fits():
    dims = Dims(w=127, d=38, h=45)
    meas = Measurement(value_m=0.165, axis="w")  # 165mm - 127mm = 38mm spare
    fit = search.compute_fit(dims, meas, "w", None)
    assert fit.status == "fits"
    assert fit.spare_mm == pytest.approx(38.0, abs=0.5)
    assert "38 mm spare" in fit.note


def test_compute_fit_axis_exact_fit():
    dims = Dims(w=127, d=38, h=45)
    meas = Measurement(value_m=0.1275, axis="w")  # 0.5mm spare -> "exact fit", not "0 mm spare"
    fit = search.compute_fit(dims, meas, "w", None)
    assert fit.status == "fits"
    assert fit.note == "exact fit"


def test_compute_fit_axis_too_big():
    dims = Dims(w=127, d=38, h=45)
    meas = Measurement(value_m=0.115, axis="w")  # 115mm < 127mm -> too wide by 12mm
    fit = search.compute_fit(dims, meas, "w", None)
    assert fit.status == "too_big"
    assert "too wide by 12 mm" in fit.note


def test_compute_fit_length_axis_fits_trivially():
    dims = Dims(w=127, d=38, h=45)
    meas = Measurement(value_m=4.2, axis="length")
    fit = search.compute_fit(dims, meas, "length", None)
    assert fit.status == "fits"
    assert fit.axis == "length"


def test_compute_fit_no_measurement_is_unknown():
    dims = Dims(w=127, d=38, h=45)
    fit = search.compute_fit(dims, None, None, None)
    assert fit.status == "unknown"


# --- find_parts: happy path ----------------------------------------------------------------


@pytest.mark.asyncio
async def test_find_parts_happy_path(monkeypatch):
    async def fake_shopping(query):
        return [HD_LISTING, OTHER_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_stores(token):
        assert token == "tok-hd"
        return [
            Seller(
                name="Home Depot",
                price_usd=4.27,
                shipping_usd=0.0,
                total_usd=4.27,
                rating=4.6,
                reviews=120,
                in_stock=True,
                url=HD_LISTING["product_link"],
                verified=True,
            ),
            Seller(
                name="Spectra Metals",
                price_usd=5.10,
                shipping_usd=3.0,
                total_usd=8.10,
                rating=4.2,
                reviews=30,
                in_stock=True,
                url=OTHER_LISTING["product_link"],
                verified=True,
            ),
        ]

    async def fake_extract(role, messages, model_cls):
        assert role == "extract"
        return _plan(
            [_candidate(listing_indexes=[1], spec_url="https://amerimax.example.com/spec")]
        )

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", fake_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req())

    assert len(parts) == 1
    part = parts[0]
    assert isinstance(part, Part)
    assert part.dims_mm == Dims(w=127, d=38, h=45)
    assert part.dims_source == "llm"
    assert len(part.sellers) == 2
    assert part.recommended_seller is not None
    assert part.recommendation_reason
    assert part.fit.status == "fits"
    assert part.spec_url == "https://amerimax.example.com/spec"
    assert part.cached is False
    assert part.asset.status == "pending"


# --- Home Depot dims override -------------------------------------------------------------


@pytest.mark.asyncio
async def test_home_depot_dims_override_llm_dims(monkeypatch):
    hd_dims = Dims(w=200.0, d=50.0, h=60.0)  # deliberately different from the LLM's dims

    async def fake_shopping(query):
        return [HD_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_home_depot_lookup(query_or_model_no):
        assert query_or_model_no == "150121"
        return {
            "dims": hd_dims,
            "title": "Amerimax 150121 K-style hidden gutter hanger",
            "brand": "Amerimax",
            "model_number": "150121",
            "image": "https://img.example.com/hd-full.jpg",
            "link": "https://www.homedepot.com/p/amerimax-150121/999",
            "price": 3.18,
            "rating": 4.6,
            "reviews": 3700,
            "eta": "Sep 26",
        }

    async def fake_extract(role, messages, model_cls):
        return _plan([_candidate(listing_indexes=[1])])  # LLM dims_mm = 127/38/45

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", fake_home_depot_lookup)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req())

    assert len(parts) == 1
    part = parts[0]
    assert part.dims_mm == hd_dims
    assert part.dims_source == "home_depot"
    assert part.image_url == "https://img.example.com/hd-full.jpg"
    # the matched Home Depot product itself becomes a verified seller
    hd_seller = next(s for s in part.sellers if s.name == "Home Depot")
    assert hd_seller.eta == "Sep 26"
    assert hd_seller.price_usd == 3.18
    assert hd_seller.pack_qty == 1
    assert hd_seller.unit_price_usd == pytest.approx(3.18)
    assert hd_seller.reviews == 3700
    assert hd_seller.verified is True


@pytest.mark.asyncio
async def test_home_depot_fit_range_wins_over_llm_when_candidate_has_none(monkeypatch):
    """A published Home Depot fit range (e.g. a window AC's min/max window-opening width) is
    used for `compute_fit` -- and exposed on the Part -- when the candidate's own fit_range_mm
    is null, per docs/design.md's "published spec beats LLM"."""

    async def fake_shopping(query):
        return [HD_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_home_depot_lookup(query_or_model_no):
        return {
            "dims": Dims(w=457.2, d=396.9, h=304.8),
            "fit_range_mm": {"min": 558.8, "max": 914.4},
            "title": "Windmill 6,000 BTU Window Air Conditioner",
            "brand": "Amerimax",
            "model_number": "150121",
            "image": None,
            "link": "https://www.homedepot.com/p/windmill/319772228",
            "price": 299.0,
            "rating": 4.4,
            "reviews": 4007,
            "eta": None,
        }

    async def fake_extract(role, messages, model_cls):
        # LLM candidate published no fit range of its own -- null, not a guess.
        return _plan([_candidate(listing_indexes=[1], fit_range_mm=None)])

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", fake_home_depot_lookup)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req(measurement=Measurement(value_m=0.8, axis="w")))

    assert len(parts) == 1
    part = parts[0]
    assert part.fit_range_mm == {"min": 558.8, "max": 914.4}
    assert part.fit.status == "fits"  # 800mm is inside the 558.8-914.4mm range


@pytest.mark.asyncio
async def test_candidate_fit_range_wins_over_home_depot(monkeypatch):
    """The LLM-published fit range on the candidate itself is never overridden by Home Depot's
    -- HD only fills in when the candidate had none."""

    async def fake_shopping(query):
        return [HD_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_home_depot_lookup(query_or_model_no):
        return {
            "dims": Dims(w=457.2, d=396.9, h=304.8),
            "fit_range_mm": {"min": 100.0, "max": 200.0},  # must be ignored
            "title": "Windmill 6,000 BTU Window Air Conditioner",
            "brand": "Amerimax",
            "model_number": "150121",
            "image": None,
            "link": "https://www.homedepot.com/p/windmill/319772228",
            "price": 299.0,
            "rating": 4.4,
            "reviews": 4007,
            "eta": None,
        }

    async def fake_extract(role, messages, model_cls):
        return _plan(
            [_candidate(listing_indexes=[1], fit_range_mm=search.FitRangeMm(min=558.8, max=914.4))]
        )

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", fake_home_depot_lookup)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req(measurement=Measurement(value_m=0.8, axis="w")))

    assert parts[0].fit_range_mm == {"min": 558.8, "max": 914.4}


HD_SEARCH_LISTING = {
    "title": "Amerimax 150121 5 in. K-style hidden gutter hanger",
    "brand": "Amerimax Home Products",
    "model_number": "150121",
    "price_usd": 3.18,
    "product_id": "100024377",
    "link": "https://homedepot.example/p/amerimax-150121/100024377",
    "thumbnail": "https://img.example.com/hd-search-thumb.jpg",
    "rating": 4.6,
    "reviews": 3700,
}


async def _no_hd_search(_query):
    return []


# --- Home Depot as a discovery source (sellers.home_depot_search) --------------------------


@pytest.mark.asyncio
async def test_prompt_includes_home_depot_listings(monkeypatch):
    """`_discover` runs shopping + home_depot_search + web.search in parallel, and the merged
    listing shows up in the extract prompt the LLM sees."""

    async def fake_shopping(query):
        return [OTHER_LISTING]

    async def fake_home_depot_search(query):
        return [HD_SEARCH_LISTING]

    async def fake_web_search(query, n=6):
        return []

    captured = {}

    async def fake_extract(role, messages, model_cls):
        captured["user"] = messages[1]["content"]
        return _plan([_candidate(listing_indexes=[])])

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "home_depot_search", fake_home_depot_search)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    await search.find_parts(_req())

    assert "Home Depot" in captured["user"]
    assert HD_SEARCH_LISTING["title"] in captured["user"]
    assert OTHER_LISTING["title"] in captured["user"]


@pytest.mark.asyncio
async def test_hd_tied_candidate_uses_home_depot_product_directly(monkeypatch):
    """A candidate whose listing_indexes point at a Home Depot listing (with product_id) skips
    the search+match dance entirely: one home_depot_product call, zero home_depot_lookup calls,
    and no extra home_depot_search call beyond the one discovery already made."""
    hd_dims = Dims(w=200.0, d=50.0, h=60.0)
    calls = {"search": 0, "product": 0, "lookup": 0}

    async def fake_shopping(query):
        return []

    async def fake_home_depot_search(query):
        calls["search"] += 1
        return [HD_SEARCH_LISTING]

    async def fake_home_depot_product(product_id):
        calls["product"] += 1
        assert product_id == "100024377"
        return {
            "dims": hd_dims,
            "title": HD_SEARCH_LISTING["title"],
            "brand": HD_SEARCH_LISTING["brand"],
            "model_number": HD_SEARCH_LISTING["model_number"],
            "image": None,
            "link": HD_SEARCH_LISTING["link"],
            "price": 3.18,
            "rating": 4.6,
            "reviews": 3700,
            "eta": None,
            "color": None,
            "material": None,
        }

    async def fake_home_depot_lookup(_query_or_model_no):
        calls["lookup"] += 1

    async def fake_web_search(query, n=6):
        return []

    async def fake_extract(role, messages, model_cls):
        # listing 1 in the merged block is the sole Home Depot listing (shopping is empty)
        return _plan(
            [
                _candidate(
                    listing_indexes=[1],
                    dims_mm=search.DimsMm(),
                    manufacturer="Home Depot",  # LLM named the retailer; HD's brand must win
                    model_no=None,
                )
            ]
        )

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "home_depot_search", fake_home_depot_search)
    monkeypatch.setattr(search.sellers, "home_depot_product", fake_home_depot_product)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", fake_home_depot_lookup)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req())

    assert calls == {"search": 1, "product": 1, "lookup": 0}
    assert len(parts) == 1
    assert parts[0].dims_mm == hd_dims
    assert parts[0].dims_source == "home_depot"
    # Home Depot's record beats/fills the LLM's brand and model; its link survives the dead-URL check
    assert parts[0].spec_url == HD_SEARCH_LISTING["link"]
    assert parts[0].model_no == HD_SEARCH_LISTING["model_number"]
    assert parts[0].manufacturer == HD_SEARCH_LISTING["brand"]
    assert parts[0].name == HD_SEARCH_LISTING["title"]


@pytest.mark.asyncio
async def test_dims_from_home_depot_single_query_no_retry(monkeypatch):
    """Fallback HD lookup (no listing_indexes tie) is at most ONE query per candidate -- model
    number if present, never a second attempt with the name if that query doesn't match."""
    calls = []

    async def fake_shopping(query):
        return [OTHER_LISTING]

    async def fake_home_depot_lookup(query_or_model_no):
        calls.append(query_or_model_no)
        return {  # wrong brand/model -- _title_matches rejects it, no retry should follow
            "dims": Dims(w=1, d=1, h=1),
            "title": "Some other product",
            "brand": "Other Brand",
            "model_number": "XXX",
        }

    async def fake_web_search(query, n=6):
        return []

    async def fake_fetch(url, max_chars=12000):
        return None

    async def fake_extract(role, messages, model_cls):
        return _plan(
            [_candidate(listing_indexes=[], dims_mm=search.DimsMm(w=None, d=None, h=None))]
        )

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "home_depot_search", _no_hd_search)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", fake_home_depot_lookup)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "fetch", fake_fetch)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req())

    assert calls == ["150121"]  # candidate.model_no -- one query, no retry with the name
    assert parts == []  # no match anywhere -> dropped


# --- _build_sellers: union / dedupe / cap / fallback ----------------------------------------


@pytest.mark.asyncio
async def test_build_sellers_unions_hd_and_stores_dedupes_and_caps(monkeypatch):
    hd = {
        "title": "5 in. Hanger",
        "price": 3.18,
        "link": "https://homedepot.example/1",
        "eta": "Sep 26",
        "rating": 4.6,
    }
    listings = [
        {"immersive_token": "tok-a"},
        {"immersive_token": "tok-b"},
        {"immersive_token": "tok-c"},  # a 3rd token must never be fetched
    ]
    calls = []

    async def fake_stores(token):
        calls.append(token)
        if token == "tok-a":
            return [
                # pricier dup of the HD product -- the real hd seller must win
                Seller(name="Home Depot", price_usd=5.00, total_usd=5.00, verified=True),
                Seller(name="Zoro", price_usd=40.0, total_usd=45.0, verified=True),
            ]
        return [
            Seller(name="Zoro", price_usd=30.0, total_usd=32.0, verified=True),  # cheaper dup
            *[
                Seller(name=f"Seller{i}", price_usd=float(i), total_usd=float(i), verified=True)
                for i in range(10)
            ],
        ]

    monkeypatch.setattr(search.sellers, "stores", fake_stores)

    result = await search._build_sellers(hd, listings)

    assert calls == ["tok-a", "tok-b"]  # capped at 2 immersive calls
    assert len(result) == 8  # capped at _MAX_SELLERS
    hd_seller = next(s for s in result if s.name == "Home Depot")
    assert hd_seller.price_usd == 3.18  # real HD product beats the $5 stores() dup
    zoro = next(s for s in result if s.name == "Zoro")
    assert zoro.total_usd == 32.0  # cheaper of the two Zoro entries kept


@pytest.mark.asyncio
async def test_build_sellers_falls_back_to_listings_when_empty(monkeypatch):
    async def fake_stores(token):
        return []

    monkeypatch.setattr(search.sellers, "stores", fake_stores)

    listings = [
        {
            "title": "50 Pack Gutter Hangers",
            "source": "eBay - seller1",
            "price_usd": 40.0,
            "delivery": "Free",
            "immersive_token": None,
        }
    ]

    result = await search._build_sellers(None, listings)

    assert len(result) == 1
    assert result[0].name == "eBay - seller1"
    assert result[0].pack_qty == 50
    assert result[0].unit_price_usd == pytest.approx(0.8)


# --- candidate with no dims anywhere is dropped ---------------------------------------------


@pytest.mark.asyncio
async def test_candidate_with_no_dims_is_dropped(monkeypatch):
    async def fake_shopping(query):
        return [OTHER_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_extract(role, messages, model_cls):
        good = _candidate(listing_indexes=[1])
        bad = _candidate(
            name="Mystery hanger",
            manufacturer="Mystery Co",
            model_no="???",
            dims_mm=search.DimsMm(w=None, d=None, h=None),
            listing_indexes=[],
            spec_url=None,
        )
        return _plan([good, bad])

    async def fake_fetch(url, max_chars=12000):
        return None  # no page text -> nothing for the "page" fallback either

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "fetch", fake_fetch)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req())

    assert len(parts) == 1
    assert parts[0].name == "Amerimax 5 in. K-style hidden gutter hanger"


@pytest.mark.asyncio
async def test_candidate_with_no_sellers_is_dropped(monkeypatch):
    async def fake_shopping(query):
        return [OTHER_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_extract(role, messages, model_cls):
        # dims from a manual page, but no listing and no Home Depot record: nowhere to buy it
        return _plan([_candidate(name="Manual-only AC", listing_indexes=[])])

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    assert await search.find_parts(_req()) == []


@pytest.mark.asyncio
async def test_spare_candidate_replaces_a_dropped_pick(monkeypatch):
    built = []

    async def fake_shopping(query):
        return [OTHER_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_extract(role, messages, model_cls):
        bad = _candidate(
            name="Mystery hanger",
            model_no="???",
            dims_mm=search.DimsMm(w=None, d=None, h=None),
            listing_indexes=[],
        )
        spare = _candidate(name="Spare hanger", listing_indexes=[1])
        unused = _candidate(name="Unused hanger", listing_indexes=[1])
        return _plan([bad, spare, unused])

    async def fake_fetch(url, max_chars=12000):
        return None

    real_build = search._build_candidate

    async def tracking_build(candidate, *args):
        built.append(candidate.name)
        return await real_build(candidate, *args)

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "fetch", fake_fetch)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)
    monkeypatch.setattr(search, "_build_candidate", tracking_build)

    parts = await search.find_parts(_req(max_candidates=1))

    assert [p.name for p in parts] == ["Spare hanger"]
    assert built == ["Mystery hanger", "Spare hanger"]  # spares only built on demand


# --- page-fetch dims path uses role "cheap" -------------------------------------------------


@pytest.mark.asyncio
async def test_page_fetch_dims_uses_cheap_role(monkeypatch):
    calls = []

    async def fake_shopping(query):
        return [OTHER_LISTING]

    async def fake_web_search(query, n=6):
        return []

    async def fake_fetch(url, max_chars=12000):
        assert url == OTHER_LISTING["product_link"]
        return "Spectra 5 in K style gutter hanger. Width: 5 in. Depth: 1.5 in. Height: 1.75 in."

    async def fake_extract(role, messages, model_cls):
        calls.append(role)
        if role == "extract":
            return _plan(
                [
                    _candidate(
                        name="Spectra 5 in K style gutter hanger",
                        manufacturer="Spectra",
                        model_no="SP-5K",
                        dims_mm=search.DimsMm(w=None, d=None, h=None),
                        listing_indexes=[1],
                    )
                ]
            )
        assert role == "cheap"
        assert model_cls is search.DimsMm
        return search.DimsMm(w=127.0, d=38.0, h=45.0)

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "fetch", fake_fetch)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    parts = await search.find_parts(_req())

    assert calls == ["extract", "cheap"]
    assert len(parts) == 1
    assert parts[0].dims_source == "page"
    assert parts[0].dims_mm == Dims(w=127, d=38, h=45)


# --- last resort: sizes printed in the title (integration finding #15) ---------------------


def _title_candidate(name, **dims):
    return _candidate(
        name=name,
        manufacturer="Acme",
        model_no=None,
        dims_mm=search.DimsMm(**{"w": None, "d": None, "h": None, **dims}),
        listing_indexes=[],
    )


def test_dims_from_title_wdh_labels_and_flat_goods_rule():
    labelled = _title_candidate("Acme 36 in. W x 24 in. D x 34.5 in. H Base Cabinet")
    assert search._dims_from_title(labelled, [], None) == Dims(w=914.4, d=609.6, h=876.3)
    # unlabelled: thinnest = depth off the mounting surface, longest runs vertical
    plain = _title_candidate("Acme Wall Shelf")
    listing = {"title": "Acme Wall Shelf 12 x 8 x 3 in., White"}
    assert search._dims_from_title(plain, [listing], None) == Dims(w=203.2, d=76.2, h=304.8)


def test_dims_from_title_fills_only_the_one_missing_side():
    # the LLM published width and depth ("1.5 in. wide, 10 in. arm"); the title has the height
    bracket = _title_candidate("Acme 8 in. Shelf Bracket", w=38.1, d=254.0)
    assert search._dims_from_title(bracket, [], None) == Dims(w=38.1, d=254.0, h=203.2)
    # a title size the LLM already has is not a new side
    same = _title_candidate("Acme 10 in. Shelf Bracket", w=38.1, d=254.0)
    assert search._dims_from_title(same, [], None) is None


@pytest.mark.parametrize(
    "name,dims",
    [
        ("Acme 12 in. x 8 in. Shelf Bracket", {}),  # two sides, the third unknown
        ("Acme 3-3/4 in. Bar Pull", {"w": 146.0}),  # one size, two sides missing
        ("Acme 3 in. (76 mm) Center-to-Center Bar Pull", {"w": 146.0, "d": 32.0}),  # spacing
        ("Acme Mystery Pull", {"w": 146.0, "d": 32.0}),  # no size at all
    ],
)
def test_dims_from_title_never_guesses_a_side(name, dims):
    assert search._dims_from_title(_title_candidate(name, **dims), [], None) is None


@pytest.mark.asyncio
async def test_title_sizes_rescue_a_candidate_with_no_other_dims(monkeypatch):
    """No Home Depot match, no page text: before, this candidate was dropped for "no dims"."""
    listing = {**OTHER_LISTING, "title": "Acme 8 in. Heavy Duty Shelf Bracket, Black"}

    async def fake_shopping(query):
        return [listing]

    async def fake_web_search(query, n=6):
        return []

    async def fake_fetch(url, max_chars=12000):
        return None

    async def fake_extract(role, messages, model_cls):
        assert role == "extract"  # no page text, so no "cheap" dims call either
        cand = _title_candidate("Acme Heavy Duty Shelf Bracket", w=38.1, d=254.0)
        return _plan([cand.model_copy(update={"listing_indexes": [1]})])

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "fetch", fake_fetch)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    [part] = await search.find_parts(_req(query="shelf bracket"))

    assert part.dims_source == "title"
    assert part.dims_mm == Dims(w=38.1, d=254.0, h=203.2)


# --- whole-result cache: a hit skips discovery + the LLM entirely --------------------------


@pytest.mark.asyncio
async def test_cache_hit_skips_discovery(monkeypatch):
    counts = {"shopping": 0, "web_search": 0, "extract": 0}

    async def fake_shopping(query):
        counts["shopping"] += 1
        return [OTHER_LISTING]

    async def fake_web_search(query, n=6):
        counts["web_search"] += 1
        return []

    async def fake_extract(role, messages, model_cls):
        counts["extract"] += 1
        return _plan([_candidate(listing_indexes=[1])])

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    req = _req()
    first = await search.find_parts(req)
    assert counts == {"shopping": 1, "web_search": 1, "extract": 1}
    assert first[0].cached is False

    second = await search.find_parts(req)
    assert counts == {"shopping": 1, "web_search": 1, "extract": 1}  # unchanged: discovery skipped
    assert second[0].cached is True
    assert second[0].id == first[0].id


# --- prompt size guard --------------------------------------------------------------------


@pytest.mark.asyncio
async def test_extract_prompt_size_guard(monkeypatch):
    shopping_fixture = json.loads((FIXTURES / "serpapi_google_shopping.json").read_text())
    hd_fixture = json.loads((FIXTURES / "serpapi_home_depot.json").read_text())
    exa_fixture = json.loads((FIXTURES / "exa_search.json").read_text())

    # Reshape the raw fixtures the way sellers.shopping()/home_depot_search()/web.search() would,
    # so this is a realistic (40 shopping listings, 24 HD listings, 5 web hits) fixture-sized
    # input to the prompt condenser -- the whole extract prompt must still fit the ~4K token
    # budget even with both discovery sources at full fixture size.
    listings = [
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
        for r in shopping_fixture["shopping_results"]
    ]
    assert len(listings) == 40
    hd_listings = [
        {
            "title": p.get("title"),
            "brand": p.get("brand"),
            "model_number": p.get("model_number"),
            "price_usd": p.get("price"),
            "product_id": p.get("product_id"),
            "link": p.get("link"),
            "thumbnail": (
                p["thumbnails"][0][-1] if p.get("thumbnails") and p["thumbnails"][0] else None
            ),
            "rating": p.get("rating"),
            "reviews": p.get("reviews"),
        }
        for p in hd_fixture["products"]
    ]
    assert len(hd_listings) == 24
    hits = [
        search.web.Hit(title=r["title"], url=r["url"], text=r["text"], source="exa")
        for r in exa_fixture["results"]
    ]
    assert len(hits) == 5

    async def fake_shopping(query):
        return listings

    async def fake_home_depot_search(query):
        return hd_listings

    async def fake_web_search(query, n=6):
        return hits

    captured = {}

    async def fake_extract(role, messages, model_cls):
        captured["role"] = role
        captured["chars"] = sum(len(m["content"]) for m in messages)
        return _plan([_candidate(listing_indexes=[])])

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "home_depot_search", fake_home_depot_search)
    monkeypatch.setattr(search.sellers, "stores", _no_stores)
    monkeypatch.setattr(search.sellers, "home_depot_lookup", _no_hd)
    monkeypatch.setattr(search.web, "search", fake_web_search)
    monkeypatch.setattr(search.web, "url_alive", _dead_url_alive)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    await search.find_parts(_req())

    assert captured["role"] == "extract"
    assert captured["chars"] < 16000


# --- expand_sellers (lazy) ------------------------------------------------------------------


def _part_for_expand(**kw):
    defaults = {
        "id": "windmill-w1w12",
        "name": "Windmill 6,000 BTU Window Air Conditioner",
        "manufacturer": "Windmill",
        "model_no": "W1W12",
        "dims_mm": Dims(w=457.2, d=396.9, h=304.8),
        "sellers": [Seller(name="Home Depot", price_usd=299.0, total_usd=299.0, verified=True)],
    }
    defaults.update(kw)
    return Part(**defaults)


@pytest.mark.asyncio
async def test_expand_sellers_merges_dedupes_and_sets_flag(monkeypatch):
    calls = {"shopping": 0, "stores": 0}

    async def fake_shopping(query):
        calls["shopping"] += 1
        assert query == "Windmill W1W12"
        return [
            {  # matches: model_no token in the title
                "title": "Windmill W1W12 6000 BTU Window AC",
                "source": "Best Buy",
                "immersive_token": "tok-1",
            },
            {  # no model_no/brand match -- must be ignored
                "title": "Frigidaire 5000 BTU Window AC",
                "source": "Wayfair",
                "immersive_token": "tok-2",
            },
        ]

    async def fake_stores(token):
        calls["stores"] += 1
        assert token == "tok-1"
        return [
            # cheaper dup of the seller already on the part -- must win over the existing $299
            Seller(name="Home Depot", price_usd=250.0, total_usd=250.0, verified=True),
            Seller(name="Best Buy", price_usd=320.0, total_usd=320.0, verified=True),
        ]

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)
    monkeypatch.setattr(search.sellers, "stores", fake_stores)

    part = await search.expand_sellers(_part_for_expand())

    assert calls == {"shopping": 1, "stores": 1}
    assert part.sellers_expanded is True
    by_name = {s.name: s for s in part.sellers}
    assert set(by_name) == {"Home Depot", "Best Buy"}
    assert by_name["Home Depot"].total_usd == 250.0  # cheaper dup won the merge
    assert part.recommended_seller is not None
    assert part.recommendation_reason


@pytest.mark.asyncio
async def test_expand_sellers_skips_when_already_expanded_or_enough_sellers(monkeypatch):
    calls = {"shopping": 0}

    async def fake_shopping(query):
        calls["shopping"] += 1
        return []

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping)

    already_expanded = _part_for_expand(sellers_expanded=True)
    result = await search.expand_sellers(already_expanded)
    assert result is already_expanded
    assert calls["shopping"] == 0

    enough_sellers = _part_for_expand(
        sellers=[
            Seller(name="A", price_usd=1, verified=True),
            Seller(name="B", price_usd=2, verified=True),
            Seller(name="C", price_usd=3, verified=True),
        ]
    )
    result = await search.expand_sellers(enough_sellers)
    assert result is enough_sellers
    assert calls["shopping"] == 0


@pytest.mark.asyncio
async def test_expand_sellers_failure_keeps_existing_sellers_and_still_marks_expanded(
    monkeypatch,
):
    async def fake_shopping_raises(query):
        raise RuntimeError("serpapi down")

    monkeypatch.setattr(search.sellers, "shopping", fake_shopping_raises)

    part = _part_for_expand()
    original_sellers = list(part.sellers)
    result = await search.expand_sellers(part)

    assert result.sellers_expanded is True  # never retried again
    assert result.sellers == original_sellers  # untouched on failure


# --- refine_sellers ------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_refine_sellers_cheapest():
    part = Part(
        id="x",
        name="hanger",
        dims_mm=Dims(w=1, d=1, h=1),
        sellers=[
            Seller(name="pricey", price_usd=10, total_usd=10, verified=True),
            Seller(name="cheap", price_usd=3, total_usd=3, verified=True),
        ],
        recommended_seller=0,
        sellers_expanded=True,  # already expanded -- no lazy-expansion network call here
    )
    refined = await search.refine_sellers(part, "cheapest")
    assert refined.sellers[0].name == "cheap"
    assert refined.recommended_seller == 0
    assert "cheapest" in refined.recommendation_reason


@pytest.mark.asyncio
async def test_refine_sellers_expands_first_when_few_sellers(monkeypatch):
    """`refine_sellers` runs the (mocked) lazy expansion before re-ranking, for a part that
    still has fewer than 3 sellers and hasn't been expanded yet."""
    part = Part(
        id="x",
        name="hanger",
        dims_mm=Dims(w=1, d=1, h=1),
        sellers=[Seller(name="pricey", price_usd=10, total_usd=10, verified=True)],
    )

    async def fake_expand(p):
        p.sellers = [*p.sellers, Seller(name="cheap", price_usd=3, total_usd=3, verified=True)]
        p.sellers_expanded = True
        return p

    monkeypatch.setattr(search, "expand_sellers", fake_expand)

    refined = await search.refine_sellers(part, "cheapest")
    assert refined.sellers_expanded is True
    assert refined.sellers[0].name == "cheap"


def test_title_match_rejects_other_size_same_brand():
    from server.search import Candidate, DimsMm, _title_matches

    c = Candidate(
        name="Amerimax 5 in. Hidden Gutter Hanger", manufacturer="Amerimax", dims_mm=DimsMm()
    )
    assert _title_matches(
        c, "Amerimax Home Products", "21812", "5 in. Aluminum Hidden Gutter Hanger"
    )
    assert not _title_matches(c, "Amerimax Home Products", "47814", "6 in. Aluminum Gutter Hanger")
    assert not _title_matches(c, "Hangtite", None, "5 in. Hidden Hanger")
    usa = Candidate(
        name='U.S. Aluminum 5HGNC 5" Hanger',
        manufacturer="U.S. Aluminum",
        model_no="5HGNC",
        dims_mm=DimsMm(),
    )
    assert not _title_matches(usa, "Spectra Pro Select", "5HNSRT", "5 in. Aluminum Hidden Hanger")
    assert _title_matches(usa, "Other", "5hgnc", "whatever")


@pytest.mark.asyncio
async def test_empty_result_not_cached(monkeypatch, tmp_path):
    from server import cache
    from server.models import SearchRequest

    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    calls = []

    async def fake_uncached(req, progress):
        calls.append(1)
        return []

    monkeypatch.setattr(search, "_search_uncached", fake_uncached)
    await search.find_parts(SearchRequest(query="nothing"))
    await search.find_parts(SearchRequest(query="nothing"))
    assert len(calls) == 2
    assert cache.get("search", {"q": "nothing", "m": None}) is None


def test_prompt_includes_photo_description():
    req = SearchRequest(query="gutter hanger")
    user = search._build_prompt(req, "", "", photo="white aluminium K-style hanger")[1]["content"]
    assert "Photo of the existing part (from the headset): white aluminium K-style hanger" in user
    assert "Photo" not in search._build_prompt(req, "", "")[1]["content"]


@pytest.mark.asyncio
async def test_describe_photo_failure_is_not_fatal(monkeypatch):
    async def broken(frame, hint=None):
        raise ValueError("bad jpeg")

    monkeypatch.setattr(search.vision, "identify_part", broken)
    assert await search._describe_photo(SearchRequest(query="x", frame_jpg_b64="zz")) is None
    assert await search._describe_photo(SearchRequest(query="x")) is None


# --- OFFLINE ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_offline_cache_hit_returns_cached_parts_without_discovery(monkeypatch):
    req = _req(query="cached gutter hanger")

    async def fake_uncached_once(req, progress):
        return [Part(id="hanger-1", name="Amerimax hanger", dims_mm=Dims(w=127, d=38, h=45))]

    # warm the cache the normal (online) way first
    monkeypatch.setattr(search, "_search_uncached", fake_uncached_once)
    warmed = await search.find_parts(req)
    assert len(warmed) == 1

    async def fail_uncached(req, progress):
        raise AssertionError("OFFLINE must never call _search_uncached on a cache hit")

    monkeypatch.setattr(search, "_search_uncached", fail_uncached)
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()

    cached_result = await search.find_parts(req)
    assert len(cached_result) == 1
    assert cached_result[0].id == "hanger-1"
    assert cached_result[0].cached is True


@pytest.mark.asyncio
async def test_offline_other_measurement_reuses_query_cache_and_refits(monkeypatch):
    async def fake_uncached_once(req, progress):
        return [Part(id="h", name="hanger", dims_mm=Dims(w=127, d=38, h=45))]

    monkeypatch.setattr(search, "_search_uncached", fake_uncached_once)
    await search.find_parts(_req(query="gutter hanger"))  # warmed with a 165 mm measurement

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    live = Measurement(label="gutter width", value_m=0.100, axis="w")
    parts = await search.find_parts(_req(query="gutter hanger", measurement=live))

    assert [p.id for p in parts] == ["h"]
    assert parts[0].fit.status == "too_big"  # refit against 100 mm, not the warmed 165 mm


@pytest.mark.asyncio
async def test_offline_cache_miss_returns_empty_without_calling_discovery(monkeypatch):
    async def fail_uncached(req, progress):
        raise AssertionError("OFFLINE must never call _search_uncached on a cache miss")

    monkeypatch.setattr(search, "_search_uncached", fail_uncached)
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()

    parts = await search.find_parts(_req(query="never seen before"))
    assert parts == []


@pytest.mark.asyncio
async def test_uplink_failure_mid_pipeline_does_not_lose_a_cached_answer(monkeypatch):
    """OFFLINE is false, but a cache hit must still short-circuit before any network call --
    an uplink failure elsewhere in the pipeline can never shadow an already-cached result."""
    req = _req(query="already cached hanger")
    key = {"q": search.cache.normalize(req.query), "m": req.measurement.model_dump()}
    cached_part = Part(id="hanger-cached", name="Cached hanger", dims_mm=Dims(w=127, d=38, h=45))
    search.cache.put("search", key, [cached_part.model_dump(mode="json")])

    async def raises_connect_error(*args, **kwargs):
        raise httpx.ConnectError("network is down")

    monkeypatch.setattr(search.sellers, "shopping", raises_connect_error)
    monkeypatch.setattr(search.sellers, "home_depot_search", raises_connect_error)
    monkeypatch.setattr(search.web, "search", raises_connect_error)
    monkeypatch.setattr(search.llm, "extract", raises_connect_error)

    parts = await search.find_parts(req)
    assert len(parts) == 1
    assert parts[0].id == "hanger-cached"
    assert parts[0].cached is True


def test_listing_match_brand_words_and_sizes():
    part = Part(
        id="p",
        name="5 in. Aluminum Hidden Gutter Hanger with Screw",
        manufacturer="Amerimax Home Products",
        model_no="21812",
        dims_mm=Dims(w=19, d=127, h=25),
    )
    assert search._listing_matches_part(part, "Amerimax Gutter Hanger K-Style Hidden")
    assert search._listing_matches_part(part, "Amerimax 5 in. Hidden Gutter Hanger, White")
    assert not search._listing_matches_part(part, "Amerimax 6 in. Hidden Gutter Hanger")
    assert not search._listing_matches_part(part, "Amerimax Downspout Extension")
    assert not search._listing_matches_part(part, "Spectra Hidden Gutter Hanger")


@pytest.mark.asyncio
async def test_dims_fall_back_to_web_search_text(monkeypatch):
    queries = []

    async def fake_fetch(url, max_chars=12000):
        return None

    async def fake_search(query, n=6):
        queries.append(query)
        return [
            search.web.Hit(
                title="GLXS4BA spec sheet",
                url="https://x.example",
                text="29 x 29 x 32 in",
                source="exa",
            )
        ]

    async def fake_extract(role, messages, model_cls):
        assert role == "cheap"
        return search.DimsMm(w=737.0, d=737.0, h=813.0)

    monkeypatch.setattr(search.web, "fetch", fake_fetch)
    monkeypatch.setattr(search.web, "search", fake_search)
    monkeypatch.setattr(search.llm, "extract", fake_extract)

    cand = _candidate(manufacturer="Goodman", model_no="GLXS4BA", spec_url=None, listing_indexes=[])
    dims = await search._dims_from_page(cand, [])
    assert dims == Dims(w=737.0, d=737.0, h=813.0)
    assert queries == ["Goodman GLXS4BA dimensions"]


def test_dedupe_keeps_first_part_per_id():
    a1 = Part(id="a", name="first", dims_mm=Dims(w=1, d=1, h=1))
    b = Part(id="b", name="b", dims_mm=Dims(w=1, d=1, h=1))
    a2 = Part(id="a", name="second", dims_mm=Dims(w=1, d=1, h=1))
    assert [p.name for p in search._dedupe([a1, b, a2])] == ["first", "b"]
