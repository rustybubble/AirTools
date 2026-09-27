using System;
using UnityEngine;

namespace AirTools.UI
{
    /// A main-slot window the status line can dock on (declutter M3): FloatingWindow and NotebookPanel.
    public interface IDockTarget
    {
        /// While it is open: the centre of its glass panel's top edge (world, live from the "Panel" GlassSurface, so
        /// runtime-sized cards count) and the panel's rotation.
        bool TryPanelTop(out Vector3 top, out Quaternion rotation);
    }

    /// One window at a time in front of you (UX W0.7): Notebook, Sellers and Checkout share the main slot — opening
    /// one closes the other (Checkout has Back to Sellers). The Scene window and Find parts live in side slots.
    public static class WindowSlot
    {
        static object s_Owner;
        static Action s_Close;

        /// The window in the main slot (null when it's free).
        public static object Current => s_Owner;
        public static int Claims { get; private set; }

        /// `owner` takes the main slot; whatever held it is closed first.
        public static void Claim(object owner, Action close)
        {
            if (owner == null) return;
            if (s_Owner != null && !ReferenceEquals(s_Owner, owner))
            {
                var closePrevious = s_Close;
                s_Owner = null; s_Close = null;
                closePrevious?.Invoke();
            }
            s_Owner = owner;
            s_Close = close;
            Claims++;
        }

        public static void Release(object owner)
        {
            if (ReferenceEquals(s_Owner, owner)) { s_Owner = null; s_Close = null; }
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset() { s_Owner = null; s_Close = null; Claims = 0; }
    }
}
