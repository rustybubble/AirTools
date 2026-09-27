#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AirTools.Dev
{
    /// Glass lane, in the running app (through AgentHarness):
    /// - **State()**: the glass census of the live scene (GlassCensus: roles, liquid / marks, strays, wrong materials) and
    ///   whether the Lite switch or high contrast is on.
    /// - **Lite(bool)**: the AIRTOOLS_GLASS_LITE A/B for the headset's frame time (logs "Glass: lite" / "Glass: liquid").
    /// - **Look(shot)** / **LookReset()**: capture setups — opens a surface and turns the rig about the eye so it's centred
    ///   ("settings", "parts", "notebook", "wrist", "ring", "model", "toast"). The Adjust panel's shot is AssetLook("panel").
    /// - **ToastDockCheck()**: the gate's bug (a toast drew over the Adjust panel): with the rail off, a toast while a
    ///   main-slot window is open sits wholly above its top rim. [AirTools.Check] glass.*; poll Result().
    public static class GlassCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;
        static int s_Pass, s_Total;
        static Pose? s_RigFrom;
        static Transform s_FakeWrist;
        static Transform s_WristAnchorWas;
        static bool s_WristSaved, s_PalmSaved;
        static Transform s_PalmAnchorWas;

        static Transform Rig => Object.FindFirstObjectByType<OVRCameraRig>()?.transform;

        public static string State()
        {
            var roots = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) roots.AddRange(s.GetRootGameObjects());
            }
            var r = GlassCensus.Take(roots, UiTheme.Current);
            var sb = new System.Text.StringBuilder();
            sb.Append($"{(GlassQuality.Lite ? "lite" : "liquid")} · high contrast {(UiSettings.HighContrast ? "on" : "off")} | {r.Line()}");
            for (int i = 0; i < r.Stray.Count && i < 8; i++) sb.Append($"\n  stray {r.Stray[i]}");
            for (int i = 0; i < r.WrongMaterial.Count && i < 8; i++) sb.Append($"\n  material {r.WrongMaterial[i]}");
            return sb.ToString();
        }

        public static string Lite(bool on)
        {
            GlassQuality.SetLite(on);
            return State();
        }

        // ---------------- capture setups ----------------

        public static string Look(string shot)
        {
            var rig = Rig;
            var cam = Camera.main;
            if (rig == null || cam == null) return "no rig / camera";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            Transform target = null;
            float lookDown = 0f;
            switch ((shot ?? "").Trim().ToLowerInvariant())
            {
                case "settings":
                    AppCommands.ShowScenePanel(true);
                    target = Services.Get<AirTools.Structure.ScenePanel>()?.window?.transform;
                    break;
                case "parts":
                    AppCommands.ShowFindParts();
                    target = Services.Get<PartsBrowser>()?.content?.transform;
                    break;
                case "notebook":
                    AppCommands.ShowNotebook(true);
                    target = Services.Get<AirTools.Notes.NotebookPanel>()?.content?.transform;
                    break;
                case "toast":
                    AppCommands.ShowNotebook(true);
                    AirTools.UI.GuideRail.Enabled = false;
                    UiToast.Show("Glass check · a toast while a window is open", ColorRole.Info);
                    target = Services.Get<AirTools.Notes.NotebookPanel>()?.content?.transform;
                    lookDown = -6f;   // a little higher: the rim and the pill above it
                    break;
                case "wrist":
                {
                    var chip = Services.Get<LimitsChip>();
                    if (chip == null) return "no LimitsChip (AirTools ▸ Wire Main Scene)";
                    if (LimitsChip.Current == null || !LimitsChip.Current.Any)
                        AppCommands.ShowLimits(new MandateIntent { max_total_usd = 40f, deliver_by = "Fri", seller_policy = "fastest", source = "glass" }, false);
                    // A stand-in wrist 38 cm ahead and 18 cm below the eye (no hand or controller needed in the Editor).
                    if (s_FakeWrist == null) s_FakeWrist = new GameObject("GlassCheckWrist") { hideFlags = HideFlags.DontSave }.transform;
                    s_FakeWrist.SetParent(rig, true);
                    s_FakeWrist.position = cam.transform.position + cam.transform.forward * 0.38f + Vector3.down * 0.18f;
                    if (!s_WristSaved) { s_WristAnchorWas = chip.controllerAnchor; s_WristSaved = true; }
                    chip.controllerAnchor = s_FakeWrist;
                    target = s_FakeWrist;
                    break;
                }
                case "ring":
                {
                    var palm = Services.Get<AirTools.Input.PalmMenu>();
                    if (palm == null) return "no PalmMenu";
                    if (!s_PalmSaved) { s_PalmAnchorWas = palm.controllerAnchor; s_PalmSaved = true; }
                    palm.controllerAnchor = null;
                    palm.Force(true);
                    target = palm.ring != null ? palm.ring.transform : palm.transform;
                    break;
                }
                case "model":
                    AppCommands.SetTabletop(true);
                    target = Services.Get<AirTools.Scene.ModelSwitcher>()?.content?.transform;
                    break;
                default:
                    return "shots: settings · parts · notebook · toast · wrist · ring · model (Adjust: AssetLook(\"panel\"))";
            }
            if (target == null) return $"{shot}: nothing to look at (Wire Main Scene?)";
            s_RigFrom ??= new Pose(rig.position, rig.rotation);
            // Turn about the eye so the view centres on it (a window re-centres only past 40°, so it stays put).
            var eye = cam.transform.position;
            var dir = target.position - eye;
            if (dir.sqrMagnitude < 1e-6f) return $"{shot}: the target is at the eye";
            var want = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(lookDown, 0f, 0f);
            var turn = want * Quaternion.Inverse(cam.transform.rotation);
            rig.SetPositionAndRotation(eye + turn * (rig.position - eye), turn * rig.rotation);
            return $"looking at {shot} ({target.name}, {dir.magnitude.ToString("0.00", C)} m) | {State()}";
        }

        public static string LookReset()
        {
            var palm = Services.Get<AirTools.Input.PalmMenu>();
            if (palm != null && s_PalmSaved) { palm.Force(null); palm.controllerAnchor = s_PalmAnchorWas; }
            s_PalmSaved = false;
            var chip = Services.Get<LimitsChip>();
            if (chip != null && s_WristSaved) chip.controllerAnchor = s_WristAnchorWas;
            s_WristSaved = false;
            if (s_FakeWrist != null) { Object.Destroy(s_FakeWrist.gameObject); s_FakeWrist = null; }
            AppCommands.CloseWindows();
            var rig = Rig;
            if (rig != null && s_RigFrom.HasValue) rig.SetPositionAndRotation(s_RigFrom.Value.position, s_RigFrom.Value.rotation);
            s_RigFrom = null;
            return "glass look reset";
        }

        // ---------------- the toast dock (the gate's bug) ----------------

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        public static string ToastDockCheck()
        {
            if (!s_Done) return "a glass check is already running: poll GlassDockResult()";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = "glass.dock running";
            s_Done = false;
            bool railWas = AirTools.UI.GuideRail.Enabled;
            if (!DemoRunner.Run(DockRoutine(), ex => { AirTools.UI.GuideRail.Enabled = railWas; s_Summary = $"glass.dock error {ex.GetType().Name}: {ex.Message}"; s_Done = true; },
                    () => { AirTools.UI.GuideRail.Enabled = railWas; s_Summary = $"glass.dock {s_Pass}/{s_Total}"; s_Done = true; }))
            {
                s_Done = true;
                return "another routine is running (DemoRunner busy)";
            }
            return "glass.dock started: poll GlassDockResult()";
        }

        static float Pitch(Vector3 eye, Vector3 p) =>
            Mathf.Atan2(p.y - eye.y, new Vector2(p.x - eye.x, p.z - eye.z).magnitude) * Mathf.Rad2Deg;

        static IEnumerator DockRoutine()
        {
            if (AppState.Mode == AppMode.Passthrough) { AppCommands.OpenChest(); yield return new WaitForSecondsRealtime(1.5f); }
            AirTools.UI.GuideRail.Enabled = false;   // the rail off (DemoMode off): the fallback toast pill
            AppCommands.ShowNotebook(true);
            yield return new WaitForSecondsRealtime(0.6f);
            UiToast.Show("Glass check · a toast while a window is open", ColorRole.Info);
            yield return new WaitForSecondsRealtime(0.5f);
            var toast = UiToast.Toast;
            var cam = Camera.main;
            var window = WindowSlot.Current as IDockTarget;
            Check("glass.dock.window", window != null, $"main slot {(WindowSlot.Current as Component)?.name ?? "empty"}");
            Check("glass.dock.toast", toast != null && toast.Showing, toast != null ? $"\"{toast.Message}\"" : "no toast");
            if (window != null && toast != null && cam != null && window.TryPanelTop(out var top, out _))
            {
                var eye = cam.transform.position;
                var s = toast.surface;
                var bottom = s.transform.TransformPoint(new Vector3(0f, -s.size.y * 0.5f, 0f));
                float rim = Pitch(eye, top), low = Pitch(eye, bottom);
                Check("glass.dock.above", toast.Docked && low >= rim - 0.05f,
                    $"docked={toast.Docked} toast bottom {low.ToString("0.00", C)}° vs the window's top rim {rim.ToString("0.00", C)}°");
                float em = UiText.EmDmmAt(toast.text, (toast.text.transform.position - eye).magnitude);
                Check("glass.dock.size", em >= UiTheme.EmFloorDmm - 0.05f, $"toast text {em.ToString("0.0", C)} dmm (floor {UiTheme.EmFloorDmm})");
            }
            AppCommands.ShowNotebook(false);
        }
    }
}
#endif
