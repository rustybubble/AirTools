"""A catalog that fits the place: `GET /catalog` and `GET /catalog/search` (catalog lane).

`GET /catalog?site=` answers what kind of place the scan is and a few product categories for it,
each with up to `MAX_ITEMS` parts known locally:

1. Environment (`detect`). Cheap signals first, all from the scene package and the data dir: the
   parts file's components (dw1 dishwasher -> kitchen), the structure's objects (windows, cabinet
   doors) and planes (large sloped planes: a pitched roof; large flat planes well above the
   lowest: a flat roof), the pre-labelled anchors (`labels.scene_labels`) and the site's name.
   Then one Grok vision call on two thumbnails (`ask_place`, role `catalog`) names the place and
   may add up to 2 site-specific categories. The call is cached per site revision + frames, and
   the whole detection is kept in `<DATA_DIR>/catalog/<site>.r<rev>.json`: once it is there no
   request asks again. The first request for a new site waits up to `GROK_WAIT_S` for it.
2. Categories (`categories_for`): the environment's curated list (server/catalog_tables.py), the
   ones the scene shows first ("in_scene": a component or a label names them), then Grok's extras.
3. Items (`catalog_index.INDEX`): parts on disk and cached search results, classified into
   categories. A category with fewer than `MIN_ITEMS` gets its query searched in the background
   (one at a time, `SearchRequest.lite`); its first `MODELS_PER_CATEGORY` parts get their model
   built (assets.resolve_shared) and every listed part its photo, so "3D" badges and images fill
   in on the next poll. `pending` says how many searches are still running.

`GET /catalog/search?q=` is the lazy search behind the catalog's keyboard: the in-memory index
only, no LLM, no web (the app's Enter runs the live `/parts/search`).

`python -m server.warm --catalog` runs all of it for every site before a demo (`warm`).
"""

import asyncio
import hashlib
import json
import logging
import re
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Literal

import numpy as np
import openai
from pydantic import BaseModel, ValidationError

from server import assets, cache, jobs, labels, llm, search
from server import catalog_tables as tables
from server.catalog_index import INDEX, Doc
from server.config import get_settings
from server.meshgen import photo_data_url
from server.models import Part, SearchRequest

logger = logging.getLogger(__name__)

VERSION = 3  # bump when detection rules change: stored detections are then recomputed
MAX_ITEMS = 8
MIN_ITEMS = 3  # fewer -> the category's query is searched in the background
MODELS_PER_CATEGORY = 3
FOCUS_SEARCH_LEAD = 6  # a focused list searches only its first thin categories (SerpApi credit)
GROK_WAIT_S = 8.0
RETRY_S = 600.0  # a failed background search may run again after this
EMPTY_S = 86400.0  # a search that found nothing is not repeated for a day (kept on disk)
THUMB_PX = 640
_SITE_RE = re.compile(r"^[a-z0-9-]+$")

# --- the scene --------------------------------------------------------------------------------


def _site_dir(site: str) -> Path:
    return Path(get_settings().SCENE_DIR) / site


def load_scene(site: str) -> dict:
    """scene.json. LookupError: not a served site."""
    path = _site_dir(site) / "scene.json"
    if not _SITE_RE.match(site or "") or not path.is_file():
        raise LookupError(f"unknown site {site!r}")
    return json.loads(path.read_text())


def _read(site: str, name: str | None) -> dict | list | None:
    if not name:
        return None
    try:
        return json.loads((_site_dir(site) / name).read_text())
    except (OSError, ValueError):
        return None


def scene_things(site: str, meta: dict) -> list[dict]:
    """What the scene says is in it: parts-file components, structure objects, labels.
    [{"source", "label", "id"}]."""
    things = []
    parts = _read(site, (meta.get("parts") or {}).get("file"))
    for c in (parts or {}).get("components") or []:
        if c.get("label"):
            things.append({"source": "parts", "label": str(c["label"]), "id": c.get("id")})
    structure = _read(site, (meta.get("structure") or {}).get("file"))
    for o in (structure or {}).get("objects") or []:
        if o.get("label"):
            label = str(o["label"]).replace("_", " ")
            things.append({"source": "objects", "label": label, "id": o.get("id")})
    anchors = labels.scene_labels(site) or {}
    for a in anchors.get("labels") or []:
        if a.get("name"):
            things.append({"source": "labels", "label": str(a["name"]), "id": a.get("id")})
    return things


def _plane_rows(structure: dict) -> list[tuple[float, float, float]]:
    """(|normal.y|, area m², mean height) per plane with a 3D polygon."""
    rows = []
    for p in structure.get("planes") or []:
        try:
            poly = np.asarray(p.get("polygon") or p.get("polygon3d"), float)
            n = np.asarray(p["normal"], float)
        except (KeyError, TypeError, ValueError):
            continue
        if poly.ndim != 2 or poly.shape[1] != 3 or len(poly) < 3 or not np.linalg.norm(n):
            continue
        n = n / np.linalg.norm(n)
        area = p.get("area_m2")
        if area is None:
            area = float(np.linalg.norm(np.cross(poly, np.roll(poly, -1, axis=0)).sum(0)) / 2)
        rows.append((abs(float(n[1])), float(area), float(poly[:, 1].mean())))
    return rows


def geometry_signals(structure: dict | None) -> list[dict]:
    """Roofs from the plane layout. A pitched roof: sloped planes (20-70° off level) are a
    quarter of the plane area. A flat roof: large level planes 3 m or more above the lowest
    level plane (a drone over a building)."""
    rows = _plane_rows(structure or {})
    total = sum(a for _, a, _ in rows)
    if not rows or total <= 0:
        return []
    out = []
    sloped = sum(a for ny, a, _ in rows if 0.35 <= ny < 0.95)
    if sloped >= 0.25 * total and sloped >= 20:
        out.append(
            _signal(
                "structure",
                "rooftop",
                2.0,
                f"sloped planes {sloped:.0f} m² of {total:.0f} m²: a pitched roof",
            )
        )
    level = [(a, y) for ny, a, y in rows if ny >= 0.95]
    if level:
        low = min(y for _, y in level)
        high = sum(a for a, y in level if y - low >= 3.0)
        if high >= 30 and high >= 0.2 * sum(a for a, _ in level):
            out.append(
                _signal(
                    "structure",
                    "rooftop",
                    1.5,
                    f"level planes {high:.0f} m² at 3 m+ above the lowest: flat roofs",
                )
            )
    return out


def _signal(source: str, env: str, weight: float, why: str) -> dict:
    return {"source": source, "env": env, "weight": round(weight, 2), "why": why}


# Per source: a cap on its total vote per environment, and a scale on each thing's weight. The
# structure layer's rectangles ("cabinet door" x40) are many and weak; parts and labels count
# each thing as named.
_CAPS = {"parts": 4.5, "objects": 1.5, "labels": 2.0}
_SCALE = {"parts": 1.0, "objects": 0.25, "labels": 1.0}


def cheap_signals(site: str, meta: dict, things: list[dict]) -> list[dict]:
    out = [
        _signal("name", env, w, f"'{word}' in the site name")
        for env, w, word in tables.name_votes(meta.get("name") or site)
    ]
    for source in ("parts", "objects", "labels"):
        per_env: dict[str, tuple[float, list[str]]] = {}
        seen_labels = set()
        for t in things:
            if t["source"] != source:
                continue
            label = t["label"].lower()
            if source == "labels" and label in seen_labels:
                continue  # a label name counts once, however many anchors carry it
            seen_labels.add(label)
            for env, w in tables.env_votes_for_thing(label):
                total, why = per_env.get(env, (0.0, []))
                per_env[env] = (total + w * _SCALE[source], why + [label])
        for env, (total, why) in per_env.items():
            names = ", ".join(sorted(set(why))[:6])
            out.append(_signal(source, env, min(total, _CAPS[source]), names))
    structure = _read(site, (meta.get("structure") or {}).get("file"))
    out += geometry_signals(structure if isinstance(structure, dict) else None)
    return out


# --- Grok: what kind of place is this? --------------------------------------------------------

ENV_IDS = tuple(tables.ENVIRONMENTS)
EnvId = Literal[ENV_IDS]  # type: ignore[valid-type]


class Extra(BaseModel):
    title: str
    query: str
    icon: str


class Place(BaseModel):
    environment: EnvId  # type: ignore[valid-type]
    confidence: float  # 0-1; no schema bounds (strict json_schema modes reject min/max)
    setting: Literal["indoor", "outdoor", "aerial"]
    place: str
    extras: list[Extra]


PROMPT = """You set up a hardware-store catalog for someone standing inside a 3D scan of a real \
place. The images are {n} frames from the scan.{hints}
Which kind of place is it? Pick "environment" from:
{envs}
"setting": indoor, outdoor or aerial (a drone above it). "place": at most 6 words naming what you \
see, e.g. "pitched tile roof with dormers". "confidence": 0-1.
"extras": at most 2 more product categories for installed fixtures, equipment or building \
parts you can see in the images that your environment's list lacks, e.g. polycarbonate roof \
panels on a pavilion, a rooftop exhaust fan on a flat roof, a pendant light over a kitchen \
island. Never loose items, supplies, cleaning products, decor, signs or furniture that is not \
built in. Each has "title" (2-3 words, plural), "query" (a short product search at a hardware \
store) and "icon" (one of: {icons}). Leave it empty unless one clearly stands out."""


def _prompt(hints: str) -> str:
    envs = "\n".join(
        f"- {e.id}: {e.about}; the catalog lists "
        + ", ".join(tables.CATEGORIES[c].title for c in e.categories)
        for e in tables.ENVIRONMENTS.values()
    )
    return PROMPT.format(
        n=2,
        hints=f" {hints}" if hints else "",
        envs=envs,
        icons=", ".join(sorted(tables.ICON_CODEPOINTS)),
    )


def _hints(things: list[dict]) -> str:
    bits = []
    for source, lead in (
        ("parts", "Parts cut out of the scan"),
        ("labels", "Labels seen in it"),
        ("objects", "Rectangles found on its walls"),
    ):
        names = list(dict.fromkeys(t["label"].lower() for t in things if t["source"] == source))
        if names:
            bits.append(f"{lead}: {', '.join(names[:15])}.")
    return " ".join(bits)


def pick_thumbs(site: str, meta: dict, n: int = 2) -> list[Path]:
    """`n` thumbnails spread over the flight (a third and two thirds of the way)."""
    base = _site_dir(site)
    cams = _read(site, meta.get("cameras"))
    paths = []
    if isinstance(cams, list):
        paths = [base / (c.get("thumb") or f"thumbs/{c.get('id')}.jpg") for c in cams]
    if not paths and (base / "thumbs").is_dir():
        paths = sorted((base / "thumbs").glob("*.jpg"))
    paths = [p for p in paths if p.is_file()]
    if not paths:
        return []
    picks = sorted({min(len(paths) - 1, (i + 1) * len(paths) // (n + 1)) for i in range(n)})
    return [paths[i] for i in picks]


async def ask_place(site: str, meta: dict, things: list[dict]) -> dict | None:
    """Grok's reading of the place (cached per site revision, frames, hints, prompt, model):
    {"place": Place dict, "model", "cost_usd", "latency_ms", "cached"}. None without frames or a
    Grok key. cache.OfflineMiss offline when never asked; openai / validation errors raise."""
    thumbs = pick_thumbs(site, meta)
    if not thumbs or not get_settings().GROK_API_KEY:
        return None
    hints = _hints(things)
    text = _prompt(hints)
    provider, model = llm.resolve_role("catalog")
    key = {
        "site": site,
        "rev": int(meta.get("revision", 1)),
        "frames": [hashlib.sha1(p.read_bytes()).hexdigest()[:16] for p in thumbs],
        "hints": hashlib.sha1(hints.encode()).hexdigest()[:12],
        "prompt": hashlib.sha1(text.encode()).hexdigest()[:8],
        "model": f"{provider}:{model}",
    }
    fresh = cache.get("catalog_place", key) is None

    async def call() -> dict:
        urls = [await asyncio.to_thread(photo_data_url, p, THUMB_PX) for p in thumbs]
        content = [{"type": "text", "text": text}] + [
            {"type": "image_url", "image_url": {"url": u, "detail": "high"}} for u in urls
        ]
        schema = llm._strict_schema(Place.model_json_schema())
        start = time.monotonic()
        resp = await llm.chat(
            "catalog",
            [{"role": "user", "content": content}],
            response_format={
                "type": "json_schema",
                "json_schema": {"name": "Place", "strict": True, "schema": schema},
            },
            temperature=0,
            max_tokens=400,
            max_retries=1,
            timeout=30,
        )
        place = Place.model_validate_json(resp.choices[0].message.content or "")
        return {
            "place": place.model_dump(),
            "model": f"{provider}:{model}",
            "cost_usd": round(llm.cost_usd(resp.usage) or 0.0, 5),
            "latency_ms": round((time.monotonic() - start) * 1000),
        }

    answer = await cache.cached("catalog_place", key, call)
    return {**answer, "cached": not fresh}


# --- detection ----------------------------------------------------------------------------------

_detections: dict[tuple[str, int], dict] = {}
_evidence: dict[tuple[str, int], tuple[list, list]] = {}  # (things, cheap signals) per revision
_place_tasks: dict[tuple[str, int], asyncio.Task] = {}


def _detection_path(site: str, rev: int) -> Path:
    return Path(get_settings().DATA_DIR) / "catalog" / f"{site}.r{rev}.json"


ALSO_RATIO = 0.5  # a runner-up environment this close to the winner adds its first categories
ALSO_MAX = 2


def combine(signals: list[dict], place: dict | None) -> tuple[str, float, list[dict], str | None]:
    """(environment, confidence, signals incl. Grok's, runner-up) from weighted votes. The
    runner-up is named when its score is at least ALSO_RATIO of the winner's (a drone scan of a
    building is roof and facade at once)."""
    signals = list(signals)
    if place is not None:
        conf = min(1.0, max(0.3, float(place.get("confidence") or 0)))
        signals.append(
            _signal(
                "grok",
                place["environment"],
                3.0 * conf,
                f"{place.get('place', '')} ({place.get('setting', '')})",
            )
        )
    score: dict[str, float] = {}
    for s in signals:
        if s["env"] in tables.ENVIRONMENTS:
            score[s["env"]] = score.get(s["env"], 0.0) + s["weight"]
    score.pop(tables.GENERIC, None)
    if not score:
        return tables.GENERIC, 0.0, signals, None
    env = max(score, key=lambda e: (score[e], e == (place or {}).get("environment")))
    rest = {e: v for e, v in score.items() if e != env}
    also = max(rest, key=rest.get) if rest else None
    if also is not None and rest[also] < ALSO_RATIO * score[env]:
        also = None
    return env, round(score[env] / sum(score.values()), 2), signals, also


def in_scene(things: list[dict]) -> dict[str, dict]:
    """category id -> {"components": [parts-file ids], "seen": [labels]} for what the scene
    shows (a dishwasher component -> dishwashers)."""
    out: dict[str, dict] = {}
    for t in things:
        cat = tables.classify(t["label"])
        if cat is None:
            continue
        entry = out.setdefault(cat, {"components": [], "seen": []})
        if t["source"] == "parts" and t.get("id"):
            entry["components"].append(t["id"])
        elif t["label"] not in entry["seen"]:
            entry["seen"].append(t["label"])
    return out


def _generic() -> dict:
    return {
        "site": None,
        "rev": 0,
        "environment": tables.GENERIC,
        "confidence": 0.0,
        "place": None,
        "setting": None,
        "signals": [],
        "extras": [],
        "in_scene": {},
        "grok": "none",
        "version": VERSION,
    }


def known_detection(site: str | None) -> dict | None:
    """A detection already made in this process (no disk, no awaits); for /catalog/search."""
    if not site:
        return _generic()
    return next((d for (s, _), d in _detections.items() if s == site), None)


async def detect(site: str | None, wait_s: float | None = GROK_WAIT_S) -> dict:
    """The site's environment. The first call for a revision waits up to `wait_s` for a Grok
    answer not cached yet (None: until it comes); after that the cheap signals answer alone, and
    a later call picks Grok's reply up without waiting. LookupError: unknown site."""
    if not site:
        return _generic()
    meta = load_scene(site)
    rev = int(meta.get("revision", 1))
    key = (site, rev)
    memo = _detections.get(key)
    if memo is not None and memo["grok"] != "pending":
        return memo
    stored = _detection_path(site, rev)
    if memo is None and stored.is_file():
        try:
            det = json.loads(stored.read_text())
            if det.get("version") == VERSION:
                det.update(grok="stored", grok_cost_usd=0.0)  # asked in an earlier run
                _detections[key] = det
                return det
        except (OSError, ValueError):
            pass
    if key not in _evidence:
        things = await asyncio.to_thread(scene_things, site, meta)
        _evidence[key] = (things, await asyncio.to_thread(cheap_signals, site, meta, things))
    things, signals = _evidence[key]

    task = _place_tasks.get(key)
    if task is None:
        task = asyncio.create_task(ask_place(site, meta, things))
        _place_tasks[key] = task
    grok, answer = "pending", None
    try:
        answer = await asyncio.wait_for(asyncio.shield(task), wait_s if memo is None else 0)
        grok = "none" if answer is None else ("cached" if answer["cached"] else "fresh")
    except TimeoutError:
        pass
    except cache.OfflineMiss:
        grok = "offline"
    except (openai.OpenAIError, ValidationError, ValueError, OSError) as exc:
        logger.warning("catalog: place call for %s failed: %s", site, exc)
        grok = "failed"
    place = (answer or {}).get("place")
    env, confidence, all_signals, also = combine(signals, place)
    det = {
        "site": site,
        "rev": rev,
        "environment": env,
        "confidence": confidence,
        "also": also,
        "place": (place or {}).get("place"),
        "setting": (place or {}).get("setting"),
        "signals": all_signals,
        "extras": (place or {}).get("extras") or [],
        "in_scene": in_scene(things),
        "grok": grok,
        "grok_cost_usd": (answer or {}).get("cost_usd") if grok == "fresh" else 0.0,
        "grok_ms": (answer or {}).get("latency_ms"),
        "version": VERSION,
    }
    _detections[key] = det
    if grok in ("cached", "fresh"):
        cache.write_json_atomic(stored, json.dumps(det, indent=1))
    return det


# --- categories -----------------------------------------------------------------------------------


def _evidence_and_extras(
    det: dict, ids: set[str]
) -> tuple[dict, list[tuple[tables.Category, str]]]:
    """(category id -> evidence, Grok's extras as (category, the extra's own words) not in
    `ids`). A listed category Grok names counts as seen; a known one it names from elsewhere is
    used as is; supplies and decor are dropped."""
    seen = {k: dict(v) for k, v in (det.get("in_scene") or {}).items()}
    named = []  # Grok's extras, as categories
    for x in (det.get("extras") or [])[:2]:
        title, query = str(x.get("title") or ""), str(x.get("query") or "")
        if not query.strip() or tables.not_an_extra(f"{title} {query}"):
            continue
        known = tables.classify(query) or tables.classify(title)
        if known in ids:
            entry = seen.setdefault(known, {"components": [], "seen": []})
            entry["seen"] = [*entry.get("seen", []), f"grok: {title}"]
            continue
        cat = (
            tables.CATEGORIES[known]
            if known
            else tables.extra_category(title, query, str(x.get("icon") or ""))
        )
        named.append((cat, f"{title} {query}"))
    return seen, named


def categories_for(det: dict, focus: str | None = None) -> list[tuple[tables.Category, dict]]:
    """The environment's categories, each with its evidence ({"components", "seen"} from the
    scene; "also": True for a runner-up's): what the scene shows first, then up to ALSO_MAX of
    the runner-up environment's, then Grok's extras. With a `focus` the environment's table has
    (focus_categories), that list instead."""
    if focus is not None:
        focused = focus_categories(det, focus)
        if focused is not None:
            return focused
    env = tables.ENVIRONMENTS[det["environment"]]
    listed = [tables.CATEGORIES[c] for c in env.categories]
    ids = {c.id for c in listed}
    seen, named = _evidence_and_extras(det, ids)

    def rank(pair):
        i, c = pair
        ev = seen.get(c.id) or {}
        return (0 if ev.get("components") else 1 if ev.get("seen") else 2, i)

    out = [(c, seen.get(c.id) or {}) for _, c in sorted(enumerate(listed), key=rank)]
    also = tables.ENVIRONMENTS.get(det.get("also") or "")
    if also is not None:
        pool = [tables.CATEGORIES[c] for c in also.categories if c not in ids]
        for _, c in sorted(enumerate(pool), key=rank)[:ALSO_MAX]:
            ids.add(c.id)
            out.append((c, {**(seen.get(c.id) or {}), "also": True}))
    for cat, _ in named:
        if cat.id not in ids:
            ids.add(cat.id)
            out.append((cat, seen.get(cat.id) or {}))
    return out


def focus_categories(det: dict, focus: str) -> list[tuple[tables.Category, dict]] | None:
    """What the wearer looks at (the app's gaze: roof, wall, ground, ceiling, counter, opening)
    orders and filters the site's catalog: the curated table for the environment's setting and
    that focus (catalog_tables.FOCUS_TABLES), in its order, then the site's Grok extras whose
    words put them there ("Red Tile Roofs" on the roof). None: no table for it (the caller shows
    the unfocused list)."""
    ids = tables.focus_ids(det.get("environment"), focus, det.get("setting"))
    if not ids:
        return None
    seen, named = _evidence_and_extras(det, set(ids))
    out = [(tables.CATEGORIES[c], {**(seen.get(c) or {}), "focus": True}) for c in ids]
    taken = set(ids)
    for cat, words in named:  # by the extra's own words: "Dormer Windows" is on the roof
        if cat.id not in taken and focus in tables.foci_of_text(words):
            taken.add(cat.id)
            out.append((cat, {**(seen.get(cat.id) or {}), "focus": True}))
    return out


def foci(det: dict) -> list[str]:
    """The foci the site's catalog answers (empty: it has no focus table)."""
    return tables.foci_for(det.get("environment"), det.get("setting"))


# --- background work: category searches, models, photos ---------------------------------------

_search_queue: list[str] = []
_search_state: dict[str, tuple[str, float]] = {}  # normalized query -> (state, when)
_search_worker: asyncio.Task | None = None
_model_queue: list[str] = []
_model_seen: set[str] = set()
_model_worker: asyncio.Task | None = None
_image_queue: list[tuple[str, str]] = []
_image_seen: set[str] = set()
_image_worker: asyncio.Task | None = None


def reset() -> None:
    """Forget every in-process queue and detection (tests)."""
    global _search_worker, _model_worker, _image_worker
    for q in (_search_queue, _model_queue, _image_queue):
        q.clear()
    for d in (_search_state, _detections, _evidence, _place_tasks):
        d.clear()
    _model_seen.clear()
    _image_seen.clear()
    _search_worker = _model_worker = _image_worker = None


def search_state(query: str) -> str | None:
    state = _search_state.get(cache.normalize(query))
    return state[0] if state else None


def is_pending(query: str) -> bool:
    return search_state(query) in ("queued", "running")


def searched_already(query: str) -> bool:
    """Its answer is cached, or it found nothing within EMPTY_S."""
    q = cache.normalize(query)
    if cache.get("search_by_query", q) is not None:
        return True
    empty = cache.get("catalog_empty", q)
    return bool(empty) and time.time() - float(empty.get("at", 0)) < EMPTY_S


def schedule_search(query: str) -> bool:
    """Queue a background search for a category's query. False when offline, already cached,
    or tried in the last RETRY_S."""
    global _search_worker
    q = cache.normalize(query)
    state, when = _search_state.get(q, (None, 0.0))
    if state in ("queued", "running"):
        return True
    if get_settings().OFFLINE or searched_already(query):
        return False
    if state is not None and time.monotonic() - when < RETRY_S:
        return False
    _search_state[q] = ("queued", time.monotonic())
    _search_queue.append(query)
    if _search_worker is None or _search_worker.done():
        _search_worker = asyncio.create_task(_drain_searches())
    return True


async def _drain_searches() -> None:
    while _search_queue:
        query = _search_queue.pop(0)
        try:
            await run_search(query)
        except Exception:
            logger.exception("catalog: search %r failed", query)


async def run_search(query: str, background: bool = True) -> list[Part]:
    """One catalog search (the live pipeline, lite sellers), saved like a job's candidates and
    added to the index. `background`: queue the first parts' models and every photo (the warm
    builds them itself)."""
    q = cache.normalize(query)
    _search_state[q] = ("running", time.monotonic())
    req = SearchRequest(query=query, max_candidates=get_settings().CATALOG_SEARCH_N, lite=True)
    try:
        parts = await search.find_parts(req)
    except BaseException:
        _search_state[q] = ("failed", time.monotonic())
        raise
    saved = []
    for part in parts:
        part.asset.status = "pending"
        jobs.save_part(part)  # keeps an already-built model (jobs.save_part)
        saved.append(jobs.load_part(part.id) or part)
    INDEX.add_parts([p.model_dump(mode="json") for p in saved], query=query, from_part_json=True)
    _search_state[q] = ("done" if parts else "empty", time.monotonic())
    if not parts:  # find_parts caches no empty answer; remember it here, so it isn't re-bought
        cache.put("catalog_empty", q, {"at": time.time()})
    logger.info("catalog: searched %r: %d parts", query, len(parts))
    if background:
        for part in saved[:MODELS_PER_CATEGORY]:
            queue_model(part.id)
        for part in saved:
            if (doc := INDEX.get(part.id)) is not None:
                queue_image(doc)
    return saved


def _part_dir(part_id: str) -> Path:
    return assets.part_dir(part_id)


def model_ready(part_id: str) -> bool:
    return (_part_dir(part_id) / "model.glb").is_file()


def queue_model(part_id: str) -> bool:
    """Build the part's model in the background (one at a time), once per process."""
    global _model_worker
    if get_settings().OFFLINE or part_id in _model_seen or model_ready(part_id):
        return False
    _model_seen.add(part_id)
    _model_queue.append(part_id)
    if _model_worker is None or _model_worker.done():
        _model_worker = asyncio.create_task(_drain_models())
    return True


async def build_model(part_id: str) -> bool:
    part = jobs.load_part(part_id)
    if part is None:
        return False
    try:
        done = await assets.resolve_shared(part)
    except Exception:
        logger.exception("catalog: model for %s failed", part_id)
        failed = jobs.load_part(part_id) or part
        failed.asset.status = "failed"
        jobs.save_part(failed)
        return False
    INDEX.add_parts([done.model_dump(mode="json")], from_part_json=True)
    return model_ready(part_id)


async def _drain_models() -> None:
    while _model_queue:
        await build_model(_model_queue.pop(0))


def queue_image(doc: Doc) -> bool:
    """Fetch the product photo to image.jpg (no LLM), so image_url turns local."""
    global _image_worker
    if (
        get_settings().OFFLINE
        or not doc.image_remote
        or doc.part_id in _image_seen
        or (_part_dir(doc.part_id) / "image.jpg").is_file()
    ):
        return False
    _image_seen.add(doc.part_id)
    _image_queue.append((doc.part_id, doc.image_remote))
    if _image_worker is None or _image_worker.done():
        _image_worker = asyncio.create_task(_drain_images())
    return True


async def _drain_images() -> None:
    while _image_queue:
        part_id, url = _image_queue.pop(0)
        await assets.fetch_image(url, _part_dir(part_id) / "image.jpg")


def ensure_part_json(doc: Doc) -> None:
    """A part known only from a cached search result gets its part.json (as a search job would
    write it), so its model, image and sellers endpoints work."""
    if doc.has_part_json or (_part_dir(doc.part_id) / "part.json").is_file():
        return
    try:
        part = Part.model_validate(doc.part)
    except ValidationError:
        return
    part.asset.status = "pending"
    jobs.save_part(part)
    INDEX.add_parts([part.model_dump(mode="json")], from_part_json=True)


# --- the responses ------------------------------------------------------------------------------


def item(doc: Doc) -> dict:
    pdir = _part_dir(doc.part_id)
    image = f"/parts/{doc.part_id}/image.jpg" if (pdir / "image.jpg").is_file() else None
    return {
        "part_id": doc.part_id,
        "name": doc.name,
        "brand": doc.brand,
        "category": doc.category,
        "price_usd": doc.price_usd,
        "dims_mm": doc.dims_mm,
        "image_url": image or doc.image_remote,
        "model_url": f"/parts/{doc.part_id}/model.glb",
        "model_ready": (pdir / "model.glb").is_file(),
        "seller": doc.seller,
    }


def category_items(
    cat: tables.Category, background: bool = True, may_search: bool = True
) -> tuple[list[dict], bool]:
    """(items, pending) for one category; queues its search (unless `may_search` is False) / models
    / photos when asked."""
    docs = INDEX.in_category(cat)[:MAX_ITEMS]
    if background:
        if may_search and len(docs) < MIN_ITEMS:
            schedule_search(cat.query)
        for doc in docs:
            ensure_part_json(doc)
            queue_image(doc)
        for doc in docs[:MODELS_PER_CATEGORY]:
            queue_model(doc.part_id)
    return [item(d) for d in docs], is_pending(cat.query)


async def build(site: str | None, session_id: str | None = None, focus: str | None = None) -> dict:
    """GET /catalog. `focus` (the app's gaze: roof, wall, ground, ceiling, counter, opening)
    orders and filters the categories (focus_categories; categories with items first, and only
    the first FOCUS_SEARCH_LEAD thin ones are searched in the background); none or one the site
    has no table for: the unfocused catalog. LookupError: unknown site."""
    start = time.monotonic()
    INDEX.maybe_refresh()
    det = await detect(site)
    env = tables.ENVIRONMENTS[det["environment"]]
    wanted = tables.normalize_focus(focus)
    pairs = focus_categories(det, wanted) if wanted else None
    applied = wanted if pairs is not None else None
    if pairs is None:
        pairs = categories_for(det)
    cats = []
    for i, (cat, evidence) in enumerate(pairs):
        items, pending = category_items(cat, may_search=applied is None or i < FOCUS_SEARCH_LEAD)
        cats.append(
            {
                "id": cat.id,
                "title": cat.title,
                "icon": cat.icon,
                "query": cat.query,
                "in_scene": bool(evidence.get("components") or evidence.get("seen")),
                "components": evidence.get("components") or [],
                "extra": cat.extra,
                "also": bool(evidence.get("also")),
                "pending": pending,
                "items": items,
            }
        )
    if applied is not None:
        cats.sort(key=lambda c: not c["items"])  # stable: the table's order among the stocked
    return {
        "site": site,
        "environment": env.id,
        "title": env.title,
        "place": det.get("place"),
        "confidence": det.get("confidence"),
        "also": det.get("also"),
        "focus": applied,
        "foci": foci(det),
        "generated_at": datetime.now(UTC).isoformat(),
        "pending": sum(c["pending"] for c in cats),
        "categories": cats,
        "signals": det.get("signals") or [],
        "grok": det.get("grok"),
        "took_ms": round((time.monotonic() - start) * 1000, 1),
    }


def _category_hits(words: list[str], pool: list[tables.Category]) -> list[tables.Category]:
    """Categories whose title or keywords hold every query word (the last one as a prefix)."""
    if not words:
        return []
    out = []
    for cat in pool:
        vocab = set(re.findall(r"[a-z0-9]+", f"{cat.title} {' '.join(cat.keywords)}".lower()))
        ok = all(
            any(v == w or (v.startswith(w) and (i == len(words) - 1 or len(w) >= 3)) for v in vocab)
            for i, w in enumerate(words)
        )
        if ok:
            out.append(cat)
    return out


def search_body(q: str, site: str | None = None, limit: int = 20) -> dict:
    """GET /catalog/search: the in-memory index only."""
    start = time.monotonic()
    INDEX.maybe_refresh()
    det = known_detection(site)
    site_cats: list[tables.Category] = [c for c, _ in categories_for(det)] if det else []
    boost = frozenset(c.id for c in site_cats)
    hits = INDEX.search(q, max(1, min(limit, 50)), boost)
    words = [w for w in re.findall(r"[a-z0-9]+", q.lower()) if w not in {"a", "an", "the"}]
    rest = [c for c in tables.CATEGORIES.values() if c.id not in boost]
    cats = _category_hits(words, site_cats + rest)[:5]
    return {
        "q": q,
        "site": site,
        "items": [item(doc) for doc, _ in hits],
        "categories": [c.title for c in cats],
        "category_ids": [c.id for c in cats],
        "took_ms": round((time.monotonic() - start) * 1000, 2),
    }


# --- warm ---------------------------------------------------------------------------------------


def served_sites() -> list[str]:
    base = Path(get_settings().SCENE_DIR)
    if not base.is_dir():
        return []
    return sorted(p.name for p in base.iterdir() if (p / "scene.json").is_file())


class _CostMeter(logging.Handler):
    """Adds up `server.llm`'s per-call log lines (role, provider, cost_usd) during a warm."""

    _RE = re.compile(r"role=(\S+) provider=(\S+) model=(\S+) .* cost_usd=(\S+)")

    def __init__(self) -> None:
        super().__init__(logging.INFO)
        self.calls: dict[str, int] = {}
        self.cost = 0.0

    def emit(self, record: logging.LogRecord) -> None:
        m = self._RE.search(record.getMessage())
        if not m:
            return
        role, provider, model, cost = m.groups()
        key = f"{role} {provider}:{model}"
        self.calls[key] = self.calls.get(key, 0) + 1
        if cost not in ("None", ""):
            self.cost += float(cost)


def _count_files(ns: str) -> int:
    d = Path(get_settings().DATA_DIR) / "cache" / ns
    return sum(1 for _ in d.glob("*.json")) if d.is_dir() else 0


async def warm(
    sites: list[str] | None = None,
    models: bool = True,
    echo=print,
    focus: bool = True,
    max_searches: int | None = None,
) -> dict:
    """Fill every site's catalog: detect (Grok once per site), search every category with too
    few items, build the first models and fetch every photo. `focus`: also each focus variant the
    site answers (roof, wall, ...: only the first FOCUS_SEARCH_LEAD thin categories of each are
    searched, as GET /catalog?focus= does). `max_searches`: stop searching after that many (the
    rest stay thin). A category is warmed once per run however many lists show it. Totals."""
    t_all = time.monotonic()
    meter = _CostMeter()
    llm_logger = logging.getLogger("server.llm")
    llm_logger.addHandler(meter)
    old_level = llm_logger.level
    llm_logger.setLevel(logging.INFO)
    before = {ns: _count_files(ns) for ns in ("serpapi", "web", "catalog_place", "asset_llm")}
    INDEX.load()
    totals = {"sites": 0, "searches": 0, "models": 0, "grok_place_usd": 0.0, "focus_lists": 0}
    warmed: dict[str, str] = {}  # category id -> what this run did ("searched", "", ...)

    async def warm_one(cat: tables.Category, evidence: dict, may_search: bool) -> str:
        searched = warmed.get(cat.id)
        if searched is None:
            searched = ""
            if (
                may_search
                and len(INDEX.in_category(cat)) < MIN_ITEMS
                and not get_settings().OFFLINE
                and not searched_already(cat.query)
            ):
                if max_searches is not None and totals["searches"] >= max_searches:
                    searched = "over budget"
                else:
                    try:
                        await run_search(cat.query, background=False)
                        searched = "searched"
                    except Exception as exc:  # noqa: BLE001 -- keep warming the rest
                        logger.warning("catalog warm: %r failed: %s", cat.query, exc)
                        searched = "failed"
                    totals["searches"] += 1
            docs = INDEX.in_category(cat)[:MAX_ITEMS]
            for doc in docs:
                ensure_part_json(doc)
                if not (_part_dir(doc.part_id) / "image.jpg").is_file() and doc.image_remote:
                    await assets.fetch_image(doc.image_remote, _part_dir(doc.part_id) / "image.jpg")
            if models:
                for doc in docs[:MODELS_PER_CATEGORY]:
                    if not model_ready(doc.part_id):
                        totals["models"] += bool(await build_model(doc.part_id))
            if may_search or searched:
                warmed[cat.id] = searched
        docs = INDEX.in_category(cat)[:MAX_ITEMS]
        ready = sum(model_ready(d.part_id) for d in docs)
        shown = evidence.get("components") or evidence.get("seen")
        flag = "*" if shown else ("+" if cat.extra else "~" if evidence.get("also") else " ")
        return f"   {flag} {cat.title:<24} {len(docs)} items, {ready} 3D  {searched}"

    try:
        for site in sites or served_sites():
            t0 = time.monotonic()
            det = await detect(site, wait_s=None)
            totals["sites"] += 1
            totals["grok_place_usd"] += det.get("grok_cost_usd") or 0.0
            echo(
                f"{site}: {det['environment']} ({det['confidence']:.2f}) "
                f"place={det.get('place')!r} grok={det['grok']}"
            )
            rows = [await warm_one(cat, ev, True) for cat, ev in categories_for(det)]
            echo("\n".join(rows))
            for f in foci(det) if focus else []:
                pairs = focus_categories(det, f) or []
                totals["focus_lists"] += 1
                rows = [
                    await warm_one(cat, ev, i < FOCUS_SEARCH_LEAD)
                    for i, (cat, ev) in enumerate(pairs)
                ]
                echo(f"   focus={f}:\n" + "\n".join("  " + r for r in rows))
            echo(f"   ({time.monotonic() - t0:.0f} s)")
    finally:
        llm_logger.removeHandler(meter)
        llm_logger.setLevel(old_level)
    after = {ns: _count_files(ns) for ns in before}
    totals.update(
        {
            "seconds": round(time.monotonic() - t_all, 1),
            "serpapi_calls": after["serpapi"] - before["serpapi"],
            "web_calls": after["web"] - before["web"],
            "llm_calls": meter.calls,
            "llm_cost_usd": round(meter.cost, 4),
        }
    )
    echo(
        f"catalog warm: {totals['sites']} sites in {totals['seconds']} s, "
        f"{totals['searches']} searches ({totals['serpapi_calls']} SerpApi calls, "
        f"{totals['web_calls']} web calls), {totals['models']} models; "
        f"LLM ${totals['llm_cost_usd']:.4f} over {sum(meter.calls.values())} calls "
        f"{json.dumps(meter.calls)}"
    )
    return totals
