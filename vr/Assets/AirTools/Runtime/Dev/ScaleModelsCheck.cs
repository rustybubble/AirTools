#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Scene;
using AirTools.Structure;
using UnityEngine;

namespace AirTools.Dev
{
    /// scalemodels, in the running app (through AgentHarness). Every result is an [AirTools.Check] line; ScaleModelsResult()
    /// has the report.
    /// - **ScaleCheck()** loads the kitchen if it isn't showing, then checks:
    ///   - its default ×1.63 (after Reset, if a person's scale was saved);
    ///   - the context's scale / scale_source, and the Settings and wrist wording;
    ///   - the dw1 gap ≈ 712 × 963 × 725 mm, and the tape across it in calibrated metres (World mode);
    ///   - that a person's Set scale overrides the default (source "user"), and that Reset brings ×1.63 back.
    ///   It cleans up: dw1 back in, the tapes undone, the default scale.
    /// - **ModelsOffline()** simulates the laptop gone (ServerConfig.SetOverride to an unroutable address), then checks:
    ///   - the listing fails, but the switcher still lists every model on the headset;
    ///   - the rest reads "Not downloaded" and won't open;
    ///   - a cached model (the kitchen) loads from the headset's cache.
    ///   It puts the server back and lists again.
    public static class ScaleModelsCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        /// Nothing answers here (TEST-NET-like, unroutable from the headset and the laptop): requests hang until our clock.
        public const string Unreachable = "http://10.255.255.1:8004";

        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;
        static int s_Pass, s_Total;

        static SceneStreamer Streamer => Services.Get<SceneStreamer>();
        static SceneRoot Root => Services.Get<SceneRoot>();

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

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

        static string Start(IEnumerator routine, string name, System.Action cleanup = null)
        {
            if (!s_Done) return $"{name}: a check is already running: poll ScaleModelsResult()";
            if (Streamer == null || Root == null) return "no SceneStreamer / SceneRoot (AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = $"{name} running";
            s_Done = false;
            if (!DemoRunner.Run(routine, ex => { cleanup?.Invoke(); s_Summary = $"{name} error {ex.GetType().Name}: {ex.Message}"; s_Done = true; },
                    () => { s_Summary = $"{name} {s_Pass}/{s_Total}"; s_Done = true; }))
            {
                s_Done = true;
                return "another routine is running (DemoRunner busy)";
            }
            return $"{name} started: poll ScaleModelsResult()";
        }

        static string F3(float v) => v.ToString("0.000", C);
        static string Mm(Vector3 m) => $"{m.x * 1000f:0} × {m.y * 1000f:0} × {m.z * 1000f:0} mm";

        // ---------------- (1) the kitchen's scale ----------------

        public static string ScaleCheck() => Start(ScaleRoutine(), "ScaleCheck");

        static IEnumerator ScaleRoutine()
        {
            var streamer = Streamer; var root = Root;
            // 0. The kitchen on screen.
            if (!root.IsRuntimePackage || root.Site != "kitchen")
            {
                AppCommands.LoadSite("kitchen");
                yield return null;
                yield return Until(() => !streamer.Loading, 90f);
            }
            if (!root.IsRuntimePackage || root.Site != "kitchen") { Check("scale.kitchen.loaded", false, $"the kitchen didn't load: {streamer.Status}"); yield break; }

            // 1. As loaded: the default, unless a person's scale was saved for it (then Reset, and say so).
            float def = streamer.SiteDefaultScale;
            Check("scale.kitchen.default", Mathf.Abs(def - SiteScales.Kitchen) < 1e-4f, $"SiteScales default ×{SiteScales.Factor(def)} (want ×{SiteScales.Factor(SiteScales.Kitchen)})");
            string asLoaded = $"loaded at ×{root.Calibration.ToString("0.0000", C)} ({SiteScales.Wire(streamer.ScaleSource)})";
            if (streamer.ScaleSource == ScaleSource.User)
            {
                AppCommands.ResetScale();
                asLoaded += $"; a person's scale was saved on this device: Reset → ×{root.Calibration.ToString("0.0000", C)} ({SiteScales.Wire(streamer.ScaleSource)})";
            }
            Check("scale.kitchen.applied", Mathf.Abs(root.Calibration - SiteScales.Kitchen) < 1e-4f && streamer.ScaleSource == ScaleSource.SiteDefault, asLoaded);

            // 2. The context and the wording.
            ContextCheck("scale.kitchen.context", SiteScales.Kitchen, "site_default");
            string chip = ScenePanel.ScaleChip(root);
            Check("scale.kitchen.settings", chip == SiteScales.DefaultLine(SiteScales.Kitchen), $"Settings: \"{chip}\"");
            var wrist = Services.Get<AirTools.Parts.LimitsChip>();
            if (wrist != null)
            {
                wrist.Refresh();
                Check("scale.kitchen.wrist", wrist.Line1.Contains(SiteScales.DefaultWord(SiteScales.Kitchen)), $"wrist line 1: \"{wrist.Line1}\"");
            }

            // 3. The dishwasher's gap in calibrated metres: the file's box, then the tape across it.
            bool removed = AppCommands.RemoveComponent("dw1");
            yield return null;
            if (!removed || !Gaps.TryGet(out var gap, "dw1")) { Check("scale.kitchen.gap", false, $"dw1 didn't come out (parts: {streamer.PartsStatus})"); yield break; }
            var size = gap.SizeM;
            bool sizeOk = Mathf.Abs(size.x - 0.712f) < 0.004f && Mathf.Abs(size.y - 0.963f) < 0.004f && Mathf.Abs(size.z - 0.725f) < 0.004f;
            Check("scale.kitchen.gap", sizeOk, $"dw1 gap {Mm(size)} (want ≈ 712 × 963 × 725; raw {Mm(size / Mathf.Max(root.Calibration, 1e-6f))})");
            var cavity = Gaps.CurrentContext();
            Check("scale.kitchen.gap_context", cavity != null && System.Math.Abs(System.Convert.ToDouble(cavity["h_m"]) - 0.963) < 0.004,
                cavity != null ? $"context.cavity w {cavity["w_m"]} h {cavity["h_m"]} d {cavity["d_m"]} m" : "no context.cavity");

            if (AppState.Mode != AppMode.World)
            {
                AppCommands.SetTabletop(false);
                AppCommands.OpenChest();
                var mode = Services.Get<ModeController>();
                yield return Until(() => AppState.Mode == AppMode.World && (mode == null || !mode.IsTransitioning), 8f);
            }
            bool taped = AppCommands.MeasureCavity("dw1");
            yield return null;
            if (taped && Gaps.TryMeasured("dw1", out var tape))
            {
                bool Near(float got, float want) => float.IsNaN(got) || (got > want * 0.85f && got < want * 1.15f);
                Check("scale.kitchen.tape", Near(tape.x, size.x) && Near(tape.y, size.y) && Near(tape.z, size.z),
                    $"the tape across dw1 reads {Mm(tape)} (the file's {Mm(size)}; NaN = no reading on that axis)");

                // 4. A person's Set scale overrides the default (2 % on the width tape), and Reset brings ×1.63 back.
                var width = ScaleCalibration.LatestTape();
                if (width != null && AppCommands.SetScale(width.ValueSI * 1.02))
                {
                    bool user = streamer.ScaleSource == ScaleSource.User && Mathf.Abs(root.Calibration - SiteScales.Kitchen * 1.02f) < 0.002f;
                    Check("scale.kitchen.override", user, $"Set scale on the width tape ×1.02 → ×{root.Calibration.ToString("0.0000", C)} ({SiteScales.Wire(streamer.ScaleSource)}) · \"{ScenePanel.ScaleChip(root)}\"");
                    ContextCheck("scale.kitchen.override_context", root.Calibration, "user");
                }
                else Check("scale.kitchen.override", false, $"Set scale refused: {ScaleCalibration.LastResult}");
                AppCommands.ResetScale();
                Check("scale.kitchen.reset", Mathf.Abs(root.Calibration - SiteScales.Kitchen) < 1e-4f && streamer.ScaleSource == ScaleSource.SiteDefault,
                    $"Reset → ×{root.Calibration.ToString("0.0000", C)} ({SiteScales.Wire(streamer.ScaleSource)})");
                AppCommands.Undo();   // the three gap tapes
            }
            else Check("scale.kitchen.tape", false, $"MeasureCavity(dw1) didn't tape (mode {AppState.Mode})");

            // 5. Clean up: dw1 back in, the default scale.
            AppCommands.RestoreComponent("dw1");
            if (streamer.ScaleSource != ScaleSource.SiteDefault) AppCommands.ResetScale();
            yield return null;
        }

        static void ContextCheck(string id, float scale, string source)
        {
            var ctx = GrokContext.Build(GrokView.Snapshot(null, null));
            bool ok = ctx.TryGetValue("scale", out var s) && System.Math.Abs(System.Convert.ToDouble(s) - scale) < 1e-4
                      && ctx.TryGetValue("scale_source", out var src) && (string)src == source;
            ctx.TryGetValue("scale_source", out var got);
            Check(id, ok, $"context site={(ctx.TryGetValue("site", out var site) ? site : "-")} scale={(s ?? "-")} scale_source={(got ?? "-")} (want {scale.ToString("0.####", C)} / {source})");
        }

        // ---------------- (3) the capture: the ring's Settings item, and the window ----------------

        static Transform s_Anchor;
        static bool s_AnchorSaved;
        static Pose? s_RigFrom;

        /// For a capture in Play mode (Simulator: no hands): the palm ring forced open in front of the camera
        /// (controllerAnchor = null, CLAUDE.md), spun so Settings (the gear) is on the lens; `window` also opens the
        /// Settings window (then SettingsLookAt() turns the view to it). SettingsLookReset() undoes all of it.
        public static string SettingsLook(bool window = false)
        {
            var palm = Services.Get<AirTools.Input.PalmMenu>();
            if (palm == null || palm.ring == null || palm.ring.items == null) return "no PalmMenu / ToolRing (AirTools ▸ Wire Main Scene)";
            if (!s_AnchorSaved) { s_Anchor = palm.controllerAnchor; s_AnchorSaved = true; }
            palm.controllerAnchor = null;
            palm.Force(true);
            var items = palm.ring.items;
            int i = System.Array.FindIndex(items, x => x.label == ScenePanel.Title);
            if (i < 0) return $"no \"{ScenePanel.Title}\" on the ring: [{string.Join(" · ", System.Array.ConvertAll(items, x => x.label))}] (Wire Main Scene)";
            palm.ring.SpinTo(i);
            var panel = Services.Get<ScenePanel>();
            if (window && panel != null && !panel.IsOpen) panel.Open();
            string glyph = items[i].iconText != null && items[i].iconText.text.Length > 0 ? $"U+{(int)items[i].iconText.text[0]:X4}" : "none";
            bool inAtlas = items[i].iconText != null && items[i].iconText.font != null && items[i].iconText.font.HasCharacter(AirTools.UI.Icons.Settings[0]);
            return $"ring [{string.Join(" · ", System.Array.ConvertAll(items, x => x.label))}]; {ScenePanel.Title} (item {i}) on the lens, glyph {glyph} " +
                   $"(gear-six U+E272; in the icon atlas={inAtlas}: false → AirTools ▸ Build UI Assets, then Wire); window open={(panel != null && panel.IsOpen)}";
        }

        /// Turn the rig about the eye so the view centres on the Settings window (open it with SettingsLook(true) a frame
        /// before).
        public static string SettingsLookAt()
        {
            var panel = Services.Get<ScenePanel>();
            var cam = Camera.main;
            var rig = Services.Get<SceneStreamer>()?.rig;
            if (panel == null || !panel.IsOpen || cam == null || rig == null) return "open it first: SettingsLook(true)";
            s_RigFrom ??= new Pose(rig.position, rig.rotation);
            var eye = cam.transform.position;
            var dir = panel.window.transform.position - eye;
            if (dir.sqrMagnitude < 1e-6f) return "the window is at the eye";
            var turn = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Inverse(cam.transform.rotation);
            rig.SetPositionAndRotation(eye + turn * (rig.position - eye), turn * rig.rotation);
            return $"looking at {ScenePanel.Title}: \"{(panel.title != null ? panel.title.text : "?")}\"";
        }

        public static string SettingsLookReset()
        {
            var palm = Services.Get<AirTools.Input.PalmMenu>();
            if (palm != null) { palm.Force(null); if (s_AnchorSaved) palm.controllerAnchor = s_Anchor; }
            s_AnchorSaved = false;
            Services.Get<ScenePanel>()?.Close();
            var rig = Services.Get<SceneStreamer>()?.rig;
            if (rig != null && s_RigFrom.HasValue) rig.SetPositionAndRotation(s_RigFrom.Value.position, s_RigFrom.Value.rotation);
            s_RigFrom = null;
            return "ring and window back";
        }

        // ---------------- (2) the models with the laptop away ----------------

        static string s_SavedOverride;
        static bool s_Swapped;

        public static string ModelsOffline(bool load = true) => Start(OfflineRoutine(load), "ModelsOffline", PutServerBack);

        static void PutServerBack()
        {
            if (!s_Swapped) return;
            s_Swapped = false;
            ServerConfig.SetOverride(s_SavedOverride);
            Streamer?.ListSites(null);
        }

        static IEnumerator OfflineRoutine(bool load)
        {
            var streamer = Streamer;
            var switcher = Services.Get<ModelSwitcher>();

            // 0. Online first, so the kept listing is fresh.
            bool got = false;
            streamer.ListSites(_ => got = true);
            yield return Until(() => got, 10f);
            bool wasOnline = streamer.ServerListed;
            int knownOnline = streamer.KnownSites.Count;
            streamer.HeadsetCount(out int onBefore, out int totalBefore);
            s_Report.Add($"online={wasOnline} server={ServerConfig.Current} known={knownOnline} {PrefetchPlan.CountText(onBefore, totalBefore)}" +
                         $"{(Services.TryGet<ScenePrefetcher>(out var pf) ? " · " + pf.Describe() : "")}");

            // 1. The laptop goes away.
            s_SavedOverride = ServerConfig.OverrideUrl;
            s_Swapped = true;
            ServerConfig.SetOverride(Unreachable);
            got = false;
            streamer.ListSites(_ => got = true);
            yield return Until(() => got, 15f);
            Check("models.offline.listing", !streamer.ServerListed && streamer.Sites.Count == 0, $"GET /scenes from {Unreachable}: {streamer.LastError}");
            Check("models.offline.kept", streamer.KnownSites.Count > 0 && streamer.KnownSites.Count >= knownOnline,
                $"the headset still knows {streamer.KnownSites.Count} models (online {knownOnline}): {string.Join(", ", streamer.KnownSites.ConvertAll(s => s.site))}");

            // 2. The switcher: every cached model and the built-in facade; the rest dimmed.
            yield return null;   // the switcher re-reads KnownSites on its next Update
            yield return null;
            if (switcher == null) Check("models.offline.switcher", false, "no ModelSwitcher (AirTools ▸ Wire Main Scene)");
            else
            {
                var cached = streamer.OfflineSites();
                var row = new List<string>(switcher.Sites);
                var missing = cached.FindAll(s => !row.Contains(s));
                bool builtInLast = row.Count > 0 && row[row.Count - 1] == ModelSites.BuiltIn;
                Check("models.offline.switcher", missing.Count == 0 && builtInLast && cached.Count > 0,
                    $"on the headset [{string.Join(", ", cached)}] all in the row [{string.Join(", ", row)}]{(missing.Count > 0 ? $"; missing {string.Join(", ", missing)}" : "")}");
                var dim = row.FindAll(s => !switcher.Available(s));
                var wrong = dim.FindAll(s => streamer.OnHeadset(s));
                Check("models.offline.not_downloaded", wrong.Count == 0, $"Not downloaded: [{string.Join(", ", dim)}]");
                if (dim.Count > 0)
                {
                    bool refused = !switcher.Choose(dim[0]);
                    Check("models.offline.tap_not_downloaded", refused, $"a tap on {dim[0]}: {switcher.LastAction}");
                }
                Check("models.offline.kitchen", streamer.OnHeadset("kitchen"), "the kitchen is on the headset");

                // 3. A cached model opens from the headset.
                if (load)
                {
                    string site = cached.Contains("kitchen") ? "kitchen" : cached.Count > 0 ? cached[0] : null;
                    if (site == null) Check("models.offline.load", false, "nothing cached to load");
                    else
                    {
                        float t0 = Time.realtimeSinceStartup;
                        AppCommands.LoadSite(site);
                        yield return null;
                        yield return Until(() => !streamer.Loading, 60f);
                        var root = Root;
                        Check("models.offline.load", !streamer.LoadFailed && root.IsRuntimePackage && root.Site == site,
                            $"{site} from the headset's cache in {Time.realtimeSinceStartup - t0:0.0} s: {streamer.Status}");
                    }
                }
                s_Report.Add(switcher.Describe());
            }

            // 4. The laptop comes back.
            PutServerBack();
            if (wasOnline)
            {
                got = false;
                streamer.ListSites(_ => got = true);
                yield return Until(() => got, 10f);
                Check("models.online_again", streamer.ServerListed, $"GET /scenes from {ServerConfig.Current}: {(streamer.ServerListed ? "answered" : streamer.LastError)}");
            }
        }
    }
}
#endif
