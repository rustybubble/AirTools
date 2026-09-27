"""LLM-made part assets: the `llm` tier of `server/assets.py` (docs/research/p4-llm-assets-bench.md
§5, experiments E1/E3/E4/E5 under docs/research/assets-llm/).

- `ask()`: one vision call on role `asset` (`LLM_ASSET`, default Groq qwen) picks a parametric
  template (`server/part_templates.py`) and returns its params, colours, finishes, the face the
  product photo shows, whether the photo shows one product, and a shape class. No photo, or a
  failed vision call: the `extract` text model on the part's name and listing text instead, with
  template colours. Cached through `server/cache.py`; `None` (never raises) when both fail.
  assetgen: a poor answer -- none at all, or not the template a known shape needs (a "window"
  answered as a flat panel or a box, `expected_template`) -- is asked once more on role
  `asset_retry` (`LLM_ASSET_RETRY`, default xAI Grok 4.20 non-reasoning), and Grok's answer wins
  when it's better. `Plan.made_by` says which provider:model answered (a Groq call that fell back
  to xAI inside `llm.chat` says xai), `Plan.retried` whether the retry made it.
- `build()`: the template at exactly W x H x D in the api.md §2 frame.
- `project()`: the product photo on the triangles facing the face it shows (E5).
- `decal_box()`: the exact-size box with the photo on one face (E3), the instant/last-resort tier.
- `grok_scad()` / `grok_scad_run()`: E4's Grok OpenSCAD loop (role `asset_scad`, LLM_ASSET_SCAD at
  ASSET_SCAD_EFFORT; default grok-4.7 at low effort, ~45-55 s and ~$0.03 a part). cad: the asset-mode
  "llm_scad" tier runs it in the background (server/assets.py) and `server.warm --scad` ahead of
  need; each run leaves its record next to the GLB (scad.json / scad.failed.json, and
  scad.writing.json while it runs).
"""

import asyncio
import base64
import hashlib
import io
import json
import logging
import os
import re
import shutil
import subprocess
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image, ImageDraw
from scipy import ndimage

from server import cache, llm, photo_cut
from server import part_templates as T
from server.config import get_settings
from server.models import Dims, Part

logger = logging.getLogger(__name__)

# face -> (outward normal, image-right, image-up) for a camera outside that face, +Y up
# (top/bottom: the part's back at the top of the image).
FACES = {
    "front": ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
    "back": ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
    "right": ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
    "left": ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
    "top": ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
    "bottom": ((0, -1, 0), (-1, 0, 0), (0, 0, -1)),
}
SIDES = ("left", "right")  # a profile photo goes on both sides
SHAPE_CLASSES = ("template", "moulded", "organic")
FACE_DOT = 0.35  # triangles facing the photo axis at least this much get the photo
TEX_MAX = 1024
JPEG_Q = 80
SIL = 160  # silhouette raster for the profile flip test
# Catalogue sink photos look down into the bowl; the view answer said "front" (E3 §4).
TEMPLATE_FACE = {"sink": "top"}
# Photo mode (E5): front relief the photo already shows is dropped, so photo and geometry don't
# draw the same slats twice, offset.
FLAT = {
    "box_appliance": {"grille1_style": "none", "grille2_style": "none", "control": False},
    "fixture": {"nozzle_rings": 0, "center_disc": False},
    "window": {"grid_x": 0, "grid_y": 0, "hardware": False},  # assetgen: the photo shows them
}

PROMPT = """Map {subject} to ONE parametric 3D template and fill its parameters.
Frame: the product's front faces the viewer. W = width (left-right), H = height (up), D = depth
(back/mounting face to front). Product: {name}. Material/finish: {material} / {finish}.
Exact size mm: W {w}, H {h}, D {d}. If {several} several items or accessories, model only the
one that matches this size.

Templates (param=default [range] note). Region params are [u0,u1,v0,v1] fractions of the front
face, u from the left, v from the bottom.
{catalogue}

Pick the template whose shape fits best. Set only params that differ from the defaults.
Finishes, per colour role: {finishes} (chrome = mirror-like plated metal, stainless / brushed =
satin metal, galvanized = dull zinc-coated steel).
shape_class: "template" when the chosen template captures the shape, "moulded" for a curved
moulded or cast part no template captures (plastic clip, vinyl hanger), "organic" for any other
free-form shape.
"""

PHOTO_PART = """Give colors as sRGB hex taken from the photo for every visible colour role.
Photo: which one face of the product does it mostly show? "front" is the side facing a person
standing in front of the installed product, "back" is the side against the mounting surface
({surface}). In a left/right view you see the product's depth: the mounting surface is at the
left or right edge. In a top view you look down on it. rotate_cw is 0 unless the product is
clearly lying on its side. "single" is true when the photo shows just this one product (no kit,
multi-pack, or accessories around it).
Reply JSON only: {{"template": "...", "params": {{...}}, "colors": {{"role": "#RRGGBB"}},
"finishes": {{"role": "finish"}}, "photo": {{"face": "front|back|left|right|top|bottom",
"rotate_cw": 0, "single": true}}, "shape_class": "template|moulded|organic"}}"""

TEXT_PART = """Description (the listing, no photo): {desc}
Reply JSON only: {{"template": "...", "params": {{...}}, "finishes": {{"role": "finish"}},
"shape_class": "template|moulded|organic"}}"""


@dataclass
class Plan:
    """A validated `ask()` answer. `face` is None on the text path (no photo seen)."""

    template: str
    params: dict
    colors: dict
    finishes: dict
    face: str | None
    rotate_cw: int
    single: bool
    shape_class: str
    # assetgen: which provider:model answered ("groq:qwen/qwen3.8-27b", "xai:grok-4.20-...";
    # "... (cached)" when the answer predates the record), and whether the Grok retry made it.
    made_by: str = ""
    retried: bool = False


def photo_data_url(photo: Path, max_side: int) -> str:
    img = Image.open(photo).convert("RGB")
    img.thumbnail((max_side, max_side))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=85)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


def _user(text: str, photo_url: str | None = None) -> list[dict]:
    if photo_url is None:
        return [{"role": "user", "content": text}]
    content = [
        {"type": "text", "text": text},
        {"type": "image_url", "image_url": {"url": photo_url}},
    ]
    return [{"role": "user", "content": content}]


async def cached_chat(role: str, messages: list[dict], **kw) -> str:
    """`llm.chat`'s reply text through the disk cache, keyed by the role's provider:model and a
    hash of the request: a warmed part costs no call, online or offline (OfflineMiss on a miss)."""
    text, _ = await cached_chat_by(role, messages, **kw)
    return text


def made_by(provider: str, model: str, answered: str | None) -> str:
    """assetgen: "provider:model" of the model that answered. `answered` is the response's own
    model name: a Groq call `llm.chat` re-sent to xAI (LLM_FALLBACK) comes back as grok-*."""
    if not answered:
        return f"{provider}:{model}"
    if answered.startswith("grok") and provider != "xai":
        return f"xai:{answered}"
    return f"{provider}:{answered}"


async def cached_chat_by(role: str, messages: list[dict], **kw) -> tuple[str, str]:
    """assetgen: `cached_chat` plus who answered ("provider:model", `made_by`). The first call
    records it next to the answer (cache namespace `asset_llm_by`, same key) with its seconds;
    an answer cached before that record existed says "<role's provider:model> (cached)"."""
    text, meta = await cached_chat_meta(role, messages, **kw)
    return text, meta["by"]


async def cached_chat_meta(
    role: str, messages: list[dict], *, sample: int = 0, **kw
) -> tuple[str, dict]:
    """cad: `cached_chat_by` with the call's record: {by, seconds, cost_usd, prompt_tokens,
    completion_tokens, reasoning_tokens, cached}. A cache hit costs nothing now (cost_usd 0,
    seconds 0); its first call's figures stay under `first_*`. `sample` > 0: another answer to
    the same request (its own cache entry)."""
    provider, model = llm.resolve_role(role)
    keyed = [messages, kw] if not sample else [messages, kw, {"sample": sample}]
    digest = hashlib.sha1(json.dumps(keyed, sort_keys=True).encode()).hexdigest()
    key = {"model": f"{provider}:{model}", "req": digest}
    live: dict = {}

    async def call() -> str:
        start = time.monotonic()
        resp = await llm.chat(role, messages, **kw)
        by = made_by(provider, model, getattr(resp, "model", None))
        seconds = round(time.monotonic() - start, 2)
        usage = getattr(resp, "usage", None)
        details = getattr(usage, "completion_tokens_details", None)
        record = {
            "by": by,
            "seconds": seconds,
            "cost_usd": llm.cost_usd(usage) if usage is not None else None,
            "prompt_tokens": getattr(usage, "prompt_tokens", None),
            "completion_tokens": getattr(usage, "completion_tokens", None),
            "reasoning_tokens": getattr(details, "reasoning_tokens", None),
        }
        cache.put("asset_llm_by", key, record)
        live.update(record)
        logger.info("meshgen: role=%s answered by %s in %.1f s", role, by, seconds)
        return resp.choices[0].message.content or ""

    text = await cache.cached("asset_llm", key, call)
    if live:
        return text, {**live, "cached": False}
    meta = cache.get("asset_llm_by", key)
    meta = meta if isinstance(meta, dict) else {}
    return text, {
        "by": meta.get("by") or f"{provider}:{model} (cached)",
        "seconds": 0.0,
        "cost_usd": 0.0,
        "first_seconds": meta.get("seconds"),
        "first_cost_usd": meta.get("cost_usd"),
        "prompt_tokens": None,
        "completion_tokens": None,
        "reasoning_tokens": None,
        "cached": True,
    }


def _parse(text: str) -> dict:
    text = re.sub(r"<think>.*?</think>", "", text, flags=re.DOTALL).strip()
    text = re.sub(r"^```(?:json)?|```$", "", text.strip(), flags=re.MULTILINE).strip()
    obj = json.loads(text)
    if not isinstance(obj, dict):
        raise ValueError("reply is not a JSON object")  # noqa: TRY004 -- callers catch ValueError
    return obj


def parse_plan(obj: dict, with_photo: bool) -> Plan:
    """Clamp an LLM answer to the catalogue: unknown template -> box, params clipped to their
    ranges, bad colours -> template colours, unknown face -> front."""
    template, params, colors = T.validate(
        obj.get("template"), obj.get("params"), obj.get("colors") if with_photo else None
    )
    finishes = obj.get("finishes") if isinstance(obj.get("finishes"), dict) else {}
    finishes = {r: f for r, f in finishes.items() if r in colors and f in T.FINISHES}
    photo = obj.get("photo") if isinstance(obj.get("photo"), dict) else {}
    face = photo.get("face") if photo.get("face") in FACES else "front"
    if face in ("front", "back"):
        face = TEMPLATE_FACE.get(template, face)
    rotate = photo.get("rotate_cw") if photo.get("rotate_cw") in (0, 90, 180, 270) else 0
    shape = obj.get("shape_class") if obj.get("shape_class") in SHAPE_CLASSES else "template"
    return Plan(
        template=template,
        params=params,
        colors=colors,
        finishes=finishes,
        face=face if with_photo else None,
        rotate_cw=rotate if with_photo else 0,
        single=photo.get("single") is not False,  # missing = one product, catalogue default
        shape_class=shape,
    )


def _prompt(part: Part, with_photo: bool) -> str:
    d = part.dims_mm
    text = PROMPT.format(
        subject="the product in the photo" if with_photo else "the product described below",
        several="the photo shows" if with_photo else "the description mentions",
        name=part.name,
        material=part.material,
        finish=part.finish,
        w=round(d.w, 1),
        h=round(d.h, 1),
        d=round(d.d, 1),
        catalogue=T.catalogue(),
        finishes="|".join(T.FINISHES),
    )
    if with_photo:
        surface = part.mount.get("surface") or "wall, floor or structure"
        return text + PHOTO_PART.format(surface=surface)
    listing = [part.name, part.manufacturer, part.model_no, part.material, part.finish]
    desc = ". ".join(str(x) for x in listing if x)
    return text + TEXT_PART.format(desc=desc)


async def _ask(role: str, part: Part, photo: Path | None) -> Plan | None:
    try:
        prompt = _prompt(part, photo is not None)
        if photo is not None:
            messages = _user(prompt, await asyncio.to_thread(photo_data_url, photo, 512))
            kw = {"max_tokens": 1000}
        else:  # gpt-oss reasons before answering; keep it short, it counts as output
            messages = [{"role": "system", "content": "Reasoning: low"}, *_user(prompt)]
            kw = {"max_tokens": 2000}
        text, by = await cached_chat_by(
            role, messages, response_format={"type": "json_object"}, temperature=0.2, **kw
        )
        plan = parse_plan(_parse(text), photo is not None)
        plan.made_by = by
        return plan
    # Blind except is deliberate: the tier must never raise. Network/SDK errors, rate limits,
    # KeysExhausted, OfflineMiss and bad JSON all mean "no plan", and the caller falls through.
    except Exception as exc:  # noqa: BLE001
        logger.info("meshgen: %s call for %s failed: %s", role, part.id, exc)
        return None


# assetgen: a window, not a window screen / film / lock / well ("... Window with Screen" is one).
WINDOW_NAME = (
    r"\bwindows?\b(?!\s+(?:(?!with\b|and\b|plus\b|in\b)\w+\s+)?(?:screens?|films?|shades?"
    r"|blinds?|curtains?|locks?|cranks?|operators?|latch(?:es)?|wells?|trim|flashing|tape"
    r"|treatments?|valances?|hardware|kits?|fans?)\b)"
)

# assetgen: the template a product's name asks for. A plan with another template is "poor" and
# gets one Grok retry (`ask`). First match wins; `None` = any template will do.
_EXPECTED = [
    (r"\b(?:air conditioner|a/c|ac unit|dehumidifier|microwave)\b", "box_appliance"),
    (WINDOW_NAME, "window"),
    (r"\b(?:water heater|hot water tank)\b", "cylinder_tank"),
    (r"\b(?:refrigerator|fridge|freezer)\b", "tall_box"),
    (r"\b(?:sink|basin)\b", "sink"),
    (r"\b(?:faucet|shower ?head)\b", "fixture"),
    (r"\bsolar panel\b", "flat_panel"),
    (r"\b(?:condenser|mini[- ]split|heat pump)\b", "fan_unit"),
    (r"\bjoist hanger\b", "joist_hanger"),
]


def expected_template(part: Part) -> str | None:
    """The template a known shape needs, from the part's name ("Double Hung Vinyl Window" ->
    window), or None."""
    name = (part.name or "").lower()
    for pattern, template in _EXPECTED:
        if re.search(pattern, name):
            return template
    return None


def poor(plan: Plan | None, part: Part) -> bool:
    """assetgen: no plan, or not the template the product's shape needs."""
    if plan is None:
        return True
    want = expected_template(part)
    return want is not None and plan.template != want


def _retry_ready() -> bool:
    """The retry role resolves to a provider with a key (Grok by default)."""
    try:
        provider, _ = llm.resolve_role("asset_retry")
    except ValueError:
        return False
    return bool(getattr(get_settings(), llm.PROVIDERS[provider]["api_key_field"], None))


async def ask(part: Part, photo: Path | None) -> Plan | None:
    """The vision call on `photo`, else (no photo, or it failed) the text model; a poor answer
    gets one retry on `asset_retry` (assetgen). Never raises."""
    plan = await _ask("asset", part, photo) if photo is not None else None
    if plan is None:
        plan = await _ask("extract", part, None)
    if poor(plan, part) and _retry_ready():
        first = f"{plan.template} by {plan.made_by}" if plan else "no answer"
        better = await _ask("asset_retry", part, photo)
        if better is not None and (plan is None or not poor(better, part)):
            better.retried = True
            logger.info(
                "meshgen: %s: %s was poor (want %s); the retry's %s by %s wins",
                part.id,
                first,
                expected_template(part),
                better.template,
                better.made_by,
            )
            return better
        logger.info("meshgen: %s: the retry didn't do better than %s", part.id, first)
    return plan


def build(plan: Plan, dims: Dims) -> trimesh.Scene:
    """The plan's template at exactly `dims`; front relief dropped when a front photo goes on."""
    params = plan.params
    if plan.face == "front" and plan.single and plan.template in FLAT:
        params = {**params, **FLAT[plan.template]}
    scene, _ = T.build(plan.template, dims.model_dump(), params, plan.colors, plan.finishes)
    return scene


# ---------------------------------------------------------------- photo (E3/E5)


def photo_mask(photo: Path, tol: int = 12) -> np.ndarray:
    """Foreground of a plain-background product photo: flood the border colour in from the edges
    (a white product on white keeps its body), keep the largest blob, fill holes.
    ponytail: no learned matting; add rembg if lifestyle photos matter."""
    img = np.asarray(Image.open(photo).convert("RGB"), dtype=np.int16)
    border = np.concatenate([img[0], img[-1], img[:, 0], img[:, -1]])
    near_bg = np.abs(img - np.median(border, axis=0)).max(axis=2) <= tol
    lab, _ = ndimage.label(near_bg)
    edge = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
    fg = ndimage.binary_opening(~np.isin(lab, edge[edge > 0]))
    lab, n = ndimage.label(fg)
    if n > 1:
        fg = lab == (np.argmax(np.bincount(lab.ravel())[1:]) + 1)
    return ndimage.binary_fill_holes(fg)


def cut_product(photo: Path) -> tuple[Image.Image, np.ndarray, str]:
    """(crop to the product with its background painted the product's edge colour, mask crop,
    edge colour hex). Edge colour = median of product pixels in a band just inside the outline."""
    img = np.asarray(Image.open(photo).convert("RGB"))
    mask = photo_mask(photo)
    if not mask.any():
        mask[:] = True
    ys, xs = np.nonzero(mask)
    k = max(2, min(mask.shape) // 100)  # skip the anti-aliased rim, sample the next 2k px
    inner = ndimage.binary_erosion(mask, iterations=k)
    band = inner & ~ndimage.binary_erosion(inner, iterations=2 * k)
    edge = np.median(img[band if band.any() else mask], axis=0).astype(np.uint8)
    out = np.where(mask[..., None], img, edge)
    box = (slice(ys.min(), ys.max() + 1), slice(xs.min(), xs.max() + 1))
    return Image.fromarray(out[box]), mask[box], "#{:02X}{:02X}{:02X}".format(*edge)


def aspect_guess(dims: Dims, photo: Path) -> str:
    """No-LLM face guess: the face whose aspect best matches the product crop (9/12 in E3);
    "front" if the photo won't load."""
    try:
        if get_settings().ASSET_PHOTO_CUT:  # texture: the product cut out, not the background
            size = photo_cut.cut(photo, dims.w / dims.h, size=(dims.w, dims.h, dims.d)).image.size
        else:
            size = cut_product(photo)[0].size
    except OSError:  # PIL's UnidentifiedImageError included
        return "front"
    a = np.log(size[0] / size[1])
    cand = {"front": dims.w / dims.h, "right": dims.d / dims.h, "top": dims.w / dims.d}
    return min(cand, key=lambda k: abs(a - np.log(cand[k])))


def _world_meshes(scene: trimesh.Scene) -> list[tuple[str, trimesh.Trimesh]]:
    out = []
    for node in scene.graph.nodes_geometry:
        tf, name = scene.graph[node]
        m = scene.geometry[name].copy()
        m.apply_transform(tf)
        out.append((name, m))
    return out


def _silhouette(meshes, r, up, lo, ext) -> np.ndarray:
    """Orthographic silhouette along the photo axis, stretched to SIL x SIL over the bbox face."""
    img = Image.new("1", (SIL, SIL), 0)
    draw = ImageDraw.Draw(img)
    for _, m in meshes:
        tri = m.vertices[m.faces]
        u = (tri @ r - lo[0]) / ext[0] * (SIL - 1)
        v = (1 - (tri @ up - lo[1]) / ext[1]) * (SIL - 1)
        for pu, pv in zip(u, v, strict=True):
            draw.polygon(list(zip(pu, pv, strict=True)), fill=1)
    return np.asarray(img, bool)


def _project_legacy(
    geometry: trimesh.Scene | trimesh.Trimesh, photo: Path, face: str, rotate_cw: int = 0
) -> trimesh.Scene:
    """ASSET_PHOTO_CUT off (the pre-texture-lane projection): stretch the cropped photo over the
    geometry's bbox on `face` and put it on every triangle
    facing that way that can see the camera (a ray from its centre or a corner escapes the part);
    every other triangle keeps its material. A profile photo goes on both sides, mirrored or not,
    whichever silhouette matches the photo better. One JPEG on one extra `photo` primitive.
    ponytail: hard FACE_DOT cut, no normal-weighted fade to the template colour."""
    scene = geometry if isinstance(geometry, trimesh.Scene) else trimesh.Scene(geometry)
    crop, crop_mask, _ = cut_product(photo)
    crop = crop.rotate(-rotate_cw, expand=True)
    n, r, up = (np.array(v, float) for v in FACES[face])
    meshes = _world_meshes(scene)
    allv = np.vstack([m.vertices for _, m in meshes])
    lo = np.array([(allv @ r).min(), (allv @ up).min()])
    ext = np.maximum(np.array([np.ptp(allv @ r), np.ptp(allv @ up)]), 1e-9)
    side = face in SIDES
    flip = False
    if side:  # which way round does the profile run? compare silhouettes both ways
        pm = Image.fromarray(crop_mask).rotate(-rotate_cw, expand=True).resize((SIL, SIL))
        pm = np.asarray(pm, bool)
        geo = _silhouette(meshes, r, up, lo, ext)
        scores = [float((pm & g).sum() / max((pm | g).sum(), 1)) for g in (geo, geo[:, ::-1])]
        flip = scores[1] > scores[0]

    tex = crop.copy()
    tex.thumbnail((TEX_MAX, TEX_MAX))
    buf = io.BytesIO()
    tex.save(buf, "JPEG", quality=JPEG_Q)
    tex = Image.open(io.BytesIO(buf.getvalue()))  # format JPEG: kept as JPEG on export

    out, photo_parts = trimesh.Scene(), []
    whole = trimesh.util.concatenate([m for _, m in meshes])
    eps = 1e-4 * float(np.linalg.norm(whole.extents))
    for name, m in meshes:
        dots = m.face_normals @ n
        hit = np.abs(dots) >= FACE_DOT if side else dots >= FACE_DOT
        idx = np.nonzero(hit)[0]
        if len(idx):
            d = np.sign(dots[idx])[:, None] * n
            c = m.triangles_center[idx]
            pts = [c] + [c + 0.9 * (m.triangles[idx][:, k] - c) for k in range(3)]
            blocked = np.logical_and.reduce([whole.ray.intersects_any(p + d * eps, d) for p in pts])
            hit[idx[blocked]] = False
        if (~hit).any():
            rest = m.submesh([np.nonzero(~hit)[0]], append=True)
            material = getattr(m.visual, "material", None) or T.material("#B0B0B0", "body")
            rest.visual = trimesh.visual.TextureVisuals(material=material)
            out.add_geometry(rest, node_name=name, geom_name=name)
        if hit.any():
            photo_parts.append(m.submesh([np.nonzero(hit)[0]], append=True))
    if photo_parts:
        pm_ = trimesh.util.concatenate(photo_parts)
        pm_.unmerge_vertices()
        u = (pm_.vertices @ r - lo[0]) / ext[0]
        uv = np.c_[1 - u if flip else u, (pm_.vertices @ up - lo[1]) / ext[1]]
        pm_.visual = trimesh.visual.TextureVisuals(
            uv=uv,
            material=trimesh.visual.material.PBRMaterial(
                baseColorTexture=tex, metallicFactor=0.0, roughnessFactor=0.8
            ),
        )
        pm_.merge_vertices()
        out.add_geometry(pm_, node_name="photo", geom_name="photo")
    return out


def decal_box(dims: Dims, photo: Path, face: str, rotate_cw: int = 0, finish: str | None = None):
    """Exact-size box with the photo on `face` (E3): its sides in the product's sampled body
    colour and material cue (ASSET_SIDE_COLOR; the photo's edge colour without it)."""
    settings = get_settings()
    if settings.ASSET_SIDE_COLOR:
        c = photo_cut.cut(photo, face_aspect(dims, face, rotate_cw), size=(dims.w, dims.h, dims.d))
        box, _ = T.build("box", dims.model_dump(), None, {"body": c.colors["body"]})
        box = recolor(box, c.colors, finish or "painted", replace_all=True)
    else:
        _, _, edge = cut_product(photo)
        box, _ = T.build("box", dims.model_dump(), None, {"body": edge})
    return project(box, photo, face, rotate_cw, finish=finish)


# ---------------------------------------------------------------- the photo texture (texture lane)
#
# ASSET_PHOTO_CUT (default on): the photo is the product cut out of it (server/photo_cut.py: the
# studio background removed, a showroom's neighbours dropped, a slight keystone straightened when
# the model's front is a rectangle), fitted to the model's *front footprint* -- the extent of the
# triangles that face the photo's side (normal within FACE_DOT_CUT) and can see the camera -- not
# the whole bounding box, and put on those triangles only. Every other face keeps its own paint.
# ASSET_SIDE_COLOR (default on): `recolor` paints what isn't photo from the product: the sampled
# body colour on pieces with no meaningful paint of their own (a Hunyuan mesh, a box, a CAD piece
# Grok left the default grey, a template shell role still in the template's default colour), the
# trim colour on small unpainted CAD pieces, and a material cue (FINISH_PBR: slightly metallic and
# smooth for stainless, matte for plastic) on those and on CAD pieces painted about the body colour.

FACE_DOT_CUT = 0.5  # a triangle within 60 degrees of the photo's axis gets the photo
RECT_FILL = 0.9  # the front footprint's silhouette fills its box this much: a rectangular front
PLACEHOLDER_GREYS = {"#B0B0B0", "#CCCCCC"}  # build_scad's unpainted piece; assets' proxy grey
SHELL_ROLES = {"body", "door", "frame", "panel", "housing", "shell", "sash", "case"}
TRIM_MAX_AREA = 0.25  # an unpainted CAD piece smaller than this share of the area takes the trim
CUE_NEAR = (
    60.0  # sRGB distance: a painted piece this close to the body colour is the body's material
)


def face_aspect(dims: Dims, face: str, rotate_cw: int = 0) -> float:
    """Width / height of the product face the photo shows, as it appears in the photo."""
    w, h, d = dims.w, dims.h, dims.d
    a = {"front": w / h, "back": w / h, "left": d / h, "right": d / h}.get(face, w / d)
    return 1.0 / a if rotate_cw in (90, 270) else a


def _hex_of(material) -> str | None:
    bc = getattr(material, "baseColorFactor", None)
    if bc is None:
        return None
    bc = np.asarray(bc, float)[:3]
    return "#{:02X}{:02X}{:02X}".format(
        *np.round(_lin_to_srgb255(bc / 255.0 if bc.max() > 1.0 else bc)).astype(int)
    )


def _pbr(color_hex: str, finish: str, texture=None):
    metallic, rough = photo_cut.FINISH_PBR.get(finish, photo_cut.FINISH_PBR["painted"])
    if texture is not None:
        metallic, rough = photo_cut.PHOTO_PBR.get(finish, photo_cut.PHOTO_PBR["painted"])
        return trimesh.visual.material.PBRMaterial(
            baseColorTexture=texture, metallicFactor=metallic, roughnessFactor=rough
        )
    return trimesh.visual.material.PBRMaterial(
        baseColorFactor=T.srgb_to_linear(color_hex), metallicFactor=metallic, roughnessFactor=rough
    )


def recolor(
    geometry,
    colors: dict,
    finish: str,
    *,
    replace_all: bool = False,
    replace: set[str] | None = None,
    cue_near: bool = False,
) -> trimesh.Scene:
    """The model's pieces painted from the product (`colors` {body, trim} from photo_cut):
    `replace_all` (a Hunyuan mesh, a box) every piece gets the body colour; else the pieces named
    in `replace` (a template's shell roles in their default colour) and any piece in a placeholder
    grey (PLACEHOLDER_GREYS: the body colour when it's a big piece, the trim colour when it's
    small). `cue_near` (CAD): pieces painted about the body colour keep their paint and take the
    finish's material cue. Returns a Scene; `metadata["recolored"]` names what changed."""
    scene = geometry if isinstance(geometry, trimesh.Scene) else trimesh.Scene(geometry)
    total = sum(g.area for g in scene.geometry.values()) or 1.0
    body = photo_cut.hex_rgb(colors["body"])
    changed = []
    for name, g in scene.geometry.items():
        mat = getattr(g.visual, "material", None)
        if getattr(mat, "baseColorTexture", None) is not None:
            continue  # already a photo
        paint = _hex_of(mat) if mat is not None else None
        if replace_all or name in (replace or ()) or paint in PLACEHOLDER_GREYS or paint is None:
            small = (
                not replace_all and g.area / total < TRIM_MAX_AREA and paint in PLACEHOLDER_GREYS
            )
            new = colors["trim"] if small else colors["body"]
            g.visual = trimesh.visual.TextureVisuals(material=_pbr(new, finish))
            changed.append(name)
        elif cue_near and np.linalg.norm(photo_cut.hex_rgb(paint) - body) < CUE_NEAR:
            g.visual = trimesh.visual.TextureVisuals(material=_pbr(paint, finish))
            changed.append(name)
    scene.metadata["recolored"] = changed
    return scene


def project(
    geometry: trimesh.Scene | trimesh.Trimesh,
    photo: Path,
    face: str,
    rotate_cw: int = 0,
    *,
    finish: str | None = None,
) -> trimesh.Scene:
    """The product photo on the triangles facing `face` (E5). ASSET_PHOTO_CUT: the cut-out
    (photo_cut) fitted to the front footprint of the triangles that face that way (normal within
    FACE_DOT_CUT) and can see the camera (a ray from the centre or a corner escapes the part),
    straightened when the photo has a keystone and that footprint is a rectangle; every other
    triangle keeps its material. A profile photo goes on both sides, mirrored or not, whichever
    silhouette matches the photo better. One JPEG on one extra `photo` primitive; `finish` sets its
    subtle material cue. `metadata["texture"]` says how (cut method, keystone, triangles).
    ASSET_PHOTO_CUT off: `_project_legacy`."""
    if not get_settings().ASSET_PHOTO_CUT:
        return _project_legacy(geometry, photo, face, rotate_cw)
    scene = geometry if isinstance(geometry, trimesh.Scene) else trimesh.Scene(geometry)
    n, r, up = (np.array(v, float) for v in FACES[face])
    meshes = _world_meshes(scene)
    allv = np.vstack([m.vertices for _, m in meshes])
    aspect = float(np.ptp(allv @ r) / max(np.ptp(allv @ up), 1e-9))
    if rotate_cw in (90, 270):
        aspect = 1.0 / aspect
    c = photo_cut.cut(photo, aspect, size=tuple(np.ptp(allv, axis=0)))
    side = face in SIDES
    whole = trimesh.util.concatenate([m for _, m in meshes])
    eps = 1e-4 * float(np.linalg.norm(whole.extents))
    hits = []
    for _, m in meshes:
        dots = m.face_normals @ n
        hit = np.abs(dots) >= FACE_DOT_CUT if side else dots >= FACE_DOT_CUT
        idx = np.nonzero(hit)[0]
        if len(idx):
            d = np.sign(dots[idx])[:, None] * n
            ctr = m.triangles_center[idx]
            pts = [ctr] + [ctr + 0.9 * (m.triangles[idx][:, k] - ctr) for k in range(3)]
            blocked = np.logical_and.reduce([whole.ray.intersects_any(p + d * eps, d) for p in pts])
            hit[idx[blocked]] = False
        hits.append(hit)
    info = {**c.info(), "photo": False, "keystone": False, "front_tris": 0}
    hit_tris = [m.triangles[h] for (_, m), h in zip(meshes, hits, strict=True) if h.any()]
    if not hit_tris:
        scene.metadata["texture"] = info
        return scene
    hv = np.concatenate(hit_tris).reshape(-1, 3)
    lo = np.array([(hv @ r).min(), (hv @ up).min()])
    ext = np.maximum(np.array([np.ptp(hv @ r), np.ptp(hv @ up)]), 1e-9)
    foot = [("front", trimesh.Trimesh(*_tri_soup(np.concatenate(hit_tris))))]
    rect = _silhouette(foot, r, up, lo, ext).mean() >= RECT_FILL
    keystone = bool(rect and c.quad is not None)
    crop = c.texture(rectify=keystone).rotate(-rotate_cw, expand=True)
    flip = False
    if side:  # which way round does the profile run? compare silhouettes both ways
        pm = Image.fromarray(c.mask).rotate(-rotate_cw, expand=True).resize((SIL, SIL))
        pm = np.asarray(pm, bool)
        geo = _silhouette(foot, r, up, lo, ext)
        scores = [float((pm & g).sum() / max((pm | g).sum(), 1)) for g in (geo, geo[:, ::-1])]
        flip = scores[1] > scores[0]
    tex = crop.copy()
    tex.thumbnail((TEX_MAX, TEX_MAX))
    buf = io.BytesIO()
    tex.save(buf, "JPEG", quality=JPEG_Q)
    tex = Image.open(io.BytesIO(buf.getvalue()))  # format JPEG: kept as JPEG on export

    out, photo_parts = trimesh.Scene(), []
    for (name, m), hit in zip(meshes, hits, strict=True):
        if (~hit).any():
            rest = m.submesh([np.nonzero(~hit)[0]], append=True)
            material = getattr(m.visual, "material", None) or T.material("#B0B0B0", "body")
            rest.visual = trimesh.visual.TextureVisuals(material=material)
            out.add_geometry(rest, node_name=name, geom_name=name)
        if hit.any():
            photo_parts.append(m.submesh([np.nonzero(hit)[0]], append=True))
    pm_ = trimesh.util.concatenate(photo_parts)
    pm_.unmerge_vertices()
    u = np.clip((pm_.vertices @ r - lo[0]) / ext[0], 0.0, 1.0)
    v = np.clip((pm_.vertices @ up - lo[1]) / ext[1], 0.0, 1.0)
    pm_.visual = trimesh.visual.TextureVisuals(
        uv=np.c_[1 - u if flip else u, v], material=_pbr("#FFFFFF", finish or "painted", tex)
    )
    pm_.merge_vertices()
    out.add_geometry(pm_, node_name="photo", geom_name="photo")
    info.update(photo=True, keystone=keystone, front_tris=len(pm_.faces))
    out.metadata["texture"] = info
    return out


def _tri_soup(tris: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    return tris.reshape(-1, 3), np.arange(len(tris) * 3).reshape(-1, 3)


# ---------------------------------------------------------------- orientation (texture lane)
#
# The contract (api.md §2, SCAD_PROMPT): Y up, the back / mounting face at Z=0, the front the
# viewer sees at +Z. `orientation()` checks a model against it from what each side shows: the
# pieces a viewer sees from the front vs from behind (a front carries the handles, the control
# strip and the kick plate as their own painted pieces; a back is one plain slab), how much relief
# each side has, and -- with the product photo -- which side's colours look like the photo (the
# back view mirrored, as a viewer behind the part sees it). `turn_around()` fixes a model built
# backwards: 180 degrees about Y, the mount face still at Z=0.

ORIENT_RES = 64
ORIENT_MIN_SHARE = 0.01  # a piece covering this share of a side's view counts as seen there
ORIENT_PHOTO_MARGIN = 0.15  # the back matches the photo this much better than the front
ORIENT_PHOTO_MIN = 0.3  # ... and matches it at least this well (NCC of the paint vs the photo)
ORIENT_DETAIL_MARGIN = 0.35  # the back shows this much more detail than the front


def _lin_to_srgb255(c: np.ndarray) -> np.ndarray:
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055) * 255.0


def _piece_srgb(m: trimesh.Trimesh) -> np.ndarray:
    """A piece's paint (sRGB 0..255) from its material's linear baseColorFactor; mid grey for a
    textured or unpainted piece."""
    mat = getattr(m.visual, "material", None)
    bc = getattr(mat, "baseColorFactor", None) if mat is not None else None
    if bc is None or getattr(mat, "baseColorTexture", None) is not None:
        return np.array([128.0, 128.0, 128.0])
    bc = np.asarray(bc, float)[:3]
    return _lin_to_srgb255(bc / 255.0 if bc.max() > 1.0 else bc)


def side_view(meshes, side: str = "front", res: int = ORIENT_RES):
    """Orthographic view of `meshes` [(name, mesh)] from +Z ("front") or -Z ("back", mirrored so
    +X is on the viewer's left, as seen from behind), stretched over the XY bbox: (piece index
    per pixel, -1 = none; depth 0..1 from that side's bbox face). Painter's order, PIL polygons."""
    tris = np.concatenate([m.triangles for _, m in meshes])
    idx = np.concatenate([np.full(len(m.faces), i) for i, (_, m) in enumerate(meshes)])
    lo, hi = tris.reshape(-1, 3).min(axis=0), tris.reshape(-1, 3).max(axis=0)
    ext = np.maximum(hi - lo, 1e-9)
    u = (tris[:, :, 0] - lo[0]) / ext[0]
    if side == "back":
        u = 1.0 - u
    u = u * (res - 1)
    v = (1.0 - (tris[:, :, 1] - lo[1]) / ext[1]) * (res - 1)
    z = tris[:, :, 2].mean(axis=1)
    depth = (hi[2] - z) / ext[2] if side == "front" else (z - lo[2]) / ext[2]
    ids = Image.new("I", (res, res), -1)
    dep = Image.new("F", (res, res), 0.0)
    di, dd = ImageDraw.Draw(ids), ImageDraw.Draw(dep)
    for t in np.argsort(-depth):  # far first, the nearest drawn last
        poly = list(zip(u[t].tolist(), v[t].tolist(), strict=True))
        di.polygon(poly, fill=int(idx[t]))
        dd.polygon(poly, fill=float(depth[t]))
    return np.asarray(ids, np.int32), np.asarray(dep, np.float32)


def _ncc(a: np.ndarray, b: np.ndarray) -> float:
    a, b = a - a.mean(), b - b.mean()
    den = float(np.sqrt((a * a).sum() * (b * b).sum()))
    return float((a * b).sum() / den) if den > 1e-6 else 0.0


def orientation(scene, photo: Image.Image | None = None, res: int = ORIENT_RES) -> dict:
    """Which way the model faces: per side (front = +Z, back = -Z) the pieces seen, the share of
    the view that isn't the main body (`detail`), the relief (depth spread) and, with `photo` (the
    product cut-out, front view), the luminance correlation of the side's paint with it. `reversed`
    when the back looks like the front should: it matches the photo clearly better and shows no
    less detail, or it shows clearly more detail and matches the photo no worse."""
    scene = scene if isinstance(scene, trimesh.Scene) else trimesh.Scene(scene)
    meshes = [(n, m) for n, m in _world_meshes(scene) if len(m.faces)]
    if not meshes:
        return {"reversed": False, "reason": "empty"}
    body = int(np.argmax([m.area for _, m in meshes]))
    paint = np.array([_piece_srgb(m) for _, m in meshes])
    lum_paint = paint @ np.array([0.299, 0.587, 0.114])
    target = None
    if photo is not None:
        target = np.asarray(photo.convert("L").resize((res, res), Image.BILINEAR), np.float32)
    out: dict = {}
    for side in ("front", "back"):
        ids, depth = side_view(meshes, side, res)
        cov = ids >= 0
        n = max(int(cov.sum()), 1)
        share = np.bincount(ids[cov], minlength=len(meshes)) / n
        rec = {
            "pieces": int((share >= ORIENT_MIN_SHARE).sum()),
            "detail": round(float(1.0 - share[body]), 3),
            "relief": round(float(depth[cov].std()) if cov.any() else 0.0, 3),
        }
        if target is not None and cov.any():
            lum = np.where(cov, lum_paint[np.clip(ids, 0, None)], 255.0)
            rec["photo"] = round(_ncc(lum[cov], target[cov]), 3)
        out[side] = rec
    f, b = out["front"], out["back"]
    detail = (b["detail"] - f["detail"]) + 0.1 * (b["pieces"] - f["pieces"])
    detail += 0.5 * (b["relief"] - f["relief"])
    photo_m = (b["photo"] - f["photo"]) if "photo" in f and "photo" in b else None
    by_photo = (
        photo_m is not None
        and photo_m > ORIENT_PHOTO_MARGIN
        and b["photo"] >= ORIENT_PHOTO_MIN  # a real match, not two poor ones
        and detail > -0.05
    )
    by_detail = detail > ORIENT_DETAIL_MARGIN and (photo_m is None or photo_m > -0.05)
    out["detail_margin"] = round(float(detail), 3)
    out["photo_margin"] = None if photo_m is None else round(float(photo_m), 3)
    out["reversed"] = bool(by_photo or by_detail)
    out["reason"] = (
        "the back matches the photo" if by_photo else "the back has the detail" if by_detail else ""
    )
    return out


def turn_around(scene: trimesh.Scene) -> trimesh.Scene:
    """The model turned 180 degrees about Y, its mount face still at Z=0 and X/Y still centred:
    (x, y, z) -> (-x, y, zmin + zmax - z)."""
    lo, hi = scene.bounds
    tf = np.diag([-1.0, 1.0, -1.0, 1.0])
    tf[0, 3] = lo[0] + hi[0]
    tf[2, 3] = lo[2] + hi[2]
    out = scene.copy()
    out.apply_transform(tf)
    return out


RELIEF_MIN = 0.02  # under this share of the front view off its main plane: a flat slab
RELIEF_NOTE = (
    "The front (Z=D) is flat: the handles, controls and door features are painted flush on one "
    "slab, so the model reads as its back. Stop the body short of the front (box(z1=D-t), t about "
    "3 % of D) and build the handles, control panel, knobs and door or drawer fronts as their own "
    "painted pieces in the last t mm, standing proud. Keep everything else."
)
# Products whose front carries handles / controls / doors (a flat front is a modelling miss).
_RELIEF_NAME = re.compile(
    r"\b(?:dishwasher|range|oven|stove|cooktop|refrigerator|fridge|freezer|microwave|hood|washer"
    r"|dryer|air conditioner|dehumidifier|wine cooler|beverage cooler|ice maker)\b",
    re.IGNORECASE,
)


def wants_relief(part: Part) -> bool:
    """A product whose front should stand proud (handles, controls, doors), by its name."""
    return bool(_RELIEF_NAME.search(part.name or ""))


def front_relief(scene) -> float:
    """The share of the front view that stands off the front's main plane (by more than 1 % of
    the depth): handles, a control panel, knobs, a recessed door. 0 = one flat slab."""
    scene = scene if isinstance(scene, trimesh.Scene) else trimesh.Scene(scene)
    meshes = [(n, m) for n, m in _world_meshes(scene) if len(m.faces)]
    if not meshes:
        return 0.0
    ids, depth = side_view(meshes, "front")
    d = depth[ids >= 0]
    return float((np.abs(d - np.median(d)) > 0.01).mean()) if d.size else 0.0


# ---------------------------------------------------------------- Grok OpenSCAD (E4), offline

SCAD_LIB = Path(__file__).with_name("scad_lib.scad")
# Where E2/E4 put the OpenSCAD nightly (docs/research/assets-llm/r3-toolchain.md): no distro
# package has the manifold backend yet.
_APPIMAGE = (
    Path(__file__).resolve().parents[1] / "work" / "assets-llm" / "tools" / "openscad.AppImage"
)
SCAD_RESCALE_MAX_PCT = 5.0  # clipped bbox short of the envelope by more: not this part
FULL_CODE = "Reply with the full corrected code in one ```openscad block."
SCAD_KW = {"max_tokens": 16000, "temperature": 0.2, "timeout": 600, "max_retries": 0}

SCAD_PROMPT = """You are a CAD modeller. Write OpenSCAD code that builds a recognisable 3D model \
of the product, using ONLY the helper library below. Model the overall shape and the main \
visible features (body, clips, hooks, flanges, holes, ribs); skip text and logos.

Product: {name}
The product photo is attached.
Material: {material}. Colour: {color}. Mounted on: {surface}.
Envelope (mm), fixed: W={w} along X, H={h} along Y, D={d} along Z. The model must fill exactly \
this box.

Frame (mm): X across, -W/2..W/2 (+X = right when facing the front). Y up, 0..H (floor/bottom \
at Y=0). Z depth, 0..D: Z=0 is the back / mounting face (against the wall, floor or \
structure), Z=D is the front the viewer sees. W, H, D are predefined; write sizes as \
expressions of them.

Front and back: everything the photo shows on the product's front (handles, control panel, \
display, knobs, door and drawer seams, grilles, the kick plate) goes on the FRONT, at Z=D; the \
back at Z=0 is plain. Make the front read as the front in 3D: stop the body short of it \
(box(z1=D-t)) and build each front feature as its own painted piece in the last t mm, so \
handles and controls stand proud; a front that is one flat slab looks like the back.

Library:
{api}

Rules: Plain OpenSCAD 2021+ syntax; the library is already included. Use difference() / union() \
/ hull() for booleans. OpenSCAD has no object variables (`b = box();` is a syntax error): call \
modules directly, or wrap a group in `module name() {{ ... }}`. Colours are strings: \
paint("#RRGGBB"). Every solid must be painted with a hex colour matching the product. Build \
everything with the helpers (world coordinates, no rotations). Top-level variables must not use \
W, H or D (define them inside a module instead). Thin bent or sloped strips: build each straight \
segment as hull() of two thin boxes or two cyl("x", ...) at its ends; hull() of cylinders also \
makes rounded bends. At most 70 lines. Reply with one ```openscad code block only."""


def openscad() -> str | None:
    """`openscad` on PATH, else the AppImage E2/E4 used; None when neither is there."""
    return shutil.which("openscad") or (str(_APPIMAGE) if _APPIMAGE.exists() else None)


def _scad_api() -> str:
    lib = SCAD_LIB.read_text()
    return re.search(r"// ---- API.*?\n(.*?)\n\n", lib, re.DOTALL).group(1).replace("// ", "")


def extract_code(text: str) -> str:
    m = re.search(r"```[a-zA-Z]*\n(.*?)```", text, re.DOTALL) or re.search(
        r"```[a-zA-Z]*\n(.*)", text, re.DOTALL
    )
    return (m.group(1) if m else text).strip()


def _scad_run(exe: str, src: Path, out: Path, dims: tuple, only: str):
    w, h, d = dims
    args = [exe, "--backend=manifold", "-o", str(out), "-D", f"W={w}", "-D", f"H={h}"]
    args += ["-D", f"D={d}", "-D", f'ONLY="{only}"', str(src)]
    return subprocess.run(args, capture_output=True, text=True, timeout=180, check=False)


def build_scad(exe: str, code: str, work: Path, dims: tuple) -> tuple[list, str | None]:
    """(pieces [(mesh in mm, hex)], error). One run lists the paint colours, then one run per
    colour (the library filters on ONLY). The library goes last so model line numbers stay true."""
    src = work / "model.scad"
    src.write_text(code + f"\ninclude <{SCAD_LIB}>\n")
    try:
        p = _scad_run(exe, src, work / "none.stl", dims, "__none__")
    except subprocess.TimeoutExpired:
        return [], "Timed out (over 3 minutes). Avoid minkowski and huge loops."
    log = p.stdout + p.stderr
    bad = [
        ln
        for ln in log.splitlines()
        if "ERROR" in ln
        or "unknown" in ln.lower()
        or "undefined" in ln.lower()
        or "Assertion" in ln
    ]
    if bad or (p.returncode and "top level object is empty" not in log):
        # cad: the call chain back into the model (an assertion fires inside the library; the
        # TRACE lines say which model line called it, with the arguments it got)
        trace = [
            ln
            for ln in log.splitlines()
            if ln.startswith("TRACE:") and ("call of" in ln or "model.scad" in ln)
        ]
        err = "\n".join([*bad, *trace[:6]] if bad else log.splitlines()[-8:])[:1500]
        err = re.sub(r"in file ([^,]*), ", _scad_file_words, err)
        lines = code.splitlines()
        for n in dict.fromkeys(int(m) for m in re.findall(r"in model\.scad, line (\d+)", err)):
            if 0 < n <= len(lines):  # show the offending source line, not just its number
                err += f"\nline {n}: {lines[n - 1].strip()}"
        return [], err
    pieces = []
    if (work / "none.stl").exists() and p.returncode == 0:
        pieces.append((trimesh.load(work / "none.stl", force="mesh"), "#B0B0B0"))
    for i, c in enumerate(dict.fromkeys(re.findall(r"E2COLOR=(#[0-9A-Fa-f]{6})", log))):
        stl = work / f"c{i}.stl"
        if _scad_run(exe, src, stl, dims, c).returncode == 0 and stl.exists():
            pieces.append((trimesh.load(stl, force="mesh"), c))
    if not pieces:
        return [], 'The model is empty: nothing was painted. Wrap every solid in paint("#hex").'
    return pieces, None


def _scad_file_words(m: re.Match) -> str:
    """cad: "in file <lib path>, " -> "in the helper library, " (its line numbers aren't the
    model's), anything else -> "in model.scad, "."""
    return "in the helper library, " if m.group(1).endswith(SCAD_LIB.name) else "in model.scad, "


def _bool(op: str, meshes: list) -> trimesh.Trimesh | None:
    try:
        out = getattr(trimesh.boolean, op)(meshes, engine="manifold")
        return out if len(out.faces) else None
    except Exception:  # noqa: BLE001 -- non-manifold input: keep the unmodified mesh
        return meshes[0] if op != "union" else trimesh.util.concatenate(meshes)


def _bbox_err(lo, hi, t_lo, t_hi) -> float:
    """Worst face offset from the envelope, % of that dimension."""
    return float(np.max(np.maximum(abs(lo - t_lo), abs(hi - t_hi)) / (t_hi - t_lo)) * 100)


def assemble(pieces: list, dims: tuple) -> tuple[trimesh.Scene, float]:
    """(contract-frame Scene, raw bbox error %). Same-colour pieces merge, later paints win and
    everything is clipped to the envelope; a clipped bbox within SCAD_RESCALE_MAX_PCT is then
    stretched onto it so the GLB is exactly W x H x D (ValueError past that)."""
    w, h, d = dims
    allv = np.vstack([m.vertices for m, _ in pieces])
    t_lo, t_hi = np.array([-w / 2, 0, 0]), np.array([w / 2, h, d])
    raw_err = _bbox_err(allv.min(0), allv.max(0), t_lo, t_hi)
    env = trimesh.creation.box(bounds=[t_lo, t_hi])
    by_color: dict[str, list] = {}
    for m, c in pieces:
        by_color.setdefault(c.upper(), []).append(m)
    merged = [(_bool("union", ms) if len(ms) > 1 else ms[0], c) for c, ms in by_color.items()]
    final = []
    for i, (m, c) in enumerate(merged):
        m = _bool("intersection", [m, env])
        later = [mm for mm, _ in merged[i + 1 :]]
        if m is not None and later:
            m = _bool("difference", [m, *later])
        if m is not None and len(m.faces):
            final.append((m, c))
    if not final:
        raise ValueError("nothing inside the envelope")
    allv = np.vstack([m.vertices for m, _ in final])
    lo, hi = allv.min(0), allv.max(0)
    if _bbox_err(lo, hi, t_lo, t_hi) > SCAD_RESCALE_MAX_PCT:
        raise ValueError(f"the model fills only part of the envelope (bbox err {raw_err:.0f}%)")
    scene = trimesh.Scene()
    for i, (m, c) in enumerate(final):
        m = m.copy()
        m.vertices = t_lo + (m.vertices - lo) / np.maximum(hi - lo, 1e-9) * (t_hi - t_lo)
        m.vertices = (m.vertices - [0, h / 2, 0]) / 1000.0  # product frame (mm) -> api.md §2
        m.visual = trimesh.visual.TextureVisuals(material=T.material(c, "body"))
        scene.add_geometry(m, node_name=f"c{i}")
    return scene, raw_err


async def _scad_attempt(exe: str, text: str, dims: tuple):
    """(scene, raw bbox err, code, error) for one model answer."""
    code = extract_code(text)
    with tempfile.TemporaryDirectory() as tmp:
        pieces, err = await asyncio.to_thread(build_scad, exe, code, Path(tmp), dims)
        if err:
            return None, None, code, err
        try:
            scene, raw_err = await asyncio.to_thread(assemble, pieces, dims)
        except ValueError as exc:
            return None, None, code, f"Mesh assembly failed: {exc}"
    return scene, raw_err, code, None


# cad: the CAD model's sidecars next to scad.glb, so the server, `warm --scad` and the headset all
# see the same state across processes: scad.json (who wrote it, LLM calls, seconds, cost, bbox
# error), scad.failed.json (the last failure: why, when, which model) and scad.writing.json (a run
# in progress: {started_at, model, pid, eta_s}; removed when it ends, ignored once stale).
SCAD_SAMPLE_TEMPERATURE = 0.7  # the 2nd.. parallel first answers: different takes
SCAD_FIT_PCT = 2.0  # a raw bbox this far off the envelope gets one fit pass
# Typical (seconds, $) of one part's run per profile -- the model, "@effort" for models that take
# a reasoning effort -- measured on the dishwasher, a window frame and a cabinet knob (docs/api.md
# "CAD models"): the "Grok is writing the CAD model" ETA and warm --scad's estimate. Finished runs
# in this process refine the seconds.
SCAD_TYPICAL = {
    "grok-4.7": (280.0, 0.14),  # 216-400 s, $0.10-0.20
    "grok-4.7@high": (280.0, 0.14),
    "grok-4.7@medium": (240.0, 0.11),  # 178-332 s, $0.08-0.17
    "grok-4.7@low": (55.0, 0.032),  # 15-157 s, median 45, $0.03 (58 runs incl. a warm): the default
    "grok-4.20-0309-reasoning": (140.0, 0.036),  # 118-180 s
    "grok-4.20-0309-non-reasoning": (15.0, 0.008),  # 9-19 s, $0.005-0.015
    "grok-4.3@low": (20.0, 0.007),  # 14-27 s
}
SCAD_DEFAULT = (180.0, 0.05)
SCAD_WRITING_STALE_S = 1800.0  # a lock older than this (or of a dead pid) is ignored
_scad_seen_s: dict[str, list[float]] = {}


def scad_sidecar(out: Path, kind: str) -> Path:
    """`out`'s sidecar: kind "json" (the record), "failed" or "writing" (scad.<kind>.json)."""
    return out.with_name(f"{out.stem}.json" if kind == "json" else f"{out.stem}.{kind}.json")


def _read(path: Path) -> dict | None:
    try:
        value = json.loads(path.read_text())
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def scad_model() -> str:
    """The `asset_scad` role's "provider:model" (LLM_ASSET_SCAD, else the default)."""
    try:
        provider, model = llm.resolve_role("asset_scad")
    except ValueError:
        return "?"
    return f"{provider}:{model}"


def _fast_model(model: str) -> bool:
    name = model.split(":", 1)[-1]
    return "non-reasoning" in name or name.startswith(("grok-4-fast", "grok-code"))


def scad_budget(model: str | None = None) -> tuple[int, int]:
    """(parallel first answers, compile-fix rounds) for `model`. One answer; a non-reasoning model
    (~5 s and ~$0.003 a call, but it misses more often) gets 2 fix rounds, a reasoning one 1.
    ASSET_SCAD_SAMPLES > 1 asks that many first answers at once and keeps the first that builds
    and fills the envelope (measured: no better than one answer plus fixes on the demo parts)."""
    settings = get_settings()
    fast = _fast_model(model or scad_model())
    samples = settings.ASSET_SCAD_SAMPLES or 1
    fixes = (
        settings.ASSET_SCAD_FIXES if settings.ASSET_SCAD_FIXES is not None else (2 if fast else 1)
    )
    return max(1, samples), max(0, fixes)


def scad_effort(model: str | None = None) -> str | None:
    """The reasoning effort the OpenSCAD writer runs at (ASSET_SCAD_EFFORT), None for a model that
    takes none (grok-4.20) or when unset (the model's default)."""
    provider, _, name = (model or scad_model()).partition(":")
    effort = (get_settings().ASSET_SCAD_EFFORT or "").strip().lower() or None
    return effort if effort and llm.takes_reasoning_effort(provider, name) else None


def scad_profile(model: str | None = None) -> str:
    """ "grok-4.7@low", "grok-4.20-0309-non-reasoning": the model and its effort (SCAD_TYPICAL)."""
    name = (model or scad_model()).split(":", 1)[-1]
    effort = scad_effort(model)
    return f"{name}@{effort}" if effort else name


def _typical(profile: str) -> tuple[float, float]:
    return SCAD_TYPICAL.get(profile) or SCAD_TYPICAL.get(profile.split("@")[0]) or SCAD_DEFAULT


def scad_eta_s(profile: str | None = None) -> float:
    """Typical seconds for one CAD run of `profile` (default: the configured one): this
    process's recent runs, else SCAD_TYPICAL."""
    profile = profile or scad_profile()
    seen = sorted(_scad_seen_s.get(profile, [])[-5:])
    if seen:
        return round(seen[len(seen) // 2], 1)
    return _typical(profile)[0]


def scad_cost_usd(profile: str | None = None) -> float:
    """Typical dollars for one part's CAD run of `profile` (warm --scad's estimate)."""
    return _typical(profile or scad_profile())[1]


def _pid_alive(pid: object) -> bool:
    try:
        os.kill(int(pid), 0)
    except (OSError, TypeError, ValueError):
        return False
    return True


def scad_writing(out: Path) -> dict | None:
    """The run writing `out` right now (any process), else None: {started_at, model, pid, eta_s}."""
    lock = _read(scad_sidecar(out, "writing"))
    if lock is None or out.exists():
        return None
    age = time.time() - float(lock.get("started_at") or 0)
    if age > SCAD_WRITING_STALE_S or (lock.get("pid") and not _pid_alive(lock["pid"])):
        return None
    return lock


def scad_record(out: Path) -> dict | None:
    """scad.json: how the CAD model at `out` was made (None for one made before records)."""
    return _read(scad_sidecar(out, "json"))


def scad_failure(out: Path) -> dict | None:
    """scad.failed.json: the last failed run for `out` (None when it built, or never failed)."""
    return None if out.exists() else _read(scad_sidecar(out, "failed"))


async def grok_scad(part: Part, photo: Path, out: Path) -> bool:
    """E4's loop on role `asset_scad` (`grok_scad_run`). True when `out` exists afterwards."""
    return bool((await grok_scad_run(part, photo, out))["ok"])


async def grok_scad_run(part: Part, photo: Path, out: Path) -> dict:
    """E4's loop on role `asset_scad` (LLM_ASSET_SCAD): generate, SCAD_FIXES compile-error fixes,
    one bbox feedback pass when the model misses the envelope by > SCAD_FIT_PCT. Writes the
    contract GLB (geometry only) to `out` and the run's record next to it (scad.json, or
    scad.failed.json), holding scad.writing.json while it runs. Returns the record: {ok, model,
    made_by, seconds, calls, fixes, fit_pass, cost_usd, prompt/completion/reasoning tokens,
    first_bbox_err_pct, bbox_err_pct, faces, error}. Never raises."""
    if out.exists():
        return {**(scad_record(out) or {}), "ok": True, "skipped": "exists"}
    model = scad_model()
    profile = scad_profile(model)
    record: dict = {
        "ok": False,
        "part_id": part.id,
        "model": model,
        "profile": profile,
        "calls": 0,
        "fixes": 0,
    }
    exe = openscad()
    if exe is None:
        logger.info("grok_scad: OpenSCAD not found (PATH or %s), skipping %s", _APPIMAGE, part.id)
        return {**record, "error": "OpenSCAD isn't installed"}
    dims = (part.dims_mm.w, part.dims_mm.h, part.dims_mm.d)
    prompt = SCAD_PROMPT.format(
        name=part.name,
        material=part.material or "?",
        color=part.color_hex or "see photo",
        surface=part.mount.get("surface") or "wall/structure",
        w=dims[0],
        h=dims[1],
        d=dims[2],
        api=_scad_api(),
    )
    calls: list[dict] = []
    samples, fixes = scad_budget(model)
    envelope = (
        f"It must span X [{-dims[0] / 2}, {dims[0] / 2}], Y [0, {dims[1]}], Z [0, {dims[2]}];"
        " anything outside is cut off. Check the frame (Y up, back face at Z=0, front at Z=D)"
        " and fix the sizes and positions."
    )

    def follow_up(code: str, note: str) -> list[dict]:
        return [
            {"role": "user", "content": prompt},
            {"role": "assistant", "content": f"```openscad\n{code}\n```"},
            {"role": "user", "content": note},
        ]

    effort = scad_effort(model)
    base_kw = dict(SCAD_KW, reasoning_effort=effort) if effort else SCAD_KW
    record["effort"] = effort

    async def ask(messages: list[dict], sample: int = 0) -> str:
        kw = dict(base_kw, temperature=SCAD_SAMPLE_TEMPERATURE) if sample else base_kw
        text, meta = await cached_chat_meta("asset_scad", messages, sample=sample, **kw)
        calls.append(meta)
        return text

    def fix_note(err: str) -> str:
        more = f" {envelope}" if err.startswith("Mesh assembly failed") else ""
        return f"It failed with:\n{err}\n\nFix it.{more} {FULL_CODE}"

    t0 = time.monotonic()
    started = time.time()
    lock = scad_sidecar(out, "writing")
    out.parent.mkdir(parents=True, exist_ok=True)
    cache.write_json_atomic(
        lock,
        json.dumps(
            {
                "started_at": started,
                "model": model,
                "profile": profile,
                "pid": os.getpid(),
                "eta_s": scad_eta_s(profile),
            }
        ),
    )
    scene, err = None, None
    record["samples"] = samples
    try:
        url = await asyncio.to_thread(photo_data_url, photo, 768)
        first = _user(prompt, url)
        texts = await asyncio.gather(
            *(ask(first, i) for i in range(samples)), return_exceptions=samples > 1
        )
        tries = []
        for text in texts:
            if isinstance(text, BaseException):
                logger.info("grok_scad: %s: one answer failed: %s", part.id, text)
                continue
            tries.append(await _scad_attempt(exe, text, dims))
        if not tries:
            raise next(t for t in texts if isinstance(t, BaseException))
        built = [t for t in tries if t[3] is None]
        record["compiled_first"] = len(built)
        # the first (temperature 0.2) answer that builds and roughly fills the envelope; the
        # closest fit only when none does (a bare envelope box also "fits" perfectly)
        near = [t for t in built if t[1] <= SCAD_RESCALE_MAX_PCT]
        best = near[0] if near else min(built, key=lambda t: t[1]) if built else tries[0]
        scene, raw_err, code, err = best
        while err and record["fixes"] < fixes:
            record["fixes"] += 1
            # a 2nd.. round is its own answer (a fix that repeats itself would be a cache hit)
            text = await ask(follow_up(code, fix_note(err)), record["fixes"] - 1)
            scene, raw_err, code, err = await _scad_attempt(exe, text, dims)
        if err:
            logger.info("grok_scad: %s never compiled: %s", part.id, err[:200])
        else:
            record["first_bbox_err_pct"] = round(raw_err, 2)
            record["fit_pass"] = raw_err > SCAD_FIT_PCT
            if raw_err > SCAD_FIT_PCT:
                note = (
                    f"The model's bounding box misses the envelope by up to {raw_err:.0f} % of a"
                    f" dimension. {envelope} {FULL_CODE}"
                )
                text = await ask(follow_up(code, note))
                fixed, fixed_err, _, _ = await _scad_attempt(exe, text, dims)
                if fixed is not None and fixed_err < raw_err:
                    scene, raw_err = fixed, fixed_err
            record["bbox_err_pct"] = round(raw_err, 2)
            # texture lane: the front is the front -- proud features, and never built backwards
            if wants_relief(part) and front_relief(scene) < RELIEF_MIN:
                text = await ask(follow_up(code, RELIEF_NOTE + f" {envelope} {FULL_CODE}"))
                proud, proud_err, proud_code, _ = await _scad_attempt(exe, text, dims)
                better = (
                    proud is not None
                    and proud_err <= max(raw_err, SCAD_FIT_PCT)
                    and front_relief(proud) > front_relief(scene)
                )
                record["relief_pass"] = "kept" if better else "rejected"
                if better:
                    scene, raw_err, code = proud, proud_err, proud_code
                    record["bbox_err_pct"] = round(raw_err, 2)
            record["front_relief"] = round(front_relief(scene), 4)
            if get_settings().ASSET_SCAD_ORIENT:
                cut = await asyncio.to_thread(photo_cut.cut, photo, dims[0] / dims[1])
                ori = await asyncio.to_thread(orientation, scene, cut.texture(rectify=True))
                record["orientation"] = {k: ori.get(k) for k in ("front", "back", "reason")}
                if ori["reversed"]:
                    scene = turn_around(scene)
                    record["turned"] = ori["reason"]
                    logger.info(
                        "grok_scad: %s was built backwards (%s): turned", part.id, ori["reason"]
                    )
    # Blind except is deliberate: an offline batch job over many parts; one part's network,
    # quota or build failure is logged and the job moves on.
    except Exception as exc:  # noqa: BLE001
        logger.info("grok_scad: %s failed: %s", part.id, exc)
        err = f"{type(exc).__name__}: {str(exc)[:300]}"
        scene = None
    finally:
        lock.unlink(missing_ok=True)
    record.update(_calls_summary(calls))
    record.update(
        seconds=round(time.monotonic() - t0, 1),
        at=time.time(),
        started_at=started,
        made_by=f"{calls[0]['by'].removesuffix(' (cached)') if calls else model} + openscad",
    )
    if scene is None or err:
        record["error"] = (err or "no model")[:600]
        cache.write_json_atomic(scad_sidecar(out, "failed"), json.dumps(record, indent=2))
        return record
    tmp = out.with_name(f".{out.name}.tmp")
    scene.export(tmp, file_type="glb")
    os.replace(tmp, out)
    record["ok"] = True
    record["faces"] = int(sum(len(g.faces) for g in scene.geometry.values()))
    cache.write_json_atomic(scad_sidecar(out, "json"), json.dumps(record, indent=2))
    scad_sidecar(out, "failed").unlink(missing_ok=True)
    if not any(c.get("cached") for c in calls):
        _scad_seen_s.setdefault(profile, []).append(record["seconds"])
    logger.info(
        "grok_scad: %s -> %s (bbox err %.1f%%, %d calls, %.0f s, $%.3f)",
        part.id,
        out,
        raw_err,
        record["calls"],
        record["seconds"],
        record["cost_usd"] or 0.0,
    )
    return record


def _calls_summary(calls: list[dict]) -> dict:
    def total(key: str):
        values = [c.get(key) for c in calls if c.get(key) is not None]
        return sum(values) if values else None

    cost = total("cost_usd")
    return {
        "calls": len(calls),
        "cached_calls": sum(1 for c in calls if c.get("cached")),
        "llm_seconds": round(total("seconds") or 0.0, 1),
        "cost_usd": round(cost, 5) if cost is not None else None,
        "prompt_tokens": total("prompt_tokens"),
        "completion_tokens": total("completion_tokens"),
        "reasoning_tokens": total("reasoning_tokens"),
    }
