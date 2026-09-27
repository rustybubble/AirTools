using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: what the Edit view needs from a part (docs/edit-view.md).
    /// - **Shade.** A swatch over the texture: every material but the glass takes its own colour blended toward the
    ///   swatch (EditShades.Tint; the base colour multiplies the texture, so the grain stays). The part's own material
    ///   copies are changed (OwnMaterials), not property blocks, so it stays in the SRP batcher. It is the part's finish
    ///   (FinishColor / FinishName, like assetgen's TintExceptGlass), so the spec card, the notebook row and a saved
    ///   placement carry it. Original takes it off.
    /// - **Draw order.** While the world is dimmed the part draws after the dim (its materials' render queue raised), so it
    ///   alone stays bright; put back afterwards.
    public partial class PartInstance
    {
        /// The shade on it now (default: none).
        public PartShade Shade { get; private set; }

        readonly Dictionary<(Material, int), Color> m_Unshaded = new Dictionary<(Material, int), Color>();
        readonly Dictionary<Material, int> m_Queues = new Dictionary<Material, int>();

        /// Put `shade` on (Original: off). The glass keeps its own colour.
        public void SetShade(PartShade shade)
        {
            // Back to the model's own colours first, so strengths don't stack.
            foreach (var kv in m_Unshaded)
                if (kv.Key.Item1 != null) kv.Key.Item1.SetColor(kv.Key.Item2, kv.Value);
            m_Unshaded.Clear();
            Shade = shade;
            if (shade.IsOriginal)
            {
                if (FinishLabel != null && FinishLabel.EndsWith(" shade", StringComparison.Ordinal)) FinishLabel = null;
                FinishColor = null;
                FinishName = !string.IsNullOrEmpty(Spec?.finish) ? Spec.finish : null;
                return;
            }
            foreach (var r in Model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !m_Materials.Contains(m)) continue;
                    foreach (var id in s_ColorProps)
                    {
                        if (!m.HasProperty(id)) continue;
                        var own = m.GetColor(id);
                        m_Unshaded[(m, id)] = own;
                        m.SetColor(id, EditShades.Tint(own, shade.Color, shade.Strength));
                    }
                }
            }
            FinishColor = shade.Color;
            FinishName = shade.Name;
            FinishLabel = EditShades.Label(shade);
        }

        /// Draw after the world's dim (true) or as before (false).
        public void DrawOverDim(bool on, int queue = 3060)
        {
            if (!on)
            {
                foreach (var kv in m_Queues) if (kv.Key != null) kv.Key.renderQueue = kv.Value;
                m_Queues.Clear();
                return;
            }
            foreach (var m in m_Materials)
            {
                if (m == null) continue;
                if (!m_Queues.ContainsKey(m)) m_Queues[m] = m.renderQueue;
                // Transparent materials (glass) keep their order among themselves, just after the opaque ones.
                m.renderQueue = m_Queues[m] > 2500 ? queue + 5 : queue;
            }
        }

        /// Its materials are drawing after the dim.
        public bool OverDim => m_Queues.Count > 0;
    }
}
