using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Lane G2: the Grok actions that draw in the scene (backend docs/api.md §5): show_plan (dashed segments with their
    /// lengths, 30 % ghosts of the selected part, the plan's label, a Place chip), place_array on a plan's points,
    /// survey_started (spinner, then polling) / show_survey (severity pins with pinchable "issue · 90 %" labels),
    /// show_coverage (the capture coach's ring, legs and drone icons) and show_labels / the pre-labelled scan (pinchable
    /// name labels that search for the part).
    /// Everything lives under the scene content (the package frame), so it follows calibration, the tabletop model and
    /// revision swaps; points come in the glTF / structure-file frame and go through GltfFrame's X flip, the same one the
    /// mesh and the structure layer get. Nothing is drawn without a loaded scene or for another site. Lines are the
    /// measure style's (camera-facing, dark casing), captions are MeasureLabels (LabelLayout), and pinchable labels are
    /// clones of a UiBuild chip. Labels come from WorldLabels' one pool of 12 (declutter S10): the layer's honesty
    /// caption first, then its items; over the pool an item drops to its pin, line, wedge or anchor dot. Nothing here pays.
    /// One Grok layer at a time (DC6, declutter M10): a new plan, survey, coverage ring or live-label set replaces the
    /// others; the scan's own labels (Settings ▸ Labels) are the exception, a layer toggle rather than a Grok answer.
    public class GrokOverlays : MonoBehaviour, IWorldLabelSource, ISiteScoped   // sitescope: ISiteScoped
    {
        public SceneRoot sceneRoot;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("Transparent unlit material (alpha blend, both faces, no depth write): coverage wedges and part ghosts.")]
        public Material glassMaterial;
        [Tooltip("Inactive chip (UiBuild GlassButton, Chip style, poke + ray) cloned for every pinchable world label.")]
        public GlassButton chipTemplate;
        public GrokOverlayCard card;
        [Tooltip("The Scene window's Labels toggle (pre-labelled scan).")]
        public GlassButton sceneLabelsToggle;

        public const float GhostAlpha = 0.3f;
        public const float LiveLabelSeconds = 20f;
        public const float PlanLineWidth = 0.01f;
        const string PlaceCommand = "place them";

        class OverlaySet
        {
            public GrokOverlayKind Kind;
            public GameObject Root;
            public float ShownAt;
            public float ExpiresAt = float.PositiveInfinity;
            /// Mandatory (the honesty label, the Place chip); counted first by the budget.
            public readonly List<GameObject> Captions = new List<GameObject>();
            /// Item labels, in priority order (the budget hides the tail).
            public readonly List<GameObject> Items = new List<GameObject>();
            /// Items[k]'s anchor dot (null when a pin marker or a line already marks the spot): shown while the pool
            /// has no room for the label (declutter §5.1).
            public readonly List<Transform> Dots = new List<Transform>();
            /// Markers that keep a constant angular size: (transform, world size per metre, minimum world size).
            public readonly List<(Transform t, float perMetre, float min)> Scaled = new List<(Transform, float, float)>();
            public readonly List<PartInstance> Ghosts = new List<PartInstance>();
            public readonly List<DashedLine> Lines = new List<DashedLine>();
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly List<Vector3> Anchors = new List<Vector3>();
        }

        readonly Dictionary<GrokOverlayKind, OverlaySet> m_Sets = new Dictionary<GrokOverlayKind, OverlaySet>();
        GameObject m_Root, m_Content;
        /// modelview: every overlay set lives under this (Model view hides it while the scene is on the table).
        public Transform AnnotationRoot => m_Root != null ? m_Root.transform : null;
        string m_Site;
        SceneRoot m_Hooked;
        PartTool m_Parts;
        bool m_Placing;
        MaterialPropertyBlock m_Block;
        MaterialPropertyBlock Block => m_Block ?? (m_Block = new MaterialPropertyBlock());
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");

        public PlanView Plan { get; private set; }
        public SurveyView Survey { get; private set; }
        public CoverageView Coverage { get; private set; }
        public LabelsView Labels { get; private set; }
        public LabelsView SceneLabels { get; private set; }
        public bool SceneLabelsOn { get; private set; }
        /// GET /scenes/{site}/labels status for the loaded site (−1 = not asked, 404 = none).
        public long SceneLabelsHttp { get; private set; } = -1;
        public string PendingSurveyId { get; private set; }
        public int SurveyPolls { get; private set; }
        public string LastSpoken { get; private set; }
        public string LastResult { get; private set; } = "";

        public int PlanGhosts { get; private set; }
        public int PlanMarkers { get; private set; }
        public int PinsDrawn { get; private set; }
        public int LabelsPlaced { get; private set; }
        public int LabelsMissed { get; private set; }
        public int SceneLabelsDrawn { get; private set; }
        public int WedgesDrawn { get; private set; }
        public int GreenWedges { get; private set; }
        public int LegsDrawn { get; private set; }

        SceneRoot Root => sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();

        void OnEnable() { Services.Register(this); Hook(); RegisterSite(); }   // sitescope: RegisterSite

        void OnDisable()
        {
            Services.Unregister(this);
            SiteScope.Unregister(this);   // sitescope
            WorldLabels.Remove(this);
            if (m_Hooked != null) m_Hooked.ContentChanged -= OnContentChanged;
            m_Hooked = null;
            if (m_Parts != null) m_Parts.SelectionChanged -= OnSelectionChanged;
            m_Parts = null;
        }

        void Start() => Hook();

        void Hook()
        {
            var r = Root;
            if (r != null && r != m_Hooked)
            {
                if (m_Hooked != null) m_Hooked.ContentChanged -= OnContentChanged;
                r.ContentChanged += OnContentChanged;
                m_Hooked = r;
                OnContentChanged();
            }
            if (m_Parts == null && Services.TryGet(out m_Parts)) m_Parts.SelectionChanged += OnSelectionChanged;
        }

        // ---------------- the scene ----------------

        /// The loaded scene's content (the package frame), or null: nothing is drawn without one.
        public Transform Content
        {
            get
            {
                var r = Root;
                return r != null && r.Content != null ? r.Content.transform : null;
            }
        }

        public string LoadedSite
        {
            get
            {
                var r = Root;
                return r != null && r.Content != null ? r.Site : null;
            }
        }

        /// True when `site` names the loaded scene (a null site can't be checked, so it passes).
        public bool IsLoadedSite(string site) => string.IsNullOrEmpty(site) || string.Equals(site, LoadedSite, StringComparison.OrdinalIgnoreCase);

        /// A fresh load (another site, the built-in fallback) replaces the content and everything under it; a revision
        /// swap keeps the content, so the overlays stay (the frame id doesn't change across revisions).
        void OnContentChanged()
        {
            var r = Root;
            var content = r != null ? r.Content : null;
            if (content == m_Content) return;
            m_Content = content;
            foreach (var s in m_Sets.Values) DestroySet(s);
            m_Sets.Clear();
            m_Root = null;
            Plan = null; Survey = null; Coverage = null; Labels = null;
            GrokState.PlanId = null;
            string site = LoadedSite;
            if (site != m_Site)
            {
                m_Site = site;
                SceneLabels = null;
                SceneLabelsHttp = -1;
                if (!string.IsNullOrEmpty(site) && r != null && r.IsRuntimePackage && Application.isPlaying) StartCoroutine(FetchSceneLabels(site, show: SceneLabelsOn));
            }
            else if (SceneLabelsOn && SceneLabels != null) DrawSceneLabels();
            RefreshToggle();
            ApplyBudget();
            RestoreParked();   // sitescope: the arriving site's plan / survey / coverage / labels, under the new content
        }

        Transform OverlayRoot()
        {
            var content = Content;
            if (content == null) return null;
            if (m_Root == null || m_Root.transform.parent != content)
            {
                m_Root = new GameObject("GrokOverlays");
                m_Root.transform.SetParent(content, false);
            }
            return m_Root.transform;
        }

        /// The kinds that draw in the scene, in a fixed order (deterministic walks over m_Sets).
        static readonly GrokOverlayKind[] s_Layers =
        {
            GrokOverlayKind.Plan, GrokOverlayKind.Survey, GrokOverlayKind.Coverage, GrokOverlayKind.Labels, GrokOverlayKind.SceneLabels,
        };

        OverlaySet NewSet(GrokOverlayKind kind)
        {
            if (m_Sets.TryGetValue(kind, out var old)) DestroySet(old);
            ReplaceOtherLayers(kind);
            var parent = OverlayRoot();
            var s = new OverlaySet { Kind = kind, ShownAt = Time.unscaledTime, Root = new GameObject(kind.ToString()) };
            s.Root.transform.SetParent(parent, false);
            m_Sets[kind] = s;
            return s;
        }

        void DestroySet(OverlaySet s)
        {
            if (s == null) return;
            foreach (var m in s.Meshes) GrokObjects.Destroy(m);
            GrokObjects.DestroyGameObject(s.Root);
        }

        /// Remove one overlay from the scene (the card's Hide, a new scene, a placed plan).
        public void Hide(GrokOverlayKind kind)
        {
            Drop(kind);
            ApplyBudget();
        }

        void Drop(GrokOverlayKind kind)
        {
            if (m_Sets.TryGetValue(kind, out var s)) { DestroySet(s); m_Sets.Remove(kind); }
            if (kind == GrokOverlayKind.Plan) { Plan = null; GrokState.PlanId = null; }
            if (kind == GrokOverlayKind.Coverage) Coverage = null;
            if (kind == GrokOverlayKind.Labels) Labels = null;
            if (kind == GrokOverlayKind.SceneLabels) { SceneLabelsOn = false; RefreshToggle(); }
        }

        /// DC6 (M10): one Grok world layer at a time. A plan replaces the live labels and the pins, a survey the plan,
        /// and so on; the survey's data (GrokState.SurveyId, "find a fix for pin f1") stays for the agent. The scan's
        /// own labels neither replace nor get replaced: they are Settings ▸ Labels, a toggle the user owns.
        void ReplaceOtherLayers(GrokOverlayKind kind)
        {
            if (kind == GrokOverlayKind.SceneLabels || kind == GrokOverlayKind.None) return;
            foreach (var other in s_Layers)
                if (other != kind && other != GrokOverlayKind.SceneLabels && m_Sets.ContainsKey(other))
                {
                    Log.Info($"Grok overlays: {kind} replaces {other} (one layer at a time)");
                    Drop(other);
                }
        }

        /// The Grok layers drawn now, in a fixed order ("Plan,SceneLabels"), for the harness.
        public string ShownLayers()
        {
            var sb = new StringBuilder();
            foreach (var k in s_Layers) if (m_Sets.ContainsKey(k)) { if (sb.Length > 0) sb.Append(','); sb.Append(k); }
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        public void ClearAll()
        {
            foreach (var s in m_Sets.Values) DestroySet(s);
            m_Sets.Clear();
            Plan = null; Survey = null; Coverage = null; Labels = null;
            GrokState.PlanId = null;
            SceneLabelsOn = false;
            RefreshToggle();
            ApplyBudget();
        }

        public bool IsShown(GrokOverlayKind kind) => m_Sets.ContainsKey(kind);

        // ---------------- sitescope: a Grok layer belongs to the site it was drawn on ----------------

        /// One site's Grok layer while another site is loaded: its sets (plan with its ghosts, survey pins, coverage
        /// ring, live labels) parked under an inactive holder in their package-frame poses, the views, the plan and
        /// survey ids the context sends, and a survey still being polled. The scan's own labels (Settings ▸ Labels) are
        /// the loaded site's already (fetched per site).
        sealed class SiteState
        {
            public readonly List<OverlaySet> Sets = new List<OverlaySet>();
            public PlanView Plan;
            public SurveyView Survey;
            public CoverageView Coverage;
            public LabelsView Labels;
            public string PlanId, SurveyId, PendingSurveyId;
        }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);
        SiteState m_Restore;
        Transform m_Parking;

        Transform Parking
        {
            get
            {
                if (m_Parking != null) return m_Parking;
                var go = new GameObject("ParkedOverlays");
                go.SetActive(false);
                go.transform.SetParent(transform, false);
                return m_Parking = go.transform;
            }
        }

        /// Grok sets parked for `site` (the harness census).
        public int ParkedSets(string site) => Sites.Peek(site)?.Sets.Count ?? 0;

        public void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderGrok);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            SiteState park = null;
            foreach (var kind in s_Layers)
            {
                if (kind == GrokOverlayKind.SceneLabels || !m_Sets.TryGetValue(kind, out var set)) continue;
                park ??= new SiteState();
                if (set.Root != null) set.Root.transform.SetParent(Parking, false);   // local = package frame: kept
                park.Sets.Add(set);
                m_Sets.Remove(kind);
            }
            if (park != null || Plan != null || Survey != null || Coverage != null || Labels != null || !string.IsNullOrEmpty(GrokState.SurveyId) || PendingSurveyId != null)
            {
                park ??= new SiteState();
                park.Plan = Plan; park.Survey = Survey; park.Coverage = Coverage; park.Labels = Labels;
                park.PlanId = GrokState.PlanId; park.SurveyId = GrokState.SurveyId; park.PendingSurveyId = PendingSurveyId;
            }
            if (m_Restore != null)
            {
                // The site being left came back but its layer wasn't put back yet (no content): it parks again as it was.
                if (park == null) park = m_Restore;
                else
                {
                    park.Sets.AddRange(m_Restore.Sets);
                    park.Plan ??= m_Restore.Plan; park.Survey ??= m_Restore.Survey; park.Coverage ??= m_Restore.Coverage; park.Labels ??= m_Restore.Labels;
                    park.PlanId ??= m_Restore.PlanId; park.SurveyId ??= m_Restore.SurveyId; park.PendingSurveyId ??= m_Restore.PendingSurveyId;
                }
                m_Restore = null;
            }
            Plan = null; Survey = null; Coverage = null; Labels = null;
            GrokState.PlanId = null; GrokState.SurveyId = null;
            PendingSurveyId = null;   // its poll stops; it starts again when the site comes back
            GrokState.LastFrameId = null; GrokState.ViewFrameId = null;   // frame ids are the site left's cameras'
            GrokState.ViewFrame = default;
            m_Restore = Sites.Swap(to, park);
            var r = Root;
            // A held arrival (SceneStreamer) comes back after ContentChanged already ran for the new content: now.
            if (m_Restore != null && r != null && r.Content != null && r.Content == m_Content) RestoreParked();
            ApplyBudget();
        }

        /// The arriving site's parked layer back under the loaded content (OnContentChanged, or the switch itself).
        void RestoreParked()
        {
            var st = m_Restore;
            if (st == null || Content == null) return;
            m_Restore = null;
            var parent = OverlayRoot();
            foreach (var set in st.Sets)
            {
                if (set.Root == null) continue;
                if (m_Sets.TryGetValue(set.Kind, out var clash)) DestroySet(clash);
                set.Root.transform.SetParent(parent, false);
                m_Sets[set.Kind] = set;
            }
            Plan = st.Plan; Survey = st.Survey; Coverage = st.Coverage; Labels = st.Labels;
            GrokState.PlanId = st.PlanId;
            if (!string.IsNullOrEmpty(st.SurveyId)) GrokState.SurveyId = st.SurveyId;
            if (!string.IsNullOrEmpty(st.PendingSurveyId))
            {
                PendingSurveyId = st.PendingSurveyId;
                if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(AwaitSurvey(st.PendingSurveyId));
            }
            ApplyBudget();
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll()) foreach (var set in st.Sets) DestroySet(set);
            if (m_Restore != null) { foreach (var set in m_Restore.Sets) DestroySet(set); m_Restore = null; }
        }
        // end sitescope

        Color RoleColor(ColorRole role) => UiTheme.Current.Color(role);

        // ---------------- show_plan / place_array (F7) ----------------

        public bool PlanMatches(string planId) => Plan != null && !string.IsNullOrEmpty(planId) && planId == Plan.PlanId && IsShown(GrokOverlayKind.Plan);

        /// Draw a plan: each segment dashed (1 cm) with its length at the middle, a 30 % ghost of the selected part on
        /// each point (a small marker without one), the plan's label, and a Place chip.
        public bool ShowPlan(PlanView plan, string reply = null)
        {
            if (plan == null) return Fail("show_plan: unreadable");
            if (Content == null) return Fail("show_plan ignored: no scene loaded");
            Plan = plan;
            GrokState.PlanId = plan.PlanId;
            DrawPlan();
            string subtitle = plan.HasPoints
                ? $"{plan.Points.Count} {(plan.Points.Count == 1 ? "point" : "points")} · {Copy.Len(plan.TotalLengthM)}"
                : $"{plan.Segments.Count} {(plan.Segments.Count == 1 ? "run" : "runs")} · {Copy.Len(plan.TotalLengthM)}";
            card?.Show(GrokOverlayKind.Plan, "Plan", subtitle, string.IsNullOrWhiteSpace(reply) ? PlanSummary(plan) : Copy.Clean(reply), plan.Label, true, PlaceText(plan));
            return Ok($"show_plan {plan.PlanId}: {plan.Segments.Count} segments, {plan.Points.Count} points, ghosts={PlanGhosts} markers={PlanMarkers}");
        }

        static string PlaceText(PlanView plan) => plan.HasPoints ? $"Place {plan.Points.Count}" : "Place";

        static string PlanSummary(PlanView plan) => plan.HasPoints
            ? $"{plan.Points.Count} along {Copy.LenFull(plan.TotalLengthM)}"
            : $"{(plan.Segments.Count == 1 ? "One run" : $"{plan.Segments.Count} runs")}, {Copy.LenFull(plan.TotalLengthM)}";

        void DrawPlan()
        {
            var plan = Plan;
            var s = NewSet(GrokOverlayKind.Plan);
            var ink = RoleColor(SeverityStyle.Named("Ink", ColorRole.TextPrimary));
            int k = 0;
            foreach (var seg in plan.Segments)
            {
                var pts = new List<Vector3> { PlanGeometry.ToContent(seg.A), PlanGeometry.ToContent(seg.B) };
                var line = DashedLine.Create(s.Root.transform, $"Segment{k++}", style);
                line.nominalWidth = PlanLineWidth;
                Dashes.Pattern(Dashes.Length(pts, false), 16, 0.03f, out float dash, out float gap);
                line.Set(pts, false, dash, gap, ink);
                s.Lines.Add(line);
                var label = Label(s.Root.transform, $"Length{k}", Copy.LenFull(seg.LengthM), PlanGeometry.Midpoint(seg) + Vector3.up * 0.04f, 1f, 2);
                AddItem(s, label.gameObject);   // over the pool: the dashed segment stays
            }
            BuildGhosts(s);
            var centre = PlanGeometry.Centroid(plan);
            if (!string.IsNullOrEmpty(plan.Label))
                s.Captions.Add(Label(s.Root.transform, "PlanLabel", plan.Label, centre - Vector3.up * 0.08f, 0.9f, 3).gameObject);
            // "Place N" is the plan's first item (the task, P2), ahead of the lengths; the label above is its honesty
            // caption (P1).
            var chip = Chip(s, "Place", PlaceText(plan), centre, () => RequestPlace());
            if (chip != null) { s.Items.Insert(0, chip.gameObject); s.Dots.Insert(0, null); }
            ApplyBudget();
        }

        /// The ghosts follow the part in hand (or the selected placed part): taking a part after the plan arrived swaps
        /// the markers for ghosts.
        void OnSelectionChanged(PartInstance part)
        {
            if (m_Placing || Plan == null || !Plan.HasPoints || !m_Sets.TryGetValue(GrokOverlayKind.Plan, out var s)) return;
            BuildGhosts(s);
        }

        void BuildGhosts(OverlaySet s)
        {
            foreach (var g in s.Ghosts) if (g != null) GrokObjects.DestroyGameObject(g.gameObject);
            s.Ghosts.Clear();
            for (int i = s.Scaled.Count - 1; i >= 0; i--)
                if (s.Scaled[i].t != null && s.Scaled[i].t.name.StartsWith("PlanPoint", StringComparison.Ordinal)) { GrokObjects.DestroyGameObject(s.Scaled[i].t.gameObject); s.Scaled.RemoveAt(i); }
            PlanGhosts = PlanMarkers = 0;
            var plan = Plan;
            if (plan == null || !plan.HasPoints) return;
            var content = Content;
            var tool = m_Parts != null ? m_Parts : Services.Get<PartTool>();
            var template = tool != null ? (tool.Held != null ? tool.Held : tool.Selected) : null;
            var ink = RoleColor(SeverityStyle.Named("Ink", ColorRole.TextPrimary));
            var viewer = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            int k = 0;
            foreach (var p in plan.Points)
            {
                var local = PlanGeometry.ToContent(p);
                if (template != null && template.Spec != null)
                {
                    var ghost = Ghost(template, s.Root.transform, content, local, viewer, $"Ghost{k}");
                    if (ghost != null) { s.Ghosts.Add(ghost); PlanGhosts++; k++; continue; }
                }
                var marker = Marker(s.Root.transform, $"PlanPoint{k++}", local, ink);
                s.Scaled.Add((marker, 0.008f, 0.012f));
                PlanMarkers++;
            }
        }

        /// A clone of the part at 30 % alpha: no collider, no callout, seated on the surface within 8 cm of the point.
        PartInstance Ghost(PartInstance template, Transform parent, Transform content, Vector3 local, Vector3 viewer, string name)
        {
            PartInstance g;
            try { g = template.Clone(parent, style); }
            catch (Exception ex) { Log.Warn($"Grok plan: couldn't ghost {template.Spec.id}: {ex.Message}"); return null; }
            g.name = name;
            if (g.Box != null) g.Box.enabled = false;
            if (g.Outline != null) g.Outline.gameObject.SetActive(false);
            // True size under the calibrated content (parts live under SceneRoot; this lives under its content).
            g.transform.localScale = Vector3.one / Mathf.Max(content.localScale.x, 1e-4f);
            var world = content.TransformPoint(local);
            g.transform.SetPositionAndRotation(world, template.transform.rotation);
            float radius = 0.08f * Mathf.Max(SnapService.Scale, 1e-4f);
            if (SnapService.TrySnap(world, out var hit, radius, features: false)) PartPlacer.Apply(g, PartPlacer.MatchSurface(g.Spec, hit), viewer);
            Tint(g);
            return g;
        }

        void Tint(PartInstance g)
        {
            var tint = UiTheme.Current.colors.textPrimary;
            tint.a = GhostAlpha;
            foreach (var r in g.Model.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                var textures = new Texture[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    textures[i] = MainTexture(mats[i]);
                    if (glassMaterial != null) mats[i] = glassMaterial;
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var b = new MaterialPropertyBlock();
                    b.SetColor(s_BaseColor, tint);
                    if (textures[i] != null) b.SetTexture(s_BaseMap, textures[i]);
                    r.SetPropertyBlock(b, i);
                }
            }
        }

        static Texture MainTexture(Material m)
        {
            if (m == null) return null;
            foreach (var prop in new[] { "_BaseMap", "baseColorTexture", "_MainTex" })
                if (m.HasProperty(prop) && m.GetTexture(prop) != null) return m.GetTexture(prop);
            return null;
        }

        /// The Place pinch (card or chip): "place them" as a normal agent command (the server answers place_array with
        /// this plan's id, or the BOM for a run). Without an agent (tests), place at the points directly.
        public void RequestPlace()
        {
            if (Plan == null) { UiToast.Show("No plan to place · ask for one first", ColorRole.Warning); return; }
            if (AppCommands.SendCommand(PlaceCommand)) { Log.Info($"Grok plan {Plan.PlanId}: sent \"{PlaceCommand}\""); return; }
            PlacePlan(null);
        }

        /// place_array on the plan on screen: the part at every point, one Undo for the set (PartTool.PlaceAtPoints).
        public bool PlacePlan(float? spacingMm)
        {
            var plan = Plan;
            var content = Content;
            if (plan == null || !plan.HasPoints || content == null) return Fail("place_array: no plan with points on screen");
            var tool = m_Parts != null ? m_Parts : Services.Get<PartTool>();
            if (tool == null) return Fail("place_array: no part tool");
            var points = PlanGeometry.WorldPoints(plan, content.localToWorldMatrix);
            // The ghosts go first: the placed parts take their spots (and ghosts carry no collider anyway).
            if (m_Sets.TryGetValue(GrokOverlayKind.Plan, out var s)) { foreach (var g in s.Ghosts) if (g != null) g.gameObject.SetActive(false); }
            PartTool.ArrayGroup group;
            m_Placing = true;
            try { group = tool.PlaceAtPoints(points, spacingMm ?? (plan.Points.Count > 1 ? PlanGeometry.Spacing(plan) * 1000f : (float?)null), plan.PlanId); }
            finally { m_Placing = false; }
            if (group == null)
            {
                if (s != null) foreach (var g in s.Ghosts) if (g != null) g.gameObject.SetActive(true);
                UiToast.Show(tool.LastAction != null && tool.LastAction.Contains("take a part") ? "Take a part first, then place them" : "Couldn't place the plan", ColorRole.Warning);
                return Fail($"place_array: {tool.LastAction}");
            }
            int n = PartTool.ArrayMembers(group).Count;
            var first = PartTool.ArrayMembers(group);
            string noun = first.Count > 0 ? Copy.Noun(first[0].Spec, first[0].SearchQuery) : "part";
            UiToast.Show($"✓ {n} {(n == 1 ? noun : Copy.Plural(noun))} placed · Undo removes them", ColorRole.Success);
            Hide(GrokOverlayKind.Plan);
            card?.Close();
            return Ok($"place_array {plan.PlanId}: {n} placed ({tool.LastAction})");
        }

        // ---------------- survey_started / show_survey (F10) ----------------

        /// survey_started: a spinner; if no show_survey for it follows in the same reply, poll the survey and speak it.
        public bool BeginSurvey(string surveyId)
        {
            if (string.IsNullOrEmpty(surveyId)) return Fail("survey_started: no survey_id");
            PendingSurveyId = surveyId;
            card?.SetBusy(GrokOverlayKind.Survey, "Condition survey", "Grok is looking at the drone photos", SurveyLabel);
            if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(AwaitSurvey(surveyId));
            return Ok($"survey_started {surveyId}");
        }

        const string SurveyLabel = "AI triage from drone frames, not an inspection";

        IEnumerator AwaitSurvey(string id)
        {
            yield return null;   // the rest of this reply's actions run first (a show_survey may follow)
            if (PendingSurveyId != id || (Survey != null && Survey.SurveyId == id)) yield break;
            float t0 = Time.realtimeSinceStartup;
            while (PendingSurveyId == id)
            {
                long code = 0; JObject body = null;
                yield return GrokOverlayClient.GetJson($"/scene/survey/{id}", 10, (c, b) => { code = c; body = b; });
                SurveyPolls++;
                if (PendingSurveyId != id || (Survey != null && Survey.SurveyId == id)) yield break;
                var step = SurveyPoll.Next(code, body != null ? (string)body["status"] : null, Time.realtimeSinceStartup - t0);
                if (step == SurveyPollStep.Done)
                {
                    PendingSurveyId = null;
                    var view = SurveyView.Parse(body);
                    if (string.IsNullOrEmpty(view.SurveyId)) view.SurveyId = id;
                    ShowSurvey(view);
                    Say(view.Spoken);
                    yield break;
                }
                if (step != SurveyPollStep.Poll)
                {
                    PendingSurveyId = null;
                    card?.StopBusy();
                    string why = step == SurveyPollStep.GiveUp ? "The survey is taking too long · ask again in a minute"
                        : body != null && body["error"] != null ? $"The survey failed · {Copy.Clip((string)body["error"], 60)}" : "The survey didn't finish";
                    UiToast.Show(why, ColorRole.Warning);
                    card?.Show(GrokOverlayKind.Survey, "Condition survey", "no result", why, SurveyLabel, false);
                    Log.Warn($"Grok survey {id}: {step} (HTTP {code})");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(SurveyPoll.IntervalSeconds);
            }
        }

        /// Speak a line the way voice replies are (POST /voice/speak), and show it on the reply card.
        public void Say(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            LastSpoken = text;
            UiToast.Reply(Copy.Clip(text, 80));
            if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(GrokOverlayClient.Speak(text, gameObject));
        }

        /// show_survey with pins: a sphere at each pin's point, by severity, with a pinchable "issue · 90 %" label; the
        /// survey's label always shows. The survey id goes to GrokState for the context.
        public bool ShowSurvey(SurveyView survey)
        {
            if (survey == null) return Fail("show_survey: unreadable");
            if (!string.IsNullOrEmpty(survey.SurveyId)) GrokState.SurveyId = survey.SurveyId;
            if (PendingSurveyId == survey.SurveyId) PendingSurveyId = null;
            if (Content == null) { card?.StopBusy(); return Fail("show_survey ignored: no scene loaded"); }
            if (!IsLoadedSite(survey.Site)) { card?.StopBusy(); return Fail($"show_survey ignored: site {survey.Site} isn't the loaded {LoadedSite}"); }
            Survey = survey;
            var s = NewSet(GrokOverlayKind.Survey);
            PinsDrawn = 0;
            var centre = Vector3.zero;
            foreach (var pin in survey.Ranked())
            {
                if (pin.P == null) continue;
                var local = PlanGeometry.ToContent(pin.P);
                var marker = Marker(s.Root.transform, $"Pin {pin.Id}", local, RoleColor(SeverityStyle.Role(pin.Severity)));
                float scale = SeverityStyle.MarkerScale(pin.Severity);
                s.Scaled.Add((marker, 0.012f * scale, 0.02f * scale));
                string id = pin.Id;
                var chip = Chip(s, $"PinLabel {pin.Id}", pin.LabelText, local, () => FixPin(id));
                if (chip != null) AddItem(s, chip.gameObject);   // over the pool: the pin's own marker stays
                centre += local;
                PinsDrawn++;
            }
            string label = survey.Label ?? SurveyLabel;
            if (PinsDrawn > 0) s.Captions.Add(Label(s.Root.transform, "SurveyLabel", label, centre / PinsDrawn - Vector3.up * 0.1f, 0.9f, 3).gameObject);
            ApplyBudget();
            int hidden = survey.Pins.Count - PinsDrawn;
            string subtitle = survey.Pins.Count == 0 ? "nothing pinned" : $"{PinsDrawn} pinned" + (hidden > 0 ? $" · {hidden} not located" : "");
            card?.Show(GrokOverlayKind.Survey, "Condition survey", subtitle, SurveyBody(survey), label, false);
            return Ok($"show_survey {survey.SurveyId}: {PinsDrawn}/{survey.Pins.Count} pins drawn");
        }

        static string SurveyBody(SurveyView s)
        {
            if (s.Pins.Count == 0) return string.IsNullOrEmpty(s.Spoken) ? "No problems found in these photos." : s.Spoken;
            var sb = new StringBuilder();
            int n = 0;
            foreach (var p in s.Ranked())
            {
                if (n++ >= 5) { sb.Append($"+{s.Pins.Count - 5} more"); break; }
                sb.Append($"{p.Id} · {Copy.Cap(p.Severity)} · {Copy.Clip(p.Issue ?? p.Element ?? "", 44)}{(p.Confidence > 0 ? " · " + GrokJson.Percent(p.Confidence) : "")}");
                if (p.P == null) sb.Append(" · not located");
                sb.Append('\n');
            }
            sb.Append("Pinch a pin to find a fix");
            return sb.ToString();
        }

        /// A pinch on a pin: "find a fix for pin f1" as a normal agent command (search_started + a notebook note back).
        public void FixPin(string pinId)
        {
            string text = $"find a fix for pin {pinId}";
            if (!AppCommands.SendCommand(text)) UiToast.Show("Can't reach the agent · say it instead", ColorRole.Warning);
            Log.Info($"Grok survey: \"{text}\"");
        }

        // ---------------- show_coverage (F12) ----------------

        /// The capture coach: 8 ground wedges (green seen, red not, labelled), the legs as dashed arcs / a dashed square
        /// with a drone icon tilted by the gimbal pitch, and the caption. Interior scans: the note only.
        public bool ShowCoverage(CoverageView c)
        {
            if (c == null) return Fail("show_coverage: unreadable");
            if (Content == null) return Fail("show_coverage ignored: no scene loaded");
            if (!IsLoadedSite(c.Site)) return Fail($"show_coverage ignored: site {c.Site} isn't the loaded {LoadedSite}");
            Coverage = c;
            WedgesDrawn = GreenWedges = LegsDrawn = 0;
            if (c.Interior || !c.HasRing)
            {
                Hide(GrokOverlayKind.Coverage);
                Coverage = c;
                card?.Show(GrokOverlayKind.None, "Capture coverage", c.Interior ? "room scan" : "", c.Caption, "", false);
                return Ok($"show_coverage {c.Site}: {c.Mode}, caption only");
            }
            var s = NewSet(GrokOverlayKind.Coverage);
            var verts = new List<Vector3>();
            var tris = new List<int>();
            double lift = 0.05;
            for (int k = 0; k < c.Sides.Count; k++)
            {
                CoverageGeometry.Wedge(c, k, 8, lift, verts, tris);
                var side = c.Sides[k];
                var color = RoleColor(side.Seen ? ColorRole.Success : ColorRole.Danger);
                color.a = 0.35f;
                Surface(s, $"Wedge {side.Label}", verts, tris, color);
                WedgesDrawn++;
                if (side.Seen) GreenWedges++;
                var label = Label(s.Root.transform, $"Side {side.Label}", $"{(side.Seen ? "✓" : "✗")} {Copy.Cap(side.Label)}", CoverageGeometry.WedgeCentre(c, k, lift + 0.3), 1f, 1);
                AddItem(s, label.gameObject);   // over the pool: the wedge stays
            }
            var legColor = RoleColor(SeverityStyle.Named("Info", ColorRole.TextPrimary));
            int n = 0;
            foreach (var leg in c.Legs)
            {
                List<Vector3> pts; bool closed;
                if (leg.IsArc) { pts = CoverageGeometry.Arc(c, leg); closed = CoverageGeometry.IsFullCircle(leg); }
                else if (leg.Pattern == "nadir_grid") { pts = CoverageGeometry.NadirSquare(c, leg); closed = true; }
                else continue;
                if (pts.Count < 2) continue;
                var line = DashedLine.Create(s.Root.transform, $"Leg{n++} {leg.Pattern}", style);
                line.nominalWidth = 0.05f;
                float sweep = (float)(leg.IsArc ? Math.Abs(leg.SweepDeg ?? 360.0) : 360.0);
                Dashes.Pattern(Dashes.Length(pts, closed), Mathf.Max(8, Mathf.RoundToInt(sweep / 6f)), 0.2f, out float dash, out float gap);
                line.Set(pts, closed, dash, gap, legColor);
                s.Lines.Add(line);
                Drone(s, $"Drone {leg.Pattern}", CoverageGeometry.DronePoint(c, leg), CoverageGeometry.DroneForward(c, leg), legColor);
                LegsDrawn++;
            }
            ApplyBudget();
            card?.Show(GrokOverlayKind.Coverage, "Capture coverage", $"{CoverageGeometry.SeenCount(c)} of {c.Sides.Count} sides", c.Caption, VerdictWords(c.Verdict), false);
            return Ok($"show_coverage {c.Site}: wedges={WedgesDrawn} green={GreenWedges} legs={LegsDrawn}");
        }

        static string VerdictWords(string verdict) => verdict switch
        {
            "good" => "Good coverage",
            "reshoot_some" => "Reshoot some sides",
            "reshoot_most" => "Reshoot most sides",
            _ => "",
        };

        /// A small drone: body, four rotors and a camera nose, its forward along the gimbal's view.
        void Drone(OverlaySet s, string name, Vector3 local, Vector3 forwardLocal, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(s.Root.transform, false);
            go.transform.localPosition = local;
            var fwd = forwardLocal.sqrMagnitude > 1e-8f ? forwardLocal.normalized : Vector3.forward;
            var up = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.98f ? Vector3.forward : Vector3.up;
            // The body's plane tilts with the gimbal (a drone icon "tilted down by gimbal_pitch_deg").
            go.transform.localRotation = Quaternion.LookRotation(fwd, up);
            Prim(go.transform, PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0.18f, 0.5f), color);
            foreach (var (x, z) in new[] { (0.45f, 0.45f), (-0.45f, 0.45f), (0.45f, -0.45f), (-0.45f, -0.45f) })
                Prim(go.transform, PrimitiveType.Cylinder, new Vector3(x, 0.08f, z), new Vector3(0.42f, 0.02f, 0.42f), color);
            Prim(go.transform, PrimitiveType.Cube, new Vector3(0f, -0.02f, 0.38f), new Vector3(0.16f, 0.16f, 0.26f), RoleColor(ColorRole.Background));
            s.Scaled.Add((go.transform, 0.03f, 0.3f));
        }

        void Prim(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Color color)
        {
            var p = GameObject.CreatePrimitive(type);
            var col = p.GetComponent<Collider>();
            if (col != null) GrokObjects.Destroy(col);
            p.transform.SetParent(parent, false);
            p.transform.localPosition = pos;
            p.transform.localScale = scale;
            var r = p.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (style.markerMaterial != null) r.sharedMaterial = style.markerMaterial;
            var block = Block;
            r.GetPropertyBlock(block);
            block.SetColor(s_BaseColor, color);
            r.SetPropertyBlock(block);
        }

        void Surface(OverlaySet s, string name, List<Vector3> verts, List<int> tris, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(s.Root.transform, false);
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            s.Meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sharedMaterial = glassMaterial != null ? glassMaterial : style.markerMaterial;
            var block = Block;
            r.GetPropertyBlock(block);
            block.SetColor(s_BaseColor, color);
            r.SetPropertyBlock(block);
        }

        // ---------------- show_labels / the pre-labelled scan (F16) ----------------

        /// show_labels: each label placed where the ray from camera `frame_id` through its box centre meets the scan (the
        /// same camera ray as scene_pin), a pinchable name that searches for the part; the set fades after 20 s or when
        /// the next lands. Its label shows once (a toast).
        public bool ShowLabels(LabelsView view)
        {
            if (view == null) return Fail("show_labels: unreadable");
            var content = Content;
            if (content == null) return Fail("show_labels ignored: no scene loaded");
            var streamer = Services.Get<SceneStreamer>();
            var cam = streamer != null && !string.IsNullOrEmpty(view.FrameId) ? streamer.FindCamera(view.FrameId) : null;
            if (cam == null) return Fail($"show_labels: no camera '{view.FrameId}' in this scene package (can't place the labels)");
            Labels = view;
            var s = NewSet(GrokOverlayKind.Labels);
            s.ExpiresAt = Time.unscaledTime + LiveLabelSeconds;
            LabelsPlaced = LabelsMissed = 0;
            var ranked = new List<SceneLabel>(view.Labels);
            ranked.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            foreach (var l in ranked)
            {
                if (!FrameRay.TryCast(content, cam, l.Box, 500f, out var hit)) { LabelsMissed++; continue; }
                var local = content.InverseTransformPoint(hit.point);
                s.Anchors.Add(local);
                var query = l.SearchQuery;
                var chip = Chip(s, $"Label {l.Id}", LabelText(l), local, () => SearchFor(query));
                if (chip != null) AddItem(s, chip.gameObject, Dot(s, $"Dot {l.Id}", local));
                LabelsPlaced++;
            }
            ApplyBudget();
            if (!string.IsNullOrEmpty(view.Label)) UiToast.Show(view.Label, ColorRole.TextSecondary);
            return Ok($"show_labels {view.FrameId}: {LabelsPlaced} placed, {LabelsMissed} missed the scan");
        }

        /// Name large, detail under it in the secondary colour; greyed below 0.7 confidence.
        static string LabelText(SceneLabel l)
        {
            var colors = UiTheme.Current.colors;
            string sec = ColorUtility.ToHtmlStringRGBA(colors.textSecondary);
            string name = Copy.Cap(l.Name);
            if (l.Greyed) name = $"<color=#{sec}>{name}</color>";
            return string.IsNullOrWhiteSpace(l.Detail) ? name : $"{name}\n<color=#{sec}>{Copy.Clip(l.Detail, 36)}</color>";
        }

        /// A tap on a label: the Find parts search for its query (POST /parts/search {"query"}).
        public void SearchFor(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            Log.Info($"Grok label: search \"{query}\"");
            if (!AppCommands.FindPart(query)) UiToast.Show("Find parts isn't ready · try again", ColorRole.Warning);
        }

        /// The Scene window's Labels toggle: the scan's pre-made labels (GET /scenes/{site}/labels), no Grok call.
        public void ToggleSceneLabels() => SetSceneLabels(!SceneLabelsOn);

        public void SetSceneLabels(bool on)
        {
            SceneLabelsOn = on;
            RefreshToggle();
            if (!on) { Hide(GrokOverlayKind.SceneLabels); return; }
            string site = LoadedSite;
            if (SceneLabels != null && IsLoadedSite(SceneLabels.Site)) { DrawSceneLabels(); return; }
            if (SceneLabelsHttp == 404) { UiToast.Show("No labels for this scan yet", ColorRole.Warning); return; }
            if (!string.IsNullOrEmpty(site) && Application.isPlaying && isActiveAndEnabled) StartCoroutine(FetchSceneLabels(site, show: true));
            else if (string.IsNullOrEmpty(site)) UiToast.Show("Load a scan first", ColorRole.Warning);
        }

        IEnumerator FetchSceneLabels(string site, bool show)
        {
            long code = 0; JObject body = null;
            yield return GrokOverlayClient.GetJson($"/scenes/{site}/labels", 10, (c, b) => { code = c; body = b; });
            if (site != LoadedSite) yield break;
            SceneLabelsHttp = code;
            SceneLabels = body != null && code >= 200 && code < 300 ? LabelsView.Parse(body) : null;
            if (SceneLabels != null && string.IsNullOrEmpty(SceneLabels.Site)) SceneLabels.Site = site;
            Log.Info($"Grok scene labels for {site}: HTTP {code}, {(SceneLabels != null ? SceneLabels.Labels.Count : 0)} labels");
            if (!SceneLabelsOn) yield break;
            if (SceneLabels != null) DrawSceneLabels();
            else if (show) UiToast.Show(code == 404 ? "No labels for this scan yet" : "Couldn't get the scan's labels", ColorRole.Warning);
        }

        /// Draw a GET /scenes/{site}/labels body (the harness feeds the backend's example; the toggle fetches it).
        public bool ShowSceneLabels(LabelsView view)
        {
            if (view == null) return Fail("scene labels: unreadable");
            if (Content == null) return Fail("scene labels ignored: no scene loaded");
            if (!IsLoadedSite(view.Site)) return Fail($"scene labels ignored: site {view.Site} isn't the loaded {LoadedSite}");
            SceneLabels = view;
            SceneLabelsOn = true;
            RefreshToggle();
            DrawSceneLabels();
            return Ok($"scene labels {view.Site}: {SceneLabelsDrawn} drawn");
        }

        void DrawSceneLabels()
        {
            if (SceneLabels == null || Content == null) return;
            var s = NewSet(GrokOverlayKind.SceneLabels);
            SceneLabelsDrawn = 0;
            foreach (var l in SceneLabels.Ranked())
            {
                if (l.Pos == null) continue;
                var local = PlanGeometry.ToContent(l.Pos);
                s.Anchors.Add(local);
                var query = l.SearchQuery;
                var chip = Chip(s, $"SceneLabel {l.Id}", LabelText(l), local, () => SearchFor(query));
                if (chip != null) AddItem(s, chip.gameObject, Dot(s, $"Dot {l.Id}", local));
                SceneLabelsDrawn++;
            }
            ApplyBudget();
            if (!string.IsNullOrEmpty(SceneLabels.Label)) UiToast.Show(SceneLabels.Label, ColorRole.TextSecondary);
        }

        void RefreshToggle()
        {
            if (sceneLabelsToggle != null) sceneLabelsToggle.SetSelected(SceneLabelsOn);
        }

        // ---------------- building blocks ----------------

        MeasureLabel Label(Transform parent, string name, string text, Vector3 local, float scale, int priority)
        {
            var label = MeasureLabel.Create(parent, style, name);
            label.tabular = false;
            label.Set(text, scale);
            label.Priority = priority;
            label.SetAnchorLocal(local);
            return label;
        }

        Transform Marker(Transform parent, string name, Vector3 local, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (style.markerMaterial != null) r.sharedMaterial = style.markerMaterial;
            var block = Block;
            r.GetPropertyBlock(block);
            block.SetColor(s_BaseColor, color);
            r.SetPropertyBlock(block);
            return go.transform;
        }

        WorldChip Chip(OverlaySet s, string name, string text, Vector3 local, Action onClick)
        {
            if (chipTemplate == null) return null;
            var chip = WorldChip.Create(chipTemplate, s.Root.transform, local, text, name);
            if (chip != null && onClick != null) chip.Clicked += onClick;
            return chip;
        }

        static void AddItem(OverlaySet s, GameObject item, Transform dot = null)
        {
            s.Items.Add(item);
            s.Dots.Add(dot);
        }

        /// A 1.2° anchor dot for a chip that has no marker of its own (live and scan labels): what the label drops to
        /// when the pool is full. Hidden until then.
        Transform Dot(OverlaySet s, string name, Vector3 local)
        {
            var dot = Marker(s.Root.transform, name, local, RoleColor(ColorRole.TextPrimary));
            dot.gameObject.SetActive(false);
            return dot;
        }

        /// World size of an anchor dot per metre of viewing distance (1.2°) and its floor.
        const float DotPerMetre = 0.021f, DotMin = 0.008f;

        // ---------------- the label pool (declutter S10, DC7) ----------------

        /// The label diet is WorldLabels' one pool of 12 now, shared with the tapes, the survey, fall edges, parts and
        /// readings: this set's honesty caption is P1 (mandatory), its items P2 (the scan's own labels P6), newest
        /// layer first. Hidden items come back when there's room again.
        void ApplyBudget() => WorldLabels.Changed(this);

        readonly List<OverlaySet> m_ClaimSets = new List<OverlaySet>(8);

        /// Two claims per drawn set, in the fixed kind order: its captions, then its items. A layer counts while it
        /// exists, even while the scan is hidden (freeze scope: everything active counts; the scan's visibility has no
        /// event, so gating on it could let the pool under-count on the way back into the world).
        public void ClaimLabels(List<LabelClaim> claims)
        {
            m_ClaimSets.Clear();
            foreach (var kind in s_Layers)
            {
                if (!m_Sets.TryGetValue(kind, out var s) || s.Root == null) continue;
                m_ClaimSets.Add(s);
                int captions = 0, items = 0;
                foreach (var c in s.Captions) if (c != null) captions++;
                foreach (var c in s.Items) if (c != null) items++;
                bool passive = kind == GrokOverlayKind.SceneLabels;
                claims.Add(new LabelClaim(LabelClass.Focus, passive ? 0 : captions, 0, s.ShownAt));
                claims.Add(new LabelClaim(passive ? LabelClass.Passive : LabelClass.Task, passive ? captions : 0, items, s.ShownAt));
            }
        }

        public void ApplyLabels(LabelGrant[] grants, int first)
        {
            for (int i = 0; i < m_ClaimSets.Count; i++)
            {
                var s = m_ClaimSets[i];
                if (s.Root == null) continue;
                int captions = grants[first + 2 * i].Mandatory + grants[first + 2 * i + 1].Mandatory;
                int items = grants[first + 2 * i + 1].Items;
                int k = 0;
                foreach (var c in s.Captions) if (c != null) Show(c, k++ < captions);
                k = 0;
                for (int j = 0; j < s.Items.Count; j++)
                {
                    var item = s.Items[j];
                    if (item == null) continue;
                    bool on = k++ < items;
                    Show(item, on);
                    var dot = j < s.Dots.Count ? s.Dots[j] : null;
                    if (dot != null) Show(dot.gameObject, !on);
                }
            }
        }

        static void Show(GameObject go, bool on) { if (go.activeSelf != on) go.SetActive(on); }

        /// Labels showing now (captions + items), for the harness.
        public int VisibleLabelCount
        {
            get
            {
                int n = 0;
                foreach (var s in m_Sets.Values)
                {
                    foreach (var c in s.Captions) if (c != null && c.activeSelf) n++;
                    foreach (var c in s.Items) if (c != null && c.activeSelf) n++;
                }
                return n;
            }
        }

        /// Positions of what a kind drew, in the scene frame (glTF: the flip undone) — the harness logs them.
        public List<Vector3> ScenePositions(GrokOverlayKind kind)
        {
            var list = new List<Vector3>();
            if (!m_Sets.TryGetValue(kind, out var s)) return list;
            foreach (var g in s.Ghosts) if (g != null) list.Add(GltfFrame.ToGltf(g.transform.localPosition));
            foreach (var m in s.Scaled) if (m.t != null && !m.t.name.StartsWith("Drone", StringComparison.Ordinal)) list.Add(GltfFrame.ToGltf(m.t.localPosition));
            foreach (var a in s.Anchors) list.Add(GltfFrame.ToGltf(a));
            return list;
        }

        public int LineCount(GrokOverlayKind kind) => m_Sets.TryGetValue(kind, out var s) ? s.Lines.Count : 0;

        void LateUpdate()
        {
            if (m_Sets.Count == 0) return;
            if (m_Sets.TryGetValue(GrokOverlayKind.Labels, out var live) && Time.unscaledTime > live.ExpiresAt) { Hide(GrokOverlayKind.Labels); if (m_Sets.Count == 0) return; }
            var cam = Camera.main;
            var content = Content;
            float inv = content != null ? 1f / Mathf.Max(content.localScale.x, 1e-4f) : 1f;
            foreach (var s in m_Sets.Values)
            {
                if (s.Root == null) continue;
                float parentScale = Mathf.Max(s.Root.transform.lossyScale.x, 1e-5f);
                foreach (var (t, perMetre, min) in s.Scaled)
                {
                    if (t == null) continue;
                    float d = cam != null ? Vector3.Distance(cam.transform.position, t.position) : 2f;
                    t.localScale = Vector3.one * (Mathf.Max(min * parentScale, d * perMetre) / parentScale);
                }
                // Ghosts keep their true size when the scene's calibration changes (they live under the content).
                foreach (var g in s.Ghosts)
                    if (g != null && Mathf.Abs(g.transform.localScale.x - inv) > 1e-5f) g.transform.localScale = Vector3.one * inv;
                // Anchor dots (labels the pool had no room for) keep 1.2°.
                foreach (var t in s.Dots)
                {
                    if (t == null || !t.gameObject.activeSelf) continue;
                    float d = cam != null ? Vector3.Distance(cam.transform.position, t.position) : 2f;
                    t.localScale = Vector3.one * (Mathf.Max(DotMin * parentScale, d * DotPerMetre) / parentScale);
                }
            }
        }

        bool Ok(string result)
        {
            LastResult = result;
            Log.Info($"Grok overlays: {result}");
            return true;
        }

        bool Fail(string result)
        {
            LastResult = result;
            Log.Warn($"Grok overlays: {result}");
            return false;
        }
    }
}
