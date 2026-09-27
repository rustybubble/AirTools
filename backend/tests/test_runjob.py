"""'Do the whole job' (server/runjob.py): every step's module is mocked; no network."""

import asyncio
import json
import uuid
from collections import OrderedDict

import pytest

from server import (
    agent,
    cache,
    checkout,
    imagine,
    jobs,
    packet,
    postcard,
    rules,
    runjob,
    safety,
    survey,
)
from server.config import get_settings
from server.models import Dims, Job, Part, Seller
from server.safety import SafetyReport

PART = Part(
    id="lg-knsah121b-04da2e",
    name="LG 12,000 BTU ductless mini-split indoor unit",
    model_no="KNSAH121B",
    dims_mm=Dims(w=837, d=189, h=308),
    image_url="https://example.com/lg.jpg",
    sellers=[Seller(name="Supply", price_usd=899.0, shipping_usd=0.0, url="https://x.com/lg")],
    recommended_seller=0,
)
PIN = {
    "id": "f1",
    "frame_id": "0001",
    "box": [0.2, 0.2, 0.6, 0.5],
    "element": "roof_covering",
    "severity": "severe",
    "issue": "membrane failed",
    "action": "replace",
    "part_query": "ductless mini-split",
    "p": [0, 0, 0],
}
PARALLEL = {"show_safety", "show_rules", "show_postcard"}


@pytest.fixture
def mocked(tmp_path, monkeypatch):
    """Every step's module mocked to a fast cache-hit-like answer; `calls` logs what ran."""
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    monkeypatch.setattr(jobs, "_jobs", OrderedDict())
    monkeypatch.setattr(runjob, "_runs", OrderedDict())
    calls: list[str] = []

    monkeypatch.setattr(survey, "plan", lambda site, **kw: calls.append("survey.plan") or site)
    monkeypatch.setattr(survey, "is_cached", lambda plan: True)

    async def fake_survey(plan):
        calls.append("survey.run")
        return {
            "site": plan,
            "pins": [PIN],
            "label": survey.LABEL,
            "spoken": "1 problem found.",
            "cost_usd": 0.01,
        }

    monkeypatch.setattr(survey, "run", fake_survey)

    def fake_search(req):
        calls.append(f"search:{req.query}")
        jobs.save_part(PART)
        job = Job(id=uuid.uuid4().hex[:12], status="done", candidates=[PART], query=req.query)
        jobs._jobs[job.id] = job
        return job

    monkeypatch.setattr(jobs, "start_search", fake_search)

    async def fake_safety(part):
        calls.append("safety.check")
        return _safety_report(part, "clear")

    monkeypatch.setattr(safety, "check", fake_safety)

    async def fake_juris(address, location):
        return rules.Juris(state="GA", place="Atlanta city", authority="Atlanta city",
                           source="location_text", confidence="city_only")  # fmt: skip

    async def fake_rules(juris, job, parts, as_of=None):
        calls.append(f"rules.check:{job}")
        incentive = {"name": "HEIP", "amount_usd": 150.0, "status": "active", "url": "u"}
        return {
            "job": job,
            "spoken": "You'll need a permit. Not legal advice.",
            "money": {"incentives": [incentive]},
            "cost_usd": 0.19,
            "part_ids": [p.id for p in parts],
        }

    monkeypatch.setattr(rules, "jurisdiction", fake_juris)
    monkeypatch.setattr(rules, "check", fake_rules)

    monkeypatch.setattr(postcard, "load_site", lambda site, frame_id, b64: (b"jpg", "/before"))

    async def fake_fetch(url, dest):
        return True

    monkeypatch.setattr(runjob.assets, "fetch_image", fake_fetch)

    async def fake_postcard(part, jpg, **kw):
        calls.append(f"postcard:{kw['box']}")
        return {
            "part_id": part.id,
            "image_url": "/p.jpg",
            "before_url": "/before",
            "label": imagine.LABEL,
            "cost_usd": 0.07,
            "cached": False,
        }

    monkeypatch.setattr(postcard, "make_postcard", fake_postcard)

    async def fake_packet(session_id, days=7, public=True):
        calls.append("packet.make")
        return {
            "packet_id": "abc",
            "url": "https://files-cdn.x.ai/t/f.pdf",
            "local_url": "/packet/abc.pdf",
            "expires_at": "2026-10-03T00:00:00+00:00",
            "public": True,
            "label": "Public link",
            "cost_usd": 0.001,
        }

    monkeypatch.setattr(packet, "make", fake_packet)

    async def never(*a, **kw):  # the chain must never pay
        raise AssertionError("run_job called checkout.authorize")

    monkeypatch.setattr(checkout, "authorize", never)
    return calls


def _safety_report(part: Part, verdict: str) -> SafetyReport:
    return SafetyReport(
        part_id=part.id,
        verdict=verdict,
        headline="Recalled May 2026: fire hazard" if verdict == "recalled" else "No recalls",
        spoken="Heads up: recalled." if verdict == "recalled" else "No recalls on this one.",
        checked_at="2026-09-26T00:00:00",
        cost_usd=0.09,
    )


async def _finish(session_id: str, site="hospital", goal=None, context=None) -> runjob.Run:
    session = agent._get_session(session_id)
    run = runjob.start(session, site, goal, context or {})
    for _ in range(200):
        if run.status != "running":
            return run
        await asyncio.sleep(0.01)
    raise AssertionError("run never finished")


def _names(run: runjob.Run) -> list[str]:
    return [a["name"] for a in run.actions]


@pytest.mark.asyncio
async def test_happy_path_runs_every_step_and_stops_at_the_pay_panel(mocked, tmp_path):
    run = await _finish("happy")
    names = _names(run)
    assert names[:8] == [
        "job_started",
        "job_step",
        "survey_started",
        "show_survey",
        "job_step",
        "search_started",
        "select_candidate",
        "job_step",
    ]
    middle = names[7:13]  # three (job_step, action) pairs in finishing order
    assert middle[::2] == ["job_step"] * 3 and set(middle[1::2]) == PARALLEL
    assert names[13:] == ["job_step", "show_packet", "job_step", "start_checkout", "job_done"]
    assert run.actions[0]["args"]["steps"] == runjob.STEPS
    assert [s["status"] for s in run.steps] == ["done"] * 7
    assert run.status == "done"
    # the survey's pin drives the search and the postcard's box
    assert "search:ductless mini-split" in mocked and f"postcard:{PIN['box']}" in mocked
    assert "rules.check:minisplit_install" in mocked
    # the pick becomes the session's selection and joins the notebook for the packet
    session = agent._get_session("happy")
    assert session.selected_part_id == PART.id
    notebook = json.loads((tmp_path / "data" / "notebook" / "happy.json").read_text())
    assert notebook == [
        {
            "type": "placement",
            "part_id": PART.id,
            "count": 1,
            "source": "run_job",
            "site": "hospital",
        }
    ]
    done = run.actions[-1]["args"]
    assert done["total_usd"] == 899.0 and done["after_rebates_usd"] == 749.0
    assert done["packet_url"] == "https://files-cdn.x.ai/t/f.pdf"
    assert done["cost_usd"] == pytest.approx(0.01 + 0.09 + 0.19 + 0.07 + 0.001)
    checkout_step = run.actions[-3]["args"]
    assert checkout_step["spoken"] == runjob.PAY_LINE
    assert run.actions[-2]["args"] == {"seller_index": 0, "part_id": PART.id}
    # nothing but the panel: no authorisation action, no checkout call (mock raises)
    assert not {n for n in names if "pay" in n or "authori" in n or n == "checkout"}


@pytest.mark.asyncio
async def test_recalled_part_stops_before_the_pay_panel(mocked, monkeypatch):
    async def recalled(part):
        return _safety_report(part, "recalled")

    monkeypatch.setattr(safety, "check", recalled)
    run = await _finish("recall")
    assert "start_checkout" not in _names(run)
    assert run.status == "stopped"
    last = run.steps[-1]
    assert (last["name"], last["status"], last["spoken"]) == (
        "checkout",
        "stopped",
        runjob.STOP_LINE,
    )
    assert "packet.make" in mocked  # the packet still carries the recall notice
    assert run.actions[-1]["args"]["status"] == "stopped"


@pytest.mark.asyncio
async def test_failed_or_slow_step_is_skipped_with_a_note(mocked, monkeypatch):
    async def broken(*a, **kw):
        raise imagine.ImagineError("xai 500")

    async def hangs(juris, job, parts, as_of=None):
        await asyncio.sleep(10)

    monkeypatch.setattr(postcard, "make_postcard", broken)
    monkeypatch.setattr(rules, "check", hangs)
    monkeypatch.setattr(runjob, "STEP_CAP_S", 0.05)
    run = await _finish("fail")
    by = {s["name"]: s for s in run.steps}
    assert by["postcard"]["status"] == "skipped"
    assert by["postcard"]["spoken"] == "The postcard step failed; skipped it."
    assert by["rules"]["status"] == "skipped"
    assert "show_postcard" not in _names(run) and "show_rules" not in _names(run)
    assert by["packet"]["status"] == by["checkout"]["status"] == "done"  # the chain went on
    assert "start_checkout" in _names(run)


@pytest.mark.asyncio
async def test_rebate_bigger_than_the_price_floors_at_zero(mocked, monkeypatch):
    """Live run: "up to $500" off a $448 kit read as "after rebates: $-52"."""
    real = rules.check

    async def big_rebate(juris, job, parts, as_of=None):
        out = await real(juris, job, parts, as_of)
        out["money"]["incentives"][0]["amount_usd"] = 1000.0
        return out

    monkeypatch.setattr(rules, "check", big_rebate)
    run = await _finish("rebate")
    assert run.actions[-1]["args"]["after_rebates_usd"] == 0


@pytest.mark.asyncio
async def test_unverified_rebate_stays_on_the_price(mocked, monkeypatch):
    """F13's eligibility fix: an `unverified` amount is shown, never subtracted."""
    real = rules.check

    async def unverified(juris, job, parts, as_of=None):
        out = await real(juris, job, parts, as_of)
        out["money"]["incentives"][0].update(
            eligibility="unverified", eligibility_reason=rules.NOT_CHECKED
        )
        return out

    monkeypatch.setattr(rules, "check", unverified)
    run = await _finish("unverified")
    done = run.actions[-1]["args"]
    assert done["total_usd"] == 899.0 and done["after_rebates_usd"] is None


@pytest.mark.asyncio
async def test_no_part_skips_what_needs_one(mocked, monkeypatch):
    def empty(req):
        job = Job(id="empty", status="done", candidates=[])
        jobs._jobs[job.id] = job
        return job

    monkeypatch.setattr(jobs, "start_search", empty)
    run = await _finish("nopart", site=None, goal="a flux capacitor")
    by = {s["name"]: s for s in run.steps}
    assert "survey" not in by  # a stated need skips the survey
    assert by["part"]["spoken"] == "Couldn't find a part for a flux capacitor."
    assert {by[n]["status"] for n in ("safety", "rules", "postcard", "checkout")} == {"skipped"}
    assert by["safety"]["spoken"] == "No part, so no recall check."


@pytest.mark.asyncio
async def test_after_paging_and_the_http_routes(mocked):
    from httpx import ASGITransport, AsyncClient

    from server.app import app

    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://t") as client:
        started = await client.post("/job/run", json={"session_id": "pg", "site": "hospital"})
        assert started.status_code == 200
        body = started.json()
        assert body["steps"] == runjob.STEPS
        run = runjob.get(body["run_id"])
        busy = await client.post("/job/run", json={"session_id": "pg", "site": "hospital"})
        assert busy.status_code == 409  # one run per session at a time
        for _ in range(200):
            if run.status != "running":
                break
            await asyncio.sleep(0.01)
        full = (await client.get(f"/job/run/{run.id}")).json()
        page = (await client.get(f"/job/run/{run.id}?after=3")).json()
        assert full["status"] == "done" and full["next"] == len(run.actions)
        assert page["actions"] == full["actions"][3:] and page["next"] == full["next"]
        tail = (await client.get(f"/job/run/{run.id}?after={full['next']}")).json()
        assert tail["actions"] == []
        assert (await client.get("/job/run/nope")).status_code == 404
        bad = await client.post("/job/run", json={"session_id": "pg2", "site": "../etc"})
        assert bad.status_code == 400
        nothing = await client.post("/job/run", json={"session_id": "pg3"})
        assert nothing.status_code == 400  # no scene and no need


@pytest.mark.asyncio
async def test_offline_with_everything_cached(mocked, monkeypatch, tmp_path):
    """OFFLINE: the cached survey, search and rules replay, safety reads its real cache, and the
    real packet is built LAN-only with no network call (conftest blocks sockets)."""
    monkeypatch.setenv("OFFLINE", "true")
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    get_settings.cache_clear()
    monkeypatch.setattr(safety, "check", _real_safety_check)
    monkeypatch.setattr(packet, "make", _real_packet_make)
    cache.put("safety_grok", PART.id, {"recalls": [], "complaints": [], "cost_usd": 0.09})
    run = await _finish("offline")
    assert [s["status"] for s in run.steps] == ["done"] * 7
    by = {s["name"]: s for s in run.steps}
    assert by["safety"]["spoken"] == "No recalls or complaint patterns on this one."
    assert by["safety"]["cost_usd"] is None  # a cached recall check spends nothing this run
    shown = next(a for a in run.actions if a["name"] == "show_packet")["args"]
    assert shown["public"] is False and shown["url"].startswith("/packet/")
    assert by["packet"]["spoken"] == "The job packet is on the local network only (offline)."
    assert (tmp_path / "data" / "packets" / f"{shown['packet_id']}.pdf").is_file()

    # an uncached survey offline is skipped with a note, and the chain has nothing to buy
    monkeypatch.setattr(survey, "is_cached", lambda plan: False)
    run = await _finish("offline2")
    assert run.steps[0]["spoken"] == "No survey: it needs the uplink."
    assert run.steps[1]["spoken"] == "Nothing in the survey needs a part."

    # a view whose Imagine edit isn't cached: that step says it needs the uplink
    async def uncached(*a, **kw):
        raise cache.OfflineMiss("offline: no cached value for imagine/...")

    monkeypatch.setattr(postcard, "make_postcard", uncached)
    run = await _finish("offline3", site="kitchen", goal="mini-split", context={"frame_id": "9"})
    by = {s["name"]: s for s in run.steps}
    assert by["postcard"]["spoken"] == "The postcard step needs the uplink; skipped it."
    assert by["checkout"]["status"] == "done"


_real_safety_check = safety.check
_real_packet_make = packet.make


@pytest.mark.asyncio
async def test_agent_fast_path_starts_the_chain(mocked):
    for text in ("do the whole job", "take care of it", "handle it"):
        assert agent._match_fast_intent(text)[0] == "run_job"
    assert agent._match_fast_intent("how do I handle it?") is None
    first = await agent.handle_command("voice", "do the whole job", {"site": "hospital"})
    assert [a["name"] for a in first["actions"]] == ["job_started"]
    assert first["reply"] == (
        "On it: survey, part, safety, rules, preview, packet. I'll stop at the pay panel."
    )
    again = await agent.handle_command("voice", "handle it", {"site": "hospital"})
    assert again["reply"] == "Can't do that yet: a job is already running."
    nothing = await agent.handle_command("voice2", "do the whole job", {})
    assert nothing["reply"] == "Can't do that yet: tell me what to fix, or load a scan."
    run = runjob.get(first["actions"][0]["args"]["run_id"])
    for _ in range(200):
        if run.status != "running":
            break
        await asyncio.sleep(0.01)
    assert run.status == "done"
