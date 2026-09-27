"""Job packet (docs/research/grok-ideas/g5-round4.md pick 2): a 4-page PDF for the homeowner and
the contractor, on a public, revocable xAI Files link.

- `gather(session_id)` reads the session notebook through F4's site-report data model
  (measurements, pins, parts, BOMs, orders with their honesty labels verbatim) plus the
  optional `extras` other features cached. Free-text notes are left out unless tagged
  `"share": true` (privacy rule).
- `blurbs()` makes one fast Grok call (role "packet") for a homeowner and a contractor summary.
  A summary with any number that isn't in the facts is replaced by a template summary.
- `render()` draws Letter pages at 150 dpi with PIL and saves them as one PDF: cover, homeowner,
  contractor, sources.
- `publish()` uploads to xAI Files and makes a public URL (7 days by default, 30 max);
  `revoke()` kills the URL and deletes the file. OFFLINE there is no public link: the PDF is
  served on the LAN only at `GET /packet/<id>.pdf`.

Records live in `DATA_DIR/packets/<packet_id>.{pdf,json}`. The id is random, so the LAN URL
can't be guessed from the session id.
"""

import hashlib
import json
import logging
import re
import secrets
from datetime import UTC, datetime, timedelta
from functools import lru_cache
from pathlib import Path

import httpx
from PIL import Image, ImageDraw, ImageFont
from pydantic import BaseModel

from server import cache, jobs, llm, rules, safety, survey
from server.config import get_settings
from server.report import Report, _notebook_entries, assemble  # F4's data model

logger = logging.getLogger(__name__)

FILES_URL = "https://api.x.ai/v1/files"
DEFAULT_DAYS = 7
MAX_EXPIRES_S = 2_592_000  # xAI public-url cap: 30 days (min 3600 s)
BLURB_MAX_WORDS = 120  # asked for <= 90; anything much longer is a runaway reply
PACKET_ID_RE = re.compile(r"^[a-f0-9]{16}$")
PUBLIC_LABEL = (
    "Public link: anyone with it can open this until {date}. Say 'take the packet down' to "
    "revoke it."
)
LAN_LABEL = "Offline: no public link. The PDF is on the local network only."

# --- extras: what other features already know about this job ---------------------------------
# Read through each feature's own getter; nothing is fetched or paid for here.
# - safety (F6): `safety.peek(part)` per part, from the CPSC + Grok cache (disk, survives a
#   restart). A part with no cached check is skipped.
# - survey (F10): the newest finished survey of the report's site in the jobs table.
# - rules (F13): the newest finished rules check that covered one of the parts, or one of their
#   job kinds, in the jobs table.
# - postcard (F5): `data/parts/<id>/postcard.jpg`, the cover image.
# ponytail: survey and rules live in the in-memory jobs table like every job, so a restart
# forgets them until they run again (both replay from cache in a second).
EXTRA_LABELS = {
    "survey": survey.LABEL + ".",
    "safety": "Recall check from the CPSC database and a Grok web search; check cpsc.gov for "
    "the official list.",
    "rules": rules.LABEL,
}


def extras(report: Report) -> dict:
    site = (report.scene or {}).get("site")
    part_ids = [p["part_id"] for p in report.parts]
    kinds = {rules.job_for(p["name"]) for p in report.parts} - {None}
    out: dict = {}
    loaded = [part for pid in part_ids if (part := jobs.load_part(pid))]
    if checked := {part.id: r.model_dump() for part in loaded if (r := safety.peek(part))}:
        out["safety"] = checked
    if site and (found := jobs.latest("survey", lambda r: r.get("site") == site)):
        out["survey"] = found
    if found := jobs.latest(
        rules.STAGE,
        lambda r: bool(set(r.get("part_ids") or []) & set(part_ids)) or r.get("job") in kinds,
    ):
        out["rules"] = found
    parts_dir = Path(get_settings().DATA_DIR) / "parts"
    postcards = [parts_dir / pid / "postcard.jpg" for pid in part_ids]
    if found := [str(p) for p in postcards if p.is_file()]:
        out["postcard"] = found[0]
    return out


def _incentives(ex: dict) -> list[dict]:
    return ((ex.get("rules") or {}).get("money") or {}).get("incentives") or []


def _extra_rows(ex: dict) -> dict[str, list[tuple[str, str | None]]]:
    """(line, url) rows per extra section."""
    rows: dict[str, list[tuple[str, str | None]]] = {}
    if "survey" in ex:  # F10's pins are already triaged (confidence, severity, hail rule)
        rows["survey"] = [
            (f"{p['element']}: {p['issue']} ({p['severity']}, {p['action']})", None)
            for p in ex["survey"].get("pins") or []
        ]
    for rep in ex.get("safety", {}).values():
        rows.setdefault("safety", []).append((f"{rep['verdict']}: {rep['headline']}", None))
        rows["safety"] += [
            (
                f"Recall ({r['applies']}): {r['title']} {r.get('date') or ''}".strip(),
                r["url"],
            )
            for r in rep.get("recalls") or []
        ]
    if "rules" in ex:
        permit = ex["rules"].get("permit") or {}
        if permit.get("required") in ("yes", "no"):
            items = permit.get("items") or []
            names = ", ".join(i["name"] for i in items) or "a permit"
            office = (permit.get("office") or {}).get("name") or "the permitting office"
            if permit["required"] == "yes":
                line = f"Permit: {names} from {office}"
            else:
                line = f"Permit: likely none needed ({office})"
            rows["rules"] = [(line, items[0]["url"] if items else None)]
        rows.setdefault("rules", []).extend(
            (
                f"{i['name']}: {i['status']}"
                + (f", ${i['amount_usd']:.2f}" if i.get("amount_usd") else "")
                + (f" ({i['eligibility_reason']})" if i.get("eligibility_reason") else ""),
                i.get("url") or i.get("status_source"),
            )
            for i in _incentives(ex)
        )
    return {k: v for k, v in rows.items() if v}


def rebates_usd(ex: dict) -> float:
    """F13's counted rows only: live, with an amount, and eligible (never `unverified`)."""
    return round(sum(i["amount_usd"] for i in _incentives(ex) if rules.counted(i)), 2)


# --- facts -------------------------------------------------------------------------------------


def gather(session_id: str) -> tuple[Report, dict] | None:
    """F4's report data plus extras. None when the session has no notebook."""
    report = assemble(session_id)
    if report is None:
        return None
    # Privacy: only notes the user tagged for sharing leave the headset.
    entries = _notebook_entries(session_id) or []
    report.notes = [
        str(e["text"])
        for e in entries
        if e.get("type") == "note" and e.get("share") and e.get("text")
    ]
    for p in report.parts:
        part = jobs.load_part(p["part_id"])
        seller = (
            part.sellers[part.recommended_seller] if part.recommended_seller is not None else None
        )
        p["model_no"] = part.model_no
        p["seller_url"] = seller.url if seller else None
        p["spec_url"] = part.spec_url
    ex = extras(report)
    report.totals["estimate_usd"] = round(
        sum((p["price_usd"] or 0) * (p["count"] or 1) for p in report.parts)
        + report.totals["bom_usd"],
        2,
    )
    if rebate := rebates_usd(ex):
        report.totals["rebates_usd"] = rebate
        # "up to $500" off a $448 kit is $0, not a $52 credit (F15 live run)
        report.totals["after_rebates_usd"] = max(
            round(report.totals["estimate_usd"] - rebate, 2), 0
        )
    return report, ex


def _usd(value) -> str:
    return "n/a" if value is None else f"${value:,.2f}"


def _dims(p: dict) -> str:
    d = p["dims_mm"]
    return f"{d['w']:.0f} x {d['d']:.0f} x {d['h']:.0f} mm"


def facts(report: Report, ex: dict) -> list[str]:
    """Plain lines the summaries are written from, and the number check's reference."""
    s = report.scene or {}
    t = report.totals
    lines = [f"Site: {s.get('name') or report.session_id}. {s.get('accuracy') or ''}".strip()]
    for m in report.measurements:
        lines.append(
            f"Measured {m['label']}: {m['display']}" + (" (preview)" if m["preview"] else "")
        )
    lines += [f"Pin: {p['label']}" for p in report.pins]
    lines += [f"Note: {n}" for n in report.notes]
    for p in report.parts:
        model = f" (model {p['model_no']})" if p.get("model_no") else ""
        lines.append(
            f"Part: {p['name']}{model}, {_dims(p)}, placed x{p['count'] or 1}, "
            f"{p['seller'] or 'no seller'} {_usd(p['price_usd'])}"
        )
    for b in report.boms:
        for line in b["lines"]:
            seller = (line.get("seller") or {}).get("name")
            price = f"{seller} {_usd(line['cost_usd'])}" if seller else "no priced listing"
            lines.append(f"Also needed: {line['name']} x{line['qty']}, {price}")
    for o in report.orders:
        lines.append(
            f"Order for {o.get('part_name')}: {_usd(o.get('total_usd'))}. {o.get('label')}"
        )
    lines.append(f"Estimate for parts and extras: {_usd(t['estimate_usd'])}")
    if "rebates_usd" in t:
        lines.append(f"Active rebates: {_usd(t['rebates_usd'])}")
        lines.append(f"After rebates: {_usd(t['after_rebates_usd'])}")
    for name, rows in _extra_rows(ex).items():
        lines += [f"{name.capitalize()}: {text}" for text, _ in rows]
    return lines


# --- summaries ---------------------------------------------------------------------------------

_NUM_RE = re.compile(r"\d[\d,]*(?:\.\d+)?")


def _numbers(text: str) -> set[float]:
    return {float(n.replace(",", "")) for n in _NUM_RE.findall(text) if n.replace(",", "")}


def backed(text: str, fact_lines: list[str]) -> bool:
    """Every number in `text` appears in the facts (1,234 == 1234 == 1234.0). ponytail: digits
    only; a number spelled out in words slips through."""
    return _numbers(text) <= _numbers("\n".join(fact_lines))


class Blurbs(BaseModel):
    homeowner: str
    contractor: str


_INSTRUCTIONS = (
    "You write two short summaries for a home repair job packet from the numbered facts. "
    "`homeowner`: at most 90 words, plain language: what is being installed, what it costs, "
    "what was and wasn't paid, and what happens next. `contractor`: at most 90 words: exact "
    "models, dimensions, measurements to check and site notes. Use only the facts. Copy every "
    "number exactly as written in a fact; never round, add up or invent a number. An order "
    "that is not 'sandbox' was not paid."
)


def template_blurbs(report: Report) -> dict:
    """The number-safe fallback: every number is formatted exactly as in `facts()`."""
    t = report.totals
    names = ", ".join(p["name"] for p in report.parts) or "no parts yet"
    home = [f"The job: {names}.", f"Estimate for parts and extras: {_usd(t['estimate_usd'])}."]
    if "after_rebates_usd" in t:
        home.append(f"After rebates: {_usd(t['after_rebates_usd'])}.")
    unpaid = [o for o in report.orders if o.get("mode") != "sandbox"]
    if unpaid:
        home.append("Nothing has been paid yet: the order is a record, not a purchase.")
    home.append("Next: share this packet with your contractor and confirm the measurements.")
    site = (report.scene or {}).get("name") or report.session_id
    con = [f"Site: {site}."]
    con += [f"{p['name']}: {_dims(p)}, x{p['count'] or 1}." for p in report.parts]
    con += [f"{m['label']}: {m['display']}." for m in report.measurements[:4]]
    if any(m["preview"] for m in report.measurements):
        con.append("Tape-check the preview readings before cutting.")
    return {"homeowner": " ".join(home), "contractor": " ".join(con)}


async def _grok_blurbs(fact_lines: list[str]) -> dict:
    provider, model = llm.resolve_role("packet")
    if provider != "xai":
        raise ValueError(f"packet role must be an xai model, got {provider}")
    result = await llm.responses(
        model,
        input="\n".join(f"{i}. {line}" for i, line in enumerate(fact_lines, 1)),
        instructions=_INSTRUCTIONS,
        text={
            "format": {
                "type": "json_schema",
                "name": "packet_blurbs",
                "schema": llm._strict_schema(Blurbs.model_json_schema()),
                "strict": True,
            }
        },
        timeout_s=30,
    )
    reply = Blurbs.model_validate_json(result.text)
    return {**reply.model_dump(), "source": f"xai:{model}", "cost_usd": result.cost_usd}


async def blurbs(report: Report, fact_lines: list[str]) -> dict:
    """{homeowner, contractor, sources: {homeowner, contractor}, cost_usd}. The raw reply is
    cached by the facts' hash; the number check runs on every read."""
    key = hashlib.sha256(json.dumps(fact_lines).encode()).hexdigest()
    try:
        reply = await cache.cached("packet_blurbs", key, lambda: _grok_blurbs(fact_lines))
    except Exception as exc:  # noqa: BLE001 -- offline miss or xAI down: templates
        logger.info("packet blurbs: template fallback (%s)", exc)
        reply = {}
    template = template_blurbs(report)
    out: dict = {"sources": {}, "cost_usd": reply.get("cost_usd")}
    for who in ("homeowner", "contractor"):
        text = (reply.get(who) or "").strip()
        if text and len(text.split()) <= BLURB_MAX_WORDS and backed(text, fact_lines):
            out[who], out["sources"][who] = text, reply["source"]
        else:
            if text:
                logger.warning("packet blurbs: %s summary failed the number check", who)
            out[who], out["sources"][who] = template[who], "template"
    return out


# --- PDF ---------------------------------------------------------------------------------------

PAGE_W, PAGE_H, MARGIN = 1275, 1650, 90  # US Letter at 150 dpi
INK, MUTE, ACCENT, LINE = (29, 35, 39), (95, 107, 115), (180, 83, 31), (217, 222, 226)


# ponytail: Pillow's bundled font has no typographic dashes or quotes; other missing glyphs
# still draw as boxes. Ship a TTF (e.g. DejaVu Sans) if product names need more.
_GLYPHS = str.maketrans({"—": "-", "–": "-", "…": "...", "‘": "'", "’": "'", "“": '"', "”": '"'})


@lru_cache
def _font(size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.load_default(size=size)


class _Page:
    def __init__(self, title: str, n: int, subtitle: str):
        self.img = Image.new("RGB", (PAGE_W, PAGE_H), "white")
        self.draw = ImageDraw.Draw(self.img)
        self.y = MARGIN
        self.draw.text((MARGIN, self.y), title, font=_font(44), fill=INK, stroke_width=1)
        self.y += 62
        self.draw.line((MARGIN, self.y, PAGE_W - MARGIN, self.y), fill=INK, width=4)
        self.y += 24
        footer = f"AirTool job packet · {subtitle} · page {n} of 4"
        self.draw.text((MARGIN, PAGE_H - MARGIN + 30), footer, font=_font(20), fill=MUTE)

    def text(self, s: str, size: int = 26, color=INK, bold: bool = False, indent: int = 0) -> bool:
        """Wrapped text; False (and nothing drawn) once the page is full."""
        font, width = _font(size), PAGE_W - 2 * MARGIN - indent

        def fits(line: str) -> bool:
            return self.draw.textlength(line, font=font) <= width

        lines, cur = [], ""
        for word in s.translate(_GLYPHS).split():
            if fits(trial := f"{cur} {word}" if cur else word):
                cur = trial
            elif fits(word):
                lines.append(cur)
                cur = word
            else:  # longer than a line (a URL): break it by characters
                for ch in f" {word}" if cur else word:
                    if fits(cur + ch):
                        cur += ch
                    else:
                        lines.append(cur)
                        cur = ch.strip()
        lines.append(cur)
        step = int(size * 1.35)
        if self.y + step * len(lines) > PAGE_H - MARGIN - 20:
            return False
        for line in lines:
            self.draw.text(
                (MARGIN + indent, self.y), line, font=font, fill=color, stroke_width=int(bold)
            )
            self.y += step
        return True

    def heading(self, s: str) -> bool:
        self.y += 18
        return self.text(s.upper(), size=24, color=ACCENT, bold=True)

    def rows(self, items: list[str], size: int = 24, color=INK) -> None:
        for i, item in enumerate(items):
            if not self.text(f"- {item}", size=size, color=color, indent=10):
                self.text(f"... {len(items) - i} more in the session notebook", 20, MUTE)
                return

    def image(self, path: str | Path, w: int, h: int, x: int | None = None) -> int:
        """Pastes the picture fitted into w x h at the cursor; its height, 0 when unreadable."""
        try:
            with Image.open(path) as src:
                pic = src.convert("RGB")
        except (OSError, ValueError):  # missing or unreadable (e.g. a test stub): skip it
            return 0
        pic.thumbnail((w, h))
        self.img.paste(pic, (MARGIN if x is None else x, self.y))
        return pic.height


def _scene_file(url: str | None) -> Path | None:
    """`/scenes/<site>/thumbs/x.jpg` (from F4's `_thumb_url`) back to its file."""
    if not url or not url.startswith("/scenes/"):
        return None
    return Path(get_settings().SCENE_DIR) / url.removeprefix("/scenes/")


def _hero(report: Report, ex: dict) -> Path | None:
    if ex.get("postcard"):
        return Path(ex["postcard"])
    for item in [*report.pins, *report.measurements]:
        if path := _scene_file(item.get("thumb_url")):
            return path
    return None


def render(report: Report, ex: dict, blurb: dict, expires: datetime | None) -> list[Image.Image]:
    s, t = report.scene or {}, report.totals
    site = s.get("name") or report.session_id
    date = report.generated_at[:10]
    rows = _extra_rows(ex)

    cover = _Page("Job packet", 1, site)
    cover.text(f"Site: {site} · prepared {date}", 30, MUTE)
    cover.y += 20
    if (hero := _hero(report, ex)) and (h := cover.image(hero, PAGE_W - 2 * MARGIN, 640)):
        cover.y += h + 10
        if ex.get("postcard"):
            cover.text("Preview image, not to scale.", 20, MUTE)
    cover.y += 20
    cover.text(f"Estimate for parts and extras: {_usd(t['estimate_usd'])}", 40, INK, bold=True)
    if "after_rebates_usd" in t:
        cover.text(f"After active rebates: {_usd(t['after_rebates_usd'])}", 34, ACCENT, bold=True)
    cover.text("Before tax and labour. Prices are from the listing at search time.", 22, MUTE)
    cover.y += 30
    cover.text("Page 2: for the homeowner · Page 3: for the contractor · Page 4: sources", 24)
    if expires:
        cover.text(PUBLIC_LABEL.format(date=expires.date().isoformat()), 22, MUTE)

    home = _Page("For the homeowner", 2, site)
    home.text(blurb["homeowner"], 28)
    home.text(f"Summary by {blurb['sources']['homeowner']}, number-checked.", 18, MUTE)
    if report.parts:
        home.heading("What goes in")
        x, photos = MARGIN, Path(get_settings().DATA_DIR) / "parts"
        heights = [
            home.image(photos / p["part_id"] / "image.jpg", 240, 240, x=x + 260 * i)
            for i, p in enumerate(report.parts[:4])
        ]
        home.y += max(heights) + 10 if max(heights) else 0
        home.rows(
            [
                f"{p['name']}: {p['seller'] or 'no seller'} {_usd(p['price_usd'])}"
                for p in report.parts
            ]
        )
    if report.boms:
        home.heading("Also needed")
        home.rows(
            [
                f"{line['name']} x{line['qty']}: "
                + (_usd(line["cost_usd"]) if line.get("seller") else "no priced listing")
                for b in report.boms
                for line in b["lines"]
            ]
        )
    if report.orders:
        home.heading("Orders")
        home.rows(
            [
                f"{o.get('part_name')}: {_usd(o.get('total_usd'))}. {o.get('label')}"
                for o in report.orders
            ]
        )
    for name, title in (("rules", "Rebates and incentives"), ("safety", "Recall check")):
        if name in rows:
            home.heading(title)
            home.rows([text for text, _ in rows[name]])
            home.text(EXTRA_LABELS[name], 20, MUTE)

    con = _Page("For the contractor", 3, site)
    con.text(blurb["contractor"], 26)
    con.text(f"Summary by {blurb['sources']['contractor']}, number-checked.", 18, MUTE)
    if report.parts:
        con.heading("Parts")
        con.rows(
            [
                f"{p['name']}"
                + (f" · model {p['model_no']}" if p.get("model_no") else "")
                + f" · {_dims(p)} · x{p['count'] or 1}"
                for p in report.parts
            ]
        )
    if report.measurements:
        con.heading("Measurements")
        con.rows(
            [
                f"{m['label']}: {m['display']}" + (" (preview scale)" if m["preview"] else "")
                for m in report.measurements
            ]
        )
        if s.get("accuracy"):
            con.text(s["accuracy"], 20, MUTE)
    if "survey" in rows:
        con.heading("Drone survey findings")
        con.rows([text for text, _ in rows["survey"]])
        con.text(EXTRA_LABELS["survey"], 20, MUTE)
    if report.notes:
        con.heading("Shared notes")
        con.rows(report.notes)
    pins = [p for p in report.pins if _scene_file(p["thumb_url"])]
    if report.pins and con.heading("Pins"):
        x, tw, th = MARGIN, 350, 197
        if pins and con.y + th + 60 < PAGE_H - MARGIN:
            for p in pins[:3]:
                con.image(_scene_file(p["thumb_url"]), tw, th, x=x)
                x += tw + 22
            con.y += th + 10
        con.rows([p["label"] for p in report.pins], size=22)

    src = _Page("Sources and labels", 4, site)
    src.heading("Links")
    urls = [u for p in report.parts for u in (p.get("seller_url"), p.get("spec_url")) if u] + [
        u for section in rows.values() for _, u in section if u
    ]
    src.rows(list(dict.fromkeys(urls)) or ["No links recorded."], size=20)
    src.heading("Labels, verbatim")
    labels = [s["accuracy"]] if s.get("accuracy") else []
    labels += [o["label"] for o in report.orders if o.get("label")]
    labels += [EXTRA_LABELS[name] for name in rows]
    src.rows(list(dict.fromkeys(labels)), size=20)
    src.heading("How this packet was made")
    src.rows(
        [
            "Measurements come from a reconstructed scene, not a laser.",
            (
                f"Summaries: homeowner by {blurb['sources']['homeowner']}, contractor by "
                f"{blurb['sources']['contractor']}. A summary with any number not in the facts "
                "is replaced by a template."
            ),
            "Free-text notebook notes are left out unless tagged for sharing.",
            (PUBLIC_LABEL.format(date=expires.date().isoformat()) if expires else LAN_LABEL),
        ],
        size=20,
    )
    return [cover.img, home.img, con.img, src.img]


def save_pdf(pages: list[Image.Image], path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    pages[0].save(path, "PDF", save_all=True, append_images=pages[1:], resolution=150)


# --- xAI Files hosting -------------------------------------------------------------------------


def _auth() -> dict:
    key = get_settings().GROK_API_KEY
    if not key:
        raise RuntimeError("GROK_API_KEY missing")
    return {"Authorization": f"Bearer {key}"}


async def publish(path: Path, days: int = DEFAULT_DAYS) -> dict:
    """Upload, then a public URL. {file_id, url, expires_at}. File calls cost no tokens."""
    seconds = min(max(days * 86400, 3600), MAX_EXPIRES_S)
    async with httpx.AsyncClient(timeout=60, headers=_auth()) as client:
        resp = await client.post(
            FILES_URL,
            files={"file": (path.name, path.read_bytes(), "application/pdf")},
            data={"purpose": "assistants"},
        )
        resp.raise_for_status()
        file_id = resp.json()["id"]
        resp = await client.post(
            f"{FILES_URL}/{file_id}/public-url", json={"expires_after": seconds}
        )
        if resp.is_error:  # don't leave a private orphan behind
            await client.delete(f"{FILES_URL}/{file_id}")
            resp.raise_for_status()
    body = resp.json()
    expires = body.get("expires_at")
    return {
        "file_id": file_id,
        "url": body["public_url"],
        "expires_at": datetime.fromtimestamp(expires, UTC).isoformat() if expires else None,
    }


async def revoke(file_id: str) -> None:
    """Kill the public URL, then delete the file. A 404 means it's already gone."""
    async with httpx.AsyncClient(timeout=30, headers=_auth()) as client:
        for resp in (
            await client.post(f"{FILES_URL}/{file_id}/public-url/revoke", json={}),
            await client.delete(f"{FILES_URL}/{file_id}"),
        ):
            if resp.status_code != 404:
                resp.raise_for_status()


# --- packets -----------------------------------------------------------------------------------


def _dir() -> Path:
    return Path(get_settings().DATA_DIR) / "packets"


def pdf_path(packet_id: str) -> Path | None:
    return _dir() / f"{packet_id}.pdf" if PACKET_ID_RE.match(packet_id) else None


def load(packet_id: str) -> dict | None:
    if not PACKET_ID_RE.match(packet_id):
        return None
    path = _dir() / f"{packet_id}.json"
    return json.loads(path.read_text()) if path.is_file() else None


def _save(record: dict) -> None:
    cache.write_json_atomic(_dir() / f"{record['packet_id']}.json", json.dumps(record, indent=2))


def latest_public(session_id: str) -> dict | None:
    records = [json.loads(p.read_text()) for p in _dir().glob("*.json")]
    live = [r for r in records if r["session_id"] == session_id and r["public"]]
    return max(live, key=lambda r: r["created_at"], default=None)


class PublishError(Exception):
    """xAI Files failed; `record` still has the LAN copy."""

    def __init__(self, record: dict):
        super().__init__(record.get("error"))
        self.record = record


async def make(session_id: str, days: int = DEFAULT_DAYS, public: bool = True) -> dict | None:
    """Build the PDF and (unless OFFLINE or `public=False`) publish it. None: no notebook."""
    gathered = gather(session_id)
    if gathered is None:
        return None
    report, ex = gathered
    fact_lines = facts(report, ex)
    blurb = await blurbs(report, fact_lines)
    public = public and not get_settings().OFFLINE
    days = min(max(days, 1), MAX_EXPIRES_S // 86400)
    expires = datetime.now(UTC) + timedelta(days=days) if public else None
    pages = render(report, ex, blurb, expires)
    packet_id = secrets.token_hex(8)
    path = _dir() / f"{packet_id}.pdf"
    save_pdf(pages, path)
    record = {
        "packet_id": packet_id,
        "session_id": session_id,
        "created_at": datetime.now(UTC).isoformat(),
        "pages": len(pages),
        "local_url": f"/packet/{packet_id}.pdf",
        "url": None,
        "file_id": None,
        "expires_at": None,
        "public": False,
        "label": LAN_LABEL,
        "sections": sorted(ex),
        "summary_sources": blurb["sources"],
        "cost_usd": blurb["cost_usd"],
    }
    if public:
        try:
            hosted = await publish(path, days)
        except (httpx.HTTPError, RuntimeError, KeyError) as exc:
            record["error"] = f"xAI Files failed: {exc}"
            _save(record)
            raise PublishError(record) from exc
        expires_at = hosted["expires_at"] or expires.isoformat()
        record.update(
            url=hosted["url"],
            file_id=hosted["file_id"],
            expires_at=expires_at,
            public=True,
            label=PUBLIC_LABEL.format(date=expires_at[:10]),
        )
        logger.info("packet %s published for %s days (file %s)", packet_id, days, hosted["file_id"])
    _save(record)
    return record


def show_action(record: dict) -> dict:
    """The `show_packet` action for a packet record (send_packet and run_job)."""
    return {
        "name": "show_packet",
        "args": {
            "url": record["url"] or record["local_url"],
            # ponytail: no server-side QR (segno not installed); Unity draws it from `url`.
            "qr_png_url": None,
            "expires_at": record["expires_at"],
            "public": record["public"],
            "packet_id": record["packet_id"],
            "label": record["label"],
        },
    }


async def take_down(record: dict) -> dict:
    if record["public"] and record["file_id"]:
        await revoke(record["file_id"])
    record.update(public=False, url=None, revoked_at=datetime.now(UTC).isoformat(), label=LAN_LABEL)
    _save(record)
    return record
