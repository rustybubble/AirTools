"""Measured mandate (brain.md S2 / B3): bounded, evidenced, hold-signed checkout.

Three records tie a purchase to what the user said and what the headset measured:

- **IntentMandate**, one per session: the limits (budget cap on the order total, delivery
  deadline, seller policy) plus the instruction text and a change history. Voice can only
  tighten a limit; the checkout panel (the user's own hand, `source: "panel"`) may also loosen
  or clear one. Expires 24 h after its last change.
- **CartMandate**: exactly what will be charged -- the part line (units needed -> packs), the
  "what else" BOM lines, shipping, total, and the headset's evidence (the tape reading and array
  that set the quantity). `hash` is sha256 over its canonical JSON (sorted keys, no whitespace).
- **Hold nonce**: issued by `POST /checkout/prepare`, single-use, 120 s, bound to the cart hash
  and the session (a newer prepare for the session revokes older nonces). `POST /checkout`
  redeems it only with `hold_ms >= 1000`, the 1 s physical hold. Nothing else can pay.

Prices are always re-read from the saved part on the server. No LLM on this path.
"""

import hashlib
import json
import math
import re
import secrets
import uuid
from datetime import UTC, date, datetime, timedelta
from pathlib import Path
from typing import Any, Literal

from pydantic import BaseModel, Field

from server import bom, cache, checkout
from server.config import get_settings
from server.models import Part

INTENT_TTL = timedelta(hours=24)
NONCE_TTL = timedelta(seconds=120)
MIN_HOLD_MS = 1000
MAX_NONCES = 256
MAX_UNITS = 5000

_WEEKDAYS = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]

Policy = Literal["cheapest", "fastest", "best"]
Source = Literal["voice", "panel"]


class MandateError(Exception):
    """An HTTP-mappable refusal: 400 missing/short hold proof, 409 stale/reused/mismatched
    proof, 422 a check failed."""

    def __init__(self, status_code: int, detail: Any):
        super().__init__(detail)
        self.status_code = status_code
        self.detail = detail


def _now() -> datetime:
    return datetime.now(UTC)


def _today() -> date:
    """The laptop's local date -- "by Friday" means the user's Friday."""
    return datetime.now().astimezone().date()


def _dir() -> Path:
    return Path(get_settings().DATA_DIR) / "mandates"


def _session_file(session_id: str) -> str:
    safe = re.sub(r"[^a-zA-Z0-9_-]", "_", session_id)[:60]
    return f"{safe}-{hashlib.sha1(session_id.encode()).hexdigest()[:8]}.json"


def _money(usd: float) -> str:
    return f"${usd:.0f}" if float(usd).is_integer() else f"${usd:.2f}"


def _day(d: date) -> str:
    return f"{d.strftime('%a %b')} {d.day}"  # "Fri Oct 2"


# --- intent: the limits -----------------------------------------------------------------------


class Refusal(BaseModel):
    field: str
    asked: Any
    kept: Any
    why: str


class IntentMandate(BaseModel):
    id: str
    session_id: str
    max_total_usd: float | None = None  # cap on the order total (part + BOM lines + shipping)
    deliver_by: date | None = None
    seller_policy: Policy | None = None
    source: Source = "voice"  # who changed it last
    text: str = ""  # the latest instruction, verbatim
    history: list[dict[str, Any]] = Field(default_factory=list)
    created_at: datetime
    expires_at: datetime

    def summary(self) -> dict[str, Any]:
        return self.model_dump(
            mode="json",
            include={"id", "max_total_usd", "deliver_by", "seller_policy", "source", "text"},
        )

    def spoken(self) -> str:
        """ "under $40, by Fri Oct 2, fastest" (empty when nothing is limited)."""
        bits = []
        if self.max_total_usd is not None:
            bits.append(f"under {_money(self.max_total_usd)}")
        if self.deliver_by is not None:
            bits.append(f"by {_day(self.deliver_by)}")
        if self.seller_policy:
            bits.append(self.seller_policy)
        return ", ".join(bits)


def parse_deliver_by(value: Any, today: date | None = None) -> date | None:
    """ISO date, "today", "tomorrow" or a weekday (the next one, today included)."""
    if value is None or value == "":
        return None
    if isinstance(value, date):
        return value
    today = today or _today()
    word = str(value).strip().lower()
    if word == "today":
        return today
    if word == "tomorrow":
        return today + timedelta(days=1)
    if word in _WEEKDAYS:
        return today + timedelta(days=(_WEEKDAYS.index(word) - today.weekday()) % 7)
    try:
        return date.fromisoformat(word[:10])
    except ValueError:
        raise ValueError(f"deliver_by must be an ISO date or a weekday, got {value!r}") from None


def _intent_path(session_id: str) -> Path:
    return _dir() / "intents" / _session_file(session_id)


def current_intent(session_id: str, now: datetime | None = None) -> IntentMandate | None:
    path = _intent_path(session_id)
    if not path.exists():
        return None
    intent = IntentMandate.model_validate_json(path.read_text())
    if intent.session_id != session_id or intent.expires_at <= (now or _now()):
        return None
    return intent


def set_limits(
    session_id: str,
    *,
    source: Source,
    text: str = "",
    now: datetime | None = None,
    today: date | None = None,
    **limits: Any,
) -> tuple[IntentMandate, list[Refusal], list[str]]:
    """Apply the given limits (only keys present change anything) -> (intent, refusals, changed
    fields). Voice may lower the cap, move the deadline earlier and pick a policy; asking to
    raise or clear is refused, not applied. The panel may set anything (None clears)."""
    now = now or _now()
    intent = current_intent(session_id, now) or IntentMandate(
        id=f"int-{uuid.uuid4().hex[:8]}",
        session_id=session_id,
        source=source,
        created_at=now,
        expires_at=now + INTENT_TTL,
    )
    refused: list[Refusal] = []
    changed: list[str] = []

    def apply(field: str, asked: Any, looser: bool) -> None:
        kept = getattr(intent, field)
        if asked == kept:
            return
        if source == "voice" and looser:
            if asked is not None:  # a voice "no limit" is simply ignored
                refused.append(
                    Refusal(
                        field=field,
                        asked=str(asked) if isinstance(asked, date) else asked,
                        kept=str(kept) if isinstance(kept, date) else kept,
                        why="voice can only tighten a limit; change it on the panel",
                    )
                )
            return
        setattr(intent, field, asked)
        changed.append(field)
        intent.history.append(
            {
                "field": field,
                "from": str(kept) if isinstance(kept, date) else kept,
                "to": str(asked) if isinstance(asked, date) else asked,
                "source": source,
                "text": text,
                "at": now.isoformat(),
            }
        )

    if "max_total_usd" in limits:
        asked = limits["max_total_usd"]
        if asked is not None:
            asked = round(float(asked), 2)
            if not asked > 0:
                raise ValueError("max_total_usd must be positive")
        kept = intent.max_total_usd
        apply("max_total_usd", asked, asked is None or (kept is not None and asked > kept))
    if "deliver_by" in limits:
        asked = parse_deliver_by(limits["deliver_by"], today)
        kept = intent.deliver_by
        apply("deliver_by", asked, asked is None or (kept is not None and asked > kept))
    if "seller_policy" in limits:
        asked = limits["seller_policy"]
        if asked is not None and asked not in ("cheapest", "fastest", "best"):
            raise ValueError(f"seller_policy must be cheapest, fastest or best, got {asked!r}")
        apply("seller_policy", asked, asked is None)

    if changed:
        intent.source = source
        intent.text = text[:500]
        intent.expires_at = now + INTENT_TTL
        cache.write_json_atomic(_intent_path(session_id), intent.model_dump_json(indent=2))
    return intent, refused, changed


# --- cart: exactly what will be charged -----------------------------------------------------


class EvidenceArray(BaseModel):
    spacing_mm: float | None = None
    count: int | None = None


class Evidence(BaseModel):
    """A headset measurement behind the quantity (a notebook tape entry, its array, its photo)."""

    notebook_id: int | str | None = None
    label: str = Field("", max_length=80)  # "tape #3"
    value_m: float | None = None
    camera_id: int | None = None
    photo: str | None = Field(None, max_length=300)  # e.g. /scenes/<site>/thumbs/0316.jpg
    array: EvidenceArray | None = None


class CartLine(BaseModel):
    part_id: str
    seller_idx: int
    seller: str
    units_needed: int
    pack_qty: int
    packs: int  # what POST /checkout calls `qty` (listings as sold)
    unit_price_usd: float  # per listing (pack)
    shipping_usd: float
    line_total_usd: float


class CartMandate(BaseModel):
    id: str
    session_id: str
    intent_id: str | None = None
    lines: list[CartLine]
    bom_id: str | None = None
    bom_lines: list[dict[str, Any]] = Field(default_factory=list)
    shipping_usd: float
    total_usd: float
    evidence: list[Evidence] = Field(default_factory=list)
    hash: str = ""
    created_at: datetime
    receipt_id: str | None = None


_HASHED = {
    "session_id",
    "intent_id",
    "lines",
    "bom_id",
    "bom_lines",
    "shipping_usd",
    "total_usd",
    "evidence",
}


def cart_hash(cart: CartMandate) -> str:
    body = cart.model_dump(mode="json", include=_HASHED)
    canonical = json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return "sha256:" + hashlib.sha256(canonical.encode()).hexdigest()


def build_cart(
    session_id: str,
    part: Part,
    seller_idx: int,
    units_needed: int,
    *,
    bom_id: str | None = None,
    bom_lines: list[bom.BomLine] | None = None,
    evidence: list[Evidence] | None = None,
    intent_id: str | None = None,
    now: datetime | None = None,
) -> CartMandate:
    """Price the cart from the saved listing, the same arithmetic as `checkout.authorize`
    (price_usd x packs + shipping, plus BOM lines). Raises ValueError on bad input."""
    if not 1 <= units_needed <= MAX_UNITS:
        raise ValueError(f"units_needed must be between 1 and {MAX_UNITS}, got {units_needed}")
    if not 0 <= seller_idx < len(part.sellers):
        raise ValueError(f"seller_idx {seller_idx} out of range for part {part.id!r}")
    pack_qty = max(1, part.sellers[seller_idx].pack_qty)
    packs = math.ceil(units_needed / pack_qty)
    seller = checkout._validate(part, seller_idx, packs)
    shipping = seller.shipping_usd or 0.0
    line_total = round(seller.price_usd * packs + shipping, 2)
    bom_receipts = [checkout._bom_line_receipt(line) for line in bom_lines or []]
    total = round(line_total + round(sum(b["total_usd"] for b in bom_receipts), 2), 2)
    cart = CartMandate(
        id=f"cart-{uuid.uuid4().hex[:8]}",
        session_id=session_id,
        intent_id=intent_id,
        lines=[
            CartLine(
                part_id=part.id,
                seller_idx=seller_idx,
                seller=seller.name,
                units_needed=units_needed,
                pack_qty=pack_qty,
                packs=packs,
                unit_price_usd=seller.price_usd,
                shipping_usd=shipping,
                line_total_usd=line_total,
            )
        ],
        bom_id=bom_id if bom_receipts else None,
        bom_lines=bom_receipts,
        shipping_usd=shipping,
        total_usd=total,
        evidence=evidence or [],
        created_at=now or _now(),
    )
    cart.hash = cart_hash(cart)
    return cart


def _cart_path(cart_id: str) -> Path:
    return _dir() / "carts" / f"{cart_id}.json"


def save_cart(cart: CartMandate) -> None:
    cache.write_json_atomic(_cart_path(cart.id), cart.model_dump_json(indent=2))


def load_cart(cart_id: str) -> CartMandate | None:
    path = _cart_path(cart_id)
    return CartMandate.model_validate_json(path.read_text()) if path.exists() else None


# --- checks: what the panel shows before the hold ---------------------------------------------


class Check(BaseModel):
    id: str
    status: Literal["ok", "warn", "fail"]  # fail blocks payment (422); warn is shown, not blocking
    ok: bool
    detail: str


def _check(check_id: str, status: str, detail: str) -> Check:
    return Check(id=check_id, status=status, ok=status != "fail", detail=detail)


def checks(
    cart: CartMandate, intent: IntentMandate | None, part: Part, today: date | None = None
) -> list[Check]:
    today = today or _today()
    line = cart.lines[0]
    seller = part.sellers[line.seller_idx]
    out = []

    ship = f" + {_money(line.shipping_usd)} shipping" if line.shipping_usd else ""
    out.append(
        _check(
            "price_reread",
            "ok",
            f"${line.unit_price_usd:.2f} × {line.packs} from the saved listing{ship}",
        )
    )

    cap = intent.max_total_usd if intent else None
    if cap is None:
        out.append(_check("within_limit", "ok", "no budget limit set"))
    elif cart.total_usd <= cap:
        out.append(_check("within_limit", "ok", f"${cart.total_usd:.2f} ≤ {_money(cap)}"))
    else:
        out.append(
            _check(
                "within_limit", "fail", f"${cart.total_usd:.2f} is over your {_money(cap)} limit"
            )
        )

    deadline = intent.deliver_by if intent else None
    arrival = today + timedelta(days=seller.eta_days) if seller.eta_days is not None else None
    if deadline is None:
        eta = f"arrives {_day(arrival)}" if arrival else (seller.eta or "no delivery estimate")
        out.append(_check("delivery", "ok", f"{eta}; no deadline set"))
    elif arrival is None:
        detail = f"{seller.name} gave no delivery estimate; you asked for {_day(deadline)}"
        out.append(_check("delivery", "warn", detail))
    elif arrival <= deadline:
        out.append(_check("delivery", "ok", f"arrives {_day(arrival)}, by {_day(deadline)}"))
    else:
        detail = f"arrives {_day(arrival)}, after your {_day(deadline)} deadline"
        out.append(_check("delivery", "fail", detail))

    measured = next((e for e in cart.evidence if e.array and e.array.count), None)
    if measured is not None:
        spacing = measured.array.spacing_mm
        how = (
            f"{measured.value_m:.2f} m ÷ {spacing:g} mm → {measured.array.count}"
            if measured.value_m is not None and spacing
            else f"{measured.array.count}"
        )
        detail = f"{measured.label or 'tape'} {how} (reported by headset)"
        if measured.array.count == line.units_needed:
            out.append(_check("qty_evidence", "ok", detail))
        else:
            out.append(_check("qty_evidence", "warn", f"{detail}, cart has {line.units_needed}"))
    elif cart.evidence:
        e = cart.evidence[0]
        value = f" {e.value_m:.2f} m" if e.value_m is not None else ""
        out.append(
            _check("qty_evidence", "ok", f"{e.label or 'tape'}{value} (reported by headset)")
        )
    else:
        out.append(_check("qty_evidence", "warn", "no measurement evidence for the quantity"))

    if seller.verified:
        out.append(_check("seller_verified", "ok", f"{seller.name}: structured seller listing"))
    else:
        detail = f"{seller.name}: listing read by the model, not a seller API"
        out.append(_check("seller_verified", "warn", detail))

    fit = part.fit
    if fit.status == "fits":
        spare = f", {fit.spare_mm:g} mm spare" if fit.spare_mm is not None else ""
        out.append(_check("fit", "ok", f"green{spare}"))
    elif fit.status == "unknown":
        out.append(_check("fit", "warn", "fit not checked against a measurement"))
    else:
        by = f" by {abs(fit.spare_mm):g} mm" if fit.spare_mm is not None else ""
        out.append(_check("fit", "warn", f"{fit.status.replace('_', ' ')}{by}"))

    out.append(_check("card", "ok", "Visa test card •••• 1111, held by the server"))
    return out


# --- hold nonces --------------------------------------------------------------------------

_nonces: dict[str, dict[str, Any]] = {}


def issue_nonce(cart: CartMandate, now: datetime | None = None) -> tuple[str, datetime]:
    """A fresh single-use nonce for this cart; the session's older nonces stop working."""
    now = now or _now()
    for key in [
        k
        for k, v in _nonces.items()
        if v["expires_at"] <= now or v["session_id"] == cart.session_id
    ]:
        del _nonces[key]
    while len(_nonces) >= MAX_NONCES:
        del _nonces[next(iter(_nonces))]
    nonce = "hn_" + secrets.token_urlsafe(32)
    expires_at = now + NONCE_TTL
    _nonces[nonce] = {
        "cart_id": cart.id,
        "cart_hash": cart.hash,
        "session_id": cart.session_id,
        "expires_at": expires_at,
    }
    return nonce, expires_at


def _redeem_nonce(nonce: str, session_id: str, cart_hash_: str, now: datetime) -> str:
    entry = _nonces.pop(nonce, None)  # single use: gone after the first attempt, good or bad
    if entry is None:
        raise MandateError(409, "hold nonce unknown or already used; reopen checkout")
    if entry["expires_at"] <= now:
        raise MandateError(409, "hold nonce expired (2 min); reopen checkout")
    if entry["session_id"] != session_id:
        raise MandateError(409, "hold nonce belongs to another session")
    if entry["cart_hash"] != cart_hash_:
        raise MandateError(409, "cart_hash doesn't match the prepared cart; reopen checkout")
    return entry["cart_id"]


# --- the two checkout steps -----------------------------------------------------------------


def prepare(
    session_id: str,
    part: Part,
    seller_idx: int,
    units_needed: int,
    *,
    bom_id: str | None = None,
    bom_lines: list[bom.BomLine] | None = None,
    evidence: list[Evidence] | None = None,
    now: datetime | None = None,
    today: date | None = None,
) -> dict[str, Any]:
    """`POST /checkout/prepare`: price the cart, run the checks, issue the hold nonce."""
    now = now or _now()
    intent = current_intent(session_id, now)
    cart = build_cart(
        session_id,
        part,
        seller_idx,
        units_needed,
        bom_id=bom_id,
        bom_lines=bom_lines,
        evidence=evidence,
        intent_id=intent.id if intent else None,
        now=now,
    )
    save_cart(cart)
    results = checks(cart, intent, part, today)
    nonce, expires_at = issue_nonce(cart, now)
    return {
        "cart": cart.model_dump(mode="json", exclude={"hash", "session_id", "receipt_id"}),
        "cart_hash": cart.hash,
        "hold_nonce": nonce,
        "nonce_expires_at": expires_at.isoformat(),
        "intent": intent.summary() if intent else None,
        "checks": [c.model_dump() for c in results],
        "all_ok": all(c.ok for c in results),
    }


def verify_hold(
    *,
    session_id: str | None,
    part: Part,
    seller_idx: int,
    qty: int,
    bom_id: str | None,
    bom_lines: list[bom.BomLine],
    cart_hash_: str | None,
    hold_nonce: str | None,
    hold_ms: int | None,
    now: datetime | None = None,
    today: date | None = None,
) -> dict[str, Any]:
    """`POST /checkout` with the hold proof: the nonce is fresh, unused, this session's and this
    cart's; the request still describes that cart; the price hasn't moved (rebuilt hash); every
    check still passes against the current limits. Returns the mandate `checkout.authorize`
    records on the receipt. Raises MandateError (400 / 409 / 422)."""
    if not (session_id and cart_hash_ and hold_nonce and hold_ms is not None):
        raise MandateError(
            400, "hold proof required: session_id, cart_hash, hold_nonce and hold_ms"
        )
    if hold_ms < MIN_HOLD_MS:
        raise MandateError(400, f"hold_ms {hold_ms} < {MIN_HOLD_MS}: paying needs the full hold")
    now = now or _now()
    cart = load_cart(_redeem_nonce(hold_nonce, session_id, cart_hash_, now))
    if cart is None:
        raise MandateError(409, "prepared cart not found; reopen checkout")
    line = cart.lines[0]
    asked = (
        part.id,
        seller_idx,
        qty,
        bom_id if bom_lines else None,
        sorted(b.idx for b in bom_lines),
    )
    prepared = (
        line.part_id,
        line.seller_idx,
        line.packs,
        cart.bom_id,
        sorted(b["idx"] for b in cart.bom_lines),
    )
    if asked != prepared:
        raise MandateError(
            409,
            f"cart changed since prepare ({line.packs} × {line.part_id} from seller "
            f"{line.seller_idx} was prepared); reopen checkout",
        )
    rebuilt = build_cart(
        session_id,
        part,
        seller_idx,
        line.units_needed,
        bom_id=cart.bom_id,
        bom_lines=bom_lines,
        evidence=cart.evidence,
        intent_id=cart.intent_id,
    )
    if rebuilt.hash != cart.hash:
        raise MandateError(409, "the price or listing changed since prepare; reopen checkout")
    intent = current_intent(session_id, now)
    results = checks(cart, intent, part, today)
    if not all(c.ok for c in results):
        raise MandateError(
            422,
            {
                "error": "a mandate check failed",
                "checks": [c.model_dump() for c in results],
            },
        )
    return {
        "session_id": session_id,
        "intent": intent.summary() if intent else None,
        "cart": cart,
        "checks": results,
        "hold_ms": hold_ms,
        "evidence_summary": evidence_summary(cart.evidence),
    }


def record_receipt(cart: CartMandate, receipt_id: str) -> None:
    cart.receipt_id = receipt_id
    save_cart(cart)


def evidence_summary(evidence: list[Evidence]) -> str:
    """ "tape#3 4.20m->8@600mm" (<= 64 chars, ASCII) for the processor's merchant-defined data."""
    e = next((e for e in evidence if e.array and e.array.count), evidence[0] if evidence else None)
    if e is None:
        return "no measurement evidence"
    text = re.sub(r"\s+", "", e.label) or "tape"
    if e.value_m is not None:
        text += f" {e.value_m:.2f}m"
    if e.array and e.array.count:
        text += f"->{e.array.count}"
        if e.array.spacing_mm:
            text += f"@{e.array.spacing_mm:g}mm"
    return text.encode("ascii", "replace").decode()[:64]
