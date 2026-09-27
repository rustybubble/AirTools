"""F18 voice-refined reimagine: the refine stack (push/undo/start over), the two-image prompt,
drift and its retry, cache/OFFLINE replay, fast-path routing and the walk-in hand-off."""

import base64
import io
import json

import httpx
import pytest
import respx
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, flythrough, imagine, jobs
from server.app import app
from server.config import get_settings
from tests.test_agent import _no_llm_chat


def _jpg(left: int, right: int = 100, size: tuple[int, int] = (64, 36)) -> bytes:
    """Grey frame: the left third is `left` (the "cabinets" an edit changes), the rest `right`."""
    img = Image.new("L", size, right)
    img.paste(left, (0, 0, size[0] // 3, size[1]))
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "JPEG", quality=95)
    return buf.getvalue()


FRAME = _jpg(100)
NAVY = _jpg(20)  # first edit: only the left third changed
DARKER = _jpg(5)  # a clean refinement
RELIT = _jpg(5, right=160)  # a refinement that relit the whole room (drift 60)
CAM = {"id": "0301", "thumb": "thumbs/0301.jpg", "R": [[1, 0, 0], [0, 1, 0], [0, 0, 1]]}
CAM.update({"t": [0, 0, 0], "position": [0, 0, 0], "fx": 500.0, "fy": 500.0})
CTX = {"site": "kitchen", "frame_id": "0301"}


def _ok(jpg: bytes, ticks: int = 800_000_000) -> httpx.Response:
    data = [{"b64_json": base64.b64encode(jpg).decode()}]
    return httpx.Response(200, json={"data": data, "usage": {"cost_in_usd_ticks": ticks}})


@pytest.fixture(autouse=True)
def dirs(tmp_path, monkeypatch):
    site = tmp_path / "scene" / "kitchen"
    (site / "thumbs").mkdir(parents=True)
    (site / "thumbs" / "0301.jpg").write_bytes(FRAME)
    (site / "cameras.r1.json").write_text(json.dumps([CAM]))
    (site / "scene.json").write_text(json.dumps({"cameras": "cameras.r1.json", "revision": 1}))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()
    agent._sessions.clear()


def _bodies(route) -> list[dict]:
    return [json.loads(c.request.content) for c in route.calls]


def test_drift_ignores_the_edited_region_and_catches_a_relit_room():
    assert imagine.drift(FRAME, NAVY, NAVY) < 2
    assert imagine.drift(FRAME, NAVY, DARKER) < 2  # darker cabinets: inside the region
    assert imagine.drift(FRAME, NAVY, RELIT) > imagine.DRIFT_MAX


@pytest.mark.asyncio
async def test_refine_sends_latest_edit_plus_original_and_measures_drift():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(side_effect=[_ok(NAVY), _ok(DARKER)])
        first = await imagine.refine("kitchen", "0301", ["navy cabinets"], [])
        meta = await imagine.refine(
            "kitchen", "0301", ["navy cabinets", "darker blue."], [first["id"]]
        )
    base, step = _bodies(route)
    assert first["drift"] is None and "image" in base  # a first step is a plain reimagine
    assert base["prompt"] == "navy cabinets" + imagine.KEEP_CLAUSE
    current, original = (ref["url"].split(",")[1] for ref in step["images"])
    assert base64.b64decode(current) == NAVY and base64.b64decode(original) == FRAME
    assert "Edit image 1: darker blue." in step["prompt"]
    assert "identical to image 2" in step["prompt"]
    assert meta["drift"] < 2 and meta["retried"] is False and meta["cost_usd"] == 0.08


@pytest.mark.asyncio
async def test_drift_past_threshold_retries_once_from_the_original_merged():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(
            side_effect=[_ok(NAVY), _ok(RELIT), _ok(DARKER, 700_000_000)]
        )
        first = await imagine.refine("kitchen", "0301", ["navy cabinets"], [])
        meta = await imagine.refine(
            "kitchen", "0301", ["navy cabinets", "no, lighter"], [first["id"]]
        )
    retry = _bodies(route)[2]
    assert base64.b64decode(retry["image"]["url"].split(",")[1]) == FRAME
    assert retry["prompt"] == "navy cabinets; then no, lighter." + imagine.KEEP_CLAUSE
    assert meta["retried"] is True and meta["drift"] < 2
    assert meta["id"] != first["id"] and meta["cost_usd"] == 0.15  # both calls


async def _chain(monkeypatch) -> list[dict]:
    """reimagine (via the route with a session_id), then 'darker blue' and 'add brass pulls'
    through the fast path. Returns each show_reimagined's args."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    with respx.mock(assert_all_called=True) as router, TestClient(app) as client:
        router.post(imagine.EDITS_URL).mock(side_effect=[_ok(NAVY), _ok(DARKER), _ok(_jpg(8))])
        payload = {**CTX, "prompt": "navy cabinets", "session_id": "s"}
        assert client.post("/scene/reimagine", json=payload).status_code == 200
        shown = []
        for text in ("darker blue", "add brass pulls"):
            out = await agent.handle_command("s", text, CTX)
            assert out["reply"] == agent._CANNED_REPLY["refine_reimagine"]
            [action] = out["actions"]
            assert action["name"] == "show_reimagined"
            shown.append(action["args"])
    return shown


@pytest.mark.asyncio
async def test_stack_push_undo_start_over(monkeypatch):
    shown = await _chain(monkeypatch)
    assert [a["step"] for a in shown] == [2, 3] and all(a["can_undo"] for a in shown)
    assert shown[0]["drift"] is not None and shown[0]["label"] == imagine.LABEL
    steps = agent._sessions["s"].reimagine["steps"]
    assert [s["prompt"] for s in steps] == ["navy cabinets", "darker blue", "add brass pulls"]

    with respx.mock(assert_all_mocked=True):  # no edit calls from here on
        undo = await agent.handle_command("s", "undo", CTX)
        assert undo["reply"] == "Stepped back."
        assert undo["actions"][0]["args"]["step"] == 2
        assert undo["actions"][0]["args"]["image_url"] == shown[0]["image_url"]

        reset = await agent.handle_command("s", "start over", CTX)
        args = reset["actions"][0]["args"]
        assert args["step"] == 0 and args["can_undo"] is False
        assert args["image_url"] == "/scenes/kitchen/thumbs/0301.jpg"  # the original frame

        empty = await agent.handle_command("s", "go back", CTX)
        assert empty["actions"][0]["args"]["step"] == 0


@pytest.mark.asyncio
async def test_chain_replays_from_cache_then_offline(monkeypatch):
    first = await _chain(monkeypatch)
    agent._sessions.clear()
    again = await _chain_cached(monkeypatch)
    assert [a["image_url"] for a in again] == [a["image_url"] for a in first]

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    agent._sessions.clear()
    offline = await _chain_cached(monkeypatch)
    assert [a["image_url"] for a in offline] == [a["image_url"] for a in first]
    miss = await agent.handle_command("s", "paint the walls green", CTX)
    assert miss["reply"] == "Can't do that yet: no sketch available right now."
    assert agent._sessions["s"].reimagine["steps"][-1]["prompt"] == "add brass pulls"


async def _chain_cached(monkeypatch) -> list[dict]:
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    with respx.mock(assert_all_mocked=True), TestClient(app) as client:  # any call would fail
        payload = {**CTX, "prompt": "navy cabinets", "session_id": "s"}
        assert client.post("/scene/reimagine", json=payload).status_code == 200
        return [
            (await agent.handle_command("s", t, CTX))["actions"][0]["args"]
            for t in ("darker blue", "add brass pulls")
        ]


@pytest.mark.parametrize(
    ("text", "tool"),
    [
        ("darker blue", "refine_reimagine"),
        ("no, lighter", "refine_reimagine"),
        ("add brass pulls", "refine_reimagine"),
        ("make it a bit warmer", "refine_reimagine"),
        ("undo", "undo_reimagine"),
        ("undo that", "undo_reimagine"),
        ("let's start over", "start_over_reimagine"),
        ("back to the original", "start_over_reimagine"),
        # the older fast paths still win while a reimagine is up
        ("show it in darker blue", "set_finish"),
        ("is it recalled?", "check_safety"),
        ("send me the report", "make_report"),
    ],
)
@pytest.mark.asyncio
async def test_routing_with_a_reimagine_on_screen(monkeypatch, text, tool):
    seen = []

    async def fake_run_tool(session, name, args, context):
        seen.append(name)
        return {"ok": True}, None, None

    monkeypatch.setattr(agent, "_run_tool", fake_run_tool)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    out = {"frame_id": "0301", "camera": {}, "before_url": "/b.jpg", "id": "a" * 40}
    agent.remember_reimagine("s", "kitchen", "navy cabinets", out)
    await agent.handle_command("s", text, {})
    assert seen == [tool]


@pytest.mark.parametrize(
    "text", ["darker blue", "undo", "start over", "add a note: the sink drips", "go back to it"]
)
@pytest.mark.asyncio
async def test_refine_phrases_go_to_the_llm_otherwise(monkeypatch, text):
    """No reimagine on screen (or not a refine phrase): the LLM decides, refine_reimagine hidden."""
    from tests.test_agent import _text_completion

    seen = []

    async def fake(role, messages, tools=None, **kw):
        seen.append({t["function"]["name"] for t in tools})
        return _text_completion("Aye.")

    monkeypatch.setattr(agent, "chat", fake)
    if text.startswith(("add a note", "go back to")):  # a stack exists, but not a refine phrase
        out = {"frame_id": "0301", "camera": {}, "before_url": "/b.jpg", "id": "a" * 40}
        agent.remember_reimagine("s", "kitchen", "navy cabinets", out)
    await agent.handle_command("s", text, {})
    assert len(seen) == 1
    assert ("refine_reimagine" in seen[0]) == (agent._sessions["s"].reimagine is not None)


@pytest.mark.asyncio
async def test_walk_in_right_after_a_refinement_lands_on_the_refined_frame(monkeypatch):
    shown = await _chain(monkeypatch)
    calls = []

    async def fake_start(site, frame_id, prompt, **kw):
        calls.append((site, frame_id, prompt, kw))
        return {"job_id": "j1", "status": "pending"}

    monkeypatch.setattr(flythrough, "start", fake_start)
    walk = ("walk_in_preview", {"prompt": "navy"})
    from tests.test_agent import _scripted_chat, _tool_call_completion

    monkeypatch.setattr(agent, "chat", _scripted_chat(*[_tool_call_completion(*walk)] * 2))
    await agent.handle_command("s", "walk me into it", {})  # no site/frame in context: offered
    site, frame_id, prompt, kw = calls[-1]
    assert (site, frame_id) == ("kitchen", "0301")
    assert kw["end_image_id"] == shown[-1]["image_url"].split("/")[-1][:-4]
    assert prompt == "navy cabinets; then darker blue; then add brass pulls"

    await agent.handle_command("s", "walk me into it", CTX)  # a clip since: not "right after"
    assert calls[-1][3]["end_image_id"] is None and calls[-1][2] == "navy"


@pytest.mark.asyncio
async def test_flythrough_end_image_skips_the_edit(monkeypatch, dirs):
    edited = imagine.image_path("b" * 40)
    edited.parent.mkdir(parents=True)
    edited.write_bytes(DARKER)

    async def no_edit(*a, **kw):
        raise AssertionError("the refined frame is already made")

    monkeypatch.setattr(imagine, "edit", no_edit)
    monkeypatch.setattr(flythrough, "POLL_S", 0)
    with respx.mock() as router:
        submit = router.post(flythrough.VIDEO_URL).mock(
            return_value=httpx.Response(200, json={"request_id": "r"})
        )
        router.get(flythrough.POLL_URL.format("r")).mock(return_value=httpx.Response(500))
        out = await flythrough.start("kitchen", "0301", "navy", end_image_id="b" * 40)
        await jobs.wait(out["job_id"], 5)  # the render fails; only the request matters here
    body = json.loads(submit.calls.last.request.content)
    assert base64.b64decode(body["last_frame"]["url"].split(",")[1]) == DARKER
    with pytest.raises(ValueError):
        await flythrough.start("kitchen", "0301", "navy", end_image_id="c" * 40)
