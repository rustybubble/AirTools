#!/usr/bin/env python3
"""Write the mock part fixtures (SPEC §5.3) in the parts-server contract format (implementation plan §1b):

  Assets/AirTools/Fixtures/Parts/<id>/part.json   specs, sellers, provenance
  Assets/AirTools/Fixtures/Parts/<id>/model.glb   exact-size proxy box, metres, +Y up, origin at the mounting face centre
  Assets/AirTools/Fixtures/Parts/<id>/image.jpg   placeholder product photo

Stdlib only (+ macOS `sips` for PNG → JPG). Re-run any time; output is deterministic.
"""
import json, os, struct, subprocess, sys, zlib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT = os.path.join(ROOT, "Assets", "AirTools", "Fixtures", "Parts")
FETCHED = "2026-09-25T00:00:00-04:00"

PARTS = [
    {
        "id": "hidden-hanger-5k",
        "name": "5\" K-style hidden gutter hanger with screw",
        "manufacturer": "Amerimax (mock)", "model_no": "HH5K-W",
        "dims_mm": {"w": 127, "d": 38, "h": 45},
        "weight_g": 60,
        "color_hex": "#F2F2F0", "finish": "white", "material": "aluminum",
        "finishes": [{"name": "white", "hex": "#F2F2F0", "part_id": "hidden-hanger-5k"},
                     {"name": "brown", "hex": "#5A3E2B", "part_id": "hidden-hanger-5k"}],
        "mount": {"face": "-z", "surface": "fascia"},
        "clearance_mm": {"front": 0, "back": 0, "top": 25, "bottom": 0, "left": 0, "right": 0},
        "spacing_mm": 600,
        "asset": {"tier": "proxy", "source_url": "mock://fixtures", "license": "CC0", "scale_residual_pct": 0.0},
        "spec_url": "https://example.com/specs/hh5k", "citations": ["mock fixture (SPEC §5.3)"],
        "sellers": [
            {"name": "Home Depot (mock)", "price_usd": 4.27, "shipping": "free", "eta": "Tue", "rating": 4.6, "in_stock": True, "url": "https://example.com/hd/hh5k"},
            {"name": "Lowe's (mock)", "price_usd": 4.48, "shipping": "free", "eta": "Mon", "rating": 4.5, "in_stock": True, "url": "https://example.com/lowes/hh5k"},
            {"name": "Amazon (mock)", "price_usd": 3.99, "shipping": "$5.99", "eta": "Thu", "rating": 4.3, "in_stock": True, "url": "https://example.com/amazon/hh5k"},
        ],
        "recommended_seller": 0, "recommendation_reason": "cheapest with free shipping",
        "keywords": ["hanger", "gutter", "bracket", "k-style", "fascia"],
        "rgb": (0.95, 0.95, 0.94),
    },
    {
        "id": "window-ac-small",
        "name": "Small window air conditioner, 5,000 BTU",
        "manufacturer": "Frost (mock)", "model_no": "FW05",
        "dims_mm": {"w": 470, "d": 380, "h": 300},
        "weight_g": 16500,
        "color_hex": "#E8E8E8", "finish": "white", "material": "steel/plastic",
        "finishes": [{"name": "white", "hex": "#E8E8E8", "part_id": "window-ac-small"}],
        "mount": {"face": "-y", "surface": "sill"},
        "clearance_mm": {"front": 0, "back": 0, "top": 25, "bottom": 0, "left": 0, "right": 0},
        "min_window_width_mm": 590, "max_window_width_mm": 1000,
        "spacing_mm": None,
        "asset": {"tier": "proxy", "source_url": "mock://fixtures", "license": "CC0", "scale_residual_pct": 0.0},
        "spec_url": "https://example.com/specs/fw05", "citations": ["mock fixture (SPEC §5.3)"],
        "sellers": [
            {"name": "Best Buy (mock)", "price_usd": 189.99, "shipping": "free", "eta": "Wed", "rating": 4.4, "in_stock": True, "url": "https://example.com/bb/fw05"},
            {"name": "Walmart (mock)", "price_usd": 179.00, "shipping": "free", "eta": "Fri", "rating": 4.2, "in_stock": True, "url": "https://example.com/wm/fw05"},
            {"name": "Home Depot (mock)", "price_usd": 199.00, "shipping": "free", "eta": "Tue", "rating": 4.6, "in_stock": False, "url": "https://example.com/hd/fw05"},
        ],
        "recommended_seller": 1, "recommendation_reason": "cheapest in stock",
        "keywords": ["ac", "air", "conditioner", "window", "cooling"],
        "rgb": (0.91, 0.91, 0.91),
    },
]


def png(width, height, rgb, inset_rgb):
    """Solid background with a centred darker rectangle (a stand-in for the product)."""
    rows = []
    for y in range(height):
        row = bytearray([0])
        for x in range(width):
            inside = width * 0.25 < x < width * 0.75 and height * 0.3 < y < height * 0.7
            c = inset_rgb if inside else rgb
            row += bytes(int(v * 255) for v in c)
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    os.makedirs(OUT, exist_ok=True)
    for p in PARTS:
        d = os.path.join(OUT, p["id"])
        os.makedirs(d, exist_ok=True)
        spec = {k: v for k, v in p.items() if k not in ("keywords", "rgb")}
        spec["fetched_at"] = FETCHED
        spec["cached"] = True
        with open(os.path.join(d, "part.json"), "w") as f:
            json.dump(spec, f, indent=2, ensure_ascii=False)
            f.write("\n")
        dims = p["dims_mm"]
        r, g, b = p["rgb"]
        subprocess.check_call([sys.executable, os.path.join(ROOT, "tools", "make_box_glb.py"),
                               str(dims["w"]), str(dims["h"]), str(dims["d"]), os.path.join(d, "model.glb"),
                               str(r), str(g), str(b), "--mount", p["mount"]["face"]], stdout=subprocess.DEVNULL)
        png_path = os.path.join(d, "image.png")
        with open(png_path, "wb") as f:
            f.write(png(256, 256, (0.93, 0.94, 0.96), tuple(c * 0.8 for c in p["rgb"])))
        subprocess.check_call(["sips", "-s", "format", "jpeg", png_path, "--out", os.path.join(d, "image.jpg")],
                              stdout=subprocess.DEVNULL)
        os.remove(png_path)
        print("wrote", d)


if __name__ == "__main__":
    main()
