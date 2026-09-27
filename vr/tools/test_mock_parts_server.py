#!/usr/bin/env python3
"""Contract tests for tools/mock_parts_server.py against the backend's docs/api.md
(run: python3 -m unittest tools/test_mock_parts_server.py -v)."""
import base64, json, os, sys, tempfile, threading, time, unittest, urllib.error, urllib.request, uuid

sys.path.insert(0, os.path.dirname(__file__))
import mock_parts_server as mps  # noqa: E402

PART_KEYS = {"id", "name", "manufacturer", "model_no", "dims_mm", "dims_source", "weight_g", "color_hex", "finish", "material",
             "finishes", "mount", "clearance_mm", "spacing_mm", "asset", "image_url", "spec_url", "citations", "sellers",
             "sellers_expanded", "recommended_seller", "recommendation_reason", "fit", "fetched_at", "cached"}
SELLER_KEYS = {"name", "price_usd", "pack_qty", "unit_price_usd", "shipping_usd", "total_usd", "eta", "eta_days", "rating",
               "reviews", "in_stock", "url", "verified"}


class HttpMixin:
    base = ""

    def get(self, path, headers=None):
        req = urllib.request.Request(self.base + path, headers=headers or {})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, r.headers, r.read()
        except urllib.error.HTTPError as e:
            return e.code, e.headers, e.read()

    def post(self, path, obj):
        req = urllib.request.Request(self.base + path, data=json.dumps(obj).encode(), method="POST",
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def voice(self, session_id, context, tts=b"true"):
        boundary = "----b" + uuid.uuid4().hex
        parts = [(b'Content-Disposition: form-data; name="audio"; filename="c.wav"\r\nContent-Type: audio/wav\r\n\r\n', mps.tone_wav(0.2)),
                 (b'Content-Disposition: form-data; name="session_id"\r\n\r\n', session_id.encode()),
                 (b'Content-Disposition: form-data; name="context"\r\n\r\n', json.dumps(context).encode()),
                 (b'Content-Disposition: form-data; name="tts"\r\n\r\n', tts)]
        body = b"".join(b"--" + boundary.encode() + b"\r\n" + h + v + b"\r\n" for h, v in parts) + b"--" + boundary.encode() + b"--\r\n"
        req = urllib.request.Request(self.base + "/voice/command", data=body, method="POST",
                                     headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        with urllib.request.urlopen(req, timeout=5) as resp:
            return json.loads(resp.read())


class MockServerTests(HttpMixin, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scenes = tempfile.mkdtemp()
        os.makedirs(os.path.join(cls.scenes, "site-a", "thumbs"))
        with open(os.path.join(cls.scenes, "site-a", "scene.json"), "w") as f:
            json.dump({"name": "a", "revision": 2, "quality": "full", "mesh": {"file": "mesh.r2.glb"}}, f)
        with open(os.path.join(cls.scenes, "site-a", "mesh.r2.glb"), "wb") as f:
            f.write(b"glTF")
        cls.httpd = mps.serve(port=0, delay=0.3, host="127.0.0.1", asset_delay=0.3, scene_dir=cls.scenes,
                              voice_transcript="find a gutter hanger")
        cls.base = f"http://127.0.0.1:{cls.httpd.server_address[1]}"
        threading.Thread(target=cls.httpd.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()

    def wait_job(self, job_id):
        for _ in range(40):
            j = json.loads(self.get(f"/parts/jobs/{job_id}")[2])
            if j["status"] == "done":
                return j
            time.sleep(0.05)
        self.fail("job never finished")

    def test_part_json_matches_contract(self):
        for pid in ("hidden-hanger-5k", "window-ac-small"):
            status, headers, body = self.get(f"/parts/{pid}/part.json")
            self.assertEqual(200, status)
            self.assertEqual("application/json", headers["Content-Type"])
            part = json.loads(body)
            self.assertTrue(PART_KEYS <= set(part), PART_KEYS - set(part))
            self.assertEqual({"w", "d", "h"}, set(part["dims_mm"]))
            self.assertIn(part["asset"]["status"], ("pending", "ready"))
            for s in part["sellers"]:
                self.assertTrue(SELLER_KEYS <= set(s), SELLER_KEYS - set(s))
        ac = json.loads(self.get("/parts/window-ac-small/part.json")[2])
        self.assertEqual({"min": 590, "max": 1000}, ac["fit_range_mm"])

    def test_model_and_image(self):
        status, headers, body = self.get("/parts/hidden-hanger-5k/model.glb")
        self.assertEqual((200, "model/gltf-binary"), (status, headers["Content-Type"]))
        self.assertEqual(b"glTF", body[:4])
        self.assertEqual(b"\xff\xd8", self.get("/parts/hidden-hanger-5k/image.jpg")[2][:2])
        self.assertEqual(400, self.get("/parts/BAD_ID/part.json")[0])
        self.assertEqual(404, self.get("/parts/nope/part.json")[0])

    def test_search_measurement_object_and_job_with_full_parts(self):
        status, job = self.post("/parts/search", {"query": f"gutter hanger {uuid.uuid4().hex}",
                                                  "measurement": {"label": "tape #1", "value_m": 0.130, "axis": "length"}})
        self.assertEqual(200, status)
        self.assertEqual("running", job["status"])
        self.assertEqual("running", json.loads(self.get(f"/parts/jobs/{job['job_id']}")[2])["status"])
        done = self.wait_job(job["job_id"])
        self.assertEqual(["hidden-hanger-5k"], [c["id"] for c in done["candidates"]])
        self.assertTrue(PART_KEYS <= set(done["candidates"][0]))
        self.assertEqual("fits", done["candidates"][0]["fit"]["status"])
        self.assertIn("summary", done)
        # A bare number (what the old app sent) is rejected, like the real server's 422.
        self.assertEqual(422, self.post("/parts/search", {"query": "gutter hanger", "measurement": 1.5})[0])

    def test_cache_hit_is_done_at_once_with_candidates(self):
        self.post("/parts/search", {"query": "window ac"})
        _, job = self.post("/parts/search", {"query": "ac window"})
        self.assertEqual("done", job["status"])
        self.assertEqual("window-ac-small", job["candidates"][0]["id"])

    def test_asset_goes_pending_then_ready(self):
        _, job = self.post("/parts/search", {"query": f"fascia bracket {uuid.uuid4().hex}"})
        self.wait_job(job["job_id"])
        time.sleep(0.4)
        self.assertEqual("ready", json.loads(self.get("/parts/hidden-hanger-5k/part.json")[2])["asset"]["status"])

    def test_sellers_rerank(self):
        status, part = self.post("/parts/hidden-hanger-5k/sellers?sort=fastest", {})
        self.assertEqual(200, status)
        days = [s["eta_days"] for s in part["sellers"]]
        self.assertEqual(sorted(days), days)
        self.assertEqual(0, part["recommended_seller"])
        _, cheap = self.post("/parts/hidden-hanger-5k/sellers?sort=cheapest", {})
        totals = [s["total_usd"] for s in cheap["sellers"]]
        self.assertEqual(sorted(totals), totals)

    def test_checkout_offline_receipt_label_and_bom(self):
        status, r = self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 0, "qty": 8, "session_id": "t1"})
        self.assertEqual(200, status)
        self.assertEqual(("OFFLINE_RECEIPT", "offline"), (r["status"], r["mode"]))
        self.assertEqual(mps.OFFLINE_LABEL, r["label"])
        self.assertIsNone(r["approval_code"])
        self.assertEqual(400, self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 9, "qty": 1})[0])
        self.assertEqual(400, self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 0, "qty": 0})[0])
        _, bom = self.post("/parts/bom", {"part_ids": ["hidden-hanger-5k"], "counts": {"hidden-hanger-5k": 8}})
        self.assertEqual(1, len(bom["lines"]))
        _, r2 = self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 0, "qty": 1, "bom_id": bom["id"], "bom_lines": [0]})
        self.assertEqual(1, len(r2["bom_lines"]))
        self.assertAlmostEqual(r2["total_usd"], r["total_usd"] / 8 * 1 + 12.0, delta=5)   # part + screws
        nb = json.loads(self.get("/notebook/t1")[2])
        self.assertEqual("order", nb["entries"][-1]["type"])

    def test_agent_fast_path_and_literal_find(self):
        _, r = self.post("/agent/command", {"session_id": "s", "text": "find me a gutter hanger", "context": {}})
        self.assertEqual("search_started", r["actions"][0]["name"])
        self.assertEqual(r["job_id"], r["actions"][0]["args"]["job_id"])
        _, r = self.post("/agent/command", {"session_id": "s", "text": "select the first", "context": {"candidate_ids": ["hidden-hanger-5k"]}})
        self.assertEqual({"name": "select_candidate", "args": {"index": 0}}, r["actions"][0])
        _, r = self.post("/agent/command", {"session_id": "s", "text": "cheapest first", "context": {"selected_part_id": "hidden-hanger-5k"}})
        self.assertEqual("show_sellers", r["actions"][0]["name"])
        _, r = self.post("/agent/command", {"session_id": "s", "text": "put one every 60 cm", "context": {}})
        self.assertEqual({"spacing_mm": 600.0}, r["actions"][0]["args"])
        _, r = self.post("/agent/command", {"session_id": "s", "text": "what else do I need?", "context": {"placed": [{"part_id": "hidden-hanger-5k", "count": 8}]}})
        self.assertEqual("show_bom", r["actions"][0]["name"])
        self.assertEqual(422, self.post("/agent/command", {"text": "hi"})[0])

    def test_voice_command_multipart(self):
        boundary = "----b" + uuid.uuid4().hex
        wav = mps.tone_wav(0.2)
        parts = [(b'Content-Disposition: form-data; name="audio"; filename="c.wav"\r\nContent-Type: audio/wav\r\n\r\n', wav),
                 (b'Content-Disposition: form-data; name="session_id"\r\n\r\n', b"v1"),
                 (b'Content-Disposition: form-data; name="context"\r\n\r\n', b"{}"),
                 (b'Content-Disposition: form-data; name="tts"\r\n\r\n', b"true")]
        body = b"".join(b"--" + boundary.encode() + b"\r\n" + h + v + b"\r\n" for h, v in parts) + b"--" + boundary.encode() + b"--\r\n"
        req = urllib.request.Request(self.base + "/voice/command", data=body, method="POST",
                                     headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        with urllib.request.urlopen(req, timeout=5) as resp:
            r = json.loads(resp.read())
        self.assertEqual("find a gutter hanger", r["transcript"])
        self.assertEqual("audio/wav", r["audio_mime"])
        self.assertEqual(b"RIFF", base64.b64decode(r["audio_b64"])[:4])
        self.assertEqual("search_started", r["actions"][0]["name"])

    def test_scene_ask_and_scenes(self):
        _, r = self.post("/scene/ask", {"question": "what?", "frames": [{"id": "0007", "jpg_b64": base64.b64encode(b"\xff\xd8x").decode()}]})
        self.assertEqual("0007", r["frame_id"])
        self.assertEqual(4, len(r["box"]))
        self.assertEqual(400, self.post("/scene/ask", {"question": "what?", "frames": []})[0])
        self.assertEqual([{"site": "site-a", "revision": 2, "quality": "full"}],
                         [{k: s[k] for k in ("site", "revision", "quality")} for s in json.loads(self.get("/scenes")[2])])
        status, headers, _ = self.get("/scenes/site-a/scene.json")
        self.assertEqual("no-cache", headers["Cache-Control"])
        self.assertEqual(304, self.get("/scenes/site-a/scene.json", {"If-None-Match": headers["ETag"]})[0])
        self.assertIn("immutable", self.get("/scenes/site-a/mesh.r2.glb")[1]["Cache-Control"])
        self.assertEqual(404, self.get("/scenes/site-a/..%2F..%2Fetc")[0])

    def test_voice_token_and_notebook_contract(self):
        self.assertEqual(503, self.post("/voice/token", {})[0])
        self.assertEqual(422, self.post("/notebook", {"entries": []})[0])
        status, r = self.post("/notebook", {"session_id": "n1", "entries": [{"type": "note", "text": "hi"}]})
        self.assertEqual((200, "n1", 1), (status, r["session_id"], len(r["entries"])))


# Like the real kitchen capture (backend tests/test_survey.py): 18 cabinet doors in 4 repeat sizes + 4 odd ones, 2 drawers.
KITCHEN_DOORS = [(262, 279)] * 6 + [(208, 525)] * 4 + [(261, 426)] * 2 + [(205, 422)] * 2 + [(548, 282), (141, 87), (102, 65), (70, 162)]


def _obj(oid, label, w_mm, h_mm, x0=0.0, y0=0.0):
    w, h = w_mm / 1000, h_mm / 1000
    return {"id": oid, "label": label, "plane": "p0", "w_m": w, "h_m": h,
            "corners3d": [[x0, y0, 0.0], [x0 + w, y0, 0.0], [x0 + w, y0 + h, 0.0], [x0, y0 + h, 0.0]]}


def write_site(root, site, structure, **meta):
    os.makedirs(os.path.join(root, site))
    scene = {"name": site, "revision": 1, "gravity_residual_deg": 0.0, **meta}
    if structure is not None:
        with open(os.path.join(root, site, "structure.r1.json"), "w") as f:
            json.dump(structure, f)
        scene["structure"] = {"file": "structure.r1.json", "schema": "airtools.structure/1"}
    with open(os.path.join(root, site, "scene.json"), "w") as f:
        json.dump(scene, f)


def kitchen_structure():
    objects = [_obj(f"o{i}", "cabinet_door", w, h, x0=i) for i, (w, h) in enumerate(KITCHEN_DOORS)]
    objects += [_obj("o18", "drawer", 528, 90), _obj("o19", "drawer", 410, 90)]
    return {"schema": "airtools.structure/1", "planes": [{"id": "p0", "normal": [0, 0, 1], "offset": 0}],
            "edges": [{"id": "e0", "a": [0, 0.6, 0], "b": [1.5, 0.6, 0], "kind": "crease"}], "objects": objects}


def facade_structure():
    # One 1.5 x 1.2 m window, one door, a level 4.2 m gutter box at ~6 m (top edge 6.2 m, front lip 6.0 m), a sloped
    # roof edge, a 20 cm trim piece and low wall edges.
    edges = [{"id": "e_lip", "a": [0, 6.0, 0.3], "b": [4.2, 6.0, 0.3], "kind": "crease"},
             {"id": "e_top", "a": [0, 6.2, 0.2], "b": [4.2, 6.2, 0.2], "kind": "crease"},
             {"id": "e_roof", "a": [0, 6.2, 0], "b": [0, 8.0, -2.0], "kind": "crease"},
             {"id": "e_sill", "a": [0.75, 3.5, 0], "b": [-0.75, 3.5, 0], "kind": "boundary"},
             {"id": "e_ground", "a": [-4, 0, 0], "b": [4, 0, 0], "kind": "crease"},
             {"id": "e_short", "a": [0, 6.3, 0], "b": [0.2, 6.3, 0], "kind": "line"},
             {"id": "e_head", "a": [0.75, 4.7, 0], "b": [-0.75, 4.7, 0], "kind": "boundary"},
             {"id": "e_door", "a": [2.0, 2.03, 0], "b": [2.9, 2.03, 0], "kind": "boundary"},
             {"id": "e_plinth", "a": [-4, 0.3, 0], "b": [4, 0.3, 0], "kind": "crease"}]
    return {"schema": "airtools.structure/1", "planes": [{"id": "p0", "normal": [0, 0, 1], "offset": 0}], "edges": edges,
            "objects": [_obj("o0", "window", 1500, 1200, -0.75, 3.5), _obj("o1", "door", 914, 2032)]}


def kitchen_results(unverified=("o9", "o11")):
    return [{"id": f"o{i}", "label": "cabinet_door", "w_m": (w + (i % 3)) / 1000, "h_m": h / 1000, "area_m2": w * h / 1e6,
             "angles_deg": [90.1, 89.8, 90.2, 89.9], "snap": ["corner", "corner", "edge", "corner"],
             "unverified": f"o{i}" in unverified, "notebook_id": 12 + i, "camera_id": 316}
            for i, (w, h) in enumerate(KITCHEN_DOORS)]


class B1B3Base(HttpMixin, unittest.TestCase):
    """A mock over synthetic kitchen / facade / preview scene packages, with 'today' pinned to Sat 2026-09-26
    (so "by Friday" is Fri Oct 2, like the backend's tests)."""
    TODAY = __import__("datetime").date(2026, 9, 26)

    @classmethod
    def setUpClass(cls):
        cls.scenes = tempfile.mkdtemp()
        write_site(cls.scenes, "kitchen", kitchen_structure(), gravity_residual_deg=0.08)
        write_site(cls.scenes, "facade", facade_structure())
        write_site(cls.scenes, "facade-preview", None)
        cls.httpd = mps.serve(port=0, delay=0.05, host="127.0.0.1", asset_delay=0.05, scene_dir=cls.scenes,
                              voice_transcript="measure every cabinet door|which one is the tallest?")
        cls.base = f"http://127.0.0.1:{cls.httpd.server_address[1]}"
        threading.Thread(target=cls.httpd.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()

    def setUp(self):
        self._today = mps.today
        mps.today = lambda: self.TODAY
        self.sid = "t-" + uuid.uuid4().hex[:8]

    def tearDown(self):
        mps.today = self._today

    def command(self, text, context=None, sid=None):
        status, r = self.post("/agent/command", {"session_id": sid or self.sid, "text": text, "context": context or {}})
        self.assertEqual(200, status, r)
        return r

    def observe(self, **body):
        status, r = self.post("/agent/observe", {"session_id": self.sid, **body})
        self.assertEqual(200, status, r)
        return r


class SurveyTests(B1B3Base):
    """B1: survey / check_slope / stop_survey fast paths and POST /agent/observe (contract §1-§4)."""

    def test_digest_groups_like_the_real_kitchen(self):
        d = mps.scene_digest("kitchen")
        self.assertEqual({"cabinet_door": 18, "drawer": 2}, d["counts"])
        groups = d["groups"]["cabinet_door"]
        self.assertEqual([(6, 262, 279), (4, 208, 525), (2, 261, 426), (2, 205, 422)], [(g["count"], g["w_mm"], g["h_mm"]) for g in groups[:4]])
        self.assertEqual(8, len(groups))
        self.assertEqual(["e_top", "e_lip"], [e["id"] for e in mps.scene_digest("facade")["gutter_edges"]])
        self.assertEqual((2250, 1800), next((o["w_mm"], o["h_mm"]) for o in mps.scene_digest("facade", 1.5)["objects"] if o["label"] == "window"))
        self.assertFalse(mps.scene_digest("facade-preview")["structure"])
        self.assertIsNone(mps.scene_digest("no-such-site"))
        self.assertIsNone(mps.scene_digest("../etc"))

    def test_measure_every_cabinet_door_with_site(self):
        r = self.command("measure every cabinet door", {"site": "kitchen", "tool": "tape"})
        self.assertEqual("Surveying 18 doors.", r["reply"])
        self.assertIsNone(r["job_id"])
        [a] = r["actions"]
        self.assertEqual("survey", a["name"])
        self.assertEqual({"label": "cabinet_door", "where": "all", "measure": "size"}, {k: a["args"][k] for k in ("label", "where", "measure")})
        self.assertRegex(a["args"]["request_id"], r"^sv-[0-9a-f]{8}$")

    def test_survey_replies_depend_on_the_site(self):
        self.assertEqual({"reply": "No windows in this scene's structure layer.", "actions": [], "job_id": None},
                         self.command("measure all the windows", {"site": "kitchen"}))
        self.assertEqual("cabinet_door", self.command("measure all the doors", {"site": "kitchen"})["actions"][0]["args"]["label"])
        facade = self.command("measure all the doors", {"site": "facade"}, sid=self.sid + "f")
        self.assertEqual(("Surveying 1 door.", "door"), (facade["reply"], facade["actions"][0]["args"]["label"]))
        preview = self.command("measure the window", {"site": "facade-preview"}, sid=self.sid + "p")
        self.assertEqual(("No structure layer for this scene; mark the corners yourself.", []), (preview["reply"], preview["actions"]))
        # Without a site (the built-in scene) the survey still goes to the headset.
        for text, spoken in (("measure every cabinet door", "Surveying the doors."), ("measure the upper drawers", "Surveying the upper drawers."),
                             ("measure the window", "Measuring the nearest window."), ("measure the panels I can see", "Surveying the panels in view.")):
            r = self.command(text, sid=self.sid + "n")
            self.assertEqual((spoken, "survey"), (r["reply"], r["actions"][0]["name"]), text)

    def test_survey_phrasings(self):
        cases = [("Quartermaster, measure every cabinet door", ("cabinet_door", "all", "size")),
                 ("measure all the windows", ("window", "all", "size")),
                 ("measure all of the drawers.", ("drawer", "all", "size")),
                 ("size up the upper cabinet doors", ("cabinet_door", "upper", "size")),
                 ("measure the lower drawers", ("drawer", "lower", "size")),
                 ("measure the left doors", ("door", "left", "size")),
                 ("measure the window", ("window", "nearest", "size")),
                 ("measure the doors I can see", ("door", "visible", "size")),
                 ("measure every window's width", ("window", "all", "width")),
                 ("measure all the appliances, just the height", ("appliance", "all", "height"))]
        for text, want in cases:
            a = self.command(text)["actions"][0]
            self.assertEqual(("survey", want), (a["name"], (a["args"]["label"], a["args"]["where"], a["args"]["measure"])), text)

    def test_check_slope_suggests_gutter_edges_and_leaves_searches_alone(self):
        r = self.command("check the gutter slope", {"site": "facade"})
        self.assertEqual("Checking the gutter's fall.", r["reply"])
        [a] = r["actions"]
        self.assertEqual(("check_slope", "gutter", ["e_top", "e_lip"]), (a["name"], a["args"]["target"], a["args"]["edge_ids"]))
        self.assertRegex(a["args"]["request_id"], r"^sl-[0-9a-f]{8}$")
        for text, target in (("Is this gutter sloped enough to drain?", "gutter"), ("will water drain off that sill?", "sill"),
                             ("check the pitch of the ledge", "ledge")):
            a = self.command(text, sid=self.sid + "n")["actions"][0]  # no site: the app picks its own edge
            self.assertEqual(("check_slope", target, []), (a["name"], a["args"]["target"], a["args"]["edge_ids"]), text)
        self.assertEqual("search_started", self.command("find a gutter drain outlet")["actions"][0]["name"])

    def test_stop_is_the_whole_utterance(self):
        for text in ("stop", "Stop.", "okay, stop", "cancel that", "abort the survey"):
            self.assertEqual({"reply": "Stopping.", "actions": [{"name": "stop_survey", "args": {}}], "job_id": None}, self.command(text), text)
        self.assertNotEqual("stop_survey", (self.command("stop by the hardware store and find hinges")["actions"] or [{}])[0].get("name"))

    def test_observe_readme_example(self):
        r = self.observe(request_id="sv-0c45d47f", kind="survey_result", status="done", planned=18, results=[
            {"id": "o0", "label": "cabinet_door", "group": "g0", "w_m": 0.262, "h_m": 0.279, "area_m2": 0.0731,
             "angles_deg": [90.1, 89.8, 90.2, 89.9], "off_plane_m": 0.004, "snap": ["corner", "corner", "edge", "corner"],
             "unverified": False, "notebook_id": 12, "camera_id": 316},
            {"id": "o1", "label": "cabinet_door", "w_m": 0.263, "h_m": 0.278, "unverified": True, "notebook_id": 13, "camera_id": 316},
            {"id": "o6", "label": "cabinet_door", "w_m": 0.208, "h_m": 0.525, "notebook_id": "n14", "camera_id": 318}],
            skipped=[{"id": "o9", "reason": "corner behind scanned surface"}])
        self.assertEqual({"reply": "3 doors in 2 sizes: 2 at 26 by 28 and 1 at 21 by 53 centimetres. One didn't lock onto its corners; "
                                   "it's amber. One skipped.",
                          "actions": [{"name": "show_tape_survey", "args": {
                              "request_id": "sv-0c45d47f", "label": "cabinet_door",
                              "groups": [{"w_mm": 262, "h_mm": 278, "count": 2, "ids": ["o0", "o1"]},
                                         {"w_mm": 208, "h_mm": 525, "count": 1, "ids": ["o6"]}],
                              "unverified": ["o1"], "skipped": ["o9"], "focus": []}}],
                          "job_id": None}, r)
        self.assertNotIn("audio_b64", r)

    def test_observe_groups_the_kitchen_and_uses_the_pending_request(self):
        queued = self.command("measure every cabinet door", {"site": "kitchen"})
        rid = queued["actions"][0]["args"]["request_id"]
        r = self.observe(request_id=rid, kind="survey_result", results=kitchen_results())  # no label: from the request
        self.assertEqual("18 doors in 8 sizes: 6 at 26 by 28, 4 at 21 by 53, 2 at 26 by 43 centimetres and 5 more sizes on the card. "
                         "Two didn't lock onto corners; they're amber.", r["reply"])
        args = r["actions"][0]["args"]
        self.assertEqual((rid, "cabinet_door", ["o9", "o11"]), (args["request_id"], args["label"], args["unverified"]))
        self.assertEqual(["o0", "o1", "o2", "o3", "o4", "o5"], args["groups"][0]["ids"])
        self.assertEqual(sorted(f"o{i}" for i in range(18)), sorted(i for g in args["groups"] for i in g["ids"]))
        # measure: width from the pending request
        rid = self.command("measure every window's width", sid=self.sid + "w")["actions"][0]["args"]["request_id"]
        _, r = self.post("/agent/observe", {"session_id": self.sid + "w", "request_id": rid, "kind": "survey_result",
                                            "results": [{"id": "o0", "w_m": 1.502, "h_m": 1.199}]})
        self.assertEqual("1 window: 150 centimetres wide.", r["reply"])

    def test_observe_edge_cases(self):
        cases = [({"status": "aborted", "planned": 18, "results": kitchen_results(())[:6]}, "Stopped after 6 of 18. 6 doors, all 26 by 28 centimetres."),
                 ({"status": "no_structure"}, "No structure layer for this scene; mark the corners yourself."),
                 ({"results": []}, "Found no doors to measure."),
                 ({"results": [{"id": "o7", "w_m": 0.12, "h_m": 0.08, "unverified": True}], "skipped": [{"id": "o9", "reason": "behind"}]},
                  "1 door: 120 by 80 millimetres. One didn't lock onto its corners; it's amber. One skipped.")]
        for body, spoken in cases:
            r = self.observe(kind="survey_result", label="cabinet_door", **body)
            self.assertEqual(spoken, r["reply"])
            self.assertEqual(bool(body.get("results")), bool(r["actions"]))

    def test_tallest_follow_up_uses_the_stored_survey(self):
        self.assertEqual("Offline: try a cached part or the crate menu.", self.command("which one is the tallest?")["reply"])
        self.observe(kind="survey_result", label="cabinet_door", results=kitchen_results())
        r = self.command("which one is the widest?")
        self.assertEqual("The widest door is o14: 55 by 28 centimetres.", r["reply"])
        self.assertEqual(("show_tape_survey", ["o14"]), (r["actions"][0]["name"], r["actions"][0]["args"]["focus"]))
        r = self.command("which one is the tallest?")
        self.assertEqual("The tallest door is o6: 21 by 53 centimetres, tied with 3 more.", r["reply"])
        self.assertEqual(["o6", "o7", "o8", "o9"], r["actions"][0]["args"]["focus"])

    def test_slope_verdicts(self):
        r = self.observe(request_id="sl-5f6ccf4a", kind="slope_result", target="gutter", run_m=4.2, fall_mm=0.4, low_end=[2.1, 6.2, 0.03],
                         gravity_residual_deg=0.0, uncertainty_mm=2.0, notebook_id=15, tts=False)
        self.assertEqual({"reply": "Flat: 0 ± 2 mm of fall over 4.20 m. It needs about 9 mm, so water will pond.",
                          "actions": [{"name": "add_note", "args": {"text": "Gutter slope: won't drain (0.4 ± 2 mm of fall over 4.20 m; needs 8.7 mm)"}}],
                          "job_id": None}, r)
        self.assertEqual("12 ± 2 mm of fall over 4.20 m; it needs 9, so it drains.", self.observe(kind="slope_result", run_m=4.2, fall_mm=-12)["reply"])
        r = self.observe(kind="slope_result", target="sill", run_m=4.2, fall_mm=7, uncertainty_mm=6)
        self.assertEqual("About 7 ± 6 mm of fall over 4.20 m; it needs 9, so that's inconclusive. Put a real level on it.", r["reply"])
        self.assertTrue(r["actions"][0]["args"]["text"].startswith("Sill slope: inconclusive"))

    def test_slope_uncertainty_defaults_to_the_sites_gravity_residual(self):
        rid = self.command("does the ledge drain?", {"site": "kitchen"})["actions"][0]["args"]["request_id"]
        r = self.observe(request_id=rid, kind="slope_result", run_m=4.2, fall_mm=3.0)  # 4.2 m x tan(0.08 deg) + 2 = 7.9 mm
        self.assertIn("± 8 mm", r["reply"])
        self.assertTrue(r["actions"][0]["args"]["text"].startswith("Ledge slope: inconclusive (3 ± 7.9 mm"))

    def test_observe_tts(self):
        r = self.observe(kind="survey_result", label="window", tts=True, results=[{"id": "o0", "label": "window", "w_m": 1.5, "h_m": 1.2}])
        self.assertEqual("1 window: 150 by 120 centimetres.", r["reply"])
        self.assertEqual("audio/wav", r["audio_mime"])
        self.assertEqual(b"RIFF", base64.b64decode(r["audio_b64"])[:4])

    def test_observe_422s(self):
        for body in ({"session_id": "s", "kind": "slope_result", "target": "gutter"},       # no run_m / fall_mm
                     {"session_id": "s", "kind": "slope_result", "run_m": 4.2},
                     {"session_id": "s", "kind": "slope_result", "run_m": 0, "fall_mm": 1},
                     {"session_id": "s", "kind": "guess"},
                     {"kind": "survey_result"},
                     {"session_id": "s", "kind": "survey_result", "results": [{"w_m": 1}]},  # a result needs its id
                     {"session_id": "s", "kind": "survey_result", "results": [{"id": f"o{i}"} for i in range(501)]},
                     {"session_id": "s", "kind": "survey_result", "status": "maybe"}):
            status, r = self.post("/agent/observe", body)
            self.assertEqual(422, status, body)
            self.assertIsInstance(r["detail"], list)
        self.assertEqual("Value error, slope_result needs run_m and fall_mm",
                         self.post("/agent/observe", {"session_id": "s", "kind": "slope_result"})[1]["detail"][0]["msg"])

    def test_voice_command_carries_site_and_speaks(self):
        sid = self.sid + "v"
        with mps.State.lock:
            mps.State.voice_index = 0
        r = self.voice(sid, {"site": "kitchen", "scale": 1.0})
        self.assertEqual(("measure every cabinet door", "Surveying 18 doors."), (r["transcript"], r["reply"]))
        self.assertEqual(b"RIFF", base64.b64decode(r["audio_b64"])[:4])
        self.post("/agent/observe", {"session_id": sid, "request_id": r["actions"][0]["args"]["request_id"], "kind": "survey_result",
                                     "results": kitchen_results()})
        r = self.voice(sid, {"site": "kitchen"})
        self.assertEqual("which one is the tallest?", r["transcript"])
        self.assertEqual(["o6", "o7", "o8", "o9"], r["actions"][0]["args"]["focus"])


EVIDENCE = [{"notebook_id": 3, "label": "tape #3", "value_m": 4.2, "camera_id": 316, "photo": "/scenes/synthetic-facade/thumbs/0316.jpg",
             "array": {"spacing_mm": 600, "count": 8}}]
BULK = {"id": "bulk-hanger", "name": "Hanger 50-pack", "dims_mm": {"w": 127, "d": 38, "h": 45},
        "sellers": [{"name": "Bulk Supply", "price_usd": 39.0, "pack_qty": 50, "shipping": "$4.50", "eta": "10 days", "verified": False},
                    {"name": "Mystery Shop", "price_usd": 3.10, "shipping": "free", "eta": ""}]}


class MandateTests(B1B3Base):
    """B3: POST /commerce/limits, POST /checkout/prepare and POST /checkout with the hold proof (contract §5-§6)."""

    def setUp(self):
        super().setUp()
        self._raw_part, self._now = mps.raw_part, mps.now
        self.prices = {}
        mps.raw_part = self.raw_part
        mps.State.fit_measurement.clear()  # no search has measured anything yet

    def tearDown(self):
        mps.raw_part, mps.now = self._raw_part, self._now
        mps.State.sandbox = mps.State.require_hold_proof = False
        super().tearDown()

    def raw_part(self, part_id):
        part = json.loads(json.dumps(BULK)) if part_id == "bulk-hanger" else self._raw_part(part_id)
        if part is not None and part_id in self.prices:
            part["sellers"][0]["price_usd"] = self.prices[part_id]
        return part

    def limits(self, **body):
        status, r = self.post("/commerce/limits", {"session_id": self.sid, **body})
        self.assertEqual(200, status, r)
        return r

    def prepare(self, units=8, seller_idx=0, part_id="hidden-hanger-5k", **extra):
        body = {"session_id": self.sid, "part_id": part_id, "seller_idx": seller_idx, "units_needed": units, "evidence": EVIDENCE, **extra}
        status, r = self.post("/checkout/prepare", body)
        self.assertEqual(200, status, r)
        return r

    def pay(self, prepared, hold_ms=1043, **overrides):
        line = prepared["cart"]["lines"][0]
        return self.post("/checkout", {"part_id": line["part_id"], "seller_idx": line["seller_idx"], "qty": line["packs"], "session_id": self.sid,
                                       "cart_hash": prepared["cart_hash"], "hold_nonce": prepared["hold_nonce"], "hold_ms": hold_ms, **overrides})

    # --- limits

    def test_limits_voice_tightens_panel_loosens(self):
        first = self.limits(max_total_usd=40, deliver_by="friday", text="under forty, by Friday")
        self.assertEqual({"intent": {"id": first["intent"]["id"], "max_total_usd": 40.0, "deliver_by": "2026-10-02", "seller_policy": None,
                                     "source": "voice", "text": "under forty, by Friday"},
                          "refused": [], "changed": ["max_total_usd", "deliver_by"]}, first)
        second = self.limits(seller_policy="cheapest")  # only the fields present change
        self.assertEqual((first["intent"]["id"], 40.0, "2026-10-02", "cheapest"),
                         tuple(second["intent"][k] for k in ("id", "max_total_usd", "deliver_by", "seller_policy")))
        raised = self.limits(max_total_usd=100, deliver_by="2026-10-09")
        self.assertEqual([], raised["changed"])
        self.assertEqual([{"field": "max_total_usd", "asked": 100.0, "kept": 40.0, "why": "voice can only tighten a limit; change it on the panel"},
                          {"field": "deliver_by", "asked": "2026-10-09", "kept": "2026-10-02", "why": "voice can only tighten a limit; change it on the panel"}],
                         raised["refused"])
        ignored = self.limits(max_total_usd=None)  # a voice "no limit" is ignored, not refused
        self.assertEqual((40.0, [], []), (ignored["intent"]["max_total_usd"], ignored["refused"], ignored["changed"]))
        panel = self.limits(source="panel", max_total_usd=100, deliver_by=None, text="panel")
        self.assertEqual((100.0, None, "panel", ["max_total_usd", "deliver_by"]),
                         (panel["intent"]["max_total_usd"], panel["intent"]["deliver_by"], panel["intent"]["source"], panel["changed"]))
        self.assertEqual("2026-09-27", self.limits(deliver_by="tomorrow")["intent"]["deliver_by"])

    def test_limits_errors(self):
        for body in ({"max_total_usd": -5}, {"deliver_by": "someday"}):
            status, r = self.post("/commerce/limits", {"session_id": self.sid, **body})
            self.assertEqual(400, status)
            self.assertIsInstance(r["detail"], str)
        self.assertEqual("deliver_by must be an ISO date or a weekday, got 'someday'",
                         self.post("/commerce/limits", {"session_id": self.sid, "deliver_by": "someday"})[1]["detail"])
        for body in ({"session_id": self.sid, "seller_policy": "cheap"}, {"max_total_usd": 5}, {"session_id": self.sid, "source": "robot"},
                     {"session_id": self.sid, "max_total_usd": "lots"}, {"session_id": ""}):
            status, r = self.post("/commerce/limits", body)
            self.assertEqual(422, status, body)
            self.assertIsInstance(r["detail"], list)

    # --- prepare

    def test_prepare_prices_the_cart_and_lists_the_checks(self):
        intent = self.limits(source="panel", max_total_usd=40, deliver_by="friday", seller_policy="fastest", text="panel: fastest")["intent"]
        p = self.prepare(evidence=EVIDENCE + [{"label": "partial", "extra": "dropped"}])
        self.assertEqual({"cart", "cart_hash", "hold_nonce", "nonce_expires_at", "intent", "checks", "all_ok"}, set(p))
        cart = p["cart"]
        self.assertEqual(["id", "intent_id", "lines", "bom_id", "bom_lines", "shipping_usd", "total_usd", "evidence", "created_at"], list(cart))
        self.assertEqual(intent["id"], cart["intent_id"])
        self.assertEqual([{"part_id": "hidden-hanger-5k", "seller_idx": 0, "seller": "Home Depot (mock)", "units_needed": 8, "pack_qty": 1,
                           "packs": 8, "unit_price_usd": 4.27, "shipping_usd": 0.0, "line_total_usd": 34.16}], cart["lines"])
        self.assertEqual((34.16, None, []), (cart["total_usd"], cart["bom_id"], cart["bom_lines"]))
        self.assertEqual(EVIDENCE[0], cart["evidence"][0])  # passed through (spacing_mm comes back as 600.0)
        self.assertEqual({"notebook_id": None, "label": "partial", "value_m": None, "camera_id": None, "photo": None, "array": None}, cart["evidence"][1])
        self.assertRegex(cart["created_at"], r"Z$")
        self.assertRegex(p["cart_hash"], r"^sha256:[0-9a-f]{64}$")
        self.assertTrue(p["hold_nonce"].startswith("hn_"))
        left = mps.datetime.fromisoformat(p["nonce_expires_at"]) - mps.now()
        self.assertTrue(mps.timedelta(seconds=115) < left <= mps.timedelta(seconds=120), left)
        self.assertEqual(intent, p["intent"])
        self.assertTrue(p["all_ok"])
        self.assertEqual([("price_reread", "ok", True, "$4.27 × 8 from the saved listing"),
                          ("within_limit", "ok", True, "$34.16 ≤ $40"),
                          ("delivery", "ok", True, "arrives Tue Sep 29, by Fri Oct 2"),
                          ("qty_evidence", "ok", True, "tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)"),
                          ("seller_verified", "ok", True, "Home Depot (mock): structured seller listing"),
                          ("fit", "warn", True, "fit not checked against a measurement"),
                          ("card", "ok", True, "Visa test card •••• 1111, held by the server")],
                         [(c["id"], c["status"], c["ok"], c["detail"]) for c in p["checks"]])

    def test_prepare_turns_units_into_packs_and_warns_honestly(self):
        p = self.prepare(units=8, part_id="bulk-hanger", evidence=[])
        line = p["cart"]["lines"][0]
        self.assertEqual((50, 1, 4.5, 43.5), (line["pack_qty"], line["packs"], line["shipping_usd"], line["line_total_usd"]))
        checks = {c["id"]: c for c in p["checks"]}
        self.assertEqual("$39.00 × 1 from the saved listing + $4.50 shipping", checks["price_reread"]["detail"])
        self.assertEqual(("warn", "no measurement evidence for the quantity"), (checks["qty_evidence"]["status"], checks["qty_evidence"]["detail"]))
        self.assertEqual(("warn", "Bulk Supply: listing read by the model, not a seller API"),
                         (checks["seller_verified"]["status"], checks["seller_verified"]["detail"]))
        self.assertEqual({"id": "delivery", "status": "ok", "ok": True, "detail": "arrives Tue Oct 6; no deadline set"}, checks["delivery"])
        self.assertTrue(p["all_ok"])  # warnings don't block the hold
        other = {c["id"]: c for c in self.prepare(units=10)["checks"]}  # the tape said 8
        self.assertEqual(("warn", "tape #3 4.20 m ÷ 600 mm → 8 (reported by headset), cart has 10"),
                         (other["qty_evidence"]["status"], other["qty_evidence"]["detail"]))

    def test_prepare_fit_comes_from_the_last_search(self):
        _, job = self.post("/parts/search", {"query": f"gutter hanger {uuid.uuid4().hex}", "measurement": {"label": "tape #1", "value_m": 0.130, "axis": "w"}})
        fit = next(c for c in self.prepare()["checks"] if c["id"] == "fit")
        self.assertEqual(("ok", "green, 3 mm spare"), (fit["status"], fit["detail"]))

    def test_prepare_over_the_limit_or_late_is_not_ok(self):
        self.limits(max_total_usd=20, deliver_by="tomorrow")
        p = self.prepare()
        self.assertEqual({"within_limit": "$34.16 is over your $20 limit", "delivery": "arrives Tue Sep 29, after your Sun Sep 27 deadline"},
                         {c["id"]: c["detail"] for c in p["checks"] if c["status"] == "fail"})
        self.assertFalse(p["all_ok"])
        delivery = next(c for c in self.prepare(units=2, seller_idx=1, part_id="bulk-hanger")["checks"] if c["id"] == "delivery")
        self.assertEqual(("warn", "Mystery Shop gave no delivery estimate; you asked for Sun Sep 27"), (delivery["status"], delivery["detail"]))

    def test_prepare_bad_input(self):
        body = {"session_id": self.sid, "part_id": "hidden-hanger-5k", "seller_idx": 0}
        for extra, code in (({"units_needed": 0}, 400), ({"units_needed": 501}, 400), ({"seller_idx": 9, "units_needed": 1}, 400),
                            ({"part_id": "no-such-part", "units_needed": 1}, 404), ({"part_id": "Bad_Id", "units_needed": 1}, 400),
                            ({"units_needed": 1, "bom_id": "bom-nope", "bom_lines": [0]}, 404),
                            ({}, 422), ({"units_needed": 1, "session_id": ""}, 422), ({"units_needed": 1, "evidence": EVIDENCE * 21}, 422),
                            ({"units_needed": 1, "evidence": [{"label": 3}]}, 422), ({"units_needed": 1, "evidence": [{"array": {"count": "x"}}]}, 422)):
            status, r = self.post("/checkout/prepare", {**body, **extra})
            self.assertEqual(code, status, (extra, r))
            self.assertIsInstance(r["detail"], list if code == 422 else str)
        self.assertEqual("units_needed must be between 1 and 5000, got 0",
                         self.post("/checkout/prepare", {**body, "units_needed": 0})[1]["detail"])

    # --- the hold pays, once

    def test_hold_pays_once_and_the_receipt_carries_the_chain(self):
        intent = self.limits(max_total_usd=40, deliver_by="friday", text="under $40, by Friday")["intent"]
        p = self.prepare()
        status, receipt = self.pay(p)
        self.assertEqual(200, status, receipt)
        self.assertEqual(("OFFLINE_RECEIPT", "offline", 8, 34.16), (receipt["status"], receipt["mode"], receipt["qty"], receipt["total_usd"]))
        self.assertEqual(mps.OFFLINE_LABEL, receipt["label"])
        chain = receipt["mandate"]
        self.assertEqual(["intent", "cart", "authorization", "evidence", "checks"], list(chain))
        self.assertEqual(intent, chain["intent"])
        self.assertEqual({"id": p["cart"]["id"], "hash": p["cart_hash"], "total_usd": 34.16, "units_needed": 8, "packs": 8}, chain["cart"])
        self.assertEqual({"mode": "offline", "status": "OFFLINE_RECEIPT", "approval_code": None, "hold_ms": 1043}, chain["authorization"])
        self.assertEqual(EVIDENCE, chain["evidence"])
        self.assertEqual(p["checks"], chain["checks"])
        self.assertEqual(1043, json.loads(self.get(f"/notebook/{self.sid}")[2])["entries"][-1]["mandate"]["authorization"]["hold_ms"])
        status, again = self.pay(p)
        self.assertEqual((409, "hold nonce unknown or already used; reopen checkout"), (status, again["detail"]))

    def test_sandbox_receipt_mandate_says_authorized(self):
        mps.State.sandbox = True
        status, receipt = self.pay(self.prepare(), hold_ms=1000)
        self.assertEqual(200, status)
        self.assertEqual({"mode": "sandbox", "status": "AUTHORIZED", "approval_code": receipt["approval_code"], "hold_ms": 1000},
                         receipt["mandate"]["authorization"])
        self.assertIsNone(receipt["mandate"]["intent"])  # no limits set: the app shows "no limits"

    def test_short_or_partial_hold_is_400(self):
        p = self.prepare()
        status, r = self.pay(p, hold_ms=600)
        self.assertEqual((400, "hold_ms 600 < 1000: paying needs the full hold"), (status, r["detail"]))
        for drop in (("cart_hash",), ("hold_nonce",), ("hold_ms",), ("session_id",), ("cart_hash", "hold_nonce")):
            status, r = self.pay(p, **{k: None for k in drop})
            self.assertEqual((400, "hold proof required: session_id, cart_hash, hold_nonce and hold_ms"), (status, r["detail"]), drop)
        self.assertEqual(200, self.pay(p, hold_ms=1000)[0])  # a short hold kept the nonce

    def test_tampered_or_foreign_proof_is_409(self):
        for overrides, detail in (({"qty": 9}, "cart changed since prepare"), ({"seller_idx": 2, "qty": 8}, "cart changed since prepare"),
                                  ({"cart_hash": "sha256:" + "0" * 64}, "cart_hash doesn't match"),
                                  ({"session_id": "quest-someone-else"}, "belongs to another session"),
                                  ({"hold_nonce": "hn_made-up"}, "unknown or already used")):
            status, r = self.pay(self.prepare(), **overrides)
            self.assertEqual(409, status, overrides)
            self.assertIn(detail, r["detail"])

    def test_a_new_prepare_revokes_the_old_nonce_and_nonces_expire(self):
        first, second = self.prepare(units=8), self.prepare(units=10)  # the user tapped + on the panel
        self.assertEqual(409, self.pay(first)[0])
        status, r = self.pay(second)
        self.assertEqual((200, 10), (status, r["qty"]))
        p = self.prepare()
        later = mps.now() + mps.timedelta(seconds=121)
        mps.now = lambda: later
        status, r = self.pay(p)
        self.assertEqual((409, "hold nonce expired (2 min); reopen checkout"), (status, r["detail"]))

    def test_price_change_after_prepare_is_409(self):
        p = self.prepare()
        self.prices["hidden-hanger-5k"] = 4.49
        status, r = self.pay(p)
        self.assertEqual((409, "the price or listing changed since prepare; reopen checkout"), (status, r["detail"]))

    def test_a_failed_check_is_422_with_the_checks(self):
        self.limits(max_total_usd=20)
        status, r = self.pay(self.prepare())
        self.assertEqual(422, status)
        self.assertEqual("a mandate check failed", r["detail"]["error"])
        self.assertEqual("fail", {c["id"]: c["status"] for c in r["detail"]["checks"]}["within_limit"])
        self.sid += "b"
        self.limits(max_total_usd=40)
        p = self.prepare()
        self.limits(max_total_usd=30, text="actually keep it under $30")  # tightened after prepare
        self.assertEqual(422, self.pay(p)[0])

    def test_bom_lines_ride_in_the_cart_and_cannot_be_added_later(self):
        _, bom = self.post("/parts/bom", {"part_ids": ["hidden-hanger-5k"], "counts": {"hidden-hanger-5k": 8}})
        with_bom = self.prepare(bom_id=bom["id"], bom_lines=[0])
        self.assertEqual((bom["id"], 46.16, [0]), (with_bom["cart"]["bom_id"], with_bom["cart"]["total_usd"], [b["idx"] for b in with_bom["cart"]["bom_lines"]]))
        status, r = self.pay(with_bom, bom_id=bom["id"], bom_lines=[0])
        self.assertEqual((200, 46.16), (status, r["total_usd"]))
        self.assertEqual(409, self.pay(self.prepare(), bom_id=bom["id"], bom_lines=[0])[0])

    def test_checkout_without_proof_is_unchanged_unless_required(self):
        status, r = self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 0, "qty": 2})
        self.assertEqual(200, status)
        self.assertNotIn("mandate", r)
        for bad in ({"bom_id": 5, "bom_lines": [0]}, {"bom_id": "bom-x", "bom_lines": 0}):  # a 4xx, never a crash
            self.assertIn(self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 0, "qty": 2, **bad})[0], (400, 404, 422), bad)
        mps.State.require_hold_proof = True
        status, r = self.post("/checkout", {"part_id": "hidden-hanger-5k", "seller_idx": 0, "qty": 2, "session_id": self.sid})
        self.assertEqual((400, "hold proof required: session_id, cart_hash, hold_nonce and hold_ms"), (status, r["detail"]))


class VoiceLimitsTests(B1B3Base):
    """B3 by voice (patch 0011): limit phrases in any command become show_limits; voice can't loosen a limit."""

    def test_limit_phrases_never_need_the_llm(self):
        for text, want in (("under $40", {"max_total_usd": 40.0}), ("keep it under 40 dollars", {"max_total_usd": 40.0}),
                           ("no more than $37.50 total", {"max_total_usd": 37.5}), ("it has to arrive by Friday", {"deliver_by": "2026-10-02"}),
                           ("delivered before Friday", {"deliver_by": "2026-10-01"}),
                           ("up to 40 bucks, here by tomorrow", {"max_total_usd": 40.0, "deliver_by": "2026-09-27"})):
            r = self.command(text, sid=f"{self.sid}-{len(text)}")
            [a] = r["actions"]
            self.assertEqual("show_limits", a["name"], text)
            self.assertEqual(want, {k: a["args"][k] for k in ("max_total_usd", "deliver_by") if a["args"][k]}, text)
            self.assertTrue(r["reply"].startswith("Limits set: "), r["reply"])

    def test_sizes_and_unit_prices_are_not_limits(self):
        for text in ("find a hanger under 40 mm wide", "measure up to 40 cm", "every 60 cm", "find hinges under $5 each",
                     "pulls below 3 dollars apiece", "anything under $2 per foot"):
            self.assertEqual(({}, text), mps.extract_limits(text), text)

    def test_limits_then_the_rest_of_the_command(self):
        r = self.command("under $40, arriving by Friday")
        self.assertEqual({"reply": "Limits set: under $40, by Fri Oct 2.", "job_id": None, "actions": [
            {"name": "show_limits", "args": {"intent_id": r["actions"][0]["args"]["intent_id"], "max_total_usd": 40.0,
                                             "deliver_by": "2026-10-02", "seller_policy": None, "refused": []}}]}, r)
        r = self.command("Find hangers, under $40, arriving by Friday.", sid=self.sid + "f")
        self.assertEqual("Limits set: under $40, by Fri Oct 2. Searching the supply shops.", r["reply"])
        self.assertEqual(["show_limits", "search_started"], [a["name"] for a in r["actions"]])
        self.assertEqual(r["actions"][1]["args"]["job_id"], r["job_id"])
        # the limits are the ones checkout/prepare checks against
        _, p = self.post("/checkout/prepare", {"session_id": self.sid + "f", "part_id": "hidden-hanger-5k", "seller_idx": 0, "units_needed": 8})
        self.assertEqual((40.0, "Find hangers, under $40, arriving by Friday."), (p["intent"]["max_total_usd"], p["intent"]["text"]))

    def test_voice_cannot_raise_a_limit(self):
        self.command("under $40, arriving by Friday")
        r = self.command("actually, up to $100")
        self.assertEqual("You'd need to raise that on the panel. Still under $40, by Fri Oct 2.", r["reply"])
        [a] = r["actions"]
        self.assertEqual((40.0, 100.0, 40.0), (a["args"]["max_total_usd"], a["args"]["refused"][0]["asked"], a["args"]["refused"][0]["kept"]))
        self.assertEqual(100.0, self.post("/commerce/limits", {"session_id": self.sid, "source": "panel", "max_total_usd": 100})[1]["intent"]["max_total_usd"])

if __name__ == "__main__":
    unittest.main()
