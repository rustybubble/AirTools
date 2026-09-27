"""Measure and replace by voice (the app's e2e hand-off, docs/handoff p4-e2e): the removable
parts of the open scene package (`parts.r<rev>.json`, docs/api.md "parts") and the gap a removed
one leaves, for the agent's fast path (`agent._replace_route`):

  "remove / take out the dishwasher"      -> remove_component {component_id}
  "put it back"                            -> restore_component {component_id}
  "measure it / the gap"                   -> measure_cavity {component_id}  (the headset tapes it)
  "find a dishwasher that fits"            -> find_part, the search fitted to the gap (SearchRequest.cavity)
  "put it in there"                        -> place_part {part_id, model_url, component_id, fits, clearance_mm}
  "next / previous one", "option 2"        -> place_part for that candidate (the app swaps the model)

Pure helpers plus a file read (works OFFLINE). Sizes: the headset's `cavity` context (its tape,
calibrated) when it sent one, else the file's `cavity.size_m` x the scale calibration.
"""

import json
import logging
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from server.config import get_settings
from server.models import Cavity, Dims, Fit

logger = logging.getLogger(__name__)

_SITE_RE = re.compile(r"^[a-z0-9-]+$")
_FILE_RE = re.compile(r"^[A-Za-z0-9._-]+$")
_cache: dict[tuple[str, str], list[dict[str, Any]]] = {}


def _norm(s: str | None) -> str:
    return re.sub(r"\s+", " ", (s or "").replace("_", " ")).strip().lower()


def singular(noun: str) -> str:
    """ "dishwashers" -> "dishwasher", "ranges" -> "range", "pantries" -> "pantry"."""
    n = _norm(noun)
    if n.endswith(("ches", "shes", "sses", "xes")):
        return n[:-2]
    if n.endswith("ies") and len(n) > 4:
        return n[:-3] + "y"
    if n.endswith("s") and not n.endswith("ss") and len(n) > 3:
        return n[:-1]
    return n


def components(site: str | None) -> list[dict[str, Any]]:
    """The site's components (`scene.json` -> its `parts` entry -> `parts.r<rev>.json`), each
    {id, label, class, removable, cavity}. [] when the site has no parts file (or no site)."""
    if not site or not _SITE_RE.match(site):
        return []
    base = Path(get_settings().SCENE_DIR) / site
    try:
        scene = json.loads((base / "scene.json").read_text())
    except (OSError, ValueError):
        return []
    entry = scene.get("parts")
    name = entry.get("file") if isinstance(entry, dict) else None
    if not name:
        rev = scene.get("revision")
        name = f"parts.r{rev}.json" if rev is not None else None
    if not name or not _FILE_RE.match(name):
        return []
    key = (site, name)
    if key in _cache:
        return _cache[key]
    try:
        doc = json.loads((base / name).read_text())
    except (OSError, ValueError):
        return []
    comps = [c for c in doc.get("components") or [] if isinstance(c, dict) and c.get("id")]
    _cache[key] = comps
    return comps


def find(comps: list[dict[str, Any]], thing: str | None) -> dict[str, Any] | None:
    """By id ("dw1"), else label or class ("dishwasher", "sink cabinet"), singular or plural,
    else a label containing it ("cabinet" -> the first "sink cabinet")."""
    if not thing:
        return None
    q = _norm(thing)
    for c in comps:
        if _norm(c.get("id")) == q:
            return c
    for want in (q, singular(q)):
        for c in comps:
            if want in (_norm(c.get("label")), _norm(c.get("class"))):
                return c
    for want in (q, singular(q)):
        for c in comps:
            if want and want in _norm(c.get("label")):
                return c
    return None


def label(c: dict[str, Any]) -> str:
    return _norm(c.get("label") or c.get("class") or c.get("id") or "part")


def file_gap_m(c: dict[str, Any] | None, scale: float = 1.0) -> tuple[float, float, float] | None:
    """(w, h, d) real metres from the file: cavity.size_m x the scale calibration."""
    size = ((c or {}).get("cavity") or {}).get("size_m") or {}
    try:
        w, h, d = (float(size[k]) * (scale or 1.0) for k in ("w", "h", "d"))
    except (KeyError, TypeError, ValueError):
        return None
    return (w, h, d) if w > 0 and h > 0 and d > 0 else None


def context_cavity(ctx_cavity: Any) -> Cavity | None:
    """The headset's `cavity` context -> a Cavity (its tape readings win over the file's)."""
    if not isinstance(ctx_cavity, dict):
        return None
    try:
        cav = Cavity.model_validate(ctx_cavity)
    except ValueError:
        return None
    return cav


def cavity_for(c: dict[str, Any], scale: float, ctx_cavity: Any = None) -> Cavity | None:
    """The gap of component `c`: the headset's context when it is about `c`, else the file."""
    cav = context_cavity(ctx_cavity)
    if cav is not None and (cav.component_id in (None, c.get("id"))):
        return cav
    gap = file_gap_m(c, scale)
    if gap is None:
        return None
    return Cavity(w_m=gap[0], h_m=gap[1], d_m=gap[2], component_id=c.get("id"), label=label(c))


def fit(dims: Dims, cavity: Cavity) -> Fit:
    """A part against the gap on all three axes (width across the opening, height, depth): the
    tightest clearance decides. The app shows it as "Fits the gap · 7 mm spare"."""
    w, h, d = cavity.size_mm()
    clearance = {"w": w - dims.w, "h": h - dims.h, "d": d - dims.d}
    axis = min(clearance, key=lambda k: clearance[k])
    spare = clearance[axis]
    if spare >= 0:
        note = "exact fit in the gap" if spare < 2 else f"fits the gap, {spare:.0f} mm spare"
        return Fit(status="fits", spare_mm=round(spare, 1), axis=axis, note=note)
    word = {"w": "wide", "h": "tall", "d": "deep"}[axis]
    return Fit(
        status="too_big",
        spare_mm=round(spare, 1),
        axis=axis,
        note=f"too {word} for the gap by {abs(spare):.0f} mm",
    )


TIGHT_MM = 5.0  # up to this much over on one axis is "tight" (amber): the gap is an estimate


def fits_word(f: Fit) -> str:
    """place_part's `fits`: "fits", "tight" (within TIGHT_MM over) or "too_big"."""
    if f.status == "fits":
        return "fits"
    return "tight" if f.spare_mm is not None and f.spare_mm >= -TIGHT_MM else "too_big"


def clearance_mm(dims: Dims, cavity: Cavity) -> dict[str, float]:
    w, h, d = cavity.size_mm()
    return {"w": round(w - dims.w), "h": round(h - dims.h), "d": round(d - dims.d)}


def best(dims: list[Dims | None], cavity: Cavity) -> int:
    """The first candidate that fits (the search's own order), else the one that overruns least;
    -1 for none."""
    least, least_over = -1, float("inf")
    for i, d in enumerate(dims):
        if d is None:
            continue
        f = fit(d, cavity)
        if f.status == "fits":
            return i
        if -(f.spare_mm or 0) < least_over:
            least, least_over = i, -(f.spare_mm or 0)
    return least if least >= 0 else (0 if dims else -1)


def short_name(part: Any) -> str:
    """What to call a candidate aloud: "Whirlpool WDP540HAMW", else the first words of its name."""
    if part.manufacturer:
        return (
            f"{part.manufacturer} {part.model_no}".strip() if part.model_no else part.manufacturer
        )
    words = re.sub(r"\s*\(.*?\)", "", part.name or "").split()
    return " ".join(words[:4]) if words else "that one"


def cm(v_m: float) -> str:
    return f"{v_m * 100:.0f}"


def spoken_gap(cavity: Cavity) -> str:
    """ "60 by 82 by 58 centimetres" (W x H x D, the headset's tape where it measured)."""
    w, h, d = cavity.size_m()
    return f"{cm(w)} by {cm(h)} by {cm(d)} centimetres"


def spoken_fit(f: Fit) -> str:
    if f.status == "fits":
        return (
            "It fits exactly."
            if (f.spare_mm or 0) < 2
            else f"It fits with {f.spare_mm:.0f} millimetres to spare."
        )
    word = {"w": "wide", "h": "tall", "d": "deep"}.get(f.axis or "", "big")
    if fits_word(f) == "tight":
        over = abs(f.spare_mm or 0)
        amount = "under a millimetre" if over < 1 else f"{over:.0f} millimetres"
        return f"It's tight: {amount} too {word}."
    return f"It's too {word} by {abs(f.spare_mm or 0):.0f} millimetres."


# --- kinds: the words for a scene part, its standard sizes, the scale prior (replace_job) -----

IN = 0.0254

KINDS: dict[str, tuple[str, ...]] = {
    "dishwasher": ("dishwasher", "dish washer", "dishwashing machine"),
    "range": ("range", "stove", "oven", "cooker", "cooktop", "cook top", "stovetop", "stove top"),
    "fridge": ("fridge", "refrigerator", "fridge freezer", "freezer", "icebox"),
    "base cabinet": (
        "base cabinet",
        "sink cabinet",
        "lower cabinet",
        "sink base",
        "cabinet",
        "cupboard",
    ),
}

# Inches. plausible_in: what a real opening for the kind measures, per axis (w across, h, d);
# widths_in: the kind's standard widths (a size-anchored search, sized_query).
# standards: (axis, inches, words) in order of preference; the first that leaves every axis
# plausible wins.
PRIORS: dict[str, dict[str, Any]] = {
    "dishwasher": {
        "plausible_in": {"w": (20.0, 26.0), "h": (32.0, 36.5), "d": (21.0, 28.0)},
        "widths_in": [18, 24],
        "standards": [
            ("h", 34.5, "34½″ dishwasher opening"),
            ("w", 24.0, "24″ dishwasher opening"),
        ],
    },
    "range": {
        "plausible_in": {"w": (19.0, 37.0), "h": (34.0, 50.0), "d": (22.0, 30.0)},
        "widths_in": [20, 24, 30, 36],
        "standards": [("w", 30.0, "30″ range"), ("w", 36.0, "36″ range"), ("w", 24.0, "24″ range")],
    },
    "fridge": {
        "plausible_in": {"w": (23.0, 40.0), "h": (60.0, 76.0), "d": (24.0, 36.0)},
        "widths_in": [24, 28, 30, 33, 36],
        "standards": [
            ("w", 36.0, "36″ fridge opening"),
            ("w", 33.0, "33″ fridge opening"),
            ("w", 30.0, "30″ fridge opening"),
        ],
    },
    "base cabinet": {
        "plausible_in": {"w": (9.0, 60.0), "h": (30.0, 36.5), "d": (20.0, 28.0)},
        "widths_in": [12, 15, 18, 21, 24, 27, 30, 33, 36, 42, 48],
        "standards": [
            ("h", 34.5, "34½″ base cabinet"),
            ("d", 24.0, "24″-deep base cabinet"),
        ],
    },
}


def kind_of(words: str | None) -> str | None:
    """The PRIORS kind a phrase or label names ("old stove" -> "range"), else None. Longest
    synonym first, so "sink cabinet" is a base cabinet and "fridge freezer" a fridge."""
    t = _norm(words)
    if not t:
        return None
    pairs = sorted(
        ((syn, k) for k, syns in KINDS.items() for syn in syns), key=lambda p: -len(p[0])
    )
    for syn, k in pairs:
        if re.search(rf"\b{re.escape(syn)}s?\b", t):
            return k
    return None


def component_kind(c: dict[str, Any]) -> str | None:
    return kind_of(c.get("label")) or kind_of(c.get("class"))


def _out_of_range(size_in: dict[str, float], plausible: dict[str, tuple[float, float]]) -> float:
    """How far outside the plausible ranges a size is, summed over axes (relative)."""
    total = 0.0
    for axis, (lo, hi) in plausible.items():
        v = size_in[axis]
        if v < lo:
            total += (lo - v) / lo
        elif v > hi:
            total += (v - hi) / hi
    return total


def implausible(kind: str | None, gap_m: tuple[float, float, float]) -> bool:
    """The gap can't be an opening of this kind at this scale (e.g. a dishwasher < 20 in wide)."""
    prior = PRIORS.get(kind or "")
    if prior is None:
        return False
    size_in = dict(zip("whd", (v / IN for v in gap_m), strict=True))
    return _out_of_range(size_in, prior["plausible_in"]) > 0


@dataclass
class ScalePrior:
    factor: float  # multiply the scene's scale by this
    axis: str  # w | h | d: the gap's axis set to `real_m`
    real_m: float
    said: str  # "34½″ dishwasher opening"


def scale_prior(kind: str | None, gap_m: tuple[float, float, float]) -> ScalePrior | None:
    """The standard opening to scale the scene from, when the gap is implausible for its kind;
    None when it's plausible, or the kind has no standard sizes on file."""
    prior = PRIORS.get(kind or "")
    if prior is None or not implausible(kind, gap_m):
        return None
    gap = dict(zip("whd", gap_m, strict=True))
    best, best_score = None, None
    for rank, (axis, inches, said) in enumerate(prior["standards"]):
        if gap[axis] <= 0:
            continue
        factor = inches * IN / gap[axis]
        size_in = {a: v * factor / IN for a, v in gap.items()}
        score = (_out_of_range(size_in, prior["plausible_in"]), rank)
        if best_score is None or score < best_score:
            best, best_score = ScalePrior(factor, axis, round(inches * IN, 4), said), score
    return best


def sized_query(kind: str | None, noun: str, cavity: Cavity) -> str:
    """A search for parts of the gap's size: the widest standard width of the kind that fits it
    ("30 inch fridge" for an 80 cm fridge gap), else the gap's whole inches."""
    w_in = cavity.size_m()[0] / IN
    widths = [w for w in (PRIORS.get(kind or "") or {}).get("widths_in", []) if w <= w_in - 0.25]
    size = max(widths) if widths else int(w_in)
    return f"{size} inch {noun}"


def _say_size(axis: str, inches: float) -> str:
    """ "the opening is 34 and a half inches tall" (what LocalIntents / _SCALE_RE take)."""
    whole = int(inches)
    frac = _FRACTION_WORDS.get(round(inches - whole, 3), "")
    word = {"w": "wide", "h": "tall", "d": "deep"}[axis]
    return f"the opening is {whole}{frac} inches {word}"


def scale_hint(cavity: Cavity | None, fits: list[str], scale: float = 1.0) -> str | None:
    """When nothing fits a gap that is implausible for its kind at an uncalibrated scale, the
    line that says how to fix it; None otherwise (something fits, the scale is set, or the
    kind has no standard sizes)."""
    if (
        cavity is None
        or abs((scale or 1.0) - 1.0) > 0.005
        or any(f in ("fits", "tight") for f in fits)
    ):
        return None
    kind = kind_of(cavity.label)
    prior = PRIORS.get(kind or "")
    if prior is None or not implausible(kind, cavity.size_m()):
        return None
    axis, inches, _ = prior["standards"][0]
    return f"The scan may read small; say “{_say_size(axis, inches)}” to set the scale."


# --- the phrases (mirrors the app's Runtime/Agent/LocalIntents.cs; both are tested) --------------
# Whole utterances only (fillers allowed: "okay", "please", "grok"...), or a few joined by "and" /
# "then" / commas ("take out the dishwasher and measure the gap").

_LEAD = (
    r"^\W*(?:(?:ok(?:ay)?|hey|so|now|and|then|alright|all\s+right|please|grok|quartermaster"
    r"|can\s+you|could\s+you|would\s+you|will\s+you|let'?s|go\s+ahead\s+and|i\s+want\s+to"
    r"|i'?d\s+like\s+to|we\s+need\s+to|i\s+need\s+you\s+to)\W+)*"
)
_TAIL = (
    r"(?:\W+(?:please|for\s+me|now|then|instead|again|thanks|thank\s+you|grok|quartermaster))*\W*$"
)
_DET = r"(?:(?:the|that|this|my|our|your|a|an|some|new|old|existing|current)\s+)*"
_GAP = r"(?:gap|hole|opening|cavity|space|spot|slot|void|empty\s+space|niche)"


def _thing(name: str) -> str:
    """1-5 words, never "and" / "then" (those split an utterance)."""
    return (
        rf"(?P<{name}>(?!(?:and|then)\b)[a-z][a-z0-9'\-]*"
        rf"(?:\s+(?!(?:and|then)\b)[a-z0-9'\-]+){{0,4}}?)"
    )


_I = re.IGNORECASE
_PUT_BACK_RE = re.compile(
    _LEAD
    + r"(?:(?:put|bring|set|move)\s+(?:it|that|this|them|(?:the\s+)?original(?:\s+one)?|"
    + _DET
    + _thing("t1")
    + r")\s+back(?:\s+in(?:\s+(?:there|place|its\s+place))?)?"
    + r"|(?:restore|reinstall)\s+(?:it|that|this|(?:the\s+)?original(?:\s+one)?|"
    + _DET
    + _thing("t2")
    + r")"
    + r"|undo\s+the\s+removal)"
    + _TAIL,
    _I,
)
_REMOVE_RE = re.compile(
    _LEAD
    + r"(?:(?:remove|take\s+out|pull\s+out|get\s+rid\s+of|take\s+away|rip\s+out|yank\s+out|uninstall)\s+"
    + _DET
    + _thing("t1")
    + r"(?:\s+out)?"
    + r"|(?:take|pull|rip|yank)\s+"
    + _DET
    + _thing("t2")
    + r"\s+out)"
    + _TAIL,
    _I,
)
_MEASURE_RE = re.compile(
    _LEAD
    + r"(?:(?:measure|size\s+up|tape|get\s+the\s+(?:size|dimensions)\s+of|check\s+the\s+size\s+of"
    + r"|how\s+big\s+is|what(?:'s|\s+is|\s+are)\s+the\s+(?:size|dimensions?)\s+of)\s+"
    + r"(?:the\s+(?:dimensions|size)\s+of\s+)?"
    + r"(?:it|that|this|there|the\s+"
    + _GAP
    + r"|that\s+"
    + _GAP
    + r"|the\s+[a-z]+(?:\s+[a-z]+)?\s+"
    + _GAP
    + r"|where\s+(?:it|the\s+[a-z]+(?:\s+[a-z]+)?)\s+(?:was|used\s+to\s+be))"
    + r"|what(?:'s|\s+is|\s+are)\s+the\s+(?:size|dimensions)(?:\s+of\s+the\s+"
    + _GAP
    + r")?"
    + r"|how\s+big\s+is\s+(?:it|the\s+"
    + _GAP
    + r"))"
    + _TAIL,
    _I,
)
_UNDO_RE = re.compile(
    _LEAD
    + r"(?:undo|undo\s+(?:that|it|the\s+last\s+(?:one|step|change))|take\s+that\s+back)"
    + _TAIL,
    _I,
)
_OPTION_RE = re.compile(
    _LEAD
    + r"(?:(?:show\s+me|try|go\s+to|switch\s+to|give\s+me|put\s+in|use|pick|let'?s\s+see|let\s+me\s+see"
    + r"|i'?ll\s+take)\s+)?(?:the\s+)?"
    + r"(?:(?:option|number|model|choice|candidate|pick)\s+(?:#\s*)?(?P<n>\d{1,2}|one|two|three|four|five)"
    + r"|(?P<ord>first|second|third|fourth|fifth)(?:\s+(?:one|option|model|choice|pick))?)"
    + _TAIL,
    _I,
)
_PREVIOUS_RE = re.compile(
    _LEAD
    + r"(?:(?:show\s+me|try|go\s+back\s+to|back\s+to|switch\s+to|give\s+me)\s+)?(?:the\s+)?"
    + r"(?:previous|prior|one\s+before)(?:\s+(?:one|model|option|choice|pick|candidate))?"
    + _TAIL,
    _I,
)
_NEXT_RE = re.compile(
    _LEAD
    + r"(?:(?:(?:show\s+me|try|let'?s\s+see|let\s+me\s+see|go\s+to|switch\s+to|give\s+me|put\s+in"
    + r"|how\s+about)\s+)?(?:the\s+|a\s+)?(?:next|another|different|other)"
    + r"(?:\s+(?:one|model|option|choice|pick|candidate|brand))?"
    + r"|(?:swap|switch|change)\s+(?:it|models?|them|it\s+out)"
    + r"(?:\s+(?:out|for\s+(?:another|the\s+next)(?:\s+one)?))?)"
    + _TAIL,
    _I,
)
_PLACE_RE = re.compile(
    _LEAD
    + r"(?:put|place|install|drop|stick|slot|slide|fit|try|set|pop)\s+"
    + r"(?:it|that|this|that\s+one|this\s+one|one|them|the\s+(?:best|top|first|recommended)"
    + r"(?:\s+(?:one|fit|match|pick))?|"
    + _DET
    + _thing("t1")
    + r")"
    + r"(?:\s+(?:in|into|there|in\s+there|in\s+here|here|in\s+(?:the\s+)?"
    + _GAP
    + r"|into\s+the\s+"
    + _GAP
    + r"|in\s+place|in\s+its\s+place))?"
    + _TAIL,
    _I,
)
_FIND_RE = re.compile(
    _LEAD
    + r"(?:find|get|show|look\s+for|search\s+for|shop\s+for|search|browse|pick\s+out)(?:\s+me)?\s+"
    + _DET
    + _thing("t1")
    + r"(?P<fits>\s+(?:that|which|to)\s+(?:will\s+|would\s+|can\s+|could\s+)?fits?"
    + r"(?:\s+(?:in\s+)?(?:there|here|it|the\s+"
    + _GAP
    + r"))?"
    + r"|\s+for\s+(?:the\s+"
    + _GAP
    + r"|it|there))?"
    + _TAIL,
    _I,
)
_AMOUNT = (
    r"(?P<n>\d+(?:\.\d+)?)(?P<half>\s*(?:and\s+(?:a\s+)?(?:half|quarter)|and\s+three\s+quarters"
    r"|[½¼¾⅛⅜⅝⅞]|\d{1,2}/\d{1,2}))?\s*"
    r"(?P<u>inches|inch|in\b|\"|″|centimet(?:re|er)s?|cm|millimet(?:re|er)s?|mm)"
)
_SCALE_RE = re.compile(
    _LEAD
    + r"(?:(?:set\s+the\s+scale|scale\s+it)\W+)?"
    + r"(?:(?:the\s+)?(?:gap|opening|hole|space|cavity|cutout)(?:'s)?\s+(?P<axis2>width|height|depth)\s+is\s+"
    + _AMOUNT
    + r"|(?:(?:the\s+)?(?:gap|opening|hole|space|cavity|cutout)\s+(?:is|should\s+be|was)"
    + r"|it(?:'s|\s+is|\s+should\s+be))\s+"
    + _AMOUNT.replace("?P<n>", "?P<n2>").replace("?P<half>", "?P<half2>").replace("?P<u>", "?P<u2>")
    + r"\s+(?P<axis>wide|tall|high|deep))"
    + _TAIL,
    _I,
)


_VULGAR = {"½": 0.5, "¼": 0.25, "¾": 0.75, "⅛": 0.125, "⅜": 0.375, "⅝": 0.625, "⅞": 0.875}
_FRACTION_WORDS = {0.5: " and a half", 0.25: " and a quarter", 0.75: " and three quarters"}


def fraction(text: str | None) -> float:
    """ " and a half" / "½" / " 3/4" -> 0.5 / 0.5 / 0.75; 0 for none."""
    t = (text or "").strip().lower()
    if not t:
        return 0.0
    if t in _VULGAR:
        return _VULGAR[t]
    if "/" in t:
        a, b = t.split("/", 1)
        return float(a) / float(b)
    if "three quarters" in t:
        return 0.75
    return 0.25 if "quarter" in t else 0.5


_UNITS_WORDS = {
    "zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
    "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13,
    "fourteen": 14, "fifteen": 15, "sixteen": 16, "seventeen": 17, "eighteen": 18,
    "nineteen": 19,
}  # fmt: skip
_TENS_WORDS = {
    "twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70,
    "eighty": 80, "ninety": 90,
}  # fmt: skip
_NUM_WORD = "|".join([*_TENS_WORDS, *_UNITS_WORDS])
_SIZE_UNIT = (
    r"(?:inch(?:es)?|centimet(?:re|er)s?|millimet(?:re|er)s?|cm|mm|feet|foot|dollars?|bucks)"
)
# "thirty-four and a half inches", "twelve hundred dollars": number words before a unit (only
# there: "next one" / "option two" keep their words).
_NUMBER_WORDS_RE = re.compile(
    rf"\b(?P<num>(?:{_NUM_WORD})(?:[\s-]+(?:{_NUM_WORD}))?(?:\s+hundred(?:\s+(?:and\s+)?"
    rf"(?:{_NUM_WORD})(?:[\s-]+(?:{_NUM_WORD}))?)?)?)"
    rf"(?P<rest>(?:\s+and\s+(?:a\s+)?(?:half|quarter)|\s+and\s+three\s+quarters)?\s+{_SIZE_UNIT}\b)",
    re.IGNORECASE,
)


def _words_value(words: str) -> int | None:
    total, current = 0, 0
    for w in re.split(r"[\s-]+", words.lower()):
        if w == "and" or not w:
            continue
        if w == "hundred":
            current = max(current, 1) * 100
        elif w in _TENS_WORDS:
            current += _TENS_WORDS[w]
        elif w in _UNITS_WORDS:
            current += _UNITS_WORDS[w]
        else:
            return None
    return total + current


def normalize(text: str | None) -> str:
    """A transcript as the phrases expect it: Whisper's "34 1⁄2" (U+2044 fraction slash) and
    "34½" are "34 1/2"; number words before a unit are digits ("thirty-four and a half inches"
    -> "34 and a half inches", "twelve hundred dollars" -> "1200 dollars"); curly quotes
    straight."""
    t = (text or "").replace("\u2044", "/").replace("\u2215", "/")
    t = (
        t.replace("\u2019", "'")
        .replace("\u2018", "'")
        .replace("\u201c", '"')
        .replace("\u201d", '"')
    )
    t = re.sub(r"(\d)\s*([½¼¾⅛⅜⅝⅞])", r"\1 \2", t)

    def digits(m: re.Match) -> str:
        v = _words_value(m.group("num"))
        return m.group(0) if v is None else f"{v}{m.group('rest')}"

    return _NUMBER_WORDS_RE.sub(digits, t)


def _scale_of(m: re.Match) -> dict[str, Any] | None:
    """ "the opening is 34 and a half inches tall" -> {axis: "h", metres: 0.8763, said: "34 and a half inches"}."""
    g = m.groupdict()
    axis_word = (g.get("axis") or g.get("axis2") or "").lower()
    axis = "w" if axis_word.startswith("wid") else "d" if axis_word.startswith("dep") else "h"
    n, half = g.get("n") or g.get("n2"), g.get("half") or g.get("half2")
    unit = (g.get("u") or g.get("u2") or "").lower()
    try:
        frac = fraction(half)
        value = float(n) + frac
    except (TypeError, ValueError, ZeroDivisionError):
        return None
    per = (
        0.01 if unit.startswith("c") else 0.001 if unit == "mm" or unit.startswith("mi") else 0.0254
    )
    metres = value * per
    if not 0.01 < metres < 5.0:
        return None
    word = {0.0254: "inches", 0.01: "centimetres", 0.001: "millimetres"}[per]
    said = f"{n}{_FRACTION_WORDS.get(round(frac, 3), f' {frac:g}' if frac else '')} {word}"
    return {"kind": "scale", "axis": axis, "metres": round(metres, 4), "said": said}


_SPLIT_RE = re.compile(  # "34 and a half inches" is one amount, not two phrases
    r"\s*(?:,\s*(?:and\s+)?(?:then\s+)?|[.;!?]\s+(?:and\s+)?(?:then\s+)?|\s+and\s+then\s+"
    r"|\s+and\s+(?!(?:a\s+)?(?:half|quarter)\b|three\s+quarters\b)|\s+then\s+)\s*",
    _I,
)
_PRONOUNS = {
    "it",
    "that",
    "this",
    "them",
    "these",
    "those",
    "one",
    "ones",
    "everything",
    "all",
    "something",
    "replacement",
    "replacements",
    "a replacement",
    "another",
    "another one",
}
_ORDINALS = {
    "one": 0,
    "first": 0,
    "two": 1,
    "second": 1,
    "three": 2,
    "third": 2,
    "four": 3,
    "fourth": 3,
    "five": 4,
    "fifth": 4,
}


def _thing_of(m: re.Match) -> str | None:
    for name in ("t1", "t2"):
        v = m.groupdict().get(name)
        if v:
            s = re.sub(
                r"^(?:the|a|an|my|our|that|this|some|new|old|existing|current)\s+",
                "",
                v.strip().lower(),
            )
            return None if not s or s in _PRONOUNS else s
    return None


def match(text: str) -> dict[str, Any] | None:
    """One phrase: {kind, thing, index, fits} or None. kind: remove | put_back | measure | scale |
    undo | option | previous | next | place | find."""
    t = re.sub(r"\s+", " ", normalize(text).strip())
    if not t:
        return None
    if (m := _SCALE_RE.match(t)) and (scale := _scale_of(m)):
        return scale
    if m := _PUT_BACK_RE.match(t):
        return {"kind": "put_back", "thing": _thing_of(m)}
    if _MEASURE_RE.match(t):
        return {"kind": "measure"}
    if _UNDO_RE.match(t):
        return {"kind": "undo"}
    if m := _REMOVE_RE.match(t):
        thing = _thing_of(m)
        return {"kind": "remove", "thing": thing} if thing else None
    if m := _OPTION_RE.match(t):
        n = (m.group("n") or m.group("ord") or "").lower()
        index = _ORDINALS.get(n, int(n) - 1 if n.isdigit() and int(n) >= 1 else -1)
        return {"kind": "option", "index": index}
    if _PREVIOUS_RE.match(t):
        return {"kind": "previous"}
    if _NEXT_RE.match(t):
        return {"kind": "next"}
    if m := _PLACE_RE.match(t):
        thing = _thing_of(m)
        if thing and re.search(
            r"\b(?:note|pin|tape|point|marker|label)s?\b|^(?:it|that|this|them|these|those)\b"
            r"|\b(?:every|each)\b|\d",
            thing,
            _I,
        ):
            return None
        return {"kind": "place", "thing": thing}
    if m := _FIND_RE.match(t):
        thing = _thing_of(m)
        if thing and re.search(
            r"^(?:next|previous|option|number|other|another|it|that|this|them|me|us)\b|^"
            + _GAP
            + r"$|\b(?:model|scan|scene|site)s?$",
            thing,
            _I,
        ):
            return None
        return {"kind": "find", "thing": thing, "fits": bool(m.group("fits"))}
    return None


def match_all(text: str) -> list[dict[str, Any]]:
    """The phrases of an utterance in order (one, or 2-4 joined by and / then / commas, each a
    phrase); [] otherwise."""
    text = normalize(text)
    if not text or len(text) > 200:
        return []
    one = match(text)
    if one:
        return [one]
    pieces = [p for p in _SPLIT_RE.split(text.strip().rstrip(".!?;, ")) if p.strip()]
    if not 2 <= len(pieces) <= 4:
        return []
    out = []
    for p in pieces:
        m = match(p)
        if m is None:
            return []
        out.append(m)
    return out
