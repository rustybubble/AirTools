"""Recall and defect radar: CPSC model/brand matching, the Grok honesty filter, verdict rules,
markup stripping, caching/OFFLINE, the endpoint, the agent tool, checkout. CPSC is respx-mocked
with a trimmed copy of the real Midea response (record #25320 lists MAW08U1QWT), `/v1/responses`
with a fixture trimmed from the live Midea call (2026-09-26); tests swap the message text."""

import asyncio
import json
from datetime import UTC, date, datetime
from pathlib import Path

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from server import agent, jobs, llm, safety
from server.app import app
from server.config import get_settings
from server.models import Dims, Part, Seller

FIXTURES = Path(__file__).parent / "fixtures"
CPSC = json.loads((FIXTURES / "cpsc_recalls_midea.json").read_text())
CPSC_URL = CPSC[0]["URL"]  # cpsc.gov page of recall #25320

MIDEA = Part(
    id="midea-maw08u1qwt-8d6d08",
    name="8,000 BTU U+ Smart Inverter Window Air Conditioner",
    manufacturer="Midea",
    model_no="MAW08U1QWT",
    dims_mm=Dims(w=470, d=560, h=350),
    sellers=[Seller(name="Home Depot", price_usd=399.0)],
)


@pytest.fixture(autouse=True)
def _tmp_data(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    safety._inflight.clear()
    agent._sessions.clear()
    jobs.save_part(MIDEA)


def _offline(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()


def _xai(reply: dict | None = None, cited: tuple[str, ...] = ()) -> httpx.Response:
    """The live fixture, optionally with `reply` as the message text and extra cited URLs."""
    body = json.loads((FIXTURES / "llm" / "xai_responses_safety.json").read_text())
    if reply is not None:
        body["output"][-1]["content"][0]["text"] = (
            reply if isinstance(reply, str) else json.dumps(reply)
        )
    body["output"][0]["action"]["sources"] += [{"type": "url", "url": u} for u in cited]
    return httpx.Response(200, json=body)


def _mock(router, cpsc: httpx.Response | None = None, xai: httpx.Response | None = None):
    c = router.get(url__startswith=safety.CPSC_URL).mock(
        return_value=cpsc or httpx.Response(200, json=CPSC)
    )
    x = router.post(llm.XAI_RESPONSES_URL).mock(return_value=xai or _xai())
    return c, x


EMPTY = {"recalls": [], "complaints": []}


# --- verdict rules --------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_cpsc_model_match_is_recalled_and_request_shapes():
    with respx.mock(assert_all_called=True) as router:
        c, x = _mock(router)
        report = await safety.check(MIDEA)

    assert c.calls.last.request.url.params["Manufacturer"] == "Midea"
    body = json.loads(x.calls.last.request.content)
    assert body["model"] == safety.MODEL and body["max_turns"] == safety.MAX_TURNS
    web, xs = body["tools"]
    assert web == {"type": "web_search"} and xs["type"] == "x_search" and xs["from_date"] < "2099"
    assert body["text"]["format"]["strict"] is True
    assert body["include"] == ["no_inline_citations"]
    assert "Midea MAW08U1QWT" in body["input"]

    assert report.verdict == "recalled"
    assert report.headline == "Recalled June 2025: risk of mold exposure (CPSC #25320)"
    assert report.spoken.startswith("Heads up: this exact model was recalled in June 2025")
    # Grok found the same recall: deduped by URL, CPSC's record wins
    [recall] = report.recalls
    assert (recall.source, recall.number, recall.applies) == ("cpsc", "25320", "yes")
    assert len(report.complaints) == 5 and report.sources == ["cpsc", "grok"]
    assert report.cost_usd == pytest.approx(0.09099345)


@pytest.mark.asyncio
async def test_brand_only_cpsc_match_is_caution(monkeypatch):
    # pin the brand-recall window to start 2025-01-01, so the test doesn't age out
    monkeypatch.setattr(safety, "LOOKBACK_DAYS", (datetime.now(UTC).date() - date(2025, 1, 1)).days)
    part = MIDEA.model_copy(update={"id": "midea-other", "model_no": "XYZ123"})
    with respx.mock() as router:
        _mock(router, xai=_xai(EMPTY))
        report = await safety.check(part)
    assert report.verdict == "caution"
    # 25320 is a same-type (window AC) Midea recall; 17024 (2016 dehumidifiers) is neither
    # recent nor the same kind of product, so it's left out.
    assert [(r.number, r.applies) for r in report.recalls] == [("25320", "brand")]
    assert "not this model" in report.headline


@pytest.mark.asyncio
async def test_grok_recall_from_disallowed_domain_is_only_caution():
    reddit = "https://www.reddit.com/r/hvac/comments/abc/midea_recall/"
    reply = {
        "recalls": [
            {"title": "Midea AC recall", "date": None, "url": reddit, "applies_to_model": "yes"}
        ],
        "complaints": [],
    }
    with respx.mock() as router:
        _mock(router, cpsc=httpx.Response(200, json=[]), xai=_xai(reply, cited=(reddit,)))
        report = await safety.check(MIDEA)
    assert report.verdict == "caution"
    assert report.headline.startswith("Unconfirmed recall report")
    assert report.recalls[0].url == reddit


@pytest.mark.asyncio
async def test_cpsc_down_still_yields_grok_verdict():
    """The fixture's Grok recall is on cpsc.gov and applies to the model: recalled on its own."""
    with respx.mock() as router:
        _mock(router, cpsc=httpx.Response(500, text="boom"))
        report = await safety.check(MIDEA)
    assert report.sources == ["grok"]
    assert report.verdict == "recalled"
    assert report.recalls[0].source == "web" and "CPSC #" not in report.headline


@pytest.mark.asyncio
async def test_markup_stripped_and_uncited_claims_dropped():
    a, b = "https://www.bbb.org/complaint/1", "https://forum.example.com/t/2"
    reply = {
        "recalls": [
            # not in this call's citations: dropped, can't make the part "recalled"
            {"title": "Made up", "date": None, "url": CPSC_URL + "-x", "applies_to_model": "yes"}
        ],
        "complaints": [
            {
                "theme": "Leaks water [[1]](https://x.com/a)",
                "source": "web",
                "url": a,
                "quote": "It leaked [[2]](https://x.com/b).",
            },
            {"theme": "leaks water", "source": "x", "url": b, "quote": None},
            {"theme": "Ghost", "source": "web", "url": "https://never.searched/", "quote": None},
        ],
    }
    with respx.mock() as router:
        _mock(router, cpsc=httpx.Response(200, json=[]), xai=_xai(reply, cited=(a, b)))
        report = await safety.check(MIDEA)
    assert report.recalls == []
    assert [c.url for c in report.complaints] == [a, b]
    assert report.complaints[0].quote == "It leaked."
    assert report.verdict == "caution"  # 2 cited complaints, one theme
    assert report.spoken == "Caution: several owners report Leaks water."
    assert "[[" not in report.model_dump_json()


def test_strip_citations_and_match_model():
    assert (
        safety.strip_citations("Recalled [[1]](https://cpsc.gov/a) in June.") == "Recalled in June."
    )
    record = {"Description": "Model Numbers LUS26Z, MAW08-U1QWT and 2025"}
    assert safety.match_model(record, "MAW08U1QWT")
    assert not safety.match_model(record, "LUS26")  # whole token only
    assert not safety.match_model(record, "25")  # too short to trust


def test_allowed_domains():
    assert safety._allowed("https://www.cpsc.gov/Recalls/x", "Midea")
    assert safety._allowed("https://us.midea.com/recall", "Midea")
    assert safety._allowed("https://www.simpsonstrongtie.com/x", "Simpson Strong-Tie")
    assert not safety._allowed("https://www.reddit.com/r/x", "Midea")
    assert not safety._allowed("https://cpsc.gov.evil.io/x", "Midea")


# --- cache, OFFLINE, failures -----------------------------------------------------------------


@pytest.mark.asyncio
async def test_second_check_is_a_cache_hit_then_offline_serves_it(monkeypatch):
    with respx.mock() as router:
        c, x = _mock(router)
        first = await safety.check(MIDEA)
        again = await safety.check(MIDEA)
        assert (c.call_count, x.call_count) == (1, 1)
    assert again == first

    _offline(monkeypatch)
    assert (await safety.check(MIDEA)).verdict == "recalled"  # no respx: network is blocked


@pytest.mark.asyncio
async def test_concurrent_checks_share_one_paid_call():
    with respx.mock() as router:
        _, x = _mock(router)
        safety.start(MIDEA)  # background kick-off, then the user asks
        a, b = await asyncio.gather(safety.check(MIDEA), safety.check(MIDEA))
    assert x.call_count == 1 and a == b


def test_endpoint_offline_without_cache_is_unknown_200(monkeypatch):
    _offline(monkeypatch)
    client = TestClient(app)
    resp = client.get(f"/parts/{MIDEA.id}/safety")
    assert resp.status_code == 200
    assert resp.json()["verdict"] == "unknown" and resp.json()["sources"] == []
    assert client.get("/parts/no-such-part/safety").status_code == 404
    assert client.get("/parts/BAD_ID/safety").status_code == 400


def test_endpoint_both_sources_down_is_unknown_not_5xx():
    with respx.mock() as router:
        _mock(router, cpsc=httpx.Response(503), xai=httpx.Response(500, text="boom"))
        resp = TestClient(app).get(f"/parts/{MIDEA.id}/safety")
    assert resp.status_code == 200 and resp.json()["verdict"] == "unknown"


# --- agent + checkout ---------------------------------------------------------------------


@pytest.mark.asyncio
async def test_agent_check_safety_emits_show_safety_and_speaks_it():
    agent._sessions["s1"] = agent.Session(selected_part_id=MIDEA.id)
    with respx.mock() as router:
        _mock(router)
        result = await agent.handle_command("s1", "is this thing recalled?", {})  # fast path
    [action] = result["actions"]
    assert action == {
        "name": "show_safety",
        "args": {
            "part_id": MIDEA.id,
            "verdict": "recalled",
            "headline": "Recalled June 2025: risk of mold exposure (CPSC #25320)",
        },
    }
    assert result["reply"].startswith("Heads up: this exact model was recalled")


@pytest.mark.asyncio
async def test_check_safety_runs_even_when_the_model_also_returns_text(monkeypatch):
    """Integration: text alongside the tool call used to skip the tool (see ends_turn)."""
    from openai.types.chat import ChatCompletion

    call = {"name": "check_safety", "arguments": json.dumps({"part_id": None})}
    completion = ChatCompletion.model_validate(
        {
            "id": "x",
            "object": "chat.completion",
            "created": 0,
            "model": "m",
            "choices": [
                {
                    "index": 0,
                    "finish_reason": "tool_calls",
                    "message": {
                        "role": "assistant",
                        "content": "Let me check that.",
                        "tool_calls": [{"id": "c0", "type": "function", "function": call}],
                    },
                }
            ],
        }
    )

    async def fake_chat(*args, **kwargs):
        return completion

    monkeypatch.setattr(agent, "chat", fake_chat)
    agent._sessions["s4"] = agent.Session(selected_part_id=MIDEA.id)
    with respx.mock() as router:
        _mock(router)
        result = await agent.handle_command("s4", "any problems with this one?", {})
    assert [a["name"] for a in result["actions"]] == ["show_safety"]
    assert result["reply"].startswith("Heads up: this exact model was recalled")


@pytest.mark.asyncio
async def test_start_checkout_on_recalled_part_warns_once_and_still_opens(monkeypatch):
    with respx.mock() as router:
        _mock(router)
        await safety.check(MIDEA)  # cached, as the select/show_sellers kick-off would leave it
    session = agent._get_session("s2")
    session.selected_part_id = MIDEA.id

    result, action, _ = await agent._run_tool(session, "start_checkout", {"seller_index": 0}, {})
    assert action == {"name": "start_checkout", "args": {"seller_index": 0}}
    assert result["spoken"].startswith("Heads up: this exact model was recalled")
    result, _, _ = await agent._run_tool(session, "start_checkout", {"seller_index": 0}, {})
    assert result == {"ok": True}  # said once, not on every tap


@pytest.mark.asyncio
async def test_buy_it_on_recalled_part_warns_and_names_the_seller(monkeypatch):
    """The app lane's "buy it" fast path (the server picks the seller) keeps F6's warning."""
    with respx.mock() as router:
        _mock(router)
        await safety.check(MIDEA)

    async def no_llm(*args, **kwargs):
        raise AssertionError("a fast path must not call the LLM")

    monkeypatch.setattr(agent, "chat", no_llm)
    ctx = {"selected_part_id": MIDEA.id}
    result = await agent.handle_command("s6", "buy it", ctx)
    assert result["actions"] == [{"name": "start_checkout", "args": {"seller_index": 0}}]
    assert result["reply"].startswith("Heads up: this exact model was recalled")
    assert result["reply"].endswith("The pay panel's open for Home Depot if you still want it.")
    result = await agent.handle_command("s6", "buy it", ctx)
    assert result["reply"] == "Hold-to-pay panel's open for Home Depot."  # warned once


@pytest.mark.asyncio
async def test_select_candidate_kicks_off_the_check_in_background(monkeypatch):
    monkeypatch.setattr(agent.manuals, "prefetch", lambda part: None)  # F9's own xAI call
    session = agent._get_session("s3")
    session.candidate_ids = [MIDEA.id]
    with respx.mock() as router:
        _, x = _mock(router)
        await agent._run_tool(session, "select_candidate", {"index": 0}, {})
        await safety._inflight[MIDEA.id]
    assert x.call_count == 1 and safety.peek(MIDEA).verdict == "recalled"


def test_checkout_receipt_carries_the_verdict():
    client = TestClient(app)
    body = {"part_id": MIDEA.id, "seller_idx": 0, "qty": 1}
    assert client.post("/checkout", json=body).json()["safety_verdict"] == "unknown"
    with respx.mock() as router:
        _mock(router)
        assert client.get(f"/parts/{MIDEA.id}/safety").json()["verdict"] == "recalled"
    receipt = client.post("/checkout", json=body).json()
    assert receipt["status"] == "OFFLINE_RECEIPT" and receipt["safety_verdict"] == "recalled"


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_midea_is_recalled():
    """~$0.10: real CPSC + xAI on the part G3 found the recall for."""
    report = await safety._check(MIDEA)
    assert report.verdict == "recalled" and "25320" in report.headline
