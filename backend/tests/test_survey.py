"""Drone condition survey (server/survey.py): rules, boxes, projection, cache, endpoints, agent.
Only the Grok call (`llm.chat`) is mocked; the scene is a trimmed kitchen package (2 cameras)."""

import json
import shutil
import time
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import trimesh
from fastapi.testclient import TestClient

from server import agent, cache, jobs, llm, survey
from server.app import app
from server.config import get_settings
from server.models import Job

KITCHEN = Path(__file__).parent / "fixtures" / "survey" / "kitchen"

# The shape of G4 call #2's reply, on the kitchen fixture's two frames.
REPLY = {
    "findings": [
        {"frame": "0001", "element": "gutter", "issue": "Gutter sagging, joint split",
         "severity": "severe", "confidence": 0.9, "box": [400, 400, 600, 600],
         "action": "replace", "part_query": "5 in aluminium K-style gutter section"},
        {"frame": "0001", "element": "gutter", "issue": "Gutter joint leaking",
         "severity": "moderate", "confidence": 0.7, "box": [420, 380, 620, 580],
         "action": "repair", "part_query": "gutter seam sealant"},
        {"frame": "0701", "element": "roof_covering", "issue": "Possible hail impact marks",
         "severity": "minor", "confidence": 0.8, "box": [400, 400, 600, 600],
         "action": "replace", "part_query": "architectural shingles"},
        {"frame": "0701", "element": "window", "issue": "Looks fine", "severity": "none",
         "confidence": 0.9, "box": [100, 100, 300, 300], "action": "none", "part_query": ""},
        {"frame": "0001", "element": "facade", "issue": "Maybe a crack", "severity": "severe",
         "confidence": 0.3, "box": [0, 0, 500, 500], "action": "repair", "part_query": "filler"},
    ],
    "coverage_gaps": ["Downspouts are out of frame"],
}  # fmt: skip


@pytest.fixture
def scene(tmp_path, monkeypatch):
    shutil.copytree(KITCHEN, tmp_path / "scene" / "kitchen")
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    yield tmp_path / "scene"
    get_settings.cache_clear()


@pytest.fixture
def grok(monkeypatch):
    """Mock `llm.chat`: returns REPLY at $0.0071 and records each call's role and messages."""
    calls = []

    async def fake_chat(role, messages, tools=None, **kw):
        calls.append({"role": role, "messages": messages, **kw})
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(REPLY)))],
            usage=SimpleNamespace(cost_in_usd_ticks=71_000_000),
        )

    monkeypatch.setattr(llm, "chat", fake_chat)
    return calls


def _offline(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()


# --- pure pieces --------------------------------------------------------------------------


def test_boxes_on_both_scales():
    assert survey.box_scale([[20, 20, 95, 55]]) == 100  # G4 call #1: 0-100 though asked 0-1000
    assert survey.box_scale([[200, 200, 950, 550], [10, 10, 20, 20]]) == 1000
    assert survey.normalise_box([20, 20, 95, 55], 100) == [0.2, 0.2, 0.95, 0.55]
    assert survey.normalise_box([200, 200, 950, 550], 1000) == [0.2, 0.2, 0.95, 0.55]
    assert survey.normalise_box([950, 550, 200, 200], 1000) == [0.2, 0.2, 0.95, 0.55]
    assert survey.normalise_box([-10, 0, 1200, 500], 1000) == [0.0, 0.0, 1.0, 0.5]
    assert survey.normalise_box([300, 300, 300, 500], 1000) is None
    assert survey.normalise_box([1, 2, 3], 1000) is None


def test_to_pin_on_a_synthetic_plane():
    # camera 5 m above a 1 m square at z=0, looking down -Z (OpenCV axes: y down in the image)
    R = np.array([[1.0, 0, 0], [0, -1, 0], [0, 0, -1]])
    cam = {"R": R.tolist(), "position": [0, 0, 5], "fx": 500, "fy": 500, "cx": 320, "cy": 240,
           "w": 640, "h": 480}  # fmt: skip
    plane = trimesh.Trimesh([[-0.5, -0.5, 0], [0.5, -0.5, 0], [0.5, 0.5, 0], [-0.5, 0.5, 0]],
                            [[0, 1, 2], [0, 2, 3]])  # fmt: skip
    # centre pixel u = 0.52 * 640 = 332.8 -> x = 12.8 / 500 * 5 = 0.128 m
    assert np.allclose(survey.to_pin(cam, [0.48, 0.4, 0.56, 0.6], plane), [0.128, 0, 0], atol=0.01)
    assert survey.to_pin(cam, [0.9, 0.4, 1.0, 0.6], plane) is None  # off the square


def test_kitchen_projection_round_trips():
    cams = json.loads((KITCHEN / "cameras.r1.json").read_text())
    mesh = trimesh.load(KITCHEN / "collision.r1.glb", force="mesh")
    for cam in cams:
        R, t = np.array(cam["R"]), np.array(cam["t"])
        assert np.allclose(-R.T @ t, cam["position"])
        p = survey.to_pin(cam, [0.45, 0.45, 0.55, 0.55], mesh)
        assert p is not None
        x = R @ p + t  # back into the camera: lands on the box centre
        u, v = cam["fx"] * x[0] / x[2] + cam["cx"], cam["fy"] * x[1] / x[2] + cam["cy"]
        assert abs(u / cam["w"] - 0.5) < 0.02 and abs(v / cam["h"] - 0.5) < 0.02


def _cam(cid: str, forward: list[float]) -> dict:
    f = np.array(forward) / np.linalg.norm(forward)
    return {"id": cid, "R": [[1, 0, 0], [0, 1, 0], f.tolist()]}


def test_pick_frames_mixes_roof_and_facade_views():
    roof = [_cam(f"r{i}", [-1, -1, 0]) for i in range(10)]  # 45 deg down, side 0
    facade = [_cam(f"f{i}", [0, 0, 1]) for i in range(10)]  # level, the opposite side
    oblique = [_cam(f"o{i}", [-1, -0.36, 0]) for i in range(5)]  # ~20 deg: neither band
    cams = roof + facade + oblique
    picked = survey.pick_frames(cams, 6)
    assert len(picked) == 6
    assert sum(p.startswith("r") for p in picked) == 3 and picked[0].startswith("r")
    assert sum(p.startswith("f") for p in picked) == 3
    assert all(p.startswith("r") for p in survey.pick_frames(cams, 6, "roof"))
    assert survey.pick_frames(oblique, 6) == [c["id"] for c in oblique]  # fallback: everything


def test_rules_drop_low_confidence_skip_fine_and_mark_hail():
    pins, fine = survey.triage(REPLY["findings"], ["0001", "0701"])
    assert [p["element"] for p in pins] == ["gutter", "gutter", "roof_covering"]  # 0.3 dropped
    assert fine == ["window (0701)"]  # "none" is listed, never pinned
    hail = pins[2]
    assert hail["suspected"] and hail["action"] == "inspect_closer"
    assert hail["issue"].endswith("(suspected, verify on the roof)")
    assert hail["part_query"] is None  # only repair/replace keep a part query
    assert pins[0]["box"] == [0.4, 0.4, 0.6, 0.6]
    # an unknown frame id is dropped too
    assert survey.triage([{**REPLY["findings"][0], "frame": "9999"}], ["0001"]) == ([], [])


def test_nearby_pins_of_one_element_merge():
    def pin(element, severity, p, frame="0001"):
        return {"element": element, "severity": severity, "confidence": 0.8, "p": p,
                "frame_id": frame}  # fmt: skip

    pins = [
        pin("gutter", "moderate", [0.8, 0, 0], "0002"),
        pin("gutter", "severe", [0, 0, 0]),
        pin("gutter", "minor", [3, 0, 0]),  # too far
        pin("flashing", "minor", [0.1, 0, 0]),  # other element
        pin("gutter", "minor", None),  # unprojected, never merges
    ]
    merged = survey.merge_pins(pins, 1.5)
    assert [(m["id"], m["severity"]) for m in merged] == [
        ("f1", "severe"), ("f2", "minor"), ("f3", "minor"), ("f4", "minor")
    ]  # fmt: skip
    assert merged[0]["also_in"] == ["0002"]
    # Grok names one flat roof both ways across frames: those merge too
    roof = [
        pin("roof_covering", "severe", [0, 0, 0]),
        pin("flat_roof_membrane", "severe", [0.5, 0, 0]),
    ]
    assert len(survey.merge_pins(roof, 1.5)) == 1


# --- the survey run: cache and OFFLINE ----------------------------------------------------


@pytest.mark.asyncio
async def test_survey_run_pins_caches_and_serves_offline(scene, grok, monkeypatch):
    plan = survey.plan("kitchen")
    assert sorted(plan.frames) == ["0001", "0701"] and plan.merge_radius == survey.MERGE_M
    first = await survey.run(plan)
    assert len(grok) == 1 and grok[0]["role"] == "survey"
    assert first["cost_usd"] == 0.0071 and not first["cached"]
    assert [(p["id"], p["element"]) for p in first["pins"]] == [
        ("f1", "gutter"), ("f2", "roof_covering")
    ]  # fmt: skip
    assert first["pins"][0]["also_in"] == ["0001"]  # the moderate gutter folded in
    assert all(len(p["p"]) == 3 for p in first["pins"])
    assert first["looked_fine"] == ["window (0701)"]
    assert first["spoken"].startswith("2 problems found, 2 pinned. Worst is the gutter, severe")

    again = await survey.run(survey.plan("kitchen"))
    assert len(grok) == 1 and again["cached"] and again["cost_usd"] == 0.0
    assert again["pins"] == first["pins"]

    _offline(monkeypatch)
    assert (await survey.run(survey.plan("kitchen")))["pins"] == first["pins"]
    with pytest.raises(cache.OfflineMiss):
        await survey.run(survey.plan("kitchen", frames=["0001"]))
    assert len(grok) == 1


# --- endpoints ----------------------------------------------------------------------------


def _poll(client, survey_id):
    for _ in range(50):
        body = client.get(f"/scene/survey/{survey_id}").json()
        if body["status"] != "running":
            return body
        time.sleep(0.05)
    raise AssertionError("survey never finished")


def test_survey_endpoints(scene, grok, monkeypatch):
    with TestClient(app) as client:
        resp = client.post("/scene/survey", json={"site": "kitchen", "focus": "roof"})
        assert resp.status_code == 200
        body = _poll(client, resp.json()["survey_id"])
        assert body["status"] == "done" and body["label"] == survey.LABEL
        assert [p["id"] for p in body["pins"]] == ["f1", "f2"]
        assert body["frames"][0] == {"id": "0001", "file": "thumbs/0001.jpg"}

        # a cache hit comes back done in the POST itself, and so does OFFLINE
        _offline(monkeypatch)
        hit = client.post("/scene/survey", json={"site": "kitchen", "focus": "roof"}).json()
        assert hit["status"] == "done" and hit["pins"] == body["pins"]
        assert len(grok) == 1

        miss = client.post("/scene/survey", json={"site": "kitchen", "frames": ["0701"]})
        assert miss.status_code == 503
        assert client.get("/scene/survey/s-nope").status_code == 404


def test_survey_errors(scene):
    (scene / "bare").mkdir()
    (scene / "bare" / "scene.json").write_text(json.dumps({"name": "bare", "revision": 1}))
    with TestClient(app) as client:
        assert client.post("/scene/survey", json={"site": "nowhere"}).status_code == 404
        assert client.post("/scene/survey", json={"site": "../etc"}).status_code == 404
        bad = client.post("/scene/survey", json={"site": "kitchen", "frames": ["9999"]})
        assert bad.status_code == 404
        resp = client.post("/scene/survey", json={"site": "bare"})
        assert resp.status_code == 409
        assert resp.json()["detail"]["spoken"] == survey.NOT_READY


# --- agent --------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_agent_survey_then_fix_pin(scene, grok, monkeypatch):
    agent._sessions.clear()
    searched = []

    def fake_start_search(req):
        searched.append(req.query)
        return Job(id="job9", status="running", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    ctx = {"site": "kitchen"}

    result = await agent.handle_command("q1", "survey the roof", ctx)
    names = [a["name"] for a in result["actions"]]
    assert names == ["survey_started", "show_survey"]
    show = result["actions"][1]["args"]
    assert show["survey_id"] == result["actions"][0]["args"]["survey_id"]
    assert [p["id"] for p in show["pins"]] == ["f1", "f2"] and show["label"] == survey.LABEL
    assert result["reply"].startswith("2 problems found")

    fix = await agent.handle_command("q1", "find a fix for pin f1", ctx)
    assert searched == ["5 in aluminium K-style gutter section"]
    assert fix["job_id"] == "job9"
    note = fix["actions"][1]
    assert fix["actions"][0]["name"] == "search_started" and note["name"] == "add_note"
    assert note["args"]["pin_id"] == "f1" and note["args"]["frame_id"] == "0001"

    # the hail pin has no part query: a closer look, no search
    closer = await agent.handle_command("q1", "find a fix for pin f2", ctx)
    assert closer["reply"] == "That one needs a closer look before any part."
    assert searched == ["5 in aluminium K-style gutter section"]

    no_scene = await agent.handle_command("q2", "survey the roof", {})
    assert no_scene["reply"] == "Can't do that yet: no scene loaded."
    agent._sessions.clear()
