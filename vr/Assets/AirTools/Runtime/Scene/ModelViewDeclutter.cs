using System.Collections.Generic;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Structure;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Scene
{
    /// Model view shows the model only (modelview): applies the ModelView gate in one place. While the scene is (going)
    /// on the table, every renderer under the world annotation owners' roots (ModelView.Owners: tapes, levels, ladders,
    /// ask pins, Grok overlays, the coach's boxes and card, the crosshair, fall edges, part callouts and outlines, cavity
    /// labels, placement guides, bought-part labels) is forced off with Renderer.forceRenderingOff — a flag the owners
    /// never touch, so their own enabled / active state, the label pool and the layout go on as before and leaving the
    /// table restores exactly what they show then. Runs after the label layout (MeasureLabel, order 500), so a leader line
    /// made this frame is hidden this frame. No allocation per frame after warm-up.
    [DefaultExecutionOrder(1000)]
    public class ModelViewDeclutter : MonoBehaviour
    {
        public TabletopController tabletop;
        public SceneRoot sceneRoot;
        [Tooltip("The coach's crosshair (not a service).")]
        public DrillCrosshair crosshair;

        /// Renderers hidden right now.
        public int HiddenCount => m_Forced.Count;
        public bool Hiding { get; private set; }

        readonly HashSet<Renderer> m_Forced = new HashSet<Renderer>();
        readonly List<Transform> m_Roots = new List<Transform>(32);
        readonly List<Renderer> m_Scratch = new List<Renderer>(256);
        readonly List<Renderer> m_Plates = new List<Renderer>(4);
        readonly List<PartOutline> m_Outlines = new List<PartOutline>(32);
        readonly List<CavityView> m_Cavities = new List<CavityView>(8);

        void OnEnable() => Services.Register(this);

        void OnDisable()
        {
            Services.Unregister(this);
            Restore();
            ModelView.Set(false);
        }

        void LateUpdate() => Refresh();

        /// edit6dof: the Edit view (Parts/EditView) shows the item alone: the same annotations are hidden while it's open,
        /// without it being Model view (ModelView's gate and Hiding stay as they are).
        public static bool EditViewHides;

        /// Follow the mode: hide (and keep hiding what appears) while the scene is on the table, restore once it's back.
        public void Refresh()
        {
            bool hide = ModelView.Hides(AppState.Mode, tabletop != null && tabletop.OnTable);
            if (ModelView.Set(hide)) Log.Info($"Model view: world labels and annotations {(hide ? "hidden (the model only)" : "back")}");
            Hiding = hide;
            if (hide || EditViewHides) HideNow();   // edit6dof: the Edit view too
            else if (m_Forced.Count > 0) Restore();
            if (hide) HideGroundPlates();
        }

        /// The scene's big flat ground plate stays in the world (headset 2026-09-27: the built-in facade's 30 × 25 m test
        /// ground became a 3 m slab at chest height on the table, around you and over the switcher wheel). The same rule as
        /// the table fit's footprint (TabletopFit.IsGroundPlate); a scene that is all slab keeps it. Returns how many.
        public int HideGroundPlates()
        {
            var root = sceneRoot != null ? sceneRoot : tabletop != null ? tabletop.root : null;
            if (root == null) return 0;
            var content = root.Content != null ? root.Content.transform : root.transform;
            float s = Mathf.Max(root.transform.lossyScale.x, 1e-4f);
            content.GetComponentsInChildren(false, m_Scratch);
            m_Plates.Clear();
            bool building = false;
            for (int i = 0; i < m_Scratch.Count; i++)
            {
                var r = m_Scratch[i];
                if (r == null || !r.enabled || r is LineRenderer) continue;
                if (TabletopFit.IsGroundPlate(r.bounds.extents / s)) m_Plates.Add(r);
                else building = true;
            }
            if (!building) return 0;
            int n = 0;
            for (int i = 0; i < m_Plates.Count; i++)
            {
                var r = m_Plates[i];
                if (r.forceRenderingOff) continue;
                r.forceRenderingOff = true;
                m_Forced.Add(r);
                n++;
            }
            return n;
        }

        /// Force every annotation renderer off now; returns how many are hidden.
        public int HideNow()
        {
            CollectRoots(m_Roots);
            for (int i = 0; i < m_Roots.Count; i++)
            {
                var root = m_Roots[i];
                if (root == null) continue;
                root.GetComponentsInChildren(true, m_Scratch);
                for (int k = 0; k < m_Scratch.Count; k++)
                {
                    var r = m_Scratch[k];
                    if (r == null || r.forceRenderingOff) continue;
                    r.forceRenderingOff = true;
                    m_Forced.Add(r);
                }
            }
            return m_Forced.Count;
        }

        /// Everything back as the owners left it.
        public void Restore()
        {
            foreach (var r in m_Forced) if (r != null) r.forceRenderingOff = false;
            m_Forced.Clear();
        }

        /// Is this renderer hidden by Model view?
        public bool Hides(Renderer r) => r != null && m_Forced.Contains(r);

        /// Where each owner's annotations live (the census in ModelView.Owners).
        public void CollectRoots(List<Transform> into)
        {
            into.Clear();
            void Add(Transform t) { if (t != null) into.Add(t); }
            // Tapes and areas, the finish chip, the B1 survey labels and the survey progress (SurveyRunner).
            if (Services.TryGet<MeasureTool>(out var measure)) Add(measure.viewRoot);
            if (Services.TryGet<LevelTool>(out var level)) Add(level.viewRoot);
            if (Services.TryGet<LadderTool>(out var ladder)) Add(ladder.viewRoot);
            if (Services.TryGet<SceneAsk>(out var ask))
                for (int i = 0; i < ask.Pins.Count; i++) if (ask.Pins[i].marker != null) Add(ask.Pins[i].marker.transform);
            if (Services.TryGet<GrokOverlays>(out var overlays)) Add(overlays.AnnotationRoot);   // chips (WorldChip) included
            if (Services.TryGet<CoachOverlay>(out var coach)) Add(coach.transform);
            Add(crosshair != null ? crosshair.transform : null);
            if (Services.TryGet<FallEdges>(out var fall)) Add(fall.AnnotationRoot);
            if (Services.TryGet<PlacementGuides>(out var guides)) Add(guides.AnnotationRoot);
            if (Services.TryGet<TakeItHome>(out var home))
                for (int i = 0; i < home.Labels.Count; i++) if (home.Labels[i] != null) Add(home.Labels[i].transform);
            // Part callouts and outlines (the parts' own meshes stay), cavity labels and outlines (the cavity mesh is scene).
            var root = sceneRoot != null ? sceneRoot : tabletop != null ? tabletop.root : null;
            if (root != null)
            {
                root.GetComponentsInChildren(true, m_Outlines);
                for (int i = 0; i < m_Outlines.Count; i++) Add(m_Outlines[i].transform);
                root.GetComponentsInChildren(true, m_Cavities);
                for (int i = 0; i < m_Cavities.Count; i++) Add(m_Cavities[i].transform);
            }
            if (home != null)
                for (int i = 0; i < home.OnTable.Count; i++)
                    if (home.OnTable[i] != null && home.OnTable[i].Outline != null) Add(home.OnTable[i].Outline.transform);
        }

        /// World text still drawing (harness and tests): every text and label plate under the owners' roots, every
        /// MeasureLabel and WorldChip anywhere, and every text under the scene root, that is active, enabled and not
        /// forced off. Allocates (not for per-frame use).
        public List<string> VisibleWorldText()
        {
            var seen = new HashSet<Renderer>();
            var shown = new List<string>();
            void Check(Renderer r)
            {
                if (r == null || !seen.Add(r)) return;
                if (r.gameObject.activeInHierarchy && r.enabled && !r.forceRenderingOff) shown.Add(PathOf(r.transform));
            }
            var roots = new List<Transform>();
            CollectRoots(roots);
            foreach (var t in roots)
                foreach (var tmp in t.GetComponentsInChildren<TMPro.TMP_Text>(true)) Check(tmp.GetComponent<Renderer>());
            foreach (var l in FindObjectsByType<MeasureLabel>(FindObjectsSortMode.None))
                foreach (var tmp in l.GetComponentsInChildren<TMPro.TMP_Text>(true)) Check(tmp.GetComponent<Renderer>());
            foreach (var c in FindObjectsByType<WorldChip>(FindObjectsSortMode.None))
                foreach (var tmp in c.GetComponentsInChildren<TMPro.TMP_Text>(true)) Check(tmp.GetComponent<Renderer>());
            var sr = sceneRoot != null ? sceneRoot : tabletop != null ? tabletop.root : null;
            if (sr != null)
                foreach (var tmp in sr.GetComponentsInChildren<TMPro.TMP_Text>(true)) Check(tmp.GetComponent<Renderer>());
            return shown;
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
