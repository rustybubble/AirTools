using System.Collections.Generic;
using AirTools.Core;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// Fit outline around a part's true-size box (12 edges, green / amber / red) and a callout with W×D×H and the
    /// fit headline. Edge thickness grows with viewing distance so it stays visible on a facade.
    /// The callout is a label in WorldLabels' one pool of 12 (declutter S10, §5): P4 for a placed part (newest placed
    /// first), P1 for the part in hand (its fit), and an array shows one group label ("8 × hanger · all fit ✓") on its
    /// first member. Over the pool a callout drops to its outline.
    public class PartOutline : MonoBehaviour, IWorldLabelSource
    {
        public static readonly Color Green = new Color(0.2f, 0.95f, 0.4f);
        public static readonly Color Amber = new Color(1f, 0.72f, 0.1f);
        public static readonly Color Red = new Color(1f, 0.22f, 0.18f);
        public static readonly Color Neutral = new Color(0.85f, 0.9f, 1f);

        readonly Transform[] m_Edges = new Transform[12];
        Bounds m_Box;
        MeasureLabel m_Label;
        MeasureStyle m_Style;
        MaterialPropertyBlock m_Block;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public Color CurrentColor { get; private set; } = Neutral;
        /// What the callout says: the part's own W×D×H and fit, or its array's group label.
        public string CalloutText => m_Label != null ? m_Label.Text : "";
        /// The array's group label while this part carries it (null otherwise).
        public string GroupText => m_GroupText;

        PartInstance m_Part;
        string m_Callout = "";
        FitStatus m_Status;
        string m_GroupText;
        bool m_Registered, m_Held, m_Placed, m_ClaimedLive, m_ClaimedMember;
        float m_Newest;

        public static PartOutline Create(Transform part, Bounds localBox, MeasureStyle style)
        {
            var go = new GameObject("Outline");
            go.transform.SetParent(part, false);
            var o = go.AddComponent<PartOutline>();
            o.m_Part = part.GetComponent<PartInstance>();
            o.m_Newest = Time.unscaledTime;
            o.m_Box = localBox;
            o.m_Style = style ?? new MeasureStyle();
            o.m_Block = new MaterialPropertyBlock();
            for (int i = 0; i < 12; i++)
            {
                var e = new GameObject($"Edge{i}");
                e.transform.SetParent(go.transform, false);
                e.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                var r = e.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (o.m_Style.markerMaterial != null) r.sharedMaterial = o.m_Style.markerMaterial;
                o.m_Edges[i] = e.transform;
            }
            o.m_Label = MeasureLabel.Create(go.transform, o.m_Style, "Callout");
            o.m_Label.Priority = 3;
            o.Layout(0.004f);
            o.SetColor(Neutral);
            return o;
        }

        /// assetgen: the part's true-size box changed (resized in the Size & finish panel).
        public void SetBox(Bounds localBox)
        {
            m_Box = localBox;
            Layout(0.004f);
        }

        public static Color ColorFor(FitStatus s) => s switch
        {
            FitStatus.Green => Green,
            FitStatus.Amber => Amber,
            FitStatus.Red => Red,
            _ => Neutral,
        };

        public void Show(FitStatus status, string callout)
        {
            SetColor(ColorFor(status));
            string hex = ColorUtility.ToHtmlStringRGB(ColorFor(status));
            string text = string.IsNullOrEmpty(callout) ? "" : callout.Replace("{c}", hex);
            // The pool hears when the fit changes (an array's group label counts its members' fits) or the callout
            // comes or goes; not on every re-evaluation with the same result.
            bool changed = !m_Registered || status != m_Status || string.IsNullOrEmpty(text) != string.IsNullOrEmpty(m_Callout);
            m_Status = status;
            m_Callout = text;
            m_Label.Set(m_GroupText ?? m_Callout, 1f);
            if (!changed) return;
            m_Registered = true;
            WorldLabels.Changed(this);
        }

        void OnEnable() { if (m_Registered) WorldLabels.Changed(this); }   // back from an undo
        void OnDisable() => WorldLabels.Remove(this);

        // ---------------- the label pool (declutter S10) ----------------

        static bool Member(PartInstance p) => p != null && p.gameObject.activeInHierarchy;

        /// This part's placed array (null when it's alone), how many of its members are in the scene, and whether this
        /// part carries the group label (the reference, else the first clone still there).
        PartTool.ArrayGroup Group(out int members, out bool leader)
        {
            members = 0;
            leader = false;
            if (m_Part == null || !Services.TryGet<PartTool>(out var tool)) return null;
            var g = tool.ArrayOf(m_Part);
            if (g == null) return null;
            PartInstance first = null;
            if (Member(g.Reference)) { members++; first = g.Reference; }
            foreach (var c in g.Clones) if (Member(c)) { members++; if (first == null) first = c; }
            leader = first == m_Part;
            return g;
        }

        /// "8 × hanger · all fit ✓" / "8 × hanger · 2 don't fit ✗" / "8 × hanger · 3 need a look" (words and a glyph,
        /// never colour alone).
        string GroupLabel(PartTool.ArrayGroup g, int members)
        {
            int green = 0, amber = 0, red = 0;
            void Count(PartInstance p)
            {
                if (!Member(p) || p.Fit == null) return;
                if (p.Fit.Status == FitStatus.Green) green++;
                else if (p.Fit.Status == FitStatus.Amber) amber++;
                else if (p.Fit.Status == FitStatus.Red) red++;
            }
            Count(g.Reference);
            foreach (var c in g.Clones) Count(c);
            string noun = m_Part != null && m_Part.Spec != null ? Copy.Noun(m_Part.Spec, m_Part.SearchQuery) : "part";
            string head = $"{members} × {noun}";
            if (green + amber + red == 0) return head;
            var status = red > 0 ? FitStatus.Red : amber > 0 ? FitStatus.Amber : FitStatus.Green;
            string words = red > 0 ? $"{red} don't fit ✗" : amber > 0 ? $"{amber} need a look" : green == members ? "all fit ✓" : $"{green} of {members} fit ✓";
            return $"{head} · <color=#{ColorUtility.ToHtmlStringRGB(ColorFor(status))}>{words}</color>";
        }

        /// One claim: nothing while hidden or blank, nothing for an array member that doesn't carry the group label,
        /// else one label (P1 in hand, P4 placed).
        public void ClaimLabels(List<LabelClaim> claims)
        {
            m_ClaimedMember = false;
            m_ClaimedLive = m_Label != null && gameObject.activeInHierarchy && !string.IsNullOrEmpty(m_Callout);
            if (!m_ClaimedLive) { claims.Add(new LabelClaim(LabelClass.Parts, 0, 0, m_Newest)); return; }
            var g = Group(out int members, out bool leader);
            m_ClaimedMember = g != null && members > 1 && !leader;
            bool held = m_Part != null && m_Part.Held;
            claims.Add(new LabelClaim(held ? LabelClass.Focus : LabelClass.Parts, 0, m_ClaimedMember ? 0 : 1, m_Newest));
        }

        public void ApplyLabels(LabelGrant[] grants, int first)
        {
            if (m_Label == null) return;
            string group = null;
            if (m_ClaimedLive && !m_ClaimedMember)
            {
                var g = Group(out int members, out bool leader);
                if (g != null && members > 1 && leader) group = GroupLabel(g, members);
            }
            if (group != m_GroupText)
            {
                m_GroupText = group;
                m_Label.Set(m_GroupText ?? m_Callout, 1f);
            }
            if (!m_ClaimedLive) return;   // hidden or blank: leave the callout as it is
            bool show = !m_ClaimedMember && grants[first].Items > 0;
            if (m_Label.gameObject.activeSelf != show) m_Label.gameObject.SetActive(show);
        }

        void SetColor(Color c)
        {
            CurrentColor = c;
            foreach (var e in m_Edges)
            {
                if (e == null) continue;
                var r = e.GetComponent<MeshRenderer>();
                r.GetPropertyBlock(m_Block);
                m_Block.SetColor(s_BaseColor, c);
                r.SetPropertyBlock(m_Block);
            }
        }

        void Layout(float thickness)
        {
            var c = m_Box.center; var e = m_Box.extents;
            int i = 0;
            // 4 edges along each axis.
            for (int axis = 0; axis < 3; axis++)
            {
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                for (int k = 0; k < 4; k++)
                {
                    var p = c;
                    p[a1] += (k & 1) == 0 ? -e[a1] : e[a1];
                    p[a2] += (k & 2) == 0 ? -e[a2] : e[a2];
                    var s = Vector3.one * thickness;
                    s[axis] = m_Box.size[axis] + thickness;
                    m_Edges[i].localPosition = p;
                    m_Edges[i].localScale = s;
                    i++;
                }
            }
        }

        void LateUpdate()
        {
            // Taken in hand (P1) or placed (P4, newest first): the pool hears once, not every frame.
            if (m_Part != null && (m_Part.Held != m_Held || m_Part.Placed != m_Placed))
            {
                m_Held = m_Part.Held;
                m_Placed = m_Part.Placed;
                m_Newest = Time.unscaledTime;
                if (m_Registered) WorldLabels.Changed(this);
            }
            var cam = Camera.main;
            // Edges can already be gone while the part is torn down (scene unload / session exit).
            if (cam == null || m_Edges[0] == null || m_Label == null) return;
            var centre = transform.TransformPoint(m_Box.center);
            float dist = Vector3.Distance(cam.transform.position, centre);
            float scale = Mathf.Max(transform.lossyScale.x, 1e-4f);
            Layout(Mathf.Max(0.003f, dist * 0.0015f) / scale);
            // Callout above the box, pulled toward the viewer.
            var toCam = (cam.transform.position - centre).normalized;
            var ex = m_Box.extents * scale;
            float halfHeight = Mathf.Abs(transform.right.y) * ex.x + Mathf.Abs(transform.up.y) * ex.y + Mathf.Abs(transform.forward.y) * ex.z;
            m_Label.SetAnchorWorld(centre + Vector3.up * (halfHeight + 0.04f + dist * 0.02f) + toCam * 0.08f);
        }
    }
}
