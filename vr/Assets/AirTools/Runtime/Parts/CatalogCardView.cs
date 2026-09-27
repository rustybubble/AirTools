using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// catalog: one card of the catalog grid — a glass button (tap: take the part) with the product photo, its name (the
    /// button's label, two lines), "$459 · 23⅞″ W" under it and a "3D" badge when its model is ready. The part in hand
    /// shows selected (ink).
    public class CatalogCardView : MonoBehaviour
    {
        public GlassButton button;
        public MeshRenderer photo;
        public TextMeshPro detail;
        public GameObject badge;

        public CatalogHit Hit { get; private set; }
        public string ItemId => Hit?.Id;
        public bool HasImage { get; private set; }

        MaterialPropertyBlock m_Block;
        string m_Detail;
        bool m_Selected;
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        /// Show `hit` (null hides the card). The photo is cleared until SetImage.
        public void Show(CatalogHit hit, UnitSystem units)
        {
            bool same = hit != null && Hit != null && hit.Id == Hit.Id;
            Hit = hit;
            if (gameObject.activeSelf != (hit != null)) gameObject.SetActive(hit != null);
            if (hit == null) return;
            if (button != null) button.SetText(SpecCard.Wrap(Copy.Clean(CatalogText.Name(hit.Summary.name)), 22, 2));
            Relabel(units);
            if (badge != null && badge.activeSelf != hit.ModelReady) badge.SetActive(hit.ModelReady);
            if (!same) SetImage(null);
        }

        /// The units changed (D2): the width line again.
        public void Relabel(UnitSystem units)
        {
            if (Hit == null || detail == null) return;
            string d = CatalogText.Detail(Hit.Summary, units);
            if (d == m_Detail) return;
            m_Detail = d;
            detail.text = UiText.Tabular(d);
        }

        public void SetImage(Texture2D tex)
        {
            HasImage = tex != null;
            if (photo == null) return;
            m_Block ??= new MaterialPropertyBlock();
            photo.GetPropertyBlock(m_Block);
            if (tex != null) { m_Block.SetTexture(s_BaseMap, tex); m_Block.SetColor(s_BaseColor, Color.white); }
            else m_Block.SetColor(s_BaseColor, new Color(0.22f, 0.23f, 0.27f));
            photo.SetPropertyBlock(m_Block);
        }

        /// In hand: ink (the button's selected state), the second line dark on it.
        public void SetSelected(bool on)
        {
            if (button != null) button.SetSelected(on);
            if (on == m_Selected && detail != null && detail.color != default) return;
            m_Selected = on;
            if (detail != null) detail.color = on ? UiTheme.Current.colors.onInk : UiTheme.Current.colors.textSecondary;
        }
    }
}
