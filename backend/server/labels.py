"""Live labels: one streamed Grok vision call names what the headset sees, with boxes.

Spec: docs/research/grok-ideas/g6-round5.md pick 1; results in docs/research/grok-ideas/f16-labels.md.

- `label_frame` sends one frame (960 px long edge) to `grok-4.20-0309-non-reasoning` with
  `stream=True`. The model writes one JSON object per line; `iter_labels` parses each line as it
  arrives, so the time to the first label is measured (0.6-0.8 s to the first token, G6 #6-7).
- The rules run in code on every label, not in the prompt: sizes/ratings only when read off
  printed text (G6 #1 copied "15 A" from a prompt example), confidence >= 0.5, names mapped to a
  small `KINDS` vocabulary, boxes accepted on 0-1, 0-100 (what 4.20 answers) or 0-1000.
- The raw reply is cached per frame hash + focus + model: a frame is never billed twice, and
  OFFLINE replays warmed frames.
- `label_scene` (`python -m server.warm --labels <site>`) labels every Nth thumb of a scan,
  lifts each box centre onto the collision mesh (`to_pin`, imported from F10's survey.py) and
  merges same-kind labels across frames into 3D anchors for `GET /scenes/{site}/labels`.
"""

import asyncio
import hashlib
import io
import json
import logging
import math
import re
import time
from collections import defaultdict, deque
from collections.abc import AsyncIterator
from functools import lru_cache
from pathlib import Path

import trimesh

from server import cache, llm
from server.config import get_settings
from server.meshgen import photo_data_url
from server.survey import to_pin  # F10's box-centre raycast onto the collision mesh
from server.vision import MAX_FRAME_BYTES

logger = logging.getLogger(__name__)

LABEL = "Grok's reading of the camera view. Sizes are shown only when printed on the part."
MIN_CONFIDENCE = 0.5
SEND_PX = 960  # the Quest gives 1280x960; image size isn't the latency lever (G6 #8)
PER_MIN = 20  # Grok calls per session per minute: at most ~$0.03/min
# Same-kind anchors closer than this merge (metres; the kitchen scan is metric). Box centres of
# big things wander across views: the kitchen warm left 4 countertops and 3 ranges at 0.3 m.
MERGE_M = {"countertop": 1.0, "range": 0.8, "refrigerator": 0.8, "cabinet": 0.6}
MERGE_DEFAULT_M = 0.3
NOT_VISIBLE = "size not visible"
_SITE_RE = re.compile(r"^[a-z0-9-]+$")
_SURFACE_RE = re.compile(r"\b(?:wall|floor|ceiling)s?$")  # a room surface isn't a thing to label

# (kind, words) in match order: "light switch" is a switch, "microwave oven" isn't a range.
_KIND_WORDS = [
    ("switch", "switch|dimmer"),
    ("outlet", "outlet|receptacle|socket|gfci|plug"),
    ("breaker panel", "breaker|breaker box|electrical panel|load center|load centre|fuse box"),
    ("range hood", "hood|range hood|extractor"),
    ("microwave", "microwave"),
    ("dishwasher", "dishwasher"),
    ("refrigerator", "fridge|refrigerator|freezer"),
    ("range", "range|stove|stovetop|cooktop|oven|hob"),
    ("faucet", "faucet|tap|spout"),
    ("sink", "sink|basin"),
    ("cabinet", "cabinet|cupboard|drawer|pantry"),
    ("countertop", "countertop|counter|worktop"),
    ("window", "window|blind"),
    ("door", "door"),
    ("vent", "vent|register|grille|diffuser|duct"),
    ("pipe", "pipe|drain|trap|supply line|valve"),
    ("light", "light|lamp|fixture|bulb|sconce|pendant"),
]
KINDS = [k for k, _ in _KIND_WORDS]
_KIND_RES = [(k, re.compile(rf"\b(?:{w})(?:e?s)?\b", re.IGNORECASE)) for k, w in _KIND_WORDS]
# A number with a unit: "15 A", "36-inch", '1/2"', "5,000 BTU", "120V".
_SIZE_RE = re.compile(
    r"\d+(?:[.,/]\d+)*\s*-?\s*(?:(?:amps?|a|volts?|v|watts?|w|inch(?:es)?|in|mm|cm|btus?|psi"
    r"|gal(?:lons?)?|ft|feet|qt|cu\.?\s*ft)\b|\"|″)",
    re.IGNORECASE,
)

PROMPT = """You label what a person sees through an AR headset in a home, for a repair \
contractor. List at most 6 fixtures, appliances or building parts in this image{focus}, most \
useful first. Skip loose items.
Write one JSON object per line and nothing else: no list, no code fence. Each object has:
- name: what it is, a short common noun phrase;
- detail: a few words on what you can see (material, colour, style). Give a number with a unit \
only if it is printed on the part and readable in the image;
- source: "printed_text" if detail uses text printed on the part, else "seen";
- box: [x0, y0, x1, y1] in percent of the image width and height (0-100), origin top-left;
- confidence: 0-1 that the name is right.
A light switch has a toggle or rocker and no plug holes; an outlet has plug holes."""


class RateLimited(Exception):
    """More than `PER_MIN` calls from one session in the last minute (HTTP 429)."""

    def __init__(self, retry_after_s: float):
        super().__init__(f"rate limited, retry in {retry_after_s:.0f} s")
        self.retry_after_s = retry_after_s


# --- rate guard ---------------------------------------------------------------------------

_calls: dict[str, deque[float]] = defaultdict(deque)


def take(session_id: str) -> None:
    """Count one call for `session_id`, or raise RateLimited.
    ponytail: in-process sliding window, one deque per session ever seen; fine for a demo box."""
    now = time.monotonic()
    window = _calls[session_id]
    while window and now - window[0] > 60:
        window.popleft()
    if len(window) >= PER_MIN:
        raise RateLimited(60 - (now - window[0]))
    window.append(now)


# --- parsing and rules --------------------------------------------------------------------


def parse_line(line: str) -> dict | None:
    line = line.strip().rstrip(",")
    if not line.startswith("{"):
        return None  # a fence, a bracket or prose
    try:
        obj = json.loads(line)
    except json.JSONDecodeError:
        return None
    return obj if isinstance(obj, dict) else None


async def iter_labels(chunks: AsyncIterator[str]) -> AsyncIterator[dict]:
    """Each complete JSON line in a text stream, as soon as its newline arrives. A truncated last
    line fails to parse and is dropped."""
    buf = ""
    async for piece in chunks:
        buf += piece
        *lines, buf = buf.split("\n")
        for line in lines:
            if (obj := parse_line(line)) is not None:
                yield obj
    if (obj := parse_line(buf)) is not None:
        yield obj


def unit_box(box: list[float]) -> list[float] | None:
    """[x0, y0, x1, y1] on 0-1, 0-100 or 0-1000 -> 0-1, clamped, corners sorted; None if bad.
    ponytail: scale per box, so a 0-1000 box inside the top-left 10 % reads as percent. We ask
    for percent and 4.20 answers in percent (G6: 12 of 13 replies); vote per reply if it drifts."""
    if len(box) != 4:
        return None
    top = max(box)
    scale = 1.0 if top <= 1.5 else 100.0 if top <= 100 else 1000.0
    x0, y0, x1, y1 = (min(max(v / scale, 0.0), 1.0) for v in box)
    x0, x1 = sorted((x0, x1))
    y0, y1 = sorted((y0, y1))
    if x1 - x0 < 1e-3 or y1 - y0 < 1e-3:
        return None
    return [round(v, 4) for v in (x0, y0, x1, y1)]


def kind_of(name: str) -> str:
    return next((k for k, pattern in _KIND_RES if pattern.search(name)), "other")


def honest(detail: str, printed: bool) -> tuple[str, str]:
    """(detail to show, detail for a search query). A size or rating the model didn't read off
    printed text is removed and flagged."""
    if printed or not _SIZE_RE.search(detail):
        return detail, detail
    stripped = re.sub(r"\s+", " ", _SIZE_RE.sub("", detail)).strip(" ,;-")
    return (f"{stripped}, {NOT_VISIBLE}" if stripped else NOT_VISIBLE), stripped


def normalise(obj: dict) -> dict | None:
    """One model line -> a label, or None when it breaks a rule."""
    try:
        confidence = float(obj.get("confidence") or 0)
        box = unit_box([float(v) for v in obj.get("box") or []])
    except (TypeError, ValueError):
        return None
    name = re.sub(r"\s+", " ", str(obj.get("name") or "")).strip().lower()
    if confidence < MIN_CONFIDENCE or not name or box is None or _SURFACE_RE.search(name):
        return None
    source = "printed_text" if obj.get("source") == "printed_text" else "seen"
    detail, query = honest(str(obj.get("detail") or "").strip(), source == "printed_text")
    return {
        "name": name,
        "kind": kind_of(name),
        "detail": detail,
        "source": source,
        "box": box,
        "confidence": round(confidence, 2),
        "query": " ".join(f"{query} {name}".replace(",", " ").split()),
    }


def spoken(labels: list[dict]) -> str:
    if not labels:
        return "I can't make out anything to label here."
    names = [f"the {n}" for n in dict.fromkeys(label["name"] for label in labels)][:3]
    joined = names[0] if len(names) == 1 else f"{', '.join(names[:-1])} and {names[-1]}"
    return f"I see {joined}. Tap one to shop for it."


# --- the Grok call ------------------------------------------------------------------------


def _data_url(jpg: bytes) -> str:
    try:
        return photo_data_url(io.BytesIO(jpg), SEND_PX)
    except OSError as exc:  # PIL's UnidentifiedImageError, or a truncated JPEG
        raise ValueError("not a readable image") from exc


async def _ask(jpg: bytes, focus: str | None) -> dict:
    """One streamed call; the raw text plus timings and cost, for the cache."""
    url = await asyncio.to_thread(_data_url, jpg)
    provider, model = llm.resolve_role("labels")
    text = PROMPT.format(focus=f", focusing on {focus}" if focus else "")
    messages = [
        {
            "role": "user",
            "content": [
                {"type": "text", "text": text},
                {"type": "image_url", "image_url": {"url": url, "detail": "high"}},
            ],
        }
    ]
    start = time.monotonic()
    ms = lambda: round((time.monotonic() - start) * 1000)
    first_token_ms = first_label_ms = None
    parts: list[str] = []
    usage = None
    stream = await llm._client(provider, max_retries=0).chat.completions.create(
        model=model,
        messages=messages,
        temperature=0,
        stream=True,
        stream_options={"include_usage": True},
        timeout=30,
    )

    async def deltas() -> AsyncIterator[str]:
        nonlocal first_token_ms, usage
        async for chunk in stream:
            usage = chunk.usage or usage
            piece = chunk.choices[0].delta.content if chunk.choices else None
            if piece:
                first_token_ms = first_token_ms or ms()
                parts.append(piece)
                yield piece

    async for obj in iter_labels(deltas()):
        if first_label_ms is None and normalise(obj):
            first_label_ms = ms()
    ticks = getattr(usage, "cost_in_usd_ticks", None)
    reply = {
        "text": "".join(parts),
        "model": f"{provider}:{model}",
        "first_token_ms": first_token_ms,
        "first_label_ms": first_label_ms,
        "latency_ms": ms(),
        "cost_usd": round(ticks / 1e10, 5) if ticks is not None else None,
    }
    logger.info(
        "labels: %s cost_usd=%s first_token_ms=%s first_label_ms=%s latency_ms=%s",
        reply["model"],
        reply["cost_usd"],
        first_token_ms,
        first_label_ms,
        reply["latency_ms"],
    )
    return reply


async def label_frame(jpg: bytes, focus: str | None = None) -> dict:
    """Labels for one JPEG. ValueError: empty, too big or unreadable; cache.OfflineMiss: OFFLINE
    and never labelled; openai.APIError: xAI failed."""
    if not jpg:
        raise ValueError("empty frame")
    if len(jpg) > MAX_FRAME_BYTES:
        raise ValueError(f"frame is {len(jpg)} bytes, over the {MAX_FRAME_BYTES}-byte cap")
    key = {
        "frame": hashlib.sha1(jpg).hexdigest(),
        "focus": focus or "",
        "model": ":".join(llm.resolve_role("labels")),
        "prompt": hashlib.sha1(PROMPT.encode()).hexdigest()[:8],  # a prompt edit re-labels
    }
    fresh = cache.get("labels", key) is None
    reply = await cache.cached("labels", key, lambda: _ask(jpg, focus))
    labels = [
        label
        for label in (normalise(obj) for obj in map(parse_line, reply["text"].splitlines()) if obj)
        if label
    ]
    for i, label in enumerate(labels):
        label["id"] = f"l{i}"
    return {
        "labels": labels,
        "spoken": spoken(labels),
        "model": reply["model"],
        "first_token_ms": reply["first_token_ms"],
        "first_label_ms": reply["first_label_ms"],
        "latency_ms": reply["latency_ms"],
        "cost_usd": (reply["cost_usd"] or 0.0) if fresh else 0.0,
        "cached": not fresh,
        "label": LABEL,
    }


# --- scanned scenes: labels lifted to 3D --------------------------------------------------


@lru_cache(maxsize=4)
def _mesh(path: str) -> trimesh.Trimesh:
    return trimesh.load(path, force="mesh")


def merge(anchors: list[dict]) -> list[dict]:
    """Most confident first; a label of the same kind (or, for "other", the same name) within
    the kind's `MERGE_M` of a kept one folds into it and adds its frame to `frames`."""
    kept: list[dict] = []
    same = lambda a, b: a["kind"] == b["kind"] and (a["kind"] != "other" or a["name"] == b["name"])
    for anchor in sorted(anchors, key=lambda a: -a["confidence"]):
        twin = next(
            (
                k
                for k in kept
                if same(k, anchor)
                and math.dist(k["pos"], anchor["pos"]) < MERGE_M.get(k["kind"], MERGE_DEFAULT_M)
            ),
            None,
        )
        if twin is None:
            kept.append(anchor)
        else:
            twin["frames"] += anchor["frames"]
    for i, anchor in enumerate(kept, 1):
        anchor["id"] = f"a{i}"
    return kept


def _scene(site: str) -> tuple[Path, dict]:
    site_dir = Path(get_settings().SCENE_DIR) / site
    if not _SITE_RE.match(site) or not (site_dir / "scene.json").is_file():
        raise LookupError(f"unknown site {site!r}")
    return site_dir, json.loads((site_dir / "scene.json").read_text())


def _anchors_path(site: str, rev: int) -> Path:
    return Path(get_settings().DATA_DIR) / "labels" / f"{site}.r{rev}.json"


async def label_scene(site: str, every: int = 4) -> dict:
    """Label every `every`th thumb of the scan, lift the labels to 3D and merge them. Writes
    `<DATA_DIR>/labels/<site>.r<rev>.json`. LookupError: unknown site or no cameras/collision."""
    site_dir, meta = _scene(site)
    cams_file, collision = meta.get("cameras"), (meta.get("collision") or {}).get("file")
    if not cams_file or not collision:
        raise LookupError(f"{site!r} has no cameras or collision mesh")
    cams = json.loads((site_dir / cams_file).read_text())[::every]
    mesh = await asyncio.to_thread(_mesh, str(site_dir / collision))
    anchors, cost, unpinned = [], 0.0, 0
    for cam in cams:
        jpg = (site_dir / (cam.get("thumb") or f"thumbs/{cam['id']}.jpg")).read_bytes()
        result = await label_frame(jpg)
        cost += result["cost_usd"]
        print(
            f"  {cam['id']}: {len(result['labels'])} labels, first {result['first_label_ms']} ms,"
            f" all {result['latency_ms']} ms, ${result['cost_usd']:.4f}"
            + (" (cached)" if result["cached"] else "")
        )
        for label in result["labels"]:
            pos = await asyncio.to_thread(to_pin, cam, label["box"], mesh)
            if pos is None:
                unpinned += 1
                continue
            keep = ("name", "kind", "detail", "source", "confidence", "query")
            anchors.append({**{k: label[k] for k in keep}, "pos": pos, "frames": [cam["id"]]})
    rev = int(meta.get("revision", 1))
    out = {
        "site": site,
        "rev": rev,
        "frames": len(cams),
        "unpinned": unpinned,
        "cost_usd": round(cost, 4),
        "label": LABEL,
        "labels": merge(anchors),
    }
    cache.write_json_atomic(_anchors_path(site, rev), json.dumps(out, indent=1))
    return out


def scene_labels(site: str) -> dict | None:
    """The warmed anchors for the site's current revision, or None."""
    try:
        _, meta = _scene(site)
    except LookupError:
        return None
    path = _anchors_path(site, int(meta.get("revision", 1)))
    return json.loads(path.read_text()) if path.is_file() else None
