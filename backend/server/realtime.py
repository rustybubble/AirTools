"""Realtime Quartermaster: a server-side relay between the headset and xAI's realtime voice API
(docs/research/grok-ideas/g2-voice-agents.md idea #1, docs/api.md `WS /voice/realtime`).

The server holds the xAI socket itself, so the API key never leaves the laptop and the headset
needs no ephemeral token. Tool calls run here through `agent._run_tool` (same session state as
`/agent/command`), and the headset receives the same action JSON it already executes for
`actions[]` (docs/api.md §5). `/voice/command` stays as the fallback.

Audio uses xAI's "binary" transport both ways (raw PCM16 24 kHz mono frames, no base64), so the
relay forwards mic and reply frames untouched.
"""

import asyncio
import contextlib
import json
import logging
import time
from typing import Any

import websockets
from fastapi import WebSocket, WebSocketDisconnect

from server import agent, jobs, voice
from server.config import get_settings
from server.plan import hanger_plan  # shared with the planner: voice and plan give one count

logger = logging.getLogger(__name__)

XAI_REALTIME_WS = "wss://api.x.ai/v1/realtime"
PCM_BYTES_PER_S = 24_000 * 2  # 24 kHz, 16-bit mono
USD_PER_AUDIO_MIN = 0.08  # grok-voice-think-fast-2.0 (G2 §2); counted on audio in + out here
COMMIT_ACK_TIMEOUT_S = 1.5
JOB_ANNOUNCE_TIMEOUT_S = 90  # speak the search summary if the job finishes within this

INSTRUCTIONS = (
    agent.SYSTEM_PROMPT + " Before a tool call say a few words at most. find_part only starts "
    "a search: the results are announced to the user separately, so never invent part names or "
    "prices. For hanger counts or air-conditioner sizing, call hanger_plan or btu_for_room and "
    "read out their numbers; never guess a number. hanger_plan reads the headset's tape "
    "measurement itself."
)

KEYTERMS = [t.strip() for t in voice.DEFAULT_STT_PROMPT.split(",")] + [
    "spike and ferrule",
    "drip edge",
    "downspout",
    "joist hanger",
]

# Spoken form of units the model tends to read out letter by letter.
PRONUNCIATION = {"mm": "millimetres", "BTU": "B T U"}


# --- local sizing tools (pure; realtime-only, so the Groq agent's prompt stays small) ---------
# hanger_plan is imported from server/plan.py above.

AC_SIZES_BTU = [5000, 6000, 8000, 10000, 12000, 14000, 18000, 24000, 30000, 36000]


def btu_for_room(
    w_m: float,
    d_m: float,
    h_m: float = 2.44,
    sunny: bool = False,
    occupants: int = 2,
    kitchen: bool = False,
) -> dict:
    """Energy Star room-AC rule of thumb: 20 BTU/ft² at an 8 ft ceiling (scaled up for taller
    rooms), +10 % if sunny, +600 per occupant over two, +4,000 for a kitchen."""
    w_m, d_m, h_m = float(w_m), float(d_m), float(h_m)
    if not (0 < w_m <= 50 and 0 < d_m <= 50 and 0 < h_m <= 10):
        raise ValueError("room dimensions must be positive metres (w, d <= 50, h <= 10)")
    area_sqft = w_m * d_m * 10.7639
    btu = 20 * area_sqft * max(1.0, h_m / 2.44)
    if sunny:
        btu *= 1.1
    btu += 600 * max(0, int(occupants) - 2)
    if kitchen:
        btu += 4000
    btu = round(btu, -2)
    unit = next((s for s in AC_SIZES_BTU if s >= btu), None)
    spoken = f"About {btu:,.0f} BTU" + (
        f"; a {unit:,} BTU unit covers it." if unit else "; that needs a ducted system."
    )
    return {"btu": btu, "unit_btu": unit, "area_sqft": round(area_sqft), "spoken": spoken}


LOCAL_TOOLS: list[dict[str, Any]] = [
    {
        "type": "function",
        "name": "hanger_plan",
        "description": "How many gutter hangers for a run and where they go. Leave out "
        "run_length_m to use the headset's current tape measurement. Typical spacing is 600 mm "
        "(450 mm in snow country).",
        "parameters": {
            "type": "object",
            "properties": {
                "run_length_m": {"type": "number"},
                "spacing_mm": {"type": "number"},
            },
            "required": ["spacing_mm"],
        },
    },
    {
        "type": "function",
        "name": "btu_for_room",
        "description": "Air-conditioner size in BTU for a room measured in metres.",
        "parameters": {
            "type": "object",
            "properties": {
                "w_m": {"type": "number"},
                "d_m": {"type": "number"},
                "h_m": {"type": "number"},
                "sunny": {"type": "boolean"},
                "occupants": {"type": "integer"},
                "kitchen": {"type": "boolean"},
            },
            "required": ["w_m", "d_m"],
        },
    },
]
_LOCAL_FUNCS = {"hanger_plan": hanger_plan, "btu_for_room": btu_for_room}


def realtime_tools() -> list[dict[str, Any]]:
    """`agent.TOOLS` in the realtime API's flat shape ({"type":"function","name",...} rather
    than chat-completions' nested "function" object), plus the local sizing tools."""
    return [{"type": "function", **t["function"]} for t in agent.TOOLS] + LOCAL_TOOLS


def session_config() -> dict[str, Any]:
    pcm = {"type": "audio/pcm", "rate": 24_000}
    return {
        "voice": get_settings().XAI_VOICE,
        "instructions": INSTRUCTIONS,
        "reasoning": {"effort": "none"},  # ~1 s to first audio after a tool result (G2 §1)
        "turn_detection": None,  # push-to-talk: the headset commits on pinch release
        "audio": {
            "input": {
                "format": pcm,
                "transport": "binary",
                "transcription": {
                    "model": "grok-transcribe",
                    "language_hint": "en",
                    "keyterms": KEYTERMS,
                },
            },
            "output": {"format": pcm, "transport": "binary"},
        },
        "replace": PRONUNCIATION,
        "tools": realtime_tools(),
    }


# --- the relay -------------------------------------------------------------------------


def _connect():
    """xAI realtime socket, server key as Bearer (patched out in tests)."""
    settings = get_settings()
    return websockets.connect(
        f"{XAI_REALTIME_WS}?model={settings.XAI_REALTIME_MODEL}",
        additional_headers={"Authorization": f"Bearer {settings.GROK_API_KEY}"},
        open_timeout=10,
        max_size=2**22,
    )


class _Relay:
    """One headset <-> xAI session. Two loops (`_uplink`, `_downlink`) plus short-lived tasks
    for tool calls and job announcements. Only one `response.create` is ever in flight: a turn
    that ended in tool calls gets its follow-up only after `response.done` and after the reply
    audio already sent has had time to play (xAI docs, "Avoid Audio Overlap")."""

    def __init__(self, client: WebSocket, xai, session_id: str):
        self.client, self.xai, self.session_id = client, xai, session_id
        self.session = agent._get_session(session_id)
        self.context: dict[str, Any] = {}
        self.committed = asyncio.Event()
        self.responding = False
        self.turn = 0  # bumped per user turn; a stale tool follow-up must not answer a new turn
        self.tool_tasks: list[asyncio.Task] = []
        self.tool_names: list[str] = []
        self.spoke = False  # the current response has sent audio
        self.follow_up_pending = False
        self.background: set[asyncio.Task] = set()
        self.playback_end = 0.0  # monotonic time the client finishes playing sent audio
        self.assistant_text = ""
        self.stats = {"audio_in": 0, "audio_out": 0, "text_items": 0, "tool_calls": 0}
        self.t0 = time.monotonic()
        # (what started it, monotonic time) of the last response.create, until its first audio
        self.mark: tuple[str, float] | None = None
        self.latency: list[str] = []  # e.g. "speech 0.84", "tool 1.12" (seconds to first audio)

    async def _xai_send(self, event: dict) -> None:
        await self.xai.send(json.dumps(event))

    async def _to_client(self, msg: dict) -> None:
        with contextlib.suppress(WebSocketDisconnect, RuntimeError):
            await self.client.send_json(msg)

    async def _create_response(self, why: str) -> None:
        self.responding = True
        self.mark = (why, time.monotonic())
        await self._xai_send({"type": "response.create"})

    def _spawn(self, coro) -> asyncio.Task:
        task = asyncio.create_task(coro)
        self.background.add(task)
        task.add_done_callback(self.background.discard)
        return task

    # client -> xAI
    async def _uplink(self) -> None:
        while True:
            msg = await self.client.receive()
            if msg["type"] == "websocket.disconnect":
                return
            if msg.get("bytes") is not None:
                self.stats["audio_in"] += len(msg["bytes"])
                await self.xai.send(msg["bytes"])
                continue
            try:
                event = json.loads(msg.get("text") or "")
                kind = event["type"]
            except (ValueError, KeyError, TypeError):
                await self._to_client({"type": "error", "message": "bad message", "fatal": False})
                continue
            if kind == "commit":
                await self._new_turn()
                self.committed.clear()
                await self._xai_send({"type": "input_audio_buffer.commit"})
                # Live check: a response.create racing ahead of the commit answered without
                # the user's audio. Wait for the ack (~30 ms), but never hang the turn on it.
                try:
                    await asyncio.wait_for(self.committed.wait(), COMMIT_ACK_TIMEOUT_S)
                except TimeoutError:
                    logger.warning("realtime %s: no commit ack", self.session_id)
                await self._create_response("speech")
            elif kind == "text":
                await self._new_turn()
                self.stats["text_items"] += 1
                text = str(event.get("text", ""))[: agent.MAX_TEXT_LEN]
                await self._to_client({"type": "transcript", "role": "user", "text": text})
                await self._send_message("user", text)
                await self._create_response("text")
            elif kind == "cancel":
                await self._cancel()
            elif kind == "context":
                self.context = event.get("context") or {}
                agent._apply_context(self.session, self.context)
            elif kind == "frame":  # a fresh camera frame for check_step ("check it")
                self.context["frame_jpg_b64"] = str(event.get("jpg_b64") or "")
                self.context["frame_at"] = time.time()
            else:
                await self._to_client(
                    {"type": "error", "message": f"unknown type {kind!r}", "fatal": False}
                )

    async def _new_turn(self) -> None:
        self.turn += 1
        await self._cancel()

    async def _cancel(self) -> None:
        """Barge-in: stop the reply in flight (the client flushes its own playback)."""
        if self.responding:
            await self._xai_send({"type": "response.cancel"})
            self.responding = False
        self.playback_end = 0.0

    async def _send_message(self, role: str, text: str) -> None:
        await self._xai_send(
            {
                "type": "conversation.item.create",
                "item": {
                    "type": "message",
                    "role": role,
                    "content": [{"type": "input_text", "text": text}],
                },
            }
        )

    # xAI -> client
    async def _downlink(self) -> None:
        async for raw in self.xai:
            if isinstance(raw, bytes):
                await self._audio_out(raw)
                continue
            event = json.loads(raw)
            kind = event.get("type", "")
            if kind == "input_audio_buffer.committed":
                self.committed.set()
            elif kind == "session.updated":
                await self._to_client({"type": "ready"})
            elif kind == "response.created":
                self.responding = True
                self.assistant_text = ""
                self.spoke = False
            elif kind == "response.output_audio_transcript.delta":
                self.assistant_text += event.get("delta", "")
                await self._to_client(
                    {"type": "transcript", "role": "assistant", "text": self.assistant_text}
                )
            elif kind == "response.output_audio_transcript.done":
                text = event.get("transcript") or self.assistant_text
                await self._to_client(
                    {"type": "transcript", "role": "assistant", "text": text, "final": True}
                )
            elif kind.startswith("conversation.item.input_audio_transcription."):
                text = event.get("transcript") or event.get("delta") or ""
                if text:
                    await self._to_client(
                        {
                            "type": "transcript",
                            "role": "user",
                            "text": text,
                            "final": kind.endswith(".completed"),
                        }
                    )
            elif kind == "response.function_call_arguments.done":
                self.tool_tasks.append(asyncio.create_task(self._run_tool(event)))
                self.tool_names.append(event.get("name", ""))
            elif kind == "response.done":
                await self._response_done(event)
            elif kind == "error":
                logger.warning("realtime %s: xai error %s", self.session_id, event.get("error"))
                err = event.get("error") or {}
                message = err.get("message") if isinstance(err, dict) else str(err)
                await self._to_client({"type": "error", "message": message, "fatal": False})

    async def _audio_out(self, chunk: bytes) -> None:
        now = time.monotonic()
        if self.mark is not None:
            self.latency.append(f"{self.mark[0]} {now - self.mark[1]:.2f}")
            self.mark = None
        self.stats["audio_out"] += len(chunk)
        self.spoke = True
        self.playback_end = max(self.playback_end, now) + len(chunk) / PCM_BYTES_PER_S
        with contextlib.suppress(WebSocketDisconnect, RuntimeError):
            await self.client.send_bytes(chunk)

    async def _run_tool(self, event: dict) -> dict:
        name, call_id = event.get("name", ""), event.get("call_id", "")
        self.stats["tool_calls"] += 1
        try:
            args = json.loads(event.get("arguments") or "{}")
        except json.JSONDecodeError:
            args = {}
        action, job_id = None, None
        try:
            if name == "hanger_plan" and not args.get("run_length_m"):
                # The model never sees the headset's measurement (sending it as a conversation
                # item broke the next audio turn in the live check), so fill it in here.
                measured = (self.session.measurement or {}).get("value_m")
                if not measured:
                    raise ValueError("no tape measurement yet: measure the run first")
                args["run_length_m"] = measured
            if name in _LOCAL_FUNCS:
                result = _LOCAL_FUNCS[name](**args)
            else:
                result, action, job_id = await agent._run_tool(
                    self.session, name, args, self.context
                )
        except (TypeError, ValueError) as exc:
            result = {"error": str(exc)}
        except Exception as exc:  # a tool failure must not kill the voice session
            logger.exception("realtime %s: tool %s failed", self.session_id, name)
            result = {"error": f"{name} failed: {exc}"}
        for a in agent._as_list(action):  # survey_condition/fix_pin queue several
            await self._to_client({"type": "action", "action": a})
        if job_id:
            self._spawn(self._announce_job(job_id))
        await self._xai_send(
            {
                "type": "conversation.item.create",
                "item": {
                    "type": "function_call_output",
                    "call_id": call_id,
                    "output": json.dumps(result),
                },
            }
        )
        return result

    async def _response_done(self, event: dict) -> None:
        self.responding = False
        await self._to_client({"type": "response_done"})
        status = (event.get("response") or {}).get("status")
        tasks, self.tool_tasks = self.tool_tasks, []
        names, self.tool_names = self.tool_names, []
        # A spoken "searching..." plus find_part needs no follow-up: its tool result is only a
        # job id, and the real answer is the job announcement (live check: the follow-up was
        # just filler, "you'll hear the matches when they're ready").
        search_only = self.spoke and set(names) == {"find_part"}
        verbatim = bool(names) and set(names) <= set(agent.COACH_TOOLS)
        if tasks and status != "cancelled" and verbatim:
            self.follow_up_pending = True
            self._spawn(self._speak_results(tasks, self.turn))
        elif tasks and status != "cancelled" and not search_only:
            self.follow_up_pending = True
            self._spawn(self._follow_up(tasks, self.turn))
        elif tasks:  # still answer the calls so the conversation stays valid
            self._spawn(asyncio.wait(tasks))

    async def _follow_up(self, tasks: list[asyncio.Task], turn: int) -> None:
        """All tool outputs of a response are in -> one response.create, once the preamble has
        finished playing. Skipped if the user already started a new turn."""
        try:
            await asyncio.wait(tasks)
            await asyncio.sleep(max(0.0, self.playback_end - time.monotonic()))
            if turn == self.turn and not self.responding:
                await self._create_response("tool")
        finally:
            self.follow_up_pending = False

    async def _speak_results(self, tasks: list[asyncio.Task], turn: int) -> None:
        """Coach tools: say their `spoken` word for word (force_message) instead of asking the
        model for a follow-up, so manual steps are never paraphrased."""
        try:
            await asyncio.wait(tasks)
            await asyncio.sleep(max(0.0, self.playback_end - time.monotonic()))
            results = [t.result() for t in tasks if not t.cancelled()]
            text = " ".join(
                r.get("spoken") or f"Can't do that yet: {r.get('error', 'no answer')}."
                for r in results
            )
            if turn == self.turn and not self.responding and text:
                await self._force(text)
        finally:
            self.follow_up_pending = False

    async def _force(self, text: str) -> None:
        """Speak `text` verbatim: no model call, so nothing gets invented or reworded."""
        self.mark = ("announce", time.monotonic())
        await self._xai_send(
            {
                "type": "conversation.item.create",
                "item": {
                    "type": "force_message",
                    "role": "assistant",
                    "interruptible": True,
                    "content": [{"type": "output_text", "text": text}],
                },
            }
        )

    async def _announce_job(self, job_id: str) -> None:
        """Speak the search summary verbatim when the background search finishes (force_message:
        no model call, so no invented part names), and remember the candidates so "pick the
        second one" works without the headset re-sending candidate_ids."""
        job = await jobs.wait(job_id, JOB_ANNOUNCE_TIMEOUT_S)
        if job is None or job.status not in ("done", "failed"):
            return
        self.session.candidate_ids = [c.id for c in job.candidates]
        while (
            self.responding
            or self.tool_tasks
            or self.follow_up_pending
            or time.monotonic() < self.playback_end
        ):
            await asyncio.sleep(0.2)
        await self._to_client({"type": "job_done", "job_id": job_id, "status": job.status})
        await self._force(jobs.summary(job))

    async def run(self) -> None:
        await self._xai_send({"type": "session.update", "session": session_config()})
        up = asyncio.create_task(self._uplink())
        down = asyncio.create_task(self._downlink())
        try:
            done, _ = await asyncio.wait(
                {up, down},
                timeout=get_settings().REALTIME_MAX_S,
                return_when=asyncio.FIRST_COMPLETED,
            )
            if not done:
                await self._to_client(
                    {"type": "error", "message": "session time limit reached", "fatal": True}
                )
            elif down in done and not down.cancelled() and down.exception() is None:
                await self._to_client(
                    {"type": "error", "message": "voice service closed", "fatal": True}
                )
            for task in done:
                if not task.cancelled() and task.exception() is not None:
                    raise task.exception()
        finally:
            for task in (up, down, *self.tool_tasks, *self.background):
                task.cancel()
            self._log_stats()

    def _log_stats(self) -> None:
        audio_in = self.stats["audio_in"] / PCM_BYTES_PER_S
        audio_out = self.stats["audio_out"] / PCM_BYTES_PER_S
        cost = (audio_in + audio_out) / 60 * USD_PER_AUDIO_MIN + 0.004 * self.stats["text_items"]
        lat = ", ".join(self.latency) or "-"
        logger.info(
            "realtime %s: %.1fs wall, audio in %.1fs out %.1fs, %d text items, %d tool calls, "
            "first-audio latency [%s] s, ~$%.3f",
            self.session_id,
            time.monotonic() - self.t0,
            audio_in,
            audio_out,
            self.stats["text_items"],
            self.stats["tool_calls"],
            lat,
            cost,
        )


async def bridge(client: WebSocket, session_id: str) -> None:
    """Run one relay session on an accepted client websocket; always closes it."""
    settings = get_settings()
    problem = None
    if settings.OFFLINE:
        problem = "offline: realtime voice unavailable, use /voice/command"
    elif not settings.GROK_API_KEY:
        problem = "GROK_API_KEY not set; realtime voice unavailable"
    if problem:
        await client.send_json({"type": "error", "message": problem, "fatal": True})
        await client.close()
        return
    try:
        async with _connect() as xai:
            await _Relay(client, xai, session_id).run()
    except WebSocketDisconnect:
        pass
    except (OSError, TimeoutError, websockets.WebSocketException) as exc:
        logger.warning("realtime %s: xai connection failed: %s", session_id, exc)
        await _close_with_error(client, f"voice service unavailable: {type(exc).__name__}")
        return
    await _close_with_error(client, None)


async def _close_with_error(client: WebSocket, message: str | None) -> None:
    with contextlib.suppress(WebSocketDisconnect, RuntimeError):
        if message:
            await client.send_json({"type": "error", "message": message, "fatal": True})
        await client.close()
