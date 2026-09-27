"""Tape survey and slope results from the headset's own tape (brain.md S1) -- deterministic, no
LLM. Not F10's drone condition survey (server/survey.py): that one triages damage from drone
frames; this one groups the sizes the headset measured.

The agent never measures anything itself: `survey` / `check_slope` actions make the headset run its
real measure tool, and the headset reports what it measured back through `POST /agent/observe`
(`Observation`). This module turns those reports into size groups, a spoken summary, a gutter
drainage verdict, and template answers to follow-ups ("which is the widest?").
"""

import math
from typing import Any, Literal

from pydantic import BaseModel, Field, model_validator

LABELS = ("cabinet_door", "drawer", "panel", "appliance", "window", "door", "any")
WHERES = ("all", "visible", "upper", "lower", "left", "right", "nearest")
MEASURES = ("size", "width", "height")
SLOPE_TARGETS = ("gutter", "sill", "ledge", "nearest_edge")

GROUP_TOLERANCE_MM = 10.0
SPOKEN_GROUPS = 3
GUTTER_MM_PER_M = 2.083  # 1/4 in of fall per 10 ft
SNAP_UNCERTAINTY_MM = 2.0

_NOUNS = {
    "cabinet_door": ("door", "doors"),
    "drawer": ("drawer", "drawers"),
    "panel": ("panel", "panels"),
    "appliance": ("appliance", "appliances"),
    "window": ("window", "windows"),
    "door": ("door", "doors"),
    "any": ("object", "objects"),
}
_NUMBER_WORDS = ["No", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine"]


def noun(label: str | None, n: int = 2) -> str:
    """Spoken noun for a structure label: "door"/"doors" for cabinet_door (brain.md S1 speech)."""
    if label in _NOUNS:
        singular, plural = _NOUNS[label]
    else:
        singular = (label or "object").replace("_", " ")
        plural = singular + "s"
    return singular if n == 1 else plural


# --- what the headset reports (POST /agent/observe) ---------------------------------------


class SurveyItem(BaseModel):
    """One object the headset measured with its tape (a notebook entry)."""

    id: str = Field(max_length=64)  # the structure object id, e.g. "o5"
    label: str = Field("", max_length=64)
    group: str | None = Field(None, max_length=64)
    w_m: float | None = None
    h_m: float | None = None
    area_m2: float | None = None
    angles_deg: list[float] = Field(default_factory=list, max_length=8)
    off_plane_m: float | None = None
    snap: list[str] = Field(default_factory=list, max_length=8)  # per corner: corner|edge|plane|raw
    unverified: bool = False  # a corner didn't snap within 2 cm: drawn amber
    notebook_id: int | str | None = None
    camera_id: int | None = None


class SurveySkip(BaseModel):
    id: str = Field(max_length=64)
    reason: str = Field("", max_length=200)


class Observation(BaseModel):
    """`POST /agent/observe`: what the headset's tools measured for an agent action."""

    session_id: str
    request_id: str | None = Field(None, max_length=64)  # from the survey/check_slope action
    kind: Literal["survey_result", "slope_result"]
    query: str | None = Field(None, max_length=500)  # the user's words, if the app has them
    tts: bool = False  # also return the spoken reply as audio (like /voice/command)

    # survey_result
    label: str | None = Field(None, max_length=64)
    measure: Literal["size", "width", "height"] | None = None
    status: Literal["done", "aborted", "no_structure"] = "done"
    planned: int | None = Field(None, ge=0)  # how many objects the plan had
    results: list[SurveyItem] = Field(default_factory=list, max_length=500)
    skipped: list[SurveySkip] = Field(default_factory=list, max_length=500)

    # slope_result
    target: str | None = Field(None, max_length=32)
    run_m: float | None = None
    fall_mm: float | None = None
    low_end: list[float] | None = None
    gravity_residual_deg: float | None = None
    uncertainty_mm: float | None = None
    notebook_id: int | str | None = None

    @model_validator(mode="after")
    def _slope_needs_numbers(self):
        if self.kind == "slope_result" and (self.run_m is None or self.fall_mm is None):
            raise ValueError("slope_result needs run_m and fall_mm")
        if self.run_m is not None and self.run_m <= 0:
            raise ValueError("run_m must be positive")
        return self


# --- size groups ----------------------------------------------------------------------------


def group_sizes(
    items: list[tuple[str, float, float]], measure: str = "size", tol_mm: float = GROUP_TOLERANCE_MM
) -> list[dict[str, Any]]:
    """Greedy clustering of (id, w_mm, h_mm): smallest first, an item joins the first group whose
    running mean is within `tol_mm` on the measured side(s) (`size` both, `width` w, `height` h),
    else starts one. Groups come back most common first, then widest, as {w_mm, h_mm, count, ids};
    `ids` keep the order the items came in (the headset's sweep order)."""
    groups: list[dict[str, Any]] = []
    order = {item_id: i for i, (item_id, _, _) in enumerate(items)}
    for item_id, w, h in sorted(items, key=lambda it: (it[1], it[2], order[it[0]])):
        for g in groups:
            close_w = abs(g["_w"] - w) <= tol_mm or measure == "height"
            close_h = abs(g["_h"] - h) <= tol_mm or measure == "width"
            if close_w and close_h:
                n = len(g["ids"])
                g["_w"] = (g["_w"] * n + w) / (n + 1)
                g["_h"] = (g["_h"] * n + h) / (n + 1)
                g["ids"].append(item_id)
                break
        else:
            groups.append({"_w": w, "_h": h, "ids": [item_id]})
    out = [
        {
            "w_mm": round(g["_w"]),
            "h_mm": round(g["_h"]),
            "count": len(g["ids"]),
            "ids": sorted(g["ids"], key=order.get),
        }
        for g in groups
    ]
    out.sort(key=lambda g: (-g["count"], -g["w_mm"], -g["h_mm"]))
    return out


def _mm_items(results: list[SurveyItem]) -> list[tuple[str, float, float]]:
    return [
        (r.id, r.w_m * 1000, r.h_m * 1000)
        for r in results
        if r.w_m is not None and r.h_m is not None
    ]


# --- speech ---------------------------------------------------------------------------------


def _spoken_len(mm: float, unit: str) -> str:
    """Half-up rounding (525 mm is "53", not banker's "52"): cm, or mm to the nearest 5."""
    return str(int(mm / 10 + 0.5)) if unit == "cm" else str(5 * int(mm / 5 + 0.5))


_UNIT_WORD = {"cm": "centimetres", "mm": "millimetres"}
_SIDE_WORD = {"width": " wide", "height": " tall"}


def _dims(w_mm: float, h_mm: float, measure: str) -> tuple[str, str]:
    """("39 by 41", "cm") -- centimetres when every spoken side is over 100 mm, else mm rounded
    to 5 (a hinge cup, a small drawer front). `width`/`height` speak one side only."""
    sides = {"width": [w_mm], "height": [h_mm]}.get(measure, [w_mm, h_mm])
    unit = "cm" if all(s > 100 for s in sides) else "mm"
    return " by ".join(_spoken_len(s, unit) for s in sides), unit


def _dims_with_unit(w_mm: float, h_mm: float, measure: str) -> str:
    """ "39 by 41 centimetres", "26 centimetres wide"."""
    dims, unit = _dims(w_mm, h_mm, measure)
    return f"{dims} {_UNIT_WORD[unit]}{_SIDE_WORD.get(measure, '')}"


def _group_phrase(g: dict[str, Any], measure: str) -> tuple[str, str]:
    """("6 at 39 by 41", "cm")."""
    dims, unit = _dims(g["w_mm"], g["h_mm"], measure)
    return f"{g['count']} at {dims}", unit


def _groups_phrase(groups: list[dict[str, Any]], measure: str) -> str:
    spoken = [_group_phrase(g, measure) for g in groups[:SPOKEN_GROUPS]]
    units = {u for _, u in spoken}
    side = _SIDE_WORD.get(measure, "")
    if len(units) == 1:
        texts = [t for t, _ in spoken]
        texts[-1] += f" {_UNIT_WORD[units.pop()]}{side}"
    else:
        texts = [f"{t} {_UNIT_WORD[u]}{side}" for t, u in spoken]
    more = len(groups) - SPOKEN_GROUPS
    if more > 0:
        texts.append(f"{more} more size{'s' if more > 1 else ''} on the card")
    return ", ".join(texts[:-1]) + (" and " if len(texts) > 1 else "") + texts[-1]


def _count_word(n: int) -> str:
    return _NUMBER_WORDS[n] if n < len(_NUMBER_WORDS) else str(n)


def survey_summary(obs: Observation, label: str, measure: str) -> dict[str, Any]:
    """Spoken reply + `show_tape_survey` args for a survey the headset finished (or stopped)."""
    plural = noun(label)
    if obs.status == "no_structure":
        return {
            "reply": "No structure layer for this scene; mark the corners yourself.",
            "groups": [],
            "unverified": [],
            "skipped": [s.id for s in obs.skipped],
        }
    groups = group_sizes(_mm_items(obs.results), measure)
    unverified = [r.id for r in obs.results if r.unverified]
    n = sum(g["count"] for g in groups)

    bits = []
    if obs.status == "aborted":
        planned = f" of {obs.planned}" if obs.planned else ""
        bits.append(f"Stopped after {n}{planned}.")
    if n == 0:
        bits.append(
            f"No {plural} measured."
            if obs.status == "aborted"
            else f"Found no {plural} to measure."
        )
    elif n == 1:
        g = groups[0]
        bits.append(f"1 {noun(label, 1)}: {_dims_with_unit(g['w_mm'], g['h_mm'], measure)}.")
    elif len(groups) == 1:
        g = groups[0]
        bits.append(f"{n} {plural}, all {_dims_with_unit(g['w_mm'], g['h_mm'], measure)}.")
    else:
        bits.append(f"{n} {plural} in {len(groups)} sizes: {_groups_phrase(groups, measure)}.")
    if unverified:
        k = len(unverified)
        bits.append(
            f"{_count_word(k)} didn't lock onto corners; they're amber."
            if k > 1
            else "One didn't lock onto its corners; it's amber."
        )
    if obs.skipped:
        bits.append(f"{_count_word(len(obs.skipped))} skipped.")
    return {
        "reply": " ".join(bits),
        "groups": groups,
        "unverified": unverified,
        "skipped": [s.id for s in obs.skipped],
    }


# --- gutter slope ---------------------------------------------------------------------------


def slope_uncertainty_mm(run_m: float, gravity_residual_deg: float | None) -> float:
    """How wrong "up" can be over the run, plus snapping (S1: run * tan(residual) + 2 mm)."""
    residual = math.radians(abs(gravity_residual_deg or 0.0))
    return round(run_m * 1000 * math.tan(residual) + SNAP_UNCERTAINTY_MM, 1)


def slope_verdict(run_m: float, fall_mm: float, uncertainty_mm: float) -> dict[str, Any]:
    """Drainage verdict against the gutter rule (>= 1/4 in per 10 ft = 2.083 mm/m):
    "drains" if even the low estimate is enough, "won't drain" if even the high one isn't,
    else "inconclusive" -- the honest answer when the capture's "up" isn't good enough."""
    required = run_m * GUTTER_MM_PER_M
    fall = abs(fall_mm)
    if fall - uncertainty_mm >= required:
        verdict = "drains"
    elif fall + uncertainty_mm < required:
        verdict = "won't drain"
    else:
        verdict = "inconclusive"
    return {
        "verdict": verdict,
        "fall_mm": round(fall, 1),
        "uncertainty_mm": round(uncertainty_mm, 1),
        "required_mm": round(required, 1),
        "run_m": round(run_m, 3),
    }


def slope_reply(v: dict[str, Any]) -> str:
    fall, unc, req = (
        round(v["fall_mm"]),
        max(1, round(v["uncertainty_mm"])),
        round(v["required_mm"]),
    )
    over = f"of fall over {v['run_m']:.2f} m"
    if v["verdict"] == "won't drain":
        flat = "Flat: " if fall == 0 else ""
        return f"{flat}{fall} ± {unc} mm {over}. It needs about {req} mm, so water will pond."
    if v["verdict"] == "drains":
        return f"{fall} ± {unc} mm {over}; it needs {req}, so it drains."
    return f"About {fall} ± {unc} mm {over}; it needs {req}, so that's inconclusive. Put a real level on it."


# --- follow-up questions on the stored survey -----------------------------------------------

_SUPERLATIVES = {
    "widest": ("w", max),
    "narrowest": ("w", min),
    "tallest": ("h", max),
    "shortest": ("h", min),
    "largest": ("area", max),
    "biggest": ("area", max),
    "smallest": ("area", min),
}


def answer_query(question: str, survey: dict[str, Any]) -> tuple[str, list[str]] | None:
    """(spoken answer, ids to highlight) for the questions templates cover -- widest, narrowest,
    tallest, shortest, largest/biggest, smallest -- else None (the agent answers from `csv()`)."""
    q = question.lower()
    word = next((w for w in _SUPERLATIVES if w in q), None)
    items = [i for i in survey.get("items", []) if i.get("w_m") and i.get("h_m")]
    if word is None or not items:
        return None
    key, pick = _SUPERLATIVES[word]

    def value(i):
        return {"w": i["w_m"], "h": i["h_m"]}.get(key, i["w_m"] * i["h_m"])

    best = pick(items, key=value)
    ties = [i["id"] for i in items if abs(value(i) - value(best)) * 1000 <= 2 and i is not best]
    tail = f", tied with {len(ties)} more" if ties else ""
    amber = " It's amber, so check it by hand." if best.get("unverified") else ""
    spoken = (
        f"The {word} {noun(survey.get('label'), 1)} is {best['id']}: "
        f"{_dims_with_unit(best['w_m'] * 1000, best['h_m'] * 1000, 'size')}{tail}.{amber}"
    )
    return spoken, [best["id"], *ties]


def csv(survey: dict[str, Any], limit: int = 60) -> str:
    """Compact results for the LLM (brain.md S1 "survey_query" prompt): id,label,w,h,unverified."""
    rows = ["id,label,w_mm,h_mm,unverified"]
    for i in survey.get("items", [])[:limit]:
        w = round(i["w_m"] * 1000) if i.get("w_m") else ""
        h = round(i["h_m"] * 1000) if i.get("h_m") else ""
        rows.append(f"{i['id']},{i.get('label', '')},{w},{h},{int(bool(i.get('unverified')))}")
    return "\n".join(rows)
