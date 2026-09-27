"""FastAPI routes for the AirTool parts server (plan §1b endpoint table)."""

import asyncio
import base64
import contextlib
import hashlib
import json
import logging
import math
import re
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Literal

import httpx
import openai
from fastapi import (
    FastAPI,
    File,
    Form,
    Header,
    HTTPException,
    Request,
    UploadFile,
    WebSocket,
)
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse, Response
from pydantic import BaseModel, Field, ValidationError

from server import (
    __version__,
    agent,
    assets,
    bom,
    booth,
    cache,
    catalog,
    catalog_index,
    checkout,
    coach,
    coverage,
    finish,
    flythrough,
    hf_warm,
    imagine,
    intel,
    jobs,
    labels,
    mandate,
    manuals,
    packet,
    plan,
    postcard,
    quote,
    realtime,
    report,
    resize,
    rules,
    runjob,
    safety,
    search,
    survey,
    tape_survey,
    vision,
    voice,
)
from server.config import get_settings
from server.keys import KeysExhausted
from server.models import Dims, SearchRequest

logging.basicConfig(level=logging.INFO)
# httpx logs full request URLs at INFO, and SerpApi takes its key as a query param.
logging.getLogger("httpx").setLevel(logging.WARNING)
logger = logging.getLogger(__name__)


@asynccontextmanager
async def _lifespan(_app: FastAPI) -> AsyncIterator[None]:
    catalog_index.INDEX.start()  # catalog: the lazy-search index, built in a background thread
    yield


app = FastAPI(title="airtools parts server", version=__version__, lifespan=_lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])

_PART_ID_RE = re.compile(r"^[a-z0-9-]+$")
_STATIC_DIR = Path(__file__).parent / "static"

# /scene/ask's frames are base64 JPEGs decoded+capped by vision.py (MAX_FRAMES x
# MAX_FRAME_BYTES), but that check only runs *after* FastAPI has already buffered the whole
# JSON body -- this bounds the raw body itself (base64 inflates ~4/3, plus JSON overhead).
_SCENE_ASK_MAX_BYTES = vision.MAX_FRAMES * (vision.MAX_FRAME_BYTES * 4 // 3 + 1024) + 4096


async def _read_capped(chunks: AsyncIterator[bytes], max_bytes: int) -> bytes:
    """Buffer `chunks`, aborting with 413 the moment the total would exceed `max_bytes` --
    the shared choke point for /voice/command's upload and /scene/ask's body, so neither reads
    an oversized payload fully into memory before checking its size."""
    total = 0
    buf = bytearray()
    async for chunk in chunks:
        total += len(chunk)
        if total > max_bytes:
            raise HTTPException(413, f"request body exceeds {max_bytes} byte cap")
        buf += chunk
    return bytes(buf)


async def _iter_upload(upload: UploadFile, chunk_size: int = 1024 * 1024) -> AsyncIterator[bytes]:
    while chunk := await upload.read(chunk_size):
        yield chunk


def _retry_after_s(exc: openai.RateLimitError) -> int:
    """The provider's Retry-After in whole seconds (rounded up), else a minute -- Groq's limits
    are per minute."""
    try:
        return max(1, math.ceil(float(exc.response.headers.get("retry-after", ""))))
    except (TypeError, ValueError):
        return 60


@app.exception_handler(openai.RateLimitError)
async def llm_rate_limited(request: Request, exc: openai.RateLimitError) -> JSONResponse:
    """A provider 429 (Groq free tier: ~8K tokens/min for the agent, 1K output tokens/min for
    vision) means "try again shortly", not a server bug: 503 + Retry-After (the app already shows
    503 as "needs the live model") instead of an unhandled 500. Covers every route that reaches
    an LLM: /scene/ask, /agent/command, /voice/command."""
    retry_after = _retry_after_s(exc)
    logger.warning(
        "llm rate-limited on %s, retry after %d s: %s", request.url.path, retry_after, exc
    )
    return JSONResponse(
        {"detail": f"the live model is rate-limited, retry in {retry_after} s"},
        status_code=503,
        headers={"Retry-After": str(retry_after)},
    )


@app.get("/health")
def health() -> dict:
    return {"ok": True, "version": __version__}


@app.get("/debug")
def debug_console() -> FileResponse:
    """Plain-HTML dev console for exercising the server without the Unity/Quest app."""
    return FileResponse(_STATIC_DIR / "debug.html", media_type="text/html")


# --- parts: search + job polling ---------------------------------------------


@app.post("/parts/search")
async def search_parts(req: SearchRequest) -> dict:
    job = jobs.start_search(req)
    job = await jobs.wait(job.id, 0.5)
    body = {"job_id": job.id, "status": job.status, "stage": job.stage}
    if job.status == "done":
        body["candidates"] = job.candidates
    return body


@app.get("/parts/jobs/{job_id}")
def get_job(job_id: str) -> dict:
    job = jobs.get(job_id)
    if job is None:
        raise HTTPException(404, "unknown job")
    body = job.model_dump()
    if job.status == "done":
        body["summary"] = jobs.summary(job)
    return body


# --- catalog: products that fit the place (server/catalog.py) ---------------------------------


@app.get("/catalog")
async def get_catalog(
    site: str | None = None, session_id: str | None = None, focus: str | None = None
) -> dict:
    """The site's environment and up to 8 known parts per category for it; categories still
    being searched say `pending` (poll again). No site: the generic catalog. `focus` (what the
    wearer looks at: roof, wall, ground, ceiling, counter, opening) orders and filters the
    categories; `foci` says which ones the site answers."""
    try:
        return await catalog.build(site or None, session_id, focus)
    except LookupError as exc:
        raise HTTPException(404, "unknown site") from exc


@app.get("/catalog/search")
def catalog_search(q: str = "", site: str | None = None, limit: int = 20) -> dict:
    """Lazy search over every part known locally (in memory; no LLM, no web)."""
    return catalog.search_body(q[:100], site or None, limit)


# --- parts: static files -------------------------------------------------


def _validated_part_dir(part_id: str) -> Path:
    if not _PART_ID_RE.match(part_id):
        raise HTTPException(400, "invalid part id")
    return assets.part_dir(part_id)


def _serve_part_file(part_id: str, filename: str, media_type: str) -> FileResponse:
    path = _validated_part_dir(part_id) / filename
    if not path.exists():
        raise HTTPException(404, f"{filename} not found for part {part_id!r}")
    return FileResponse(path, media_type=media_type)


@app.get("/parts/{part_id}/part.json", response_model=None)
async def get_part_json(part_id: str, mode: str | None = None) -> FileResponse | JSONResponse:
    """asset-mode: with `?mode=hf|llm_scad|auto` the part with that mode's model (asset.tier,
    made_by, note); asset.status "pending" while it's being made (started here, poll again)."""
    if mode is None:
        return _serve_part_file(part_id, "part.json", "application/json")
    _validated_part_dir(part_id)
    part = jobs.load_part(part_id)
    if part is None:
        raise HTTPException(404, f"part.json not found for part {part_id!r}")
    m = assets.normalize_mode(mode)
    got = assets.ready(part_id, m)
    if got is None:
        task = assets.start(part, mode=m)
        if assets.has_variant(part_id, m):  # cad: a stale one is made again in seconds
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(asyncio.shield(task), STALE_REBUILD_WAIT_S)
            got = assets.ready(part_id, m)
    if got is not None:
        got = assets.select(got, m)
    else:
        pending = part.asset.model_copy(update={"status": "pending", "mode": m, "note": None})
        got = part.model_copy(update={"asset": pending})
    if m == "hf" and not assets.hf_final(part_id):
        hf_warm.enqueue(part, front=True)
    return JSONResponse(assets.with_cad(got).model_dump(mode="json"))


# cad: an llm_scad model kept for a reason that has passed (OpenSCAD installed since, the CAD
# model landed) is rebuilt on request; the template and a cached plan take a few seconds.
STALE_REBUILD_WAIT_S = 15.0


MODEL_ON_DEMAND_S = 90.0  # one LLM template call + build; a queued Groq call can take a minute


def _asset_headers(asset) -> dict[str, str]:
    """asset-mode: which tier made the served model, ASCII-safe (a badge without part.json).
    cad: X-Asset-Cad = the CAD model's status on an llm_scad answer ("writing", "ready", ...)."""
    safe = lambda v: str(v).encode("ascii", "replace").decode()
    headers = {
        "X-Asset-Tier": safe(asset.tier),
        "X-Asset-Made-By": safe(asset.made_by or ""),
        "X-Asset-Mode": safe(asset.mode or "auto"),
    }
    if asset.cad:
        headers["X-Asset-Cad"] = safe(asset.cad.get("status") or "")
    return headers


@app.get("/parts/{part_id}/model.glb")
async def get_part_model(part_id: str, mode: str | None = None) -> FileResponse:
    """The part's GLB. A searched part whose model isn't built yet (the search's background worker
    hasn't got to it; "next one" after a replace job that stopped) is built now, once
    (assets.resolve_shared), so a candidate's model_url never 404s.
    asset-mode: `?mode=hf|llm_scad|auto` serves (building it first when needed) that mode's
    model, `model.<hf|scad|auto>.glb`, and makes it model.glb; X-Asset-Tier / X-Asset-Made-By say
    who made it."""
    pdir = _validated_part_dir(part_id)
    if mode is not None:
        m = assets.normalize_mode(mode)
        part = assets.ready(part_id, m) or jobs.load_part(part_id)
        if part is None:
            raise HTTPException(404, f"no part {part_id!r}")
        try:
            part = await asyncio.wait_for(assets.resolve_shared(part, mode=m), MODEL_ON_DEMAND_S)
        except TimeoutError as exc:
            raise HTTPException(504, f"the {m} model for {part_id!r} is still being built") from exc
        if m == "hf" and not assets.hf_final(part_id):
            hf_warm.enqueue(part, front=True)
        glb, _ = assets.variant_paths(part_id, m)
        if not glb.exists():
            raise HTTPException(404, f"no {m} model for part {part_id!r}")
        headers = _asset_headers(assets.with_cad(part).asset)
        return FileResponse(glb, media_type="model/gltf-binary", headers=headers)
    if not (pdir / "model.glb").exists():
        part = jobs.load_part(part_id)
        if part is not None:
            try:
                await asyncio.wait_for(assets.resolve_shared(part), MODEL_ON_DEMAND_S)
            except TimeoutError as exc:
                raise HTTPException(504, f"the model for {part_id!r} is still being built") from exc
    return _serve_part_file(part_id, "model.glb", "model/gltf-binary")


class ModelRequest(BaseModel):
    """asset-mode: POST /parts/{id}/model body (all optional; `?mode=` works too)."""

    mode: str | None = None
    wait_s: float = Field(default=0, ge=0, le=60)


@app.post("/parts/{part_id}/model")
async def request_part_model(
    part_id: str, mode: str | None = None, wait_s: float = 0, body: ModelRequest | None = None
) -> dict:
    """asset-mode: ask for the part's model in a mode ("hf" | "llm_scad" | "auto"); it's made in
    the background (a Hunyuan mesh ~15-20 s + queue; Grok's OpenSCAD minutes) and `status` is
    "ready" or "pending" -- poll this or part.json?mode=. `wait_s` (<= 60) waits that long first.
    HF meshes are made once per part and kept; an "hf" request that got a stand-in (quota spent)
    jumps the HF warm queue."""
    _validated_part_dir(part_id)
    part = _load_part(part_id)
    m = assets.normalize_mode(mode or (body.mode if body else None))
    wait = min(60.0, max(wait_s, body.wait_s if body else 0))
    got = assets.ready(part_id, m)
    if got is None:
        task = assets.start(part, mode=m)
        if assets.has_variant(part_id, m):  # cad: a stale one is made again in seconds
            wait = max(wait, STALE_REBUILD_WAIT_S)
        if wait > 0:
            try:
                await asyncio.wait_for(asyncio.shield(task), wait)
            except TimeoutError:
                pass
        got = assets.ready(part_id, m)
    hf_queued = m == "hf" and not assets.hf_final(part_id) and hf_warm.enqueue(part, front=True)
    asset = (
        got.asset
        if got is not None
        else part.asset.model_copy(update={"status": "pending", "mode": m, "note": None})
    )
    if m == "llm_scad":
        asset = asset.model_copy(update={"cad": assets.cad_status(part_id)})
    return {
        "part_id": part_id,
        "mode": m,
        "status": "ready" if got is not None else "pending",
        "asset": asset.model_dump(mode="json"),
        "model_url": f"/parts/{part_id}/model.glb?mode={m}",
        "hf_queued": bool(hf_queued),
        "scad_pending": assets.scad_pending(part_id),
        "cad": asset.cad,
    }


@app.get("/assets/hf-warm")
def get_hf_warm() -> dict:
    """asset-mode: the HF warm queue (queued / running / made, the backoff, ZeroGPU quota notes)."""
    return hf_warm.status()


@app.get("/parts/{part_id}/image.jpg")
def get_part_image(part_id: str) -> FileResponse:
    return _serve_part_file(part_id, "image.jpg", "image/jpeg")


@app.get("/parts/{part_id}/postcard.jpg")
def get_part_postcard(part_id: str) -> FileResponse:
    return _serve_part_file(part_id, "postcard.jpg", "image/jpeg")


@app.get("/parts/{part_id}/manual.pdf")
def get_part_manual(part_id: str) -> FileResponse:
    """The install manual `POST /parts/{id}/manual` fetched; `#page=n` opens it at a page."""
    return _serve_part_file(part_id, "manual.pdf", "application/pdf")


@app.get("/parts/{part_id}/model-{slug}.glb")
def get_part_finish_model(part_id: str, slug: str) -> FileResponse:
    if not _PART_ID_RE.match(slug):  # same slug alphabet
        raise HTTPException(400, "invalid finish")
    return _serve_part_file(part_id, f"model-{slug}.glb", "model/gltf-binary")


@app.get("/parts/{part_id}/finish-{slug}.jpg")
def get_part_finish_image(part_id: str, slug: str) -> FileResponse:
    if not _PART_ID_RE.match(slug):  # same slug alphabet
        raise HTTPException(400, "invalid finish")
    return _serve_part_file(part_id, f"finish-{slug}.jpg", "image/jpeg")


# --- parts: finish variants (F11, server/finish.py) --------------------------


class FinishRequest(BaseModel):
    finish: str = Field(max_length=60, pattern=r"[A-Za-z0-9]")


@app.post("/parts/{part_id}/finish")
async def part_finish(part_id: str, body: FinishRequest) -> dict:
    return await finish.apply(_load_part(part_id), body.finish)


# --- parts: made to size (assetgen, server/resize.py) -------------------------


class ResizeRequest(BaseModel):
    """The size the headset's Size & finish panel asks for (mm), and optionally a finish."""

    w: float = Field(gt=0)
    h: float = Field(gt=0)
    d: float = Field(gt=0)
    finish: str | None = Field(default=None, max_length=60)


@app.post("/parts/{part_id}/resize")
async def part_resize(part_id: str, body: ResizeRequest) -> dict:
    """The part's model rebuilt at w x h x d (its template at the new size; a mesh stretched),
    served at the returned `model_url`. 409 while its model.glb is still being made; 422 for a
    size outside 0.5-2x the listing."""
    part = _load_part(part_id)
    if part.asset.status != "ready" or not (assets.part_dir(part_id) / "model.glb").exists():
        raise HTTPException(409, "the part's model isn't ready yet")
    try:
        return await resize.resize(part, Dims(w=body.w, h=body.h, d=body.d), body.finish)
    except resize.ResizeError as exc:
        raise HTTPException(422, str(exc)) from exc


# --- scenes: read-only scene packages (plan §1a/§1b) ------------------------

_SITE_RE = re.compile(r"^[a-z0-9-]+$")
_SCENE_SEGMENT_RE = re.compile(r"^[A-Za-z0-9._-]+$")
_SCENE_REVISIONED_RE = re.compile(r"\.r\d+\.")  # e.g. mesh.r2.glb -- immutable once published
_SCENE_CONTENT_TYPES = {
    ".glb": "model/gltf-binary",
    ".json": "application/json",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
}


def _scene_dir() -> Path:
    return Path(get_settings().SCENE_DIR)


def _scene_content_type(name: str) -> str:
    return _SCENE_CONTENT_TYPES.get(Path(name).suffix.lower(), "application/octet-stream")


def _safe_scene_path(site: str, file: str) -> Path | None:
    """`scene/<site>/<file>` resolved and checked to stay inside `scene/<site>` -- rejects `..`
    segments, absolute-looking segments, and symlinks that escape the site directory. `scene/` is
    served read-only (plan §1a), so this is the only thing standing between a crafted request path
    and the rest of the laptop's disk."""
    if not _SITE_RE.match(site):
        return None
    parts = file.split("/")
    if any(not _SCENE_SEGMENT_RE.match(p) or p in (".", "..") for p in parts):
        return None
    base = (_scene_dir() / site).resolve()
    candidate = base.joinpath(*parts).resolve()
    if candidate != base and base not in candidate.parents:
        return None
    return candidate


@app.get("/scenes")
def list_scenes() -> list[dict]:
    """Every site under SCENE_DIR with a scene.json, and its current revision/quality/publish
    time (plan §1a) -- lets the app discover what's available without hard-coding site names."""
    base = _scene_dir()
    if not base.is_dir():
        return []
    sites = []
    for child in sorted(base.iterdir()):
        scene_json = child / "scene.json"
        if not child.is_dir() or not scene_json.is_file():
            continue
        try:
            meta = json.loads(scene_json.read_text())
        except (json.JSONDecodeError, OSError):
            continue
        sites.append(
            {
                "site": child.name,
                # "revision"/"quality" are new §1a fields the pipeline may not write yet --
                # default to a single already-full publish so old packages still list sanely.
                "revision": meta.get("revision", 1),
                "quality": meta.get("quality", "full"),
                "updated_at": datetime.fromtimestamp(
                    scene_json.stat().st_mtime, tz=UTC
                ).isoformat(),
            }
        )
    return sites


@app.get("/scenes/{site}/coverage")
async def scene_coverage(site: str) -> dict:
    """Capture coach (server/coverage.py): which sides the flight covered, the legs to fly next
    and one spoken line. Registered before the file route so `coverage` isn't read as a file."""
    scene_dir = coverage.site_dir(site)
    if scene_dir is None:
        raise HTTPException(404, "unknown site")
    try:
        return await coverage.coach(scene_dir)
    except coverage.Incomplete as exc:
        raise HTTPException(409, f"scene package incomplete: {exc}") from exc


@app.get("/scenes/{site}/labels")
def get_scene_labels(site: str) -> dict:
    """The scan's pre-labelled 3D anchors (`python -m server.warm --labels <site>`). Registered
    before the file route below, which would otherwise take `labels` as a file name."""
    body = labels.scene_labels(site)
    if body is None:
        raise HTTPException(404, "no labels for this scene; run python -m server.warm --labels")
    return body


@app.get("/scenes/{site}/{file:path}")
def get_scene_file(site: str, file: str, if_none_match: str | None = Header(None)) -> Response:
    """Serves scene/<site>/<file> read-only. scene.json gets `Cache-Control: no-cache` plus a
    content-hash ETag so the app's ~5 s poll (§1a) can use conditional GET (304, no body) instead
    of re-downloading it every time; revision-named files (`mesh.r<rev>.glb` etc., §1a) are
    immutable once published and get a long max-age instead."""
    path = _safe_scene_path(site, file)
    if path is None or not path.is_file():
        raise HTTPException(404, "not found")

    media_type = _scene_content_type(path.name)

    if path.name == "scene.json":
        body = path.read_bytes()
        etag = f'"{hashlib.sha1(body).hexdigest()}"'
        headers = {"Cache-Control": "no-cache", "ETag": etag}
        if if_none_match and etag in if_none_match:
            return Response(status_code=304, headers=headers)
        return Response(content=body, media_type=media_type, headers=headers)

    headers = {}
    if _SCENE_REVISIONED_RE.search(path.name):
        headers["Cache-Control"] = "public, max-age=31536000, immutable"
    return FileResponse(path, media_type=media_type, headers=headers)


# --- parts: seller re-ranking ----------------------------------------------


@app.post("/parts/{part_id}/sellers")
async def resort_sellers(part_id: str, sort: Literal["cheapest", "fastest", "best"] = "cheapest"):
    if not _PART_ID_RE.match(part_id):
        raise HTTPException(400, "invalid part id")
    part = jobs.load_part(part_id)
    if part is None:
        raise HTTPException(404, "unknown part")

    part = await search.refine_sellers(part, sort)
    jobs.save_part(part)
    return part


# --- parts: recall and defect radar ------------------------------------------


@app.get("/parts/{part_id}/safety")
async def part_safety(part_id: str) -> safety.SafetyReport:
    """~11 s live (CPSC + one xAI call in parallel), instant from the 24 h cache. A source
    failure (or OFFLINE with no cache) is `verdict: "unknown"`, never a 5xx."""
    if not _PART_ID_RE.match(part_id):
        raise HTTPException(400, "invalid part id")
    part = jobs.load_part(part_id)
    if part is None:
        raise HTTPException(404, "unknown part")
    return await safety.check(part)


# --- parts: bill of materials ("what else do I need?", plan §4b.6) -----------


class BomRequest(BaseModel):
    part_ids: list[str]
    counts: dict[str, int] = {}  # part_id -> placed count, default 1
    session_id: str | None = None  # optional: logs {"type":"bom","bom_id"} for the report


@app.post("/parts/bom")
async def parts_bom(req: BomRequest) -> bom.Bom:
    parts = []
    for part_id in req.part_ids:
        if not _PART_ID_RE.match(part_id):
            raise HTTPException(400, "invalid part id")
        part = jobs.load_part(part_id)
        if part is None:
            raise HTTPException(404, f"unknown part {part_id!r}")
        parts.append(part)
    result = await bom.what_else(parts, req.counts)
    if req.session_id and result.id:
        _append_notebook(req.session_id, [{"type": "bom", "bom_id": result.id}])
    return result


# --- parts: install-manual Q&A ------------------------------------------------


def _load_part(part_id: str):
    if not _PART_ID_RE.match(part_id):
        raise HTTPException(400, "invalid part id")
    part = jobs.load_part(part_id)
    if part is None:
        raise HTTPException(404, "unknown part")
    return part


class ManualRequest(BaseModel):
    domains: list[str] | None = None  # override the manufacturer's domain(s), max 5
    refresh: bool = False  # ignore a cached result (e.g. last week's "not found")


@app.post("/parts/{part_id}/manual")
async def find_manual(part_id: str, body: ManualRequest | None = None) -> manuals.Manual:
    """Synchronous: ~10-130 s live (one xAI web search + PDF downloads), instant when cached.
    `found: false` is a 200 -- no manual is an answer, not an error."""
    part = _load_part(part_id)
    body = body or ManualRequest()
    domains = [d.strip().lower() for d in body.domains or [] if d.strip()][:5] or None
    try:
        return await manuals.index(part, domains, body.refresh)
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except (RuntimeError, httpx.HTTPError) as exc:
        logger.warning("manual find for %s failed: %s", part_id, exc)
        raise HTTPException(502, "manual search failed") from exc


class ManualAskRequest(BaseModel):
    question: str


@app.post("/parts/{part_id}/manual/ask")
async def ask_manual(part_id: str, body: ManualAskRequest) -> manuals.Answer:
    """~1-2 s once the manual is indexed (indexes it first otherwise). OFFLINE with the manual
    cached still answers, pointing at the best-matching page."""
    part = _load_part(part_id)
    question = body.question.strip()[:500]
    if not question:
        raise HTTPException(400, "question is required")
    try:
        return await manuals.ask(part, question)
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except (RuntimeError, httpx.HTTPError) as exc:
        logger.warning("manual ask for %s failed: %s", part_id, exc)
        raise HTTPException(502, "manual lookup failed") from exc


# --- install coach (server/coach.py) ------------------------------------------


class CoachStartRequest(BaseModel):
    session_id: str = "default"
    part_id: str | None = None  # its manual's steps, else the template its name matches
    job: str | None = None  # a coach.JOBS key, for a job with no part


class CoachCheckRequest(BaseModel):
    frame: vision.Frame  # {id, jpg_b64}: one headset camera frame
    drill_px: list[float] | None = None  # [x, y] 0-1 in the frame; default its centre


class CoachAdvanceRequest(BaseModel):
    move: Literal["next", "back", "repeat"] = "next"  # "next" = "I did it" if not checked


def _coach_or_404(coach_id: str) -> dict:
    state = coach.load(coach_id)
    if state is None:
        raise HTTPException(404, "unknown coach")
    return state


@app.post("/coach/start")
async def coach_start(body: CoachStartRequest) -> dict:
    """Instant from a template or cached manual steps; ~5-15 s the first time for a manual."""
    if body.job is not None and body.job not in coach.JOBS:
        raise HTTPException(400, f"unknown job, want one of {sorted(coach.JOBS)}")
    part = _load_part(body.part_id) if body.part_id else None
    if part is None and body.job is None:
        raise HTTPException(400, "need a part_id or a job")
    try:
        return await coach.start(body.session_id, part, body.job)
    except coach.CantCoach as exc:
        raise HTTPException(409 if exc.refused else 404, {"spoken": exc.spoken}) from exc


@app.get("/coach/{coach_id}")
def coach_state(coach_id: str) -> dict:
    return coach.view(_coach_or_404(coach_id))


@app.post("/coach/{coach_id}/check")
async def coach_check(coach_id: str, body: CoachCheckRequest) -> dict:
    """One vision call (~2 s, ~$0.001); a frame seen before replays from the cache."""
    state = _coach_or_404(coach_id)
    try:
        return await coach.check(state, body.frame.jpg_b64, body.drill_px, body.frame.id)
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except coach.CheckFailed as exc:
        logger.warning("coach check %s failed: %s", coach_id, exc)
        raise HTTPException(502, {"spoken": coach.FAILED_LINE}) from exc


@app.post("/coach/{coach_id}/advance")
def coach_advance(coach_id: str, body: CoachAdvanceRequest | None = None) -> dict:
    return coach.move(_coach_or_404(coach_id), (body or CoachAdvanceRequest()).move)


# --- checkout ----------------------------------------------------------------


class CheckoutRequest(BaseModel):
    part_id: str
    seller_idx: int
    qty: int
    session_id: str | None = None  # optional: also logs the receipt into that notebook
    bom_id: str | None = None  # optional: a "what else do I need?" Bom to add to the same cart
    bom_lines: list[int] = []  # BomLine.idx values from that bom to include
    # The hold proof (measured mandate, brain.md S2): from /checkout/prepare + the 1 s hold.
    cart_hash: str | None = None
    hold_nonce: str | None = None
    hold_ms: int | None = None


def _checkout_inputs(part_id: str, bom_id: str | None, bom_lines: list[int]):
    """The saved part and the chosen priced BOM lines -- never client-supplied prices."""
    if not _PART_ID_RE.match(part_id):
        raise HTTPException(400, "invalid part id")
    part = jobs.load_part(part_id)
    if part is None:
        raise HTTPException(404, "unknown part")

    selected_lines = []
    if bom_id is not None:
        if not _PART_ID_RE.match(bom_id):
            raise HTTPException(400, "invalid bom id")
        saved_bom = bom.load_bom(bom_id)
        if saved_bom is None:
            raise HTTPException(404, "unknown bom")
        by_idx = {line.idx: line for line in saved_bom.lines}
        for idx in bom_lines:
            line = by_idx.get(idx)
            if line is None or line.seller is None:  # unknown, or nothing to honestly charge
                raise HTTPException(400, f"unknown bom line {idx}")
            selected_lines.append(line)
    return part, selected_lines


@app.post("/checkout")
async def do_checkout(req: CheckoutRequest) -> dict:
    part, selected_lines = _checkout_inputs(req.part_id, req.bom_id, req.bom_lines)

    signed = None
    proof = (req.cart_hash, req.hold_nonce, req.hold_ms)
    if any(p is not None for p in proof) or get_settings().REQUIRE_HOLD_PROOF:
        try:
            signed = mandate.verify_hold(
                session_id=req.session_id,
                part=part,
                seller_idx=req.seller_idx,
                qty=req.qty,
                bom_id=req.bom_id,
                bom_lines=selected_lines,
                cart_hash_=req.cart_hash,
                hold_nonce=req.hold_nonce,
                hold_ms=req.hold_ms,
            )
        except mandate.MandateError as exc:
            raise HTTPException(exc.status_code, exc.detail) from exc
        except ValueError as exc:
            raise HTTPException(400, str(exc)) from exc

    try:
        safety_report = safety.peek(part)  # cache only: checkout never waits on a live check
        receipt = await checkout.authorize(
            part,
            req.seller_idx,
            req.qty,
            bom_lines=selected_lines,
            safety_verdict=safety_report.verdict if safety_report else "unknown",
            mandate=signed,
        )
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    if signed is not None:
        mandate.record_receipt(signed["cart"], receipt["receipt_id"])
    entry = {"type": "order", **receipt, **({"bom_id": req.bom_id} if req.bom_id else {})}
    _append_notebook(req.session_id or "default", [entry])
    manuals.prefetch(part)  # bought it: have the install manual ready for questions
    return receipt


class PrepareRequest(BaseModel):
    session_id: str = Field(min_length=1, max_length=128)
    part_id: str
    seller_idx: int
    units_needed: int  # pieces the headset needs (e.g. the array count); packs are computed
    bom_id: str | None = None
    bom_lines: list[int] = []
    evidence: list[mandate.Evidence] = Field(default_factory=list, max_length=20)


@app.post("/checkout/prepare")
def checkout_prepare(req: PrepareRequest) -> dict:
    """Measured mandate (brain.md S2): price the cart on the server, run the checks the panel
    shows, and issue the single-use hold nonce (120 s) that the 1 s hold sends to /checkout.
    Never pays."""
    part, selected_lines = _checkout_inputs(req.part_id, req.bom_id, req.bom_lines)
    try:
        return mandate.prepare(
            req.session_id,
            part,
            req.seller_idx,
            req.units_needed,
            bom_id=req.bom_id,
            bom_lines=selected_lines,
            evidence=req.evidence,
        )
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc


class LimitsRequest(BaseModel):
    session_id: str = Field(min_length=1, max_length=128)
    max_total_usd: float | None = None
    deliver_by: str | None = None  # ISO date, weekday, "today" or "tomorrow"
    seller_policy: Literal["cheapest", "fastest", "best"] | None = None
    source: Literal["voice", "panel"] = "voice"
    text: str = Field("", max_length=500)


@app.post("/commerce/limits")
def commerce_limits(req: LimitsRequest) -> dict:
    """Set the session's purchase limits. Only fields present in the body change; voice can only
    tighten (a looser ask comes back in `refused`), the panel may loosen or clear (null)."""
    fields = {
        k: getattr(req, k)
        for k in ("max_total_usd", "deliver_by", "seller_policy")
        if k in req.model_fields_set
    }
    try:
        intent, refused, changed = mandate.set_limits(
            req.session_id, source=req.source, text=req.text, **fields
        )
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    return {
        "intent": intent.summary(),
        "refused": [r.model_dump() for r in refused],
        "changed": changed,
    }


# --- notebook ------------------------------------------------------------


class NotebookAppend(BaseModel):
    session_id: str
    entries: list[dict]


_notebook_path = report.notebook_path  # shared with the report, packet and run_job
_append_notebook = report.append_notebook


@app.post("/notebook")
def post_notebook(body: NotebookAppend) -> dict:
    return {
        "session_id": body.session_id,
        "entries": _append_notebook(body.session_id, body.entries),
    }


@app.get("/notebook/{session_id}")
def get_notebook(session_id: str) -> dict:
    path = _notebook_path(session_id)
    entries = json.loads(path.read_text()) if path.exists() else []
    return {"session_id": session_id, "entries": entries}


# --- agent (text + voice) -----------------------------------------------


class AgentCommandRequest(BaseModel):
    session_id: str
    text: str
    context: dict = {}
    wait_s: float = 0


@app.post("/agent/command")
async def agent_command(body: AgentCommandRequest) -> dict:
    result = await agent.handle_command(body.session_id, body.text, body.context)
    job_id = result.get("job_id")
    if job_id and body.wait_s > 0:
        job = await jobs.wait(job_id, body.wait_s)
        if job is not None and job.status == "done":
            result["summary"] = jobs.summary(job)
            result["candidates"] = job.candidates
    return result


@app.post("/voice/command")
async def voice_command(
    audio: UploadFile = File(...),  # noqa: B008 - fastapi's documented pattern
    session_id: str = Form(...),
    context: str | None = Form(None),
    tts: bool = Form(True),
) -> dict:
    audio_bytes = await _read_capped(_iter_upload(audio), voice.STT_MAX_BYTES)
    try:
        transcript = await voice.transcribe(audio_bytes, filename=audio.filename or "audio.wav")
    except (voice.VoiceError, KeysExhausted, httpx.HTTPError) as exc:
        raise HTTPException(503, f"speech-to-text unavailable: {exc}") from exc
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    try:
        ctx = json.loads(context) if context else {}
    except json.JSONDecodeError as exc:
        raise HTTPException(400, "context must be JSON") from exc
    result = await agent.handle_command(session_id, transcript, ctx)
    extra = await _spoken_fields(result.get("reply", "")) if tts else _NO_AUDIO
    return {"transcript": transcript, **extra, **result}


_NO_AUDIO = {"audio_b64": None, "audio_mime": None}


async def _spoken_fields(reply: str) -> dict:
    """`audio_b64`/`audio_mime` for a spoken reply, or nulls + `tts_error` when TTS fails."""
    try:
        wav_bytes, mime = await voice.speak(reply)
    except voice.VoiceError as exc:
        return {**_NO_AUDIO, "tts_error": str(exc)}
    return {"audio_b64": base64.b64encode(wav_bytes).decode(), "audio_mime": mime}


@app.post("/agent/observe")
async def agent_observe(body: tape_survey.Observation) -> dict:
    """The headset reports what its tools measured for an agent `survey` / `check_slope` action
    (brain.md S1): size groups + spoken summary, or the drainage verdict. Same response shape as
    /agent/command; `tts: true` adds the spoken reply as audio, like /voice/command."""
    result = agent.observe(body)
    if body.tts:
        result.update(await _spoken_fields(result["reply"]))
    return result


class SpeakRequest(BaseModel):
    text: str


@app.post("/voice/speak")
async def voice_speak(body: SpeakRequest) -> Response:
    try:
        wav_bytes, mime = await voice.speak(body.text)
    except voice.VoiceError as exc:
        raise HTTPException(503, str(exc)) from exc
    return Response(content=wav_bytes, media_type=mime)


@app.post("/voice/token")
async def voice_token() -> dict:
    try:
        return await voice.realtime_token()
    except voice.VoiceError as exc:
        raise HTTPException(503, str(exc)) from exc


@app.websocket("/voice/realtime")
async def voice_realtime(ws: WebSocket, session_id: str = "default") -> None:
    """Push-to-talk speech-to-speech relay to xAI realtime (docs/api.md `WS /voice/realtime`)."""
    await ws.accept()
    await realtime.bridge(ws, session_id[:128])


@app.get("/realtime")
def realtime_console() -> FileResponse:
    """Browser test page for WS /voice/realtime (mic in, reply audio out, action log)."""
    return FileResponse(_STATIC_DIR / "realtime.html", media_type="text/html")


# --- installer intel ("who installs this near me?") --------------------------


class InstallersRequest(BaseModel):
    query: str
    location: str | None = None  # default: Settings.DEFAULT_LOCATION


@app.post("/intel/installers")
async def intel_installers(body: InstallersRequest) -> intel.InstallerIntel:
    """Synchronous: ~13 s live (one xAI Responses call, web + X search), instant when cached."""
    query = body.query.strip()[:200]
    if not query:
        raise HTTPException(400, "query is required")
    location = (body.location or "").strip()[:200] or get_settings().DEFAULT_LOCATION
    try:
        return await intel.find_installers(query, location)
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except (RuntimeError, httpx.HTTPError) as exc:
        logger.warning("intel/installers failed: %s", exc)
        raise HTTPException(502, "installer search failed") from exc


# --- rules and money check (permits, code editions, rebates) ----------------


class RulesCheckRequest(BaseModel):
    part_id: str | None = None
    bom: list[str] = []  # other part ids in the cart, for the ENERGY STAR indoor/outdoor pair
    job: str | None = None  # a rules.JOBS key; default: read from the part's name
    address: str | None = None  # street address -> Census jurisdiction
    location: str | None = None  # "City, ST"; default Settings.DEFAULT_LOCATION without address


@app.post("/rules/check")
async def rules_check(body: RulesCheckRequest) -> dict:
    """Background job: ~15-40 s cold (two Grok calls), done at once when cached."""
    if body.job is not None and body.job not in rules.JOBS:
        raise HTTPException(400, f"unknown job, want one of {sorted(rules.JOBS)}")
    parts = []
    for part_id in [p for p in (body.part_id, *body.bom) if p]:
        if not _PART_ID_RE.match(part_id):
            raise HTTPException(400, "invalid part id")
        part = jobs.load_part(part_id)
        if part is None:
            raise HTTPException(404, f"unknown part {part_id!r}")
        parts.append(part)
    if body.job is None and not parts:
        raise HTTPException(400, "need a part_id or a job")
    address = (body.address or "").strip()[:200] or None
    location = (body.location or "").strip()[:200] or None
    try:
        juris = await rules.jurisdiction(
            address, location or (None if address else get_settings().DEFAULT_LOCATION)
        )
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except rules.NoMatch as exc:
        raise HTTPException(422, str(exc)) from exc
    job = rules.start(juris, body.job or rules.job_for(parts[0].name), parts)
    return rules.body(await jobs.wait(job.id, 0.5))


@app.get("/rules/check/{check_id}")
def get_rules_check(check_id: str) -> dict:
    job = jobs.get(check_id)
    if job is None or job.stage != rules.STAGE:
        raise HTTPException(404, "unknown check")
    return rules.body(job)


# --- paper-quote checker (F20) -----------------------------------------------


@app.post("/quote/check")
async def quote_check(
    file: UploadFile = File(...),  # noqa: B008 - fastapi's documented pattern
    address: str | None = Form(None),
    location: str | None = Form(None),
) -> dict:
    """A photo or PDF of a contractor's quote: ~6 s live read (one Grok vision call), instant
    when this file was read before; the F13/F6 checks come from their caches when warm."""
    data = await _read_capped(_iter_upload(file), quote.MAX_BYTES)
    try:
        return await quote.check(
            data, (address or "").strip()[:200] or None, (location or "").strip()[:200] or None
        )
    except quote.BadQuote as exc:
        raise HTTPException(400, str(exc)) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline and this quote wasn't read before") from exc
    except quote.ReadFailed as exc:
        logger.warning("quote/check failed: %s", exc)
        raise HTTPException(502, "couldn't read the quote") from exc


# --- scene vision ----------------------------------------------------------


class SceneAskRequest(BaseModel):
    question: str
    frames: list[vision.Frame]


@app.post("/scene/ask")
async def scene_ask(request: Request) -> vision.SceneAnswer:
    body = await _read_capped(request.stream(), _SCENE_ASK_MAX_BYTES)
    try:
        req = SceneAskRequest.model_validate_json(body)
    except ValidationError as exc:
        raise HTTPException(400, str(exc)) from exc
    try:
        return await vision.ask_scene(req.question, req.frames)
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc


# --- scene reimagine (Grok Imagine edit of a capture frame) -------------------


class ReimagineRequest(BaseModel):
    site: str
    frame_id: str
    prompt: str
    resolution: Literal["1k", "2k"] = "1k"
    session_id: str | None = None  # also start this agent session's refine stack (F18)


@app.post("/scene/reimagine")
async def scene_reimagine(req: ReimagineRequest) -> dict:
    try:
        out = await imagine.reimagine(req.site, req.frame_id, req.prompt, resolution=req.resolution)
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except imagine.ModerationBlocked as exc:
        raise HTTPException(422, f"blocked by moderation: {exc}") from exc
    except imagine.RateLimited as exc:
        raise HTTPException(429, str(exc)) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except imagine.ImagineError as exc:
        raise HTTPException(502, str(exc)) from exc
    if req.session_id:
        agent.remember_reimagine(req.session_id, req.site, req.prompt, out)
    return out


@app.get("/scene/reimagine/{image_id}.jpg")
def get_reimagined(image_id: str) -> FileResponse:
    path = imagine.image_path(image_id)
    if path is None or not path.is_file():
        raise HTTPException(404, "not found")
    return FileResponse(path, media_type="image/jpeg")


# --- site-walk report (server/report.py) --------------------------------------------------


async def _report_or_404(session_id: str) -> report.Report:
    result = await report.build_report(session_id)
    if result is None:
        raise HTTPException(404, "no notebook for that session")
    return result


# Registered before /report/{session_id}: that pattern would otherwise also match "x.json".
@app.get("/report/{session_id}.json")
async def get_report_json(session_id: str) -> report.Report:
    return await _report_or_404(session_id)


@app.get("/report/{session_id}")
async def get_report(session_id: str) -> HTMLResponse:
    return HTMLResponse(report.render_html(await _report_or_404(session_id)))


@app.post("/report/{session_id}/narrate")
async def narrate_report(session_id: str) -> Response:
    try:
        audio, mime = await report.narrate(await _report_or_404(session_id))
    except voice.VoiceError as exc:
        raise HTTPException(503, str(exc)) from exc
    return Response(content=audio, media_type=mime)


# --- "see it installed" postcard (Grok Imagine edit, g1-imagine.md §3 idea 2) --------------

# One base64 site frame (<= vision.MAX_FRAME_BYTES) plus a few short fields.
_POSTCARD_MAX_BYTES = vision.MAX_FRAME_BYTES * 4 // 3 + 4096


class PostcardRequest(BaseModel):
    site: str | None = None  # scene package + frame id ...
    frame_id: str | None = None
    frame_jpg_b64: str | None = None  # ... or the headset's own frame
    placement: str = ""  # e.g. "on the fascia, left of the downspout"
    box: list[float] | None = None  # placed part's screen box [x0,y0,x1,y1], normalized 0..1


@app.post("/parts/{part_id}/postcard")
async def part_postcard(part_id: str, request: Request) -> dict:
    """Direct (not a job): ~10 s, like /scene/ask. Fire it when the seller panel opens."""
    if not _PART_ID_RE.match(part_id):
        raise HTTPException(400, "invalid part id")
    body = await _read_capped(request.stream(), _POSTCARD_MAX_BYTES)
    try:
        req = PostcardRequest.model_validate_json(body)
    except ValidationError as exc:
        raise HTTPException(400, str(exc)) from exc
    part = jobs.load_part(part_id)
    if part is None:
        raise HTTPException(404, "unknown part")
    try:
        jpg, before_url = postcard.load_site(req.site, req.frame_id, req.frame_jpg_b64)
        return await postcard.make_postcard(
            part, jpg, placement=req.placement, box=req.box, before_url=before_url
        )
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except imagine.ModerationBlocked as exc:
        raise HTTPException(422, f"blocked by moderation: {exc}") from exc
    except imagine.RateLimited as exc:
        raise HTTPException(429, str(exc)) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except imagine.ImagineError as exc:
        raise HTTPException(502, str(exc)) from exc


# --- scene planning ("where does it go?") -----------------------------------------------


class ScenePlanRequest(BaseModel):
    site: str
    utterance: str
    context: dict = {}  # pointer ([x, y, z], structure frame), selected_part_id, dims_mm


@app.post("/scene/plan")
async def scene_plan(req: ScenePlanRequest) -> dict:
    if not _SITE_RE.match(req.site):
        raise HTTPException(400, "invalid site")
    try:
        return await plan.plan(req.site, req.utterance[: agent.MAX_TEXT_LEN], req.context)
    except plan.PlanError as exc:
        raise HTTPException(exc.status, exc.spoken) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc


# --- scene flythrough (walk-in renovation clip, Grok Imagine video) ----------


class FlythroughRequest(BaseModel):
    site: str
    frame_id: str  # the capture frame the user looks through = the clip's (reimagined) last frame
    prompt: str
    duration_s: int = Field(3, ge=1, le=15)
    resolution: Literal["480p", "720p"] = "480p"
    public: bool = False  # also store it on xAI Files with a public URL (share_url, for a QR)


@app.post("/scene/flythrough")
async def scene_flythrough(req: FlythroughRequest) -> dict:
    try:
        return await flythrough.start(
            req.site,
            req.frame_id,
            req.prompt,
            duration_s=req.duration_s,
            resolution=req.resolution,
            public=req.public,
        )
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except imagine.ModerationBlocked as exc:
        raise HTTPException(422, f"blocked by moderation: {exc}") from exc
    except imagine.RateLimited as exc:
        raise HTTPException(429, str(exc)) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline") from exc
    except imagine.ImagineError as exc:
        raise HTTPException(502, str(exc)) from exc


def _flythrough_file(clip_id: str, ext: str, media_type: str) -> FileResponse:
    path = flythrough.clip_path(clip_id, ext)
    if path is None or not path.is_file():
        raise HTTPException(404, "not found")
    return FileResponse(path, media_type=media_type)


# registered before /scene/flythrough/{job_id}, which would otherwise swallow "<id>.mp4"
@app.get("/scene/flythrough/{clip_id}.mp4")
def get_flythrough_video(clip_id: str) -> FileResponse:
    return _flythrough_file(clip_id, "mp4", "video/mp4")


@app.get("/scene/flythrough/{clip_id}.jpg")
def get_flythrough_poster(clip_id: str) -> FileResponse:
    return _flythrough_file(clip_id, "jpg", "image/jpeg")


@app.get("/scene/flythrough/{job_id}")
def get_flythrough_job(job_id: str) -> dict:
    job = jobs.get(job_id)
    if job is None or job.stage != "flythrough":
        raise HTTPException(404, "unknown job")
    status = "pending" if job.status == "running" else job.status
    body = {**(job.result or {}), "job_id": job.id, "status": status}
    if job.status == "failed":
        body["error"] = job.error
        body["spoken"] = flythrough.FAILED_LINE
    return body


# --- scene condition survey (docs/research/grok-ideas/g4-round3.md pick 1) ------------------


class SurveyRequest(BaseModel):
    site: str
    frames: list[str] | None = None
    model: Literal["fast", "careful"] = "fast"
    n: int = Field(6, ge=1, le=6)
    focus: Literal["roof", "gutters", "facade", "all"] = "all"


@app.post("/scene/survey")
async def scene_survey(req: SurveyRequest) -> dict:
    """Start a survey job; a cached survey comes back `done` at once."""
    try:
        plan = survey.plan(req.site, req.frames, req.model == "careful", req.n, req.focus)
    except LookupError as exc:
        raise HTTPException(404, str(exc)) from exc
    except survey.NotReady as exc:
        raise HTTPException(
            409, {"error": "scene has no cameras or collision mesh", "spoken": str(exc)}
        ) from exc
    hit = survey.is_cached(plan)
    if not hit and get_settings().OFFLINE:
        raise HTTPException(503, "offline and no cached survey for this scene")
    job = jobs.start_task("survey", survey.run(plan))
    return _survey_body(await jobs.wait(job.id, 30 if hit else 0.5) or job)


@app.get("/scene/survey/{survey_id}")
def get_survey(survey_id: str) -> dict:
    job = jobs.get(survey_id)
    if job is None or job.stage != "survey":
        raise HTTPException(404, "unknown survey")
    return _survey_body(job)


def _survey_body(job) -> dict:
    """`{survey_id, status: running|done|failed, **result}` (+ `error` when failed)."""
    body = {"survey_id": job.id, "status": job.status, **(job.result or {})}
    if job.status == "failed":
        body["error"] = job.error
    return body


# --- job packet (server/packet.py) --------------------------------------------------------


class PacketRequest(BaseModel):
    days: int = packet.DEFAULT_DAYS
    public: bool = True


# Registered before POST /packet/{session_id}; the GET below is LAN-only, never the public link.
@app.get("/packet/{packet_id}.pdf")
def get_packet_pdf(packet_id: str) -> FileResponse:
    path = packet.pdf_path(packet_id)
    if path is None or not path.is_file():
        raise HTTPException(404, "unknown packet")
    return FileResponse(path, media_type="application/pdf")


@app.post("/packet/{session_id}")
async def make_packet(session_id: str, body: PacketRequest | None = None) -> dict:
    body = body or PacketRequest()
    try:
        record = await packet.make(session_id, body.days, body.public)
    except packet.PublishError as exc:
        raise HTTPException(502, exc.record) from exc
    if record is None:
        raise HTTPException(404, "no notebook for that session")
    return record


@app.delete("/packet/{packet_id}")
async def revoke_packet(packet_id: str) -> dict:
    record = packet.load(packet_id)
    if record is None:
        raise HTTPException(404, "unknown packet")
    if record["public"] and get_settings().OFFLINE:
        raise HTTPException(503, "offline: can't reach xAI to revoke the link")
    try:
        return await packet.take_down(record)
    except (httpx.HTTPError, RuntimeError) as exc:
        raise HTTPException(502, f"revoke failed: {exc}") from exc


# --- "do the whole job" (server/runjob.py) ------------------------------------------------


class JobRunRequest(BaseModel):
    session_id: str
    site: str | None = None  # a scanned scene: the chain starts with its condition survey
    goal: str | None = None  # the user's stated need ("a ductless mini-split"): skips the survey
    context: dict = {}  # the agent context: frame_id / frame_jpg_b64, address, location


@app.post("/job/run")
async def job_run(body: JobRunRequest) -> dict:
    """Starts the chain; poll `GET /job/run/{run_id}?after=n` for its steps and actions."""
    site = body.site or body.context.get("site")
    if site is not None and not _SITE_RE.match(str(site)):
        raise HTTPException(400, "invalid site")
    session = agent._get_session(body.session_id[:128])
    try:
        run = runjob.start(session, site, body.goal, body.context)
    except runjob.Busy as exc:
        raise HTTPException(409, str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    return {"run_id": run.id, "steps": run.actions[0]["args"]["steps"]}


@app.get("/job/run/{run_id}")
def job_run_status(run_id: str, after: int = 0) -> dict:
    run = runjob.get(run_id)
    if run is None:
        raise HTTPException(404, "unknown run")
    return runjob.body(run, after)


class JobCancelRequest(BaseModel):
    session_id: str | None = None  # when sent, it must be the run's own session
    reason: str | None = None  # "switched to zabel-gymnasium" (logged, and in job_done)


@app.post("/job/run/{run_id}/cancel")
async def job_run_cancel(run_id: str, body: JobCancelRequest | None = None) -> dict:
    """job-cancel: stop a running run ("do the whole job" or a replace) now: its chain task is
    cancelled, it ends `cancelled`, and its session can start a new job at once (the headset calls
    this when the user switches to another model mid-run). Idempotent: a run that already ended
    answers with its status and `cancelled: false`. async: task.cancel() must run on the loop."""
    run = runjob.get(run_id)
    if run is None:
        raise HTTPException(404, "unknown run")
    sid = body.session_id if body is not None else None
    if sid and run.session_id not in (sid, sid[:128]):
        raise HTTPException(403, "not this session's run")
    done = runjob.cancel(run, body.reason if body is not None else None)
    return {"run_id": run.id, "status": run.status, "cancelled": done, "next": len(run.actions)}


# --- live labels (docs/research/grok-ideas/g6-round5.md pick 1) ----------------------------


@app.post("/scene/labels")
async def scene_labels(
    session_id: str = Form("default"),
    image: UploadFile | None = File(None),  # noqa: B008 - fastapi's documented pattern
    site: str | None = Form(None),
    frame_id: str | None = Form(None),
    focus: str | None = Form(None),
) -> dict:
    """Label one frame: an uploaded JPEG, or a scan thumb by `site` + `frame_id`."""
    if image is not None:
        jpg = await _read_capped(_iter_upload(image), vision.MAX_FRAME_BYTES)
        frame_id = frame_id or image.filename or "upload"
    elif site and frame_id:
        path = _safe_scene_path(site, f"thumbs/{frame_id}.jpg")
        if path is None or not path.is_file():
            raise HTTPException(404, "unknown frame")
        jpg = path.read_bytes()
    else:
        raise HTTPException(400, "send an image, or a site and frame_id")
    try:
        labels.take(session_id)
        result = await labels.label_frame(jpg, (focus or "")[:80] or None)
    except labels.RateLimited as exc:
        retry = max(1, round(exc.retry_after_s))
        raise HTTPException(
            429, {"error": str(exc), "retry_after_s": retry}, {"Retry-After": str(retry)}
        ) from exc
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except cache.OfflineMiss as exc:
        raise HTTPException(503, "offline and this frame was never labelled") from exc
    except openai.APIError as exc:
        logger.warning("labels: xAI failed: %s", exc)
        raise HTTPException(502, "the labelling model failed") from exc
    return {"frame_id": frame_id, **result}


# --- booth wall (server/booth.py) ---------------------------------------------------------


class ShareRequest(BaseModel):
    consent: bool = False  # the user asked for the wall; nothing goes up without it
    name: str | None = None  # "Judge <n>" when unset


class ConfirmRequest(BaseModel):
    confirm_token: str  # from the share's `x`, sent by the headset's hold-to-post gesture


@app.get("/booth")
def booth_screen() -> HTMLResponse:
    return HTMLResponse(booth.render_html(booth.load_entries()))


@app.get("/booth.json")
def booth_json() -> dict:
    entries = booth.load_entries()
    return {
        "entries": entries,
        "leaderboard": booth.leaderboard(entries),
        "x_needs": booth.x_missing(),
    }


@app.get("/booth/cards/{entry_id}.jpg")
def booth_card(entry_id: str) -> FileResponse:
    path = booth.card_path(entry_id)
    if path is None or not path.is_file():
        raise HTTPException(404, "unknown entry")
    return FileResponse(path, media_type="image/jpeg")


@app.get("/booth/x/login")
def booth_x_login(request: Request) -> RedirectResponse:
    if missing := [v for v in booth.x_missing() if v != "X_REFRESH_TOKEN"]:
        raise HTTPException(503, f"X not configured: set {', '.join(missing)}")
    return RedirectResponse(booth.login_url(str(request.url_for("booth_x_callback"))))


@app.get("/booth/x/callback")
async def booth_x_callback(
    request: Request, code: str = "", state: str = "", error: str = ""
) -> HTMLResponse:
    if error or not code:
        raise HTTPException(400, f"X login failed: {error or 'no code'}")
    try:
        await booth.finish_login(state, code, str(request.url_for("booth_x_callback")))
    except booth.XError as exc:
        raise HTTPException(exc.status, exc.detail) from exc
    return HTMLResponse(
        "<p>X login done. The refresh token is saved in <code>DATA_DIR/booth/x_token.json</code>"
        " (the server rotates it there). You can close this tab.</p>"
    )


@app.post("/booth/x/confirm")
async def booth_x_confirm(body: ConfirmRequest) -> dict:
    try:
        return await booth.confirm(body.confirm_token)
    except booth.XError as exc:
        raise HTTPException(exc.status, exc.detail) from exc


@app.post("/booth/{session_id}/share")
async def booth_share(session_id: str, body: ShareRequest) -> dict:
    try:
        result = await booth.share(session_id, body.name, body.consent)
    except PermissionError as exc:
        raise HTTPException(403, str(exc)) from exc
    if result is None:
        raise HTTPException(404, "no notebook for that session")
    return result


@app.delete("/booth/{entry_id}")
def booth_unshare(entry_id: str) -> dict:
    entry = booth.unshare(entry_id)
    if entry is None:
        raise HTTPException(404, "unknown entry")
    return {"removed": entry_id, "tweet_id": entry.get("tweet_id")}
