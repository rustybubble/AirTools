"""The catalog (server/catalog.py, catalog_index.py, catalog_tables.py): environment detection,
category tables, items from the local caches, background fill, lazy search, endpoint shapes.

The kitchen fixture is the real scan's scene.json + parts.r1.json (tests/fixtures/catalog/kitchen)
with the trimmed structure in tests/fixtures/structure/kitchen. Only xAI is mocked (respx), and
only in the Grok tests; everything else monkeypatches `ask_place`, `search.find_parts` and
`assets.resolve_shared`.
"""

import asyncio
import io
import json
import random
import shutil
import time
from pathlib import Path

import httpx
import pytest
import respx
from fastapi.testclient import TestClient
from PIL import Image

from server import assets, cache, catalog, catalog_index, jobs, search, warm
from server import catalog_tables as tables
from server.app import app
from server.config import get_settings
from server.models import Dims, Part, Seller

FIX = Path(__file__).parent / "fixtures"
XAI_URL = "https://api.x.ai/v1/chat/completions"


@pytest.fixture(autouse=True)
def _dirs(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    catalog.reset()
    catalog_index.INDEX = catalog_index.Index()
    monkeypatch.setattr(catalog, "INDEX", catalog_index.INDEX)
    monkeypatch.setattr(jobs.catalog_index, "INDEX", catalog_index.INDEX)
    yield
    catalog.reset()


@pytest.fixture
def no_grok(monkeypatch):
    async def none(site, meta, things):
        return None

    monkeypatch.setattr(catalog, "ask_place", none)


def _scene_dir() -> Path:
    return Path(get_settings().SCENE_DIR)


def _data_dir() -> Path:
    return Path(get_settings().DATA_DIR)


def _kitchen() -> Path:
    site = _scene_dir() / "kitchen"
    site.mkdir(parents=True)
    for f in (FIX / "catalog" / "kitchen").iterdir():
        shutil.copy(f, site / f.name)
    shutil.copy(FIX / "structure" / "kitchen" / "structure.r1.json", site / "structure.r1.json")
    return site


def _jpg(shade: int) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (64, 36), (shade, shade, 200 - shade)).save(buf, "JPEG")
    return buf.getvalue()


def _site(name: str, structure: dict | None = None, thumbs: int = 0, **meta) -> Path:
    site = _scene_dir() / name
    site.mkdir(parents=True)
    scene = {"name": name, "revision": 1, **meta}
    if structure is not None:
        (site / "structure.r1.json").write_text(json.dumps(structure))
        scene["structure"] = {"file": "structure.r1.json"}
    if thumbs:
        (site / "thumbs").mkdir()
        cams = []
        for i in range(thumbs):
            (site / "thumbs" / f"{i:04d}.jpg").write_bytes(_jpg(40 + i * 10))
            cams.append({"id": f"{i:04d}", "thumb": f"thumbs/{i:04d}.jpg"})
        (site / "cameras.r1.json").write_text(json.dumps(cams))
        scene["cameras"] = "cameras.r1.json"
    (site / "scene.json").write_text(json.dumps(scene))
    return site


def _plane(normal, pts, area=None) -> dict:
    p = {"id": f"p{random.random()}", "normal": normal, "polygon": pts}
    if area is not None:
        p["area_m2"] = area
    return p


def _quad(y0, y1, x=(0.0, 10.0), z=(0.0, 10.0)):
    """A rectangle from (x0, y0, z0) to (x1, y1, z1) -- sloped when y0 != y1."""
    return [[x[0], y0, z[0]], [x[1], y0, z[0]], [x[1], y1, z[1]], [x[0], y1, z[1]]]


def _part(pid: str, name: str, price: float = 100.0, image: str | None = None, **kw) -> Part:
    return Part(
        id=pid,
        name=name,
        manufacturer=kw.pop("manufacturer", "Acme"),
        model_no=kw.pop("model_no", pid.upper()),
        dims_mm=Dims(w=600, d=650, h=880),
        image_url=image or f"https://images.example.com/{pid}.jpg",
        sellers=[Seller(name="Home Depot", price_usd=price, total_usd=price)],
        recommended_seller=0,
        **kw,
    )


def _save(part: Part, model: bool = False, image: bool = False) -> None:
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True, exist_ok=True)
    if model:
        part.asset.status = "ready"
        (pdir / "model.glb").write_bytes(b"glb")
    if image:
        (pdir / "image.jpg").write_bytes(_jpg(90))
    (pdir / "part.json").write_text(part.model_dump_json())


async def _background_done(timeout_s: float = 3.0) -> None:
    """Until the catalog's search, model and photo workers have all finished."""
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        workers = (catalog._search_worker, catalog._model_worker, catalog._image_worker)
        if all(w is None or w.done() for w in workers):
            return
        await asyncio.sleep(0.01)
    raise AssertionError("background work did not finish")


def _cache_search(query: str, parts: list[Part]) -> None:
    dumped = [p.model_dump(mode="json") for p in parts]
    cache.put("search_by_query", cache.normalize(query), dumped)
    cache.put("search", {"q": cache.normalize(query), "m": None}, dumped)


# --- environment detection ----------------------------------------------------------------


@pytest.mark.asyncio
async def test_the_real_kitchen_scan_is_a_kitchen_from_its_parts_file(no_grok):
    _kitchen()
    det = await catalog.detect("kitchen")
    assert det["environment"] == "kitchen"
    parts = next(s for s in det["signals"] if s["source"] == "parts")
    assert parts["env"] == "kitchen" and parts["weight"] == 4.5  # 3 appliances + a cabinet, capped
    assert parts["why"] == "base cabinet, dishwasher, fridge, range"
    assert det["confidence"] >= 0.8
    assert not any(s["env"] == "facade" for s in det["signals"])  # cabinet doors aren't doors
    assert det["in_scene"]["dishwashers"]["components"] == ["dw1"]
    assert det["in_scene"]["fridges"]["components"] == ["fr1"]
    assert det["in_scene"]["ranges"]["components"] == ["rg1"]
    order = [c.id for c, _ in catalog.categories_for(det)]
    assert order[:3] == ["fridges", "dishwashers", "ranges"]  # what the scan shows first
    assert set(order) == set(tables.ENVIRONMENTS["kitchen"].categories)


@pytest.mark.asyncio
async def test_a_kitchen_with_only_its_parts_file_needs_no_name(no_grok):
    site = _kitchen()
    scene = json.loads((site / "scene.json").read_text())
    scene["name"] = "scan-0042"
    (site / "scene.json").write_text(json.dumps(scene))
    det = await catalog.detect("kitchen")
    assert det["environment"] == "kitchen"
    assert not any(s["source"] == "name" for s in det["signals"])


@pytest.mark.asyncio
async def test_large_sloped_planes_are_a_pitched_roof(no_grok):
    structure = {
        "planes": [
            _plane([0, 0.72, 0.69], _quad(8, 12), 150),
            _plane([0, 0.72, -0.69], _quad(12, 8, z=(10, 20)), 150),
            _plane([0, 1, 0], _quad(0, 0), 120),  # the ground
            _plane([1, 0, 0], [[0, 0, 0], [0, 8, 0], [0, 8, 10], [0, 0, 10]], 80),
        ],
        "objects": [{"label": "window"}] * 6,
    }
    _site("scan-7", structure)
    det = await catalog.detect("scan-7")
    assert det["environment"] == "rooftop"
    roof = next(s for s in det["signals"] if s["source"] == "structure")
    assert "pitched roof" in roof["why"]
    assert [c.id for c, _ in catalog.categories_for(det)][:3] == ["gutters", "hvac", "solar-panels"]


@pytest.mark.asyncio
async def test_large_level_planes_high_above_the_ground_are_flat_roofs(no_grok):
    structure = {
        "planes": [
            _plane([0, 1, 0], _quad(0, 0, x=(0, 60), z=(0, 60))),  # ground, area from polygon
            _plane([0, -1, 0], _quad(24, 24, x=(10, 40), z=(10, 40))),  # the roof, 900 m²
            _plane([0, 1, 0], _quad(27, 27, x=(12, 15), z=(12, 15))),  # a roof unit
        ]
    }
    _site("hospital-block", structure)
    det = await catalog.detect("hospital-block")
    assert det["environment"] == "rooftop"  # the building word only hints; the roof decides
    assert any("flat roofs" in s["why"] for s in det["signals"])
    assert any(s["env"] == "hospital" and s["source"] == "name" for s in det["signals"])


@pytest.mark.asyncio
async def test_a_room_sized_scan_with_no_evidence_is_generic(no_grok):
    _site("scan-9", {"planes": [_plane([0, 1, 0], _quad(0, 0, x=(0, 3), z=(0, 3)))]})
    det = await catalog.detect("scan-9")
    assert det["environment"] == "generic" and det["confidence"] == 0.0
    assert catalog.categories_for(det)[0][0].id == "windows"


@pytest.mark.asyncio
async def test_the_site_name_votes(no_grok):
    for name, env in [
        ("gt-lcc-canopy", "pavilion"),
        ("synthetic-facade", "facade"),
        ("my-garage", "garage"),
        ("roof-3", "rooftop"),
    ]:
        _site(name)
        assert (await catalog.detect(name))["environment"] == env, name


def test_scene_things_vote_for_their_environment():
    assert tables.env_votes_for_thing("dishwasher") == [("kitchen", 1.5)]
    assert tables.env_votes_for_thing("cabinet door") == [("kitchen", 0.5)]
    assert tables.env_votes_for_thing("window") == [("facade", 0.25)]
    assert tables.env_votes_for_thing("microwave oven") == [("kitchen", 1.5)]
    assert tables.env_votes_for_thing("toilet") == [("bathroom", 1.5)]
    assert tables.env_votes_for_thing("workbench") == [("garage", 1.5)]


# --- Grok: the place call ---------------------------------------------------------------------


def _xai_reply(place: dict, ticks: int = 15_000_000) -> httpx.Response:
    return httpx.Response(
        200,
        json={
            "id": "x",
            "object": "chat.completion",
            "created": 0,
            "model": "grok-4.20-0309-non-reasoning",
            "choices": [
                {
                    "index": 0,
                    "finish_reason": "stop",
                    "message": {"role": "assistant", "content": json.dumps(place)},
                }
            ],
            "usage": {
                "prompt_tokens": 1500,
                "completion_tokens": 60,
                "total_tokens": 1560,
                "cost_in_usd_ticks": ticks,
            },
        },
    )


PAVILION = {
    "environment": "pavilion",
    "confidence": 0.9,
    "setting": "aerial",
    "place": "pavilion with polycarbonate roof",
    "extras": [
        {"title": "Polycarbonate panels", "query": "polycarbonate roof panel", "icon": "grid-four"},
        {"title": "Bike racks", "query": "bike rack", "icon": "wrench"},
        {"title": "Third one", "query": "flag pole", "icon": "not-an-icon"},
    ],
}


@pytest.mark.asyncio
@respx.mock
async def test_grok_names_the_place_once_per_revision_and_adds_site_extras():
    route = respx.post(XAI_URL).mock(return_value=_xai_reply(PAVILION))
    _site("gt-lcc-tower", thumbs=9)
    det = await catalog.detect("gt-lcc-tower")
    assert det["environment"] == "pavilion" and det["grok"] == "fresh"
    assert det["place"] == "pavilion with polycarbonate roof"
    assert det["grok_cost_usd"] == 0.0015
    body = json.loads(route.calls[0].request.content)
    images = [c for c in body["messages"][0]["content"] if c["type"] == "image_url"]
    assert len(images) == 2 and images[0]["image_url"]["url"].startswith("data:image/jpeg")
    assert body["model"] == "grok-4.20-0309-non-reasoning"
    assert body["response_format"]["json_schema"]["strict"] is True
    assert "pavilion:" in body["messages"][0]["content"][0]["text"]

    cats = [c for c, _ in catalog.categories_for(det)]
    ids = [c.id for c in cats]
    # "polycarbonate roof panel" is the curated roof-panels, already listed: Grok saw it, so it
    # comes first. "bike rack" is new: a site extra with its own query. Only 2 extras are read.
    assert ids[0] == "roof-panels"
    assert sorted(ids[:7]) == sorted(tables.ENVIRONMENTS["pavilion"].categories)
    assert ids[7:] == ["x-bike-racks"]
    assert cats[7].query == "bike rack" and cats[7].extra and cats[7].icon == "wrench"

    # stored per revision: a new process (memo gone) never asks again
    catalog._detections.clear()
    catalog._place_tasks.clear()
    again = await catalog.detect("gt-lcc-tower")
    assert again["environment"] == "pavilion" and route.call_count == 1
    assert again["grok"] == "stored" and again["grok_cost_usd"] == 0.0
    stored = _data_dir() / "catalog" / "gt-lcc-tower.r1.json"
    assert json.loads(stored.read_text())["grok"] == "fresh"

    # the stored detection gone too: the disk cache answers, still one call
    stored.unlink()
    catalog.reset()
    third = await catalog.detect("gt-lcc-tower")
    assert third["grok"] == "cached" and route.call_count == 1


@pytest.mark.asyncio
async def test_a_slow_grok_answer_does_not_hold_the_catalog(monkeypatch):
    gate = asyncio.Event()

    async def slow(site, meta, things):
        await gate.wait()
        return {
            "place": {**PAVILION, "extras": []},
            "cached": False,
            "cost_usd": 0.001,
            "latency_ms": 5000,
        }

    monkeypatch.setattr(catalog, "ask_place", slow)
    _site("gt-lcc-canopy")
    first = await catalog.detect("gt-lcc-canopy", wait_s=0.01)
    assert first["grok"] == "pending" and first["environment"] == "pavilion"  # from the name
    start = time.monotonic()
    second = await catalog.detect("gt-lcc-canopy")  # later calls never wait
    assert time.monotonic() - start < 0.5 and second["grok"] == "pending"
    gate.set()
    await asyncio.sleep(0)
    third = await catalog.detect("gt-lcc-canopy")
    assert third["grok"] == "fresh" and any(s["source"] == "grok" for s in third["signals"])


@pytest.mark.asyncio
@respx.mock
async def test_offline_the_place_is_never_asked(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    route = respx.post(XAI_URL).mock(return_value=_xai_reply(PAVILION))
    _site("gt-lcc-pavilion", thumbs=3)
    det = await catalog.detect("gt-lcc-pavilion")
    assert det["grok"] == "offline" and det["environment"] == "pavilion"
    assert not route.called
    assert not (_data_dir() / "catalog" / "gt-lcc-pavilion.r1.json").exists()


@pytest.mark.asyncio
@respx.mock
async def test_a_failed_grok_call_falls_back_to_the_cheap_signals():
    respx.post(XAI_URL).mock(return_value=httpx.Response(400, json={"error": "bad"}))
    site = _kitchen()
    (site / "thumbs").mkdir()
    (site / "thumbs" / "0001.jpg").write_bytes(_jpg(80))  # no cameras file: the thumbs dir
    det = await catalog.detect("kitchen")
    assert det["grok"] == "failed" and det["environment"] == "kitchen"


def test_thumbs_are_spread_over_the_flight():
    _site("s", thumbs=9)
    picks = catalog.pick_thumbs("s", json.loads((_scene_dir() / "s" / "scene.json").read_text()))
    assert [p.name for p in picks] == ["0003.jpg", "0006.jpg"]


# --- the category tables --------------------------------------------------------------------


def test_every_environment_lists_known_categories_with_known_icons():
    assert len(tables.CATEGORIES) == len({c.id for c in tables._CATEGORY_LIST})
    for env in tables.ENVIRONMENTS.values():
        assert 5 <= len(env.categories) <= 9, env.id
        for cid in env.categories:
            assert cid in tables.CATEGORIES, (env.id, cid)
    for cat in tables.CATEGORIES.values():
        assert cat.icon in tables.ICON_CODEPOINTS, cat.id
    for icon, cp in tables.ICON_CODEPOINTS.items():
        assert len(cp) == 4 and 0xE000 <= int(cp, 16) <= 0xEFFF, icon


def test_the_briefs_categories_are_there():
    kitchen = set(tables.ENVIRONMENTS["kitchen"].categories)
    assert {
        "fridges",
        "dishwashers",
        "ranges",
        "microwaves",
        "cooktops",
        "range-hoods",
        "sinks-faucets",
        "cabinet-hardware",
        "lighting",
    } <= kitchen
    roof = set(tables.ENVIRONMENTS["rooftop"].categories)
    assert {
        "gutters",
        "hvac",
        "solar-panels",
        "chimney-caps",
        "roof-vents",
        "skylights",
        "flashing",
    } <= roof
    facade = set(tables.ENVIRONMENTS["facade"].categories)
    assert {"windows", "doors", "siding", "gutters", "exterior-lights"} <= facade
    pavilion = set(tables.ENVIRONMENTS["pavilion"].categories)
    assert {"shade-sails", "benches", "outdoor-lighting"} <= pavilion
    for env in ("gym", "hospital", "generic", "bathroom"):
        assert env in tables.ENVIRONMENTS


def test_each_category_owns_its_own_query_aliases_and_title():
    for cat in tables.CATEGORIES.values():
        for text in (cat.query, *cat.aliases, cat.title):
            assert tables.classify(text) == cat.id, (cat.id, text)


RANGE = "Gallery 30 in. 5 Element Slide-In Induction Range in Smudge-Proof Stainless Steel"
WINDOW_AC = (
    "8,000 BTU U+ Smart Inverter Window Air Conditioner for Rooms up to 350 Sq. Ft. with Dual "
    "Filter, Remote in White"
)
WINDOW = (
    "59.5 in. x 47.5 in. V-4500 Series White Vinyl Right-Handed Sliding Window with Fiberglass "
    "Mesh Screen"
)
DISHWASHER = (
    "24 in. Top Control Built-In Tall Tub 55 dBA Dishwasher in White with Boost Cycle and Third "
    "Rack"
)
TOY = "1/12 Scale Mini Gym Equipment Set Barbell Bench Press & Squat Rack Model for Desktop"
HINGES = "Pivot Door Hinges - Rear Pivoting - High Quality Steel - Statuary Bronze Finish"
STRIP = "RibbonFlex Pro 32.8 ft. 12-Volt White Tape Strip Light 60 LEDs/m Soft White (2700K)"


@pytest.mark.parametrize(
    ("name", "category"),
    [  # real product names from the demo's search cache
        (RANGE, "ranges"),
        (WINDOW_AC, "window-ac"),
        (WINDOW, "windows"),
        ("5 in. White Vinyl K-Style Hidden Gutter Hanger", "gutters"),
        (DISHWASHER, "dishwashers"),
        ("Mainstays 3.2 Cu. Ft. 2-Door Refrigerator with Freezer", "fridges"),
        (HINGES, "cabinet-hardware"),
        (STRIP, "lighting"),
        (TOY, None),
        # and a few from each environment
        ("1.7 cu. ft. Over the Range Microwave in Stainless Steel", "microwaves"),
        ("30 in. Convertible Under Cabinet Range Hood with LED Light", "range-hoods"),
        ("Water Filter for Refrigerator", None),
        ("Galvanized Steel Chimney Cap 13 in. x 13 in.", "chimney-caps"),
        ("400-Watt Monocrystalline Solar Panel", "solar-panels"),
        ("Solar LED Path Light (6-Pack)", "outdoor-lighting"),
        ("36 in. x 80 in. Steel Prehung Front Entry Door", "doors"),
        ("Kwikset Door Knob", "cabinet-hardware"),
        ("Rectangle Shade Sail 10 ft. x 13 ft.", "shade-sails"),
        ("6 ft. Wood Picnic Table", "picnic-tables"),
        ("Two-Piece Elongated Toilet in White", "toilets"),
        ("Front Load Washer with Steam", "washers"),
        ("Box of 100 Flat Washers", None),
        ("Semi-Electric Hospital Bed with Rails", "hospital-beds"),
    ],
)
def test_product_names_land_in_their_category(name, category):
    assert tables.classify(name) == category


def test_a_close_runner_up_adds_two_of_its_categories():
    signals = [
        catalog._signal("structure", "rooftop", 2.0, "pitched roof"),
        catalog._signal("objects", "facade", 1.2, "window"),
    ]
    env, conf, _, also = catalog.combine(signals, None)
    assert (env, also) == ("rooftop", "facade") and conf == 0.62
    det = {"environment": env, "also": also, "in_scene": {"doors": {"seen": ["door"]}}}
    cats = catalog.categories_for(det)
    ids = [c.id for c, _ in cats]
    assert ids[:7] == list(tables.ENVIRONMENTS["rooftop"].categories)
    assert ids[7:] == ["doors", "windows"]  # the one the scene shows first; gutters is listed
    assert cats[7][1]["also"] and cats[7][1]["seen"] == ["door"]
    far = [catalog._signal("structure", "rooftop", 3.0, "x"), catalog._signal("n", "facade", 1, "")]
    assert catalog.combine(far, None)[3] is None


def test_supplies_and_decor_are_no_site_extras():
    det = {
        "environment": "kitchen",
        "extras": [
            {"title": "Cleaning Supplies", "query": "kitchen cleaners and sponges", "icon": "drop"},
            {
                "title": "Countertop Items",
                "query": "kitchen countertop organizers",
                "icon": "table",
            },
        ],
    }
    ids = [c.id for c, _ in catalog.categories_for(det)]
    assert ids == list(tables.ENVIRONMENTS["kitchen"].categories)
    assert not tables.not_an_extra("Rooftop exhaust fans upblast exhaust fan")


def test_a_grok_extra_gets_keywords_from_its_query():
    cat = tables.extra_category("Bike racks", "bike racks", "wrench")
    assert cat.id == "x-bike-racks" and cat.extra
    assert tables.match_strength(cat, "Steel 5-Bike Rack, Galvanized") > 0
    assert tables.match_strength(cat, "Road Bike Helmet") == 0
    assert tables.extra_category("x", "y", "nope").icon == tables.DEFAULT_ICON


# --- items from the caches -----------------------------------------------------------------------


def _seed_kitchen_parts() -> None:
    _save(_part("fr-a", "Top-Freezer Refrigerator 18 cu. ft.", 598.0), model=True, image=True)
    _save(_part("fr-b", "French Door Refrigerator 27 cu. ft.", 1899.0), model=True, image=True)
    _save(_part("fr-c", "Mini Fridge 3.2 cu. ft.", 139.0))
    _cache_search(
        "dishwasher",
        [
            _part("dw-a", "24 in. Built-In Dishwasher", 498.0),
            _part("dw-b", "18 in. Compact Dishwasher", 429.0),
        ],
    )
    _cache_search("gym model", [_part("toy", "1/12 Scale Mini Gym Equipment Model")])


@pytest.mark.asyncio
async def test_items_come_from_part_json_and_the_search_cache(no_grok, monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")  # nothing in the background
    get_settings.cache_clear()
    _kitchen()
    _seed_kitchen_parts()
    body = await catalog.build("kitchen")
    cats = {c["id"]: c for c in body["categories"]}
    fridges = cats["fridges"]["items"]
    assert [i["part_id"] for i in fridges] == ["fr-a", "fr-b", "fr-c"]  # ready models first
    assert [i["model_ready"] for i in fridges] == [True, True, False]
    assert fridges[0]["image_url"] == "/parts/fr-a/image.jpg"
    assert fridges[2]["image_url"] == "https://images.example.com/fr-c.jpg"  # still remote
    assert fridges[0]["model_url"] == "/parts/fr-a/model.glb"
    assert fridges[0]["dims_mm"] == {"w": 600.0, "h": 880.0, "d": 650.0}
    assert fridges[0]["price_usd"] == 598.0 and fridges[0]["seller"] == "Home Depot"
    dish = cats["dishwashers"]["items"]
    assert [i["part_id"] for i in dish] == ["dw-a", "dw-b"]  # the cached search's own order
    # a part known only from a cached search gets its part.json, so its endpoints work
    assert (assets.part_dir("dw-a") / "part.json").is_file()
    assert all("toy" not in i["part_id"] for c in body["categories"] for i in c["items"])
    assert body["pending"] == 0  # offline: nothing is searched


@pytest.mark.asyncio
async def test_thin_categories_are_searched_in_the_background_and_fill_in(no_grok, monkeypatch):
    _kitchen()
    _seed_kitchen_parts()
    searched = []

    async def fake_find_parts(req, progress=lambda s: None):
        searched.append((req.query, req.max_candidates, req.lite))
        await asyncio.sleep(0)
        slug = tables.slug(req.query)
        return [_part(f"{slug}-{i}", f"{req.query} {i}") for i in range(4)]

    built = []

    async def fake_resolve(part, **kw):
        built.append(part.id)
        (assets.part_dir(part.id) / "model.glb").write_bytes(b"glb")
        part.asset.status = "ready"
        jobs.save_part(part)
        return part

    fetched = []

    async def fake_fetch(url, dest):
        fetched.append(url)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(_jpg(10))
        return True

    monkeypatch.setattr(search, "find_parts", fake_find_parts)
    monkeypatch.setattr(assets, "resolve_shared", fake_resolve)
    monkeypatch.setattr(assets, "fetch_image", fake_fetch)

    first = await catalog.build("kitchen")
    cats = {c["id"]: c for c in first["categories"]}
    assert cats["microwaves"]["items"] == [] and cats["microwaves"]["pending"]
    assert not cats["fridges"]["pending"]  # 3 known fridges: not searched
    assert not cats["dishwashers"]["pending"]  # 2 items, but "dishwasher" is cached already
    assert {c for c, v in cats.items() if v["pending"]} == {
        "ranges",
        "microwaves",
        "cooktops",
        "range-hoods",
        "sinks-faucets",
        "cabinet-hardware",
        "lighting",
    }
    assert first["pending"] == 7
    await _background_done()
    assert ("over-the-range microwave", 4, True) in searched
    assert ("dishwasher", 4, True) not in searched  # its query is cached already
    second = await catalog.build("kitchen")
    cats = {c["id"]: c for c in second["categories"]}
    micro = cats["microwaves"]["items"]
    assert len(micro) == 4 and not cats["microwaves"]["pending"]
    assert [i["model_ready"] for i in micro[:3]] == [True, True, True]  # first 3 built
    assert micro[3]["model_ready"] is False
    assert micro[0]["image_url"].startswith("/parts/")  # photos fetched
    assert "fr-c" in built  # the one fridge without a model got it
    assert second["pending"] == 0


@pytest.mark.asyncio
async def test_a_category_search_that_fails_is_not_retried_at_once(no_grok, monkeypatch):
    _kitchen()
    calls = []

    async def boom(req, progress=lambda s: None):
        calls.append(req.query)
        raise RuntimeError("SerpApi down")

    monkeypatch.setattr(search, "find_parts", boom)
    assert catalog.schedule_search("chimney cap")
    await asyncio.sleep(0.01)
    assert catalog.search_state("chimney cap") == "failed"
    assert not catalog.schedule_search("chimney cap")
    assert calls == ["chimney cap"]


@pytest.mark.asyncio
async def test_a_search_that_finds_nothing_is_not_bought_again(no_grok, monkeypatch):
    calls = []

    async def nothing(req, progress=lambda s: None):
        calls.append(req.query)
        return []

    monkeypatch.setattr(search, "find_parts", nothing)
    assert catalog.schedule_search("outdoor string lights")
    await _background_done()
    assert catalog.search_state("outdoor string lights") == "empty"
    catalog.reset()  # a new process: the disk still knows
    assert catalog.searched_already("outdoor string lights")
    assert not catalog.schedule_search("outdoor string lights")
    assert calls == ["outdoor string lights"]


# --- the lazy search -----------------------------------------------------------------------------


def _index_of(names: list[tuple[str, str, str | None]]) -> catalog_index.Index:
    idx = catalog_index.Index()
    idx.add_parts(
        [
            _part(pid, name, manufacturer=brand).model_dump(mode="json")
            for pid, name, brand in names
        ],
        from_part_json=True,
    )
    return idx


SAMPLE = [
    ("fr1", "Top-Freezer Refrigerator in White", "Frigidaire"),
    ("fr2", "Counter-Depth French Door Refrigerator", "LG"),
    ("dw1", "24 in. Built-In Tall Tub Dishwasher", "Frigidaire"),
    ("dw2", "Countertop Dishwasher, 6 Place Settings", "BLACK+DECKER"),
    ("kn1", "Matte Black Square Cabinet Knob (10-Pack)", "Liberty"),
    ("kn2", "Satin Nickel Round Cabinet Knob", "Liberty"),
    ("gh1", "5 in. Aluminum Hidden Gutter Hanger", "Amerimax"),
    ("mw1", "Over-the-Range Microwave with Sensor Cooking", "GE"),
]


def _ids(hits) -> list[str]:
    return [doc.part_id for doc, _ in hits]


def test_search_ranks_exact_then_prefix_then_typo():
    idx = _index_of(SAMPLE)
    assert _ids(idx.search("dishwasher"))[:2] == ["dw1", "dw2"]
    assert set(_ids(idx.search("dish"))) == {"dw1", "dw2"}  # the word being typed
    assert set(_ids(idx.search("dishwaser"))) == {"dw1", "dw2"}  # a typo
    assert set(_ids(idx.search("refridgerator"))) == {"fr1", "fr2"}
    assert set(_ids(idx.search("refrig"))) == {"fr1", "fr2"}
    assert set(_ids(idx.search("fridge"))) == {"fr1", "fr2"}  # the category's word
    exact = idx.search("refrigerator")[0][1]
    prefix = idx.search("refrig")[0][1]
    typo = idx.search("refridgerator")[0][1]
    assert exact > prefix > typo


def test_search_needs_every_word_and_knows_brands_and_models():
    idx = _index_of(SAMPLE)
    assert _ids(idx.search("black knob")) == ["kn1"]
    assert _ids(idx.search("liberty knob"))[:2] in (["kn1", "kn2"], ["kn2", "kn1"])
    assert _ids(idx.search("frigidaire dish")) == ["dw1"]
    assert _ids(idx.search("DW2")) == ["dw2"]  # the model number
    assert idx.search("zzzz") == []
    assert idx.search("") == []
    # no part has every word: the ones with most of them, whole words only
    assert _ids(idx.search("gutter knob"))[0] in ("gh1", "kn1", "kn2")
    lite = _index_of([*SAMPLE, ("pl1", "Lightweight Resin Planter", "Eden")])
    assert _ids(lite.search("string light")) == []  # not "lightweight"
    assert _ids(lite.search("f")) and "for" not in lite._state.inv  # stop words aren't indexed


def test_search_boosts_the_sites_categories():
    idx = _index_of(
        [
            ("gh1", "5 in. Aluminum Hidden Gutter Hanger", "Amerimax"),
            ("wf1", "Aluminum Sliding Window 36 in.", "Pella"),
        ]
    )
    assert _ids(idx.search("alumin", boost=frozenset({"gutters"})))[0] == "gh1"
    assert _ids(idx.search("alumin", boost=frozenset({"windows"})))[0] == "wf1"


def test_search_stays_under_150_ms_on_2000_parts():
    rng = random.Random(7)
    brands = [
        "Frigidaire",
        "LG",
        "GE",
        "Whirlpool",
        "Samsung",
        "Amerimax",
        "Liberty",
        "Everbilt",
        "Jeld-Wen",
        "Pella",
        "Broan",
        "Moen",
        "Delta",
        "Kohler",
        "Hampton Bay",
        "Lithonia",
    ]
    nouns = [
        "Refrigerator",
        "Dishwasher",
        "Range",
        "Microwave",
        "Cooktop",
        "Range Hood",
        "Kitchen Faucet",
        "Cabinet Knob",
        "Gutter Hanger",
        "Solar Panel",
        "Chimney Cap",
        "Skylight",
        "Vinyl Window",
        "Entry Door",
        "Shade Sail",
        "Picnic Table",
        "Toilet",
        "Ceiling Fan",
        "Smoke Detector",
        "Water Heater",
    ]
    adjs = [
        "Stainless Steel",
        "Matte Black",
        "White",
        "Smart",
        "Compact",
        "Counter-Depth",
        "Energy Star",
        "Brushed Nickel",
        "Galvanized",
        "Heavy Duty",
        "Aluminum",
        "LED",
    ]
    parts = []
    for i in range(2000):
        name = (
            f"{rng.randint(10, 48)} in. {rng.choice(adjs)} {rng.choice(adjs)} "
            f"{rng.choice(nouns)} with {rng.choice(adjs)} Finish Model {i}"
        )
        parts.append(_part(f"p{i}", name, manufacturer=rng.choice(brands)).model_dump(mode="json"))
    idx = catalog_index.Index()
    start = time.perf_counter()
    for chunk in range(0, 2000, 100):
        idx.add_parts(parts[chunk : chunk + 100], query=f"query {chunk}", from_part_json=True)
    build_ms = (time.perf_counter() - start) * 1000
    worst = 0.0
    for q in [
        "refrigerator",
        "refrig",
        "refridgerator",
        "stainless dish",
        "matte black knob",
        "frigidaire",
        "gutter hang",
        "solar",
        "wat",
        "zzz",
        "counter depth fridge",
        "brushed nickel faucet",
        "e",
        "galvanised chimney",
    ]:
        start = time.perf_counter()
        hits = idx.search(q, 20)
        worst = max(worst, (time.perf_counter() - start) * 1000)
        assert q in ("zzz",) or hits, q
    assert worst < 150, f"worst query {worst:.0f} ms"
    assert build_ms < 5000


def test_the_index_loads_part_json_and_caches_from_disk_and_rescans_changes():
    _save(_part("fr-a", "Top-Freezer Refrigerator"), model=True)
    _cache_search(
        "fridge", [_part("fr-a", "Top-Freezer Refrigerator"), _part("fr-z", "Garage Refrigerator")]
    )
    idx = catalog_index.Index()
    assert idx.load() == 3  # a part.json and two cache files
    assert len(idx) == 2
    assert idx.get("fr-a").has_part_json and idx.get("fr-a").ready_hint
    assert idx.get("fr-a").queries == {"fridge": 0}
    assert idx.get("fr-z").category == "fridges" and not idx.get("fr-z").has_part_json
    assert idx.load() == 0  # nothing changed
    time.sleep(0.01)
    _save(_part("fr-z", "Garage Refrigerator 18 cu. ft."))
    assert idx.load() == 1
    assert idx.get("fr-z").has_part_json and idx.get("fr-z").name.endswith("18 cu. ft.")
    assert idx.get("fr-z").queries == {"fridge": 1}  # the search that found it is kept


@pytest.mark.asyncio
async def test_a_finished_search_job_is_searchable_at_once(monkeypatch):
    async def fake_find_parts(req, progress=lambda s: None):
        return [_part("chim-1", "Galvanized Chimney Cap")]

    async def no_assets(parts):
        return None

    monkeypatch.setattr(search, "find_parts", fake_find_parts)
    monkeypatch.setattr(jobs, "_resolve_assets", no_assets)
    from server.models import SearchRequest

    job = jobs.start_search(SearchRequest(query="chimney cap"))
    await jobs.wait(job.id, 2)
    doc = catalog_index.INDEX.get("chim-1")
    assert doc is not None and doc.queries == {"chimney cap": 0}
    assert _ids(catalog_index.INDEX.search("chimn")) == ["chim-1"]


# --- endpoints ----------------------------------------------------------------------------------


ITEM_KEYS = {
    "part_id",
    "name",
    "brand",
    "category",
    "price_usd",
    "dims_mm",
    "image_url",
    "model_url",
    "model_ready",
    "seller",
}


def test_catalog_endpoint_shape(no_grok, monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    _kitchen()
    _seed_kitchen_parts()
    client = TestClient(app)
    r = client.get("/catalog", params={"site": "kitchen", "session_id": "s1"})
    assert r.status_code == 200
    body = r.json()
    assert {"site", "environment", "title", "generated_at", "categories"} <= body.keys()
    assert body["site"] == "kitchen" and body["environment"] == "kitchen"
    assert body["title"] == "Kitchen"
    cat = body["categories"][0]
    assert {"id", "title", "icon", "query", "items", "pending", "in_scene"} <= cat.keys()
    assert cat["id"] == "fridges" and cat["icon"] == "snowflake" and cat["in_scene"]
    assert cat["components"] == ["fr1"]
    item = cat["items"][0]
    assert set(item) == ITEM_KEYS
    assert set(item["dims_mm"]) == {"w", "h", "d"}
    assert client.get("/catalog", params={"site": "nope"}).status_code == 404
    generic = client.get("/catalog").json()
    assert generic["environment"] == "generic" and generic["site"] is None


def test_catalog_search_endpoint_shape_and_category_hints(no_grok, monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    _kitchen()
    _seed_kitchen_parts()
    client = TestClient(app)
    client.get("/catalog", params={"site": "kitchen"})  # the site's detection, for the boost
    body = client.get("/catalog/search", params={"q": "fri", "site": "kitchen"}).json()
    assert body["q"] == "fri"
    ids = [i["part_id"] for i in body["items"]]
    assert ids[0] == "fr-c"  # "Mini Fridge": its name holds the word; the others via "Fridges"
    assert set(ids[:3]) == {"fr-a", "fr-b", "fr-c"}
    assert set(body["items"][0]) == ITEM_KEYS
    assert body["categories"][0] == "Fridges" and body["category_ids"][0] == "fridges"
    assert body["took_ms"] < 150
    limited = client.get("/catalog/search", params={"q": "dishwasher", "limit": 1}).json()
    assert len(limited["items"]) == 1
    assert client.get("/catalog/search", params={"q": ""}).json()["items"] == []


# --- lite searches, warm -------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_a_lite_search_makes_no_store_calls(monkeypatch):
    async def no_stores(token):
        raise AssertionError("a lite search must not call stores()")

    monkeypatch.setattr(search.sellers, "stores", no_stores)
    listings = [
        {
            "immersive_token": "tok",
            "title": "Cap",
            "source": "Lowe's",
            "price_usd": 49.0,
            "product_link": "https://example.com/cap",
        }
    ]
    hd = {"title": "Chimney Cap", "price": 59.0, "link": "https://homedepot.example/1"}
    sellers = await search._build_sellers(hd, listings, stores=False)
    assert [s.name for s in sellers] == ["Home Depot"]
    sellers = await search._build_sellers(None, listings, stores=False)
    assert [s.name for s in sellers] == ["Lowe's"]  # the listing's own seller


@pytest.mark.asyncio
async def test_warm_fills_every_site_and_reports_what_it_spent(monkeypatch, no_grok):
    _kitchen()
    _site("synthetic-facade")

    async def fake_find_parts(req, progress=lambda s: None):
        parts = [_part(f"{tables.slug(req.query)}-{i}", f"{req.query} {i}") for i in range(2)]
        _cache_search(req.query, parts)  # as the real find_parts caches its answer
        return parts

    async def fake_resolve(part, **kw):
        (assets.part_dir(part.id) / "model.glb").write_bytes(b"glb")
        return part

    async def fake_fetch(url, dest):
        return False

    monkeypatch.setattr(search, "find_parts", fake_find_parts)
    monkeypatch.setattr(assets, "resolve_shared", fake_resolve)
    monkeypatch.setattr(assets, "fetch_image", fake_fetch)
    lines = []
    totals = await catalog.warm(models=True, echo=lines.append, focus=False)
    assert totals["sites"] == 2
    # kitchen 9 + facade 6 categories, gutters and window-ac... every one searched once
    assert totals["searches"] == 15
    assert totals["models"] == 30  # 2 parts per category, both built
    assert any(line.startswith("kitchen: kitchen") for line in lines)
    assert any(line.startswith("synthetic-facade: facade") for line in lines)
    again = await catalog.warm(models=True, echo=lines.append, focus=False)
    assert again["searches"] == 0 and again["models"] == 0  # idempotent


def test_warm_cli_runs_the_catalog(monkeypatch):
    seen = {}

    async def fake_warm(sites, models=True, focus=True, max_searches=None):
        seen.update(sites=sites, models=models, focus=focus, max_searches=max_searches)
        return {}

    monkeypatch.setattr(catalog, "warm", fake_warm)
    monkeypatch.setattr(
        "sys.argv", ["warm", "--catalog", "--sites", "kitchen, gt-lcc-canopy", "--no-models"]
    )
    warm.main()
    assert seen == {
        "sites": ["kitchen", "gt-lcc-canopy"],
        "models": False,
        "focus": True,
        "max_searches": None,
    }
