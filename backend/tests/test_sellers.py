import json
import logging
from datetime import date
from pathlib import Path

import httpx
import pytest
import respx

from server import sellers
from server.config import get_settings
from server.models import Dims, Seller
from server.sellers import (
    home_depot_lookup,
    home_depot_product,
    home_depot_search,
    parse_eta,
    parse_shipping,
    rank_sellers,
    recommend,
    shopping,
    stores,
)

FIXTURES = Path(__file__).parent / "fixtures" / "search"
URL = "https://serpapi.com/search.json"


def _load(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text())


@pytest.fixture(autouse=True)
def _cache_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _engine_router():
    by_engine = {
        "google_shopping": _load("serpapi_google_shopping.json"),
        "google_immersive_product": _load("serpapi_google_immersive_product.json"),
        "home_depot": _load("serpapi_home_depot.json"),
        "home_depot_product": _load("serpapi_home_depot_product.json"),
    }

    def side_effect(request):
        engine = request.url.params.get("engine")
        return httpx.Response(200, json=by_engine[engine])

    router = respx.mock(assert_all_called=False)
    router.get(URL).mock(side_effect=side_effect)
    return router


# --- shopping ----------------------------------------------------------------


@pytest.mark.asyncio
async def test_shopping_condenses_results():
    with _engine_router() as router:
        results = await shopping("Amerimax 5 in. K-style hidden gutter hanger")
        assert router.calls.call_count == 1

    assert len(results) == 40
    hd = next(r for r in results if r["source"] == "Home Depot")
    assert hd["price_usd"] == 3.18
    assert hd["delivery"] == "Free delivery"
    assert hd["rating"] == 3.7
    assert hd["reviews"] == 76
    assert hd["immersive_token"]
    assert "test-dummy-key" not in json.dumps(results)


@pytest.mark.asyncio
async def test_shopping_no_key_returns_empty(monkeypatch):
    monkeypatch.setenv("SERP_API_KEY", "")
    get_settings.cache_clear()
    with _engine_router() as router:
        results = await shopping("anything")
        assert router.calls.call_count == 0
    assert results == []


@pytest.mark.asyncio
async def test_cache_prevents_second_http_call():
    with _engine_router() as router:
        await shopping("same query")
        await shopping("same query")
        assert router.calls.call_count == 1


@pytest.mark.asyncio
async def test_serpapi_429_never_logs_the_api_key(caplog):
    """HTTPStatusError.__str__ embeds the full request URL (api_key rides in it as a query
    param) -- `_serpapi_get` must never let that string reach a log record or exception.

    (httpx's own wire-level "HTTP Request: GET ..." INFO log is a separate, already-mitigated
    concern -- production silences it via `logging.getLogger("httpx").setLevel(WARNING)`,
    covered by test_app.py::test_httpx_request_urls_not_logged -- so it's excluded here.)
    """
    with caplog.at_level(logging.INFO), respx.mock(assert_all_called=True) as router:
        router.get(URL).mock(return_value=httpx.Response(429, text="rate limited"))
        with pytest.raises(sellers.SerpApiError) as exc_info:
            await shopping("anything")

    assert "test-dummy-key" not in str(exc_info.value)
    assert not any("test-dummy-key" in r.getMessage() for r in caplog.records if r.name != "httpx")
    assert "status=429" in str(exc_info.value)


# --- stores -------------------------------------------------------------


@pytest.mark.asyncio
async def test_stores_maps_13_sellers():
    with _engine_router():
        result = await stores("fake-token")

    assert len(result) == 13
    assert all(isinstance(s, Seller) for s in result)

    hd = next(s for s in result if s.name == "Home Depot")
    assert hd.price_usd == 3.18
    assert hd.shipping_usd == 0.0
    assert hd.total_usd == 3.18
    assert hd.verified is True
    assert hd.in_stock is True
    assert hd.title == "5 in. Brown Vinyl K-Style Hidden Gutter Hanger"
    assert hd.pack_qty == 1
    assert hd.unit_price_usd == pytest.approx(3.18)

    hamshaw = next(s for s in result if s.name == "Hamshawlumber.com")
    assert hamshaw.shipping_usd == 20.0
    assert hamshaw.total_usd == 24.59
    assert hamshaw.eta == "Sep 25 - Oct 1"

    mccoys = next(s for s in result if s.name == "McCoy's Building Supply")
    assert mccoys.eta is None  # no delivery-date text in details_and_offers

    max_warehouse = next(s for s in result if s.name == "Max Warehouse")
    assert max_warehouse.pack_qty == 75  # title ends "... K Gutter Style - Pack of 75"
    assert max_warehouse.unit_price_usd == pytest.approx(max_warehouse.price_usd / 75, rel=1e-6)


@pytest.mark.parametrize(
    "shipping_text,expected",
    [
        ("Free", 0.0),
        ("+ $20.00", 20.0),
        ("+ $2,500.00", 2500.0),
        (None, None),
    ],
)
def test_parse_shipping(shipping_text, expected):
    assert parse_shipping(shipping_text) == expected


# --- eta parsing --------------------------------------------------------


@pytest.mark.parametrize(
    "text,expected_str,expected_days",
    [
        ("Delivery between Sep 25 – Oct 1 $20", "Sep 25 - Oct 1", 7),
        ("Free delivery by Fri", "Fri", 1),
        ("Get it by Sep 26", "Sep 26", 2),
        ("Delivery between Oct 5 – 8 $8.99", "Oct 5 - 8", 14),
        ("Delivery by Wed $16.40", "Wed", 6),
    ],
)
def test_parse_eta(text, expected_str, expected_days):
    today = date(2026, 9, 24)  # Thursday
    assert parse_eta(text, today) == (expected_str, expected_days)


def test_parse_eta_no_date_returns_none():
    today = date(2026, 9, 24)
    assert parse_eta("In stock online", today) is None
    assert parse_eta("Free delivery", today) is None
    assert parse_eta("", today) is None


# --- home depot dims / lookup --------------------------------------------


@pytest.mark.parametrize(
    "raw,expected",
    [
        ("5 in", 5.0),
        ("0.75 in", 0.75),
        ("17.32 in", 17.32),
        ("1-1/2 in", 1.5),
    ],
)
def test_parse_inches(raw, expected):
    assert sellers._parse_inches(raw) == pytest.approx(expected)


# --- parse_pack_qty ------------------------------------------------------------------------


@pytest.mark.parametrize(
    "title,expected",
    [
        ("AMERIMAX (50-Pack) Gutter Hanger", 50),
        ("Gutter Hangers 50 pk", 50),
        ("Box of 100 Screws", 100),
        ("Gutter Hangers 100/Box", 100),
        ("Case of 50 Hangers", 50),
        ("Gutter Hangers 50 Count", 50),
        ("Gutter Hangers 50 ct", 50),
        ("Pack of 10 Gutter Hangers", 10),
        ("Gutter Hangers 10 pcs", 10),
        ("Amerimax 5 in. Hidden Gutter Hanger with Screw", 1),  # no pack keyword at all
        ("5 in. Aluminum Hidden Gutter Hanger with Screw", 1),  # size number, not a pack
        ("5-in Hidden Gutter Hanger", 1),  # hyphenated size number
        ("#8 x 3/8-in Wood Screw", 1),  # fastener size, never a pack
        ("Amerimax 21-030pk 5 in Roof Metal Gutter Hanger", 1),  # SKU tail, not a real pack
        (None, 1),
        ("", 1),
    ],
)
def test_parse_pack_qty(title, expected):
    assert sellers.parse_pack_qty(title) == expected


def test_pack_pricing_unit_price():
    assert sellers.pack_pricing("(50-Pack)", 35.50) == (50, pytest.approx(0.71))
    assert sellers.pack_pricing("5 in. Hanger", 3.18) == (1, pytest.approx(3.18))
    assert sellers.pack_pricing("Box of 10", None) == (10, None)


@pytest.mark.asyncio
async def test_home_depot_search_condenses_results():
    """One `home_depot` call, condensed to the discovery fields `_merge_listings` needs
    (title/brand/model_number/price_usd/product_id/link/thumbnail/rating/reviews)."""
    with _engine_router() as router:
        results = await home_depot_search("Amerimax K-style hidden gutter hanger")
        assert router.calls.call_count == 1

    assert len(results) == 24
    top = results[0]
    assert top["title"] == "5 in. Aluminum Hidden Gutter Hanger with Screw"
    assert top["brand"] == "Amerimax Home Products"
    assert top["model_number"] == "21812"
    assert top["price_usd"] == 2.74
    assert top["product_id"] == "100085356"
    assert top["link"].startswith("https://apionline.homedepot.com/p/")
    assert top["thumbnail"].endswith("_1000.jpg")
    assert top["rating"] == pytest.approx(4.5363)
    assert top["reviews"] == 537
    assert "test-dummy-key" not in json.dumps(results)


@pytest.mark.asyncio
async def test_home_depot_product_direct_call():
    """A known product_id resolves with exactly one `home_depot_product` call -- no search."""
    with _engine_router() as router:
        result = await home_depot_product("100085356")
        assert router.calls.call_count == 1

    assert result["title"] == "5 in. Aluminum Hidden Gutter Hanger with Screw"
    assert result["model_number"] == "21812"
    dims = result["dims"]
    assert isinstance(dims, Dims)
    assert dims.w == pytest.approx(0.75 * 25.4)


@pytest.mark.asyncio
async def test_home_depot_lookup_fields_and_dims():
    with _engine_router() as router:
        result = await home_depot_lookup("Amerimax K-style hidden gutter hanger")
        assert router.calls.call_count == 2  # home_depot then home_depot_product

    assert result["title"] == "5 in. Aluminum Hidden Gutter Hanger with Screw"
    assert result["brand"] == "Amerimax Home Products"
    assert result["model_number"] == "21812"
    assert result["price"] == 2.74
    assert result["rating"] == pytest.approx(4.5363)
    assert result["reviews"] == 537
    assert result["eta"] == "Sep 26"
    assert result["color"] == "Silver"
    assert result["material"] == "Aluminum"

    dims = result["dims"]
    assert isinstance(dims, Dims)
    assert dims.d == pytest.approx(5 * 25.4)
    assert dims.h == pytest.approx(1 * 25.4)
    assert dims.w == pytest.approx(0.75 * 25.4)


# --- fit_range_mm (window opening min/max width) --------------------------------------------


@pytest.mark.asyncio
async def test_home_depot_product_parses_window_fit_range():
    """Real (trimmed) Home Depot window-AC specs: "Window opening minimum/maximum width (in.)"
    -> fit_range_mm in mm, alongside the ordinary product dims."""
    fixture = _load("serpapi_home_depot_product_window_ac.json")
    with respx.mock(assert_all_called=False) as router:
        router.get(URL).mock(return_value=httpx.Response(200, json=fixture))
        result = await home_depot_product("319772228")
        assert router.calls.call_count == 1

    assert result["fit_range_mm"] == {
        "min": pytest.approx(22 * 25.4),
        "max": pytest.approx(36 * 25.4),
    }
    dims = result["dims"]
    assert isinstance(dims, Dims)
    assert dims.w == pytest.approx(18 * 25.4)


def test_parse_fit_range_ignores_height_and_product_width():
    specs = [
        {
            "key": "Dimensions",
            "value": [
                {"name": "Product Width (in.)", "value": "18 in"},
                {"name": "Window opening minimum height (in.)", "value": "13 in"},
                {"name": "Window opening maximum height (in.)", "value": "36 in"},
                {"name": "Window opening minimum width (in.)", "value": "22 in"},
                {"name": "Window opening maximum width (in.)", "value": "36 in"},
            ],
        }
    ]
    assert sellers._parse_fit_range(specs) == {
        "min": pytest.approx(22 * 25.4),
        "max": pytest.approx(36 * 25.4),
    }


def test_parse_fit_range_none_when_absent():
    specs = [{"key": "Dimensions", "value": [{"name": "Product Width (in.)", "value": "18 in"}]}]
    assert sellers._parse_fit_range(specs) is None


# --- rank / recommend ------------------------------------------------------


def _sample_sellers():
    return [
        Seller(
            name="A",
            price_usd=10,
            shipping_usd=0,
            total_usd=10,
            eta_days=5,
            rating=4.0,
            reviews=100,
            in_stock=True,
        ),
        Seller(
            name="B",
            price_usd=5,
            shipping_usd=2,
            total_usd=7,
            eta_days=2,
            rating=4.8,
            reviews=5,
            in_stock=True,
        ),
        Seller(
            name="C",
            price_usd=20,
            shipping_usd=None,
            total_usd=None,
            eta_days=None,
            rating=4.9,
            reviews=1,
            in_stock=False,
        ),
    ]


def test_rank_cheapest():
    ranked = rank_sellers(_sample_sellers(), "cheapest")
    assert [s.name for s in ranked] == ["B", "A", "C"]


def test_rank_fastest():
    ranked = rank_sellers(_sample_sellers(), "fastest")
    assert [s.name for s in ranked] == ["B", "A", "C"]


def test_rank_best():
    ranked = rank_sellers(_sample_sellers(), "best")
    assert [s.name for s in ranked] == ["A", "B", "C"]  # C out of stock -> last


def test_recommend_cheapest():
    idx, reason = recommend(_sample_sellers(), "cheapest")
    assert idx == 1
    assert "cheapest" in reason
    assert "$7.00" in reason


def test_recommend_fastest():
    idx, reason = recommend(_sample_sellers(), "fastest")
    assert idx == 1
    assert "arrives soonest" in reason


def test_recommend_best():
    idx, reason = recommend(_sample_sellers(), "best")
    assert idx == 0
    assert "in stock" in reason


def test_recommend_empty_list():
    assert recommend([], "cheapest") is None


# --- pack-aware ranking / recommend -----------------------------------------------------


def _pack_sellers():
    return [
        # a $35.39 single unit -- cheap in total, expensive per unit
        Seller(
            name="Single",
            price_usd=35.39,
            total_usd=35.39,
            pack_qty=1,
            unit_price_usd=35.39,
            in_stock=True,
        ),
        # a $223.23 case of 50 -- expensive in total, cheap per unit
        Seller(
            name="Bulk",
            title="(50-Pack)",
            price_usd=223.23,
            total_usd=223.23,
            pack_qty=50,
            unit_price_usd=223.23 / 50,
            in_stock=True,
        ),
    ]


def test_rank_cheapest_uses_unit_price_not_total():
    ranked = rank_sellers(_pack_sellers(), "cheapest")
    assert [s.name for s in ranked] == ["Bulk", "Single"]  # $4.46/unit beats $35.39/unit


def test_recommend_cheapest_reason_mentions_per_unit():
    idx, reason = recommend(_pack_sellers(), "cheapest")
    assert idx == 1  # "Bulk" is second in the input list, cheapest per unit
    assert "per unit" in reason
    assert "$4.46 each" in reason
    assert "pack of 50" in reason


def test_recommend_cheapest_reason_no_per_unit_wording_for_single_units():
    _idx, reason = recommend(_sample_sellers(), "cheapest")
    assert "per unit" not in reason


def test_rank_best_prefers_small_pack_when_stock_and_rating_tie():
    small = Seller(name="Small", pack_qty=1, in_stock=True, rating=4.5, reviews=50)
    bulk = Seller(name="Bulk", pack_qty=50, in_stock=True, rating=4.5, reviews=50)
    ranked = rank_sellers([bulk, small], "best")
    assert [s.name for s in ranked] == ["Small", "Bulk"]


def test_unit_cost_prefers_total_then_unit_price_then_price():
    import math

    assert sellers.unit_cost(Seller(name="a", total_usd=20, pack_qty=2)) == 10
    assert sellers.unit_cost(Seller(name="b", unit_price_usd=5, shipping_usd=2, pack_qty=2)) == 6
    assert sellers.unit_cost(Seller(name="c", price_usd=9, pack_qty=3)) == pytest.approx(3)
    assert sellers.unit_cost(Seller(name="d")) == math.inf


def test_best_product_skips_fuzzy_junk():
    from server.sellers import _best_product

    products = [
        {
            "title": "5 gal. White Base Semi-Gloss Interior Paint",
            "brand": "BEHR PRO",
            "product_id": "1",
        },
        {
            "title": "6 in. Aluminum Hidden Gutter Hanger with Screw",
            "brand": "Amerimax",
            "product_id": "2",
        },
        {
            "title": "5 in. Aluminum Hidden Gutter Hanger with Screw",
            "brand": "Amerimax",
            "product_id": "3",
        },
    ]
    assert _best_product(products, "Amerimax 5 in. Hidden Gutter Hanger")["product_id"] == "3"
    assert _best_product(products, "5HGNC") is None
    assert (
        _best_product([{"model_number": "21812", "product_id": "9"}], "21812")["product_id"] == "9"
    )


def test_eta_days_for_home_depot_arrival_text():
    from datetime import date

    today = date(2026, 9, 24)
    assert sellers.eta_days("Sep 26", today) == 2
    assert sellers.eta_days("Sep 26 - Sep 29", today) == 5
    assert sellers.eta_days(None, today) is None


def test_parse_dims_flat_panel_with_length_and_swapped_labels():
    specs = [
        {
            "key": "Dimensions",
            "value": [
                {"name": "Panel Height (in.)", "value": "26.6 in"},
                {"name": "Panel Width (in.)", "value": "1.4 in"},
                {"name": "Panel length (in.)", "value": "39.7"},
            ],
        }
    ]
    dims = sellers._parse_dims(specs)
    assert (round(dims.w), round(dims.d), round(dims.h)) == (676, 36, 1008)


def test_parse_sink_directional_dims_and_min_cabinet():
    specs = [
        {"key": "Details", "value": [{"name": "Minimum Cabinet Size (in.)", "value": "36"}]},
        {
            "key": "Dimensions",
            "value": [
                {"name": "Sink Front to Back Width (in.)", "value": "19 in"},
                {"name": "Sink Left to Right Length (in.)", "value": "32 in"},
                {"name": "Sink Top to Bottom Depth (in.)", "value": "10 in"},
            ],
        },
    ]
    dims = sellers._parse_dims(specs)
    assert (round(dims.w), round(dims.d), round(dims.h)) == (813, 483, 254)
    assert sellers._parse_fit_range(specs) == {"min": 914.4, "max": 10_000.0}


# Spec groups verbatim from the live `home_depot_product` responses behind integration finding
# #15 ("find me a cabinet door handle": all 5 listings dropped for "no dims").
_PULL_SPECS = [  # 206951260 European Style 3 in. (76 mm) Center-to-Center ... Pull (25-Pack)
    {
        "key": "Dimensions",
        "value": [
            {"name": "Center to Center Measurement (mm)", "value": "76"},
            {"name": "Pull Length (in.)", "value": "5.75 in"},
            {"name": "Pull Projection (in.)", "value": "1.25 in"},
        ],
    }
]
_KNOB_SPECS = [  # 202824439 10-Pack Garrett 1-1/4 in. (32 mm) Classic Satin Nickel Round Knob
    {
        "key": "Dimensions",
        "value": [
            {"name": "Knob Diameter (in.)", "value": "1.25 in"},
            {"name": "Knob Projection (in.)", "value": "1.13 in"},
        ],
    }
]


def test_parse_dims_cabinet_pull_length_and_projection():
    dims = sellers._parse_dims(_PULL_SPECS)
    # w = pull length, d = projection off the door; its thickness isn't published, so h takes
    # the projection. The 76 mm centre-to-centre is screw spacing, never read as a size.
    assert (dims.w, dims.d, dims.h) == (146.05, 31.75, 31.75)


def test_parse_dims_cabinet_knob_diameter_and_projection():
    dims = sellers._parse_dims(_KNOB_SPECS)
    assert (dims.w, dims.d, dims.h) == (31.75, 28.7, 31.75)


def test_parse_dims_still_none_without_enough_sides():
    """A shelf bracket's only field ("Product Length") and a hinge with no depth stay unknown --
    no guessing from one side."""
    bracket = [{"key": "Dimensions", "value": [{"name": "Product Length (in.)", "value": "8 in"}]}]
    hinge = [
        {
            "key": "Dimensions",
            "value": [
                {"name": "Product Height (in.)", "value": "3.5 in"},
                {"name": "Product Width (in.)", "value": "3.5 in"},
            ],
        }
    ]
    assert sellers._parse_dims(bracket) is None
    assert sellers._parse_dims(hinge) is None


# --- parse_title_sizes (search's last-resort dims, integration finding #15) ----------------


@pytest.mark.parametrize(
    "title,expected",
    [
        # the printed metric wins over the inch figure
        ("(4-Pack) Solid Bar 3 in. (76 mm) Brushed Stainless Steel Pulls", [(76.0, None)]),
        ("Hammercraft 3-2/5 in. (87 mm) Vintage Matte Black Drawer Pull", [(87.0, None)]),
        ("3-3/4 in. Matte Black Solid Cabinet Bar Pulls (10-Pack)", [(95.25, None)]),
        ("10 Pack 1.2 in. Champagne Bronze Round Cabinet Knobs", [(30.48, None)]),
        ("Pull, 96mm", [(96.0, None)]),
        ("1 Pair 22 in. Full Extension Drawer Slide Set", [(558.8, None)]),
        # "x" chains share the last unit
        ("12 in. x 8 in. Black Steel HD Shelf Bracket", [(304.8, None), (203.2, None)]),
        ("10 in. x12 in. Black Steel Shelf Bracket", [(254.0, None), (304.8, None)]),
        ("12 x 8 inch shelf bracket", [(304.8, None), (203.2, None)]),
        ("10-1/4 in. x 1 in. 250 lbs. White Steel Shelf Bracket", [(260.35, None), (25.4, None)]),
        (
            "36 in. W x 24 in. D x 34.5 in. H Base Cabinet",
            [(914.4, "w"), (609.6, "d"), (876.3, "h")],
        ),
        # screw spacing, radii and overlays are never the part's size
        ("European Style 3 in. (76 mm) Center-to-Center Satin Nickel Bar Pull (25-Pack)", []),
        ("Ravinte 30 Pack 5 Inch Cabinet Pulls 3 Inch Hole Center", [(127.0, None)]),
        ("3-1/2 in. Bronze Door Hinge 5/8 in. Corner Radius", [(88.9, None)]),
        ("5 Pair Face Frames 35 mm 105-Degree 1/2 in. Overlay Hinge", [(35.0, None)]),
        ("Center Mount 22 in. Drawer Slide", [(558.8, None)]),  # "center" alone is fine
        # conservative: alternatives, gauges, unitless numbers, several separate sizes
        ("Step Edge 3 or 3-3/4 in. (76/96 mm) Matte Black Drawer Pull", []),
        ("#8 x 1-1/4 in. Phillips Wood Screw", []),
        ("5,000 BTU 115-Volt Window Air Conditioner cools 150 sq. ft.", []),
        ("2 in 1 hinge", []),
        ("Acme 5 in. Hanger for 6 in. Gutters", []),
        (None, []),
        ("", []),
    ],
)
def test_parse_title_sizes(title, expected):
    assert sellers.parse_title_sizes(title) == expected
