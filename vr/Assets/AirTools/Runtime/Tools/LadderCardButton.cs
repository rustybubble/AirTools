using AirTools.UI;
using UnityEngine;

namespace AirTools.Tools
{
    public enum LadderCardAction { FindLadder, ToggleFallEdges, Close }

    /// A button on the ladder card.
    public class LadderCardButton : MonoBehaviour
    {
        public GlassButton button;
        public LadderCard card;
        public LadderCardAction action;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press() { if (card != null) card.Press(action); }
    }
}
