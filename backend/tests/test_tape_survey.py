"""The Quartermaster uses the tape (brain.md S1): scene digest, `survey`/`check_slope` fast paths
and tools, `POST /agent/observe` grouping + speech, the gutter drainage verdict, and follow-ups.
No LLM: every scripted completion is local."""

import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from server import agent, scene_digest, tape_survey, voice
from server.app import app
from server.config import get_settings
from tests.test_agent import (
    _no_llm_chat,
    _scripted_chat,
    _text_completion,
    _tool_call_completion,
)

# Like the real kitchen capture: 18 cabinet doors in 4 repeat sizes + 4 odd ones, 2 drawers.
_KITCHEN_DOORS = (
    [(262, 279)] * 6 + [(208, 525)] * 4 + [(261, 426)] * 2 + [(205, 422)] * 2
    + [(548, 282), (141, 87), (102, 65), (70, 162)]
)  # fmt: skip


def _rect(w_mm: float, h_mm: float, x0: float = 0.0, y0: float = 0.0) -> list[list[float]]:
    w, h = w_mm / 1000, h_mm / 1000
    return [[x0, y0, 0.0], [x0 + w, y0, 0.0], [x0 + w, y0 + h, 0.0], [x0, y0 + h, 0.0]]


def _obj(oid: str, label: str, w_mm: float, h_mm: float, x0: float = 0.0, y0: float = 0.0):
    return {
        "id": oid,
        "label": label,
        "plane": "p0",
        "corners3d": _rect(w_mm, h_mm, x0, y0),
        "w_m": w_mm / 1000,
        "h_m": h_mm / 1000,
    }


def _write_site(root: Path, site: str, structure: dict | None, **meta) -> None:
    site_dir = root / site
    site_dir.mkdir(parents=True)
    scene = {"name": site, "revision": 1, "gravity_residual_deg": 0.0, **meta}
    if structure is not None:
        (site_dir / "structure.r1.json").write_text(json.dumps(structure))
        scene["structure"] = {"file": "structure.r1.json", "schema": "airtools.structure/1"}
    (site_dir / "scene.json").write_text(json.dumps(scene))


def _kitchen_structure() -> dict:
    objects = [_obj(f"o{i}", "cabinet_door", w, h, x0=i) for i, (w, h) in enumerate(_KITCHEN_DOORS)]
    objects += [_obj("o18", "drawer", 528, 90), _obj("o19", "drawer", 410, 90)]
    return {
        "schema": "airtools.structure/1",
        "planes": [{"id": "p0", "normal": [0, 0, 1], "offset": 0}],
        "edges": [{"id": "e0", "a": [0, 0.6, 0], "b": [1.5, 0.6, 0], "kind": "crease"}],
        "objects": objects,
    }


def _facade_structure() -> dict:
    # The synthetic facade: one 1.5 x 1.2 m window, one door, a level 4.2 m gutter box at ~6 m
    # (top edge 6.2 m, front lip 6.0 m), a sloped roof edge and low wall edges.
    edges = [
        {"id": "e_lip", "a": [0, 6.0, 0.3], "b": [4.2, 6.0, 0.3], "kind": "crease"},
        {"id": "e_top", "a": [0, 6.2, 0.2], "b": [4.2, 6.2, 0.2], "kind": "crease"},
        {"id": "e_roof", "a": [0, 6.2, 0], "b": [0, 8.0, -2.0], "kind": "crease"},
        {"id": "e_sill", "a": [0.75, 3.5, 0], "b": [-0.75, 3.5, 0], "kind": "boundary"},
        {"id": "e_ground", "a": [-4, 0, 0], "b": [4, 0, 0], "kind": "crease"},
        {"id": "e_short", "a": [0, 6.3, 0], "b": [0.2, 6.3, 0], "kind": "line"},
        {"id": "e_head", "a": [0.75, 4.7, 0], "b": [-0.75, 4.7, 0], "kind": "boundary"},
        {"id": "e_door", "a": [2.0, 2.03, 0], "b": [2.9, 2.03, 0], "kind": "boundary"},
        {"id": "e_plinth", "a": [-4, 0.3, 0], "b": [4, 0.3, 0], "kind": "crease"},
    ]
    return {
        "schema": "airtools.structure/1",
        "planes": [{"id": "p0", "normal": [0, 0, 1], "offset": 0}],
        "edges": edges,
        "objects": [_obj("o0", "window", 1500, 1200, -0.75, 3.5), _obj("o1", "door", 914, 2032)],
    }


@pytest.fixture(autouse=True)
def _clean(tmp_path, monkeypatch):
    root = tmp_path / "scene"
    _write_site(root, "kitchen", _kitchen_structure(), gravity_residual_deg=0.08)
    _write_site(root, "facade", _facade_structure())
    _write_site(root, "facade-preview", None)
    monkeypatch.setenv("SCENE_DIR", str(root))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    scene_digest._layers.clear()
    agent._sessions.clear()
    yield root
    agent._sessions.clear()
    scene_digest._layers.clear()
    get_settings.cache_clear()


# --- scene digest ---------------------------------------------------------------------------


def test_kitchen_digest_counts_and_size_groups():
    d = scene_digest.digest("kitchen")
    assert d.structure and d.counts == {"cabinet_door": 18, "drawer": 2}
    groups = d.groups["cabinet_door"]
    assert [(g["count"], g["w_mm"], g["h_mm"]) for g in groups[:4]] == [
        (6, 262, 279),
        (4, 208, 525),
        (2, 261, 426),
        (2, 205, 422),
    ]
    assert len(groups) == 8  # the 4 repeats + 4 one-offs
    assert sum(g["count"] for g in groups) == 18
    assert d.gravity_residual_deg == 0.08
    o0 = d.objects[0]
    assert o0["id"] == "o0" and o0["w_mm"] == 262 and o0["normal"] == [0, 0, 1]
    assert o0["center"] == pytest.approx([0.131, 0.1395, 0.0])


def test_digest_llm_line_is_counts_and_groups_only():
    line = scene_digest.digest("kitchen").llm_line()
    assert line.startswith("Scene objects (structure layer): 18 cabinet_door (6x 262x279 mm,")
    assert "+5 sizes" in line and "2 drawer (1x 528x90 mm, 1x 410x90 mm)" in line
    assert "o0" not in line and "corners" not in line  # no per-object detail for the LLM
    assert len(line) < 1600  # << 400 tokens


def test_digest_scale_multiplies_sizes():
    d = scene_digest.digest("facade", scale=1.5)
    window = next(o for o in d.objects if o["label"] == "window")
    assert (window["w_mm"], window["h_mm"]) == (2250, 1800)
    assert d.gutter_edges[0]["length_m"] == pytest.approx(6.3)


def test_digest_gutter_candidates_are_long_high_near_horizontal_edges():
    d = scene_digest.digest("facade")
    ids = [e["id"] for e in d.gutter_edges]
    # Longest first, then highest: the gutter's top edge beats its front lip. The sloped roof
    # edge (not horizontal), the 20 cm trim piece (too short) and everything below the median
    # height (sill, head, door, plinth, ground) never qualify.
    assert ids == ["e_top", "e_lip"]
    assert d.gutter_edges[0] == {"id": "e_top", "length_m": 4.2, "height_m": 6.2, "kind": "crease"}


def test_digest_without_structure_or_site():
    preview = scene_digest.digest("facade-preview")
    assert preview is not None and not preview.structure and preview.count("window") == 0
    assert "no structure layer" in preview.llm_line()
    assert scene_digest.digest("no-such-site") is None
    assert scene_digest.digest("../etc") is None
    assert scene_digest.digest(None) is None


# --- fast paths -----------------------------------------------------------------------------


@pytest.mark.parametrize(
    "text,args",
    [
        ("Quartermaster, measure every cabinet door", ("cabinet_door", "all", "size")),
        ("measure all the windows", ("window", "all", "size")),
        ("measure all of the drawers.", ("drawer", "all", "size")),
        ("size up the upper cabinet doors", ("cabinet_door", "upper", "size")),
        ("measure the lower drawers", ("drawer", "lower", "size")),
        ("survey each panel on the left", ("panel", "all", "size")),
        ("measure the left doors", ("door", "left", "size")),
        ("measure the window", ("window", "nearest", "size")),
        ("measure the doors I can see", ("door", "visible", "size")),
        ("measure every window's width", ("window", "all", "width")),
        ("measure the heights of all the appliances", None),  # "of" breaks the phrase: LLM
        ("measure all the appliances, just the height", ("appliance", "all", "height")),
    ],
)
def test_survey_fast_path_phrasing(text, args):
    match = agent._match_fast_intent(text)
    if args is None:
        assert match is None or match[0] != "survey"
        return
    name, got, _ = match
    assert name == "survey"
    assert (got["label"], got["where"], got["measure"]) == args


@pytest.mark.parametrize(
    "text,target",
    [
        ("Is this gutter sloped enough to drain?", "gutter"),
        ("does the gutter have enough fall", "gutter"),
        ("will water drain off that sill?", "sill"),
        ("check the pitch of the ledge", "ledge"),
    ],
)
def test_slope_fast_path(text, target):
    assert agent._match_fast_intent(text)[:2] == ("check_slope", {"target": target})


@pytest.mark.parametrize(
    "text",
    [
        "find a gutter drain outlet",
        "I need a gutter slope bracket",
        "find a hanger for this gutter",
    ],
)
def test_slope_fast_path_leaves_part_searches_alone(text):
    match = agent._match_fast_intent(text)
    assert match is None or match[0] != "check_slope"


@pytest.mark.parametrize("text", ["stop", "Stop.", "okay, stop", "cancel that", "abort the survey"])
def test_stop_fast_path(text):
    assert agent._match_fast_intent(text)[:2] == ("stop_survey", {})


def test_stop_needs_the_whole_utterance():
    assert agent._match_fast_intent("stop by the hardware store and find hinges") is None


def test_existing_fast_paths_unchanged():
    assert agent._match_fast_intent("every 60 cm")[0] == "place_array"
    assert agent._match_fast_intent("buy it")[0] == "start_checkout"
    assert agent._match_fast_intent("find a hanger for this gutter") is None


# --- "survey" is two tools: the tape (app lane) and F10's condition survey ------------------


@pytest.mark.parametrize(
    ("text", "tool"),
    [
        # a structure-object noun is the tape
        ("survey every cabinet door", "survey"),
        ("Survey all the windows.", "survey"),
        ("survey each drawer", "survey"),
        ("survey the upper cabinet doors", "survey"),
        ("survey the facade windows", "survey"),  # the windows are the thing surveyed
        ("measure every window on the facade", "survey"),
        ("survey all the doors in the building", "survey"),
        ("size up the appliances", "survey"),
        # roof / gutter(s) / facade / building / condition words are F10's
        ("survey the roof", "survey_condition"),
        ("survey the roof carefully", "survey_condition"),
        ("survey the gutters", "survey_condition"),
        ("survey the facade", "survey_condition"),
        ("survey the building", "survey_condition"),
        ("survey the condition of the windows", "survey_condition"),
        ("survey every window for damage", "survey_condition"),
        ("survey all the doors for rot", "survey_condition"),
        ("survey the gutter slope", "survey_condition"),  # gutter(s): F10's
        # stop is the whole utterance, before F10 can read "survey" as a new one
        ("cancel the survey", "stop_survey"),
        ("abort the survey", "stop_survey"),
        # the slope check comes after the Grok matchers
        ("is this gutter sloped enough to drain?", "check_slope"),
        ("check the sill's fall", "check_slope"),
        ("show me the report on the gutter slope", "make_report"),
        ("any rebates for gutter drainage?", "check_rules"),
    ],
)
def test_survey_phrasings_route_between_tape_and_condition(text, tool):
    assert agent._fast_route(agent.Session(id="route"), text, {})[0] == tool


def test_condition_survey_args_unchanged_by_the_tape():
    name, args, _ = agent._match_fast_intent("survey the gutters carefully")
    assert (name, args) == ("survey_condition", {"focus": "gutters", "careful": True})
    name, args, _ = agent._match_fast_intent("survey every cabinet door")
    assert (name, args) == ("survey", {"label": "cabinet_door", "where": "all", "measure": "size"})


@pytest.mark.asyncio
async def test_both_surveys_end_to_end_on_one_site(monkeypatch):
    """With a site loaded, "measure every cabinet door" is the tape and "survey the roof" still
    reaches F10's `_survey` (stubbed: it needs a real drone scene)."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    seen = []

    async def fake_condition_survey(session, args, context):
        seen.append((args, context.get("site")))
        return {"survey_id": "abc", "spoken": "Looking over the footage."}, [], None

    monkeypatch.setattr(agent, "_survey", fake_condition_survey)
    ctx = {"site": "kitchen"}
    tape = await agent.handle_command("both", "measure every cabinet door", ctx)
    condition = await agent.handle_command("both", "survey the roof", ctx)

    assert tape["reply"] == "Surveying 18 doors."
    assert [a["name"] for a in tape["actions"]] == ["survey"]
    assert condition["reply"] == "Looking over the footage."
    assert seen == [({"focus": "roof", "careful": False}, "kitchen")]


# --- survey via the agent -------------------------------------------------------------------


@pytest.mark.asyncio
async def test_measure_every_cabinet_door_queues_one_survey_without_the_llm(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command(
        "k1", "measure every cabinet door", {"site": "kitchen", "tool": "tape"}
    )

    assert result["reply"] == "Surveying 18 doors."
    assert result["job_id"] is None
    [action] = result["actions"]
    assert action["name"] == "survey"
    args = action["args"]
    assert {k: args[k] for k in ("label", "where", "measure")} == {
        "label": "cabinet_door",
        "where": "all",
        "measure": "size",
    }
    assert args["request_id"].startswith("sv-")
    pending = agent._sessions["k1"].pending[args["request_id"]]
    assert pending["label"] == "cabinet_door" and pending["query"] == "measure every cabinet door"


@pytest.mark.asyncio
async def test_doors_means_cabinet_doors_in_a_kitchen(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    kitchen = await agent.handle_command("k2", "measure all the doors", {"site": "kitchen"})
    facade = await agent.handle_command("f2", "measure all the doors", {"site": "facade"})
    assert kitchen["actions"][0]["args"]["label"] == "cabinet_door"
    assert facade["actions"][0]["args"]["label"] == "door"
    assert facade["reply"] == "Surveying 1 door."


@pytest.mark.asyncio
async def test_survey_of_a_label_the_scene_lacks_answers_straight(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command("k3", "measure all the windows", {"site": "kitchen"})
    assert result == {
        "reply": "No windows in this scene's structure layer.",
        "actions": [],
        "job_id": None,
    }


@pytest.mark.asyncio
async def test_survey_without_a_structure_layer(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command("p1", "measure the window", {"site": "facade-preview"})
    assert result["reply"] == "No structure layer for this scene; mark the corners yourself."
    assert result["actions"] == []


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "text,reply",
    [
        ("measure every cabinet door", "Surveying the doors."),
        ("measure the upper drawers", "Surveying the upper drawers."),
        ("measure the window", "Measuring the nearest window."),
        ("measure the panels I can see", "Surveying the panels in view."),
    ],
)
async def test_survey_without_a_known_site_still_goes_to_the_headset(monkeypatch, text, reply):
    """The built-in scene (or a site this server doesn't have): the headset owns its structure
    layer, so the survey goes out anyway and the headset reports what it found."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command("n1", text, {})
    assert result["reply"] == reply
    assert result["actions"][0]["name"] == "survey"


@pytest.mark.asyncio
async def test_llm_survey_ends_the_turn_with_the_spoken_count(monkeypatch):
    calls = []
    scripted = _scripted_chat(
        _tool_call_completion("survey", {"label": "drawer", "where": "all", "measure": "size"})
    )

    async def fake(role, messages, tools=None, **kw):
        calls.append((messages, tools))
        return await scripted(role, messages, tools, **kw)

    monkeypatch.setattr(agent, "chat", fake)
    # ("how big are the drawers here?" is the fast path now: tests/test_measure.py)
    result = await agent.handle_command(
        "k4", "what sizes are the drawers here?", {"site": "kitchen"}
    )

    assert len(calls) == 1  # no second turn: the headset does the work
    assert result["reply"] == "Surveying 2 drawers."
    assert result["actions"][0]["args"]["label"] == "drawer"
    messages, tools = calls[0]
    assert "Never estimate a size yourself." in messages[0]["content"]
    assert "Scene objects (structure layer): 18 cabinet_door" in messages[0]["content"]
    names = {t["function"]["name"] for t in tools}
    assert {"survey", "check_slope"} <= names
    assert "survey_query" not in names  # nothing surveyed yet


def test_survey_tool_schemas():
    tools = {t["function"]["name"]: t["function"] for t in agent.TOOLS}
    props = tools["survey"]["parameters"]["properties"]
    assert props["label"]["enum"] == [
        "cabinet_door",
        "drawer",
        "panel",
        "appliance",
        "window",
        "door",
        "any",
    ]
    assert props["where"]["enum"] == [
        "all",
        "visible",
        "upper",
        "lower",
        "left",
        "right",
        "nearest",
    ]
    assert props["measure"]["enum"] == ["size", "width", "height"]
    target = tools["check_slope"]["parameters"]["properties"]["target"]
    assert target["enum"] == ["gutter", "sill", "ledge", "nearest_edge"]
    assert tools["survey_query"]["parameters"]["required"] == ["question"]


# --- /agent/observe: survey results -----------------------------------------------------------


def _kitchen_results(unverified=("o9", "o11")) -> list[dict]:
    out = []
    for i, (w, h) in enumerate(_KITCHEN_DOORS):
        out.append(
            {
                "id": f"o{i}",
                "label": "cabinet_door",
                "w_m": (w + (i % 3)) / 1000,  # a mm or two of tape noise
                "h_m": h / 1000,
                "area_m2": w * h / 1e6,
                "angles_deg": [90.1, 89.8, 90.2, 89.9],
                "snap": ["corner", "corner", "edge", "corner"],
                "unverified": f"o{i}" in unverified,
                "notebook_id": 12 + i,
                "camera_id": 316,
            }
        )
    return out


def _observe(**body) -> tape_survey.Observation:
    return tape_survey.Observation(**{"session_id": "k9", **body})


def test_observe_survey_groups_sizes_and_speaks_them():
    agent._sessions.clear()
    obs = _observe(
        request_id="sv-1", kind="survey_result", label="cabinet_door", results=_kitchen_results()
    )
    result = agent.observe(obs)

    assert result["reply"] == (
        "18 doors in 8 sizes: 6 at 26 by 28, 4 at 21 by 53, 2 at 26 by 43 centimetres and "
        "5 more sizes on the card. Two didn't lock onto corners; they're amber."
    )
    [action] = result["actions"]
    assert action["name"] == "show_tape_survey"
    args = action["args"]
    assert args["request_id"] == "sv-1" and args["label"] == "cabinet_door"
    assert args["unverified"] == ["o9", "o11"] and args["skipped"] == [] and args["focus"] == []
    assert sum(g["count"] for g in args["groups"]) == 18
    assert sorted(i for g in args["groups"] for i in g["ids"]) == sorted(f"o{i}" for i in range(18))
    assert args["groups"][0]["count"] == 6 and args["groups"][0]["ids"] == [
        "o0",
        "o1",
        "o2",
        "o3",
        "o4",
        "o5",
    ]
    session = agent._sessions["k9"]
    assert len(session.tape_survey["items"]) == 18
    assert session.history[-1] == {"role": "assistant", "content": result["reply"]}


@pytest.mark.asyncio
async def test_observe_uses_the_pending_request_for_label_and_measure(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    queued = await agent.handle_command("k9", "measure every window's width", {})
    request_id = queued["actions"][0]["args"]["request_id"]

    result = agent.observe(
        _observe(
            request_id=request_id,
            kind="survey_result",
            results=[{"id": "o0", "w_m": 1.502, "h_m": 1.199}],
        )
    )

    assert result["reply"] == "1 window: 150 centimetres wide."
    assert request_id not in agent._sessions["k9"].pending
    assert agent._sessions["k9"].tape_survey["query"] == "measure every window's width"


@pytest.mark.parametrize(
    "body,reply",
    [
        (
            {"status": "aborted", "planned": 18, "results": _kitchen_results(())[:6]},
            "Stopped after 6 of 18. 6 doors, all 26 by 28 centimetres.",
        ),
        (
            {"status": "no_structure"},
            "No structure layer for this scene; mark the corners yourself.",
        ),
        ({"results": []}, "Found no doors to measure."),
        (
            {
                "results": [{"id": "o7", "w_m": 0.12, "h_m": 0.08, "unverified": True}],
                "skipped": [{"id": "o9", "reason": "corner behind scanned surface"}],
            },
            (
                "1 door: 120 by 80 millimetres. One didn't lock onto its corners; it's amber. "
                "One skipped."
            ),
        ),
    ],
)
def test_observe_survey_edge_cases(body, reply):
    result = agent.observe(_observe(kind="survey_result", label="cabinet_door", **body))
    assert result["reply"] == reply
    assert bool(result["actions"]) == bool(body.get("results"))


# --- gutter slope ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "fall,unc,verdict",
    [
        (0.0, 2.0, "won't drain"),  # the synthetic facade's level gutter
        (12.0, 2.0, "drains"),
        (7.0, 6.0, "inconclusive"),
        (-12.0, 2.0, "drains"),  # sign is the direction; only the size of the fall matters
    ],
)
def test_slope_verdict_table(fall, unc, verdict):
    v = tape_survey.slope_verdict(4.2, fall, unc)
    assert v["verdict"] == verdict
    assert v["required_mm"] == pytest.approx(8.7, abs=0.05)  # 4.2 m x 2.083 mm/m


def test_slope_uncertainty_from_gravity_residual():
    assert tape_survey.slope_uncertainty_mm(4.2, 0.0) == 2.0
    assert tape_survey.slope_uncertainty_mm(4.2, 0.08) == pytest.approx(7.9, abs=0.05)


def test_slope_replies_are_honest():
    assert tape_survey.slope_reply(tape_survey.slope_verdict(4.2, 0.4, 2.0)) == (
        "Flat: 0 ± 2 mm of fall over 4.20 m. It needs about 9 mm, so water will pond."
    )
    assert tape_survey.slope_reply(tape_survey.slope_verdict(4.2, 12, 2)) == (
        "12 ± 2 mm of fall over 4.20 m; it needs 9, so it drains."
    )
    assert tape_survey.slope_reply(tape_survey.slope_verdict(4.2, 3, 7.9)) == (
        "About 3 ± 8 mm of fall over 4.20 m; it needs 9, so that's inconclusive. "
        "Put a real level on it."
    )


@pytest.mark.asyncio
async def test_check_slope_suggests_gutter_edges_then_judges_the_report(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    queued = await agent.handle_command(
        "f1", "Is this gutter sloped enough to drain?", {"site": "facade"}
    )
    assert queued["reply"] == "Checking the gutter's fall."
    [action] = queued["actions"]
    assert action["name"] == "check_slope"
    assert action["args"]["target"] == "gutter"
    assert action["args"]["edge_ids"] == ["e_top", "e_lip"]

    result = agent.observe(
        tape_survey.Observation(
            session_id="f1",
            request_id=action["args"]["request_id"],
            kind="slope_result",
            run_m=4.2,
            fall_mm=0.4,
            low_end=[2.1, 6.2, 0.03],
            notebook_id=13,
        )
    )
    # No uncertainty sent: the facade's gravity residual (0 deg) + 2 mm of snapping.
    assert result["reply"] == (
        "Flat: 0 ± 2 mm of fall over 4.20 m. It needs about 9 mm, so water will pond."
    )
    assert result["actions"] == [
        {
            "name": "add_note",
            "args": {
                "text": "Gutter slope: won't drain (0.4 ± 2 mm of fall over 4.20 m; needs 8.7 mm)"
            },
        }
    ]


@pytest.mark.asyncio
async def test_slope_report_on_a_real_capture_is_inconclusive(monkeypatch):
    """The kitchen's 0.08 deg gravity residual over 4.2 m is ~6 mm of doubt on its own."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    queued = await agent.handle_command("k5", "does the ledge drain?", {"site": "kitchen"})
    assert queued["actions"][0]["args"]["edge_ids"] == []  # gutter candidates are for gutters
    request_id = queued["actions"][0]["args"]["request_id"]
    result = agent.observe(
        tape_survey.Observation(
            session_id="k5", request_id=request_id, kind="slope_result", run_m=4.2, fall_mm=3.0
        )
    )
    assert "inconclusive" in result["reply"] and "± 8 mm" in result["reply"]
    assert result["actions"][0]["args"]["text"].startswith("Ledge slope: inconclusive")


# --- follow-ups on the stored survey ---------------------------------------------------------


@pytest.mark.asyncio
async def test_which_is_the_widest_answers_from_the_survey_without_the_llm(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    agent.observe(_observe(kind="survey_result", label="cabinet_door", results=_kitchen_results()))

    result = await agent.handle_command("k9", "which is the widest?", {})

    assert result["reply"] == "The widest door is o14: 55 by 28 centimetres."
    [action] = result["actions"]
    assert action["name"] == "show_tape_survey" and action["args"]["focus"] == ["o14"]


@pytest.mark.asyncio
async def test_superlatives_without_a_survey_stay_with_the_llm(monkeypatch):
    monkeypatch.setattr(agent, "chat", _scripted_chat(_text_completion("No survey yet, matey.")))
    result = await agent.handle_command("k10", "which one is the cheapest and smallest?", {})
    assert result["reply"] == "No survey yet, matey."


@pytest.mark.asyncio
async def test_survey_query_tool_hands_the_model_a_csv(monkeypatch):
    agent.observe(_observe(kind="survey_result", label="cabinet_door", results=_kitchen_results()))
    seen = []
    scripted = _scripted_chat(
        _tool_call_completion("survey_query", {"question": "how many are taller than 40 cm?"}),
        _text_completion("Eight are taller than 40 centimetres; two of the doors are amber."),
    )

    async def fake(role, messages, tools=None, **kw):
        seen.append((list(messages), tools))
        return await scripted(role, messages, tools, **kw)

    monkeypatch.setattr(agent, "chat", fake)
    result = await agent.handle_command("k9", "how many are taller than 40 cm?", {})

    assert result["reply"].startswith("Eight are taller")
    assert "survey_query" in {t["function"]["name"] for t in seen[0][1]}
    tool_msg = seen[1][0][-1]
    assert tool_msg["role"] == "tool"
    payload = json.loads(tool_msg["content"])
    assert payload["csv"].splitlines()[0] == "id,label,w_mm,h_mm,unverified"
    assert "o9,cabinet_door,208,525,1" in payload["csv"]


# --- HTTP ---------------------------------------------------------------------------------


def test_observe_route_shape_and_tts(monkeypatch):
    async def fake_speak(text):
        assert text.startswith("1 window")
        return b"RIFFfake", "audio/wav"

    monkeypatch.setattr(voice, "speak", fake_speak)
    body = {
        "session_id": "http1",
        "request_id": "sv-x",
        "kind": "survey_result",
        "label": "window",
        "results": [{"id": "o0", "label": "window", "w_m": 1.5, "h_m": 1.2, "notebook_id": "n12"}],
    }
    with TestClient(app) as client:
        plain = client.post("/agent/observe", json=body).json()
        spoken = client.post("/agent/observe", json={**body, "tts": True}).json()

    assert plain == {
        "reply": "1 window: 150 by 120 centimetres.",
        "actions": [
            {
                "name": "show_tape_survey",
                "args": {
                    "request_id": "sv-x",
                    "label": "window",
                    "groups": [{"w_mm": 1500, "h_mm": 1200, "count": 1, "ids": ["o0"]}],
                    "unverified": [],
                    "skipped": [],
                    "focus": [],
                },
            }
        ],
        "job_id": None,
    }
    assert spoken["audio_mime"] == "audio/wav" and spoken["audio_b64"] == "UklGRmZha2U="


def test_observe_route_rejects_a_slope_report_without_numbers():
    with TestClient(app) as client:
        resp = client.post(
            "/agent/observe", json={"session_id": "s", "kind": "slope_result", "target": "gutter"}
        )
        assert resp.status_code == 422
        resp = client.post("/agent/observe", json={"session_id": "s", "kind": "guess"})
        assert resp.status_code == 422
