#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Dev
{
    /// Declutter census (docs/ux/declutter.md §6, S2): what is on screen right now, counted against the §2 budget —
    /// the heads-up surfaces, the main slot, the side slots, the pill, the wrist, the palm, the quad, the Grok layers,
    /// the world labels and any overlap between head-relative surfaces (measured from the live poses as the eye sees
    /// them). AgentHarness.Surfaces() prints Line(); SurfaceCheck(activity) checks it against UiBudget. UiCensusWatch
    /// samples the heads-up and head-relative counts every frame (no allocation) and keeps their maxima.
    public static class UiCensus
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        static StatusLine s_Line;
        static JobRailView s_Job;
        static NextStepPill s_Pill;
        static ChestController s_Enter;
        static float s_NextFind = -1f;

        public static int MaxHud { get; private set; }
        public static int MaxHeadRelative { get; private set; }
        public static int Samples { get; private set; }

        public static void ResetMax() { MaxHud = 0; MaxHeadRelative = 0; Samples = 0; }

        /// Look the views up again (they aren't services); at most once a second unless forced.
        static void Find(bool force)
        {
            if (!force && Time.unscaledTime < s_NextFind) return;
            s_NextFind = Time.unscaledTime + 1f;
            if (s_Line == null) s_Line = Object.FindFirstObjectByType<StatusLine>(FindObjectsInactive.Include);
            if (s_Job == null) s_Job = Object.FindFirstObjectByType<JobRailView>(FindObjectsInactive.Include);
            if (s_Pill == null) s_Pill = Object.FindFirstObjectByType<NextStepPill>(FindObjectsInactive.Include);
            if (s_Enter == null) s_Enter = Object.FindFirstObjectByType<ChestController>(FindObjectsInactive.Include);
        }

        // ---------------- the counts (no allocation) ----------------

        public static bool LineUp => s_Line != null && s_Line.isActiveAndEnabled && s_Line.Showing;
        public static bool ToastUp => UiToast.Toast != null && UiToast.Toast.isActiveAndEnabled && UiToast.Toast.Showing;
        public static bool ReplyUp => UiToast.ReplyCard != null && UiToast.ReplyCard.isActiveAndEnabled && UiToast.ReplyCard.Showing;
        public static bool JobRailUp => s_Job != null && s_Job.isActiveAndEnabled && s_Job.Showing;
        public static bool PillUp => s_Pill != null && s_Pill.isActiveAndEnabled && s_Pill.Visible && s_Pill.content != null && s_Pill.content.activeInHierarchy;
        public static bool EnterUp => s_Enter != null && s_Enter.button != null && s_Enter.button.gameObject.activeInHierarchy;
        public static bool PartsUp => Services.TryGet<PartsBrowser>(out var b) && b.content != null && b.content.activeInHierarchy;
        public static bool CoachUp => Services.TryGet<CoachRailView>(out var c) && c.IsOpen;
        public static bool SettingsUp => Services.TryGet<AirTools.Structure.ScenePanel>(out var s) && s.IsOpen;
        public static bool CreditUp => Services.TryGet<AirTools.Scene.SceneCreditChip>(out var k) && k.content != null && k.content.activeInHierarchy;

        /// Everything but the labels, the Grok layers and the overlaps.
        public static UiCounts Count()
        {
            Find(false);
            var c = new UiCounts { Labels = -1 };
            c.Hud = (LineUp ? 1 : 0) + (ToastUp ? 1 : 0) + (ReplyUp ? 1 : 0) + (JobRailUp ? 1 : 0);
            c.Main = WindowSlot.Current != null ? 1 : 0;
            c.SideLeft = (PartsUp ? 1 : 0) + (CoachUp ? 1 : 0);
            c.SideRight = SettingsUp ? 1 : 0;
            c.Pill = PillUp ? 1 : 0;
            c.Enter = EnterUp ? 1 : 0;
            c.HeadRelative = c.Hud + c.Main + c.SideLeft + c.SideRight + c.Pill + c.Enter + (CreditUp ? 1 : 0);
            return c;
        }

        /// Every frame (UiCensusWatch): the maxima. No allocation.
        public static void Tick()
        {
            var c = Count();
            Samples++;
            if (c.Hud > MaxHud) MaxHud = c.Hud;
            if (c.HeadRelative > MaxHeadRelative) MaxHeadRelative = c.HeadRelative;
        }

        // ---------------- the full census (allocates: harness only) ----------------

        /// The counts with the Grok layers, the world labels and the overlaps; `line` is the one-line census.
        public static UiCounts Take(out string line)
        {
            Find(true);
            var c = Count();
            var layers = new List<string>();
            if (Services.TryGet<GrokOverlays>(out var overlays))
                foreach (GrokOverlayKind k in System.Enum.GetValues(typeof(GrokOverlayKind)))
                    if (k != GrokOverlayKind.None && overlays.IsShown(k))
                    {
                        layers.Add(k.ToString().ToLowerInvariant());
                        if (k != GrokOverlayKind.SceneLabels) c.GrokLayers++;   // scan labels are a Settings ▸ Layers toggle (M10)
                    }
            c.Labels = LabelsUp();
            var overlaps = Overlaps();
            c.Overlaps = overlaps.Count;

            var sb = new StringBuilder();
            sb.Append($"hud={c.Hud} (max {MaxHud}) docked={Docked()} main={MainName()} sideL={SideLeftName()} sideR={(c.SideRight > 0 ? "settings" : "-")} ");
            sb.Append($"pill={c.Pill} enter={c.Enter} head={c.HeadRelative} (max {MaxHeadRelative}) wrist={Wrist()} palm={Palm()} quad={Quad()} ");
            sb.Append($"layers=[{string.Join(",", layers)}] labels={c.Labels}/12 overlaps={c.Overlaps}");
            sb.Append($" model={Model()}");   // modelview
            if (overlaps.Count > 0) sb.Append($" [{string.Join("; ", overlaps)}]");
            sb.Append($" | heads-up: {HudNames()}");
            line = sb.ToString();
            return c;
        }

        static string HudNames()
        {
            var up = new List<string>();
            if (LineUp) up.Add($"line \"{Copy.Clip(s_Line.Message, 40)}\"");
            if (ToastUp) up.Add($"toast \"{Copy.Clip(UiToast.Toast.Message, 40)}\"");
            if (ReplyUp) up.Add($"reply \"{Copy.Clip(UiToast.ReplyCard.Message, 40)}\"");
            if (JobRailUp) up.Add("job rail");
            return up.Count == 0 ? "-" : string.Join(", ", up);
        }

        /// The window the status line is docked on ("-" when it floats; declutter M3).
        static string Docked() => LineDocked ? MainName() : "-";

        static bool LineDocked => s_Line != null && s_Line.Docked && WindowSlot.Current != null;

        public static string MainName() => WindowSlot.Current is Component w && w != null ? Short(w.gameObject.name) : "-";

        static string SideLeftName()
        {
            bool parts = PartsUp, coach = CoachUp;
            // catalog: the parts window is the Catalog; "+keys" while its glass keyboard is up.
            string p = KeyboardUp ? "catalog+keys" : "catalog";
            return parts && coach ? p + "+coach" : parts ? p : coach ? "coach" : "-";
        }

        /// catalog: the Catalog's keyboard is showing.
        public static bool KeyboardUp => Services.TryGet<CatalogWindow>(out var c) && c.keyboard != null && c.keyboard.IsOpen && c.Showing;

        static string Wrist() => Services.TryGet<LimitsChip>(out var l) && l.Visible ? "limits" : "-";
        static string Palm() => Services.TryGet<AirTools.Input.PalmMenu>(out var p) && p.IsOpen ? "open" : "closed";
        static string Quad() => Services.TryGet<ReimagineQuad>(out var q) && q.Mode != ReimagineQuad.QuadMode.Hidden ? q.Mode.ToString().ToLowerInvariant() : "0";

        /// "CheckoutPanel" → "Checkout", "SurveyCard" → "Survey", "GrokCard" stays.
        static string Short(string name)
        {
            if (name.EndsWith("Panel")) return name.Substring(0, name.Length - 5);
            if (name.EndsWith("Card") && name != "GrokCard" && name != "GrokOverlayCard") return name.Substring(0, name.Length - 4);
            return name;
        }

        /// World labels on screen now: measurement / part / level labels and Grok chips (lane C's WorldLabels pool, S10,
        /// can replace this count). modelview: a label Model view hides (its text forced off) isn't on screen.
        static int LabelsUp()
        {
            int n = 0;
            foreach (var l in Object.FindObjectsByType<AirTools.Tools.MeasureLabel>(FindObjectsSortMode.None)) if (l.isActiveAndEnabled && Drawn(l)) n++;
            foreach (var w in Object.FindObjectsByType<WorldChip>(FindObjectsSortMode.None)) if (w.isActiveAndEnabled && Drawn(w)) n++;
            return n;
        }

        // modelview
        /// Its text renders (not forced off by Model view).
        static bool Drawn(Component c)
        {
            var t = c.GetComponentInChildren<TMPro.TMP_Text>();
            var r = t != null ? t.GetComponent<Renderer>() : null;
            return r == null || !r.forceRenderingOff;
        }

        /// Model view's wheel: "-" outside it, else the model on view, the one under the lens and what's loading
        /// ("kitchen 1:5 lens=zabel-gymnasium models=7 loading=-"; modelwheel).
        static string Model()
        {
            if (!Services.TryGet<AirTools.Scene.ModelSwitcher>(out var s) || !s.Shown) return "-";
            var table = s.tabletop;
            string scale = table != null ? table.ScaleLabel : "?";
            var wheel = s.GetComponent<AirTools.Scene.ModelWheel>();
            return $"{s.Current} {scale} lens={(wheel != null ? wheel.LensSite ?? "-" : "-")} models={s.Sites.Count} loading={s.Loading ?? "-"} hidden={(Services.TryGet<AirTools.Scene.ModelViewDeclutter>(out var d) ? d.HiddenCount : 0)}";
        }
        // end modelview

        // ---------------- overlaps, from the live poses ----------------

        struct Up
        {
            public string Name;
            public UiFootprint Box;
            public float Distance;
            public bool IsMain;
        }

        /// Pairs of head-relative surfaces up now whose boxes (as the eye sees them) overlap by more than an edge; a side
        /// slot's inner edge over the main window (≥ 4 cm behind it, ≤ 6°) is tolerated.
        public static List<string> Overlaps()
        {
            var ups = new List<Up>();
            var head = Camera.main != null ? Camera.main.transform : null;
            if (head == null) return new List<string>();
            void Add(string name, params GlassSurface[] parts)
            {
                bool any = false;
                var box = default(UiFootprint);
                float dist = 0f;
                foreach (var s in parts)
                {
                    if (s == null || !s.gameObject.activeInHierarchy) continue;
                    var b = BoxOf(s, head, out float d);
                    box = any ? UiFootprint.Union(box, b) : b;
                    dist = any ? Mathf.Min(dist, d) : d;
                    any = true;
                }
                if (any) ups.Add(new Up { Name = name, Box = box, Distance = dist, IsMain = name.StartsWith("main:") });
            }
            if (LineUp) Add("line", s_Line.surface);
            if (ToastUp) Add("toast", UiToast.Toast.surface);
            if (ReplyUp) Add("reply", UiToast.ReplyCard.surface);
            if (JobRailUp) Add("job rail", s_Job.panel);
            if (PillUp) Add("pill", s_Pill.primary != null ? s_Pill.primary.surface : null,
                s_Pill.secondary0 != null ? s_Pill.secondary0.surface : null, s_Pill.secondary1 != null ? s_Pill.secondary1.surface : null);
            if (EnterUp) Add("enter", s_Enter.button.surface);
            if (WindowSlot.Current is Component w && w != null) Add($"main:{MainName()}", PanelOf(w.transform));
            if (PartsUp && Services.TryGet<PartsBrowser>(out var parts)) Add("catalog", parts.panel);   // catalog: was "parts"
            if (CoachUp && Services.TryGet<CoachRailView>(out var coach)) Add("coach", coach.panel);
            if (SettingsUp && Services.TryGet<AirTools.Structure.ScenePanel>(out var settings)) Add("settings", PanelOf(settings.transform));
            if (CreditUp && Services.TryGet<AirTools.Scene.SceneCreditChip>(out var credit)) Add("credit", credit.pill);

            var hits = new List<string>();
            for (int i = 0; i < ups.Count; i++)
            for (int j = i + 1; j < ups.Count; j++)
            {
                var a = ups[i];
                var b = ups[j];
                var o = UiZones.Overlap(a.Box, b.Box);
                if (o.x <= UiZones.EdgeEpsilonDeg || o.y <= UiZones.EdgeEpsilonDeg) continue;
                // M3: docked, the line, the reply and (glass lane) the fallback toast sit on the window's top rim, wholly above
                // it; their boxes may touch the window's within the rim's lift.
                if (LineDocked && a.IsMain != b.IsMain && (IsDockedPill(a.Name) || IsDockedPill(b.Name))) continue;
                if (a.IsMain != b.IsMain)
                {
                    var main = a.IsMain ? a : b;
                    var side = a.IsMain ? b : a;
                    if (side.Distance >= main.Distance + 0.04f && o.x <= UiZones.SideSlotEdgeDeg) continue;   // tolerated side-slot edge
                }
                hits.Add($"{a.Name} × {b.Name} {o.x.ToString("0.0", C)}×{o.y.ToString("0.0", C)}°");
            }
            return hits;
        }

        static bool IsDockedPill(string name) => name == "line" || name == "reply" || name == "toast" && UiToast.Toast != null && UiToast.Toast.Docked;   // glass

        /// The window's one glass panel ("Content/Panel").
        public static GlassSurface PanelOf(Transform window)
        {
            if (window == null) return null;
            foreach (var s in window.GetComponentsInChildren<GlassSurface>(true))
                if (s.name == "Panel") return s;
            return null;
        }

        /// A surface's angular box from the eye: yaw from the head's heading, pitch from the eye line; `distance` to its
        /// centre.
        static UiFootprint BoxOf(GlassSurface s, Transform head, out float distance)
        {
            var t = s.transform;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            var box = new UiFootprint { YawMin = float.MaxValue, YawMax = float.MinValue, PitchMin = float.MaxValue, PitchMax = float.MinValue };
            float hx = s.size.x * 0.5f, hy = s.size.y * 0.5f;
            for (int k = 0; k < 4; k++)
            {
                var p = t.TransformPoint(new Vector3((k & 1) == 0 ? -hx : hx, (k & 2) == 0 ? -hy : hy, 0f));
                var v = p - head.position;
                var flat = Vector3.ProjectOnPlane(v, Vector3.up);
                float yaw = Vector3.SignedAngle(fwd, flat, Vector3.up);
                float pitch = Mathf.Atan2(v.y, flat.magnitude) * Mathf.Rad2Deg;
                box.YawMin = Mathf.Min(box.YawMin, yaw); box.YawMax = Mathf.Max(box.YawMax, yaw);
                box.PitchMin = Mathf.Min(box.PitchMin, pitch); box.PitchMax = Mathf.Max(box.PitchMax, pitch);
            }
            distance = Vector3.Distance(t.position, head.position);
            return box;
        }

        // ---------------- the budget ----------------

        /// [AirTools.Check] UI.budget.&lt;activity&gt;: the census now against that activity's §2 row.
        public static string Check(string activity)
        {
            if (!UiBudget.TryFind(activity, out var budget)) return $"unknown activity '{activity}' ({UiBudget.Activities()})";
            var c = Take(out var line);
            bool ok = budget.Check(c, out var why);
            Log.Check($"UI.budget.{budget.Activity}", ok, $"{(ok ? "within" : "over: " + why)} | {line}");
            return $"{(ok ? "PASS" : "FAIL")} UI.budget.{budget.Activity} {(ok ? "" : why + " | ")}{line}";
        }
    }
}
#endif
