"""Measuring by voice (server/structure_measure.py, agent._measure_intent / measure tool): a
measuring request is never F10's condition survey. On a scan with planes but no labelled
objects (the GT LCC canopy) it's a structural tape (measure_edges, endpoints from the planes);
with objects (the kitchen) it's B1's tape survey; otherwise the tape in hand. Damage phrasings
still get the condition survey."""

import json
import math
from pathlib import Path

import numpy as np
import pytest
from openai.types.chat import ChatCompletion

from server import agent, scene_digest, structure_measure
from server.config import get_settings

CANOPY, KITCHEN = "canopy-test", "kitchen-test"
REAL = Path("/Users/ravil/airtools-drone-backend/scene/gt-lcc-canopy/structure.r2.json")

# The GT scans' up is ~15 deg off +Y (the canopy's is about (-0.10, 0.97, 0.24)).
UP = np.array([-0.1, 0.966, 0.237])
UP /= np.linalg.norm(UP)
U1 = np.cross(UP, [0.0, 0.0, 1.0])
U1 /= np.linalg.norm(U1)
U2 = np.cross(UP, U1)


def _rect(centre, length, width) -> list[list[float]]:
    c = np.asarray(centre, dtype=float)
    pts = [c + U1 * sx * length / 2 + U2 * sy * width / 2 for sx, sy in ((-1, -1), (1, -1),
           (1, 1), (-1, 1))]  # fmt: skip
    return [[round(float(v), 6) for v in p] for p in pts]


CANOPY_LAYER = {
    "schema": "airtools.structure/1",
    "planes": [
        {"id": "p0", "normal": UP.tolist(), "polygon": _rect([0, 0, 0], 10, 10), "area_m2": 100.0},
        {"id": "p1", "normal": UP.tolist(), "polygon": _rect(UP * 4.0, 8, 6), "area_m2": 48.0},
        {
            "id": "p9",
            "normal": U2.tolist(),
            "polygon": _rect(UP * 2 + U2 * 5, 3, 1),
            "area_m2": 3.0,
        },
    ],
    "edges": [{"id": "e0", "a": [0, 0, 0], "b": [1, 0, 0], "kind": "line", "planes": ["p0"]}],
    "corners": [],
    "objects": [],
}
KITCHEN_LAYER = {
    "schema": "airtools.structure/1",
    "planes": [
        {
            "id": "p0",
            "normal": [0, 0, 1],
            "polygon": [[0, 0, 0], [1, 0, 0], [1, 1, 0]],
            "area_m2": 1,
        }
    ],
    "edges": [],
    "corners": [],
    "objects": [
        {
            "id": "o1",
            "label": "window",
            "plane": "p0",
            "w_m": 0.9,
            "h_m": 1.2,
            "corners3d": [[0, 0, 0], [0.9, 0, 0], [0.9, 1.2, 0], [0, 1.2, 0]],
        },
        {
            "id": "o2",
            "label": "cabinet_door",
            "plane": "p0",
            "w_m": 0.4,
            "h_m": 0.7,
            "corners3d": [[0, 0, 0], [0.4, 0, 0], [0.4, 0.7, 0], [0, 0.7, 0]],
        },
    ],
}


@pytest.fixture(autouse=True)
def _scenes(tmp_path, monkeypatch):
    for site, layer in ((CANOPY, CANOPY_LAYER), (KITCHEN, KITCHEN_LAYER)):
        d = tmp_path / "scene" / site
        d.mkdir(parents=True)
        (d / "structure.r2.json").write_text(json.dumps(layer))
        meta = {"name": site, "revision": 2, "structure": {"file": "structure.r2.json"}}
        (d / "scene.json").write_text(json.dumps(meta))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    scene_digest._layers.clear()
    agent._sessions.clear()
    yield
    agent._sessions.clear()
    scene_digest._layers.clear()
    get_settings.cache_clear()


async def _no_llm(role, messages, tools=None, **kw):
    raise AssertionError("a measuring request is the fast path")


def _tool_call(name: str, arguments: dict) -> ChatCompletion:
    call = {"id": "c0", "type": "function",
            "function": {"name": name, "arguments": json.dumps(arguments)}}  # fmt: skip
    return ChatCompletion.model_validate(
        {"id": "x", "object": "chat.completion", "created": 1, "model": "grok-4.20",
         "choices": [{"index": 0, "finish_reason": "tool_calls",
                      "message": {"role": "assistant", "content": None, "tool_calls": [call]}}]}
    )  # fmt: skip


async def _say(text, site=CANOPY, session="m", **ctx):
    return await agent.handle_command(session, text, {"site": site, "scale": 1.0, **ctx})


def _length(action) -> float:
    (seg,) = action["args"]["segments"]
    return math.dist(seg["a"], seg["b"])


# --- the phrases ----------------------------------------------------------------------------


@pytest.mark.parametrize(
    "text,dimension,thing,e2e",
    [
        ("measure the length of the top roof from end to end", "length", "top roof", True),
        ("How long is the roof?", "length", "roof", False),
        ("measure the height of the tower platform", "height", "tower platform", False),
        ("how tall is the building", "height", "building", False),
        ("what's the width of the canopy", "width", "canopy", False),
        ("okay grok, measure the roof end to end please", "length", "roof", True),
        ("tape the wall", "length", "wall", False),
        ("how big is this", "size", None, False),
        ("measure it", "length", None, False),
        ("get me the height of the platform", "height", "platform", False),
    ],
)
def test_measuring_phrases(text, dimension, thing, e2e):
    ask = structure_measure.parse(text)
    assert (ask.dimension, ask.thing, ask.end_to_end) == (dimension, thing, e2e)


@pytest.mark.parametrize(
    "text",
    [
        "is the roof damaged?",
        "what's wrong with the roof",
        "survey the roof for problems",
        "inspect the canopy",
        "measure the gap",  # the replace flow's measure_cavity
        "check the roof",
        "get me a new fridge",
        "find a 30 inch range",
    ],
)
def test_not_a_measuring_phrase(text):
    assert structure_measure.parse(text) is None


# --- a scan with planes and edges but no labelled objects (the GT LCC canopy) ---------------


@pytest.mark.asyncio
async def test_the_top_roof_end_to_end_is_a_structural_tape(monkeypatch):
    """The headset's "measure the length of the top roof from end to end" on gt-lcc-canopy got
    F10's condition pins; :8004 said "No objects in this scene's structure layer"."""
    monkeypatch.setattr(agent, "chat", _no_llm)
    r = await _say("measure the length of the top roof from end to end")
    (act,) = r["actions"]
    assert act["name"] == "measure_edges"
    assert act["args"]["label"] == "Top roof length"
    assert _length(act) == pytest.approx(8.0, abs=1e-3)  # the 8 x 6 m roof's long side
    a, b = (np.asarray(act["args"]["segments"][0][k]) for k in "ab")
    assert float((a - b) @ UP) == pytest.approx(0.0, abs=1e-3)  # level, in the scan's own up
    assert float(a @ UP) == pytest.approx(4.0, abs=1e-3)  # on the roof, not the ground
    assert act["args"]["request_id"].startswith("me-")
    assert r["reply"] == "Taping the top roof end to end; the number's on the tape."
    assert not any(x["name"] in ("survey_started", "show_survey") for x in r["actions"])


@pytest.mark.asyncio
async def test_how_long_is_the_roof_and_how_wide(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    r = await _say("how long is the roof")
    assert r["actions"][0]["name"] == "measure_edges"
    assert _length(r["actions"][0]) == pytest.approx(8.0, abs=1e-3)
    r = await _say("how wide is the canopy")
    assert r["actions"][0]["args"]["label"] == "Canopy width"
    assert _length(r["actions"][0]) == pytest.approx(6.0, abs=1e-3)


@pytest.mark.asyncio
async def test_the_height_of_the_platform_is_straight_down_to_the_ground(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    r = await _say("measure the height of the tower platform")
    (act,) = r["actions"]
    assert (act["name"], act["args"]["label"]) == ("measure_edges", "Tower platform height")
    assert r["reply"] == "Taping the tower platform's height; the number's on the tape."
    a, b = (np.asarray(act["args"]["segments"][0][k]) for k in "ab")
    assert _length(act) == pytest.approx(4.0, abs=1e-3)
    along_up = abs(float((a - b) @ UP)) / float(np.linalg.norm(a - b))
    assert along_up == pytest.approx(1.0, abs=1e-6)  # vertical in the scan's up
    r = await _say("how tall is the building", session="m2")
    assert _length(r["actions"][0]) == pytest.approx(4.0, abs=1e-3)


@pytest.mark.asyncio
async def test_a_thing_the_scan_cant_find_puts_the_tape_in_hand(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    r = await _say("how wide is the window")
    assert r["actions"] == [
        {"name": "equip_tool", "args": {"tool": "tape", "label": "Window width"}}
    ]
    assert r["reply"] == "Measure's ready: pinch one end of the window, then the other."
    r = await _say("measure it")
    assert r["reply"] == "Measure's ready: pinch one end, then the other."


@pytest.mark.asyncio
async def test_this_with_the_pointer_measures_the_plane_its_on(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    pointer = (UP * 4.0 + U1 * 1.0).tolist()  # on the roof
    r = await _say("how long is this", pointer=pointer)
    assert r["actions"][0]["name"] == "measure_edges"
    assert _length(r["actions"][0]) == pytest.approx(8.0, abs=1e-3)


# --- a scene with objects: B1's tape survey, as before ----------------------------------------


@pytest.mark.asyncio
async def test_with_objects_it_is_still_the_tape_survey(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    r = await _say("how wide is the window", site=KITCHEN)
    (act,) = r["actions"]
    assert act["name"] == "survey"
    assert (act["args"]["label"], act["args"]["where"], act["args"]["measure"]) == (
        "window",
        "nearest",
        "width",
    )
    r = await _say("measure every cabinet door", site=KITCHEN, session="k2")
    assert (r["actions"][0]["name"], r["actions"][0]["args"]["label"]) == ("survey", "cabinet_door")
    assert r["actions"][0]["args"]["where"] == "all"
    r = await _say("how big are the windows", site=KITCHEN, session="k3")
    assert r["actions"][0]["args"]["where"] == "all"


# --- damage still gets the condition survey; a sizes question never does ----------------------


@pytest.fixture
def condition(monkeypatch):
    calls = []

    async def fake_survey(session, args, ctx):
        calls.append(args)
        return {"spoken": "2 problems found."}, {"name": "survey_started", "args": {}}, None

    monkeypatch.setattr(agent, "_survey", fake_survey)
    return calls


@pytest.mark.asyncio
@pytest.mark.parametrize("text", ["is the roof damaged?", "what's wrong with the roof"])
async def test_damage_still_gets_the_condition_survey(monkeypatch, condition, text):
    async def grok(role, messages, tools=None, **kw):
        names = [t["function"]["name"] for t in tools]
        assert "measure" in names and "survey_condition" in names
        return _tool_call("survey_condition", {"focus": "roof", "careful": False})

    monkeypatch.setattr(agent, "chat", grok)
    r = await _say(text)
    assert condition == [{"focus": "roof", "careful": False}]
    assert r["actions"] == [{"name": "survey_started", "args": {}}]


@pytest.mark.asyncio
async def test_survey_the_roof_is_still_the_condition_survey(monkeypatch, condition):
    monkeypatch.setattr(agent, "chat", _no_llm)
    await _say("survey the roof")
    assert condition and condition[0]["focus"] == "roof"


@pytest.mark.asyncio
async def test_a_sizes_question_the_llm_sends_to_the_condition_survey_is_measured(
    monkeypatch, condition
):
    """The headset case: the LLM picked survey_condition for a measuring request."""

    async def grok(role, messages, tools=None, **kw):
        return _tool_call("survey_condition", {"focus": "roof", "careful": False})

    monkeypatch.setattr(agent, "chat", grok)
    r = await _say("roughly how long, end to end, would you say the roof runs?")
    assert condition == []
    assert r["actions"][0]["name"] in ("measure_edges", "equip_tool")
    r = await _say("survey the length of the roof", session="m3")  # the fast path's "survey"
    assert condition == []
    assert r["actions"][0]["name"] == "measure_edges"


# --- the real canopy scan (when this machine has it) -----------------------------------------


@pytest.mark.skipif(not REAL.is_file(), reason="needs the gt-lcc-canopy scene package")
def test_the_real_canopy_roof():
    layer = json.loads(REAL.read_text())
    ask = structure_measure.parse("measure the length of the top roof from end to end")
    segs, what = structure_measure.segments(layer, ask)
    assert what == "length"
    assert math.dist(segs[0]["a"], segs[0]["b"]) == pytest.approx(8.35, abs=0.05)  # scene metres
    up = structure_measure.up_vector(structure_measure._planes(layer))
    assert up[1] == pytest.approx(0.966, abs=0.01)  # the scan's up is ~15 deg off +Y
    ask = structure_measure.parse("measure the height of the tower platform")
    segs, what = structure_measure.segments(layer, ask)
    assert what == "height"
    assert math.dist(segs[0]["a"], segs[0]["b"]) == pytest.approx(4.05, abs=0.05)
