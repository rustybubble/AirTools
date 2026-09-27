#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Dev
{
    // gaze-catalog: the Catalog follows what the wearer looks at, in the running app. The eye is aimed through the
    // tracker's own override (GazeFocusTracker.eyeOverride): the same sampling, classification, ~1.2 s hold, request per
    // focus and header chip as a head on the headset. unity command eval --code
    // 'return AirTools.Dev.AgentHarness.CatalogFocusCheck();' then poll AgentHarness.CatalogFocusResult().
    // Results are [AirTools.Check] catalog.focus.* lines.
    public static partial class AgentHarness
    {
        static GazeFocusTracker GazeT => Services.Get<GazeFocusTracker>();

        /// The exterior table's roof and wall categories (backend catalog_tables.FOCUS_TABLES) and Grok's roof extras.
        static readonly HashSet<string> s_RoofIds = new HashSet<string>
            { "solar-panels", "gutters", "roof-vents", "hvac", "skylights", "chimney-caps", "flashing", "snow-guards", "satellite-mounts" };
        static readonly HashSet<string> s_WallIds = new HashSet<string>
            { "wall-hvac", "windows", "window-ac", "doors", "siding", "exterior-lights", "wall-vents", "downspouts" };

        /// One line: the tracker (sampling or why paused, its frame, the last sample, the held focus) and the catalog's
        /// focus (followed / pinned, what the list shown is for, the chip, the first categories).
        public static string CatalogFocusState()
        {
            var g = GazeT; var w = CatalogW;
            if (g == null || w == null) return "no GazeFocusTracker / CatalogWindow";
            var m = w.Model;
            var hit = g.LastHit;
            string cats = string.Join(", ", (m.Data?.categories ?? new List<CatalogCategory>()).Take(6).Select(c => c.id));
            return $"gaze: {(g.Paused != null ? "paused (" + g.Paused + ")" : "sampling")} samples={g.Samples} override={(g.eyeOverride.HasValue ? "on" : "off")} " +
                   $"last={g.LastSample}{(g.LastSubject != null ? " (" + g.LastSubject + ")" : "")} on {g.LastSurface}" +
                   (hit.Hit ? $" at {F(hit.Point)} h={g.Frame.HeightOf(hit.Point):0.00} m tilt={GazeFocusMath.TiltDeg(hit.Normal, g.Frame.Up):0}°" : "") +
                   $" held={g.Filter.Held}{(g.Filter.Subject != null ? " (" + g.Filter.Subject + ")" : "")} | frame {g.Frame}" +
                   $" | catalog: open={w.IsOpen} site={m.Site} focus={m.Focus}{(m.FocusPinned ? " PINNED" : "")} gaze={m.Gaze} shown={m.ShownFocus} " +
                   $"foci=[{string.Join(",", m.Data?.foci ?? new List<string>())}] chip=\"{(w.focusChip != null ? w.focusChip.Text : "-")}\" source={m.Source} fetches={w.Fetches} [{cats}]";
        }

        /// Aim the eye from `from` (world; default the wearer's eye) at a SceneRoot-space point; the tracker samples along
        /// it until GazeClear().
        public static string GazeAt(float x, float y, float z)
        {
            var g = GazeT;
            if (g == null || Root == null) return "no GazeFocusTracker / SceneRoot";
            var target = Root.transform.TransformPoint(new Vector3(x, y, z));
            var eye = WearerEye();
            g.eyeOverride = new Pose(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            return $"eye {F(eye)} → {F(target)} | {CatalogFocusState()}";
        }

        /// Aim the eye from a SceneRoot-space point at another.
        public static string GazeFrom(float ox, float oy, float oz, float x, float y, float z)
        {
            var g = GazeT;
            if (g == null || Root == null) return "no GazeFocusTracker / SceneRoot";
            var eye = Root.transform.TransformPoint(new Vector3(ox, oy, oz));
            var target = Root.transform.TransformPoint(new Vector3(x, y, z));
            g.eyeOverride = new Pose(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            return $"eye {F(eye)} → {F(target)} | {CatalogFocusState()}";
        }

        /// Back to the head.
        public static string GazeClear()
        {
            var g = GazeT;
            if (g != null) g.eyeOverride = null;
            return CatalogFocusState();
        }

        /// Play Without XR leaves the camera at the rig's feet: a standing eye there.
        static Vector3 WearerEye()
        {
            var cam = Camera.main;
            if (cam == null) return Vector3.up * 1.6f;
            return cam.transform.position + (cam.transform.localPosition.y < 0.5f && !UnityEngine.XR.XRSettings.isDeviceActive ? Vector3.up * 1.6f : Vector3.zero);
        }

        // ---------------- targets from the structure ----------------

        /// A point on the scan's roof or a wall and an eye to look at it from (world), from the structure planes: the
        /// biggest sloped plane high up (else the biggest level one in the roof band) / the biggest near-vertical plane.
        /// The eye is the wearer's own when the ray from it lands on that plane; otherwise `distance` off the plane on its
        /// open side. False: no such plane, or no clear view of it.
        static bool FocusTarget(string kind, float distance, out Vector3 eye, out Vector3 target, out string how)
        {
            eye = target = default;
            how = "";
            var r = Root; var g = GazeT;
            if (r?.Structure == null || r.StructureSpace == null || g == null) { how = "no structure"; return false; }
            if (!g.Frame.Valid) g.RebuildFrame();
            var frame = g.Frame;
            var space = r.StructureSpace;
            var rootT = r.transform;
            var planes = r.Structure.Planes;
            var order = new List<(int i, float score, Vector3 n, Vector3 c)>();
            for (int i = 0; i < planes.Length; i++)
            {
                var p = planes[i];
                if (p.outline3d == null || p.outline3d.Length < 3) continue;
                var c = Vector3.zero;
                foreach (var q in p.outline3d) c += q;
                c /= p.outline3d.Length;
                var cRoot = rootT.InverseTransformPoint(space.TransformPoint(c));
                var nRoot = rootT.InverseTransformDirection(space.TransformDirection(p.normal)).normalized;
                if (Vector3.Dot(nRoot, frame.Up) < 0f) nRoot = -nRoot;
                float tilt = GazeFocusMath.TiltDeg(nRoot, frame.Up), h = frame.HeightOf(cRoot);
                float area = GazeFocusMath.OutlineArea(p.outline, r.Calibration);
                float score = -1f;
                if (kind == "roof")
                {
                    float band = Mathf.Max(GazeFocusMath.RoofMinM, GazeFocusMath.RoofBandSloped * frame.Span);
                    if (tilt >= 20f && tilt <= 60f && h >= band) score = 2f * area;
                    else if (tilt < GazeFocusMath.LevelMaxDeg && h >= Mathf.Max(GazeFocusMath.RoofMinM, GazeFocusMath.RoofBandLevel * frame.Span)) score = area;
                }
                else if (tilt >= 80f && tilt <= 100f) score = area;
                if (score > 0f) order.Add((i, score, nRoot, cRoot));
            }
            var wearer = WearerEye();
            foreach (var (i, _, nRoot, cRoot) in order.OrderByDescending(o => o.score).Take(4))
            {
                // The centroid, else a point halfway to each corner (a concave outline's centroid may be off it).
                var candidates = new List<Vector3> { cRoot };
                foreach (var q in planes[i].outline3d) candidates.Add(Vector3.Lerp(cRoot, rootT.InverseTransformPoint(space.TransformPoint(q)), 0.5f));
                foreach (var pRoot in candidates)
                {
                    if (kind == "wall" && g.InOpening(pRoot)) continue;   // a window on it: that's an opening's focus
                    var t = rootT.TransformPoint(pRoot);
                    if (OnPlane(wearer, t, i)) { eye = wearer; target = t; how = $"{planes[i].id} from the wearer's eye"; return true; }
                    foreach (float side in new[] { 1f, -1f })
                    {
                        var e = rootT.TransformPoint(pRoot + nRoot * (side * distance));
                        if (OnPlane(e, t, i)) { eye = e; target = t; how = $"{planes[i].id} from {distance:0} m off its {(side > 0 ? "front" : "back")}"; return true; }
                    }
                }
            }
            // No structure plane of that kind with a clear view (the hospital's layer has only level planes): rays at the
            // scan itself, from a ring around it (a wall: at mid height, looking in; a roof: above it, looking down 50°),
            // classified by the tracker.
            var want = kind == "roof" ? CatalogFocus.Roof : CatalogFocus.Wall;
            if (ScanBounds(out var bmin, out var bmax))
            {
                var centre = (bmin + bmax) * 0.5f;
                float radius = Mathf.Max(bmax.x - bmin.x, bmax.z - bmin.z) * (kind == "roof" ? 0.3f : 0.7f);
                for (int a = 0; a < 360; a += 20)
                {
                    var outward = new Vector3(Mathf.Cos(a * Mathf.Deg2Rad), 0f, Mathf.Sin(a * Mathf.Deg2Rad));
                    float height = kind == "roof" ? frame.Top + 20f : frame.Ground + frame.Span * 0.4f;
                    var eRoot = new Vector3(centre.x, 0f, centre.z) + outward * radius;
                    eRoot += frame.Up * (height - Vector3.Dot(frame.Up, eRoot));
                    var dRoot = kind == "roof" ? (-outward * Mathf.Cos(50f * Mathf.Deg2Rad) - frame.Up * Mathf.Sin(50f * Mathf.Deg2Rad)) : -outward;
                    var e = rootT.TransformPoint(eRoot);
                    var d = rootT.TransformDirection(dRoot).normalized;
                    if (g.Probe(e, d, out var hit) != want || !hit.Hit) continue;
                    eye = e;
                    target = rootT.TransformPoint(hit.Point);
                    how = $"a mesh ray from {a}° around the scan ({g.LastSurface})";
                    return true;
                }
            }
            how = order.Count == 0 ? $"no {kind} plane, and no {kind} from rays around the scan" : $"no clear view of the {kind} planes or from rays around the scan";
            return false;
        }

        /// The scan's collision bounds (SceneRoot space).
        static bool ScanBounds(out Vector3 min, out Vector3 max)
        {
            min = max = default;
            var r = Root;
            if (r == null || r.Content == null) return false;
            bool any = false;
            var world = new Bounds();
            int layer = SceneLayers.SceneSurface;
            foreach (var c in r.Content.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.gameObject.layer != layer || !c.enabled) continue;
                if (!any) { world = c.bounds; any = true; } else world.Encapsulate(c.bounds);
            }
            if (!any) return false;
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = -min;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? world.min.x : world.max.x, (i & 2) == 0 ? world.min.y : world.max.y, (i & 4) == 0 ? world.min.z : world.max.z);
                var q = r.transform.InverseTransformPoint(corner);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            }
            return true;
        }

        /// The first scan hit from `eye` towards `target` is on structure plane `plane`, near the target.
        static bool OnPlane(Vector3 eye, Vector3 target, int plane)
        {
            var d = target - eye;
            if (d.sqrMagnitude < 1e-4f) return false;
            if (!Physics.Raycast(eye, d.normalized, out var hit, d.magnitude + 1f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) return false;
            if (Vector3.Distance(hit.point, target) > 1.0f) return false;
            var r = Root;
            var pk = r.StructureSpace.InverseTransformPoint(hit.point);
            var p = r.Structure.Planes[plane];
            return Mathf.Abs(p.SignedDistance(pk)) * r.Calibration < 0.3f;
        }

        // ---------------- CatalogFocusCheck ----------------

        static readonly List<string> s_FocusLog = new List<string>();
        static bool s_FocusDone, s_FocusPass;
        static int s_FocusPassN, s_FocusTotal;
        static string s_FocusSummary = "not run";

        /// The catalog follows the gaze (Play mode; poll CatalogFocusResult()): on `site` (default Zabel, loaded if it
        /// isn't) open the Catalog → aim the eye at the roof (a structure plane) → after the hold the catalog leads with
        /// roof categories → at a wall → wall categories → a short glance at the roof changes nothing → pin (the header
        /// chip) → a long look at the roof changes nothing → unpin → it catches up → back to the wall from the headset's
        /// copy at once. `fixture`: /catalog answers from Assets/AirTools/Fixtures/Catalog (focus/…); `server`: another
        /// laptop for this run (e.g. http://localhost:8010).
        public static string CatalogFocusCheck(string site = "zabel-gymnasium", bool fixture = false, string server = null)
        {
            if (!Application.isPlaying) return "Play mode only";
            s_FocusLog.Clear(); s_FocusDone = s_FocusPass = false; s_FocusPassN = s_FocusTotal = 0; s_FocusSummary = "running…";
            if (!string.IsNullOrEmpty(server)) ServerConfig.SetOverride(server);
            if (!DemoRunner.Run(CatalogFocusFlow(site, fixture), ex => { s_FocusLog.Add($"exception: {ex}"); s_FocusSummary = $"exception: {ex.Message}"; },
                    () => { s_FocusDone = true; var g = GazeT; if (g != null) g.eyeOverride = null; }))
                return "another routine is running (DemoRunner busy)";
            return $"catalog focus check started on {site} (fixture={fixture}) against {ServerConfig.Current} — poll AgentHarness.CatalogFocusResult()";
        }

        public static string CatalogFocusResult() =>
            $"{(s_FocusDone ? (s_FocusPass ? "PASS" : "FAIL") : "running…")} {s_FocusSummary}\n{string.Join("\n", s_FocusLog)}";

        static void FocusLine(string id, bool ok, string detail)
        {
            s_FocusTotal++;
            if (ok) s_FocusPassN++;
            Log.Check(id, ok, detail);
            s_FocusLog.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static string FirstCategories(CatalogModel m, int n = 5) =>
            string.Join(", ", (m.Data?.categories ?? new List<CatalogCategory>()).Take(n).Select(c => $"{c.Title}:{c.items.Count}"));

        static string FirstId(CatalogModel m) => m.Data?.categories != null && m.Data.categories.Count > 0 ? m.Data.categories[0].id : null;

        static bool IsRoofFirst(CatalogModel m)
        {
            string id = FirstId(m);
            var cat = m.Data?.categories?.FirstOrDefault();
            return id != null && (s_RoofIds.Contains(id) || (cat != null && cat.Title.ToLowerInvariant().Contains("roof")));
        }

        static IEnumerator CatalogFocusFlow(string site, bool fixture)
        {
            var w = CatalogW; var g = GazeT;
            if (w == null || g == null) { s_FocusSummary = "no CatalogWindow / GazeFocusTracker (run AirTools ▸ Wire Main Scene)"; yield break; }
            var client = Services.Get<CatalogClient>();
            if (client != null) client.testTransport = fixture ? CatalogFixtures.FromProject : null;

            // 1. The site, in the world.
            var streamer = Services.Get<SceneStreamer>();
            if (Root == null || Root.Site != site || Root.Structure == null)
            {
                AppCommands.OpenChest();
                AppCommands.LoadSite(site);
                yield return CatalogWait(() => Root != null && Root.Site == site && Root.Structure != null && (streamer == null || !streamer.Loading), 120f);
            }
            if (AppState.Mode != AppMode.World) AppCommands.OpenChest();
            yield return CatalogWait(() => AppState.Mode == AppMode.World, 8f);
            yield return new WaitForSecondsRealtime(0.5f);
            if (Root == null || Root.Site != site || Root.Structure == null) { FocusLine("catalog.focus.site", false, $"{site} not loaded ({Root?.Site ?? "-"})"); Finish(); yield break; }
            g.RebuildFrame();
            FocusLine("catalog.focus.frame", g.Frame.Valid, $"{site}: {g.Frame}");

            // 2. Open the Catalog; the laptop's catalog says which foci the site answers.
            var m = w.Model;
            if (m.FocusPinned) w.ToggleFocusPin();
            AppCommands.ShowCatalog();
            w.Fetch(w.Site, force: true);
            yield return CatalogWait(() => m.Source == CatalogSource.Server && m.FocusSupported, 12f);
            FocusLine("catalog.focus.supported", m.FocusSupported,
                $"source={m.Source} foci=[{string.Join(",", m.Data?.foci ?? new List<string>())}] env={m.Data?.environment} ({(fixture ? "fixtures" : ServerConfig.Current)}{(w.LastFetchError != null ? ", " + w.LastFetchError : "")})");

            // 3. The roof and a wall, from the structure.
            bool roofOk = FocusTarget("roof", 14f, out var roofEye, out var roofAt, out string roofHow);
            bool wallOk = FocusTarget("wall", 10f, out var wallEye, out var wallAt, out string wallHow);
            FocusLine("catalog.focus.targets", roofOk && wallOk, $"roof {(roofOk ? F(ToRoot(roofAt)) : "-")} ({roofHow}); wall {(wallOk ? F(ToRoot(wallAt)) : "-")} ({wallHow})");
            if (!roofOk || !wallOk) { Finish(); yield break; }
            var roofPose = new Pose(roofEye, Quaternion.LookRotation(roofAt - roofEye, Vector3.up));
            var wallPose = new Pose(wallEye, Quaternion.LookRotation(wallAt - wallEye, Vector3.up));

            // 4. Look at the roof: after the hold, roof categories lead.
            g.eyeOverride = roofPose;
            float t0 = CNow;
            yield return CatalogWait(() => g.Filter.Held == CatalogFocus.Roof, 6f);
            float held = CNow - t0;
            yield return CatalogWait(() => m.ShownFocus == CatalogFocus.Roof, 8f);
            float listed = CNow - t0;
            yield return null;
            FocusLine("catalog.focus.roof", m.ShownFocus == CatalogFocus.Roof && IsRoofFirst(m) && w.focusChip != null && w.focusChip.Text.StartsWith("Looking at: the roof"),
                $"sample={g.LastSample} on {g.LastSurface} held {g.Filter.Held} after {held:0.00} s, list after {listed:0.00} s; chip \"{w.focusChip?.Text}\"; [{FirstCategories(m)}]");

            // 5. At a wall: wall categories (after the hold: not at once).
            int fetches = w.Fetches;
            g.eyeOverride = wallPose;
            t0 = CNow;
            yield return CatalogWait(() => g.Filter.Held == CatalogFocus.Wall, 6f);
            held = CNow - t0;
            yield return CatalogWait(() => m.ShownFocus == g.Filter.Held, 8f);
            listed = CNow - t0;
            yield return null;
            bool wallShown = m.ShownFocus == CatalogFocus.Wall && s_WallIds.Contains(FirstId(m) ?? "");
            FocusLine("catalog.focus.wall", wallShown && (w.focusChip?.Text ?? "").StartsWith("Looking at: the wall"),
                $"sample={g.LastSample}{(g.LastSubject != null ? " (" + g.LastSubject + ")" : "")} on {g.LastSurface} held {g.Filter.Held} after {held:0.00} s, list after {listed:0.00} s ({w.Fetches - fetches} fetch); chip \"{w.focusChip?.Text}\"; [{FirstCategories(m)}]");
            FocusLine("catalog.focus.hold", held >= g.holdSeconds - 0.3f && held <= g.holdSeconds + 1.5f,
                $"the wall took {held:0.00} s to hold (hold {g.holdSeconds} s at {g.samplesPerSecond}/s)");

            // 6. A glance at the roof (0.6 s) changes nothing.
            var before = m.ShownFocus;
            fetches = w.Fetches;
            g.eyeOverride = roofPose;
            yield return new WaitForSecondsRealtime(0.6f);
            g.eyeOverride = wallPose;
            yield return new WaitForSecondsRealtime(1.0f);
            FocusLine("catalog.focus.glance", m.ShownFocus == before && w.Fetches == fetches && g.Filter.Held == before,
                $"after a 0.6 s glance: shown={m.ShownFocus} held={g.Filter.Held} fetches +{w.Fetches - fetches}");

            // 7. Pin (the header chip's button): a long look at the roof changes nothing.
            bool pressed = w.focusChip != null && w.focusChip.isActiveAndEnabled && w.focusChip.Press();
            if (!pressed) w.ToggleFocusPin();
            yield return null;
            g.eyeOverride = roofPose;
            yield return new WaitForSecondsRealtime(g.holdSeconds + 1.3f);
            FocusLine("catalog.focus.pin", m.FocusPinned && m.ShownFocus == before && g.Filter.Held == CatalogFocus.Roof && (w.focusChip?.Text ?? "").StartsWith("Pinned:"),
                $"pinned={m.FocusPinned} via {(pressed ? "the chip" : "ToggleFocusPin")}; the gaze holds {g.Filter.Held}, the list stays {m.ShownFocus}; chip \"{w.focusChip?.Text}\"");

            // 8. Unpin: it catches up with the gaze (the roof), from the headset's copy at once.
            float tu = CNow;
            pressed = w.focusChip != null && w.focusChip.isActiveAndEnabled && w.focusChip.Press();
            if (!pressed) w.ToggleFocusPin();
            yield return CatalogWait(() => m.ShownFocus == CatalogFocus.Roof, 4f);
            float caught = CNow - tu;
            FocusLine("catalog.focus.unpin", !m.FocusPinned && m.ShownFocus == CatalogFocus.Roof && IsRoofFirst(m) && caught < 0.3f,
                $"unpinned → {m.ShownFocus} in {caught:0.000} s ({m.Source}); chip \"{w.focusChip?.Text}\"; [{FirstCategories(m, 3)}]");

            // 9. Back at the wall: switched from the copy kept on the headset the moment the hold completes.
            g.eyeOverride = wallPose;
            yield return CatalogWait(() => g.Filter.Held != CatalogFocus.Roof, 6f);
            float th = CNow;
            yield return CatalogWait(() => m.ShownFocus == g.Filter.Held, 4f);
            FocusLine("catalog.focus.cache", m.ShownFocus == g.Filter.Held && CNow - th < 0.2f,
                $"held {g.Filter.Held} → list in {CNow - th:0.000} s (kept per site and focus)");

            g.eyeOverride = null;
            Finish();

            void Finish()
            {
                s_FocusPass = s_FocusTotal > 0 && s_FocusPassN == s_FocusTotal;
                s_FocusSummary = $"catalog focus: {s_FocusPassN}/{s_FocusTotal} | {CatalogFocusState()}";
                Log.Check("catalog.focus.check", s_FocusPass, s_FocusSummary);
            }
        }

        /// The capture setup: turn and move the rig so the eye looks at the site's roof or a wall (as the check finds
        /// them), open the Catalog in front of that view and let the tracker follow the camera. Then capture the game view.
        /// CatalogFocusViewReset() puts the rig back.
        public static string CatalogFocusView(string what = "roof", float distance = 0f)
        {
            var rigT = Rig; var cam = Camera.main; var g = GazeT;
            if (rigT == null || cam == null || g == null || Root == null) return "no rig / camera / tracker / SceneRoot";
            string kind = (what ?? "roof").Trim().ToLowerInvariant() == "wall" ? "wall" : "roof";
            if (!FocusTarget(kind, distance > 0f ? distance : kind == "roof" ? 14f : 10f, out var eye, out var target, out string how)) return $"no view: {how}";
            s_FocusViewFrom ??= new Pose(rigT.position, rigT.rotation);
            var want = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
            var turn = want * Quaternion.Inverse(cam.transform.rotation);
            var e = cam.transform.position;
            rigT.SetPositionAndRotation(e + turn * (rigT.position - e), turn * rigT.rotation);
            rigT.position += eye - cam.transform.position;
            g.eyeOverride = new Pose(cam.transform.position, cam.transform.rotation);   // the tracker samples what the camera shows
            AppCommands.ShowCatalog();
            return $"looking at the {kind} ({how}) from {F(ToRoot(eye))} | {CatalogFocusState()}";
        }

        static Pose? s_FocusViewFrom;

        public static string CatalogFocusViewReset()
        {
            var g = GazeT;
            if (g != null) g.eyeOverride = null;
            var rigT = Rig;
            if (rigT == null || !s_FocusViewFrom.HasValue) return "nothing to reset";
            rigT.SetPositionAndRotation(s_FocusViewFrom.Value.position, s_FocusViewFrom.Value.rotation);
            s_FocusViewFrom = null;
            return "rig back";
        }
    }
}
#endif
