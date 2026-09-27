import io
import json
import math
import struct
import wave

import httpx
import pytest
import respx

from server.config import get_settings
from server.voice import TTS_CHAR_LIMIT, VoiceError, realtime_token, speak, transcribe

TRANSCRIPTIONS_URL = "https://api.groq.com/openai/v1/audio/transcriptions"
SPEECH_URL = "https://api.groq.com/openai/v1/audio/speech"
REALTIME_URL = "https://api.x.ai/v1/realtime/client_secrets"


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    """speak() now caches to disk (namespace "tts") -- isolate each test's cache dir so an
    earlier test's cached audio for the same text (e.g. "hello") can't skip a later test's
    respx mock."""
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


# --- transcribe --------------------------------------------------------------


@pytest.mark.asyncio
async def test_transcribe_sends_multipart_and_returns_text():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(TRANSCRIPTIONS_URL).mock(
            return_value=httpx.Response(200, json={"text": "5 inch K-style hanger"})
        )
        text = await transcribe(b"fake-wav-bytes", filename="clip.wav")

    assert text == "5 inch K-style hanger"
    request = route.calls.last.request
    assert b'name="model"' in request.content
    assert b"whisper-large-v3-turbo" in request.content
    assert b'name="file"; filename="clip.wav"' in request.content
    assert b"fake-wav-bytes" in request.content
    assert b'name="language"' in request.content and b"en" in request.content


@pytest.mark.asyncio
async def test_transcribe_empty_audio_rejected():
    with pytest.raises(ValueError, match="empty audio"):
        await transcribe(b"")


@pytest.mark.asyncio
async def test_transcribe_oversized_rejected():
    with pytest.raises(ValueError, match="exceeds"):
        await transcribe(b"x" * (25 * 1024 * 1024 + 1))


# --- speak ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_speak_returns_bytes_and_mime():
    fake_wav = b"RIFF....WAVEfmt "
    with respx.mock(assert_all_called=True) as router:
        router.post(SPEECH_URL).mock(return_value=httpx.Response(200, content=fake_wav))
        audio, mime = await speak("A five inch K-style hanger bracket.")

    assert audio == fake_wav
    assert mime == "audio/wav"


@pytest.mark.asyncio
async def test_speak_truncates_long_text_at_sentence_boundary():
    long_text = ("This is a sentence about gutters. " * 200) + "Trailing fragment with no end"
    assert len(long_text) > TTS_CHAR_LIMIT

    with respx.mock(assert_all_called=True) as router:
        route = router.post(SPEECH_URL).mock(return_value=httpx.Response(200, content=b"wav"))
        await speak(long_text)

    body = json.loads(route.calls.last.request.content)
    assert len(body["input"]) <= TTS_CHAR_LIMIT
    assert body["input"].endswith(".")
    assert body["model"] == "canopylabs/orpheus-v1-english"
    assert body["voice"] == "troy"
    assert body["response_format"] == "wav"


@pytest.mark.asyncio
async def test_speak_falls_back_to_edge_when_groq_refuses(monkeypatch):
    from server import voice

    async def fake_edge(text):
        return b"ID3fake-mp3"

    monkeypatch.setattr(voice, "_speak_edge", fake_edge)
    with respx.mock(assert_all_called=True) as router:
        router.post(SPEECH_URL).mock(
            return_value=httpx.Response(400, json={"error": "model terms not accepted"})
        )
        assert await speak("hello") == (b"ID3fake-mp3", "audio/mpeg")


@pytest.mark.asyncio
async def test_groq_terms_refusal_skips_groq_for_later_lines(monkeypatch):
    from server import voice

    async def fake_edge(text):
        return b"ID3fake-mp3"

    monkeypatch.setattr(voice, "_speak_edge", fake_edge)
    monkeypatch.setattr(voice, "_groq_tts_disabled", False)
    with respx.mock(assert_all_called=True) as router:
        route = router.post(SPEECH_URL).mock(
            return_value=httpx.Response(400, json={"error": {"code": "model_terms_required"}})
        )
        await speak("first line")
        await speak("second line")
    assert route.call_count == 1


@pytest.mark.asyncio
async def test_speak_uses_tts_cache_before_any_provider():
    fake_wav = b"RIFF-cached"
    with respx.mock(assert_all_called=True) as router:
        router.post(SPEECH_URL).mock(return_value=httpx.Response(200, content=fake_wav))
        first = await speak("Searching the supply shops.")
    assert first == (fake_wav, "audio/wav")

    # second call: no route registered at all -- a cache hit must never touch the network
    with respx.mock(assert_all_called=False) as router:
        second = await speak("Searching the supply shops.")
    assert second == (fake_wav, "audio/wav")
    assert router.calls.call_count == 0


@pytest.mark.asyncio
async def test_speak_offline_hit_returns_cached_audio(monkeypatch):
    fake_wav = b"RIFF-warmed"
    with respx.mock(assert_all_called=True) as router:
        router.post(SPEECH_URL).mock(return_value=httpx.Response(200, content=fake_wav))
        await speak("Array placed.")  # warm the tts cache while online

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    with respx.mock(assert_all_called=False) as router:
        audio, mime = await speak("Array placed.")
    assert (audio, mime) == (fake_wav, "audio/wav")
    assert router.calls.call_count == 0


@pytest.mark.asyncio
async def test_speak_offline_miss_raises_voice_error_without_network(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    with respx.mock(assert_all_called=False) as router, pytest.raises(VoiceError):
        await speak("never warmed")
    assert router.calls.call_count == 0


@pytest.mark.asyncio
async def test_transcribe_offline_raises_voice_error_without_network(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    with respx.mock(assert_all_called=False) as router, pytest.raises(VoiceError):
        await transcribe(b"fake-wav-bytes")
    assert router.calls.call_count == 0


@pytest.mark.asyncio
async def test_speak_raises_voice_error_when_all_providers_fail(monkeypatch):
    from server import voice

    async def broken_edge(text):
        raise RuntimeError("no network")

    monkeypatch.setattr(voice, "_speak_edge", broken_edge)
    with respx.mock(assert_all_called=True) as router:
        router.post(SPEECH_URL).mock(return_value=httpx.Response(400, json={"error": "x"}))
        with pytest.raises(VoiceError):
            await speak("hello")


# --- realtime_token ------------------------------------------------------


@pytest.mark.asyncio
async def test_realtime_token_request_shape():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(REALTIME_URL).mock(
            return_value=httpx.Response(
                200, json={"value": "xai-client-secret.abc123", "expires_at": 1_800_000_000}
            )
        )
        result = await realtime_token()

    assert result == {"value": "xai-client-secret.abc123", "expires_at": 1_800_000_000}
    request = route.calls.last.request
    assert request.headers["Authorization"] == "Bearer test-dummy-key"
    body = json.loads(request.content)
    assert body == {"expires_after": {"seconds": 300}}


@pytest.mark.asyncio
async def test_realtime_token_missing_key_raises(monkeypatch):
    monkeypatch.setenv("GROK_API_KEY", "")
    get_settings.cache_clear()
    with respx.mock(assert_all_called=False) as router:
        router.post(REALTIME_URL).mock(return_value=httpx.Response(200, json={}))
        with pytest.raises(VoiceError):
            await realtime_token()
    assert router.calls.call_count == 0


# --- live smoke test, never run by default (see pyproject `-m 'not live'') --


def _tiny_test_tone_wav(duration_s: float = 1.0, freq: float = 440.0, rate: int = 16000) -> bytes:
    """1s, 16kHz mono WAV tone -- stdlib `wave` only, no external fixtures."""
    n_samples = int(duration_s * rate)
    frames = bytearray()
    for i in range(n_samples):
        sample = int(32767 * 0.2 * math.sin(2 * math.pi * freq * i / rate))
        frames += struct.pack("<h", sample)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(rate)
        wav_file.writeframes(bytes(frames))
    return buf.getvalue()


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_transcribe_short_tone():
    audio = _tiny_test_tone_wav()
    text = await transcribe(audio, filename="tone.wav")
    assert isinstance(text, str)


# --- Groq STT can't answer: one retry, then xAI's grok-transcribe (realtime socket) -------------


@pytest.mark.asyncio
async def test_transcribe_retries_a_dropped_connection_once():
    with respx.mock(assert_all_called=True) as router:
        route = router.post(TRANSCRIPTIONS_URL).mock(
            side_effect=[httpx.ConnectError("reset"), httpx.Response(200, json={"text": "ok"})]
        )
        assert await transcribe(b"fake-wav-bytes") == "ok"
    assert route.call_count == 2


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", ["connect", "503", "429"])
async def test_transcribe_falls_back_to_xai_when_groq_cant(monkeypatch, failure):
    from server import voice

    sent = {}

    async def fake_xai(audio):
        sent["audio"] = audio
        return "the opening is thirty-four and a half inches tall"

    monkeypatch.setattr(voice, "transcribe_xai", fake_xai)
    effect = {
        "connect": httpx.ConnectError("down"),
        "503": httpx.Response(503, json={"error": "busy"}),
        "429": httpx.Response(429, json={"error": "limit"}),
    }[failure]
    with respx.mock() as router:
        router.post(TRANSCRIPTIONS_URL).mock(side_effect=[effect, effect])
        text = await transcribe(b"wav")
    assert text.startswith("the opening is")
    assert sent["audio"] == b"wav"


@pytest.mark.asyncio
async def test_transcribe_without_a_grok_key_is_a_voice_error(monkeypatch):
    monkeypatch.setenv("GROK_API_KEY", "")
    get_settings.cache_clear()
    with respx.mock() as router:
        router.post(TRANSCRIPTIONS_URL).mock(return_value=httpx.Response(503))
        with pytest.raises(VoiceError):
            await transcribe(b"wav")


def test_pcm24k_resamples_a_16k_mono_wav():
    from server.voice import pcm24k

    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16_000)
        w.writeframes(struct.pack("<1600h", *([1000] * 1600)))  # 0.1 s
    out = pcm24k(buf.getvalue())
    assert len(out) == 2400 * 2  # 0.1 s at 24 kHz, 16-bit
    assert struct.unpack("<h", out[:2])[0] == 1000
    with pytest.raises(VoiceError):
        pcm24k(b"not a wav")
