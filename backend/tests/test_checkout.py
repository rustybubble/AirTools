import base64
import hashlib
import json

import httpx
import pytest
import respx

from server.bom import BomLine
from server.checkout import CYBERSOURCE_HOST, CYBERSOURCE_PATH, authorize
from server.config import get_settings
from server.models import Part, Seller

CYBERSOURCE_URL = f"https://{CYBERSOURCE_HOST}{CYBERSOURCE_PATH}"

CS_ENV = {
    "CYBERSOURCE_MERCHANT_ID": "test-merchant",
    "CYBERSOURCE_KEY_ID": "test-key-id",
    # any valid base64 string works as the shared secret for signing
    "CYBERSOURCE_SECRET_KEY": base64.b64encode(b"super-secret-shared-key").decode(),
}


def _part(**sellers_kw) -> Part:
    seller = Seller(name="Ace Hardware", price_usd=12.99, shipping_usd=5.0, **sellers_kw)
    no_price = Seller(name="Mystery Shop", price_usd=None)
    return Part(
        id="amerimax-hidden-hanger-5k",
        name="hanger",
        dims_mm={"w": 127, "d": 38, "h": 45},
        sellers=[seller, no_price],
    )


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    for key in CS_ENV:
        monkeypatch.delenv(key, raising=False)
    get_settings.cache_clear()
    yield tmp_path
    get_settings.cache_clear()


# --- validation --------------------------------------------------------------


@pytest.mark.asyncio
async def test_qty_too_low():
    with pytest.raises(ValueError, match="qty"):
        await authorize(_part(), 0, 0)


@pytest.mark.asyncio
async def test_qty_too_high():
    with pytest.raises(ValueError, match="qty"):
        await authorize(_part(), 0, 501)


@pytest.mark.asyncio
async def test_seller_idx_out_of_range():
    with pytest.raises(ValueError, match="seller_idx"):
        await authorize(_part(), 5, 1)


@pytest.mark.asyncio
async def test_seller_has_no_price():
    with pytest.raises(ValueError, match="no price"):
        await authorize(_part(), 1, 1)


# --- offline mode (no sandbox creds configured) -------------------------------


@pytest.mark.asyncio
async def test_offline_receipt_math_and_persistence(tmp_path):
    part = _part()
    receipt = await authorize(part, 0, 3)

    assert receipt["mode"] == "offline"
    assert receipt["status"] == "OFFLINE_RECEIPT"
    assert receipt["approval_code"] is None
    assert receipt["card_last4"] == "1111"
    assert receipt["currency"] == "USD"
    assert receipt["part_id"] == "amerimax-hidden-hanger-5k"
    assert receipt["seller"] == "Ace Hardware"
    assert receipt["qty"] == 3
    assert receipt["unit_price_usd"] == 12.99
    assert receipt["shipping_usd"] == 5.0
    assert receipt["total_usd"] == round(12.99 * 3 + 5.0, 2)
    assert "not configured" in receipt["label"]

    saved = json.loads((tmp_path / "orders" / f"{receipt['receipt_id']}.json").read_text())
    assert saved == receipt


# --- sandbox mode --------------------------------------------------------------


@pytest.mark.asyncio
async def test_sandbox_authorized(monkeypatch):
    for key, value in CS_ENV.items():
        monkeypatch.setenv(key, value)
    get_settings.cache_clear()

    fixture = {
        "id": "1234567890",
        "status": "AUTHORIZED",
        "processorInformation": {"approvalCode": "831000"},
    }
    with respx.mock(assert_all_called=True) as router:
        route = router.post(CYBERSOURCE_URL).mock(return_value=httpx.Response(201, json=fixture))
        receipt = await authorize(_part(), 0, 2)

    assert receipt["mode"] == "sandbox"
    assert receipt["status"] == "AUTHORIZED"
    assert receipt["approval_code"] == "831000"
    assert receipt["card_last4"] == "1111"
    assert receipt["total_usd"] == round(12.99 * 2 + 5.0, 2)

    request = route.calls.last.request
    sig = request.headers["signature"]
    assert 'keyid="test-key-id"' in sig
    assert 'algorithm="HmacSHA256"' in sig
    assert "headers=" in sig
    assert "signature=" in sig

    expected_digest = (
        "SHA-256=" + base64.b64encode(hashlib.sha256(request.content).digest()).decode()
    )
    assert request.headers["digest"] == expected_digest


@pytest.mark.asyncio
async def test_sandbox_network_failure_falls_back_offline(monkeypatch):
    for key, value in CS_ENV.items():
        monkeypatch.setenv(key, value)
    get_settings.cache_clear()

    with respx.mock(assert_all_called=True) as router:
        router.post(CYBERSOURCE_URL).mock(side_effect=httpx.ConnectError("no route to host"))
        receipt = await authorize(_part(), 0, 1)

    assert receipt["mode"] == "offline"
    assert receipt["status"] == "OFFLINE_RECEIPT"
    assert receipt["approval_code"] is None
    assert "sandbox authorization failed" in receipt["label"]


@pytest.mark.asyncio
async def test_sandbox_declined_falls_back_offline_honestly(monkeypatch):
    for key, value in CS_ENV.items():
        monkeypatch.setenv(key, value)
    get_settings.cache_clear()

    fixture = {"id": "1234567890", "status": "DECLINED"}
    with respx.mock(assert_all_called=True) as router:
        router.post(CYBERSOURCE_URL).mock(return_value=httpx.Response(200, json=fixture))
        receipt = await authorize(_part(), 0, 1)

    assert receipt["mode"] == "offline"
    assert receipt["approval_code"] is None
    assert receipt["status"] == "OFFLINE_RECEIPT"


# --- bom lines: "same cart" (plan §4b.6) ---------------------------------------


def _bom_line(idx: int, name: str, qty: int, price: float, pack_qty: int = 1) -> BomLine:
    seller = Seller(
        name="Ace Hardware", price_usd=price, pack_qty=pack_qty, unit_price_usd=price / pack_qty
    )
    return BomLine(idx=idx, name=name, qty=qty, reason="fasten it", seller=seller)


@pytest.mark.asyncio
async def test_offline_receipt_adds_bom_lines_to_total():
    line = _bom_line(0, "Gutter screws", 2, 12.0, pack_qty=100)  # two $12 100-packs = $24
    receipt = await authorize(_part(), 0, 3, bom_lines=[line])

    part_total = round(12.99 * 3 + 5.0, 2)
    bom_total = round(12.0 * 2, 2)
    assert receipt["total_usd"] == round(part_total + bom_total, 2)
    assert receipt["bom_lines"] == [
        {
            "idx": 0,
            "name": "Gutter screws",
            "qty": 2,
            "seller": "Ace Hardware",
            "price_usd": 12.0,
            "total_usd": pytest.approx(24.0),
        }
    ]


@pytest.mark.asyncio
async def test_bom_line_without_a_seller_contributes_nothing():
    line = BomLine(idx=0, name="Mystery item", qty=1, reason="", seller=None)
    receipt = await authorize(_part(), 0, 1, bom_lines=[line])

    assert receipt["bom_lines"][0]["total_usd"] == 0.0
    assert receipt["total_usd"] == round(12.99 * 1 + 5.0, 2)


@pytest.mark.asyncio
async def test_sandbox_authorizes_grand_total_including_bom_lines(monkeypatch):
    for key, value in CS_ENV.items():
        monkeypatch.setenv(key, value)
    get_settings.cache_clear()

    fixture = {
        "id": "1234567890",
        "status": "AUTHORIZED",
        "processorInformation": {"approvalCode": "831000"},
    }
    line = _bom_line(0, "Gutter screws", 2, 12.0, pack_qty=100)
    with respx.mock(assert_all_called=True) as router:
        route = router.post(CYBERSOURCE_URL).mock(return_value=httpx.Response(201, json=fixture))
        receipt = await authorize(_part(), 0, 3, bom_lines=[line])

    expected_total = round(12.99 * 3 + 5.0 + 12.0 * 2, 2)
    body = json.loads(route.calls.last.request.content)
    assert body["orderInformation"]["amountDetails"]["totalAmount"] == f"{expected_total:.2f}"
    assert receipt["total_usd"] == expected_total
