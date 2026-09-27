#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Parts;
using UnityEngine;

namespace AirTools.Dev
{
    /// settings-assets: Settings ▸ 3D models and the card's Compare in the running app, through AgentHarness:
    /// AssetModeCheck(partId) finds the placed server part (or places `partId` into the gap of the part taken out last),
    /// switches the setting to HF, then LLM+CAD (the other way round when it shows HF) — each time waiting for the swap and
    /// checking the model came from that mode (asset.mode, tier, made_by) with the part's anchor and turn unchanged — then
    /// Compare twice (the other model and back, the setting staying), optionally (twin) loads a second copy in the other
    /// mode in front of it for a one-shot capture, and runs the pure
    /// Talk-style checks. Side by side: AssetModeLook(), capture, AssetCompare(), capture again from the same pose. Every result is an [AirTools.Check] assetmode.* line; AssetModeResult() has the report.
    /// Needs the backend with asset modes (ServerConfig.SetOverride("http://127.0.0.1:8009")).
    public static class AssetModeCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;
        static PartInstance s_Target, s_Twin;
        static Pose? s_LookFrom;
        static int s_Pass, s_Total;

        static PartTool Tool => Services.Get<PartTool>();
        static PlacementEditor Editor => Services.Get<PlacementEditor>();
        static AssetModeSwitcher Switcher => Services.Get<AssetModeSwitcher>();
        static PartLoader Loader => Services.Get<PartLoader>();

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

        /// The setting, the switcher, and every server part: its shown mode, badge, model and anchor.
        public static string State()
        {
            var sw = Switcher; var t = Tool; var e = Editor;
            var lines = new List<string> { $"3D models={UserPrefs.Label(UserPrefs.Assets)} talk={UserPrefs.Label(UserPrefs.Talk)} | {(sw != null ? sw.Describe() : "no AssetModeSwitcher")}" };
            if (t != null)
            {
                var seen = new HashSet<PartInstance>();
                void Row(PartInstance p, string where)
                {
                    if (p == null || !seen.Add(p)) return;
                    string anchor = e != null && e.TryAnchor(p, out var a, out _) ? a.ToString("F3") : "-";
                    lines.Add($"{where} {p.Spec?.id} src={p.Source} mode={p.Spec?.asset?.mode ?? "-"} badge=\"{AssetModes.TierLine(p.Spec?.asset) ?? "-"}\" " +
                              $"made_by={p.Spec?.asset?.made_by ?? "-"} note=\"{p.Spec?.asset?.note}\" model={ModelName(p)} anchor={anchor} status={sw?.StatusFor(p) ?? "-"}");
                }
                Row(t.Held, "held");
                foreach (var p in t.PlacedParts) Row(p, "placed");
            }
            if (sw != null) lines.Add(sw.DescribeCad());   // cad
            return string.Join("\n", lines);
        }

        static string ModelName(PartInstance p) => p != null && p.Model != null && p.Model.childCount > 0 ? p.Model.GetChild(0).name : "-";

        static PartInstance FindServerPart(string partId)
        {
            var t = Tool;
            if (t == null) return null;
            bool Match(PartInstance p) => AssetModeSwitcher.IsServerPart(p) && (string.IsNullOrEmpty(partId) || p.Spec.id == partId) && p != s_Twin;
            if (Match(t.Selected)) return t.Selected;
            foreach (var p in t.PlacedParts) if (Match(p)) return p;
            return null;
        }

        public static string Run(string partId = null, bool twin = false, float timeout = 150f)
        {
            if (!s_Done) return "AssetModeCheck is running: poll AssetModeResult()";
            if (Tool == null || Editor == null || Switcher == null || Loader == null)
                return "missing PartTool / PlacementEditor / AssetModeSwitcher / PartLoader (run AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = "running";
            s_Done = false;
            if (!DemoRunner.Run(Routine(partId, twin, timeout), ex => { s_Summary = $"error {ex.GetType().Name}: {ex.Message}"; s_Done = true; }, () => s_Done = true))
            {
                s_Done = true;
                return "couldn't start (not playing, or another routine is running)";
            }
            return "started: poll AssetModeResult()";
        }

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static IEnumerator Routine(string partId, bool withTwin, float timeout)
        {
            var sw = Switcher; var e = Editor; var t = Tool;
            if (s_Twin != null) { t.Remove(s_Twin); s_Twin = null; }
            var target = FindServerPart(partId);
            if (target == null && !string.IsNullOrEmpty(partId))
            {
                bool started = AppCommands.PlacePart(new PlacePartArgs { PartId = partId });
                float until = Time.realtimeSinceStartup + timeout;
                while (started && (target = FindServerPart(partId)) == null && Time.realtimeSinceStartup < until) yield return null;
            }
            Check("assetmode.target", target != null, target != null ? $"{target.Spec.id} src={target.Source} mode={target.Spec.asset?.mode ?? "-"}"
                : "no server part placed: take out the dishwasher (Settings ▸ Take out) and pass its part id, or place one first");
            if (target == null) { s_Summary = $"{s_Pass}/{s_Total} checks (no target)"; yield break; }
            s_Target = target;
            t.Select(target);
            e.TryAnchor(target, out var a0, out var r0);

            // The setting: first the mode the part doesn't show (HF, unless it shows HF), then the other.
            var first = AssetModeSwitcher.ShownMode(target) == AssetMode.Hf ? AssetMode.LlmScad : AssetMode.Hf;
            foreach (var mode in new[] { first, AssetModes.CompareTarget(first) })
            {
                int swaps = sw.Swaps, fails = sw.Failures;
                string before = ModelName(target);
                if (UserPrefs.Assets == mode) sw.RebuildAll(mode);   // already the setting (the part was in Compare)
                else UserPrefs.Assets = mode;   // the chip's path
                yield return Wait(() => sw.Pending == 0 && (sw.Swaps > swaps || sw.Failures > fails), timeout);
                CheckSwap($"assetmode.{UserPrefs.Wire(mode)}", target, mode, a0, r0, before, sw.Swaps > swaps);
            }

            // Compare: the other model on this part while the setting stays, then back.
            var setting = UserPrefs.Assets;
            foreach (var want in new[] { AssetModes.CompareTarget(setting), setting })
            {
                int swaps = sw.Swaps;
                string before = ModelName(target);
                bool started = sw.Compare(target);
                yield return Wait(() => sw.Pending == 0 && sw.Swaps > swaps, timeout);
                CheckSwap($"assetmode.compare.{UserPrefs.Wire(want)}", target, want, a0, r0, before, started && sw.Swaps > swaps);
                Check($"assetmode.compare.{UserPrefs.Wire(want)}.flag", AssetModeSwitcher.Comparing(target) == (want != UserPrefs.Assets),
                    $"comparing={AssetModeSwitcher.Comparing(target)} setting={UserPrefs.Wire(UserPrefs.Assets)}");
            }

            // twin: the same part again, loaded in the other mode (PartLoader.Load with a mode), standing in front of it —
            // one capture shows both generators (in a cavity a neighbour would cut into a twin beside it).
            if (withTwin)
            {
                PartInstance twin = null;
                bool done = false;
                var other = AssetModes.CompareTarget(setting);
                Loader.Load(new PartSummary { id = target.Spec.id, name = target.Spec.name }, other, p => { twin = p; done = true; });
                yield return Wait(() => done, timeout);
                if (twin != null)
                {
                    var frontWorld = -target.WorldMountDirection;
                    var front = t.frame != null ? t.frame.InverseTransformDirection(frontWorld) : frontWorld;
                    float depth = target.Spec.dims_mm.d * 0.001f;
                    t.PlaceAt(twin, target.transform.localPosition + front.normalized * (depth + 0.6f), target.transform.localRotation);
                    s_Twin = twin;
                    t.Select(target);
                }
                Check("assetmode.twin", twin != null && AssetModes.ModeOf(twin.Spec.asset) == other && AssetModes.ModeOf(target.Spec.asset) == setting,
                    twin != null ? $"{target.Spec.id} \"{AssetModes.TierLine(target.Spec.asset)}\" in place · its twin \"{AssetModes.TierLine(twin.Spec.asset)}\" in front → AssetModeLook(), capture_game_view"
                                 : $"twin didn't load: {Loader.LastError}");
            }

            foreach (var line in BackendHarness.TalkStyleChecks()) Check(line.id, line.ok, line.detail);
            s_Summary = $"{s_Pass}/{s_Total} checks · setting {UserPrefs.Label(UserPrefs.Assets)}";
        }

        // --- cad: the LLM+CAD template → Grok's CAD model upgrade ----------------------------------------------------

        /// cad: AssetCadCheck(partId): the placed server part (or `partId` placed into the gap of the part taken out last)
        /// switched to LLM+CAD. If Grok is still writing its CAD model the template shows "Template · Grok is writing the
        /// CAD model… N s" and the watch polls until the CAD model swaps in; either way the part ends on tier "scad" with
        /// the badge "CAD · Grok 4.20 · OpenSCAD" and its anchor unchanged. [AirTools.Check] assetmode.cad.* lines; poll
        /// AssetModeResult(). A part whose CAD model exists already (warm --scad) skips the writing phase.
        public static string CadRun(string partId = null, float timeout = 240f)
        {
            if (!s_Done) return "AssetModeCheck is running: poll AssetModeResult()";
            if (Tool == null || Editor == null || Switcher == null || Loader == null)
                return "missing PartTool / PlacementEditor / AssetModeSwitcher / PartLoader (run AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = "running (cad)";
            s_Done = false;
            if (!DemoRunner.Run(CadRoutine(partId, timeout), ex => { s_Summary = $"error {ex.GetType().Name}: {ex.Message}"; s_Done = true; }, () => s_Done = true))
            {
                s_Done = true;
                return "couldn't start (not playing, or another routine is running)";
            }
            return "started: poll AssetModeResult() (the CAD model can take a minute)";
        }

        static IEnumerator CadRoutine(string partId, float timeout)
        {
            var sw = Switcher; var e = Editor; var t = Tool;
            var target = FindServerPart(partId);
            if (target == null && !string.IsNullOrEmpty(partId))
            {
                bool started = AppCommands.PlacePart(new PlacePartArgs { PartId = partId });
                float until = Time.realtimeSinceStartup + timeout;
                while (started && (target = FindServerPart(partId)) == null && Time.realtimeSinceStartup < until) yield return null;
            }
            Check("assetmode.cad.target", target != null, target != null ? $"{target.Spec.id} mode={target.Spec.asset?.mode ?? "-"} tier={target.Spec.asset?.tier}"
                : "no server part placed: take one out (Settings ▸ Take out) and pass its part id, or place one first");
            if (target == null) { s_Summary = $"{s_Pass}/{s_Total} checks (no target)"; yield break; }
            s_Target = target;
            t.Select(target);
            e.TryAnchor(target, out var a0, out var r0);

            int swaps = sw.Swaps, fails = sw.Failures, upgrades = sw.CadUpgrades;
            string before = ModelName(target);
            if (AssetModeSwitcher.ShownMode(target) != AssetMode.LlmScad)
            {
                if (UserPrefs.Assets == AssetMode.LlmScad) sw.Switch(target, AssetMode.LlmScad, compareOnly: false, force: true);
                else UserPrefs.Assets = AssetMode.LlmScad;
                yield return Wait(() => sw.Pending == 0 && (sw.Swaps > swaps || sw.Failures > fails), timeout);
            }
            var asset = target.Spec.asset;
            bool writing = asset?.cad != null && asset.cad.Writing && asset.tier != "scad";
            string line = AssetModes.TierLine(asset);
            Check("assetmode.cad.first", AssetModes.ModeOf(asset) == AssetMode.LlmScad,
                $"tier={asset?.tier} cad={CadUpgrade.Describe(asset?.cad, Time.realtimeSinceStartup)} badge=\"{line}\" model {before} → {ModelName(target)}");
            if (writing)
            {
                Check("assetmode.cad.writing_badge", line != null && line.StartsWith("Template · Grok is writing the CAD model", System.StringComparison.Ordinal), $"badge=\"{line}\"");
                float t0 = Time.realtimeSinceStartup;
                yield return Wait(() => sw.CadWatching > 0 || sw.CadUpgrades > upgrades, 5f);
                Check("assetmode.cad.watched", sw.CadWatching > 0 || sw.CadUpgrades > upgrades, sw.DescribeCad());
                int swapsNow = sw.Swaps;
                yield return Wait(() => sw.CadUpgrades > upgrades && sw.Pending == 0 && sw.Swaps > swapsNow, timeout);
                Check("assetmode.cad.upgraded", sw.CadUpgrades > upgrades && sw.Swaps > swapsNow,
                    $"after {Time.realtimeSinceStartup - t0:0} s · {sw.DescribeCad()} | {sw.LastSwitch}");
            }
            asset = target.Spec.asset;
            line = AssetModes.TierLine(asset);
            Check("assetmode.cad.model", asset?.tier == "scad" && line != null && line.StartsWith("CAD · Grok", System.StringComparison.Ordinal),
                $"tier={asset?.tier} made_by={asset?.made_by ?? "-"} badge=\"{line}\" model={ModelName(target)} ({(writing ? "upgraded while placed" : "ready at once")})");
            bool placed = e.TryAnchor(target, out var a1, out var r1);
            float moved = placed ? Vector3.Distance(a0, a1) : -1f;
            float turned = placed ? Quaternion.Angle(r0, r1) : -1f;
            Check("assetmode.cad.anchor", placed && moved <= 0.001f && turned <= 0.1f,
                $"anchor moved {moved.ToString("0.0000", C)} m, turned {turned.ToString("0.00", C)}° (≤ 1 mm, ≤ 0.1°)");
            s_Summary = $"{s_Pass}/{s_Total} checks · cad {(writing ? "upgraded" : "ready at once")}";
        }

        static IEnumerator Wait(System.Func<bool> done, float timeout)
        {
            float until = Time.realtimeSinceStartup + timeout;
            yield return null;   // let the request go out
            while (!done() && Time.realtimeSinceStartup < until) yield return null;
        }

        static void CheckSwap(string id, PartInstance p, AssetMode want, Vector3 a0, Quaternion r0, string modelBefore, bool swapped)
        {
            var asset = p.Spec.asset;
            var e = Editor;
            bool placed = e.TryAnchor(p, out var a1, out var r1);
            float moved = placed ? Vector3.Distance(a0, a1) : -1f;
            float turned = placed ? Quaternion.Angle(r0, r1) : -1f;
            Check(id, swapped && AssetModes.ModeOf(asset) == want,
                $"mode={asset?.mode ?? "-"} tier={asset?.tier} made_by={asset?.made_by ?? "-"} badge=\"{AssetModes.TierLine(asset)}\" note=\"{asset?.note}\" model {modelBefore} → {ModelName(p)} | {Switcher?.LastSwitch}");
            Check(id + ".anchor", placed && moved <= 0.001f && turned <= 0.1f,
                $"anchor moved {moved.ToString("0.0000", C)} m, turned {turned.ToString("0.00", C)}° (≤ 1 mm, ≤ 0.1°)");
        }

        /// A capture pose: the rig moved so the eye is in front of the part and its HF twin, looking at the pair.
        /// AssetModeLookReset() puts it back.
        public static string Look(float distance = 0f)
        {
            var cam = Camera.main;
            var rig = Object.FindFirstObjectByType<OVRCameraRig>()?.transform;
            if (s_Target == null || cam == null || rig == null) return "no target / camera / rig: AssetModeCheck() first";
            s_LookFrom ??= new Pose(rig.position, rig.rotation);
            var centre = s_Target.WorldBoxCentre;
            float span = Mathf.Max(s_Target.Spec.dims_mm.w, s_Target.Spec.dims_mm.h) * 0.001f;
            if (s_Twin != null) { centre = (centre + s_Twin.WorldBoxCentre) * 0.5f; span *= 1.8f; }
            var front = s_Target.WorldMountDirection * -1f;   // the mount face is the back: the front looks the other way
            if (front.sqrMagnitude < 1e-6f) front = s_Target.transform.forward;
            float d = distance > 0f ? distance : Mathf.Max(1.0f, span * 1.3f);
            // Twin: from the side at 45° (the twin stands in front); alone: straight on, a little above.
            var side = s_Twin != null ? Vector3.Cross(Vector3.up, front).normalized : Vector3.zero;
            var eyeWant = centre + (front.normalized + side).normalized * d + Vector3.up * 0.3f;
            rig.position += eyeWant - cam.transform.position;
            var dir = centre - cam.transform.position;
            var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
            var turn = want * Quaternion.Inverse(cam.transform.rotation);
            var eye = cam.transform.position;
            rig.SetPositionAndRotation(eye + turn * (rig.position - eye), turn * rig.rotation);
            return $"looking at {s_Target.Spec.id} ({AssetModes.TierLine(s_Target.Spec.asset)})" +
                   (s_Twin != null ? $" and its twin in front ({AssetModes.TierLine(s_Twin.Spec.asset)})" : "") + $" from {d.ToString("0.0", C)} m";
        }

        public static string LookReset()
        {
            var rig = Object.FindFirstObjectByType<OVRCameraRig>()?.transform;
            if (rig == null || !s_LookFrom.HasValue) return "nothing to reset";
            rig.SetPositionAndRotation(s_LookFrom.Value.position, s_LookFrom.Value.rotation);
            s_LookFrom = null;
            return "rig back";
        }
    }
}
#endif
