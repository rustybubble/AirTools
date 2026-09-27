#!/usr/bin/env python3
"""Check (and warm) the demo's parts searches against a backend, with the exact requests the app sends.

    python3 tools/demo/parts_check.py                              # :8000, the six preset chips, no tape
    python3 tools/demo/parts_check.py --tapes none,demo --sellers  # + with each chip's demo tape, + seller lists
    python3 tools/demo/parts_check.py --base http://127.0.0.1:8002 --tapes none,demo --sellers --extras
    python3 tools/demo/parts_check.py --pace 25                    # warming a live server: >= 25 s between searches

Requests mirror Assets/AirTools/Runtime/Parts/PartsClient.cs: POST /parts/search {query, max_candidates: 3,
measurement: {label: "tape #N", value_m, axis}} (measurement only while a tape exists), then poll
GET /parts/jobs/{id}; the first candidate's part.json / model.glb / image.jpg; POST /parts/{id}/sellers?sort=cheapest.
A HIT is a finished job with >= 1 candidate. On an OFFLINE=true server a hit means the query's normalized text is in
the search_by_query cache (the tape only changes the fit, never hit/miss).
--extras adds the offline-relevant non-search calls: typed agent commands, /voice/command (503 offline expected)
and one /checkout (writes an order + notebook entry on that server: use it on a throwaway instance).
Standard library only. Prints a table; exit 1 if any chip search misses.
"""

import argparse
import io
import json
import sys
import time
import urllib.error
import urllib.request
import uuid
import wave

# The Find parts window's preset chips (Assets/AirTools/Editor/MainSceneBuilder.cs BuildPartsMenu): label, query.
CHIPS = ["cabinet hinge", "drawer slide", "shelf bracket", "gutter hanger", "window ac", "cabinet knob"]
# A plausible tape per chip for the demo beat (value_m, axis as PartsClient.TapeAxis would send it).
DEMO_TAPES = {
    "cabinet hinge": (0.2625, "w"),   # kitchen door o29 width
    "drawer slide": (0.4174, "w"),    # a drawer/dishwasher front, as taped on the headset
    "shelf bracket": (0.3, "w"),
    "gutter hanger": (4.2, "length"), # the synthetic facade's gutter run
    "window ac": (1.5, "w"),          # the synthetic facade's window
    "cabinet knob": (0.2625, "w"),
}


def call(base, method, path, body=None, timeout=60, raw=None, headers=None):
    """(status, parsed JSON or bytes or None, seconds)."""
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(base + path, data=data, method=method, headers=headers or {})
    if body is not None and raw is None:
        req.add_header("Content-Type", "application/json")
    t0 = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            payload = resp.read()
            status = resp.status
    except urllib.error.HTTPError as exc:
        payload, status = exc.read(), exc.code
    except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
        return None, str(exc), time.monotonic() - t0
    dt = time.monotonic() - t0
    try:
        return status, json.loads(payload), dt
    except ValueError:
        return status, payload, dt


def search(base, query, tape, n_tape, poll_timeout):
    body = {"query": query, "max_candidates": 3}
    if tape:
        body["measurement"] = {"label": f"tape #{n_tape}", "value_m": tape[0], "axis": tape[1]}
    t0 = time.monotonic()
    status, job, _ = call(base, "POST", "/parts/search", body)
    if status != 200 or not isinstance(job, dict):
        return {"state": f"HTTP {status}", "secs": time.monotonic() - t0, "cands": [], "err": job}
    instant = job.get("status") == "done"
    while job.get("status") not in ("done", "failed") and time.monotonic() - t0 < poll_timeout:
        time.sleep(0.5)
        status, job, _ = call(base, "GET", f"/parts/jobs/{job['job_id'] if 'job_id' in job else job['id']}")
        if status != 200:
            return {"state": f"poll HTTP {status}", "secs": time.monotonic() - t0, "cands": []}
    if job.get("status") not in ("done", "failed"):
        return {"state": "timeout", "secs": time.monotonic() - t0, "cands": []}
    if instant:  # the fast-path response has no summary: fetch the job once for it
        _, full, _ = call(base, "GET", f"/parts/jobs/{job['job_id']}")
        job = full if isinstance(full, dict) else job
    return {
        "state": job["status"],
        "instant": instant,
        "secs": time.monotonic() - t0,
        "cands": job.get("candidates") or [],
        "summary": job.get("summary"),
        "err": job.get("error"),
    }


def files(base, part_id):
    out = []
    s, pj, _ = call(base, "GET", f"/parts/{part_id}/part.json")
    out.append(f"json:{(pj.get('asset') or {}).get('status') if s == 200 and isinstance(pj, dict) else s}")
    for name in ("model.glb", "image.jpg"):
        s, _, _ = call(base, "GET", f"/parts/{part_id}/{name}")
        out.append(f"{name.split('.')[1]}:{'ok' if s == 200 else s}")
    return " ".join(out)


def sellers(base, part_id):
    s, part, dt = call(base, "POST", f"/parts/{part_id}/sellers?sort=cheapest", {}, timeout=60)
    if s != 200 or not isinstance(part, dict):
        return f"HTTP {s}"
    rec = part.get("recommended_seller")
    names = [x.get("name") for x in part.get("sellers") or []]
    pick = f", rec {names[rec]}" if rec is not None and 0 <= rec < len(names) else ""
    return f"{len(names)} ({dt:.1f}s{pick})"


def tiny_wav():
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1), w.setsampwidth(2), w.setframerate(16000)
        w.writeframes(b"\x00\x00" * 8000)
    return buf.getvalue()


def extras(base, first_part):
    session = f"quest-demoops-{uuid.uuid4().hex[:6]}"
    rows = []
    for text in ("find me a cabinet hinge", "find a hanger for this gutter", "cheapest first", "select the first one"):
        ctx = {"candidate_ids": [first_part], "selected_part_id": first_part} if first_part else {}
        s, r, dt = call(base, "POST", "/agent/command", {"session_id": session, "text": text, "context": ctx, "wait_s": 3})
        if s == 200 and isinstance(r, dict):
            acts = ",".join(a.get("name", "?") for a in r.get("actions") or [])
            job = r.get("job_id")
            n = ""
            if job:
                _, j, _ = call(base, "GET", f"/parts/jobs/{job}")
                n = f" -> job {j.get('status')} with {len(j.get('candidates') or [])} candidates" if isinstance(j, dict) else ""
            rows.append(f"agent {text!r}: {r.get('reply')!r} [{acts}]{n} ({dt:.1f}s)")
        else:
            rows.append(f"agent {text!r}: HTTP {s} {r}")
    boundary = "demoops" + uuid.uuid4().hex[:8]
    parts = [
        (f'--{boundary}\r\nContent-Disposition: form-data; name="audio"; filename="c.wav"\r\n'
         "Content-Type: audio/wav\r\n\r\n").encode() + tiny_wav() + b"\r\n",
        f'--{boundary}\r\nContent-Disposition: form-data; name="session_id"\r\n\r\n{session}\r\n'.encode(),
        f'--{boundary}\r\nContent-Disposition: form-data; name="context"\r\n\r\n{{}}\r\n'.encode(),
        f"--{boundary}--\r\n".encode(),
    ]
    s, r, dt = call(base, "POST", "/voice/command", raw=b"".join(parts),
                    headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    rows.append(f"voice/command (0.5 s silence): HTTP {s} {r if s != 200 else ''} ({dt:.1f}s)")
    if first_part:
        s, r, dt = call(base, "POST", "/checkout", {"part_id": first_part, "seller_idx": 0, "qty": 1, "session_id": session})
        if s == 200 and isinstance(r, dict):
            rows.append(f"checkout {first_part} x1: {r.get('status')} mode={r.get('mode')} ${r.get('total_usd')} label={r.get('label')!r}")
        else:
            rows.append(f"checkout: HTTP {s} {r}")
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--base", default="http://127.0.0.1:8000")
    ap.add_argument("--queries", default=",".join(CHIPS), help="comma-separated (default: the six chips)")
    ap.add_argument("--tapes", default="none", help="none | demo | none,demo (demo = DEMO_TAPES per chip)")
    ap.add_argument("--sellers", action="store_true", help="POST /parts/{first}/sellers?sort=cheapest (may spend SerpApi on a live server)")
    ap.add_argument("--extras", action="store_true", help="agent text, voice 503, one checkout (see docstring)")
    ap.add_argument("--pace", type=float, default=0.0, help="seconds between searches (>= 25 on a live Groq server)")
    ap.add_argument("--timeout", type=float, default=60.0, help="job poll timeout, s (the app gives up at 45)")
    args = ap.parse_args()

    s, h, _ = call(args.base, "GET", "/health", timeout=5)
    if s != 200:
        sys.exit(f"{args.base}/health: {s} {h}")
    queries = [q.strip() for q in args.queries.split(",") if q.strip()]
    modes = [m.strip() for m in args.tapes.split(",")]
    print(f"{args.base}  {h}")
    print(f"{'query':15} {'tape':12} {'result':9} {'secs':>5}  {'n':>1}  first candidate (fit)                         files                    sellers")
    hits, misses, first_parts, n_tape, last = 0, 0, {}, 0, None
    for q in queries:
        for mode in modes:
            tape = DEMO_TAPES.get(q) if mode == "demo" else None
            if mode == "demo" and tape is None:
                continue
            if last is not None and args.pace:
                time.sleep(max(0.0, args.pace - (time.monotonic() - last)))
            n_tape += 1
            r = search(args.base, q, tape, n_tape, args.timeout)
            last = time.monotonic()
            hit = r["state"] == "done" and r["cands"]
            hits, misses = hits + bool(hit), misses + (not hit)
            tape_s = f"{tape[1]} {tape[0]:g} m" if tape else "-"
            result = ("HIT" if hit else "MISS") + (" cached" if r.get("instant") else "")
            if not hit:
                print(f"{q:15} {tape_s:12} {result:9} {r['secs']:5.1f}  0  {r['state']}: {r.get('summary') or r.get('err') or ''}")
                continue
            c = r["cands"][0]
            fit = c.get("fit") or {}
            spare = fit.get("spare_mm")
            fit_s = f"{fit.get('status')}{'' if spare is None else f' {spare:+.0f}mm'}"
            first_parts.setdefault(q, c["id"])
            f = files(args.base, c["id"])
            sl = sellers(args.base, c["id"]) if args.sellers else ""
            print(f"{q:15} {tape_s:12} {result:9} {r['secs']:5.1f}  {len(r['cands'])}  {c['id'][:34]:34} ({fit_s:12}) {f:24} {sl}")
            if r.get("summary"):
                print(f"{'':15} {'':12} {'':9} {'':5}     “{r['summary']}”")
    if args.extras:
        print("\nextras:")
        for row in extras(args.base, first_parts.get("cabinet hinge") or next(iter(first_parts.values()), None)):
            print("  " + row)
    print(f"\n{hits} hit(s), {misses} miss(es)")
    sys.exit(1 if misses else 0)


if __name__ == "__main__":
    main()
