"""assetgen: a search fitted to a taped opening (search, agent) and a window's own size from
Home Depot's specs or its title (sellers, search). No network, no keys."""

from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import trimesh
from PIL import Image

from server import agent, cache, jobs, meshgen, search
from server.config import get_settings
from server.models import Dims, Job, Opening, Part, SearchRequest

WINDOW = Dims(w=1486, h=1181, d=80)
NAME = "58.5 in. x 46.5 in. Double Hung White Vinyl Replacement Window"
WINDOW_ANSWER = {
    "template": "window",
    "params": {"style": "double_hung", "grid_x": 2, "grid_y": 1},
    "colors": {"frame": "#F2F2F2", "glass": "#9FB4C4"},
    "finishes": {"frame": "plastic"},
    "photo": {"face": "front", "rotate_cw": 0, "single": True},
    "shape_class": "template",
}
PANEL_ANSWER = {**WINDOW_ANSWER, "template": "flat_panel", "params": {"frame_w": 0.1}}


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()
    agent._sessions.clear()


@pytest.fixture
def photo(tmp_path) -> Path:
    """A white window frame with grey glass on a white catalogue background."""
    path = tmp_path / "window.jpg"
    img = Image.new("RGB", (500, 400), (255, 255, 255))
    img.paste((230, 230, 228), (50, 40, 450, 360))
    img.paste((120, 140, 160), (80, 70, 420, 330))
    img.save(path)
    return path


def _part(**kw) -> Part:
    return Part(**{"id": "win", "name": NAME, "dims_mm": WINDOW, **kw})


def fake_llm(monkeypatch, replies: dict, answered: dict | None = None) -> list:
    """Stub `llm.chat`: role -> reply text (or an exception); `answered`: role -> the model name
    the response says answered it (a Groq call that fell back to xAI says grok-*)."""
    calls = []

    async def chat(role, messages, **kw):
        calls.append(role)
        reply = replies[role]
        if isinstance(reply, Exception):
            raise reply
        msg = SimpleNamespace(content=reply)
        return SimpleNamespace(
            choices=[SimpleNamespace(message=msg)], model=(answered or {}).get(role)
        )

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _bounds_mm(glb: Path) -> np.ndarray:
    lo, hi = trimesh.load(glb, force="scene").bounds
    return (hi - lo) * 1000


# --- a search fitted to a taped opening ---------------------------------------------------------

OPENING = Opening(w_m=1.5, h_m=1.2, label="taped opening")


def _cand(pid: str, w: float, h: float, d: float = 80) -> Part:
    return Part(id=pid, name=pid, dims_mm=Dims(w=w, h=h, d=d))


@pytest.mark.parametrize(
    ("w", "h", "status", "spare", "axis"),
    [
        (1486, 1181, "fits", 14, "w"),
        (1500, 1199, "fits", 0, "w"),
        (1502, 1190, "fits", -2, "w"),  # within the 3 mm tolerance of a tape
        (1511, 1206, "too_big", -11, "w"),
        (1486, 1110, "too_small", -90, "h"),  # a 90 mm gap to fill: not made for it
        (711, 1168, "too_small", -789, "w"),
    ],
)
def test_opening_fit(w, h, status, spare, axis):
    f = search.opening_fit(Dims(w=w, h=h, d=80), OPENING)
    assert (f.status, round(f.spare_mm), f.axis) == (status, spare, axis)


def test_the_best_sized_candidates_come_first_and_the_planner_is_told():
    parts = [_cand("big", 1511, 1206), _cand("small", 711, 1168), _cand("snug", 1490, 1190)]
    parts.append(_cand("loose", 1470, 1170))
    ranked = search.fit_to_opening(parts, OPENING)
    assert [p.id for p in ranked][:2] == ["snug", "loose"]
    assert [p.fit.status for p in ranked][:2] == ["fits", "fits"]
    req = SearchRequest(query="window frame", opening=OPENING)
    user = search._build_prompt(req, "", "")[1]["content"]
    assert "Opening (taped by the headset): 1500 x 1200 mm (59.1 x 47.2 in), width x height" in user
    assert search.fit_to_opening(parts, None) is parts


@pytest.mark.asyncio
async def test_a_cached_search_is_fitted_to_the_opening(monkeypatch):
    parts = [_cand("big", 1511, 1206), _cand("snug", 1490, 1190)]
    key = {"q": cache.normalize("window frame"), "m": None, "o": [150, 120]}  # to the cm
    cache.put("search", key, [p.model_dump(mode="json") for p in parts])
    out = await search.find_parts(SearchRequest(query="window frame", opening=OPENING))
    assert [p.id for p in out] == ["snug", "big"]
    assert out[0].fit.note == "fits the opening, 10 mm spare"


def test_the_shops_are_searched_with_the_opening_size():
    assert search.discovery_query(SearchRequest(query="window frame")) == "window frame"
    req = SearchRequest(query="window frame", opening=OPENING)
    assert search.discovery_query(req) == "window frame 59 x 47 in"
    req = SearchRequest(query="replacement window", opening=Opening(w_m=0.9, h_m=1.5))
    assert search.discovery_query(req) == "replacement window 35.5 x 59 in"


@pytest.mark.asyncio
async def test_find_part_takes_the_opening_from_the_context(monkeypatch):
    captured = []

    def fake_start_search(req):
        captured.append(req)
        return Job(id="job-open", status="running", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    session = agent._get_session("s-open")
    ctx = {"opening": {"w_m": 1.5, "h_m": 1.2, "label": "taped opening"}}
    agent._apply_context(session, ctx)
    await agent._run_tool(session, "find_part", {"query": "window frame"}, ctx)
    assert captured[0].opening == OPENING
    agent._apply_context(session, {})  # the headset stopped sending it (its tapes were undone)
    await agent._run_tool(session, "find_part", {"query": "window frame"}, {})
    assert captured[1].opening is None
    agent._apply_context(session, {"opening": {"w_m": -1}})  # malformed: no opening
    await agent._run_tool(session, "find_part", {"query": "window frame"}, {})
    assert captured[2].opening is None


# --- window dims: Home Depot's window specs and window titles -----------------------------------

V2500 = [  # home_depot_product 59.5 in. x 47.5 in. V-2500 Series ... Sliding Window (SerpApi, live)
    {
        "key": "Dimensions",
        "value": [
            {"name": "Product Height (in.)", "value": "47.5 in"},
            {"name": "Product Width (in.)", "value": "59.5 in"},
            {"name": "Rough Opening Height (in.)", "value": "48 in"},
            {"name": "Rough Opening Width (in.)", "value": "60 in"},
            {"name": "Width (in.) x Height (in.)", "value": "59.5 x 47.5"},
        ],
    }
]
PRO70 = [  # 27.75 in. x 45.25 in. 70 Pro Series ... Double Hung ... Window
    {
        "key": "Dimensions",
        "value": [
            {"name": "Jamb Depth (in.)", "value": "3.25 in"},
            {"name": "Product Depth (in.)", "value": "4.5 in"},
            {"name": "Product Height (in.)", "value": "45.25 in"},
            {"name": "Product Width (in.)", "value": "27.75 in"},
            {"name": "Rough Opening Height (in.)", "value": "46 in"},
            {"name": "Rough Opening Width (in.)", "value": "28 in"},
            {"name": "Width (in.) x Height (in.)", "value": "27.75 x 45.25"},
        ],
    }
]


def test_a_windows_rough_opening_is_not_its_size():
    from server import sellers

    d = sellers._parse_dims(PRO70)  # was 28 x 46 in (the rough opening)
    assert (d.w, d.h, d.d) == pytest.approx((27.75 * 25.4, 45.25 * 25.4, 4.5 * 25.4))
    assert sellers._parse_dims(V2500) is None  # no depth published
    dims, typical = sellers._window_dims(V2500)
    assert typical and (dims.w, dims.h, dims.d) == pytest.approx((1511.3, 1206.5, 82.55))
    assert sellers._window_dims(PRO70)[1] is False
    assert sellers._window_dims([{"key": "Dimensions", "value": V2500[0]["value"][:2]}]) == (
        None,
        False,
    )  # no rough opening: not a window unit, no guess


def test_a_window_sold_by_its_size_gets_the_typical_depth():
    cand = search.Candidate(
        name="59.5 in. x 47.5 in. V-2500 Series White Vinyl Right-Handed Sliding Window",
    )
    d = search._window_dims_from_title(cand, [], None)
    assert (d.w, d.h, d.d) == pytest.approx((1511.3, 1206.5, 82.55))
    screen = cand.model_copy(update={"name": "5/16 in. x 84 in. Aluminum Screen Frame"})
    assert search._window_dims_from_title(screen, [], None) is None


@pytest.mark.asyncio
async def test_no_dims_from_the_cheap_model_gets_a_grok_retry(monkeypatch):
    calls = []

    async def extract(role, messages, cls):
        calls.append(role)
        if role == "cheap":  # gpt-oss-20b read the page but found only W x H
            return cls(w=1511.3, h=1206.5, d=None)
        return cls(w=1511.3, h=1206.5, d=82.55)

    monkeypatch.setattr(search.llm, "extract", extract)
    cand = search.Candidate(name="59.5 in. x 47.5 in. V-2500 Series Sliding Window")
    assert await search._extract_dims(cand, "Jamb depth 3-1/4 in.") is None  # retry off (conftest)
    assert calls == ["cheap"]
    monkeypatch.setenv("LLM_EXTRACT_RETRY", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    calls.clear()
    d = await search._extract_dims(cand, "Jamb depth 3-1/4 in.")
    assert calls == ["cheap", "extract_retry"] and d.d == pytest.approx(82.55)
