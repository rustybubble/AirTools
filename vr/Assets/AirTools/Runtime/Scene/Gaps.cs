using System;
using System.Collections.Generic;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// e2e: a removed component's gap — what "measure the gap", "find one that fits", "put it in there" and "next one"
    /// work on. The cavity box is in package space; SizeM is its real size (the file's size_m × the scene calibration,
    /// the numbers the cavity label shows).
    public struct Gap
    {
        public ScenePartComponent Component;
        public CavityBox Box;
        public float Calibration;

        public string Id => Component?.id;
        /// "dishwasher", "sink cabinet": what a search or a sentence calls it.
        public string Noun => (Component?.DisplayName ?? "part").ToLowerInvariant();
        /// Real metres: x = width, y = height, z = depth.
        public Vector3 SizeM => Box.Size * Calibration;
        public Vector3 SizeMm => SizeM * 1000f;
    }

    /// The gaps of the loaded scene: the component taken out most recently (still out) is "the gap"; the width, depth and
    /// height the tape measured in it (CavityTapes) are kept per component until it goes back. The context and the parts
    /// search carry them (`cavity`, `removed`).
    public static class Gaps
    {
        /// Per component: the tape readings in package units (÷ the calibration when taped, so a later "set scale"
        /// carries them along) and the tapes' notebook entries by axis (w, h, d; "set scale from the gap" uses them).
        static readonly Dictionary<(string site, string id), Vector3> s_Measured = new Dictionary<(string, string), Vector3>();   // sitescope: keyed by site too
        static readonly Dictionary<(string site, string id), AirTools.Notes.NotebookEntry[]> s_Tapes = new Dictionary<(string, string), AirTools.Notes.NotebookEntry[]>();

        static float Calibration => Services.TryGet<SceneRoot>(out var r) ? Mathf.Max(r.Calibration, 1e-6f) : 1f;

        /// The gap of `idOrLabel` (it must be out), else of the part taken out most recently. False: nothing is out, or
        /// its cavity has no usable box.
        public static bool TryGet(out Gap gap, string idOrLabel = null)
        {
            gap = default;
            if (!Services.TryGet<SceneParts>(out var parts) || !parts.HasParts) return false;
            var c = !string.IsNullOrWhiteSpace(idOrLabel) ? parts.Find(idOrLabel) : parts.LastRemoved;
            if (c == null || !parts.IsRemoved(c.id)) return false;
            if (!CavityBox.TryFrom(c, out var box, out _)) return false;
            var root = Services.Get<SceneRoot>();
            gap = new Gap { Component = c, Box = box, Calibration = root != null ? root.Calibration : 1f };
            return true;
        }

        /// Any gap open right now.
        public static bool Open => TryGet(out _);

        /// The ids that are out, oldest first (context.removed: [] when none; null when the scene has no parts, so the
        /// server keeps its own memory for an app that never sends it).
        public static List<string> RemovedIds()
        {
            if (!Services.TryGet<SceneParts>(out var parts) || !parts.HasParts) return null;
            return new List<string>(parts.RemovedIds);
        }

        // ---------------- the model standing in a gap ----------------

        /// A placed part stands in the gap when its front-bottom-centre is this close to the cavity insert (scene-root
        /// metres; a nudge in the placement editor stays "in the gap").
        public const float InGapRadius = 0.12f;

        /// The placed part standing in `gap` (the nearest to its insert within InGapRadius), `except` left out; null when
        /// the gap is empty. Whoever put it there (ModelCycler, place_part, the placement editor's swap, a hand).
        public static AirTools.Parts.PartInstance ModelIn(Gap gap, AirTools.Parts.PartInstance except = null)
        {
            if (gap.Component == null || !Services.TryGet<AirTools.Parts.PartTool>(out var tool) || !Services.TryGet<SceneRoot>(out var root)) return null;
            if (!AppCommands.TryPlaceTarget(new AirTools.Parts.PlacePartArgs { ComponentId = gap.Id }, root, out var target, out _)) return null;
            var frame = tool.frame != null ? tool.frame : root.transform;
            AirTools.Parts.PartInstance best = null;
            float bestD = InGapRadius;
            foreach (var p in tool.PlacedParts)
            {
                if (p == null || p == except || !p.Placed || !p.gameObject.activeInHierarchy || p.Spec == null) continue;
                var fbc = frame.InverseTransformPoint(p.transform.TransformPoint(AirTools.Parts.PlacePartMath.FrontBottomCentre(p.LocalBox)));
                float d = Vector3.Distance(fbc, target.Anchor);
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /// The model in the open gap (the part taken out last), or null.
        public static AirTools.Parts.PartInstance ModelInOpenGap() => TryGet(out var g) ? ModelIn(g) : null;

        // ---------------- the tapes measured in a gap ----------------

        /// CavityTapes: the real width / height / depth the tape read (metres at the current calibration; NaN = not taped,
        /// e.g. an open top), and the tapes (w, h, d; null where one didn't take).
        public static void RecordMeasured(string id, Vector3 metres, AirTools.Notes.NotebookEntry[] tapes = null)
        {
            if (string.IsNullOrEmpty(id)) return;
            s_Measured[Key(id)] = metres / Calibration;
            if (tapes != null) s_Tapes[Key(id)] = tapes;
        }

        /// The tape readings in real metres at the current calibration.
        public static bool TryMeasured(string id, out Vector3 metres)
        {
            metres = default;
            if (string.IsNullOrEmpty(id) || !s_Measured.TryGetValue(Key(id), out var pkg)) return false;
            metres = pkg * Calibration;
            return true;
        }

        /// The gap's tape on `axis` ("w" / "h" / "d"), if it is still in the notebook.
        public static AirTools.Notes.NotebookEntry TapeOf(string id, string axis)
        {
            if (string.IsNullOrEmpty(id) || !s_Tapes.TryGetValue(Key(id), out var t) || t == null) return null;
            var e = axis == "w" ? t[0] : axis == "h" ? t[1] : axis == "d" ? t[2] : null;
            if (e == null) return null;
            foreach (var x in AirTools.Notes.Notebook.Entries) if (ReferenceEquals(x, e)) return e;
            return null;
        }

        /// assetgen: one of a gap's own tapes (CavityTapes): a taped opening (Parts.Openings) never pairs it — the gap is
        /// its own insert target.
        public static bool IsGapTape(AirTools.Notes.NotebookEntry e)
        {
            if (e == null) return false;
            foreach (var kv in s_Tapes)
                if (kv.Value != null)
                    foreach (var t in kv.Value) if (ReferenceEquals(t, e)) return true;
            return false;
        }

        /// Forget the tapes (the part went back, a demo reset, a new scene).
        public static void Forget(string id = null)
        {
            if (id == null) { s_Measured.Clear(); s_Tapes.Clear(); }
            else { s_Measured.Remove(Key(id)); s_Tapes.Remove(Key(id)); }
        }

        /// sitescope: a gap's tapes belong to the site they were taken on (component ids repeat across scans: another
        /// site's dw1 never reads the kitchen's tapes, and the kitchen's are there again when it loads again).
        static (string, string) Key(string id) => (SiteScope.Current, id);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Measured.Clear(); s_Tapes.Clear(); }

        // ---------------- context and search ----------------

        /// context.cavity (docs/demo-prompts.md; the e2e backend patch reads it): the gap the next "find one that fits" /
        /// "put it in there" means — {component_id, label, w_m, h_m, d_m, estimated: true, measured?: {w_m, h_m, d_m}}
        /// in real metres, to the millimetre. Pure.
        public static Dictionary<string, object> Context(string id, string label, Vector3 sizeM, Vector3? measuredM)
        {
            var d = new Dictionary<string, object>
            {
                ["component_id"] = id,
                ["label"] = label,
                ["w_m"] = R3(sizeM.x), ["h_m"] = R3(sizeM.y), ["d_m"] = R3(sizeM.z),
                ["estimated"] = true,
            };
            if (measuredM.HasValue)
            {
                var m = new Dictionary<string, object>();
                if (Finite(measuredM.Value.x)) m["w_m"] = R3(measuredM.Value.x);
                if (Finite(measuredM.Value.y)) m["h_m"] = R3(measuredM.Value.y);
                if (Finite(measuredM.Value.z)) m["d_m"] = R3(measuredM.Value.z);
                if (m.Count > 0) d["measured"] = m;
            }
            return d;
        }

        /// The live context.cavity, or null when no gap is open.
        public static Dictionary<string, object> CurrentContext()
        {
            if (!TryGet(out var g)) return null;
            return Context(g.Id, g.Noun, g.SizeM, TryMeasured(g.Id, out var m) ? m : (Vector3?)null);
        }

        /// The size a part must fit: the tape's reading where it measured one, else the file's (real metres).
        public static Vector3 FitSizeM(Gap g)
        {
            var s = g.SizeM;
            if (TryMeasured(g.Id, out var m))
            {
                if (Finite(m.x) && m.x > 0f) s.x = m.x;
                if (Finite(m.y) && m.y > 0f) s.y = m.y;
                if (Finite(m.z) && m.z > 0f) s.z = m.z;
            }
            return s;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static double R3(float v) => Math.Round(v, 3);
    }
}
