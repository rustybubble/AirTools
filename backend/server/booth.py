"""Booth wall (docs/research/grok-ideas/g6-round5.md pick 2, f19-booth.md): a booth-TV gallery
of shared designs with a local leaderboard, plus an optional X post of a design's card.

- `share(session_id, name, consent)` puts a session on the wall, only with explicit consent (the
  endpoint's `consent: true`, or the agent's `share_design` with a confirming phrase). The facts
  come from F14's `packet.gather()` (F4's report plus the safety/rules extras), so this needs both
  features. One entry per session; re-sharing replaces it.
- `caption()`: one fast Grok line (role "booth"), cached by its facts. Any number not in the
  facts, any link or @mention, or a reply over 90 characters -> the template.
- `render_card()`: a 1200x675 JPEG from the F5 postcard or a scan thumb (F14's `_hero`), never
  the passthrough camera. ponytail: no QR on the card (segno isn't installed; F14 skipped it
  too). Add segno and draw the packet link in the card's corner if the X post needs the link.
- X (optional): `x_missing()` names the unset env vars. `share` hands out a one-use confirm
  token (5 min) with the post text; only `POST /booth/x/confirm` with that token posts, which the
  headset sends after its hold-to-post gesture. A voice turn never posts. The text never holds a
  URL ($0.20 per post instead of $0.015) or an @mention.

Entries: `DATA_DIR/booth/entries.json` (newest first); cards: `DATA_DIR/booth/cards/<id>.jpg`;
the rotated X refresh token: `DATA_DIR/booth/x_token.json`.
"""

import base64
import hashlib
import html
import json
import logging
import os
import re
import secrets
import textwrap
import time
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlencode

import httpx
from PIL import Image, ImageDraw, ImageOps

from server import cache, llm, packet
from server.config import get_settings

logger = logging.getLogger(__name__)

ENTRY_ID_RE = re.compile(r"^[a-f0-9]{12}$")
CAPTION_MAX = 90
POST_MAX = 240
HASHTAG = "#AirTool"
CONFIRM_TTL_S = 300
MAX_NAME = 40
CARD_W, CARD_H = 1200, 675
X_API = "https://api.x.com/2"
X_AUTHORIZE = "https://x.com/i/oauth2/authorize"
X_SCOPES = "tweet.read tweet.write users.read media.write offline.access"
X_VARS = ("X_CLIENT_ID", "X_CLIENT_SECRET", "X_REFRESH_TOKEN")
# A link X would render (http..., www..., foo.com) or a mention. X bills a post with a URL at
# $0.20 instead of $0.015, and a booth account has no business tagging people.
LINK_RE = re.compile(r"https?:|www\.|\b[\w-]+\.[a-z]{2,}\b|@\w", re.IGNORECASE)
BOARDS = (  # key, title, entry field, higher is better
    ("fastest", "Fastest scan to cart", "seconds_to_cart", False),
    ("most_parts", "Most parts placed", "parts_placed", True),
    ("rebate", "Biggest rebate found", "rebates_usd", True),
)
PROMPT = (
    "Write one upbeat line, at most 70 characters, for a trade-show screen about this home "
    "repair design, from the facts below. Copy any number exactly as written in a fact; never "
    "invent one. It was designed and priced in AR, not installed or bought. No links, no "
    "@mentions, no hashtags, no quotes. Plain text.\n\nFacts:\n{facts}"
)


class XError(Exception):
    def __init__(self, status: int, detail: str):
        super().__init__(detail)
        self.status, self.detail = status, detail


# --- entries -----------------------------------------------------------------------------------


def _dir() -> Path:
    return Path(get_settings().DATA_DIR) / "booth"


def card_path(entry_id: str) -> Path | None:
    return _dir() / "cards" / f"{entry_id}.jpg" if ENTRY_ID_RE.match(entry_id) else None


def load_entries() -> list[dict]:
    try:
        return json.loads((_dir() / "entries.json").read_text())
    except (OSError, json.JSONDecodeError):
        return []


def _save(entries: list[dict]) -> None:
    cache.write_json_atomic(_dir() / "entries.json", json.dumps(entries, indent=2))


def _ts(value) -> datetime | None:
    try:
        dt = datetime.fromisoformat(str(value))
    except ValueError:
        return None
    return dt if dt.tzinfo else dt.replace(tzinfo=UTC)


def seconds_to_cart(entries: list[dict]) -> int | None:
    """From the session's first timestamped notebook entry (`ts` or `created_at`) to its first
    checkout receipt. None when the headset logged no earlier timestamp than the order."""
    stamps = [t for e in entries if (t := _ts(e.get("ts") or e.get("created_at")))]
    orders = [t for e in entries if e.get("type") == "order" and (t := _ts(e.get("created_at")))]
    if not stamps or not orders or min(orders) <= min(stamps):
        return None
    return round((min(orders) - min(stamps)).total_seconds())


def leaderboard(entries: list[dict], top: int = 3) -> dict:
    """Each board's top rows; an entry without a value (or 0) isn't ranked. The stable sort over
    oldest-first entries gives a tie to the earlier entry."""
    oldest_first = sorted(entries, key=lambda e: e["created_at"])
    out = {}
    for key, title, field, high in BOARDS:
        rows = [e for e in oldest_first if e.get(field)]
        rows.sort(key=lambda e: -e[field] if high else e[field])
        out[key] = {
            "title": title,
            "rows": [
                {"entry_id": e["entry_id"], "name": e["name"], "value": e[field]}
                for e in rows[:top]
            ],
        }
    return out


# --- caption -----------------------------------------------------------------------------------


def _usd(value: float) -> str:
    return f"${value:,.2f}"


def facts(entry: dict) -> list[str]:
    """What Grok writes from (the designer's name stays out) and the number check's reference."""
    lines = [f"Site: {entry['site']}"]
    if entry["part"]:
        lines.append(f"Part: {entry['part']}")
    lines += [f"Parts placed: {entry['parts_placed']}", f"Estimate: {_usd(entry['total_usd'])}"]
    if entry["rebates_usd"]:
        lines.append(f"Active rebates found: {_usd(entry['rebates_usd'])}")
        lines.append(f"After rebates: {_usd(entry['after_rebates_usd'])}")
    if entry["seconds_to_cart"]:
        lines.append(f"Scan to cart: {entry['seconds_to_cart']} seconds")
    if entry["safety_verdict"] != "unknown":
        lines.append(f"Recall check: {entry['safety_verdict']}")
    return lines


def template(entry: dict) -> str:
    part = textwrap.shorten(entry["part"] or "A design", 40, placeholder="...")
    text = f"{part} at {entry['site'][:24]}, {_usd(entry['total_usd'])}"
    if entry["rebates_usd"]:
        text += f", {_usd(entry['rebates_usd'])} back in rebates"
    return text + "."


def clean(text: str, fact_lines: list[str]) -> bool:
    """Short, no link or mention, and every number backed by the facts (F14's check)."""
    return (
        0 < len(text) <= CAPTION_MAX
        and not LINK_RE.search(text)
        and packet.backed(text, fact_lines)
    )


async def _ask(fact_lines: list[str]) -> dict:
    resp = await llm.chat(  # chat() logs the role, latency and cost (usage.cost_in_usd_ticks)
        "booth",
        [{"role": "user", "content": PROMPT.format(facts="\n".join(fact_lines))}],
        max_tokens=60,
        max_retries=0,  # a timed-out call may still bill; don't pay twice
        timeout=20,
    )
    cost = llm.cost_usd(resp.usage)
    return {
        "text": (resp.choices[0].message.content or "").strip(),
        "cost_usd": round(cost, 6) if cost is not None else None,
    }


async def caption(entry: dict) -> tuple[str, str]:
    """(line, source): Grok's line when it passes `clean()`, else the template."""
    fact_lines = facts(entry)
    _, model = llm.resolve_role("booth")
    key = {"model": model, "prompt": PROMPT, "facts": fact_lines}
    try:
        reply = await cache.cached("booth_caption", key, lambda: _ask(fact_lines))
        text = " ".join(reply["text"].split()).strip("\"'")
        if clean(text, fact_lines):
            return text, model
        logger.warning("booth caption failed the length, number or link check: %r", text)
    except Exception as exc:  # noqa: BLE001 -- offline miss, no key, HTTP: the template
        logger.info("booth caption: template fallback (%r)", exc)
    return template(entry), "template"


def x_text(entry: dict) -> str:
    """The post: the caption (already checked, or the template, whose part name came from a
    listing and might carry a domain) with any link or mention stripped, plus one hashtag."""
    text = " ".join(LINK_RE.sub("", entry["caption"]).split())
    return f"{text[: POST_MAX - len(HASHTAG) - 1].rstrip()} {HASHTAG}"


# --- card --------------------------------------------------------------------------------------

BG, INK, MUTE, ACCENT = (17, 20, 24), (240, 242, 244), (150, 160, 168), (255, 140, 66)
PILL = {"recalled": (200, 40, 40), "caution": (214, 150, 20), "clear": (40, 150, 80)}


def _wrap(draw: ImageDraw.ImageDraw, text: str, size: int, width: int, max_lines: int):
    font, lines, cur = packet._font(size), [], ""
    for word in text.translate(packet._GLYPHS).split():
        trial = f"{cur} {word}".strip()
        if draw.textlength(trial, font=font) <= width or not cur:
            cur = trial
        else:
            lines.append(cur)
            cur = word
    lines.append(cur)
    if len(lines) > max_lines:
        lines = [*lines[: max_lines - 1], lines[max_lines - 1] + "..."]
    return font, lines


def render_card(entry: dict, hero: Path | None, path: Path) -> None:
    img = Image.new("RGB", (CARD_W, CARD_H), BG)
    draw = ImageDraw.Draw(img)
    draw.rectangle((0, 0, CARD_H, CARD_H), fill=(35, 40, 46))  # shows when there's no image
    try:
        if hero:
            with Image.open(hero) as src:
                img.paste(ImageOps.fit(src.convert("RGB"), (CARD_H, CARD_H)), (0, 0))
    except (OSError, ValueError):  # unreadable: keep the blank square
        pass
    x, y, width = CARD_H + 40, 40, CARD_W - CARD_H - 80

    def text(s: str, size: int, color, max_lines: int = 2, gap: int = 12) -> None:
        nonlocal y
        font, lines = _wrap(draw, s, size, width, max_lines)
        for line in lines:
            draw.text((x, y), line, font=font, fill=color)
            y += int(size * 1.2)
        y += gap

    text(entry["name"], 30, ACCENT, 1)
    text(entry["part"] or entry["site"], 40, INK, 3)
    text(_usd(entry["total_usd"]), 56, INK, 1, 4)
    if entry["rebates_usd"]:
        text(f"{_usd(entry['rebates_usd'])} back in rebates", 28, ACCENT, 1)
    if color := PILL.get(entry["safety_verdict"]):
        label = f"recall check: {entry['safety_verdict']}"
        w = draw.textlength(label, font=packet._font(24))
        draw.rounded_rectangle((x, y, x + w + 28, y + 40), 20, fill=color)
        draw.text((x + 14, y + 7), label, font=packet._font(24), fill=INK)
        y += 60
    text(entry["caption"], 28, MUTE, 3)
    foot = f"AirTool booth · {entry['site']} · built with Grok"
    draw.text((x, CARD_H - 50), foot.translate(packet._GLYPHS), font=packet._font(20), fill=MUTE)
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, "JPEG", quality=88)


# --- share -------------------------------------------------------------------------------------

_PREVIEWS: dict[str, dict] = {}  # confirm token -> {entry_id, text, expires (monotonic)}


async def share(session_id: str, name: str | None = None, consent: bool = False) -> dict | None:
    """Put the session's design on the wall. None: no notebook for that session."""
    if not consent:
        raise PermissionError("consent required: the user has to ask for the wall")
    gathered = packet.gather(session_id)  # F4's report + F14's extras
    if gathered is None:
        return None
    report, ex = gathered
    entries = [e for e in load_entries() if e["session_id"] != session_id]
    part = report.parts[0] if report.parts else None
    safety_row = (ex.get("safety") or {}).get(part["part_id"]) if part else None
    t = report.totals
    entry = {
        "entry_id": secrets.token_hex(6),
        "session_id": session_id,
        "name": " ".join((name or "").split())[:MAX_NAME] or f"Judge {len(entries) + 1}",
        "site": (report.scene or {}).get("name") or session_id,
        "part": part["name"] if part else None,
        "parts_placed": sum(p["count"] or 0 for p in report.parts),
        "total_usd": t["estimate_usd"],
        "rebates_usd": t.get("rebates_usd"),
        "after_rebates_usd": t.get("after_rebates_usd"),
        "safety_verdict": safety_row["verdict"] if safety_row else "unknown",
        "seconds_to_cart": seconds_to_cart(packet._notebook_entries(session_id) or []),
        "created_at": datetime.now(UTC).isoformat(),
    }
    entry["caption"], entry["caption_source"] = await caption(entry)
    render_card(entry, packet._hero(report, ex), card_path(entry["entry_id"]))
    _save([entry, *entries])
    return {
        "entry_id": entry["entry_id"],
        "card_url": f"/booth/cards/{entry['entry_id']}.jpg",
        "caption": entry["caption"],
        "caption_source": entry["caption_source"],
        "entry": entry,
        "x": x_preview(entry),
    }


def unshare(entry_id: str) -> dict | None:
    """Take an entry off the wall. ponytail: an X post of it stays up; delete it on x.com (add
    `DELETE /2/tweets/{id}` here if judges ask for that)."""
    entries = load_entries()
    entry = next((e for e in entries if e["entry_id"] == entry_id), None)
    if entry is None:
        return None
    _save([e for e in entries if e["entry_id"] != entry_id])
    if path := card_path(entry_id):
        path.unlink(missing_ok=True)
    return entry


# --- X -----------------------------------------------------------------------------------------


def _token_file() -> Path:
    return _dir() / "x_token.json"


def _refresh_token() -> str | None:
    """The rotated token from the last refresh or login, else `X_REFRESH_TOKEN`."""
    try:
        return json.loads(_token_file().read_text())["refresh_token"]
    except (OSError, json.JSONDecodeError, KeyError):
        return get_settings().X_REFRESH_TOKEN


def _save_refresh_token(token: str) -> None:
    cache.write_json_atomic(_token_file(), json.dumps({"refresh_token": token}))
    os.chmod(_token_file(), 0o600)


def x_missing() -> list[str]:
    s = get_settings()
    have = {"X_CLIENT_ID": s.X_CLIENT_ID, "X_CLIENT_SECRET": s.X_CLIENT_SECRET}
    have["X_REFRESH_TOKEN"] = _refresh_token()
    return [v for v in X_VARS if not have[v]]


def x_preview(entry: dict) -> dict:
    """The `x` block of a share: a confirm token and the post text, or why X is off."""
    if missing := x_missing():
        return {"ready": False, "error": "X not configured", "needs": missing}
    if get_settings().OFFLINE:
        return {"ready": False, "error": "offline", "needs": []}
    now = time.monotonic()
    for token in [k for k, p in _PREVIEWS.items() if p["expires"] < now]:
        del _PREVIEWS[token]
    token = secrets.token_urlsafe(16)
    text = x_text(entry)
    _PREVIEWS[token] = {"entry_id": entry["entry_id"], "text": text, "expires": now + CONFIRM_TTL_S}
    return {
        "ready": True,
        "preview_text": text,
        "confirm_token": token,
        "expires_in_s": CONFIRM_TTL_S,
    }


def _ok(resp: httpx.Response) -> dict:
    if resp.is_error:
        try:
            body = resp.json()
            why = body.get("title") or body.get("error_description") or body.get("error")
        except ValueError:
            why = resp.text[:200]
        raise XError(502, f"X {resp.status_code}: {why}")
    return resp.json() if resp.content else {}


def _client_auth() -> tuple[str, str]:
    s = get_settings()
    return s.X_CLIENT_ID or "", s.X_CLIENT_SECRET or ""


async def _access_token(client: httpx.AsyncClient) -> str:
    """Refresh the 2 h access token; X may rotate the refresh token, so keep the new one."""
    resp = await client.post(
        f"{X_API}/oauth2/token",
        data={
            "grant_type": "refresh_token",
            "refresh_token": _refresh_token(),
            "client_id": _client_auth()[0],
        },
        auth=_client_auth(),  # confidential client: HTTP Basic with the client secret
    )
    body = _ok(resp)
    if new := body.get("refresh_token"):
        _save_refresh_token(new)
    return body["access_token"]


async def x_post(card: Path, text: str) -> str:
    """Token refresh, initialize -> append -> finalize, then the post. The tweet id."""
    if LINK_RE.search(text):  # last guard at the paid boundary
        raise XError(400, "post text holds a link or mention")
    data = card.read_bytes()
    async with httpx.AsyncClient(timeout=60) as client:
        auth = {"Authorization": f"Bearer {await _access_token(client)}"}
        init = _ok(
            await client.post(
                f"{X_API}/media/upload/initialize",
                headers=auth,
                json={
                    "media_type": "image/jpeg",
                    "total_bytes": len(data),
                    "media_category": "tweet_image",
                },
            )
        )
        media_id = init["data"]["id"]
        _ok(  # one chunk: a card is ~100 KB, the limit is 5 MB
            await client.post(
                f"{X_API}/media/upload/{media_id}/append",
                headers=auth,
                data={"segment_index": "0"},
                files={"media": ("card.jpg", data, "image/jpeg")},
            )
        )
        # ponytail: no STATUS poll; images come back ready. Poll if X ever sends processing_info.
        _ok(await client.post(f"{X_API}/media/upload/{media_id}/finalize", headers=auth))
        post = _ok(
            await client.post(
                f"{X_API}/tweets",
                headers=auth,
                json={"text": text, "media": {"media_ids": [media_id]}},
            )
        )
    logger.info("booth: posted to X, tweet %s (~$0.015, no URL)", post["data"]["id"])
    return post["data"]["id"]


async def confirm(token: str) -> dict:
    """The hold-to-post gesture's call. 503 X off, 409 bad/expired/used token, 502 X error."""
    if missing := x_missing():
        raise XError(503, f"X not configured: set {', '.join(missing)}")
    if get_settings().OFFLINE:
        raise XError(503, "offline: can't reach X")
    preview = _PREVIEWS.pop(token, None)  # one use, even when the post then fails
    if preview is None or preview["expires"] < time.monotonic():
        raise XError(409, "that preview expired or was already used; share again")
    entries = load_entries()
    entry = next((e for e in entries if e["entry_id"] == preview["entry_id"]), None)
    if entry is None or not (card := card_path(entry["entry_id"])).is_file():
        raise XError(409, "that design is no longer on the wall")
    tweet_id = await x_post(card, preview["text"])
    entry["tweet_id"] = tweet_id
    _save(entries)
    return {"tweet_id": tweet_id, "url": f"https://x.com/i/web/status/{tweet_id}"}


# One-time PKCE login, as the booth's bot account. state -> code_verifier.
_LOGINS: dict[str, str] = {}


def login_url(redirect_uri: str) -> str:
    verifier, state = secrets.token_urlsafe(48), secrets.token_urlsafe(16)
    _LOGINS[state] = verifier
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest())
    return f"{X_AUTHORIZE}?" + urlencode(
        {
            "response_type": "code",
            "client_id": _client_auth()[0],
            "redirect_uri": redirect_uri,
            "scope": X_SCOPES,
            "state": state,
            "code_challenge": challenge.rstrip(b"=").decode(),
            "code_challenge_method": "S256",
        }
    )


async def finish_login(state: str, code: str, redirect_uri: str) -> None:
    """Swap the code for tokens and keep the refresh token on disk (never served or logged)."""
    verifier = _LOGINS.pop(state, None)
    if verifier is None:
        raise XError(400, "unknown or used login state; start again at /booth/x/login")
    async with httpx.AsyncClient(timeout=30) as client:
        resp = await client.post(
            f"{X_API}/oauth2/token",
            data={
                "grant_type": "authorization_code",
                "code": code,
                "redirect_uri": redirect_uri,
                "code_verifier": verifier,
                "client_id": _client_auth()[0],
            },
            auth=_client_auth(),
        )
    body = _ok(resp)
    if not body.get("refresh_token"):
        raise XError(502, "X returned no refresh token: is offline.access in the scopes?")
    _save_refresh_token(body["refresh_token"])
    logger.info("booth: X login done, refresh token saved to %s", _token_file())


# --- booth screen ------------------------------------------------------------------------------

_CSS = """
:root{--bg:#111418;--card:#1c2127;--ink:#f0f2f4;--mute:#96a0a8;--accent:#ff8c42}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--ink);
font:clamp(18px,1.6vw,34px)/1.3 system-ui,-apple-system,"Segoe UI",sans-serif}
main{display:grid;grid-template-columns:1fr 30%;gap:2vw;padding:2vw;min-height:100vh}
h1{font-size:2.4em;margin:0 0 .3em}h1 span{color:var(--accent)}
h2{font-size:1.1em;color:var(--accent);text-transform:uppercase;letter-spacing:.06em;
margin:1em 0 .4em}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(28vw,1fr));gap:1.5vw}
figure{margin:0;background:var(--card);border-radius:.6em;overflow:hidden}
figure img{width:100%;display:block;aspect-ratio:16/9;object-fit:cover}
figcaption{padding:.5em .7em}figcaption b{color:var(--accent)}
ol{margin:0;padding-left:1.4em}li{margin:.25em 0}li .v{float:right;color:var(--accent);
font-variant-numeric:tabular-nums}
.mute{color:var(--mute)}.empty{font-size:1.4em;color:var(--mute);margin-top:3em}
@media (max-width:900px){main{grid-template-columns:1fr}.grid{grid-template-columns:1fr}}
"""


def _board_value(field_key: str, value) -> str:
    if field_key == "fastest":
        return f"{value // 60}:{value % 60:02d}"
    return _usd(value) if field_key == "rebate" else str(value)


def render_html(entries: list[dict], show: int = 6) -> str:
    """The booth TV page. Server-rendered and escaped; a meta refresh keeps it live (no JS)."""
    e = html.escape
    out = [
        "<!doctype html><html lang=en><head><meta charset=utf-8>",
        '<meta http-equiv=refresh content=5><meta name=viewport content="width=device-width">',
        f"<title>AirTool booth wall</title><style>{_CSS}</style></head><body><main><section>",
        (
            f"<h1>AirTool <span>booth wall</span></h1><div class=mute>{len(entries)} design(s) "
            "shared today. Say &ldquo;add it to the wall&rdquo; in the headset.</div>"
        ),
    ]
    if not entries:
        out.append("<p class=empty>No designs yet. Yours could be first.</p>")
    else:
        out.append("<div class=grid>")
        for entry in entries[:show]:
            out.append(
                f"<figure><img src='/booth/cards/{e(entry['entry_id'])}.jpg' alt=''>"
                f"<figcaption><b>{e(entry['name'])}</b> &middot; {e(entry['caption'])}"
                "</figcaption></figure>"
            )
        out.append("</div>")
    out.append("</section><aside><h2>Leaderboard</h2>")
    for key, board in leaderboard(entries).items():
        out.append(f"<h2 class=mute>{e(board['title'])}</h2>")
        if not board["rows"]:
            out.append("<div class=mute>nobody yet</div>")
            continue
        out.append("<ol>")
        out += [
            f"<li>{e(r['name'])}<span class=v>{e(_board_value(key, r['value']))}</span></li>"
            for r in board["rows"]
        ]
        out.append("</ol>")
    out.append("</aside></main></body></html>")
    return "".join(out)
