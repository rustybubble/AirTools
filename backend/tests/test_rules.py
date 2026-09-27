"""Rules and money check: jurisdiction, the honesty filters, program status, ENERGY STAR, caching
and OFFLINE, the endpoints and the agent tool. xAI, Census, ENERGY STAR and every fetched page are
respx-mocked with fixtures trimmed from live calls on 2026-09-26 (Atlanta mini-split, DeKalb water
heater); atlantaga.gov answers 403, as it does to any script."""

import asyncio
import copy
import json
import time
from datetime import date
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from server import agent, jobs, llm, rules
from server.app import app
from server.config import get_settings
from server.models import Dims, Part

FIX = Path(__file__).parent / "fixtures" / "rules"
ATLANTA = "225 North Ave NW, Atlanta, GA 30332"
DEKALB = "4380 Memorial Dr, Decatur, GA 30032"
BLOCKED_PAGE = (
    "https://www.atlantaga.gov/residents/city-hall/online-services/apply-for-a-technical-permit"
)


@pytest.fixture(autouse=True)
def _tmp_data(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    agent._sessions.clear()


def _load(name: str) -> dict:
    return json.loads((FIX / name).read_text())


def _reply_text(body: dict) -> dict:
    msg = next(o for o in body["output"] if o["type"] == "message")
    return json.loads(msg["content"][0]["text"])


def _with_reply(body: dict, reply: dict) -> dict:
    body = copy.deepcopy(body)
    msg = next(o for o in body["output"] if o["type"] == "message")
    msg["content"][0]["text"] = json.dumps(reply)
    return body


def _census(request: httpx.Request) -> httpx.Response:
    address = request.url.params["address"]
    if "North Ave" in address:
        return httpx.Response(200, json=_load("census_atlanta.json"))
    if "Memorial" in address:
        return httpx.Response(200, json=_load("census_dekalb.json"))
    return httpx.Response(200, json={"result": {"addressMatches": []}})


def _mock(router: respx.MockRouter, permit: dict | None = None, money: dict | None = None):
    """Every external source. `permit`/`money` override the xAI reply bodies."""

    def xai(request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        if body["text"]["format"]["name"] == "incentives":
            return httpx.Response(200, json=money or _load("xai_money_atlanta.json"))
        dekalb = "DeKalb" in body["input"]
        default = _load("xai_permit_dekalb.json" if dekalb else "xai_permit_atlanta.json")
        return httpx.Response(200, json=permit or default)

    def page(name: str) -> httpx.Response:
        return httpx.Response(200, html=(FIX / name).read_text())

    routes = {
        "xai": router.post(llm.XAI_RESPONSES_URL).mock(side_effect=xai),
        "census": router.get(rules.CENSUS_URL).mock(side_effect=_census),
        "energystar": router.get(rules.ENERGY_STAR_URL.format("akti-mt5s")).mock(
            return_value=httpx.Response(200, json=_load("energystar_knsah121b.json"))
        ),
        "atlanta": router.get(host="www.atlantaga.gov").mock(
            return_value=httpx.Response(403, text="Access denied")
        ),
        "heip": router.get(host="www.georgiapower.com").mock(return_value=page("page_heip.html")),
        "hear": router.get(host="energyrebates.georgia.gov").mock(
            return_value=page("page_hear.html")
        ),
        "irs": router.get(host="www.irs.gov").mock(return_value=page("page_irs25c.html")),
        "dekalb_pdf": router.get(host="dekalbcountyga.gov", path__regex=r"\.pdf$").mock(
            return_value=httpx.Response(
                200, content=b"%PDF-1.7", headers={"content-type": "application/pdf"}
            )
        ),
        "dekalb": router.get(host="dekalbcountyga.gov").mock(
            return_value=page("page_dekalb_permits.html")
        ),
    }
    return routes


def _lg_part(**kw) -> Part:
    return Part(
        id="lg-knsah121b-04da2e",
        name="Indoor Unit 12,000 BTU 20 SEER2 Wi-Fi 1-Zone Ductless Mini Split Heat Pump",
        manufacturer="LG",
        model_no="KNSAH121B",
        dims_mm=Dims(w=839, d=324, h=760),
        **kw,
    )


def _lg_outdoor() -> Part:
    return Part(
        id="lg-kusah121b",
        name="Outdoor Unit 12,000 BTU Ductless Mini Split Heat Pump",
        manufacturer="LG",
        model_no="KUSAH121B",
        dims_mm=Dims(w=770, d=300, h=545),
    )


async def _atlanta_check(router, parts: list[Part] | None = None, **mock_kw) -> dict:
    _mock(router, **mock_kw)
    juris = await rules.jurisdiction(ATLANTA, None)
    parts = [_lg_part()] if parts is None else parts
    return await rules.check(juris, "minisplit_install", parts, as_of=date(2026, 9, 26))


# --- jurisdiction -------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_jurisdiction_census_city_and_unincorporated_county():
    with respx.mock(assert_all_called=False) as router:
        _mock(router)
        atl = await rules.jurisdiction(ATLANTA, None)
        dek = await rules.jurisdiction(DEKALB, None)
    assert (atl.place, atl.county, atl.authority) == (
        "Atlanta city",
        "Fulton County",
        "Atlanta city",
    )
    assert (atl.source, atl.confidence) == ("census", "address")
    # the mailing city says Decatur; the permit comes from unincorporated DeKalb
    assert (dek.place, dek.authority, dek.state) == (None, "DeKalb County", "GA")


@pytest.mark.asyncio
async def test_jurisdiction_text_fallbacks():
    with respx.mock(assert_all_called=False) as router:
        census = router.get(rules.CENSUS_URL).mock(return_value=httpx.Response(500))
        city = await rules.jurisdiction(None, "Atlanta, GA")
        assert census.call_count == 0 and city.confidence == "city_only"
        assert city.authority == "Atlanta city"
        down = await rules.jurisdiction(DEKALB, None)  # Census down: the address text, flagged
        assert (down.source, down.place, down.confidence) == (
            "location_text",
            "Decatur city",
            "city_only",
        )
        census.mock(side_effect=_census)
        with pytest.raises(rules.NoMatch):
            await rules.jurisdiction("1 Nowhere Lane", None)
        assert (await rules.jurisdiction("1 Nowhere Lane", "Atlanta, GA")).place == "Atlanta city"


# --- code editions ---------------------------------------------------------------------------


def test_code_table_by_date():
    before = {r["code"]: r for r in rules.codes("GA", ("IMC", "NEC"), date(2026, 9, 26))}
    after = {r["code"]: r for r in rules.codes("GA", ("IMC", "NEC"), date(2027, 1, 1))}
    assert before["IMC"]["applies"] == rules.PRIOR
    assert before["IMC"]["next_edition"] == "2024 with Georgia Amendments"
    assert after["IMC"]["applies"] == "2024 with Georgia Amendments"
    assert after["NEC"]["applies"] == "2023 with 2026 Georgia Amendments"
    assert (
        after["NEC"]["effective"] == "2027-01-01"
        and "dca.georgia.gov" in after["NEC"]["source_url"]
    )


@pytest.mark.asyncio
async def test_edition_from_table_even_when_model_says_otherwise():
    body = _load("xai_permit_atlanta.json")
    reply = _reply_text(body)
    reply["code_basis"] = ["2024 IMC with Georgia Amendments, effective for permits Jan 1, 2026"]
    reply["caveats"].append("The 2024 IMC applies to permits pulled after Feb 1, 2026.")
    with respx.mock(assert_all_called=False) as router:
        result = await _atlanta_check(router, permit=_with_reply(body, reply))
    imc = next(r for r in result["codes"] if r["code"] == "IMC")
    assert imc["applies"] == rules.PRIOR and imc["effective"] == "2027-01-01"
    text = json.dumps(result)
    assert "Jan 1, 2026" not in text and "Feb 1, 2026" not in text


# --- permit ----------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_permit_replay_blocked_site_marked_unverified():
    with respx.mock(assert_all_called=False) as router:
        result = await _atlanta_check(router)
    permit = result["permit"]
    assert permit["required"] == "yes" and permit["dropped"] == 0
    assert [i["name"] for i in permit["items"]] == ["Mechanical (HVAC) Permit", "Electrical Permit"]
    for item in permit["items"]:  # kept, not dropped, and not claimed as verified
        assert item["quote_status"] == "unreadable"
        assert item["quote_note"] == "unverified (site blocks automated checks)"
    assert permit["office"]["name"] == "City of Atlanta Office of Buildings"
    # the model said "yes" but no page it cited could be read: stays unclear
    assert permit["who_can_pull"] == {
        "homeowner_allowed": "unclear",
        "model_said": "yes",
        "licence": permit["who_can_pull"]["licence"],
    }
    assert result["cost_usd"] == pytest.approx(0.1134 + 0.0764, abs=1e-3)


@pytest.mark.asyncio
async def test_uncited_url_dropped():
    body = _load("xai_permit_atlanta.json")
    reply = _reply_text(body)
    reply["permits"].append(
        {
            "name": "Building Permit",
            "office": "Office of Buildings",
            "fee_note": "$500",
            "url": "https://www.atlantaga.gov/made-up-page",
            "quote": "A building permit is required.",
        }
    )
    with respx.mock(assert_all_called=False) as router:
        result = await _atlanta_check(router, permit=_with_reply(body, reply))
    assert result["permit"]["dropped"] == 1
    assert "Building Permit" not in [i["name"] for i in result["permit"]["items"]]


@pytest.mark.asyncio
async def test_dekalb_replay_verified_quote_pdf_and_office_phone():
    with respx.mock(assert_all_called=False) as router:
        _mock(router)
        juris = await rules.jurisdiction(DEKALB, None)
        result = await rules.check(juris, "water_heater_swap", [])
    permit = result["permit"]
    by_status = {i["quote_status"]: i for i in permit["items"]}
    assert by_status["verified"]["url"].endswith("permits-plan-review-and-inspections")
    assert by_status["unreadable"]["quote_note"] == rules.NOT_HTML  # the plumbing PDF
    assert permit["who_can_pull"]["homeowner_allowed"] == "no"  # backed by a readable page
    assert permit["office"]["phone"] == "404-371-2155"
    assert {r["code"] for r in result["codes"]} == {"IRC", "IPC", "IFGC"}
    assert "DeKalb County Planning and Sustainability Department" in result["spoken"]


def test_quote_check_and_backed_amount():
    page = {"status": 200, "text": "Up to $500 Ductless Mini Split Heat Pump - Combined 2 ..."}
    assert rules.quote_check("Up to $500 Ductless Mini Split", page) == ("verified", None)
    assert rules.quote_check("Up to $800 Ductless Mini Split", page)[0] == "not_found"
    assert rules.quote_check("anything at all here", {"status": 403, "text": ""}) == (
        "unreadable",
        rules.BLOCKED,
    )
    assert rules.backed_amount("Up to $500", "Up to $500 Ductless", "verified") == 500
    # the number isn't in the quote, or the quote isn't on the page: no amount
    assert rules.backed_amount("$8,000", "not accepting new applications", "verified") is None
    assert rules.backed_amount("Up to $500", "Up to $500 Ductless", "unreadable") is None


# --- money -----------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_money_replay_closed_program_ended_credit_and_amounts():
    with respx.mock(assert_all_called=False) as router:
        result = await _atlanta_check(router, parts=[_lg_part(), _lg_outdoor()])
    rows = {r["program"]: r for r in result["money"]["incentives"]}
    assert set(rows) == {"georgia_power_heip", "fed_25c", "ga_hear"}
    # HEAR: closed per its own page; the model's "$8,000" isn't in its quote, so it's gone
    assert rows["ga_hear"]["status"] == "closed"
    assert rows["ga_hear"]["status_source"] == "https://energyrebates.georgia.gov/"
    assert rows["ga_hear"]["amount_usd"] is None
    # 25C: the model's "up to $2,000" isn't in its quote either; the IRS rule says ended
    assert rows["fed_25c"]["status"] == "ended" and rows["fed_25c"]["amount_usd"] is None
    # Georgia Power: tagged "other" by the model, matched by its URL; $500 is in a verified quote
    gp = rows["georgia_power_heip"]
    assert (gp["status"], gp["amount_usd"], gp["quote_status"]) == ("active", 500, "verified")
    spoken = result["spoken"]
    assert "up to 500 dollars" in spoken
    assert "The federal tax credit ended after 2025." in spoken
    assert "The state HEAR rebate isn't taking applications." in spoken
    assert spoken.endswith(rules.DISCLAIMER)


@pytest.mark.asyncio
async def test_fixed_rule_and_status_probe_beat_the_model():
    body = _load("xai_money_atlanta.json")
    reply = _reply_text(body)
    for inc in reply["incentives"]:
        inc["status"] = "active"
    with respx.mock(assert_all_called=False) as router:
        result = await _atlanta_check(router, money=_with_reply(body, reply))
    rows = {r["program"]: r for r in result["money"]["incentives"]}
    assert rows["fed_25c"]["status"] == "ended" and rows["ga_hear"]["status"] == "closed"


@pytest.mark.asyncio
async def test_energy_star_pair_flag():
    with respx.mock(assert_all_called=False) as router:
        _mock(router)
        alone = await rules.energy_star("KNSAH121B", "akti-mt5s", ["KNSAH121B"])
        both = await rules.energy_star("KNSAH121B", "akti-mt5s", ["KNSAH121B", "KUSAH121B"])
    assert alone["certified"] and (alone["seer2"], alone["hspf2"]) == (20.0, 9.0)
    assert alone["pair"] == {"indoor": "KNSAH121B", "outdoor": "KUSAH121B"}
    assert (alone["bom_has_pair"], alone["missing"]) == (False, "KUSAH121B")
    assert (both["bom_has_pair"], both["missing"]) == (True, None)


@pytest.mark.asyncio
async def test_grok_down_keeps_the_deterministic_sections():
    with respx.mock(assert_all_called=False) as router:
        routes = _mock(router)
        routes["xai"].mock(return_value=httpx.Response(500, json={"error": "down"}))
        juris = await rules.jurisdiction(ATLANTA, None)
        result = await rules.check(juris, "minisplit_install", [_lg_part()])
    assert result["permit"] is None and result["codes"]
    money = result["money"]
    assert money["research"] == "unavailable" and money["energy_star"]["missing"] == "KUSAH121B"
    rows = {r["program"]: r["status"] for r in money["incentives"]}
    # the BOM has only the indoor half of the certified pair
    assert rows == {"fed_25c": "ended", "georgia_power_heip": "not_eligible", "ga_hear": "closed"}
    assert "couldn't research the permit rules" in result["spoken"]


# --- eligibility: a rebate needs an ENERGY STAR certified system ------------------------------


def _tcl_part() -> Part:
    return Part(
        id="tcl-mini-split",
        name="TCL 12,000 BTU Ductless Mini Split Heat Pump",
        manufacturer="TCL",
        model_no="TAC-12HPB/DWL",
        dims_mm=Dims(w=805, d=195, h=285),
    )


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("case", "eligibility", "status", "reason", "spoken"),
    [
        ("pair", "eligible", "active", None, "Rebate: up to 500 dollars."),
        ("uncertified", "not_eligible", "not_eligible", rules.NOT_LISTED, "doesn't apply"),
        ("half_pair", "not_eligible", "not_eligible", "add the KUSAH121B", "doesn't apply"),
        ("lookup_failed", "unverified", "active", rules.NOT_CHECKED, "up to 500 dollars, but"),
    ],
)
async def test_rebate_eligibility(case, eligibility, status, reason, spoken):
    parts = {
        "pair": [_lg_part(), _lg_outdoor()],
        "uncertified": [_tcl_part()],  # ENERGY STAR has no row for it
        "half_pair": [_lg_part()],
        "lookup_failed": [_lg_part(), _lg_outdoor()],
    }[case]
    with respx.mock(assert_all_called=False) as router:
        routes = _mock(router)
        if case == "lookup_failed":
            routes["energystar"].mock(return_value=httpx.Response(500))
        juris = await rules.jurisdiction(ATLANTA, None)
        result = await rules.check(juris, "minisplit_install", parts, as_of=date(2026, 9, 26))
    money = result["money"]
    gp = next(r for r in money["incentives"] if r["program"] == "georgia_power_heip")
    assert (gp["eligibility"], gp["status"], gp["amount_usd"]) == (eligibility, status, 500)
    assert gp["eligibility_reason"] == reason or reason in gp["eligibility_reason"]
    assert gp["requires"]["energy_star"] and gp["requires"]["source_url"].endswith(".pdf")
    # only an eligible, live amount comes off the price
    assert gp["counted"] is (case == "pair")
    assert money["rebates_usd"] == (500 if case == "pair" else 0)
    assert spoken in result["spoken"]
    if case != "pair":
        assert "Rebate: up to 500 dollars." not in result["spoken"]
    # the show_rules payload is the same result
    job = SimpleNamespace(id="c1", status="done", result=result, error=None)
    shown = rules.body(job)["money"]["incentives"]
    assert (
        next(r for r in shown if r["program"] == "georgia_power_heip")["eligibility"] == eligibility
    )
    # HEAR's own page states ENERGY STAR; it's closed, so it stays closed either way
    hear = next(r for r in money["incentives"] if r["program"] == "ga_hear")
    assert (
        hear["status"] == "closed"
        and hear["requires"]["source_url"] == rules.PROGRAMS["GA"]["ga_hear"].url
    )


def test_requirement_from_page_else_table():
    hear = rules._visible((FIX / "page_hear.html").read_text())
    req = rules.page_requires("https://energyrebates.georgia.gov/", hear)
    assert req and req.energy_star and req.source_url == "https://energyrebates.georgia.gov/"
    heip = rules._visible((FIX / "page_heip.html").read_text())
    assert rules.page_requires("https://www.georgiapower.com/", heip) is None  # -> the table
    minimum = rules.page_requires("u", "Systems must be minimum 16 SEER2 and HSPF2 of at least 8.1")
    assert (minimum.seer2_min, minimum.hspf2_min, minimum.energy_star) == (16.0, 8.1, False)
    low = {"certified": True, "seer2": 15.2, "hspf2": 9.0}
    assert rules.eligibility(minimum, low) == (
        "not_eligible",
        "needs at least 16 SEER2; this system is 15.2",
    )
    assert rules.eligibility(None, low) == (None, None)


# --- cache, OFFLINE, endpoints ----------------------------------------------------------------


def test_endpoint_cache_and_offline(monkeypatch):
    jobs.save_part(_lg_part())
    req = {"part_id": "lg-knsah121b-04da2e", "address": ATLANTA}
    # `with`: one event loop for all requests, so the background job outlives its POST
    with respx.mock(assert_all_called=False) as router, TestClient(app) as client:
        routes = _mock(router)
        monkeypatch.setenv("OFFLINE", "1")
        get_settings.cache_clear()
        assert client.post("/rules/check", json=req).status_code == 503  # nothing cached yet
        monkeypatch.setenv("OFFLINE", "0")
        get_settings.cache_clear()

        first = client.post("/rules/check", json=req).json()
        for _ in range(50):
            if first["status"] != "running":
                break
            time.sleep(0.05)
            first = client.get(f"/rules/check/{first['check_id']}").json()
        assert first["status"] == "done" and first["job"] == "minisplit_install"
        calls = {name: r.call_count for name, r in routes.items()}
        assert calls["xai"] == 2

        second = client.post("/rules/check", json=req).json()  # warm: done at once, no HTTP
        assert second["status"] == "done"
        assert {name: r.call_count for name, r in routes.items()} == calls

        monkeypatch.setenv("OFFLINE", "1")
        get_settings.cache_clear()
        offline = client.post("/rules/check", json=req).json()
        assert offline["status"] == "done" and offline["spoken"] == first["spoken"]
        assert {name: r.call_count for name, r in routes.items()} == calls
    assert first["label"].endswith(rules.DISCLAIMER)


def test_endpoint_errors():
    client = TestClient(app)
    assert client.post("/rules/check", json={}).status_code == 400
    assert client.post("/rules/check", json={"job": "roof_moonshot"}).status_code == 400
    assert client.get("/rules/check/nope").status_code == 404
    with respx.mock(assert_all_called=False) as router:
        _mock(router)
        bad = {"job": "water_heater_swap", "address": "1 Nowhere Lane"}
        assert client.post("/rules/check", json=bad).status_code == 422


def test_unknown_job_spends_nothing():
    jobs.save_part(_lg_part().model_copy(update={"id": "odd-part", "name": "Garden gnome"}))
    with respx.mock(assert_all_called=False) as router, TestClient(app) as client:
        routes = _mock(router)
        body = client.post("/rules/check", json={"part_id": "odd-part"}).json()
    assert body["status"] == "done" and body["permit"]["required"] == "unknown"
    assert routes["xai"].call_count == 0


# --- agent ------------------------------------------------------------------------------------


async def _no_llm(*a, **kw):
    raise AssertionError("the fast path must not call the LLM")


@pytest.mark.asyncio
async def test_agent_permit_question_fast_path(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    jobs.save_part(_lg_part())
    ctx = {"selected_part_id": "lg-knsah121b-04da2e", "address": ATLANTA}
    with respx.mock(assert_all_called=False) as router:
        _mock(router)
        out = await agent.handle_command("s1", "Do I need a permit for this?", ctx)
    names = [a["name"] for a in out["actions"]]
    assert names == ["rules_started", "show_rules"]
    shown = out["actions"][1]["args"]
    assert out["reply"] == shown["spoken"] and shown["disclaimer"] == rules.DISCLAIMER
    assert out["reply"].startswith("You'll need a mechanical (HVAC) permit and an electrical")


@pytest.mark.asyncio
async def test_agent_slow_check_rides_on_the_next_command(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm)
    monkeypatch.setattr(agent, "RULES_WAIT_S", 0.01)
    gate = asyncio.Event()

    async def slow_check(juris, job, parts, as_of=None):
        await gate.wait()
        return {"spoken": "done line", "label": rules.LABEL}

    monkeypatch.setattr(rules, "check", slow_check)
    first = await agent.handle_command("s2", "any rebates for a water heater?", {})
    assert [a["name"] for a in first["actions"]] == ["rules_started"]
    assert first["reply"].startswith("Checking Atlanta's rules")
    gate.set()
    await asyncio.sleep(0.01)
    second = await agent.handle_command("s2", "grab the tape", {})
    assert [a["name"] for a in second["actions"]] == ["show_rules", "equip_tool"]
    assert second["actions"][0]["args"]["spoken"] == "done line"
