"""Drone condition survey: capture frames -> one Grok vision call -> severity pins on the mesh.

Spec: docs/research/grok-ideas/g4-round3.md pick 1; results in docs/research/grok-ideas/f10-survey.md.

- `pick_frames` chooses up to 6 of the scene's cameras: roof views (pitched >30 deg down) and
  facade views (<15 deg), spread over view sides.
- One `llm.chat("survey")` call (grok-4.20 non-reasoning; `careful` = grok-4.7, low effort) with a
  strict schema returns findings with boxes. The raw reply is cached per scene revision + frames +
  model, so a repeat survey costs $0 and works OFFLINE.
- The rules below run in code on every reply, not in the prompt: drop confidence < 0.5, never pin
  "none" findings, hail/impact is "suspected, verify", `part_query` only for repair/replace,
  same-element pins merge.
- `to_pin` casts rays through the camera pose (`position == -R^T t`, OpenCV axes, checked on the
  kitchen and B1's scenes) at the box centre and 4 inner points against the collision mesh and
  takes the median hit.
"""

import asyncio
import json
import logging
import math
import re
import time
from collections import defaultdict
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

import numpy as np
import trimesh

from server import cache, llm
from server.config import get_settings
from server.meshgen import photo_data_url

logger = logging.getLogger(__name__)

LABEL = "AI triage from drone frames, not an inspection"
NOT_READY = "I need the full scan to pin anything"
MIN_CONFIDENCE = 0.5
MERGE_M = 1.5  # same-element pins closer than this merge, on metric scenes
# B1's building scenes are `scale_method: none` (a whole building is ~1 unit across), so there
# the merge radius is a share of the collision mesh's bbox diagonal instead. 0.15 folds Hospital's
# five views of one failed roof into one pin and keeps its facade apart (f10-survey.md).
# ponytail: tuned on one scene; use MERGE_M once the P2 tape scales the building scenes.
MERGE_FRAC = 0.15
SEND_PX = 1280  # long edge sent to Grok; `survey/<id>.jpg` should be at least this big
SEVERITY_RANK = {"minor": 1, "moderate": 2, "severe": 3}
ELEMENTS = [
    "roof_covering",
    "flat_roof_membrane",
    "flashing",
    "gutter",
    "downspout",
    "fascia_soffit",
    "chimney",
    "skylight",
    "dormer",
    "facade",
    "window",
    "door",
    "vegetation",
    "solar_panel",
    "other",
]
ACTIONS = ["none", "monitor", "inspect_closer", "repair", "replace"]
_HAIL_RE = re.compile(r"\b(hail|impact)", re.IGNORECASE)
# Grok names one failed flat roof either way across frames (live Hospital run), so they merge.
_SAME_AS = {"flat_roof_membrane": "roof_covering"}
_SITE_RE = re.compile(r"^[a-z0-9-]+$")

PROMPT = """You are triaging the condition of a building from drone frames for a repair \
contractor. Frame ids, in order: {ids}. Focus: {focus}.
For each building element you can see, add one finding:
- frame: the id of the frame it is clearest in;
- element, issue (a short phrase), severity ("none" when it looks fine);
- confidence 0-1 that the issue is real;
- box: [x0, y0, x1, y1] around it in that frame, on a 0-1000 scale with the origin top-left. \
For example the top-right quarter of a frame is [500, 0, 1000, 500];
- action, and part_query: a short shopping query for the repair material when the action is \
repair or replace, else "".
Report only what is visible. Hail bruising can't be judged from a photo; call it suspected.
coverage_gaps: parts of the building these frames don't show well enough to judge."""


class NotReady(Exception):
    """The site exists but has no cameras or collision mesh to pin against (HTTP 409)."""


@dataclass
class Plan:
    site: str
    dir: Path
    rev: int
    cams: dict[str, dict]
    collision: Path
    merge_radius: float
    frames: list[str]
    role: str
    focus: str

    @property
    def model(self) -> str:
        return ":".join(llm.resolve_role(self.role))

    @property
    def key(self) -> dict:
        return {
            "site": self.site,
            "rev": self.rev,
            "frames": self.frames,
            "model": self.model,
            "focus": self.focus,
        }


# --- frames ------------------------------------------------------------------------------


def view(cam: dict) -> tuple[float, int]:
    """(pitch in degrees, positive looking down; view-side octant 0-7) from `R[2]`, the camera
    +Z in the world (glTF, +Y up). The side is the direction from the target back to the camera."""
    f = np.asarray(cam["R"])[2]
    pitch = math.degrees(math.asin(float(np.clip(-f[1], -1, 1))))
    side = int(math.degrees(math.atan2(-f[2], -f[0])) % 360 // 45)
    return pitch, side


def pick_frames(cams: list[dict], n: int = 6, focus: str = "all") -> list[str]:
    """Up to `n` frame ids: cameras grouped by (roof/facade band, view side), roof groups first,
    round robin over the groups, evenly spaced within each."""
    bands = {"roof": ("roof",), "facade": ("facade",)}.get(focus, ("roof", "facade"))
    groups: dict[tuple, list[str]] = defaultdict(list)
    for cam in cams:
        pitch, side = view(cam)
        band = "roof" if pitch > 30 else "facade" if pitch < 15 else None
        if band in bands:
            groups[(band != "roof", side)].append(cam["id"])
    if not groups:  # e.g. an all-oblique flight: fall back to spreading over every camera
        groups[(0, 0)] = [cam["id"] for cam in cams]
    quota = dict.fromkeys(sorted(groups), 0)
    while sum(quota.values()) < n and any(quota[k] < len(groups[k]) for k in quota):
        for k in quota:
            if sum(quota.values()) < n and quota[k] < len(groups[k]):
                quota[k] += 1
    return [
        groups[k][int((j + 0.5) * len(groups[k]) / q)] for k, q in quota.items() for j in range(q)
    ]


def frame_file(plan: Plan, frame_id: str) -> str:
    """Scene-relative image for a frame: `survey/<id>.jpg` (full-res export) if the package has
    one, else the 640 px thumb."""
    survey = f"survey/{frame_id}.jpg"
    if (plan.dir / survey).is_file():
        return survey
    return plan.cams[frame_id].get("thumb") or f"thumbs/{frame_id}.jpg"


# --- scene -------------------------------------------------------------------------------


def plan(
    site: str,
    frames: list[str] | None = None,
    careful: bool = False,
    n: int = 6,
    focus: str = "all",
) -> Plan:
    """Resolve the scene package and the frames to send. LookupError: unknown site or frame;
    NotReady: no cameras or collision mesh."""
    site_dir = Path(get_settings().SCENE_DIR) / site
    if not _SITE_RE.match(site) or not (site_dir / "scene.json").is_file():
        raise LookupError(f"unknown site {site!r}")
    meta = json.loads((site_dir / "scene.json").read_text())
    cams_file = meta.get("cameras")
    collision = (meta.get("collision") or {}).get("file")
    if not cams_file or not collision:
        raise NotReady(NOT_READY)
    cams_path, collision_path = site_dir / cams_file, site_dir / collision
    if not cams_path.is_file() or not collision_path.is_file():
        raise NotReady(NOT_READY)
    cams = json.loads(cams_path.read_text())
    if not cams:
        raise NotReady(NOT_READY)
    by_id = {c["id"]: c for c in cams}
    if frames:
        missing = [f for f in frames if f not in by_id]
        if missing:
            raise LookupError(f"unknown frames {missing}")
        frames = frames[: max(1, n)]
    else:
        frames = pick_frames(cams, n, focus)
    if meta.get("scale_method") in (None, "none"):
        merge = MERGE_FRAC * float(np.linalg.norm(_mesh(str(collision_path)).extents))
    else:
        merge = MERGE_M
    return Plan(
        site=site,
        dir=site_dir,
        rev=int(meta.get("revision", 1)),
        cams=by_id,
        collision=collision_path,
        merge_radius=merge,
        frames=frames,
        role="survey_careful" if careful else "survey",
        focus=focus,
    )


@lru_cache(maxsize=4)
def _mesh(path: str) -> trimesh.Trimesh:
    return trimesh.load(path, force="mesh")


# --- boxes and pins ----------------------------------------------------------------------


def box_scale(boxes: list[list[float]]) -> float:
    """The scale one reply's boxes are on. Grok 4.20 answered on 0-100 although asked for 0-1000
    (G4 call #1), so a reply whose values all fit 0-100 is read as percent.
    ponytail: per reply, not per box -- a 0-1000 reply with only top-left boxes under 100 would
    be misread; add a per-box vote if that shows up."""
    top = max((v for b in boxes for v in b), default=0)
    return 1.0 if top <= 1 else 100.0 if top <= 100 else 1000.0


def normalise_box(box: list[float], scale: float) -> list[float] | None:
    """[x0, y0, x1, y1] on `scale` -> 0-1, clamped, reversed corners swapped; None if degenerate."""
    if len(box) != 4:
        return None
    x0, y0, x1, y1 = (min(max(v / scale, 0.0), 1.0) for v in box)
    x0, x1 = sorted((x0, x1))
    y0, y1 = sorted((y0, y1))
    if x1 - x0 < 1e-3 or y1 - y0 < 1e-3:
        return None
    return [round(v, 4) for v in (x0, y0, x1, y1)]


def to_pin(cam: dict, box: list[float], mesh: trimesh.Trimesh) -> list[float] | None:
    """Median first hit of rays through the box centre and its 25/75 % inner points, or None.
    `box` is 0-1 of the image, so it maps onto the camera's own w x h whatever size was sent."""
    x0, y0, x1, y1 = box
    uv = [((x0 + x1) / 2, (y0 + y1) / 2)] + [
        (x0 + (x1 - x0) * a, y0 + (y1 - y0) * b) for a in (0.25, 0.75) for b in (0.25, 0.75)
    ]
    K = np.array([[cam["fx"], 0, cam["cx"]], [0, cam["fy"], cam["cy"]], [0, 0, 1]])
    pix = np.array([[u * cam["w"], v * cam["h"], 1.0] for u, v in uv])
    dirs = (np.asarray(cam["R"]).T @ np.linalg.inv(K) @ pix.T).T
    dirs /= np.linalg.norm(dirs, axis=1, keepdims=True)
    origins = np.repeat(np.asarray(cam["position"], dtype=float)[None], len(dirs), axis=0)
    hits, _, _ = mesh.ray.intersects_location(origins, dirs, multiple_hits=False)
    if not len(hits):
        return None
    return [round(float(v), 4) for v in np.median(hits, axis=0)]


# --- rules -------------------------------------------------------------------------------


def triage(findings: list[dict], frames: list[str]) -> tuple[list[dict], list[str]]:
    """(pin candidates, "looked fine" lines). Boxes normalised, rules applied; no 3D yet."""
    scale = box_scale([f.get("box") or [] for f in findings])
    pins, fine = [], []
    for f in findings:
        if f.get("frame") not in frames or float(f.get("confidence", 0)) < MIN_CONFIDENCE:
            continue
        if f.get("severity") not in SEVERITY_RANK:
            fine.append(f"{f.get('element', 'other')} ({f['frame']})")
            continue
        issue, action = f.get("issue", "").strip(), f.get("action", "monitor")
        suspected = bool(_HAIL_RE.search(issue))
        if suspected:
            issue, action = f"{issue} (suspected, verify on the roof)", "inspect_closer"
        pins.append(
            {
                "frame_id": f["frame"],
                "box": normalise_box(f.get("box") or [], scale),
                "element": f.get("element", "other"),
                "severity": f["severity"],
                "confidence": round(float(f["confidence"]), 2),
                "issue": issue,
                "action": action,
                "part_query": (f.get("part_query") or None)
                if action in ("repair", "replace")
                else None,
                "suspected": suspected,
            }
        )
    return pins, fine


def merge_pins(pins: list[dict], radius: float) -> list[dict]:
    """Worst first; a pin of the same element within `radius` of a kept one folds into it
    (its frame goes to `also_in`). Unprojected pins (`p: None`) never merge."""
    kept: list[dict] = []
    for pin in sorted(pins, key=lambda p: (-SEVERITY_RANK[p["severity"]], -p["confidence"])):
        twin = next(
            (
                k
                for k in kept
                if pin["p"]
                and k["p"]
                and _SAME_AS.get(k["element"], k["element"])
                == _SAME_AS.get(pin["element"], pin["element"])
                and math.dist(k["p"], pin["p"]) < radius
            ),
            None,
        )
        if twin is None:
            kept.append({**pin, "also_in": []})
        else:
            twin["also_in"].append(pin["frame_id"])
    for i, pin in enumerate(kept, 1):
        pin["id"] = f"f{i}"
    return kept


def spoken(pins: list[dict], fine: list[str]) -> str:
    if not pins:
        return (
            "Nothing wrong that I can see in the footage."
            if fine
            else ("I couldn't make out the building in those frames.")
        )
    worst = pins[0]
    drawn = sum(1 for p in pins if p["p"])
    noun = "problem" if len(pins) == 1 else "problems"
    element = worst["element"].replace("_", " ")
    return (
        f"{len(pins)} {noun} found, {drawn} pinned. "
        f"Worst is the {element}, {worst['severity']}: {worst['issue'].rstrip('.')}."
    )


# --- the Grok call -----------------------------------------------------------------------


def _schema(frames: list[str]) -> dict:
    finding = {
        "type": "object",
        "properties": {
            "frame": {"type": "string", "enum": frames},
            "element": {"type": "string", "enum": ELEMENTS},
            "issue": {"type": "string"},
            "severity": {"type": "string", "enum": ["none", *SEVERITY_RANK]},
            "confidence": {"type": "number"},
            "box": {"type": "array", "items": {"type": "number"}},
            "action": {"type": "string", "enum": ACTIONS},
            "part_query": {"type": "string"},
        },
    }
    return llm._strict_schema(
        {
            "type": "object",
            "properties": {
                "findings": {"type": "array", "items": finding},
                "coverage_gaps": {"type": "array", "items": {"type": "string"}},
            },
        }
    )


async def _ask(plan: Plan) -> dict:
    content: list[dict] = [
        {"type": "text", "text": PROMPT.format(ids=", ".join(plan.frames), focus=plan.focus)}
    ]
    for fid in plan.frames:
        url = await asyncio.to_thread(photo_data_url, plan.dir / frame_file(plan, fid), SEND_PX)
        content += [
            {"type": "text", "text": f"[frame {fid}]"},
            {"type": "image_url", "image_url": {"url": url, "detail": "high"}},
        ]
    kw = {"reasoning_effort": "low"} if plan.role == "survey_careful" else {}
    start = time.monotonic()
    resp = await llm.chat(
        plan.role,
        [{"role": "user", "content": content}],
        response_format={
            "type": "json_schema",
            "json_schema": {"name": "Survey", "strict": True, "schema": _schema(plan.frames)},
        },
        max_retries=0,  # a timed-out Grok call may still bill; don't pay twice
        timeout=150,
        **kw,
    )
    cost = llm.cost_usd(resp.usage)
    cost = round(cost, 5) if cost is not None else None
    latency = round(time.monotonic() - start, 1)
    logger.info(
        "survey: %s %s frames=%s cost_usd=%s latency_s=%s",
        plan.site,
        plan.model,
        plan.frames,
        cost,
        latency,
    )
    reply = json.loads(resp.choices[0].message.content or "{}")
    return {**reply, "cost_usd": cost, "latency_s": latency}


def is_cached(p: Plan) -> bool:
    return cache.get("survey", p.key) is not None


async def run(p: Plan) -> dict:
    """The survey body for `GET /scene/survey/{id}`. OfflineMiss when OFFLINE with no cache."""
    fresh = not is_cached(p)
    reply = await cache.cached("survey", p.key, lambda: _ask(p))
    pins, fine = triage(reply.get("findings") or [], p.frames)
    mesh = await asyncio.to_thread(_mesh, str(p.collision))
    for pin in pins:
        box = pin["box"]
        pin["p"] = (
            await asyncio.to_thread(to_pin, p.cams[pin["frame_id"]], box, mesh) if box else None
        )
    pins = merge_pins(pins, p.merge_radius)
    return {
        "site": p.site,
        "rev": p.rev,
        "model": p.model,
        "frames": [{"id": f, "file": frame_file(p, f)} for f in p.frames],
        "pins": pins,
        "looked_fine": fine,
        "coverage_gaps": reply.get("coverage_gaps") or [],
        "spoken": spoken(pins, fine),
        "label": LABEL,
        "cached": not fresh,
        "cost_usd": (reply.get("cost_usd") or 0.0) if fresh else 0.0,
        "latency_s": reply.get("latency_s"),
    }
