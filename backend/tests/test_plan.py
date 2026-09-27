""" "Where does it go?" planner (server/plan.py, POST /scene/plan, agent plan_placement).

Geometry runs on a trimmed copy of scene/kitchen/structure.r1.json (tests/fixtures/structure/);
the only thing mocked is the LLM call (`llm.chat`), so `llm.extract`'s strict schema and
validation still run.
"""

import json
import shutil
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from openai.types.chat import ChatCompletion

from server import agent, llm, plan
from server.app import app
from server.config import get_settings

FIXTURE = Path(__file__).parent / "fixtures" / "structure"


@pytest.fixture
def kitchen():
    return json.loads((FIXTURE / "kitchen" / "structure.r1.json").read_text())


@pytest.fixture
def scene_dir(tmp_path, monkeypatch):
    root = tmp_path / "scene"
    shutil.copytree(FIXTURE, root)
    monkeypatch.setenv("SCENE_DIR", str(root))
    get_settings.cache_clear()
    yield root
    get_settings.cache_clear()


def _completion(content: str) -> ChatCompletion:
    return ChatCompletion.model_validate(
        {
            "id": "x",
            "object": "chat.completion",
            "created": 0,
            "model": "grok-4.20-0309-non-reasoning",
            "choices": [
                {
                    "index": 0,
                    "finish_reason": "stop",
                    "message": {"role": "assistant", "content": content},
                }
            ],
        }
    )


def _choice(**kw) -> dict:
    return {
        "planner": "none",
        "target_id": None,
        "spacing_mm": None,
        "count": None,
        "stock_length_m": None,
        "hint": "",
        **kw,
    }


@pytest.fixture
def grok(monkeypatch):
    """Scripted replies for the one planner call; records what it was sent."""
    calls: list[dict] = []
    replies: list[str] = []

    async def fake_chat(role, messages, tools=None, **kw):
        assert role == "plan" and tools is None
        assert kw["response_format"]["json_schema"]["strict"] is True
        calls.append({"messages": messages, **kw})
        return _completion(replies.pop(0))

    monkeypatch.setattr(llm, "chat", fake_chat)

    def script(*choices) -> list[dict]:
        replies.extend(c if isinstance(c, str) else json.dumps(c) for c in choices)
        return calls

    return script


# --- geometry -------------------------------------------------------------------------------


def test_under_cabinet_run_is_one_2432mm_run(kitchen):
    runs = plan.under_cabinet_runs(kitchen)
    assert len(runs) == 1
    assert runs[0]["length_m"] == pytest.approx(2.432, abs=0.02)
    uppers = {o["id"] for o in plan.upper_doors(kitchen)}
    assert "o10" in uppers and not {"o9", "o11"} & uppers  # inside the outer o10
    assert not {"o23", "o25", "o27", "o29"} & uppers  # base cabinets stay out


def test_along_edge_matches_hanger_plan():
    s = {"edges": [{"id": "e1", "a": [0, 0, 0], "b": [3.66, 0, 0]}], "objects": [], "planes": []}
    geo = plan.along_edge(s, "e1", 600)
    assert len(geo["points"]) == 7 == plan.hanger_plan(3.66, 600)["count"]
    assert geo["points"][0] == [0.15, 0, 0] and geo["points"][-1] == [3.51, 0, 0]
    assert geo["spacing_mm"] == 560
    assert [p[0] for p in plan.along_edge(s, "e1", count=3)["points"]] == [0.61, 1.83, 3.05]


def test_centered_on_flags_a_part_wider_than_the_door(kitchen):
    spawn = [3.3, 0.42, 0.22]  # scene.json recommended_spawn: the room side of the wall
    geo = plan.centered_on(kitchen, "o5", {"w": 600, "d": 100, "h": 50}, spawn)
    assert geo["fits"] is False  # o5 is 0.26 m wide
    assert geo["points"][0][0] > -1.36 + 0.04  # pushed ~5 cm off the wall, into the room
    assert plan.centered_on(kitchen, "o5", {"w": 200, "d": 100, "h": 50}, spawn)["fits"]


def test_measure_a_plane_takes_its_long_horizontal_extent(kitchen):
    seg = plan.target_segment(kitchen, "p5")  # the counter top
    assert float(abs(seg[1] - seg[0])[[0, 2]].max()) == pytest.approx(1.74, abs=0.02)
    assert seg[0][1] == pytest.approx(seg[1][1])  # level


# --- POST /scene/plan -----------------------------------------------------------------------


def test_plan_led_run_counts_strips(scene_dir, grok):
    calls = grok(_choice(planner="under_cabinet_runs", stock_length_m=1))
    resp = TestClient(app).post(
        "/scene/plan", json={"site": "kitchen", "utterance": "LED strip under these cabinets"}
    )
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert body["planner"] == "under_cabinet_runs"
    assert body["count"] == 3 and body["total_m"] == pytest.approx(2.43, abs=0.02)
    assert len(body["segments"]) == 1 and body["points"] == []
    assert "cut the last" in body["spoken"]
    prompt = calls[0]["messages"][1]["content"]
    assert "o10 cabinet_door wall-cabinet" in prompt and "LED strip" in prompt
    assert "[-1.4" not in prompt  # a summary, never coordinates


def test_plan_along_an_edge(scene_dir, grok):
    grok(_choice(planner="along_edge", target_id="o5", spacing_mm=100))
    body = (
        TestClient(app)
        .post(
            "/scene/plan",
            json={"site": "kitchen", "utterance": "hooks under this door every 10 cm"},
        )
        .json()
    )
    assert body["count"] == len(body["points"]) == 3  # 0.26 m door: insets capped to a quarter


def test_plan_none_is_422_with_the_hint(scene_dir, grok):
    grok(_choice(hint="Point at the gutter."))
    resp = TestClient(app).post(
        "/scene/plan", json={"site": "kitchen", "utterance": "hangers along the gutter"}
    )
    assert resp.status_code == 422
    assert resp.json()["detail"] == "Point at the gutter."


def test_plan_without_structure_is_409(scene_dir, grok):
    scene_json = scene_dir / "kitchen" / "scene.json"
    scene_json.write_text(json.dumps({**json.loads(scene_json.read_text()), "structure": None}))
    calls = grok()
    resp = TestClient(app).post("/scene/plan", json={"site": "kitchen", "utterance": "LED strip"})
    assert resp.status_code == 409
    assert resp.json()["detail"] == "I can only plan on the full scan."
    assert calls == []  # no LLM spend when there is nothing to plan on


def test_plan_rejects_a_schema_invalid_reply(scene_dir, grok):
    bad = json.dumps(_choice(planner="teleport"))
    grok(bad, bad)  # extract() retries once, then gives up
    resp = TestClient(app).post("/scene/plan", json={"site": "kitchen", "utterance": "LED strip"})
    assert resp.status_code == 502


def test_plan_unknown_target_is_422(scene_dir, grok):
    grok(_choice(planner="measure", target_id="e99999"))
    resp = TestClient(app).post("/scene/plan", json={"site": "kitchen", "utterance": "how long"})
    assert resp.status_code == 422


# --- agent ----------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_agent_plan_placement_shows_the_plan_then_places_it(scene_dir, grok, monkeypatch):
    agent._sessions.clear()
    grok(_choice(planner="along_edge", target_id="o10", spacing_mm=200))
    tool_call = {
        "id": "x",
        "object": "chat.completion",
        "created": 0,
        "model": "gpt-oss-120b",
        "choices": [
            {
                "index": 0,
                "finish_reason": "tool_calls",
                "message": {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [
                        {
                            "id": "c0",
                            "type": "function",
                            "function": {
                                "name": "plan_placement",
                                "arguments": json.dumps({"request": "hooks every 20 cm"}),
                            },
                        }
                    ],
                },
            }
        ],
    }
    offered = []

    async def fake_agent_chat(role, messages, tools=None, **kw):
        offered.extend(t["function"]["name"] for t in tools)
        return ChatCompletion.model_validate(tool_call)

    monkeypatch.setattr(agent, "chat", fake_agent_chat)
    ctx = {"site": "kitchen"}
    result = await agent.handle_command("s-plan", "hooks along this cabinet every 20 cm", ctx)

    assert "plan_placement" in offered
    (action,) = result["actions"]
    assert action["name"] == "show_plan"
    assert set(action["args"]) == {"plan_id", "segments", "points", "label"}
    assert len(action["args"]["points"]) == 3  # 0.55 m: 137 mm insets and gaps
    assert result["reply"].startswith("Three along 0.55 metres")

    placed = await agent.handle_command("s-plan", "place them", ctx)
    assert placed["actions"] == [
        {"name": "place_array", "args": {"spacing_mm": 137, "plan_id": action["args"]["plan_id"]}}
    ]
    agent._sessions.clear()


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_led_strip_on_the_kitchen(scene_dir):
    result = await plan.plan("kitchen", "LED strip under these cabinets, 1 metre strips", {})
    assert result["planner"] == "under_cabinet_runs" and result["count"] == 3
