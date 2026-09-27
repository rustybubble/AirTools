#!/usr/bin/env python3
"""Summarise Quest frame rate and GPU load from a logcat capture (VrApi once-a-second lines).

    python3 tools/demo/fps.py SpikeData/quest-20260926-101500.log
    python3 tools/demo/fps.py SpikeData/quest.log 'SceneStreamer: kitchen'          # from the kitchen load on
    python3 tools/demo/fps.py SpikeData/quest.log 'AppState Passthrough -> World' --until 'AppState World -> Passthrough'

`since` / `--until` are regexes matched against whole log lines; the summary starts at the LAST line
matching `since` (the latest app run or beat), or at the first line when omitted. Each VrApi line looks like
`FPS=72/72,...,App=9.8ms,...,GPU%=0.72,...,Stale=0,...`: FPS=<rendered>/<refresh target>, GPU% is a
0..1 fraction (negative = no sample, skipped), App = the app's GPU time per frame. Seconds are grouped by
refresh target (72/90/120 Hz) and by process: VrApi logs from the app (the pid that also writes Unity lines)
and from a system process; they are reported apart. A second counts as "at target" when FPS >= target - 1.
Exit status: 0, or 1 with --min-at-target P when an app group is below P percent at target.
"""

import argparse
import re
import statistics
import sys
from collections import defaultdict

VRAPI = re.compile(r"^(\S+ \S+) I/VrApi\s*\(\s*(\d+)\):.*?\bFPS=(\d+)/(\d+)")
UNITY_PID = re.compile(r"^\S+ \S+ [VDIWEF]/Unity\s*\(\s*(\d+)\)")
FIELD = {
    "gpu": re.compile(r"GPU%=(-?[\d.]+)"),
    "app": re.compile(r"\bApp=([\d.]+)ms"),
    "stale": re.compile(r"\bStale=(\d+)"),
    "temp": re.compile(r"Temp=([\d.]+)C"),
}


def pct(values, q):
    """Nearest-rank percentile (q in 0..100) of a non-empty list."""
    s = sorted(values)
    return s[min(len(s) - 1, max(0, round(q / 100 * (len(s) - 1))))]


def parse(lines):
    samples = []
    for line in lines:
        m = VRAPI.search(line)
        if not m:
            continue
        s = {"t": m.group(1), "pid": m.group(2), "fps": int(m.group(3)), "target": int(m.group(4))}
        for key, rx in FIELD.items():
            f = rx.search(line)
            s[key] = float(f.group(1)) if f else None
        samples.append(s)
    return samples


def summarise(samples, app_pids, dips_to_show):
    """One block per (process, refresh target). VrApi logs once a second from the app's own process
    (the pid that also writes Unity lines) and from a system process (compositor/shell), so they are
    kept apart instead of being double-counted. Returns the worst app at-target percentage."""
    groups = defaultdict(list)
    for s in samples:
        groups[(s["pid"], s["target"])].append(s)
    worst_pct = 100.0
    order = sorted(groups, key=lambda k: (k[0] not in app_pids, -len(groups[k])))
    for pid, target in order:
        group = groups[(pid, target)]
        is_app = pid in app_pids
        fps = [s["fps"] for s in group]
        at = sum(1 for f in fps if f >= target - 1)
        at_pct = 100.0 * at / len(fps)
        if is_app or not app_pids:
            worst_pct = min(worst_pct, at_pct)
        who = f"app pid {pid}" if is_app else f"system pid {pid} (compositor/shell, not the app)"
        print(f"\n{who}, {target} Hz target: {len(group)} s  ({group[0]['t']} -> {group[-1]['t']})")
        print(
            f"  FPS    median {statistics.median(fps):g}  p5 {pct(fps, 5)}  min {min(fps)}  mean {statistics.fmean(fps):.1f}"
            f"   at target (>= {target - 1}): {at}/{len(fps)} = {at_pct:.1f}%"
        )
        gpu = [100 * s["gpu"] for s in group if s["gpu"] is not None and s["gpu"] >= 0]
        if gpu:
            print(
                f"  GPU%   median {statistics.median(gpu):.0f}  p90 {pct(gpu, 90):.0f}  max {max(gpu):.0f}"
                f"   ({len(gpu)} valid samples)"
            )
        app = [s["app"] for s in group if s["app"] is not None and s["app"] > 0]
        if app:
            budget = 1000.0 / target
            print(
                f"  App GPU ms  median {statistics.median(app):.2f}  p90 {pct(app, 90):.2f}  max {max(app):.2f}"
                f"   (frame budget {budget:.1f} ms)"
            )
        stale = [s["stale"] for s in group if s["stale"] is not None]
        if stale:
            print(f"  Stale frames total {int(sum(stale))} (in {sum(1 for x in stale if x > 0)} s)")
        temps = [s["temp"] for s in group if s["temp"]]
        if temps:
            print(f"  Temp   {min(temps):.0f} -> {max(temps):.0f} C")
        dips = [s for s in group if s["fps"] < 0.9 * target]
        if dips:
            shown = ", ".join(f"{s['t'].split()[-1]} {s['fps']}" for s in dips[:dips_to_show])
            more = f" (+{len(dips) - dips_to_show} more)" if len(dips) > dips_to_show else ""
            print(f"  Dips < 90% of target: {len(dips)} s: {shown}{more}")
    return worst_pct


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("log", help="logcat capture (adb logcat -v time ... VrApi:I ...)")
    ap.add_argument("since", nargs="?", help="regex: start at the last line that matches")
    ap.add_argument("--until", help="regex: stop at the first matching line after `since`")
    ap.add_argument("--app-only", action="store_true", help="hide the system process's VrApi lines")
    ap.add_argument("--dips", type=int, default=8, help="how many dip timestamps to list (default 8)")
    ap.add_argument("--min-at-target", type=float, help="exit 1 if any group is below this %% at target")
    args = ap.parse_args()

    with open(args.log, errors="replace") as f:
        lines = f.readlines()
    start = 0
    if args.since:
        rx = re.compile(args.since)
        hits = [i for i, line in enumerate(lines) if rx.search(line)]
        if not hits:
            sys.exit(f"no line matches since-regex {args.since!r} in {args.log}")
        start = hits[-1]
        print(f"since line {start + 1}: {lines[start].strip()[:140]}")
        if len(hits) > 1:
            print(f"  ({len(hits)} matches; using the last. Earlier ones at lines {', '.join(str(h + 1) for h in hits[:-1][-5:])})")
    end = len(lines)
    if args.until:
        rx = re.compile(args.until)
        end = next((i for i in range(start + 1, len(lines)) if rx.search(lines[i])), len(lines))
        if end < len(lines):
            print(f"until line {end + 1}: {lines[end].strip()[:140]}")

    window = lines[start:end]
    samples = parse(window)
    app_pids = {m.group(1) for m in map(UNITY_PID.search, window) if m}
    if args.app_only and app_pids:
        samples = [s for s in samples if s["pid"] in app_pids]
    if not samples:
        sys.exit(f"no VrApi FPS lines in {args.log} (lines {start + 1}-{end}); was logcat started with VrApi:I?")
    print(f"{args.log}: {len(samples)} VrApi lines; app pid(s): {', '.join(sorted(app_pids)) or 'none seen (no Unity lines)'}")
    worst = summarise(samples, app_pids, args.dips)
    if args.min_at_target is not None and worst < args.min_at_target:
        print(f"\nBELOW {args.min_at_target:g}% at target (worst group {worst:.1f}%)")
        sys.exit(1)


if __name__ == "__main__":
    main()
