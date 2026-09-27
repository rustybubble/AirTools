"""Parse DJI SRT subtitle telemetry (plan §2.2 step 1) into GPS/altitude records.

Two on-the-wire formats, auto-detected per block:
  - "single-line OSD" (Mini 4K, confirmed against a real 88s Mini 4K clip flown indoors,
    `vid/DJI_0095.MP4`): one comma-separated line, e.g. `F/2.8, SS 30.15, ISO 710, EV 0,
    DZOOM 1.000, GPS (n/a, n/a, 4), D n/a, H 1.10m, H.S 0.00m/s, V.S -0.00m/s`. `GPS(lon, lat,
    sats)` -- the third number is the satellite count, NOT an altitude; lon/lat/sats/D all read
    "n/a" whenever there's no GPS fix (e.g. flown indoors, no sky view), while `H` (height above
    the home point, from the barometer/VPS) and `H.S`/`V.S` (horizontal/vertical speed) keep
    reporting regardless of GPS state. There is no absolute-altitude field in this format.
  - "bracket-tag" (seen on some other DJI exports, not this repo's real corpus clip): a
    `FrameCnt:` line plus bracket tags, e.g. `[latitude: 33.776123] [longitude: -84.396456]
    [rel_alt: 30.100 abs_alt: 312.456]`.
"""

import math
import re
from dataclasses import dataclass

import numpy as np

# WGS84 equatorial radius -- good enough for the equirectangular/local-tangent-plane approximation
# `to_enu` uses; the flight path is a few hundred metres across, far below where earth curvature
# or the equatorial/polar radius difference would matter.
_WGS84_RADIUS_M = 6_378_137.0

_BLOCK_SPLIT_RE = re.compile(r"\r?\n\s*\r?\n")
_TIME_RE = re.compile(r"(\d+):(\d+):(\d+)[.,](\d+)\s*-->")
_LATLON_RE = re.compile(r"\[latitude:\s*(-?[\d.]+)]\s*\[longitude:\s*(-?[\d.]+)]")
_RELABS_RE = re.compile(r"\[rel_alt:\s*(-?[\d.]+)\s+abs_alt:\s*(-?[\d.]+)]")
# lon/lat/sats each read "n/a" with no GPS fix (real DJI_0095.MP4 footage, flown indoors)
_GPS_RE = re.compile(
    r"GPS\s*\(\s*(n/a|-?[\d.]+)\s*,\s*(n/a|-?[\d.]+)\s*,\s*(n/a|-?[\d.]+)\s*\)", re.IGNORECASE
)
_HEIGHT_RE = re.compile(r"(?<![.\w])H\s+(-?[\d.]+)\s*m\b")  # "H 30.10m", not "H.S 0.00m/s"
_HSPEED_RE = re.compile(r"H\.S\s+(-?[\d.]+)\s*m/s")
_VSPEED_RE = re.compile(r"V\.S\s+(-?[\d.]+)\s*m/s")
# "D 12.34m" or "D n/a" -- distance from home; lookbehind keeps it off "DZOOM 1.000"'s D
_DIST_RE = re.compile(r"(?<![A-Za-z])D\s+(n/a|-?[\d.]+)m?", re.IGNORECASE)
_ISO_RE = re.compile(r"\bISO\s+(-?[\d.]+)")
_SHUTTER_RE = re.compile(r"\bSS\s+(-?[\d.]+)")  # raw "SS" value as DJI reports it, not converted


@dataclass
class TelemetryRecord:
    t_s: float
    lat: float  # NaN when the SRT has no GPS fix ("n/a") -- see to_enu/resample, both NaN-safe
    lon: float  # NaN the same way
    rel_alt_m: float | None = None  # "H": height above the home point, metres -- present even
    # with no GPS fix (barometer/VPS, not satellite-derived)
    abs_alt_m: float | None = None  # only set by the bracket-tag format; the single-line OSD
    # format has no independent absolute-altitude field (see module docstring, bug (c))
    satellites: float | None = None  # GPS satellite count (single-line OSD format's 3rd GPS(...)
    # number -- NOT altitude); None when "n/a"
    dist_home_m: float | None = None  # "D": horizontal distance from home, metres; None when "n/a"
    h_speed_mps: float | None = None  # "H.S": horizontal speed, m/s
    v_speed_mps: float | None = None  # "V.S": vertical speed, m/s
    iso: float | None = None
    shutter: float | None = None  # raw "SS" value, unit as DJI writes it


def _timecode_to_s(match: re.Match) -> float:
    h, m, s, frac = match.groups()
    return int(h) * 3600 + int(m) * 60 + int(s) + int(frac) / 10 ** len(frac)


def _num_or_none(text: str) -> float | None:
    return None if text.lower() == "n/a" else float(text)


def parse_srt(text: str) -> list[TelemetryRecord]:
    """Parse an SRT telemetry track's full text into records, oldest first.

    Blocks with no recognisable GPS/telemetry field at all (title cards, a malformed entry) are
    skipped rather than raising -- a drone-exported SRT is third-party input, not worth failing
    the whole parse over one bad block. A block whose GPS reads "n/a" (no fix -- common flying
    indoors) is kept, not skipped: lat/lon come back as NaN, but H/H.S/V.S/D/ISO/SS are still real
    readings worth having (bug (b)).
    """
    records: list[TelemetryRecord] = []
    for block in _BLOCK_SPLIT_RE.split(text.strip()):
        block = block.strip()
        time_match = _TIME_RE.search(block)
        if block == "" or time_match is None:
            continue
        t_s = _timecode_to_s(time_match)

        latlon = _LATLON_RE.search(block)
        if latlon is not None:  # bracket-tag format
            lat, lon = float(latlon.group(1)), float(latlon.group(2))
            relabs = _RELABS_RE.search(block)
            rel_alt = float(relabs.group(1)) if relabs else None
            abs_alt = float(relabs.group(2)) if relabs else None
            records.append(
                TelemetryRecord(t_s=t_s, lat=lat, lon=lon, rel_alt_m=rel_alt, abs_alt_m=abs_alt)
            )
            continue

        gps = _GPS_RE.search(block)  # single-line OSD format
        if gps is None:
            continue  # no recognisable telemetry at all -- title card/malformed block
        lon_s, lat_s, sats_s = gps.groups()
        lon = _num_or_none(lon_s)
        lat = _num_or_none(lat_s)
        height = _HEIGHT_RE.search(block)
        hspeed = _HSPEED_RE.search(block)
        vspeed = _VSPEED_RE.search(block)
        dist = _DIST_RE.search(block)
        iso = _ISO_RE.search(block)
        shutter = _SHUTTER_RE.search(block)

        records.append(
            TelemetryRecord(
                t_s=t_s,
                lat=lat if lat is not None else float("nan"),
                lon=lon if lon is not None else float("nan"),
                rel_alt_m=float(height.group(1)) if height else None,
                abs_alt_m=None,  # single-line OSD format has no separate absolute-altitude field
                satellites=_num_or_none(sats_s),  # bug (c): NOT abs_alt_m
                dist_home_m=_num_or_none(dist.group(1)) if dist else None,
                h_speed_mps=float(hspeed.group(1)) if hspeed else None,
                v_speed_mps=float(vspeed.group(1)) if vspeed else None,
                iso=float(iso.group(1)) if iso else None,
                shutter=float(shutter.group(1)) if shutter else None,
            )
        )
    return records


def to_enu(records: list[TelemetryRecord], origin: tuple[float, float] | None = None) -> np.ndarray:
    """Local East-North-Up metres, one row per record. `origin` is (lat, lon); defaults to the
    first record with a real GPS fix (skipping any leading NaN lat/lon, e.g. no fix yet at
    take-off) -- (0.0, 0.0) if no record has one at all, at which point every row's east/north
    comes out NaN anyway (safe: propagates rather than raising, never a crash). Up is `rel_alt_m`
    (height above launch) when present, else `abs_alt_m` relative to the first record's
    `abs_alt_m` (falls back to 0.0 if neither exists) -- unlike lat/lon, `rel_alt_m` needs no GPS
    fix (barometer/VPS), so Up is usually populated even when East/North are all NaN."""
    if not records:
        return np.zeros((0, 3))
    if origin is not None:
        lat0, lon0 = origin
    else:
        fix = next((r for r in records if not math.isnan(r.lat) and not math.isnan(r.lon)), None)
        lat0, lon0 = (fix.lat, fix.lon) if fix is not None else (0.0, 0.0)
    lat0_rad = math.radians(lat0)
    abs_alt0 = records[0].abs_alt_m or 0.0

    out = np.empty((len(records), 3))
    for i, r in enumerate(records):
        out[i, 0] = math.radians(r.lon - lon0) * _WGS84_RADIUS_M * math.cos(lat0_rad)  # east
        out[i, 1] = math.radians(r.lat - lat0) * _WGS84_RADIUS_M  # north
        if r.rel_alt_m is not None:
            out[i, 2] = r.rel_alt_m
        elif r.abs_alt_m is not None:
            out[i, 2] = r.abs_alt_m - abs_alt0
        else:
            out[i, 2] = 0.0
    return out


def resample(records: list[TelemetryRecord], times_s: list[float]) -> list[TelemetryRecord]:
    """Linear interpolation to `times_s` (e.g. one per extracted frame).

    Handles the Mini 4K's reported 1 Hz GPS update (many consecutive records share the same
    lat/lon even though a subtitle line is written every ~33ms): interpolating between two
    identical points just returns that point, so duplicate positions need no special-casing here.
    Times outside the recorded range clamp to the nearest endpoint (numpy.interp's default).
    NaN lat/lon (no GPS fix) needs no special-casing either -- `numpy.interp` just propagates NaN
    through any interpolated point that touches one, which is the safe behaviour (never fabricate
    a position), including the all-NaN case (indoor flight, no fix the whole clip).
    """
    if not records:
        raise ValueError("resample: no telemetry records to interpolate from")
    order = np.argsort([r.t_s for r in records])
    ts = np.array([records[i].t_s for i in order])
    lats = np.array([records[i].lat for i in order])
    lons = np.array([records[i].lon for i in order])
    rel_alts = np.array([records[i].rel_alt_m for i in order], dtype=np.float64)  # NaN where None
    abs_alts = np.array([records[i].abs_alt_m for i in order], dtype=np.float64)

    lat_i = np.interp(times_s, ts, lats)
    lon_i = np.interp(times_s, ts, lons)
    rel_i = np.interp(times_s, ts, rel_alts)
    abs_i = np.interp(times_s, ts, abs_alts)

    return [
        TelemetryRecord(
            t_s=float(t),
            lat=float(lat_i[i]),
            lon=float(lon_i[i]),
            rel_alt_m=None if np.isnan(rel_i[i]) else float(rel_i[i]),
            abs_alt_m=None if np.isnan(abs_i[i]) else float(abs_i[i]),
        )
        for i, t in enumerate(times_s)
    ]
