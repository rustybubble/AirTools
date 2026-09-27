using System;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// A pinchable world label for the Grok overlays: a clone of the chip template (a UiBuild GlassButton with poke and an
    /// always-on ray target, built by GrokOverlaysBuilder) anchored at a point in its parent's space (the scene content).
    /// It stands upright facing the viewer, keeps a constant angular size (built for 1 m, so a pin on a roof 20 m away
    /// is as easy to pinch as one on the counter) and fits its glass pill to its text. Hands: ray + pinch far away, poke
    /// up close; Clicked comes from GlassButton.Clicked.
    public class WorldChip : MonoBehaviour
    {
        public GlassButton button;
        [Tooltip("World size per metre of viewing distance (1 = as built, for a 1 m read distance).")]
        public float angularScale = 1f;
        [Tooltip("Closer than this, stop shrinking (m).")]
        public float minDistance = 0.5f;
        [Tooltip("Lift above the anchor, in chip heights (it sits over the pin / point it names).")]
        public float liftHeights = 0.9f;

        public Vector3 AnchorLocal { get; private set; }
        public string Text => button != null ? button.Text : "";
        public event Action Clicked;

        Vector2 m_Size = new Vector2(0.09f, 0.03f);
        Transform m_Interaction;
        bool m_Hooked;

        /// Clone `template` under `parent` at `anchorLocal` (parent space) showing `text`.
        public static WorldChip Create(GlassButton template, Transform parent, Vector3 anchorLocal, string text, string name)
        {
            if (template == null || parent == null) return null;
            var go = Instantiate(template.gameObject, parent, false);
            go.name = name;
            go.SetActive(true);
            var chip = go.GetComponent<WorldChip>();
            if (chip == null) chip = go.AddComponent<WorldChip>();
            chip.button = go.GetComponent<GlassButton>();
            chip.m_Interaction = chip.button != null && chip.button.poke != null ? chip.button.poke.transform : go.transform.Find("Interaction");
            chip.Hook();
            chip.AnchorLocal = anchorLocal;
            chip.SetText(text);
            chip.Place();
            return chip;
        }

        void OnEnable() => Hook();

        void OnDisable()
        {
            if (m_Hooked && button != null) button.Clicked -= OnClicked;
            m_Hooked = false;
        }

        void Hook()
        {
            if (m_Hooked || button == null) return;
            button.Clicked += OnClicked;
            m_Hooked = true;
        }

        void OnClicked() => Clicked?.Invoke();

        public void SetAnchor(Vector3 anchorLocal)
        {
            AnchorLocal = anchorLocal;
            Place();
        }

        /// New text; the pill (and its poke / ray target) resizes to fit it. Rich text is allowed (colour tags only).
        public void SetText(string text)
        {
            if (button == null) return;
            button.SetText(text ?? "");
            var label = button.label;
            if (label == null) return;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = false;   // glass: the pill grows to the text instead (UiBuild.FitLabel auto-sizes fixed buttons)
            var pref = label.GetPreferredValues(text ?? "");
            float w = Mathf.Clamp(pref.x + 0.024f, 0.05f, 0.34f);
            float h = Mathf.Max(0.03f, pref.y + 0.012f);
            m_Size = new Vector2(w, h);
            label.rectTransform.sizeDelta = new Vector2(w - 0.006f, h);
            if (button.surface != null) button.surface.SetSize(m_Size);
            if (m_Interaction != null) m_Interaction.localScale = new Vector3(w, h, Mathf.Min(w, h));
            if (button.indicator != null) button.indicator.transform.localPosition = new Vector3(0f, -h * 0.5f + 0.0035f, -0.001f);
        }

        void LateUpdate() => Place();

        void Place()
        {
            var parent = transform.parent;
            if (parent == null) return;
            var anchor = parent.TransformPoint(AnchorLocal);
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : anchor + Vector3.back * 2f;
            var toCam = eye - anchor;
            float dist = Mathf.Max(minDistance, toCam.magnitude);
            float worldScale = dist * angularScale;
            float parentScale = Mathf.Max(parent.lossyScale.x, 1e-5f);
            transform.localScale = Vector3.one * (worldScale / parentScale);
            var dir = toCam.sqrMagnitude > 1e-8f ? toCam.normalized : Vector3.back;
            // Over the anchor, a little toward the viewer (so the surface it names doesn't cut it).
            var pos = anchor + Vector3.up * (m_Size.y * worldScale * liftHeights) + dir * (0.02f * worldScale);
            var look = pos - eye;
            if (look.sqrMagnitude < 1e-8f) look = Vector3.forward;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look.normalized, Mathf.Abs(look.normalized.y) < 0.97f ? Vector3.up : (cam != null ? cam.transform.up : Vector3.up)));
        }
    }
}
