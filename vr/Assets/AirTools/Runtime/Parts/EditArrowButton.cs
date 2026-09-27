using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit-touch: one of the Edit view's arrow knobs as a poke button. A poke (GlassButton.Clicked: a fingertip, or a
    /// controller's poke tip) steps the item once and starts the hold-repeat (EditView.PokeArrow); a finger near it shows
    /// its axis in the readout pill (GlassButton.HoverChanged → EditView.HoverArrow). Press() is the path a poke takes;
    /// tests and the harness call it too.
    public class EditArrowButton : MonoBehaviour
    {
        public GlassButton button;
        [Tooltip("EditViewMath.Arrows index.")]
        public int index;

        void OnEnable()
        {
            if (button == null) return;
            button.Clicked += Press;
            button.HoverChanged += OnHover;
        }

        void OnDisable()
        {
            if (button == null) return;
            button.Clicked -= Press;
            button.HoverChanged -= OnHover;
        }

        public void Press()
        {
            var v = EditView.Current;
            if (v == null) { Log.Warn("EditArrowButton: no Edit view in the scene"); return; }
            v.PokeArrow(index);
        }

        void OnHover(bool on) => EditView.Current?.HoverArrow(index, on);
    }
}
