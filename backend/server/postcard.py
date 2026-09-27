"""'See it installed' postcard (docs/research/grok-ideas/g1-imagine.md §3 idea 2).

One Grok Imagine multi-image edit: image 1 = the user's site view (a scene thumb or a headset
frame), image 2 = the part's seller photo (`data/parts/<id>/image.jpg`). The prompt carries the
part's name, published dims and where it goes. When the headset sends the placed part's
screen-space box, image 1 gets a magenta outline there and the prompt asks the model to fill it
and drop the outline: the box carries the real scale, which the dims text alone can't (text-only
placement came out ~1.3x too wide; see docs/research/grok-ideas/f5-postcard.md). Magenta, not
red: a red outline also wiped a red wall sign next to it.

The result lands at `data/parts/<id>/postcard.jpg` (served by `GET /parts/{id}/postcard.jpg`),
so `checkout.authorize()` can put it on the receipt. Labelled "not to scale": the true-size fit
stays the 3D part's job.
"""

import base64
import io
import logging
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw

from server import assets, imagine, vision
from server.models import Part

logger = logging.getLogger(__name__)

MAX_PLACEMENT_LEN = 200
MAX_SIDE = 1280  # site views are downscaled to this before the edit (1k output anyway)


def _check_box(box: Any) -> list[float] | None:
    """The box as 4 floats, or None. `box` may be raw agent context, so trust nothing."""
    if box is None:
        return None
    try:
        box = [float(c) for c in box]
    except (TypeError, ValueError):
        box = []
    if (
        len(box) != 4
        or not all(0.0 <= c <= 1.0 for c in box)
        or box[0] >= box[2]
        or (box[1] >= box[3])
    ):
        raise ValueError("box must be [x0, y0, x1, y1] normalized 0..1 with x0<x1, y0<y1")
    return box


def prepare_site(site_jpg: bytes, box: list[float] | None) -> bytes:
    """Site view as a <=MAX_SIDE JPEG, with a magenta outline around the normalized `box` if any."""
    try:
        img = Image.open(io.BytesIO(site_jpg)).convert("RGB")
    except OSError as exc:  # PIL.UnidentifiedImageError is an OSError
        raise ValueError("site image is not a readable image") from exc
    img.thumbnail((MAX_SIDE, MAX_SIDE))
    if box:
        w, h = img.size
        xy = [box[0] * w, box[1] * h, box[2] * w, box[3] * h]
        ImageDraw.Draw(img).rectangle(xy, outline=(255, 0, 255), width=max(2, min(w, h) // 120))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=90)
    return buf.getvalue()


def build_prompt(part: Part, placement: str, box: list[float] | None) -> str:
    brand = part.manufacturer or ""
    name = part.name if brand.lower() in part.name.lower() else f"{brand} {part.name}"
    d = part.dims_mm
    where = placement or "where it would normally be installed in this room"
    if box:
        where = f"exactly inside the magenta rectangle in image 1 ({where})"
        size = "It fills the magenta rectangle; remove the magenta outline. "
    else:
        size = (
            "Size it to its real dimensions relative to the counters, cabinets, doors and "
            "appliances around it. "
        )
    return (
        "Image 1 is a photo of the customer's own home. Image 2 is the seller's product photo "
        f"of the {name}, {d.w:.0f} mm wide x {d.d:.0f} mm deep x {d.h:.0f} mm high. "
        f"Edit image 1 so that this exact product is installed {where}. "
        "Use only the main product from image 2 -- not its accessories, badges, labels or "
        "text -- and match its shape, colour and material. "
        f"{size}Fit it the way its name says (an undermount sink sits below the counter edge "
        "with no rim showing; a wall-mounted unit hangs flat on the wall). Match the "
        "perspective, lighting, shadows and grain of image 1 so it looks photographed in place, "
        "fitted by a professional installer." + imagine.KEEP_CLAUSE
    )


def load_site(
    site: str | None, frame_id: str | None, frame_jpg_b64: str | None
) -> tuple[bytes, str | None]:
    """(site jpg, before_url) from a headset frame or a scene thumb. ValueError if neither."""
    if frame_jpg_b64:
        try:
            jpg = base64.b64decode(frame_jpg_b64, validate=True)
        except ValueError as exc:
            raise ValueError("frame_jpg_b64 is not valid base64") from exc
        if len(jpg) > vision.MAX_FRAME_BYTES:
            raise ValueError(f"frame exceeds the {vision.MAX_FRAME_BYTES}-byte cap")
        return jpg, None
    if site and frame_id:
        jpg, _, rel = imagine.load_frame(site, frame_id)
        return jpg, f"/scenes/{site}/{rel}"
    raise ValueError("send frame_jpg_b64, or site + frame_id")


def postcard_path(part_id: str) -> Path:
    return assets.part_dir(part_id) / "postcard.jpg"


def postcard_url(part_id: str) -> str | None:
    """URL of the part's latest postcard (cache-busted by mtime), or None. The receipt hook."""
    path = postcard_path(part_id)
    if not path.is_file():
        return None
    return f"/parts/{part_id}/postcard.jpg?v={path.stat().st_mtime_ns}"


async def make_postcard(
    part: Part,
    site_jpg: bytes,
    *,
    placement: str = "",
    box: list[float] | None = None,
    before_url: str | None = None,
) -> dict[str, Any]:
    """Edit `site_jpg` so `part` is installed in it. Raises ValueError on bad input,
    imagine.ImagineError / cache.OfflineMiss on a failed or offline edit."""
    box = _check_box(box)
    placement = " ".join(placement.split())[:MAX_PLACEMENT_LEN]
    photo = assets.part_dir(part.id) / "image.jpg"
    if not photo.is_file():
        raise ValueError(f"part {part.id!r} has no product photo")
    site = prepare_site(site_jpg, box)
    jpg, meta = await imagine.edit([site, photo.read_bytes()], build_prompt(part, placement, box))
    postcard_path(part.id).write_bytes(jpg)
    logger.info("postcard: %s $%.4f %.1f s", part.id, meta["cost_usd"], meta["latency_s"])
    return {
        "part_id": part.id,
        "image_url": postcard_url(part.id),
        "before_url": before_url,
        "label": imagine.LABEL,
        "cost_usd": meta["cost_usd"],
        "latency_s": meta["latency_s"],
        "cached": meta["cached"],
    }
