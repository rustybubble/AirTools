"""Measure and replace by voice (server/replace.py, agent._replace_route): the app's e2e hand-off
(docs/handoff/p4-e2e in the app repo). Remove -> measure the gap -> find ones that fit -> put the best
in -> switch models -> put it back, all on the fast path (no LLM), from the site's parts file."""

import json

import pytest

from server import agent, jobs, replace, search
from server.config import get_settings
from server.models import Cavity, Dims, Fit, Job, Part

SITE = "test-kitchen"

PARTS = {
    "schema": "airtools.parts/1",
    "frame": "scene",
    "revision": 1,
    "components": [
        {
            "id": "dw1",
            "label": "dishwasher",
            "class": "appliance",
            "removable": True,
            "cavity": {
                "node": "cavity_dw1",
                "estimated": True,
                "size_m": {"w": 0.6, "h": 0.82, "d": 0.58},
                "insert": {"p": [0.4, 0.0, 0.58], "axes": [[1, 0, 0], [0, 1, 0], [0, 0, 1]]},
            },
        },
        {
            "id": "bc1",
            "label": "sink cabinet",
            "class": "cabinet",
            "removable": True,
            "cavity": {"size_m": {"w": 0.9, "h": 0.82, "d": 0.58}},
        },
        {"id": "wall", "label": "wall", "class": "structure", "removable": False},
    ],
}


@pytest.fixture(autouse=True)
def _scene(tmp_path, monkeypatch):
    base = tmp_path / "scene" / SITE
    base.mkdir(parents=True)
    (base / "scene.json").write_text(
        json.dumps(
            {
                "name": SITE,
                "revision": 1,
                "parts": {"file": "parts.r1.json", "schema": "airtools.parts/1", "components": 3},
            }
        )
    )
    (base / "parts.r1.json").write_text(json.dumps(PARTS))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    replace._cache.clear()
    monkeypatch.setattr(agent.safety, "start", lambda part: None)
    agent._sessions.clear()
    yield
    agent._sessions.clear()
    replace._cache.clear()
    get_settings.cache_clear()


async def _no_llm_chat(role, messages, tools=None, **kw):
    raise AssertionError("measure and replace is the fast path: no LLM")


def _part(pid: str, w: float, h: float, d: float) -> Part:
    return Part(
        id=pid, name=f"{pid} 24 in. Built-In Dishwasher (Stainless)", dims_mm=Dims(w=w, h=h, d=d)
    )


CANDIDATES = {
    "tall": _part("tall", 609.6, 889.0, 635.0),  # too tall and deep for 600 x 820 x 580
    "ok": _part("ok", 595.0, 815.0, 570.0),  # fits: 5 mm spare across, 5 up, 10 deep
    "slim": _part("slim", 450.0, 810.0, 560.0),  # fits (an 18-inch)
}


@pytest.fixture
def candidates(monkeypatch):
    monkeypatch.setattr(jobs, "load_part", lambda pid: CANDIDATES.get(pid))


async def _say(text: str, **ctx):
    context = {"site": SITE, "scale": 1.0, **ctx}
    return await agent.handle_command("e2e", text, context)


# --- the phrases ----------------------------------------------------------------------------


@pytest.mark.parametrize(
    "text,kind,extra",
    [
        ("remove the dishwasher", "remove", {"thing": "dishwasher"}),
        ("Take the dishwasher out.", "remove", {"thing": "dishwasher"}),
        ("okay grok, pull out the old dishwasher please", "remove", {"thing": "dishwasher"}),
        ("put it back", "put_back", {"thing": None}),
        ("put the dishwasher back in", "put_back", {"thing": "dishwasher"}),
        ("measure it", "measure", {}),
        ("measure the dimensions of it", "measure", {}),
        ("how big is the gap?", "measure", {}),
        ("find a dishwasher that fits", "find", {"thing": "dishwasher", "fits": True}),
        ("look for new dishwashers", "find", {"thing": "dishwashers", "fits": False}),
        ("find one that fits", "find", {"thing": None, "fits": True}),
        ("put it in there", "place", {"thing": None}),
        ("install it", "place", {"thing": None}),
        ("next one", "next", {}),
        ("show me the next model", "next", {}),
        ("previous one", "previous", {}),
        ("show me option 2", "option", {"index": 1}),
        ("the third one", "option", {"index": 2}),
    ],
)
def test_phrases(text, kind, extra):
    assert replace.match(text) == {"kind": kind, **extra}


@pytest.mark.parametrize(
    "text",
    [
        "what am I looking at?",
        "measure every cabinet door",
        "place them every 60 cm",
        "put a note here",
        "show it in stainless",
        "next step",
        "go back",
        "remove it",
        "select the first",
        "do the whole job",
        "find a 30-inch induction range under $1,200 that fits",
        "buy it",
        "",
    ],
)
def test_not_a_replace_phrase(text):
    assert replace.match_all(text) == []


def test_compound_phrases():
    kinds = [
        p["kind"]
        for p in replace.match_all("take out the dishwasher, measure it, and find one that fits")
    ]
    assert kinds == ["remove", "measure", "find"]
    assert replace.match_all("find a hinge and a screw") == []


# --- the parts file and the fit ---------------------------------------------------------------


def test_components_and_lookup():
    comps = replace.components(SITE)
    assert [c["id"] for c in comps] == ["dw1", "bc1", "wall"]
    assert replace.find(comps, "dishwashers")["id"] == "dw1"
    assert replace.find(comps, "sink_cabinet")["id"] == "bc1"
    assert replace.find(comps, "cabinet")["id"] == "bc1"
    assert replace.find(comps, "fridge") is None
    assert replace.components("no-such-site") == []
    assert replace.components("../etc") == []
    assert replace.file_gap_m(comps[0], 1.25) == pytest.approx((0.75, 1.025, 0.725))


def test_fit_on_three_axes():
    cav = Cavity(w_m=0.6, h_m=0.82, d_m=0.58)
    ok = replace.fit(Dims(w=595, h=815, d=570), cav)
    assert (ok.status, ok.spare_mm, ok.axis) == ("fits", 5.0, "w")
    tall = replace.fit(Dims(w=595, h=850, d=570), cav)
    assert (tall.status, tall.spare_mm, tall.axis) == ("too_big", -30.0, "h")
    assert "too tall" in tall.note
    assert replace.clearance_mm(Dims(w=595, h=815, d=570), cav) == {"w": 5, "h": 5, "d": 10}
    # the headset's tape wins over the file
    taped = Cavity(w_m=0.6, h_m=0.82, d_m=0.58, measured={"w_m": 0.59})
    assert replace.fit(Dims(w=595, h=815, d=570), taped).status == "too_big"
    dims = [Dims(w=609.6, h=889, d=635), Dims(w=595, h=815, d=570), None]
    assert replace.best(dims, cav) == 1
    assert replace.best([Dims(w=700, h=900, d=700), Dims(w=610, h=830, d=590)], cav) == 1


def test_search_fits_candidates_to_the_cavity():
    parts = [CANDIDATES["tall"].model_copy(), CANDIDATES["ok"].model_copy()]
    parts[0].fit = Fit(status="fits", spare_mm=100, axis="w")  # the width tape alone said it fits
    out = search.fit_to_cavity(parts, Cavity(w_m=0.6, h_m=0.82, d_m=0.58))
    assert [p.id for p in out] == ["ok", "tall"]
    assert out[0].fit.status == "fits" and out[1].fit.status == "too_big"
    assert search.fit_to_cavity(parts, None) is parts


# --- the agent, phrase by phrase ------------------------------------------------------------------


@pytest.mark.asyncio
async def test_the_whole_flow(monkeypatch, candidates):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    started = {}

    def fake_start_search(req):
        started["req"] = req
        return Job(id="job1", status="running", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)

    r = await _say("remove the dishwasher", removed=[])
    assert r["actions"] == [{"name": "remove_component", "args": {"component_id": "dw1"}}]
    assert "60 by 82 by 58 centimetres" in r["reply"]

    r = await _say("measure the gap", removed=["dw1"])
    assert r["actions"] == [{"name": "measure_cavity", "args": {"component_id": "dw1"}}]

    cavity = {
        "component_id": "dw1",
        "label": "dishwasher",
        "w_m": 0.6,
        "h_m": 0.82,
        "d_m": 0.58,
        "estimated": True,
        "measured": {"w_m": 0.601, "d_m": 0.579, "h_m": 0.818},
    }
    r = await _say(
        "find a dishwasher that fits",
        removed=["dw1"],
        cavity=cavity,
        measurement={"label": "tape #3", "value_m": 0.601, "axis": "w"},
    )
    assert r["actions"] == [{"name": "search_started", "args": {"job_id": "job1"}}]
    req = started["req"]
    assert req.query == "dishwasher"
    assert req.cavity.size_mm() == pytest.approx((601.0, 818.0, 579.0))
    assert req.measurement.axis == "w"

    ids = ["tall", "ok", "slim"]
    r = await _say("put it in there", removed=["dw1"], cavity=cavity, candidate_ids=ids)
    (act,) = r["actions"]
    assert act["name"] == "place_part"
    a = act["args"]
    assert (a["part_id"], a["component_id"], a["fits"], a["model_url"]) == (
        "ok",
        "dw1",
        "fits",
        "/parts/ok/model.glb",
    )
    assert a["clearance_mm"] == {"w": 6, "h": 3, "d": 9}
    assert a["cycle"] == {"index": 1, "count": 3}
    assert "fits with 3 millimetres to spare" in r["reply"]

    # A model stands in the gap now: switching is the placement editor's cycle_model {index}.
    r = await _say(
        "next one", removed=["dw1"], cavity=cavity, candidate_ids=ids, selected_part_id="ok"
    )
    assert r["actions"] == [
        {
            "name": "cycle_model",
            "args": {
                "index": 2,
                "part_id": "slim",
                "component_id": "dw1",
                "cycle": {"index": 2, "count": 3},
            },
        }
    ]
    assert r["reply"].startswith("Option 3 of 3")

    r = await _say(
        "next one", removed=["dw1"], cavity=cavity, candidate_ids=ids, selected_part_id="slim"
    )
    assert r["actions"][0]["args"]["part_id"] == "tall"
    assert "too tall" in r["reply"] or "too deep" in r["reply"]

    r = await _say(
        "previous one", removed=["dw1"], cavity=cavity, candidate_ids=ids, selected_part_id="tall"
    )
    assert r["actions"][0]["args"]["index"] == 2

    r = await _say("show me option 2", removed=["dw1"], cavity=cavity, candidate_ids=ids)
    assert (r["actions"][0]["name"], r["actions"][0]["args"]["index"]) == ("cycle_model", 1)

    r = await _say("put it back", removed=["dw1"], cavity=cavity, candidate_ids=ids)
    assert r["actions"] == [{"name": "restore_component", "args": {"component_id": "dw1"}}]
    assert "back" in r["reply"]


@pytest.mark.asyncio
async def test_compound_remove_and_measure(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("take out the dishwasher and measure the gap", removed=[])
    assert [a["name"] for a in r["actions"]] == ["remove_component", "measure_cavity"]
    assert r["reply"].startswith("Took out the dishwasher.")
    assert r["reply"].endswith("Taping the gap."), "the size is said once"


@pytest.mark.asyncio
async def test_without_the_app_state_the_server_remembers(monkeypatch, candidates):
    """An app that doesn't send `removed` (older builds): the server's own memory of what it took out."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    await _say("remove the dishwasher")
    r = await _say("measure it")
    assert r["actions"][0]["name"] == "measure_cavity"
    r = await _say("put it in there", candidate_ids=["tall", "ok"])
    assert r["actions"][0]["args"]["part_id"] == "ok"
    assert r["actions"][0]["args"]["clearance_mm"] == {"w": 5, "h": 5, "d": 10}, "the file's gap"


@pytest.mark.asyncio
async def test_state_gates_the_phrases(monkeypatch):
    calls = []

    async def fake_chat(role, messages, tools=None, **kw):
        calls.append(messages[-1]["content"])
        raise RuntimeError("stop here")

    monkeypatch.setattr(agent, "chat", fake_chat)
    session = agent._get_session("e2e")
    assert agent._replace_route(session, "remove the fridge", {"site": SITE}) is None, (
        "no fridge in the parts file"
    )
    assert agent._replace_route(session, "remove the wall", {"site": SITE}) is None, "not removable"
    session.site = SITE
    assert agent._replace_route(session, "measure it", {"site": SITE}) is None, "no gap open"
    assert agent._replace_route(session, "next one", {"site": SITE}) is None
    session.removed = ["dw1"]
    assert agent._replace_route(session, "measure it", {"site": SITE})[0] == "measure_cavity"
    assert agent._replace_route(session, "put it in there", {"site": SITE}) is None, (
        "no candidates yet"
    )
    session.site = None
    assert agent._replace_route(session, "remove the dishwasher", {}) is None, "no scene"
    # the headset closed the gap (More ▸ Take out): its `removed` wins. No gap: "measure it" is
    # the plain tape (structure_measure: nothing named, nothing pointed at), not measure_cavity.
    r = await agent.handle_command("e2e", "measure it", {"site": SITE, "removed": []})
    assert r["actions"] == [{"name": "equip_tool", "args": {"tool": "tape", "label": "Length"}}]
    assert calls == [], "the fast path answers"


@pytest.mark.parametrize(
    "text,usd,rest",
    [
        (
            "find a 30-inch induction range under $1,200 that fits",
            1200.0,
            "find a 30-inch induction range that fits",
        ),
        ("order matching hinges under $1,200.50 please", 1200.5, "order matching hinges please"),
        ("no more than 2,500 dollars", 2500.0, "no more than 2,500 dollars"[:0]),
        ("find hangers under $40, arriving by Friday", 40.0, None),
    ],
)
def test_a_spoken_limit_with_thousands(text, usd, rest):
    limits, left = agent._extract_limits(text)
    assert limits["max_total_usd"] == usd
    if rest is not None:
        assert left == rest


@pytest.mark.asyncio
async def test_remove_find_and_put_it_in_in_one_breath(monkeypatch, candidates):
    """ "remove the dishwasher, find a 24-inch dishwasher under $1,200 that fits, put it in": the
    limit is set, then the whole replace runs as one job (server/replace_job.py): the dishwasher
    comes out, the search fits its gap, the best fit goes in."""
    import asyncio

    from server import assets, runjob

    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    started = {}

    def fake_start_search(req):
        started.setdefault("req", req)  # the named kind first; then "dishwasher", its fallback
        job = Job(id=f"job-{len(jobs._jobs)}", status="done", query=req.query)
        job.candidates = search.fit_to_cavity([CANDIDATES[k] for k in ("tall", "ok")], req.cavity)
        jobs._jobs[job.id] = job
        return job

    async def fake_resolve(part, **kw):
        return part

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    monkeypatch.setattr(assets, "resolve_shared", fake_resolve)
    r = await _say(
        "remove the dishwasher, find a 24-inch dishwasher under $1,200 that fits, put it in",
        removed=[],
    )
    names = [a["name"] for a in r["actions"]]
    assert names == ["show_limits", "job_started"]
    assert r["actions"][0]["args"]["max_total_usd"] == 1200.0
    run = runjob.get(r["actions"][1]["args"]["run_id"])
    for _ in range(300):
        if run.status != "running":
            break
        await asyncio.sleep(0.01)
    acts = [a["name"] for a in run.actions if a["name"] != "job_step"]
    assert acts == [
        "job_started",
        "remove_component",
        "measure_cavity",
        "search_started",
        "place_part",
        "job_done",
    ]  # the 60 x 82 x 58 cm gap is a plausible dishwasher opening: no scale_gap
    assert started["req"].query == "24 inch dishwasher"
    assert started["req"].cavity.size_mm() == pytest.approx((600.0, 820.0, 580.0))


@pytest.mark.asyncio
async def test_option_before_anything_is_in_the_gap_places_it(monkeypatch, candidates):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("show me option 3", removed=["dw1"], candidate_ids=["tall", "ok", "slim"])
    (act,) = r["actions"]
    assert (act["name"], act["args"]["part_id"], act["args"]["fits"]) == (
        "place_part",
        "slim",
        "fits",
    )


def test_a_tight_fit_is_amber_not_red():
    cav = Cavity(w_m=0.61, h_m=0.876, d_m=0.65)
    f = replace.fit(Dims(w=606.3, h=876.3, d=622.3), cav)
    assert (f.status, replace.fits_word(f)) == ("too_big", "tight")
    assert replace.spoken_fit(f) == "It's tight: under a millimetre too tall."
    f3 = replace.fit(Dims(w=613, h=870, d=600), cav)
    assert (replace.fits_word(f3), replace.spoken_fit(f3)) == (
        "tight",
        "It's tight: 3 millimetres too wide.",
    )
    assert replace.fits_word(replace.fit(Dims(w=620, h=870, d=600), cav)) == "too_big"


@pytest.mark.parametrize(
    "text,axis,metres",
    [
        ("the opening is 34 and a half inches tall", "h", 0.8763),
        ("The gap is 24 inches wide.", "w", 0.6096),
        ("it's 610 mm wide", "w", 0.61),
        ("the opening's height is 87.6 cm", "h", 0.876),
    ],
)
def test_scale_phrases(text, axis, metres):
    m = replace.match(text)
    assert (m["kind"], m["axis"]) == ("scale", axis)
    assert m["metres"] == pytest.approx(metres, abs=1e-4)


@pytest.mark.asyncio
async def test_scale_from_the_gap_then_search(monkeypatch):
    """The kitchen reads ~1.47x small: "the opening is 34 and a half inches tall" sets the scale
    (the headset applies it to its own tape), and the search in the same breath uses it."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    started = {}

    def fake_start_search(req):
        started["req"] = req
        return Job(id="job3", status="running", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    # the headset's context: the file's gap at its current scale (1.0 here)
    small = {"component_id": "dw1", "label": "dishwasher", "w_m": 0.6, "h_m": 0.82, "d_m": 0.58}
    r = await _say(
        "the opening is 34 and a half inches tall and find a dishwasher that fits",
        removed=["dw1"],
        cavity=small,
    )
    assert [a["name"] for a in r["actions"]] == ["scale_gap", "search_started"]
    assert r["actions"][0]["args"] == {"component_id": "dw1", "axis": "h", "real_m": 0.8763}
    assert r["reply"].startswith("Setting the scale from the gap's height: 34 and a half inches.")
    w, h, _ = started["req"].cavity.size_mm()
    assert h == pytest.approx(876.3, abs=0.5)
    assert w == pytest.approx(600 * 0.8763 / 0.82, abs=0.5), "the file x the corrected scale"
