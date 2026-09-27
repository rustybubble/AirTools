using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    public enum PartsAction { Search, Take, Finish, Remove, Array, Sellers }

    /// Buttons of the crate menu (preset searches), candidate cards (take) and the spec card (finish swatches,
    /// remove). Press() is the same path a poke or ray press takes.
    public class PartsButton : MonoBehaviour
    {
        public GlassButton button;
        public PartsAction action;
        [Tooltip("Search: the query. Finish: filled in by the spec card.")]
        public string text;
        [Tooltip("Take: candidate index. Finish: swatch index.")]
        public int index;
        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            switch (action)
            {
                case PartsAction.Search: AppCommands.FindPart(text); break;
                case PartsAction.Take: AppCommands.SelectCandidate(index); break;
                case PartsAction.Finish: AppCommands.SetFinish(text); break;
                case PartsAction.Array: AppCommands.PlaceArray(null); break;
                case PartsAction.Sellers: AppCommands.ShowSellers("price"); break;
                case PartsAction.Remove: AppCommands.RemoveSelectedPart(); break;   // delete-undo: undoable (was a hard remove)
            }
        }
    }
}
