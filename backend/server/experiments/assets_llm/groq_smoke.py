"""One-shot Groq smoke test: product photo + dims -> CadQuery code (r3-toolchain.md §4).

    uv run python -m server.experiments.assets_llm.groq_smoke data/parts/<id> work/assets-llm/smoke

Writes <out>/code.py, <out>/response.json (usage, latency; no key). Then run cq_to_glb, render and
score by hand (commands in the doc).
"""

import base64
import io
import json
import re
import sys
import time
from pathlib import Path

from openai import OpenAI
from PIL import Image

from server.config import get_settings

MODEL = "qwen/qwen3.8-27b"

PROMPT = """You are a CAD engineer. Write CadQuery (Python) code that models the product in the photo
as a clean, recognisable 3D asset for an AR catalogue. Match the overall shape and the main
visible features (panels, grilles, bowls, flanges, holes, knobs); skip tiny text and logos.

Product: {name}
Material/finish: {material} / {finish}
Exact overall size (mm): width {w} (X), depth {d} (Y), height {h} (Z).

Frame (must follow exactly): millimetres, Z up. The product's front faces -Y (CadQuery "front").
Its back / mounting face lies on the plane Y=0, so the product occupies -{d} <= Y <= 0,
-{hw} <= X <= {hw}, 0 <= Z <= {h}. The bounding box must be exactly these dims.

Rules: `import cadquery as cq` is already done; no other imports, no file I/O, no show().
End with `parts = [(workplane_or_shape, "#RRGGBB"), ...]`, one entry per colour.
Reply with one ```python code block only."""


def _photo_data_url(path: Path, max_side: int = 768) -> str:
    img = Image.open(path).convert("RGB")
    img.thumbnail((max_side, max_side))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=85)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


def main(part_dir: Path, out: Path) -> None:
    part = json.loads((part_dir / "part.json").read_text())
    dm = part["dims_mm"]
    prompt = PROMPT.format(
        name=part["name"],
        material=part.get("material"),
        finish=part.get("finish"),
        w=dm["w"],
        d=dm["d"],
        h=dm["h"],
        hw=round(dm["w"] / 2, 2),
    )
    client = OpenAI(base_url="https://api.groq.com/openai/v1", api_key=get_settings().GROQ_API_KEY)
    t0 = time.time()
    raw = client.chat.completions.with_raw_response.create(
        model=MODEL,
        messages=[
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": prompt},
                    {
                        "type": "image_url",
                        "image_url": {"url": _photo_data_url(part_dir / "image.jpg")},
                    },
                ],
            }
        ],
        temperature=0.3,
        max_tokens=8000,
        extra_body={"reasoning_format": "parsed"},
    )
    latency = time.time() - t0
    resp = raw.parse()
    limits = {k: v for k, v in raw.headers.items() if k.startswith("x-ratelimit")}
    msg = resp.choices[0].message
    text = msg.content or ""
    m = re.search(r"```(?:python)?\n(.*?)```", text, re.DOTALL)
    out.mkdir(parents=True, exist_ok=True)
    (out / "code.py").write_text(m.group(1) if m else text)
    (out / "response.json").write_text(
        json.dumps(
            {
                "model": MODEL,
                "part": part["id"],
                "latency_s": round(latency, 2),
                "usage": resp.usage.model_dump() if resp.usage else None,
                "finish_reason": resp.choices[0].finish_reason,
                "ratelimit_headers": limits,
                "reasoning_chars": len(getattr(msg, "reasoning", None) or ""),
                "content": text,
            },
            indent=1,
        )
    )
    print(json.dumps({"latency_s": round(latency, 2), "usage": resp.usage.model_dump(), **limits}))


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
