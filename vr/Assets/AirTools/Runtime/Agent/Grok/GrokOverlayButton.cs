using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    public enum GrokOverlayAction { ToggleSceneLabels }

    /// Hooks a UiBuild button to the Grok overlays (the Scene window's "Labels" toggle: the pre-labelled scan's
    /// labels without asking Grok). Behaviour hangs on GlassButton.Clicked.
    public class GrokOverlayButton : MonoBehaviour
    {
        public GlassButton button;
        public GrokOverlayAction action;

        void OnEnable() { if (button != null) button.Clicked += OnClick; }
        void OnDisable() { if (button != null) button.Clicked -= OnClick; }

        void OnClick()
        {
            if (!Services.TryGet<GrokOverlays>(out var overlays)) return;
            switch (action)
            {
                case GrokOverlayAction.ToggleSceneLabels: overlays.ToggleSceneLabels(); break;
            }
        }
    }
}
