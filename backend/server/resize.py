"""assetgen: a part's model at another size (the headset's Size & finish panel: width / height /
depth steppers, "Fit to opening"), built the way its tier built model.glb (`POST
/parts/{id}/resize`).

- `llm`: the template plan `resolve_asset` cached (asked with the part's own size, so it's a cache
  hit and no LLM call) is built again at the new W x H x D -- frame members, sashes and panes keep
  their proportions instead of stretching -- and the product photo goes on the same face.
- `proxy`: the photo decal box (or the plain box) at the new size.
- `cad` / `scad` / `ai_mesh`: model.glb stretched per axis about its mount-face origin (`scaled`):
  a mesh has no parameters to rebuild.
- `finish`: with a finish name, F11 (`finish.apply`, Grok Imagine, cached) first; its variant
  photo and colours go on the rebuilt model. When F11 only tints (`source: "tint"`), the model is
  built in the listing's colours and the headset tints it (`finish_source: "tint"`).

Each size (and finish) is built once: `model-size-<w>x<h>x<d>[-<finish>].glb` next to model.glb,
served by the `model-{slug}.glb` route, with its reply in a `.json` beside it. Sizes stay within
MIN_FACTOR..MAX_FACTOR of the listing (a window made to measure, not a different product).
Never calls an LLM for a warmed part.
"""

import asyncio
import json
import logging
import time
from pathlib import Path

import trimesh

from server import assets, cache, finish, meshgen
from server.models import Dims, Part

logger = logging.getLogger(__name__)

MIN_FACTOR, MAX_FACTOR = 0.5, 2.0  # of the listing's size, per axis
MIN_MM = 10.0


class ResizeError(ValueError):
    """A size the endpoint refuses (422)."""


def check(part: Part, dims: Dims) -> None:
    """Within MIN_FACTOR..MAX_FACTOR of the listing on every axis, and at least MIN_MM."""
    for axis in ("w", "h", "d"):
        want, listed = getattr(dims, axis), getattr(part.dims_mm, axis)
        if want < MIN_MM:
            raise ResizeError(f"{axis} {want:.0f} mm is under {MIN_MM:.0f} mm")
        if not (MIN_FACTOR * listed - 0.5 <= want <= MAX_FACTOR * listed + 0.5):
            raise ResizeError(
                f"{axis} {want:.0f} mm is outside {MIN_FACTOR:g}-{MAX_FACTOR:g}x the listing's "
                f"{listed:.0f} mm"
            )


def slug(dims: Dims, finish_name: str | None) -> str:
    """ "size-1486x1181x80" (+ "-matte-black"): whole millimetres, the model-{slug}.glb alphabet."""
    s = f"size-{round(dims.w)}x{round(dims.h)}x{round(dims.d)}"
    f = finish.slug(finish_name or "")
    return f"{s}-{f}" if f else s


def _scaled(part: Part, dims: Dims) -> trimesh.Scene:
    """model.glb stretched per axis onto `dims` about its origin (the mount-face centre)."""
    scene = trimesh.load(assets.part_dir(part.id) / "model.glb", force="scene")
    scene.apply_transform(_scale_matrix(finish.stretch(part.dims_mm, dims)))
    return scene


def _scale_matrix(k: list[float]):
    m = trimesh.transformations.identity_matrix()
    m[0, 0], m[1, 1], m[2, 2] = k
    return m


async def _template(part: Part, dims: Dims, photo: Path | None) -> trimesh.Scene | None:
    """The cached plan at the new size, photo on (the part's own photo)."""
    plan = await meshgen.ask(part, photo)  # the plan resolve_asset cached: no LLM call
    if plan is None:
        return None
    geometry = await asyncio.to_thread(meshgen.build, plan, dims)
    if photo is not None:  # texture: textured like model.glb (cut-out, front only, sides)
        face = plan.face or await asyncio.to_thread(meshgen.aspect_guess, dims, photo)
        sized = part.model_copy(update={"dims_mm": dims})
        geometry, _ = await assets._textured(sized, geometry, photo, plan, face, "llm")
    return geometry


async def resize(part: Part, dims: Dims, finish_name: str | None = None) -> dict:
    """The part's model at `dims` (and `finish_name`): the `POST /parts/{id}/resize` body. Raises
    ResizeError for a size out of range; otherwise never raises (a failed rebuild is `scaled`)."""
    check(part, dims)
    finish_name = (finish_name or "").strip() or None
    name = slug(dims, finish_name)
    pdir = assets.part_dir(part.id)
    glb, sidecar = pdir / f"model-{name}.glb", pdir / f"model-{name}.json"
    if glb.exists() and sidecar.exists():
        try:
            return {**json.loads(sidecar.read_text()), "cached": True}
        except (OSError, ValueError):
            pass
    start = time.monotonic()
    photo = pdir / "image.jpg"
    photo = photo if photo.exists() else None
    out = {
        "part_id": part.id,
        "dims_mm": dims.model_dump(),
        "listed_mm": part.dims_mm.model_dump(),
        "tier": part.asset.tier,
        "template": part.asset.template,
        "finish": finish_name,
        "finish_source": None,
        "source": "scaled",
        "reason": None,
    }
    geometry = None
    try:
        variant = None
        if finish_name:
            res = await finish.apply(part, finish_name)  # F11: Imagine edit (cached) or tint
            out["finish_source"] = res["source"]
            out["reason"] = res.get("reason")
            if res["source"] == "imagine":
                variant = pdir / f"finish-{finish.slug(finish_name)}.jpg"
        if variant is not None and variant.exists() and photo is not None:
            geometry = await finish.build(part, photo, variant, finish_name, dims=dims)
            out["source"] = "box" if part.asset.tier == "proxy" else "template"
            if part.asset.tier not in ("llm", "proxy"):
                out["source"] = "scaled"
        elif part.asset.tier == "llm":
            geometry = await _template(part, dims, photo)
            out["source"] = "template" if geometry is not None else "scaled"
        elif part.asset.tier == "proxy":
            geometry, _ = await asyncio.to_thread(  # texture: (box, its texture info)
                assets._box, part.model_copy(update={"dims_mm": dims}), photo
            )
            out["source"] = "box"
    # Blind except is deliberate: a template, photo or Imagine failure still leaves the stretched
    # model below, which is the right size.
    except Exception as exc:  # noqa: BLE001
        logger.info("resize %s: rebuild failed (%s), stretching model.glb", part.id, exc)
        out["reason"] = str(exc)[:200]
        geometry = None
    if geometry is None:
        geometry = await asyncio.to_thread(_scaled, part, dims)
        out["source"] = "scaled"
    await asyncio.to_thread(geometry.export, glb)
    out["model_url"] = f"/parts/{part.id}/model-{name}.glb"
    out["seconds"] = round(time.monotonic() - start, 2)
    cache.write_json_atomic(sidecar, json.dumps(out, indent=2))
    logger.info(
        "resize %s -> %s: %s (%s) in %.2f s",
        part.id,
        name,
        out["source"],
        part.asset.tier,
        out["seconds"],
    )
    return {**out, "cached": False}
