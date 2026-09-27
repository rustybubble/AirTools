using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Scene
{
    /// The objects an owner hid when their site was left (sitescope). Only objects that were showing are hidden and
    /// remembered, so an undone item (already inactive, kept for redo) stays hidden when its site comes back.
    /// Inactive objects draw nothing and their colliders are off: the tools can't snap to another site's items.
    public sealed class ParkedObjects
    {
        readonly List<GameObject> m_Hidden = new List<GameObject>();

        public IReadOnlyList<GameObject> Objects => m_Hidden;
        public int Count => m_Hidden.Count;

        public void Hide(GameObject go)
        {
            if (go == null || !go.activeSelf) return;
            go.SetActive(false);
            m_Hidden.Add(go);
        }

        public void Hide(Component c) { if (c != null) Hide(c.gameObject); }

        /// Everything hidden here shows again (objects destroyed since are skipped).
        public void ShowAll()
        {
            foreach (var go in m_Hidden) if (go != null) go.SetActive(true);
            m_Hidden.Clear();
        }

        /// Forget without showing (the owner destroys its items itself).
        public void Clear() => m_Hidden.Clear();

        /// Destroy every hidden object (Demo reset of a parked site).
        public void DestroyAll()
        {
            foreach (var go in m_Hidden) Destroy(go);
            m_Hidden.Clear();
        }

        public static void Destroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
