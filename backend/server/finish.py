"""Finish variants: "show it in matte black" re-textures the part's GLB instead of tinting it
(docs/research/grok-ideas/g4-round3.md pick 3, results in f11-finish.md).

- `edit()`: one Grok Imagine edit (`/v1/images/edits`) of the product photo into the finish.
  Cached with its image (`finish` namespace), so a warm finish costs $0 and works OFFLINE.
- `registered()`: the variant must keep the photo's outline (`meshgen.photo_mask`), or it's
  dropped: a moved outline would put the product off its own geometry.
- `build()`: the variant on the part's geometry, the way its tier made `model.glb`: `llm`
  rebuilds the cached template plan (a cache hit, no LLM call) with the body in the variant's
  colour; `ai_mesh` repaints model.glb's mesh; `proxy` is a new decal box. Then the photo goes on
  through `meshgen.project`, same face, so size and origin match `model.glb` exactly.
- `apply()`: all of it, never raises. `cad`/`scad` parts, no photo, a moved outline, OFFLINE
  with nothing cached or any failure: `source: "tint"`, the client-side tint as before.
"""

import asyncio
import base64
import dataclasses
import hashlib
import io
import logging
import re
import time
from pathlib import Path

import httpx
import numpy as np
import trimesh
from PIL import Image

from server import assets, cache, meshgen
from server import part_templates as T
from server.config import get_settings
from server.models import Dims, Part

logger = logging.getLogger(__name__)

EDIT_URL = "https://api.x.ai/v1/images/edits"
EDIT_MODEL = "grok-imagine-image-2.0"
PROMPT = (
    "The same product in a {finish} finish. Keep the exact same shape, size, angle, framing, "
    "background and lighting; change only the product's finish and colour."
)
TICKS_PER_USD = 1e10
RETEXTURE_TIERS = ("llm", "ai_mesh", "proxy")
# Outline check: a bbox side may move this fraction of the image and the masks must overlap this
# much. Live (f11-finish.md): 0.1-0.2 % and IoU >= 0.99 on good edits.
MAX_SHIFT = 0.02
MIN_IOU = 0.85
NOT_LISTED = "Not a listed finish"
ROLE_TOL = 24  # sRGB levels: template roles this close to the product colour are recoloured
_METAL_WORDS = ("nickel", "brass", "bronze", "gold", "copper", "steel", "aluminum", "metal")


def slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def listed(part: Part, name: str) -> bool:
    """The seller lists it: `name` matches the part's own finish or one of `finishes[]`
    ("black" matches "Matte Black")."""
    s = slug(name)
    names = [slug(f.name) for f in part.finishes] + [slug(part.finish or "")]
    return any(n and (s in n or n in s) for n in names)


def pbr_finish(name: str) -> str:
    """The `part_templates.FINISHES` key for a finish name: "brushed nickel" -> brushed."""
    words = name.lower()
    metal = "brushed" if any(w in words for w in _METAL_WORDS) else "painted"
    return next((k for k in T.FINISHES if k in words), metal)


def _rgb(hex_str: str) -> np.ndarray:
    return np.array([int(hex_str.lstrip("#")[i : i + 2], 16) for i in (0, 2, 4)])


# assetgen: roles a finish never recolours (a bronze window keeps clear glass; the panes are also
# a window's largest role, which would otherwise make glass "the product's colour").
KEEP_ROLES = {"glass"}


def product_roles(colors: dict[str, str], areas: dict[str, float]) -> list[str]:
    """The template roles in the product's own colour: the largest role, plus any within
    ROLE_TOL of its colour (a water heater's jacket, top and panels, not its copper pipes or
    dark base). ponytail: sRGB max-channel distance, no Lab."""
    areas = {r: a for r, a in areas.items() if r not in KEEP_ROLES} or areas
    main = colors[max(areas, key=areas.get)]
    return [
        r
        for r, c in colors.items()
        if r not in KEEP_ROLES and np.abs(_rgb(c) - _rgb(main)).max() <= ROLE_TOL
    ]


async def edit(photo: Path, finish: str) -> dict:
    """One Imagine edit of `photo` into `finish`: {jpg_b64, cost_usd, latency_s}. Raises on any
    HTTP/response error (the caller falls back to the tint)."""
    url = await asyncio.to_thread(meshgen.photo_data_url, photo, 1024)
    body = {
        "model": EDIT_MODEL,
        "prompt": PROMPT.format(finish=finish),
        "image": {"url": url},
        "response_format": "b64_json",
    }
    headers = {"Authorization": f"Bearer {get_settings().GROK_API_KEY}"}
    start = time.monotonic()
    async with httpx.AsyncClient(timeout=60) as client:
        resp = await client.post(EDIT_URL, json=body, headers=headers)
    resp.raise_for_status()
    data = resp.json()
    cost = (data.get("usage") or {}).get("cost_in_usd_ticks", 0) / TICKS_PER_USD
    latency = time.monotonic() - start
    logger.info("finish: edit %r cost_usd=%.4f latency_s=%.1f", finish, cost, latency)
    return {"jpg_b64": data["data"][0]["b64_json"], "cost_usd": cost, "latency_s": latency}


def _bbox(mask: np.ndarray) -> np.ndarray:
    ys, xs = np.nonzero(mask)
    return np.array([xs.min(), ys.min(), xs.max(), ys.max()])


def registered(photo: Path, variant: Path) -> tuple[bool, float, float]:
    """(outline kept, worst bbox side shift as a fraction of the image, mask IoU). `variant` is
    at `photo`'s size."""
    a, b = meshgen.photo_mask(photo), meshgen.photo_mask(variant)
    if not a.any() or not b.any():
        return False, 1.0, 0.0
    h, w = a.shape
    shift = float((np.abs(_bbox(a) - _bbox(b)) / [w, h, w, h]).max())
    iou = float((a & b).sum() / (a | b).sum())
    return shift <= MAX_SHIFT and iou >= MIN_IOU, shift, iou


def save_variant(jpg_b64: str, photo: Path, out: Path) -> None:
    """The edit's JPEG at the photo's size (Imagine returns ~1k, seller photos vary)."""
    with Image.open(photo) as orig:
        size = orig.size
    img = Image.open(io.BytesIO(base64.b64decode(jpg_b64))).convert("RGB")
    img.resize(size, Image.LANCZOS).save(out, "JPEG", quality=90)


async def build(
    part: Part, photo: Path, variant: Path, name: str, dims: Dims | None = None
) -> trimesh.Scene:
    """`variant` on the part's geometry, the way `assets._generated`/`_box` made model.glb.
    assetgen: `dims` builds it at another size (server/resize.py); the plan is still asked with
    the part's own size, so it's the cached one."""
    dims = dims or part.dims_mm
    if part.asset.tier == "proxy":
        face = await asyncio.to_thread(meshgen.aspect_guess, dims, photo)
        return await asyncio.to_thread(meshgen.decal_box, dims, variant, face)
    plan = await meshgen.ask(part, photo)  # the plan resolve_asset cached: no LLM call
    face = (plan and plan.face) or await asyncio.to_thread(meshgen.aspect_guess, dims, photo)
    _, _, color = await asyncio.to_thread(meshgen.cut_product, variant)
    if part.asset.tier == "llm":
        if plan is None:
            raise ValueError("no template plan for an llm-tier part")
        before = await asyncio.to_thread(meshgen.build, plan, dims)
        roles = product_roles(plan.colors, {r: g.area for r, g in before.geometry.items()})
        colors = {r: color if r in roles else c for r, c in plan.colors.items()}
        finishes = {**plan.finishes, **dict.fromkeys(roles, pbr_finish(name))}
        plan = dataclasses.replace(plan, colors=colors, finishes=finishes)
        geometry = await asyncio.to_thread(meshgen.build, plan, dims)
    else:  # ai_mesh: model.glb's mesh, one colour again, as before its photo went on
        loaded = await asyncio.to_thread(
            trimesh.load, assets.part_dir(part.id) / "model.glb", force="scene"
        )
        geometry = assets._paint(assets._flatten(loaded), color)
        if dims != part.dims_mm:  # assetgen: stretched onto the new size about the mount face
            geometry.apply_scale(stretch(part.dims_mm, dims))
    if plan is None or plan.single:
        rotate = plan.rotate_cw if plan else 0
        geometry = await asyncio.to_thread(meshgen.project, geometry, variant, face, rotate)
    return geometry


def stretch(before: Dims, after: Dims) -> list[float]:
    """assetgen: per-axis factors (X = w, Y = h, Z = d) taking a GLB of `before` onto `after`; the
    origin (the mount-face centre) stays put."""
    return [after.w / before.w, after.h / before.h, after.d / before.d]


def _spoken(name: str, source: str, tint_only: bool, is_listed: bool) -> str:
    if source == "imagine":
        line = f"Here it is in {name}."
    elif tint_only:
        line = f"Tinted it {name}."
    else:
        line = f"I've tinted it {name}; I couldn't render that finish."
    return line if is_listed else line + " That's not a listed finish, so check the seller has it."


async def apply(part: Part, name: str) -> dict:
    """Re-texture `part` in finish `name`: the `POST /parts/{id}/finish` body. Never raises."""
    name = name.strip()
    s, pdir = slug(name), assets.part_dir(part.id)
    photo, glb, variant = pdir / "image.jpg", pdir / f"model-{s}.glb", pdir / f"finish-{s}.jpg"
    is_listed = listed(part, name)
    out = {
        "part_id": part.id,
        "finish": name,
        "source": "tint",
        "model_url": None,
        "image_url": None,
        "label": None if is_listed else NOT_LISTED,
        "cost_usd": None,
        "reason": None,
    }
    if part.asset.tier not in RETEXTURE_TIERS:
        out["reason"] = f"{part.asset.tier} tier"
    elif part.asset.status != "ready" or not photo.exists():
        out["reason"] = "no product photo or model yet"
    else:
        digest = hashlib.sha1(photo.read_bytes()).hexdigest()[:12]
        key = {"part": part.id, "finish": s, "photo": digest}
        try:
            res = await cache.cached("finish", key, lambda: edit(photo, name))
            out["cost_usd"] = res["cost_usd"]
            if not glb.exists():
                await asyncio.to_thread(save_variant, res["jpg_b64"], photo, variant)
                ok, shift, iou = await asyncio.to_thread(registered, photo, variant)
                logger.info("finish: %s %s shift=%.4f iou=%.3f", part.id, s, shift, iou)
                if not ok:
                    raise ValueError(f"outline moved (shift {shift:.1%}, IoU {iou:.2f})")
                geometry = await build(part, photo, variant, name)
                await asyncio.to_thread(geometry.export, glb)
            out.update(
                source="imagine",
                model_url=f"/parts/{part.id}/model-{s}.glb",
                image_url=f"/parts/{part.id}/finish-{s}.jpg",
            )
        except cache.OfflineMiss:
            out["reason"] = "offline, finish not cached"
        # Blind except is deliberate: an HTTP error, a moderation refusal, a bad image, a moved
        # outline or a mesh failure all mean the same thing here: keep the tint.
        except Exception as exc:  # noqa: BLE001
            logger.info("finish: %s %r failed: %s", part.id, name, exc)
            out["reason"] = str(exc)[:200]
    tint_only = part.asset.tier not in RETEXTURE_TIERS
    out["spoken"] = _spoken(name, out["source"], tint_only, is_listed)
    return out
