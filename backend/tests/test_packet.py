import asyncio
import json
import re
import uuid
from collections import OrderedDict

import httpx
import pytest
import respx
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, cache, jobs, llm, packet, report_demo, rules
from server.app import app
from server.config import get_settings
from server.models import Dims, Job, Part, Seller

SESSION = "demo-kitchen"
PART_ID = "karran-qu-670-bl-2d781c"
FILES = packet.FILES_URL
FILE_ID = "file_0b4c9d2e-demo"
PUBLIC_URL = f"https://files-cdn.x.ai/tok123/{FILE_ID}.pdf"


@pytest.fixture
def seeded(tmp_path, monkeypatch):
    """F4's demo seed on a tmp DATA_DIR and a tiny kitchen scene with real JPEG thumbs."""
    scene = tmp_path / "scene" / "kitchen"
    (scene / "thumbs").mkdir(parents=True)
    for cam in ("0021", "0101"):
        Image.new("RGB", (320, 180), (120, 90, 60)).save(scene / "thumbs" / f"{cam}.jpg")
    (scene / "scene.json").write_text(
        json.dumps({"name": "kitchen", "scale_method": "altitude", "scale_residual_m": 0.05})
    )
    monkeypatch.setattr(jobs, "_jobs", OrderedDict())  # no other test's survey or rules check
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    jobs.save_part(
        Part(
            id=PART_ID,
            name="Undermount Quartz Sink",
            model_no="QU-670-BL",
            dims_mm=Dims(w=806.45, d=488.95, h=228.6),
            sellers=[
                Seller(
                    name="Ferguson Home",
                    price_usd=359.95,
                    shipping_usd=0.0,
                    url="https://example.com/sink",
                )
            ],
            recommended_seller=0,
        )
    )
    asyncio.run(report_demo.seed(SESSION, "kitchen", PART_ID))
    return tmp_path


def _fake_grok(monkeypatch, homeowner: str, contractor: str, calls: list | None = None):
    async def fake(model, input, **kw):
        if calls is not None:
            calls.append({"model": model, "input": input, **kw})
        text = json.dumps({"homeowner": homeowner, "contractor": contractor})
        return llm.ResponsesResult(text=text, citations=[], cost_usd=0.0021, tool_usage={})

    monkeypatch.setattr(packet.llm, "responses", fake)


def _page_count(pdf: bytes) -> int:
    return len(re.findall(rb"/Type\s*/Page\b", pdf))


def _files_routes(router, expires_at=1790000000):
    return {
        "upload": router.post(FILES).mock(
            return_value=httpx.Response(
                200, json={"id": FILE_ID, "filename": "x.pdf", "purpose": "", "expires_at": None}
            )
        ),
        "public": router.post(f"{FILES}/{FILE_ID}/public-url").mock(
            return_value=httpx.Response(
                200, json={"public_url": PUBLIC_URL, "expires_at": expires_at}
            )
        ),
        "revoke": router.post(f"{FILES}/{FILE_ID}/public-url/revoke").mock(
            return_value=httpx.Response(
                200, json={"id": FILE_ID, "revoked": True, "public_url": PUBLIC_URL}
            )
        ),
        "delete": router.delete(f"{FILES}/{FILE_ID}").mock(
            return_value=httpx.Response(200, json={"id": FILE_ID, "deleted": True})
        ),
    }


# --- facts, summaries ------------------------------------------------------------------------


def test_gather_keeps_only_shared_notes_and_totals(seeded):
    app_notes = [
        {"type": "note", "text": "Owner Jane Doe, unit 4B", "share": False},
        {"type": "note", "text": "Gate code is on the fridge", "share": True},
    ]
    from server.app import _append_notebook

    _append_notebook(SESSION, app_notes)
    report, ex = packet.gather(SESSION)
    assert report.notes == ["Gate code is on the fridge"]  # the seed's untagged note is gone
    assert ex == {}
    assert report.totals["estimate_usd"] == 364.93  # 359.95 sink + 4.98 caulk
    lines = "\n".join(packet.facts(report, ex))
    assert "Jane Doe" not in lines and "4B" not in lines
    assert "QU-670-BL" in lines and "2.41 m (94.9 in)" in lines


def test_template_blurbs_pass_their_own_number_check(seeded):
    report, ex = packet.gather(SESSION)
    template = packet.template_blurbs(report)
    lines = packet.facts(report, ex)
    assert packet.backed(template["homeowner"], lines)
    assert packet.backed(template["contractor"], lines)


def test_unbacked_number_falls_back_to_template(seeded, monkeypatch):
    calls = []
    _fake_grok(
        monkeypatch,
        homeowner="The black quartz sink costs $359.95 and saves $900 a year.",
        contractor="Install the QU-670-BL sink, 806 x 489 x 229 mm, on the 2.41 m run.",
        calls=calls,
    )
    report, ex = packet.gather(SESSION)
    lines = packet.facts(report, ex)
    out = asyncio.run(packet.blurbs(report, lines))
    assert out["homeowner"] == packet.template_blurbs(report)["homeowner"]
    assert out["sources"] == {
        "homeowner": "template",
        "contractor": "xai:grok-4.20-0309-non-reasoning",
    }
    assert "2.41 m run" in out["contractor"]
    assert calls[0]["model"] == "grok-4.20-0309-non-reasoning"
    assert calls[0]["text"]["format"]["strict"] is True
    # cached by the facts' hash: a second build makes no call and re-runs the check
    asyncio.run(packet.blurbs(report, lines))
    assert len(calls) == 1


def test_backed_normalises_commas_and_decimals():
    assert packet.backed("about $1,234 total, 2.41 m", ["Total $1234.00", "run 2.41 m"])
    assert not packet.backed("2.4 m", ["run 2.41 m"])


# --- optional sections -----------------------------------------------------------------------


def _done_job(stage: str, result: dict) -> None:
    job = Job(id=uuid.uuid4().hex[:12], status="done", stage=stage, result=result)
    jobs._jobs[job.id] = job


def _seed_extras(tmp_path):
    cache.put(
        "safety_grok",
        PART_ID,
        {
            "recalls": [
                {
                    "source": "web",
                    "title": "Karran sink bracket recall",
                    "date": "2026-05-01",
                    "url": "https://www.cpsc.gov/Recalls/2026/karran",
                    "applies": "brand",
                }
            ],
            "complaints": [],
        },
    )
    # F10 and F13 results as their own jobs leave them in the jobs table (survey.run's and
    # rules.check's shapes, trimmed to what the packet reads).
    _done_job(
        "survey",
        {
            "site": "kitchen",
            "pins": [
                {
                    "id": "f1",
                    "element": "facade",
                    "issue": "stain",
                    "severity": "minor",
                    "action": "monitor",
                    "confidence": 0.8,
                }
            ],
            "looked_fine": ["roof (0021)"],  # triaged by F10: a severity-none finding isn't a pin
            "label": "AI triage from drone frames, not an inspection",
        },
    )
    _done_job(
        rules.STAGE,
        {
            "job": None,
            "part_ids": [PART_ID],
            "permit": {
                "required": "yes",
                "office": {"name": "Office of Buildings"},
                "items": [{"name": "Plumbing permit", "url": "https://example.com/permits"}],
            },
            "money": {
                "incentives": [
                    {
                        "name": "Utility sink rebate",
                        "amount_usd": 50.0,
                        "status": "active",
                        "url": "https://example.com/rebate",
                    },
                    {
                        "name": "Federal 25C credit",
                        "amount_usd": None,
                        "status": "ended",
                        "url": None,
                        "status_source": "https://www.irs.gov/credits",
                    },
                ]
            },
        },
    )
    postcard = tmp_path / "data" / "parts" / PART_ID / "postcard.jpg"
    Image.new("RGB", (400, 300), (10, 10, 10)).save(postcard)


def test_optional_sections_absent_then_present(seeded, monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    client = TestClient(app)
    bare = client.post(f"/packet/{SESSION}").json()
    assert bare["sections"] == [] and bare["pages"] == 4

    _seed_extras(seeded)
    report, ex = packet.gather(SESSION)
    assert sorted(ex) == ["postcard", "rules", "safety", "survey"]
    lines = packet.facts(report, ex)
    assert "Survey: facade: stain (minor, monitor)" in lines
    assert not any("roof" in line for line in lines)  # severity none never pinned
    assert "Rules: Permit: Plumbing permit from Office of Buildings" in lines
    assert "Rules: Utility sink rebate: active, $50.00" in lines
    assert "Rules: Federal 25C credit: ended" in lines
    assert any(line.startswith("Safety: caution: ") for line in lines)  # safety.peek's verdict
    assert report.totals["after_rebates_usd"] == 314.93
    body = client.post(f"/packet/{SESSION}").json()
    assert body["sections"] == ["postcard", "rules", "safety", "survey"]
    pdf = client.get(body["local_url"]).content
    assert _page_count(pdf) == 4


def test_rebates_count_only_eligible_rows(seeded):
    """F13's `counted` rule: an unverified or not-eligible amount never comes off the total."""
    _seed_extras(seeded)
    jobs.latest(rules.STAGE)["money"]["incentives"] += [
        {
            "name": "HEIP rebate",
            "amount_usd": 500.0,
            "status": "active",
            "eligibility": "unverified",
            "eligibility_reason": rules.NOT_CHECKED,
        },
        {
            "name": "HEAR rebate",
            "amount_usd": 800.0,
            "status": "not_eligible",
            "eligibility": "not_eligible",
            "eligibility_reason": rules.NOT_LISTED,
        },
    ]
    report, ex = packet.gather(SESSION)
    assert packet.rebates_usd(ex) == 50.0
    assert report.totals["after_rebates_usd"] == 314.93
    lines = packet.facts(report, ex)
    assert f"Rules: HEIP rebate: active, $500.00 ({rules.NOT_CHECKED})" in lines


# --- PDF, hosting ------------------------------------------------------------------------------


def test_publish_uploads_and_returns_public_url(seeded, monkeypatch):
    _fake_grok(monkeypatch, "The sink costs $359.95.", "Sink QU-670-BL, 806 x 489 x 229 mm.")
    with respx.mock(assert_all_called=False) as router:
        routes = _files_routes(router)
        resp = TestClient(app).post(f"/packet/{SESSION}", json={"days": 90})
    body = resp.json()
    assert resp.status_code == 200
    assert body["public"] is True and body["url"] == PUBLIC_URL and body["pages"] == 4
    assert json.loads(routes["public"].calls[0].request.content) == {"expires_after": 2_592_000}
    upload = routes["upload"].calls[0].request
    assert b"application/pdf" in upload.content and b"%PDF-" in upload.content
    assert body["expires_at"] == "2026-09-21T14:13:20+00:00"  # the API's 1790000000
    assert "Public link" in body["label"] and not routes["revoke"].called
    pdf = (seeded / "data" / "packets" / f"{body['packet_id']}.pdf").read_bytes()
    assert pdf.startswith(b"%PDF-") and _page_count(pdf) == 4


def test_revoke_kills_link_then_deletes_file(seeded, monkeypatch):
    _fake_grok(monkeypatch, "Sink.", "Sink.")
    client = TestClient(app)
    with respx.mock(assert_all_called=False) as router:
        routes = _files_routes(router)
        packet_id = client.post(f"/packet/{SESSION}", json={"days": 7}).json()["packet_id"]
        assert json.loads(routes["public"].calls[0].request.content) == {"expires_after": 604800}
        body = client.delete(f"/packet/{packet_id}").json()
        assert routes["revoke"].call_count == 1 and routes["delete"].call_count == 1
    assert body["public"] is False and body["url"] is None and "revoked_at" in body
    assert packet.load(packet_id)["public"] is False
    assert client.get(f"/packet/{packet_id}.pdf").status_code == 200  # LAN copy stays
    assert client.delete("/packet/0123456789abcdef").status_code == 404


def test_files_failure_is_502_with_the_lan_copy(seeded, monkeypatch):
    _fake_grok(monkeypatch, "Sink.", "Sink.")
    with respx.mock(assert_all_called=False) as router:
        router.post(FILES).mock(return_value=httpx.Response(500, json={"error": "down"}))
        resp = TestClient(app).post(f"/packet/{SESSION}")
    assert resp.status_code == 502
    detail = resp.json()["detail"]
    assert detail["public"] is False and detail["local_url"].endswith(".pdf")


def test_offline_is_lan_only_with_no_xai_call(seeded, monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    client = TestClient(app)
    with respx.mock(assert_all_called=False) as router:
        body = client.post(f"/packet/{SESSION}").json()
        assert not router.calls
    assert body["public"] is False and body["url"] is None and body["label"] == packet.LAN_LABEL
    assert body["summary_sources"] == {"homeowner": "template", "contractor": "template"}
    pdf = client.get(body["local_url"])
    assert pdf.status_code == 200 and pdf.headers["content-type"] == "application/pdf"
    assert _page_count(pdf.content) == 4
    assert client.post("/packet/nobody").status_code == 404
    assert client.get("/packet/../../etc.pdf").status_code == 404


# --- agent -----------------------------------------------------------------------------------


def test_agent_sends_then_revokes_packet(seeded, monkeypatch):
    agent._sessions.clear()
    _fake_grok(monkeypatch, "Sink.", "Sink.")
    with respx.mock(assert_all_called=False) as router:
        routes = _files_routes(router)
        sent = asyncio.run(agent.handle_command(SESSION, "send it to my contractor", {}))
        (action,) = sent["actions"]
        assert action["name"] == "show_packet"
        assert action["args"]["url"] == PUBLIC_URL and action["args"]["public"] is True
        assert action["args"]["qr_png_url"] is None and action["args"]["expires_at"]
        assert "public" in sent["reply"] and "take it down" in sent["reply"]

        down = asyncio.run(agent.handle_command(SESSION, "take the packet down", {}))
        assert down["actions"][0]["name"] == "packet_revoked"
        assert routes["revoke"].called and routes["delete"].called
        again = asyncio.run(agent.handle_command(SESSION, "take the packet down", {}))
    assert again["actions"] == [] and "no public packet" in again["reply"]
    agent._sessions.clear()
