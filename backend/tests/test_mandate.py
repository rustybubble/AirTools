"""Measured mandate (brain.md S2 / B3): voice can only narrow the limits, /checkout/prepare prices
the cart and issues a single-use hold nonce, and only a >= 1 s hold with that nonce pays. The
receipt carries intent -> cart (hash) -> authorization and the evidence. Cybersource is respx."""

import json
from datetime import UTC, date, datetime, timedelta

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

import server.app as app_module
from server import agent, jobs, mandate
from server.app import app
from server.checkout import CYBERSOURCE_HOST, CYBERSOURCE_PATH
from server.config import get_settings
from server.models import Dims, Fit, Job, Part, Seller
from tests.test_agent import _no_llm_chat, _scripted_chat, _tool_calls_completion
from tests.test_checkout import CS_ENV

TODAY = date(2026, 9, 26)  # a Saturday: "by Friday" is Fri Oct 2
SESSION = "quest-1a2b3c4d5e"
EVIDENCE = [
    {
        "notebook_id": 3,
        "label": "tape #3",
        "value_m": 4.2,
        "camera_id": 316,
        "photo": "/scenes/facade/thumbs/0316.jpg",
        "array": {"spacing_mm": 600, "count": 8},
    }
]


def _hanger() -> Part:
    return Part(
        id="gutter-hanger-hidden-k-style",
        name="Hidden K-style gutter hanger",
        dims_mm=Dims(w=127, d=38, h=45),
        sellers=[
            Seller(name="Home Depot", price_usd=2.98, shipping_usd=0.0, eta_days=5, verified=True),
            Seller(name="Bulk Supply", price_usd=39.0, pack_qty=50, shipping_usd=4.5, eta_days=10),
            Seller(name="Mystery Shop", price_usd=3.10),  # no ETA, read by the model
        ],
        fit=Fit(status="fits", spare_mm=38, axis="w"),
    )


@pytest.fixture(autouse=True)
def _env(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    for key in CS_ENV:
        monkeypatch.delenv(key, raising=False)
    get_settings.cache_clear()
    monkeypatch.setattr(mandate, "_today", lambda: TODAY)
    mandate._nonces.clear()
    agent._sessions.clear()
    jobs.save_part(_hanger())
    yield tmp_path
    mandate._nonces.clear()
    agent._sessions.clear()
    get_settings.cache_clear()


@pytest.fixture
def client():
    with TestClient(app) as c:
        yield c


def _limits(client, **body):
    resp = client.post("/commerce/limits", json={"session_id": SESSION, **body})
    assert resp.status_code == 200, resp.text
    return resp.json()


def _prepare(client, units=8, seller_idx=0, **extra):
    body = {
        "session_id": SESSION,
        "part_id": "gutter-hanger-hidden-k-style",
        "seller_idx": seller_idx,
        "units_needed": units,
        "evidence": EVIDENCE,
        **extra,
    }
    resp = client.post("/checkout/prepare", json=body)
    assert resp.status_code == 200, resp.text
    return resp.json()


def _pay(client, prepared, hold_ms=1043, **overrides):
    line = prepared["cart"]["lines"][0]
    body = {
        "part_id": line["part_id"],
        "seller_idx": line["seller_idx"],
        "qty": line["packs"],
        "session_id": SESSION,
        "cart_hash": prepared["cart_hash"],
        "hold_nonce": prepared["hold_nonce"],
        "hold_ms": hold_ms,
        **overrides,
    }
    return client.post("/checkout", json=body)


# --- limits: voice narrows, the panel may loosen ---------------------------------------------


def test_voice_can_only_tighten():
    intent, refused, changed = mandate.set_limits(
        SESSION, source="voice", text="under 40 dollars", max_total_usd=40, deliver_by="friday"
    )
    assert (intent.max_total_usd, intent.deliver_by) == (40, date(2026, 10, 2))
    assert refused == [] and changed == ["max_total_usd", "deliver_by"]

    intent, refused, changed = mandate.set_limits(
        SESSION,
        source="voice",
        text="under $100 by next week",
        max_total_usd=100,
        deliver_by="2026-10-09",
    )
    assert (intent.max_total_usd, intent.deliver_by) == (40, date(2026, 10, 2))
    assert changed == []
    assert [r.model_dump() for r in refused] == [
        {
            "field": "max_total_usd",
            "asked": 100.0,
            "kept": 40.0,
            "why": "voice can only tighten a limit; change it on the panel",
        },
        {
            "field": "deliver_by",
            "asked": "2026-10-09",
            "kept": "2026-10-02",
            "why": "voice can only tighten a limit; change it on the panel",
        },
    ]

    intent, refused, changed = mandate.set_limits(
        SESSION, source="voice", text="under 30", max_total_usd=30, seller_policy="fastest"
    )
    assert (intent.max_total_usd, intent.seller_policy, changed) == (
        30,
        "fastest",
        ["max_total_usd", "seller_policy"],
    )
    assert intent.text == "under 30" and intent.source == "voice"
    assert [h["to"] for h in intent.history] == [40.0, "2026-10-02", 30.0, "fastest"]


def test_panel_may_raise_or_clear_and_voice_cannot_clear():
    mandate.set_limits(SESSION, source="voice", max_total_usd=40)
    intent, refused, _ = mandate.set_limits(SESSION, source="voice", max_total_usd=None)
    assert intent.max_total_usd == 40 and refused == []  # a voice "no limit" is ignored
    intent, _, changed = mandate.set_limits(SESSION, source="panel", max_total_usd=100)
    assert intent.max_total_usd == 100 and changed == ["max_total_usd"]
    intent, _, _ = mandate.set_limits(SESSION, source="panel", max_total_usd=None)
    assert intent.max_total_usd is None and intent.source == "panel"


@pytest.mark.parametrize(
    "word,expected",
    [
        ("friday", date(2026, 10, 2)),
        ("saturday", TODAY),
        ("sunday", date(2026, 9, 27)),
        ("tomorrow", date(2026, 9, 27)),
        ("today", TODAY),
        ("2026-10-05", date(2026, 10, 5)),
    ],
)
def test_parse_deliver_by(word, expected):
    assert mandate.parse_deliver_by(word, TODAY) == expected


def test_bad_limits_are_refused_with_400(client):
    assert mandate.parse_deliver_by(None) is None
    for body in ({"max_total_usd": -5}, {"deliver_by": "someday"}):
        resp = client.post("/commerce/limits", json={"session_id": SESSION, **body})
        assert resp.status_code == 400
    resp = client.post("/commerce/limits", json={"session_id": SESSION, "seller_policy": "cheap"})
    assert resp.status_code == 422


def test_limits_route_changes_only_fields_present(client):
    first = _limits(client, max_total_usd=40, deliver_by="friday", text="under forty, by Friday")
    assert first == {
        "intent": {
            "id": first["intent"]["id"],
            "max_total_usd": 40.0,
            "deliver_by": "2026-10-02",
            "seller_policy": None,
            "source": "voice",
            "text": "under forty, by Friday",
        },
        "refused": [],
        "changed": ["max_total_usd", "deliver_by"],
    }
    second = _limits(client, seller_policy="cheapest")
    assert second["intent"]["id"] == first["intent"]["id"]
    assert (
        second["intent"]["max_total_usd"] == 40 and second["intent"]["deliver_by"] == "2026-10-02"
    )
    raised = _limits(client, max_total_usd=100)
    assert raised["intent"]["max_total_usd"] == 40 and raised["refused"][0]["asked"] == 100
    cleared = _limits(client, source="panel", deliver_by=None)
    assert cleared["intent"]["deliver_by"] is None and cleared["changed"] == ["deliver_by"]


def test_expired_intent_starts_over(monkeypatch):
    old, _, _ = mandate.set_limits(SESSION, source="voice", max_total_usd=40)
    later = datetime.now(UTC) + timedelta(hours=25)
    assert mandate.current_intent(SESSION, later) is None
    fresh, refused, _ = mandate.set_limits(SESSION, source="voice", max_total_usd=100, now=later)
    assert fresh.id != old.id and fresh.max_total_usd == 100 and refused == []


# --- prepare: priced cart, checks, nonce ------------------------------------------------------


def test_prepare_prices_the_cart_and_lists_the_checks(client):
    intent = _limits(client, max_total_usd=40, deliver_by="friday")["intent"]
    prepared = _prepare(client)

    cart = prepared["cart"]
    assert cart["intent_id"] == intent["id"]
    assert cart["lines"] == [
        {
            "part_id": "gutter-hanger-hidden-k-style",
            "seller_idx": 0,
            "seller": "Home Depot",
            "units_needed": 8,
            "pack_qty": 1,
            "packs": 8,
            "unit_price_usd": 2.98,
            "shipping_usd": 0.0,
            "line_total_usd": 23.84,
        }
    ]
    assert cart["total_usd"] == 23.84 and cart["evidence"][0]["array"]["count"] == 8
    assert prepared["cart_hash"].startswith("sha256:") and len(prepared["cart_hash"]) == 71
    saved = mandate.load_cart(cart["id"])
    assert mandate.cart_hash(saved) == prepared["cart_hash"] == saved.hash
    assert prepared["hold_nonce"].startswith("hn_")
    expires = datetime.fromisoformat(prepared["nonce_expires_at"])
    assert timedelta(seconds=115) < expires - datetime.now(UTC) <= timedelta(seconds=120)
    assert prepared["intent"]["max_total_usd"] == 40
    assert prepared["all_ok"] is True
    assert [(c["id"], c["status"], c["detail"]) for c in prepared["checks"]] == [
        ("price_reread", "ok", "$2.98 × 8 from the saved listing"),
        ("within_limit", "ok", "$23.84 ≤ $40"),
        ("delivery", "ok", "arrives Thu Oct 1, by Fri Oct 2"),
        ("qty_evidence", "ok", "tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)"),
        ("seller_verified", "ok", "Home Depot: structured seller listing"),
        ("fit", "ok", "green, 38 mm spare"),
        ("card", "ok", "Visa test card •••• 1111, held by the server"),
    ]


def test_prepare_turns_units_into_packs_and_warns_honestly(client):
    prepared = _prepare(client, units=8, seller_idx=1, evidence=[])
    line = prepared["cart"]["lines"][0]
    assert (line["pack_qty"], line["packs"], line["line_total_usd"]) == (50, 1, 43.5)
    checks = {c["id"]: c for c in prepared["checks"]}
    assert checks["qty_evidence"]["status"] == "warn"
    assert checks["seller_verified"]["status"] == "warn"
    assert checks["delivery"] == {
        "id": "delivery",
        "status": "ok",
        "ok": True,
        "detail": "arrives Tue Oct 6; no deadline set",
    }
    assert prepared["all_ok"] is True  # warnings don't block the hold


def test_prepare_over_the_limit_or_late_is_not_ok(client):
    _limits(client, max_total_usd=20, deliver_by="tomorrow")
    prepared = _prepare(client)
    failed = {c["id"]: c["detail"] for c in prepared["checks"] if c["status"] == "fail"}
    assert failed == {
        "within_limit": "$23.84 is over your $20 limit",
        "delivery": "arrives Thu Oct 1, after your Sun Sep 27 deadline",
    }
    assert prepared["all_ok"] is False

    no_eta = _prepare(client, units=2, seller_idx=2)
    delivery = next(c for c in no_eta["checks"] if c["id"] == "delivery")
    assert delivery["status"] == "warn"
    assert delivery["detail"] == "Mystery Shop gave no delivery estimate; you asked for Sun Sep 27"


def test_prepare_bad_input(client):
    body = {"session_id": SESSION, "part_id": "gutter-hanger-hidden-k-style", "seller_idx": 0}
    assert client.post("/checkout/prepare", json={**body, "units_needed": 0}).status_code == 400
    assert client.post("/checkout/prepare", json={**body, "units_needed": 501}).status_code == 400
    assert (
        client.post(
            "/checkout/prepare", json={**body, "seller_idx": 9, "units_needed": 1}
        ).status_code
        == 400
    )
    missing = {**body, "part_id": "no-such-part", "units_needed": 1}
    assert client.post("/checkout/prepare", json=missing).status_code == 404


# --- the hold pays, once ------------------------------------------------------------------


def test_hold_pays_once_and_the_receipt_carries_the_chain(client, tmp_path):
    intent = _limits(client, max_total_usd=40, deliver_by="friday", text="under $40, by Friday")
    prepared = _prepare(client)

    resp = _pay(client, prepared)
    assert resp.status_code == 200, resp.text
    receipt = resp.json()
    assert receipt["status"] == "OFFLINE_RECEIPT" and receipt["qty"] == 8
    assert receipt["total_usd"] == prepared["cart"]["total_usd"] == 23.84
    chain = receipt["mandate"]
    assert chain["intent"] == intent["intent"]
    assert chain["cart"] == {
        "id": prepared["cart"]["id"],
        "hash": prepared["cart_hash"],
        "total_usd": 23.84,
        "units_needed": 8,
        "packs": 8,
    }
    assert chain["authorization"] == {
        "mode": "offline",
        "status": "OFFLINE_RECEIPT",
        "approval_code": None,
        "hold_ms": 1043,
    }
    assert chain["evidence"] == EVIDENCE
    assert all(c["ok"] for c in chain["checks"])

    saved = json.loads((tmp_path / "orders" / f"{receipt['receipt_id']}.json").read_text())
    assert saved["mandate"]["cart"]["hash"] == prepared["cart_hash"]
    assert mandate.load_cart(prepared["cart"]["id"]).receipt_id == receipt["receipt_id"]
    notebook = client.get(f"/notebook/{SESSION}").json()["entries"]
    assert notebook[-1]["mandate"]["authorization"]["hold_ms"] == 1043

    again = _pay(client, prepared)
    assert again.status_code == 409 and "already used" in again.json()["detail"]


def test_sandbox_authorization_carries_the_mandate(client, monkeypatch):
    for key, value in CS_ENV.items():
        monkeypatch.setenv(key, value)
    get_settings.cache_clear()
    intent_id = _limits(client, max_total_usd=40)["intent"]["id"]
    prepared = _prepare(client)
    approval = {"status": "AUTHORIZED", "processorInformation": {"approvalCode": "831000"}}
    url = f"https://{CYBERSOURCE_HOST}{CYBERSOURCE_PATH}"

    with respx.mock(assert_all_called=True) as router:
        route = router.post(url).mock(return_value=httpx.Response(201, json=approval))
        resp = _pay(client, prepared, hold_ms=1000)

    receipt = resp.json()
    assert receipt["mandate"]["authorization"] == {
        "mode": "sandbox",
        "status": "AUTHORIZED",
        "approval_code": "831000",
        "hold_ms": 1000,
    }
    sent = json.loads(route.calls.last.request.content)
    code = sent["clientReferenceInformation"]["code"]
    assert code == "at-" + prepared["cart_hash"].removeprefix("sha256:")[:47] and len(code) == 50
    assert sent["merchantDefinedInformation"] == [
        {"key": "1", "value": intent_id},
        {"key": "2", "value": "tape#3 4.20m->8@600mm"},
        {"key": "3", "value": SESSION},
    ]
    assert sent["orderInformation"]["amountDetails"]["totalAmount"] == "23.84"


def test_short_hold_is_400_and_keeps_the_nonce(client):
    prepared = _prepare(client)
    short = _pay(client, prepared, hold_ms=600)
    assert short.status_code == 400 and "hold_ms 600" in short.json()["detail"]
    assert _pay(client, prepared, hold_ms=1000).status_code == 200


@pytest.mark.parametrize(
    "drop",
    [("cart_hash",), ("hold_nonce",), ("hold_ms",), ("session_id",), ("cart_hash", "hold_nonce")],
)
def test_partial_proof_is_400(client, drop):
    prepared = _prepare(client)
    resp = _pay(client, prepared, **{k: None for k in drop})
    assert resp.status_code == 400 and "hold proof required" in resp.json()["detail"]


@pytest.mark.parametrize(
    "overrides",
    [
        {"qty": 9},  # tampered after prepare
        {"seller_idx": 2, "qty": 8},
        {"cart_hash": "sha256:" + "0" * 64},
        {"session_id": "quest-someone-else"},
        {"hold_nonce": "hn_made-up"},
    ],
)
def test_tampered_or_foreign_proof_is_409(client, overrides):
    prepared = _prepare(client)
    assert _pay(client, prepared, **overrides).status_code == 409


def test_price_change_after_prepare_is_409(client):
    prepared = _prepare(client)
    part = _hanger()
    part.sellers[0].price_usd = 3.49
    jobs.save_part(part)
    resp = _pay(client, prepared)
    assert resp.status_code == 409 and "price or listing changed" in resp.json()["detail"]


def test_over_the_limit_is_422_with_the_checks(client):
    _limits(client, max_total_usd=20)
    prepared = _prepare(client)
    resp = _pay(client, prepared)
    assert resp.status_code == 422
    detail = resp.json()["detail"]
    assert detail["error"] == "a mandate check failed"
    assert {c["id"]: c["status"] for c in detail["checks"]}["within_limit"] == "fail"


def test_limit_tightened_by_voice_after_prepare_is_422(client):
    _limits(client, max_total_usd=40)
    prepared = _prepare(client)
    _limits(client, max_total_usd=20, text="actually keep it under $20")
    assert _pay(client, prepared).status_code == 422


def test_nonce_expires_after_two_minutes(client, monkeypatch):
    prepared = _prepare(client)
    later = datetime.now(UTC) + timedelta(seconds=121)
    monkeypatch.setattr(mandate, "_now", lambda: later)
    resp = _pay(client, prepared)
    assert resp.status_code == 409 and "expired" in resp.json()["detail"]


def test_a_new_prepare_revokes_the_old_nonce(client):
    first = _prepare(client, units=8)
    second = _prepare(client, units=10)  # the user tapped + on the panel
    assert _pay(client, first).status_code == 409
    resp = _pay(client, second)
    assert resp.status_code == 200 and resp.json()["qty"] == 10


def test_bom_lines_ride_in_the_cart_and_cannot_be_added_later(client):
    from tests.test_app import _saved_bom

    _saved_bom("bom-a1b2c3d4e5f6", "gutter-hanger-hidden-k-style")  # 2 x $12 screws
    with_bom = _prepare(client, bom_id="bom-a1b2c3d4e5f6", bom_lines=[0])
    assert with_bom["cart"]["total_usd"] == round(23.84 + 24.0, 2)
    ok = _pay(client, with_bom, bom_id="bom-a1b2c3d4e5f6", bom_lines=[0])
    assert ok.status_code == 200 and ok.json()["total_usd"] == 47.84

    plain = _prepare(client)
    late = _pay(client, plain, bom_id="bom-a1b2c3d4e5f6", bom_lines=[0])
    assert late.status_code == 409


def test_proof_receipt_keeps_the_grok_fields(client, monkeypatch):
    """integration/grok-all: F6's safety verdict, F5's postcard and the notebook's bom_id stay on
    a receipt that also carries the mandate; F9's manual prefetch still runs."""
    from tests.test_app import _saved_bom

    prefetched = []
    monkeypatch.setattr(app_module.manuals, "prefetch", lambda part: prefetched.append(part.id))
    monkeypatch.setattr(
        app_module.checkout.postcard, "postcard_url", lambda pid: f"/parts/{pid}/postcard.jpg?v=1"
    )
    _saved_bom("bom-a1b2c3d4e5f6", "gutter-hanger-hidden-k-style")
    prepared = _prepare(client, bom_id="bom-a1b2c3d4e5f6", bom_lines=[0])
    resp = _pay(client, prepared, bom_id="bom-a1b2c3d4e5f6", bom_lines=[0])

    assert resp.status_code == 200, resp.text
    receipt = resp.json()
    assert receipt["safety_verdict"] == "unknown"  # never checked: cache only
    assert receipt["postcard_url"] == "/parts/gutter-hanger-hidden-k-style/postcard.jpg?v=1"
    assert receipt["mandate"]["cart"]["hash"] == prepared["cart_hash"]
    entry = client.get(f"/notebook/{SESSION}").json()["entries"][-1]
    assert entry["bom_id"] == "bom-a1b2c3d4e5f6" and entry["mandate"]["cart"]["packs"] == 8
    assert prefetched == ["gutter-hanger-hidden-k-style"]


def test_legacy_checkout_without_proof(client, monkeypatch):
    body = {"part_id": "gutter-hanger-hidden-k-style", "seller_idx": 0, "qty": 2}
    legacy = client.post("/checkout", json=body)
    assert legacy.status_code == 200 and "mandate" not in legacy.json()

    monkeypatch.setenv("REQUIRE_HOLD_PROOF", "true")
    get_settings.cache_clear()
    refused = client.post("/checkout", json={**body, "session_id": SESSION})
    assert refused.status_code == 400 and "hold proof required" in refused.json()["detail"]


def test_evidence_summary_is_short_ascii():
    ev = [mandate.Evidence(**EVIDENCE[0])]
    assert mandate.evidence_summary(ev) == "tape#3 4.20m->8@600mm"
    assert mandate.evidence_summary([]) == "no measurement evidence"
    long = [mandate.Evidence(label="ruban n° 3 " + "x" * 60, value_m=1.0)]
    summary = mandate.evidence_summary(long)
    assert len(summary) == 64 and summary.isascii()


# --- the agent: limits by voice --------------------------------------------------------------


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "text,limits",
    [
        ("under $40", {"max_total_usd": 40.0}),
        ("keep it under 40 dollars", {"max_total_usd": 40.0}),
        ("no more than $37.50 total", {"max_total_usd": 37.5}),
        ("it has to arrive by Friday", {"deliver_by": "2026-10-02"}),
        ("delivered before Friday", {"deliver_by": "2026-10-01"}),
        ("up to 40 bucks, here by tomorrow", {"max_total_usd": 40.0, "deliver_by": "2026-09-27"}),
    ],
)
async def test_limits_fast_path_never_calls_the_llm(monkeypatch, text, limits):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command(SESSION, text, {})
    [action] = result["actions"]
    assert action["name"] == "show_limits"
    got = {k: action["args"][k] for k in ("max_total_usd", "deliver_by") if action["args"][k]}
    assert got == limits
    assert result["reply"].startswith("Limits set: ")


@pytest.mark.parametrize(
    "text",
    [
        "find a hanger under 40 mm wide",
        "measure up to 40 cm",
        "every 60 cm",
        "buy it",
        "find hinges under $5 each",
        "pulls below 3 dollars apiece",
        "anything under $2 per foot",
    ],
)
def test_sizes_are_not_limits(text):
    assert agent._extract_limits(text)[0] == {}


@pytest.mark.asyncio
async def test_find_with_limits_sets_them_then_searches(monkeypatch):
    seen = []
    scripted = _scripted_chat(_tool_calls_completion(("find_part", {"query": "gutter hanger"})))

    async def fake(role, messages, tools=None, **kw):
        seen.append(messages[-1]["content"])
        return await scripted(role, messages, tools, **kw)

    monkeypatch.setattr(agent, "chat", fake)
    monkeypatch.setattr(
        jobs, "start_search", lambda req: Job(id="job9", status="running", query=req.query)
    )

    result = await agent.handle_command(SESSION, "Find hangers, under $40, arriving by Friday.", {})

    assert seen == ["Find hangers"]  # the LLM routes only what's left
    assert result["reply"] == "Limits set: under $40, by Fri Oct 2. Searching the supply shops."
    assert [a["name"] for a in result["actions"]] == ["show_limits", "search_started"]
    assert result["job_id"] == "job9"
    intent = mandate.current_intent(SESSION)
    assert intent.text == "Find hangers, under $40, arriving by Friday."


@pytest.mark.asyncio
async def test_voice_cannot_raise_a_limit(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    await agent.handle_command(SESSION, "under $40, arriving by Friday", {})
    result = await agent.handle_command(SESSION, "actually, up to $100", {})
    assert (
        result["reply"] == "You'd need to raise that on the panel. Still under $40, by Fri Oct 2."
    )
    [action] = result["actions"]
    assert action["args"]["max_total_usd"] == 40
    assert action["args"]["refused"][0]["asked"] == 100


@pytest.mark.asyncio
async def test_llm_set_limits_survives_a_find_part_turn(monkeypatch):
    monkeypatch.setattr(
        jobs, "start_search", lambda req: Job(id="job7", status="running", query=req.query)
    )
    completion = _tool_calls_completion(
        ("find_part", {"query": "gutter hanger"}),
        ("set_limits", {"max_total_usd": 40, "deliver_by": "friday", "seller_policy": "fastest"}),
    )
    monkeypatch.setattr(agent, "chat", _scripted_chat(completion))

    result = await agent.handle_command(SESSION, "find hangers, forty bucks tops, and quick", {})

    assert [a["name"] for a in result["actions"]] == ["search_started", "show_limits"]
    assert result["actions"][1]["args"] == {
        "intent_id": mandate.current_intent(SESSION).id,
        "max_total_usd": 40.0,
        "deliver_by": "2026-10-02",
        "seller_policy": "fastest",
        "refused": [],
    }


def test_set_limits_schema():
    tools = {t["function"]["name"]: t["function"] for t in agent.TOOLS}
    params = tools["set_limits"]["parameters"]
    assert params["required"] == ["max_total_usd", "deliver_by", "seller_policy"]
    assert params["properties"]["seller_policy"]["enum"] == ["cheapest", "fastest", "best", None]
    assert "Never pays" in tools["set_limits"]["description"]


# --- integration/grok-all: limits next to the Grok fast paths ---------------------------------


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("text", "tool"),
    [
        ("any rebates up to $500 on a mini-split?", "check_rules"),  # F13
        ("check this quote, is it under $2000?", "check_quote"),  # F20
    ],
)
async def test_money_in_a_rules_or_quote_command_is_not_a_limit(monkeypatch, text, tool):
    ran = []

    async def fake_run_tool(session, name, args, ctx):
        ran.append(name)
        return {"spoken": "Checked."}, None, None

    monkeypatch.setattr(agent, "_run_tool", fake_run_tool)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command(SESSION, text, {})
    assert ran == [tool] and result["reply"] == "Checked."
    assert mandate.current_intent(SESSION) is None


@pytest.mark.asyncio
async def test_limits_then_a_grok_fast_path(monkeypatch):
    """The rest of the command still takes the Grok routes: limits, then "do the whole job"."""
    ran = []

    async def fake_run_job(session, args, context):
        ran.append(args)
        return {"spoken": "On it."}, [{"name": "job_started", "args": {}}], None

    real = agent._run_tool

    async def run_tool(session, name, args, ctx):
        if name == "run_job":
            return await fake_run_job(session, args, ctx)
        return await real(session, name, args, ctx)

    monkeypatch.setattr(agent, "_run_tool", run_tool)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command(SESSION, "do the whole job, under $500", {})
    assert [a["name"] for a in result["actions"]] == ["show_limits", "job_started"]
    assert result["reply"] == "Limits set: under $500. On it."
    assert ran == [{"goal": None}]
