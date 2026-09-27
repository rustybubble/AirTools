"""Measuring by voice (the headset tapes; the server only decides what to tape).

"measure the length of the top roof from end to end", "how long is the roof", "measure the height
of the tower platform", "how wide is the window". A measuring request never becomes F10's
condition survey (that answers damage / problems / condition / "what's wrong"). Three outcomes:

1. The thing names structure-layer objects the scene has ("the window", "every cabinet door"):
   the B1 tape `survey {label, where, measure}`, as before.
2. The scene has planes but no such objects (the GT LCC scans, the hospital): a structural tape
   from the planes -- `measure_edges {label, request_id, segments: [{a, b}]}`, the endpoints in the
   structure file's frame (glTF, like `pointer`); the headset tapes a -> b with its real tape
   (snapping to the scan) and logs it to the notebook under `label`:
   - roof / top / canopy / platform / deck ...: the highest large near-horizontal plane; its
     length (or "end to end") / width along its major / minor horizontal axis through its centre;
     its height = straight down from its centre to the lowest large near-horizontal plane;
   - floor / ground / plaza ...: the lowest one, the same way;
   - wall / facade: the largest near-vertical plane; its height along up, its length across;
   - building / tower / "it": the height from the ground plane to the top plane;
   - "this" / "that" with the headset's `pointer`: the plane nearest it.
   "Up" is the area-weighted normal of the near-horizontal planes (the GT scans' +Y is ~15 deg
   off), so "near-horizontal" and "height" mean what they look like.
3. Neither: `equip_tool {tool: "tape"}` and "Measure's ready: pinch one end of the roof, then the
   other."

Pure apart from reading the structure file (scene_digest's loader and cache). The numbers the
user hears always come from the headset's tape, never from here.
"""

import math
import re
from dataclasses import dataclass
from typing import Any

import numpy as np

from server import replace, scene_digest

_I = re.IGNORECASE
_DIM = r"(?:length|height|width|depth|size|dimensions|span|distance)"
_E2E = r"(?P<e2e>\s+(?:from\s+)?(?:one\s+)?end\s+to\s+(?:the\s+other\s+)?end|\s+all\s+the\s+way(?:\s+across)?|\s+across)?"
_DET = r"(?:(?:the|this|that|our|my|a)\s+)?"
_THING = r"(?P<thing>[a-z0-9][a-z0-9'\- ]{0,60}?)"
_TAIL = replace._TAIL
_LEAD = replace._LEAD

_MEASURE_RE = re.compile(
    _LEAD
    + r"(?:(?:measure|tape(?:\s+measure)?|size\s+up)\s+(?:the\s+)?(?:(?P<dim>"
    + _DIM
    + r")\s+(?:of|across|along)\s+)?"
    + r"|(?:get|give|find|survey|check)\s+(?:me\s+)?the\s+(?P<dim2>"
    + _DIM
    + r")\s+(?:of|across|along)\s+)"
    + _DET
    + _THING
    + _E2E
    + _TAIL,
    _I,
)
_HOW_RE = re.compile(
    _LEAD
    + r"how\s+(?P<adj>long|tall|wide|high|deep|big|large|far\s+across)\s+(?:is|are|'s)\s+"
    + _DET
    + _THING
    + _E2E
    + _TAIL,
    _I,
)
_WHAT_RE = re.compile(
    _LEAD
    + r"(?:what(?:'s|\s+is|\s+are)|tell\s+me)\s+the\s+(?P<dim>"
    + _DIM
    + r")\s+(?:of|across|along)\s+"
    + _DET
    + _THING
    + _E2E
    + _TAIL,
    _I,
)
_ADJ = {
    "long": "length",
    "tall": "height",
    "high": "height",
    "wide": "width",
    "deep": "depth",
    "big": "size",
    "large": "size",
    "far across": "length",
}
# Condition words: these phrasings are F10's survey, never a measurement.
_CONDITION_RE = re.compile(
    r"\b(?:condition|damage[ds]?|damaged|cracks?|cracked|rot(?:ten|ting)?|rust(?:y|ed|ing)?|leak"
    r"(?:s|y|ing)?|problems?|issues?|wrong|broken|inspect(?:ion)?|worn|missing)\b",
    _I,
)
_PRONOUN = {"it", "this", "that", "this one", "that one", "them", "these", "those", "here", "there"}

# B1 labels (tape_survey.LABELS) by the words people say.
_LABEL_WORDS = [
    ("cabinet_door", r"cabinet\s+doors?|cupboard\s+doors?"),
    ("drawer", r"drawers?"),
    ("window", r"windows?"),
    ("door", r"doors?"),
    ("panel", r"panels?"),
    ("appliance", r"appliances?"),
]
TOP_WORDS = r"roof(?:top)?|top|canopy|ceiling|platform|deck|terrace|awning|overhang|cover"
GROUND_WORDS = r"floor|ground|slab|plaza|pavement|patio|courtyard|lot"
WALL_WORDS = r"wall|facade|façade|front|side"
WHOLE_WORDS = r"building|tower|structure|house|pavilion|hospital|gym|gymnasium|place"


@dataclass
class Ask:
    """A measuring request: what to measure and which dimension."""

    dimension: str  # length | width | height | depth | size
    thing: str | None  # "top roof", "tower platform"; None for "it" / "this"
    end_to_end: bool = False


def condition(text: str | None) -> bool:
    """Damage / problems / condition / inspect / "what's wrong": F10's survey, not a tape."""
    return bool(_CONDITION_RE.search(text or ""))


def parse(text: str) -> Ask | None:
    """The measuring request in a whole utterance, else None (condition phrasings included)."""
    t = re.sub(r"\s+", " ", replace.normalize(text or "").strip())
    if not t or len(t) > 200 or _CONDITION_RE.search(t):
        return None
    for regex in (_WHAT_RE, _HOW_RE, _MEASURE_RE):
        m = regex.match(t)
        if not m:
            continue
        g = m.groupdict()
        dim = g.get("dim") or g.get("dim2") or _ADJ.get((g.get("adj") or "").lower()) or "length"
        dim = dim.lower()
        dim = {"span": "length", "distance": "length", "dimensions": "size"}.get(dim, dim)
        thing = re.sub(r"\s+(?:is|are|please)$", "", (g.get("thing") or "").strip().lower())
        thing = re.sub(r"^(?:the|this|that|our|my)\s+", "", thing).strip() or None
        if thing in _PRONOUN:
            thing = None
        if thing and re.fullmatch(r"(?:the\s+)?(?:gap|hole|opening|cavity)", thing):
            return None  # the replace flow's measure_cavity
        return Ask(dimension=dim, thing=thing, end_to_end=bool(g.get("e2e")))
    return None


def label_for(thing: str | None) -> tuple[str, bool] | None:
    """(B1 label, plural) a thing names ("the windows" -> window, True), else None."""
    if not thing:
        return None
    for label, words in _LABEL_WORDS:
        m = re.search(rf"\b(?:{words})\b", thing, _I)
        if m:
            plural = m.group(0).lower().endswith("s") or bool(
                re.search(r"\b(?:all|every|each)\b", thing, _I)
            )
            return label, plural
    return None


# --- the planes --------------------------------------------------------------------------------


@dataclass
class Plane:
    id: str
    normal: np.ndarray
    points: np.ndarray  # the polygon (N x 3), file frame
    area: float

    @property
    def centre(self) -> np.ndarray:
        return self.points.mean(axis=0)


def _planes(layer: dict[str, Any]) -> list[Plane]:
    out = []
    for p in layer.get("planes") or []:
        poly = p.get("polygon") or []
        if len(poly) < 3 or not p.get("normal"):
            continue
        n = np.asarray(p["normal"], dtype=float)
        norm = np.linalg.norm(n)
        if norm == 0:
            continue
        out.append(Plane(str(p.get("id")), n / norm, np.asarray(poly, dtype=float),
                         float(p.get("area_m2") or 0.0)))  # fmt: skip
    return out


def up_vector(planes: list[Plane]) -> np.ndarray:
    """Area-weighted normal of the planes within 35 deg of +Y (flipped to point up); +Y when
    there are none."""
    acc = np.zeros(3)
    for p in planes:
        n = p.normal if p.normal[1] >= 0 else -p.normal
        if n[1] > math.cos(math.radians(35)):
            acc += n * max(p.area, 0.01)
    norm = np.linalg.norm(acc)
    return acc / norm if norm > 0 else np.array([0.0, 1.0, 0.0])


def _horizontal(planes: list[Plane], up: np.ndarray, tilt_deg: float = 20.0) -> list[Plane]:
    """Planes within `tilt_deg` of level (20: floors, flat roofs, platforms; 50: pitched roofs
    too) of real size (>= 1 m2 and >= 10 % of the largest such)."""
    flat = [p for p in planes if abs(float(p.normal @ up)) > math.cos(math.radians(tilt_deg))]
    if not flat:
        return []
    big = max(p.area for p in flat)
    return [p for p in flat if p.area >= max(1.0, 0.1 * big)]


def _vertical(planes: list[Plane], up: np.ndarray) -> list[Plane]:
    return [p for p in planes if abs(float(p.normal @ up)) < math.sin(math.radians(20))]


def _height(p: np.ndarray, up: np.ndarray) -> float:
    return float(p @ up)


def _extent(plane: Plane, axis: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """The plane's end points along a horizontal `axis`, through its centre."""
    c = plane.centre
    t = (plane.points - c) @ axis
    return c + axis * float(t.min()), c + axis * float(t.max())


def _axes(plane: Plane, up: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """(major, minor) horizontal axes of a plane's outline (PCA of its points, up removed)."""
    q = plane.points - plane.centre
    q = q - np.outer(q @ up, up)
    _, _, vt = np.linalg.svd(q, full_matrices=False)
    major = vt[0] - up * float(vt[0] @ up)
    major /= np.linalg.norm(major)
    minor = np.cross(up, major)
    return major, minor / np.linalg.norm(minor)


def _down_to(plane_point: np.ndarray, floor: Plane, up: np.ndarray) -> np.ndarray:
    return plane_point - up * (_height(plane_point, up) - _height(floor.centre, up))


def _seg(a: np.ndarray, b: np.ndarray) -> dict[str, list[float]]:
    return {"a": [round(float(v), 4) for v in a], "b": [round(float(v), 4) for v in b]}


def _words(thing: str | None, words: str) -> bool:
    return bool(thing) and bool(re.search(rf"\b(?:{words})s?\b", thing or "", _I))


def segments(layer: dict[str, Any], ask: Ask, pointer: Any = None) -> tuple[list[dict], str] | None:
    """([{a, b}], what) to tape for `ask` from the layer's planes, else None."""
    planes = _planes(layer)
    if not planes:
        return None
    up = up_vector(planes)
    flat = sorted(_horizontal(planes, up), key=lambda p: _height(p.centre, up))
    walls = sorted(_vertical(planes, up), key=lambda p: -p.area)
    thing = ask.thing
    target: Plane | None = None
    if thing is None and isinstance(pointer, list | tuple) and len(pointer) == 3:
        try:
            ptr = np.asarray([float(v) for v in pointer])
        except (TypeError, ValueError):
            ptr = None
        if ptr is not None:
            target = min(
                planes, key=lambda p: float(np.min(np.linalg.norm(p.points - ptr, axis=1)))
            )
    elif _words(thing, TOP_WORDS):
        roofs = sorted(_horizontal(planes, up, 50.0), key=lambda p: _height(p.centre, up))
        target = roofs[-1] if roofs else None  # a pitched roof counts (the gym's)
    elif _words(thing, GROUND_WORDS):
        target = flat[0] if flat else None
    elif _words(thing, WALL_WORDS):
        target = walls[0] if walls else None
    elif _words(thing, WHOLE_WORDS):
        if ask.dimension == "height" and len(flat) >= 2:
            top = flat[-1]
            return [_seg(top.centre, _down_to(top.centre, flat[0], up))], "height"
        target = flat[-1] if flat and ask.dimension != "height" else None
    if target is None:
        return None
    is_flat = abs(float(target.normal @ up)) > math.cos(math.radians(50))
    if ask.dimension == "height":
        if is_flat:
            if len(flat) < 2 or target is flat[0]:
                return None  # the ground has no height above the ground
            return [_seg(target.centre, _down_to(target.centre, flat[0], up))], "height"
        h = (target.points - target.centre) @ up
        c = target.centre
        return [_seg(c + up * float(h.min()), c + up * float(h.max()))], "height"
    if is_flat:
        major, minor = _axes(target, up)
        axis = minor if ask.dimension in ("width", "depth") else major
        return [
            _seg(*_extent(target, axis))
        ], ask.dimension if ask.dimension != "size" else "length"
    across = np.cross(up, target.normal)
    across /= np.linalg.norm(across)
    return [_seg(*_extent(target, across))], "length" if ask.dimension != "width" else "width"


def title(ask: Ask, what: str) -> str:
    """The notebook label: "Top roof length", "Tower platform height", "Height"."""
    word = {"size": "length"}.get(what, what)
    thing = (ask.thing or "").strip()
    return f"{thing[:1].upper()}{thing[1:]} {word}" if thing else word.capitalize()


def plan(site: str | None, scale: float, ask: Ask, pointer: Any = None) -> dict[str, Any]:
    """What to do for a measuring request on `site`:
    {"kind": "survey", label, where, measure} | {"kind": "edges", segments, label, what} |
    {"kind": "tape"} (the user pinches the two ends)."""
    d = scene_digest.digest(site, scale) if site else None
    named = label_for(ask.thing)
    if named and d is not None and d.count(named[0]):
        label, plural = named
        measure = ask.dimension if ask.dimension in ("width", "height") else "size"
        return {"kind": "survey", "label": label, "where": "all" if plural else "nearest",
                "measure": measure}  # fmt: skip
    if d is not None and d.structure and not named:
        layer = scene_digest.layer(site)
        found = segments(layer or {}, ask, pointer)
        if found:
            segs, what = found
            return {"kind": "edges", "segments": segs, "label": title(ask, what), "what": what}
    return {"kind": "tape"}
