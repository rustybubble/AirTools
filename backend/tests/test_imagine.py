"""Grok Imagine "reimagine this view": xAI request shape, cost, cache/offline, routes, agent tool."""

import base64
import json

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from server import agent, imagine
from server.app import app
from server.config import get_settings

FRAME = b"\xff\xd8\xff\xe0frame-0241"
EDITED = b"\xff\xd8\xff\xe0edited"
CAM = {
    "id": "0241",
    "file": "frames/0241.jpg",  # not shipped in the package: falls back to the thumb
    "thumb": "thumbs/0241.jpg",
    "R": [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
    "t": [0.1, 0.2, 0.3],
    "position": [-0.1, -0.2, -0.3],
    "fx": 1457.6,
    "fy": 1457.6,
    "cx": 960.0,
    "cy": 539.5,
    "w": 1920,
    "h": 1079,
}


def _ok(ticks: int = 700_000_000) -> httpx.Response:
    return httpx.Response(
        200,
        json={
            "data": [{"b64_json": base64.b64encode(EDITED).decode(), "mime_type": "image/jpeg"}],
            "usage": {"cost_in_usd_ticks": ticks},
        },
    )


@pytest.fixture(autouse=True)
def dirs(tmp_path, monkeypatch):
    site = tmp_path / "scene" / "kitchen"
    (site / "thumbs").mkdir(parents=True)
    (site / "thumbs" / "0241.jpg").write_bytes(FRAME)
    (site / "cameras.r1.json").write_text(json.dumps([CAM]))
    (site / "scene.json").write_text(json.dumps({"cameras": "cameras.r1.json", "revision": 1}))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()
    agent._sessions.clear()


@pytest.mark.asyncio
async def test_edit_request_shape_and_cost():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        jpg, meta = await imagine.edit([FRAME], "navy cabinets")
    req = route.calls.last.request
    body = json.loads(req.content)
    assert req.headers["Authorization"] == "Bearer test-dummy-key"
    assert body["model"] == "grok-imagine-image-2.0"
    assert body["prompt"] == "navy cabinets"
    assert body["resolution"] == "1k"
    assert body["image"]["url"] == "data:image/jpeg;base64," + base64.b64encode(FRAME).decode()
    assert "images" not in body
    assert jpg == EDITED
    assert meta["cost_usd"] == 0.07 and meta["cached"] is False


@pytest.mark.asyncio
async def test_edit_multi_image_uses_images_list():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        await imagine.edit([FRAME, b"second"], "put image 2 in image 1")
    body = json.loads(route.calls.last.request.content)
    assert len(body["images"]) == 2 and "image" not in body


@pytest.mark.asyncio
async def test_edit_cached_then_offline_replay(monkeypatch):
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        await imagine.edit([FRAME], "navy cabinets")
        jpg, meta = await imagine.edit([FRAME], "navy cabinets")
    assert route.call_count == 1
    assert jpg == EDITED and meta["cached"] is True and meta["cost_usd"] == 0.0

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    with respx.mock(assert_all_called=False) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        jpg, _ = await imagine.edit([FRAME], "navy cabinets")
        assert jpg == EDITED and route.call_count == 0
        with pytest.raises(imagine.cache.OfflineMiss):
            await imagine.edit([FRAME], "a prompt nobody rehearsed")


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("resp", "exc"),
    [
        (
            httpx.Response(400, json={"error": "Generated image rejected by content moderation."}),
            imagine.ModerationBlocked,
        ),
        (
            httpx.Response(200, json={"data": [{"respect_moderation": False}]}),
            imagine.ModerationBlocked,
        ),
        (httpx.Response(429, json={"error": "slow down"}), imagine.RateLimited),
        (httpx.Response(500, text="boom"), imagine.ImagineError),
    ],
)
async def test_edit_error_mapping(resp, exc):
    with respx.mock() as router:
        router.post(imagine.EDITS_URL).mock(return_value=resp)
        with pytest.raises(exc):
            await imagine.edit([FRAME], "navy cabinets")


def test_route_returns_camera_label_and_serves_image():
    with respx.mock(assert_all_called=True) as router, TestClient(app) as client:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        resp = client.post(
            "/scene/reimagine", json={"site": "kitchen", "frame_id": "0241", "prompt": "navy"}
        )
        assert resp.status_code == 200, resp.text
        out = resp.json()
        assert out["label"] == "AI preview, not to scale"
        assert out["frame_id"] == "0241"
        assert out["before_url"] == "/scenes/kitchen/thumbs/0241.jpg"
        assert out["camera"]["fx"] == 1457.6 and out["camera"]["R"] == CAM["R"]
        assert "file" not in out["camera"]
        assert out["cost_usd"] == 0.07
        body = json.loads(route.calls.last.request.content)
        assert body["prompt"].startswith("navy") and imagine.KEEP_CLAUSE.strip() in body["prompt"]
        img = client.get(out["image_url"])
        assert img.status_code == 200 and img.content == EDITED
        assert client.get("/scene/reimagine/" + "0" * 40 + ".jpg").status_code == 404
        assert client.get("/scene/reimagine/..%2Fx.jpg").status_code == 404


@pytest.mark.parametrize(
    "payload",
    [
        {"site": "kitchen", "frame_id": "9999", "prompt": "navy"},
        {"site": "nope", "frame_id": "0241", "prompt": "navy"},
        {"site": "../etc", "frame_id": "0241", "prompt": "navy"},
        {"site": "kitchen", "frame_id": "0241", "prompt": "   "},
    ],
)
def test_route_400_on_bad_input(payload):
    with respx.mock(assert_all_called=False) as router, TestClient(app) as client:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        assert client.post("/scene/reimagine", json=payload).status_code == 400
        assert route.call_count == 0


def test_route_422_on_moderation_and_503_offline(monkeypatch):
    blocked = httpx.Response(400, json={"error": "blocked by moderation"})
    with respx.mock() as router, TestClient(app) as client:
        router.post(imagine.EDITS_URL).mock(return_value=blocked)
        payload = {"site": "kitchen", "frame_id": "0241", "prompt": "navy"}
        assert client.post("/scene/reimagine", json=payload).status_code == 422
        monkeypatch.setenv("OFFLINE", "true")
        get_settings.cache_clear()
        assert client.post("/scene/reimagine", json=payload).status_code == 503


@pytest.mark.asyncio
async def test_agent_reimagine_view_returns_show_reimagined(monkeypatch):
    from tests.test_agent import _scripted_chat, _tool_call_completion

    monkeypatch.setattr(
        agent, "chat", _scripted_chat(_tool_call_completion("reimagine_view", {"prompt": "navy"}))
    )
    ctx = {"site": "kitchen", "frame_id": "0241"}
    with respx.mock(assert_all_called=True) as router:
        router.post(imagine.EDITS_URL).mock(return_value=_ok())
        result = await agent.handle_command("r1", "what would navy cabinets look like?", ctx)
    assert result["reply"] == agent._CANNED_REPLY["reimagine_view"]
    [action] = result["actions"]
    assert action["name"] == "show_reimagined"
    assert set(action["args"]) >= {"image_url", "frame_id", "camera", "label"}
    assert action["args"]["step"] == 1 and action["args"]["can_undo"] is True
    assert action["args"]["frame_id"] == "0241"
    assert action["args"]["label"] == "AI preview, not to scale"


@pytest.mark.asyncio
async def test_agent_hides_reimagine_without_site(monkeypatch):
    from tests.test_agent import _text_completion

    seen = []

    async def fake(role, messages, tools=None, **kw):
        seen.append({t["function"]["name"] for t in tools})
        return _text_completion("Aye.")

    monkeypatch.setattr(agent, "chat", fake)
    await agent.handle_command("r2", "make it navy", {})
    await agent.handle_command("r3", "make it navy", {"site": "kitchen", "frames": [{"id": "1"}]})
    assert "reimagine_view" not in seen[0]
    assert "reimagine_view" in seen[1]
