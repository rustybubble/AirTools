"""Scene vision: "ask the scene" + "what is this part?" (plan §4b.1 item 3, §5).

Sends the headset's nearest camera thumbnails (+ a question) to Groq's vision model and parses
a JSON answer: a spoken sentence, optionally a frame id + normalized 2D box (the headset
projects the box centre onto the mesh to drop a pin), and optionally a shopping query for
`find_part` when the question is about identifying a part.

Groq limit, confirmed live 2026-09-24 (`GET /openai/v1/models` + console.groq.com/docs/vision):
`qwen/qwen3.8-27b` is the *only* vision-capable model in the current listing, and it hard-caps
requests to 3 images. The design doc's "~6 nearest thumbnails" (implementation plan §4b.1 item
3, §5) doesn't fit that -- `ask_scene` truncates to `MAX_FRAMES`; callers should already sort
frames nearest-first.
"""

import base64
import binascii
import re
from typing import Any

from pydantic import BaseModel, field_validator

from server import llm

MAX_FRAMES = 3  # Groq qwen3.8-27b hard limit (see module docstring) -- bump if a bigger
# vision model ships on Groq; the design doc assumed 6.
MAX_FRAME_BYTES = 4 * 1024 * 1024  # ~4 MB per frame, per the brief

_SENTENCE_SPLIT = re.compile(r"(?<=[.!?])\s+")


class Frame(BaseModel):
    id: str
    jpg_b64: str


class SceneAnswer(BaseModel):
    answer: str
    frame_id: str | None = None
    box: list[float] | None = None  # [x0, y0, x1, y1] normalized 0..1
    part_query: str | None = None

    @field_validator("answer")
    @classmethod
    def _limit_sentences(cls, v: str) -> str:
        sentences = [s for s in _SENTENCE_SPLIT.split(v.strip()) if s]
        return " ".join(sentences[:2]).strip()

    @field_validator("box")
    @classmethod
    def _sanitize_box(cls, v: list[float] | None) -> list[float] | None:
        if v is None:
            return None
        if len(v) != 4:
            return None
        x0, y0, x1, y1 = v
        if not all(0.0 <= c <= 1.0 for c in v):
            return None
        if x0 >= x1 or y0 >= y1:
            return None
        return v


class _PartQuery(BaseModel):
    part_query: str | None = None


def _validate_frames(frames: list[Frame]) -> list[Frame]:
    if not frames:
        raise ValueError("need at least one frame")
    capped = frames[:MAX_FRAMES]
    for f in capped:
        try:
            raw = base64.b64decode(f.jpg_b64, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise ValueError(f"frame {f.id!r}: invalid base64 jpg") from exc
        if len(raw) > MAX_FRAME_BYTES:
            raise ValueError(
                f"frame {f.id!r}: {len(raw)} bytes exceeds the {MAX_FRAME_BYTES}-byte cap"
            )
    return capped


def _frame_content(frame: Frame) -> list[dict[str, Any]]:
    return [
        {"type": "text", "text": f"[frame {frame.id}]"},
        {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{frame.jpg_b64}"}},
    ]


async def ask_scene(question: str, frames: list[Frame]) -> SceneAnswer:
    """Send `question` + up to `MAX_FRAMES` nearest thumbnails to the vision model."""
    frames = _validate_frames(frames)
    # Live, "Leave ... null if you're not sure" got no frame_id/box at all (2 of 2 asks), so the
    # headset had nothing to pin: insist on a box whenever the thing is in view.
    prompt = (
        f"Question: {question}\n"
        f"Frame ids, in order: {', '.join(f.id for f in frames)} -- the headset's photos nearest "
        "the spot the user is asking about, nearest first. The headset drops a pin at the centre "
        "of your box, so always point when you can: if the thing the question is about is "
        "visible in any frame, you MUST set frame_id to that frame's id and box to its bounding "
        "box [x0,y0,x1,y1], normalized 0..1 within that image (top-left origin). A rough box "
        "beats none; leave frame_id and box null only if it isn't visible in any frame. If the "
        "question is about identifying a part, also set part_query to a short shopping search "
        "query for it (profile, material, colour, approximate size cues), else null."
    )
    content: list[dict[str, Any]] = [{"type": "text", "text": prompt}]
    for frame in frames:
        content.extend(_frame_content(frame))

    result = await llm.extract("vision", [{"role": "user", "content": content}], SceneAnswer)

    frame_ids = {f.id for f in frames}
    if result.frame_id is not None and result.frame_id not in frame_ids:
        result.frame_id = None
    if result.frame_id is None:
        result.box = None
    return result


async def identify_part(frame_jpg_b64: str, hint: str | None = None) -> str | None:
    """One-image call: a concise shopping query for the part in view, or None."""
    frame = _validate_frames([Frame(id="f0", jpg_b64=frame_jpg_b64)])[0]
    prompt = (
        "Identify the building/hardware part in this photo for a shopping search. Reply with a "
        "concise shopping query describing it (profile, material, colour, approximate size "
        "cues), or null if you can't tell."
    )
    if hint:
        prompt += f" Hint: {hint}."
    content = [{"type": "text", "text": prompt}, *_frame_content(frame)]

    result = await llm.extract("vision", [{"role": "user", "content": content}], _PartQuery)
    return result.part_query
