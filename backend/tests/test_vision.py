import base64
import json
from pathlib import Path

import httpx
import pytest
import respx

from server import llm, vision
from server.vision import Frame, SceneAnswer

ASSETS = Path(__file__).parent / "fixtures" / "assets"
GROQ_URL = "https://api.groq.com/openai/v1/chat/completions"

_TINY_B64 = base64.b64encode(b"tiny-jpeg-bytes").decode()


def _frame(fid: str = "f0", b64: str = _TINY_B64) -> Frame:
    return Frame(id=fid, jpg_b64=b64)


# --- frame validation: cap + reject ------------------------------------------------------


def test_validate_frames_caps_to_max_frames():
    frames = [_frame(f"f{i}") for i in range(6)]
    capped = vision._validate_frames(frames)
    assert len(capped) == vision.MAX_FRAMES == 3
    assert [f.id for f in capped] == ["f0", "f1", "f2"]


def test_validate_frames_rejects_empty_list():
    with pytest.raises(ValueError, match="at least one frame"):
        vision._validate_frames([])


def test_validate_frames_rejects_invalid_base64():
    with pytest.raises(ValueError, match="invalid base64"):
        vision._validate_frames([_frame(b64="not-valid-base64!!!")])


def test_validate_frames_rejects_oversized():
    huge = base64.b64encode(b"x" * (vision.MAX_FRAME_BYTES + 1)).decode()
    with pytest.raises(ValueError, match="exceeds"):
        vision._validate_frames([_frame(b64=huge)])


# --- SceneAnswer validation: box sanitising + sentence limit -----------------------------


def test_scene_answer_keeps_valid_box():
    answer = SceneAnswer(answer="It's a gutter hanger.", frame_id="f0", box=[0.1, 0.2, 0.8, 0.9])
    assert answer.box == [0.1, 0.2, 0.8, 0.9]


@pytest.mark.parametrize(
    "box",
    [
        [0.1, 0.2, 0.8],  # wrong length
        [0.1, 0.2, 0.8, 1.5],  # out of range
        [-0.1, 0.2, 0.8, 0.9],  # out of range
        [0.8, 0.2, 0.1, 0.9],  # x0 >= x1
        [0.1, 0.9, 0.8, 0.2],  # y0 >= y1
        [0.5, 0.5, 0.5, 0.9],  # x0 == x1
    ],
)
def test_scene_answer_drops_invalid_box(box):
    answer = SceneAnswer(answer="Hard to say.", frame_id="f0", box=box)
    assert answer.box is None


def test_scene_answer_limits_to_two_sentences():
    answer = SceneAnswer(answer="First one. Second one. Third one should be dropped.")
    assert answer.answer == "First one. Second one."


# --- ask_scene: message construction -----------------------------------------------------


@pytest.mark.asyncio
async def test_ask_scene_builds_multimodal_message_and_caps_frames(monkeypatch):
    captured = {}

    async def fake_extract(role, messages, model_cls):
        captured["role"] = role
        captured["messages"] = messages
        captured["model_cls"] = model_cls
        return SceneAnswer(answer="A gutter hanger.", frame_id="f0", box=[0.1, 0.1, 0.5, 0.5])

    monkeypatch.setattr(llm, "extract", fake_extract)

    frames = [_frame(f"f{i}") for i in range(5)]  # more than MAX_FRAMES
    result = await vision.ask_scene("what is this?", frames)

    assert captured["role"] == "vision"
    assert captured["model_cls"] is SceneAnswer
    content = captured["messages"][0]["content"]
    image_parts = [c for c in content if c["type"] == "image_url"]
    text_parts = [c for c in content if c["type"] == "text"]
    assert len(image_parts) == vision.MAX_FRAMES == 3
    assert all(p["image_url"]["url"] == f"data:image/jpeg;base64,{_TINY_B64}" for p in image_parts)
    assert any("f0" in t["text"] and "f1" in t["text"] and "f2" in t["text"] for t in text_parts)
    assert "f3" not in text_parts[0]["text"]  # only the capped frames are mentioned
    assert result.answer == "A gutter hanger."


@pytest.mark.asyncio
async def test_ask_scene_drops_frame_id_not_among_inputs(monkeypatch):
    async def fake_extract(role, messages, model_cls):
        return SceneAnswer(answer="Over there.", frame_id="unknown-frame", box=[0.1, 0.1, 0.5, 0.5])

    monkeypatch.setattr(llm, "extract", fake_extract)

    result = await vision.ask_scene("what is this?", [_frame("f0")])
    assert result.frame_id is None
    assert result.box is None  # box is meaningless without a frame reference


@pytest.mark.asyncio
async def test_ask_scene_no_box_when_frame_id_absent(monkeypatch):
    async def fake_extract(role, messages, model_cls):
        return SceneAnswer(answer="Not sure.")

    monkeypatch.setattr(llm, "extract", fake_extract)

    result = await vision.ask_scene("what is this?", [_frame("f0")])
    assert result.frame_id is None
    assert result.box is None


@pytest.mark.asyncio
async def test_ask_scene_rejects_bad_frames_before_calling_llm(monkeypatch):
    async def fake_extract(role, messages, model_cls):
        raise AssertionError("must not call the LLM with invalid frames")

    monkeypatch.setattr(llm, "extract", fake_extract)

    with pytest.raises(ValueError):
        await vision.ask_scene("what is this?", [])


# --- ask_scene: full request shape via respx (exercises the qwen json_object path) --------


@pytest.mark.asyncio
async def test_ask_scene_groq_request_uses_json_object_not_json_schema():
    body = {
        "id": "x",
        "object": "chat.completion",
        "created": 1,
        "model": "qwen/qwen3.8-27b",
        "choices": [
            {
                "index": 0,
                "finish_reason": "stop",
                "message": {
                    "role": "assistant",
                    "content": json.dumps(
                        {
                            "answer": "It's a window AC unit.",
                            "frame_id": "f0",
                            "box": [0.2, 0.2, 0.7, 0.8],
                            "part_query": "18x14 inch window air conditioner, white plastic",
                        }
                    ),
                },
            }
        ],
    }
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(return_value=httpx.Response(200, json=body))
        result = await vision.ask_scene("what is this?", [_frame("f0")])

    assert result.answer == "It's a window AC unit."
    assert result.part_query.startswith("18x14")
    req_body = json.loads(route.calls.last.request.content)
    assert req_body["model"] == "qwen/qwen3.8-27b"
    assert req_body["response_format"] == {"type": "json_object"}
    assert "tools" not in req_body
    assert req_body["messages"][0]["role"] == "system"
    assert "schema" in req_body["messages"][0]["content"].lower()


@pytest.mark.asyncio
async def test_ask_scene_request_caps_output_and_insists_on_a_box():
    """Integration finding #12: live, the vision model answered with no frame_id/box, and a
    second ask within the minute hit Groq's 1000 output-tokens/min limit."""
    body = {
        "id": "x",
        "object": "chat.completion",
        "created": 1,
        "model": "qwen/qwen3.8-27b",
        "choices": [
            {
                "index": 0,
                "finish_reason": "stop",
                "message": {
                    "role": "assistant",
                    "content": json.dumps(
                        {"answer": "A dishwasher.", "frame_id": "0316", "box": [0.2, 0.3, 0.6, 0.9]}
                    ),
                },
            }
        ],
    }
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(return_value=httpx.Response(200, json=body))
        result = await vision.ask_scene("What is this?", [_frame("0316"), _frame("0321")])

    assert (result.frame_id, result.box) == ("0316", [0.2, 0.3, 0.6, 0.9])
    req_body = json.loads(route.calls.last.request.content)
    assert req_body["max_tokens"] == llm.ROLE_MAX_TOKENS["vision"] == 400
    prompt = req_body["messages"][1]["content"][0]["text"]
    assert "MUST set frame_id" in prompt
    assert "only if it isn't visible in any frame" in prompt
    assert "if you're not sure" not in prompt  # the old escape hatch the model took


# --- identify_part -------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_identify_part_returns_query(monkeypatch):
    captured = {}

    async def fake_extract(role, messages, model_cls):
        captured["messages"] = messages
        captured["model_cls"] = model_cls
        return model_cls(part_query="5 inch K-style aluminum gutter hanger, white")

    monkeypatch.setattr(llm, "extract", fake_extract)

    result = await vision.identify_part(_TINY_B64, hint="near the fascia")
    assert result == "5 inch K-style aluminum gutter hanger, white"
    content = captured["messages"][0]["content"]
    image_parts = [c for c in content if c["type"] == "image_url"]
    assert len(image_parts) == 1
    assert any("near the fascia" in c["text"] for c in content if c["type"] == "text")


@pytest.mark.asyncio
async def test_identify_part_returns_none_when_unsure(monkeypatch):
    async def fake_extract(role, messages, model_cls):
        return model_cls(part_query=None)

    monkeypatch.setattr(llm, "extract", fake_extract)

    result = await vision.identify_part(_TINY_B64)
    assert result is None


# --- live smoke test, never run by default (see pyproject `-m 'not live'`) -----------------


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_groq_vision_window_ac(monkeypatch):
    jpg_b64 = base64.b64encode((ASSETS / "source_photo_window_ac.jpg").read_bytes()).decode()

    part_query = await vision.identify_part(
        jpg_b64, hint="photo of a part to buy a replacement for"
    )
    print(f"\nidentify_part -> {part_query!r}")

    answer = await vision.ask_scene("What is this?", [Frame(id="frame0", jpg_b64=jpg_b64)])
    print(
        f"ask_scene -> answer={answer.answer!r} frame_id={answer.frame_id} "
        f"box={answer.box} part_query={answer.part_query!r}"
    )

    assert part_query is None or isinstance(part_query, str)
    assert isinstance(answer, SceneAnswer)
    assert answer.answer
