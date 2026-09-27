"""Install coach (server/coach.py): steps, verdicts, the drill rule, overrides, restarts,
OFFLINE, the endpoints, the agent and the voice relay. Only the Grok calls are mocked, with the
replies the live run got (tests/fixtures/coach, recorded 2026-09-26 on kitchen thumbs)."""

import asyncio
import base64
import json
import time
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from server import agent, cache, coach, jobs, llm, realtime
from server.app import app
from server.config import get_settings
from server.models import Dims, Job, Part
from tests.test_realtime import AUDIO, FakeXai, _receive_until, _response

FIX = Path(__file__).parent / "fixtures" / "coach"
PAGES = json.loads((FIX / "manual_pages_midea.json").read_text())
STEPS = json.loads((FIX / "steps_midea.json").read_text())
MIDEA = "midea-maw08u1qwt-8d6d08"


def reply_for(prefix: str, frame: str = "") -> dict:
    """A recorded vision reply by its first question (and frame hash, for the drill ones)."""
    raw = json.loads((FIX / "checks.json").read_text())
    return next(
        v["reply"]
        for k, v in raw.items()
        if k.split("|")[1].startswith(prefix[:40]) and k.startswith(frame)
    )


RIM = reply_for("Are any objects standing within a hand's")  # yes: bottles by the rim
VALVES = reply_for("Are the shut-off valves under the sink v")  # cant_see
FAUCET = reply_for("Is a faucet standing on the sink deck?")  # yes
DRILL_0141 = reply_for("Are the cotter pins installed", "ece45a09")  # three "outlets"


def jpg(tag: bytes = b"a") -> str:
    return base64.b64encode(b"\xff\xd8\xff\xe0" + tag).decode()


@pytest.fixture
def data(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path / "data"
    agent._sessions.clear()
    get_settings.cache_clear()


@pytest.fixture
def grok(monkeypatch):
    """Mock `llm.chat`: answers with the queued replies in order, records each call."""

    class Calls(list):
        queue: list

    calls, queue = Calls(), []

    async def fake_chat(role, messages, tools=None, **kw):
        calls.append({"role": role, "messages": messages, **kw})
        body = {k: v for k, v in queue.pop(0).items() if k not in ("cost_usd", "latency_s")}
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(body)))],
            usage=SimpleNamespace(cost_in_usd_ticks=8_300_000),
        )

    monkeypatch.setattr(llm, "chat", fake_chat)
    calls.queue = queue
    return calls


def _offline(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()


def _part(name: str, pid: str = MIDEA, manufacturer: str = "Midea") -> Part:
    dims = Dims(w=470, d=540, h=340)
    return Part(id=pid, name=name, manufacturer=manufacturer, model_no="MAW08U1QWT", dims_mm=dims)


def _seed_manual(part: Part) -> None:
    jobs.save_part(part)
    manual = {"part_id": part.id, "found": True, "title": "m.pdf", "pages": 44,
              "source_url": "https://www.midea.com/m.pdf", "model_match": True}  # fmt: skip
    cache.put("manual", part.id, manual)
    cache.put("manual_text", part.id, PAGES)


async def _faucet(session="s1") -> dict:
    """A started faucet coach's saved state."""
    return coach.load((await coach.start(session, None, "faucet_swap"))["coach_id"])


# --- steps ---------------------------------------------------------------------------------


def test_templates_have_numbered_visual_checks():
    for key in coach.JOBS:
        steps = coach.template_steps(key)
        assert steps and all(1 <= len(s["checks"]) <= coach.MAX_CHECKS for s in steps)
        assert all(coach.visual_question(c["question"]) for s in steps for c in s["checks"])
    faucet = coach.template_steps("faucet_swap")
    assert len(faucet) == 6 and [c["id"] for c in faucet[2]["checks"]] == ["c1", "c2"]
    assert [s["i"] for s in coach.template_steps("led_strip") if s["tool"] == "drill"] == [2]
    assert coach.job_for("Delta Leland kitchen faucet") == "faucet_swap"
    assert coach.job_for("8,000 BTU Window Air Conditioner") == "window_ac"


def test_manual_steps_keep_only_quotes_found_in_the_pdf():
    raw = json.loads(json.dumps(STEPS["steps"]))
    raw[1]["quote"] = "Bolt the bracket to the brick with masonry anchors."  # not in the manual
    raw[2]["page"] = 3  # wrong page: the quote is really on page 12
    raw[0]["checks"].append({"question": "Is the step done?", "expect": "yes"})  # status: dropped
    steps = coach.keep_quoted(raw, PAGES)
    assert len(steps) == len(STEPS["steps"]) - 1
    assert [s["i"] for s in steps] == list(range(len(steps)))
    assert steps[1]["page"] == 12 and steps[1]["quote"].startswith("A. Install the Main Support")
    assert all("done" not in c["question"] for c in steps[0]["checks"])
    drill = [s for s in steps if s["tool"] == "drill"]
    assert len(drill) == 1 and "1/8" in drill[0]["say"] and drill[0]["page"] == 13


@pytest.mark.asyncio
async def test_start_from_the_manual_once_then_from_the_cache(data, monkeypatch):
    part = _part("8,000 BTU U+ Smart Inverter Window Air Conditioner")
    _seed_manual(part)
    sent = []

    async def fake_responses(model, input, **kw):
        sent.append({"model": model, "input": input, **kw})
        text = json.dumps({"steps": STEPS["steps"]})
        return llm.ResponsesResult(text=text, citations=[], cost_usd=0.0102, tool_usage={})

    monkeypatch.setattr(llm, "responses", fake_responses)
    out = await coach.start("s1", part)
    assert out["source"] == "manual" and len(out["steps"]) == 8
    assert "=== PAGE 13 ===" in sent[0]["input"] and "=== PAGE 44 ===" not in sent[0]["input"]
    assert out["spoken"].startswith("Let's install the Midea MAW08U1QWT: 8 steps from its manual.")
    step = out["actions"][1]["args"]
    assert step["pdf_url"] == f"/parts/{MIDEA}/manual.pdf#page=11" and step["quote"]
    _offline(monkeypatch)
    again = await coach.start("s2", part)  # cached steps: no second call, works offline
    assert len(sent) == 1 and again["source"] == "manual"


@pytest.mark.asyncio
async def test_no_manual_falls_back_to_the_template(data, monkeypatch):
    monkeypatch.setattr(coach.manuals, "prefetch", lambda part: None)
    out = await coach.start("s1", _part("Window Air Conditioner"))
    assert out["source"] == "template" and out["job"] == "window_ac"
    with pytest.raises(coach.CantCoach) as exc:
        await coach.start("s1", _part("Smart doorbell"))
    assert exc.value.spoken == coach.NO_STEPS and not exc.value.refused


@pytest.mark.asyncio
async def test_licensed_trade_jobs_are_refused(data):
    with pytest.raises(coach.CantCoach) as exc:
        await coach.start("s1", _part("LG 12,000 BTU Ductless Mini Split", "lg-1", "LG"))
    assert exc.value.refused and "EPA-certified HVAC tech" in exc.value.spoken
    assert exc.value.spoken.startswith("I won't coach this one:")
    # a finished F13 check whose permit page says a homeowner can't pull it
    result = {"job": "water_heater_swap", "permit": {"who_can_pull": {"homeowner_allowed": "no"}}}
    jobs._jobs["r1"] = Job(id="r1", status="done", stage=coach.rules.STAGE, result=result)
    try:
        assert "licensed contractor" in coach.refusal("Rheem 40 gal water heater")
    finally:
        jobs._jobs.pop("r1")
    assert coach.refusal("Rheem 40 gal water heater") is None


# --- checks: verdicts in code ------------------------------------------------------------


@pytest.mark.asyncio
async def test_real_yes_no_replies_become_verdicts(data, grok):
    state = await _faucet()
    grok.queue += [RIM, VALVES, FAUCET]
    out = await coach.check(state, jpg(b"1"), frame_id="0221")
    assert out["verdict"] == "not_yet"  # #14's c1: bottles by the rim
    assert out["spoken"] == "Not yet: black shaker bottle and soap bottles near sink."
    assert out["results"][0]["box"] == [0.05, 0.52, 0.15, 0.7]  # percent -> 0-1
    coach.move(state, "next")
    out = await coach.check(state, jpg(b"1"))
    assert out["verdict"] == "look" and out["spoken"].endswith("Look under the sink at the valves.")
    state["i"] = 4  # the new faucet on the deck
    out = await coach.check(state, jpg(b"1"))
    assert out["verdict"] == "passed" and out["i"] == 5
    assert out["spoken"].startswith("Looks right. Step 6 of 6: Connect the supply lines")
    assert [a["name"] for a in out["actions"]] == ["coach_check", "coach_step"]
    assert state["done"]["4"] == "camera"
    call = grok[0]
    assert call["role"] == "coach_vision" and call["temperature"] == 0
    schema = call["response_format"]["json_schema"]["schema"]
    assert "electrical" not in schema["properties"]  # not a drill step
    assert "done" not in call["messages"][0]["content"][0]["text"].lower()


@pytest.mark.asyncio
async def test_the_same_frame_is_never_billed_twice(data, grok):
    state = await _faucet()
    grok.queue += [RIM]
    first = await coach.check(state, jpg(b"x"))
    again = await coach.check(state, jpg(b"x"))
    assert len(grok) == 1 and first["verdict"] == again["verdict"] == "not_yet"
    assert first["cost_usd"] == 0.00083 and again["cost_usd"] == 0.0


@pytest.mark.asyncio
async def test_status_style_reply_gives_no_verdict(data, grok):
    """G6 #13 answered "done" to a status question; that shape is refused, never judged."""
    state = await _faucet()
    grok.queue += [{"answers": [{"id": "c1", "answer": "done", "evidence": "clear", "box": None}]}]
    with pytest.raises(coach.CheckFailed):
        await coach.check(state, jpg())
    assert state["i"] == 0 and not (data / "cache" / "coach_check").exists()


def test_drill_point_above_an_outlet_stops():
    step = coach.template_steps("window_ac")[2]
    assert step["tool"] == "drill"
    verdict, _, hazard = coach.judge(step, DRILL_0141, [0.127, 0.10])  # just above the outlet
    assert verdict == "stop" and hazard["kind"] == "outlet"
    verdict, _, hazard = coach.judge(step, DRILL_0141, [0.45, 0.10])  # 20 % to the side
    assert verdict == "look" and hazard == {}
    verdict, _, _ = coach.judge(step, DRILL_0141, [0.127, 0.60])  # below the box: not "above"
    assert verdict == "look"
    no_drill = coach.template_steps("faucet_swap")[0]  # not a drill step: outlets don't matter
    assert coach.judge(no_drill, DRILL_0141, [0.127, 0.1])[0] == "look"


@pytest.mark.asyncio
async def test_drill_step_check_says_stop_and_always_the_finder_hint(data, grok):
    state = coach.load((await coach.start("s1", None, "led_strip"))["coach_id"])
    state["i"] = 2  # "Drill a 10 millimetre hole ..."
    no_hole = [{"id": "c1", "answer": "no", "evidence": "no hole", "box": None}]
    grok.queue += [DRILL_0141 | {"answers": no_hole}]
    out = await coach.check(state, jpg(b"d"), [0.127, 0.10])
    assert out["verdict"] == "stop" and "straight above an outlet" in out["spoken"]
    assert coach.WIRE_HINT in out["spoken"] and out["actions"][1]["name"] == "coach_stop"
    assert "electrical" in grok[0]["response_format"]["json_schema"]["schema"]["properties"]
    out = await coach.check(state, jpg(b"d"), [0.45, 0.10])  # cached frame, other spot
    assert out["verdict"] == "not_yet" and coach.WIRE_HINT in out["spoken"]
    assert len(grok) == 1
    with pytest.raises(ValueError):
        await coach.check(state, jpg(), [1.5, 0.2])
    with pytest.raises(ValueError):
        await coach.check(state, base64.b64encode(b"GIF89a").decode())


# --- the user's word, restarts, offline --------------------------------------------------


@pytest.mark.asyncio
async def test_i_did_it_overrides_and_lands_in_the_notebook(data, grok):
    state = await _faucet("nb")
    for _ in range(4):
        coach.move(state, "next")  # steps 1-4 on the user's word
    grok.queue += [FAUCET]
    assert (await coach.check(state, jpg()))["verdict"] == "passed"  # step 5 by camera
    out = coach.move(state, "next")
    assert out["status"] == "done" and out["actions"][0]["name"] == "coach_done"
    assert out["actions"][0]["args"]["checked"] == 1
    assert out["actions"][0]["args"]["overridden"] == 5
    notes = [e["text"] for e in coach.report._notebook_entries("nb")]
    assert len(notes) == 6 and "step 1: the user said it's done; not checked by camera" in notes[0]
    assert notes[-1].endswith("1 checked by camera, 5 on the user's word.")
    assert coach.move(state, "next")["spoken"] == "That job's done."


@pytest.mark.asyncio
async def test_back_and_repeat_keep_the_first_record(data):
    state = await _faucet()
    coach.move(state, "next")
    assert coach.move(state, "back")["i"] == 0
    again = coach.move(state, "repeat")
    assert again["i"] == 0 and again["spoken"].startswith("Step 1 of 6: Clear the counter")
    coach.move(state, "next")
    assert state["done"] == {"0": "said"}


@pytest.mark.asyncio
async def test_state_survives_a_restart(data):
    out = await _faucet("sess-r")
    coach.move(coach.load(out["coach_id"]), "next")
    agent._sessions.clear()  # the agent's session memory goes with the process
    with TestClient(app) as client:
        body = client.get(f"/coach/{out['coach_id']}").json()
        assert body["i"] == 1 and body["spoken"].startswith("Step 2 of 6")
        reply = client.post(
            "/agent/command", json={"session_id": "sess-r", "text": "repeat that"}
        ).json()
    assert reply["reply"] == body["spoken"] and reply["actions"][0]["name"] == "coach_step"
    assert coach.load("../../etc") is None and coach.current(None, "nobody") is None


@pytest.mark.asyncio
async def test_offline_reads_steps_and_says_i_did_it(data, grok, monkeypatch):
    state = await _faucet()
    grok.queue += [RIM]
    await coach.check(state, jpg(b"warm"))
    _offline(monkeypatch)
    out = await coach.check(state, jpg(b"new frame"))
    assert out["verdict"] == "offline" and out["spoken"] == coach.OFFLINE_LINE
    assert (await coach.check(state, jpg(b"warm")))["verdict"] == "not_yet"  # cached
    assert len(grok) == 1
    assert (await coach.start("s2", None, "faucet_swap"))["spoken"].startswith("Let's install")
    reply = await agent.handle_command("s1", "check it", {"frame_jpg_b64": jpg(b"other")})
    assert reply["reply"] == coach.OFFLINE_LINE  # the coach fast path runs offline too


# --- endpoints ------------------------------------------------------------------------------


def test_endpoints(data, grok):
    with TestClient(app) as client:
        assert client.post("/coach/start", json={}).status_code == 400
        assert client.post("/coach/start", json={"job": "roofing"}).status_code == 400
        r = client.post("/coach/start", json={"session_id": "e", "job": "faucet_swap"})
        cid = r.json()["coach_id"]
        assert r.status_code == 200 and len(r.json()["steps"]) == 6
        grok.queue += [RIM]
        frame = {"id": "0221", "jpg_b64": jpg()}
        r = client.post(f"/coach/{cid}/check", json={"frame": frame})
        assert r.json()["verdict"] == "not_yet"
        assert r.json()["actions"][0]["args"]["frame_id"] == "0221"
        bad = client.post(f"/coach/{cid}/check", json={"frame": {"id": "f", "jpg_b64": "!!"}})
        assert bad.status_code == 400
        grok.queue += [{"verdict": "done"}]
        frame = {"id": "f", "jpg_b64": jpg(b"z")}
        failed = client.post(f"/coach/{cid}/check", json={"frame": frame})
        assert failed.status_code == 502 and failed.json()["detail"]["spoken"] == coach.FAILED_LINE
        assert client.post(f"/coach/{cid}/advance").json()["i"] == 1
        assert client.post(f"/coach/{cid}/advance", json={"move": "back"}).json()["i"] == 0
        assert client.get("/coach/000000000000").status_code == 404
        jobs.save_part(_part("LG Ductless Mini-Split", "lg-2", "LG"))
        r = client.post("/coach/start", json={"part_id": "lg-2"})
        assert r.status_code == 409 and "HVAC tech" in r.json()["detail"]["spoken"]


# --- agent and voice ------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_agent_fast_paths(data, grok):
    say = agent.handle_command
    assert agent._coach_intent(agent._get_session("a1"), "check it") is None  # no coach yet
    out = await say("a1", "teach me to install the faucet", {})
    assert [a["name"] for a in out["actions"]] == ["coach_started", "coach_step"]
    assert out["reply"].startswith("Let's install the kitchen faucet: 6 steps.")
    out = await say("a1", "check it", {})  # no frame: no call
    assert out["reply"] == coach.HOLD_STILL and not grok
    grok.queue += [RIM]
    out = await say("a1", "check it", {"frame_jpg_b64": jpg()})
    assert out["reply"].startswith("Not yet: black shaker bottle")
    out = await say("a1", "I did it", {})
    assert out["reply"].startswith("Step 2 of 6") and agent._sessions["a1"].coach_id
    assert (await say("a1", "go back", {}))["reply"].startswith("Step 1 of 6")
    stale = {"frame_jpg_b64": jpg(), "frame_at": 0}  # the relay's frame, long gone
    assert (await say("a1", "done", stale))["reply"] == coach.HOLD_STILL


@pytest.mark.asyncio
async def test_agent_refuses_a_licensed_job_out_loud(data):
    jobs.save_part(_part("LG Ductless Mini-Split", "lg-3", "LG"))
    out = await agent.handle_command("a2", "walk me through it", {"selected_part_id": "lg-3"})
    assert out["reply"].startswith("I won't coach this one:") and out["actions"] == []


def test_coach_tools_hidden_until_a_coach_runs(data, monkeypatch):
    seen = []

    async def fake_chat(role, messages, tools=None, **kw):
        seen.append({t["function"]["name"] for t in tools})
        msg = SimpleNamespace(content="Aye.", tool_calls=None)
        return SimpleNamespace(choices=[SimpleNamespace(message=msg)])

    monkeypatch.setattr(agent, "chat", fake_chat)
    with TestClient(app) as client:
        client.post("/agent/command", json={"session_id": "h", "text": "hello there"})
        client.post("/coach/start", json={"session_id": "h", "job": "gutter_hanger"})
        client.post("/agent/command", json={"session_id": "h", "text": "hello again"})
    assert "start_coach" in seen[0] and not {"coach_step", "check_step"} & seen[0]
    assert {"coach_step", "check_step"} <= seen[1]


def test_voice_check_step_speaks_verbatim(data, grok, monkeypatch):
    """Over F1's relay: `frame` uplink, the model calls check_step, the coach line goes out as a
    force_message word for word, and there is no follow-up response.create."""
    started = asyncio.run(coach.start("v1", None, "faucet_swap"))
    grok.queue += [RIM]
    call = {"type": "response.function_call_arguments.done", "name": "check_step",
            "call_id": "c1", "arguments": "{}"}  # fmt: skip
    xai = FakeXai([_response(call)])
    monkeypatch.setattr(realtime, "_connect", lambda: xai)

    def forced() -> list[str]:
        items = [m.get("item", {}) for m in xai.sent if isinstance(m, dict)]
        return [i["content"][0]["text"] for i in items if i.get("type") == "force_message"]

    with TestClient(app) as client, client.websocket_connect("/voice/realtime?session_id=v1") as ws:
        ws.receive_json()
        ws.send_json({"type": "frame", "jpg_b64": jpg()})
        ws.send_bytes(AUDIO)
        ws.send_json({"type": "commit"})
        _receive_until(ws, "response_done")
        deadline = time.monotonic() + 3  # the line goes out once the tool task finishes
        while not forced() and time.monotonic() < deadline:
            time.sleep(0.02)
    assert agent._sessions["v1"].coach_id == started["coach_id"]
    assert forced() == ["Not yet: black shaker bottle and soap bottles near sink."]
    assert xai.types().count("response.create") == 1  # no model follow-up to paraphrase it
    items = [m.get("item", {}) for m in xai.sent if isinstance(m, dict)]
    output = next(i["output"] for i in items if i.get("type") == "function_call_output")
    assert json.loads(output)["verdict"] == "not_yet"
