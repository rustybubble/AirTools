import asyncio
import json

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from server import agent, checkout, jobs, llm, report, report_demo
from server.app import app
from server.config import get_settings
from server.models import Dims, Part, Seller

SESSION = "demo-kitchen"
PART_ID = "karran-qu-670-bl-2d781c"


@pytest.fixture
def seeded(tmp_path, monkeypatch):
    """The demo seed (server/report_demo.py) on a tmp DATA_DIR and a tiny fake kitchen scene."""
    scene = tmp_path / "scene" / "kitchen"
    (scene / "thumbs").mkdir(parents=True)
    for cam in ("0021", "0041", "0101"):  # 0061/0081 deliberately missing -> no <img>
        (scene / "thumbs" / f"{cam}.jpg").write_bytes(b"\xff\xd8jpg")
    (scene / "scene.json").write_text(
        json.dumps(
            {
                "name": "kitchen",
                "revision": 1,
                "quality": "full",
                "scale_method": "altitude",
                "scale_residual_m": 0.05,
                "scale_notes": "caption H fit",
            }
        )
    )
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    jobs.save_part(
        Part(
            id=PART_ID,
            name="Undermount Quartz Sink <Black>",
            dims_mm=Dims(w=806.45, d=488.95, h=228.6),
            sellers=[Seller(name="Ferguson Home", price_usd=359.95, shipping_usd=0.0)],
            recommended_seller=0,
        )
    )
    asyncio.run(report_demo.seed(SESSION, "kitchen", PART_ID))
    return tmp_path


def _fake_extract(calls: list, fail_roles=()):
    async def fake(role, messages, model_cls):
        calls.append(role)
        if role in fail_roles:
            raise httpx.ConnectError("xai down")
        return model_cls(summary="Measured the sink wall.", next_steps=["Buy clips."])

    return fake


def test_assemble_joins_notebook_bom_orders_and_scene(seeded):
    r = report.assemble(SESSION)
    assert r is not None
    assert r.scene["accuracy"] == "Metric scale from altitude, fit residual ±5 cm."
    assert [m["label"] for m in r.measurements][:2] == [
        "Counter run, sink wall",
        "Sink cabinet inside width",
    ]
    tape = r.measurements[0]
    assert tape["display"] == "2.41 m (94.9 in)"
    assert tape["thumb_url"] == "/scenes/kitchen/thumbs/0021.jpg"
    assert r.measurements[2]["preview"] is True and r.measurements[2]["thumb_url"] is None
    assert r.measurements[3]["display"] == "0.40 °"
    assert r.pins == [
        {
            "label": "Water stain under the sink: check for a leak before install",
            "thumb_url": "/scenes/kitchen/thumbs/0101.jpg",
        }
    ]
    assert r.parts[0]["part_id"] == PART_ID and r.parts[0]["count"] == 1
    assert [b["id"] for b in r.boms] == ["bom-demo-kitchen"]  # listed once (bom + order entry)
    assert r.boms[0]["lines"][0]["cost_usd"] == 4.98
    (order,) = r.orders
    assert order["label"] == checkout.OFFLINE_LABEL_UNCONFIGURED
    assert order["total_usd"] == 364.93  # 359.95 sink + 4.98 caulk
    assert r.totals == {
        "orders_usd": 364.93,
        "sandbox_authorized_usd": 0.0,
        "offline_unpaid_usd": 364.93,
        "bom_usd": 4.98,
    }


def test_assemble_unknown_session_is_none(seeded):
    assert report.assemble("nobody") is None


@pytest.mark.asyncio
async def test_llm_summary_is_cached_by_content_hash(seeded, monkeypatch):
    calls: list = []
    monkeypatch.setattr(llm, "extract", _fake_extract(calls))
    first = await report.build_report(SESSION)
    second = await report.build_report(SESSION)
    assert calls == ["report"]  # second build hit the cache
    assert first.summary == second.summary == "Measured the sink wall."
    assert first.summary_source == "xai:grok-4.3"


@pytest.mark.asyncio
async def test_xai_failure_falls_back_to_agent_role(seeded, monkeypatch):
    calls: list = []
    monkeypatch.setattr(llm, "extract", _fake_extract(calls, fail_roles=("report",)))
    r = await report.build_report(SESSION)
    assert calls == ["report", "agent"]
    assert r.summary_source.startswith("groq:")


@pytest.mark.asyncio
async def test_offline_uses_template_and_never_calls_llm(seeded, monkeypatch):
    calls: list = []
    monkeypatch.setattr(llm, "extract", _fake_extract(calls))
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    r = await report.build_report(SESSION)
    assert calls == []
    assert r.summary_source == "template"
    assert "$0.00 sandbox-authorized, $364.93 recorded offline with no payment" in r.summary
    assert r.next_steps[0].startswith("Place the real order for Undermount Quartz Sink")
    assert "Buy Undermount sink mounting clips x1." in r.next_steps  # caulk was already ordered
    assert not any("caulk" in step for step in r.next_steps)


def test_html_and_json_routes(seeded, monkeypatch):
    monkeypatch.setattr(llm, "extract", _fake_extract([]))
    with TestClient(app) as client:
        page = client.get(f"/report/{SESSION}")
        data = client.get(f"/report/{SESSION}.json")
        missing = client.get("/report/nobody")
    assert page.status_code == 200 and page.headers["content-type"].startswith("text/html")
    body = page.text
    assert checkout.OFFLINE_LABEL_UNCONFIGURED in body  # receipt label verbatim
    assert "$364.93" in body and "$4.98" in body  # order + BOM totals
    assert "±5 cm" in body
    assert "src='/scenes/kitchen/thumbs/0021.jpg'" in body
    assert "Undermount Quartz Sink &lt;Black&gt;" in body  # escaped, never raw markup
    assert "https://x.com/intent/tweet?text=" in body
    assert "@media print" in body
    assert data.status_code == 200 and data.json()["totals"]["orders_usd"] == 364.93
    assert missing.status_code == 404


@respx.mock
def test_narrate_uses_xai_tts_in_quartermaster_voice_and_caches(seeded, monkeypatch):
    monkeypatch.setattr(llm, "extract", _fake_extract([]))
    route = respx.post(report.XAI_TTS_URL).mock(
        return_value=httpx.Response(200, content=b"ID3mp3", headers={"content-type": "audio/mpeg"})
    )
    with TestClient(app) as client:
        first = client.post(f"/report/{SESSION}/narrate")
        second = client.post(f"/report/{SESSION}/narrate")
    assert first.content == second.content == b"ID3mp3"
    assert first.headers["content-type"] == "audio/mpeg"
    assert route.call_count == 1
    sent = json.loads(route.calls[0].request.content)
    assert sent["voice_id"] == "rex" and sent["language"] == "en"
    assert (
        "Site report for kitchen." in sent["text"]
        and len(sent["text"]) <= report.NARRATION_CHAR_LIMIT
    )


@pytest.mark.asyncio
async def test_agent_send_me_the_report_queues_show_report():
    agent._sessions.clear()
    result = await agent.handle_command("quest 1", "send me the report", {})
    assert result["actions"] == [{"name": "show_report", "args": {"url": "/report/quest%201"}}]
    assert result["reply"] == agent._CANNED_REPLY["make_report"]
    agent._sessions.clear()
