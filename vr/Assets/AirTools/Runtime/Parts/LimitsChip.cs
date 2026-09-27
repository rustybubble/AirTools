using System;
using AirTools.Core;
using AirTools.UI;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// The wrist strip (declutter M9, DC5; it grew out of the B3 limits chip): glanceable context 5 cm above the left
    /// wrist (hand tracking) or the left controller, facing the eyes, 0.16 m wide.
    /// - Line 1: the purchase limits ("≤ $40 · by Fri · fastest"; voice can only tighten a limit, and a refused loosening
    ///   adds "raise it on the panel") and the scale state ("Scale ✓" / "Scale ×1.63"; not "Scale not set", user 09-27).
    /// - Under it: the loaded scan's credit, verbatim (SceneCreditChip; the Zabel credit wraps to 3 rows at the floor).
    /// Shown in the world while there are limits or a credit (the scale alone isn't a reason to show); hidden in
    /// passthrough and whenever neither the wrist nor a controller is tracked — never head-locked. Recomposed only when
    /// its inputs change (no per-frame allocation).
    public class LimitsChip : MonoBehaviour
    {
        public Transform head;
        [Tooltip("Left ISDK Hand (the wrist it rides on).")]
        public Hand hand;
        [Tooltip("Left controller anchor (used when a controller is in the hand).")]
        public Transform controllerAnchor;
        public GameObject content;
        [Tooltip("Line 1: limits · scale.")]
        public TextMeshPro text;
        [Tooltip("The scan's credit, under line 1.")]
        public TextMeshPro creditText;
        public GlassSurface pill;
        [Tooltip("Lift above the wrist (m).")]
        public float lift = 0.05f;
        [Tooltip("Strip width (m): the declutter Wrist zone.")]
        public float width = 0.16f;
        public float padding = 0.007f;

        public const string ScaleSet = "Scale ✓", ScaleNotSet = "Scale not set";

        /// The session's limits as the server last reported them (null = none set this session).
        public static MandateIntent Current { get; private set; }
        /// A voice ask the server refused (it would have loosened a limit).
        public static bool LastRefused { get; private set; }
        public static event Action Changed;

        /// Line 1 as composed (limits · scale), shown or not.
        public string Line1 { get; private set; } = "";
        /// The scan credit on the strip ("" when none).
        public string Credit { get; private set; } = "";
        /// Everything the strip says (line 1, then the credit).
        public string Shown => Compose(Line1, Credit);
        /// There is something to show (limits or a credit, not in passthrough).
        public bool HasContent { get; private set; }
        /// The left wrist or controller is tracked (the strip has somewhere to be).
        public bool Tracked { get; private set; }
        public bool Visible => content != null && content.activeSelf;

        /// show_limits {intent_id, max_total_usd, deliver_by, seller_policy, refused}.
        public static void Set(MandateIntent intent, bool refused = false)
        {
            Current = intent;
            LastRefused = refused;
            Log.Info($"Limits: {MandateText.Limits(intent)}{(refused ? " (a looser ask was refused: use the panel)" : "")}");
            Changed?.Invoke();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() { Current = null; LastRefused = false; Changed = null; }

        // ---------------- pure (EditMode-tested) ----------------

        /// "Scale ✓" / "Scale not set" for the loaded scene (null: nothing loaded). A scan package is at true size once
        /// Set scale was saved for it; the built-in facade is built to size (ScenePanel.ScaleChip's rule).
        public static string ScaleWord(bool loaded, bool runtimePackage, float calibration) =>
            !loaded ? null : runtimePackage && Mathf.Abs(calibration - 1f) <= 1e-4f ? ScaleNotSet : ScaleSet;

        /// scalemodels: a scan at its site's default scale says so with the factor ("Scale ×1.63", SiteScales); a person's
        /// scale is "Scale ✓", as before.
        public static string ScaleWord(bool loaded, bool runtimePackage, float calibration, AirTools.Scene.ScaleSource source) =>
            loaded && runtimePackage && AirTools.Scene.SiteScales.Consistent(calibration, source) == AirTools.Scene.ScaleSource.SiteDefault
                ? AirTools.Scene.SiteScales.DefaultWord(calibration) : ScaleWord(loaded, runtimePackage, calibration);

        /// Line 1: the limits (with the refusal hint), then the scale — but never "Scale not set" (user 09-27: remove it from
        /// above the palm tools; Settings ▸ Set scale still says it).
        public static string FirstLine(string limits, bool refused, string scale)
        {
            string s = string.IsNullOrEmpty(limits) ? "" : refused ? limits + " · raise it on the panel" : limits;
            if (!string.IsNullOrEmpty(scale) && scale != ScaleNotSet) s = s.Length == 0 ? scale : s + " · " + scale;
            return s;
        }

        /// What the strip says: line 1, then the credit under it.
        public static string Compose(string line1, string credit) =>
            string.IsNullOrEmpty(credit) ? line1 ?? "" : string.IsNullOrEmpty(line1) ? credit : line1 + "\n" + credit;

        /// The strip shows in the world with limits or a scan credit; the scale state rides along.
        public static bool HasSomething(bool hasLimits, string credit, bool passthrough) =>
            !passthrough && (hasLimits || !string.IsNullOrEmpty(credit));

        /// modelview: the model's scale on the table while in Model view (TabletopController.ScaleWord, cached there).
        static bool ModelViewScale(out string word)
        {
            word = null;
            if (AppState.Mode != AppMode.Tabletop || !Services.TryGet<AirTools.Scene.TabletopController>(out var table) || !table.OnTable) return false;
            word = table.ScaleWord;
            return !string.IsNullOrEmpty(word);
        }

        // ---------------- runtime ----------------

        Vector3 m_Pos;
        bool m_Placed, m_Composed, m_LayoutDirty, m_RefusedShown;
        float m_Next;
        MandateIntent m_Limits;
        string m_Scale, m_Credit;

        void OnEnable() { Services.Register(this); Changed += OnChanged; m_Composed = false; Refresh(); }
        void OnDisable() { Services.Unregister(this); Changed -= OnChanged; }

        void OnChanged() { m_Composed = false; Refresh(); }

        /// Read the limits, the scale and the credit; recompose the text only when one of them changed.
        public void Refresh()
        {
            var root = Services.Get<AirTools.Scene.SceneRoot>();
            bool loaded = root != null && root.Content != null;
            var source = Services.TryGet<AirTools.Scene.SceneStreamer>(out var streamer) ? streamer.ScaleSource : AirTools.Scene.ScaleSource.None;   // scalemodels
            string scale = ScaleWord(loaded, loaded && root.IsRuntimePackage, loaded ? root.Calibration : 1f, source);
            string credit = AirTools.Scene.SceneCreditChip.Current();
            var limits = Current != null && Current.Any ? Current : null;
            // modelview: in Model view the strip gives the model's scale ("Model 1:10") and shows even with nothing else.
            bool modelView = ModelViewScale(out string modelScale);
            if (modelView) scale = modelScale;
            HasContent = HasSomething(limits != null, credit, AppState.Mode == AppMode.Passthrough) || modelView;
            if (m_Composed && ReferenceEquals(limits, m_Limits) && LastRefused == m_RefusedShown && ReferenceEquals(scale, m_Scale)
                && ReferenceEquals(credit, m_Credit)) return;
            m_Composed = true;
            m_Limits = limits; m_RefusedShown = LastRefused; m_Scale = scale; m_Credit = credit;
            Line1 = FirstLine(limits != null ? MandateText.Limits(limits) : null, limits != null && LastRefused, scale);
            Credit = credit ?? "";
            if (text != null) text.text = UiText.Tabular(Line1);
            if (creditText != null) creditText.text = Credit;
            m_LayoutDirty = true;
        }

        /// Stack line 1 and the credit from the top and fit the pill round them (only after a change, while shown).
        void Layout()
        {
            m_LayoutDirty = false;
            if (pill == null) return;
            float inner = width - 2f * padding;
            bool hasLine = text != null && Line1.Length > 0, hasCredit = creditText != null && Credit.Length > 0;
            if (text != null) text.gameObject.SetActive(hasLine);
            if (creditText != null) creditText.gameObject.SetActive(hasCredit);
            var s1 = hasLine ? text.GetPreferredValues(text.text, inner, 0f) : Vector2.zero;
            var s2 = hasCredit ? creditText.GetPreferredValues(creditText.text, inner, 0f) : Vector2.zero;
            float h1 = s1.y, h2 = s2.y;   // the credit is never cut: the pill grows with it (3 caption rows at 0.16 m)
            float gap = hasLine && hasCredit ? 0.002f : 0f;
            float W = hasCredit ? width : Mathf.Clamp(s1.x + 2f * padding, 0.06f, width);
            float H = Mathf.Max(0.026f, h1 + gap + h2 + 2f * padding);
            pill.SetSize(new Vector2(W, H));
            float top = H * 0.5f - padding;
            if (hasLine) Place(text, top, inner, h1);
            if (hasCredit) Place(creditText, top - h1 - gap, inner, h2);
        }

        static void Place(TextMeshPro t, float top, float w, float h)
        {
            t.rectTransform.pivot = new Vector2(0.5f, 1f);
            t.rectTransform.sizeDelta = new Vector2(w, Mathf.Max(0.005f, h));
            t.transform.localPosition = new Vector3(0f, top, -0.002f);
        }

        /// 5 cm above the left wrist (hand tracking), else the left controller; false when neither is tracked.
        bool TryAnchor(out Vector3 p)
        {
            if (hand != null && hand.GetJointPose(HandJointId.HandWristRoot, out var wrist)) { p = wrist.position + Vector3.up * lift; return true; }
            if (controllerAnchor != null && controllerAnchor.gameObject.activeInHierarchy && controllerAnchor.localPosition.sqrMagnitude > 1e-6f)
            { p = controllerAnchor.position + Vector3.up * lift; return true; }
            p = default;
            return false;
        }

        void LateUpdate()
        {
            if (Time.unscaledTime >= m_Next) { m_Next = Time.unscaledTime + 0.25f; Refresh(); }
            Tracked = TryAnchor(out var target);
            bool show = HasContent && Tracked && head != null;
            if (content != null && content.activeSelf != show) { content.SetActive(show); m_Placed = false; }
            if (!show) return;
            if (m_LayoutDirty) Layout();
            m_Pos = m_Placed ? Vector3.Lerp(m_Pos, target, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime)) : target;
            m_Placed = true;
            var look = m_Pos - head.position;
            transform.SetPositionAndRotation(m_Pos, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : transform.rotation);
        }
    }
}
