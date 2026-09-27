"""Checkout: authorize a card for a part + seller + qty (plan §1b, §4b.4).

Rail: Visa Acceptance / Cybersource REST sandbox, `apitest.cybersource.com`, test card
4111 1111 1111 1111, `capture=false` (auth-only, nothing is captured or shipped).

Auth is Cybersource's HTTP Signature scheme (shared-secret HMAC, not the SDK):
sign `host`, `date`, `(request-target)`, `digest`, `v-c-merchant-id` with HMAC-SHA256 over
the base64-decoded shared secret. See developer.cybersource.com "HTTP Signature
authentication" / the REST getting-started guide.

Honesty rule: never report an authorization that didn't happen. `mode` is "sandbox" only
when Cybersource actually returned AUTHORIZED; every other case (unconfigured, network
error, declined) is an "offline" receipt and says so.

"Same cart" (plan §4b.6): an optional list of `bom.BomLine`s (already loaded + validated by the
caller from a saved `Bom` -- never client-supplied prices, see server/app.py's `do_checkout`)
rides along as extra receipt line items; the sandbox call authorizes the grand total (part +
bom lines), never just the part.

Measured mandate (brain.md S2, server/mandate.py): when `/checkout` came with a verified hold
proof, `authorize` gets the mandate. The sandbox request then carries the cart hash as
`clientReferenceInformation.code` and the intent id, the quantity's evidence and the session as
`merchantDefinedInformation`, and the receipt gains a `mandate` block: intent -> cart (hash) ->
authorization (with hold_ms), the evidence and the checks. Offline receipts carry it too, with
the honest offline authorization.
"""

import base64
import hashlib
import hmac
import json
import logging
import uuid
from datetime import UTC, datetime
from email.utils import formatdate
from pathlib import Path

import httpx

from server import bom, cache, postcard
from server.config import get_settings
from server.models import Part

logger = logging.getLogger(__name__)

CYBERSOURCE_HOST = "apitest.cybersource.com"
CYBERSOURCE_PATH = "/pts/v2/payments"
TEST_CARD_NUMBER = "4111111111111111"
OFFLINE_LABEL_UNCONFIGURED = "Offline receipt — no payment was authorized (sandbox not configured)"
OFFLINE_LABEL_FAILED = "Offline receipt — no payment was authorized (sandbox authorization failed)"
SANDBOX_LABEL = (
    "Sandbox authorization — Visa Acceptance / Cybersource test environment, no real charge"
)


def _validate(part: Part, seller_idx: int, qty: int):
    if not 1 <= qty <= 500:
        raise ValueError(f"qty must be between 1 and 500, got {qty}")
    if not 0 <= seller_idx < len(part.sellers):
        raise ValueError(
            f"seller_idx {seller_idx} out of range for part {part.id!r} "
            f"with {len(part.sellers)} seller(s)"
        )
    seller = part.sellers[seller_idx]
    if seller.price_usd is None:
        raise ValueError(f"seller {seller.name!r} on part {part.id!r} has no price")
    return seller


def _sign(merchant_id: str, key_id: str, secret_key: str, body: bytes) -> dict[str, str]:
    """HTTP Signature headers for a POST to CYBERSOURCE_PATH. Never logs `secret_key`."""
    date = formatdate(usegmt=True)
    digest = "SHA-256=" + base64.b64encode(hashlib.sha256(body).digest()).decode()
    signing_string = "\n".join(
        [
            f"host: {CYBERSOURCE_HOST}",
            f"date: {date}",
            f"(request-target): post {CYBERSOURCE_PATH}",
            f"digest: {digest}",
            f"v-c-merchant-id: {merchant_id}",
        ]
    )
    mac = hmac.new(base64.b64decode(secret_key), signing_string.encode(), hashlib.sha256)
    signature = base64.b64encode(mac.digest()).decode()
    signature_header = (
        f'keyid="{key_id}", algorithm="HmacSHA256", '
        f'headers="host date (request-target) digest v-c-merchant-id", '
        f'signature="{signature}"'
    )
    return {
        "host": CYBERSOURCE_HOST,
        "date": date,
        "digest": digest,
        "v-c-merchant-id": merchant_id,
        "signature": signature_header,
        "content-type": "application/json",
        "accept": "application/json",
    }


async def _authorize_sandbox(
    merchant_id: str,
    key_id: str,
    secret_key: str,
    total_usd: float,
    receipt_id: str,
    extra: dict | None = None,
) -> str | None:
    """POST the authorization to Cybersource (plus `extra` top-level fields). Returns
    approval_code or raises."""
    body = json.dumps(
        {
            **(extra or {}),
            "clientReferenceCode": receipt_id,
            "processingInformation": {"capture": False},
            "paymentInformation": {
                "card": {
                    "number": TEST_CARD_NUMBER,
                    "expirationMonth": "12",
                    "expirationYear": "2031",
                    "type": "001",
                }
            },
            "orderInformation": {
                "amountDetails": {"totalAmount": f"{total_usd:.2f}", "currency": "USD"},
                "billTo": {
                    "firstName": "John",
                    "lastName": "Doe",
                    "address1": "1 Market St",
                    "locality": "San Francisco",
                    "administrativeArea": "CA",
                    "postalCode": "94105",
                    "country": "US",
                    "email": "test@cybersource.com",
                    "phoneNumber": "4158880000",
                },
            },
        }
    ).encode()
    headers = _sign(merchant_id, key_id, secret_key, body)

    async with httpx.AsyncClient(timeout=15.0) as client:
        resp = await client.post(
            f"https://{CYBERSOURCE_HOST}{CYBERSOURCE_PATH}", content=body, headers=headers
        )
    resp.raise_for_status()
    data = resp.json()
    status = data.get("status")
    if status != "AUTHORIZED":
        raise ValueError(f"cybersource returned status {status!r}, not AUTHORIZED")
    return data.get("processorInformation", {}).get("approvalCode")


def _persist(receipt: dict) -> None:
    orders_dir = Path(get_settings().DATA_DIR) / "orders"
    cache.write_json_atomic(
        orders_dir / f"{receipt['receipt_id']}.json", json.dumps(receipt, indent=2)
    )


def _mandate_fields(mandate: dict) -> dict:
    """Cybersource fields linking the authorization to the mandate ("commerce signals"): the cart
    hash as the client reference (50 chars max) and three merchant-defined values (64 max)."""
    cart = mandate["cart"]
    intent_id = (mandate.get("intent") or {}).get("id") or "no-limits"
    return {
        "clientReferenceInformation": {"code": "at-" + cart.hash.removeprefix("sha256:")[:47]},
        "merchantDefinedInformation": [
            {"key": "1", "value": intent_id[:64]},
            {"key": "2", "value": mandate["evidence_summary"][:64]},
            {"key": "3", "value": mandate["session_id"][:64]},
        ],
    }


def _mandate_receipt(mandate: dict, mode: str, status: str, approval_code: str | None) -> dict:
    cart = mandate["cart"]
    line = cart.lines[0]
    return {
        "intent": mandate.get("intent"),
        "cart": {
            "id": cart.id,
            "hash": cart.hash,
            "total_usd": cart.total_usd,
            "units_needed": line.units_needed,
            "packs": line.packs,
        },
        "authorization": {
            "mode": mode,
            "status": status,
            "approval_code": approval_code,
            "hold_ms": mandate["hold_ms"],
        },
        "evidence": [e.model_dump(mode="json") for e in cart.evidence],
        "checks": [c.model_dump() for c in mandate["checks"]],
    }


def _bom_line_receipt(line: bom.BomLine) -> dict:
    return {
        "idx": line.idx,
        "name": line.name,
        "qty": line.qty,
        "seller": line.seller.name if line.seller else None,
        "price_usd": line.seller.price_usd if line.seller else None,
        "total_usd": bom.line_cost(line),
    }


async def authorize(
    part: Part,
    seller_idx: int,
    qty: int,
    bom_lines: list[bom.BomLine] | None = None,
    safety_verdict: str | None = None,
    mandate: dict | None = None,
) -> dict:
    """Authorize (or record offline) a purchase of `qty` of `part` from `part.sellers[seller_idx]`,
    plus any `bom_lines` riding along in the same cart (see module docstring). `mandate` is
    `server.mandate.verify_hold`'s result when the request carried a hold proof.

    Raises ValueError on bad input (qty out of range, bad seller_idx, seller has no price).
    """
    seller = _validate(part, seller_idx, qty)
    unit_price = seller.price_usd
    shipping = seller.shipping_usd or 0.0
    part_total = round(unit_price * qty + shipping, 2)

    bom_line_receipts = [_bom_line_receipt(line) for line in bom_lines or []]
    bom_total = round(sum(item["total_usd"] for item in bom_line_receipts), 2)
    total = round(part_total + bom_total, 2)
    receipt_id = f"rcpt_{uuid.uuid4().hex[:16]}"

    settings = get_settings()
    merchant_id = settings.CYBERSOURCE_MERCHANT_ID
    key_id = settings.CYBERSOURCE_KEY_ID
    secret_key = settings.CYBERSOURCE_SECRET_KEY

    mode, status, approval_code, label = (
        "offline",
        "OFFLINE_RECEIPT",
        None,
        OFFLINE_LABEL_UNCONFIGURED,
    )
    if merchant_id and key_id and secret_key:
        try:
            approval_code = await _authorize_sandbox(
                merchant_id,
                key_id,
                secret_key,
                total,
                receipt_id,
                extra=_mandate_fields(mandate) if mandate else None,
            )
            mode, status, label = "sandbox", "AUTHORIZED", SANDBOX_LABEL
            logger.info("checkout receipt_id=%s mode=sandbox total_usd=%.2f", receipt_id, total)
        except (httpx.HTTPError, ValueError) as exc:
            logger.warning("checkout sandbox authorization failed, falling back offline: %s", exc)
            mode, status, approval_code, label = (
                "offline",
                "OFFLINE_RECEIPT",
                None,
                OFFLINE_LABEL_FAILED,
            )
    else:
        logger.info("checkout receipt_id=%s mode=offline (sandbox not configured)", receipt_id)

    receipt = {
        "status": status,
        "mode": mode,
        "approval_code": approval_code,
        "card_last4": "1111",
        "total_usd": total,
        "currency": "USD",
        "receipt_id": receipt_id,
        "part_id": part.id,
        "seller": seller.name,
        "qty": qty,
        "unit_price_usd": unit_price,
        "shipping_usd": shipping,
        "bom_lines": bom_line_receipts,
        "safety_verdict": safety_verdict,  # server/safety.py; a recalled part still checks out
        "created_at": datetime.now(UTC).isoformat(),
        "label": label,
    }
    # "See it installed" (server/postcard.py): a disk check only, so it can never fail a checkout.
    if url := postcard.postcard_url(part.id):
        receipt["postcard_url"] = url
    if mandate:
        receipt["mandate"] = _mandate_receipt(mandate, mode, status, approval_code)
    _persist(receipt)
    return receipt
