using AirTools.UI;
using UnityEngine;

namespace AirTools.Structure
{
    /// One button on the Scene window (GlassButton.Clicked → ScenePanel.Do).
    public class ScenePanelButton : MonoBehaviour
    {
        public GlassButton button;
        public ScenePanel panel;
        public ScenePanelAction action;
        public string arg;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press() { if (panel != null) panel.Do(action, arg); }
    }
}
