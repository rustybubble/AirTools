"""Capture coach (server/coverage.py): side coverage, pitch bands, merged legs, the spoken line,
`GET /scenes/{site}/coverage` and the agent's `coach_capture`. Only the LLM is mocked."""

import json
from pathlib import Path
from types import SimpleNamespace

import pytest
import trimesh
from fastapi.testclient import TestClient

from server import agent, coverage
from server.app import app
from server.config import get_settings

CAMS = json.loads((Path(__file__).parent / "fixtures" / "coverage" / "cameras.json").read_text())


@pytest.fixture(autouse=True)
def _dirs(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield
    get_settings.cache_clear()


@pytest.fixture
def llm_calls(monkeypatch):
    """Mock `llm.chat`: records each call and answers with a fixed line at $0.002."""
    calls = []

    async def fake_chat(role, messages, tools=None, **kw):
        calls.append(role)
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content="Grok says fly the back."))],
            usage=SimpleNamespace(cost_in_usd_ticks=20_000_000),
        )

    monkeypatch.setattr(coverage.llm, "chat", fake_chat)
    return calls


def _scene(site: str, cams: str | None, *, size=(10, 6, 10), scale="none", north=None) -> Path:
    """A scene package around a box on the ground (y=0), cameras from the fixture."""
    d = Path(get_settings().SCENE_DIR) / site
    d.mkdir(parents=True)
    box = trimesh.creation.box(extents=size)
    box.apply_translation([0, size[1] / 2, 0])
    box.export(d / "collision.r1.glb")
    if cams:
        (d / "cameras.r1.json").write_text(json.dumps(CAMS[cams]))
    scene = {"revision": 1, "scale_method": scale, "north": north,
             "collision": {"file": "collision.r1.glb"}, "cameras": "cameras.r1.json"}  # fmt: skip
    (d / "scene.json").write_text(json.dumps(scene))
    return d


# --- the maths --------------------------------------------------------------------------------


def test_full_orbit_sees_all_eight_sides():
    cov = coverage.coverage(_scene("orbit", "full_orbit"))
    assert cov["seen"] == 8
    assert all(s["views"] >= coverage.MIN_VIEWS for s in cov["sides"])
    assert not [leg for leg in cov["legs"] if leg["pattern"] == "orbit"]
    assert cov["pitch"] == {"nadir": 0, "oblique": 36, "level": 0}


def test_one_sided_flight_gives_one_merged_orbit_leg():
    cov = coverage.coverage(_scene("one", "one_sided"))
    assert 1 <= cov["seen"] <= 2
    assert cov["verdict"] == "reshoot_most"
    orbits = [leg for leg in cov["legs"] if leg["pattern"] == "orbit"]
    assert len(orbits) == 1  # not one leg per empty side (G4 call #6)
    assert orbits[0]["sweep_deg"] == 360 - 45 * cov["seen"]
    # all views are level or oblique: a roof grid, no eave pass
    assert [leg["pattern"] for leg in cov["legs"]] == ["orbit", "nadir_grid"]
    assert cov["sides"][0]["label"] == "front" and cov["sides"][0]["seen"]


def test_north_labels_the_real_side():
    # the one-sided cameras sit at +Z, which is south when north is -Z
    cov = coverage.coverage(_scene("north", "one_sided", north=[0, 0, -1]))
    assert cov["zero"] == "north"
    assert [s["label"] for s in cov["sides"] if s["seen"]] == ["S"]
    assert cov["ring"]["quarter_dir"] == [1.0, 0.0, 0.0]  # 90 degrees clockwise = east = +X


def test_nadir_only_fills_the_nadir_band_and_no_side():
    cov = coverage.coverage(_scene("nadir", "nadir"))
    assert cov["pitch"] == {"nadir": 9, "oblique": 0, "level": 0}
    assert cov["seen"] == 0
    assert [leg["pattern"] for leg in cov["legs"]] == ["orbit", "eave_pass"]
    assert cov["legs"][0]["sweep_deg"] == 360


def test_gaps_merge_around_the_circle():
    t, f = True, False
    assert coverage.gaps([t, f, f, t, f, f, f, f]) == [(1, 2), (4, 4)]
    assert coverage.gaps([f, t, t, t, t, t, t, f]) == [(7, 2)]  # 7 and 0 join across 0 degrees
    assert coverage.gaps([t] * 8) == []
    assert coverage.gaps([f] * 8) == [(0, 8)]


def test_legs_scale_with_the_building():
    cov = coverage.coverage(_scene("one", "one_sided"))
    orbit = cov["legs"][0]
    assert orbit["radius"] == pytest.approx(cov["target"]["width"])  # one width out
    assert orbit["radius_widths"] == 1.0
    relative = coverage.template(coverage.facts(cov))
    assert "building width" in relative and " m " not in relative
    metric = coverage.coverage(_scene("metric", "one_sided", scale="known_dimension"))
    assert "14 m out" in coverage.template(coverage.facts(metric))


def test_small_metric_scene_is_interior():
    cov = coverage.coverage(_scene("room", "one_sided", size=(3, 2.5, 3), scale="altitude"))
    assert cov["mode"] == "interior"
    assert cov["legs"] == []
    assert "room" in cov["note"]


# --- the spoken line ----------------------------------------------------------------------


@pytest.mark.asyncio
async def test_spoken_is_cached_by_the_facts(llm_calls):
    d = _scene("one", "one_sided")
    first = await coverage.coach(d)
    assert first["spoken"] == "Grok says fly the back."
    assert first["spoken_source"] == "grok-4.20-0309-non-reasoning"
    second = await coverage.coach(d)
    assert second["spoken_source"] == "cache"
    assert llm_calls == ["coach"]


@pytest.mark.asyncio
async def test_offline_miss_uses_the_template(llm_calls, monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    cov = await coverage.coach(_scene("one", "one_sided"))
    assert cov["spoken_source"] == "template"
    assert cov["spoken"].startswith(f"You've covered {cov['seen']} of 8 sides")
    assert "orbit from the front-left round to the front-right" in cov["spoken"]
    assert llm_calls == []


@pytest.mark.asyncio
async def test_llm_failure_uses_the_template(monkeypatch):
    async def broken(*a, **kw):
        raise RuntimeError("503")

    monkeypatch.setattr(coverage.llm, "chat", broken)
    cov = await coverage.coach(_scene("one", "one_sided"))
    assert cov["spoken_source"] == "template"
    assert not list((Path(get_settings().DATA_DIR) / "cache").glob("coach/*"))


@pytest.mark.asyncio
async def test_interior_never_calls_the_llm(llm_calls):
    cov = await coverage.coach(_scene("room", "one_sided", size=(3, 2.5, 3), scale="altitude"))
    assert cov["spoken"] == cov["note"]
    assert llm_calls == []


# --- endpoint and agent -------------------------------------------------------------------


def test_coverage_endpoint(llm_calls):
    _scene("one", "one_sided")
    _scene("bare", None)
    client = TestClient(app)
    body = client.get("/scenes/one/coverage").json()
    assert body["site"] == "one" and len(body["sides"]) == 8 and body["spoken"]
    assert client.get("/scenes/nope/coverage").status_code == 404
    assert client.get("/scenes/bare/coverage").status_code == 409
    assert client.get("/scenes/one/scene.json").status_code == 200  # file route still works


@pytest.mark.asyncio
async def test_agent_coach_capture_emits_show_coverage(llm_calls, monkeypatch):
    async def no_agent_llm(*a, **kw):
        raise AssertionError("fast path must not call the agent LLM")

    monkeypatch.setattr(agent, "chat", no_agent_llm)
    _scene("one", "one_sided")
    out = await agent.handle_command("s1", "what did I miss?", {"site": "one"})
    assert out["reply"] == "Grok says fly the back."
    [action] = out["actions"]
    assert action["name"] == "show_coverage"
    assert action["args"]["site"] == "one" and action["args"]["legs"]

    out = await agent.handle_command("s1", "what did I miss?", {})
    assert out["actions"] == [] and "no scene loaded" in out["reply"]
