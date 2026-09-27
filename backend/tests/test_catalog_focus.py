"""Gaze focus for the catalog (gaze-catalog lane): `GET /catalog?site=&focus=roof|wall|ground|
ceiling|counter|opening` orders and filters the site's categories by what the wearer looks at
(catalog_tables.FOCUS_TABLES, catalog.focus_categories); no focus or an unknown one is today's
list. Offline: detections are written as stored files (the way a warmed server has them), and
`search.find_parts` / `schedule_search` are faked.
"""

import json
import shutil
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from server import assets, cache, catalog, catalog_index, search
from server import catalog_tables as tables
from server.app import app
from server.config import get_settings
from server.models import Dims, Part, Seller

FIX = Path(__file__).parent / "fixtures"

ROOF = set(tables.FOCUS_TABLES["exterior"]["roof"])
WALL = set(tables.FOCUS_TABLES["exterior"]["wall"])


@pytest.fixture(autouse=True)
def _dirs(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    catalog.reset()
    catalog_index.INDEX = catalog_index.Index()
    monkeypatch.setattr(catalog, "INDEX", catalog_index.INDEX)
    yield
    catalog.reset()


@pytest.fixture
def no_background(monkeypatch):
    """Record what would be searched / built / fetched instead of doing it."""
    asked = []
    monkeypatch.setattr(catalog, "schedule_search", lambda q: asked.append(q) or True)
    monkeypatch.setattr(catalog, "queue_model", lambda pid: False)
    monkeypatch.setattr(catalog, "queue_image", lambda doc: False)
    return asked


def _scene(name: str, rev: int = 3) -> Path:
    site = Path(get_settings().SCENE_DIR) / name
    site.mkdir(parents=True)
    (site / "scene.json").write_text(json.dumps({"name": name, "revision": rev}))
    return site


def _stored(site: str, rev: int, environment: str, setting: str, extras=(), also=None, **kw):
    """A detection as a warmed server keeps it (DATA_DIR/catalog/<site>.r<rev>.json)."""
    det = {
        "site": site,
        "rev": rev,
        "environment": environment,
        "confidence": 0.6,
        "also": also,
        "place": kw.get("place", "a building"),
        "setting": setting,
        "signals": [],
        "extras": list(extras),
        "in_scene": kw.get("in_scene", {}),
        "grok": "fresh",
        "version": catalog.VERSION,
    }
    path = Path(get_settings().DATA_DIR) / "catalog" / f"{site}.r{rev}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(det))
    return det


ZABEL_EXTRAS = [
    {"title": "Red Tile Roofs", "query": "roof tiles", "icon": "solar-roof"},
    {"title": "Dormer Windows", "query": "dormer windows", "icon": "app-window"},
]


def _zabel() -> dict:
    _scene("zabel-gymnasium")
    return _stored(
        "zabel-gymnasium",
        3,
        "facade",
        "aerial",
        ZABEL_EXTRAS,
        also="rooftop",
        place="red brick institutional building",
        in_scene={"windows": {"components": [], "seen": ["window"]}},
    )


def _hospital() -> dict:
    _scene("hospital-bg")
    return _stored(
        "hospital-bg",
        3,
        "facade",
        "aerial",
        [
            {"title": "Rooftop HVAC", "query": "rooftop HVAC units", "icon": "fan"},
            {"title": "Roof Vents", "query": "roof vents", "icon": "cloud-rain"},
        ],
        also="rooftop",
    )


def _kitchen() -> dict:
    site = Path(get_settings().SCENE_DIR) / "kitchen"
    site.mkdir(parents=True)
    for f in (FIX / "catalog" / "kitchen").iterdir():
        shutil.copy(f, site / f.name)
    return _stored(
        "kitchen",
        1,
        "kitchen",
        "indoor",
        [
            {"title": "Range Hoods", "query": "range hoods", "icon": "fan"},
            {"title": "Countertops", "query": "kitchen countertops", "icon": "square"},
        ],
        in_scene={
            "dishwashers": {"components": ["dw1"], "seen": ["dishwasher"]},
            "fridges": {"components": ["fr1"], "seen": ["refrigerator"]},
            "ranges": {"components": ["rg1"], "seen": ["electric range"]},
        },
    )


def _part(pid: str, name: str) -> Part:
    return Part(
        id=pid,
        name=name,
        manufacturer="Acme",
        model_no=pid.upper(),
        dims_mm=Dims(w=600, d=650, h=880),
        image_url=f"https://images.example.com/{pid}.jpg",
        sellers=[Seller(name="Home Depot", price_usd=99.0, total_usd=99.0)],
        recommended_seller=0,
    )


def _stock(query: str, n: int = 3) -> list[Part]:
    """Cached search results for `query` (what the index reads), n parts named after it."""
    parts = [_part(f"{tables.slug(query)}-{i}", f"{query} {i}") for i in range(n)]
    dumped = [p.model_dump(mode="json") for p in parts]
    cache.put("search_by_query", cache.normalize(query), dumped)
    cache.put("search", {"q": cache.normalize(query), "m": None}, dumped)
    return parts


def _get(client: TestClient, **params) -> dict:
    r = client.get("/catalog", params=params)
    assert r.status_code == 200, r.text
    return r.json()


# --- the tables -------------------------------------------------------------------------------


def test_every_focus_table_names_known_categories_and_foci():
    for setting, table in tables.FOCUS_TABLES.items():
        for focus, ids in table.items():
            assert focus in tables.FOCI, (setting, focus)
            assert ids and len(ids) == len(set(ids)), (setting, focus)
            for cid in ids:
                assert cid in tables.CATEGORIES, (setting, focus, cid)
    for env in tables.ENVIRONMENTS:
        if env != tables.GENERIC:
            assert tables.focus_setting(env) in tables.FOCUS_TABLES, env


def test_the_exterior_roof_and_wall_lead_with_what_the_user_asked_for():
    roof = tables.focus_ids("facade", "roof")
    wall = tables.focus_ids("facade", "wall")
    assert roof[:2] == ("solar-panels", "gutters")  # "panels, gutters, etc."
    assert wall[:2] == ("wall-hvac", "windows")  # "wall mounted hvacs, window frames, etc."
    assert {"roof-vents", "chimney-caps", "skylights", "flashing", "snow-guards"} <= set(roof)
    assert {"satellite-mounts"} <= set(roof)
    assert {"window-ac", "doors", "siding", "exterior-lights", "wall-vents", "downspouts"} <= set(
        wall
    )
    assert tables.focus_ids("rooftop", "roof") == roof == tables.focus_ids("pavilion", "roof")
    assert set(tables.focus_ids("facade", "ground")) >= {
        "pavers",
        "planters",
        "benches",
        "drainage",
    }


def test_the_kitchen_counter_and_floor():
    assert tables.focus_ids("kitchen", "counter")[:2] == ("microwaves", "sinks-faucets")
    assert "backsplash" in tables.focus_ids("kitchen", "counter")
    assert tables.focus_ids("kitchen", "ground")[:3] == ("dishwashers", "fridges", "ranges")
    assert tables.focus_ids("kitchen", "roof") == ()  # no roof indoors


def test_a_room_keeps_its_own_categories_and_the_shared_ones():
    ground = tables.focus_ids("bathroom", "ground")
    assert "toilets" in ground and "vanities" in ground
    assert "washers" not in ground and "hospital-beds" not in ground  # another room's
    assert "ceiling-lights" in tables.focus_ids("laundry", "ceiling")  # shared


def test_foci_and_their_names():
    assert tables.normalize_focus("Roof") == "roof"
    assert tables.normalize_focus(" floor ") == "ground"
    assert tables.normalize_focus("window") == "opening"
    for nothing in (None, "", "all", "sky", "none"):
        assert tables.normalize_focus(nothing) is None
    assert tables.foci_for("facade") == ["roof", "wall", "ground", "ceiling", "opening"]
    assert tables.foci_for("kitchen") == ["wall", "ground", "ceiling", "counter", "opening"]
    assert tables.foci_for(tables.GENERIC, "aerial") == tables.foci_for("facade")
    assert tables.foci_for(tables.GENERIC, None) == []
    assert tables.focus_setting(tables.GENERIC, "indoor") == "room"


def test_an_extra_goes_where_its_words_put_it():
    assert tables.foci_of_text("Red Tile Roofs roof tiles") == {"roof"}
    assert tables.foci_of_text("Dormer Windows dormer windows") == {"roof"}  # a roof word wins
    assert tables.foci_of_text("Countertops kitchen countertops") == {"counter"}
    assert tables.foci_of_text("Awnings retractable awning") == {"wall"}
    assert tables.foci_of_text("Rope Bridges suspension rope bridges") == {"ground"}
    assert tables.foci_of_text("Water heaters") == set()


def test_new_focus_categories_take_their_own_parts_without_stealing():
    assert tables.classify("Pioneer 12,000 BTU Ductless Mini Split Air Conditioner") == "wall-hvac"
    assert tables.classify("Amana 12,000 BTU PTAC Unit") == "wall-hvac"
    assert tables.classify("5,000 BTU 115-Volt Window Air Conditioner") == "window-ac"
    assert tables.classify("13.4 SEER2 Air Conditioner Condenser") == "hvac"
    assert tables.classify("Vinyl Downspout Extension") == "downspouts"
    assert tables.classify("5 in. Aluminum Hidden Gutter Hanger") == "gutters"
    assert tables.classify("Galvanized Steel Roof Snow Guard") == "snow-guards"
    assert tables.classify("16 in. x 16 in. Concrete Patio Paver") == "pavers"
    assert tables.classify("4 in. Channel Drain with Grate") == "drainage"
    assert tables.classify("Peel and Stick Subway Backsplash Tile") == "backsplash"
    assert tables.classify("60 sq. in. Slant Back Roof Louver") == "roof-vents"


# --- ordering ---------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_zabel_leads_with_roof_categories_on_the_roof_and_wall_ones_on_a_wall():
    det = await catalog.detect(_zabel()["site"])
    roof = [c.id for c, _ in catalog.categories_for(det, "roof")]
    wall = [c.id for c, _ in catalog.categories_for(det, "wall")]
    assert roof[: len(tables.focus_ids("facade", "roof"))] == list(
        tables.focus_ids("facade", "roof")
    )
    # Grok's roof extras follow: its tiles, and its dormer windows (known: Windows & frames)
    assert roof[-2:] == ["x-red-tile-roofs", "windows"]
    assert wall == list(tables.focus_ids("facade", "wall"))  # no roof extra on a wall
    assert catalog.foci(det) == ["roof", "wall", "ground", "ceiling", "opening"]


@pytest.mark.asyncio
async def test_no_focus_or_an_unknown_one_is_todays_list():
    det = await catalog.detect(_zabel()["site"])
    today = [c.id for c, _ in catalog.categories_for(det)]
    assert today[0] == "windows"  # the facade's list, what the scene shows first
    assert [c.id for c, _ in catalog.categories_for(det, None)] == today
    assert catalog.focus_categories(det, "counter") is None  # no counter outdoors
    assert [c.id for c, _ in catalog.categories_for(det, "counter")] == today


@pytest.mark.asyncio
async def test_the_hospital_names_its_rooftop_hvac_on_the_roof():
    det = await catalog.detect(_hospital()["site"])
    pairs = catalog.categories_for(det, "roof")
    ids = [c.id for c, _ in pairs]
    assert ids == list(tables.focus_ids("facade", "roof"))  # Grok's both are curated roof ones
    evidence = {c.id: ev for c, ev in pairs}
    assert "grok: Rooftop HVAC" in evidence["hvac"]["seen"]
    assert "grok: Roof Vents" in evidence["roof-vents"]["seen"]


# --- the endpoint -----------------------------------------------------------------------------


def test_get_catalog_with_focus_orders_stocked_categories_first(no_background):
    _zabel()
    _stock("400W solar panel")
    _stock("gutter hanger")
    _stock("vinyl window")
    _stock("ductless mini split air conditioner")
    with TestClient(app) as client:
        roof = _get(client, site="zabel-gymnasium", focus="roof")
        wall = _get(client, site="zabel-gymnasium", focus="wall")
        plain = _get(client, site="zabel-gymnasium")
        bogus = _get(client, site="zabel-gymnasium", focus="sky")
        floor = _get(client, site="zabel-gymnasium", focus="Floor")
    assert roof["focus"] == "roof" and roof["foci"] == [
        "roof",
        "wall",
        "ground",
        "ceiling",
        "opening",
    ]
    ids = [c["id"] for c in roof["categories"]]
    assert ids[:2] == ["solar-panels", "gutters"]  # stocked, in the table's order
    assert set(ids) == ROOF | {"x-red-tile-roofs", "windows"}
    stocked = [bool(c["items"]) for c in roof["categories"]]
    assert stocked == sorted(stocked, reverse=True)  # the empty ones last
    assert ids[2] == "windows"  # stocked: Grok's dormer windows (after the table's stocked ones)
    assert [c["id"] for c in wall["categories"]][:2] == ["wall-hvac", "windows"]
    assert wall["focus"] == "wall" and {c["id"] for c in wall["categories"]} == WALL
    assert plain["focus"] is None and bogus["focus"] is None
    assert [c["id"] for c in bogus["categories"]] == [c["id"] for c in plain["categories"]]
    assert floor["focus"] == "ground" and floor["categories"][0]["id"] in ("pavers", "planters")


def test_a_focused_list_searches_only_its_first_thin_categories(no_background):
    _zabel()
    with TestClient(app) as client:
        _get(client, site="zabel-gymnasium", focus="roof")
    lead = tables.focus_ids("facade", "roof")[: catalog.FOCUS_SEARCH_LEAD]
    assert no_background == [tables.CATEGORIES[c].query for c in lead]
    no_background.clear()
    with TestClient(app) as client:
        _get(client, site="zabel-gymnasium")  # the unfocused list searches every thin one
    assert len(no_background) == len(
        catalog.categories_for(catalog.known_detection("zabel-gymnasium"))
    )


def test_the_kitchen_floor_and_counter(no_background):
    _kitchen()
    _stock("dishwasher")
    _stock("fridge")
    _stock("over-the-range microwave")
    with TestClient(app) as client:
        ground = _get(client, site="kitchen", focus="ground")
        counter = _get(client, site="kitchen", focus="counter")
        roof = _get(client, site="kitchen", focus="roof")
    assert [c["id"] for c in ground["categories"]][:2] == ["dishwashers", "fridges"]
    assert ground["categories"][0]["components"] == ["dw1"]
    assert counter["categories"][0]["id"] == "microwaves"
    assert "x-countertops" in [c["id"] for c in counter["categories"]]  # Grok's counter extra
    assert roof["focus"] is None  # no roof in a kitchen: today's list


def test_a_focused_answer_from_the_cache_takes_under_10_ms(no_background):
    _zabel()
    for q in ("400W solar panel", "gutter hanger", "roof vent", "vinyl window", "window ac"):
        _stock(q, 8)
    with TestClient(app) as client:
        _get(client, site="zabel-gymnasium", focus="roof")  # the detection is read once
        took = []
        for focus in ("roof", "wall", "ground", "roof", "opening"):
            t0 = time.perf_counter()
            body = _get(client, site="zabel-gymnasium", focus=focus)
            took.append((body["took_ms"], (time.perf_counter() - t0) * 1000))
    assert max(t for t, _ in took) < 10, took


# --- warm -------------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_warm_fills_the_focus_variants_within_a_budget(monkeypatch):
    _zabel()
    searched = []

    async def fake_find_parts(req, progress=lambda s: None):
        searched.append(req.query)
        parts = [_part(f"{tables.slug(req.query)}-{i}", f"{req.query} {i}") for i in range(2)]
        dumped = [p.model_dump(mode="json") for p in parts]
        cache.put("search_by_query", cache.normalize(req.query), dumped)
        return parts

    async def fake_fetch(url, dest):
        return False

    monkeypatch.setattr(search, "find_parts", fake_find_parts)
    monkeypatch.setattr(assets, "fetch_image", fake_fetch)
    lines = []
    totals = await catalog.warm(["zabel-gymnasium"], models=False, echo=lines.append)
    assert totals["focus_lists"] == 5
    text = "\n".join(lines)
    assert "focus=roof:" in text and "focus=wall:" in text
    wanted = set()
    det = catalog.known_detection("zabel-gymnasium")
    for c, _ in catalog.categories_for(det):
        wanted.add(c.query)
    for f in catalog.foci(det):
        for c, _ in (catalog.focus_categories(det, f) or [])[: catalog.FOCUS_SEARCH_LEAD]:
            wanted.add(c.query)
    assert set(searched) == wanted and len(searched) == len(wanted)  # each query once
    assert tables.CATEGORIES["snow-guards"].query not in searched  # 8th on the roof: not warmed
    again = await catalog.warm(["zabel-gymnasium"], models=False, echo=lines.append)
    assert again["searches"] == 0  # idempotent

    catalog.reset()
    searched.clear()
    _stored("scan-k", 1, "kitchen", "indoor")  # nothing of a kitchen searched yet
    _scene("scan-k", rev=1)
    capped = await catalog.warm(["scan-k"], models=False, echo=lines.append, max_searches=2)
    assert capped["searches"] == 2 and len(searched) <= 2
    assert "over budget" in "\n".join(lines)
