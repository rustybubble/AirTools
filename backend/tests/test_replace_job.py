"""One sentence, the whole replace (server/replace_job.py): routing (fast path, the LLM's
replace_component tool), the chain's actions in order, the scale prior, the pick (fits or
tight only, within the limit), the models, and the follow-ups on the state it leaves. The
kitchen's real gap sizes (parts.r1.json at scale 1); search and assets are faked, no network."""

import asyncio
import json
import uuid
from collections import OrderedDict

import pytest
from openai.types.chat import ChatCompletion

from server import agent, assets, jobs, mandate, replace, replace_job, runjob, search
from server.config import get_settings
from server.keys import KeysExhausted
from server.models import Asset, Cavity, Dims, Job, Part, Seller

SITE = "kitchen-test"
IDENTITY = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]


def _comp(cid, label, cls, w, h, d, bbox):
    return {
        "id": cid,
        "label": label,
        "class": cls,
        "removable": True,
        "bbox": {"min": bbox[0], "max": bbox[1]},
        "cavity": {
            "node": f"cavity_{cid}",
            "estimated": True,
            "size_m": {"w": w, "h": h, "d": d},
            "insert": {"p": [0.0, 0.0, 0.0], "axes": IDENTITY},
        },
    }


# The kitchen scan's parts (scale 1: it reads ~1.47x small indoors).
PARTS = {
    "schema": "airtools.parts/1",
    "frame": "scene",
    "revision": 1,
    "components": [
        _comp("dw1", "dishwasher", "appliance", 0.4371, 0.5911, 0.4447,
              ([-1.51, -1.16, -0.69], [-1.07, -0.59, -0.24])),
        _comp("bc1", "base cabinet", "cabinet", 0.9492, 0.5826, 0.4512,
              ([-1.48, -1.16, -0.24], [-1.11, -0.58, 0.71])),
        _comp("fr1", "fridge", "appliance", 0.4921, 1.1777, 0.5428,
              ([-1.57, -1.15, 0.69], [-1.04, 0.02, 1.20])),
        _comp("rg1", "range", "appliance", 0.5166, 0.7932, 0.4401,
              ([-1.50, -1.16, -1.21], [-1.01, -0.38, -0.68])),
        {"id": "wall", "label": "wall", "class": "structure", "removable": False},
    ],
}  # fmt: skip


def _part(pid, maker, model, w, h, d, usd):
    return Part(
        id=pid,
        name=f"{maker} {model} 24 in. Built-In Dishwasher",
        manufacturer=maker,
        model_no=model,
        dims_mm=Dims(w=w, h=h, d=d),
        sellers=[Seller(name="Home Depot", price_usd=usd, shipping_usd=0.0)],
        recommended_seller=0,
    )


# The live cache's "dishwasher" answer (docs: 34.5 in opening = 876.3 mm).
DISHWASHERS = [
    _part("frigidaire-fdpc4221as", "Frigidaire", "FDPC4221AS", 609.6, 889.0, 635.0, 329.0),
    _part("whirlpool-wdp540hamw", "Whirlpool", "WDP540HAMW", 606.3, 876.3, 622.3, 459.0),
    _part("bosch-shx3ar75uc", "Bosch", "SHX3AR75UC", 598.0, 865.0, 573.0, 699.0),
]
RANGES = [
    _part("ge-jb645", "GE", "JB645RKSS", 759.0, 1181.0, 718.0, 749.0),  # too deep for 649
    _part("frigidaire-gcri3060", "Frigidaire", "GCRI3060AF", 759.0, 1168.0, 640.0, 1099.0),
    _part("ge-phs700", "GE", "PHS700AYFS", 759.0, 1166.0, 635.0, 1899.0),
]


@pytest.fixture(autouse=True)
def _scene(tmp_path, monkeypatch):
    base = tmp_path / "scene"
    (base / SITE).mkdir(parents=True)
    (base / SITE / "scene.json").write_text(
        json.dumps({"name": SITE, "revision": 1, "parts": {"file": "parts.r1.json"}})
    )
    (base / SITE / "parts.r1.json").write_text(json.dumps(PARTS))
    for other in ("zabel-gymnasium", "hospital-bg", "gt-lcc-tower", "synthetic-facade"):
        (base / other).mkdir()
        (base / other / "scene.json").write_text(json.dumps({"name": other, "revision": 1}))
    monkeypatch.setenv("SCENE_DIR", str(base))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    replace._cache.clear()
    monkeypatch.setattr(agent.safety, "start", lambda part: None)
    monkeypatch.setattr(jobs, "_jobs", OrderedDict())
    monkeypatch.setattr(runjob, "_runs", OrderedDict())
    monkeypatch.setattr(assets, "_inflight", {})
    monkeypatch.setattr(assets, "_upgrade_tried", set())
    monkeypatch.setattr(replace_job, "PROGRESS_EVERY_S", 0.01)
    agent._sessions.clear()
    yield
    agent._sessions.clear()
    replace._cache.clear()
    get_settings.cache_clear()


@pytest.fixture
def shop(monkeypatch):
    """jobs.start_search -> a finished job (fitted to the request's cavity like the real one);
    resolve_asset -> a tiny GLB on disk. `calls` logs the searches and the models built."""
    calls: dict[str, list] = {"search": [], "models": []}
    answers = {
        "dishwasher": DISHWASHERS,
        "range": RANGES,
        "30 inch induction range": RANGES,
        "induction range": RANGES,
    }

    def fake_start_search(req):
        calls["search"].append(req)
        found = [p.model_copy(deep=True) for p in answers.get(req.query, DISHWASHERS)]
        job = Job(id=uuid.uuid4().hex[:12], status="done", query=req.query)
        job.candidates = search.fit_to_cavity(found, req.cavity)
        for part in job.candidates:  # like jobs._run_search: part.json on disk, the GLB not yet
            jobs.save_part(part)
        jobs._jobs[job.id] = job
        jobs._done_events[job.id] = asyncio.Event()
        jobs._done_events[job.id].set()
        return job

    async def fake_resolve(part, *, allow_ai=True):
        calls["models"].append(part.id)
        await asyncio.sleep(0.01)
        pdir = assets.part_dir(part.id)
        pdir.mkdir(parents=True, exist_ok=True)
        (pdir / "model.glb").write_bytes(b"glTF")
        part = part.model_copy(update={"asset": Asset(tier="llm", status="ready")})
        (pdir / "part.json").write_text(part.model_dump_json())
        return part

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    monkeypatch.setattr(assets, "resolve_asset", fake_resolve)
    return calls


async def _no_llm_chat(role, messages, tools=None, **kw):
    raise AssertionError("the fast path must not call the LLM")


async def _say(text, session="auto", **ctx):
    context = {"site": SITE, "scale": 1.0, "removed": [], **ctx}
    return await agent.handle_command(session, text, context)


async def _finish(run_id: str) -> runjob.Run:
    for _ in range(500):
        run = runjob.get(run_id)
        if run.status != "running":
            return run
        await asyncio.sleep(0.01)
    raise AssertionError(f"run {run_id} still running: {runjob.body(runjob.get(run_id))}")


def _run_id(result) -> str:
    started = [a for a in result["actions"] if a["name"] == "job_started"]
    assert started, result
    return started[0]["args"]["run_id"]


def _named(actions, *names):
    return [a for a in actions if a["name"] in names]


# --- the scale prior ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "kind,gap,axis,inches,factor",
    [
        ("dishwasher", (0.4371, 0.5911, 0.4447), "h", 34.5, 1.482),
        ("range", (0.5166, 0.7932, 0.4401), "w", 30.0, 1.475),
        # 36 in wide would make it 86 in tall: the 30 in standard is the one that stays plausible
        ("fridge", (0.4921, 1.1777, 0.5428), "w", 30.0, 1.549),
        ("base cabinet", (0.9492, 0.5826, 0.4512), "h", 34.5, 1.504),
    ],
)
def test_the_kitchen_gaps_scale_from_their_standard_openings(kind, gap, axis, inches, factor):
    assert replace.implausible(kind, gap)
    prior = replace.scale_prior(kind, gap)
    assert (prior.axis, prior.real_m) == (axis, round(inches * replace.IN, 4))
    assert prior.factor == pytest.approx(factor, abs=0.002)
    scaled = tuple(v * prior.factor for v in gap)
    assert not replace.implausible(kind, scaled)
    assert replace.scale_prior(kind, scaled) is None  # plausible now: nothing to do


def test_no_prior_for_a_plausible_gap_or_an_unknown_kind():
    assert replace.scale_prior("dishwasher", (0.61, 0.876, 0.62)) is None
    assert replace.scale_prior(None, (0.1, 0.1, 0.1)) is None
    assert replace.kind_of("old stove") == "range"
    assert replace.kind_of("sink cabinet") == "base cabinet"
    assert replace.kind_of("counter-depth fridge") == "fridge"
    assert replace.kind_of("microwave") is None


# --- the phrases ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "text,component,query",
    [
        ("Replace the dishwasher with a new one that fits.", "dw1", "dishwasher"),
        ("replace the dishwasher", "dw1", "dishwasher"),
        ("okay grok, swap out the dishwasher for one that fits", "dw1", "dishwasher"),
        ("the dishwasher is broken, find me a new one and put it in", "dw1", "dishwasher"),
        ("take out the dishwasher and put in a new one", "dw1", "dishwasher"),
        ("take out the dishwasher, find one that fits and put it in", "dw1", "dishwasher"),
        ("get a counter-depth fridge in here", "fr1", "counter-depth fridge"),
        ("get me a new fridge", "fr1", "fridge"),
        ("replace the stove with a 30-inch induction one", "rg1", "30 inch induction range"),
        ("upgrade the cabinet", "bc1", "base cabinet"),
        # the voice test's p09: two sentences, the limit cut out first
        ("Remove the range. Find a 30-inch induction range that fits.", "rg1",
         "30 inch induction range"),
        ("take out the fridge and find a counter depth fridge that fits", "fr1",
         "counter depth fridge"),
    ],
)  # fmt: skip
def test_replace_sentences_route_to_the_job(text, component, query):
    session = agent.Session(id="p", site=SITE)
    fast = agent._fast_route(session, replace.normalize(text), {"site": SITE})
    assert fast is not None and fast[0] == "replace_component", fast
    assert (fast[1]["component"], fast[1]["query"]) == (component, query)


@pytest.mark.parametrize(
    "text",
    [
        "take out the dishwasher and measure the gap",  # the step flow's compound
        "find a dishwasher that fits",  # no "new": not a replace on its own
        "replace the batteries",  # not a part of this scene
        "swap the finish for brushed nickel",
        "remove the dishwasher",
        "show me the next model",
    ],
)
def test_not_a_replace(text):
    session = agent.Session(id="p", site=SITE)
    fast = agent._fast_route(session, text, {"site": SITE})
    assert fast is None or fast[0] != "replace_component", fast


def test_a_new_one_with_its_gap_open_is_the_step_flows_search():
    session = agent.Session(id="p", site=SITE, removed=["dw1"])
    fast = agent._fast_route(session, "find a new dishwasher that fits", {"site": SITE})
    assert fast[0] == "find_in_gap"
    fast = agent._fast_route(session, "replace the dishwasher", {"site": SITE})
    assert fast[0] == "replace_component"  # "replace" still means the whole job


def test_replace_this_takes_the_part_the_pointer_is_on():
    session = agent.Session(id="p", site=SITE)
    ctx = {"site": SITE, "pointer": [-1.2, -0.8, -0.9]}  # inside the range's box
    fast = agent._fast_route(session, "replace this with a new one", ctx)
    assert fast[1]["component"] == "rg1"
    assert agent._fast_route(session, "replace it", {"site": SITE}) is None  # nothing aimed at
    session.removed = ["fr1"]
    assert agent._fast_route(session, "replace it", {"site": SITE})[1]["component"] == "fr1"


# --- the whole job ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_one_sentence_replaces_the_dishwasher(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("Replace the dishwasher with a new one that fits.")
    assert [a["name"] for a in r["actions"]] == ["job_started"]
    started = r["actions"][0]["args"]
    assert started["steps"] == replace_job.STEPS
    assert (started["kind"], started["title"]) == ("replace", "Replace the dishwasher")
    assert r["reply"].startswith("Replacing the dishwasher")

    run = await _finish(started["run_id"])
    assert run.status == "done"
    acts = runjob.body(run, 0)["actions"]
    order = [a["name"] for a in acts if a["name"] not in ("job_step",)]
    assert order == [
        "job_started",
        "remove_component",
        "measure_cavity",
        "scale_gap",
        "search_started",
        "place_part",
        "job_done",
    ]
    steps = {s["name"]: s for s in run.steps}
    assert all(s["status"] == "done" for s in run.steps), steps
    # each step's job_step comes right before its own actions
    names = [a["name"] if a["name"] != "job_step" else f"step:{a['args']['name']}" for a in acts]
    assert names.index("step:remove") < names.index("remove_component")
    assert names.index("step:scale") < names.index("scale_gap") < names.index("step:search")

    (scale,) = _named(acts, "scale_gap")
    assert scale["args"] == {"component_id": "dw1", "axis": "h", "real_m": 0.8763}
    assert "scaled it from a standard 34½″ dishwasher opening (×1.48)" in steps["scale"]["spoken"]

    (req,) = shop["search"]
    assert req.query == "dishwasher"
    assert req.cavity.component_id == "dw1"
    assert req.cavity.size_mm() == pytest.approx((648.0, 876.3, 659.3), abs=0.5)

    (place,) = _named(acts, "place_part")
    a = place["args"]
    assert (a["part_id"], a["component_id"], a["fits"]) == ("whirlpool-wdp540hamw", "dw1", "fits")
    assert a["model_url"] == "/parts/whirlpool-wdp540hamw/model.glb"
    assert a["clearance_mm"] == {"w": 42, "h": 0, "d": 37}
    assert a["cycle"] == {"index": 0, "count": 3}
    assert steps["pick"]["spoken"] == "Picked the Whirlpool WDP540HAMW: an exact fit, $459."
    # the pick and the next two have real GLBs before job_done
    for pid in ("whirlpool-wdp540hamw", "bosch-shx3ar75uc", "frigidaire-fdpc4221as"):
        assert (assets.part_dir(pid) / "model.glb").exists()
    assert sorted(shop["models"]) == sorted(p.id for p in DISHWASHERS)

    done = acts[-1]["args"]
    assert (done["status"], done["kind"], done["part_id"], done["fits"]) == (
        "done",
        "replace",
        "whirlpool-wdp540hamw",
        "fits",
    )
    assert done["spoken"] == (
        "The scan reads small; scaled it from a standard 34½″ dishwasher opening (×1.48). "
        "Put a Whirlpool WDP540HAMW in the dishwasher gap, an exact fit. "
        "Say next one to see the other two."
    )
    assert done["summary"] == "Whirlpool WDP540HAMW in the dishwasher gap · an exact fit"
    assert (done["total_usd"], done["models_ready"]) == (459.0, 3)

    session = agent._sessions["auto"]
    assert session.removed == ["dw1"]
    assert session.gap_index == 0
    assert session.scale == pytest.approx(1.4825, abs=0.001)
    ids = [p.id for p in jobs.get(session.job_id).candidates]
    assert session.candidate_ids == ids

    # --- the follow-ups, as the headset sends them (its calibration, its candidates)
    ctx = {"scale": session.scale, "removed": ["dw1"], "candidate_ids": ids}
    r = await _say("next one", selected_part_id="whirlpool-wdp540hamw", **ctx)
    assert r["actions"][0]["name"] == "cycle_model"
    assert r["actions"][0]["args"]["index"] == 1
    assert r["reply"].startswith("Option 2 of 3, the Bosch SHX3AR75UC.")
    r = await _say("show me option 3", selected_part_id="bosch-shx3ar75uc", **ctx)
    assert (r["actions"][0]["name"], r["actions"][0]["args"]["index"]) == ("cycle_model", 2)
    r = await _say("undo", **ctx)
    assert r["actions"] == [{"name": "undo_edit", "args": {}}]
    r = await _say("put it back", **ctx)
    assert r["actions"] == [{"name": "restore_component", "args": {"component_id": "dw1"}}]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "ctx,said,pick",
    [
        # the kitchen's site default (the app starts it at x1.63): the gap is 712 x 963 x 725 mm,
        # every 24 in dishwasher fits; the search's order (the cheapest that fits) decides
        ({"scale": 1.63}, "×1.63", "frigidaire-fdpc4221as"),
        ({"scale": 1.4826}, "×1.48", "whirlpool-wdp540hamw"),
        ({"scale": 1.0, "scale_source": "user"}, "×1.00", None),  # deliberate: nothing fits
    ],
)
async def test_a_calibrated_scene_keeps_its_scale(monkeypatch, shop, ctx, said, pick):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("replace the dishwasher", **ctx)
    run = await _finish(_run_id(r))
    steps = {s["name"]: s for s in run.steps}
    assert steps["scale"]["status"] == "skipped"
    assert steps["scale"]["spoken"] == f"The scale's already set ({said}); kept it."
    acts = runjob.body(run, 0)["actions"]
    assert not _named(acts, "scale_gap")
    placed = _named(acts, "place_part")
    assert (placed[0]["args"]["part_id"] if placed else None) == pick
    assert not acts[-1]["args"]["spoken"].startswith("The scan reads")
    if ctx["scale"] == 1.63:
        assert shop["search"][0].cavity.size_mm() == pytest.approx((712.5, 963.5, 724.9), abs=0.5)
        assert placed[0]["args"]["fits"] == "fits"


@pytest.mark.asyncio
async def test_nothing_that_fits_stops_before_placing_and_says_why(monkeypatch, shop):
    """The voice test's p05: a red "too tall by 285 mm" went in. The job only places a part that
    fits or is tight; none does at this (wrong) calibration, so it stops at the pick."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("replace the dishwasher", scale=1.2)  # calibrated, but too small
    run = await _finish(_run_id(r))
    assert run.status == "stopped"
    steps = {s["name"]: s for s in run.steps}
    assert steps["pick"]["status"] == "stopped"
    assert steps["pick"]["spoken"].startswith("None of the 3 fits the 52 × 71 × 53 cm gap")
    assert (steps["model"]["status"], steps["place"]["status"]) == ("pending", "pending")
    acts = runjob.body(run, 0)["actions"]
    assert not _named(acts, "place_part")
    done = acts[-1]["args"]
    assert done["status"] == "stopped" and done["part_id"] is None
    assert done["summary"].startswith("Stopped: None of the 3 fits")


def test_the_scale_hint_only_at_an_uncalibrated_scale_on_an_implausible_gap():
    small = Cavity(w_m=0.4371, h_m=0.5911, d_m=0.4447, label="dishwasher")
    hint = replace.scale_hint(small, ["too_big", "too_big"])
    assert hint == (
        "The scan may read small; say “the opening is 34 and a half inches tall” to set the scale."
    )
    assert replace.match(hint.split("“")[1].split("”")[0])["kind"] == "scale"  # it's sayable
    assert replace.scale_hint(small, ["too_big", "tight"]) is None
    assert replace.scale_hint(small, ["too_big"], scale=1.48) is None
    assert replace.scale_hint(Cavity(w_m=0.3, h_m=0.3, d_m=0.3, label="microwave"), []) is None


@pytest.mark.asyncio
async def test_the_price_limit_picks_the_fit_under_it(monkeypatch, shop):
    """ "Remove the range. Find a 30-inch induction range under $1,200 that fits." (p09): the
    range comes out (not the stale fridge gap), the search fits the range's gap, the limit holds."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    text = "Remove the range. Find a 30-inch induction range under $1,200 that fits."
    r = await _say(text, removed=["fr1"], cavity={"component_id": "fr1", "w_m": 0.49,
                                                   "h_m": 1.18, "d_m": 0.54})  # fmt: skip
    assert [a["name"] for a in r["actions"]] == ["show_limits", "job_started"]
    assert r["actions"][0]["args"]["max_total_usd"] == 1200.0
    run = await _finish(_run_id(r))
    acts = runjob.body(run, 0)["actions"]
    assert _named(acts, "remove_component")[0]["args"] == {"component_id": "rg1"}
    assert _named(acts, "scale_gap")[0]["args"]["axis"] == "w"  # a 30 in range
    first = shop["search"][0]
    assert (first.query, first.cavity.component_id) == ("30 inch induction range", "rg1")
    # GE JB645 is too deep; Frigidaire GCRI3060 ($1,099) fits; GE PHS700 fits but is $1,899
    assert _named(acts, "place_part")[0]["args"]["part_id"] == "frigidaire-gcri3060"
    assert agent._sessions["auto"].removed == ["fr1", "rg1"]


@pytest.mark.asyncio
async def test_over_the_limit_when_only_that_fits(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    mandate.set_limits("auto", source="voice", text="under $500", max_total_usd=500.0)
    r = await _say("replace the stove")
    run = await _finish(_run_id(r))
    steps = {s["name"]: s for s in run.steps}
    assert steps["pick"]["spoken"] == (
        "Picked the Frigidaire GCRI3060AF: 2 mm to spare, $1,099. It's over your $500; nothing "
        "under it fits."
    )
    done = runjob.body(run, 0)["actions"][-1]["args"]
    assert "It's over your $500; nothing under it fits." in done["spoken"]  # said, not only shown
    assert steps["search"]["spoken"] == (
        "Found three ranges for the 76 × 117 × 65 cm gap: two fit, one too big."
    )


@pytest.mark.asyncio
async def test_a_slow_search_reports_progress_then_times_out(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    monkeypatch.setattr(replace_job, "SEARCH_CAP_S", 0.2)

    def never(req):
        job = Job(id=uuid.uuid4().hex[:12], status="running", stage="charting the candidates")
        jobs._jobs[job.id] = job
        jobs._done_events[job.id] = asyncio.Event()
        return job

    monkeypatch.setattr(jobs, "start_search", never)
    r = await _say("replace the dishwasher")
    run = await _finish(_run_id(r))
    running = [
        a for a in runjob.body(run, 0)["actions"]
        if a["name"] == "job_step" and a["args"]["status"] == "running"
    ]  # fmt: skip
    assert running and "charting the candidates" in running[-1]["args"]["spoken"]
    steps = {s["name"]: s for s in run.steps}
    assert steps["search"]["status"] == "stopped"
    assert "took too long" in steps["search"]["spoken"]


@pytest.mark.asyncio
async def test_a_fresh_query_falls_back_to_the_plain_kind(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    real = jobs.start_search

    def empty_for_the_fancy_one(req):
        job = real(req)
        if req.query != "range":
            job.candidates = []
        return job

    monkeypatch.setattr(jobs, "start_search", empty_for_the_fancy_one)
    r = await _say("replace the range with a 30-inch induction one")
    run = await _finish(_run_id(r))
    steps = {s["name"]: s for s in run.steps}
    assert "these are plain ranges" in steps["search"]["spoken"]
    assert run.status == "done"


@pytest.mark.asyncio
async def test_a_second_job_while_one_runs_is_refused(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    gate = asyncio.Event()
    real = assets.resolve_asset

    async def slow(part, **kw):
        await gate.wait()
        return await real(part, **kw)

    monkeypatch.setattr(assets, "resolve_asset", slow)
    first = await _say("replace the dishwasher")
    second = await _say("replace the fridge")
    assert second["actions"] == []
    assert second["reply"] == "Can't do that yet: a job is already running; give it a moment."
    gate.set()
    await _finish(_run_id(first))


# --- the LLM's replace_component tool (free phrasing), with Grok's tool-call shape ------------


def _tool_call(name: str, arguments: dict) -> ChatCompletion:
    return ChatCompletion.model_validate(
        {
            "id": "chatcmpl-grok",
            "object": "chat.completion",
            "created": 1,
            "model": "grok-4.7",
            "choices": [
                {
                    "index": 0,
                    "finish_reason": "tool_calls",
                    "message": {
                        "role": "assistant",
                        "content": None,
                        "tool_calls": [
                            {
                                "id": "call_0",
                                "type": "function",
                                "function": {"name": name, "arguments": json.dumps(arguments)},
                            }
                        ],
                    },
                }
            ],
        }
    )


@pytest.mark.asyncio
async def test_the_llm_routes_free_phrasing_to_replace_component(monkeypatch, shop):
    seen = {}

    async def grok(role, messages, tools=None, **kw):
        seen["system"] = messages[0]["content"]
        seen["tools"] = [t["function"]["name"] for t in tools]
        args = {"component": "stove", "query": "induction range", "max_price_usd": 1200}
        return _tool_call("replace_component", args)

    monkeypatch.setattr(agent, "chat", grok)
    r = await _say("my old stove died, can you sort me out with an induction one")
    assert "replace_component" in seen["tools"]
    assert "Scene parts that come out: dishwasher, base cabinet, fridge, range" in seen["system"]
    assert [a["name"] for a in r["actions"]] == ["show_limits", "job_started"]
    assert r["reply"].startswith("Replacing the range")
    run = await _finish(_run_id(r))
    assert shop["search"][0].query == "induction range"
    assert _named(runjob.body(run, 0)["actions"], "place_part")[0]["args"]["component_id"] == "rg1"


@pytest.mark.asyncio
async def test_the_tool_is_hidden_without_scene_parts_and_refuses_unknown_parts(monkeypatch):
    seen = {}

    async def grok(role, messages, tools=None, **kw):
        seen.setdefault("tools", [t["function"]["name"] for t in tools])
        return _tool_call("replace_component", {"component": "microwave", "query": None,
                                                "max_price_usd": None})  # fmt: skip

    monkeypatch.setattr(agent, "chat", grok)
    await agent.handle_command("nosite", "hello there", {})
    assert "replace_component" not in seen["tools"]
    r = await _say("the microwave is dead, swap it for a new one", session="mw")
    assert r["reply"] == "Can't do that yet: there's no microwave in this scan to replace."
    assert r["actions"] == []


# --- the voice test's other findings -----------------------------------------------------------


@pytest.mark.asyncio
async def test_a_provider_failure_is_a_busy_reply_not_a_500(monkeypatch):
    async def exhausted(role, messages, tools=None, **kw):
        raise KeysExhausted("GROQ_API_KEY: all 3 keys rate limited")

    monkeypatch.setattr(agent, "chat", exhausted)
    r = await agent.handle_command("busy", "what's the weather like", {})
    assert r == {"reply": agent.BUSY_REPLY, "actions": [], "job_id": None}


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "text,site",
    [
        ("Show me the gym model.", "zabel-gymnasium"),
        ("open the hospital scan", "hospital-bg"),
        ("show me the GT tower model", "gt-lcc-tower"),
        ("show me the model of the kitchen", SITE),
    ],
)
async def test_show_me_the_gym_model_is_model_view(monkeypatch, text, site):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    assert agent._show_model_intent("show me the bathroom model") is None  # not a site
    # p14: with the fridge gap open it searched "gym models that fit the gap"
    r = await _say(text, removed=["fr1"])
    assert r["actions"] == [{"name": "show_model", "args": {"site": site}}]


@pytest.mark.asyncio
async def test_whispers_fraction_slash_sets_the_scale(monkeypatch):
    """p03: Whisper wrote "34 1⁄2" (U+2044); the scale phrase has to match it."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    said = (
        "The opening is 34 1⁄2 inches tall",
        "the opening is thirty-four and a half inches tall",
        "The opening is 34½ inches tall.",
    )
    for text in said:
        r = await _say(text, removed=["dw1"], session=f"s-{text[:12]}")
        assert r["actions"][0]["name"] == "scale_gap", (text, r)
        assert r["actions"][0]["args"]["real_m"] == pytest.approx(0.8763)


@pytest.mark.asyncio
async def test_the_step_flow_says_how_to_fix_the_scale(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    monkeypatch.setattr(
        jobs, "start_search", lambda req: Job(id="j1", status="running", query=req.query)
    )
    r = await _say("find a dishwasher that fits", removed=["dw1"])
    assert r["reply"].endswith("say “the opening is 34 and a half inches tall” to set the scale.")
    r = await _say("find a dishwasher that fits", removed=["dw1"], scale=1.48, session="cal")
    assert "set the scale" not in r["reply"]
    # the search's own summary (GET /parts/jobs/{id}) says it too when nothing fits
    cav = Cavity(w_m=0.4371, h_m=0.5911, d_m=0.4447, label="dishwasher", component_id="dw1")
    job = Job(id="j2", status="done", query="dishwasher")
    job.candidates = search.fit_to_cavity([p.model_copy() for p in DISHWASHERS], cav)
    jobs._cavities["j2"] = cav
    assert jobs.summary(job).endswith("to set the scale.")


# --- models: shared resolution, proxy upgrade ---------------------------------------------------


@pytest.mark.asyncio
async def test_two_callers_share_one_model_build(monkeypatch):
    built = []

    async def fake(part, *, allow_ai=True):
        built.append(part.id)
        await asyncio.sleep(0.02)
        pdir = assets.part_dir(part.id)
        pdir.mkdir(parents=True, exist_ok=True)
        (pdir / "model.glb").write_bytes(b"glTF")
        done = part.model_copy(update={"asset": Asset(tier="llm", status="ready")})
        (pdir / "part.json").write_text(done.model_dump_json())
        return done

    monkeypatch.setattr(assets, "resolve_asset", fake)
    part = DISHWASHERS[1]
    a, b = await asyncio.gather(assets.resolve_shared(part), assets.resolve_shared(part))
    assert built == [part.id]
    assert a.asset.tier == b.asset.tier == "llm"
    await assets.resolve_shared(part)  # ready on disk: no third build
    assert built == [part.id]


@pytest.mark.asyncio
async def test_a_proxy_box_gets_one_more_try_at_a_real_model(monkeypatch):
    import trimesh

    part = DISHWASHERS[0].model_copy(update={"asset": Asset(tier="proxy", status="ready")})
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True, exist_ok=True)
    (pdir / "model.glb").write_bytes(b"box")
    (pdir / "part.json").write_text(part.model_dump_json())
    tries = []

    async def generated(p, photo, d):
        tries.append(p.id)
        return trimesh.Scene(trimesh.creation.box((0.6, 0.9, 0.6))), "llm", 0.0

    monkeypatch.setattr(assets, "_generated", generated)
    better = await assets.upgrade_proxy(part)
    assert better.asset.tier == "llm"
    assert (pdir / "model.glb").read_bytes()[:4] == b"glTF"
    assert json.loads((pdir / "part.json").read_text())["asset"]["tier"] == "llm"
    await assets.upgrade_proxy(part)  # once per part per run
    assert tries == [part.id]


@pytest.mark.asyncio
async def test_when_nothing_fits_it_searches_for_the_gaps_size(monkeypatch, shop):
    """Live on :8005: "the fridge is too small, get us a bigger one" found 36-60 in fridges for the
    kitchen's 80 cm fridge gap (x1.63). Nothing fit, so it searches for "30 inch fridge" once more."""
    big = [
        _part("samsung-rf32", "Samsung", "RF32CG5D00SR", 914.0, 1778.0, 921.0, 1599.0),
        _part("forno-ffffd", "Forno", "FFFFD1722", 1518.0, 2134.0, 754.0, 2899.0),
    ]
    thirty = [_part("ge-gie18gs", "GE", "GIE18GSNRSS", 756.0, 1683.0, 822.0, 899.0)]
    real = jobs.start_search

    def by_query(req):
        job = real(req)
        found = {"bigger fridge": big, "30 inch fridge": thirty}.get(req.query)
        if found is not None:
            job.candidates = search.fit_to_cavity([p.model_copy() for p in found], req.cavity)
        return job

    monkeypatch.setattr(jobs, "start_search", by_query)

    async def grok(role, messages, tools=None, **kw):
        args = {"component": "fridge", "query": "bigger fridge", "max_price_usd": None}
        return _tool_call("replace_component", args)

    monkeypatch.setattr(agent, "chat", grok)
    r = await _say("the fridge is too small for us, get us a bigger one", scale=1.63)
    run = await _finish(_run_id(r))
    assert [q.query for q in shop["search"]] == ["bigger fridge", "30 inch fridge"]
    steps = {s["name"]: s for s in run.steps}
    assert steps["search"]["spoken"].endswith(
        "None of the bigger fridges fit; these are 30 inch fridges."
    )
    acts = runjob.body(run, 0)["actions"]
    assert _named(acts, "place_part")[0]["args"]["part_id"] == "ge-gie18gs"
    progress = [a["args"]["spoken"] for a in acts if a["name"] == "job_step"]
    assert "None of those fit; searching for 30 inch fridges." in progress


@pytest.mark.asyncio
async def test_after_a_stopped_job_next_one_puts_a_model_in(monkeypatch, shop):
    """Nothing stands in the gap after a job that stopped at the pick: "next one" places a model
    (place_part, which loads by URL) instead of swapping one that isn't there (cycle_model)."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("replace the dishwasher", scale=1.2)
    run = await _finish(_run_id(r))
    assert run.status == "stopped"
    ids = agent._sessions["auto"].candidate_ids
    r = await _say("next one", scale=1.2, removed=["dw1"], candidate_ids=ids)
    assert r["actions"][0]["name"] == "place_part"


def test_a_candidates_model_is_built_on_first_request(monkeypatch, shop):
    """GET /parts/{id}/model.glb for a searched part whose GLB isn't built yet builds it (once)."""
    from fastapi.testclient import TestClient

    from server.app import app

    part = DISHWASHERS[2].model_copy()
    jobs.save_part(part)  # part.json only, like a fresh search's candidate
    with TestClient(app) as client:
        first = client.get(f"/parts/{part.id}/model.glb")
        again = client.get(f"/parts/{part.id}/model.glb")
        missing = client.get("/parts/no-such-part/model.glb")
    assert first.status_code == again.status_code == 200
    assert first.content[:4] == b"glTF"
    assert shop["models"] == [part.id]
    assert missing.status_code == 404
