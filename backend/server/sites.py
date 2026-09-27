"""Which scanned site a spoken name means, for Model view by voice ("show me the gym model" ->
`show_model {site: "zabel-gymnasium"}`; the app's Runtime/Agent/ModelViewActions.cs and
Runtime/Scene/ModelSites.Match take the same names). The sites are SCENE_DIR's packages (GET
/scenes) plus the facade built into the app ("built-in")."""

import json
import re
from pathlib import Path

from server.config import get_settings

BUILT_IN = "built-in"
# Spoken names the site ids don't contain.
ALIASES = {
    "gym": "zabel-gymnasium",
    "gymnasium": "zabel-gymnasium",
    "zabel": "zabel-gymnasium",
    "hospital": "hospital-bg",
    "tower": "gt-lcc-tower",
    "canopy": "gt-lcc-canopy",
    "pavilion": "gt-lcc-pavilion",
    "facade": BUILT_IN,
    "house": BUILT_IN,
    "test facade": BUILT_IN,
}
NAMES = {
    "zabel-gymnasium": "Zabel gym",
    "hospital-bg": "hospital",
    "gt-lcc-tower": "GT tower",
    "gt-lcc-canopy": "GT canopy",
    "gt-lcc-pavilion": "GT pavilion",
    BUILT_IN: "test facade",
}
_FILLER = {"the", "a", "an", "model", "scan", "scene", "site", "of", "our", "my", "that", "this"}


def listed() -> list[str]:
    """The site ids with a scene.json under SCENE_DIR, A-Z."""
    base = Path(get_settings().SCENE_DIR)
    try:
        return sorted(d.name for d in base.iterdir() if (d / "scene.json").is_file())
    except OSError:
        return []


def name(site: str) -> str:
    """ "Zabel gym", "kitchen", "GT tower"."""
    if site in NAMES:
        return NAMES[site]
    try:
        title = json.loads((Path(get_settings().SCENE_DIR) / site / "scene.json").read_text())
        title = title.get("title")
    except (OSError, ValueError, AttributeError):
        title = None
    return title or site.replace("-", " ")


def _words(s: str) -> list[str]:
    return [w for w in re.findall(r"[a-z0-9]+", s.lower()) if w not in _FILLER]


def match(said: str | None) -> str | None:
    """The site a name means: the id itself, an alias ("the gym"), else the listed site sharing
    the most words with it ("gt tower" -> gt-lcc-tower). Test packages (synthetic-*) only by id.
    None when nothing matches."""
    words = _words(said or "")
    if not words:
        return None
    sites = listed()
    joined = "-".join(words)
    if joined in sites:
        return joined
    phrase = " ".join(words)
    for alias in sorted(ALIASES, key=len, reverse=True):
        if re.search(rf"\b{re.escape(alias)}\b", phrase):
            site = ALIASES[alias]
            if site == BUILT_IN or site in sites:
                return site
    best, best_score = None, 0
    for site in sites:
        if site.startswith("synthetic"):
            continue
        own = set(_words(site.replace("-", " ") + " " + name(site)))
        score = sum(1 for w in words if w in own)
        if score > best_score:
            best, best_score = site, score
    return best
