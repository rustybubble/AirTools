using System.Collections.Generic;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Parts
{
    /// assetgen: the openings taped in this session — pairs of a width and a height tape (or rectangle shapes) in the
    /// notebook that frame the same hole (OpeningMath.FromTapes / FromQuad), newest first. A gap's own tapes (a removed
    /// part's cavity, Gaps) don't count: that gap already is the insert target. Each opening's plane is refined against
    /// the scan: the tapes land on the jambs, sill and head (in the reveal), so a few rays just outside the opening find
    /// the wall's face, and a part snapped in sits flush with it.
    ///
    /// Recomputed only when the notebook or the scene's calibration changes: placement asks every frame (the held
    /// part's snap) without allocating. Points are SceneRoot space, like the notebook's.
    public static class Openings
    {
        static readonly List<TapedOpening> s_All = new List<TapedOpening>();
        static bool s_Hooked, s_Dirty = true;
        static float s_Calibration = -1f;
        static SceneRoot s_Root;

        /// Bumped whenever the list is rebuilt.
        public static int Version { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_All.Clear(); s_Hooked = false; s_Dirty = true; s_Calibration = -1f; s_Root = null; Version = 0; s_SiteEntries.Clear(); s_Site = null; }

        static void Hook()
        {
            if (s_Hooked) return;
            s_Hooked = true;
            Notebook.Added += OnEntry;
            Notebook.Removed += OnEntry;
            Notebook.Updated += OnEntry;
        }

        /// sitescope: openings are the loaded site's tapes (another scan's pair never makes an opening here); a site
        /// change rebuilds them.
        static readonly List<NotebookEntry> s_SiteEntries = new List<NotebookEntry>();
        static string s_Site;

        static void OnEntry(NotebookEntry e) { if (e != null && e.Tool == "measure") s_Dirty = true; }

        /// Mark stale (tests; a scene load).
        public static void Invalidate() => s_Dirty = true;

        /// Every opening, newest first.
        public static IReadOnlyList<TapedOpening> All { get { Refresh(); return s_All; } }

        static void Refresh()
        {
            Hook();
            var root = Services.TryGet<SceneRoot>(out var r) ? r : null;
            float cal = root != null ? root.Calibration : 1f;
            if (!s_Dirty && root == s_Root && Mathf.Abs(cal - s_Calibration) < 1e-6f && s_Site == SiteScope.Current) return;   // sitescope: && site
            s_Dirty = false;
            s_Site = SiteScope.Current;   // sitescope
            s_Root = root;
            s_Calibration = cal;
            s_All.Clear();
            foreach (var o in Pairs(Notebook.OfSite(null, s_SiteEntries), Viewer(root)))   // sitescope: was Notebook.Entries
            {
                var x = o;
                if (root != null) RefinePlane(ref x, root);
                s_All.Add(x);
            }
            Version++;
        }

        /// The openings in `entries`, newest first (pure): tapes paired newest first, each tape used once; then rectangle
        /// shapes. Refresh then refines each one's plane against the scan.
        public static List<TapedOpening> Pairs(IReadOnlyList<NotebookEntry> entries, Vector3 viewer)
        {
            var list = new List<TapedOpening>();
            var used = new HashSet<NotebookEntry>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var a = entries[i];
                if (!IsTape(a) || used.Contains(a)) continue;
                for (int j = i - 1; j >= 0; j--)
                {
                    var b = entries[j];
                    if (!IsTape(b) || used.Contains(b)) continue;
                    if (!Pair(a, b, viewer, out var o)) continue;
                    used.Add(a); used.Add(b);
                    list.Add(o);
                    break;
                }
            }
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e == null || e.Tool != "measure" || e.Points == null || e.Points.Length != 4 || Gaps.IsGapTape(e)) continue;
                if (!OpeningMath.FromQuad(e.Points, viewer, out var o)) continue;
                o.Id = $"shape#{e.Id}";
                o.EntryA = e.Id;
                list.Add(o);
            }
            return list;
        }

        static bool IsTape(NotebookEntry e) => e != null && e.Tool == "measure" && e.Points != null && e.Points.Length == 2 && !Gaps.IsGapTape(e);

        /// Two tapes, a width and a height (either order), as one opening.
        static bool Pair(NotebookEntry a, NotebookEntry b, Vector3 viewer, out TapedOpening o)
        {
            o = default;
            NotebookEntry w, h;
            if (OpeningMath.IsHorizontal(a.Points[0], a.Points[1], Vector3.up) && OpeningMath.IsVertical(b.Points[0], b.Points[1], Vector3.up)) { w = a; h = b; }
            else if (OpeningMath.IsHorizontal(b.Points[0], b.Points[1], Vector3.up) && OpeningMath.IsVertical(a.Points[0], a.Points[1], Vector3.up)) { w = b; h = a; }
            else return false;
            if (!OpeningMath.FromTapes(w.Points[0], w.Points[1], h.Points[0], h.Points[1], viewer, out o)) return false;
            o.Id = $"tape#{w.Id}+#{h.Id}";
            o.EntryA = w.Id;
            o.EntryB = h.Id;
            return true;
        }

        static Vector3 Viewer(SceneRoot root)
        {
            var cam = Camera.main;
            if (cam == null) return SyntheticFacadeSpec.SpawnPosition + Vector3.up * 1.6f;
            return root != null ? root.transform.InverseTransformPoint(cam.transform.position) : cam.transform.position;
        }

        /// The wall's face: rays at the wall around the opening (10 cm outside the tapes' extent, left and right at three
        /// heights, above at three places), cast from both sides of the tapes' plane against the scan. Each side's hits
        /// become a plane from their normals and positions (OpeningMath.FitFace: no dependence on the tapes' own tilt);
        /// ChooseFace takes the face the tapes were taken at (the nearer, not in front of them); OnFace puts the opening on
        /// it — Out from the wall's normal (so a camera on the far side, or tape ends snapped at different depths, don't
        /// turn or tilt it) and W × H measured along the wall. No face found: left as taped.
        public static bool RefinePlane(ref TapedOpening o, SceneRoot root)
        {
            if (root == null) return false;
            var t = root.transform;
            float scale = Mathf.Max(t.lossyScale.x, 1e-6f);
            const float Outside = 0.10f, Start = 0.6f, Reach = 1.2f;
            var p = s_Probes;
            float hw = o.W * 0.5f + Outside, hh = o.H * 0.5f + Outside;
            p[0] = o.Centre + o.Right * hw; p[1] = o.Centre + o.Right * hw + o.Up * (o.H * 0.25f); p[2] = o.Centre + o.Right * hw - o.Up * (o.H * 0.25f);
            p[3] = o.Centre - o.Right * hw; p[4] = o.Centre - o.Right * hw + o.Up * (o.H * 0.25f); p[5] = o.Centre - o.Right * hw - o.Up * (o.H * 0.25f);
            p[6] = o.Centre + o.Up * hh; p[7] = o.Centre + o.Up * hh + o.Right * (o.W * 0.3f); p[8] = o.Centre + o.Up * hh - o.Right * (o.W * 0.3f);
            var n = o.Out;
            int nf = 0, nb = 0;
            for (int i = 0; i < p.Length; i++)
            {
                if (Probe(t, p[i] + n * Start, -n, (Start + Reach) * scale, out var pf, out var nrmF)) { s_FrontP[nf] = pf; s_FrontN[nf++] = nrmF; }
                if (Probe(t, p[i] - n * Start, n, (Start + Reach) * scale, out var pb, out var nrmB)) { s_BackP[nb] = pb; s_BackN[nb++] = nrmB; }
            }
            bool front = OpeningMath.FitFace(s_FrontP, s_FrontN, nf, n, out var nF, out float dF);
            bool back = OpeningMath.FitFace(s_BackP, s_BackN, nb, -n, out var nB, out float dB);
            if (!OpeningMath.ChooseFace(front, nF, dF, back, nB, dB, o.Centre, out var normal, out float offset)) return false;
            OpeningMath.OnFace(ref o, normal, offset);
            return true;
        }

        /// One ray (SceneRoot space in, world for the physics): the hit's point and normal in SceneRoot space.
        static bool Probe(Transform t, Vector3 from, Vector3 dir, float reachWorld, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            if (!Physics.Raycast(t.TransformPoint(from), t.TransformDirection(dir).normalized, out var hit, reachWorld, SceneLayers.SceneSurfaceMask,
                    QueryTriggerInteraction.Ignore)) return false;
            point = t.InverseTransformPoint(hit.point);
            normal = t.InverseTransformDirection(hit.normal).normalized;
            return true;
        }

        static readonly Vector3[] s_Probes = new Vector3[9];
        static readonly Vector3[] s_FrontP = new Vector3[9], s_FrontN = new Vector3[9], s_BackP = new Vector3[9], s_BackN = new Vector3[9];

        // ---------------- queries ----------------

        /// The opening the newest tape belongs to (the one being worked on): the parts search and the agent's context
        /// carry it. False when the newest reading isn't one of an opening's.
        public static bool TryCurrent(out TapedOpening o)
        {
            o = default;
            var list = All;
            if (list.Count == 0) return false;
            int newest = -1;
            var entries = Notebook.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Tool == "measure" && !Gaps.IsGapTape(entries[i]) && entries[i].OnCurrentSite) { newest = entries[i].Id; break; }   // sitescope
            foreach (var x in list)
                if (x.EntryA == newest || x.EntryB == newest) { o = x; return true; }
            return false;
        }

        /// The current opening as context.opening / the search's `opening`, or null.
        public static Dictionary<string, object> CurrentContext() => TryCurrent(out var o) ? OpeningMath.Context(o) : null;

        public static bool TryGet(string id, out TapedOpening o)
        {
            o = default;
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var x in All) if (x.Id == id) { o = x; return true; }
            return false;
        }

        /// The opening a part (its box centre at `rootPoint`, `partW` × `partH` metres) is at and is for: the nearest one
        /// whose region holds the point (OpeningMath.AtOpening, ForOpening). No allocation.
        public static bool TryAt(Vector3 rootPoint, float partW, float partH, out TapedOpening o)
        {
            o = default;
            var list = All;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                var x = list[i];
                if (!OpeningMath.ForOpening(partW, partH, x) || !OpeningMath.AtOpening(x, rootPoint)) continue;
                float d = (rootPoint - x.Centre).sqrMagnitude;
                if (d >= best) continue;
                best = d; o = x; found = true;
            }
            return found;
        }

        /// One line per opening (the harness).
        public static string Report()
        {
            var list = All;
            if (list.Count == 0) return "openings: none";
            var sb = new System.Text.StringBuilder("openings:");
            foreach (var o in list) sb.Append("\n  ").Append(o.ToString());
            return sb.ToString();
        }
    }
}
