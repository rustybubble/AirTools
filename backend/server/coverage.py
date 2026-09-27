"""Capture coach (G4 pick 2): which sides of the scanned building the drone saw, the reshoot
legs that fill the gaps, and one spoken line for the pilot.

All the maths is local, from the scene package (`docs/api.md` §4, scene files):
- the target is the collision mesh, sampled by area: its 5-95 % XZ box gives the centre and
  width, its 2-98 % Y range the ground and the top;
- each camera (`cameras.r<rev>.json`, `x_cam = R x_world + t`, OpenCV axes) looks along
  `f = R[2]`; its pitch is `asin(-f_y)` (positive = down) and its side is the bearing of its
  horizontal offset from the centre;
- a view counts by how well it points at the target: 1 when the target sphere is on the
  optical axis, falling to 0 once it leaves the horizontal field of view;
- nadir views (pitched 60 degrees or more) see the roof, not a side.

Bearings are degrees clockwise seen from above (+Y) in the scene frame (glTF, +Y up), from
`zero_dir`: north when `scene.json` has `north`, else the side the flight started on (the
first view of the target). Grok only phrases the result (`LLM_COACH`); the legs are computed
here, because G4 call #6 showed the model lists 7 identical legs for 7 empty sides.
"""

import asyncio
import json
import logging
import math
import re
from functools import lru_cache
from pathlib import Path

import numpy as np
import trimesh

from server import cache, llm
from server.config import get_settings

logger = logging.getLogger(__name__)

SIDES = 8
MIN_VIEWS = 3.0  # weighted views before a side counts as seen (a point needs ~3 views in SfM)
NADIR_DEG = 60.0  # at or above: straight-down roof views
LEVEL_DEG = 20.0  # below: eave/facade-level views
NADIR_MIN_FRAC = 0.10  # fewer nadir views than this -> a nadir_grid leg
LEVEL_MIN_FRAC = 0.15  # fewer level views than this -> an eave_pass leg
ORBIT_PITCH, EAVE_PITCH = 30, 10
INDOOR_MAX_M = 8.0  # a metric target narrower than this is a room or object, not a flight
COMPASS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"]
# Seen from the start side, looking at the building: clockwise from above is to your left.
START = [
    "front", "front-left", "left", "back-left", "back", "back-right", "right", "front-right"
]  # fmt: skip
PROMPT = (
    "You coach a drone pilot right after a scan. Say the draft below to the pilot as exactly "
    "two short, natural spoken sentences: first how much of the building the flight covered, "
    "then the legs to fly next, in order. Keep every side name, number and unit; add nothing "
    "new. Plain text.\n\nDraft: {draft}"
)


class Incomplete(Exception):
    """The scene package lacks the camera file or the mesh the coach needs."""


def site_dir(site: str) -> Path | None:
    """`SCENE_DIR/<site>` when it holds a scene.json, else None (bad or unknown site name). The
    name pattern is the scene routes' own, so it can't climb out of SCENE_DIR."""
    if not re.fullmatch(r"[a-z0-9-]+", site or ""):
        return None
    path = Path(get_settings().SCENE_DIR) / site
    return path if (path / "scene.json").is_file() else None


@lru_cache(maxsize=8)
def target(mesh_path: str, mtime_ns: int = 0) -> dict:
    """Centre, half-width and height of what was scanned, cached per file version."""
    mesh = trimesh.load(mesh_path, force="mesh")
    pts, _ = trimesh.sample.sample_surface(mesh, 20000, seed=0)
    lo, hi = np.percentile(pts, 5, axis=0), np.percentile(pts, 95, axis=0)
    ground, top = np.percentile(pts[:, 1], [2, 98])
    return {
        "centre": [float((lo[0] + hi[0]) / 2), float(ground), float((lo[2] + hi[2]) / 2)],
        "half_width": float(math.hypot(hi[0] - lo[0], hi[2] - lo[2]) / 2),
        "height": float(top - ground),
    }


def views(cams: list[dict], tgt: dict) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """(horizontal offsets from the centre as [x, z], pitch degrees, aim weight 0-1)."""
    pos = np.array([c["position"] for c in cams], dtype=float)
    fwd = np.array([c["R"][2] for c in cams], dtype=float)
    cx, gy, cz = tgt["centre"]
    to = np.array([cx, gy + tgt["height"] / 2, cz]) - pos
    dist = np.maximum(np.linalg.norm(to, axis=1), 1e-9)
    off_axis = np.arccos(np.clip((fwd * to).sum(1) / dist, -1, 1))
    sphere = math.hypot(tgt["half_width"], tgt["height"] / 2)
    cone = np.arcsin(np.clip(sphere / dist, 0, 1))  # angular radius of the target
    hfov = np.array([math.atan(c["w"] / (2 * c["fx"])) if c.get("fx") else 0.6 for c in cams])
    weight = np.clip(1 - np.maximum(off_axis - cone, 0) / hfov, 0, 1)
    pitch = np.degrees(np.arcsin(np.clip(-fwd[:, 1], -1, 1)))
    return pos[:, [0, 2]] - [cx, cz], pitch, weight


def gaps(seen: list[bool]) -> list[tuple[int, int]]:
    """Runs of unseen sides as (first side, count), merged around the circle."""
    n = len(seen)
    if not any(seen):
        return [(0, n)]
    start = seen.index(True)
    runs: list[tuple[int, int]] = []
    for i in range(1, n + 1):
        k = (start + i) % n
        if seen[k]:
            continue
        if runs and (runs[-1][0] + runs[-1][1]) % n == k:
            runs[-1] = (runs[-1][0], runs[-1][1] + 1)
        else:
            runs.append((k, 1))
    return runs


def plan_legs(seen: list[bool], bands: dict, half_width: float, height: float) -> list[dict]:
    """Merged reshoot legs. `radius` is the horizontal distance from the centre, `altitude` the
    height above the ground, both in scene units."""
    step = 360 / SIDES
    legs = []
    for first, count in gaps(seen):
        radius = 2 * half_width
        legs.append(
            {
                "pattern": "orbit",
                "from_deg": (first * step - step / 2) % 360,
                "sweep_deg": count * step,
                "sides": [(first + i) % SIDES for i in range(count)],
                "gimbal_pitch_deg": ORBIT_PITCH,
                "radius": radius,
                "altitude": height / 2 + radius * math.tan(math.radians(ORBIT_PITCH)),
            }
        )
    total = sum(bands.values()) or 1
    if bands["nadir"] / total < NADIR_MIN_FRAC:
        legs.append(
            {
                "pattern": "nadir_grid",
                "from_deg": None,
                "sweep_deg": None,
                "sides": [],
                "gimbal_pitch_deg": 90,
                "radius": half_width,
                "altitude": height + 2 * half_width,
            }
        )
    if bands["level"] / total < LEVEL_MIN_FRAC:
        legs.append(
            {
                "pattern": "eave_pass",
                "from_deg": 0.0,
                "sweep_deg": 360.0,
                "sides": list(range(SIDES)),
                "gimbal_pitch_deg": EAVE_PITCH,
                "radius": 1.5 * half_width,
                "altitude": 0.8 * height,
            }
        )
    return legs


def _horizontal(v) -> np.ndarray | None:
    v = np.array([v[0], v[2]], dtype=float)
    n = np.linalg.norm(v)
    return v / n if n > 1e-9 else None


def coverage(scene_dir: Path) -> dict:
    """The coverage body for `GET /scenes/{site}/coverage`, without the spoken line."""
    scene = json.loads((scene_dir / "scene.json").read_text())
    cams_file = scene.get("cameras") or "cameras.json"  # old flat packages have no field
    mesh = (scene.get("collision") or scene.get("mesh") or {}).get("file", "collision.glb")
    for name in (cams_file, mesh):
        if not (scene_dir / name).is_file():
            raise Incomplete(f"{name} is missing")
    cams = json.loads((scene_dir / cams_file).read_text())
    if not cams:
        raise Incomplete(f"{cams_file} is empty")
    tgt = target(str(scene_dir / mesh), (scene_dir / mesh).stat().st_mtime_ns)
    metric = scene.get("scale_method", "none") != "none"
    width, height = 2 * tgt["half_width"], tgt["height"]
    base = {
        "site": scene_dir.name,
        "rev": scene.get("revision", 1),
        "cameras": cams_file,
        "registered": len(cams),
        "units": "m" if metric else "scene",
        "target": {"centre": tgt["centre"], "width": width, "height": height},
    }
    if metric and width < INDOOR_MAX_M:
        return {
            **base,
            "mode": "interior",
            "verdict": "not_applicable",
            "legs": [],
            "note": (
                f"This scan is only {width:.0f} metres across, so it looks like a room or an "
                "object. The capture coach only plans drone flights around a building."
            ),
        }

    off, pitch, weight = views(cams, tgt)
    aimed = weight > 0
    side_view = (
        aimed & (pitch < NADIR_DEG) & (np.linalg.norm(off, axis=1) > 0.25 * tgt["half_width"])
    )
    north = _horizontal(scene["north"]) if scene.get("north") else None
    first = np.flatnonzero(side_view)
    if north is not None:
        zero = north
    elif len(first):
        zero = off[first[0]] / np.linalg.norm(off[first[0]])
    else:
        zero = np.array([0.0, -1.0])  # nothing aimed at a side: scene -Z
    quarter = np.array([-zero[1], zero[0]])  # zero x up, i.e. 90 degrees clockwise from above
    bearing = np.degrees(np.arctan2(off @ quarter, off @ zero)) % 360
    side = np.round(bearing / (360 / SIDES)).astype(int) % SIDES
    hist = np.bincount(side[side_view], weights=weight[side_view], minlength=SIDES)
    labels = COMPASS if north is not None else START
    seen = [bool(v >= MIN_VIEWS) for v in hist]
    bands = {
        "nadir": int((aimed & (pitch >= NADIR_DEG)).sum()),
        "oblique": int((aimed & (pitch >= LEVEL_DEG) & (pitch < NADIR_DEG)).sum()),
        "level": int((aimed & (pitch < LEVEL_DEG)).sum()),
    }
    legs = plan_legs(seen, bands, tgt["half_width"], height)
    for leg in legs:
        leg["radius_widths"] = round(leg["radius"] / width, 2)
        leg["altitude_widths"] = round(leg["altitude"] / width, 2)
    n_seen = sum(seen)
    return {
        **base,
        "mode": "exterior",
        "zero": "north" if north is not None else "start_side",
        "ring": {
            "centre": tgt["centre"],
            "radius": 1.1 * tgt["half_width"],
            "zero_dir": [float(zero[0]), 0.0, float(zero[1])],
            "quarter_dir": [float(quarter[0]), 0.0, float(quarter[1])],
        },
        "sides": [
            {
                "label": labels[k],
                "bearing_deg": k * 360 / SIDES,
                "views": round(float(hist[k]), 1),
                "seen": seen[k],
            }
            for k in range(SIDES)
        ],
        "seen": n_seen,
        "aimed": int(aimed.sum()),
        "pitch": bands,
        "legs": legs,
        "verdict": "good"
        if n_seen >= 6 and not legs
        else "reshoot_most"
        if n_seen < SIDES / 2
        else "reshoot_some",
    }


# --- the spoken line -----------------------------------------------------------------------


def _dist(value: float, cov: dict) -> str:
    """'40 m' in a metric scene, else in building widths ('1.4 building widths'): the width is
    the one size a one-sided scan still measures well."""
    if cov["units"] == "m":
        return f"{value:.0f} m" if value < 20 else f"{5 * round(value / 5)} m"
    rel = round(value / cov["target"]["width"], 1)
    return "one building width" if rel == 1 else f"{rel} building widths"


def facts(cov: dict) -> dict:
    """What the spoken line may say: sides by label and one phrase per leg, numbers included."""
    labels = [s["label"] for s in cov["sides"]]
    covered = [s["label"] for s in cov["sides"] if s["seen"]]
    legs = []
    for leg in cov["legs"]:
        out, up = _dist(leg["radius"], cov), _dist(leg["altitude"], cov)
        if leg["pattern"] == "orbit":
            a, b = labels[leg["sides"][0]], labels[leg["sides"][-1]]
            span = f"from the {a} round to the {b}" if a != b else f"on the {a} side"
            if leg["sweep_deg"] == 360:
                span = "all the way round"
            legs.append(
                f"orbit {span}, {out} out from the middle and {up} up, camera "
                f"{leg['gimbal_pitch_deg']} degrees down"
            )
        elif leg["pattern"] == "nadir_grid":
            legs.append(f"a straight-down grid over the roof, {up} up")
        else:
            legs.append(
                f"one slow eave pass all the way round at eave height ({up} up), camera "
                f"{leg['gimbal_pitch_deg']} degrees down"
            )
    return {"covered": covered, "of": SIDES, "legs": legs}


def template(f: dict) -> str:
    covered = ", ".join(f["covered"]) or "none"
    first = f"You've covered {len(f['covered'])} of {f['of']} sides ({covered})."
    if not f["legs"]:
        return f"{first} Nothing left to fly."
    return f"{first} Next, {'; then '.join(f['legs'])}."


async def _ask(f: dict) -> dict:
    resp = await llm.chat(  # chat() logs the role, latency and cost
        "coach",
        [{"role": "user", "content": PROMPT.format(draft=template(f))}],
        max_tokens=150,
        max_retries=0,  # a timed-out call may still bill; don't pay twice
        timeout=20,
    )
    cost = llm.cost_usd(resp.usage)
    cost = round(cost, 6) if cost is not None else None
    return {"text": (resp.choices[0].message.content or "").strip(), "cost_usd": cost}


async def spoken(cov: dict) -> tuple[str, str]:
    """(line, source): Grok's phrasing cached by the facts, else the template (OFFLINE with no
    cache, no key, any API failure)."""
    if cov["mode"] != "exterior":
        return cov["note"], "template"
    f = facts(cov)
    _, model = llm.resolve_role("coach")
    key = {"model": model, "prompt": PROMPT, "facts": f}
    hit = cache.get("coach", key) is not None
    try:
        reply = await cache.cached("coach", key, lambda: _ask(f))
        if reply["text"]:
            return " ".join(reply["text"].split()), "cache" if hit else model
    except Exception as exc:  # noqa: BLE001 -- no key, HTTP, timeout: the template below
        logger.warning("coach: phrasing failed, using the template: %r", exc)
    return template(f), "template"


async def coach(scene_dir: Path) -> dict:
    """`coverage()` plus `spoken`/`spoken_source`: the `show_coverage` action's args."""
    cov = await asyncio.to_thread(coverage, scene_dir)
    cov["spoken"], cov["spoken_source"] = await spoken(cov)
    return cov
