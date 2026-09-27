using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    public enum EditViewAction { Move, Save, Place, Cancel, ResetTurn, Step, Shade, Swatch, FitToOpening, MenuEdit, MenuSimilar, MenuDelete, MenuUndo }

    /// edit6dof: a button of the Edit view's panel, its move bar or a part's context menu. Press() is the path a poke or a
    /// ray press takes (GlassButton.Clicked); tests and the harness call it too.
    public class EditViewButton : MonoBehaviour
    {
        public GlassButton button;
        public EditViewAction action;
        [Tooltip("Swatch: EditShades index (0 = Original).")]
        public int index;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            var v = EditView.Current;
            if (v == null) { Log.Warn("EditViewButton: no Edit view in the scene"); return; }
            v.Do(action, index);
        }
    }
}
