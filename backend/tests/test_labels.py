"""Live labels (server/labels.py): stream parsing, rules, cache, rate guard, 3D anchors, agent.

Only xAI is mocked (respx on its chat completions URL). The stream fixture is a real
`grok-4.20-0309-non-reasoning` reply for kitchen frame 0221 (focus "electrical outlets and
switches"), 2026-09-26.
"""

import asyncio
import base64
import io
import json
from pathlib import Path

import httpx
import numpy as np
import pytest
import respx
import trimesh
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, cache, labels
from server.app import app
from server.config import get_settings

XAI_URL = "https://api.x.ai/v1/chat/completions"
REAL = (Path(__file__).parent / "fixtures" / "labels" / "grok_0221_electrical.ndjson").read_text()


def _jpg(shade: int = 128) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (64, 36), (shade, shade, shade)).save(buf, "JPEG")
    return buf.getvalue()


def _sse(text: str, ticks: int = 12_500_000, chunk: int = 7) -> httpx.Response:
    """`text` as an OpenAI-style SSE stream in `chunk`-byte deltas, then a usage chunk."""
    head = {"id": "x", "object": "chat.completion.chunk", "created": 0, "model": "grok"}
    events = [
        {**head, "choices": [{"index": 0, "delta": {"content": text[i : i + chunk]}}]}
        for i in range(0, len(text), chunk)
    ]
    usage = {"prompt_tokens": 700, "completion_tokens": 240, "total_tokens": 940}
    events.append({**head, "choices": [], "usage": {**usage, "cost_in_usd_ticks": ticks}})
    body = "".join(f"data: {json.dumps(e)}\n\n" for e in events) + "data: [DONE]\n\n"
    return httpx.Response(200, headers={"content-type": "text/event-stream"}, content=body)


@pytest.fixture(autouse=True)
def _dirs(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    labels._calls.clear()
    agent._sessions.clear()
    yield
    labels._calls.clear()
    agent._sessions.clear()


async def _chunks(text: str, size: int):
    for i in range(0, len(text), size):
        yield text[i : i + size]


# --- stream parsing ----------------------------------------------------------------------


@pytest.mark.asyncio
async def test_real_stream_in_7_byte_chunks_yields_six_labels_in_order():
    got = [obj async for obj in labels.iter_labels(_chunks(REAL, 7))]
    assert [o["name"] for o in got] == [
        "electrical outlet",
        "light switch",
        "stainless steel sink",
        "chrome faucet",
        "warning sign",
        "wood cabinet",
    ]


@pytest.mark.asyncio
async def test_truncated_stream_yields_the_complete_lines_only():
    got = [obj async for obj in labels.iter_labels(_chunks(REAL.rstrip()[:-20], 7))]
    assert len(got) == 5


@pytest.mark.asyncio
async def test_fences_and_prose_are_skipped():
    text = "```json\n" + REAL + "```\nThat's all I can see."
    assert len([o async for o in labels.iter_labels(_chunks(text, 11))]) == 6


@pytest.mark.asyncio
@respx.mock
async def test_label_frame_streams_times_and_costs_the_call():
    route = respx.post(XAI_URL).mock(return_value=_sse(REAL))
    result = await labels.label_frame(_jpg())
    sent = json.loads(route.calls[0].request.content)
    assert sent["stream"] is True and sent["model"] == "grok-4.20-0309-non-reasoning"
    assert "15 A" not in json.dumps(sent)  # no spec examples in the prompt (G6 #1)
    assert [label["kind"] for label in result["labels"]] == [
        "outlet",
        "switch",
        "sink",
        "faucet",
        "other",
        "cabinet",
    ]
    assert result["labels"][0]["box"] == [0.125, 0.29, 0.182, 0.385]
    assert result["cost_usd"] == 0.00125
    assert result["first_label_ms"] is not None
    assert result["first_label_ms"] <= result["latency_ms"]
    assert not result["cached"]
    assert result["spoken"].startswith("I see the electrical outlet, the light switch and")


# --- rules -------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "box, want",
    [
        ([12.5, 29.0, 18.2, 38.5], [0.125, 0.29, 0.182, 0.385]),  # percent, what 4.20 sends
        ([125, 290, 182, 385], [0.125, 0.29, 0.182, 0.385]),  # 0-1000
        ([0.125, 0.29, 0.182, 0.385], [0.125, 0.29, 0.182, 0.385]),  # unit
        ([18.2, 38.5, 12.5, 29.0], [0.125, 0.29, 0.182, 0.385]),  # reversed corners
        ([-3, 10, 50, 60], [0.0, 0.1, 0.5, 0.6]),  # clamped
        ([10, 10, 10, 60], None),  # zero width
        ([10, 10, 60], None),  # not a box
    ],
)
def test_unit_box_scales(box, want):
    assert labels.unit_box(box) == want


def _line(**kw) -> dict:
    return {"name": "outlet", "detail": "", "source": "seen", "box": [10, 10, 20, 20]} | {
        "confidence": 0.9,
        **kw,
    }


def test_sizes_not_read_from_printed_text_are_replaced():
    label = labels.normalise(_line(detail="duplex 15 A receptacle"))
    assert label["detail"] == "duplex receptacle, size not visible"
    assert label["query"] == "duplex receptacle outlet"
    for detail in ['36" wide stainless', "30-inch white", "5,000 BTU window unit", "120V"]:
        assert "size not visible" in labels.normalise(_line(detail=detail))["detail"], detail


def test_sizes_read_from_printed_text_are_kept():
    label = labels.normalise(_line(detail="duplex 15 A receptacle", source="printed_text"))
    assert label["detail"] == "duplex 15 A receptacle"
    assert label["source"] == "printed_text"


def test_detail_without_sizes_is_untouched():
    assert labels.normalise(_line(detail="white duplex"))["detail"] == "white duplex"


def test_low_confidence_surfaces_and_bad_lines_are_dropped():
    assert labels.normalise(_line(confidence=0.49)) is None
    assert labels.normalise(_line(confidence=0.5)) is not None
    assert labels.normalise(_line(name="Wall")) is None
    assert labels.normalise(_line(name="ceiling")) is None
    assert labels.normalise(_line(box="left")) is None
    assert labels.normalise(_line(name="")) is None


@pytest.mark.parametrize(
    "name, kind",
    [
        ("Electrical outlets", "outlet"),
        ("GFCI receptacle", "outlet"),
        ("Electric Range", "range"),
        ("stove", "range"),
        ("light switch", "switch"),
        ("microwave oven", "microwave"),
        ("range hood", "range hood"),
        ("pull-down faucet", "faucet"),
        ("upper cabinets", "cabinet"),
        ("breaker box", "breaker panel"),
        ("HVAC return vent", "vent"),
        ("P-trap", "pipe"),
        ("soap bottle", "other"),
        ("tape measure", "other"),  # "tap" is a faucet, "tape" isn't
    ],
)
def test_names_map_to_the_vocabulary(name, kind):
    assert labels.kind_of(name) == kind
    assert kind in labels.KINDS or kind == "other"


# --- cache, OFFLINE, rate guard ------------------------------------------------------------


@pytest.mark.asyncio
@respx.mock
async def test_same_frame_is_billed_once():
    route = respx.post(XAI_URL).mock(return_value=_sse(REAL))
    first = await labels.label_frame(_jpg())
    again = await labels.label_frame(_jpg())
    assert route.call_count == 1
    assert again["cached"] and again["cost_usd"] == 0.0
    assert again["labels"] == first["labels"]
    assert again["first_label_ms"] == first["first_label_ms"]  # the measured call's timing


@pytest.mark.asyncio
@respx.mock
async def test_focus_is_part_of_the_cache_key():
    route = respx.post(XAI_URL).mock(return_value=_sse(REAL))
    await labels.label_frame(_jpg())
    await labels.label_frame(_jpg(), "plumbing")
    assert route.call_count == 2
    assert "focusing on plumbing" in route.calls[1].request.content.decode()


@pytest.mark.asyncio
@respx.mock
async def test_offline_replays_a_labelled_frame_and_misses_a_new_one(monkeypatch):
    respx.post(XAI_URL).mock(return_value=_sse(REAL))
    await labels.label_frame(_jpg())
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    assert len((await labels.label_frame(_jpg()))["labels"]) == 6
    with pytest.raises(cache.OfflineMiss):
        await labels.label_frame(_jpg(shade=10))


@pytest.mark.asyncio
async def test_empty_and_oversized_frames_are_rejected():
    with pytest.raises(ValueError, match="empty"):
        await labels.label_frame(b"")
    with pytest.raises(ValueError, match="cap"):
        await labels.label_frame(b"x" * (labels.MAX_FRAME_BYTES + 1))


def test_rate_guard_allows_20_a_minute_per_session():
    for _ in range(labels.PER_MIN):
        labels.take("s1")
    with pytest.raises(labels.RateLimited) as exc:
        labels.take("s1")
    assert 0 < exc.value.retry_after_s <= 60
    labels.take("s2")  # other sessions are unaffected


# --- endpoint ----------------------------------------------------------------------------


@respx.mock
def test_endpoint_upload_then_429_after_20():
    route = respx.post(XAI_URL).mock(return_value=_sse(REAL))
    with TestClient(app) as client:
        for _ in range(labels.PER_MIN):
            resp = client.post(
                "/scene/labels",
                data={"session_id": "q1"},
                files={"image": ("pca-1.jpg", _jpg(), "image/jpeg")},
            )
            assert resp.status_code == 200
        body = resp.json()
        assert body["frame_id"] == "pca-1.jpg" and len(body["labels"]) == 6
        assert body["label"] == labels.LABEL
        resp = client.post(
            "/scene/labels", data={"session_id": "q1"}, files={"image": ("a.jpg", _jpg())}
        )
    assert resp.status_code == 429
    assert resp.json()["detail"]["retry_after_s"] >= 1
    assert route.call_count == 1


def test_endpoint_errors(monkeypatch):
    with TestClient(app) as client:
        assert client.post("/scene/labels", data={}).status_code == 400
        bad = client.post("/scene/labels", files={"image": ("x.jpg", b"not a jpeg")})
        assert bad.status_code == 400
        missing = client.post("/scene/labels", data={"site": "kitchen", "frame_id": "9999"})
        assert missing.status_code == 404
        monkeypatch.setenv("OFFLINE", "true")
        get_settings.cache_clear()
        offline = client.post("/scene/labels", files={"image": ("x.jpg", _jpg())})
        assert offline.status_code == 503


@respx.mock
def test_endpoint_502_when_xai_fails():
    respx.post(XAI_URL).mock(return_value=httpx.Response(500, json={"error": "boom"}))
    with TestClient(app) as client:
        resp = client.post("/scene/labels", files={"image": ("x.jpg", _jpg())})
    assert resp.status_code == 502


# --- scanned scenes: 3D anchors ------------------------------------------------------------

# A wall 2 m in front of two cameras 0.5 m apart, both looking down -Z (OpenCV axes:
# R = diag(1, -1, -1), t = -R @ position). An outlet sits on the wall at OUTLET.
OUTLET = np.array([0.2, 0.1, -1.95])
_R = np.diag([1.0, -1.0, -1.0])


def _cam(cid: str, x: float) -> dict:
    pos = np.array([x, 0.0, 0.0])
    t = -_R @ pos
    return {
        "id": cid,
        "thumb": f"thumbs/{cid}.jpg",
        "R": _R.tolist(),
        "t": t.tolist(),
        "position": pos.tolist(),
        "fx": 100.0,
        "fy": 100.0,
        "cx": 50.0,
        "cy": 50.0,
        "w": 100,
        "h": 100,
    }


def _percent_box(cam: dict, p: np.ndarray, half: float = 2.0) -> list[float]:
    """A 4 % box around world point `p` as the camera sees it."""
    x = _R @ (p - np.asarray(cam["position"]))
    u, v = cam["fx"] * x[0] / x[2] + cam["cx"], cam["fy"] * x[1] / x[2] + cam["cy"]
    return [u - half, v - half, u + half, v + half]


def _write_site(root: Path) -> list[dict]:
    site = root / "den"
    (site / "thumbs").mkdir(parents=True)
    wall = trimesh.creation.box(extents=[6, 6, 0.1])
    wall.apply_translation([0, 0, -2])
    wall.export(site / "collision.r3.glb")
    cams = [_cam("0001", 0.0), _cam("0002", 0.5)]
    (site / "cameras.r3.json").write_text(json.dumps(cams))
    for shade, cam in zip((60, 200), cams, strict=True):
        (site / cam["thumb"]).write_bytes(_jpg(shade))
    meta = {"revision": 3, "cameras": "cameras.r3.json", "collision": {"file": "collision.r3.glb"}}
    (site / "scene.json").write_text(json.dumps(meta))
    return cams


def test_to_pin_lands_on_the_wall():
    cam = _cam("0001", 0.0)
    mesh = trimesh.creation.box(extents=[6, 6, 0.1])
    mesh.apply_translation([0, 0, -2])
    box = [v / 100 for v in _percent_box(cam, OUTLET)]
    assert np.allclose(labels.to_pin(cam, box, mesh), OUTLET, atol=0.01)


def test_merge_uses_a_radius_per_kind():
    def anchor(kind, x, conf, frame):
        return {"name": kind, "kind": kind, "pos": [x, 0, 0], "confidence": conf, "frames": [frame]}

    merged = labels.merge(
        [
            anchor("outlet", 0.0, 0.8, "a"),
            anchor("outlet", 0.5, 0.9, "b"),  # 0.5 m apart: two outlets
            anchor("countertop", 0.0, 0.7, "a"),
            anchor("countertop", 0.9, 0.9, "b"),  # one long counter
        ]
    )
    assert sorted((m["kind"], tuple(m["frames"])) for m in merged) == [
        ("countertop", ("b", "a")),
        ("outlet", ("a",)),
        ("outlet", ("b",)),
    ]


@respx.mock
def test_label_scene_merges_one_outlet_seen_from_two_frames():
    cams = _write_site(Path(get_settings().SCENE_DIR))
    replies = iter(
        _sse(
            json.dumps(
                {
                    "name": name,
                    "detail": "white duplex",
                    "source": "seen",
                    "box": _percent_box(cam, OUTLET),
                    "confidence": conf,
                }
            )
            + "\n"
        )
        for cam, name, conf in ((cams[0], "outlet", 0.8), (cams[1], "electrical receptacle", 0.9))
    )
    respx.post(XAI_URL).mock(side_effect=lambda request: next(replies))
    out = asyncio.run(labels.label_scene("den", every=1))
    assert out["rev"] == 3 and out["frames"] == 2 and out["cost_usd"] == 0.0025
    [anchor] = out["labels"]
    assert anchor["kind"] == "outlet" and anchor["name"] == "electrical receptacle"
    assert anchor["frames"] == ["0002", "0001"]
    assert np.allclose(anchor["pos"], OUTLET, atol=0.02)

    with TestClient(app) as client:
        served = client.get("/scenes/den/labels")
        assert served.status_code == 200 and served.json()["labels"] == out["labels"]
        assert client.get("/scenes/nowhere/labels").status_code == 404


# --- agent -------------------------------------------------------------------------------


async def _no_llm(*a, **kw):
    raise AssertionError("the fast path must not call the agent LLM")


@pytest.mark.asyncio
@respx.mock
async def test_agent_what_am_i_looking_at_shows_labels(monkeypatch):
    respx.post(XAI_URL).mock(return_value=_sse(REAL))
    monkeypatch.setattr(agent, "chat", _no_llm)
    ctx = {"frames": [{"id": "pca-7", "jpg_b64": base64.b64encode(_jpg()).decode()}]}
    result = await agent.handle_command("a1", "What am I looking at?", ctx)
    [action] = result["actions"]
    assert action["name"] == "show_labels"
    assert action["args"]["frame_id"] == "pca-7" and len(action["args"]["labels"]) == 6
    assert result["reply"].startswith("I see the electrical outlet")
    assert labels._calls["a1"]  # counted against the session's rate guard


@pytest.mark.asyncio
async def test_agent_label_fast_path_without_a_frame(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    result = await agent.handle_command("a2", "label this", {})
    assert result == {
        "reply": "Can't do that yet: no frame to look at.",
        "actions": [],
        "job_id": None,
    }


@pytest.mark.asyncio
@respx.mock
async def test_agent_offline_serves_a_labelled_frame(monkeypatch):
    respx.post(XAI_URL).mock(return_value=_sse(REAL))
    await labels.label_frame(_jpg())
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    ctx = {"frame_jpg_b64": base64.b64encode(_jpg()).decode()}
    result = await agent.handle_command("a3", "what am I looking at", ctx)
    assert result["actions"][0]["name"] == "show_labels"
    ctx = {"frame_jpg_b64": base64.b64encode(_jpg(shade=5)).decode()}
    result = await agent.handle_command("a3", "what am I looking at", ctx)
    assert result["reply"] == "Can't do that yet: offline and this view was never labelled."


@pytest.mark.asyncio
@respx.mock
async def test_agent_llm_path_offers_the_tool_only_with_a_frame(
    monkeypatch,
):
    from tests.test_agent import _tool_call_completion

    respx.post(XAI_URL).mock(return_value=_sse(REAL))
    offered = []

    async def fake_chat(role, messages, tools=None, **kw):
        offered.append({t["function"]["name"] for t in tools})
        return _tool_call_completion("label_view", {"focus": "electrical"})

    monkeypatch.setattr(agent, "chat", fake_chat)
    await agent.handle_command("a4", "hmm, tell me about the stuff on the wall", {})
    assert "label_view" not in offered[-1]
    ctx = {"frame_jpg_b64": base64.b64encode(_jpg()).decode()}
    result = await agent.handle_command("a4", "hmm, tell me about the stuff on the wall", ctx)
    assert "label_view" in offered[-1]
    assert result["reply"].startswith("I see the electrical outlet")  # the template, one turn
    assert result["actions"][0]["name"] == "show_labels"
