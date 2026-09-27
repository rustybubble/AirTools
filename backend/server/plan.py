""" "Where does it go?" planner (docs/research/grok-ideas/g3-round2.md pick 3, docs/api.md
`POST /scene/plan`): "LED strip under these cabinets" -> ghost segments/points on the scan.

Local Python does all the geometry over the scene's structure layer (`structure.r<rev>.json`,
`airtools.structure/1`: glTF frame, metres, +Y up). One fast LLM call (role "plan", xAI by
default) only turns the sentence into a planner name plus arguments, from a compact text summary
of the scene; it never outputs coordinates. G3's live checks: Grok alone took 194 s (reasoning +
code interpreter) or picked the wrong cabinets (fast model), so it never touches geometry here.
"""

import json
import logging
import math
import time
import uuid
from pathlib import Path
from typing import Any, Literal

import numpy as np
from pydantic import BaseModel, Field

from server import jobs, llm
from server.config import get_settings
from server.models import Part

logger = logging.getLogger(__name__)

UPPER_ABOVE_COUNTER_M = 0.3  # a door whose bottom is this far above the counter is a wall cabinet
UPPER_FRACTION = 0.4  # no counter plane: wall cabinets are the top 40 % of the objects' span
JOIN_M = 0.02  # bottom edges closer than this along the wall join into one run
SAME_WALL_M = 0.25  # bottom edges further apart than this across the wall are separate runs
LABEL = "Planned from the scan; check with the tape"


class PlanError(Exception):
    """`status` is the HTTP code for `POST /scene/plan`; `spoken` is safe to read out."""

    def __init__(self, status: int, spoken: str):
        super().__init__(spoken)
        self.status = status
        self.spoken = spoken


class Choice(BaseModel):
    """The LLM's whole job: which planner, and its arguments. No coordinates."""

    planner: Literal["under_cabinet_runs", "along_edge", "centered_on", "measure", "none"]
    target_id: str | None = Field(
        description="edge, object or plane id from the scene summary; null for under_cabinet_runs"
    )
    spacing_mm: float | None = Field(description="along_edge: spacing the user asked for")
    count: int | None = Field(description="along_edge: a fixed number of items instead")
    stock_length_m: float | None = Field(
        description="under_cabinet_runs: length the part is sold in, e.g. 1 for 1 m strips"
    )
    hint: str = Field(description="planner none only: one short sentence saying what you need")


SYSTEM_PROMPT = (
    "You route an AR home-improvement request to one local geometry planner. You never compute "
    "positions. Planners: under_cabinet_runs = continuous runs under the wall (upper) cabinets, "
    "for LED strips or under-cabinet lights; set stock_length_m if the part comes in fixed "
    "lengths. along_edge = evenly spaced items along one edge, or along the bottom of one "
    "object: hangers, hooks, brackets; set spacing_mm from the user's words, or count if they "
    "give a number of items. centered_on = one part centred on one object or plane: a light over "
    "the sink, a handle on a door, a TV on a wall. measure = the length of one edge, object or "
    "plane (a counter or wall is a plane). none = the request is unclear, or it names something "
    "the summary doesn't list (a gutter, a window, a fence); put what you need in hint, e.g. "
    "'Point at the gutter.' target_id must be an id from the summary. When the user names a "
    "cabinet, door, drawer or appliance, target the object id, never one of its edges. 'this', "
    "'that' and 'here' mean the object nearest the pointer."
)


# --- structure layer --------------------------------------------------------------------------


def load_structure(site: str) -> tuple[dict, dict]:
    """(structure, scene.json) for `site`. 404 unknown site, 409 no structure layer (the
    preview, `structure: null`, or a missing file). `site` is validated by the caller."""
    site_dir = Path(get_settings().SCENE_DIR) / site
    try:
        scene = json.loads((site_dir / "scene.json").read_text())
    except (OSError, json.JSONDecodeError) as exc:
        raise PlanError(404, "I don't have that scene.") from exc
    name = (scene.get("structure") or {}).get("file")
    path = site_dir / Path(name).name if name else None
    if path is None or not path.is_file():
        raise PlanError(409, "I can only plan on the full scan.")
    return json.loads(path.read_text()), scene


def _corners(o: dict) -> np.ndarray:
    return np.asarray(o["corners3d"], float)


def _bottom(o: dict) -> np.ndarray:
    """The object's bottom edge: its two lowest corners, shape (2, 3)."""
    c = _corners(o)
    return c[np.argsort(c[:, 1])[:2]]


def _hdir(seg: np.ndarray) -> np.ndarray:
    """Horizontal unit direction of a segment."""
    d = seg[1] - seg[0]
    d[1] = 0
    return d / (np.linalg.norm(d) or 1)


def _overlap(x: np.ndarray, y: np.ndarray) -> float:
    return max(0.0, min(x.max(), y.max()) - max(x.min(), y.min()))


def plane_outline(p: dict) -> np.ndarray:
    """3D outline of a plane: `polygon3d`, a 3-number `polygon`, or a 2-number `polygon` in
    (u, v = normal x u) around `origin`."""
    poly = np.asarray(p.get("polygon3d") or p.get("polygon") or [], float)
    if poly.ndim == 2 and poly.shape[1] == 2:
        u = np.asarray(p["u"], float)
        v = np.cross(np.asarray(p["normal"], float), u)
        poly = np.asarray(p["origin"], float) + poly[:, :1] * u + poly[:, 1:] * v
    return poly.reshape(-1, 3)


def _counter_y(s: dict) -> float | None:
    """Height of the counter: the biggest horizontal plane with cabinets wholly below and
    wholly above it."""
    spans = [
        (c[:, 1].min(), c[:, 1].max())
        for c in (_corners(o) for o in s["objects"] if o["label"] in ("cabinet_door", "drawer"))
    ]
    best: tuple[float, float] | None = None
    for p in s["planes"]:
        outline = plane_outline(p)
        if abs(p["normal"][1]) < 0.95 or not len(outline):
            continue
        y = float(outline[:, 1].mean())
        if any(top < y for _, top in spans) and any(lo > y for lo, _ in spans):
            area = p.get("area_m2") or 0.0
            if best is None or area > best[0]:
                best = (area, y)
    return best[1] if best else None


def upper_doors(s: dict) -> list[dict]:
    """Wall-cabinet doors: above the counter band, minus any door that lies inside a bigger
    rectangle of the same plane (o9/o11 inside the outer o10 on the kitchen)."""
    counter = _counter_y(s)
    if counter is not None:
        floor_y = counter + UPPER_ABOVE_COUNTER_M
    else:
        ys = np.concatenate([_corners(o)[:, 1] for o in s["objects"]])
        floor_y = ys.max() - UPPER_FRACTION * (ys.max() - ys.min())
    doors = [
        o
        for o in s["objects"]
        if o["label"] == "cabinet_door" and _corners(o)[:, 1].min() > floor_y
    ]

    def covered(o: dict) -> bool:
        a, d, area = _corners(o), _hdir(_bottom(o)), o["w_m"] * o["h_m"]
        return any(
            b is not o
            and b["plane"] == o["plane"]
            and b["w_m"] * b["h_m"] > area
            and _overlap(a @ d, _corners(b) @ d) * _overlap(a[:, 1], _corners(b)[:, 1]) > 0.5 * area
            for b in doors
        )

    return [o for o in doors if not covered(o)]


def _segment(a: np.ndarray, b: np.ndarray, length: float | None = None) -> dict:
    length = float(np.linalg.norm(b - a)) if length is None else length
    return {
        "a": [round(float(x), 3) for x in a],
        "b": [round(float(x), 3) for x in b],
        "length_m": round(float(length), 3),
    }


# --- planners (pure) ------------------------------------------------------------------------


def under_cabinet_runs(s: dict) -> list[dict]:
    """Continuous runs under the wall cabinets: the doors' bottom edges, grouped per wall line
    and merged as intervals along it (2 cm join). Kitchen fixture: one run, 2.432 m."""
    edges = sorted((_bottom(o) for o in upper_doors(s)), key=lambda e: -np.linalg.norm(e[1] - e[0]))
    walls: list[tuple[np.ndarray, np.ndarray, list[np.ndarray]]] = []
    for e in edges:
        d, mid = _hdir(e), e.mean(0)
        for origin, axis, members in walls:
            off = mid - origin
            off[1] = 0
            if abs(d @ axis) > 0.9 and np.linalg.norm(off - (off @ axis) * axis) < SAME_WALL_M:
                members.append(e)
                break
        else:
            walls.append((mid, d, [e]))

    runs = []
    for origin, axis, members in walls:
        spans = []  # per edge (t0, p0, t1, p1) along the wall axis, t0 <= t1
        for e in members:
            (u0, q0), (u1, q1) = sorted((((p - origin) @ axis, p) for p in e), key=lambda x: x[0])
            spans.append((u0, q0, u1, q1))
        spans.sort(key=lambda sp: sp[0])
        t0, p0, t1, p1 = spans[0]
        for u0, q0, u1, q1 in spans[1:]:
            if u0 > t1 + JOIN_M:
                runs.append(_segment(p0, p1, t1 - t0))
                t0, p0, t1, p1 = u0, q0, u1, q1
            elif u1 > t1:
                t1, p1 = u1, q1
        runs.append(_segment(p0, p1, t1 - t0))
    return sorted(runs, key=lambda r: -r["length_m"])


def hanger_plan(run_length_m: float, spacing_mm: float = 600, end_inset_mm: float = 150) -> dict:
    """Evenly spaced gutter hangers along a run: never wider apart than `spacing_mm`, with the
    end hangers `end_inset_mm` in from each end (capped to a quarter of short runs).

    server/realtime.py imports this one too, so voice and plan give the same count."""
    run_mm = float(run_length_m) * 1000
    spacing_mm = float(spacing_mm)
    if not 0 < run_mm <= 100_000 or spacing_mm <= 0:
        raise ValueError("run_length_m must be in (0, 100] and spacing_mm > 0")
    inset = min(float(end_inset_mm), run_mm / 4)
    span = run_mm - 2 * inset
    gaps = max(1, math.ceil(span / spacing_mm - 1e-9))
    step = span / gaps
    return {
        "count": gaps + 1,
        "spacing_mm": round(step),
        "positions_mm": [round(inset + i * step) for i in range(gaps + 1)],
        "spoken": f"{gaps + 1} hangers, one every {round(step)} millimetres.",
    }


def _find(s: dict, target_id: str | None) -> tuple[str, dict]:
    for kind in ("edges", "objects", "planes"):
        for item in s.get(kind, []):
            if item["id"] == target_id:
                return kind, item
    raise PlanError(422, "I can't find that in the scan. Point at it and ask again.")


def target_segment(s: dict, target_id: str | None) -> np.ndarray:
    """An edge's endpoints, an object's bottom edge, or a plane's longest horizontal extent
    through its centre ("how long is the counter?")."""
    kind, item = _find(s, target_id)
    if kind == "edges":
        return np.asarray([item["a"], item["b"]], float)
    if kind == "objects":
        return _bottom(item)
    pts = plane_outline(item)
    c = pts.mean(0)
    d2 = np.linalg.svd((pts - c)[:, [0, 2]], full_matrices=False)[2][0]
    d = np.array([d2[0], 0.0, d2[1]])
    ts = (pts - c) @ d
    return np.array([c + ts.min() * d, c + ts.max() * d])


def along_edge(
    s: dict, target_id: str | None, spacing_mm: float | None = None, count: int | None = None
) -> dict:
    """Evenly spaced points along an edge: `hanger_plan` spacing (150 mm end inset), or `count`
    items each centred in an equal share of the edge."""
    seg = target_segment(s, target_id)
    length = float(np.linalg.norm(seg[1] - seg[0]))
    if count:
        ts = [(i + 0.5) / count for i in range(count)]
        spacing = round(length * 1000 / count)
    else:
        hp = hanger_plan(length, spacing_mm or 600)
        ts = [mm / 1000 / length for mm in hp["positions_mm"]]
        spacing = hp["spacing_mm"]
    points = [[round(float(x), 3) for x in seg[0] + t * (seg[1] - seg[0])] for t in ts]
    return {"segments": [_segment(*seg)], "points": points, "spacing_mm": spacing}


def centered_on(
    s: dict, target_id: str | None, dims_mm: dict | None = None, toward: list | None = None
) -> dict:
    """The target's centre, pushed out by half the part depth along its normal (turned to face
    `toward`, the scene's viewer spawn). `fits` is False when the part is wider than the object."""
    kind, item = _find(s, target_id)
    if kind == "edges":
        raise PlanError(422, "Point at a surface or a door, not an edge.")
    if kind == "objects":
        c = _corners(item)
        centre = c.mean(0)
        n = np.cross(c[1] - c[0], c[3] - c[0])
    else:
        centre = plane_outline(item).mean(0)
        n = np.asarray(item["normal"], float)
    n = n / (np.linalg.norm(n) or 1)
    if toward is not None and (np.asarray(toward, float) - centre) @ n < 0:
        n = -n
    depth_m = (dims_mm or {}).get("d", 0) / 1000
    fits = None
    if kind == "objects" and dims_mm:
        fits = dims_mm["w"] <= item["w_m"] * 1000
    point = [round(float(x), 3) for x in centre + n * depth_m / 2]
    return {"segments": [], "points": [point], "fits": fits, "label": item.get("label")}


# --- scene summary for the LLM ----------------------------------------------------------------


def _orient(p: dict) -> str:
    ny = p["normal"][1]
    return "horizontal" if abs(ny) > 0.95 else "vertical" if abs(ny) < 0.3 else "sloped"


def _seg_dist(p: np.ndarray, seg: np.ndarray) -> float:
    d = seg[1] - seg[0]
    t = np.clip((p - seg[0]) @ d / (d @ d or 1), 0, 1)
    return float(np.linalg.norm(seg[0] + t * d - p))


def summary(s: dict, pointer: list | None = None, part: Part | None = None) -> str:
    """Compact text the LLM chooses from: ids, labels, sizes and heights; no coordinates."""
    counter = _counter_y(s)
    uppers = {o["id"] for o in upper_doors(s)}
    labels = sorted({o["label"] for o in s["objects"]})
    known = f"Object labels: {', '.join(labels)}; nothing else is known in this scan."
    lines = [f"Scene: metres, y = height (+Y up). {known}"]
    if counter is not None:
        lines.append(f"Counter top at y {counter:.2f}.")

    edges = sorted(s.get("edges", []), key=lambda e: -np.linalg.norm(np.subtract(e["b"], e["a"])))
    if pointer is not None:
        p = np.asarray(pointer, float)
        near_e = min(s.get("edges", []), key=lambda e: _seg_dist(p, np.asarray([e["a"], e["b"]])))
        near_o = min(s["objects"], key=lambda o: float(np.linalg.norm(_corners(o).mean(0) - p)))
        lines.append(
            f"Pointer is on object {near_o['id']} ({near_o['label']}); nearest edge {near_e['id']}."
        )
        edges = [near_e, *[e for e in edges if e is not near_e]]

    lines.append("Objects (id label width x height, y bottom..top):")
    for o in s["objects"]:
        y = _corners(o)[:, 1]
        tag = " wall-cabinet" if o["id"] in uppers else ""
        lines.append(
            f"{o['id']} {o['label']}{tag} {o['w_m']:.2f}x{o['h_m']:.2f} y {y.min():.2f}..{y.max():.2f}"
        )
    lines.append("Longest edges (id length orientation y):")
    for e in edges[:12]:
        a, b = np.asarray(e["a"]), np.asarray(e["b"])
        horiz = abs(b[1] - a[1]) < 0.3 * np.linalg.norm(b - a)
        lines.append(
            f"{e['id']} {np.linalg.norm(b - a):.2f} m {'horizontal' if horiz else 'vertical'} "
            f"y {(a[1] + b[1]) / 2:.2f}"
        )
    lines.append("Largest planes (id orientation area y):")
    for p in sorted(s["planes"], key=lambda p: -(p.get("area_m2") or 0))[:10]:
        lines.append(
            f"{p['id']} {_orient(p)} {p.get('area_m2') or 0:.2f} m2 y {plane_outline(p)[:, 1].mean():.2f}"
        )
    if part is not None:
        d = part.dims_mm
        spacing = f", spacing {part.spacing_mm:g} mm" if part.spacing_mm else ""
        lines.append(
            f"Selected part: {part.name}, {d.w:g} x {d.d:g} x {d.h:g} mm (w x d x h){spacing}."
        )
    return "\n".join(lines)


# --- the endpoint's whole flow ----------------------------------------------------------------


def _say_count(n: int) -> str:
    return ["No", "One", "Two", "Three", "Four", "Five"][n] if n < 6 else str(n)


async def plan(site: str, utterance: str, context: dict[str, Any]) -> dict:
    s, scene = load_structure(site)
    part = jobs.load_part(context["selected_part_id"]) if context.get("selected_part_id") else None
    dims = context.get("dims_mm") or (part.dims_mm.model_dump() if part else None)
    user = f"{summary(s, context.get('pointer'), part)}\n\nRequest: {utterance}"

    start = time.monotonic()
    try:
        choice = await llm.extract(
            "plan",
            [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": user}],
            Choice,
        )
    except ValueError as exc:  # pydantic.ValidationError / JSONDecodeError: bad reply, draw nothing
        raise PlanError(502, "I couldn't work that out. Try saying it another way.") from exc
    latency_ms = round((time.monotonic() - start) * 1000)
    logger.info("plan site=%s choice=%s latency_ms=%d", site, choice.model_dump(), latency_ms)

    unit = part.name if part else "item"
    count: int | None = None
    if choice.planner == "none":
        raise PlanError(422, choice.hint or "Point at what you mean and ask again.")
    if choice.planner == "under_cabinet_runs":
        geo = {"segments": under_cabinet_runs(s), "points": []}
        if not geo["segments"]:
            raise PlanError(422, "I don't see wall cabinets in this scan.")
        total = sum(r["length_m"] for r in geo["segments"])
        n = len(geo["segments"])
        spoken = f"{_say_count(n)} run{'s' * (n > 1)} under the wall cabinets, {total:.2f} metres."
        if choice.stock_length_m:
            stock = choice.stock_length_m
            count = math.ceil(total / stock - 1e-9)
            unit = f"{stock:g} m strip"
            cut = ", cut the last" if total % stock > 0.01 else ""
            spoken += f" {_say_count(count)} {stock:g}-metre strips{cut}."
    elif choice.planner == "along_edge":
        spacing = choice.spacing_mm or (part.spacing_mm if part else None)
        geo = along_edge(s, choice.target_id, spacing, choice.count)
        total = geo["segments"][0]["length_m"]
        count = len(geo["points"])
        spoken = (
            f"{_say_count(count)} along {total:.2f} metres, one every {geo['spacing_mm']} "
            f"millimetres."
        )
    elif choice.planner == "centered_on":
        geo = centered_on(
            s, choice.target_id, dims, (scene.get("recommended_spawn") or {}).get("pos")
        )
        total, count = 0.0, 1
        spoken = f"Centred on the {(geo['label'] or 'surface').replace('_', ' ')}."
        if geo["fits"] is False:
            spoken += " It's wider than that, so check the clearance."
    else:  # measure
        seg = target_segment(s, choice.target_id)
        geo = {"segments": [_segment(*seg)], "points": []}
        total = geo["segments"][0]["length_m"]
        spoken = f"That's {total:.2f} metres."

    return {
        "plan_id": f"p-{uuid.uuid4().hex[:8]}",
        "planner": choice.planner,
        "target_id": choice.target_id,
        "segments": geo["segments"],
        "points": geo["points"],
        "total_m": round(total, 2),
        "count": count,
        "unit": unit,
        "spacing_mm": geo.get("spacing_mm"),
        "fits": geo.get("fits"),
        "spoken": spoken,
        "label": LABEL,
        "latency_ms": latency_ms,
    }
