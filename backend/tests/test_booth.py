import asyncio
import json
import time
from collections import OrderedDict
from types import SimpleNamespace

import httpx
import pytest
import respx
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, booth, jobs, report_demo
from server.app import app
from server.config import get_settings
from server.models import Dims, Part, Seller

SESSION = "demo-kitchen"
PART_ID = "karran-qu-670-bl-2d781c"
X = booth.X_API


@pytest.fixture
def seeded(tmp_path, monkeypatch):
    """F4's demo seed on a tmp DATA_DIR, a kitchen scene with a real thumb, no live caption."""
    scene = tmp_path / "scene" / "kitchen"
    (scene / "thumbs").mkdir(parents=True)
    for cam in ("0021", "0101"):
        Image.new("RGB", (320, 180), (120, 90, 60)).save(scene / "thumbs" / f"{cam}.jpg")
    (scene / "scene.json").write_text(json.dumps({"name": "kitchen", "scale_method": "none"}))
    monkeypatch.setattr(jobs, "_jobs", OrderedDict())
    monkeypatch.setattr(booth, "_PREVIEWS", {})
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    jobs.save_part(
        Part(
            id=PART_ID,
            name="Undermount Quartz Sink",
            dims_mm=Dims(w=806.45, d=488.95, h=228.6),
            sellers=[Seller(name="Ferguson Home", price_usd=359.95, shipping_usd=0.0)],
            recommended_seller=0,
        )
    )
    asyncio.run(report_demo.seed(SESSION, "kitchen", PART_ID))
    _fake_caption(monkeypatch, None)
    return tmp_path


def _fake_caption(monkeypatch, text: str | None, calls: list | None = None):
    async def fake(role, messages, **kw):
        if calls is not None:
            calls.append(role)
        if text is None:
            raise RuntimeError("no network in tests")
        return SimpleNamespace(
            usage=SimpleNamespace(cost_in_usd_ticks=4_000_000),
            choices=[SimpleNamespace(message=SimpleNamespace(content=text))],
        )

    monkeypatch.setattr(booth.llm, "chat", fake)


def _x_keys(monkeypatch):
    for var in booth.X_VARS:
        monkeypatch.setenv(var, f"test-{var.lower()}")
    get_settings.cache_clear()


# --- consent ---------------------------------------------------------------------------------


def test_share_needs_consent(seeded):
    client = TestClient(app)
    resp = client.post(f"/booth/{SESSION}/share", json={"name": "Maya"})
    assert resp.status_code == 403
    assert booth.load_entries() == []
    assert client.post("/booth/nobody/share", json={"consent": True}).status_code == 404

    body = client.post(f"/booth/{SESSION}/share", json={"consent": True}).json()
    assert body["entry"]["name"] == "Judge 1"
    assert body["caption_source"] == "template"
    card = client.get(body["card_url"])
    assert card.status_code == 200
    img = Image.open(seeded / "data" / "booth" / "cards" / f"{body['entry_id']}.jpg")
    assert img.size == (1200, 675)
    # A re-share replaces the session's entry; the wall lists newest first.
    again = client.post(f"/booth/{SESSION}/share", json={"consent": True, "name": "Maya"}).json()
    wall = client.get("/booth.json").json()
    assert [e["entry_id"] for e in wall["entries"]] == [again["entry_id"]]
    assert client.delete(f"/booth/{again['entry_id']}").json()["removed"] == again["entry_id"]
    assert booth.load_entries() == []


def test_agent_share_needs_the_phrase_and_never_posts(seeded):
    session = agent._get_session(SESSION)
    result, action, _ = asyncio.run(
        agent._run_tool(session, "share_design", {"phrase": "what's this cost?"}, {})
    )
    assert "error" in result and action is None
    assert booth.load_entries() == []

    out = asyncio.run(agent.handle_command(SESSION, "Add it to the wall as Maya", {}))
    (action,) = out["actions"]
    assert action["name"] == "show_share_preview"
    assert action["args"]["x_ready"] is False and action["args"]["confirm_token"] is None
    assert booth.load_entries()[0]["name"] == "Maya"
    assert "X posting isn't set up" in out["reply"]
    # "post it" shares (with X keys it would offer the ring); a voice turn never posts.
    out = asyncio.run(agent.handle_command(SESSION, "post it", {}))
    assert [a["name"] for a in out["actions"]] == ["show_share_preview"]


# --- leaderboard, timing ---------------------------------------------------------------------


def _entry(i: int, **kw) -> dict:
    return {
        "entry_id": f"{i:012x}",
        "name": f"J{i}",
        "created_at": f"2026-09-26T10:0{i}:00+00:00",
        "seconds_to_cart": None,
        "parts_placed": 0,
        "rebates_usd": None,
        **kw,
    }


def test_leaderboard_maths_and_ties():
    entries = [
        _entry(3, seconds_to_cart=95, parts_placed=4, rebates_usd=50.0),
        _entry(1, seconds_to_cart=120, parts_placed=4),
        _entry(2, seconds_to_cart=95, parts_placed=1, rebates_usd=500.0),
    ]
    board = booth.leaderboard(entries)
    rows = {k: [(r["name"], r["value"]) for r in b["rows"]] for k, b in board.items()}
    assert rows["fastest"] == [("J2", 95), ("J3", 95), ("J1", 120)]  # tie: earlier first
    assert rows["most_parts"] == [("J1", 4), ("J3", 4), ("J2", 1)]
    assert rows["rebate"] == [("J2", 500.0), ("J3", 50.0)]  # no rebate: not ranked


def test_seconds_to_cart():
    order = {"type": "order", "created_at": "2026-09-26T10:03:05+00:00"}
    assert booth.seconds_to_cart([{"type": "scan_loaded", "ts": "2026-09-26T10:00:00"}, order])
    assert booth.seconds_to_cart([{"ts": "2026-09-26T10:00:00Z"}, order]) == 185
    assert booth.seconds_to_cart([order]) is None  # nothing earlier than the order
    assert booth.seconds_to_cart([{"ts": "garbage"}, order]) is None


# --- caption, post text --------------------------------------------------------------------


def test_caption_with_an_unbacked_number_falls_back(seeded, monkeypatch):
    _fake_caption(monkeypatch, "Quartz sink for $299, a steal!")
    body = asyncio.run(booth.share(SESSION, consent=True))
    assert body["caption_source"] == "template"
    assert body["caption"] == booth.template(body["entry"])
    assert "$364.93" in body["caption"]  # 359.95 sink + 4.98 caulk, formatted as in the facts


def test_caption_backed_by_facts_is_used(seeded, monkeypatch):
    calls = []
    _fake_caption(monkeypatch, "A black quartz sink for the kitchen, $364.93 all in.", calls)
    body = asyncio.run(booth.share(SESSION, consent=True))
    assert body["caption"] == "A black quartz sink for the kitchen, $364.93 all in."
    assert body["caption_source"] == "grok-4.20-0309-non-reasoning"
    asyncio.run(booth.share(SESSION, consent=True))
    assert calls == ["booth"]  # the second share replays the cache


@pytest.mark.parametrize(
    "text", ["see https://x.ai", "go to www.airtool", "airtool.com has it", "thanks @someone"]
)
def test_links_and_mentions_are_rejected(text):
    lines = ["Estimate: $10.00"]
    assert not booth.clean(text, lines)
    entry = {"caption": f"Sink {text} $10.00", "part": None}
    post = booth.x_text(entry)
    assert not booth.LINK_RE.search(post)
    assert post.endswith(" #AirTool") and post.count("#AirTool") == 1 and len(post) <= 240
    with pytest.raises(booth.XError):
        asyncio.run(booth.x_post(__file__, text))  # type: ignore[arg-type]


# --- X ---------------------------------------------------------------------------------------


def test_missing_keys_says_x_not_configured(seeded):
    client = TestClient(app)
    body = client.post(f"/booth/{SESSION}/share", json={"consent": True}).json()
    assert body["x"] == {
        "ready": False,
        "error": "X not configured",
        "needs": ["X_CLIENT_ID", "X_CLIENT_SECRET", "X_REFRESH_TOKEN"],
    }
    resp = client.post("/booth/x/confirm", json={"confirm_token": "anything"})
    assert resp.status_code == 503 and "X not configured" in resp.json()["detail"]
    assert client.get("/booth/x/login", follow_redirects=False).status_code == 503
    assert client.get("/booth").status_code == 200  # the wall works without X


@respx.mock
def test_x_post_order_and_shapes(seeded, monkeypatch):
    _x_keys(monkeypatch)
    token = respx.post(f"{X}/oauth2/token").mock(
        return_value=httpx.Response(200, json={"access_token": "at", "refresh_token": "rt2"})
    )
    init = respx.post(f"{X}/media/upload/initialize").mock(
        return_value=httpx.Response(200, json={"data": {"id": "m1", "media_key": "3_m1"}})
    )
    append = respx.post(f"{X}/media/upload/m1/append").mock(return_value=httpx.Response(204))
    final = respx.post(f"{X}/media/upload/m1/finalize").mock(
        return_value=httpx.Response(200, json={"data": {"id": "m1"}})
    )
    tweet = respx.post(f"{X}/tweets").mock(
        return_value=httpx.Response(201, json={"data": {"id": "t9", "text": "x"}})
    )
    client = TestClient(app)
    share = client.post(f"/booth/{SESSION}/share", json={"consent": True}).json()
    assert share["x"]["ready"] is True
    confirm = share["x"]["confirm_token"]

    resp = client.post("/booth/x/confirm", json={"confirm_token": confirm})
    assert resp.json() == {"tweet_id": "t9", "url": "https://x.com/i/web/status/t9"}
    assert [c.request.url.path for c in respx.calls] == [
        "/2/oauth2/token",
        "/2/media/upload/initialize",
        "/2/media/upload/m1/append",
        "/2/media/upload/m1/finalize",
        "/2/tweets",
    ]
    assert b"grant_type=refresh_token" in token.calls[0].request.content
    assert token.calls[0].request.headers["authorization"].startswith("Basic ")
    card = (seeded / "data" / "booth" / "cards" / f"{share['entry_id']}.jpg").read_bytes()
    assert json.loads(init.calls[0].request.content) == {
        "media_type": "image/jpeg",
        "total_bytes": len(card),
        "media_category": "tweet_image",
    }
    assert b'name="segment_index"' in append.calls[0].request.content
    assert card in append.calls[0].request.content
    assert final.calls[0].request.headers["authorization"] == "Bearer at"
    posted = json.loads(tweet.calls[0].request.content)
    assert posted == {"text": share["x"]["preview_text"], "media": {"media_ids": ["m1"]}}
    assert not booth.LINK_RE.search(posted["text"])
    assert booth._refresh_token() == "rt2"  # rotated token kept
    assert booth.load_entries()[0]["tweet_id"] == "t9"

    again = client.post("/booth/x/confirm", json={"confirm_token": confirm})
    assert again.status_code == 409  # one use
    assert len(tweet.calls) == 1


@respx.mock
def test_x_error_title_passes_through(seeded, monkeypatch):
    _x_keys(monkeypatch)
    respx.post(f"{X}/oauth2/token").mock(
        return_value=httpx.Response(400, json={"error": "invalid_request", "title": "Bad token"})
    )
    client = TestClient(app)
    share = client.post(f"/booth/{SESSION}/share", json={"consent": True}).json()
    resp = client.post("/booth/x/confirm", json={"confirm_token": share["x"]["confirm_token"]})
    assert resp.status_code == 502 and "Bad token" in resp.json()["detail"]


def test_expired_confirm_token(seeded, monkeypatch):
    _x_keys(monkeypatch)
    client = TestClient(app)
    share = client.post(f"/booth/{SESSION}/share", json={"consent": True}).json()
    token = share["x"]["confirm_token"]
    booth._PREVIEWS[token]["expires"] = time.monotonic() - 1
    with respx.mock:  # nothing may be called
        resp = client.post("/booth/x/confirm", json={"confirm_token": token})
    assert resp.status_code == 409 and "expired" in resp.json()["detail"]


def test_login_redirects_with_pkce(seeded, monkeypatch):
    _x_keys(monkeypatch)
    resp = TestClient(app).get("/booth/x/login", follow_redirects=False)
    assert resp.status_code == 307
    loc = resp.headers["location"]
    assert loc.startswith(booth.X_AUTHORIZE) and "code_challenge_method=S256" in loc
    assert "offline.access" in loc.replace("+", " ").replace("%20", " ")


# --- screen ----------------------------------------------------------------------------------


def test_booth_html_escapes_user_text(seeded):
    client = TestClient(app)
    evil = "<script>alert(1)</script>"
    client.post(f"/booth/{SESSION}/share", json={"consent": True, "name": evil})
    page = client.get("/booth").text
    assert "<script>" not in page
    assert "&lt;script&gt;alert(1)&lt;/script&gt;" in page
    assert "http-equiv=refresh" in page
