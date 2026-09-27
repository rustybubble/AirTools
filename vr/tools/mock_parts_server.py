#!/usr/bin/env python3
"""Mock of the AirTool parts server (backend repo docs/api.md). Python standard library only.

Mirrors the real FastAPI server (airtools-drone-backend `server/app.py`) closely enough for the app's automated
tests, without API keys, network or GPU:

  GET  /health
  GET  /scenes                               sites under --scene-dir that have a scene.json
  GET  /scenes/<site>/<file>                 scene.json: no-cache + ETag (304 on If-None-Match); *.r<rev>.* immutable
  POST /parts/search {query, measurement?}   -> {job_id, status, stage, candidates?}  (cache hits are done at once)
  GET  /parts/jobs/<id>                      -> {id, status: running|done, stage, query, candidates: [Part], summary}
  GET  /parts/<id>/part.json|model.glb|image.jpg   fixtures from Assets/AirTools/Fixtures/Parts/<id>/, enriched with the
                                             real contract's fields (asset.status pending → ready after --asset-delay,
                                             seller shipping_usd / total_usd / eta_days / pack_qty, image_url, fit)
  POST /parts/<id>/sellers?sort=cheapest|fastest|best -> the part with sellers re-ranked + recommended_seller/reason
  POST /parts/bom {part_ids, counts}         -> a Bom (screws for hangers, sealant for window units)
  POST /checkout {part_id, seller_idx, qty, session_id?, bom_id?, bom_lines?}
                                             -> the labelled OFFLINE receipt (status OFFLINE_RECEIPT, mode offline) like
                                                the real server without Cybersource keys; --sandbox gives AUTHORIZED.
                                                With the hold proof {cart_hash, hold_nonce, hold_ms} (B3) it is verified:
                                                400 short/partial hold, 409 stale/used/foreign nonce or changed cart, 422
                                                {"error", "checks"} when a check fails; the receipt gains `mandate`.
                                                --require-hold-proof refuses a checkout without the proof (400)
  POST /checkout/prepare {session_id, part_id, seller_idx, units_needed, bom_id?, bom_lines?, evidence[]}
                                             -> {cart (packs from pack_qty), cart_hash, hold_nonce (single use, 120 s; a newer
                                                prepare revokes it), nonce_expires_at, intent, checks[7], all_ok}
  POST /commerce/limits {session_id, source: voice|panel, max_total_usd?, deliver_by?, seller_policy?, text?}
                                             -> {intent, refused, changed}: only fields present change; voice can only
                                                tighten, the panel may loosen or clear (null). Limits, carts and nonces live
                                                in memory (a restart forgets them; the app just prepares again)
  POST /notebook {session_id, entries[]} / GET /notebook/<session_id>
  POST /agent/command {session_id, text, context}  the real agent's deterministic fast path (equip / select / cheapest|
                                             fastest / every N cm / what else … need) plus a literal "find a <thing>", i.e. the
                                             real server with OFFLINE=true. B1: "measure every cabinet door" → survey,
                                             "check the gutter slope" → check_slope (edge_ids from the site's structure layer),
                                             a bare "stop" → stop_survey, "which one is the tallest?" after a survey → show_tape_survey
                                             with focus. B3: limit phrases in any command ("under $40, arriving by Friday")
                                             → show_limits (voice only tightens), then the rest is routed as usual.
                                             Context `site` (a --scene-dir package) and `scale` like the real one
  POST /agent/observe {session_id, request_id, kind: survey_result|slope_result, …}
                                             the headset's survey / slope report → spoken reply + show_tape_survey (10 mm size
                                             groups) or add_note (drainage verdict); 422 like FastAPI; tts: true adds WAV audio
  POST /voice/command (multipart)            503 like OFFLINE, unless --voice-transcript "…": then each recording "says" the
                                             next "|"-separated phrase (one phrase = always that), and the reply has WAV audio
  POST /mock/voice-transcript {text}         mock only: replace that script at runtime, from its first phrase
  POST /voice/speak {text}                   a short WAV tone
  POST /voice/token                          503 (realtime voice is not active)
  POST /scene/ask {question, frames[]}       answers about the first frame with a box at its centre (deterministic
                                             pin test: the synthetic cameras all look at the wall centre)

Every request is logged (stdout + tools/.mock_server_data/requests.log) so tests can assert what was or wasn't sent.
Run:  python3 tools/mock_parts_server.py [--port 8000] [--delay 1.5] [--asset-delay 2] [--scene-dir tools/.scenes]
"""
import argparse, base64, email.parser, email.policy, hashlib, io, json, math, os, re, statistics, threading, time, uuid, wave
from collections import OrderedDict
from datetime import date, datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PARTS_DIR = os.path.join(ROOT, "Assets", "AirTools", "Fixtures", "Parts")
DATA_DIR = os.path.join(ROOT, "tools", ".mock_server_data")
CONTENT_TYPES = {".json": "application/json", ".glb": "model/gltf-binary", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}
OFFLINE_LABEL = "Offline receipt — no payment was authorized (sandbox not configured)"
SANDBOX_LABEL = "Sandbox authorization — Visa Acceptance / Cybersource test environment, no real charge"

KEYWORDS = {
    "hidden-hanger-5k": ["hanger", "gutter", "bracket", "k-style", "fascia"],
    "window-ac-small": ["ac", "air", "conditioner", "window", "cooling"],
}
BOMS = {
    "hidden-hanger-5k": [("Gutter screws (100-pack)", 1, "fasten the hangers to the fascia", "Home Depot", 12.0, 100)],
    "window-ac-small": [("Window foam insulation strip", 1, "seal the gap above the unit", "Lowe's", 7.48, 1)],
}
DAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]


class State:
    lock = threading.Lock()
    jobs = {}          # job_id -> (created, part_ids, query)
    cache = {}         # normalised query -> part_ids
    asset_ready_at = {}  # part_id -> time the model becomes "ready"
    boms = {}          # bom_id -> bom
    notebooks = {}     # session_id -> [entries]
    fit_measurement = {}  # part_id -> the measurement of the last search that returned it (the saved part's fit)
    intents = {}       # session_id -> intent (limits), 24 h
    carts = {}         # cart_id -> prepared cart
    nonces = {}        # hold nonce -> {cart_id, cart_hash, session_id, expires_at}, single use, 120 s
    require_hold_proof = False
    delay = 1.5
    asset_delay = 2.0
    scene_dir = os.path.join(ROOT, "tools", ".scenes")
    sandbox = False
    voice_transcript = None  # --voice-transcript "a|b|c": what each recording "says", in turn (then from the top)
    voice_index = 0


def now():
    """UTC now (tests patch this to age hold nonces)."""
    return datetime.now(timezone.utc)


def today():
    """The laptop's local date, like the real server's mandate._today() ("by Friday" is the user's Friday)."""
    return datetime.now().astimezone().date()


def log(line):
    os.makedirs(DATA_DIR, exist_ok=True)
    stamp = time.strftime("%Y-%m-%dT%H:%M:%S")
    entry = f"{stamp} {line}"
    print(entry, flush=True)
    with open(os.path.join(DATA_DIR, "requests.log"), "a") as f:
        f.write(entry + "\n")


# ---------------------------------------------------------------- parts

def raw_part(part_id):
    path = os.path.join(PARTS_DIR, part_id, "part.json")
    if not re.fullmatch(r"[a-z0-9-]+", part_id or "") or not os.path.isfile(path):
        return None
    with open(path) as f:
        return json.load(f)


def all_part_ids():
    if not os.path.isdir(PARTS_DIR):
        return []
    return sorted(d for d in os.listdir(PARTS_DIR) if os.path.isfile(os.path.join(PARTS_DIR, d, "part.json")))


def eta_days(eta):
    t = (eta or "").strip().lower()
    if t.startswith("today"):
        return 0
    if t.startswith("tomorrow"):
        return 1
    for i, d in enumerate(DAYS):
        if t.startswith(d):
            return (i - today().weekday()) % 7 or 7
    m = re.search(r"\d+", t)
    return int(m.group()) if m else None


def shipping_usd(text):
    t = (text or "").lower()
    if not t or "free" in t:
        return 0.0
    m = re.search(r"\d+(\.\d+)?", t)
    return float(m.group()) if m else None


def enrich(part, measurement=None):
    """The fixture part.json plus the fields the real server's Part model carries (models.py)."""
    p = json.loads(json.dumps(part))
    pid = p["id"]
    for s in p.get("sellers") or []:
        s.setdefault("pack_qty", 1)
        s.setdefault("shipping_usd", shipping_usd(s.get("shipping")))
        price = s.get("price_usd")
        s.setdefault("unit_price_usd", round(price / s["pack_qty"], 4) if price is not None else None)
        s.setdefault("total_usd", round(price + (s["shipping_usd"] or 0), 2) if price is not None else None)
        s.setdefault("eta_days", eta_days(s.get("eta")))
        s.setdefault("reviews", None)
        s.setdefault("verified", True)
    ready_at = State.asset_ready_at.get(pid, 0)
    p.setdefault("asset", {})["status"] = "ready" if time.time() >= ready_at else "pending"
    p.setdefault("dims_source", "home_depot")
    p.setdefault("image_url", f"/parts/{pid}/image.jpg")
    p.setdefault("sellers_expanded", False)
    lo, hi = p.get("min_window_width_mm"), p.get("max_window_width_mm")
    if lo is not None or hi is not None:
        p.setdefault("fit_range_mm", {"min": lo, "max": hi})
    p["fit"] = server_fit(p, measurement)
    p["cached"] = True
    return p


def server_fit(p, measurement):
    fit = {"status": "unknown", "spare_mm": None, "axis": None, "note": ""}
    if not measurement or measurement.get("value_m") is None:
        return fit
    have = float(measurement["value_m"]) * 1000
    rng = p.get("fit_range_mm")
    if rng:
        lo, hi = rng.get("min"), rng.get("max")
        if hi is not None and have > hi:
            return {"status": "too_small", "spare_mm": round(hi - have, 1), "axis": "w", "note": f"fits a {lo:.0f}–{hi:.0f} mm opening"}
        if lo is not None and have < lo:
            return {"status": "too_big", "spare_mm": round(have - lo, 1), "axis": "w", "note": f"fits a {lo:.0f}–{hi:.0f} mm opening"}
        return {"status": "fits", "spare_mm": round(min(have - (lo or 0), (hi or have) - have), 1), "axis": "w", "note": "fits the opening"}
    spare = round(have - p["dims_mm"]["w"], 1)
    return {"status": "fits" if spare >= 0 else "too_big", "spare_mm": spare, "axis": "w", "note": "exact fit" if abs(spare) < 1 else ""}


def match(query):
    words = set(re.findall(r"[a-z0-9-]+", (query or "").lower()))
    return [pid for pid in all_part_ids() if words & set(KEYWORDS.get(pid, []))]


def summary_line(parts):
    if not parts:
        return "The supply lines are down; only the cached parts are aboard."
    first = parts[0]
    word = {1: "One", 2: "Two", 3: "Three"}.get(len(parts), str(len(parts)))
    fit = first.get("fit") or {}
    tail = ""
    if fit.get("status") == "fits" and fit.get("spare_mm") is not None:
        tail = f" The first fits with {fit['spare_mm']:.0f} millimetres to spare."
    return f"{word} found: {first['name']}.{tail}"


def rerank(part, sort):
    sellers = part.get("sellers") or []
    order = list(range(len(sellers)))
    big = 1e9
    if sort == "fastest":
        order.sort(key=lambda i: (sellers[i].get("eta_days") if sellers[i].get("eta_days") is not None else big, sellers[i].get("total_usd") or big))
        reason = "arrives soonest"
    elif sort == "best":
        order.sort(key=lambda i: (-(sellers[i].get("rating") or 0), sellers[i].get("total_usd") or big))
        reason = "best rated"
    else:
        order.sort(key=lambda i: (sellers[i].get("total_usd") if sellers[i].get("total_usd") is not None else big))
        reason = "cheapest delivered"
    part["sellers"] = [sellers[i] for i in order]
    part["recommended_seller"] = 0 if sellers else None
    part["recommendation_reason"] = reason
    part["sellers_expanded"] = True
    return part


def make_bom(part_ids, counts):
    lines, idx = [], 0
    for pid in part_ids:
        for name, qty, why, seller, price, pack in BOMS.get(pid, []):
            lines.append({"idx": idx, "name": name, "qty": qty, "reason": why,
                          "seller": {"name": seller, "price_usd": price, "pack_qty": pack, "unit_price_usd": round(price / pack, 4),
                                     "shipping_usd": 0.0, "total_usd": price, "eta": "Tue", "eta_days": eta_days("Tue"),
                                     "rating": 4.6, "reviews": 120, "in_stock": True, "url": "https://www.homedepot.com/", "verified": True}})
            idx += 1
    bom = {"id": f"bom-{uuid.uuid4().hex[:12]}", "part_ids": part_ids, "lines": lines,
           "total_usd": round(sum(l["seller"]["price_usd"] * l["qty"] for l in lines), 2)}
    State.boms[bom["id"]] = bom
    return bom


# ---------------------------------------------------------------- scene digest (real: server/scene_digest.py)
# What a site's structure layer (--scene-dir/<site>/scene.json -> structure.r<rev>.json) holds: counts and 10 mm size
# groups per object label, and gutter-edge candidates for check_slope. The numbers the user hears never come from here:
# they come from the headset's own tape via POST /agent/observe.

SITE_RE = re.compile(r"^[a-z0-9-]+$")
GUTTER_MAX_TILT_DEG = 5.0   # "near-horizontal": a draining gutter tilts ~0.1 deg
GUTTER_MIN_LENGTH_M = 0.5
GUTTER_CANDIDATES = 5
_LAYERS = {}  # (structure file, mtime) -> parsed layer


def _gutter_edges(edges, scale):
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
        if length * scale < GUTTER_MIN_LENGTH_M or abs(a[1] - b[1]) / length > max_rise or mid_y <= median:
            continue
        found.append({"id": e["id"], "length_m": round(length * scale, 3), "height_m": round(mid_y * scale, 3), "kind": e.get("kind")})
    found.sort(key=lambda c: (-round(c["length_m"], 2), -c["height_m"]))  # longest (to the cm), then highest
    return found[:GUTTER_CANDIDATES]


def scene_digest(site, scale=1.0):
    """The site's digest, or None when the mock doesn't have that site (an unknown name, or the app's built-in scene).
    `structure` is False when the site exists but has no structure layer (a preview)."""
    if not SITE_RE.match(site or ""):
        return None
    site_dir = os.path.join(State.scene_dir, site)
    try:
        with open(os.path.join(site_dir, "scene.json")) as f:
            meta = json.load(f)
    except (OSError, json.JSONDecodeError):
        return None
    scale = scale if scale and scale > 0 else 1.0
    d = {"site": site, "revision": meta.get("revision"), "structure": False, "scale": scale,
         "gravity_residual_deg": meta.get("gravity_residual_deg"), "counts": {}, "groups": {}, "objects": [], "gutter_edges": []}
    entry = meta.get("structure") if isinstance(meta.get("structure"), dict) else {}
    file = entry.get("file") or ""
    path = os.path.join(site_dir, file)
    if not re.fullmatch(r"[A-Za-z0-9._-]+", file) or not os.path.isfile(path):
        return d
    try:
        key = (path, os.path.getmtime(path))
        if key not in _LAYERS:
            with open(path) as f:
                _LAYERS[key] = json.load(f)
        layer = _LAYERS[key]
    except (OSError, json.JSONDecodeError) as exc:
        log(f"scene digest: unreadable structure layer for {site}: {exc}")
        return d
    d["structure"] = True
    normals = {p.get("id"): p.get("normal") for p in layer.get("planes", [])}
    for obj in layer.get("objects", []):
        if obj.get("w_m") is None or obj.get("h_m") is None:
            continue
        corners = obj.get("corners3d") or []
        d["objects"].append({"id": obj["id"], "label": obj.get("label", "object"), "group": obj.get("group"),
                             "w_mm": round(obj["w_m"] * scale * 1000), "h_mm": round(obj["h_m"] * scale * 1000),
                             "center": [round(sum(p[i] for p in corners) / 4, 4) for i in range(3)] if len(corners) == 4 else None,
                             "normal": normals.get(obj.get("plane"))})
    for o in d["objects"]:
        d["counts"][o["label"]] = d["counts"].get(o["label"], 0) + 1
    for label in d["counts"]:
        d["groups"][label] = group_sizes([(o["id"], o["w_mm"], o["h_mm"]) for o in d["objects"] if o["label"] == label])
    d["gutter_edges"] = _gutter_edges(layer.get("edges", []), scale)
    return d


def digest_count(d, label):
    return sum(d["counts"].values()) if label == "any" else d["counts"].get(label, 0)


# ---------------------------------------------------------------- survey + slope (real: server/survey.py, no LLM)

LABELS = ("cabinet_door", "drawer", "panel", "appliance", "window", "door", "any")
WHERES = ("all", "visible", "upper", "lower", "left", "right", "nearest")
MEASURES = ("size", "width", "height")
SLOPE_TARGETS = ("gutter", "sill", "ledge", "nearest_edge")
GROUP_TOLERANCE_MM = 10.0
SPOKEN_GROUPS = 3
GUTTER_MM_PER_M = 2.083  # 1/4 in of fall per 10 ft
SNAP_UNCERTAINTY_MM = 2.0
NOUNS = {"cabinet_door": ("door", "doors"), "drawer": ("drawer", "drawers"), "panel": ("panel", "panels"),
         "appliance": ("appliance", "appliances"), "window": ("window", "windows"), "door": ("door", "doors"),
         "any": ("object", "objects")}
NUMBER_WORDS = ["No", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine"]
UNIT_WORD = {"cm": "centimetres", "mm": "millimetres"}
SIDE_WORD = {"width": " wide", "height": " tall"}
SUPERLATIVES = {"widest": ("w", max), "narrowest": ("w", min), "tallest": ("h", max), "shortest": ("h", min),
                "largest": ("area", max), "biggest": ("area", max), "smallest": ("area", min)}


def noun(label, n=2):
    """"door"/"doors" for cabinet_door."""
    singular, plural = NOUNS.get(label) or ((label or "object").replace("_", " "), (label or "object").replace("_", " ") + "s")
    return singular if n == 1 else plural


def group_sizes(items, measure="size", tol_mm=GROUP_TOLERANCE_MM):
    """Greedy clustering of (id, w_mm, h_mm): smallest first, an item joins the first group whose running mean is within
    `tol_mm` on the measured side(s), else starts one. Most common first, then widest; `ids` keep the sweep order."""
    groups = []
    order = {item_id: i for i, (item_id, _, _) in enumerate(items)}
    for item_id, w, h in sorted(items, key=lambda it: (it[1], it[2], order[it[0]])):
        for g in groups:
            if (abs(g["_w"] - w) <= tol_mm or measure == "height") and (abs(g["_h"] - h) <= tol_mm or measure == "width"):
                n = len(g["ids"])
                g["_w"] = (g["_w"] * n + w) / (n + 1)
                g["_h"] = (g["_h"] * n + h) / (n + 1)
                g["ids"].append(item_id)
                break
        else:
            groups.append({"_w": w, "_h": h, "ids": [item_id]})
    out = [{"w_mm": round(g["_w"]), "h_mm": round(g["_h"]), "count": len(g["ids"]), "ids": sorted(g["ids"], key=order.get)}
           for g in groups]
    out.sort(key=lambda g: (-g["count"], -g["w_mm"], -g["h_mm"]))
    return out


def _spoken_len(mm, unit):
    """Half-up: 525 mm is "53" cm; mm to the nearest 5."""
    return str(int(mm / 10 + 0.5)) if unit == "cm" else str(5 * int(mm / 5 + 0.5))


def _dims(w_mm, h_mm, measure):
    sides = {"width": [w_mm], "height": [h_mm]}.get(measure, [w_mm, h_mm])
    unit = "cm" if all(s > 100 for s in sides) else "mm"
    return " by ".join(_spoken_len(s, unit) for s in sides), unit


def _dims_with_unit(w_mm, h_mm, measure):
    dims, unit = _dims(w_mm, h_mm, measure)
    return f"{dims} {UNIT_WORD[unit]}{SIDE_WORD.get(measure, '')}"


def _groups_phrase(groups, measure):
    spoken = [(f"{g['count']} at {_dims(g['w_mm'], g['h_mm'], measure)[0]}", _dims(g["w_mm"], g["h_mm"], measure)[1])
              for g in groups[:SPOKEN_GROUPS]]
    units = {u for _, u in spoken}
    side = SIDE_WORD.get(measure, "")
    if len(units) == 1:
        texts = [t for t, _ in spoken]
        texts[-1] += f" {UNIT_WORD[units.pop()]}{side}"
    else:
        texts = [f"{t} {UNIT_WORD[u]}{side}" for t, u in spoken]
    more = len(groups) - SPOKEN_GROUPS
    if more > 0:
        texts.append(f"{more} more size{'s' if more > 1 else ''} on the card")
    return ", ".join(texts[:-1]) + (" and " if len(texts) > 1 else "") + texts[-1]


def _count_word(n):
    return NUMBER_WORDS[n] if n < len(NUMBER_WORDS) else str(n)


def survey_summary(obs, label, measure):
    """Spoken reply + show_tape_survey fields for a survey the headset finished (or was stopped)."""
    skipped = [s["id"] for s in obs["skipped"]]
    if obs["status"] == "no_structure":
        return {"reply": "No structure layer for this scene; mark the corners yourself.", "groups": [], "unverified": [], "skipped": skipped}
    groups = group_sizes([(r["id"], r["w_m"] * 1000, r["h_m"] * 1000) for r in obs["results"]
                          if r["w_m"] is not None and r["h_m"] is not None], measure)
    unverified = [r["id"] for r in obs["results"] if r["unverified"]]
    n = sum(g["count"] for g in groups)
    plural = noun(label)
    bits = []
    if obs["status"] == "aborted":
        planned = f" of {obs['planned']}" if obs["planned"] else ""
        bits.append(f"Stopped after {n}{planned}.")
    if n == 0:
        bits.append(f"No {plural} measured." if obs["status"] == "aborted" else f"Found no {plural} to measure.")
    elif n == 1:
        bits.append(f"1 {noun(label, 1)}: {_dims_with_unit(groups[0]['w_mm'], groups[0]['h_mm'], measure)}.")
    elif len(groups) == 1:
        bits.append(f"{n} {plural}, all {_dims_with_unit(groups[0]['w_mm'], groups[0]['h_mm'], measure)}.")
    else:
        bits.append(f"{n} {plural} in {len(groups)} sizes: {_groups_phrase(groups, measure)}.")
    if unverified:
        bits.append(f"{_count_word(len(unverified))} didn't lock onto corners; they're amber." if len(unverified) > 1
                    else "One didn't lock onto its corners; it's amber.")
    if skipped:
        bits.append(f"{_count_word(len(skipped))} skipped.")
    return {"reply": " ".join(bits), "groups": groups, "unverified": unverified, "skipped": skipped}


def slope_uncertainty_mm(run_m, gravity_residual_deg):
    """How wrong "up" can be over the run, plus snapping: run * tan(residual) + 2 mm."""
    return round(run_m * 1000 * math.tan(math.radians(abs(gravity_residual_deg or 0.0))) + SNAP_UNCERTAINTY_MM, 1)


def slope_verdict(run_m, fall_mm, uncertainty_mm):
    """drains if even the low estimate is enough, won't drain if even the high one isn't, else inconclusive."""
    required = run_m * GUTTER_MM_PER_M
    fall = abs(fall_mm)
    verdict = "drains" if fall - uncertainty_mm >= required else "won't drain" if fall + uncertainty_mm < required else "inconclusive"
    return {"verdict": verdict, "fall_mm": round(fall, 1), "uncertainty_mm": round(uncertainty_mm, 1),
            "required_mm": round(required, 1), "run_m": round(run_m, 3)}


def slope_reply(v):
    fall, unc, req = round(v["fall_mm"]), max(1, round(v["uncertainty_mm"])), round(v["required_mm"])
    over = f"of fall over {v['run_m']:.2f} m"
    if v["verdict"] == "won't drain":
        return f"{'Flat: ' if fall == 0 else ''}{fall} ± {unc} mm {over}. It needs about {req} mm, so water will pond."
    if v["verdict"] == "drains":
        return f"{fall} ± {unc} mm {over}; it needs {req}, so it drains."
    return f"About {fall} ± {unc} mm {over}; it needs {req}, so that's inconclusive. Put a real level on it."


def answer_query(question, stored):
    """(spoken answer, ids to highlight) for widest / narrowest / tallest / shortest / largest / biggest / smallest,
    else None (the real server hands anything else to the LLM; the mock is offline)."""
    q = question.lower()
    word = next((w for w in SUPERLATIVES if w in q), None)
    items = [i for i in stored.get("items", []) if i.get("w_m") and i.get("h_m")]
    if word is None or not items:
        return None
    key, pick = SUPERLATIVES[word]

    def value(i):
        return {"w": i["w_m"], "h": i["h_m"]}.get(key, i["w_m"] * i["h_m"])

    best = pick(items, key=value)
    ties = [i["id"] for i in items if abs(value(i) - value(best)) * 1000 <= 2 and i is not best]
    tail = f", tied with {len(ties)} more" if ties else ""
    amber = " It's amber, so check it by hand." if best.get("unverified") else ""
    spoken = (f"The {word} {noun(stored.get('label'), 1)} is {best['id']}: "
              f"{_dims_with_unit(best['w_m'] * 1000, best['h_m'] * 1000, 'size')}{tail}.{amber}")
    return spoken, [best["id"], *ties]


# ---------------------------------------------------------------- request validation (FastAPI-shaped 422 details)

def verr(loc, msg, typ, value=None):
    return {"type": typ, "loc": ["body", *loc], "msg": msg, "input": value}


def is_num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def as_int(v):
    """An int, or an integral float (pydantic's lax mode); anything else -> None."""
    if isinstance(v, bool):
        return None
    if isinstance(v, int):
        return v
    if isinstance(v, float) and v.is_integer():
        return int(v)
    return None


class Fields:
    """Collects typed body fields and 422 errors the way the real server's pydantic models report them."""

    def __init__(self, body, loc=()):
        self.body, self.loc, self.errors = body, tuple(loc), []

    def missing(self, name):
        self.errors.append(verr([*self.loc, name], "Field required", "missing", self.body))

    def string(self, name, default=None, required=False, max_len=None, min_len=0, nullable=True):
        if name not in self.body:
            if required:
                self.missing(name)
            return default
        v = self.body[name]
        if v is None and nullable and not required:
            return None
        if not isinstance(v, str):
            self.errors.append(verr([*self.loc, name], "Input should be a valid string", "string_type", v))
            return default
        if max_len is not None and len(v) > max_len:
            self.errors.append(verr([*self.loc, name], f"String should have at most {max_len} characters", "string_too_long", v))
        if len(v) < min_len:
            self.errors.append(verr([*self.loc, name], f"String should have at least {min_len} character", "string_too_short", v))
        return v

    def number(self, name, default=None, required=False):
        if name not in self.body:
            if required:
                self.missing(name)
            return default
        v = self.body[name]
        if v is None and not required:
            return None
        if not is_num(v):
            self.errors.append(verr([*self.loc, name], "Input should be a valid number", "float_type", v))
            return default
        return float(v)

    def integer(self, name, default=None, required=False):
        if name not in self.body:
            if required:
                self.missing(name)
            return default
        v = self.body[name]
        if v is None and not required:
            return None
        if as_int(v) is None:
            self.errors.append(verr([*self.loc, name], "Input should be a valid integer", "int_type", v))
            return default
        return as_int(v)

    def choice(self, name, options, default=None, nullable=False):
        if name not in self.body:
            return default
        v = self.body[name]
        if v is None and nullable:
            return None
        if v not in options:
            expected = ", ".join(f"'{o}'" for o in options[:-1]) + f" or '{options[-1]}'"
            self.errors.append(verr([*self.loc, name], f"Input should be {expected}", "literal_error", v))
            return default
        return v

    def array(self, name, max_len=None):
        if name not in self.body or self.body[name] is None:
            return []
        v = self.body[name]
        if not isinstance(v, list):
            self.errors.append(verr([*self.loc, name], "Input should be a valid list", "list_type", v))
            return []
        if max_len is not None and len(v) > max_len:
            self.errors.append(verr([*self.loc, name], f"List should have at most {max_len} items after validation, not {len(v)}",
                                    "too_long", None))
            return []
        return v

    def sub(self, name, i, item):
        """A nested object's fields at name[i] (i None: a plain nested field); errors land in this collector."""
        loc = [*self.loc, name] + ([] if i is None else [i])
        f = Fields(item if isinstance(item, dict) else {}, loc)
        if not isinstance(item, dict):
            self.errors.append(verr(loc, "Input should be a valid dictionary or object", "model_type", item))
        f.errors = self.errors
        return f


def parse_observation(body):
    """POST /agent/observe body (real: survey.Observation) -> (obs dict, [422 errors])."""
    f = Fields(body)
    obs = {"session_id": f.string("session_id", required=True, nullable=False),
           "request_id": f.string("request_id", max_len=64),
           "kind": f.choice("kind", ("survey_result", "slope_result")) if "kind" in body else f.missing("kind"),
           "query": f.string("query", max_len=500),
           "tts": body.get("tts") is True or body.get("tts") == 1 or str(body.get("tts")).lower() in ("true", "yes", "on"),
           "label": f.string("label", max_len=64),
           "measure": f.choice("measure", MEASURES, nullable=True),
           "status": f.choice("status", ("done", "aborted", "no_structure"), default="done"),
           "planned": f.integer("planned"),
           "target": f.string("target", max_len=32),
           "run_m": f.number("run_m"), "fall_mm": f.number("fall_mm"),
           "low_end": body.get("low_end"),
           "gravity_residual_deg": f.number("gravity_residual_deg"),
           "uncertainty_mm": f.number("uncertainty_mm"),
           "notebook_id": body.get("notebook_id"),
           "results": [], "skipped": []}
    if obs["planned"] is not None and obs["planned"] < 0:
        f.errors.append(verr(["planned"], "Input should be greater than or equal to 0", "greater_than_equal", obs["planned"]))
    for i, item in enumerate(f.array("results", 500)):
        r = f.sub("results", i, item)
        obs["results"].append({
            "id": r.string("id", required=True, max_len=64, nullable=False), "label": r.string("label", default="", max_len=64, nullable=False) or "",
            "group": r.string("group", max_len=64), "w_m": r.number("w_m"), "h_m": r.number("h_m"), "area_m2": r.number("area_m2"),
            "angles_deg": r.body.get("angles_deg") or [], "off_plane_m": r.number("off_plane_m"), "snap": r.body.get("snap") or [],
            "unverified": bool(r.body.get("unverified", False)), "notebook_id": r.body.get("notebook_id"), "camera_id": r.integer("camera_id")})
    for i, item in enumerate(f.array("skipped", 500)):
        s = f.sub("skipped", i, item)
        obs["skipped"].append({"id": s.string("id", required=True, max_len=64, nullable=False),
                               "reason": s.string("reason", default="", max_len=200, nullable=False) or ""})
    if not f.errors:
        if obs["kind"] == "slope_result" and (obs["run_m"] is None or obs["fall_mm"] is None):
            f.errors.append(verr([], "Value error, slope_result needs run_m and fall_mm", "value_error", body))
        elif obs["run_m"] is not None and obs["run_m"] <= 0:
            f.errors.append(verr([], "Value error, run_m must be positive", "value_error", body))
    return obs, f.errors


# ---------------------------------------------------------------- agent (the real server's fast path, agent.py)

EQUIP = re.compile(r"\b(?:equip|grab)\s+(?:the\s+)?(tape|level|protractor|plumb|area|notebook|part)\b", re.I)
SELECT = re.compile(r"\b(?:select|pick)\s+(?:the\s+)?(?:number\s+(\d+)|(first|second|third))\b", re.I)
SORT = re.compile(r"\b(cheapest|fastest)\b(?:\s+(?:first|one))?[.!]?\s*$", re.I)
ARRAY = re.compile(r"\bevery\s+(\d+(?:\.\d+)?)\s*(centimeters?|centimetres?|cm|millimeters?|millimetres?|mm|inches?|inch|in)\b", re.I)
WHAT_ELSE = re.compile(r"\bwhat else\b.*\bneed\b", re.I)
FIND = re.compile(r"\b(?:find|get|need)\s+(?:me\s+)?(?:an?\s+|the\s+|some\s+)?(.+?)\s*(?:\bfor\b.*)?[.!?]?\s*$", re.I)
UNIT_MM = {"cm": 10.0, "mm": 1.0, "in": 25.4}
# B1: "measure every cabinet door", "size up all the windows", "measure the upper drawers", "measure the window" (the + one
# = the nearest). An optional qualifier word picks `where`.
SURVEY = re.compile(r"\b(?:measure|survey|size\s+up)\s+(every|all(?:\s+(?:of\s+)?the)?|each|the)\s+"
                    r"(?:(?!cabinet\b)(\w+)\s+)?(cabinet\s+doors?|doors?|drawers?|windows?|panels?|appliances?)\b", re.I)
SURVEY_VISIBLE = re.compile(r"\b(?:i|you)\s+can\s+see\b|\bin\s+view\b|\bvisible\b", re.I)
SURVEY_WHERE = {"upper": "upper", "top": "upper", "lower": "lower", "bottom": "lower", "base": "lower", "left": "left",
                "right": "right", "visible": "visible", "nearest": "nearest", "closest": "nearest"}
WIDTH = re.compile(r"\b(?:widths?|wide)\b", re.I)
HEIGHT = re.compile(r"\b(?:heights?|tall|high)\b", re.I)
SLOPE_WORDS = r"(?:slope[sd]?|sloping|drain(?:s|ing|age)?|fall|pitch(?:ed)?)"
SLOPE = re.compile(rf"\b(gutter|sill|ledge)s?\b.*\b{SLOPE_WORDS}\b|\b{SLOPE_WORDS}\b.*\b(gutter|sill|ledge)s?\b", re.I)
SHOPPING = re.compile(r"\b(?:find|get|need|buy|order|search|shop)\b", re.I)
STOP = re.compile(r"^\s*(?:ok(?:ay)?[,\s]+)?(?:stop|cancel|abort)(?:\s+(?:it|that|now|the\s+survey|surveying|measuring))?\s*[.!]*\s*$", re.I)
SURVEY_Q = re.compile(r"\b(?:which|what)\b.*\b(?:widest|narrowest|tallest|shortest|largest|biggest|smallest)\b", re.I)
MAX_PENDING = 16  # survey/check_slope requests awaiting the headset's /agent/observe report
# B3: purchase limits spoken with any command: "under $40", "no more than 40 dollars", "arriving by Friday". A price needs
# "$" or dollars/bucks ("under 40 mm" is a size) and caps the order total, so "under $5 each" is left alone.
LIMIT_PRICE = re.compile(r"\b(?:under|below|less\s+than|no\s+more\s+than|not\s+more\s+than|up\s+to|at\s+most"
                         r"|max(?:imum)?(?:\s+of)?|budget(?:\s+of)?|cap(?:\s+it)?\s+at)\s+"
                         r"(?:\$\s?(\d+(?:\.\d{1,2})?)|(\d+(?:\.\d{1,2})?)\s*(?:dollars?|bucks|usd)\b)"
                         r"(?![\d.]|\s*(?:each|apiece|per\b|a\s+piece))", re.I)
LIMIT_DATE = re.compile(r"\b(?:arriv\w*|deliver\w*|(?:get\s+(?:it|them)\s+)?here)\s+(by|before|no\s+later\s+than)\s+"
                        r"(?:this\s+|next\s+)?(monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow"
                        r"|today|\d{4}-\d{2}-\d{2})\b", re.I)
LIMIT_FILLER = frozenset((  # words that leave nothing to do once the limits are cut out ("keep it under $40")
    "keep it that this the total budget limit limits spend spending and please price cost set a an of only get here make sure "
    "must should we i want to be is with for but so ok okay let lets let's us me my our needs need can you quartermaster all "
    "in has have got gotta will would it's its actually just now then also oh well fine").split())
SESSIONS = {}


def start_job(query, measurement=None):
    key = " ".join(sorted(re.findall(r"[a-z0-9-]+", query.lower())))
    job_id = uuid.uuid4().hex[:12]
    with State.lock:
        cached = key in State.cache
        ids = State.cache.get(key) or match(query)
        State.cache[key] = ids
        created = 0.0 if cached else time.time()
        State.jobs[job_id] = (created, ids, query, measurement)
        for pid in ids:  # the asset worker starts once the search is done
            State.asset_ready_at.setdefault(pid, created + State.delay + (0 if cached else State.asset_delay))
            State.fit_measurement[pid] = measurement  # the real server saves the part with this search's fit
    return job_id, cached


def get_session(session_id):
    s = SESSIONS.setdefault(session_id, {"candidate_ids": [], "selected": None, "measurement": None, "placed": []})
    for k, v in (("site", None), ("scale", 1.0), ("pending", OrderedDict()), ("survey", None), ("last_text", ""), ("notes", [])):
        s.setdefault(k, v)
    return s


def apply_context(s, context):
    """Headset-supplied state overrides the session's own memory of it (real: agent._apply_context)."""
    if context.get("measurement"): s["measurement"] = context["measurement"]
    if context.get("selected_part_id"): s["selected"] = context["selected_part_id"]
    if context.get("candidate_ids"): s["candidate_ids"] = context["candidate_ids"]
    if context.get("placed"): s["placed"] = context["placed"]
    if context.get("site"): s["site"] = str(context["site"])
    if context.get("scale"):
        try:
            scale = float(context["scale"])
        except (TypeError, ValueError):
            scale = 0.0
        s["scale"] = scale if scale > 0 else 1.0


def session_digest(s):
    if not s["site"]:
        return None
    try:
        return scene_digest(s["site"], s["scale"])
    except (KeyError, TypeError, ValueError, IndexError) as exc:  # a malformed structure file
        log(f"agent: no digest for {s['site']}: {exc}")
        return None


def new_request(s, prefix, entry):
    request_id = f"{prefix}-{uuid.uuid4().hex[:8]}"
    s["pending"][request_id] = entry
    while len(s["pending"]) > MAX_PENDING:
        s["pending"].popitem(last=False)
    return request_id


def reply(text, actions=(), job_id=None):
    return {"reply": text, "actions": list(actions), "job_id": job_id}


def survey_args(m, text):
    det, qualifier, thing = m.group(1).lower(), (m.group(2) or "").lower(), m.group(3).lower()
    label = re.sub(r"\s+", "_", thing).rstrip("s")
    where = SURVEY_WHERE.get(qualifier) or ("nearest" if det == "the" and not thing.endswith("s") else "all")
    if SURVEY_VISIBLE.search(text):
        where = "visible"
    wide, tall = bool(WIDTH.search(text)), bool(HEIGHT.search(text))
    return {"label": label, "where": where, "measure": "width" if wide and not tall else "height" if tall and not wide else "size"}


def survey_tool(s, args):
    """Queue the headset's own tape over every object of one label. The mock never measures: with a site it knows, it
    checks the label against the structure layer so "measure every window" in a kitchen gets a straight answer."""
    label = args.get("label") if args.get("label") in LABELS else "any"
    where = args.get("where") if args.get("where") in WHERES else "all"
    measure = args.get("measure") if args.get("measure") in MEASURES else "size"
    d = session_digest(s)
    if label == "door" and d is not None and not digest_count(d, "door") and digest_count(d, "cabinet_door"):
        label = "cabinet_door"  # "the doors" in a kitchen means its cabinet doors
    if d is not None and not d["structure"]:
        return reply("No structure layer for this scene; mark the corners yourself.")
    count = digest_count(d, label) if d is not None else None
    if count == 0:
        return reply(f"No {noun(label)} in this scene's structure layer.")
    request_id = new_request(s, "sv", {"kind": "survey", "label": label, "where": where, "measure": measure, "query": s["last_text"]})
    if where == "all":
        spoken = f"Surveying {count} {noun(label, count)}." if count else f"Surveying the {noun(label)}."
    elif where == "nearest":
        spoken = f"Measuring the nearest {noun(label, 1)}."
    elif where == "visible":
        spoken = f"Surveying the {noun(label)} in view."
    else:
        spoken = f"Surveying the {where} {noun(label)}."
    return reply(spoken, [{"name": "survey", "args": {"label": label, "where": where, "measure": measure, "request_id": request_id}}])


def slope_tool(s, target):
    """The headset tapes the edge end to end and reports the fall to /agent/observe. For a gutter, `edge_ids` suggests
    the structure layer's long, high, near-horizontal edges (best first); the app may pick its own."""
    d = session_digest(s)
    edge_ids = [e["id"] for e in d["gutter_edges"]] if d is not None and target == "gutter" else []
    request_id = new_request(s, "sl", {"kind": "slope", "target": target, "query": s["last_text"]})
    spoken = "Checking that edge's fall." if target == "nearest_edge" else f"Checking the {target}'s fall."
    return reply(spoken, [{"name": "check_slope", "args": {"target": target, "request_id": request_id, "edge_ids": edge_ids}}])


def show_survey_action(stored, focus):
    return {"name": "show_tape_survey", "args": {"request_id": stored.get("request_id"), "label": stored.get("label"),
                                            "groups": stored.get("groups", []), "unverified": stored.get("unverified", []),
                                            "skipped": stored.get("skipped", []), "focus": focus}}


def extract_limits(text):
    """(set_limits args, the text with those phrases cut out)."""
    limits = {}
    m = LIMIT_PRICE.search(text)
    if m:
        limits["max_total_usd"] = float(m.group(1) or m.group(2))
        text = f"{text[:m.start()]} {text[m.end():]}"
    m = LIMIT_DATE.search(text)
    if m:
        on = parse_deliver_by(m.group(2).lower(), today())
        if m.group(1).lower() == "before":
            on -= timedelta(days=1)
        limits["deliver_by"] = on.isoformat()
        text = f"{text[:m.start()]} {text[m.end():]}"
    rest = re.sub(r"\s*,(\s*,)+", ",", text)
    return limits, re.sub(r"\s+", " ", rest).strip(" ,.;")


def set_limits_tool(s, limits, session_id):
    """Voice narrows the purchase limits; a looser ask is refused, never applied. show_limits for the wrist chip."""
    try:
        intent, refused, _ = set_limits(session_id, "voice", s["last_text"], **limits)
    except ValueError as exc:
        return reply(f"Can't do that yet: {exc}.")
    spoken = intent_spoken(intent)
    if refused:
        text = "You'd need to raise that on the panel." + (f" Still {spoken}." if spoken else "")
    else:
        text = f"Limits set: {spoken}." if spoken else "Limits set."
    summary = intent_summary(intent)
    return reply(text, [{"name": "show_limits", "args": {"intent_id": intent["id"], "max_total_usd": summary["max_total_usd"],
                                                         "deliver_by": summary["deliver_by"], "seller_policy": summary["seller_policy"],
                                                         "refused": refused}}])


def agent_command(session_id, text, context):
    s = get_session(session_id)
    apply_context(s, context)
    text = (text or "")[:500]
    s["last_text"] = text
    limits, rest = extract_limits(text)
    if limits:  # "Find hangers, under $40, arriving by Friday": set the limits, then route what's left as usual
        head = set_limits_tool(s, limits, session_id)
        if not any(w not in LIMIT_FILLER for w in re.findall(r"[a-z0-9$']+", rest.lower())):
            return head
        tail = agent_command(session_id, rest, context)
        return {**tail, "reply": f"{head['reply']} {tail['reply']}".strip(), "actions": head["actions"] + tail["actions"]}
    m = EQUIP.search(text)
    if m:
        tool = m.group(1).lower()
        return {"reply": f"{tool.capitalize()} in hand.", "actions": [{"name": "equip_tool", "args": {"tool": tool}}], "job_id": None}
    if STOP.search(text):
        return reply("Stopping.", [{"name": "stop_survey", "args": {}}])
    m = SURVEY.search(text)
    if m:
        return survey_tool(s, survey_args(m, text))
    m = SLOPE.search(text)
    if m and not SHOPPING.search(text):
        return slope_tool(s, (m.group(1) or m.group(2)).lower())
    m = SELECT.search(text)
    if m:
        i = int(m.group(1)) - 1 if m.group(1) else {"first": 0, "second": 1, "third": 2}[m.group(2).lower()]
        if 0 <= i < len(s["candidate_ids"]): s["selected"] = s["candidate_ids"][i]
        return {"reply": "Locked that one in.", "actions": [{"name": "select_candidate", "args": {"index": i}}], "job_id": None}
    m = SORT.search(text)
    if m:
        sort = m.group(1).lower()
        if not s["selected"]:
            return {"reply": "Can't do that yet: no part selected.", "actions": [], "job_id": None}
        return {"reply": f"Pulling up sellers, {sort} first.", "actions": [{"name": "show_sellers", "args": {"part_id": s["selected"], "sort": sort}}], "job_id": None}
    m = ARRAY.search(text)
    if m:
        unit = m.group(2).lower()
        mm = float(m.group(1)) * (10.0 if unit.startswith("c") else 25.4 if unit.startswith("in") else 1.0)
        return {"reply": f"Array set every {m.group(1)}{m.group(2)}.", "actions": [{"name": "place_array", "args": {"spacing_mm": mm}}], "job_id": None}
    if WHAT_ELSE.search(text):
        placed = s["placed"] or ([{"part_id": s["selected"], "count": 1}] if s["selected"] else [])
        if not placed:
            return {"reply": "Can't do that yet: nothing placed yet.", "actions": [], "job_id": None}
        bom = make_bom([p["part_id"] for p in placed], {p["part_id"]: p.get("count", 1) for p in placed})
        return {"reply": "Also grab " + ", ".join(l["name"].lower() for l in bom["lines"]) + ".",
                "actions": [{"name": "show_bom", "args": {"bom_id": bom["id"], "lines": bom["lines"], "total_usd": bom["total_usd"]}}], "job_id": None}
    if s["survey"] and SURVEY_Q.search(text):  # follow-ups on the stored survey: "which one is the tallest?"
        answer = answer_query(text, s["survey"])
        if answer is None:
            return reply("Checked the survey.")
        return reply(answer[0], [show_survey_action(s["survey"], answer[1])])
    m = FIND.search(text)
    if m:
        job_id, _ = start_job(m.group(1), s["measurement"])
        return {"reply": "Searching the supply shops.", "actions": [{"name": "search_started", "args": {"job_id": job_id}}], "job_id": job_id}
    return {"reply": "Offline: try a cached part or the crate menu.", "actions": [], "job_id": None}


def observe(obs):
    """POST /agent/observe: the headset reports what its tools measured for a survey / check_slope action. Templates
    only (like the real server): size groups + spoken summary, or the gutter drainage verdict."""
    s = get_session(obs["session_id"])
    pending = (s["pending"].pop(obs["request_id"], None) if obs["request_id"] else None) or {}
    actions = []
    if obs["kind"] == "survey_result":
        first_label = obs["results"][0]["label"] if obs["results"] else None
        label = obs["label"] or pending.get("label") or first_label or "any"
        measure = obs["measure"] or pending.get("measure") or "size"
        summary = survey_summary(obs, label, measure)
        text = summary["reply"]
        if obs["results"]:
            s["survey"] = {"request_id": obs["request_id"], "label": label, "measure": measure,
                           "query": obs["query"] or pending.get("query"), "items": obs["results"],
                           "groups": summary["groups"], "unverified": summary["unverified"], "skipped": summary["skipped"]}
            actions.append(show_survey_action(s["survey"], []))
    else:
        residual = obs["gravity_residual_deg"]
        if residual is None:
            d = session_digest(s)
            residual = d["gravity_residual_deg"] if d is not None else None
        unc = obs["uncertainty_mm"]
        if unc is None:
            unc = slope_uncertainty_mm(obs["run_m"], residual)
        v = slope_verdict(obs["run_m"], obs["fall_mm"], unc)
        target = obs["target"] or pending.get("target") or "gutter"
        text = slope_reply(v)
        note = (f"{target.replace('_', ' ').capitalize()} slope: {v['verdict']} ({v['fall_mm']:g} ± {v['uncertainty_mm']:g} mm "
                f"of fall over {v['run_m']:.2f} m; needs {v['required_mm']:g} mm)")
        actions.append({"name": "add_note", "args": {"text": note}})
        s["notes"].append(note)
    return reply(text, actions)


def next_transcript():
    """What the next voice recording "says": --voice-transcript split on "|", in turn (one phrase = always that)."""
    with State.lock:
        phrases = [p.strip() for p in (State.voice_transcript or "").split("|") if p.strip()]
        if State.voice_transcript is None or not phrases:
            return None
        text = phrases[State.voice_index % len(phrases)]
        State.voice_index += 1
        return text


def tone_wav(seconds=0.4, rate=16000, freq=660.0):
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        n = int(seconds * rate)
        w.writeframes(b"".join(int(8000 * math.sin(2 * math.pi * freq * i / rate)).to_bytes(2, "little", signed=True) for i in range(n)))
    return buf.getvalue()


def parse_multipart(ctype, body):
    msg = email.parser.BytesParser(policy=email.policy.default).parsebytes(
        b"Content-Type: " + ctype.encode() + b"\r\nMIME-Version: 1.0\r\n\r\n" + body)
    fields = {}
    for part in msg.iter_parts():
        name = part.get_param("name", header="content-disposition")
        fields[name] = (part.get_filename(), part.get_payload(decode=True))
    return fields


# ---------------------------------------------------------------- measured mandate (real: server/mandate.py, B3)
# Limits (voice can only tighten; the panel may loosen or clear), a priced cart with a sha256 hash, the checks the panel
# shows, and a single-use 120 s hold nonce that only a >= 1 s hold may redeem. In memory: a restart forgets them all.

INTENT_TTL = timedelta(hours=24)
NONCE_TTL = timedelta(seconds=120)
MIN_HOLD_MS = 1000
MAX_NONCES = 256
MAX_UNITS = 5000
WEEKDAYS = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]
POLICIES = ("cheapest", "fastest", "best")
LIMIT_FIELDS = ("max_total_usd", "deliver_by", "seller_policy")
REFUSED_WHY = "voice can only tighten a limit; change it on the panel"
CART_HASHED = ("session_id", "intent_id", "lines", "bom_id", "bom_lines", "shipping_usd", "total_usd", "evidence")
PART_ID_RE = re.compile(r"^[a-z0-9-]+$")


class MandateError(Exception):
    """400 missing/short hold proof, 409 stale/reused/mismatched proof, 422 a check failed (detail is an object)."""

    def __init__(self, code, detail):
        super().__init__(detail)
        self.code, self.detail = code, detail


def money(usd):
    return f"${usd:.0f}" if float(usd).is_integer() else f"${usd:.2f}"


def day(d):
    return f"{d.strftime('%a %b')} {d.day}"  # "Fri Oct 2"


def iso(v):
    return v.isoformat() if isinstance(v, date) else v


def iso_z(t):
    return t.isoformat().replace("+00:00", "Z")  # pydantic's UTC datetime: "2026-09-26T08:31:26.179791Z"


def parse_deliver_by(value, on=None):
    """ISO date, "today", "tomorrow" or a weekday (the next one, today included)."""
    if value is None or value == "":
        return None
    on = on or today()
    word = str(value).strip().lower()
    if word == "today":
        return on
    if word == "tomorrow":
        return on + timedelta(days=1)
    if word in WEEKDAYS:
        return on + timedelta(days=(WEEKDAYS.index(word) - on.weekday()) % 7)
    try:
        return date.fromisoformat(word[:10])
    except ValueError:
        raise ValueError(f"deliver_by must be an ISO date or a weekday, got {value!r}") from None


def current_intent(session_id, at=None):
    intent = State.intents.get(session_id)
    return intent if intent is not None and intent["expires_at"] > (at or now()) else None


def intent_summary(intent):
    return {"id": intent["id"], "max_total_usd": intent["max_total_usd"], "deliver_by": iso(intent["deliver_by"]),
            "seller_policy": intent["seller_policy"], "source": intent["source"], "text": intent["text"]}


def intent_spoken(intent):
    """"under $40, by Fri Oct 2, fastest" (empty when nothing is limited)."""
    bits = []
    if intent["max_total_usd"] is not None:
        bits.append(f"under {money(intent['max_total_usd'])}")
    if intent["deliver_by"] is not None:
        bits.append(f"by {day(intent['deliver_by'])}")
    if intent["seller_policy"]:
        bits.append(intent["seller_policy"])
    return ", ".join(bits)


def set_limits(session_id, source, text="", **limits):
    """Apply the limits given (only keys present change anything) -> (intent, refused, changed). Voice may lower the
    cap, move the deadline earlier and pick a policy; raising or clearing is refused, not applied. The panel may set
    anything (None clears). Nothing is stored unless something changed."""
    at = now()
    existing = current_intent(session_id, at)
    intent = dict(existing) if existing else {
        "id": f"int-{uuid.uuid4().hex[:8]}", "session_id": session_id, "max_total_usd": None, "deliver_by": None,
        "seller_policy": None, "source": source, "text": "", "created_at": at, "expires_at": at + INTENT_TTL}
    refused, changed = [], []

    def apply(field, asked, looser):
        kept = intent[field]
        if asked == kept:
            return
        if source == "voice" and looser:
            if asked is not None:  # a voice "no limit" is simply ignored
                refused.append({"field": field, "asked": iso(asked), "kept": iso(kept), "why": REFUSED_WHY})
            return
        intent[field] = asked
        changed.append(field)

    if "max_total_usd" in limits:
        asked = limits["max_total_usd"]
        if asked is not None:
            asked = round(float(asked), 2)
            if not asked > 0:
                raise ValueError("max_total_usd must be positive")
        kept = intent["max_total_usd"]
        apply("max_total_usd", asked, asked is None or (kept is not None and asked > kept))
    if "deliver_by" in limits:
        asked = parse_deliver_by(limits["deliver_by"])
        kept = intent["deliver_by"]
        apply("deliver_by", asked, asked is None or (kept is not None and asked > kept))
    if "seller_policy" in limits:
        asked = limits["seller_policy"]
        if asked is not None and asked not in POLICIES:
            raise ValueError(f"seller_policy must be cheapest, fastest or best, got {asked!r}")
        apply("seller_policy", asked, asked is None)
    if changed:
        intent.update(source=source, text=text[:500], expires_at=at + INTENT_TTL)
        State.intents[session_id] = intent
    return intent, refused, changed


def saved_part(part_id):
    """The part as the real server has it saved: fixture + contract fields, fit from the last search that found it."""
    raw = raw_part(part_id)
    return enrich(raw, State.fit_measurement.get(part_id)) if raw is not None else None


def cart_hash(cart):
    canonical = json.dumps({k: cart[k] for k in CART_HASHED}, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return "sha256:" + hashlib.sha256(canonical.encode()).hexdigest()


def build_cart(session_id, part, seller_idx, units_needed, bom_id=None, bom_lines=(), evidence=(), intent_id=None, at=None):
    """Price the cart from the saved listing like the real one (price_usd x packs + shipping, plus BOM lines)."""
    if not 1 <= units_needed <= MAX_UNITS:
        raise ValueError(f"units_needed must be between 1 and {MAX_UNITS}, got {units_needed}")
    sellers = part.get("sellers") or []
    if not 0 <= seller_idx < len(sellers):
        raise ValueError(f"seller_idx {seller_idx} out of range for part {part['id']!r}")
    seller = sellers[seller_idx]
    pack_qty = max(1, int(seller.get("pack_qty") or 1))
    packs = math.ceil(units_needed / pack_qty)
    if not 1 <= packs <= 500:
        raise ValueError(f"qty must be between 1 and 500, got {packs}")
    if seller.get("price_usd") is None:
        raise ValueError(f"seller {seller['name']!r} on part {part['id']!r} has no price")
    shipping = seller.get("shipping_usd") or 0.0
    line_total = round(seller["price_usd"] * packs + shipping, 2)
    bom_lines = list(bom_lines)
    total = round(line_total + round(sum(b["total_usd"] for b in bom_lines), 2), 2)
    cart = {"id": f"cart-{uuid.uuid4().hex[:8]}", "session_id": session_id, "intent_id": intent_id,
            "lines": [{"part_id": part["id"], "seller_idx": seller_idx, "seller": seller["name"], "units_needed": units_needed,
                       "pack_qty": pack_qty, "packs": packs, "unit_price_usd": seller["price_usd"], "shipping_usd": shipping,
                       "line_total_usd": line_total}],
            "bom_id": bom_id if bom_lines else None, "bom_lines": bom_lines, "shipping_usd": shipping, "total_usd": total,
            "evidence": list(evidence), "created_at": iso_z(at or now()), "receipt_id": None}
    cart["hash"] = cart_hash(cart)
    return cart


def check(check_id, status, detail):
    return {"id": check_id, "status": status, "ok": status != "fail", "detail": detail}


def mandate_checks(cart, intent, part, on=None):
    """The rows the panel shows before the hold: ok / warn (shown, not blocking) / fail (blocks payment)."""
    on = on or today()
    line = cart["lines"][0]
    seller = part["sellers"][line["seller_idx"]]
    out = []
    ship = f" + {money(line['shipping_usd'])} shipping" if line["shipping_usd"] else ""
    out.append(check("price_reread", "ok", f"${line['unit_price_usd']:.2f} × {line['packs']} from the saved listing{ship}"))

    cap = intent["max_total_usd"] if intent else None
    if cap is None:
        out.append(check("within_limit", "ok", "no budget limit set"))
    elif cart["total_usd"] <= cap:
        out.append(check("within_limit", "ok", f"${cart['total_usd']:.2f} ≤ {money(cap)}"))
    else:
        out.append(check("within_limit", "fail", f"${cart['total_usd']:.2f} is over your {money(cap)} limit"))

    deadline = intent["deliver_by"] if intent else None
    arrival = on + timedelta(days=seller["eta_days"]) if seller.get("eta_days") is not None else None
    if deadline is None:
        eta = f"arrives {day(arrival)}" if arrival else (seller.get("eta") or "no delivery estimate")
        out.append(check("delivery", "ok", f"{eta}; no deadline set"))
    elif arrival is None:
        out.append(check("delivery", "warn", f"{seller['name']} gave no delivery estimate; you asked for {day(deadline)}"))
    elif arrival <= deadline:
        out.append(check("delivery", "ok", f"arrives {day(arrival)}, by {day(deadline)}"))
    else:
        out.append(check("delivery", "fail", f"arrives {day(arrival)}, after your {day(deadline)} deadline"))

    measured = next((e for e in cart["evidence"] if e["array"] and e["array"]["count"]), None)
    if measured is not None:
        spacing, count = measured["array"]["spacing_mm"], measured["array"]["count"]
        how = f"{measured['value_m']:.2f} m ÷ {spacing:g} mm → {count}" if measured["value_m"] is not None and spacing else f"{count}"
        detail = f"{measured['label'] or 'tape'} {how} (reported by headset)"
        out.append(check("qty_evidence", "ok", detail) if count == line["units_needed"]
                   else check("qty_evidence", "warn", f"{detail}, cart has {line['units_needed']}"))
    elif cart["evidence"]:
        e = cart["evidence"][0]
        value = f" {e['value_m']:.2f} m" if e["value_m"] is not None else ""
        out.append(check("qty_evidence", "ok", f"{e['label'] or 'tape'}{value} (reported by headset)"))
    else:
        out.append(check("qty_evidence", "warn", "no measurement evidence for the quantity"))

    if seller.get("verified"):
        out.append(check("seller_verified", "ok", f"{seller['name']}: structured seller listing"))
    else:
        out.append(check("seller_verified", "warn", f"{seller['name']}: listing read by the model, not a seller API"))

    fit = part.get("fit") or {"status": "unknown", "spare_mm": None}
    if fit["status"] == "fits":
        out.append(check("fit", "ok", "green" + (f", {fit['spare_mm']:g} mm spare" if fit.get("spare_mm") is not None else "")))
    elif fit["status"] == "unknown":
        out.append(check("fit", "warn", "fit not checked against a measurement"))
    else:
        by = f" by {abs(fit['spare_mm']):g} mm" if fit.get("spare_mm") is not None else ""
        out.append(check("fit", "warn", f"{fit['status'].replace('_', ' ')}{by}"))

    out.append(check("card", "ok", "Visa test card •••• 1111, held by the server"))
    return out


def issue_nonce(cart, at=None):
    """A fresh single-use nonce for this cart; the session's older nonces stop working."""
    at = at or now()
    for key in [k for k, v in State.nonces.items() if v["expires_at"] <= at or v["session_id"] == cart["session_id"]]:
        del State.nonces[key]
    while len(State.nonces) >= MAX_NONCES:
        del State.nonces[next(iter(State.nonces))]
    nonce = "hn_" + base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode()
    expires_at = at + NONCE_TTL
    State.nonces[nonce] = {"cart_id": cart["id"], "cart_hash": cart["hash"], "session_id": cart["session_id"], "expires_at": expires_at}
    return nonce, expires_at


def prepare(session_id, part, seller_idx, units_needed, bom_id=None, bom_lines=(), evidence=()):
    """POST /checkout/prepare: price the cart, run the checks, issue the hold nonce. Never pays."""
    at = now()
    intent = current_intent(session_id, at)
    cart = build_cart(session_id, part, seller_idx, units_needed, bom_id, bom_lines, evidence, intent["id"] if intent else None, at)
    results = mandate_checks(cart, intent, part)
    with State.lock:
        State.carts[cart["id"]] = cart
        nonce, expires_at = issue_nonce(cart, at)
    return {"cart": {k: v for k, v in cart.items() if k not in ("hash", "session_id", "receipt_id")},
            "cart_hash": cart["hash"], "hold_nonce": nonce, "nonce_expires_at": expires_at.isoformat(),
            "intent": intent_summary(intent) if intent else None, "checks": results, "all_ok": all(c["ok"] for c in results)}


def verify_hold(session_id, part, seller_idx, qty, bom_id, bom_lines, cart_hash_, hold_nonce, hold_ms):
    """POST /checkout with the hold proof: the nonce is fresh, unused, this session's and this cart's; the request still
    describes that cart; the price hasn't moved; every check still passes against the current limits."""
    if not (session_id and cart_hash_ and hold_nonce and hold_ms is not None):
        raise MandateError(400, "hold proof required: session_id, cart_hash, hold_nonce and hold_ms")
    if hold_ms < MIN_HOLD_MS:
        raise MandateError(400, f"hold_ms {hold_ms} < {MIN_HOLD_MS}: paying needs the full hold")  # the nonce is kept
    at = now()
    with State.lock:
        entry = State.nonces.pop(hold_nonce, None)  # single use: gone after the first attempt, good or bad
    if entry is None:
        raise MandateError(409, "hold nonce unknown or already used; reopen checkout")
    if entry["expires_at"] <= at:
        raise MandateError(409, "hold nonce expired (2 min); reopen checkout")
    if entry["session_id"] != session_id:
        raise MandateError(409, "hold nonce belongs to another session")
    if entry["cart_hash"] != cart_hash_:
        raise MandateError(409, "cart_hash doesn't match the prepared cart; reopen checkout")
    cart = State.carts.get(entry["cart_id"])
    if cart is None:
        raise MandateError(409, "prepared cart not found; reopen checkout")
    line = cart["lines"][0]
    asked = (part["id"], seller_idx, qty, bom_id if bom_lines else None, sorted(b["idx"] for b in bom_lines))
    prepared = (line["part_id"], line["seller_idx"], line["packs"], cart["bom_id"], sorted(b["idx"] for b in cart["bom_lines"]))
    if asked != prepared:
        raise MandateError(409, f"cart changed since prepare ({line['packs']} × {line['part_id']} from seller "
                                f"{line['seller_idx']} was prepared); reopen checkout")
    rebuilt = build_cart(session_id, part, seller_idx, line["units_needed"], cart["bom_id"], bom_lines, cart["evidence"], cart["intent_id"])
    if rebuilt["hash"] != cart["hash"]:
        raise MandateError(409, "the price or listing changed since prepare; reopen checkout")
    intent = current_intent(session_id, at)
    results = mandate_checks(cart, intent, part)
    if not all(c["ok"] for c in results):
        raise MandateError(422, {"error": "a mandate check failed", "checks": results})
    return {"session_id": session_id, "intent": intent_summary(intent) if intent else None, "cart": cart, "checks": results, "hold_ms": hold_ms}


def mandate_receipt(signed, mode, status, approval_code):
    """receipt.mandate: intent -> cart (hash) -> authorization (with hold_ms), the evidence and the checks."""
    cart = signed["cart"]
    line = cart["lines"][0]
    return {"intent": signed["intent"],
            "cart": {"id": cart["id"], "hash": cart["hash"], "total_usd": cart["total_usd"], "units_needed": line["units_needed"], "packs": line["packs"]},
            "authorization": {"mode": mode, "status": status, "approval_code": approval_code, "hold_ms": signed["hold_ms"]},
            "evidence": cart["evidence"], "checks": signed["checks"]}


def parse_evidence(f):
    """PrepareRequest.evidence like the real Evidence model: known fields only, missing ones null, at most 20."""
    out = []
    for i, item in enumerate(f.array("evidence", 20)):
        e = f.sub("evidence", i, item)
        nb = e.body.get("notebook_id")
        if nb is not None and (isinstance(nb, bool) or not isinstance(nb, (int, str))):
            f.errors.append(verr(["evidence", i, "notebook_id"], "Input should be a valid integer or string", "union_type", nb))
        arr = e.body.get("array")
        if arr is not None:
            a = e.sub("array", None, arr)
            arr = {"spacing_mm": a.number("spacing_mm"), "count": a.integer("count")}
        out.append({"notebook_id": nb, "label": e.string("label", default="", max_len=80, nullable=False) or "",
                    "value_m": e.number("value_m"), "camera_id": e.integer("camera_id"),
                    "photo": e.string("photo", max_len=300), "array": arr if isinstance(arr, dict) else None})
    return out


def bom_receipts(bom_id, idxs):
    """The chosen priced lines of a saved BOM (never client-supplied prices) -> (lines, (code, detail) | None)."""
    if bom_id is None:
        return [], None
    if not isinstance(bom_id, str) or not PART_ID_RE.match(bom_id):
        return [], (400, "invalid bom id")
    if not isinstance(idxs, list):
        return [], (422, "bom_lines must be a list of BomLine.idx values")
    bom = State.boms.get(bom_id)
    if bom is None:
        return [], (404, "unknown bom")
    by = {l["idx"]: l for l in bom["lines"]}
    lines = []
    for i in idxs:
        line = by.get(i)
        if line is None or line.get("seller") is None:
            return [], (400, f"unknown bom line {i}")
        lines.append({"idx": i, "name": line["name"], "qty": line["qty"], "seller": line["seller"]["name"],
                      "price_usd": line["seller"]["price_usd"], "total_usd": round(line["seller"]["price_usd"] * line["qty"], 2)})
    return lines, None


# ---------------------------------------------------------------- HTTP

class Handler(BaseHTTPRequestHandler):
    server_version = "AirToolsMockParts/2.0"

    def log_message(self, fmt, *args):  # replaced by log()
        pass

    def send_body(self, code, body, ctype, headers=None, note=""):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)
        log(f"{self.command} {self.path} -> {code}{note}")

    def send_json(self, code, obj, headers=None):
        self.send_body(code, json.dumps(obj).encode(), "application/json", headers)

    def detail(self, code, text):
        self.send_json(code, {"detail": text})

    def read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def read_json(self):
        raw = self.read_body()
        try:
            return json.loads(raw or b"{}")
        except json.JSONDecodeError:
            return None

    # ---- GET
    def do_GET(self):
        url = urlparse(self.path)
        path = url.path
        if path == "/health":
            return self.send_json(200, {"ok": True, "version": "mock", "parts": all_part_ids()})
        if path == "/scenes":
            return self.send_json(200, self.list_scenes())
        m = re.fullmatch(r"/scenes/([a-z0-9-]+)/(.+)", path)
        if m:
            return self.scene_file(m.group(1), m.group(2))
        m = re.fullmatch(r"/parts/jobs/([A-Za-z0-9-]+)", path)
        if m:
            with State.lock:
                job = State.jobs.get(m.group(1))
            if job is None:
                return self.detail(404, "unknown job")
            created, ids, query, measurement = job
            if time.time() - created < State.delay:
                return self.send_json(200, {"id": m.group(1), "status": "running", "stage": "searching the supply shops",
                                            "query": query, "candidates": [], "error": None})
            parts = [enrich(raw_part(i), measurement) for i in ids]
            return self.send_json(200, {"id": m.group(1), "status": "done", "stage": "done", "query": query,
                                        "candidates": parts, "error": None, "summary": summary_line(parts)})
        m = re.fullmatch(r"/parts/([^/]+)/(part\.json|model\.glb|image\.jpg)", path)
        if m:
            pid, name = m.groups()
            if not re.fullmatch(r"[a-z0-9-]+", pid):
                return self.detail(400, "invalid part id")
            part = raw_part(pid)
            if part is None:
                return self.detail(404, f"{name} not found for part {pid!r}")
            if name == "part.json":
                return self.send_json(200, enrich(part))
            f = os.path.join(PARTS_DIR, pid, name)
            if not os.path.isfile(f):
                return self.detail(404, f"{name} not found for part {pid!r}")
            with open(f, "rb") as fh:
                data = fh.read()
            return self.send_body(200, data, CONTENT_TYPES[os.path.splitext(f)[1]], note=f" ({len(data)} bytes)")
        m = re.fullmatch(r"/notebook/([^/]+)", path)
        if m:
            return self.send_json(200, {"session_id": m.group(1), "entries": State.notebooks.get(m.group(1), [])})
        return self.detail(404, "Not Found")

    def list_scenes(self):
        out = []
        base = State.scene_dir
        if not os.path.isdir(base):
            return out
        for site in sorted(os.listdir(base)):
            sj = os.path.join(base, site, "scene.json")
            if not os.path.isfile(sj):
                continue
            try:
                with open(sj) as fh:
                    meta = json.load(fh)
            except (json.JSONDecodeError, OSError):
                continue
            out.append({"site": site, "revision": meta.get("revision", 1), "quality": meta.get("quality", "full"),
                        "updated_at": time.strftime("%Y-%m-%dT%H:%M:%S+00:00", time.gmtime(os.path.getmtime(sj)))})
        return out

    def scene_file(self, site, file):
        parts = file.split("/")
        if any(p in ("", ".", "..") or not re.fullmatch(r"[A-Za-z0-9._-]+", p) for p in parts):
            return self.detail(404, "not found")
        base = os.path.realpath(os.path.join(State.scene_dir, site))
        f = os.path.realpath(os.path.join(base, *parts))
        if not f.startswith(base + os.sep) or not os.path.isfile(f):
            return self.detail(404, "not found")
        with open(f, "rb") as fh:
            data = fh.read()
        ctype = CONTENT_TYPES.get(os.path.splitext(f)[1].lower(), "application/octet-stream")
        if os.path.basename(f) == "scene.json":
            etag = '"' + hashlib.sha1(data).hexdigest() + '"'
            hdr = {"Cache-Control": "no-cache", "ETag": etag}
            if etag in (self.headers.get("If-None-Match") or ""):
                self.send_response(304)
                for k, v in hdr.items():
                    self.send_header(k, v)
                self.end_headers()
                return log(f"GET {self.path} -> 304")
            return self.send_body(200, data, ctype, hdr)
        hdr = {"Cache-Control": "public, max-age=31536000, immutable"} if re.search(r"\.r\d+\.", f) else {}
        return self.send_body(200, data, ctype, hdr, note=f" ({len(data)} bytes)")

    # ---- POST
    def do_POST(self):
        url = urlparse(self.path)
        path = url.path
        if path == "/voice/command":
            return self.voice_command()
        body = self.read_json()
        if body is None:
            return self.detail(400, "invalid json")
        if path == "/parts/search":
            query = str(body.get("query", ""))
            if not query:
                return self.detail(422, "query is required")
            meas = body.get("measurement")
            if meas is not None and not isinstance(meas, dict):
                return self.detail(422, "measurement must be an object {label, value_m, axis}")
            job_id, cached = start_job(query, meas)
            if cached:
                parts = [enrich(raw_part(i), meas) for i in State.jobs[job_id][1]]
                return self.send_json(200, {"job_id": job_id, "status": "done", "stage": "done", "candidates": parts})
            return self.send_json(200, {"job_id": job_id, "status": "running", "stage": "searching the supply shops"})
        m = re.fullmatch(r"/parts/([^/]+)/sellers", path)
        if m:
            part = raw_part(m.group(1))
            if part is None:
                return self.detail(404, "unknown part")
            sort = (parse_qs(url.query).get("sort") or ["cheapest"])[0]
            if sort not in ("cheapest", "fastest", "best"):
                return self.detail(422, "sort must be cheapest|fastest|best")
            return self.send_json(200, rerank(enrich(part), sort))
        if path == "/parts/bom":
            ids = body.get("part_ids") or []
            for pid in ids:
                if raw_part(pid) is None:
                    return self.detail(404, f"unknown part {pid!r}")
            return self.send_json(200, make_bom(ids, body.get("counts") or {}))
        if path == "/checkout":
            return self.checkout(body)
        if path == "/checkout/prepare":
            return self.checkout_prepare(body)
        if path == "/commerce/limits":
            return self.commerce_limits(body)
        if path == "/voice/token":
            return self.detail(503, "GROK_API_KEY not configured (realtime voice is not active)")
        if path == "/mock/voice-transcript":  # mock only: replace what the next recordings "say" ("a|b|c"), from the top
            text = body.get("text")
            if not isinstance(text, str) or not text.strip():
                return self.detail(422, "text is required")
            with State.lock:
                State.voice_transcript, State.voice_index = text, 0
            return self.send_json(200, {"transcripts": [p.strip() for p in text.split("|") if p.strip()]})
        if path == "/voice/speak":
            return self.send_body(200, tone_wav(), "audio/wav")
        if path == "/notebook":
            if "session_id" not in body or not isinstance(body.get("entries"), list):
                return self.detail(422, "session_id and entries[] are required")
            with State.lock:
                State.notebooks.setdefault(body["session_id"], []).extend(body["entries"])
                entries = State.notebooks[body["session_id"]]
            os.makedirs(DATA_DIR, exist_ok=True)
            with open(os.path.join(DATA_DIR, f"notebook-{re.sub(r'[^A-Za-z0-9_-]', '_', body['session_id'])}.json"), "w") as f:
                json.dump(entries, f, indent=2)
            return self.send_json(200, {"session_id": body["session_id"], "entries": entries})
        if path == "/agent/command":
            if "session_id" not in body:
                return self.detail(422, "session_id is required")
            return self.send_json(200, agent_command(body["session_id"], body.get("text", ""), body.get("context") or {}))
        if path == "/agent/observe":
            obs, errors = parse_observation(body)
            if errors:
                return self.send_json(422, {"detail": errors})
            result = observe(obs)
            if obs["tts"]:  # the spoken reply as audio, like /voice/command (the mock's TTS is a tone)
                result.update({"audio_b64": base64.b64encode(tone_wav()).decode(), "audio_mime": "audio/wav"})
            return self.send_json(200, result)
        if path == "/scene/ask":
            frames = body.get("frames") or []
            if not frames:
                return self.detail(400, "no frames")
            for fr in frames[:3]:
                try:
                    base64.b64decode(fr.get("jpg_b64", ""), validate=True)
                except Exception:
                    return self.detail(400, "invalid base64")
            return self.send_json(200, {"answer": "Mock answer: the middle of that photo.", "frame_id": frames[0].get("id"),
                                        "box": [0.45, 0.45, 0.55, 0.55], "part_query": "hidden K-style gutter hanger"})
        return self.detail(404, "Not Found")

    def checkout(self, body):
        part = saved_part(str(body.get("part_id", "")))
        if part is None:
            return self.detail(404, "unknown part")
        try:
            idx, qty = int(body.get("seller_idx", -1)), int(body.get("qty", 0))
        except (TypeError, ValueError):
            return self.detail(422, "seller_idx and qty must be integers")
        bom_lines, err = bom_receipts(body.get("bom_id") or None, body.get("bom_lines") or [])
        if err:
            return self.detail(*err)
        # The hold proof (B3): verified whenever any of it is sent (or always with --require-hold-proof).
        signed = None
        if any(body.get(k) is not None for k in ("cart_hash", "hold_nonce", "hold_ms")) or State.require_hold_proof:
            f = Fields(body)
            proof = (f.string("cart_hash"), f.string("hold_nonce"), f.integer("hold_ms"), f.string("session_id"))
            if f.errors:
                return self.send_json(422, {"detail": f.errors})
            try:
                signed = verify_hold(proof[3], part, idx, qty, body.get("bom_id"), bom_lines, *proof[:3])
            except MandateError as e:
                return self.send_json(e.code, {"detail": e.detail})
            except ValueError as e:
                return self.detail(400, str(e))
        sellers = part.get("sellers") or []
        if not 1 <= qty <= 500:
            return self.detail(400, "qty must be 1-500")
        if not 0 <= idx < len(sellers):
            return self.detail(400, "bad seller_idx")
        seller = sellers[idx]
        if seller.get("price_usd") is None:
            return self.detail(400, "seller has no price")
        part_total = round(seller["price_usd"] * qty + (seller.get("shipping_usd") or 0), 2)
        total = round(part_total + sum(l["total_usd"] for l in bom_lines), 2)
        receipt = {
            "status": "AUTHORIZED" if State.sandbox else "OFFLINE_RECEIPT",
            "mode": "sandbox" if State.sandbox else "offline",
            "approval_code": f"{uuid.uuid4().int % 1000000:06d}" if State.sandbox else None,
            "card_last4": "1111", "total_usd": total, "currency": "USD",
            "receipt_id": f"rcpt_{uuid.uuid4().hex[:16]}", "part_id": part["id"], "seller": seller["name"], "qty": qty,
            "unit_price_usd": seller["price_usd"], "shipping_usd": seller.get("shipping_usd") or 0.0, "bom_lines": bom_lines,
            "created_at": time.strftime("%Y-%m-%dT%H:%M:%S+00:00", time.gmtime()),
            "label": SANDBOX_LABEL if State.sandbox else OFFLINE_LABEL,
        }
        if signed is not None:
            receipt["mandate"] = mandate_receipt(signed, receipt["mode"], receipt["status"], receipt["approval_code"])
            signed["cart"]["receipt_id"] = receipt["receipt_id"]
        sid = body.get("session_id") or "default"
        with State.lock:
            State.notebooks.setdefault(sid, []).append({"type": "order", **receipt})
        return self.send_json(200, receipt)

    def checkout_prepare(self, body):
        f = Fields(body)
        sid = f.string("session_id", required=True, max_len=128, min_len=1, nullable=False)
        pid = f.string("part_id", required=True, nullable=False)
        idx, units = f.integer("seller_idx", required=True), f.integer("units_needed", required=True)
        bom_id = f.string("bom_id")
        idxs = f.array("bom_lines")
        for i, v in enumerate(idxs):
            if as_int(v) is None:
                f.errors.append(verr(["bom_lines", i], "Input should be a valid integer", "int_type", v))
        evidence = parse_evidence(f)
        if f.errors:
            return self.send_json(422, {"detail": f.errors})
        if not PART_ID_RE.match(pid):
            return self.detail(400, "invalid part id")
        part = saved_part(pid)
        if part is None:
            return self.detail(404, "unknown part")
        bom_lines, err = bom_receipts(bom_id, [as_int(v) for v in idxs])
        if err:
            return self.detail(*err)
        try:
            return self.send_json(200, prepare(sid, part, idx, units, bom_id, bom_lines, evidence))
        except ValueError as e:
            return self.detail(400, str(e))

    def commerce_limits(self, body):
        f = Fields(body)
        sid = f.string("session_id", required=True, max_len=128, min_len=1, nullable=False)
        fields = {"max_total_usd": f.number("max_total_usd"), "deliver_by": f.string("deliver_by"),
                  "seller_policy": f.choice("seller_policy", POLICIES, nullable=True)}
        source = f.choice("source", ("voice", "panel"), default="voice")
        text = f.string("text", default="", max_len=500, nullable=False)
        if f.errors:
            return self.send_json(422, {"detail": f.errors})
        try:
            intent, refused, changed = set_limits(sid, source, text or "", **{k: v for k, v in fields.items() if k in body})
        except ValueError as e:
            return self.detail(400, str(e))
        return self.send_json(200, {"intent": intent_summary(intent), "refused": refused, "changed": changed})

    def voice_command(self):
        ctype = self.headers.get("Content-Type") or ""
        raw = self.read_body()
        if "multipart/form-data" not in ctype:
            return self.detail(422, "multipart/form-data required")
        fields = parse_multipart(ctype, raw)
        audio = fields.get("audio", (None, b""))[1] or b""
        sid = (fields.get("session_id", (None, b""))[1] or b"").decode()
        if not audio or not sid:
            return self.detail(422 if not sid else 400, "audio and session_id are required")
        try:
            ctx = json.loads((fields.get("context", (None, b"{}"))[1] or b"{}").decode() or "{}")
        except json.JSONDecodeError:
            return self.detail(400, "context must be JSON")
        transcript = next_transcript()
        if transcript is None:
            return self.detail(503, "speech-to-text unavailable (mock: start with --voice-transcript)")
        log(f"voice: {len(audio)} bytes audio, context keys {sorted(ctx)}, {len(ctx.get('frames') or [])} frames -> {transcript!r}")
        result = agent_command(sid, transcript, ctx)
        tts = (fields.get("tts", (None, b"true"))[1] or b"true").decode().lower() != "false"
        extra = {"audio_b64": base64.b64encode(tone_wav()).decode(), "audio_mime": "audio/wav"} if tts else {"audio_b64": None, "audio_mime": None}
        return self.send_json(200, {"transcript": transcript, **extra, **result})


def serve(port=8765, delay=1.5, host="0.0.0.0", asset_delay=2.0, scene_dir=None, sandbox=False, voice_transcript=None,
          require_hold_proof=False):
    State.delay = delay
    State.asset_delay = asset_delay
    State.sandbox = sandbox
    State.require_hold_proof = require_hold_proof
    State.voice_transcript = voice_transcript
    State.voice_index = 0
    if scene_dir:
        State.scene_dir = os.path.abspath(scene_dir)
    httpd = ThreadingHTTPServer((host, port), Handler)
    log(f"mock parts server on {host}:{port} parts={all_part_ids()} delay={delay}s asset_delay={asset_delay}s scenes={State.scene_dir} "
        f"checkout={'sandbox' if sandbox else 'offline'}{' (hold proof required)' if require_hold_proof else ''} "
        f"voice={'on' if voice_transcript else 'off'}")
    return httpd


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--delay", type=float, default=1.5)
    ap.add_argument("--asset-delay", type=float, default=2.0)
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--scene-dir", default=None)
    ap.add_argument("--sandbox", action="store_true", help="AUTHORIZED sandbox receipts instead of the offline receipt")
    ap.add_argument("--voice-transcript", default=None, help='pretend each voice recording says the next of "a|b|c" (in turn)')
    ap.add_argument("--require-hold-proof", action="store_true", help="POST /checkout without the hold proof is a 400 (REQUIRE_HOLD_PROOF)")
    a = ap.parse_args()
    try:
        serve(a.port, a.delay, a.host, a.asset_delay, a.scene_dir, a.sandbox, a.voice_transcript, a.require_hold_proof).serve_forever()
    except KeyboardInterrupt:
        pass
