using UnityEngine;

namespace AirTools.Notes
{
    /// One notebook row: evidence thumbnail, two lines of text, and a SHOW poke button (NotebookButton).
    public class NotebookRowView : MonoBehaviour
    {
        public MeshRenderer thumbnail;
        public TMPro.TextMeshPro titleText;
        public TMPro.TextMeshPro detailText;
        public GameObject showButton;

        MaterialPropertyBlock m_Block;
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public NotebookEntry Entry { get; private set; }

        public void Show(NotebookEntry e, Texture2D thumb)
        {
            Entry = e;
            gameObject.SetActive(e != null);
            if (e == null) return;
            if (titleText != null) titleText.text = NotebookPage.RowTitle(e);
            if (detailText != null) detailText.text = NotebookPage.RowDetail(e);
            if (thumbnail != null)
            {
                m_Block ??= new MaterialPropertyBlock();
                thumbnail.GetPropertyBlock(m_Block);
                if (thumb != null) { m_Block.SetTexture(s_BaseMap, thumb); m_Block.SetColor(s_BaseColor, Color.white); }
                else m_Block.SetColor(s_BaseColor, new Color(0.25f, 0.25f, 0.28f));
                thumbnail.SetPropertyBlock(m_Block);
            }
            if (showButton != null) showButton.SetActive(e.Tool == "measure" || e.Tool == "level" || e.Tool == "ladder");   // P6/P7: ladder
        }
    }
}
