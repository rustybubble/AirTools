import base64
import re
import shutil
import subprocess
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from server import agent, assets, bom, jobs, search
from server import app as app_module
from server.app import app
from server.config import get_settings
from server.models import Dims, Fit, Part, SearchRequest, Seller


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield tmp_path
    get_settings.cache_clear()


def _canned_parts() -> list[Part]:
    return [
        Part(
            id="amerimax-hidden-hanger-5k",
            name="5 inch K-style hidden hanger",
            dims_mm=Dims(w=127, d=38, h=45),
            sellers=[
                Seller(name="Ace Hardware", price_usd=12.99, shipping_usd=5.0, in_stock=True),
                Seller(name="Home Depot", price_usd=9.99, shipping_usd=0.0, in_stock=True),
            ],
            fit=Fit(status="fits", spare_mm=38, axis="w"),
        ),
        Part(
            id="amerimax-hidden-hanger-6k",
            name="6 inch K-style hidden hanger",
            dims_mm=Dims(w=152, d=38, h=45),
        ),
    ]


async def _fake_resolve_asset(part: Part, *, allow_ai: bool = True) -> Part:
    """Stand-in for assets.resolve_asset: writes tiny proxy files, no network/HF."""
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True, exist_ok=True)
    (pdir / "model.glb").write_bytes(b"glTF-fake-binary")
    (pdir / "image.jpg").write_bytes(b"\xff\xd8\xff\xe0fakejpeg")
    part.asset.status = "ready"
    jobs.save_part(part)
    return part


def _install_fake_search(monkeypatch, candidates=None):
    async def fake_find_parts(req, progress):
        progress("searching the supply shops")
        return candidates if candidates is not None else _canned_parts()

    monkeypatch.setattr(search, "find_parts", fake_find_parts)


# --- search -> job -> done, then asset resolution + static file serving ------


def test_search_job_done_and_assets_resolve(monkeypatch):
    _install_fake_search(monkeypatch)
    monkeypatch.setattr(jobs.assets, "resolve_asset", _fake_resolve_asset)

    with TestClient(app) as client:
        resp = client.post("/parts/search", json={"query": "5 inch hanger"})
        assert resp.status_code == 200
        body = resp.json()
        job_id = body["job_id"]
        assert body["status"] == "done"
        assert len(body["candidates"]) == 2

        resp = client.get(f"/parts/jobs/{job_id}")
        assert resp.status_code == 200
        body = resp.json()
        assert body["status"] == "done"
        assert body["summary"] == "Two hangers. The first fits with 38 millimetres to spare."

        part_id = "amerimax-hidden-hanger-5k"
        for _ in range(50):
            resp = client.get(f"/parts/{part_id}/part.json")
            if resp.status_code == 200 and resp.json()["asset"]["status"] == "ready":
                break
            time.sleep(0.05)
        assert resp.json()["asset"]["status"] == "ready"

        resp = client.get(f"/parts/{part_id}/model.glb")
        assert resp.status_code == 200
        assert resp.headers["content-type"] == "model/gltf-binary"
        assert resp.content == b"glTF-fake-binary"

        resp = client.get(f"/parts/{part_id}/image.jpg")
        assert resp.status_code == 200
        assert resp.content.startswith(b"\xff\xd8\xff")


def test_unknown_job_404():
    with TestClient(app) as client:
        resp = client.get("/parts/jobs/does-not-exist")
        assert resp.status_code == 404


def test_part_id_traversal_rejected():
    with TestClient(app) as client:
        # single-segment ids that fail the ^[a-z0-9-]+$ guard -> 400 from our handler
        for bad_id in ("UPPER", "a_b", "a.b", "%2e%2e"):
            resp = client.get(f"/parts/{bad_id}/part.json")
            assert resp.status_code == 400, bad_id
        # ".." doesn't even match the route as a single path segment -> 404, never reaches disk
        resp = client.get("/parts/../part.json")
        assert resp.status_code == 404
        resp = client.get("/parts/unknown-part/part.json")
        assert resp.status_code == 404


# --- checkout + notebook -----------------------------------------------------


def test_checkout_ok_then_400_then_404():
    part = _canned_parts()[0]
    jobs.save_part(part)

    with TestClient(app) as client:
        resp = client.post(
            "/checkout",
            json={"part_id": part.id, "seller_idx": 0, "qty": 2, "session_id": "sess1"},
        )
        assert resp.status_code == 200
        receipt = resp.json()
        assert receipt["mode"] == "offline"  # no Cybersource keys in tests
        assert receipt["part_id"] == part.id

        notebook = client.get("/notebook/sess1").json()
        assert len(notebook["entries"]) == 1
        assert notebook["entries"][0]["receipt_id"] == receipt["receipt_id"]

        resp = client.post("/checkout", json={"part_id": part.id, "seller_idx": 99, "qty": 1})
        assert resp.status_code == 400

        resp = client.post("/checkout", json={"part_id": "no-such-part", "seller_idx": 0, "qty": 1})
        assert resp.status_code == 404


def test_checkout_invalid_part_id_400():
    with TestClient(app) as client:
        resp = client.post("/checkout", json={"part_id": "UPPER_bad", "seller_idx": 0, "qty": 1})
        assert resp.status_code == 400


# --- checkout: bom lines, same cart (plan §4b.6) ------------------------------


def _saved_bom(bom_id: str, part_id: str) -> bom.Bom:
    seller = Seller(name="Ace Hardware", price_usd=12.0, pack_qty=100, unit_price_usd=0.12)
    saved = bom.Bom(
        id=bom_id,
        part_ids=[part_id],
        lines=[bom.BomLine(idx=0, name="Gutter screws", qty=2, reason="fasten it", seller=seller)],
        total_usd=2.4,
    )
    bom._persist(saved)
    return saved


def test_checkout_with_bom_lines_adds_to_total_and_receipt():
    part = _canned_parts()[0]
    jobs.save_part(part)
    saved = _saved_bom("bom-checkout-1", part.id)

    with TestClient(app) as client:
        resp = client.post(
            "/checkout",
            json={
                "part_id": part.id,
                "seller_idx": 1,  # Home Depot, $9.99, free shipping
                "qty": 1,
                "bom_id": saved.id,
                "bom_lines": [0],
            },
        )
        assert resp.status_code == 200
        receipt = resp.json()
        assert receipt["total_usd"] == round(9.99 + 12.0 * 2, 2)
        assert receipt["bom_lines"] == [
            {
                "idx": 0,
                "name": "Gutter screws",
                "qty": 2,
                "seller": "Ace Hardware",
                "price_usd": 12.0,
                "total_usd": pytest.approx(24.0),
            }
        ]


def test_checkout_unknown_bom_line_idx_400():
    part = _canned_parts()[0]
    jobs.save_part(part)
    saved = _saved_bom("bom-checkout-2", part.id)

    with TestClient(app) as client:
        resp = client.post(
            "/checkout",
            json={
                "part_id": part.id,
                "seller_idx": 1,
                "qty": 1,
                "bom_id": saved.id,
                "bom_lines": [99],
            },
        )
        assert resp.status_code == 400


def test_checkout_unknown_bom_id_404():
    part = _canned_parts()[0]
    jobs.save_part(part)

    with TestClient(app) as client:
        resp = client.post(
            "/checkout",
            json={
                "part_id": part.id,
                "seller_idx": 1,
                "qty": 1,
                "bom_id": "bom-does-not-exist",
                "bom_lines": [0],
            },
        )
        assert resp.status_code == 404


def test_checkout_ignores_client_supplied_bom_price():
    """`CheckoutRequest.bom_lines` only ever carries indices -- there's no field for a client
    to smuggle a price through, so the receipt always prices from the saved Bom on disk."""
    part = _canned_parts()[0]
    jobs.save_part(part)
    saved = _saved_bom("bom-checkout-3", part.id)

    with TestClient(app) as client:
        resp = client.post(
            "/checkout",
            json={
                "part_id": part.id,
                "seller_idx": 1,
                "qty": 1,
                "bom_id": saved.id,
                "bom_lines": [0],
                "bom_lines_price_usd": 0.01,  # not a real field -- must be silently ignored
            },
        )
        assert resp.status_code == 200
        receipt = resp.json()
        assert receipt["bom_lines"][0]["price_usd"] == 12.0
        assert receipt["bom_lines"][0]["total_usd"] == pytest.approx(24.0)


# --- /parts/bom ("what else do I need?", plan §4b.6) --------------------------


def test_parts_bom_happy_path(monkeypatch):
    part = _canned_parts()[0]
    jobs.save_part(part)

    async def fake_what_else(parts, counts):
        assert [p.id for p in parts] == [part.id]
        assert counts == {part.id: 8}
        return bom.Bom(id="bom-endpoint-1", part_ids=[part.id], lines=[], total_usd=0.0)

    monkeypatch.setattr(app_module.bom, "what_else", fake_what_else)

    with TestClient(app) as client:
        resp = client.post("/parts/bom", json={"part_ids": [part.id], "counts": {part.id: 8}})
        assert resp.status_code == 200
        assert resp.json()["id"] == "bom-endpoint-1"


def test_parts_bom_invalid_part_id_400():
    with TestClient(app) as client:
        resp = client.post("/parts/bom", json={"part_ids": ["UPPER_bad"]})
        assert resp.status_code == 400


def test_parts_bom_unknown_part_404():
    with TestClient(app) as client:
        resp = client.post("/parts/bom", json={"part_ids": ["no-such-part"]})
        assert resp.status_code == 404


def test_notebook_round_trip():
    with TestClient(app) as client:
        client.post("/notebook", json={"session_id": "s1", "entries": [{"tool": "tape"}]})
        client.post("/notebook", json={"session_id": "s1", "entries": [{"tool": "level"}]})

        entries = client.get("/notebook/s1").json()["entries"]
        assert [e["tool"] for e in entries] == ["tape", "level"]

        assert client.get("/notebook/never-seen").json()["entries"] == []


# --- agent + voice ------------------------------------------------------


def test_agent_command_simple(monkeypatch):
    async def fake_handle_command(session_id, text, context):
        return {"reply": f"heard: {text}", "actions": [], "job_id": None}

    monkeypatch.setattr(agent, "handle_command", fake_handle_command)

    with TestClient(app) as client:
        resp = client.post("/agent/command", json={"session_id": "s1", "text": "find a hanger"})
        assert resp.status_code == 200
        body = resp.json()
        assert body["reply"] == "heard: find a hanger"
        assert body["job_id"] is None


def test_agent_command_waits_for_job(monkeypatch):
    _install_fake_search(monkeypatch)
    monkeypatch.setattr(jobs.assets, "resolve_asset", _fake_resolve_asset)

    async def fake_handle_command(session_id, text, context):
        job = jobs.start_search(SearchRequest(query=text))
        return {"reply": "searching", "actions": [], "job_id": job.id}

    monkeypatch.setattr(agent, "handle_command", fake_handle_command)

    with TestClient(app) as client:
        resp = client.post(
            "/agent/command",
            json={"session_id": "s1", "text": "find a hanger", "wait_s": 0.5},
        )
        assert resp.status_code == 200
        body = resp.json()
        assert body["job_id"]
        assert body["summary"].startswith("Two hangers")
        assert len(body["candidates"]) == 2


def test_voice_command_with_tts(monkeypatch):
    async def fake_transcribe(audio, filename="audio.wav", prompt=None):
        return "find a hanger"

    async def fake_handle_command(session_id, text, context):
        return {"reply": "Three hangers.", "actions": [], "job_id": None}

    async def fake_speak(text):
        return b"RIFFfake", "audio/wav"

    monkeypatch.setattr(app_module.voice, "transcribe", fake_transcribe)
    monkeypatch.setattr(app_module.voice, "speak", fake_speak)
    monkeypatch.setattr(agent, "handle_command", fake_handle_command)

    with TestClient(app) as client:
        resp = client.post(
            "/voice/command",
            data={"session_id": "s1", "tts": "true"},
            files={"audio": ("clip.wav", b"fake-audio-bytes", "audio/wav")},
        )
        assert resp.status_code == 200
        body = resp.json()
        assert body["transcript"] == "find a hanger"
        assert body["reply"] == "Three hangers."
        assert base64.b64decode(body["audio_b64"]) == b"RIFFfake"
        assert body["audio_mime"] == "audio/wav"
        assert "tts_error" not in body

        resp = client.post(
            "/voice/command",
            data={"session_id": "s1", "tts": "false"},
            files={"audio": ("clip.wav", b"fake-audio-bytes", "audio/wav")},
        )
        body = resp.json()
        assert body["audio_b64"] is None
        assert body["audio_mime"] is None


def test_voice_command_tts_failure(monkeypatch):
    async def fake_transcribe(audio, filename="audio.wav", prompt=None):
        return "find a hanger"

    async def fake_handle_command(session_id, text, context):
        return {"reply": "Three hangers.", "actions": [], "job_id": None}

    async def fake_speak_fail(text):
        raise app_module.voice.VoiceError("groq tts failed: 400 terms not accepted")

    monkeypatch.setattr(app_module.voice, "transcribe", fake_transcribe)
    monkeypatch.setattr(app_module.voice, "speak", fake_speak_fail)
    monkeypatch.setattr(agent, "handle_command", fake_handle_command)

    with TestClient(app) as client:
        resp = client.post(
            "/voice/command",
            data={"session_id": "s1", "tts": "true"},
            files={"audio": ("clip.wav", b"fake-audio-bytes", "audio/wav")},
        )
        assert resp.status_code == 200
        body = resp.json()
        assert body["audio_b64"] is None
        assert body["audio_mime"] is None
        assert "tts_error" in body


def test_voice_command_stt_offline_returns_503(monkeypatch):
    async def fake_transcribe_offline(audio, filename="audio.wav", prompt=None):
        raise app_module.voice.VoiceError("offline: speech-to-text unavailable")

    monkeypatch.setattr(app_module.voice, "transcribe", fake_transcribe_offline)

    with TestClient(app) as client:
        resp = client.post(
            "/voice/command",
            data={"session_id": "s1", "tts": "true"},
            files={"audio": ("clip.wav", b"fake-audio-bytes", "audio/wav")},
        )
        assert resp.status_code == 503


def test_voice_command_oversized_upload_413(monkeypatch):
    """Upload is read in capped chunks, so an oversized file is rejected before it's ever
    handed to voice.transcribe -- never fully buffered first."""
    monkeypatch.setattr(app_module.voice, "STT_MAX_BYTES", 10)

    with TestClient(app) as client:
        resp = client.post(
            "/voice/command",
            data={"session_id": "s1"},
            files={"audio": ("clip.wav", b"x" * 100, "audio/wav")},
        )
        assert resp.status_code == 413


def test_voice_command_empty_audio_maps_to_400():
    """voice.transcribe raises ValueError for empty audio -- must map to 400, not a bare 500."""
    with TestClient(app) as client:
        resp = client.post(
            "/voice/command",
            data={"session_id": "s1"},
            files={"audio": ("clip.wav", b"", "audio/wav")},
        )
        assert resp.status_code == 400


def test_voice_command_bad_context_json_400(monkeypatch):
    async def fake_transcribe(audio_bytes, filename="audio.wav"):
        return "hello"

    monkeypatch.setattr(app_module.voice, "transcribe", fake_transcribe)
    with TestClient(app) as client:
        resp = client.post(
            "/voice/command",
            data={"session_id": "s1", "context": "{not json"},
            files={"audio": ("clip.wav", b"RIFF", "audio/wav")},
        )
        assert resp.status_code == 400


# --- debug console --------------------------------------------------------


def test_debug_console_served():
    with TestClient(app) as client:
        resp = client.get("/debug")
        assert resp.status_code == 200
        assert "text/html" in resp.headers["content-type"]
        assert "model-viewer" in resp.text


def test_debug_html_script_parses(tmp_path):
    """Merges stack every feature's handler into one showCommandResult; a duplicate `const`
    there is a SyntaxError that blanks the whole console (it happened: F13's `shown`)."""
    node = shutil.which("node")
    if node is None:
        pytest.skip("node not installed")
    html = (Path(__file__).parent.parent / "server" / "static" / "debug.html").read_text()
    script = tmp_path / "debug.js"
    script.write_text("\n".join(re.findall(r"<script>(.*?)</script>", html, re.DOTALL)))
    check = subprocess.run(
        [node, "--check", str(script)], capture_output=True, text=True, check=False
    )
    assert check.returncode == 0, check.stderr


def test_debug_html_escapes_untrusted_interpolations():
    """XSS fix: part/seller names, eta, reasons, and urls are LLM/SerpApi text injected via
    template literals into innerHTML -- every one of them must be routed through esc()/safeUrl()
    (a static, no-browser-needed regression check for the raw `${...}` interpolations the
    vulnerability report named)."""
    html = (Path(__file__).parent.parent / "server" / "static" / "debug.html").read_text()
    assert "function esc(" in html
    assert "function safeUrl(" in html
    for needle in [
        "esc(part.name)",
        "esc(s.name",
        "esc(s.eta",
        "esc(s.shipping",
        "esc(part.recommendation_reason)",
        "esc(part.dims_source)",
        "safeUrl(s.url)",
        "safeUrl(part.image_url)",
    ]:
        assert needle in html, f"missing {needle!r}"
    # the raw (unescaped) interpolations named in the report must be gone
    assert "${part.name}" not in html
    assert "${s.url}" not in html
    assert 'href="${s.url}"' not in html


# --- sellers resort -------------------------------------------------------


def test_sellers_resort_cheapest():
    part = _canned_parts()[0]
    jobs.save_part(part)

    with TestClient(app) as client:
        resp = client.post(f"/parts/{part.id}/sellers", params={"sort": "cheapest"})
        assert resp.status_code == 200
        body = resp.json()
        assert [s["name"] for s in body["sellers"]] == ["Home Depot", "Ace Hardware"]
        assert body["recommended_seller"] == 0

        saved = jobs.load_part(part.id)
        assert saved.sellers[0].name == "Home Depot"

        resp = client.post("/parts/no-such-part/sellers", params={"sort": "cheapest"})
        assert resp.status_code == 404


def test_httpx_request_urls_not_logged():
    """SerpApi's api_key rides in the query string; httpx INFO logs would print it."""
    import logging

    assert logging.getLogger("httpx").getEffectiveLevel() >= logging.WARNING


# --- scene vision ----------------------------------------------------------


def _groq_429(retry_after: str | None):
    import httpx
    import openai

    headers = {"retry-after": retry_after} if retry_after is not None else {}
    request = httpx.Request("POST", "https://api.groq.com/openai/v1/chat/completions")
    return openai.RateLimitError(
        "Rate limit reached for model `qwen/qwen3.8-27b`: output tokens per minute (OTPM): "
        "Limit 1000, Used 0, Requested 1707.",
        response=httpx.Response(429, headers=headers, request=request),
        body=None,
    )


def test_scene_ask_rate_limited_is_503_with_retry_after(monkeypatch):
    """Integration finding #12: a Groq 429 reached the headset as HTTP 500."""

    async def rate_limited(question, frames):
        raise _groq_429("7.2")

    monkeypatch.setattr(app_module.vision, "ask_scene", rate_limited)

    with TestClient(app) as client:
        resp = client.post(
            "/scene/ask",
            json={"question": "What is this?", "frames": [{"id": "f0", "jpg_b64": "ZmFrZQ=="}]},
        )
    assert resp.status_code == 503
    assert resp.headers["retry-after"] == "8"  # rounded up to whole seconds
    assert "rate-limited" in resp.json()["detail"]


def test_agent_command_rate_limited_is_503_default_retry_after(monkeypatch):
    async def rate_limited(session_id, text, context):
        raise _groq_429(None)

    monkeypatch.setattr(agent, "handle_command", rate_limited)

    with TestClient(app) as client:
        resp = client.post("/agent/command", json={"session_id": "s1", "text": "what is this?"})
    assert resp.status_code == 503
    assert resp.headers["retry-after"] == "60"


def test_scene_ask_route_ok(monkeypatch):
    from server.vision import Frame, SceneAnswer

    async def fake_ask_scene(question, frames):
        assert question == "what is this?"
        assert frames == [Frame(id="f0", jpg_b64="ZmFrZQ==")]
        return SceneAnswer(answer="A gutter hanger.", frame_id="f0", box=[0.1, 0.1, 0.5, 0.5])

    monkeypatch.setattr(app_module.vision, "ask_scene", fake_ask_scene)

    with TestClient(app) as client:
        resp = client.post(
            "/scene/ask",
            json={"question": "what is this?", "frames": [{"id": "f0", "jpg_b64": "ZmFrZQ=="}]},
        )
        assert resp.status_code == 200
        body = resp.json()
        assert body == {
            "answer": "A gutter hanger.",
            "frame_id": "f0",
            "box": [0.1, 0.1, 0.5, 0.5],
            "part_query": None,
        }


def test_scene_ask_route_bad_input_400():
    with TestClient(app) as client:
        resp = client.post("/scene/ask", json={"question": "what is this?", "frames": []})
        assert resp.status_code == 400


def test_scene_ask_oversized_body_413(monkeypatch):
    """Body is read in capped chunks -- an oversized request never reaches Pydantic parsing."""
    monkeypatch.setattr(app_module, "_SCENE_ASK_MAX_BYTES", 10)

    with TestClient(app) as client:
        resp = client.post(
            "/scene/ask",
            json={"question": "what is this?", "frames": [{"id": "f0", "jpg_b64": "ZmFrZQ=="}]},
        )
        assert resp.status_code == 413


def test_scene_ask_offline_returns_503(monkeypatch):
    """OFFLINE means chat() raises cache.OfflineMiss -- the route must turn that into a 503,
    not let it fall through to an unhandled 500."""
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    try:
        with TestClient(app) as client:
            resp = client.post(
                "/scene/ask",
                json={"question": "what is this?", "frames": [{"id": "f0", "jpg_b64": "ZmFrZQ=="}]},
            )
        assert resp.status_code == 503
    finally:
        get_settings.cache_clear()
