using System;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Structure
{
    /// Settings ▸ "Take out": one chip per removable scene part (the file's label, "Dishwasher"), selected (ink) while the
    /// part is out. A tap takes it out or puts it back (AppCommands.ToggleComponent, one undoable edit each), the same
    /// path as the remove_component action. With no parts in the scene the row says so. Built by
    /// Editor/ScenePartsBuilder (AirTools ▸ Wire Main Scene) under Settings' panel; declutter §3.2: layers and scene
    /// settings live in Settings.
    public class ScenePartsRow : MonoBehaviour
    {
        public TextMeshPro caption, empty;
        public GlassButton[] chips = Array.Empty<GlassButton>();

        /// The component id on each chip ("" = hidden).
        public string[] ChipIds { get; private set; } = Array.Empty<string>();

        public const string Caption = "Take out";
        public const string Empty = "Nothing to take out in this scene";
        /// Chip words are the part's name, clipped to fit a chip.
        public const int MaxChars = 12;

        Action[] m_Handlers;
        int m_Version = -1;
        SceneParts m_Parts;

        void OnEnable()
        {
            m_Handlers = new Action[chips.Length];
            for (int i = 0; i < chips.Length; i++)
            {
                int k = i;
                m_Handlers[i] = () => Press(k);
                if (chips[i] != null) chips[i].Clicked += m_Handlers[i];
            }
            Refresh(force: true);
        }

        void OnDisable()
        {
            if (m_Handlers == null) return;
            for (int i = 0; i < chips.Length && i < m_Handlers.Length; i++)
                if (chips[i] != null) chips[i].Clicked -= m_Handlers[i];
        }

        void Update() => Refresh();

        /// Same path as a poke / ray pinch on chip i: take its part out, or put it back.
        public bool Press(int i)
        {
            if (i < 0 || i >= ChipIds.Length || string.IsNullOrEmpty(ChipIds[i])) return false;
            bool ok = AppCommands.ToggleComponent(ChipIds[i]);
            Refresh(force: true);
            return ok;
        }

        /// Redraw when the scene's parts change (a load, a removal, a restore); cheap to call every frame.
        public void Refresh(bool force = false)
        {
            var parts = Services.Get<SceneParts>();
            int version = parts != null ? parts.Version : -1;
            if (!force && parts == m_Parts && version == m_Version) return;
            m_Parts = parts;
            m_Version = version;
            if (ChipIds.Length != chips.Length) ChipIds = new string[chips.Length];
            var list = parts != null && parts.HasParts ? parts.Removable : null;
            int n = list?.Count ?? 0;
            if (n > chips.Length) Log.Warn($"Settings ▸ Take out: {n} removable parts, {chips.Length} chips; the first {chips.Length} show");
            for (int i = 0; i < chips.Length; i++)
            {
                var b = chips[i];
                if (b == null) continue;
                bool on = i < n;
                ChipIds[i] = on ? list[i].id : "";
                if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
                if (!on) continue;
                string text = ChipText(list[i]);
                if (b.Text != text) b.SetText(text);
                b.SetSelected(parts.IsRemoved(list[i].id));
            }
            if (caption != null && caption.gameObject.activeSelf != (n > 0)) caption.gameObject.SetActive(n > 0);
            if (empty != null && empty.gameObject.activeSelf != (n == 0)) empty.gameObject.SetActive(n == 0);
        }

        /// "Dishwasher", "Sink cabinet" (the file's label, capitalised, clipped to a chip).
        public static string ChipText(ScenePartComponent c) => Copy.Clip(c?.DisplayName ?? "Part", MaxChars);
    }
}
