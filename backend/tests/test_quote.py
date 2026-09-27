"""Paper-quote checker (F20): arithmetic on printed values only, printed-vs-inferred, the F13
permit/rebate/pair flags, the F6 recall flag, price spreads, the read cache, OFFLINE, the
endpoint and the agent tool. The Grok read is faked with the synthetic quotes' ground truth
(tests/fixtures/quotes/truth.json); F13's check and CPSC are faked with small dicts."""

import base64
import copy
import io
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, jobs, llm, quote, rules, safety, search
from server.app import app
from server.config import get_settings
from server.models import Dims, Part, Seller

FIX = Path(__file__).parent / "fixtures"
TRUTH = json.loads((FIX / "quotes" / "truth.json").read_text())
CLEAN_PNG = (FIX / "quotes" / "clean.png").read_bytes()
BAD_PNG = (FIX / "quotes" / "bad.png").read_bytes()
CPSC_MIDEA = json.loads((FIX / "cpsc_recalls_midea.json").read_text())
ATLANTA = "225 North Ave NW, Atlanta, GA 30332"
JURIS = rules.Juris(
    state="GA",
    county="Fulton County",
    place="Atlanta city",
    authority="Atlanta city",
    source="census",
    confidence="address",
)


def _part(pid: str, maker: str, model: str, name: str, price: float) -> Part:
    seller = Seller(name="Home Depot", price_usd=price, unit_price_usd=price, verified=True)
    return Part(
        id=pid,
        name=name,
        manufacturer=maker,
        model_no=model,
        dims_mm=Dims(w=1, d=1, h=1),
        sellers=[seller, Seller(name="Some LLM guess", price_usd=10.0, verified=False)],
    )


LG = _part("lg-knsah121b-04da2e", "LG", "KNSAH121B", "Indoor Unit 12,000 BTU Ductless Mini", 498)
MIDEA = _part("midea-maw08u1qwt-8d6d08", "Midea", "MAW08U1QWT", "8,000 BTU Window AC", 379)


def _rules_result(required: str = "yes", missing: str | None = None) -> dict:
    """The fields of an F13 result the quote checker reads."""
    return {
        "job": "minisplit_install",
        "jurisdiction": JURIS.model_dump(),
        "permit": {
            "required": required,
            "items": [{"name": "Mechanical (HVAC) Permit"}, {"name": "Electrical Permit"}],
        },
        "money": {
            "energy_star": {"model": "KNSAH121B", "certified": True, "missing": missing},
            "incentives": [
                {
                    "name": "Georgia Power Home Energy Improvement Program",
                    "amount_usd": 500.0,
                    "status": "active",
                    "note": "Georgia Power customers only",
                },
                {
                    "name": "Georgia Home Energy Rebates (HEAR)",
                    "amount_usd": None,
                    "status": "closed",
                },
            ],
        },
        "spoken": "…",
        "label": rules.LABEL,
    }


@pytest.fixture(autouse=True)
def fakes(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    agent._sessions.clear()
    jobs.save_part(LG)
    jobs.save_part(MIDEA)
    f = SimpleNamespace(
        reply=TRUTH["bad"], chat_calls=0, searches=[], rules_calls=[], rules_result=_rules_result()
    )

    async def fake_chat(role, messages, tools=None, **kw):
        assert role == "quote" and kw["response_format"]["json_schema"]["strict"]
        f.chat_calls += 1
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(f.reply)))],
            usage=SimpleNamespace(cost_in_usd_ticks=12_000_000),  # $0.0012
        )

    async def fake_find_parts(req, progress=None):
        f.searches.append(req.query)
        return []

    async def fake_juris(address, location):
        return JURIS

    async def fake_rules_check(juris, job, parts, as_of=None):
        f.rules_calls.append((job, [p.model_no for p in parts]))
        return f.rules_result

    async def fake_cpsc(manufacturer):
        return CPSC_MIDEA if manufacturer == "Midea" else []

    monkeypatch.setattr(llm, "chat", fake_chat)
    monkeypatch.setattr(search, "find_parts", fake_find_parts)
    monkeypatch.setattr(rules, "jurisdiction", fake_juris)
    monkeypatch.setattr(rules, "check", fake_rules_check)
    monkeypatch.setattr(safety, "cpsc_recalls", fake_cpsc)
    return f


def _post(data: bytes, name: str = "quote.png", **form):
    with TestClient(app) as client:
        return client.post("/quote/check", files={"file": (name, data)}, data=form)


# --- arithmetic ---------------------------------------------------------------------------


def test_clean_quote_arithmetic_adds_up():
    # tax is 8.9 % of equipment and materials only, which the rate check accepts
    assert quote.arithmetic(copy.deepcopy(TRUTH["clean"])) == []


def test_bad_quote_flags_only_the_wrong_line():
    flags = quote.arithmetic(copy.deepcopy(TRUTH["bad"]))
    assert [(f["code"], f["line"]) for f in flags] == [("math_line", 5)]
    assert "$90.00" in flags[0]["text"] and "$100.00" in flags[0]["text"]


def test_subtotal_total_and_tax_mismatches():
    r = copy.deepcopy(TRUTH["clean"])
    r["subtotal"]["value"] = 4900.0
    assert [f["code"] for f in quote.arithmetic(r)] == ["math_subtotal", "math_total"]

    r = copy.deepcopy(TRUTH["clean"])
    r["tax"]["value"], r["total"]["value"] = 400.0, 5279.0
    assert [f["code"] for f in quote.arithmetic(r)] == ["tax_rate"]

    r["tax_rate_pct"] = {"value": None, "source": "inferred"}  # no printed rate: sanity only
    assert quote.arithmetic(r) == []
    r["tax"]["value"], r["total"]["value"] = 900.0, 5779.0  # 18 % of the subtotal
    assert [f["code"] for f in quote.arithmetic(r)] == ["tax_rate"]


def test_an_inferred_value_is_never_flagged_as_wrong():
    r = copy.deepcopy(TRUTH["bad"])
    r["lines"][4]["line_total"]["source"] = "inferred"  # the model worked out the $100.00
    r["subtotal"]["source"] = "inferred"
    assert quote.arithmetic(r) == []

    r = copy.deepcopy(TRUTH["clean"])  # the lump-sum labour line has no printed qty/unit
    labor = r["lines"][6]
    assert labor["qty"]["source"] == labor["unit_price"]["source"] == "inferred"
    labor["qty"]["value"], labor["unit_price"]["value"] = 8, 99.0  # a wrong guess, not flagged
    assert quote.arithmetic(r) == []


def test_pdf_text_demotes_numbers_that_are_not_printed():
    r = copy.deepcopy(TRUTH["bad"])
    text = "Wall sleeve 2 45.00 90.00 ..."  # the page doesn't say 100.00 (or anything else)
    assert quote.demote_unprinted(r, text) > 0
    line = r["lines"][4]
    assert (
        line["qty"]["source"] == "printed_text" and line["unit_price"]["source"] == "printed_text"
    )
    assert line["line_total"]["source"] == "inferred"


def test_image_pdf_goes_as_page_images_and_junk_is_refused():
    buf = io.BytesIO()
    Image.open(io.BytesIO(BAD_PNG)).convert("RGB").save(buf, "PDF")
    kind, content, text = quote.prepare(buf.getvalue())
    assert (kind, text) == ("pdf_image", None)
    assert [c["type"] for c in content] == ["text", "image_url"]
    with pytest.raises(quote.BadQuote):
        quote.prepare(b"not a quote")
    assert _post(b"not a quote").status_code == 400


# --- the whole check ----------------------------------------------------------------------


def test_bad_quote_flags_math_missing_permit_recall_and_shows_spreads(fakes):
    resp = _post(BAD_PNG, "bad.png", address=ATLANTA)
    assert resp.status_code == 200
    body = resp.json()
    codes = [f["code"] for f in body["flags"]]
    assert {"math_line", "missing_permit", "recall", "rebate", "price_spread"} <= set(codes)
    assert body["job"] == "minisplit_install" and body["rules"]["permit_required"] == "yes"
    # F13 gets the equipment, the mini-split first (the ENERGY STAR pair is read off parts[0])
    assert fakes.rules_calls == [("minisplit_install", ["KNSAH121B", "KUSAH121B", "MAW08U1QWT"])]

    midea = body["lines"][2]
    assert [f["code"] for f in midea["flags"]] == ["recall", "price_spread"]
    assert "Recalled June 2025" in midea["flags"][0]["text"]

    lg = body["lines"][0]["compare"]
    # the verified seller, not the cheaper unverified one; a spread, never a verdict
    assert (lg["seller"], lg["seller_price"], lg["quote_price"]) == ("Home Depot", 498.0, 1195.0)
    assert (lg["spread_usd"], lg["spread_pct"]) == (697.0, 140.0)
    assert body["lines"][1]["compare"] is None  # KUSAH121B: not cached, the search found nothing
    assert fakes.searches == ["LG KUSAH121B"]
    assert "overcharg" not in json.dumps(body).lower()

    spoken = body["spoken"]
    assert spoken.startswith("I read 7 lines. Line 5: 2 × $45.00 is $90.00")
    assert "No permit line, but Atlanta requires permits for this job" in spoken
    assert "Line 3: this exact model was recalled in June 2025 for risk of mold" in spoken
    assert "Georgia Power Home Energy Improvement Program may have a rebate of up to $500" in spoken
    permit = next(f for f in body["flags"] if f["code"] == "missing_permit")
    assert "requires a mechanical (HVAC) permit and an electrical permit" in permit["text"]
    assert body["cost_usd"] == pytest.approx(0.0012) and body["cached"] is False


def test_clean_quote_has_no_warnings(fakes):
    fakes.reply = TRUTH["clean"]
    body = _post(CLEAN_PNG, address=ATLANTA).json()
    assert [f for f in body["flags"] if f["severity"] == "warn"] == []
    assert "The printed math adds up." in body["spoken"]
    assert body["totals"]["labor"] == 1400.0 and body["totals"]["permit_fee"] == 185.0
    assert body["inferred"] == 0  # the unprinted labour qty/unit are null, not guesses


def test_energy_star_pair_flag_and_no_permit_flag_when_not_required(fakes):
    fakes.reply = TRUTH["clean"]
    fakes.rules_result = _rules_result(required="depends", missing="KUSAH121B")
    codes = [f["code"] for f in _post(CLEAN_PNG, address=ATLANTA).json()["flags"]]
    assert "energy_star_pair" in codes and "missing_permit" not in codes


def test_at_most_one_live_search_and_only_an_exact_model_counts(fakes, monkeypatch):
    r = copy.deepcopy(TRUTH["bad"])
    r["lines"][0]["model_no"]["value"] = "ZZ100"  # two uncached equipment models now
    fakes.reply = r
    found = _part("lg-zz100", "LG", "ZZ-100", "LG unit", 900)

    async def one_hit(req, progress=None):
        fakes.searches.append(req.query)
        return [_part("lg-other", "LG", "ZZ1000", "near miss", 1), found]

    monkeypatch.setattr(search, "find_parts", one_hit)
    body = _post(BAD_PNG).json()
    assert fakes.searches == ["LG ZZ100"]
    assert body["lines"][0]["compare"]["seller_price"] == 900.0
    assert body["lines"][1]["compare"] is None


def test_read_is_cached_by_file_hash_and_served_offline(fakes, monkeypatch):
    first = _post(BAD_PNG, address=ATLANTA).json()
    again = _post(BAD_PNG, address=ATLANTA).json()
    assert fakes.chat_calls == 1
    assert (again["cached"], again["cost_usd"], again["quote_id"]) == (True, 0.0, first["quote_id"])

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    offline = _post(BAD_PNG, address=ATLANTA)
    assert offline.status_code == 200 and fakes.chat_calls == 1
    assert offline.json()["flags"] == first["flags"]
    assert _post(CLEAN_PNG).status_code == 503  # never read: nothing to serve


def test_rules_unavailable_is_a_note_not_a_failure(fakes, monkeypatch):
    async def no_place(address, location):
        raise rules.NoMatch("nope")

    monkeypatch.setattr(rules, "jurisdiction", no_place)
    body = _post(BAD_PNG, address="nowhere").json()
    assert body["rules"] is None and body["rules_note"] == "couldn't place the address"
    assert "missing_permit" not in [f["code"] for f in body["flags"]]


# --- agent ----------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "tool"),
    [
        ("check this quote", "check_quote"),
        ("Can you look over this estimate?", "check_quote"),
        ("check the quote for recalls", "check_quote"),  # the quote check covers recalls
        ("read this bid and check the permit", "check_quote"),
        ("do I need a permit?", "check_rules"),
        ("is this recalled?", "check_safety"),
        ("send the quote to my contractor", "send_packet"),
        ("show me the report on the quote", "make_report"),
    ],
)
def test_check_this_quote_routes_apart_from_rules_and_safety(text, tool):
    assert agent._match_fast_intent(text)[0] == tool


def test_a_price_question_is_left_to_the_llm():
    assert agent._match_fast_intent("what's a fair quote for a mini-split?") is None


@pytest.mark.asyncio
async def test_agent_check_quote_reads_the_frame(fakes):
    ctx = {"frame_jpg_b64": base64.b64encode(BAD_PNG).decode(), "address": ATLANTA}
    result = await agent.handle_command("q1", "check this quote", ctx)
    [action] = result["actions"]
    assert action["name"] == "show_quote_check"
    assert action["args"]["lines"][4]["flags"][0]["code"] == "math_line"
    assert result["reply"] == action["args"]["spoken"]

    none = await agent.handle_command("q2", "check this quote", {})
    assert none["reply"] == "Can't do that yet: hold the quote up to the camera."
    assert none["actions"] == [] and fakes.chat_calls == 1


def test_unverified_rebate_says_so():
    """F13's eligibility fix: an unverified rebate is still shown, with its reason."""
    result = _rules_result()
    result["money"]["incentives"][0].update(
        eligibility="unverified", eligibility_reason=rules.NOT_CHECKED
    )
    flag = next(f for f in quote._missing(result, {"lines": []}) if f["code"] == "rebate")
    assert flag["text"].endswith(f"Unverified: it {rules.NOT_CHECKED}.")
