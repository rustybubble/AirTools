#!/usr/bin/env python3
"""Tick off the AirTools headset ([H]) checks from a Quest logcat.

    python3 tools/demo/hcheck.py LOG [--since HH:MM[:SS]] [--until HH:MM[:SS]] [--json]

Record LOG during the session in docs/headset-checklist.md:

    adb logcat -c && adb logcat -v time Unity:I VrApi:I '*:S' > SpikeData/hcheck-$(date +%m%d-%H%M).log

Both `-v time` and `-v threadtime` lines are read. `--since` / `--until` take `HH:MM[:SS]` (the most recent
occurrence of that time in the log) or `MM-DD HH:MM[:SS]`.

Status: PASS / FAIL = the log proves or disproves it; NOT SEEN = no evidence either way; EYE = nothing in the log
can prove it, so tick it by hand. Kind: `log` = the log proves the whole check; `log+eye` = the log proves the
event happened and a person still confirms the look or feel; `eye` = visual only.

FPS comes from the VrApi lines of the app's own process (the pid that writes the [AirTools] lines; the system
shell logs VrApi lines too). One sample per second: FPS=<frames>/<display Hz>. "At target" means
frames >= Hz - 1. Samples in the first 2 s after a mode or scene change, and the first sample after a gap of
more than 2.5 s (app start or resume), are left out and counted as skipped.

Python 3 standard library only. Exit code 1 if any check FAILs.
"""
from __future__ import annotations

import argparse
import bisect
import datetime as _dt
import json
import math
import re
import statistics
import sys
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Tuple

# ------------------------------------------------------------------------------------------------ parsing

_TS = r"(?P<mo>\d\d)-(?P<dd>\d\d)\s+(?P<hh>\d\d):(?P<mi>\d\d):(?P<ss>\d\d)\.(?P<ms>\d{3})"
RE_TIME = re.compile(r"^" + _TS + r"\s+(?P<lvl>[VDIWEF])/(?P<tag>[^(]*?)\s*\(\s*(?P<pid>\d+)\):\s?(?P<msg>.*)$")
RE_THREADTIME = re.compile(r"^" + _TS + r"\s+(?P<pid>\d+)\s+\d+\s+(?P<lvl>[VDIWEF])\s+(?P<tag>[^:]*?)\s*:\s?(?P<msg>.*)$")
RE_WHEN = re.compile(r"^(?:(?P<mo>\d\d)-(?P<dd>\d\d)[ T]+)?(?P<hh>\d{1,2}):(?P<mi>\d\d)(?::(?P<ss>\d\d)(?:\.(?P<ms>\d{1,3}))?)?$")

# logcat has no year. Any leap year makes 02-29 parse; only differences matter (a log across New Year breaks).
_YEAR = 2028
SETTLE_S = 2.0          # skip FPS samples this long after a mode / scene change
GAP_S = 2.5             # a sample after a longer gap is a start / resume sample: skipped
AT_TARGET_PCT = 95.0    # "sustained": this share of samples at frames >= Hz - 1 ...
MIN_WORLD_SAMPLES = 30  # ... over at least this many settled World seconds
MIN_RING_SAMPLES = 5
FLICKER_S = 0.5         # palm menu closed and reopened within this: tracking flicker
VOICE_BUDGET_S = 3.0    # SPEC M6: voice round trip < 3 s for equip_tool


def _day(mo: int, dd: int) -> int:
    return _dt.date(_YEAR, mo, dd).toordinal()


@dataclass
class Line:
    no: int          # line number in the file (1-based)
    t: float         # seconds on a continuous clock (day * 86400 + time of day)
    day: int
    stamp: str       # "MM-DD HH:MM:SS.mmm"
    level: str
    tag: str
    pid: int
    msg: str

    @property
    def text(self) -> str:
        """The message without the "[AirTools] " prefix."""
        return self.msg[len("[AirTools] "):] if self.msg.startswith("[AirTools] ") else self.msg


def parse_line(raw: str, no: int = 0) -> Optional[Line]:
    raw = raw.rstrip("\r\n")
    m = RE_TIME.match(raw) or RE_THREADTIME.match(raw)
    if not m:
        return None
    try:
        day = _day(int(m["mo"]), int(m["dd"]))
    except ValueError:
        return None
    tod = int(m["hh"]) * 3600 + int(m["mi"]) * 60 + int(m["ss"]) + int(m["ms"]) / 1000.0
    stamp = f"{m['mo']}-{m['dd']} {m['hh']}:{m['mi']}:{m['ss']}.{m['ms']}"
    return Line(no, day * 86400 + tod, day, stamp, m["lvl"], m["tag"].strip(), int(m["pid"]), m["msg"])


def parse_text(text: str) -> List[Line]:
    out = []
    for i, raw in enumerate(text.splitlines(), 1):
        ln = parse_line(raw, i)
        if ln is not None:
            out.append(ln)
    return out


def read_log(path: str) -> List[Line]:
    with open(path, encoding="utf-8", errors="replace") as f:
        return parse_text(f.read())


def resolve_when(text: str, lines: List[Line]) -> float:
    """`HH:MM[:SS]` = its most recent occurrence in the log; `MM-DD HH:MM[:SS]` = exactly that."""
    m = RE_WHEN.match(text.strip())
    if not m or int(m["hh"]) > 23 or int(m["mi"]) > 59 or int(m["ss"] or 0) > 59:
        raise ValueError(f"can't read the time {text!r}: use HH:MM[:SS] or MM-DD HH:MM[:SS]")
    tod =int(m["hh"]) * 3600 + int(m["mi"]) * 60 + int(m["ss"] or 0) + int((m["ms"] or "0").ljust(3, "0")) / 1000.0
    if m["mo"]:
        return _day(int(m["mo"]), int(m["dd"])) * 86400 + tod
    if not lines:
        return tod
    last = max(l.t for l in lines)
    days = sorted({l.day for l in lines})
    for d in reversed(days):
        if d * 86400 + tod <= last:
            return d * 86400 + tod
    return days[0] * 86400 + tod


# ------------------------------------------------------------------------------------------------ timelines

class Timeline:
    """A value that changes at given times (set() in time order)."""

    def __init__(self, initial):
        self.ts: List[float] = [-math.inf]
        self.vals = [initial]

    def set(self, t: float, v) -> None:
        self.ts.append(t)
        self.vals.append(v)

    def at(self, t: float):
        return self.vals[bisect.bisect_right(self.ts, t) - 1]

    def changes(self) -> List[Tuple[float, object]]:
        return list(zip(self.ts[1:], self.vals[1:]))


@dataclass
class Sample:
    t: float
    pid: int
    fps: int
    target: int
    gpu: Optional[float]       # fraction 0..1 (negative readings dropped)
    app_ms: Optional[float]    # app GPU time per frame
    mode: str
    scene: str
    menu_open: bool            # the whole 1-s window lies inside a palm-menu-open interval
    holding: bool              # ... inside a part-in-hand interval (taken, not yet placed)
    skip: Optional[str]        # None, "settle" or "resume"


RE_FPS = re.compile(r"FPS=(\d+)/(\d+)")
RE_GPU = re.compile(r"GPU%=(-?[\d.]+)")
RE_APP = re.compile(r"(?:^|,)App=([\d.]+)ms")

A = r"^\[AirTools\] "            # an app log line (Log.Info / Warn / Error)
C = r"^\[AirTools\.Check\] "     # an acceptance check line (Log.Check)
RE_MODE = re.compile(A + r"AppState (\w+) -> (\w+)")
RE_SCENE_LOADED = re.compile(A + r"SceneStreamer: (\S+) · (.*) in ([\d.]+) s \(")
RE_SCENE_BUILTIN = re.compile(A + r"SceneStreamer: back to the built-in scene")
RE_MENU_OPEN = re.compile(A + r"Palm menu open(?: \((\w+)\))?")
RE_MENU_CLOSED = re.compile(A + r"Palm menu closed")
RE_IN_HAND = re.compile(A + r"Part \S+ in hand \(")
RE_HOLD_END = re.compile(A + r"(Part placed |Part tool put away|AppState )")


class Ctx:
    """Everything the checks read: app events (whole log, for state), the window's events, timelines, samples."""

    def __init__(self, lines: List[Line], since: Optional[float] = None, until: Optional[float] = None):
        self.all = sorted(lines, key=lambda l: l.t)
        self.since = -math.inf if since is None else since
        self.until = math.inf if until is None else until
        order: List[int] = []
        for l in self.all:
            if l.msg.startswith("[AirTools") and l.pid not in order:
                order.append(l.pid)
        if not order:  # no app lines at all: fall back to whoever logs as Unity
            for l in self.all:
                if l.tag == "Unity" and l.pid not in order:
                    order.append(l.pid)
        self.pids = order
        pidset = set(order)
        self.events = [l for l in self.all if l.pid in pidset and l.msg.startswith("[AirTools")]
        self.win = [e for e in self.events if self.since <= e.t <= self.until]
        self.span: Dict[int, Tuple[float, float]] = {}
        for l in self.all:
            if l.pid in pidset:
                a, b = self.span.get(l.pid, (l.t, l.t))
                self.span[l.pid] = (min(a, l.t), max(b, l.t))
        self.mode: Dict[int, Timeline] = {p: Timeline("Passthrough") for p in order}
        self.scene: Dict[int, Timeline] = {p: Timeline("built-in") for p in order}
        self.menu: Dict[int, List[Tuple[float, float]]] = {p: [] for p in order}
        self.hold: Dict[int, List[Tuple[float, float]]] = {p: [] for p in order}
        open_at: Dict[int, Optional[float]] = {p: None for p in order}
        hold_at: Dict[int, Optional[float]] = {p: None for p in order}
        for e in self.events:
            if RE_IN_HAND.search(e.msg):
                if hold_at[e.pid] is None:
                    hold_at[e.pid] = e.t
            elif hold_at[e.pid] is not None and RE_HOLD_END.search(e.msg):
                self.hold[e.pid].append((hold_at[e.pid], e.t))
                hold_at[e.pid] = None
            m = RE_MODE.search(e.msg)
            if m:
                self.mode[e.pid].set(e.t, m.group(2))
                continue
            m = RE_SCENE_LOADED.search(e.msg)
            if m:
                self.scene[e.pid].set(e.t, m.group(1))
                continue
            if RE_SCENE_BUILTIN.search(e.msg):
                self.scene[e.pid].set(e.t, "built-in")
                continue
            if RE_MENU_OPEN.search(e.msg):
                if open_at[e.pid] is None:
                    open_at[e.pid] = e.t
            elif RE_MENU_CLOSED.search(e.msg) and open_at[e.pid] is not None:
                self.menu[e.pid].append((open_at[e.pid], e.t))
                open_at[e.pid] = None
        for p, t0 in open_at.items():
            if t0 is not None:
                self.menu[p].append((t0, self.span[p][1]))
        for p, t0 in hold_at.items():
            if t0 is not None:
                self.hold[p].append((t0, self.span[p][1]))
        self.samples = self._samples(pidset)
        days = {l.day for l in self.all if self.since <= l.t <= self.until}
        self.multi_day = len(days) > 1

    def _samples(self, pidset) -> List[Sample]:
        out: List[Sample] = []
        last_t: Dict[int, float] = {}
        changes = {p: sorted([t for t, _ in self.mode[p].changes()] + [t for t, _ in self.scene[p].changes()]) for p in self.pids}
        for l in self.all:
            if l.pid not in pidset or not l.msg.startswith("FPS="):
                continue
            m = RE_FPS.search(l.msg)
            if not m:
                continue
            prev = last_t.get(l.pid)
            last_t[l.pid] = l.t
            if not (self.since <= l.t <= self.until):
                continue
            g = RE_GPU.search(l.msg)
            gpu = float(g.group(1)) if g else None
            if gpu is not None and gpu < 0:
                gpu = None
            a = RE_APP.search(l.msg)
            ch = changes[l.pid]
            i = bisect.bisect_right(ch, l.t) - 1
            skip = None
            if prev is None or l.t - prev > GAP_S:
                skip = "resume"
            elif i >= 0 and l.t - ch[i] < SETTLE_S:
                skip = "settle"
            menu = any(o <= l.t - 1.0 and l.t <= c for o, c in self.menu[l.pid])
            hold = any(o <= l.t - 1.0 and l.t <= c for o, c in self.hold[l.pid])
            out.append(Sample(l.t, l.pid, int(m.group(1)), int(m.group(2)), gpu, float(a.group(1)) if a else None,
                              self.mode[l.pid].at(l.t), self.scene[l.pid].at(l.t), menu, hold, skip))
        return out

    # -- queries (all on the window's events) --

    def find(self, pattern, after: float = None, before: float = None, pid: int = None, levels: str = None):
        rx = re.compile(pattern) if isinstance(pattern, str) else pattern
        out = []
        for e in self.win:
            if after is not None and e.t < after:
                continue
            if before is not None and e.t > before:
                continue
            if pid is not None and e.pid != pid:
                continue
            if levels is not None and e.level not in levels:
                continue
            m = rx.search(e.msg)
            if m:
                out.append((e, m))
        return out

    def first(self, pattern, **kw):
        r = self.find(pattern, **kw)
        return r[0] if r else (None, None)

    def when(self, t: float) -> str:
        d = _dt.date.fromordinal(int(t // 86400))
        s = t - (t // 86400) * 86400
        hms = f"{int(s // 3600):02d}:{int(s % 3600 // 60):02d}:{int(s % 60):02d}"
        return f"{d.month:02d}-{d.day:02d} {hms}" if self.multi_day else hms


# ------------------------------------------------------------------------------------------------ checks

PASS, FAIL, NOT_SEEN, EYE = "PASS", "FAIL", "NOT SEEN", "EYE"


@dataclass
class Result:
    status: str
    line: Optional[Line] = None
    evidence: str = ""
    note: str = ""


@dataclass
class Check:
    step: int
    id: str
    kind: str        # "log" | "log+eye" | "eye"
    title: str
    fn: Optional[Callable[[Ctx], Result]] = None


CHECKS: List[Check] = []


def check(step: int, cid: str, kind: str, title: str):
    def deco(fn):
        CHECKS.append(Check(step, cid, kind, title, fn))
        return fn
    return deco


def eye(step: int, cid: str, title: str, upgrade: Optional[str] = None) -> None:
    """A visual-only check. `upgrade`: a log line (suggested in the checklist's appendix) that, once the app writes
    it, proves the check; until then the check stays EYE."""
    fn = None
    if upgrade is not None:
        def fn(ctx: Ctx, _rx=re.compile(upgrade)) -> Result:
            e, _ = ctx.first(_rx)
            return Result(PASS, e, e.text) if e else Result(EYE)
    CHECKS.append(Check(step, cid, "eye", title, fn))


def seen(ctx: Ctx, pattern, note_if_missing: str = "", **kw) -> Result:
    """PASS on the first line matching `pattern` (or the first of several patterns, in order of preference)."""
    for p in (pattern if isinstance(pattern, (list, tuple)) else [pattern]):
        e, _ = ctx.first(p, **kw)
        if e:
            return Result(PASS, e, e.text)
    return Result(NOT_SEEN, note=note_if_missing)


# ---- 0. setup / whole session

LOOPBACK = ("127.0.0.1", "localhost", "[::1]", "::1")
RE_SERVER_CFG = re.compile(A + r"ServerConfig: server (?:override|=) (\S+?),?(?: site (\S+))?$")
SERVER_OK = [
    re.compile(r"candidates from server"),
    re.compile(A + r"Part \S+ loaded from server"),
    re.compile(A + r"Notebook POST \S+ → "),
    re.compile(A + r'Agent: "'),
    re.compile(A + r"Sellers for \S+ re-ranked by the server"),
    re.compile(A + r"Checkout authorized: "),
]
RE_UNREACHABLE = re.compile(A + r"SceneStreamer: server unreachable")


def server_evidence(ctx: Ctx, after: float = -math.inf) -> List[Line]:
    """Lines that prove a round trip to the laptop server (a scene load not from the headset cache, a search, ...)."""
    hits = []
    for e, _ in ctx.find(RE_SCENE_LOADED, after=after):
        cached = ctx.find(RE_UNREACHABLE, after=e.t - 15, before=e.t, pid=e.pid)
        if not cached:
            hits.append(e)
    for rx in SERVER_OK:
        hits += [e for e, _ in ctx.find(rx, after=after)]
    return sorted(hits, key=lambda l: l.t)


def network_path(ctx: Ctx) -> str:
    url = None
    for e in ctx.events:
        m = RE_SERVER_CFG.search(e.msg)
        if m and m.group(1) != "-":
            url = m.group(1)
    if url is None:
        return "default http://127.0.0.1:8000 (USB tunnel: adb reverse tcp:8000 tcp:8000)"
    host = re.sub(r"^\w+://", "", url).split("/")[0].rsplit(":", 1)[0]
    return f"{url} ({'USB tunnel' if host in LOOPBACK else 'LAN / hotspot'})"


@check(0, "net.server", "log", "The headset reaches the laptop server (scene, search, parts, agent)")
def _net_server(ctx: Ctx) -> Result:
    hits = server_evidence(ctx)
    if hits:
        return Result(PASS, hits[0], hits[0].text, f"{len(hits)} server round trips")
    bad = ctx.find(A + r"SceneStreamer: server unreachable|fell back to the offline catalog|Notebook POST \S+ failed|SceneStreamer: \S+ unavailable")
    if bad:
        return Result(FAIL, bad[0][0], bad[0][0].text, "only offline fallbacks: is the server running and the tunnel/hotspot up?")
    return Result(NOT_SEEN)


@check(0, "net.wifi", "log", "Optional: the headset reaches the laptop over the hotspot (not the USB tunnel)")
def _net_wifi(ctx: Ctx) -> Result:
    for e, m in ctx.find(RE_SERVER_CFG):
        url = m.group(1)
        if url == "-":
            continue
        host = re.sub(r"^\w+://", "", url).split("/")[0].rsplit(":", 1)[0]
        if host in LOOPBACK:
            continue
        hits = server_evidence(ctx, after=e.t)
        if hits:
            return Result(PASS, hits[0], f"{e.text} → {hits[0].text}")
        return Result(FAIL, e, e.text, "server set to a LAN address but no round trip succeeded")
    return Result(NOT_SEEN, note="USB-tunnel session; launch with -e server http://<hotspot-ip>:8000 to check Wi-Fi")


@check(0, "scene.real", "log", "The real scene package (kitchen) loads from the laptop")
def _scene_real(ctx: Ctx) -> Result:
    loads = ctx.find(RE_SCENE_LOADED)
    if loads:
        e, m = loads[0]
        cached = ctx.find(RE_UNREACHABLE, after=e.t - 15, before=e.t, pid=e.pid)
        note = f"loaded in {m.group(3)} s" + (" from the headset cache (server unreachable)" if cached else "")
        return Result(PASS, e, e.text, note)
    bad = ctx.find(A + r"SceneStreamer: .*(unavailable|failed|unreadable)", levels="WE")
    if bad:
        return Result(FAIL, bad[0][0], bad[0][0].text)
    return Result(NOT_SEEN, note="only the built-in facade: is the kitchen on the server (GET /scenes)?")


@check(0, "app.errors", "log", "No app errors or exceptions during the session")
def _app_errors(ctx: Ctx) -> Result:
    pidset = set(ctx.pids)
    errs = [l for l in ctx.all if l.pid in pidset and ctx.since <= l.t <= ctx.until and l.level in "EF"]
    ours = [l for l in errs if l.msg.startswith("[AirTools") or "Exception" in l.msg]
    other = [l for l in errs if l not in ours and l.msg.strip()]
    tally = ""
    if other:
        top: Dict[str, int] = {}
        for l in other:
            top[l.msg[:60]] = top.get(l.msg[:60], 0) + 1
        k = max(top, key=top.get)
        tally = f"{len(other)} other Unity error lines (most: {top[k]}× \"{k}\")"
    if ours:
        return Result(FAIL, ours[0], ours[0].text, f"{len(ours)} app error/exception lines" + (f"; {tally}" if tally else ""))
    if not ctx.win:
        return Result(NOT_SEEN)
    return Result(PASS, None, "0 app errors, 0 exceptions", tally)


# ---- 1. put on

eye(1, "m1.passthrough", "Passthrough is clear on the 3S; the Enter world pill and its hint are readable")
eye(1, "ui.text", "Text sizes and contrast read well in passthrough and in the world (toasts, windows, labels)")


# ---- 2. enter the world

@check(2, "m1.entry", "log+eye", "Enter world by a hand poke on the pill (eye: by hand, pill within easy reach)")
def _m1_entry(ctx: Ctx) -> Result:
    presses = ctx.find(A + r"(?:World button|Chest) pressed \(#\d+\) in Passthrough")
    for e, _ in presses:
        mode, _ = ctx.first(RE_MODE, after=e.t, before=e.t + 1.0, pid=e.pid)
        if mode is not None and "-> World" in mode.msg:
            # With the appendix's button line the input is proven too: "Button … (poke, hand)".
            how, _ = ctx.first(A + r"Button .*\((poke|ray), (hand|controller)\)", after=e.t - 0.3, before=e.t, pid=e.pid)
            return Result(PASS, e, (how.text + " → " if how else "") + f"{e.text} → {mode.text}")
    if presses:
        return Result(FAIL, presses[0][0], presses[0][0].text, "pressed but the world didn't open")
    return Result(NOT_SEEN)


RE_TRANSITION = re.compile(C + r"M1\.mode\.transition to=(\w+) seconds=([\d.]+) budget=([\d.]+)(?: max_frame_ms=(\d+))? (PASS|FAIL)")


@check(2, "m1.fade", "log+eye", "Mode fades finish within budget (eye: no judder during the fade)")
def _m1_fade(ctx: Ctx) -> Result:
    rows = ctx.find(RE_TRANSITION)
    if not rows:
        return Result(NOT_SEEN)
    fails = [(e, m) for e, m in rows if m.group(5) == "FAIL"]
    worst = max(rows, key=lambda r: float(r[1].group(2)))
    dips = []
    for e, m in rows:
        after = [s.fps for s in ctx.samples if s.pid == e.pid and e.t - 1.0 < s.t <= e.t + 3.0 and s.skip != "resume"]
        if after:
            dips.append(min(after))
    note = f"{len(rows)} fades, slowest {worst[1].group(2)} s (budget {worst[1].group(3)} + 0.1)"
    if dips:
        note += f"; lowest fps around a fade {min(dips)}"
    frames = [int(m.group(4)) for _, m in rows if m.group(4)]
    if frames:
        note += f"; longest frame in a fade {max(frames)} ms"
    if fails:
        return Result(FAIL, fails[0][0], fails[0][0].msg, note)
    return Result(PASS, worst[0], worst[0].msg, note)


@check(2, "ui.default_tool", "log", "The world opens with the tape armed (D1: Measure), with the entry hint")
def _default_tool(ctx: Ctx) -> Result:
    for e, _ in ctx.find(A + r"AppState Passthrough -> World"):
        # Active tool just before entering (ToolManager keeps a tool across visits; the default is equipped if none).
        active = None
        for p, _m in ctx.find(A + r"(\w+) tool (equipped|put away)", before=e.t, pid=e.pid):
            active = _m.group(1) if _m.group(2) == "equipped" else (None if active == _m.group(1) else active)
        move, _ = ctx.first(A + r"Measure tool equipped", after=e.t - 0.05, before=e.t + 2.0, pid=e.pid)
        old, _ = ctx.first(A + r"Move tool equipped", after=e.t - 0.05, before=e.t + 2.0, pid=e.pid)
        hint, _ = ctx.first(A + r"Toast: (Left palm ▸ Move|Open your left palm|Move: |Measure: |Turn your left palm up|Press the left menu button)", after=e.t - 0.05, before=e.t + 2.0, pid=e.pid)
        tail = f" · {hint.text}" if hint else ""
        if move is not None:
            return Result(PASS, move, move.text + tail)
        if active == "Measure":
            return Result(PASS, e, e.text + " (Measure already in hand)" + tail)
        if old is not None or active == "Move":
            return Result(FAIL, old or e, (old or e).text + tail, "Move in hand on entry: a build from before D1 (Sat 09-26), which opens with Measure")
        if active is None:
            return Result(FAIL, e, e.text + tail, "entered the world with no tool and Measure wasn't equipped")
    return Result(NOT_SEEN)


# ---- 3. move

@check(3, "move.teleport", "log+eye", "Move: a quick pinch on a flat surface teleports there (eye: grab-the-air drag)")
def _teleport(ctx: Ctx) -> Result:
    r = seen(ctx, A + r"Locomotion: teleported to")
    drag, _ = ctx.first(A + r"Locomotion: moved to")
    if drag:
        r.note = "grab-the-air drag logged too"
    return r


# ---- 4. tool ring

@check(4, "ring.open", "log", "The tool ring opens with the left palm up (hand tracking, not a controller)")
def _ring_open(ctx: Ctx) -> Result:
    rows = ctx.find(RE_MENU_OPEN)
    by: Dict[str, int] = {}
    for _, m in rows:
        k = m.group(1) or "?"
        by[k] = by.get(k, 0) + 1
    tally = ", ".join(f"{k} ×{v}" for k, v in sorted(by.items()))
    hand = [e for e, m in rows if m.group(1) == "hand"]
    if hand:
        return Result(PASS, hand[0], hand[0].text, f"opened: {tally}")
    if rows:
        return Result(NOT_SEEN, rows[0][0], rows[0][0].text, f"opened only by {tally}: put the controllers down")
    return Result(NOT_SEEN)


@check(4, "ring.stable", "log", "The ring stays open and still while the right hand works it (no close/reopen flicker)")
def _ring_stable(ctx: Ctx) -> Result:
    opens = ctx.find(A + r"Palm menu open \(hand\)")
    if not opens:
        return Result(NOT_SEEN)
    flickers = []
    for pid in ctx.pids:
        closed_at = None
        for e, _ in ctx.find(A + r"Palm menu (open|closed)", pid=pid):
            if "closed" in e.msg:
                closed_at = e.t
            elif closed_at is not None:
                if e.t - closed_at < FLICKER_S:
                    flickers.append(e)
                closed_at = None
    note = f"{len(opens)} opens by hand, {len(flickers)} reopened < {FLICKER_S} s after closing"
    if len(flickers) >= 2:
        return Result(FAIL, flickers[0], flickers[0].text, note)
    return Result(PASS, opens[0][0], opens[0][0].text, note)


@check(4, "ring.spin", "log+eye", "Pinch-drag spins the ring and the mode under the lens is equipped (eye: ticks, pulse, buzz)")
def _ring_spin(ctx: Ctx) -> Result:
    rows = ctx.find(A + r"Ring: equipped (.+)$")
    if rows:
        labels = sorted({m.group(1) for _, m in rows})
        return Result(PASS, rows[0][0], rows[0][0].text, "equipped: " + ", ".join(labels))
    old = ctx.find(A + r"Menu: Toggle(Measure|Level|Move)")
    return Result(NOT_SEEN, note="older palm-menu build (Menu: … lines, no ring)" if old else "")


eye(4, "ring.tap", "A quick pinch or poke on a side item spins it to the top", upgrade=A + r"Ring: spin to ")


# ---- 5. measure

@check(5, "measure.distance", "log+eye", "Two-point tape by right-hand pinch (eye: placing points feels right)")
def _measure_distance(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Notebook #\d+ measure: Distance ")


@check(5, "measure.shape", "log+eye", "3–4 point shape: sides, angles and area (eye: labels on the right corners)")
def _measure_shape(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Notebook #\d+ measure: (Triangle|Quad|Polygon)")


@check(5, "measure.done_gesture", "log", "Thumb + middle pinch finishes a shape")
def _done_gesture(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Done gesture \(\w+\)")


eye(5, "measure.labels", "Labels readable at 2 m, never overlapping, never cut by the surface they sit on")
eye(5, "measure.snap", "Snap glyphs (corner / edge / plane) and a haptic tick when a snap is acquired",
    upgrade=A + r"Measure point \d+ \((Corner|Edge|Midpoint|Plane)\)")


# ---- 6. undo / redo, back to Move

@check(6, "ring.undo_redo", "log+eye", "Undo then Redo on the ring's end capsules (undo is implied by the redo)")
def _undo_redo(ctx: Ctx) -> Result:
    e, _ = ctx.first(A + r"(Ring: redo|Notebook #\d+ restored: )")
    if e:
        u, _ = ctx.first(A + r"(Ring: undo|Menu: Undo)", before=e.t)
        return Result(PASS, e, e.text, "undo logged" if u else "undo not logged by the ring: implied by the redo")
    u, _ = ctx.first(A + r"(Ring: undo\b|Menu: Undo)")
    if u:
        return Result(NOT_SEEN, u, u.text, "undo seen, no redo")
    return Result(NOT_SEEN)


@check(6, "ring.double_pinch", "log", "Two quick left-hand pinches go back to Move")
def _double_pinch(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Double pinch \(\w+\): back to the default mode")


# ---- 7. settings window (scalemodels: "More" was renamed "Settings", with a gear; the log lines are unchanged)

@check(7, "ring.action", "log", "A pinch on the lens runs the action under it (Notebook, Model view, Settings)")
def _ring_action(ctx: Ctx) -> Result:
    rows = ctx.find(A + r"Ring: ran (\w+)")
    if rows:
        return Result(PASS, rows[0][0], rows[0][0].text, "ran: " + ", ".join(sorted({m.group(1) for _, m in rows})))
    return Result(NOT_SEEN)


@check(7, "scene.window", "log+eye", "Settings (the gear; the Scene window, once More) opens from the ring and works by hand")
def _scene_window(ctx: Ctx) -> Result:
    return seen(ctx, [A + r"Ring: ran ToggleScenePanel", A + r"Menu: ToggleScenePanel"])


@check(7, "scene.scale", "log", "Set scale from a known dimension on the keypad (tape → real length → Set scale)")
def _scene_scale(ctx: Ctx) -> Result:
    rows = ctx.find(C + r"scale\.known_dimension (.*) (PASS|FAIL)$")
    scale, _ = ctx.first(A + r"Scene scale ×")
    if rows:
        e, m = rows[-1]
        ev = (scale.text + " · " if scale else "") + m.group(1)
        return Result(PASS if m.group(2) == "PASS" else FAIL, e, ev)
    warn, _ = ctx.first(A + r"Scale: ")
    if warn:
        return Result(FAIL, warn, warn.text)
    return Result(NOT_SEEN)


# scalemodels: the kitchen opens at its default scale ×1.63 (SiteScales); a scale set on this headset overrides it.
RE_DEFAULT_SCALE = re.compile(A + r"SceneStreamer: (\S+) r(\d+) at its default scale ×([\d.]+)")
RE_RESTORED_SCALE = re.compile(A + r"SceneStreamer: restored scale ×([\d.]+) for (\S+) r(\d+)")
KITCHEN_SCALE = 1.63


@check(7, "scene.default_scale", "log+eye", "The kitchen opens at ×1.63 (eye: Settings reads \"Scale ×1.63 · set for this scan\")")
def _default_scale(ctx: Ctx) -> Result:
    for e, m in ctx.find(RE_DEFAULT_SCALE):
        if m.group(1) == "kitchen":
            ok = abs(float(m.group(3)) - KITCHEN_SCALE) < 0.005
            return Result(PASS if ok else FAIL, e, e.text, "" if ok else f"want ×{KITCHEN_SCALE}")
    for e, m in ctx.find(RE_RESTORED_SCALE):
        if m.group(2) == "kitchen":
            return Result(NOT_SEEN, e, e.text, "a scale set on this headset overrides the default: Settings ▸ Reset (tap twice) goes back to ×1.63")
    return Result(NOT_SEEN)


@check(7, "ui.contrast", "log+eye", "Contrast and Less motion chips in Settings (eye: the look changes)")
def _contrast(ctx: Ctx) -> Result:
    rows = ctx.find(A + r"Menu: Toggle(Contrast|Motion)")
    if rows:
        return Result(PASS, rows[0][0], rows[0][0].text, "toggled: " + ", ".join(sorted({m.group(1) for _, m in rows})))
    return Result(NOT_SEEN)


RE_UNITS = re.compile(A + r"Units: (ft·in|m) \((\w+)\)")
RE_UNITS_CHECK = re.compile(C + r"D2\.units\.relabel (.*) (PASS|FAIL)$")


@check(7, "ui.units", "log+eye", "Units chip (Settings) switches every label between ft·in and m (eye: the labels re-word in place)")
def _units(ctx: Ctx) -> Result:
    checks = ctx.find(RE_UNITS_CHECK)          # AgentHarness.RunD2(), if it ran on the device
    bad = [(e, m) for e, m in checks if m.group(2) == "FAIL"]
    if bad:
        return Result(FAIL, bad[0][0], bad[0][0].text)
    rows = ctx.find(RE_UNITS)
    if rows:
        return Result(PASS, rows[0][0], rows[0][0].text, "switched: " + " → ".join(m.group(1) for _, m in rows))
    if checks:
        return Result(PASS, checks[-1][0], checks[-1][0].text)
    return Result(NOT_SEEN)


# ---- 8. ask + voice

@check(8, "scene.ask", "log+eye", "\"What is this?\" answers and pins it (eye: the pin sits on the thing you looked at)")
def _scene_ask(ctx: Ctx) -> Result:
    asks = ctx.find(A + r'Scene ask "(.*?)" → "(.*?)"')
    for e, m in asks:
        pin, _ = ctx.first(A + r"Scene pin #\d+ from frame", after=e.t, before=e.t + 10, pid=e.pid)
        if pin:
            return Result(PASS, pin, f"\"{m.group(2)}\" · {pin.text}")
    if asks:
        e, m = asks[0]
        warn, _ = ctx.first(A + r"Scene pin: ", after=e.t, before=e.t + 10, pid=e.pid)
        return Result(FAIL, warn or e, (warn or e).text, f"answered \"{m.group(2)}\" but no pin")
    t, _ = ctx.first(A + r"Toast: (No photos of this spot|Scene questions need the live model|Still answering|Can't answer now|Couldn't reach the laptop|The assistant)")
    return Result(NOT_SEEN, t, t.text if t else "")


RE_THINKING = re.compile(A + r"Voice: Thinking")
RE_AGENT = re.compile(A + r'Agent: "(.*?)" → "(.*?)" actions \[(.*)\]$')


@check(8, "voice.talk", "log", "Hold to talk: a spoken request comes back as a reply and actions")
def _voice_talk(ctx: Ctx) -> Result:
    for e, _ in ctx.find(RE_THINKING):
        reply, m = ctx.first(RE_AGENT, after=e.t, before=e.t + 60, pid=e.pid)
        if reply:
            return Result(PASS, reply, reply.text, f"{reply.t - e.t:.1f} s after release")
    bad = ctx.find(A + r"(Voice: (No microphone|Microphone .* didn't start|Voice is offline|/voice/command)|Agent: (?!\"))")
    if bad:
        return Result(FAIL, bad[0][0], bad[0][0].text)
    return Result(NOT_SEEN)


@check(8, "voice.latency", "log", f"Voice round trip for equip_tool < {VOICE_BUDGET_S:.0f} s (release → tool equipped)")
def _voice_latency(ctx: Ctx) -> Result:
    thinks = ctx.find(RE_THINKING)
    runs = []
    for i, (e, _) in enumerate(thinks):
        end = thinks[i + 1][0].t if i + 1 < len(thinks) else e.t + 30
        act, m = ctx.first(A + r"Agent action equip_tool (.*) → (ok|failed)", after=e.t, before=min(end, e.t + 30), pid=e.pid)
        if act:
            said, sm = ctx.first(RE_AGENT, after=e.t, before=act.t, pid=e.pid)
            runs.append((act.t - e.t, act, m.group(2), sm.group(1) if sm else "?"))
    if not runs:
        return Result(NOT_SEEN, note="tap Talk and say e.g. \"switch to the level\"")
    worst = max(runs, key=lambda r: r[0])
    note = "round trips: " + ", ".join(f"{r[0]:.1f} s" for r in runs)
    ev = f"\"{worst[3]}\" → {worst[1].text} in {worst[0]:.1f} s"
    ok = all(r[0] <= VOICE_BUDGET_S and r[2] == "ok" for r in runs)
    return Result(PASS if ok else FAIL, worst[1], ev, note)


RE_UTTERANCE = re.compile(A + r"Voice utterance mode=(\w+) stop=(\S+) press=([\d.]+)s speech=([\d.]+)s trailing=([\d.]+)s "
                          r"preroll=([\d.]+)s kept=([\d.]+)s total=([\d.]+)s bytes=(\d+)")


# voice: tap or hold Talk logs one line per utterance (VoiceClient); nothing is sent without speech.
@check(8, "voice.utterance", "log+eye", "Talk, tap or hold: speech is heard, trimmed and sent (eye: the meter moves with your voice)")
def _voice_utterance(ctx: Ctx) -> Result:
    rows = ctx.find(RE_UTTERANCE)
    if not rows:
        return Result(NOT_SEEN, note="tap Talk and say something (builds before tap-or-hold log no utterance lines)")
    note = ", ".join(f"{m.group(1)}/{m.group(2)} speech {m.group(4)} s → {int(m.group(9)) // 1000} kB" for _, m in rows[:6])
    sent = [(e, m) for e, m in rows if int(m.group(9)) > 0]
    if not sent:
        return Result(FAIL, rows[-1][0], rows[-1][0].text, "no utterance had speech: " + note)
    e, _ = sent[-1]
    return Result(PASS, e, e.text, note)


# ---- 9. level

@check(9, "level.reading", "log+eye", "Level / plumb on a real surface (eye: the gizmo and bubble read clearly)")
def _level(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Notebook #\d+ level: (Level|Plumb) ")


# ---- 10. notebook

@check(10, "notebook.open", "log+eye", "The notebook opens from the ring (eye: rows readable, a row poke highlights)")
def _notebook_open(ctx: Ctx) -> Result:
    r = seen(ctx, [A + r"Ring: ran ToggleNotebook", A + r"Menu: ToggleNotebook"])
    row, _ = ctx.first(A + r"Notebook row \d+ highlighted")
    if row:
        r.note = row.text
    return r


@check(10, "notebook.export", "log", "Export writes CSV + HTML on the headset and sends them to the laptop")
def _notebook_export(ctx: Ctx) -> Result:
    e, _ = ctx.first(A + r"Notebook exported: ")
    if e:
        post, _ = ctx.first(A + r"Notebook POST ", after=e.t, before=e.t + 15, pid=e.pid)
        note = "" if post is None else ("sent to the laptop" if "→" in post.msg else "laptop unreachable: kept on the headset")
        return Result(PASS, e, e.text, note)
    bad, _ = ctx.first(A + r"Notebook export failed")
    return Result(FAIL, bad, bad.text) if bad else Result(NOT_SEEN)


# ---- 11. find + place

RE_SEARCH = re.compile(A + r'Parts search "(.*?)": (\d+) candidates from (.+)$')


@check(11, "parts.search", "log", "A Find parts preset (poked by hand) returns candidates from the server")
def _parts_search(ctx: Ctx) -> Result:
    rows = ctx.find(RE_SEARCH)
    good = [(e, m) for e, m in rows if m.group(3).startswith("server") and int(m.group(2)) > 0]
    if good:
        return Result(PASS, good[0][0], good[0][0].text, f"{len(rows)} searches")
    if rows:
        return Result(FAIL, rows[0][0], rows[0][0].text, "no server results (offline catalog or empty)")
    return Result(NOT_SEEN)


@check(11, "parts.take", "log+eye", "Take a candidate: the part is in hand at true size (eye: it feels right-sized)")
def _parts_take(ctx: Ctx) -> Result:
    e, m = ctx.first(A + r"Part (\S+) in hand \((\w+)\)")
    if not e:
        return Result(NOT_SEEN)
    dims, _ = ctx.first(A + r"Part " + re.escape(m.group(1)) + r" loaded from \w+: ", before=e.t, pid=e.pid)
    return Result(PASS, e, (dims.text + " · " if dims else "") + e.text,
                  "offline catalog model" if m.group(2) == "catalog" else "")


RE_PLACED = re.compile(A + r"Part placed (\S+) on (.+?): (Green|Amber|Red|None)\b(?::\s*(.*))?$")


@check(11, "parts.place", "log+eye", "Place it on the scan with a fit colour (eye: the colour matches reality)")
def _parts_place(ctx: Ctx) -> Result:
    rows = ctx.find(RE_PLACED)
    if not rows:
        return Result(NOT_SEEN)
    tally: Dict[str, int] = {}
    for _, m in rows:
        tally[m.group(3)] = tally.get(m.group(3), 0) + 1
    green = [e for e, m in rows if m.group(3) == "Green"]
    e = green[0] if green else rows[0][0]
    return Result(PASS, e, e.text, ", ".join(f"{k} {v}" for k, v in sorted(tally.items())))


@check(11, "parts.grab", "log+eye", "Pick a placed part up again by hand and re-seat it")
def _parts_grab(ctx: Ctx) -> Result:
    direct, _ = ctx.first(A + r"Part picked up ")
    if direct:
        return Result(PASS, direct, direct.text)
    # Inferred: the same part placed twice with no new "in hand" between = it was picked up off the surface.
    for pid in ctx.pids:
        last: Dict[str, str] = {}
        for e, _ in ctx.find(A + r"Part (placed \S+|\S+ in hand)", pid=pid):
            m = re.search(r"Part placed (\S+)", e.msg)
            if m:
                if last.get(m.group(1)) == "placed":
                    return Result(PASS, e, e.text, "re-seated without a new take: it was picked up")
                last[m.group(1)] = "placed"
            else:
                m = re.search(r"Part (\S+) in hand", e.msg)
                last[m.group(1)] = "hand"
    return Result(NOT_SEEN)


eye(11, "parts.preview", "The held part glides along its plane without jitter; placement guides show edges and fit")


@check(11, "parts.inspector", "log+eye", "Inspector card beside the ring: a finish swatch recolours the part (eye: readable)")
def _inspector(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Part \S+: finish \S+ #[0-9A-Fa-f]{6}")


# ---- 12. array

RE_ARRAY = re.compile(A + r"Part array: (\d+) × (\S+) every (\d+) mm along ([\d.]+) m, (\d+) green")


def array_quantity(length_m: float, spacing_mm: float) -> int:
    """ArrayPlanner.Quantity: ⌊L/s + 0.03⌋ + 1."""
    return int(math.floor(length_m / (spacing_mm / 1000.0) + 0.03)) + 1


@check(12, "array.place", "log", "Array along the last tape: ⌊L/s⌋+1 copies at the listing's spacing")
def _array(ctx: Ctx) -> Result:
    rows = ctx.find(RE_ARRAY)
    if not rows:
        hint, _ = ctx.first(A + r"Toast: .*(?i:(single unit|place one part first|measure the run first|has no spacing|no spacing listed|fill the run))")
        return Result(NOT_SEEN, hint, hint.text if hint else "")
    e, m = rows[-1]
    n, s, L, g = int(m.group(1)), float(m.group(3)), float(m.group(4)), int(m.group(5))
    ok = n in {array_quantity(L - 0.0005, s), array_quantity(L + 0.0005, s)}
    note = f"expected {array_quantity(L, s)} for {L:.3f} m at {s:.0f} mm; {g}/{n} green"
    return Result(PASS if ok else FAIL, e, e.text, note)


# ---- 13. sellers + checkout

@check(13, "sellers.sort", "log+eye", "Seller panel by hand: sort by delivery and by price (eye: rows readable)")
def _sellers(ctx: Ctx) -> Result:
    rows = ctx.find(A + r"Sellers for (\S+), by (\w+):")
    if not rows:
        return Result(NOT_SEEN)
    sorts = sorted({m.group(2) for _, m in rows})
    rer, _ = ctx.first(A + r"Sellers for \S+ re-ranked by the server")
    note = "sorted by " + ", ".join(sorts) + ("; re-ranked by the server" if rer else "")
    if {"price", "eta"} <= set(sorts):
        return Result(PASS, rows[0][0], rows[0][0].text, note)
    return Result(NOT_SEEN, rows[0][0], rows[0][0].text, note + " (tap the other sort too)")


@check(13, "checkout.hold", "log", "Hold Pay 1 s by hand: one request, receipt, chime, notebook entry")
def _checkout(ctx: Ctx) -> Result:
    for e, m in ctx.find(A + r"Checkout authorized: (\S+) ([\d.]+) USD"):
        hold, _ = ctx.first(A + r"Hold confirmed", after=e.t - 30, before=e.t, pid=e.pid)
        entry, _ = ctx.first(A + r"Notebook #\d+ purchase: ", after=e.t - 1, before=e.t + 1, pid=e.pid)
        toast, _ = ctx.first(A + r"Toast: (?:✓ )?(Paid|Offline receipt|Receipt saved)", after=e.t - 1, before=e.t + 1, pid=e.pid)
        if hold and entry:
            return Result(PASS, e, e.text + (f" · {toast.text}" if toast else ""), entry.text)
    fail, _ = ctx.first(A + r"Checkout failed: ")
    if fail:
        return Result(FAIL, fail, fail.text)
    opened, _ = ctx.first(A + r"Checkout opened: ")
    return Result(NOT_SEEN, opened, opened.text if opened else "", "opened, never paid" if opened else "")


@check(13, "checkout.gate", "log", "No checkout request without a completed 1-s hold (a short tap sends nothing)")
def _checkout_gate(ctx: Ctx) -> Result:
    reqs = ctx.find(A + r"Checkout request: ")
    if not reqs:
        return Result(NOT_SEEN)
    holds = [e for e, _ in ctx.find(A + r"Hold confirmed")]
    used = set()
    for e, _ in reqs:
        match = next((h for h in holds if h.pid == e.pid and e.t - 1.0 <= h.t <= e.t and id(h) not in used), None)
        if match is None:
            return Result(FAIL, e, e.text, "a checkout request without a completed hold")
        used.add(id(match))
    return Result(PASS, reqs[0][0], reqs[0][0].text, f"{len(holds)} completed holds → {len(reqs)} requests")


# ---- 14. exit + take it home

# Ring v2 (declutter DC3): Exit world moved from the ring to Settings (once More), where it asks twice (a GlassButton confirm: two
# "Button ExitWorld (poke|ray, …)" presses, the first only arms it). Older builds: "Ring: armed Exit" then "Ring: ran".
RE_EXIT_PRESS = A + r"Button \S*Exit\S* \((poke|ray)"


@check(14, "ring.exit_confirm", "log+eye", "Exit world from Settings (older builds: More, or the ring) (eye: it asked for a second tap)")
def _exit(ctx: Ctx) -> Result:
    exits = ctx.find(A + r"Ring: ran ToggleWorld") or ctx.find(A + r"Menu: ToggleWorld")
    for e, _ in exits:
        mode, _ = ctx.first(A + r"AppState World -> Passthrough", after=e.t - 0.1, before=e.t + 1.0, pid=e.pid)
        if mode:
            armed, _ = ctx.first(A + r"Ring: armed Exit", after=e.t - 3.5, before=e.t, pid=e.pid)
            presses = ctx.find(RE_EXIT_PRESS, after=e.t - 3.5, before=e.t + 0.1, pid=e.pid)
            if not armed and len(presses) >= 2:
                armed = presses[0][0]
            return Result(PASS, e, (armed.text + " → " if armed else "") + e.text + " → " + mode.text)
    return Result(NOT_SEEN)


@check(14, "home.table", "log+eye", "Take it home: the bought part stands on the real table (eye: at true size, on the table)")
def _home(ctx: Ctx) -> Result:
    e, m = ctx.first(A + r"Take it home: (\d+) part\(s\) on the table")
    if not e:
        return Result(NOT_SEEN, note="buy something first (step 13), then Exit world")
    return Result(PASS if int(m.group(1)) > 0 else FAIL, e, e.text)


# ---- 15. tabletop

@check(15, "tabletop.toggle", "log+eye", "Tabletop: the scene as a 1:50 model on the table (eye: the fade hides the jump)")
def _tabletop(ctx: Ctx) -> Result:
    e, _ = ctx.first(A + r"Tabletop on \(1:\d+\)")
    if not e:
        return Result(NOT_SEEN)
    back, _ = ctx.first(A + r"AppState Tabletop -> World", after=e.t, pid=e.pid)
    return Result(PASS, e, e.text + (" · back: " + back.text if back else ""))


# scalemodels: Model view lists what's on the headset even with the laptop away: the kept listing at start
# ("N models known on this headset (M downloaded)") and one "Prefetch: <site> r<rev> on the headset …" per package.
RE_KNOWN_MODELS = re.compile(A + r"SceneStreamer: (\d+) models known on this headset \((\d+) downloaded\)")
RE_PREFETCHED = re.compile(A + r"Prefetch: (\S+) r(\d+) on the headset .*Models on the headset: (\d+) of (\d+)")


@check(15, "models.headset", "log", "Model view's models are on the headset (kept listing + background download), laptop or not")
def _models_headset(ctx: Ctx) -> Result:
    got = ctx.find(RE_PREFETCHED)
    if got:
        e, m = got[-1]
        return Result(PASS, e, e.text, f"{len(got)} downloaded this session: " + ", ".join(x.group(1) for _, x in got))
    known = ctx.find(RE_KNOWN_MODELS)
    if known:
        e, m = known[-1]
        n, d = int(m.group(1)), int(m.group(2))
        return Result(PASS if d >= 2 else NOT_SEEN, e, e.text,
                      "" if d >= 2 else "only one model on the headset: keep the app open with the laptop reachable to download the rest")
    return Result(NOT_SEEN, note="an older build (no kept listing)")


# The notebook label keeps "Distance 1.50 m · 4′ 11″" (metric first; UX D2 changed only what's drawn), but a tape is
# read from any of its shapes so a label change can't blind the check: metric first, imperial first
# ("Distance 4′ 11⅛″ · 1.50 m") or feet and inches only ("Distance 4′ 11⅛″").
RE_TAPE = re.compile(A + r"Notebook #(\d+) (?:measure|updated): Distance (.+)$")
RE_METRES = re.compile(r"(?<![\d.])(\d+(?:\.\d+)?) m(?![\w²])")
RE_FEET_INCHES = re.compile(r"(?<![\d.])(?:(\d+)′\s*)?(\d+)?([⅛¼⅜½⅝¾⅞])?″")
EIGHTHS = {"⅛": 1, "¼": 2, "⅜": 3, "½": 4, "⅝": 5, "¾": 6, "⅞": 7}


def length_m(label: str) -> Optional[float]:
    """Metres from a length label: "1.50 m · 4′ 11″", "4′ 11⅛″ · 1.50 m" (the metres win when both are there, they're
    exact to the cm), "4′ 11⅛″" or "10¼″". None when it isn't a length."""
    m = RE_METRES.search(label or "")
    if m:
        return float(m.group(1))
    for m in RE_FEET_INCHES.finditer(label or ""):
        if m.group(1) or m.group(2) or m.group(3):
            inches = int(m.group(1) or 0) * 12 + int(m.group(2) or 0) + EIGHTHS.get(m.group(3) or "", 0) / 8.0
            return inches * 0.0254
    return None


@check(15, "tabletop.tape", "log+eye", "A tape on the 1:50 model reads scene metres (eye: same as the 1:1 reading)")
def _tabletop_tape(ctx: Ctx) -> Result:
    rows = ctx.find(A + r"Notebook #\d+ measure: ")
    table = [(e, m) for e, m in rows if ctx.mode[e.pid].at(e.t) == "Tabletop"]
    if not table:
        return Result(NOT_SEEN)
    e, _ = table[0]
    note = ""
    m = RE_TAPE.search(e.msg)
    v = length_m(m.group(2)) if m else None
    if v is not None:
        world = [(w, wm, length_m(wm.group(2))) for w, wm in ctx.find(RE_TAPE, before=e.t, pid=e.pid)
                 if ctx.mode[e.pid].at(w.t) == "World"]
        world = [r for r in world if r[2] is not None]
        if world:
            w, wm, wv = min(world, key=lambda r: abs(r[2] - v))
            d = abs(wv - v) / max(v, 1e-6) * 100
            note = f"closest 1:1 tape #{wm.group(1)} {wv:.2f} m (Δ {d:.1f} %)"
    return Result(PASS, e, e.text, note)


# ---- 16. home + fps

@check(16, "move.home", "log", "Home (Settings; older builds: More, or the ring) puts you back at the spawn point")
def _home_move(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Locomotion: home")


def stats(samples: List[Sample]) -> Optional[dict]:
    if not samples:
        return None
    fps = sorted(s.fps for s in samples)
    n = len(fps)
    at = sum(1 for s in samples if s.fps >= s.target - 1)
    gpu = [s.gpu * 100 for s in samples if s.gpu is not None]
    app = [s.app_ms for s in samples if s.app_ms is not None]
    targets: Dict[int, int] = {}
    for s in samples:
        targets[s.target] = targets.get(s.target, 0) + 1
    return {
        "samples": n,
        "target_hz": max(targets, key=targets.get),
        "median": statistics.median(fps),
        "p10": percentile(fps, 10),
        "min": fps[0],
        "at_target_pct": 100.0 * at / n,
        "gpu_median_pct": statistics.median(gpu) if gpu else None,
        "gpu_max_pct": max(gpu) if gpu else None,
        "app_gpu_ms_median": statistics.median(app) if app else None,
    }


def percentile(sorted_vals: List[float], p: float) -> float:
    if not sorted_vals:
        return float("nan")
    k = (len(sorted_vals) - 1) * p / 100.0
    lo, hi = int(math.floor(k)), int(math.ceil(k))
    return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * (k - lo)


def sustained(st: dict) -> bool:
    return st["at_target_pct"] >= AT_TARGET_PCT and st["median"] >= st["target_hz"] - 1


def fmt_stats(st: dict) -> str:
    gpu = f", GPU {st['gpu_median_pct']:.0f} % median / {st['gpu_max_pct']:.0f} % max" if st["gpu_median_pct"] is not None else ""
    return (f"{st['samples']} s, median {st['median']:g} fps, p10 {st['p10']:g}, "
            f"{st['at_target_pct']:.1f} % at {st['target_hz']} Hz{gpu}")


@check(16, "perf.world72", "log", "72 fps sustained in the world with the real scene (VrApi)")
def _perf_world(ctx: Ctx) -> Result:
    real = [s for s in ctx.samples if s.skip is None and s.mode == "World" and s.scene != "built-in"]
    st = stats(real)
    if st is None or st["samples"] < MIN_WORLD_SAMPLES:
        builtin = stats([s for s in ctx.samples if s.skip is None and s.mode == "World"])
        note = f"only {len(real)} s in the world with a scanned scene"
        if builtin:
            note += f"; all World: {fmt_stats(builtin)}"
        return Result(NOT_SEEN, None, "", note)
    scenes = sorted({s.scene for s in real})
    ev = f"{'/'.join(scenes)}: {fmt_stats(st)}"
    return Result(PASS if sustained(st) else FAIL, None, ev,
                  f"sustained = ≥ {AT_TARGET_PCT:.0f} % of 1-s samples at ≥ Hz−1 and median ≥ Hz−1")


@check(16, "perf.ring", "log", "Frame rate holds with the palm menu (Liquid Glass ring) open in the world")
def _perf_ring(ctx: Ctx) -> Result:
    ring = [s for s in ctx.samples if s.skip is None and s.mode == "World" and s.menu_open]
    st = stats(ring)
    if st is None or st["samples"] < MIN_RING_SAMPLES:
        return Result(NOT_SEEN, None, "", f"only {len(ring)} s with the menu open in the world (need {MIN_RING_SAMPLES})")
    return Result(PASS if sustained(st) else FAIL, None, fmt_stats(st))


@check(16, "perf.hold", "log", "Frame rate holds while a part is in hand over the scene (taken, not yet placed)")
def _perf_hold(ctx: Ctx) -> Result:
    held = [s for s in ctx.samples if s.skip is None and s.mode == "World" and s.holding]
    st = stats(held)
    if st is None or st["samples"] < MIN_RING_SAMPLES:
        return Result(NOT_SEEN, None, "", f"only {len(held)} s with a part in hand in the world (need {MIN_RING_SAMPLES})")
    worst = min(held, key=lambda s: s.fps)
    note = f"lowest {worst.fps} fps at {ctx.when(worst.t)}" if worst.fps < worst.target - 1 else ""
    return Result(PASS if sustained(st) else FAIL, None, fmt_stats(st), note)


# ---- 17. optional, last (it leaves the app)

@check(17, "sellers.open_url", "log+eye", "Optional, last: Open at seller opens the product page in the Quest browser")
def _open_url(ctx: Ctx) -> Result:
    return seen(ctx, A + r"Open at seller: ")


# ------------------------------------------------------------------------------------------------ report

def evaluate(ctx: Ctx) -> List[Tuple[Check, Result]]:
    out = []
    for c in sorted(CHECKS, key=lambda c: c.step):
        if c.fn is None:
            out.append((c, Result(EYE)))
            continue
        try:
            r = c.fn(ctx)
        except Exception as ex:  # a check must never take the report down
            r = Result(NOT_SEEN, note=f"check error: {type(ex).__name__}: {ex}")
        out.append((c, r))
    return out


def perf_rows(ctx: Ctx) -> List[Tuple[str, dict]]:
    rows = []
    kept = [s for s in ctx.samples if s.skip is None]
    for scene in sorted({s.scene for s in kept if s.mode == "World"}, key=lambda x: (x == "built-in", x)):
        st = stats([s for s in kept if s.mode == "World" and s.scene == scene])
        rows.append((f"World · {scene}", st))
    for name, pick in (("World · menu open", lambda s: s.menu_open), ("World · part in hand", lambda s: s.holding)):
        st = stats([s for s in kept if s.mode == "World" and pick(s)])
        if st:
            rows.append((name, st))
    for mode in ("Tabletop", "Passthrough"):
        st = stats([s for s in kept if s.mode == mode])
        if st:
            rows.append((mode, st))
    return rows


def world_stretches(ctx: Ctx) -> List[Tuple[int, float, float, str]]:
    out = []
    for pid in ctx.pids:
        t0, t1 = ctx.span[pid]
        cuts = sorted({t for t, _ in ctx.mode[pid].changes()} | {t for t, _ in ctx.scene[pid].changes()} | {t0, t1})
        for a, b in zip(cuts, cuts[1:]):
            a2, b2 = max(a, ctx.since), min(b, ctx.until)
            if b2 > a2 and ctx.mode[pid].at(a) == "World":
                if out and out[-1][0] == pid and abs(out[-1][2] - a2) < 1e-6 and out[-1][3] == ctx.scene[pid].at(a):
                    out[-1] = (pid, out[-1][1], b2, out[-1][3])
                else:
                    out.append((pid, a2, b2, ctx.scene[pid].at(a)))
    return out


def clip(s: str, n: int) -> str:
    return s if len(s) <= n else s[: n - 1] + "…"


def report(path: str, ctx: Ctx, results, width: int = 100) -> str:
    L = []
    win = [l for l in ctx.all if ctx.since <= l.t <= ctx.until]
    L.append(f"AirTools headset checks · {path}")
    if win:
        L.append(f"window  {win[0].stamp} → {win[-1].stamp}  ({len(win)} lines, {len(ctx.win)} app lines)")
    runs = []
    for pid in ctx.pids:
        a, b = ctx.span[pid]
        if b < ctx.since or a > ctx.until:
            continue
        runs.append(f"pid {pid} {ctx.when(max(a, ctx.since))}–{ctx.when(min(b, ctx.until))}")
    L.append("app     " + ("; ".join(runs) if runs else "no [AirTools] lines: is this an AirTools log?"))
    L.append(f"server  {network_path(ctx)}")
    L.append("")
    L.append(f"{'STEP':<4} {'ID':<21} {'STATUS':<8} {'KIND':<7} {'TIME':<{14 if ctx.multi_day else 8}}  EVIDENCE")
    for c, r in results:
        t = ctx.when(r.line.t) if r.line else ""
        ev = r.evidence if r.status != EYE else c.title
        if r.note:
            ev = f"{ev} [{r.note}]" if ev else f"[{r.note}]"
        L.append(f"{c.step:<4} {c.id:<21} {r.status:<8} {c.kind:<7} {t:<{14 if ctx.multi_day else 8}}  {clip(ev, width)}")
    L.append("")
    count: Dict[str, Dict[str, int]] = {}
    for c, r in results:
        count.setdefault(c.kind, {}).setdefault(r.status, 0)
        count[c.kind][r.status] += 1
    for kind in ("log", "log+eye", "eye"):
        if kind in count:
            L.append(f"{kind:<8} " + ", ".join(f"{k} {v}" for k, v in sorted(count[kind].items())))
    logged = [c.id for c, r in results if c.kind == "log+eye" and r.status == PASS]
    if logged:
        L.append(f"logged, now confirm the look/feel by eye: {', '.join(logged)}")
    L.append(f"eye only, tick by hand: {', '.join(c.id for c, r in results if r.status == EYE)}")
    todo = [c.id for c, r in results if r.status == NOT_SEEN]
    if todo:
        L.append(f"not seen (redo the step or confirm by eye): {', '.join(todo)}")
    L.append("")
    L.append("FPS / GPU (VrApi, app process only, 1-s samples; at target = fps ≥ Hz−1)")
    rows = perf_rows(ctx)
    if not rows:
        L.append("  no VrApi FPS lines from the app: was logcat started with VrApi:I?")
    else:
        L.append(f"  {'stretch':<24} {'n':>5} {'median':>7} {'p10':>5} {'min':>4} {'at Hz':>9}  {'GPU% med/max':>12} {'app GPU ms':>10}")
        for name, st in rows:
            gpu = f"{st['gpu_median_pct']:.0f} / {st['gpu_max_pct']:.0f}" if st["gpu_median_pct"] is not None else "-"
            app = f"{st['app_gpu_ms_median']:.1f}" if st["app_gpu_ms_median"] is not None else "-"
            L.append(f"  {clip(name, 24):<24} {st['samples']:>5} {st['median']:>7g} {st['p10']:>5.0f} {st['min']:>4} "
                     f"{st['at_target_pct']:>6.1f} % @{st['target_hz']:<3} {gpu:>12} {app:>10}")
        skipped = [s for s in ctx.samples if s.skip]
        if skipped:
            L.append(f"  skipped {len(skipped)} samples ({sum(1 for s in skipped if s.skip == 'settle')} within {SETTLE_S:g} s of a "
                     f"mode/scene change, {sum(1 for s in skipped if s.skip == 'resume')} after a start/resume gap)")
    stretches = world_stretches(ctx)
    if stretches:
        parts = [f"{ctx.when(a)}–{ctx.when(b)} {b - a:.0f} s {scene}" for _, a, b, scene in stretches]
        more = f" (+{len(parts) - 8} more)" if len(parts) > 8 else ""
        L.append("  World stretches: " + "; ".join(parts[:8]) + more)
    return "\n".join(L)


def to_json(path: str, ctx: Ctx, results) -> dict:
    return {
        "log": path,
        "server": network_path(ctx),
        "checks": [{"step": c.step, "id": c.id, "kind": c.kind, "title": c.title, "status": r.status,
                    "time": r.line.stamp if r.line else None, "line": r.line.no if r.line else None,
                    "evidence": r.evidence, "note": r.note} for c, r in results],
        "perf": [{"stretch": name, **st} for name, st in perf_rows(ctx)],
        "world_stretches": [{"pid": p, "start": ctx.when(a), "end": ctx.when(b), "seconds": round(b - a, 1), "scene": s}
                            for p, a, b, s in world_stretches(ctx)],
    }


def run(path: str, since: Optional[str] = None, until: Optional[str] = None):
    lines = read_log(path)
    t0 = resolve_when(since, lines) if since else None
    t1 = resolve_when(until, lines) if until else None
    if t0 is not None and t1 is not None and t1 < t0:
        t1 += 86400
    ctx = Ctx(lines, t0, t1)
    return ctx, evaluate(ctx)


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="Tick off AirTools headset checks from a Quest logcat (see docs/headset-checklist.md).")
    ap.add_argument("log", help="logcat file (adb logcat -v time Unity:I VrApi:I '*:S')")
    ap.add_argument("--since", help="start of the session: HH:MM[:SS] (most recent occurrence) or MM-DD HH:MM[:SS]")
    ap.add_argument("--until", help="end of the session, same forms")
    ap.add_argument("--json", action="store_true", help="print JSON instead of the table")
    ap.add_argument("--width", type=int, default=100, help="evidence column width (default 100)")
    a = ap.parse_args(argv)
    try:
        ctx, results = run(a.log, a.since, a.until)
    except (OSError, ValueError) as ex:
        print(f"hcheck: {ex}", file=sys.stderr)
        return 2
    if a.json:
        print(json.dumps(to_json(a.log, ctx, results), indent=2, ensure_ascii=False))
    else:
        print(report(a.log, ctx, results, a.width))
    return 1 if any(r.status == FAIL for _, r in results) else 0


if __name__ == "__main__":
    sys.exit(main())
