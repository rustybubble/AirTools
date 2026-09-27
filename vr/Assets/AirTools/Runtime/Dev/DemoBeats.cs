using System;
using System.Collections;
using System.Linq;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// D7 (UX W1.8): the presenter's "Skip to beat" — the state a beat of the judge's three minutes (docs/ux/README.md
    /// §2.7) leaves behind, reached through the same paths the judge uses (ToolInputHub presses for tapes and placing,
    /// AppCommands for the rest), so the guide rail picks up from there. PresenterCommands.Beats lists them.
    /// None of them pays: "sellers" opens the seller list; Pay with Visa and the 1 s hold stay the judge's.
    /// Kitchen: the door is structure object o5 (or the first cabinet_door); the built-in facade: the gutter run.
    public static class DemoBeats
    {
        /// Seconds to wait for the world to open (the transition) and for a scene still loading.
        public const float WorldTimeout = 6f, LoadTimeout = 30f, SearchTimeout = 50f, PartLoadTimeout = 20f;
        public const string KitchenDoor = "o5";

        static float Now => Time.realtimeSinceStartup;

        /// Run one beat; `done(ok, detail)` is called exactly once (possibly frames later).
        public static IEnumerator Run(string beat, Action<bool, string> done)
        {
            switch (beat)
            {
                case "measure": return Measure(done);
                case "find": return Once(() => Find(out var d) ? (true, d) : (false, d), done);
                case "take": return Take(done);
                case "place": return Place(done);
                case "sellers": return Once(() => AppCommands.ShowSellers("price") ? (true, "seller list open · the judge taps Pay with Visa") : (false, "no placed part to price: skip to 'place' first"), done);
                case "home": return Once(Home, done);
                case "report": return Once(() => AppCommands.ExportNotebook() is string dir ? (true, $"notebook exported ({Notebook.Entries.Count} readings)") : (false, "export failed"), done);
                // e2e: take the dishwasher (or the first removable part) out, measure the gap, find ones that fit, put the
                // best in, switch through three, undo, put the original back — every step a phrase through the command path.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                case "replace": return E2EHarness.Beat(done);
#else
                case "replace": return Once(() => (false, "the replace beat needs a Development build"), done);
#endif
                default: return Once(() => (false, $"unknown beat '{beat}'"), done);
            }
        }

        static IEnumerator Once(Func<(bool, string)> f, Action<bool, string> done)
        {
            var (ok, detail) = f();
            done(ok, detail);
            yield break;
        }

        /// Into the world (from the room or the table), past the transition and any scene download.
        static IEnumerator EnsureWorld()
        {
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            else if (AppState.Mode == AppMode.Tabletop) AppCommands.SetTabletop(false);
            var mode = Services.Get<ModeController>();
            float t0 = Now;
            while (Now - t0 < WorldTimeout && (AppState.Mode != AppMode.World || (mode != null && (mode.IsTransitioning || mode.VisualMode != AppMode.World))))
                yield return null;
            var streamer = Services.Get<SceneStreamer>();
            while (streamer != null && streamer.Loading && Now - t0 < LoadTimeout) yield return null;
            Physics.SyncTransforms();
        }

        // ---------------- 0:20 measure ----------------

        static IEnumerator Measure(Action<bool, string> done)
        {
            yield return EnsureWorld();
            if (AppState.Mode != AppMode.World) { done(false, "the world didn't open"); yield break; }
            AppCommands.EquipTool("measure");
            var root = Services.Get<SceneRoot>(); var hub = Services.Get<ToolInputHub>(); var measure = Services.Get<MeasureTool>();
            if (root == null || hub == null || measure == null) { done(false, "no scene, input hub or measure tool"); yield break; }
            measure.CancelSession();   // a half-made tape would join this one
            int shapes0 = measure.Shapes.Count;
            string what;
            if (TryDoor(root, out var top, out var centre, out var normal, out var id))
            {
                // Both top corners, 1 cm in (the snap pulls each onto its corner), from 0.6 m in front: two pinches.
                foreach (var corner in top) Press(hub, root, corner + (centre - corner).normalized * 0.01f + normal * 0.6f, corner + (centre - corner).normalized * 0.01f);
                what = $"door {id}";
            }
            else if (!root.IsRuntimePackage)
            {
                var s = MeasureScenarios.M2().First(x => x.Id == "M2.measure.gutter.run");
                MeasureScenarios.Run(s, measure, hub, root.transform, 1);
                what = "the gutter run";
            }
            else { done(false, "no door in this scene's structure layer"); yield break; }
            yield return null;
            if (measure.Shapes.Count == shapes0 && measure.Session.Count >= 2) AppCommands.FinishShape();   // D4 off: finish by hand
            bool ok = measure.Shapes.Count > shapes0;
            done(ok, ok ? $"taped {what}: {Notebook.Last?.Label}" : $"the tape didn't save ({measure.LastAction})");
        }

        /// The kitchen door's two top corners (scene-root space), its centre and the normal toward the camera.
        static bool TryDoor(SceneRoot root, out Vector3[] top, out Vector3 centre, out Vector3 normal, out string id)
        {
            top = null; centre = normal = Vector3.zero; id = null;
            var layer = root.Structure;
            if (layer == null || layer.Objects == null) return false;
            int pick = -1;
            for (int i = 0; i < layer.Objects.Length; i++)
            {
                var o = layer.Objects[i];
                if (o.corners == null || o.corners.Length < 3) continue;
                if (o.id == KitchenDoor) { pick = i; break; }
                if (pick < 0 && o.label == "cabinet_door") pick = i;
            }
            if (pick < 0) return false;
            var obj = layer.Objects[pick];
            id = obj.id;
            var corners = obj.corners.Select(p => root.PackageToRoot(p)).ToArray();
            foreach (var p in corners) centre += p;
            centre /= corners.Length;
            normal = Vector3.Cross(corners[1] - corners[0], corners[corners.Length - 1] - corners[0]).normalized;
            var cam = Camera.main;
            if (cam != null && Vector3.Dot(normal, root.transform.InverseTransformPoint(cam.transform.position) - centre) < 0f) normal = -normal;
            top = corners.OrderByDescending(p => p.y).Take(2).OrderBy(p => p.x).ToArray();
            return true;
        }

        /// One pinch along a ray (scene-root space) through the tool input hub, the path a hand pinch takes.
        static void Press(ToolInputHub hub, SceneRoot root, Vector3 originRoot, Vector3 targetRoot)
        {
            var pose = ToolInputHub.RayPose(root.transform.TransformPoint(originRoot), root.transform.TransformPoint(targetRoot));
            hub.SetPointerOverride(ToolHand.Right, pose);
            hub.RaisePressStart(ToolHand.Right, pose);
            hub.RaisePressEnd(ToolHand.Right, pose);
            hub.SetPointerOverride(ToolHand.Right, null);
        }

        // ---------------- 0:40 find ----------------

        /// The search the rail offers for the latest tape ("Find hinges for this door" → "cabinet hinge"), else the scene's.
        static bool Find(out string detail)
        {
            var root = Services.Get<SceneRoot>();
            string query = null;
            var tape = AirTools.Structure.ScaleCalibration.LatestTape();
            if (tape != null && root != null) query = NextStepCopy.QueryFor(SurfaceNames.ClassifyTape(tape.Points[0], tape.Points[1], root));
            query ??= root != null && root.IsRuntimePackage ? "cabinet hinge" : "gutter hanger";
            bool ok = AppCommands.FindPart(query);
            detail = ok ? $"searching \"{query}\"{(tape != null ? $" for the {tape.Label}" : "")}" : "no parts browser in the scene";
            return ok;
        }

        // ---------------- 0:55 take ----------------

        static IEnumerator Take(Action<bool, string> done)
        {
            var browser = Services.Get<PartsBrowser>(); var parts = Services.Get<PartTool>();
            if (browser == null || parts == null) { done(false, "no parts browser / part tool"); yield break; }
            if (parts.Held != null) { done(true, $"already holding {parts.Held.Spec.name}"); yield break; }
            float t0 = Now;
            while (browser.Searching && Now - t0 < SearchTimeout) yield return null;
            if (browser.Candidates.Count == 0) { done(false, browser.LastQuery == null ? "no search yet: skip to 'find' first" : $"no candidates for \"{browser.LastQuery}\""); yield break; }
            if (AppState.Mode != AppMode.World) yield return EnsureWorld();
            if (!AppCommands.SelectCandidate(0)) { done(false, "couldn't take the top pick (a load is running?)"); yield break; }
            t0 = Now;
            while (browser.Loading && Now - t0 < PartLoadTimeout) yield return null;
            bool ok = parts.Held != null;
            done(ok, ok ? $"holding {parts.Held.Spec.name}" : browser.Status);
        }

        // ---------------- 1:05 place ----------------

        static IEnumerator Place(Action<bool, string> done)
        {
            var parts = Services.Get<PartTool>();
            if (parts == null || parts.Held == null) { done(false, "nothing in hand: skip to 'take' first"); yield break; }
            yield return EnsureWorld();
            var root = Services.Get<SceneRoot>(); var hub = Services.Get<ToolInputHub>();
            if (root == null || hub == null) { done(false, "no scene / input hub"); yield break; }
            AppCommands.EquipTool("part");
            yield return null;
            int placed0 = parts.PlacedParts.Count;
            var held = parts.Held;
            if (held == null) { done(false, "the part was put away"); yield break; }
            if (TryDoor(root, out _, out var centre, out var normal, out var id))
                Press(hub, root, centre + normal * 0.6f, centre);
            else if (!root.IsRuntimePackage)
                Press(hub, root, new Vector3(0.5f, 7.0f, 1.5f), new Vector3(0.37f, 6.15f, S.FasciaProud));   // a hanger onto the fascia, from a raised eye
            else { done(false, "no door in this scene's structure layer"); yield break; }
            yield return null;
            bool ok = parts.PlacedParts.Count > placed0 && parts.Held == null;
            done(ok, ok ? $"placed {held.Spec.name}: {held.Fit?.Status.ToString() ?? "no fit"}" : parts.LastAction);
        }

        // ---------------- 2:10 home ----------------

        static (bool, string) Home()
        {
            if (AppState.Mode == AppMode.Passthrough) return (false, "already in the room");
            var home = Services.Get<TakeItHome>();
            int bought = home != null ? home.Purchases.Count : 0;
            AppCommands.ToggleChest();
            return (true, bought > 0 ? $"leaving the world: {bought} bought part(s) go on the table" : "leaving the world (nothing bought)");
        }
    }
}
