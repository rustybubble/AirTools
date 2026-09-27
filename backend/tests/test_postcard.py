""" "See it installed" postcard: request shape, prompt, route, receipt hook, agent tool."""

import base64
import io
import json

import httpx
import pytest
import respx
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, checkout, imagine, jobs, postcard
from server.app import app
from server.config import get_settings
from server.models import Part, Seller

EDITED = b"\xff\xd8\xff\xe0postcard"
PHOTO = b"\xff\xd8\xff\xe0product-photo"
PART = Part(
    id="karran-qu-670-bl-2d781c",
    name="Undermount Quartz Composite 32 in. Single Bowl Kitchen Sink in Black",
    manufacturer="Karran",
    dims_mm={"w": 806.45, "d": 488.95, "h": 228.6},
    sellers=[Seller(name="Home Depot", price_usd=299.0, shipping_usd=0.0)],
)
BOX = [0.25, 0.5, 0.75, 0.75]


def _jpg(w: int = 64, h: int = 36, color=(200, 200, 200)) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (w, h), color).save(buf, "JPEG")
    return buf.getvalue()


def _ok(ticks: int = 800_000_000) -> httpx.Response:
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
    (site / "thumbs" / "0241.jpg").write_bytes(_jpg())
    cams = [{"id": "0241", "thumb": "thumbs/0241.jpg"}]
    (site / "cameras.r1.json").write_text(json.dumps(cams))
    (site / "scene.json").write_text(json.dumps({"cameras": "cameras.r1.json"}))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    jobs.save_part(PART)
    (postcard.assets.part_dir(PART.id) / "image.jpg").write_bytes(PHOTO)
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()
    agent._sessions.clear()


def test_prompt_carries_name_dims_and_placement():
    p = postcard.build_prompt(PART, "replacing the steel sink", None)
    assert "Karran Undermount Quartz Composite" in p
    assert "806 mm wide x 489 mm deep x 229 mm high" in p
    assert "replacing the steel sink" in p and "real dimensions" in p
    assert "magenta rectangle" not in p
    boxed = postcard.build_prompt(PART, "", BOX)
    assert "inside the magenta rectangle" in boxed and "remove the magenta outline" in boxed
    assert boxed.endswith(imagine.KEEP_CLAUSE)


@pytest.mark.asyncio
async def test_make_postcard_orders_site_first_and_marks_the_box():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        out = await postcard.make_postcard(PART, _jpg(), box=BOX)
    images = json.loads(route.calls.last.request.content)["images"]
    assert len(images) == 2
    assert images[1]["url"] == imagine._data_uri(PHOTO)  # product photo second
    site = Image.open(io.BytesIO(base64.b64decode(images[0]["url"].split(",", 1)[1])))
    r, g, b = site.convert("RGB").getpixel((32, 18))  # on the box's top edge (y = 0.5 * 36)
    assert r > 200 and g < 80 and b > 200  # magenta
    assert postcard.postcard_path(PART.id).read_bytes() == EDITED
    assert out["image_url"].startswith(f"/parts/{PART.id}/postcard.jpg?v=")
    assert out["cost_usd"] == 0.08 and out["label"] == imagine.LABEL


@pytest.mark.parametrize(
    "box", [[0.5, 0.5, 0.4, 0.9], [0, 0, 1.2, 1], [0.1, 0.2, 0.3], "abcd", [None, 0, 1, 1]]
)
@pytest.mark.asyncio
async def test_make_postcard_rejects_bad_box(box):
    with pytest.raises(ValueError, match="box"):
        await postcard.make_postcard(PART, _jpg(), box=box)


def test_route_scene_thumb_serves_image_and_receipt_carries_it():
    with respx.mock(assert_all_called=True) as router, TestClient(app) as client:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        payload = {"site": "kitchen", "frame_id": "0241", "placement": "in the counter"}
        resp = client.post(f"/parts/{PART.id}/postcard", json=payload)
        assert resp.status_code == 200, resp.text
        out = resp.json()
        assert out["before_url"] == "/scenes/kitchen/thumbs/0241.jpg" and not out["cached"]
        assert "in the counter" in json.loads(route.calls.last.request.content)["prompt"]
        img = client.get(out["image_url"])
        assert img.status_code == 200 and img.content == EDITED

        receipt = client.post("/checkout", json={"part_id": PART.id, "seller_idx": 0, "qty": 1})
        assert receipt.json()["postcard_url"] == out["image_url"]


def test_route_accepts_headset_frame_upload():
    with respx.mock(assert_all_called=True) as router, TestClient(app) as client:
        router.post(imagine.EDITS_URL).mock(return_value=_ok())
        b64 = base64.b64encode(_jpg()).decode()
        resp = client.post(f"/parts/{PART.id}/postcard", json={"frame_jpg_b64": b64, "box": BOX})
        assert resp.status_code == 200, resp.text
        assert resp.json()["before_url"] is None


@pytest.mark.parametrize(
    ("part_id", "payload", "status"),
    [
        (PART.id, {}, 400),  # no site view at all
        (PART.id, {"site": "kitchen", "frame_id": "9999"}, 400),
        (PART.id, {"site": "../etc", "frame_id": "0241"}, 400),
        (PART.id, {"frame_jpg_b64": "not base64!"}, 400),
        (PART.id, {"frame_jpg_b64": base64.b64encode(b"not a jpeg").decode()}, 400),
        (PART.id, {"site": "kitchen", "frame_id": "0241", "box": [1, 1, 0, 0]}, 400),
        ("nope", {"site": "kitchen", "frame_id": "0241"}, 404),
        ("../x", {"site": "kitchen", "frame_id": "0241"}, 404),  # never reaches the route
    ],
)
def test_route_rejects_bad_input_without_calling_xai(part_id, payload, status):
    with respx.mock(assert_all_called=False) as router, TestClient(app) as client:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        assert client.post(f"/parts/{part_id}/postcard", json=payload).status_code == status
        assert route.call_count == 0


def test_route_422_on_moderation_and_503_offline(monkeypatch):
    payload = {"site": "kitchen", "frame_id": "0241"}
    with respx.mock() as router, TestClient(app) as client:
        router.post(imagine.EDITS_URL).mock(
            return_value=httpx.Response(400, json={"error": "blocked by moderation"})
        )
        assert client.post(f"/parts/{PART.id}/postcard", json=payload).status_code == 422
        monkeypatch.setenv("OFFLINE", "true")
        get_settings.cache_clear()
        assert client.post(f"/parts/{PART.id}/postcard", json=payload).status_code == 503


@pytest.mark.asyncio
async def test_receipt_has_no_postcard_url_without_one():
    receipt = await checkout.authorize(PART, 0, 1)
    assert "postcard_url" not in receipt


@pytest.mark.asyncio
async def test_agent_fast_path_returns_show_postcard(monkeypatch):
    from tests.test_agent import _no_llm_chat

    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    ctx = {"selected_part_id": PART.id, "site": "kitchen", "frame_id": "0241", "placed_box": BOX}
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        result = await agent.handle_command("p1", "show me what it'll look like", ctx)
    assert "magenta rectangle" in json.loads(route.calls.last.request.content)["prompt"]
    assert result["reply"] == agent._CANNED_REPLY["see_it_installed"]
    [action] = result["actions"]
    assert action["name"] == "show_postcard"
    assert set(action["args"]) == {"part_id", "image_url", "before_url", "label"}
    assert action["args"]["before_url"] == "/scenes/kitchen/thumbs/0241.jpg"


@pytest.mark.asyncio
async def test_agent_llm_tool_uses_headset_frame_and_placement(monkeypatch):
    from tests.test_agent import _scripted_chat, _tool_call_completion

    call = _tool_call_completion("see_it_installed", {"placement": "left of the downspout"})
    monkeypatch.setattr(agent, "chat", _scripted_chat(call))
    ctx = {"selected_part_id": PART.id, "frame_jpg_b64": base64.b64encode(_jpg()).decode()}
    with respx.mock(assert_all_called=True) as router:
        route = router.post(imagine.EDITS_URL).mock(return_value=_ok())
        result = await agent.handle_command("p2", "how would that sit on my fascia?", ctx)
    assert "left of the downspout" in json.loads(route.calls.last.request.content)["prompt"]
    assert result["actions"][0]["name"] == "show_postcard"


@pytest.mark.asyncio
async def test_agent_errors_without_selection_or_view(monkeypatch):
    from tests.test_agent import _no_llm_chat

    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    no_part = await agent.handle_command("p3", "see it installed", {"site": "kitchen"})
    assert no_part["reply"] == "Can't do that yet: no part selected." and not no_part["actions"]
    no_view = await agent.handle_command("p4", "see it installed", {"selected_part_id": PART.id})
    assert no_view["reply"] == "Can't do that yet: no camera view to draw on."


@pytest.mark.asyncio
async def test_agent_hides_tool_without_a_view(monkeypatch):
    from tests.test_agent import _text_completion

    seen = []

    async def fake(role, messages, tools=None, **kw):
        seen.append({t["function"]["name"] for t in tools})
        return _text_completion("Aye.")

    monkeypatch.setattr(agent, "chat", fake)
    await agent.handle_command("p5", "picture it on the wall", {})
    await agent.handle_command("p6", "picture it on the wall", {"site": "k", "frame_id": "1"})
    assert "see_it_installed" not in seen[0] and "see_it_installed" in seen[1]
