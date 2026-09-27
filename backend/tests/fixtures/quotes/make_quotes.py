"""Draw the two SYNTHETIC contractor quotes (F20 tests and demo) and their ground truth.

    uv run python tests/fixtures/quotes/make_quotes.py

Writes clean.png, bad.png and truth.json next to this file. The contractors are made up; every
page says SYNTHETIC. truth.json is what a perfect read returns (`server.quote.Read`): a value that
isn't printed (the clean quote's lump-sum labour has no qty or unit price) is `inferred`.
- clean: an LG mini-split install (both halves of the ENERGY STAR pair) with a permit line;
  the arithmetic adds up (tax is 8.9 % of equipment and materials only).
- bad: the same job, plus the recalled Midea MAW08U1QWT window AC, no permit line, and one
  arithmetic error (line 5: 2 x $45.00 printed as $100.00).
"""

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
W, H = 1275, 1650  # US letter at 150 dpi

# (description, kind, brand, model, qty, unit, total); None = not printed
CLEAN = {
    "contractor": "Totally Fake HVAC Co. (fictional)",
    "lines": [
        (
            "LG 12,000 BTU 20 SEER2 ductless mini-split indoor unit",
            "equipment",
            "LG",
            "KNSAH121B",
            1,
            1150.00,
            1150.00,
        ),
        (
            "LG 12,000 BTU mini-split outdoor unit (heat pump)",
            "equipment",
            "LG",
            "KUSAH121B",
            1,
            1650.00,
            1650.00,
        ),
        ("Line set 3/8 x 1/4 in, 25 ft, insulated", "material", None, None, 1, 189.00, 189.00),
        ("Condensate pump and drain line", "material", None, None, 1, 95.00, 95.00),
        ("Outdoor unit ground pad", "material", None, None, 1, 65.00, 65.00),
        ("240 V 20 A breaker, disconnect and whip", "material", None, None, 1, 145.00, 145.00),
        ("Installation labor (2 techs, 1 day)", "labor", None, None, None, None, 1400.00),
        ("City mechanical + electrical permit fees", "permit", None, None, 1, 185.00, 185.00),
    ],
    "subtotal": 4879.00,
    "tax_rate_pct": 8.9,
    "tax": 293.17,  # 8.9 % of the 3,294.00 of equipment and materials
    "total": 5172.17,
}
BAD = {
    "contractor": "Sample Bros. Heating & Air (fake)",
    "lines": [
        (
            "LG 12,000 BTU ductless mini-split indoor unit",
            "equipment",
            "LG",
            "KNSAH121B",
            1,
            1195.00,
            1195.00,
        ),
        ("LG mini-split outdoor unit", "equipment", "LG", "KUSAH121B", 1, 1695.00, 1695.00),
        (
            "Midea 8,000 BTU U+ window air conditioner (bedroom)",
            "equipment",
            "Midea",
            "MAW08U1QWT",
            1,
            429.00,
            429.00,
        ),
        ("Line set 25 ft, insulated", "material", None, None, 1, 175.00, 175.00),
        ("Wall sleeve / line hide cover", "material", None, None, 2, 45.00, 100.00),  # 90.00
        ("240 V circuit, breaker and disconnect", "material", None, None, 1, 160.00, 160.00),
        (
            "Labor: mini-split install + window unit setup (hrs)",
            "labor",
            None,
            None,
            8,
            165.00,
            1320.00,
        ),
    ],
    "subtotal": 5074.00,
    "tax_rate_pct": 8.9,
    "tax": 334.11,  # 8.9 % of 3,754.00
    "total": 5408.11,
}


def _font(size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.load_default(size=size)


def _money(x: float | None) -> str:
    return "" if x is None else f"${x:,.2f}"


def draw(q: dict, number: str) -> Image.Image:
    img = Image.new("RGB", (W, H), "white")
    d = ImageDraw.Draw(img)
    red = (200, 20, 20)
    d.rectangle((0, 0, W, 90), fill=red)
    d.text(
        (W // 2, 45),
        "SYNTHETIC TEST QUOTE - NOT A REAL CONTRACTOR",
        fill="white",
        font=_font(40),
        anchor="mm",
    )
    d.text((70, 130), q["contractor"], fill="black", font=_font(40))
    d.text(
        (70, 185),
        "100 Example Street, Nowhere, GA 00000  (not a real business)",
        fill="gray",
        font=_font(22),
    )
    d.text((W - 70, 130), f"ESTIMATE #{number}", fill="black", font=_font(32), anchor="ra")
    d.text((W - 70, 175), "Date: 09/26/2026", fill="black", font=_font(24), anchor="ra")
    d.text(
        (70, 250),
        "Customer: Jane Placeholder, 225 North Ave NW, Atlanta, GA 30332",
        fill="black",
        font=_font(24),
    )

    cols = [
        (70, "Description"),
        (700, "Model #"),
        (870, "Qty"),
        (1080, "Unit price"),
        (1205, "Amount"),
    ]
    y = 320
    d.rectangle((60, y - 8, W - 60, y + 36), fill=(230, 230, 230))
    for x, name in cols:
        anchor = "ra" if name in ("Unit price", "Amount") else "la"
        d.text((x, y), name, fill="black", font=_font(24), anchor=anchor)
    y += 60
    f = _font(21)
    for desc, _kind, _brand, model, qty, unit, total in q["lines"]:
        d.text((70, y), desc, fill="black", font=f)
        d.text((700, y), model or "", fill="black", font=f)
        d.text((870, y), "" if qty is None else f"{qty:g}", fill="black", font=f)
        d.text((1080, y), _money(unit), fill="black", font=f, anchor="ra")
        d.text((1205, y), _money(total), fill="black", font=f, anchor="ra")
        y += 52
        d.line((60, y - 14, W - 60, y - 14), fill=(210, 210, 210))
    y += 30
    rows = [
        ("Subtotal", _money(q["subtotal"])),
        (f"Sales tax {q['tax_rate_pct']:g}% (equipment & materials)", _money(q["tax"])),
        ("TOTAL", _money(q["total"])),
    ]
    for label, value in rows:
        big = label == "TOTAL"
        d.text((990, y), label, fill="black", font=_font(28 if big else 24), anchor="ra")
        d.text((1205, y), value, fill="black", font=_font(28 if big else 24), anchor="ra")
        y += 48
    d.text(
        (70, H - 200), "Quote valid 30 days. 1-year labor warranty.", fill="black", font=_font(22)
    )
    d.rectangle((0, H - 90, W, H), fill=red)
    d.text(
        (W // 2, H - 45),
        "SYNTHETIC - made for software testing - fictional business",
        fill="white",
        font=_font(30),
        anchor="mm",
    )
    return img


def _val(v, printed: bool = True) -> dict:
    return {"value": v, "source": "printed_text" if printed and v is not None else "inferred"}


def truth(q: dict) -> dict:
    return {
        "contractor": _val(q["contractor"]),
        "lines": [
            {
                "description": desc,
                "kind": kind,
                "brand": _val(brand),
                "model_no": _val(model),
                "qty": _val(qty),
                "unit_price": _val(unit),
                "line_total": _val(total),
            }
            for desc, kind, brand, model, qty, unit, total in q["lines"]
        ],
        **{k: _val(q[k]) for k in ("subtotal", "tax_rate_pct", "tax", "total")},
    }


if __name__ == "__main__":
    draw(CLEAN, "TF-1042").save(HERE / "clean.png", optimize=True)
    draw(BAD, "SB-2231").save(HERE / "bad.png", optimize=True)
    (HERE / "truth.json").write_text(
        json.dumps({"clean": truth(CLEAN), "bad": truth(BAD)}, indent=1) + "\n"
    )
