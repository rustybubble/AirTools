#!/usr/bin/env python3
"""Tests for tools/presenter/presenter_server.py (UX W1.8 / D7): the queue round trip, the refusals (never pays), expiry,
the shared /state endpoint, and that the command lists match the headset side (Runtime/Dev/PresenterCommands.cs).
Run: python3 -m unittest tools/presenter/test_presenter_server.py -v"""
import json, os, re, sys, threading, time, unittest, urllib.error, urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import presenter_server as ps  # noqa: E402

CS = os.path.join(ps.ROOT, "Assets", "AirTools", "Runtime", "Dev", "PresenterCommands.cs")
LINK = os.path.join(ps.ROOT, "Assets", "AirTools", "Runtime", "Dev", "PresenterLink.cs")


class Server:
    def __init__(self, ttl=20.0):
        self.httpd, self.relay = ps.serve(port=0, host="127.0.0.1", ttl=ttl, log_path=None)
        self.base = f"http://127.0.0.1:{self.httpd.server_address[1]}"
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()

    def get(self, path):
        try:
            with urllib.request.urlopen(self.base + path, timeout=5) as r:
                return r.status, r.headers, r.read()
        except urllib.error.HTTPError as e:
            return e.code, e.headers, e.read()

    def get_json(self, path):
        code, _, body = self.get(path)
        return code, json.loads(body)

    def post(self, path, obj, raw=None):
        data = raw if raw is not None else json.dumps(obj).encode()
        req = urllib.request.Request(self.base + path, data=data, method="POST", headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())


class PresenterServerTests(unittest.TestCase):
    def setUp(self):
        self.s = Server()

    def tearDown(self):
        self.s.close()

    # ---- basics

    def test_health_page_and_catalogue(self):
        code, body = self.s.get_json("/health")
        self.assertEqual((code, body["ok"], body["service"]), (200, True, "airtools-presenter"))
        for path in ("/", "/presenter"):
            code, headers, page = self.s.get(path)
            self.assertEqual(code, 200)
            self.assertIn("text/html", headers["Content-Type"])
            self.assertIn(b"AirTools presenter", page)
            self.assertIn(b"Never pays", page)
        code, cat = self.s.get_json("/presenter/commands")
        self.assertEqual(code, 200)
        self.assertEqual(set(cat["commands"]), {"reset", "hint", "enter", "exit", "beat", "demo", "ping"})
        self.assertEqual([b["id"] for b in cat["beats"]], ["measure", "find", "take", "place", "sellers", "home", "report", "replace"])
        self.assertTrue(all(re.match(r"^\d:\d\d$", b["t"]) for b in cat["beats"] + cat["judge_only"]))
        self.assertEqual(self.s.get("/nope")[0], 404)

    # ---- the round trip: page → queue → headset → ack → page

    def test_command_round_trip(self):
        code, r = self.s.post("/presenter/cmd", {"cmd": "reset"})
        self.assertEqual(code, 200)
        self.assertFalse(r["reused"])
        cid = r["command"]["id"]
        self.assertEqual(r["command"]["status"], "queued")
        _, st = self.s.get_json("/state")
        self.assertEqual([c["id"] for c in st["queue"]], [cid])
        self.assertFalse(st["online"])

        code, nxt = self.s.get_json("/presenter/next")
        self.assertEqual(code, 200)
        self.assertEqual([(c["id"], c["cmd"], c["arg"]) for c in nxt["commands"]], [(cid, "reset", None)])
        self.assertEqual(self.s.get_json("/presenter/next")[1]["commands"], [], "delivered once")

        self.assertEqual(self.s.post("/presenter/ack", {"id": cid, "ok": True, "detail": "reset #1: clean"})[0], 200)
        self.assertEqual(self.s.post("/state", {"v": 1, "mode": "Passthrough", "rule": "R05", "demo": True})[0], 200)
        _, st = self.s.get_json("/state")
        self.assertTrue(st["online"])
        self.assertTrue(st["polling"])
        self.assertEqual(st["headset"]["rule"], "R05")
        self.assertEqual(st["queue"], [])
        c = st["commands"][0]
        self.assertEqual((c["id"], c["status"], c["ok"], c["detail"]), (cid, "ok", True, "reset #1: clean"))

    def test_beats_and_args(self):
        for beat in [b["id"] for b in ps.BEATS]:
            code, r = self.s.post("/presenter/cmd", {"cmd": "beat", "arg": beat})
            self.assertEqual(code, 200, beat)
        self.assertEqual(self.s.post("/presenter/cmd", {"cmd": "beat", "arg": "fly"})[0], 400)
        self.assertEqual(self.s.post("/presenter/cmd", {"cmd": "beat"})[0], 400)
        self.assertEqual(self.s.post("/presenter/cmd", {"cmd": "demo", "arg": "ON"})[0], 200, "args are case-insensitive")
        self.assertEqual(self.s.post("/presenter/cmd", {"cmd": "demo", "arg": "maybe"})[0], 400)
        self.assertEqual(self.s.post("/presenter/cmd", {"cmd": "hint", "arg": "x"})[0], 400, "hint takes no argument")
        self.assertEqual(self.s.post("/presenter/cmd", {"cmd": "teleport"})[0], 400)
        _, nxt = self.s.get_json("/presenter/next?max=16")
        self.assertEqual(len(nxt["commands"]), len(ps.BEATS) + 1)
        self.assertEqual(nxt["commands"][-1], {**nxt["commands"][-1], "cmd": "demo", "arg": "on"})

    def test_same_command_queued_twice_is_reused(self):
        a = self.s.post("/presenter/cmd", {"cmd": "hint"})[1]
        b = self.s.post("/presenter/cmd", {"cmd": "hint"})[1]
        self.assertTrue(b["reused"])
        self.assertEqual(a["command"]["id"], b["command"]["id"])
        self.assertEqual(len(self.s.get_json("/presenter/next")[1]["commands"]), 1)

    def test_next_takes_at_most_max(self):
        for cmd in ("hint", "enter", "exit", "ping"):
            self.s.post("/presenter/cmd", {"cmd": cmd})
        self.assertEqual([c["cmd"] for c in self.s.get_json("/presenter/next?max=2")[1]["commands"]], ["hint", "enter"])
        self.assertEqual([c["cmd"] for c in self.s.get_json("/presenter/next")[1]["commands"]], ["exit", "ping"])

    def test_bad_requests(self):
        self.assertEqual(self.s.post("/presenter/cmd", None, raw=b"not json")[0], 400)
        self.assertEqual(self.s.post("/presenter/cmd", None, raw=b"[1, 2]")[0], 400)
        self.assertEqual(self.s.post("/state", None, raw=b"x" * (ps.MAX_BODY + 1))[0], 413)
        self.assertEqual(self.s.post("/presenter/ack", {"id": "c999", "ok": True})[0], 404)
        self.assertEqual(self.s.post("/elsewhere", {})[0], 404)

    # ---- never pays

    def test_pay_checkout_and_hold_are_refused(self):
        attempts = [
            {"cmd": "pay"}, {"cmd": "Pay"}, {"cmd": "payment"}, {"cmd": "checkout"}, {"cmd": "start_checkout"},
            {"cmd": "check-out"}, {"cmd": "hold"}, {"cmd": "hold_pay"}, {"cmd": "holdpay"}, {"cmd": "buy"}, {"cmd": "purchase"},
            {"cmd": "order"}, {"cmd": "charge"}, {"cmd": "visa"}, {"cmd": "receipt"}, {"cmd": "use_offline_receipt"},
            {"cmd": "beat", "arg": "pay"}, {"cmd": "beat", "arg": "checkout"}, {"cmd": "beat", "arg": "hold"},
            {"cmd": "demo", "arg": "pay"}, {"cmd": "reset", "arg": "then pay"},
        ]
        for a in attempts:
            code, r = self.s.post("/presenter/cmd", a)
            self.assertEqual(code, 403, a)
            self.assertEqual(r["error"], "refused")
            self.assertIn("never pays", r["detail"])
        _, st = self.s.get_json("/state")
        self.assertEqual(st["queue"], [], "nothing refused was queued")
        self.assertEqual(self.s.get_json("/presenter/next")[1]["commands"], [])

    def test_refusal_pattern_does_not_catch_the_allowed_commands(self):
        for cmd, spec in ps.COMMANDS.items():
            self.assertFalse(ps.refused(cmd), cmd)
            for arg in spec["args"] or []:
                self.assertFalse(ps.refused(arg), arg)
        self.assertFalse(ps.refused("display"), "a word start, not any substring")

    # ---- expiry: a stale command never fires late

    def test_commands_expire(self):
        self.s.close()
        self.s = Server(ttl=0.2)
        self.s.post("/presenter/cmd", {"cmd": "enter"})
        time.sleep(0.35)
        self.assertEqual(self.s.get_json("/presenter/next")[1]["commands"], [])
        _, st = self.s.get_json("/state")
        self.assertEqual(st["commands"][0]["status"], "expired")

    def test_state_is_shared_and_goes_stale(self):
        self.s.post("/state", {"source": "spectator", "note": "any JSON object"})
        _, st = self.s.get_json("/state")
        self.assertEqual(st["headset"]["note"], "any JSON object")
        self.s.relay.headset_at -= 10
        _, st = self.s.get_json("/state")
        self.assertFalse(st["online"])
        self.assertGreaterEqual(st["headset_age_s"], 10)

    # ---- the headset side agrees

    def test_headset_side_lists_the_same_commands_and_beats(self):
        with open(CS, encoding="utf-8") as f:
            cs = f.read()
        for cmd in ps.COMMANDS:
            self.assertIn(f'"{cmd}"', cs, f"PresenterCommands.cs lacks command {cmd}")
        beats = re.search(r"Beats\s*=\s*\{([^}]*)\}", cs)
        self.assertIsNotNone(beats, "PresenterCommands.Beats")
        self.assertEqual(re.findall(r'"([a-z]+)"', beats.group(1)), [b["id"] for b in ps.BEATS])
        for word in ("pay", "checkout", "hold", "buy", "purchase", "order", "charge", "visa", "receipt"):
            self.assertIn(word, cs.split("RefusedPattern", 1)[1].split(";", 1)[0], f"RefusedPattern lacks {word}")

    def test_headset_link_never_names_a_payment_call(self):
        with open(LINK, encoding="utf-8") as f:
            src = f.read()
        for call in ("StartCheckout", "CheckoutPanel", "HoldToConfirm", "HoldPay", "PayRow", "UseOfflineReceipt", ".Checkout(", "Simulate("):
            self.assertNotIn(call, src, call)


if __name__ == "__main__":
    unittest.main()
