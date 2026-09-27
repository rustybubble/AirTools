"""Scene digest (brain.md S1, "the Quartermaster uses the tape"): what a site's structure layer
holds, small enough for the agent.

`digest(site, scale)` reads `scene/<site>/scene.json` -> its `structure.r<rev>.json` (the snap
layer, api.md "structure.r<rev>.json") and returns:

- `counts` by object label (`cabinet_door`, `drawer`, `panel`, `appliance`, `window`, `door`, ...);
- size `groups` per label (greedy 10 mm clustering of `w_m` x `h_m` x `scale`, see tape_survey.py);
- `objects`, each `{id, label, group, w_mm, h_mm, center, normal}` in the package frame;
- `gutter_edges`: the 5 longest near-horizontal edges (>= 0.5 m) above the median edge height
  (gutter or fascia candidates for `check_slope`);
- `gravity_residual_deg` from scene.json (the slope verdict's uncertainty).

Only `llm_line()` (counts + groups, a few hundred tokens at most) goes to the LLM; the numbers the
user hears always come from the headset's own tape (`POST /agent/observe`), never from here.
Files on disk only, so it works offline. Parsed layers are cached by (site, structure file):
revision-named files never change once published (api.md "Revision-named files").
"""

import json
import logging
import math
import re
import statistics
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from server import tape_survey
from server.config import get_settings

logger = logging.getLogger(__name__)

_SITE_RE = re.compile(r"^[a-z0-9-]+$")
_FILE_RE = re.compile(r"^[A-Za-z0-9._-]+$")
GUTTER_MAX_TILT_DEG = 5.0  # "near-horizontal": a draining gutter tilts ~0.1 deg
GUTTER_MIN_LENGTH_M = 0.5  # shorter than a hand span is a trim detail, not a run to drain
GUTTER_CANDIDATES = 5
LLM_GROUPS_PER_LABEL = 3

_layers: dict[tuple[str, str], dict[str, Any]] = {}


@dataclass
class SceneDigest:
    site: str
    revision: int | None = None
    structure: bool = False  # False: the site has no structure layer (preview, or none written)
    scale: float = 1.0
    gravity_residual_deg: float | None = None
    counts: dict[str, int] = field(default_factory=dict)
    groups: dict[str, list[dict[str, Any]]] = field(default_factory=dict)  # label -> size groups
    objects: list[dict[str, Any]] = field(default_factory=list)
    gutter_edges: list[dict[str, Any]] = field(default_factory=list)

    def count(self, label: str) -> int:
        return sum(self.counts.values()) if label == "any" else self.counts.get(label, 0)

    def llm_line(self) -> str:
        """One compact line for the agent's system prompt, e.g. "Scene objects (structure
        layer): 18 cabinet_door (6x 262x279 mm, 4x 208x525 mm, 2x 261x426 mm, +5 sizes); ..."."""
        if not self.structure:
            return "Scene: no structure layer, so no objects to survey."
        if not self.counts:
            return "Scene objects (structure layer): none."
        parts = []
        for label, n in sorted(self.counts.items(), key=lambda kv: (-kv[1], kv[0])):
            groups = self.groups.get(label, [])
            sizes = [f"{g['count']}x {g['w_mm']}x{g['h_mm']} mm" for g in groups]
            more = len(groups) - LLM_GROUPS_PER_LABEL
            if more > 0:
                sizes = [*sizes[:LLM_GROUPS_PER_LABEL], f"+{more} size{'s' if more > 1 else ''}"]
            parts.append(f"{n} {label}" + (f" ({', '.join(sizes)})" if sizes else ""))
        return "Scene objects (structure layer): " + "; ".join(parts) + "."


def _site_dir(site: str) -> Path | None:
    if not _SITE_RE.match(site or ""):
        return None
    path = Path(get_settings().SCENE_DIR) / site
    return path if path.is_dir() else None


def _load_layer(site_dir: Path, site: str, file: str) -> dict[str, Any] | None:
    key = (site, file)
    if key not in _layers:
        path = site_dir / file
        if not _FILE_RE.match(file) or not path.is_file():
            return None
        _layers[key] = json.loads(path.read_text())
    return _layers[key]


def _center(points: list[list[float]]) -> list[float]:
    return [round(sum(p[i] for p in points) / len(points), 4) for i in range(3)]


def _gutter_edges(edges: list[dict[str, Any]], scale: float) -> list[dict[str, Any]]:
    """Longest near-horizontal edges above the median edge height (+Y is up in the package)."""
    mids = [(e["a"][1] + e["b"][1]) / 2 for e in edges if "a" in e and "b" in e]
    if not mids:
        return []
    median = statistics.median(mids)
    max_rise = math.sin(math.radians(GUTTER_MAX_TILT_DEG))
    found = []
    for e in edges:
        a, b = e.get("a"), e.get("b")
        if not a or not b:
            continue
        length = math.dist(a, b)
        mid_y = (a[1] + b[1]) / 2
        if length * scale < GUTTER_MIN_LENGTH_M or abs(a[1] - b[1]) / length > max_rise:
            continue
        if mid_y <= median:
            continue
        found.append(
            {
                "id": e["id"],
                "length_m": round(length * scale, 3),
                "height_m": round(mid_y * scale, 3),
                "kind": e.get("kind"),
            }
        )
    # Longest first (to the cm), then the highest: a gutter's top edge beats its front lip.
    found.sort(key=lambda c: (-round(c["length_m"], 2), -c["height_m"]))
    return found[:GUTTER_CANDIDATES]


def digest(site: str | None, scale: float = 1.0) -> SceneDigest | None:
    """The site's digest, or None when the server doesn't have that site (an unknown name, or the
    app's built-in scene). `structure=False` when the site exists but has no structure layer."""
    site_dir = _site_dir(site or "")
    if site_dir is None:
        return None
    try:
        meta = json.loads((site_dir / "scene.json").read_text())
    except (OSError, json.JSONDecodeError):
        return None
    scale = scale if scale and scale > 0 else 1.0
    out = SceneDigest(
        site=site,
        revision=meta.get("revision"),
        scale=scale,
        gravity_residual_deg=meta.get("gravity_residual_deg"),
    )
    entry = meta.get("structure") or {}
    try:
        layer = _load_layer(site_dir, site, entry.get("file") or "")
    except (OSError, json.JSONDecodeError) as exc:
        logger.warning("scene digest: unreadable structure layer for %s: %s", site, exc)
        layer = None
    if layer is None:
        return out
    out.structure = True

    normals = {p.get("id"): p.get("normal") for p in layer.get("planes", [])}
    for obj in layer.get("objects", []):
        corners = obj.get("corners3d") or []
        if obj.get("w_m") is None or obj.get("h_m") is None:
            continue
        out.objects.append(
            {
                "id": obj["id"],
                "label": obj.get("label", "object"),
                "group": obj.get("group"),
                "w_mm": round(obj["w_m"] * scale * 1000),
                "h_mm": round(obj["h_m"] * scale * 1000),
                "center": _center(corners) if len(corners) == 4 else None,
                "normal": normals.get(obj.get("plane")),
            }
        )
    for o in out.objects:
        out.counts[o["label"]] = out.counts.get(o["label"], 0) + 1
    for label in out.counts:
        members = [(o["id"], o["w_mm"], o["h_mm"]) for o in out.objects if o["label"] == label]
        out.groups[label] = tape_survey.group_sizes(members)
    out.gutter_edges = _gutter_edges(layer.get("edges", []), scale)
    return out


def layer(site: str | None) -> dict[str, Any] | None:
    """The site's raw structure layer (planes, edges, corners, objects; cached), or None."""
    site_dir = _site_dir(site or "")
    if site_dir is None:
        return None
    try:
        meta = json.loads((site_dir / "scene.json").read_text())
        return _load_layer(site_dir, site, (meta.get("structure") or {}).get("file") or "")
    except (OSError, json.JSONDecodeError):
        return None
