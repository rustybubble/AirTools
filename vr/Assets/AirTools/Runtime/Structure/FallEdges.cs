using System.Collections.Generic;
using AirTools.Core;
using AirTools.Input;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Structure
{
    /// Fall-edge overlay (presence.md S4, P7): every edge horizontal within 10° with the ground 1.8 m (6 ft) or more
    /// below it glows as a red / white dashed band, and up to three carry "1.8 m+ edge · fall protection"
    /// (29 CFR 1926.501(b)(1)). Pinch near one (Move or Ladder tool) to read its drop height. One mesh (two submeshes:
    /// red and white dashes) under SceneRoot, rebuilt when the scene's edges change; nothing runs while it's off.
    /// Toggle with AppCommands.ShowFallEdges, the ring's Fall edges item or the ladder card; placing a ladder turns it on.
    /// The labels are safety (P0) in WorldLabels' one pool of 12 (declutter S10): they count first.
    public class FallEdges : MonoBehaviour, IWorldLabelSource, ISiteScoped   // sitescope: ISiteScoped
    {
        public SceneRoot sceneRoot;
        [Tooltip("Transparent unlit material (tinted per submesh).")]
        public Material material;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("Band height below the edge and dash length (scene m).")]
        public float bandHeight = 0.07f, dashLength = 0.25f;
        [Tooltip("The band stands this far off the edge's face (scene m).")]
        public float standOff = 0.012f;
        public int maxLabels = 3;
        [Tooltip("A pinch whose ray passes this close to a flagged edge reads its drop (scene m).")]
        public float pickRadius = 0.2f;
        public float pickSeconds = 4f;

        public bool Visible { get; private set; }
        public int FlaggedCount { get; private set; }
        public int DashCount { get; private set; }
        public int LabelCount => m_LabelEdge.Count;
        public string LastPick { get; private set; } = "";

        GameObject m_Root;
        /// modelview: the edges and their labels (Model view hides them while the scene is on the table).
        public Transform AnnotationRoot => m_Root != null ? m_Root.transform : null;
        Mesh m_Mesh;
        MeshRenderer m_Renderer;
        MaterialPropertyBlock m_Block;
        int m_BuiltVersion = -1;
        Transform m_BuiltUnder;
        readonly List<MeasureLabel> m_Labels = new List<MeasureLabel>();
        readonly List<int> m_LabelEdge = new List<int>();
        readonly List<Vector3> m_V = new List<Vector3>(256);
        readonly List<int> m_Red = new List<int>(256), m_White = new List<int>(256);
        IToolInput m_Input;
        int m_PickedLabel = -1;
        float m_PickUntil;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        SceneRoot Root => sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();

        void OnEnable()
        {
            Services.Register(this);
            AppState.Changed += OnMode;
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            SiteScope.Register(this);   // sitescope
        }

        void OnDisable()
        {
            Services.Unregister(this);
            SiteScope.Unregister(this);   // sitescope
            AppState.Changed -= OnMode;
            SetInput(null);
            WorldLabels.Remove(this);
        }

        void Start()
        {
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null) m_Input.PressEnd -= OnPressEnd;
            m_Input = input;
            if (m_Input != null) m_Input.PressEnd += OnPressEnd;
        }

        void OnMode(AppMode from, AppMode to) => Rebuild();

        /// Shown by a ladder placement (not a command): hidden again when the ladder tool is put away.
        public bool AutoShown { get; private set; }

        /// On / off. `toast`: say what changed (a command).
        public void SetVisible(bool on, bool toast = false)
        {
            Visible = on;
            AutoShown = false;
            Rebuild();
            if (toast)
                UiToast.Show(on ? (FlaggedCount > 0 ? $"Fall edges · {FlaggedCount} at {Threshold}" : $"No fall edges at {Threshold} here") : "Fall edges off",
                    on && FlaggedCount > 0 ? ColorRole.Warning : ColorRole.Info);
            Log.Info($"Fall edges {(on ? "on" : "off")}: {FlaggedCount} flagged");
        }

        public void Toggle(bool toast = true) => SetVisible(!Visible, toast);

        /// A ladder was placed: show the edges quietly (unless they're already on).
        public void ShowForLadder()
        {
            if (Visible) return;
            SetVisible(true);
            AutoShown = true;
        }

        // sitescope: edges a ladder turned on belong to that ladder's site (the edges themselves are the loaded scan's:
        // SupportEdges rebuilds them). Turned on in Settings, they stay on for every site.
        string m_LiveSite;
        readonly HashSet<string> m_AutoSites = new HashSet<string>();

        public void SwitchSite(string from, string to, float factor)
        {
            string live = m_LiveSite ?? from;
            if (string.IsNullOrEmpty(to) || to == live) return;
            if (AutoShown && Visible) { m_AutoSites.Add(live); HideIfAutoShown(); }
            m_LiveSite = to;
            if (m_AutoSites.Remove(to)) ShowForLadder();
        }

        public void ClearParked() => m_AutoSites.Clear();
        public int ParkedCount => m_AutoSites.Count;
        // end sitescope

        /// The ladder tool was put away: hide the edges if a ladder turned them on.
        public void HideIfAutoShown()
        {
            if (AutoShown && Visible) SetVisible(false);
            AutoShown = false;
        }

        void LateUpdate()
        {
            if (!Visible) return;
            var root = Root;
            if (root != null && (SupportEdgesVersion(root) != m_BuiltVersion || m_BuiltUnder != root.transform)) Rebuild();
            if (m_Root == null || !m_Root.activeSelf) return;
            if (m_PickedLabel >= 0 && Time.unscaledTime > m_PickUntil) RestoreLabels();
            if (!UiSettings.ReducedMotion) TintRed(0.78f + 0.2f * Mathf.Sin(Time.unscaledTime * 3.2f));
        }

        static int SupportEdgesVersion(SceneRoot root)
        {
            SupportEdges.Edges(root);   // refreshes on a content / structure / calibration change
            return SupportEdges.Version;
        }

        /// Rebuild the band mesh and labels (or just show / hide them); the label pool hears about it.
        public void Rebuild()
        {
            BuildOrToggle();
            WorldLabels.Changed(this);
        }

        void BuildOrToggle()
        {
            var root = Root;
            bool show = Visible && root != null && AppState.Mode == AppMode.World;
            if (!show)
            {
                if (m_Root != null) m_Root.SetActive(false);
                if (root == null) FlaggedCount = 0;
                return;
            }
            var edges = SupportEdges.Edges(root);
            if (m_Root != null && m_BuiltVersion == SupportEdges.Version && m_BuiltUnder == root.transform) { m_Root.SetActive(true); return; }
            Clear();
            m_BuiltVersion = SupportEdges.Version;
            m_BuiltUnder = root.transform;
            m_Root = new GameObject("FallEdges");
            m_Root.transform.SetParent(root.transform, false);
            m_Block ??= new MaterialPropertyBlock();

            m_V.Clear(); m_Red.Clear(); m_White.Clear();
            int flagged = 0, dashes = 0;
            for (int i = 0; i < edges.Count; i++)
            {
                if (!edges[i].Flagged) continue;
                flagged++;
                dashes += Band(edges[i].Edge);
            }
            FlaggedCount = flagged;
            DashCount = dashes;
            if (m_V.Count > 0)
            {
                m_Mesh = new Mesh { name = "FallEdges" };
                m_Mesh.SetVertices(m_V);
                m_Mesh.subMeshCount = 2;
                m_Mesh.SetTriangles(m_Red, 0);
                m_Mesh.SetTriangles(m_White, 1);
                m_Mesh.RecalculateBounds();
                m_Root.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
                m_Renderer = m_Root.AddComponent<MeshRenderer>();
                m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Renderer.receiveShadows = false;
                if (material != null) m_Renderer.sharedMaterials = new[] { material, material };
                TintRed(0.9f);
                var white = UiTheme.Current.colors.textPrimary;
                white.a = 0.85f;
                m_Renderer.GetPropertyBlock(m_Block, 1);
                m_Block.SetColor(s_BaseColor, white);
                m_Renderer.SetPropertyBlock(m_Block, 1);
            }

            // At most three labels, highest drop first (priority 2 in the label layout).
            var order = FallEdgeMath.LabelOrder(edges, maxLabels);
            foreach (int i in order)
            {
                var e = edges[i].Edge;
                var label = MeasureLabel.Create(m_Root.transform, style, $"FallLabel{m_Labels.Count}");
                label.Priority = 2;
                label.SetAnchorLocal(e.Mid + Vector3.up * 0.10f + e.Outward * 0.05f);
                label.Set(EdgeText, 1f);
                m_Labels.Add(label);
                m_LabelEdge.Add(i);
            }
            m_PickedLabel = -1;
            Log.Info($"Fall edges: {flagged} of {edges.Count} edges flagged, {dashes} dashes, {m_Labels.Count} labels");
        }

        /// Dashes along one edge: vertical quads on the outward face, from the edge down bandHeight; alternating red /
        /// white. Returns the dash count.
        int Band(in EdgeCandidate e)
        {
            var along = e.B - e.A;
            float len = along.magnitude;
            if (len < 1e-4f) return 0;
            along /= len;
            var n = e.Outward * standOff;
            var top = Vector3.up * 0.01f;
            var bottom = Vector3.down * bandHeight;
            int count = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.05f, dashLength)));
            float step = len / count;
            for (int k = 0; k < count; k++)
            {
                var p0 = e.A + along * (k * step) + n;
                var p1 = e.A + along * ((k + 1) * step) + n;
                int i = m_V.Count;
                m_V.Add(p0 + top); m_V.Add(p1 + top); m_V.Add(p1 + bottom); m_V.Add(p0 + bottom);
                var tris = k % 2 == 0 ? m_Red : m_White;
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            return count;
        }

        void TintRed(float alpha)
        {
            if (m_Renderer == null || m_Block == null) return;
            var red = UiTheme.Current.colors.danger;
            red.a = alpha;
            m_Renderer.GetPropertyBlock(m_Block, 0);
            m_Block.SetColor(s_BaseColor, red);
            m_Renderer.SetPropertyBlock(m_Block, 0);
        }

        // ---------------- pinch to read the drop ----------------

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (!Visible || AppState.Mode != AppMode.World) return;
            if (Services.TryGet<ToolManager>(out var tools) && tools.Active != ToolKind.Move && tools.Active != ToolKind.Ladder && tools.Active != ToolKind.None) return;
            Pick(new Ray(pointer.position, pointer.forward));
        }

        /// The flagged edge the ray passes nearest (within pickRadius): its label reads the drop for a few seconds.
        public bool Pick(Ray worldRay)
        {
            var root = Root;
            if (root == null || m_Root == null) return false;
            var edges = SupportEdges.Edges(root);
            var rt = root.transform;
            var o = rt.InverseTransformPoint(worldRay.origin);
            var d = rt.InverseTransformDirection(worldRay.direction);
            int best = -1;
            float bestDist = pickRadius;
            for (int i = 0; i < edges.Count; i++)
            {
                if (!edges[i].Flagged) continue;
                float dist = FallEdgeMath.RaySegmentDistance(o, d, edges[i].Edge.A, edges[i].Edge.B, out _, out _);
                if (dist <= bestDist) { best = i; bestDist = dist; }
            }
            if (best < 0) return false;
            RestoreLabels();
            int slot = m_LabelEdge.IndexOf(best);
            if (slot < 0)
            {
                // Not one of the labelled three: borrow the last label for it.
                slot = m_Labels.Count - 1;
                if (slot < 0) return false;
                m_LabelEdge[slot] = best;
            }
            var e = edges[best];
            FallEdgeMath.RaySegmentDistance(o, d, e.Edge.A, e.Edge.B, out var onEdge, out _);
            m_Labels[slot].SetAnchorLocal(onEdge + Vector3.up * 0.10f + e.Edge.Outward * 0.05f);
            m_Labels[slot].Set(FallEdgeMath.DropLabel(e.DropM, UiSettings.UnitSystem), 1.1f);   // D2 (LastPick stays metric)
            m_PickedLabel = slot;
            m_PickUntil = Time.unscaledTime + pickSeconds;
            LastPick = $"{e.Edge.Name}: {FallEdgeMath.DropLabel(e.DropM)}";
            return true;
        }

        // D2: the edge label and the toast's threshold in the user's unit.
        static string Threshold => FallEdgeMath.Threshold(UiSettings.UnitSystem);
        static string EdgeText => FallEdgeMath.LabelFor(UiSettings.UnitSystem);

        /// D2: the unit changed — re-word the edge labels (a picked drop label goes back to its edge label).
        public void Relabel()
        {
            if (m_Labels.Count == 0) return;
            m_PickedLabel = int.MaxValue;
            RestoreLabels();
        }

        void RestoreLabels()
        {
            if (m_PickedLabel < 0) return;
            m_PickedLabel = -1;
            var root = Root;
            if (root == null) return;
            var edges = SupportEdges.Edges(root);
            var order = FallEdgeMath.LabelOrder(edges, maxLabels);
            for (int s = 0; s < m_Labels.Count && s < order.Count; s++)
            {
                m_LabelEdge[s] = order[s];
                var e = edges[order[s]].Edge;
                m_Labels[s].SetAnchorLocal(e.Mid + Vector3.up * 0.10f + e.Outward * 0.05f);
                m_Labels[s].Set(EdgeText, 1f);
            }
        }

        void Clear()
        {
            if (m_Root != null) DestroyObj(m_Root);
            if (m_Mesh != null) DestroyObj(m_Mesh);
            m_Root = null; m_Mesh = null; m_Renderer = null;
            m_Labels.Clear();
            m_LabelEdge.Clear();
            m_BuiltVersion = -1;
            m_BuiltUnder = null;
        }

        // ---------------- the label pool (declutter S10) ----------------

        /// P0 safety: every edge label counts first (at most maxLabels; a picked edge reuses one of them).
        public void ClaimLabels(List<LabelClaim> claims)
        {
            bool on = Visible && m_Root != null && m_Root.activeInHierarchy;
            claims.Add(new LabelClaim(LabelClass.Safety, on ? m_Labels.Count : 0, 0, 0f));
        }

        public void ApplyLabels(LabelGrant[] grants, int first)
        {
            int allowed = grants[first].Mandatory;
            for (int i = 0; i < m_Labels.Count; i++)
            {
                var l = m_Labels[i];
                if (l == null) continue;
                bool show = i < allowed || !Visible;   // hidden edges keep their labels for the next show
                if (l.gameObject.activeSelf != show) l.gameObject.SetActive(show);
            }
        }

        static void DestroyObj(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
