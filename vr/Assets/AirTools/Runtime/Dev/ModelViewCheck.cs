#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AirTools.Core;
using AirTools.Input;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Dev
{
    /// Model view in the running app (modelview), through AgentHarness: ModelCheck() makes sure a tape is up in the
    /// world, enters Model view with the ring's command, asserts that no world text draws, switches through every model the
    /// switcher lists (each with a timeout) and checks that the model changed and fits, goes back to the first one,
    /// walks in and checks that you land at its recommended spawn with the tape's labels back. Every result is an
    /// [AirTools.Check] model.* line; ModelCheckResult() has the report. Needs the backend for the scans
    /// (ServerConfig.SetOverride("http://127.0.0.1:8004")); without it only the built-in facade is listed.
    /// modelwheel: the model floats centred in front of the eyes (not at table height) with the wheel under it facing
    /// them; each model is opened through the wheel (spin it to the lens, pinch the lens); a flick settles on a card and
    /// the loop wraps; turning away recentres it, and so does the Recentre chip.
    public static class ModelViewCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;

        static ModelSwitcher Switcher => Services.Get<ModelSwitcher>();
        static ModelWheel Wheel => Services.Get<ModelWheel>();
        static TabletopController Table => Services.Get<TabletopController>();
        static ModelViewDeclutter Declutter => Services.Get<ModelViewDeclutter>();
        static SceneStreamer Streamer => Services.Get<SceneStreamer>();

        /// Model view on (true: the scene on the table) or off (grow back in).
        public static string ModelView(bool on)
        {
            AppCommands.SetTabletop(on);
            return Status();
        }

        /// The switcher's models (the server's listing minus the test variants, then the built-in facade), the one on the
        /// table, the window, and what Model view hides.
        public static string Status()
        {
            var s = Switcher; var t = Table; var d = Declutter;
            if (s == null || t == null) return "no ModelSwitcher / TabletopController (run AirTools ▸ Wire Main Scene)";
            return $"mode={AppState.Mode} on_table={t.OnTable} placement={t.placement} scale={t.ScaleLabel} refitting={t.Refitting} recentres={t.Recentres} pending_spawn={(t.HasPendingSpawn ? t.PendingSpawnSite : "-")} " +
                   $"hidden={(d != null ? d.HiddenCount : -1)} gate={AirTools.UI.ModelView.HidesAnnotations} | {s.Describe()}";
        }

        /// show_model's path (AppCommands.ShowModel): a site id or a spoken name.
        public static string Show(string site)
        {
            bool ok = AppCommands.ShowModel(site);
            return $"{(ok ? "ok" : "refused")} | {Status()}";
        }

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

        static Pose? s_LookFrom;

        /// A capture pose for Play Without XR (the head doesn't move there): turn the rig about the eye so the view looks
        /// at the model and the wheel under it (modelwheel: halfway between the model's centre and the wheel's lens, then
        /// `pitchBias` degrees further down; on a table, the model's footprint). ModelLookReset() puts the rig back.
        public static string Look(float pitchBias = 0f)
        {
            var t = Table; var cam = Camera.main;
            if (t == null || t.root == null || t.rig == null || cam == null) return "no tabletop / rig / camera";
            if (!t.OnTable) return $"not in Model view (mode={AppState.Mode}): ModelView(true) first";
            var rig = t.rig;
            s_LookFrom ??= new Pose(rig.position, rig.rotation);
            var eye = cam.transform.position;
            var wheel = Wheel;
            var centre = t.Floating && wheel != null && wheel.Shown
                ? Vector3.Lerp(t.ModelCentreWorld, wheel.transform.position, 0.5f)
                : t.root.transform.TransformPoint(t.SceneCentreLocal());
            var dir = centre - eye;
            var flat = new Vector3(dir.x, 0f, dir.z);
            float pitch = Mathf.Atan2(-dir.y, flat.magnitude) * Mathf.Rad2Deg + pitchBias;
            var want = Quaternion.LookRotation(flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward, Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
            var turn = want * Quaternion.Inverse(cam.transform.rotation);
            rig.SetPositionAndRotation(eye + turn * (rig.position - eye), turn * rig.rotation);
            return $"looking {pitch.ToString("0", C)}° down at the {t.ScaleLabel} model | {Geometry()} | {Status()}";
        }

        // ---------------- modelwheel: the wheel and the placement ----------------

        /// Where the model and the wheel are from your eye now: the model's centre ahead / up / to the side and how wide it
        /// looks, the wheel's lens below the eye line, its top against the model's lowest edge, and whether it faces you.
        public static string Geometry()
        {
            var t = Table; var w = Wheel; var cam = Camera.main;
            if (t == null || cam == null) return "no tabletop / camera";
            var eye = cam.transform.position;
            float hdg = ModelViewLayout.HeadingDeg(cam.transform.forward, t.AnchorHeadingDeg);
            var c = t.ModelCentreWorld;
            var flat = new Vector3(c.x - eye.x, 0f, c.z - eye.z);
            float yaw = Vector3.SignedAngle(ModelViewLayout.Dir(hdg), flat, Vector3.up);
            var half = t.ModelHalfSize;
            float span = 2f * Mathf.Atan(Mathf.Max(half.x, Mathf.Max(half.y, half.z)) / Mathf.Max(0.1f, flat.magnitude)) * Mathf.Rad2Deg;
            string model = $"model ahead={flat.magnitude.ToString("0.00", C)} m dy={(c.y - eye.y).ToString("+0.00;-0.00", C)} m yaw={yaw.ToString("0.0", C)}° " +
                           $"span≈{span.ToString("0", C)}° floor+{(c.y - (t.rig != null ? t.rig.position.y : 0f)).ToString("0.00", C)} m";
            if (w == null || !w.Shown) return $"{model} | wheel hidden";
            var lens = w.transform.position;
            float dist = Vector3.Distance(eye, lens);
            float down = TabletopFit.BelowEyeDeg(eye, lens);
            float top = down - Mathf.Atan2(w.LensTop, dist) * Mathf.Rad2Deg;
            float bottom = ModelViewLayout.ModelBottomDownDeg(eye, hdg, c, half);
            float faces = Vector3.Dot(w.transform.forward, (lens - eye).normalized);
            float wyaw = Vector3.SignedAngle(ModelViewLayout.Dir(hdg), new Vector3(lens.x - eye.x, 0f, lens.z - eye.z), Vector3.up);
            return $"{model} | wheel lens {down.ToString("0.0", C)}° down (top {top.ToString("0.0", C)}°, model bottom {bottom.ToString("0.0", C)}°) " +
                   $"{dist.ToString("0.00", C)} m yaw={wyaw.ToString("0.0", C)}° faces={faces.ToString("0.000", C)}";
        }

        /// The wheel: spin `steps` models along (+ next, − previous; not-downloaded ones skipped).
        public static string Spin(int steps)
        {
            var w = Wheel;
            if (w == null || !w.Shown) return $"the wheel isn't up (ModelView(true) first) | {Status()}";
            string to = w.SpinBy(steps);
            return $"spinning to {to} | {WheelState()}";
        }

        public static string Flick(float velocity)
        {
            var w = Wheel;
            if (w == null || !w.Shown) return $"the wheel isn't up | {Status()}";
            w.Flick(velocity);
            return WheelState();
        }

        /// Pinch the lens: open the model under it.
        public static string Pinch()
        {
            var w = Wheel;
            if (w == null || !w.Shown) return $"the wheel isn't up | {Status()}";
            bool ok = w.PinchLens();
            return $"{(ok ? "pinched" : "nothing under the lens")} | {WheelState()}";
        }

        public static string WheelState()
        {
            var w = Wheel;
            return $"{(w != null ? w.Brief() : "no ModelWheel (AirTools ▸ Wire Main Scene)")} | {Geometry()}";
        }

        public static string Recentre()
        {
            var t = Table;
            bool ok = t != null && t.Recentre("harness");
            return $"{(ok ? "recentred" : "not in Model view")} | {Geometry()}";
        }

        /// "front" (default) or "table": where Model view puts the model; applied at once when it's up.
        public static string Placement(string where)
        {
            var t = Table;
            if (t == null) return "no TabletopController";
            string w = (where ?? "").Trim().ToLowerInvariant();
            t.placement = w.StartsWith("t") ? ModelPlacement.OnTable : ModelPlacement.FrontOfMe;
            if (t.OnTable) t.Apply(true);   // re-placed from the same anchor (a table: in front of you now)
            return $"placement={t.placement} | {Geometry()}";
        }

        /// Turn the rig `deg` about the eye (Play Without XR: the head can't turn) — to see the auto-recentre.
        public static string Turn(float deg)
        {
            var t = Table; var cam = Camera.main;
            if (t == null || t.rig == null || cam == null) return "no rig / camera";
            var eye = cam.transform.position;
            var turn = Quaternion.AngleAxis(deg, Vector3.up);
            t.rig.SetPositionAndRotation(eye + turn * (t.rig.position - eye), turn * t.rig.rotation);
            return $"turned {deg.ToString("0", C)}° | {Geometry()}";
        }

        public static string LookReset()
        {
            var t = Table;
            if (t == null || t.rig == null || !s_LookFrom.HasValue) return "nothing to reset";
            t.rig.SetPositionAndRotation(s_LookFrom.Value.position, s_LookFrom.Value.rotation);
            s_LookFrom = null;
            return "rig back";
        }

        public static string Run(float timeoutPerSite = 90f)
        {
            if (!s_Done) return "ModelCheck is already running: poll ModelCheckResult()";
            if (Switcher == null || Table == null || Streamer == null || Declutter == null)
                return "missing ModelSwitcher / TabletopController / SceneStreamer / ModelViewDeclutter (run AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Summary = "running";
            s_Done = false;
            if (!DemoRunner.Run(Routine(timeoutPerSite), ex => { s_Summary = $"error {ex.GetType().Name}: {ex.Message}"; s_Done = true; }, () => s_Done = true))
            {
                s_Done = true;
                return "another routine is running (DemoRunner busy)";
            }
            return "started: poll ModelCheckResult()";
        }

        static int s_Pass, s_Total;

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < end) yield return null;
        }

        static IEnumerator Routine(float timeoutPerSite)
        {
            s_Pass = s_Total = 0;
            var switcher = Switcher; var table = Table; var declutter = Declutter; var streamer = Streamer;
            var root = table.root;
            var mode = Services.Get<ModeController>();
            bool Settled() => AppState.Mode == AppMode.Tabletop && table.OnTable && (mode == null || (mode.VisualMode == AppMode.Tabletop && !mode.IsTransitioning));

            // 0. In the world with at least one tape up (its labels are what Model view must hide and bring back).
            if (AppState.Mode != AppMode.World)
            {
                AppCommands.SetTabletop(false);
                if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
                yield return Until(() => AppState.Mode == AppMode.World && (mode == null || !mode.IsTransitioning), 6f);
            }
            string tape = EnsureTape();
            // modelwheel: something bought, so "take it home" has a part to stand beside the model (a harness receipt,
            // nothing is paid; forgotten at the end if it was ours).
            var home = Services.Get<AirTools.Parts.TakeItHome>();
            bool homeInjected = InjectPurchase(home);
            yield return null;
            yield return null;
            int labelsBefore = declutter.VisibleWorldText().Count;
            string first = switcher.Current;
            Check("model.world_labels", labelsBefore > 0, $"visible world text in the world: {labelsBefore} ({tape})");

            // 1. Model view: the model only.
            AppCommands.SetTabletop(true);
            yield return Until(Settled, 8f);
            yield return Until(() => switcher.Shown, 2f);
            var wheel = Wheel;
            yield return Until(() => wheel != null && wheel.Shown, 2f);
            yield return null;
            var shown = declutter.VisibleWorldText();
            Check("model.enter", Settled() && switcher.Shown && wheel != null && wheel.Shown, $"mode={AppState.Mode} on_table={table.OnTable} switcher={switcher.Shown} wheel={(wheel != null && wheel.Shown)} scale={table.ScaleLabel}");
            // modelwheel: the model centred in front of the eyes, not at table height; the wheel under it, facing them.
            Check("model.front", InFront(table, out string front), front);
            Check("model.wheel_under", WheelUnder(table, wheel, out string under), under);
            // modelwheel: nothing heads-up on the wheel — no Model view toast (the lens says it), and with the status line up
            // the wheel steps under it; the bought parts on the floor beside the model, clear of the wheel.
            int toasts0 = table.Toasts;
            Check("model.hud_clear", !ModelToastUp(table, toasts0, out string toast), toast);
            yield return StatusLineClear(table, wheel);
            yield return Until(() => home == null || (!home.Pending && home.OnTable.Count > 0), 3f);
            Check("model.take_home", TakeHome(table, home, out string parts), parts);
            Check("model.no_world_text", shown.Count == 0 && AirTools.UI.ModelView.HidesAnnotations,
                $"visible={shown.Count} hidden_renderers={declutter.HiddenCount}{(shown.Count > 0 ? " [" + string.Join("; ", shown.GetRange(0, Mathf.Min(6, shown.Count))) + "]" : "")}");
            var wrist = Services.Get<AirTools.Parts.LimitsChip>();
            if (wrist != null)
            {
                wrist.Refresh();
                Check("model.wrist_scale", wrist.Line1.Contains(table.ScaleLabel) && table.ScaleLabel.StartsWith("1:"), $"wrist=\"{wrist.Line1}\" scale={table.ScaleLabel}");
            }

            // 2. Every model the switcher lists.
            switcher.RefreshList();
            yield return Until(() => switcher.Listed, 8f);
            var sites = new List<string>(switcher.Sites);
            Check("model.listed", sites.Count >= 1 && sites[sites.Count - 1] == ModelSites.BuiltIn,
                $"{sites.Count} models: [{string.Join(", ", sites)}] (hidden from the switcher: test packages)");
            var order = new List<string>(sites);
            order.Remove(first);
            order.Add(first);   // end where we started
            foreach (var site in order)
            {
                var before = root.Content;
                float t0 = Time.realtimeSinceStartup;
                // modelwheel: through the wheel — spin the model to the lens, then pinch the lens.
                bool spun = wheel != null && wheel.SpinToSite(site);
                if (spun) yield return Until(() => wheel.Dial != null && wheel.Dial.Settled && wheel.LensSite == site, 4f);
                bool pinched = spun && wheel.LensSite == site && wheel.PinchLens();
                if (!pinched) switcher.Choose(site);   // the wheel refused: still load it, the check says so
                yield return Until(() => switcher.Loading == null && switcher.Queued == null && !table.Refitting && Settled(), timeoutPerSite);
                // The wheel redraws its cards the frame the switcher's state changes (the switcher may tick after it: a
                // frame or two); a highlight that never comes within half a second is a real miss.
                yield return Until(() => wheel != null && wheel.CardFor(site) is var c && c != null && c.selected, 0.5f);
                float took = Time.realtimeSinceStartup - t0;
                bool on = switcher.Current == site;
                bool changed = site == ModelSites.Current(root.IsRuntimePackage, root.Site) && (root.Content != before || site == first && sites.Count == 1);
                var box = table.FootprintLocal(out bool any);
                float longest = any ? TabletopFit.LongestSide(box.size) * table.Scale : 0f;
                bool fits = any && longest <= table.ActiveFitMax + 1e-3f && Mathf.Abs(root.transform.lossyScale.x - table.Scale) < 1e-5f && root.IsVisible;
                var text = declutter.VisibleWorldText();
                var card = wheel != null ? wheel.CardFor(site) : null;
                bool highlighted = card != null && card.selected && wheel.LensSite == site;
                bool ahead = InFront(table, out string where);
                bool toastUp = ModelToastUp(table, toasts0, out string toastWhat);
                Check($"model.site.{site}", on && changed && fits && text.Count == 0 && highlighted && pinched && ahead && !toastUp,
                    $"{(on ? "on view" : "NOT on view")} in {took.ToString("0.0", C)} s · {table.ScaleLabel} longest={longest.ToString("0.00", C)} m " +
                    $"changed={changed} fits={fits} via_wheel={pinched} lens_card_selected={highlighted} lens=\"{wheel?.lensDetail?.text}\" visible_text={text.Count} " +
                    $"{(toastUp ? toastWhat + " " : "")}{where} result=\"{switcher.LastResult}\"");
            }

            // modelwheel: a flick coasts and settles on a card that opens; the loop wraps (a full turn is the same model).
            if (wheel != null && wheel.Shown)
            {
                int ticks0 = wheel.Ticks;
                wheel.Flick(-4f);
                yield return Until(() => wheel.Dial.Settled, 5f);
                bool rests = wheel.Dial.Settled && Mathf.Abs(wheel.Dial.Position - Mathf.Round(wheel.Dial.Position)) < 1e-3f;
                bool opens = wheel.LensSite != null && switcher.Available(wheel.LensSite);
                Check("model.wheel_flick", rests && opens && wheel.Ticks > ticks0,
                    $"ticks {wheel.Ticks - ticks0} · rests on {wheel.LensSite} (opens={opens}) pos={wheel.Dial.Position.ToString("0.000", C)}");
                string from = wheel.LensSite;
                int open = 0;
                foreach (var s in sites) if (switcher.Available(s)) open++;
                wheel.SpinBy(open);
                yield return Until(() => wheel.Dial.Settled, 6f);
                Check("model.wheel_loops", wheel.LensSite == from && open > 0,
                    $"{open} models along: {from} → {wheel.LensSite} | {wheel.Brief()}");
                wheel.SpinToSite(first);
                yield return Until(() => wheel.Dial.Settled, 4f);
            }

            // modelwheel: turn away → it comes back in front of you; the Recentre chip does the same.
            if (table.Floating && table.rig != null && Camera.main != null)
            {
                int r0 = table.Recentres;
                Turn(90f);
                yield return Until(() => table.Recentres > r0 && !table.Gliding, 4f);
                yield return null;
                bool back = InFront(table, out string after);
                Check("model.recentre_turn", table.Recentres > r0 && back, $"recentres {r0} → {table.Recentres} ({table.LastRecentre}) {after}");
                Turn(-90f);
                yield return Until(() => table.Recentres > r0 + 1 && !table.Gliding, 4f);
                int r1 = table.Recentres;
                if (wheel != null) wheel.Press(ModelWheelAction.Recentre, 0);
                yield return Until(() => !table.Gliding, 2f);
                yield return null;
                Check("model.recentre_chip", table.Recentres > r1 && InFront(table, out string chip) && WheelUnder(table, wheel, out _),
                    $"recentres {r1} → {table.Recentres} ({table.LastRecentre}) {Geometry()}");
                yield return null;
                Check("model.take_home_follows", TakeHome(table, home, out string follows), $"after {table.Recentres} recentres: {follows}");
            }

            // 3. Walk in: at the first model's recommended spawn, the tape's labels back.
            table.WalkInAtSpawn();
            yield return Until(() => AppState.Mode == AppMode.World && !table.OnTable && (mode == null || (mode.VisualMode == AppMode.World && !mode.IsTransitioning)), 8f);
            yield return null;
            yield return null;
            bool atSpawn = SpawnDistance(table, streamer, out float dist);
            Check("model.walk_in_spawn", AppState.Mode == AppMode.World && !table.HasPendingSpawn && atSpawn && dist < 0.05f,
                $"mode={AppState.Mode} feet→spawn {dist.ToString("0.000", C)} m (spawn known={atSpawn})");
            int labelsAfter = declutter.VisibleWorldText().Count;
            Check("model.labels_back", !AirTools.UI.ModelView.HidesAnnotations && declutter.HiddenCount == 0 && labelsAfter >= labelsBefore,
                $"visible world text {labelsBefore} → {labelsAfter}, hidden renderers {declutter.HiddenCount}");
            if (homeInjected && home != null) home.ResetSession();   // the harness receipt: forgotten
            s_Summary = $"model.summary {s_Pass}/{s_Total}";
            Log.Check("model.summary", s_Pass == s_Total, $"{s_Pass}/{s_Total} {Status()}");
        }

        /// modelwheel: the model's centre straight ahead of the eye at 0.85–1.25 m, at eye height or a little below (not
        /// at a table's height), spanning 30–55° of view.
        static bool InFront(TabletopController t, out string detail)
        {
            detail = Geometry();
            var cam = Camera.main;
            if (t == null || cam == null || !t.OnTable) return false;
            var eye = cam.transform.position;
            var c = t.ModelCentreWorld;
            var flat = new Vector3(c.x - eye.x, 0f, c.z - eye.z);
            float yaw = Vector3.Angle(ModelViewLayout.Dir(ModelViewLayout.HeadingDeg(cam.transform.forward, t.AnchorHeadingDeg)), flat);
            var half = t.ModelHalfSize;
            float span = 2f * Mathf.Atan(Mathf.Max(half.x, Mathf.Max(half.y, half.z)) / Mathf.Max(0.1f, flat.magnitude)) * Mathf.Rad2Deg;
            return t.Floating && flat.magnitude > 0.85f && flat.magnitude < 1.25f && c.y <= eye.y + 0.08f && c.y >= eye.y - 0.15f   // a tall model is raised a little (up to ~6 cm) so the wheel fits under it
                   && yaw < 3f && span > 30f && span < 55f;
        }

        /// modelwheel: the wheel's lens 16–34° below the eye line, its top under the model's lowest edge, straight ahead,
        /// square to the line of sight.
        static bool WheelUnder(TabletopController t, ModelWheel w, out string detail)
        {
            detail = Geometry();
            var cam = Camera.main;
            if (t == null || w == null || !w.Shown || cam == null) return false;
            var eye = cam.transform.position;
            var lens = w.transform.position;
            float dist = Vector3.Distance(eye, lens);
            float down = TabletopFit.BelowEyeDeg(eye, lens);
            float top = down - Mathf.Atan2(w.LensTop, dist) * Mathf.Rad2Deg;
            float hdg = ModelViewLayout.HeadingDeg(cam.transform.forward, t.AnchorHeadingDeg);
            float bottom = ModelViewLayout.ModelBottomDownDeg(eye, hdg, t.ModelCentreWorld, t.ModelHalfSize);
            float yaw = Vector3.Angle(ModelViewLayout.Dir(hdg), new Vector3(lens.x - eye.x, 0f, lens.z - eye.z));
            float faces = Vector3.Dot(w.transform.forward, (lens - eye).normalized);
            return down > ModelViewLayout.WheelMinDownDeg - 0.1f && down < ModelViewLayout.WheelMaxDownDeg + 0.1f
                   && (top >= bottom + 1f || down >= ModelViewLayout.WheelMaxDownDeg - 0.1f) && yaw < 3f && faces > 0.999f;
        }

        // ---------------- modelwheel: heads-up surfaces and the bought parts ----------------

        /// Model view toasted its model and scale since `since` (TabletopController.Toasts: entry and re-fits, whatever the
        /// words), or a "Model view · …" toast is up. The toast that is up, if any, is named either way (diagnosis).
        static bool ModelToastUp(TabletopController table, int since, out string detail)
        {
            var t = AirTools.UI.UiToast.Toast;
            bool showing = t != null && t.Showing;
            bool counted = table.Toasts > since;
            bool up = counted || (showing && t.Message.StartsWith("Model view", System.StringComparison.Ordinal));
            detail = $"{(up ? "Model view toasted over the wheel" : "no Model view toast on the wheel")} (toasts {since} → {table.Toasts}, " +
                     $"last \"{table.LastNote}\"; up now: {(showing ? $"\"{t.Message}\"" : "none")}; placement {table.placement}, wheel {(table.wheel != null ? "wired" : "not wired")})";
            return up;
        }

        static float LensDown(ModelWheel w, Vector3 eye) => TabletopFit.BelowEyeDeg(eye, w.transform.position);

        /// The guide rail on for a moment (DemoMode's status line, 17° below the gaze): the wheel steps under the line as
        /// seen looking at the model, then back up when it goes.
        static IEnumerator StatusLineClear(TabletopController table, ModelWheel wheel)
        {
            var line = AirTools.UI.StatusLine.Current;
            var cam = Camera.main;
            if (line == null || wheel == null || !wheel.Shown || cam == null)
            {
                Check("model.status_line_clear", false, "no status line / wheel / camera");
                yield break;
            }
            var eye = cam.transform.position;
            bool was = AirTools.UI.GuideRail.Enabled;
            float before = LensDown(wheel, eye);
            AirTools.UI.GuideRail.Enabled = true;
            yield return null;
            yield return null;
            bool live = line.Live;
            float during = LensDown(wheel, eye);
            float dist = Vector3.Distance(eye, wheel.transform.position);
            float cardTop = during - Mathf.Atan2(wheel.LensTop, dist) * Mathf.Rad2Deg;
            float gaze = TabletopFit.BelowEyeDeg(eye, table.ModelCentreWorld);
            float lineBottom = gaze + line.belowGazeDeg + Mathf.Atan2(line.MaxSize.y * 0.5f, line.distance) * Mathf.Rad2Deg;
            AirTools.UI.GuideRail.Enabled = was;
            yield return null;
            yield return null;
            float after = LensDown(wheel, eye);
            bool back = was || Mathf.Abs(after - before) < 0.05f;
            Check("model.status_line_clear", live && cardTop >= lineBottom - 0.05f && back,
                $"rail on: line live={live}, its bottom {lineBottom.ToString("0.0", C)}° below the eye (looking at the model, {gaze.ToString("0.0", C)}°), " +
                $"the lens card's top {cardTop.ToString("0.0", C)}° (lens {before.ToString("0.0", C)}° → {during.ToString("0.0", C)}°); rail back {(was ? "on" : "off")}: lens {after.ToString("0.0", C)}°");
        }

        /// A harness receipt for the take-it-home hanger when nothing has been bought (no payment; ResetSession forgets it).
        static bool InjectPurchase(AirTools.Parts.TakeItHome home)
        {
            if (home == null || home.Purchases.Count > 0) return false;
            var loader = Services.Get<AirTools.Parts.PartLoader>();
            var spec = loader != null && loader.catalog != null ? loader.catalog.Spec(PartScenarios.Hanger) : null;
            if (spec == null) return false;
            home.OnPurchased(new AirTools.Parts.CheckoutReceipt { status = "HARNESS", qty = 1, total_usd = 0f, seller = "ModelCheck" }, spec);
            return true;
        }

        /// The bought parts stand on the floor at true size, right of the floating model and clear of the wheel
        /// (ModelViewLayout.PartsClearance).
        static bool TakeHome(TabletopController t, AirTools.Parts.TakeItHome home, out string detail)
        {
            if (home == null) { detail = "no TakeItHome"; return false; }
            if (home.OnTable.Count == 0) { detail = $"no parts beside the model ({home.LastPlacement}; {home.Purchases.Count} bought)"; return false; }
            var c = t.ModelCentreWorld;
            var right = Vector3.Cross(Vector3.up, ModelViewLayout.Dir(t.AnchorHeadingDeg));
            float floor = t.rig != null ? t.rig.position.y : 0f;
            bool ok = true;
            var parts = new List<string>();
            foreach (var p in home.OnTable)
            {
                if (p == null) continue;
                Bounds wb = default;
                bool any = false;
                foreach (var r in p.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r is LineRenderer) continue;
                    if (!any) { wb = r.bounds; any = true; } else wb.Encapsulate(r.bounds);
                }
                if (!any) { ok = false; parts.Add($"{p.name}: nothing drawn"); continue; }
                float left = float.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(i % 2 == 0 ? wb.min.x : wb.max.x, (i / 2) % 2 == 0 ? wb.min.y : wb.max.y, i < 4 ? wb.min.z : wb.max.z);
                    left = Mathf.Min(left, Vector3.Dot(corner - c, right));
                }
                bool floorOk = Mathf.Abs(wb.min.y - floor) <= 0.01f, sizeOk = Mathf.Abs(p.transform.lossyScale.x - 1f) < 1e-4f;
                bool clear = left >= ModelViewLayout.PartsClearance - 0.02f;
                ok &= floorOk && sizeOk && clear;
                parts.Add($"{p.Spec.id}: bottom {(wb.min.y - floor).ToString("+0.000;-0.000", C)} m off the floor, ×{p.transform.lossyScale.x.ToString("0.00", C)}, " +
                          $"left edge {left.ToString("0.00", C)} m right of the model");
            }
            detail = $"{home.OnTable.Count} part(s) {home.LastPlacement}: [{string.Join("; ", parts)}]";
            return ok && t.Floating;
        }

        /// Where a standing person's eye is: the camera, or 1.6 m above it when Play Without XR leaves it at the rig's feet.
        static Vector3 StandingEye(Camera cam) =>
            cam.transform.position + (cam.transform.localPosition.y < 0.5f ? Vector3.up * 1.6f : Vector3.zero);

        static string F(Vector3 v) => $"({v.x.ToString("0.00", C)}, {v.y.ToString("0.00", C)}, {v.z.ToString("0.00", C)})";

        /// A tape across whatever is ahead (two points either side of straight ahead where the view meets the scene),
        /// unless one is up.
        static string EnsureTape()
        {
            var measure = Services.Get<MeasureTool>();
            var hub = Services.Get<ToolInputHub>();
            if (measure == null || hub == null) return "no measure tool";
            if (measure.viewRoot != null && measure.viewRoot.GetComponentInChildren<MeasureLabel>() != null) return "a tape was up";
            var cam = Camera.main;
            if (cam == null) return "no camera";
            var tools = Services.Get<ToolManager>();
            if (tools != null && tools.Active != ToolKind.Measure) tools.Equip(ToolKind.Measure);
            var eye = StandingEye(cam);
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, fwd);
            // Two points at the same pitch either side of straight ahead; shallower pitches when the scan doesn't reach
            // the floor in front of you (the kitchen), steeper when it's a facade far off.
            var hits = new List<Vector3>();
            foreach (float pitch in new[] { 12f, 18f, 25f, 35f, 8f, 4f })
            {
                foreach (float yaw in new[] { 6f, 3f, 12f })
                {
                    hits.Clear();
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var dir = Quaternion.AngleAxis(side * yaw, Vector3.up) * (Quaternion.AngleAxis(pitch, right) * fwd);
                        if (Physics.Raycast(eye, dir, out var hit, 60f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) hits.Add(hit.point);
                    }
                    if (hits.Count == 2) break;
                }
                if (hits.Count == 2) break;
            }
            if (hits.Count < 2) return $"nothing ahead to tape (from {F(eye)})";
            foreach (var p in hits)
            {
                var pose = ToolInputHub.RayPose(eye, p);
                hub.SetPointerOverride(ToolHand.Right, pose);
                hub.RaisePressStart(ToolHand.Right, pose);
                hub.RaisePressEnd(ToolHand.Right, pose);
                hub.SetPointerOverride(ToolHand.Right, null);
            }
            hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            return $"taped: {measure.LastAction}";
        }

        /// How far your feet are from the loaded site's recommended spawn (root space → world).
        static bool SpawnDistance(TabletopController table, SceneStreamer streamer, out float distance)
        {
            distance = float.PositiveInfinity;
            var root = table.root;
            if (root == null) return false;
            Vector3 feet;
            bool got = root.IsRuntimePackage ? streamer.TrySpawnLocal(root.Manifest, out feet, out _) : SceneLoader.SpawnLocal(root.Package, out feet, out _);
            if (!got) return false;
            var w = root.transform.TransformPoint(feet);
            var f = table.FeetWorld();
            distance = Vector2.Distance(new Vector2(w.x, w.z), new Vector2(f.x, f.z));
            return true;
        }
    }
}
#endif
