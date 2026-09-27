"""Tests for tools/demo/hcheck.py: synthetic logcat snippets, plus a smoke run over real headset logs if present.

    python3 -m unittest tools/demo/test_hcheck.py

Real logs: $HCHECK_LOGS (os.pathsep-separated globs), else SpikeData/quest*.log in this repo and the usual checkouts
(~/AirTools/SpikeData/quest-*.log, ~/AirTools-backend/SpikeData/quest-backend.log). SpikeData is git-ignored.
"""
import glob
import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hcheck  # noqa: E402

APP, SYS = 4242, 3408
T0 = 14 * 3600  # 14:00:00 on 09-26


def stamp(sec, day="09-26"):
    ms = int(round((T0 + sec) * 1000))
    s, ms = divmod(ms, 1000)
    return f"{day} {s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}.{ms:03d}"


def u(sec, msg, pid=APP, lvl="I"):
    return f"{stamp(sec)} {lvl}/Unity   ({pid:5d}): [AirTools] {msg}"


def chk(sec, msg, pid=APP):
    return f"{stamp(sec)} I/Unity   ({pid:5d}): [AirTools.Check] {msg}"


def vr(sec, fps, hz=72, gpu=0.6, pid=APP, app=9.5):
    return (f"{stamp(sec)} I/VrApi   ({pid:5d}): FPS={fps}/{hz},Prd=36ms,Tear=0,Early=0,Stale=0,VSnc=1,Lat=-4,Fov=0,"
            f"CPU4/GPU=3/2,TW=1.00ms,App={app:.2f}ms,GD=0.00ms,CPU&GPU=7.00ms,LCnt=4(DR0,LM0),GPU%={gpu:.2f},CPU%=0.30(W0.35),")


def ctx_of(lines, since=None, until=None):
    parsed = hcheck.parse_text("\n".join(lines))
    c = hcheck.Ctx(parsed,
                   hcheck.resolve_when(since, parsed) if since else None,
                   hcheck.resolve_when(until, parsed) if until else None)
    return c, {ch.id: r for ch, r in hcheck.evaluate(c)}


def session(end=300):
    """A complete, clean session on the current build (tool ring), in the order of docs/headset-checklist.md."""
    ev = [
        (0.0, "ServerConfig: server override http://172.20.10.2:8000, site kitchen"),
        (0.5, "SceneLoader: loaded 'synthetic-facade'"),
        (0.6, "SceneStreamer: 6 models known on this headset (2 downloaded): kitchen, zabel-gymnasium, hospital-bg, gt-lcc-canopy, gt-lcc-pavilion, gt-lcc-tower"),
        (1.5, "SceneStreamer: kitchen r1 at its default scale ×1.63 (SiteScales; a tape + Set scale overrides it)"),
        (1.6, "SceneStreamer: spawn at (-3.30, -1.18, 0.22) yaw 94°"),
        (1.6, "SceneStreamer: kitchen · kitchen r1 full · scale altitude ±5 cm · structure 61p/671e/594c/40o in 1.0 s (collision mesh, 165 cameras)"),
        (10.0, "World button pressed (#1) in Passthrough"), (10.0, "AppState Passthrough -> World"),
        (10.001, "Measure tool equipped"), (10.002, "Toast: Measure: pinch one corner, then the other"),
        # D1 (Sat 09-26): the world opens with Measure; the judge switches to Move to walk.
        (12.0, "Palm menu open (hand)"), (13.0, "Move tool equipped"), (13.0, "Measure tool put away"),
        (13.0, "Ring: equipped Move"), (14.0, "Palm menu closed"),
        (15.0, "Locomotion: teleported to (1.00, 0.00, 2.00)"),
        (20.0, "Palm menu open (hand)"), (23.0, "Measure tool equipped"), (23.0, "Move tool put away"),
        (23.0, "Ring: equipped Measure"), (26.0, "Palm menu closed"),
        (30.0, "Notebook #1 measure: Distance 0.41 m · 1′ 4⅛″"), (30.0, "Toast: Saved · Distance 0.41 m · 1′ 4⅛″"),
        (40.0, "Done gesture (Right)"), (40.01, "Notebook #2 measure: Triangle 0.05 m² · 0.5 ft² · sides 0.410, 0.300, 0.350 m · angles 45.0, 60.0, 75.0°"),
        (50.0, "Notebook #3 measure: Quad 0.12 m² · 1.3 ft² · sides 0.410, 0.300, 0.410, 0.300 m · angles 90.0, 90.0, 90.0, 90.0°"),
        (55.0, "Palm menu open (hand)"), (57.0, "Notebook #3 restored: Quad 0.12 m²"), (59.0, "Palm menu closed"),
        (61.0, "Double pinch (Left): back to the default mode"), (61.0, "Measure tool put away"), (61.0, "Move tool equipped"), (61.0, "Toast: Move"),
        (65.0, "Palm menu open (hand)"), (67.0, "Menu: ToggleScenePanel"), (67.0, "Ring: ran ToggleScenePanel"), (68.0, "Palm menu closed"),
        (72.0, "Notebook #1 updated: Distance 0.61 m · 2′ 0″"),
        (72.0, "Scene scale ×1.4910 → calibration 1.4910 scene m per package unit"),
        (74.0, "Menu: ToggleContrast"), (75.0, "Menu: ToggleContrast"),
        (76.0, "Button UnitsChip (poke, hand)"), (76.0, "Units: m (Metric)"), (77.0, "Units: ft·in (Imperial)"),
        (80.0, 'Scene ask "What is this?" → "A dishwasher" frame  box [-] query ""'),
        (80.01, "Scene pin #1 from frame focus at package (0.100, 0.200, 0.300): A dishwasher"),
        (89.99, "Voice utterance mode=tap stop=end-of-speech press=0.18s speech=1.42s trailing=1.00s preroll=0.40s kept=1.72s total=3.10s bytes=55084 floor=-52dB peak=-14dB"),
        (90.0, "Voice: Thinking…"),
        (91.8, 'Agent: "switch to the level" → "Level on." actions [equip_tool {"tool":"level"}]'),
        (91.81, "Toast: Level on."), (91.82, "Move tool put away"), (91.82, "Level tool equipped"),
        (91.83, 'Agent action equip_tool {"tool":"level"} → ok'), (91.9, "Voice: “switch to the level” → Level on."),
        (100.0, "Notebook #4 level: Level 0.4° (0.7% slope)"),
        (110.0, "Palm menu open (hand)"), (112.0, "Menu: ToggleNotebook"), (112.0, "Ring: ran ToggleNotebook"), (113.0, "Palm menu closed"),
        (118.0, "Notebook exported: /sdcard/n.csv | /sdcard/n.html"),
        (118.5, 'Notebook POST http://172.20.10.2:8000/notebook → {"ok":true}'),
        (125.0, 'Parts search "cabinet hinge": 3 candidates from server'),
        (130.0, "Part hinge-1 loaded from server: 35 × 50 × 12 mm"), (130.0, "Level tool put away"),
        (130.0, "Part tool equipped"), (130.0, "Part hinge-1 in hand (server)"),
        (140.0, "Notebook #5 part: Hinge: Fits, 253 mm spare"),
        (140.0, "Part placed hinge-1 on Collision r1/geometry_0 (ray): Green: Fits, 253 mm spare"),
        (150.0, "Part placed hinge-1 on Collision r1/geometry_0 (ray): Green: Fits, 250 mm spare"),
        (155.0, "Palm menu open (hand)"), (156.0, "Part hinge-1: finish brown #5A3E2B"), (158.0, "Palm menu closed"),
        (170.0, "Notebook #6 measure: Distance 1.80 m · 5′ 10⅞″"),
        (175.0, 'Parts search "gutter hanger": 3 candidates from server'),
        (176.0, "Part hanger-1 loaded from server: 127 × 38 × 45 mm"), (176.0, "Part hanger-1 in hand (server)"),
        (180.0, "Part placed hanger-1 on Collision r1/geometry_0 (ray): Green: Fits"),
        (185.0, "Notebook #7 array: 4 × hanger every 600 mm"),
        (185.0, "Part array: 4 × hanger-1 every 600 mm along 1.800 m, 4 green"),
        (190.0, "Sellers for hanger-1, by price: 0, 1, 2"),
        (191.0, "Sellers for hanger-1 re-ranked by the server (price): 3, recommended 0"),
        (195.0, "Sellers for hanger-1, by eta: 1, 0, 2"),
        (200.0, "Checkout opened: hanger-1 from Hardware Tree × 4"),
        (205.0, "Hold confirmed"), (205.01, "Checkout request: hanger-1 seller 0 × 1"),
        (206.0, "Notebook #8 purchase: Ordered (offline) 1 × Hanger from Hardware Tree — $7.59"),
        (206.0, "Toast: Offline receipt · $7.59 · no payment"), (206.0, "Checkout authorized: OFF-123 7.59 USD"),
        (210.0, "Palm menu open (hand)"), (214.0, "AppState World -> Passthrough"), (214.0, "Menu: ToggleWorld"),
        (214.0, "Ring: ran ToggleWorld"), (214.4, "Tabletop off (1:1)"), (214.5, "Palm menu closed"),
        (214.6, "Take it home: 1 part(s) on the table"), (214.6, "Toast: Your parts are on the table"),
        (220.0, "World button pressed (#2) in Passthrough"), (220.0, "AppState Passthrough -> World"),
        (225.0, "Palm menu open (hand)"), (227.0, "AppState World -> Tabletop"), (227.0, "Menu: ToggleTabletop"),
        (227.0, "Ring: ran ToggleTabletop"), (227.4, "Tabletop on (1:50)"), (228.0, "Palm menu closed"),
        (235.0, "Notebook #9 measure: Distance 1.79 m · 5′ 10½″"),
        (240.0, "Palm menu open (hand)"), (242.0, "AppState Tabletop -> World"), (242.0, "Menu: ToggleTabletop"),
        (242.0, "Ring: ran ToggleTabletop"), (242.4, "Tabletop off (1:1)"), (243.0, "Palm menu closed"),
        (250.0, "Palm menu open (hand)"), (251.0, "Locomotion: home"), (251.0, "Menu: Home"), (251.0, "Ring: ran Home"),
        (262.0, "Palm menu closed"),
        (end - 1, "Open at seller: https://example.com/hanger"),
    ]
    lines = [u(t, m) for t, m in ev]
    lines += [chk(10.9, "M1.mode.transition to=World seconds=0.93 budget=0.90 PASS"),
              chk(72.0, "scale.known_dimension tape now 0.6096 m, real 0.6096 m PASS"),
              chk(214.9, "M1.mode.transition to=Passthrough seconds=0.93 budget=0.90 PASS"),
              chk(220.9, "M1.mode.transition to=World seconds=0.92 budget=0.90 PASS"),
              chk(227.9, "M1.mode.transition to=Tabletop seconds=0.93 budget=0.90 PASS"),
              chk(242.9, "M1.mode.transition to=World seconds=0.93 budget=0.90 PASS")]
    lines += [vr(s, 73) for s in range(1, end)]
    lines += [vr(s + 0.5, 30, hz=90, pid=SYS) for s in range(1, end)]  # the system shell: never counted
    return sorted(lines)


class Parsing(unittest.TestCase):
    def test_time_and_threadtime_formats(self):
        a = hcheck.parse_line("09-26 14:00:01.250 I/Unity   ( 4242): [AirTools] Palm menu open (hand)")
        b = hcheck.parse_line("09-26 14:00:01.250  4242  4260 I Unity   : [AirTools] Palm menu open (hand)")
        for ln in (a, b):
            self.assertIsNotNone(ln)
            self.assertEqual((ln.pid, ln.level, ln.tag, ln.msg), (4242, "I", "Unity", "[AirTools] Palm menu open (hand)"))
        self.assertEqual(a.t, b.t)
        self.assertEqual(a.text, "Palm menu open (hand)")
        self.assertIsNone(hcheck.parse_line("--------- beginning of main"))

    def test_since_takes_the_most_recent_occurrence(self):
        lines = hcheck.parse_text("\n".join([u(0, "a").replace("09-26 14:", "09-25 22:"), u(0, "b").replace("09-26 14:", "09-26 02:")]))
        late = hcheck.resolve_when("22:00", lines)
        early = hcheck.resolve_when("02:00", lines)
        self.assertLess(late, early)  # 22:00 is on 09-25, 02:00 on 09-26
        self.assertEqual(hcheck.resolve_when("09-26 02:00:00", lines), early)
        for bad in ("25:99", "noon", "12:61"):
            with self.assertRaises(ValueError):
                hcheck.resolve_when(bad, lines)

    def test_since_until_window(self):
        c, r = ctx_of(session(), since="14:02:00", until="14:03:30")  # 120 s … 210 s
        self.assertEqual(r["parts.search"].status, hcheck.PASS)
        self.assertEqual(r["measure.distance"].status, hcheck.PASS)       # #6 at 170 s
        self.assertEqual(r["ring.open"].status, hcheck.PASS)              # 155 s
        self.assertEqual(r["m1.entry"].status, hcheck.NOT_SEEN)           # 10 s: before the window
        self.assertEqual(r["home.table"].status, hcheck.NOT_SEEN)         # 214 s: after it
        self.assertEqual(c.mode[APP].at(c.since), "World")                # state before the window still known


class FullSession(unittest.TestCase):
    def test_every_logged_check_passes(self):
        c, r = ctx_of(session())
        bad = {k: (v.status, v.note) for k, v in r.items() if v.status not in (hcheck.PASS, hcheck.EYE)}
        self.assertEqual(bad, {})
        self.assertIn("LAN", hcheck.network_path(c))
        self.assertIn("implied", r["ring.undo_redo"].note)
        self.assertIn("picked up", r["parts.grab"].note)
        self.assertIn("#6 1.80 m", r["tabletop.tape"].note)
        self.assertIn("1.8 s", r["voice.latency"].evidence)
        self.assertIn("1 completed holds → 1 requests", r["checkout.gate"].note)

    def test_eye_checks_are_never_claimed(self):
        _, r = ctx_of(session())
        for ch in hcheck.CHECKS:
            if ch.kind == "eye":
                self.assertEqual(r[ch.id].status, hcheck.EYE, ch.id)

    def test_suggested_log_lines_upgrade_eye_checks(self):
        # The lines proposed in the checklist's appendix, once the app writes them.
        lines = session() + [u(24.0, "Ring: spin to Level"), u(29.0, "Measure point 1 (Corner)"),
                             u(9.9, "Button EnterWorld (poke, hand)"), u(16.0, "Locomotion: moved to (1.20, 0.00, 2.10)"),
                             u(112.5, "Notebook row 0 highlighted"),
                             chk(300.0, "M1.mode.transition to=World seconds=0.93 budget=0.90 max_frame_ms=31 PASS")]
        _, r = ctx_of(sorted(lines))
        self.assertEqual(r["ring.tap"].status, hcheck.PASS)
        self.assertEqual(r["measure.snap"].status, hcheck.PASS)
        self.assertIn("(poke, hand)", r["m1.entry"].evidence)
        self.assertIn("drag", r["move.teleport"].note)
        self.assertIn("row 0", r["notebook.open"].note)
        self.assertEqual(r["m1.fade"].status, hcheck.PASS)
        self.assertIn("longest frame in a fade 31 ms", r["m1.fade"].note)

    def test_fps_counts_only_the_app_and_skips_settling(self):
        c, r = ctx_of(session())
        self.assertTrue(all(s.pid == APP for s in c.samples))
        self.assertEqual(c.samples[0].skip, "resume")                    # the first sample of the run
        entry = [s for s in c.samples if 10 < s.t - hcheck.resolve_when("14:00:00", c.all) < 12]
        self.assertTrue(entry and all(s.skip == "settle" for s in entry))  # within 2 s of entering the world
        self.assertEqual(r["perf.world72"].status, hcheck.PASS)
        self.assertIn("kitchen", r["perf.world72"].evidence)
        self.assertIn("100.0 % at 72 Hz", r["perf.world72"].evidence)
        rows = dict(hcheck.perf_rows(c))
        self.assertEqual(rows["World · kitchen"]["median"], 73)
        self.assertEqual(rows["World · kitchen"]["gpu_median_pct"], 60)
        self.assertGreaterEqual(rows["World · menu open"]["samples"], hcheck.MIN_RING_SAMPLES)
        self.assertIn("Tabletop", rows)

    def test_report_and_json_render(self):
        c, r = ctx_of(session())
        results = hcheck.evaluate(c)
        text = hcheck.report("x.log", c, results)
        self.assertIn("perf.world72", text)
        self.assertIn("World · kitchen", text)
        self.assertIn("eye only, tick by hand:", text)
        j = hcheck.to_json("x.log", c, results)
        self.assertEqual(len(j["checks"]), len(hcheck.CHECKS))
        self.assertEqual(len({ch.id for ch in hcheck.CHECKS}), len(hcheck.CHECKS))  # ids are unique


class Failures(unittest.TestCase):
    def test_palm_menu_flicker_fails(self):
        lines = [u(1, "AppState Passthrough -> World")]
        t = 5.0
        for _ in range(4):  # the pre-fix kitchen bug: closes and reopens under the other hand
            lines += [u(t, "Palm menu open (hand)"), u(t + 1.0, "Palm menu closed")]
            t += 1.3
        _, r = ctx_of(lines)
        self.assertEqual(r["ring.stable"].status, hcheck.FAIL)
        self.assertIn("3 reopened", r["ring.stable"].note)

    def test_voice_over_budget_fails(self):
        lines = [u(10, "Voice: Thinking…"), u(13.4, 'Agent: "level" → "ok" actions [equip_tool {"tool":"level"}]'),
                 u(13.41, 'Agent action equip_tool {"tool":"level"} → ok')]
        _, r = ctx_of(lines)
        self.assertEqual(r["voice.talk"].status, hcheck.PASS)
        self.assertEqual(r["voice.latency"].status, hcheck.FAIL)
        self.assertIn("3.4 s", r["voice.latency"].evidence)

    def test_voice_offline_fails(self):
        _, r = ctx_of([u(10, "Voice: Thinking…"), u(11, "Voice: Voice is offline on the laptop (no speech-to-text) — use the buttons")])
        self.assertEqual(r["voice.talk"].status, hcheck.FAIL)
        self.assertEqual(r["voice.latency"].status, hcheck.NOT_SEEN)

    def test_voice_utterance_without_speech_fails(self):
        line = "Voice utterance mode=tap stop=no-speech press=0.12s speech=0.00s trailing=6.40s preroll=0.40s kept=0.00s total=6.40s bytes=0 floor=-48dB peak=-40dB"
        _, r = ctx_of([u(10, line)])
        self.assertEqual(r["voice.utterance"].status, hcheck.FAIL)
        _, r = ctx_of([u(10, line), u(20, line.replace("stop=no-speech", "stop=release").replace("bytes=0", "bytes=64044"))])
        self.assertEqual(r["voice.utterance"].status, hcheck.PASS)
        self.assertIn("tap/release", r["voice.utterance"].note)

    def test_checkout_without_hold_fails(self):
        _, r = ctx_of([u(10, "Checkout request: p seller 0 × 1")])
        self.assertEqual(r["checkout.gate"].status, hcheck.FAIL)
        _, r = ctx_of([u(9.5, "Hold confirmed"), u(10, "Checkout request: p seller 0 × 1"), u(20, "Checkout request: p seller 0 × 1")])
        self.assertEqual(r["checkout.gate"].status, hcheck.FAIL)  # one hold can't pay twice
        _, r = ctx_of([u(9.5, "Hold confirmed"), u(10, "Checkout request: p seller 0 × 1"), u(11, "Checkout failed: 500")])
        self.assertEqual(r["checkout.gate"].status, hcheck.PASS)
        self.assertEqual(r["checkout.hold"].status, hcheck.FAIL)

    def test_array_count(self):
        self.assertEqual(hcheck.array_quantity(4.2, 600), 8)
        self.assertEqual(hcheck.array_quantity(4.19, 600), 8)  # within the planner's 3 % tolerance
        self.assertEqual(hcheck.array_quantity(1.0, 600), 2)
        _, r = ctx_of([u(1, "Part array: 8 × h every 600 mm along 4.200 m, 8 green")])
        self.assertEqual(r["array.place"].status, hcheck.PASS)
        _, r = ctx_of([u(1, "Part array: 7 × h every 600 mm along 4.200 m, 7 green")])
        self.assertEqual(r["array.place"].status, hcheck.FAIL)
        _, r = ctx_of([u(1, "Toast: Cabinet hinge is a single unit (its listing has no spacing)")])
        self.assertEqual(r["array.place"].status, hcheck.NOT_SEEN)
        self.assertIn("single unit", r["array.place"].evidence)
        # UX W0.9 copy (docs/ux/specs/W0.9-copy.md F21–F23): the new refusal toasts are still evidence.
        for toast in ("No spacing listed · place each hinge by hand", "Place one hanger first, then fill the run",
                      "Measure the run first · pinch both ends"):
            _, r = ctx_of([u(1, f"Toast: {toast}")])
            self.assertEqual(r["array.place"].status, hcheck.NOT_SEEN)
            self.assertIn(toast, r["array.place"].evidence)

    def test_ux_copy_toasts_are_still_evidence(self):
        """UX W0.9 rewrote the entry hint (E5/E6) and receipt toasts (C17/C18); hcheck still quotes them."""
        for hint in ("Move: pinch the floor to walk there", "Move: aim at the floor, pull the trigger",
                     "Turn your left palm up for tools", "Measure: pinch one corner, then the other"):
            _, r = ctx_of([u(1, "AppState Passthrough -> World"), u(1.001, "Measure tool equipped"), u(1.002, f"Toast: {hint}")])
            self.assertEqual(r["ui.default_tool"].status, hcheck.PASS)
            self.assertIn(hint, r["ui.default_tool"].evidence)
            # A pre-D1 build (Move on entry) is flagged, not passed.
            _, r = ctx_of([u(1, "AppState Passthrough -> World"), u(1.001, "Move tool equipped"), u(1.002, f"Toast: {hint}")])
            self.assertEqual(r["ui.default_tool"].status, hcheck.FAIL)
            self.assertIn("before D1", r["ui.default_tool"].note)
        for toast in ("Receipt saved · $7.59 · no charge", "✓ Paid $7.59 · Hinge Outlet"):
            _, r = ctx_of([u(1, "Hold confirmed"), u(2, "Notebook #9 purchase: Ordered (offline) 1 × H from S — $7.59"),
                           u(2.001, f"Toast: {toast}"), u(2.002, "Checkout authorized: OFF-1 7.59 USD")])
            self.assertEqual(r["checkout.hold"].status, hcheck.PASS)
            self.assertIn(toast, r["checkout.hold"].evidence)
        _, r = ctx_of([u(1, "Toast: Can't answer now · the assistant is offline")])
        self.assertIn("Can't answer now", r["scene.ask"].evidence)

    def test_low_fps_fails_and_builtin_scene_is_not_the_real_scene(self):
        lines = [u(0.5, "SceneStreamer: kitchen · k r1 full · no structure in 1.0 s (collision mesh, 1 cameras)"),
                 u(1, "AppState Passthrough -> World")]
        lines += [vr(s, 60 if s % 3 else 72) for s in range(1, 80)]
        _, r = ctx_of(lines)
        self.assertEqual(r["perf.world72"].status, hcheck.FAIL)
        builtin = [u(1, "AppState Passthrough -> World")] + [vr(s, 73) for s in range(1, 80)]
        _, r = ctx_of(builtin)
        self.assertEqual(r["perf.world72"].status, hcheck.NOT_SEEN)
        self.assertIn("all World", r["perf.world72"].note)

    def test_fps_drop_while_holding_a_part_fails(self):
        # The pre-fix kitchen session: an AC unit in hand over the 50k-tri scan dropped to 32–64 fps until placed.
        lines = [u(1, "AppState Passthrough -> World"), u(20, "Part ac-1 in hand (server)"),
                 u(32, "Part placed ac-1 on Collision r1/geometry_0 (ray): Red: Hits Collision r1/geometry_0")]
        lines += [vr(s, 40 if 21 <= s <= 32 else 73) for s in range(1, 60)]
        c, r = ctx_of(lines)
        self.assertEqual(r["perf.hold"].status, hcheck.FAIL)
        self.assertIn("lowest 40 fps", r["perf.hold"].note)
        self.assertEqual(sum(1 for s in c.samples if s.holding), 12)  # 1-s windows fully inside 20 … 32 s
        self.assertEqual(r["perf.world72"].status, hcheck.NOT_SEEN)    # built-in facade only

    def test_negative_gpu_and_resume_gap(self):
        lines = [u(0, "AppState Passthrough -> World"), vr(5, 73, gpu=-0.25), vr(6, 73), vr(20, 1), vr(21, 73)]
        c, _ = ctx_of(lines)
        self.assertEqual([s.skip for s in c.samples], ["resume", None, "resume", None])
        self.assertIsNone(c.samples[0].gpu)

    def test_cached_scene_is_not_a_server_round_trip(self):
        lines = [u(1, "SceneStreamer: server unreachable, loading the cached kitchen package", lvl="W"),
                 u(1.2, "SceneStreamer: kitchen · k r1 full · no structure in 0.2 s (collision mesh, 1 cameras)")]
        _, r = ctx_of(lines)
        self.assertEqual(r["net.server"].status, hcheck.FAIL)
        self.assertEqual(r["scene.real"].status, hcheck.PASS)
        self.assertIn("cache", r["scene.real"].note)

    def test_app_errors(self):
        _, r = ctx_of([u(1, "Palm menu open (hand)"), f"{stamp(2)} E/Unity   ( {APP}): Curl error 60: Cert verify failed."])
        self.assertEqual(r["app.errors"].status, hcheck.PASS)
        self.assertIn("Curl error 60", r["app.errors"].note)
        _, r = ctx_of([u(1, "Notebook export failed: IOException", lvl="E")])
        self.assertEqual(r["app.errors"].status, hcheck.FAIL)
        self.assertEqual(r["notebook.export"].status, hcheck.FAIL)

    def test_old_build_without_the_ring(self):
        _, r = ctx_of([u(1, "AppState Passthrough -> World"), u(2, "Menu: ToggleMeasure"), u(2, "Measure tool equipped")])
        self.assertEqual(r["ring.spin"].status, hcheck.NOT_SEEN)
        self.assertIn("older palm-menu build", r["ring.spin"].note)
        self.assertEqual(r["ui.default_tool"].status, hcheck.PASS)  # Measure in hand within 2 s of entering (D1)

    def test_menu_opened_by_controller_only(self):
        _, r = ctx_of([u(1, "Palm menu open (controller)")])
        self.assertEqual(r["ring.open"].status, hcheck.NOT_SEEN)
        self.assertIn("controller", r["ring.open"].note)

    def test_exit_world_from_settings_asks_twice(self):
        # Ring v2 (declutter DC3): ring ▸ Settings (once More) ▸ Exit world; the first tap arms it, the second leaves.
        _, r = ctx_of([u(1, "AppState Passthrough -> World"), u(20, "Palm menu open (hand)"),
                       u(21, "Ring: ran ToggleScenePanel"), u(21, "Menu: ToggleScenePanel"), u(22, "Palm menu closed"),
                       u(24, "Button ExitWorld (poke, hand)"), u(25.2, "Button ExitWorld (poke, hand)"),
                       u(25.2, "AppState World -> Passthrough"), u(25.2, "Menu: ToggleWorld")])
        self.assertEqual(r["scene.window"].status, hcheck.PASS)
        self.assertEqual(r["ring.exit_confirm"].status, hcheck.PASS)
        self.assertIn("Button ExitWorld (poke, hand) → Menu: ToggleWorld", r["ring.exit_confirm"].evidence)
        self.assertIn("ToggleScenePanel", r["ring.action"].note)


class ScaleModels(unittest.TestCase):
    """scalemodels: the kitchen opens at its default ×1.63; Model view's models are on the headset."""

    def test_kitchen_default_scale(self):
        _, r = ctx_of([u(1, "SceneStreamer: kitchen r1 at its default scale ×1.63 (SiteScales; a tape + Set scale overrides it)")])
        self.assertEqual(r["scene.default_scale"].status, hcheck.PASS)
        _, r = ctx_of([u(1, "SceneStreamer: kitchen r1 at its default scale ×1.47 (SiteScales; a tape + Set scale overrides it)")])
        self.assertEqual(r["scene.default_scale"].status, hcheck.FAIL)

    def test_a_saved_scale_overrides_the_default(self):
        _, r = ctx_of([u(1, "SceneStreamer: restored scale ×1.5200 for kitchen r1 (frame f, airtools.scale.kitchen.f.r1)")])
        self.assertEqual(r["scene.default_scale"].status, hcheck.NOT_SEEN)
        self.assertIn("Reset", r["scene.default_scale"].note)

    def test_models_on_the_headset(self):
        _, r = ctx_of([u(1, "SceneStreamer: 7 models known on this headset (2 downloaded): kitchen, zabel-gymnasium")])
        self.assertEqual(r["models.headset"].status, hcheck.PASS)
        _, r = ctx_of([u(1, "SceneStreamer: 1 models known on this headset (1 downloaded): kitchen"),
                       u(90, "Prefetch: zabel-gymnasium r3 on the headset (4 files, 21.3 MB, 38 s) · Models on the headset: 3 of 7"),
                       u(150, "Prefetch: hospital-bg r3 on the headset (4 files, 18.0 MB, 30 s) · Models on the headset: 4 of 7")])
        self.assertEqual(r["models.headset"].status, hcheck.PASS)
        self.assertIn("2 downloaded this session: zabel-gymnasium, hospital-bg", r["models.headset"].note)
        _, r = ctx_of([u(1, "SceneStreamer: 1 models known on this headset (1 downloaded): kitchen")])
        self.assertEqual(r["models.headset"].status, hcheck.NOT_SEEN)


class UnitsD2(unittest.TestCase):
    """UX D2 (SPEC §9): labels on screen show one unit (imperial first, a chip for metric). The notebook label the log
    carries stays "Distance 1.50 m · 4′ 11″", but tapes are read from any shape so a label change can't blind a check."""

    def test_length_from_every_label_shape(self):
        for label, want in (("1.50 m · 4′ 11″", 1.5), ("4′ 11⅛″ · 1.50 m", 1.5), ("0.41 m · 1′ 4⅛″ (gutter)", 0.41),
                            ("4′ 11⅛″", 59.125 * 0.0254), ("10¼″", 10.25 * 0.0254), ("⅜″", 0.375 * 0.0254),
                            ("1′ 0″", 0.3048), ("13′ 9⅜″ · 4.20 m", 4.2)):
            with self.subTest(label=label):
                self.assertAlmostEqual(hcheck.length_m(label), want, places=6)
        for label in ("1.80 m² · 19.4 ft²", "Quad …", "", None):
            self.assertIsNone(hcheck.length_m(label))

    def test_tabletop_tape_reads_imperial_labels(self):
        for world_label, table_label in (("4′ 11⅛″ · 1.50 m", "4′ 10⅞″"), ("4′ 11⅛″", "1.49 m · 4′ 10⅝″"),
                                         ("1.50 m · 4′ 11″", "4′ 10⅞″ · 1.49 m")):
            with self.subTest(world=world_label, table=table_label):
                lines = [u(1, "AppState Passthrough -> World"), u(5, f"Notebook #1 measure: Distance {world_label}"),
                         u(6, "Toast: ✓ Saved · Width 4′ 11⅛″"),
                         u(10, "AppState World -> Tabletop"), u(10.4, "Tabletop on (1:50)"),
                         u(15, f"Notebook #2 measure: Distance {table_label}")]
                _, r = ctx_of(lines)
                self.assertEqual(r["measure.distance"].status, hcheck.PASS)
                self.assertEqual(r["tabletop.tape"].status, hcheck.PASS)
                self.assertIn("closest 1:1 tape #1 1.50 m", r["tabletop.tape"].note)

    def test_units_chip(self):
        _, r = ctx_of([u(1, "Button UnitsChip (poke, hand)"), u(1, "Units: m (Metric)"), u(3, "Units: ft·in (Imperial)")])
        self.assertEqual(r["ui.units"].status, hcheck.PASS)
        self.assertIn("switched: m → ft·in", r["ui.units"].note)
        _, r = ctx_of([chk(2, 'D2.units.relabel value_m=1.5000 imperial="4′ 11″" metric="1.50 m" label="Distance 1.50 m · 4′ 11″" restored=Imperial PASS')])
        self.assertEqual(r["ui.units"].status, hcheck.PASS, "the harness check alone")
        _, r = ctx_of([u(1, "Units: m (Metric)"),
                       chk(2, 'D2.units.relabel value_m=1.5000 imperial="1.50 m" metric="1.50 m" label="Distance 1.50 m · 4′ 11″" restored=Imperial FAIL')])
        self.assertEqual(r["ui.units"].status, hcheck.FAIL)
        _, r = ctx_of([u(1, "Menu: ToggleContrast")])
        self.assertEqual(r["ui.units"].status, hcheck.NOT_SEEN)


class Cli(unittest.TestCase):
    def _run(self, text, *args):
        with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False, encoding="utf-8") as f:
            f.write(text)
        try:
            out = io.StringIO()
            with redirect_stdout(out):
                code = hcheck.main([f.name, *args])
            return code, out.getvalue()
        finally:
            os.unlink(f.name)

    def test_exit_codes(self):
        code, out = self._run("\n".join(session()))
        self.assertEqual(code, 0, out)
        code, _ = self._run("\n".join([u(10, "Checkout request: p seller 0 × 1")]))
        self.assertEqual(code, 1)
        code, out = self._run("\n".join(session()), "--json")
        self.assertEqual(code, 0)
        self.assertIn('"perf.world72"', out)
        err = io.StringIO()
        with redirect_stdout(io.StringIO()):
            old, sys.stderr = sys.stderr, err
            try:
                self.assertEqual(hcheck.main([os.path.join(tempfile.gettempdir(), "no-such-hcheck.log")]), 2)
            finally:
                sys.stderr = old


def real_logs():
    env = os.environ.get("HCHECK_LOGS")
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.dirname(os.path.dirname(here))
    patterns = env.split(os.pathsep) if env else [
        os.path.join(repo, "SpikeData", "quest*.log"),
        os.path.expanduser("~/AirTools/SpikeData/quest-*.log"),
        os.path.expanduser("~/AirTools-backend/SpikeData/quest-backend.log"),
    ]
    found = []
    for p in patterns:
        for f in sorted(glob.glob(p)):
            if os.path.realpath(f) not in {os.path.realpath(x) for x in found}:
                found.append(f)
    return found


class RealLogs(unittest.TestCase):
    """Smoke: every real headset log parses, every check evaluates, and what the log plainly shows is claimed."""

    def test_real_logs(self):
        logs = real_logs()
        if not logs:
            self.skipTest("no real headset logs found (set HCHECK_LOGS)")
        for path in logs:
            with self.subTest(log=os.path.basename(path)):
                with open(path, encoding="utf-8", errors="replace") as f:
                    raw = f.read()
                ctx, results = hcheck.run(path)
                r = {c.id: res for c, res in results}
                self.assertEqual(len(r), len(hcheck.CHECKS))
                self.assertFalse([k for k, v in r.items() if "check error" in v.note], "a check raised")
                self.assertIn("FPS / GPU", hcheck.report(path, ctx, results))
                if "[AirTools] Palm menu open (hand)" in raw:
                    self.assertEqual(r["ring.open"].status, hcheck.PASS)
                if "[AirTools] SceneStreamer: kitchen ·" in raw:
                    self.assertEqual(r["scene.real"].status, hcheck.PASS)
                if "[AirTools] Notebook #1 measure: Distance" in raw:
                    self.assertEqual(r["measure.distance"].status, hcheck.PASS)
                app_fps = [s for s in ctx.samples if s.skip is None]
                if app_fps:
                    self.assertTrue(hcheck.perf_rows(ctx))
                    self.assertTrue(all(s.pid in ctx.pids for s in app_fps))


if __name__ == "__main__":
    unittest.main()
