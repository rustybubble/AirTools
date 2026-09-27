using AirTools.UI;
using UnityEngine;

namespace AirTools.Scene
{
    public enum ModelWheelAction { Card, WalkIn, Recentre, Exit }

    /// One control on Model view's wheel (GlassButton.Clicked → ModelWheel.Press): a card slot (pressed by the wheel's
    /// own pinch / tap, or the harness) or a chip under the wheel (Walk in, Recentre, Exit; poke or ray).
    public class ModelWheelButton : MonoBehaviour
    {
        public GlassButton button;
        public ModelWheel wheel;
        public ModelWheelAction action;
        [Tooltip("Card: its slot on the wheel (0 = the leftmost; the middle one is under the lens).")]
        public int slot;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press() { if (wheel != null) wheel.Press(action, slot); }
    }
}
