"""Site-walk report (G2 idea #3): "take the headset off and see your report".

`build_report(session_id)` joins everything one session produced -- the notebook (notes, tape
readings, pins with their frame thumbnails), placed parts, "what else do I need?" BOMs, checkout
receipts (honesty `label` kept verbatim) and the scene package (site, revision, scale method +
residual, so accuracy is stated, not implied) -- plus one LLM-written summary and next steps.
`render_html(report)` turns that into one printable page (inline CSS, print stylesheet: the
browser's "Save as PDF" is the PDF export, no new dependency).

Notebook entries are free-form JSON written by the headset (`POST /notebook`) and by
`/checkout` / `/parts/bom`; the shapes read here (`docs/api.md` "Notebook entry types"):

    {"type": "note", "text"}
    {"type": "measurement", "tool", "label", "value_m" | "value"+"unit", "site",
     "nearest_camera_id", "quality"}
    {"type": "pin", "label", "site", "nearest_camera_id" | "frame_id" | "thumb"}
    {"type": "placement", "part_id", "count"}
    {"type": "bom", "bom_id"}
    {"type": "order", ...receipt, "bom_id"?}

Unknown types are ignored. Summary: `cache.cached("report", <content hash>)` around one
`llm.extract` call on role "report" (default `xai:grok-4.3`), falling back to role "agent" (Groq)
if that fails; OFFLINE or both failing -> a plain template, never cached (so the next online
build still gets the real one).
"""

import base64
import hashlib
import html
import json
import logging
import re
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote

import httpx
from pydantic import BaseModel

from server import bom, cache, jobs, llm, voice
from server.config import get_settings

logger = logging.getLogger(__name__)

_ID_RE = re.compile(r"^[A-Za-z0-9_-]+$")
_SITE_RE = re.compile(r"^[a-z0-9-]+$")
_THUMB_RE = re.compile(r"^thumbs/[A-Za-z0-9_-]+\.jpg$")
_DEFAULT_UNIT = {"level": "°", "protractor": "°", "plumb": "°", "area": "m²"}
XAI_TTS_URL = "https://api.x.ai/v1/tts"
# ~30 s: a live 600-char `rex` narration ran 48 s (~12.5 chars/s incl. [pause] tags)
NARRATION_CHAR_LIMIT = 380


class Summary(BaseModel):
    summary: str
    next_steps: list[str]


class Report(BaseModel):
    session_id: str
    generated_at: str
    scene: dict | None = None  # site, name, revision, quality, scale_method, residual, accuracy
    notes: list[str] = []
    measurements: list[dict] = []  # label, tool, value, unit, display, thumb_url, preview
    pins: list[dict] = []  # label, thumb_url
    parts: list[dict] = []  # part_id, name, count, dims_mm, seller, price_usd, image_url
    boms: list[dict] = []  # Bom.model_dump() + bom totals
    orders: list[dict] = []  # receipts, label verbatim
    totals: dict = {}
    summary: str = ""
    next_steps: list[str] = []
    summary_source: str = "template"  # "<provider>:<model>" or "template"


# --- assembly ---------------------------------------------------------------------------------


def notebook_path(session_id: str) -> Path:
    safe = re.sub(r"[^a-zA-Z0-9_-]", "_", session_id)
    return Path(get_settings().DATA_DIR) / "notebook" / f"{safe}.json"


def append_notebook(session_id: str, entries: list[dict]) -> list[dict]:
    path = notebook_path(session_id)
    existing = json.loads(path.read_text()) if path.exists() else []
    existing.extend(entries)
    cache.write_json_atomic(path, json.dumps(existing, indent=2, default=str))
    return existing


def _notebook_entries(session_id: str) -> list[dict] | None:
    path = notebook_path(session_id)
    if not path.exists():
        return None
    try:
        entries = json.loads(path.read_text())
    except (json.JSONDecodeError, OSError):
        return None
    return [e for e in entries if isinstance(e, dict)] if isinstance(entries, list) else None


def _scene(site: str | None) -> dict | None:
    if not site or not _SITE_RE.match(site):
        return None
    path = Path(get_settings().SCENE_DIR) / site / "scene.json"
    try:
        meta = json.loads(path.read_text())
    except (json.JSONDecodeError, OSError):
        return {"site": site, "accuracy": "Scene package not found on this server."}
    method = meta.get("scale_method") or "none"
    residual = meta.get("scale_residual_m")
    if method == "none":
        accuracy = "Unscaled scene: lengths are relative, not metres. Tape-check before cutting."
    elif residual is not None:
        accuracy = f"Metric scale from {method}, fit residual ±{residual * 100:.0f} cm."
    else:
        accuracy = f"Metric scale from {method}, residual not measured."
    return {
        "site": site,
        "name": meta.get("name") or site,
        "revision": meta.get("revision", 1),
        "quality": meta.get("quality", "full"),
        "scale_method": method,
        "scale_residual_m": residual,
        "scale_notes": meta.get("scale_notes"),
        "accuracy": accuracy,
    }


def _thumb_url(entry: dict, site: str | None) -> str | None:
    """`/scenes/<site>/thumbs/<id>.jpg` for the entry's camera, only if that file exists."""
    site = entry.get("site") or site
    if not site or not _SITE_RE.match(site):
        return None
    thumb = entry.get("thumb")
    if not (isinstance(thumb, str) and _THUMB_RE.match(thumb)):
        cam = entry.get("nearest_camera_id") or entry.get("frame_id")
        thumb = f"thumbs/{cam}.jpg" if isinstance(cam, str) and _ID_RE.match(cam) else None
    if thumb and (Path(get_settings().SCENE_DIR) / site / thumb).is_file():
        return f"/scenes/{site}/{thumb}"
    return None


def _measurement(entry: dict, site: str | None) -> dict | None:
    tool = str(entry.get("tool") or "tape")
    if entry.get("value_m") is not None:
        value, unit = entry["value_m"], "m"
    else:
        value, unit = entry.get("value"), entry.get("unit") or _DEFAULT_UNIT.get(tool, "m")
    try:
        value = float(value)
    except (TypeError, ValueError):
        return None
    display = f"{value:.2f} {unit}"
    if unit == "m":
        display += f" ({value / 0.0254:.1f} in)"
    return {
        "label": str(entry.get("label") or tool),
        "tool": tool,
        "value": value,
        "unit": unit,
        "display": display,
        "preview": entry.get("quality") == "preview" or bool(entry.get("preview")),
        "thumb_url": _thumb_url(entry, site),
    }


def _part_row(part_id: str, count: int | None) -> dict | None:
    if not _ID_RE.match(part_id):
        return None
    part = jobs.load_part(part_id)
    if part is None:
        return None
    seller = part.sellers[part.recommended_seller] if part.recommended_seller is not None else None
    has_image = (Path(get_settings().DATA_DIR) / "parts" / part_id / "image.jpg").is_file()
    return {
        "part_id": part_id,
        "name": part.name,
        "count": count,
        "dims_mm": part.dims_mm.model_dump(),
        "asset_tier": part.asset.tier,
        "seller": seller.name if seller else None,
        "price_usd": seller.price_usd if seller else None,
        "image_url": f"/parts/{part_id}/image.jpg" if has_image else part.image_url,
    }


def assemble(session_id: str) -> Report | None:
    """Everything but the summary. None when the session has no notebook."""
    entries = _notebook_entries(session_id)
    if entries is None:
        return None
    site = next((e["site"] for e in entries if isinstance(e.get("site"), str)), None)
    report = Report(
        session_id=session_id, generated_at=datetime.now(UTC).isoformat(), scene=_scene(site)
    )
    placed: dict[str, int] = {}
    bom_ids: list[str] = []
    for e in entries:
        kind = e.get("type")
        if kind == "note" and e.get("text"):
            report.notes.append(str(e["text"]))
        elif kind == "measurement":
            if m := _measurement(e, site):
                report.measurements.append(m)
        elif kind == "pin":
            label = str(e.get("label") or e.get("text") or "pin")
            report.pins.append({"label": label, "thumb_url": _thumb_url(e, site)})
        elif kind == "placement" and isinstance(e.get("part_id"), str):
            placed[e["part_id"]] = placed.get(e["part_id"], 0) + int(e.get("count") or 1)
        elif kind == "bom" and isinstance(e.get("bom_id"), str):
            bom_ids.append(e["bom_id"])
        elif kind == "order":
            report.orders.append({k: v for k, v in e.items() if k != "type"})
            if isinstance(e.get("bom_id"), str):
                bom_ids.append(e["bom_id"])

    ordered_parts = [o.get("part_id") for o in report.orders if isinstance(o.get("part_id"), str)]
    for part_id in [*placed, *(p for p in ordered_parts if p not in placed)]:
        if row := _part_row(part_id, placed.get(part_id)):
            report.parts.append(row)
    part_names = {p["part_id"]: p["name"] for p in report.parts}
    for order in report.orders:
        order["part_name"] = part_names.get(order.get("part_id"), order.get("part_id"))

    for bom_id in dict.fromkeys(bom_ids):
        if _ID_RE.match(bom_id) and (saved := bom.load_bom(bom_id)):
            row = saved.model_dump(mode="json")
            for line, model_line in zip(row["lines"], saved.lines, strict=True):
                line["cost_usd"] = bom.line_cost(model_line)
            report.boms.append(row)

    authorized = sum(o.get("total_usd") or 0 for o in report.orders if o.get("mode") == "sandbox")
    recorded = sum(o.get("total_usd") or 0 for o in report.orders if o.get("mode") != "sandbox")
    report.totals = {
        "orders_usd": round(authorized + recorded, 2),
        "sandbox_authorized_usd": round(authorized, 2),
        "offline_unpaid_usd": round(recorded, 2),
        "bom_usd": round(sum(b["total_usd"] for b in report.boms), 2),
    }
    return report


# --- summary ------------------------------------------------------------------------------------


def _facts(report: Report) -> dict:
    """What the summary is written from -- also the cache key's input, so a new reading or
    order means a new summary."""
    return {
        "scene": report.scene,
        "notes": report.notes,
        "measurements": [
            {k: m[k] for k in ("label", "tool", "display", "preview")} for m in report.measurements
        ],
        "pins": [p["label"] for p in report.pins],
        "parts": [
            {k: p[k] for k in ("name", "count", "seller", "price_usd")} for p in report.parts
        ],
        "bom_lines": [
            {"name": line["name"], "qty": line["qty"], "cost_usd": line["cost_usd"]}
            for b in report.boms
            for line in b["lines"]
        ],
        "orders": [
            {k: o.get(k) for k in ("part_name", "qty", "total_usd", "mode", "label", "bom_lines")}
            for o in report.orders
        ],
    }


_SYSTEM = (
    "You write the executive summary of a contractor's AR site walk. From the JSON facts, "
    "write `summary`: at most 4 plain sentences (what was measured, what was placed, what was "
    "ordered and for how much). Then `next_steps`: at most 5 short imperative items -- "
    "materials still to buy and measurements to double-check (preview readings, anything "
    "within the scene's scale residual of a tight fit). Honesty: an order whose mode is not "
    "'sandbox' was NOT paid -- say it still needs ordering; a sandbox order is a test "
    "authorization, not a real purchase. Never invent numbers not in the facts."
)


def template_summary(report: Report) -> Summary:
    site = (report.scene or {}).get("name") or "the site"
    counts = (
        f"{len(report.measurements)} measurement(s), {len(report.pins)} pin(s) and "
        f"{len(report.notes)} note(s)"
    )
    sentences = [f"Site walk at {site}: {counts}."]
    if report.parts:
        sentences.append(f"{len(report.parts)} part(s) placed or ordered.")
    if report.orders:
        t = report.totals
        sentences.append(
            f"{len(report.orders)} order(s) totalling ${t['orders_usd']:.2f}: "
            f"${t['sandbox_authorized_usd']:.2f} sandbox-authorized, "
            f"${t['offline_unpaid_usd']:.2f} recorded offline with no payment."
        )
    steps = [
        f"Place the real order for {o.get('part_name')} (offline receipt, nothing was paid)."
        for o in report.orders
        if o.get("mode") != "sandbox"
    ]
    ordered = {line["name"] for o in report.orders for line in o.get("bom_lines") or []}
    steps += [
        f"Buy {line['name']} x{line['qty']}."
        for b in report.boms
        for line in b["lines"]
        if line["name"] not in ordered
    ]
    residual = (report.scene or {}).get("scale_residual_m")
    scale_method = (report.scene or {}).get("scale_method")
    for m in report.measurements:
        if m["preview"] or scale_method == "none" or (residual and m["unit"] == "m"):
            steps.append(f"Tape-check the {m['label']} ({m['display']}) before cutting.")
    return Summary(summary=" ".join(sentences), next_steps=steps[:5])


async def _llm_summary(facts: dict) -> dict:
    messages = [
        {"role": "system", "content": _SYSTEM},
        {"role": "user", "content": json.dumps(facts)},
    ]
    last_exc: Exception | None = None
    for role in ("report", "agent"):
        try:
            result = await llm.extract(role, messages, Summary)
        except cache.OfflineMiss:
            raise
        except Exception as exc:  # noqa: BLE001 -- any provider failure -> next role
            logger.warning("report summary: role=%s failed: %s", role, exc)
            last_exc = exc
            continue
        provider, model = llm.resolve_role(role)
        return {**result.model_dump(), "source": f"{provider}:{model}"}
    raise RuntimeError(f"all summary roles failed: {last_exc}")


def content_hash(report: Report) -> str:
    return hashlib.sha256(json.dumps(_facts(report), sort_keys=True).encode()).hexdigest()


async def build_report(session_id: str) -> Report | None:
    report = assemble(session_id)
    if report is None:
        return None
    facts = _facts(report)
    try:
        value = await cache.cached("report", content_hash(report), lambda: _llm_summary(facts))
        report.summary, report.next_steps = value["summary"], value["next_steps"][:5]
        report.summary_source = value["source"]
    except Exception as exc:  # noqa: BLE001 -- offline/providers down: page still renders
        logger.info("report summary: template fallback (%s)", exc)
        fallback = template_summary(report)
        report.summary, report.next_steps = fallback.summary, fallback.next_steps
    return report


# --- HTML ---------------------------------------------------------------------------------------

_CSS = """
:root{--ink:#1d2327;--mute:#5f6b73;--line:#d9dee2;--accent:#b4531f;--bg:#fff;--soft:#f5f3ef}
*{box-sizing:border-box}body{margin:0;background:var(--soft);color:var(--ink);
font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:860px;margin:24px auto;background:var(--bg);padding:28px 32px;
border:1px solid var(--line)}
header{display:flex;justify-content:space-between;gap:16px;align-items:flex-start;
border-bottom:3px solid var(--ink);padding-bottom:10px}
h1{font-size:22px;margin:0}h2{font-size:14px;text-transform:uppercase;letter-spacing:.06em;
color:var(--accent);margin:20px 0 6px}
.meta{color:var(--mute);font-size:12px}.box{background:var(--soft);padding:10px 12px;
border-left:3px solid var(--accent)}
table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;
padding:5px 6px;border-bottom:1px solid var(--line);vertical-align:top}
th{font-size:11px;color:var(--mute);text-transform:uppercase}td.n,th.n{text-align:right;
font-variant-numeric:tabular-nums}tr{break-inside:avoid}
img.t{width:84px;height:48px;object-fit:cover;border:1px solid var(--line)}
img.p{width:48px;height:48px;object-fit:contain}
.label{font-size:12px;color:var(--accent);font-weight:600}.tag{font-size:11px;
color:var(--mute);border:1px solid var(--line);padding:0 4px;border-radius:3px}
.pins{display:flex;flex-wrap:wrap;gap:10px}.pins figure{margin:0;width:170px}
.pins img{width:170px;height:96px;object-fit:cover;border:1px solid var(--line)}
.pins figcaption{font-size:12px}
.actions a,.actions button{font:inherit;font-size:12px;margin-left:6px;padding:4px 10px;
border:1px solid var(--ink);background:var(--bg);color:var(--ink);text-decoration:none;
cursor:pointer}
footer{margin-top:18px;font-size:11px;color:var(--mute)}
@media (max-width:600px){main{margin:0;padding:16px}header{flex-direction:column}}
@media print{body{background:#fff;font-size:11px}main{margin:0;border:0;padding:0;
max-width:none}.actions{display:none}h2{margin-top:12px;break-after:avoid}
table{font-size:11px}figure{break-inside:avoid}@page{size:letter;margin:12mm}}
"""


def _e(value) -> str:
    return html.escape("" if value is None else str(value))


def _usd(value) -> str:
    return "—" if value is None else f"${value:,.2f}"


def share_text(report: Report) -> str:
    site = (report.scene or {}).get("name") or "a site"
    bits = [f"Walked {site} in AR with AirTool"]
    if report.measurements:
        bits.append(f"{len(report.measurements)} measurements")
    if report.parts:
        bits.append(f"{len(report.parts)} parts placed")
    if report.orders:
        bits.append(f"{_usd(report.totals['orders_usd'])} cart")
    return ", ".join(bits) + ". Built with Grok. #AirTool"


def render_html(report: Report) -> str:
    s = report.scene or {}
    t = report.totals
    rows: list[str] = []
    add = rows.append

    title = f"Site report — {s.get('name') or report.session_id}"
    add(f"<!doctype html><html lang=en><head><meta charset=utf-8><title>{_e(title)}</title>")
    add('<meta name=viewport content="width=device-width,initial-scale=1">')
    add(f"<style>{_CSS}</style></head><body><main>")
    tweet = "https://x.com/intent/tweet?text=" + quote(share_text(report))
    add(
        f"<header><div><h1>{_e(title)}</h1><div class=meta>Session {_e(report.session_id)} · "
        f"generated {_e(report.generated_at[:16].replace('T', ' '))} UTC</div></div>"
        f"<div class=actions><button onclick='window.print()'>Print / Save PDF</button>"
        f"<a href='{_e(tweet)}' target=_blank rel=noopener>Share on X</a>"
        f"<a href='/report/{_e(quote(report.session_id))}.json'>JSON</a></div></header>"
    )

    add("<h2>Summary</h2><div class=box>")
    add(f"<p>{_e(report.summary)}</p>")
    if report.next_steps:
        add("<strong>Next steps</strong><ul>")
        rows.extend(f"<li>{_e(step)}</li>" for step in report.next_steps)
        add("</ul>")
    add(f"<div class=meta>Written by {_e(report.summary_source)}.</div></div>")

    if s:
        add("<h2>Scene &amp; accuracy</h2><p>")
        if "revision" in s:
            add(
                f"<b>{_e(s['name'])}</b> · revision {_e(s['revision'])} ({_e(s['quality'])}) · "
                f"scale method <b>{_e(s['scale_method'])}</b><br>"
            )
        add(f"{_e(s['accuracy'])}")
        if s.get("scale_notes"):
            add(f"<br><span class=meta>{_e(s['scale_notes'])}</span>")
        add("</p>")

    if report.measurements:
        add("<h2>Measurements</h2><table><tr><th>View</th><th>What</th><th>Tool</th>")
        add("<th class=n>Reading</th></tr>")
        for m in report.measurements:
            img = f"<img class=t src='{_e(m['thumb_url'])}' alt=''>" if m["thumb_url"] else ""
            tag = " <span class=tag>preview scale</span>" if m["preview"] else ""
            add(
                f"<tr><td>{img}</td><td>{_e(m['label'])}{tag}</td><td>{_e(m['tool'])}</td>"
                f"<td class=n>{_e(m['display'])}</td></tr>"
            )
        add("</table>")

    if report.pins:
        add("<h2>Pins</h2><div class=pins>")
        for p in report.pins:
            img = f"<img src='{_e(p['thumb_url'])}' alt=''>" if p["thumb_url"] else ""
            add(f"<figure>{img}<figcaption>{_e(p['label'])}</figcaption></figure>")
        add("</div>")

    if report.notes:
        add("<h2>Notes</h2><ul>")
        rows.extend(f"<li>{_e(n)}</li>" for n in report.notes)
        add("</ul>")

    if report.parts:
        add("<h2>Parts</h2><table><tr><th></th><th>Part</th><th class=n>Placed</th>")
        add("<th>Size (mm)</th><th>Best seller</th><th class=n>Price</th></tr>")
        for p in report.parts:
            img = f"<img class=p src='{_e(p['image_url'])}' alt=''>" if p["image_url"] else ""
            d = p["dims_mm"]
            add(
                f"<tr><td>{img}</td><td>{_e(p['name'])} <span class=tag>{_e(p['asset_tier'])}"
                f"</span></td><td class=n>{_e(p['count'] or '—')}</td>"
                f"<td>{d['w']:.0f} × {d['d']:.0f} × {d['h']:.0f}</td><td>{_e(p['seller'])}</td>"
                f"<td class=n>{_usd(p['price_usd'])}</td></tr>"
            )
        add("</table>")

    for b in report.boms:
        add(f"<h2>What else you need <span class=meta>({_e(b['id'])})</span></h2><table>")
        add("<tr><th>Item</th><th>Why</th><th class=n>Qty</th><th>Seller</th>")
        add("<th class=n>Cost</th></tr>")
        for line in b["lines"]:
            seller = (line.get("seller") or {}).get("name")
            cost = _usd(line["cost_usd"]) if seller else "—"
            add(
                f"<tr><td>{_e(line['name'])}</td><td>{_e(line['reason'])}</td>"
                f"<td class=n>{line['qty']}</td><td>{_e(seller or 'no priced listing')}</td>"
                f"<td class=n>{cost}</td></tr>"
            )
        add(f"<tr><th colspan=4>BOM total</th><th class=n>{_usd(b['total_usd'])}</th></tr>")
        add("</table>")

    if report.orders:
        add("<h2>Orders</h2><table><tr><th>Receipt</th><th>Items</th>")
        add("<th class=n>Total</th></tr>")
        for o in report.orders:
            first = (
                f"{_e(o.get('part_name'))} × {_e(o.get('qty'))} @ "
                f"{_usd(o.get('unit_price_usd'))} ({_e(o.get('seller'))})"
            )
            items = [first]
            items += [
                f"{_e(line.get('name'))} × {_e(line.get('qty'))} ({_e(line.get('seller'))})"
                for line in o.get("bom_lines") or []
            ]
            approval = f" · approval {_e(o['approval_code'])}" if o.get("approval_code") else ""
            add(
                f"<tr><td>{_e(o.get('receipt_id'))}<br><span class=meta>"
                f"{_e(str(o.get('created_at', ''))[:16].replace('T', ' '))}{approval}</span></td>"
                f"<td>{'<br>'.join(items)}<div class=label>{_e(o.get('label'))}</div></td>"
                f"<td class=n>{_usd(o.get('total_usd'))}</td></tr>"
            )
        add(f"<tr><th colspan=2>Orders total</th><th class=n>{_usd(t['orders_usd'])}</th></tr>")
        add(
            f"<tr><td colspan=2 class=meta>Sandbox-authorized (test, no real charge)</td>"
            f"<td class=n>{_usd(t['sandbox_authorized_usd'])}</td></tr>"
            f"<tr><td colspan=2 class=meta>Recorded offline, no payment authorized</td>"
            f"<td class=n>{_usd(t['offline_unpaid_usd'])}</td></tr></table>"
        )

    add(
        "<footer>Measurements come from a reconstructed scene, not a laser; the accuracy line "
        "above says how far to trust them. Prices are from the listing at search time.</footer>"
    )
    add("</main></body></html>")
    return "".join(rows)


# --- narration (xAI TTS, quartermaster voice) ------------------------------------------------


def narration_text(report: Report) -> str:
    site = (report.scene or {}).get("name") or "the site"
    sentences = re.split(r"(?<=[.!?])\s+", report.summary.strip())
    text = f"Site report for {site}. [pause] " + " ".join(sentences[:2])
    if report.next_steps:
        steps = ". ".join(s.rstrip(".") for s in report.next_steps[:2])
        text += f" [pause] Next: {steps}."
    return voice._truncate_at_sentence(text, NARRATION_CHAR_LIMIT)


async def _xai_tts(text: str, voice_id: str) -> dict:
    async with httpx.AsyncClient(timeout=60) as client:
        resp = await client.post(
            XAI_TTS_URL,
            headers={"Authorization": f"Bearer {get_settings().GROK_API_KEY}"},
            json={"text": text, "language": "en", "voice_id": voice_id},
        )
    resp.raise_for_status()
    mime = resp.headers.get("content-type", "audio/mpeg").split(";")[0]
    return {"audio_b64": base64.b64encode(resp.content).decode(), "mime": mime}


async def narrate(report: Report) -> tuple[bytes, str]:
    """~30 s spoken recap in the quartermaster voice (`XAI_VOICE`), cached per text+voice.
    xAI failing (or no key) falls back to `voice.speak()` (Groq/edge-tts); raises
    `voice.VoiceError` when nothing can speak (e.g. OFFLINE with no cached narration)."""
    text = narration_text(report)
    voice_id = get_settings().XAI_VOICE
    try:
        value = await cache.cached(
            "tts", {"xai": voice_id, "text": text}, lambda: _xai_tts(text, voice_id)
        )
        return base64.b64decode(value["audio_b64"]), value["mime"]
    except cache.OfflineMiss:
        pass  # voice.speak() may still have it cached
    except httpx.HTTPError as exc:
        logger.warning("xai tts failed, falling back to voice.speak: %s", exc)
    return await voice.speak(re.sub(r"\[pause\]\s*", "", text))
