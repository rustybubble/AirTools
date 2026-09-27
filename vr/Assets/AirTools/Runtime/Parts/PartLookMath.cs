using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// assetgen: the Size & finish panel's rules (PlacementEditor.Look). Pure: runs in the offline test runner.
    /// - Sizes stay within MinFactor–MaxFactor of the listing on every axis and at least MinMm (the server refuses the
    ///   same, POST /parts/{id}/resize → 422): a window made to measure, not a different product.
    /// - Which parts the server can rebuild at a new size (CanRebuild: its own template or box, tiers llm / proxy) and
    ///   which finishes it renders (ServerRenders: F11 re-textures llm / ai_mesh / proxy parts with a photo).
    /// - The finish chips (FinishChoices: the listing's finishes, then common frame colours) and the tint a finish the
    ///   server doesn't render gets (TintFor: the listed hex, else a named colour).
    public static class PartLookMath
    {
        public const float MinFactor = 0.5f, MaxFactor = 2f, MinMm = 10f;

        /// The size, each axis clamped to MinFactor–MaxFactor of `listed` (and ≥ MinMm). Millimetres.
        public static Vector3 Clamp(Vector3 mm, PartDims listed)
        {
            if (listed == null) return Vector3.Max(mm, Vector3.one * MinMm);
            var l = new Vector3(listed.w, listed.h, listed.d);
            for (int i = 0; i < 3; i++) mm[i] = Mathf.Clamp(mm[i], Mathf.Max(MinMm, MinFactor * l[i]), Mathf.Max(MinMm, MaxFactor * l[i]));
            return mm;
        }

        /// "58½ × 46¾ × 3¼″" / "1487 × 1187 × 83 mm": width × height × depth in the user's unit (windows are sold W × H).
        public static string SizeWords(PartDims d) =>
            d == null ? "" : Units.FormatTriple(d.w * 0.001f, d.h * 0.001f, d.d * 0.001f, UiSettings.UnitSystem);

        /// The server built this part's model from a template or a box: it can build it again at another size.
        public static bool CanRebuild(PartInstance p, string finish)
        {
            if (p == null || p.Spec?.asset == null || p.Source == null || !p.Source.StartsWith("server")) return false;
            string tier = p.Spec.asset.tier;
            return tier == "llm" || tier == "proxy" || (finish != null && ServerRenders(p, finish));
        }

        /// F11 re-textures these tiers (a photo on the model): a finish on them comes back as a model.
        public static bool ServerRenders(PartInstance p, string finish) =>
            finish != null && p?.Spec?.asset != null && (p.Spec.asset.tier == "llm" || p.Spec.asset.tier == "ai_mesh" || p.Spec.asset.tier == "proxy")
            && p.Source != null && p.Source.StartsWith("server");

        static readonly (string name, string hex)[] s_Named =
        {
            ("white", "#F2F2F2"), ("black", "#1F1F1F"), ("bronze", "#5B4636"), ("almond", "#E6DAC3"), ("tan", "#C8B597"), ("clay", "#BBA78A"),
            ("grey", "#8C8F92"), ("gray", "#8C8F92"), ("brown", "#5A3E2B"), ("beige", "#D9CBB0"), ("sand", "#D2C3A5"), ("silver", "#C0C4C8"),
        };

        /// Common window-frame colours for a part that lists no finishes.
        static readonly string[] s_Defaults = { "White", "Black", "Bronze" };

        /// The colour a finish tints the part: the listing's hex for it, else a named colour ("Matte Black" → black), else
        /// none.
        public static Color? TintFor(PartSpec spec, string finish)
        {
            if (string.IsNullOrWhiteSpace(finish)) return null;
            if (spec?.FindFinish(finish) is PartFinish f && Hex(f.hex, out var listed)) return listed;
            string n = finish.Trim().ToLowerInvariant();
            foreach (var (name, hex) in s_Named)
                if (n.Contains(name) && Hex(hex, out var c)) return c;
            return null;
        }

        /// "#RRGGBB" (or without the #) as a colour; managed (the offline tests run it).
        public static bool Hex(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;
            c = new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
            return true;
        }

        /// "1487x1187x83|bronze": a model of this size (whole mm) and finish (null: as listed).
        public static string LookKey(PartDims d, string finish) =>
            d == null ? "" : $"{Mathf.RoundToInt(d.w)}x{Mathf.RoundToInt(d.h)}x{Mathf.RoundToInt(d.d)}|{(finish ?? "").Trim().ToLowerInvariant()}";

        /// Within half a millimetre on every axis.
        public static bool SameDims(PartDims a, PartDims b) =>
            a != null && b != null && Mathf.Abs(a.w - b.w) < 0.5f && Mathf.Abs(a.h - b.h) < 0.5f && Mathf.Abs(a.d - b.d) < 0.5f;

        /// The part's own finish (what it looks like as listed): choosing it means "as listed".
        public static bool IsListedFinish(PartSpec spec, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string own = spec?.finish;
            if (string.IsNullOrWhiteSpace(own)) return false;
            string a = own.Trim().ToLowerInvariant(), b = name.Trim().ToLowerInvariant();
            return a == b || a.Contains(b) || b.Contains(a);
        }

        /// Up to 3 chips: the listing's finishes (its own first when listed), then common frame colours it doesn't have.
        public static void FinishChoices(PartSpec spec, List<string> into)
        {
            into.Clear();
            void Add(string n)
            {
                if (into.Count >= 3 || string.IsNullOrWhiteSpace(n)) return;
                foreach (var x in into) if (string.Equals(x, n.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                into.Add(Copy.Cap(n.Trim()));   // "desert sand" → "Desert sand" (a chip's label)
            }
            if (!string.IsNullOrWhiteSpace(spec?.finish)) Add(spec.finish);
            if (spec?.finishes != null) foreach (var f in spec.finishes) if (f != null) Add(f.name);
            foreach (var d in s_Defaults)
            {
                bool listed = false;
                foreach (var x in into) if (x.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) { listed = true; break; }
                if (!listed) Add(d);
            }
        }
    }
}
