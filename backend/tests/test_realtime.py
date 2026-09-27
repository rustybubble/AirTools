"""WS /voice/realtime relay against a fake xAI socket replaying a realistic event transcript
(shape from G2's live call #3: preamble audio, function call, tool result, answer audio)."""

import asyncio
import json
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from server import agent, jobs, realtime
from server.app import app
from server.models import Job

AUDIO = b"\x01\x00" * 2400  # 0.1 s of PCM16 24 kHz


class FakeXai:
    """Records what the relay sends; each response.create plays the next scripted response."""

    def __init__(self, responses: list[list]):
        self.sent: list = []
        self.responses = list(responses)
        self.inbox: asyncio.Queue = asyncio.Queue()
        self.inbox.put_nowait(json.dumps({"type": "session.updated", "session": {}}))

    async def send(self, msg):
        self.sent.append(msg if isinstance(msg, bytes) else json.loads(msg))
        if isinstance(msg, str) and json.loads(msg)["type"] == "input_audio_buffer.commit":
            self.inbox.put_nowait(json.dumps({"type": "input_audio_buffer.committed"}))
        if isinstance(msg, str) and json.loads(msg)["type"] == "response.create":
            for event in self.responses.pop(0) if self.responses else []:
                self.inbox.put_nowait(event if isinstance(event, bytes) else json.dumps(event))

    def __aiter__(self):
        return self

    async def __anext__(self):
        msg = await self.inbox.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    def types(self) -> list[str]:
        return [m["type"] if isinstance(m, dict) else "<audio>" for m in self.sent]


def _response(*events, status="completed") -> list:
    return [
        {"type": "response.created", "response": {"id": "resp_1"}},
        *events,
        {"type": "response.done", "response": {"status": status}},
    ]


def _transcript(text: str) -> list:
    return [
        {"type": "response.output_audio_transcript.delta", "delta": text},
        {"type": "response.output_audio_transcript.done", "transcript": text},
    ]


TOOL_TURN = _response(
    AUDIO,
    *_transcript("Searching the supply shops."),
    {
        "type": "response.function_call_arguments.done",
        "name": "find_part",
        "call_id": "call_1",
        "arguments": json.dumps({"query": "K-style gutter hanger"}),
    },
)
ANSWER_TURN = _response(AUDIO, *_transcript("On it, captain."))
# the model calls find_part without a spoken preamble -> the relay must ask for a follow-up
SILENT_TOOL_TURN = [e for e in TOOL_TURN if e is not AUDIO and "transcript" not in e["type"]]


@pytest.fixture
def fake(monkeypatch):
    agent._sessions.clear()
    holder = {}

    def install(*responses):
        holder["xai"] = FakeXai(list(responses))
        monkeypatch.setattr(realtime, "_connect", lambda: holder["xai"])
        return holder["xai"]

    started = []

    def fake_start_search(req):
        started.append(req)
        job = Job(id=f"job{len(started)}", status="running", query=req.query)
        jobs._jobs[job.id] = job
        return job

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    install.started = started
    yield install
    agent._sessions.clear()


def _receive_until(ws, kind: str, limit: int = 50) -> list:
    """All messages up to and including the first JSON message of `kind` (bytes kept as-is)."""
    got = []
    for _ in range(limit):
        msg = ws.receive()
        item = msg.get("bytes") if msg.get("bytes") is not None else json.loads(msg["text"])
        got.append(item)
        if isinstance(item, dict) and item["type"] == kind:
            return got
    raise AssertionError(f"no {kind!r} in {got}")


def test_tool_call_turn_end_to_end(fake):
    xai = fake(SILENT_TOOL_TURN, ANSWER_TURN)
    with TestClient(app) as client, client.websocket_connect("/voice/realtime?session_id=t1") as ws:
        assert ws.receive_json() == {"type": "ready"}
        ws.send_json(
            {"type": "context", "context": {"measurement": {"label": "run", "value_m": 3.66}}}
        )
        ws.send_bytes(AUDIO)
        ws.send_json({"type": "commit"})

        # the tool runs as its own task, so its action may land just after response_done
        got = _receive_until(ws, "response_done") + _receive_until(ws, "response_done")
        assert {
            "type": "action",
            "action": {"name": "search_started", "args": {"job_id": "job1"}},
        } in got
        assert AUDIO in got
        assert {
            "type": "transcript",
            "role": "assistant",
            "text": "On it, captain.",
            "final": True,
        } in got

    # the session config went up first, in the flat realtime tool shape, push-to-talk
    session = xai.sent[0]["session"]
    assert xai.sent[0]["type"] == "session.update"
    assert session["turn_detection"] is None
    assert session["voice"] == "rex"
    assert {t["name"] for t in session["tools"]} >= {"find_part", "hanger_plan", "btu_for_room"}
    # mic audio forwarded untouched, then commit + exactly one response.create per step
    types = xai.types()
    assert types[1:4] == ["<audio>", "input_audio_buffer.commit", "response.create"]
    assert types.count("response.create") == 2
    out = next(
        m
        for m in xai.sent
        if isinstance(m, dict)
        and m["type"] == "conversation.item.create"
        and m["item"]["type"] == "function_call_output"
    )
    assert out["item"]["call_id"] == "call_1"
    assert json.loads(out["item"]["output"]) == {"job_id": "job1", "status": "searching"}
    # the follow-up response.create only after the tool output
    assert types.index("response.create", 4) > xai.sent.index(out)
    # the search used the headset's measurement
    assert fake.started[0].measurement.value_m == 3.66


def test_local_tool_and_text_turn(fake):
    call = {
        "type": "response.function_call_arguments.done",
        "name": "hanger_plan",
        "call_id": "c9",
        "arguments": json.dumps({"run_length_m": 3.66, "spacing_mm": 600}),
    }
    xai = fake(_response(call), ANSWER_TURN)
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        ws.receive_json()
        ws.send_json({"type": "text", "text": "how many hangers?"})
        assert ws.receive_json() == {
            "type": "transcript",
            "role": "user",
            "text": "how many hangers?",
        }
        _receive_until(ws, "response_done")
        _receive_until(ws, "response_done")
    out = next(
        m
        for m in xai.sent
        if isinstance(m, dict) and m.get("item", {}).get("type") == "function_call_output"
    )
    assert json.loads(out["item"]["output"])["count"] == 7


def test_hanger_plan_uses_the_headset_measurement(fake):
    call = {
        "type": "response.function_call_arguments.done",
        "name": "hanger_plan",
        "call_id": "c1",
        "arguments": json.dumps({"spacing_mm": 600}),
    }
    xai = fake(_response(call), ANSWER_TURN, _response(call), ANSWER_TURN)
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        ws.receive_json()
        ws.send_json({"type": "text", "text": "how many hangers?"})
        _receive_until(ws, "response_done")
        _receive_until(ws, "response_done")
        ctx = {"measurement": {"label": "run", "value_m": 3.66, "axis": "length"}}
        ws.send_json({"type": "context", "context": ctx})
        ws.send_json({"type": "text", "text": "and now?"})
        _receive_until(ws, "response_done")
        _receive_until(ws, "response_done")
    outs = [
        json.loads(m["item"]["output"])
        for m in xai.sent
        if isinstance(m, dict) and m.get("item", {}).get("type") == "function_call_output"
    ]
    assert "no tape measurement" in outs[0]["error"]
    assert outs[1]["count"] == 7


def test_cancelled_response_gets_no_follow_up(fake):
    xai = fake(TOOL_TURN[:-1] + [{"type": "response.done", "response": {"status": "cancelled"}}])
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        ws.receive_json()
        ws.send_json({"type": "commit"})
        _receive_until(ws, "response_done")
        ws.send_json({"type": "cancel"})
        ws.send_json({"type": "context", "context": {}})  # round-trip marker
    assert xai.types().count("response.create") == 1


def test_spoken_search_skips_follow_up_and_speaks_job_summary(fake, monkeypatch):
    xai = fake(TOOL_TURN, ANSWER_TURN)
    job = Job(id="job1", status="done", query="gutter hanger", candidates=[])
    monkeypatch.setattr(jobs, "wait", lambda job_id, t: asyncio.sleep(0.2, result=job))
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        ws.receive_json()
        ws.send_json({"type": "commit"})
        got = _receive_until(ws, "job_done")
        assert got[-1] == {"type": "job_done", "job_id": "job1", "status": "done"}
        assert AUDIO in got
        assert {
            "type": "transcript",
            "role": "assistant",
            "text": "Searching the supply shops.",
            "final": True,
        } in got
    # "Searching..." was already spoken and find_part only returns a job id: no filler reply
    assert xai.types().count("response.create") == 1
    forced = [
        m
        for m in xai.sent
        if isinstance(m, dict) and m.get("item", {}).get("type") == "force_message"
    ]
    assert forced[0]["item"]["content"][0]["text"] == jobs.summary(job)


def test_unknown_and_bad_messages_are_non_fatal(fake):
    fake()
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        ws.receive_json()
        ws.send_text("not json")
        assert ws.receive_json()["fatal"] is False
        ws.send_json({"type": "nope"})
        assert "unknown type" in ws.receive_json()["message"]


def test_offline_sends_fatal_error(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        msg = ws.receive_json()
    assert msg["type"] == "error" and msg["fatal"] is True and "offline" in msg["message"]


def test_connect_failure_sends_fatal_error(monkeypatch):
    def boom():
        raise OSError("unreachable")

    monkeypatch.setattr(realtime, "_connect", boom)
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        msg = ws.receive_json()
    assert msg == {"type": "error", "message": "voice service unavailable: OSError", "fatal": True}


def test_upstream_close_tells_client(fake):
    xai = fake()
    with TestClient(app) as client, client.websocket_connect("/voice/realtime") as ws:
        ws.receive_json()
        xai.inbox.put_nowait(None)  # xAI hangs up
        assert ws.receive_json() == {
            "type": "error",
            "message": "voice service closed",
            "fatal": True,
        }


# --- pure pieces ------------------------------------------------------------------------


def test_realtime_tools_are_flat():
    tools = realtime.realtime_tools()
    assert len(tools) == len(agent.TOOLS) + 2
    for tool in tools:
        assert tool["type"] == "function" and "function" not in tool
        assert tool["name"] and tool["parameters"]["type"] == "object"
    find = next(t for t in tools if t["name"] == "find_part")
    assert find["parameters"] == agent.TOOLS[0]["function"]["parameters"]


def test_hanger_plan():
    plan = realtime.hanger_plan(3.66, 600)
    assert plan["count"] == 7 and plan["spacing_mm"] == 560
    assert plan["positions_mm"][0] == 150 and plan["positions_mm"][-1] == 3510
    assert realtime.hanger_plan(0.4, 600)["count"] == 2  # short run: still two, inset capped
    assert realtime.hanger_plan(0.4, 600)["positions_mm"] == [100, 300]
    with pytest.raises(ValueError):
        realtime.hanger_plan(0, 600)
    with pytest.raises(ValueError):
        realtime.hanger_plan(500, 600)


def test_btu_for_room():
    small = realtime.btu_for_room(3, 4)  # 12 m² = 129 ft² -> 2,583 BTU
    assert small["btu"] == 2600 and small["unit_btu"] == 5000
    big = realtime.btu_for_room(5, 6, h_m=3.0, sunny=True, occupants=4, kitchen=True)
    # 322.9 ft² * 20 * 1.23 * 1.1 + 1200 + 4000
    assert big["btu"] == pytest.approx(13900, abs=100) and big["unit_btu"] == 14000
    assert realtime.btu_for_room(20, 20)["unit_btu"] is None
    with pytest.raises(ValueError):
        realtime.btu_for_room(0, 3)


def test_test_page_is_served():
    with TestClient(app) as client:
        resp = client.get("/realtime")
    assert resp.status_code == 200 and "/voice/realtime" in resp.text


def test_voice_set_finish_runs_server_side_and_multi_action_tools_send_each(fake, monkeypatch):
    """F11 moved set_finish server-side: the voice relay (agent._run_tool directly) must render
    it too. F10's survey returns a list of actions: each one goes to the headset."""
    applied = []

    async def fake_apply(part, name):
        applied.append((part.id, name))
        return {
            "source": "imagine",
            "model_url": "/parts/p1/model-navy.glb",
            "label": None,
            "spoken": "Here it is in navy.",
        }

    async def fake_survey(session, args, context):
        actions = [
            {"name": "survey_started", "args": {"survey_id": "abc"}},
            {"name": "show_survey", "args": {"survey_id": "abc", "pins": [], "label": "x"}},
        ]
        return {"spoken": "2 problems."}, actions, None

    monkeypatch.setattr(agent.jobs, "load_part", lambda pid: SimpleNamespace(id=pid))
    monkeypatch.setattr(agent.finish, "apply", fake_apply)
    monkeypatch.setattr(agent, "_survey", fake_survey)
    calls = [
        {
            "type": "response.function_call_arguments.done",
            "name": name,
            "call_id": f"c{i}",
            "arguments": json.dumps(args),
        }
        for i, (name, args) in enumerate(
            [("set_finish", {"name": "navy"}), ("survey_condition", {"focus": "all"})]
        )
    ]
    xai = fake(_response(*calls), ANSWER_TURN)
    with TestClient(app) as client, client.websocket_connect("/voice/realtime?session_id=f") as ws:
        ws.receive_json()
        ws.send_json({"type": "context", "context": {"selected_part_id": "p1", "site": "s"}})
        ws.send_json({"type": "text", "text": "show it in navy and survey it"})
        got = _receive_until(ws, "response_done") + _receive_until(ws, "response_done")
    actions = {
        m["action"]["name"]: m["action"]
        for m in got
        if isinstance(m, dict) and m["type"] == "action"
    }
    assert set(actions) == {"set_finish", "survey_started", "show_survey"}
    assert actions["set_finish"]["args"] == {
        "name": "navy",
        "model_url": "/parts/p1/model-navy.glb",
    }
    assert applied == [("p1", "navy")]
    outputs = [
        json.loads(m["item"]["output"])
        for m in xai.sent
        if isinstance(m, dict) and m.get("item", {}).get("type") == "function_call_output"
    ]
    assert {"ok": True, "source": "imagine", "spoken": "Here it is in navy."} in outputs
