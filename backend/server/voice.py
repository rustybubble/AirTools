"""Groq Whisper STT, Groq Orpheus TTS, xAI realtime token relay.

Plain httpx (no OpenAI SDK -- see server/llm.py's `_client()` note: the SDK's vendored
transport needs an explicit `httpx.AsyncClient()` for respx to intercept it in tests; using
httpx directly here sidesteps that entirely). See docs/research/llm-providers.md §3 (Groq
speech) and §5 (xAI realtime `client_secrets`).

STT/TTS never call raw `resolve_role`/`chat` from llm.py -- speech isn't a chat-completions
role, it's its own pair of endpoints, model picked from `Settings.STT_MODEL`/`TTS_MODEL`.

`speak()` is the only one of the two that's cacheable (STT audio is unique per utterance; TTS
output is deterministic per text) -- it goes through `cache.cached()` (namespace "tts"), which
is also `OFFLINE`'s enforcement point here: a cache hit still speaks offline, a miss raises.
`transcribe()` has no cache to fall back on, so it checks `OFFLINE` directly.
"""

import asyncio
import base64
import io
import json
import logging
import time
import wave

import edge_tts
import httpx
import numpy as np
import websockets

from server import cache
from server.config import get_settings
from server.keys import KeyPool, KeysExhausted

GROQ_BASE_URL = "https://api.groq.com/openai/v1"
XAI_REALTIME_URL = "https://api.x.ai/v1/realtime/client_secrets"

# Groq free-tier STT upload cap (docs §3: "Max 25 MB (free) / 100 MB (dev tier)").
# ponytail: hardcoded to the conservative free-tier number; add a Settings field if this
# deployment is ever on the dev tier and 25MB starts rejecting real uploads.
STT_MAX_BYTES = 25 * 1024 * 1024

# Orpheus TTS input limit. The doc's model table (§3) lists "context window: 4,000" for
# canopylabs/orpheus-v1-english with no separate prose statement of a *character* limit --
# using that 4,000 figure as the truncation bound since it's the only number tied to TTS input
# capacity in the doc.
logger = logging.getLogger(__name__)

# Set once Groq refuses TTS for a reason retrying won't fix (model terms not accepted), so
# every later line goes straight to edge-tts instead of paying a failed round trip.
_groq_tts_disabled = False
TTS_CHAR_LIMIT = 4000

DEFAULT_STT_PROMPT = (
    "gutter, fascia, hanger, K-style, soffit, flashing, downspout, BTU, millimetres, quartermaster"
)


class VoiceError(Exception):
    """Voice provider call failed; caller should degrade to text-only."""


# Same rotation as llm.groq_pool: GROQ_API_KEY, GROQ_API_KEY_2, ... per speech model, on 429.
_VOICE_POOLS: dict[str, KeyPool] = {}


def _voice_pool(model: str) -> KeyPool:
    if model not in _VOICE_POOLS:
        _VOICE_POOLS[model] = KeyPool(
            "GROQ_API_KEY",
            is_limited=lambda exc: (
                isinstance(exc, httpx.HTTPStatusError) and exc.response.status_code == 429
            ),
            cooldown_s=900,
        )
    return _VOICE_POOLS[model]


async def _groq_post(model: str, post) -> httpx.Response:
    """`await post(key)` with the first usable Groq key for `model`; a 429 moves to the next."""

    async def attempt(key: str | None) -> httpx.Response:
        resp = await post(key)
        if resp.status_code == 429:
            resp.raise_for_status()
        return resp

    pool = _voice_pool(model)
    if not pool.keys():
        return await attempt(get_settings().GROQ_API_KEY)
    try:
        return await pool.call(attempt)
    except KeysExhausted as exc:
        raise VoiceError(str(exc)) from exc


def _truncate_at_sentence(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    clipped = text[:limit]
    for i in range(len(clipped) - 1, -1, -1):
        if clipped[i] in ".!?":
            return clipped[: i + 1]
    return clipped


async def transcribe(audio: bytes, filename: str = "audio.wav", prompt: str | None = None) -> str:
    """Groq Whisper STT -> transcript text."""
    if not audio:
        raise ValueError("empty audio")
    if len(audio) > STT_MAX_BYTES:
        raise ValueError(f"audio is {len(audio)} bytes, exceeds Groq's {STT_MAX_BYTES} byte cap")

    settings = get_settings()
    if settings.OFFLINE:
        # Each utterance is unique audio -- nothing to cache, so offline just means no STT
        # (app.py turns this into a 503; the headset falls back to the crate menu).
        raise VoiceError("offline: speech-to-text unavailable")

    async def post(key: str | None) -> httpx.Response:
        async with httpx.AsyncClient() as client:
            return await client.post(
                f"{GROQ_BASE_URL}/audio/transcriptions",
                headers={"Authorization": f"Bearer {key}"},
                data={
                    "model": settings.STT_MODEL,
                    "language": "en",
                    "prompt": prompt or DEFAULT_STT_PROMPT,
                },
                files={"file": (filename, audio)},
            )

    groq_error: Exception | None = None
    for attempt in range(2):  # one retry: a dropped connection (the voice test's p11)
        try:
            resp = await _groq_post(settings.STT_MODEL, post)
            resp.raise_for_status()
            return resp.json()["text"]
        except (httpx.TransportError, httpx.HTTPStatusError, VoiceError) as exc:
            groq_error = exc
            status = exc.response.status_code if isinstance(exc, httpx.HTTPStatusError) else None
            if isinstance(exc, VoiceError) or (status is not None and status < 500):
                break  # every key limited / a bad request: retrying Groq won't help
            logger.warning("groq stt attempt %d failed: %s", attempt + 1, exc)
    if not settings.GROK_API_KEY:
        raise VoiceError(f"speech-to-text failed: {groq_error}")
    logger.warning("groq stt unavailable (%s); transcribing with xAI grok-transcribe", groq_error)
    try:
        return await transcribe_xai(audio)
    except (VoiceError, OSError, TimeoutError, websockets.WebSocketException) as exc:
        raise VoiceError(f"speech-to-text failed: groq {groq_error}; xai {exc}") from exc


XAI_REALTIME_WS = "wss://api.x.ai/v1/realtime"
XAI_STT_TIMEOUT_S = 12.0
XAI_STT_SETTLE_S = 1.5  # after a transcript, how long a longer one for the same audio may take


def pcm24k(audio: bytes) -> bytes:
    """A PCM16 WAV (any rate, mono or stereo) as the realtime API's input: PCM16 24 kHz mono."""
    try:
        with wave.open(io.BytesIO(audio)) as w:
            rate, channels, width = w.getframerate(), w.getnchannels(), w.getsampwidth()
            frames = w.readframes(w.getnframes())
    except (wave.Error, EOFError) as exc:
        raise VoiceError(f"not a WAV: {exc}") from exc
    if width != 2:
        raise VoiceError(f"{width * 8}-bit WAV; want 16-bit")
    pcm = np.frombuffer(frames, dtype=np.int16).astype(np.float32)
    if channels > 1:
        pcm = pcm.reshape(-1, channels).mean(axis=1)
    if rate != 24_000 and len(pcm):
        n = max(1, int(len(pcm) * 24_000 / rate))
        pcm = np.interp(np.linspace(0, len(pcm) - 1, n), np.arange(len(pcm)), pcm)
    return np.clip(pcm, -32768, 32767).astype(np.int16).tobytes()


async def transcribe_xai(audio: bytes) -> str:
    """Speech-to-text on xAI when Groq can't: xAI has no REST transcription endpoint (checked
    2026-09-26: /v1/audio/transcriptions is a 404), so one short realtime session with
    grok-transcribe (the realtime relay's input transcription): the audio as PCM16 24 kHz,
    commit, and the newest transcript of the item. Closed before the voice model's own reply
    plays (~$0.08 per minute of audio)."""
    settings = get_settings()
    pcm = pcm24k(audio)
    session = {
        "turn_detection": None,
        "audio": {
            "input": {
                "format": {"type": "audio/pcm", "rate": 24_000},
                "transport": "binary",
                "transcription": {"model": "grok-transcribe", "language_hint": "en"},
            }
        },
    }
    t0 = time.monotonic()
    async with websockets.connect(
        f"{XAI_REALTIME_WS}?model={settings.XAI_REALTIME_MODEL}",
        additional_headers={"Authorization": f"Bearer {settings.GROK_API_KEY}"},
        open_timeout=10,
        max_size=2**22,
    ) as ws:
        await ws.send(json.dumps({"type": "session.update", "session": session}))
        for i in range(0, len(pcm), 48_000):
            await ws.send(pcm[i : i + 48_000])
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        texts: dict[str, str] = {}  # item id -> its newest transcript, in order
        settle_until = None
        while True:
            now = time.monotonic()
            left = t0 + XAI_STT_TIMEOUT_S - now
            if settle_until is not None:
                left = min(left, settle_until - now)
            if left <= 0:
                break
            try:
                raw = await asyncio.wait_for(ws.recv(), left)
            except TimeoutError:
                break
            if isinstance(raw, bytes):
                continue
            event = json.loads(raw)
            kind = event.get("type", "")
            if kind == "conversation.item.input_audio_transcription.completed":
                texts[str(event.get("item_id"))] = str(event.get("transcript") or "")
                settle_until = time.monotonic() + XAI_STT_SETTLE_S
            elif kind == "response.created" and texts:
                break  # the voice model starts its own reply: the transcript is final
            elif kind == "error":
                raise VoiceError(f"xai realtime: {event.get('error')}")
    text = " ".join(t.strip() for t in texts.values() if t.strip())
    if not text:
        raise VoiceError("xai realtime: no transcript")
    logger.info("xai stt: %d chars in %.1f s", len(text), time.monotonic() - t0)
    return text


async def speak(text: str) -> tuple[bytes, str]:
    """TTS -> (audio_bytes, mime), consulting the disk "tts" cache first (namespace "tts", key
    = the truncated text) so offline TTS still works for lines `server/warm.py` pre-generated.
    On a cache miss: Groq Orpheus first; if Groq refuses (e.g. model terms not accepted in the
    console) fall back to Edge TTS (free, no key). VoiceError if both fail, or if OFFLINE and
    nothing is cached for `text`."""
    truncated = _truncate_at_sentence(text, TTS_CHAR_LIMIT)

    async def _synthesize() -> dict:
        try:
            audio, mime = await _speak_groq(truncated), "audio/wav"
        except (VoiceError, httpx.HTTPError) as exc:
            logger.warning("groq tts unavailable, falling back to edge-tts: %s", exc)
            try:
                audio, mime = await _speak_edge(truncated), "audio/mpeg"
            except Exception as edge_exc:
                raise VoiceError(f"all tts providers failed: {edge_exc}") from edge_exc
        return {"audio_b64": base64.b64encode(audio).decode(), "mime": mime}

    try:
        value = await cache.cached("tts", truncated, _synthesize)
    except cache.OfflineMiss as exc:
        raise VoiceError(str(exc)) from exc
    return base64.b64decode(value["audio_b64"]), value["mime"]


async def _speak_groq(text: str) -> bytes:
    global _groq_tts_disabled
    if _groq_tts_disabled:
        raise VoiceError("groq tts disabled for this process (model terms not accepted)")
    settings = get_settings()

    async def post(key: str | None) -> httpx.Response:
        async with httpx.AsyncClient() as client:
            return await client.post(
                f"{GROQ_BASE_URL}/audio/speech",
                headers={"Authorization": f"Bearer {key}"},
                json={
                    "model": settings.TTS_MODEL,
                    "input": text,
                    "voice": settings.TTS_VOICE,
                    "response_format": "wav",
                },
            )

    resp = await _groq_post(settings.TTS_MODEL, post)
    if resp.is_error:
        if "model_terms_required" in resp.text:
            _groq_tts_disabled = True
        raise VoiceError(f"groq tts failed: {resp.status_code} {resp.text[:300]}")
    return resp.content


async def _speak_edge(text: str) -> bytes:
    audio = bytearray()
    async for chunk in edge_tts.Communicate(text, get_settings().EDGE_TTS_VOICE).stream():
        if chunk["type"] == "audio":
            audio += chunk["data"]
    if not audio:
        raise VoiceError("edge-tts returned no audio")
    return bytes(audio)


async def realtime_token() -> dict:
    """xAI ephemeral realtime token (docs §5), `expires_at` a unix int. The headset uses the
    `WS /voice/realtime` relay (server/realtime.py) instead, which needs no token."""
    settings = get_settings()
    if not settings.GROK_API_KEY:
        raise VoiceError("GROK_API_KEY not set; xAI realtime unavailable")

    async with httpx.AsyncClient() as client:
        try:
            resp = await client.post(
                XAI_REALTIME_URL,
                headers={"Authorization": f"Bearer {settings.GROK_API_KEY}"},
                json={"expires_after": {"seconds": 300}},
            )
            resp.raise_for_status()
        except httpx.HTTPStatusError as exc:
            raise VoiceError(
                f"xai realtime token failed: {exc.response.status_code} {exc.response.text}"
            ) from exc
    body = resp.json()
    return {"value": body["value"], "expires_at": body["expires_at"]}
