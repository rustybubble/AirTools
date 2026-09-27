using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AirTools.UI
{
    /// The glass census (glass lane): every renderer inside the app's UI — each window's / heads-up pill's content and every
    /// button's parent — must be glass drawn with its role's shared material, text, the tool ring's Liquid Glass, or a
    /// picture (photos, thumbnails, QR codes). Anything else is a stray background (an opaque quad, a Lit cube, a
    /// per-instance material copy). LiquidGlassSceneTests runs it over the Wire-built Main.unity; AgentHarness.GlassState()
    /// over the live app.
    public static class GlassCensus
    {
        public class Report
        {
            public int Surfaces, Liquid, Marks, Text, Ring, Pictures, Roots;
            /// Candidate roots too broad to be UI (a button parented straight to the rig or the app: they hold cameras or
            /// the scan), named so a builder can give its buttons a holder.
            public readonly List<string> Skipped = new List<string>();
            public readonly Dictionary<GlassRole, int> ByRole = new Dictionary<GlassRole, int>();
            /// Renderers that aren't glass, text, the ring or a picture ("path: material").
            public readonly List<string> Stray = new List<string>();
            /// Glass surfaces not drawn with their role's shared material ("path: has X, wants Y").
            public readonly List<string> WrongMaterial = new List<string>();

            public string Line()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"glass {Surfaces} (liquid {Liquid}, marks {Marks}) · text {Text} · ring {Ring} · pictures {Pictures} · roots {Roots} · roles");
                foreach (var kv in ByRole) sb.Append($" {kv.Key}={kv.Value}");
                sb.Append($" · stray {Stray.Count} · wrong material {WrongMaterial.Count}");
                if (Skipped.Count > 0) sb.Append($" · skipped roots [{string.Join(", ", Skipped)}]");
                return sb.ToString();
            }
        }

        /// Renderers that show content rather than a background: the scans' / parts' photos, notebook and receipt
        /// thumbnails, the Grok card's picture and QR code, Model view's scan pictures.
        public static bool IsPicture(string name) =>
            !string.IsNullOrEmpty(name) && (name.EndsWith("Thumb") || name == "Photo" || name == "Image" || name == "Qr" || name == "Picture");

        /// The shared material a surface must draw with: heads-up → Glass HUD, a world annotation → Glass Label, else its
        /// (effective) tier's.
        public static Material Expected(UiTheme theme, GlassSurface s)
        {
            if (s.hud && theme.materials.hudGlass != null) return theme.materials.hudGlass;
            if (s.overlay && theme.materials.labelGlass != null) return theme.materials.labelGlass;
            return theme.TierMaterial(s.EffectiveTier);
        }

        /// The UI subtrees under `roots`: the parent of every Window / Hud glass panel and of every button, outermost only.
        public static List<Transform> UiRoots(IEnumerable<GameObject> roots) => UiRoots(roots, null);

        static List<Transform> UiRoots(IEnumerable<GameObject> roots, List<string> skipped)
        {
            var found = new List<Transform>();
            foreach (var r in roots)
            {
                if (r == null) continue;
                foreach (var s in r.GetComponentsInChildren<GlassSurface>(true))
                {
                    var role = s.EffectiveRole;
                    if ((role == GlassRole.Window || role == GlassRole.Hud) && s.transform.parent != null) found.Add(s.transform.parent);
                }
                foreach (var b in r.GetComponentsInChildren<GlassButton>(true))
                    if (b.transform.parent != null) found.Add(b.transform.parent);
            }
            // Not UI: a subtree with a camera (the rig) or the scan (the app root) — a button parented straight to them.
            var candidates = new List<Transform>();
            foreach (var t in found)
            {
                if (candidates.Contains(t)) continue;
                if (t.GetComponentInChildren<Camera>(true) != null || t.GetComponentInChildren<AirTools.Scene.SceneRoot>(true) != null)
                {
                    if (skipped != null && !skipped.Contains(t.name)) skipped.Add(t.name);
                    continue;
                }
                candidates.Add(t);
            }
            var outer = new List<Transform>();
            foreach (var t in candidates)
            {
                bool nested = false;
                foreach (var o in candidates)
                    if (o != t && t.IsChildOf(o)) { nested = true; break; }
                if (!nested) outer.Add(t);
            }
            return outer;
        }

        public static Report Take(IEnumerable<GameObject> roots, UiTheme theme)
        {
            var report = new Report();
            var seen = new HashSet<MeshRenderer>();
            var uiRoots = UiRoots(roots, report.Skipped);
            report.Roots = uiRoots.Count;
            foreach (var root in uiRoots)
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!seen.Add(r)) continue;
                var mat = r.sharedMaterial;
                var s = r.GetComponent<GlassSurface>();
                if (s != null)
                {
                    report.Surfaces++;
                    var role = s.EffectiveRole;
                    report.ByRole[role] = report.ByRole.TryGetValue(role, out int n) ? n + 1 : 1;
                    if (role == GlassRole.Mark) report.Marks++;
                    else if (theme.Look(role).IsLiquid) report.Liquid++;
                    var want = Expected(theme, s);
                    if (want != null && mat != want) report.WrongMaterial.Add($"{Path(r.transform)}: has {(mat != null ? mat.name : "none")}, wants {want.name}");
                    continue;
                }
                if (r.name == "Shadow" && r.transform.parent != null && r.transform.parent.GetComponent<GlassSurface>() != null)
                {
                    if (theme.materials.shadow != null && mat != theme.materials.shadow)
                        report.WrongMaterial.Add($"{Path(r.transform)}: has {(mat != null ? mat.name : "none")}, wants {theme.materials.shadow.name}");
                    continue;
                }
                if (r.GetComponent<TMP_Text>() != null) { report.Text++; continue; }
                if (mat != null && mat == theme.materials.liquidGlass) { report.Ring++; continue; }
                if (IsPicture(r.name)) { report.Pictures++; continue; }
                report.Stray.Add($"{Path(r.transform)}: {(mat != null ? $"{mat.name} ({(mat.shader != null ? mat.shader.name : "no shader")})" : "no material")}");
            }
            return report;
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && parts.Count < 5; p = p.parent) parts.Insert(0, p.name);
            return string.Join("/", parts);
        }
    }
}
