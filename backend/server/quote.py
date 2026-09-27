"""Paper-quote checker (F20; G5 idea 7, G6 "older ideas still strongest").

The homeowner holds a contractor's quote up to the headset, or uploads a photo or PDF:
1. Read: one fast Grok vision call with a strict schema. Every value carries
   `source: printed_text | inferred`, and only printed values count as read. A PDF with text is
   sent as text, and a "printed" number that isn't in that text is demoted to inferred.
2. Arithmetic, in code: line totals, the subtotal, tax rate sanity, the grand total. A check
   needs every value it uses printed, so an inferred value is never flagged as wrong.
3. Prices: equipment lines matched to our cached parts by model number (at most one live parts
   search per quote), shown as "quote $X vs lowest verified seller $Y". A spread, never a verdict.
4. What's missing: F13 (`server/rules.py`) permit and rebates and the ENERGY STAR pair; F6
   (`server/safety.py`) recalls, CPSC by model plus F6's Grok leg only when already cached.

The read is cached by the file's sha256; OFFLINE serves it, and a miss raises cache.OfflineMiss.
"""

import asyncio
import base64
import hashlib
import io
import logging
import re
import time
from pathlib import Path
from typing import Literal

import httpx
import openai
import pydantic
import pypdf
from PIL import Image

from server import cache, jobs, llm, rules, safety, search
from server.config import get_settings
from server.models import Dims, Part, SearchRequest

logger = logging.getLogger(__name__)

MAX_BYTES = 20 * 1024 * 1024  # xAI's per-image cap
MAX_PAGES = 3
MAX_TEXT = 20_000
SEND_PX = 2048  # long side; printed text needs the pixels (G6: size isn't the latency lever)
TOL = 0.011  # a cent, plus rounding
MAX_TAX_PCT = 12.0  # above any US combined sales-tax rate
RULES_WAIT_S = 30.0
LABEL = (
    "AI reading of the quote: check it against the paper. Price spreads compare listed "
    "equipment prices, not installed cost. Not legal advice; confirm with the permitting office."
)

Source = Literal["printed_text", "inferred"]


class Num(pydantic.BaseModel):
    value: float | None
    source: Source


class Text(pydantic.BaseModel):
    value: str | None
    source: Source


class Line(pydantic.BaseModel):
    description: str
    kind: Literal["equipment", "material", "labor", "permit", "other"]
    brand: Text
    model_no: Text
    qty: Num
    unit_price: Num
    line_total: Num


class Read(pydantic.BaseModel):
    contractor: Text
    lines: list[Line]
    subtotal: Num
    tax_rate_pct: Num
    tax: Num
    total: Num


PROMPT = (
    "This is a contractor's quote or estimate for home-improvement work. Transcribe it into the "
    "schema; never compute or correct anything. For every value, source is 'printed_text' when "
    "the value is printed on the page, copied exactly as printed even if the arithmetic looks "
    "wrong; 'inferred' when you worked it out or guessed it; value null with source 'inferred' "
    "when there is nothing to go on. lines: every priced row, in order. kind: equipment for a "
    "unit with a make or model, material for supplies, labor for installation work, permit for "
    "a permit or inspection fee, other otherwise. brand and model_no: the equipment's maker and "
    "model number. tax_rate_pct is a percent, not a fraction. subtotal, tax and total are the "
    "quote's own summary rows."
)


class BadQuote(ValueError):
    """Not an image or a PDF we can read (the endpoint's 400)."""


class ReadFailed(RuntimeError):
    """The model call failed or returned something off-schema (the endpoint's 502)."""


# --- read ---------------------------------------------------------------------------------


def _jpeg_url(img: Image.Image) -> str:
    img = img.convert("RGB")
    img.thumbnail((SEND_PX, SEND_PX))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=90)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


def prepare(data: bytes) -> tuple[str, list[dict], str | None]:
    """(kind, message content, the PDF's text or None). A PDF with text goes as text; a scanned
    one as its page images (pypdf's embedded images, up to MAX_PAGES)."""
    if data.startswith(b"%PDF"):
        try:
            pages = pypdf.PdfReader(io.BytesIO(data)).pages[:MAX_PAGES]
            text = "\n".join(p.extract_text() or "" for p in pages).strip()
            if len(text) >= 40:
                msg = f"{PROMPT}\n\nThe quote's text, extracted from a PDF:\n{text[:MAX_TEXT]}"
                return "pdf_text", [{"type": "text", "text": msg}], text
            images = [im.image for p in pages for im in p.images][:MAX_PAGES]
        except (pypdf.errors.PyPdfError, OSError, ValueError) as exc:
            raise BadQuote(f"can't read the PDF: {exc}") from exc
        if not images:
            raise BadQuote("the PDF has no text or images")
        kind = "pdf_image"
    else:
        try:
            images = [Image.open(io.BytesIO(data))]
            images[0].load()
        except (OSError, SyntaxError, ValueError) as exc:
            raise BadQuote("not an image or a PDF") from exc
        kind = "image"
    content: list[dict] = [{"type": "text", "text": PROMPT}]
    content += [
        {"type": "image_url", "image_url": {"url": _jpeg_url(im), "detail": "high"}}
        for im in images
    ]
    return kind, content, None


def _nums(read: dict) -> list[dict]:
    fields = [read[k] for k in ("subtotal", "tax_rate_pct", "tax", "total")]
    return fields + [ln[k] for ln in read["lines"] for k in ("qty", "unit_price", "line_total")]


def demote_unprinted(read: dict, text: str) -> int:
    """A "printed" number that isn't in the PDF's own text becomes inferred; returns how many."""
    on_page = {round(n, 2) for n in rules._numbers(text)}
    n = 0
    for f in _nums(read):
        printed = f["source"] == "printed_text" and f["value"] is not None
        if printed and round(f["value"], 2) not in on_page:
            f["source"], n = "inferred", n + 1
    return n


async def _ask(content: list[dict]) -> dict:
    schema = llm._strict_schema(Read.model_json_schema())
    start = time.monotonic()
    try:
        resp = await llm.chat(
            "quote",
            [{"role": "user", "content": content}],
            response_format={
                "type": "json_schema",
                "json_schema": {"name": "Quote", "strict": True, "schema": schema},
            },
            temperature=0,
            max_retries=0,  # one call = one bill
            timeout=90,
        )
        read = Read.model_validate_json(resp.choices[0].message.content or "")
    except (openai.APIError, httpx.HTTPError, pydantic.ValidationError) as exc:
        raise ReadFailed(f"couldn't read the quote: {exc}") from exc
    cost = llm.cost_usd(resp.usage)
    latency = round(time.monotonic() - start, 1)
    logger.info("quote: read %d lines cost_usd=%s latency_s=%s", len(read.lines), cost, latency)
    return {"read": read.model_dump(), "cost_usd": cost, "latency_s": latency}


async def read(data: bytes) -> tuple[dict, bool]:
    """({kind, read, cost_usd, latency_s, demoted}, cached), cached by the file's sha256."""
    key = hashlib.sha256(data).hexdigest()

    async def fetch() -> dict:
        kind, content, text = await asyncio.to_thread(prepare, data)
        out = await _ask(content)
        return {"kind": kind, **out, "demoted": demote_unprinted(out["read"], text) if text else 0}

    hit = cache.get("quote_read", key) is not None
    return {"quote_id": key[:12], **await cache.cached("quote_read", key, fetch)}, hit


# --- checks -------------------------------------------------------------------------------


def _printed(f: dict) -> float | None:
    return f["value"] if f["source"] == "printed_text" else None


def _usd(x: float) -> str:
    return f"${x:,.2f}"


def _flag(
    code: str,
    severity: Literal["warn", "info"],
    text: str,
    line: int | None = None,
    say: str | None = None,  # the shorter spoken form, when `text` is long
):
    return {"code": code, "severity": severity, "text": text, "line": line, "say": say or text}


def arithmetic(read: dict) -> list[dict]:
    """Line totals, subtotal, tax, grand total. Each check runs only on printed values."""
    flags, totals, goods = [], [], []
    for i, ln in enumerate(read["lines"], 1):
        q, u, t = (_printed(ln[k]) for k in ("qty", "unit_price", "line_total"))
        totals.append(t)
        if ln["kind"] not in ("labor", "permit"):
            goods.append(t)
        if None not in (q, u, t) and abs(q * u - t) > TOL:
            text = f"Line {i}: {q:g} × {_usd(u)} is {_usd(q * u)}, but it says {_usd(t)}."
            flags.append(_flag("math_line", "warn", text, i))
    sub, rate, tax, total = (
        _printed(read[k]) for k in ("subtotal", "tax_rate_pct", "tax", "total")
    )
    lines_sum = sum(totals) if totals and None not in totals else None
    if sub is not None and lines_sum is not None and abs(lines_sum - sub) > TOL:
        text = f"The lines add up to {_usd(lines_sum)}, but the subtotal says {_usd(sub)}."
        flags.append(_flag("math_subtotal", "warn", text))
    base = sub if sub is not None else lines_sum
    if tax is not None and base:
        goods_sum = sum(goods) if goods and None not in goods else None
        if rate is not None:
            # the rate may apply to everything or only to equipment and materials
            bases = [b for b in (base, goods_sum) if b]
            if rate > MAX_TAX_PCT or not any(abs(b * rate / 100 - tax) <= TOL for b in bases):
                text = f"Tax of {_usd(tax)} doesn't match {rate:g} % of the subtotal"
                text += " or of the equipment and materials." if goods_sum else "."
                flags.append(_flag("tax_rate", "warn", text))
        elif not 0 <= tax / base * 100 <= MAX_TAX_PCT:
            text = f"Tax is {tax / base * 100:.1f} % of the subtotal, above US sales-tax rates."
            flags.append(_flag("tax_rate", "warn", text))
    raw_tax = read["tax"]
    tax_known = tax is not None or raw_tax["value"] is None  # printed, or no tax row at all
    if total is not None and base is not None and tax_known:
        want = base + (tax or 0)
        if abs(want - total) > TOL:
            text = f"Subtotal plus tax is {_usd(want)}, but the total says {_usd(total)}."
            flags.append(_flag("math_total", "warn", text))
    return flags


def _key(model: str | None) -> str:
    return re.sub(r"[^A-Z0-9]", "", (model or "").upper())


def cached_parts() -> dict[str, Part]:
    """Our cached parts (DATA_DIR/parts) by model-number key."""
    out: dict[str, Part] = {}
    for path in sorted((Path(get_settings().DATA_DIR) / "parts").glob("*/part.json")):
        part = jobs.load_part(path.parent.name)
        if part and _key(part.model_no):
            out.setdefault(_key(part.model_no), part)
    return out


async def _search_one(brand: str | None, model: str) -> Part | None:
    """The quote's one live parts search (find_parts caches, and serves only its cache OFFLINE).
    Only an exact model match counts: a similar unit's price isn't this unit's price."""
    query = " ".join(x for x in (brand, model) if x)
    try:
        found = await search.find_parts(SearchRequest(query=query))
    except Exception as exc:  # noqa: BLE001 - a failed search just means no comparison
        logger.info("quote: search %r failed: %s", query, exc)
        return None
    return next((p for p in found if _key(p.model_no) == _key(model)), None)


def compare(price: float | None, part: Part) -> dict | None:
    sellers = [s for s in part.sellers if s.verified and (s.unit_price_usd or s.price_usd)]
    best = min(sellers, key=lambda s: s.unit_price_usd or s.price_usd, default=None)
    if best is None:
        return None
    low = best.unit_price_usd or best.price_usd
    out = {"part_id": part.id, "seller": best.name, "seller_price": low, "seller_url": best.url}
    if price is None:
        return out | {
            "quote_price": None,
            "spread_usd": None,
            "spread_pct": None,
            "text": f"lowest verified seller {_usd(low)} ({best.name}); "
            "the quote's price for it isn't printed",
        }
    return out | {
        "quote_price": price,
        "spread_usd": round(price - low, 2),
        "spread_pct": round((price - low) / low * 100, 1),
        "text": f"quote {_usd(price)} vs lowest verified seller {_usd(low)} ({best.name})",
    }


async def recall(part: Part) -> safety.SafetyReport | None:
    """F6's verdict from CPSC (free, cached a day, the evidence) plus F6's Grok leg only if it's
    already cached: a quote with five units must not start five $0.10 searches."""
    if not part.manufacturer:
        return None
    try:
        cpsc = await safety.cpsc_recalls(part.manufacturer)
    except (httpx.HTTPError, cache.OfflineMiss, ValueError) as exc:
        logger.info("quote: cpsc %s failed: %s", part.manufacturer, exc)
        cpsc = None
    report = safety.merge(part, cpsc, cache.get("safety_grok", part.id))
    return report if report.verdict in ("recalled", "caution") else None


async def _rules(job: str, parts: list[Part], address: str | None, location: str | None):
    """(F13 result, None) or (None, why not). The check runs as F13's background job, so one
    that outlasts RULES_WAIT_S still finishes and caches."""
    try:
        juris = await rules.jurisdiction(address, location)
    except cache.OfflineMiss:
        return None, "offline, and that address isn't cached"
    except rules.NoMatch:
        return None, "couldn't place the address"
    check = await jobs.wait(rules.start(juris, job, parts).id, RULES_WAIT_S)
    if check.status == "done":
        return check.result | {"check_id": check.id}, None
    if check.status == "running":
        return None, f"still running: GET /rules/check/{check.id}"
    return None, "the permit and rebate check failed"


def _missing(result: dict, read: dict) -> list[dict]:
    """F13's facts against the quote: permit line, rebates, the ENERGY STAR pair."""
    flags = []
    permit = result.get("permit") or {}
    where = rules.where(rules.Juris(**result["jurisdiction"]))
    has_permit = any(ln["kind"] == "permit" for ln in read["lines"])
    if permit.get("required") == "yes" and not has_permit:
        items = permit.get("items", [])[:2]
        names = " and ".join(rules._permit_phrase(i["name"]) for i in items) or "a permit"
        text = f"No permit line, but {where} requires {names} for this job. Ask who pulls it."
        say = f"No permit line, but {where} requires permits for this job; ask the permit office."
        flags.append(_flag("missing_permit", "warn", text, say=say))
    money = result.get("money") or {}
    es = money.get("energy_star") or {}
    if es.get("missing"):
        text = f"Only half of the ENERGY STAR pair is quoted: rebates need the {es['missing']} too."
        flags.append(_flag("energy_star_pair", "warn", text))
    for inc in money.get("incentives", []):
        if inc["status"] == "active":
            upto = f"up to ${inc['amount_usd']:,.0f}" if inc.get("amount_usd") else ""
            note = f" ({inc['note']})" if inc.get("note") else ""
            text = f"You may be eligible for {inc['name']}{': ' + upto if upto else ''}{note}."
            who = inc.get("provider") or inc["name"]
            say = f"{who} may have a rebate{' of ' + upto if upto else ''}."
            if inc.get("eligibility") == "unverified":  # F13's fix: shown, never counted
                text += f" Unverified: it {inc['eligibility_reason']}."
            flags.append(_flag("rebate", "info", text, say=say))
    return flags


def spoken(read: dict, flags: list[dict], lines: list[dict]) -> str:
    """Short and neutral: what was read, then the first flag of each kind."""
    n = len(read["lines"])
    bits = [f"I read {n} line{'s' if n != 1 else ''}."]
    math = [f for f in flags if f["code"].startswith(("math", "tax"))]
    if len(math) > 1:
        bits.append(f"{len(math)} amounts don't add up. {math[0]['text']}")
    elif math:
        bits.append(math[0]["text"])
    else:
        bits.append("The printed math adds up.")
    for code in ("missing_permit", "recall", "energy_star_pair", "rebate"):
        first = next((f for f in flags if f["code"] == code), None)
        if first:
            bits.append(first["say"])
    compared = [
        ln["compare"] for ln in lines if (ln["compare"] or {}).get("spread_usd") is not None
    ]
    if compared:
        quoted = sum(c["quote_price"] for c in compared)
        low = sum(c["seller_price"] for c in compared)
        units = f"{len(compared)} unit{'s' if len(compared) > 1 else ''}"
        bits.append(
            f"The {units} I could price come to {_usd(quoted)} here; verified sellers list "
            f"{'them' if len(compared) > 1 else 'it'} from {_usd(low)}."
        )
    return " ".join(bits)


async def check(data: bytes, address: str | None = None, location: str | None = None) -> dict:
    """The whole check of one quote file. BadQuote, ReadFailed or cache.OfflineMiss on the read;
    every later source degrades to a note instead."""
    got, hit = await read(data)
    r = got["read"]
    flags = arithmetic(r)
    known = cached_parts()

    # equipment lines: our cached part by model number, else one live search, else a stub
    # (name, brand, model) so F6/F13 can still look the model up
    parts: list[Part | None] = []
    searched = False
    for ln in r["lines"]:
        model, brand = ln["model_no"]["value"], ln["brand"]["value"]
        part = known.get(_key(model)) if model else None
        if model and part is None and ln["kind"] == "equipment" and not searched:
            searched, part = True, await _search_one(brand, model)
        if model and part is None and _key(model):
            part = Part(
                id=f"quote-{_key(model).lower()}",
                name=ln["description"],
                manufacturer=brand,
                model_no=model,
                dims_mm=Dims(w=0, d=0, h=0),
            )
        parts.append(part)

    job = rules.job_for(" ".join(ln["description"] for ln in r["lines"]))
    equip = [p for p, ln in zip(parts, r["lines"], strict=True) if p and ln["kind"] == "equipment"]
    # the job's main unit first: F13 reads the ENERGY STAR model off parts[0]
    equip.sort(
        key=lambda p: not re.search(rules.JOBS[job].match, p.name, re.IGNORECASE) if job else 0
    )
    loc = location or (None if address else get_settings().DEFAULT_LOCATION)
    rules_task = _rules(job, equip, address, loc) if job else asyncio.sleep(0, (None, None))
    (rules_res, rules_note), *reports = await asyncio.gather(
        rules_task, *(recall(p) if p else asyncio.sleep(0) for p in parts)
    )

    lines = []
    for i, (ln, part, rep) in enumerate(zip(r["lines"], parts, reports, strict=True), 1):
        if rep is not None:
            text = f"The {ln['model_no']['value']} on line {i}: {rep.headline}."
            say = f"Line {i}: {rep.spoken.removeprefix('Heads up: ')}"
            severity = "warn" if rep.verdict == "recalled" else "info"
            flags.append(_flag("recall", severity, text, i, say))
        cmp_ = None
        if part is not None and ln["kind"] == "equipment" and not part.id.startswith("quote-"):
            q, u, t = (_printed(ln[k]) for k in ("qty", "unit_price", "line_total"))
            price = u if u is not None else (t / q if t is not None and q else None)
            cmp_ = compare(price, part)
            if cmp_ and cmp_["spread_usd"] is not None:
                flags.append(_flag("price_spread", "info", f"Line {i}: {cmp_['text']}.", i))
        lines.append({"i": i, **ln, "part_id": part.id if part else None, "compare": cmp_})
    if rules_res:
        flags += _missing(rules_res, r)
    for ln in lines:
        ln["flags"] = [f for f in flags if f["line"] == ln["i"]]

    def kind_sum(kind: str) -> float | None:
        vals = [ln["line_total"]["value"] for ln in r["lines"] if ln["kind"] == kind]
        return round(sum(v for v in vals if v is not None), 2) if vals else None

    return {
        "quote_id": got["quote_id"],
        "kind": got["kind"],
        "cached": hit,
        "contractor": r["contractor"],
        "lines": lines,
        "totals": {
            **{k: r[k] for k in ("subtotal", "tax_rate_pct", "tax", "total")},
            "labor": kind_sum("labor"),
            "permit_fee": kind_sum("permit"),
        },
        "inferred": sum(f["source"] == "inferred" and f["value"] is not None for f in _nums(r)),
        "demoted": got.get("demoted", 0),
        "flags": flags,
        "job": job,
        "rules": {
            "check_id": rules_res["check_id"],
            "permit_required": (rules_res.get("permit") or {}).get("required"),
            "label": rules_res["label"],
        }
        if rules_res
        else None,
        "rules_note": rules_note if job else "no permit rules on file for this kind of job",
        "spoken": spoken(r, flags, lines),
        "label": LABEL,
        "cost_usd": 0.0 if hit else got.get("cost_usd"),
    }
