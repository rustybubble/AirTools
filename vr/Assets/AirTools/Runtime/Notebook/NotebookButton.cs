using AirTools.UI;
using UnityEngine;

namespace AirTools.Notes
{
    public enum NotebookAction { Show, Prev, Next, Export, Close, AllSites }   // sitescope: AllSites (appended: the enum is serialized)

    /// A button on the notebook panel.
    public class NotebookButton : MonoBehaviour
    {
        public GlassButton button;
        public NotebookPanel panel;
        public NotebookAction action;
        [Tooltip("Row index for Show.")]
        public int row;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            if (panel == null) return;
            switch (action)
            {
                case NotebookAction.Show: panel.Show(row); break;
                case NotebookAction.Prev: panel.Prev(); break;
                case NotebookAction.Next: panel.Next(); break;
                case NotebookAction.Export: panel.Export(); break;
                case NotebookAction.Close: panel.SetOpen(false); break;
                case NotebookAction.AllSites: panel.ToggleAllSites(); break;   // sitescope
            }
        }
    }
}
