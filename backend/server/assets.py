"""Asset resolver (plan §4b.3, docs/research/assets-3d.md, docs/research/p4-llm-assets-bench.md §5).

Tiers, in order, each falling through on failure rather than raising (`asset.tier` records which
one made the GLB):

1. `cad`: part.asset.source_url is a downloadable .glb/.gltf/.obj.
2. `llm` (server/meshgen.py): one vision call (`LLM_ASSET`, Groq qwen by default) picks a
   parametric template + params + colours + the face the photo shows; the template is built at
   exact size and the photo projected onto it (photo mode) when the photo shows one product.
   No photo or a failed vision call: the text model on the listing, template colours.
3. For parts that call tags moulded/organic, or when it failed: `scad`, a cached Grok OpenSCAD
   GLB (`scad.glb`, made by `server.warm --scad` / `--grok-moulded` or an "llm_scad" request); else `ai_mesh`,
   Hunyuan3D-2.1 image-to-3D (anonymous/HF_TOKEN ZeroGPU quota), kept upright and
   photo-textured; a moulded part with neither keeps its `llm` template.
4. `proxy`: exact-size box with the product photo on the face it shows (E3 decal), or a plain
   coloured box when there's no photo.

`library` (plan's 3rd tier) is dropped: research confirmed no free, no-auth, text-searchable 3D
library exists (assets-3d.md §2) -- it's a hand-curation step, not something this resolver can
do live. `OFFLINE`: only cached LLM answers and cached `scad.glb` are used; no Hunyuan.

Axis convention (glTF native, +Y up): dims_mm.w -> X, dims_mm.h -> Y, dims_mm.d -> Z. Mount face
"-z" (the only face this project uses, per `Part.mount` default) means the mounting face sits in
the X/Y plane at Z=0 and the part extends toward +Z; origin is centred on that face (X/Y centred,
Z=0 at the mount plane). All tiers converge on `part.json` + `model.glb` (metres) + `image.jpg`
under `data/parts/<id>/`, written by `resolve_asset`.
"""

import asyncio
import hashlib
import io
import itertools
import json
import logging
import os
import shutil
import tempfile
import time
from pathlib import Path

import httpx
import numpy as np
import trimesh
from gradio_client import Client, handle_file
from PIL import Image

from server import cache, llm, meshgen, photo_cut, web
from server import part_templates as T
from server.config import get_settings
from server.keys import KeyPool, KeysExhausted
from server.models import Asset, Dims, Part

logger = logging.getLogger(__name__)

MAX_IMAGE_BYTES = 10 * 1024 * 1024
MAX_FACES = 20_000
MAX_AI_RESIDUAL_PCT = 60.0  # uniform-scale mismatch beyond this = the photo isn't this shape
# asset-mode "hf": the user asked for Hunyuan to compare it, so its mesh is kept up to this (a thin
# window frame from a front photo came back 74% off on depth -- a guess, squashed to size; a
# 10-pack box photo for one hanger was 9000%).
MAX_HF_MODE_RESIDUAL_PCT = 200.0
DEFAULT_PROXY_COLOR = "#CCCCCC"  # light grey
_MESH_EXTENSIONS = (".glb", ".gltf", ".obj")

# Tested end to end, anonymous, ~15-20s (docs/research/assets-3d.md §1.3). Hunyuan3D-2 is an
# untested fallback for when 2.1 is down (same /shape_generation shape). Both are ZeroGPU spaces
# sharing one quota per HF token (per IP when anonymous): a quota error rotates to the next
# HF_TOKEN_n, then anonymous; when all are spent `ai_mesh` stops trying (see HF_KEYS).
_HUNYUAN_SPACES = [
    "tencent/Hunyuan3D-2.1",
    "tencent/Hunyuan3D-2",
]
HF_QUOTA_SEEN: list[dict] = []  # asset-mode: the last few ZeroGPU quota messages (the report)


def _hf_limited(exc: Exception) -> bool:
    """A ZeroGPU quota refusal ("You have exceeded your ZeroGPU quota (90s requested vs. 59s
    left)"); asset-mode keeps the message for GET /assets/hf-warm."""
    limited = "ZeroGPU quota" in str(exc)
    if limited:
        HF_QUOTA_SEEN.append({"at": time.time(), "message": str(exc)[:300]})
        del HF_QUOTA_SEEN[:-5]
    return limited


HF_KEYS = KeyPool(
    "HF_TOKEN",
    is_limited=_hf_limited,
    cooldown_s=3600,  # quota resets ~daily; an hourly re-check costs one quick refusal
    anonymous_last=True,
)
_AI_MESH_TIMEOUT_S = 120
_ai_mesh_semaphore = asyncio.Semaphore(1)  # anon ZeroGPU quota: one generation at a time

_FACE_AXIS = {"+x": (0, 1), "-x": (0, -1), "+y": (1, 1), "-y": (1, -1), "+z": (2, 1), "-z": (2, -1)}


def _proper_rotations_24() -> list[np.ndarray]:
    """24 orientation-preserving (det=+1) relabelings of XYZ: permutations x sign flips."""
    rotations = []
    for perm in itertools.permutations(range(3)):
        base = np.zeros((3, 3))
        for row, col in enumerate(perm):
            base[row, col] = 1
        for signs in itertools.product((1, -1), repeat=3):
            r = base * np.array(signs)[:, None]
            if np.isclose(np.linalg.det(r), 1.0):
                rotations.append(r)
    return rotations


_ROTATIONS_24 = _proper_rotations_24()


def part_dir(part_id: str) -> Path:
    return Path(get_settings().DATA_DIR) / "parts" / part_id


def _flatten(mesh_or_scene) -> trimesh.Trimesh:
    if isinstance(mesh_or_scene, trimesh.Trimesh):
        return mesh_or_scene.copy()
    if hasattr(mesh_or_scene, "to_geometry"):
        return mesh_or_scene.to_geometry()
    return mesh_or_scene.dump(concatenate=True)


def _move_origin_to_mount_face(mesh: trimesh.Trimesh, face: str) -> None:
    axis, sign = _FACE_AXIS[face]
    bmin, bmax = mesh.bounds
    center = (bmin + bmax) / 2.0
    center[axis] = bmax[axis] if sign > 0 else bmin[axis]
    mesh.apply_translation(-center)


def normalize_mesh(
    mesh_or_scene,
    dims: Dims,
    mount_face: str = "-z",
    exact: bool = True,
    upright: bool = False,
) -> tuple[trimesh.Trimesh, float]:
    """Flatten, orient, scale to `dims`, origin to the mount face, decimate.

    Orientation: the proper rotation (of the 24 axis relabelings) whose extents best match `dims`
    by aspect ratio. `upright` (Hunyuan meshes) keeps the mesh's +Y up and only picks among the
    4 turns about it, ties going to the pose as generated: Hunyuan3D-2.1 returns objects in a
    canonical pose that already matches api.md §2 -- checked on raw meshes of the wall AC (front
    photo: grille on +Z) and the vinyl gutter hanger (3/4 photo: mount side -Z, profile on X) --
    but guesses unseen depth badly, so the free 24-way search laid the AC grille-up.

    Returns (mesh, scale_residual_pct): the residual is the max abs per-axis deviation from
    `dims` had a single uniform scale factor been used instead of exact per-axis scaling --
    reported regardless of `exact`, since it's the honesty metric for `asset.scale_residual_pct`.
    """
    mesh = _flatten(mesh_or_scene)
    target = np.array([dims.w, dims.h, dims.d]) / 1000.0  # mm -> m; X=w, Y=h, Z=d

    def cv(r: np.ndarray) -> float:
        factors = target / np.maximum(np.ptp(mesh.vertices @ r.T, axis=0), 1e-9)
        return float(factors.std() / factors.mean())

    candidates = [r for r in _ROTATIONS_24 if not upright or r[1, 1] == 1]
    best_r = min(candidates, key=cv)  # identity comes first, so it wins a tie

    rotated = mesh.vertices @ best_r.T
    best_factors = target / np.maximum(np.ptp(rotated, axis=0), 1e-9)
    achieved_uniform = np.maximum(np.ptp(rotated, axis=0), 1e-9) * best_factors.mean()
    residual_pct = float(np.max(np.abs(achieved_uniform - target) / target) * 100.0)

    scale = best_factors if exact else np.full(3, best_factors.mean())
    mesh.vertices = rotated * scale

    _move_origin_to_mount_face(mesh, mount_face)

    for _ in range(3):  # the decimator can undershoot the target on one pass
        if len(mesh.faces) <= MAX_FACES:
            break
        mesh = mesh.simplify_quadric_decimation(face_count=MAX_FACES)

    return mesh, residual_pct


def _srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _hex_to_rgba(hex_str: str) -> list[float]:
    """sRGB hex -> linear RGBA: glTF's baseColorFactor is linear, so raw hex renders washed out."""
    h = hex_str.lstrip("#")
    return [_srgb_to_linear(int(h[i : i + 2], 16) / 255.0) for i in (0, 2, 4)] + [1.0]


def _paint(mesh: trimesh.Trimesh, color_hex: str | None) -> trimesh.Trimesh:
    """PBR base colour (not vertex colour, see assets-3d.md §3.5)."""
    mesh.visual = trimesh.visual.TextureVisuals(
        material=trimesh.visual.material.PBRMaterial(
            baseColorFactor=_hex_to_rgba(color_hex or DEFAULT_PROXY_COLOR),
            metallicFactor=0.1,
            roughnessFactor=0.7,
        )
    )
    return mesh


def proxy_mesh(dims: Dims, color_hex: str | None = None) -> trimesh.Trimesh:
    """Exact-size plain box, mount face -z: the last tier when there's no photo."""
    w_m, h_m, d_m = dims.w / 1000.0, dims.h / 1000.0, dims.d / 1000.0
    box = _paint(trimesh.creation.box(extents=(w_m, h_m, d_m)), color_hex)
    _move_origin_to_mount_face(box, "-z")
    return box


async def fetch_image(url: str, dest: Path) -> bool:
    """Download `url` to `dest` as a JPEG. False (never raises) on any failure, including a
    non-public/SSRF-rejected host (see `web.safe_get`; `url` comes from LLM/SerpApi output)."""
    try:
        resp = await web.safe_get(url, timeout=20)
        if resp is None:
            logger.info("fetch_image: %s rejected (non-public host or too many redirects)", url)
            return False
        resp.raise_for_status()
        content_type = resp.headers.get("content-type", "")
        if not content_type.startswith("image/"):
            logger.info("fetch_image: %s not an image (content-type %r)", url, content_type)
            return False
        if len(resp.content) > MAX_IMAGE_BYTES:
            logger.info("fetch_image: %s too large (%d bytes)", url, len(resp.content))
            return False
        dest.parent.mkdir(parents=True, exist_ok=True)
        await asyncio.to_thread(_save_jpeg, resp.content, dest)
        return True
    except (httpx.HTTPError, OSError) as exc:
        logger.info("fetch_image: %s failed: %s", url, exc)
        return False


def _save_jpeg(content: bytes, dest: Path) -> None:
    with Image.open(io.BytesIO(content)) as img:
        img.convert("RGB").save(dest, "JPEG")


def _resolve_remote_file(client: Client, value) -> bytes:
    """Gradio's /shape_generation file output is a remote-path dict, not an auto-downloaded local
    file (see assets-3d.md §1.3 gotcha) -- fetch it via the `/file=` route.
    asset-mode (live, 2026-09-26): with gradio_client 2.x the answer's path is the local copy the
    client already downloaded, and fetching `{src}/file=<that path>` is a 403 on both Hunyuan Spaces
    (every Hunyuan call "failed" after a good generation). A path that exists here is read; else the
    Space's own file route (the client's `src_prefixed`, headers and cookies), then the old URL."""
    inner = value.get("value", value) if isinstance(value, dict) else value
    path = inner.get("path") if isinstance(inner, dict) else inner
    # asset-mode (live, 2026-09-26): gradio_client 2.x has already downloaded it -- `path` is a
    # local temp file (the /file= fetch of that path is a 403).
    if path and Path(str(path)).is_file():
        return Path(str(path)).read_bytes()
    if isinstance(value, dict):
        url = inner.get("url") if isinstance(inner, dict) else None
        root = getattr(client, "src_prefixed", None) or f"{str(client.src).rstrip('/')}/"
        quoted = utils_encode(path) if path else ""
        urls = [
            u
            for u in (
                url if url and url.startswith("http") else None,
                f"{root}file={quoted}",
                f"{client.src}/file={path}",
            )
            if u
        ]
        headers = getattr(client, "headers", None) or {}
        cookies = getattr(client, "cookies", None)
        last: Exception | None = None
        for u in dict.fromkeys(urls):
            try:
                resp = httpx.get(
                    u, headers=headers, cookies=cookies, timeout=120, follow_redirects=True
                )
                resp.raise_for_status()
                return resp.content
            except httpx.HTTPError as exc:
                last = exc
        raise last or RuntimeError("no file url in the Space's answer")
    return Path(inner).read_bytes()


def utils_encode(path: str) -> str:
    """gradio_client's own quoting for a `/file=` path (older clients: the path as is)."""
    try:
        from gradio_client.utils import encode_file_path
    except ImportError:  # pragma: no cover -- older gradio_client
        return path
    return encode_file_path(path)


def _hunyuan_shape_generation(space_id: str, image_path: Path, token: str | None) -> bytes:
    """Blocking; run via asyncio.to_thread. Exact call shape from assets-3d.md §1.3, tested live
    against tencent/Hunyuan3D-2.1 (see report)."""
    client = Client(space_id, token=token, verbose=False)
    file_out, _html, mesh_stats, _seed = client.predict(
        image=handle_file(str(image_path)),
        mv_image_front=None,
        mv_image_back=None,
        mv_image_left=None,
        mv_image_right=None,
        steps=20,
        guidance_scale=5.0,
        seed=1234,
        octree_resolution=256,
        check_box_rembg=True,
        num_chunks=8000,
        randomize_seed=False,
        api_name="/shape_generation",
    )
    logger.info("ai_mesh %s mesh_stats=%s", space_id, mesh_stats)
    return _resolve_remote_file(client, file_out)


# asset-mode: how the last ai_mesh call went (the warm queue backs off on "quota"; GET
# /assets/hf-warm and the report show `detail`, e.g. the ZeroGPU quota message).
AI_MESH_LAST: dict = {"outcome": None, "space": None, "detail": None, "at": None, "seconds": None}


def _ai_mesh_outcome(
    outcome: str, space: str | None = None, detail: str | None = None, seconds: float | None = None
) -> None:
    AI_MESH_LAST.update(
        outcome=outcome, space=space, detail=detail, at=time.time(), seconds=seconds
    )


async def ai_mesh(image_path: Path) -> Path | None:
    """Shape-only Hunyuan3D-2.1 mesh from a product photo. None (never raises) if every space in
    `_HUNYUAN_SPACES` fails or times out. Caller owns deleting the returned temp file.
    asset-mode: `AI_MESH_LAST` records the outcome ("ok" + the space that answered, "quota",
    "failed") -- callers go through `hunyuan_raw`, which caches the result by photo hash."""
    async with _ai_mesh_semaphore:
        t0 = time.monotonic()
        failures = []
        for space_id in _HUNYUAN_SPACES:

            async def generate(token: str | None, space_id: str = space_id) -> bytes:
                return await asyncio.wait_for(
                    asyncio.to_thread(_hunyuan_shape_generation, space_id, image_path, token),
                    timeout=_AI_MESH_TIMEOUT_S,
                )

            try:
                data = await HF_KEYS.call(generate)
            except KeysExhausted as exc:  # every token's (and the IP's) ZeroGPU quota is spent
                logger.info("ai_mesh: %s", exc)
                _ai_mesh_outcome("quota", space_id, str(exc), time.monotonic() - t0)
                return None
            # Blind except is deliberate: gradio_client surfaces arbitrary remote-space failures
            # (AppError wrapping any server-side traceback, plain RuntimeError, etc, see
            # docs/research/assets-3d.md §1.1/§1.2) -- there's no fixed exception taxonomy to
            # narrow to, and the whole point of the fallback loop is "space misbehaved, try next".
            except Exception as exc:  # noqa: BLE001
                logger.info("ai_mesh: %s failed: %s", space_id, exc)
                failures.append(f"{space_id}: {str(exc)[:200]}")
                continue
            fd, tmp_path = tempfile.mkstemp(suffix=".glb")
            os.close(fd)
            out_path = Path(tmp_path)
            out_path.write_bytes(data)
            _ai_mesh_outcome("ok", space_id, None, time.monotonic() - t0)
            return out_path
        _ai_mesh_outcome("failed", None, "; ".join(failures) or None, time.monotonic() - t0)
    return None


def _looks_like_mesh_url(url: str) -> bool:
    return url.lower().split("?")[0].endswith(_MESH_EXTENSIONS)


async def _try_cad(url: str) -> trimesh.Trimesh | None:
    if not _looks_like_mesh_url(url):
        return None
    resp = await web.safe_get(url, timeout=20)
    if resp is None:
        logger.info("cad: fetch %s rejected (non-public host or too many redirects)", url)
        return None
    try:
        resp.raise_for_status()
    except httpx.HTTPError as exc:
        logger.info("cad: fetch %s failed: %s", url, exc)
        return None
    suffix = Path(url.split("?")[0]).suffix.lstrip(".")
    try:
        return trimesh.load(io.BytesIO(resp.content), file_type=suffix, force="scene")
    # Blind except is deliberate: trimesh's loaders raise whatever the underlying parser (pygltflib,
    # the OBJ parser, ...) raises on malformed third-party input -- not a fixed set of types.
    except Exception as exc:  # noqa: BLE001
        logger.info("cad: parse %s failed: %s", url, exc)
        return None


# --- asset-mode: the generator preference (HF image-to-3D vs LLM + OpenSCAD) ---------------------
#
# The headset picks how parts' 3D models are made (Settings ▸ 3D models): "hf" = Hunyuan3D image-
# to-3D on the HF ZeroGPU Spaces for every part with a photo; "llm_scad" = Grok writes OpenSCAD
# (role `asset_scad`) rendered by the openscad binary; "auto" = the tier order above. Each mode's
# model is kept next to model.glb as a variant -- `model.<stem>.glb` + `asset.<stem>.json` (stems
# auto / hf / scad) -- so both can be compared per part, and model.glb (+ part.json's asset) is
# whichever mode was asked for last, for clients that don't send `?mode=`. HF results are cached
# aggressively: the raw Hunyuan mesh by photo hash under <DATA_DIR>/cache/hunyuan/ (one Space call
# per photo, ever), and a part's `ai_mesh` variant is never rebuilt; server/hf_warm.py makes them
# ahead of need within the ZeroGPU quota.

ASSET_MODES = ("auto", "hf", "llm_scad")
_MODE_ALIASES = {
    "auto": "auto",
    "hf": "hf",
    "hunyuan": "hf",
    "ai_mesh": "hf",
    "llm_scad": "llm_scad",
    "llm+scad": "llm_scad",
    "llm+cad": "llm_scad",
    "scad": "llm_scad",
    "openscad": "llm_scad",
}
_STEM = {"auto": "auto", "hf": "hf", "llm_scad": "scad"}
_FALLBACK_WORDS = {"llm": "the LLM template", "proxy": "the photo box", "scad": "Grok's OpenSCAD"}


def normalize_mode(value: object) -> str:
    """ "hf" | "llm_scad" | "auto" from whatever the client sent (None / unknown = "auto")."""
    key = str(value or "").strip().lower().replace(" ", "").replace("-", "_")
    return _MODE_ALIASES.get(key, "auto")


def model_url(part_id: str, mode: str | None = None) -> str:
    """texture: the part's model URL for a generator preference: `?mode=` for hf / llm_scad, so a
    placement loads that mode's model even if another client selected another one since."""
    m = normalize_mode(mode)
    return f"/parts/{part_id}/model.glb" + (f"?mode={m}" if m != "auto" else "")


def variant_paths(part_id: str, mode: str) -> tuple[Path, Path]:
    """(model.<stem>.glb, asset.<stem>.json) for the mode's model of the part."""
    stem = _STEM[normalize_mode(mode)]
    pdir = part_dir(part_id)
    return pdir / f"model.{stem}.glb", pdir / f"asset.{stem}.json"


def _read_json(path: Path) -> dict | None:
    try:
        value = json.loads(path.read_text())
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def _disk_part(part_id: str) -> Part | None:
    try:
        return Part.model_validate_json((part_dir(part_id) / "part.json").read_text())
    except (OSError, ValueError):
        return None


def _copy_atomic(src: Path, dst: Path) -> None:
    tmp = dst.with_name(f".{dst.name}.tmp")
    shutil.copyfile(src, tmp)
    os.replace(tmp, dst)


def photo_hash(photo: Path) -> str:
    return hashlib.sha1(photo.read_bytes()).hexdigest()


def _hy_dir() -> Path:
    return Path(get_settings().DATA_DIR) / "cache" / "hunyuan"


def hunyuan_cached(photo: Path | None) -> Path | None:
    """The raw Hunyuan mesh already made for this photo, else None."""
    if photo is None or not photo.exists():
        return None
    path = _hy_dir() / f"{photo_hash(photo)}.glb"
    return path if path.exists() else None


_hy_locks: dict[str, asyncio.Lock] = {}
HY_FAIL_RETRY_S = 600.0  # a photo the Space just failed on isn't sent again for 10 min (quota)


async def hunyuan_raw(photo: Path) -> tuple[Path | None, dict]:
    """The raw Hunyuan3D mesh for this photo, made at most once per photo: <DATA_DIR>/cache/
    hunyuan/<sha1(photo)>.glb + .json {space, seconds, at}. (None, {"outcome": "quota" | "failed"
    | "offline", ...}) when the Space can't make it now. Two callers wanting the same photo share
    one call; `ai_mesh`'s semaphore keeps every generation sequential."""
    digest = photo_hash(photo)
    glb, meta_path = _hy_dir() / f"{digest}.glb", _hy_dir() / f"{digest}.json"
    async with _hy_locks.setdefault(digest, asyncio.Lock()):
        if glb.exists():
            return glb, _read_json(meta_path) or {"outcome": "cached"}
        if get_settings().OFFLINE:
            return None, {"outcome": "offline"}
        failed = _hy_dir() / f"{digest}.failed"  # {"at": epoch s} of the last failure
        failed_ago = time.time() - ((_read_json(failed) or {}).get("at") or -1e12)
        if failed_ago < HY_FAIL_RETRY_S:
            return None, {"outcome": "failed", "detail": f"failed {failed_ago:.0f} s ago"}
        t0 = time.monotonic()
        AI_MESH_LAST.update(outcome=None, space=None, detail=None)
        tmp = await ai_mesh(photo)
        if tmp is None:
            outcome = AI_MESH_LAST.get("outcome") or "failed"
            if outcome == "failed":
                cache.write_json_atomic(failed, json.dumps({"at": time.time()}))
            return None, {"outcome": outcome, "detail": AI_MESH_LAST.get("detail")}
        glb.parent.mkdir(parents=True, exist_ok=True)
        staging = glb.with_name(f".{glb.name}.tmp")
        shutil.move(str(tmp), staging)
        os.replace(staging, glb)
        meta = {
            "outcome": "made",
            "space": AI_MESH_LAST.get("space") or _HUNYUAN_SPACES[0],
            "seconds": round(time.monotonic() - t0, 2),
            "at": time.time(),
            "photo_sha1": digest,
        }
        cache.write_json_atomic(meta_path, json.dumps(meta))
        logger.info("hunyuan: %s made in %.1f s by %s", digest[:10], meta["seconds"], meta["space"])
        return glb, meta


def _hf_made_by(meta: dict | None) -> str:
    return f"hf:{(meta or {}).get('space') or _HUNYUAN_SPACES[0]}"


def _scad_made_by(part_id: str | None = None) -> str:
    """Who wrote the part's CAD model (its scad.json), else the `asset_scad` role's model."""
    if part_id is not None:
        rec = meshgen.scad_record(part_dir(part_id) / "scad.glb")
        if rec and rec.get("made_by"):
            return str(rec["made_by"])
    try:
        provider, model = llm.resolve_role("asset_scad")
    except ValueError:
        return "openscad"
    return f"{provider}:{model} + openscad"


async def _hunyuan_detail(part: Part, photo: Path, max_residual: float = MAX_AI_RESIDUAL_PCT):
    """((mesh, residual) or None, meta): the Hunyuan mesh, upright, scaled to size and painted;
    None when the Space can't make it or its shape doesn't match the dims (e.g. a 10-pack box
    photo for one hanger: meta outcome "mismatch" with the residual)."""
    glb, meta = await hunyuan_raw(photo)
    if glb is None:
        return None, meta
    raw = trimesh.load(glb, force="scene")
    mesh, residual = await asyncio.to_thread(
        normalize_mesh, raw, part.dims_mm, part.mount.get("face") or "-z", True, True
    )
    if residual > max_residual:
        logger.info("asset %s: ai_mesh residual %.0f%% too high, dropped", part.id, residual)
        return None, {**meta, "outcome": "mismatch", "residual": round(residual, 1)}
    return (_paint(mesh, part.color_hex), residual), meta


async def _hunyuan(part: Part, photo: Path) -> tuple[trimesh.Trimesh, float] | None:
    """Hunyuan mesh, upright, scaled to size; None if the Space fails or the shape doesn't match
    the dims (e.g. a 10-pack box photo for one hanger)."""
    made, _ = await _hunyuan_detail(part, photo)
    return made


async def _plan_and_face(part: Part, photo: Path | None):
    plan = await meshgen.ask(part, photo)  # never raises; None when both LLM paths failed
    face = (plan and plan.face) or (
        photo and await asyncio.to_thread(meshgen.aspect_guess, part.dims_mm, photo)
    )
    return plan, face


def _plan_info(plan) -> dict:
    if plan is None:
        return {}
    return {"made_by": plan.made_by or None, "template": plan.template, "retried": plan.retried}


def _shell_defaults(plan) -> set[str]:
    """texture: a template's shell roles still in the template's default colour (the answer gave
    none from the photo): the sampled body colour replaces them."""
    if plan is None or plan.template not in T.TEMPLATES:
        return set()
    defaults = T.TEMPLATES[plan.template][3]
    return {
        role
        for role, color in (plan.colors or {}).items()
        if role in meshgen.SHELL_ROLES
        and str(defaults.get(role, "")).upper().lstrip("#") == str(color).upper().lstrip("#")
    }


def _size(dims: Dims) -> tuple[float, float, float]:
    return (dims.w, dims.h, dims.d)


def finish_of(part: Part, colors: dict) -> str:
    """texture: the material cue ("metal" | "plastic" | "painted") from the listing, else the
    sampled body colour."""
    return photo_cut.finish_for([part.finish, part.material, part.name], colors["body"])


def _texture_sync(part: Part, geometry, photo: Path, plan, face, tier: str | None):
    """(geometry, texture info): a CAD model built backwards turned around (ASSET_SCAD_ORIENT),
    the sides painted from the product (ASSET_SIDE_COLOR), the photo on the front footprint when
    it shows this one product (ASSET_PHOTO_CUT: the product cut out of it)."""
    settings = get_settings()
    face = face or "front"
    rotate = plan.rotate_cw if plan else 0
    info: dict = {}
    c = None
    if settings.ASSET_PHOTO_CUT or settings.ASSET_SIDE_COLOR or tier == "scad":
        c = photo_cut.cut(
            photo, meshgen.face_aspect(part.dims_mm, face, rotate), size=_size(part.dims_mm)
        )
    if tier == "scad" and settings.ASSET_SCAD_ORIENT and c is not None:
        geometry = geometry if isinstance(geometry, trimesh.Scene) else trimesh.Scene(geometry)
        ori = meshgen.orientation(geometry, c.texture(rectify=True) if face == "front" else None)
        if ori["reversed"]:
            geometry = meshgen.turn_around(geometry)
            info["turned"] = ori["reason"]
            logger.info(
                "asset %s: the CAD model faced backwards (%s): turned", part.id, ori["reason"]
            )
    finish = None
    if settings.ASSET_SIDE_COLOR and c is not None:
        finish = finish_of(part, c.colors)
        geometry = meshgen.recolor(
            geometry,
            c.colors,
            finish,
            replace_all=tier in ("ai_mesh", "proxy"),
            replace=_shell_defaults(plan) if tier == "llm" else None,
            cue_near=tier == "scad",
        )
        info.update(
            body=c.colors["body"],
            trim=c.colors["trim"],
            finish=finish,
            recolored=geometry.metadata.get("recolored", []),
        )
    if plan is None or plan.single:
        geometry = meshgen.project(geometry, photo, face, rotate, finish=finish)
        info.update(geometry.metadata.get("texture") or {"photo": True})
    return geometry, info


async def _textured(part: Part, geometry, photo: Path | None, plan, face, tier: str | None = None):
    """(geometry, texture info or None): the photo on the face it shows, when it shows this one
    product; texture: the product cut out of it, on the front footprint only, the rest painted
    from the product (`_texture_sync`)."""
    if photo is None:
        return geometry, None
    try:
        return await asyncio.to_thread(_texture_sync, part, geometry, photo, plan, face, tier)
    # Blind except is deliberate: a photo that won't project (odd crop, degenerate mesh)
    # still leaves a good untextured asset; not worth losing the tier over.
    except Exception as exc:  # noqa: BLE001
        logger.info("asset %s: photo texture failed: %s", part.id, exc)
    return geometry, None


async def _generated(part: Part, photo: Path | None, pdir: Path):
    """The `llm` / `scad` / `ai_mesh` tiers (module docstring): (geometry, tier, residual, info),
    or None when none of them produced anything. Photo-textured when there's a one-product photo.
    assetgen: `info` = {made_by, template, retried} from the template call (`meshgen.Plan`);
    asset-mode: made_by names the Hunyuan Space / the OpenSCAD writer for those tiers."""
    plan, face = await _plan_and_face(part, photo)
    moulded = plan is not None and plan.shape_class != "template"
    geometry, tier, residual, hy_meta = None, None, 0.0, None
    if moulded and (pdir / "scad.glb").exists():
        geometry, tier = trimesh.load(pdir / "scad.glb", force="scene"), "scad"
    elif (
        (plan is None or moulded)
        and photo
        and (not get_settings().OFFLINE or hunyuan_cached(photo))  # asset-mode: a cached one
    ):
        hy, hy_meta = await _hunyuan_detail(part, photo)
        if hy is not None:
            (geometry, residual), tier = hy, "ai_mesh"
    if geometry is None and plan is not None:
        geometry, tier = await asyncio.to_thread(meshgen.build, plan, part.dims_mm), "llm"
    if geometry is None:
        return None
    geometry, texture = await _textured(part, geometry, photo, plan, face, tier)
    info = _plan_info(plan)
    info["texture"] = texture
    if tier == "ai_mesh":
        info["made_by"] = _hf_made_by(hy_meta)
    elif tier == "scad":
        info["made_by"] = _scad_made_by(part.id)
    return geometry, tier, residual, info


def _hf_reason(meta: dict | None) -> str:
    outcome = (meta or {}).get("outcome")
    if outcome == "quota":
        return "Hunyuan's ZeroGPU quota is spent for now"
    if outcome == "offline":
        return "Offline, and no Hunyuan mesh is cached for this photo"
    if outcome == "mismatch":
        return f"Hunyuan's shape didn't match the listed size ({(meta or {}).get('residual', 0):.0f}% off)"
    return "Hunyuan didn't answer"


async def _generated_hf(part: Part, photo: Path | None, pdir: Path):
    """asset-mode "hf": Hunyuan3D image-to-3D for every part with a photo (not only moulded ones),
    photo-textured. ((geometry, tier, residual, info) or None, note): when the Space can't (no
    photo, the quota, a failure, a shape that doesn't match the size) the LLM template stands in
    and `note` says why."""
    plan, face = await _plan_and_face(part, photo)
    geometry, tier, residual, info, reason = None, None, 0.0, {}, None
    if photo is None:
        reason = "No product photo for image-to-3D"
    else:
        hy, meta = await _hunyuan_detail(part, photo, MAX_HF_MODE_RESIDUAL_PCT)
        if hy is not None:
            (geometry, residual), tier = hy, "ai_mesh"
            info = {**_plan_info(plan), "made_by": _hf_made_by(meta)}
        else:
            reason = _hf_reason(meta)
    if geometry is None and plan is not None:
        geometry, tier = await asyncio.to_thread(meshgen.build, plan, part.dims_mm), "llm"
        info = _plan_info(plan)
    if geometry is None:
        return None, reason
    geometry, info["texture"] = await _textured(part, geometry, photo, plan, face, tier)
    return (geometry, tier, residual, info), reason


_scad_jobs: dict[str, asyncio.Task] = {}
_scad_failed: set[str] = set()
_scad_started: dict[str, float] = {}
# cad: live CAD runs at once (a search in llm_scad mode starts one per candidate); the rest wait.
SCAD_LIVE_JOBS = 2
# cad: a part whose CAD run failed isn't retried live for this long with the same model
# (`python -m server.warm --scad --retry-failed` does; another LLM_ASSET_SCAD model does).
SCAD_FAIL_RETRY_S = 3600.0
SCAD_ETA_FLOOR_S = 10.0  # "almost done" once a run takes longer than its model usually does
_scad_gates: dict[int, asyncio.Semaphore] = {}


def _scad_gate() -> asyncio.Semaphore:
    loop = id(asyncio.get_running_loop())
    if loop not in _scad_gates:
        _scad_gates.clear()
        _scad_gates[loop] = asyncio.Semaphore(SCAD_LIVE_JOBS)
    return _scad_gates[loop]


def scad_path(part_id: str) -> Path:
    return part_dir(part_id) / "scad.glb"


def scad_pending(part_id: str) -> bool:
    task = _scad_jobs.get(part_id)
    return task is not None and not task.done()


def scad_failed(part_id: str) -> dict | None:
    """cad: the part's last CAD failure while it still counts (this process saw it fail, or
    scad.failed.json by the same model within SCAD_FAIL_RETRY_S), else None."""
    rec = meshgen.scad_failure(scad_path(part_id))
    if part_id in _scad_failed:
        return rec or {"error": "Grok's OpenSCAD model didn't build"}
    if rec is None or rec.get("model") != meshgen.scad_model():
        return None
    return rec if time.time() - float(rec.get("at") or 0) < SCAD_FAIL_RETRY_S else None


def _start_grok_scad(part: Part, photo: Path) -> None:
    """Grok's OpenSCAD loop in the background (seconds to minutes, by model); when it ends the
    part's llm_scad variant is rebuilt (the CAD model, or the template noted "didn't build"), so
    the next request serves it. Not while another process (warm --scad) is writing it."""
    if scad_pending(part.id) or meshgen.scad_writing(scad_path(part.id)) is not None:
        return

    async def run() -> None:
        out = scad_path(part.id)
        try:
            async with _scad_gate():
                ok = out.exists() or await meshgen.grok_scad(part, photo, out)
        except Exception as exc:  # noqa: BLE001 -- grok_scad logs; the template stays
            logger.info("asset %s: grok_scad failed: %s", part.id, exc)
            ok = False
        if not ok:
            _scad_failed.add(part.id)
        await resolve_asset(part, mode="llm_scad", select_it=False, rebuild=True)

    _scad_started[part.id] = time.time()
    _scad_jobs[part.id] = asyncio.create_task(run())


def cad_status(part_id: str) -> dict:
    """cad: `asset.cad` for an llm_scad answer, so the headset can show progress and upgrade:
    {status: "ready" | "writing" | "failed" | "none", started_at, eta_s, made_by, seconds,
    reason}. "writing": a run in this process or another (warm --scad) is on it, `eta_s` the
    seconds left by the model's typical run; "failed": it didn't build (or can't be made here:
    no OpenSCAD, no photo, offline), `reason` says why; "none": nothing started yet."""
    out = scad_path(part_id)
    if out.exists():
        rec = meshgen.scad_record(out) or {}
        return {
            "status": "ready",
            "made_by": rec.get("made_by") or _scad_made_by(part_id),
            "seconds": rec.get("seconds"),
            "cost_usd": rec.get("cost_usd"),
            "started_at": rec.get("started_at"),
            "eta_s": 0,
        }
    lock = meshgen.scad_writing(out)
    if lock is not None or scad_pending(part_id):
        lock = lock or {}
        started = float(lock.get("started_at") or _scad_started.get(part_id) or time.time())
        model = lock.get("model") or meshgen.scad_model()
        profile = lock.get("profile") or meshgen.scad_profile(model)
        left = meshgen.scad_eta_s(profile) - (time.time() - started)
        return {
            "status": "writing",
            "made_by": f"{model} + openscad",
            "started_at": round(started, 1),
            "eta_s": round(max(SCAD_ETA_FLOOR_S, left)),
        }
    failed = scad_failed(part_id)
    reason = None
    if failed is not None:
        reason = f"Grok's OpenSCAD model didn't build: {str(failed.get('error') or '')[:160]}"
    elif meshgen.openscad() is None:
        reason = "OpenSCAD isn't installed on the server"
    elif not (part_dir(part_id) / "image.jpg").exists():
        reason = "No product photo for Grok's OpenSCAD"
    elif get_settings().OFFLINE:
        reason = "Offline, and no OpenSCAD model is cached"
    if reason is not None:
        return {"status": "failed", "reason": reason, "at": (failed or {}).get("at")}
    return {"status": "none"}


def with_cad(part: Part) -> Part:
    """cad: the part with `asset.cad` filled in when its asset is an llm_scad one (a copy)."""
    if normalize_mode(part.asset.mode) != "llm_scad":
        return part
    asset = part.asset.model_copy(update={"cad": cad_status(part.id)})
    return part.model_copy(update={"asset": asset})


def scad_stale(part_id: str, asset: Asset) -> bool:
    """cad: a stored llm_scad variant that isn't Grok's CAD, kept for a reason that has passed,
    so it's made again: its CAD model has landed since (scad.glb, e.g. from warm --scad in
    another process); OpenSCAD was missing and is here now; it was offline and isn't; Grok was
    writing it and nothing is (a restart lost the run); it didn't build and that no longer
    counts (another model, or SCAD_FAIL_RETRY_S later)."""
    if asset.tier == "scad":
        return False
    if scad_path(part_id).exists():
        return True
    note = (asset.note or "").lower()
    if "openscad isn't installed" in note:
        return meshgen.openscad() is not None
    if note.startswith("offline, and no openscad"):
        return not get_settings().OFFLINE
    if "still writing" in note:
        return not scad_pending(part_id) and meshgen.scad_writing(scad_path(part_id)) is None
    if "didn't build" in note:
        return scad_failed(part_id) is None
    return False


async def _generated_scad(part: Part, photo: Path | None, pdir: Path):
    """asset-mode "llm_scad": Grok writes OpenSCAD (role `asset_scad`) for every part with a photo,
    rendered by the openscad binary (`meshgen.grok_scad`, E4), photo-textured. Seconds (grok-4.20)
    to minutes (grok-4.7) per part, so a missing scad.glb starts in the background and the LLM
    template stands in (noted; `asset.cad` says "writing") until it lands. No openscad binary on
    the server: the template, noted -- clearly not CAD."""
    plan, face = await _plan_and_face(part, photo)
    scad = pdir / "scad.glb"
    reason = None
    if not scad.exists():
        if photo is None:
            reason = "No product photo for Grok's OpenSCAD"
        elif meshgen.openscad() is None:
            reason = "OpenSCAD isn't installed on the server"
        elif get_settings().OFFLINE:
            reason = "Offline, and no OpenSCAD model is cached"
        elif scad_failed(part.id) is not None:
            reason = "Grok's OpenSCAD model didn't build"
        else:
            _start_grok_scad(part, photo)
            reason = "Grok is still writing the OpenSCAD model"
    geometry, tier, info = None, None, {}
    if scad.exists():
        geometry, tier = trimesh.load(scad, force="scene"), "scad"
        info = {**_plan_info(plan), "made_by": _scad_made_by(part.id)}
        reason = None
    elif plan is not None:
        geometry, tier = await asyncio.to_thread(meshgen.build, plan, part.dims_mm), "llm"
        info = _plan_info(plan)
    if geometry is None:
        return None, reason
    if tier != "scad" or get_settings().ASSET_SCAD_PHOTO:  # cad: or Grok's own paint only
        geometry, info["texture"] = await _textured(part, geometry, photo, plan, face, tier)
    return (geometry, tier, 0.0, info), reason


def _box(part: Part, photo: Path | None):
    """Last tier: (the photo decal box (E3), its texture info), or a plain coloured box (None)
    if the photo won't do. texture: its sides in the product's sampled colour."""
    if photo is not None:
        try:
            face = meshgen.aspect_guess(part.dims_mm, photo)
            finish, info = None, {}
            if get_settings().ASSET_SIDE_COLOR:
                c = photo_cut.cut(
                    photo, meshgen.face_aspect(part.dims_mm, face), size=_size(part.dims_mm)
                )
                finish = finish_of(part, c.colors)
                info = {"body": c.colors["body"], "trim": c.colors["trim"], "finish": finish}
            scene = meshgen.decal_box(part.dims_mm, photo, face, finish=finish)
            return scene, {**info, **(scene.metadata.get("texture") or {})} or None
        except Exception as exc:  # noqa: BLE001 -- any photo problem: the plain box below
            logger.info("asset %s: decal box failed: %s", part.id, exc)
    return proxy_mesh(part.dims_mm, part.color_hex), None


# --- asset-mode: variants on disk ------------------------------------------------------------


def variant_asset(part_id: str, mode: str, *, fresh: bool = True) -> Asset | None:
    """The mode's ready model of the part (its asset), else None. The auto variant only counts
    while model.glb exists: deleting model.glb still means "re-resolve" (warm --retry-proxies,
    --grok-moulded), as before variants. cad: an llm_scad variant kept for a reason that has
    passed (`scad_stale`) doesn't count unless `fresh` is False."""
    mode = normalize_mode(mode)
    glb, meta = variant_paths(part_id, mode)
    if not glb.exists() or not meta.exists():
        return None
    if mode == "auto" and not (part_dir(part_id) / "model.glb").exists():
        return None
    try:
        asset = Asset.model_validate_json(meta.read_text())
    except (OSError, ValueError):
        return None
    if asset.status != "ready":
        return None
    if mode == "llm_scad" and fresh and scad_stale(part_id, asset):  # cad: made again
        return None
    return asset


def has_variant(part_id: str, mode: str) -> bool:
    """cad: the mode's model is on disk, current or not (a stale one is rebuilt in seconds)."""
    return variant_asset(part_id, mode, fresh=False) is not None


def _store_variant(part_id: str, mode: str, geometry, asset: Asset) -> None:
    glb, meta = variant_paths(part_id, mode)
    glb.parent.mkdir(parents=True, exist_ok=True)
    tmp = glb.with_name(f".{glb.name}.tmp")
    geometry.export(tmp, file_type="glb")
    os.replace(tmp, glb)
    cache.write_json_atomic(meta, asset.model_dump_json(indent=2))


def _adopt_legacy(part_id: str) -> None:
    """A part resolved before variants existed (model.glb + a ready part.json whose asset has no
    mode): its model becomes the auto variant, and an `ai_mesh` / `scad` one is also that mode's
    (a Hunyuan mesh made once is never made again)."""
    pdir = part_dir(part_id)
    model = pdir / "model.glb"
    if not model.exists():
        return
    part = _disk_part(part_id)
    if part is None or part.asset.status != "ready" or part.asset.mode is not None:
        return
    part.asset.mode = "auto"
    for mode in ("auto", {"ai_mesh": "hf", "scad": "llm_scad"}.get(part.asset.tier)):
        if mode is None or variant_asset(part_id, mode) is not None:
            continue
        glb, meta = variant_paths(part_id, mode)
        _copy_atomic(model, glb)
        cache.write_json_atomic(
            meta, part.asset.model_copy(update={"mode": mode}).model_dump_json()
        )
    cache.write_json_atomic(pdir / "part.json", part.model_dump_json(indent=2))


def select(part: Part, mode: str, asset: Asset | None = None) -> Part:
    """model.glb := the mode's variant and part.json's asset := its asset (model.glb is the mode
    asked for last, for clients without `?mode=`). The part as written; unchanged when the mode
    has no ready model."""
    mode = normalize_mode(mode)
    asset = asset or variant_asset(part.id, mode)
    if asset is None:
        return part
    pdir = part_dir(part.id)
    glb, _ = variant_paths(part.id, mode)
    part = part.model_copy(update={"asset": asset})
    current = _disk_part(part.id)
    model = pdir / "model.glb"
    same = (
        current is not None
        and current.asset == asset
        and model.exists()
        and model.stat().st_size == glb.stat().st_size
    )
    if not same:
        _copy_atomic(glb, model)
        cache.write_json_atomic(pdir / "part.json", part.model_dump_json(indent=2))
    return part


def hf_final(part_id: str) -> bool:
    """The part's HF model is settled: a Hunyuan mesh (never rebuilt), or the variant was built
    after the Space already answered for this photo (its shape didn't fit the size)."""
    asset = variant_asset(part_id, "hf")
    if asset is None:
        return False
    return asset.tier == "ai_mesh" or hunyuan_cached(part_dir(part_id) / "image.jpg") is not None


async def resolve_asset(
    part: Part,
    *,
    allow_ai: bool = True,
    mode: str = "auto",
    select_it: bool = True,
    rebuild: bool = False,
) -> Part:
    """Resolve model.glb + image.jpg for `part`, tier by tier, always writing part.json.

    Never raises for tier failures -- falls through to the next tier. Idempotent: if the mode's
    model exists (model.glb + a ready part.json, for auto), returns that immediately.
    asset-mode: `mode` picks the tier order ("auto" as documented above, "hf" Hunyuan first,
    "llm_scad" Grok's OpenSCAD first); the result is that mode's variant, and `select_it` (the
    default) makes it model.glb too. `rebuild`: make it again even if the variant exists (the HF
    warm queue replacing a fallback; a landed OpenSCAD model).
    """
    mode = normalize_mode(mode)
    pdir = part_dir(part.id)
    image_path = pdir / "image.jpg"

    _adopt_legacy(part.id)
    if not rebuild:
        cached_asset = variant_asset(part.id, mode)
        if cached_asset is not None:
            base = _disk_part(part.id) or part
            if select_it:
                return select(base, mode, cached_asset)
            return base.model_copy(update={"asset": cached_asset})

    pdir.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()  # assetgen: asset.seconds

    image_ok = bool(part.image_url) and await fetch_image(part.image_url, image_path)
    photo = image_path if image_ok or image_path.exists() else None  # offline: last fetch's

    geometry, tier, residual, source_url, info, reason = None, "proxy", 0.0, None, {}, None

    if mode == "auto" and part.asset.source_url:
        mesh = await _try_cad(part.asset.source_url)
        if mesh is not None:
            geometry, residual = await asyncio.to_thread(
                normalize_mesh, mesh, part.dims_mm, part.mount.get("face") or "-z"
            )
            _paint(geometry, part.color_hex)
            tier, source_url = "cad", part.asset.source_url

    if geometry is None and allow_ai:
        try:
            if mode == "hf":
                made, reason = await _generated_hf(part, photo, pdir)
            elif mode == "llm_scad":
                made, reason = await _generated_scad(part, photo, pdir)
            else:
                made = await _generated(part, photo, pdir)
        # Blind except is deliberate: template booleans, a corrupt scad.glb, mesh maths on a
        # strange Hunyuan output -- whatever breaks, the part still gets its box below.
        except Exception as exc:  # noqa: BLE001
            logger.warning("asset %s: generated tiers failed: %s", part.id, exc)
            made = None
        if made is not None:
            geometry, tier, residual, *more = made
            info = more[0] if more else {}

    if geometry is None:
        geometry, info["texture"] = await asyncio.to_thread(_box, part, photo)

    part.asset = Asset(
        tier=tier,
        source_url=source_url,
        license=part.asset.license,
        scale_residual_pct=round(residual, 3),
        status="ready",
        seconds=round(time.monotonic() - start, 2),
        mode=mode,
        note=f"{reason}: {_FALLBACK_WORDS.get(tier, tier)} instead" if reason else None,
        **info,
    )
    _store_variant(part.id, mode, geometry, part.asset)
    logger.info(
        "asset %s ready (%s): tier=%s template=%s made_by=%s retried=%s in %.1f s%s",
        part.id,
        mode,
        tier,
        part.asset.template,
        part.asset.made_by,
        part.asset.retried,
        part.asset.seconds,
        f" ({part.asset.note})" if part.asset.note else "",
    )
    if select_it:
        return select(part, mode, part.asset)
    return part.model_copy()


# --- shared resolution + upgrades (the replace job, server/replace_job.py) --------------------

_inflight: dict[str, asyncio.Task] = {}
_upgrade_tried: set[str] = set()


def ready(part_id: str, mode: str | None = None) -> Part | None:
    """The part as on disk when its model.glb is ready, else None. asset-mode: with `mode`, the
    part with that mode's model (its variant), None until it's made."""
    if mode is not None:
        _adopt_legacy(part_id)
        asset = variant_asset(part_id, mode)
        base = _disk_part(part_id) if asset is not None else None
        return base.model_copy(update={"asset": asset}) if base is not None else None
    pdir = part_dir(part_id)
    if not (pdir / "model.glb").exists():
        return None
    try:
        cached = Part.model_validate_json((pdir / "part.json").read_text())
    except (OSError, ValueError):
        return None
    return cached if cached.asset.status == "ready" else None


def start(part: Part, *, allow_ai: bool = True, mode: str | None = None) -> asyncio.Task:
    """`resolve_shared` in the background (asset-mode: a `?mode=` request that isn't ready)."""
    task = asyncio.ensure_future(resolve_shared(part, allow_ai=allow_ai, mode=mode))
    task.add_done_callback(lambda t: t.cancelled() or t.exception())  # no "never retrieved"
    return task


async def resolve_shared(part: Part, *, allow_ai: bool = True, mode: str | None = None) -> Part:
    """`resolve_asset`, one run per part at a time: a second caller (the search's background
    worker and the replace job both want the same GLB) awaits the first one's task instead of
    generating it twice. Cancelling a caller (a timeout) leaves the task running.
    asset-mode: `mode` resolves (and selects) that mode's model; None is the old behaviour."""
    m = normalize_mode(mode) if mode is not None else None
    done = ready(part.id, m)
    if done is not None:
        return select(done, m) if m is not None else done
    key = f"{part.id}:{m or 'auto'}"
    task = _inflight.get(key)
    if task is None or task.done():
        kw = {"mode": m} if m is not None else {}  # tests stub resolve_asset(part, allow_ai=)
        task = asyncio.create_task(resolve_asset(part, allow_ai=allow_ai, **kw))
        _inflight[key] = task

        def _forget(t: asyncio.Task, k: str = key) -> None:
            if _inflight.get(k) is t:
                del _inflight[k]

        task.add_done_callback(_forget)
    return await asyncio.shield(task)


async def upgrade_proxy(part: Part) -> Part:
    """A part whose GLB is still the `proxy` box (its LLM call failed or was rate limited when it
    was first resolved) gets one more try at the generated tiers; a better model replaces
    model.glb in place (same URL). Once per part per server run; never offline; never raises.
    asset-mode: the auto variant is replaced (upgrades are the auto order's)."""
    current = ready(part.id) or part
    if current.asset.tier != "proxy" or part.id in _upgrade_tried or get_settings().OFFLINE:
        return current
    if normalize_mode(current.asset.mode) != "auto":
        return current
    _upgrade_tried.add(part.id)
    pdir = part_dir(part.id)
    photo = pdir / "image.jpg"
    try:
        made = await _generated(current, photo if photo.exists() else None, pdir)
    except Exception as exc:  # noqa: BLE001 -- the proxy stays; an upgrade is a bonus
        logger.info("asset %s: upgrade failed: %s", part.id, exc)
        return current
    if made is None:
        return current
    geometry, tier, residual, *more = made
    info = more[0] if more else {}
    asset = Asset(
        tier=tier,
        source_url=current.asset.source_url,
        license=current.asset.license,
        scale_residual_pct=round(residual, 3),
        status="ready",
        mode="auto",
        **info,
    )
    _store_variant(part.id, "auto", geometry, asset)
    current = select(current, "auto", asset)
    logger.info("asset %s: upgraded proxy -> %s", part.id, tier)
    return current


# --- asset-mode: the HF warm step (server/hf_warm.py, server.warm --hf) ------------------------


async def warm_hf(part: Part) -> str:
    """The part's Hunyuan model made (or found) without changing the model it shows. "ready" (a
    Hunyuan mesh exists), "mismatch" (made; its shape didn't fit the size, the template stands
    in), "quota" (every HF token's and the IP's ZeroGPU quota is spent: back off), "failed",
    "no_photo" or "offline". Never raises."""
    if hf_final(part.id):
        asset = variant_asset(part.id, "hf")
        return "ready" if asset is not None and asset.tier == "ai_mesh" else "mismatch"
    photo = part_dir(part.id) / "image.jpg"
    if not photo.exists() and not (part.image_url and await fetch_image(part.image_url, photo)):
        return "no_photo"
    if get_settings().OFFLINE and hunyuan_cached(photo) is None:
        return "offline"
    try:
        got = await resolve_asset(part, mode="hf", select_it=False, rebuild=True)
    except Exception as exc:  # noqa: BLE001 -- one part's failure never stops the queue
        logger.warning("hf warm %s failed: %s", part.id, exc)
        return "failed"
    if got.asset.tier == "ai_mesh":
        return "ready"
    if hunyuan_cached(photo) is not None:
        return "mismatch"
    return "quota" if AI_MESH_LAST.get("outcome") == "quota" or HF_KEYS.exhausted() else "failed"


# --- texture: re-texture the models already made (server.warm --retexture) ---------------------


async def _source_geometry(part: Part, asset: Asset, photo: Path | None, plan):
    """The untextured geometry a variant was made from, from what's on disk: scad.glb (scad), the
    raw Hunyuan mesh cached by photo hash (ai_mesh), the template from the cached plan (llm), the
    box (proxy). None when it isn't there (a plan the cache doesn't have, a downloaded cad mesh)."""
    pdir = part_dir(part.id)
    if asset.tier == "scad":
        path = pdir / "scad.glb"
        return trimesh.load(path, force="scene") if path.exists() else None
    if asset.tier == "ai_mesh":
        raw = hunyuan_cached(photo)
        if raw is None:
            return None
        mesh, _ = await asyncio.to_thread(
            normalize_mesh,
            trimesh.load(raw, force="scene"),
            part.dims_mm,
            part.mount.get("face") or "-z",
            True,
            True,
        )
        return _paint(mesh, part.color_hex)
    if asset.tier == "llm":
        return await asyncio.to_thread(meshgen.build, plan, part.dims_mm) if plan else None
    return None  # proxy: _box makes it whole; cad: a downloaded mesh, kept


async def retexture(part: Part, mode: str) -> dict:
    """texture: the mode's model made again from its own geometry with today's texturing (the
    product cut out of its photo, front-only mapping, sides from the product, a backwards CAD
    model turned) -- no LLM call (the plan comes from the cache; run it OFFLINE to be sure) and no
    Hunyuan call (the raw mesh is cached by photo hash). The variant keeps its asset (tier,
    made_by, seconds...) with `texture` updated, and model.glb follows when it's the selected
    one. {outcome: "done" | "kept" | "none", tier, reason, texture, seconds}. Never raises."""
    t0 = time.monotonic()
    mode = normalize_mode(mode)
    _adopt_legacy(part.id)
    asset = variant_asset(part.id, mode, fresh=False)
    if asset is None:
        return {"outcome": "none", "tier": None, "reason": "no model"}
    photo = part_dir(part.id) / "image.jpg"
    photo = photo if photo.exists() else None
    out = {"outcome": "kept", "tier": asset.tier, "reason": None, "texture": None}
    try:
        if asset.tier == "proxy":
            geometry, texture = await asyncio.to_thread(_box, part, photo)
        else:
            plan, face = await _plan_and_face(part, photo)
            geometry = await _source_geometry(part, asset, photo, plan)
            if geometry is None:
                out["reason"] = {
                    "llm": "the template plan isn't cached",
                    "ai_mesh": "the raw Hunyuan mesh isn't cached",
                    "scad": "scad.glb is missing",
                }.get(asset.tier, f"{asset.tier} models aren't re-textured")
                return {**out, "seconds": round(time.monotonic() - t0, 2)}
            texture = None
            if asset.tier != "scad" or mode != "llm_scad" or get_settings().ASSET_SCAD_PHOTO:
                geometry, texture = await _textured(part, geometry, photo, plan, face, asset.tier)
    except Exception as exc:  # noqa: BLE001 -- one part's failure never stops the warm
        logger.warning("retexture %s (%s) failed: %s", part.id, mode, exc)
        return {**out, "reason": f"{type(exc).__name__}: {str(exc)[:120]}"}
    if texture is not None:
        texture = {**texture, "retextured_at": round(time.time(), 1)}
    new = asset.model_copy(update={"texture": texture})
    _store_variant(part.id, mode, geometry, new)
    current = _disk_part(part.id)
    if current is not None and normalize_mode(current.asset.mode) == mode:
        select(current, mode, new)
    return {
        **out,
        "outcome": "done",
        "texture": texture,
        "seconds": round(time.monotonic() - t0, 2),
    }
