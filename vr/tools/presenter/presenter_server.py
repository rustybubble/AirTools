#!/usr/bin/env python3
"""AirTools presenter relay (UX W1.8, decision D7). Python standard library only, like tools/mock_parts_server.py.

The laptop's presenter page queues commands for the headset: Reset, Hint, Skip to a beat of the judge's three
minutes (docs/ux/README.md §2.7), Enter / Exit world, DemoMode on / off. The headset polls for them about once a
second (Runtime/Dev/PresenterLink.cs), runs them and acknowledges each, and posts its status, which the page shows.

It never pays: any command or argument about paying, checkout or the hold is refused with 403, here and again in
PresenterLink. The payment is the judge's own 1 s hold on Pay.

This is a separate server on its own port (default 8766). The laptop's :8000 backend belongs to another team (P4).
The headset reaches it over the USB tunnel (adb reverse tcp:8766 tcp:8766, http://127.0.0.1:8766) or, on the hotspot,
at the laptop's IP; PresenterLink uses the parts server's host with this port unless told otherwise.

  GET  /  |  /presenter        the presenter page (tools/presenter/presenter.html)
  GET  /health                 {"ok": true, "service": "airtools-presenter", "version": 1}
  GET  /presenter/commands     the command catalogue: commands, beats (with their §2.7 times), judge-only beats
  POST /presenter/cmd          {"cmd", "arg"?} -> 200 {"ok", "command"} (a same command already queued is reused) |
                               400 unknown command / argument | 403 refused (pay / checkout / hold …)
  GET  /presenter/next?max=4   the headset takes queued commands -> {"commands": [{"id","cmd","arg","age_s"}]}; commands
                               older than --ttl seconds expire instead (a stale "Enter world" never fires later)
  POST /presenter/ack          {"id", "ok", "detail"} from the headset after running one
  POST /state                  the headset's status (any JSON object ≤ 64 KB). Shared endpoint: the future big-screen
  GET  /state                  view (ideas P10 "Everyone watches") reads and writes the same /state
                               -> {"headset": <last status>, "headset_age_s", "online", "polling", "queue", "commands", …}

Every command and ack is logged to stdout and tools/.presenter_data/commands.log.
Run:  python3 tools/presenter/presenter_server.py [--port 8766] [--host 0.0.0.0] [--ttl 20]
Tests: python3 -m unittest tools/presenter/test_presenter_server.py
"""
import argparse, itertools, json, os, re, threading, time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
PAGE = os.path.join(HERE, "presenter.html")
DATA_DIR = os.path.join(ROOT, "tools", ".presenter_data")
VERSION = 1
MAX_BODY = 64 * 1024

# The judge's three minutes (docs/ux/README.md §2.7, kitchen). Mirrored in Runtime/Dev/PresenterCommands.cs (Beats);
# test_presenter_server.py checks the two lists agree.
BEATS = [
    {"id": "measure", "t": "0:20", "label": "Door measured", "does": "tapes the door's top edge with the real tool"},
    {"id": "find", "t": "0:40", "label": "Hinges found", "does": "searches parts for the latest tape"},
    {"id": "take", "t": "0:55", "label": "Hinge in hand", "does": "takes the top pick into the hand"},
    {"id": "place", "t": "1:05", "label": "Placed on the door", "does": "places the part in hand on the door"},
    {"id": "sellers", "t": "1:35", "label": "Compare prices", "does": "opens the seller list; the judge taps Pay with Visa"},
    {"id": "home", "t": "2:10", "label": "Take it home", "does": "leaves the world; bought parts land on the table"},
    {"id": "report", "t": "2:30", "label": "Send the report", "does": "exports the notebook to the laptop"},
    # e2e (docs/demo-prompts.md): after the script. Every step is a phrase through the headset's command path.
    {"id": "replace", "t": "3:00", "label": "Replace the dishwasher",
     "does": "takes it out, measures the gap, finds ones that fit, puts the best in, switches 3 models, puts it back"},
]
# Shown on the page, never sent: only the judge does these.
JUDGE_ONLY = [
    {"t": "1:40", "label": "Pay with Visa", "why": "opens the checkout: the judge's tap"},
    {"t": "1:50", "label": "Hold 1 s to pay", "why": "the payment is the judge's own hold, never the presenter's"},
]
COMMANDS = {
    "reset": {"label": "Reset demo", "args": None},
    "hint": {"label": "Hint", "args": None},
    "enter": {"label": "Enter world", "args": None},
    "exit": {"label": "Exit world", "args": None},
    "beat": {"label": "Skip to beat", "args": [b["id"] for b in BEATS]},
    "demo": {"label": "DemoMode", "args": ["on", "off"]},
    "ping": {"label": "Ping", "args": None},
}
# Refused wherever it appears in a command or its argument (a word start: "pay", "payment", "checkout", "hold"…).
# Mirrored in PresenterCommands.RefusedPattern.
REFUSED = re.compile(r"(^|[^a-z])(pay|checkout|check[ _-]?out|hold|buy|purchase|order|charge|visa|receipt)", re.I)
REFUSAL = "The presenter never pays: paying is the judge's own 1-second hold on Pay"


def refused(*texts):
    return any(t and REFUSED.search(str(t)) for t in texts)


def validate(cmd, arg):
    """(status, error) for a presenter command: (200, None) when it may be queued."""
    if refused(cmd, arg):
        return 403, REFUSAL
    spec = COMMANDS.get(cmd)
    if spec is None:
        return 400, f"unknown command {cmd!r} (one of {', '.join(COMMANDS)})"
    allowed = spec["args"]
    if allowed is None:
        return (200, None) if arg in (None, "") else (400, f"{cmd} takes no argument")
    if arg not in allowed:
        return 400, f"{cmd} needs one of {', '.join(allowed)}"
    return 200, None


class Relay:
    """The command queue and the headset's last status (thread-safe)."""

    def __init__(self, ttl=20.0, history=60, log_path=None):
        self.ttl = ttl
        self.lock = threading.Lock()
        self.pending = deque()
        self.history = deque(maxlen=history)
        self.by_id = {}
        self.ids = itertools.count(1)
        self.headset = None
        self.headset_at = 0.0
        self.last_poll_at = 0.0
        self.polls = 0
        self.log_path = log_path

    def log(self, line):
        stamp = time.strftime("%H:%M:%S")
        print(f"[presenter {stamp}] {line}", flush=True)
        if self.log_path:
            try:
                os.makedirs(os.path.dirname(self.log_path), exist_ok=True)
                with open(self.log_path, "a", encoding="utf-8") as f:
                    f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {line}\n")
            except OSError:
                pass

    def _expire(self, now):
        while self.pending and now - self.pending[0]["queued_at"] > self.ttl:
            c = self.pending.popleft()
            c["status"] = "expired"
            self.log(f"expired {c['id']} {c['cmd']} {c['arg'] or ''} (headset didn't take it within {self.ttl:.0f} s)")

    def enqueue(self, cmd, arg, source="page"):
        now = time.time()
        with self.lock:
            self._expire(now)
            for c in self.pending:
                if c["cmd"] == cmd and c["arg"] == arg:
                    return c, True
            c = {"id": f"c{next(self.ids)}", "cmd": cmd, "arg": arg, "status": "queued", "source": source,
                 "queued_at": now, "delivered_at": None, "acked_at": None, "ok": None, "detail": ""}
            self.pending.append(c)
            self.history.append(c)
            self.by_id[c["id"]] = c
            if len(self.by_id) > 4 * self.history.maxlen:
                keep = {x["id"] for x in self.history}
                self.by_id = {k: v for k, v in self.by_id.items() if k in keep}
        self.log(f"queued {c['id']} {cmd} {arg or ''} (from {source})")
        return c, False

    def take(self, max_n=4):
        now = time.time()
        out = []
        with self.lock:
            self.polls += 1
            self.last_poll_at = now
            self._expire(now)
            while self.pending and len(out) < max_n:
                c = self.pending.popleft()
                c["status"] = "delivered"
                c["delivered_at"] = now
                out.append({"id": c["id"], "cmd": c["cmd"], "arg": c["arg"], "age_s": round(now - c["queued_at"], 2)})
        for c in out:
            self.log(f"delivered {c['id']} {c['cmd']} {c['arg'] or ''}")
        return out

    def ack(self, cid, ok, detail):
        with self.lock:
            c = self.by_id.get(cid)
            if c is None:
                return None
            c["status"] = "ok" if ok else "failed"
            c["ok"] = bool(ok)
            c["detail"] = str(detail or "")[:500]
            c["acked_at"] = time.time()
        self.log(f"ack {cid} {c['cmd']} {c['arg'] or ''}: {'ok' if ok else 'FAILED'} {c['detail']}")
        return c

    def put_state(self, obj):
        with self.lock:
            self.headset = obj
            self.headset_at = time.time()

    @staticmethod
    def _public(c, now):
        d = {k: c[k] for k in ("id", "cmd", "arg", "status", "source", "ok", "detail")}
        d["age_s"] = round(now - c["queued_at"], 1)
        return d

    def snapshot(self):
        now = time.time()
        with self.lock:
            self._expire(now)
            age = now - self.headset_at if self.headset is not None else None
            poll_age = now - self.last_poll_at if self.last_poll_at else None
            return {
                "ok": True,
                "server": {"service": "airtools-presenter", "version": VERSION, "ttl_s": self.ttl, "polls": self.polls},
                "headset": self.headset,
                "headset_age_s": None if age is None else round(age, 1),
                "online": age is not None and age < 4.0,
                "polling": poll_age is not None and poll_age < 4.0,
                "queue": [self._public(c, now) for c in self.pending],
                "commands": [self._public(c, now) for c in reversed(self.history)][:20],
            }


def catalogue():
    return {"commands": COMMANDS, "beats": BEATS, "judge_only": JUDGE_ONLY, "refusal": REFUSAL, "version": VERSION}


class Handler(BaseHTTPRequestHandler):
    relay: Relay = None
    server_version = "AirToolsPresenter/1"

    def log_message(self, fmt, *args):  # quiet: the relay logs what matters
        pass

    def send_json(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def read_json(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n > MAX_BODY:
            self.rfile.read(n)
            return None, (413, f"body over {MAX_BODY} bytes")
        raw = self.rfile.read(n) if n else b""
        try:
            obj = json.loads(raw.decode("utf-8") or "{}")
        except (UnicodeDecodeError, json.JSONDecodeError):
            return None, (400, "body must be JSON")
        if not isinstance(obj, dict):
            return None, (400, "body must be a JSON object")
        return obj, None

    def do_GET(self):
        u = urlparse(self.path)
        if u.path in ("/", "/presenter", "/presenter/"):
            try:
                with open(PAGE, "rb") as f:
                    body = f.read()
            except OSError:
                return self.send_json(500, {"error": "presenter.html missing"})
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            return self.wfile.write(body)
        if u.path == "/health":
            return self.send_json(200, {"ok": True, "service": "airtools-presenter", "version": VERSION})
        if u.path == "/presenter/commands":
            return self.send_json(200, catalogue())
        if u.path == "/presenter/next":
            q = parse_qs(u.query)
            try:
                max_n = max(1, min(16, int((q.get("max") or ["4"])[0])))
            except ValueError:
                max_n = 4
            return self.send_json(200, {"commands": self.relay.take(max_n)})
        if u.path == "/state":
            return self.send_json(200, self.relay.snapshot())
        return self.send_json(404, {"error": f"no route {u.path}"})

    def do_POST(self):
        u = urlparse(self.path)
        obj, err = self.read_json()
        if err:
            return self.send_json(err[0], {"error": err[1]})
        if u.path == "/presenter/cmd":
            cmd = str(obj.get("cmd") or "").strip().lower()
            arg = obj.get("arg")
            arg = None if arg in (None, "") else str(arg).strip().lower()
            code, error = validate(cmd, arg)
            if code != 200:
                if code == 403:
                    self.relay.log(f"REFUSED {cmd} {arg or ''}: never pays")
                return self.send_json(code, {"ok": False, "error": "refused" if code == 403 else "bad command", "detail": error})
            c, reused = self.relay.enqueue(cmd, arg, str(obj.get("source") or "page")[:20])
            return self.send_json(200, {"ok": True, "reused": reused, "command": Relay._public(c, time.time())})
        if u.path == "/presenter/ack":
            c = self.relay.ack(str(obj.get("id") or ""), bool(obj.get("ok")), obj.get("detail"))
            if c is None:
                return self.send_json(404, {"ok": False, "error": "unknown command id"})
            return self.send_json(200, {"ok": True})
        if u.path == "/state":
            self.relay.put_state(obj)
            return self.send_json(200, {"ok": True})
        return self.send_json(404, {"error": f"no route {u.path}"})


def serve(port=8766, host="0.0.0.0", ttl=20.0, log_path=os.path.join(DATA_DIR, "commands.log")):
    relay = Relay(ttl=ttl, log_path=log_path)
    handler = type("BoundHandler", (Handler,), {"relay": relay})
    httpd = ThreadingHTTPServer((host, port), handler)
    httpd.daemon_threads = True
    relay.log(f"presenter relay on {host}:{httpd.server_address[1]} (page: http://localhost:{httpd.server_address[1]}/) ttl={ttl:.0f}s")
    return httpd, relay


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--port", type=int, default=8766)
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--ttl", type=float, default=20.0, help="seconds a queued command waits for the headset before it expires")
    a = ap.parse_args()
    httpd, _ = serve(a.port, a.host, a.ttl)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
