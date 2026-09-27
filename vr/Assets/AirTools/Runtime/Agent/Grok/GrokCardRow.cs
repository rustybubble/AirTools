using System;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// One row of the G3 card (an installer, a permit item, a rebate, a quote line): an ElevatedSolid backplate, rich
    /// text on the left and up to two link chips on the right (poke or ray pinch). Sizes itself to its text.
    public class GrokCardRow : MonoBehaviour
    {
        public GlassSurface backplate;
        public TextMeshPro text;
        public GlassButton[] links = new GlassButton[0];
        public float linkWidth = 0.075f, linkHeight = 0.026f, pad = 0.008f;

        /// (row, link index) when a link chip is pressed.
        public event Action<GrokCardRow, int> LinkClicked;

        public GrokRow Row { get; private set; }
        public float Height { get; private set; }

        Action[] m_Handlers = new Action[0];

        void OnEnable()
        {
            m_Handlers = new Action[links.Length];
            for (int i = 0; i < links.Length; i++)
            {
                int k = i;
                m_Handlers[i] = () => LinkClicked?.Invoke(this, k);
                if (links[i] != null) links[i].Clicked += m_Handlers[i];
            }
        }

        void OnDisable()
        {
            for (int i = 0; i < links.Length && i < m_Handlers.Length; i++)
                if (links[i] != null) links[i].Clicked -= m_Handlers[i];
        }

        /// Harness / tests: the same path as a pinch on link `i`.
        public bool PressLink(int i) => i >= 0 && i < links.Length && links[i] != null && links[i].gameObject.activeInHierarchy && links[i].Press();

        /// Fill and size the row for `width`; returns its height (metres). Hidden when row is null.
        public float Show(GrokRow row, float width)
        {
            Row = row;
            bool on = row != null;
            if (gameObject.activeSelf != on) gameObject.SetActive(on);
            if (!on) { Height = 0f; return 0f; }
            int n = Mathf.Min(row.Links.Count, links.Length);
            float textW = width - 2f * pad - (n > 0 ? linkWidth + pad : 0f);
            float textH = GrokCardLayout.TextHeight(text, row.Text, textW);
            float linksH = n > 0 ? n * linkHeight + (n - 1) * 0.004f : 0f;
            Height = Mathf.Max(textH, linksH) + 2f * pad;
            float left = -width * 0.5f;
            GrokCardLayout.Place(text, row.Text, new Vector2(left + pad, Height * 0.5f - pad), textW, textH);
            for (int i = 0; i < links.Length; i++)
            {
                bool show = i < n;
                if (links[i] == null) continue;
                if (links[i].gameObject.activeSelf != show) links[i].gameObject.SetActive(show);
                if (!show) continue;
                links[i].SetText(row.Links[i].Label);
                links[i].transform.localPosition = new Vector3(width * 0.5f - pad - linkWidth * 0.5f, Height * 0.5f - pad - linkHeight * 0.5f - i * (linkHeight + 0.004f), 0f);
            }
            if (backplate != null) { backplate.SetSize(new Vector2(width, Height)); backplate.transform.localPosition = new Vector3(0f, 0f, 0.001f); }
            return Height;
        }
    }
}
