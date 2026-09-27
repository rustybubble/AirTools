using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// catalog: the glass QWERTY keyboard under the catalog window (built by CatalogBuilder with UiBuild): digits,
    /// letters, "-", "\"", Delete, Clear, space and Search, each a GlassButton (poke with a finger, or ray + pinch / the
    /// controller trigger). It hangs just below the window, tilted back toward you like a desk, a little nearer, and shows
    /// while the search field is active. Ours rather than the system keyboard (OVRVirtualKeyboard isn't in this SDK):
    /// the same in passthrough and in the world, with hands or controllers.
    public class CatalogKeyboard : MonoBehaviour
    {
        public GameObject content;
        /// Every key's button (CatalogButton with action Key on each).
        public CatalogButton[] keys = new CatalogButton[0];

        public bool IsOpen => content != null && content.activeSelf;

        public void Show(bool open)
        {
            if (content != null && content.activeSelf != open) content.SetActive(open);
        }

        /// The button for `key` ("a", "back", "enter"…), or null.
        public GlassButton Find(string key)
        {
            foreach (var k in keys)
                if (k != null && k.key == key) return k.button;
            return null;
        }
    }
}
