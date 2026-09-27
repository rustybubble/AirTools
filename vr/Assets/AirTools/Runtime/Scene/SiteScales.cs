using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AirTools.Scene
{
    /// scalemodels: where the loaded scan's scale came from. The Grok context sends it as `scale_source`.
    public enum ScaleSource { None, SiteDefault, User }

    /// One row of the per-site default scale table (SceneStreamer.siteScales; the scene builder writes
    /// SiteScales.Defaults() there).
    [Serializable]
    public struct SiteScale
    {
        [Tooltip("The site id, as GET /scenes names it.")]
        public string site;
        [Tooltip("Scene metres per package unit this site loads with while nobody has set its scale on this headset.")]
        public float scale;
        [Tooltip("Where the number comes from (for the next person).")]
        public string why;

        public SiteScale(string site, float scale, string why = null) { this.site = site; this.scale = scale; this.why = why; }
    }

    /// scalemodels. The user, 2026-09-26: "set the proper scale (the 1.63x estimate was correct, begin with that)".
    /// Some scans load too small. The kitchen's altitude scale is about 1.6× short: its dishwasher opening reads
    /// 437 × 591 × 445 mm raw, about 712 × 963 × 725 mm at ×1.63.
    /// - **Load.** A site in the table loads at its default scale, unless this headset has a scale that a person set for
    ///   that site, frame and revision (a tape + Set scale, saved by SceneStreamer).
    /// - **Stale saves.** A scale saved before the site had a default doesn't count: a test tape from an older build
    ///   mustn't hide the user's number. Saves from now on carry a stamp (StampKey).
    /// - **Reset.** Settings ▸ Reset and Demo reset go back to the default, not to ×1.
    /// Pure (EditMode-tested); SceneStreamer applies it and tracks the source.
    public static class SiteScales
    {
        /// The kitchen (site `kitchen`, r1).
        /// - **Why 1.63.** The backend README estimates ≈1.47× (the dishwasher at 414 mm); the user decided on ×1.63
        ///   (2026-09-26).
        /// - **Check.** The dw1 gap then reads ≈ 712 × 963 × 725 mm (AgentHarness.ScaleCheck).
        public const float Kitchen = 1.63f;

        /// Bump when a default changes, so that scales saved under the old table yield to the new default.
        public const int Version = 1;

        /// The table the scene builder writes into SceneStreamer.siteScales (and the field's own default).
        public static SiteScale[] Defaults() => new[]
        {
            new SiteScale("kitchen", Kitchen, "the user's call (2026-09-26): the altitude scale reads ~1.6× short; dw1 ≈ 712 × 963 × 725 mm"),
        };

        /// The stamp saved next to a person's scale (the table Version it was saved under).
        public static string StampKey(string key) => key + ".by";

        /// A usable factor (within ScaleCalibration's range), else 1.
        public static float Valid(float k) =>
            float.IsNaN(k) || float.IsInfinity(k) || k < AirTools.Structure.ScaleCalibration.MinFactor || k > AirTools.Structure.ScaleCalibration.MaxFactor ? 1f : k;

        /// The site's default scale; 1 when it has none (or an unusable one).
        public static float DefaultFor(IReadOnlyList<SiteScale> table, string site)
        {
            if (table == null || string.IsNullOrEmpty(site)) return 1f;
            for (int i = 0; i < table.Count; i++)
                if (table[i].site == site) return Valid(table[i].scale);
            return 1f;
        }

        public static bool HasDefault(IReadOnlyList<SiteScale> table, string site) => IsSet(DefaultFor(table, site));

        /// The scale isn't the package's own.
        public static bool IsSet(float k) => Mathf.Abs(k - 1f) > 1e-6f;

        public struct Choice
        {
            public float Scale;
            public ScaleSource Source;
            /// The saved key it came from (User only).
            public string Key;
            public override string ToString() => $"×{Scale.ToString("0.####", CultureInfo.InvariantCulture)} ({Wire(Source)}{(Key != null ? ", " + Key : "")})";
        }

        /// What a site loads with. `keys`: its saved-scale keys, best first (SceneStreamer.CalibrationKeys). `saved`: the
        /// scale saved under a key, or null. `stamp`: the table Version saved with it (0 = none).
        /// - A person's scale wins. For a site with a default it must carry this Version's stamp.
        /// - A saved ×1 (the old Reset) means "not set".
        /// - Otherwise the site's default, else ×1.
        public static Choice Choose(IReadOnlyList<string> keys, Func<string, float?> saved, Func<string, int> stamp, float siteDefault)
        {
            siteDefault = Valid(siteDefault);
            bool hasDefault = IsSet(siteDefault);
            if (keys != null && saved != null)
                foreach (var key in keys)
                {
                    var v = saved(key);
                    if (!v.HasValue) continue;
                    if (hasDefault && (stamp == null || stamp(key) < Version)) continue;   // saved before this default
                    float k = v.Value;
                    if (IsSet(k) && k > 0f && !float.IsNaN(k) && !float.IsInfinity(k)) return new Choice { Scale = k, Source = ScaleSource.User, Key = key };
                    break;
                }
            return ResetTo(siteDefault);
        }

        /// What Reset goes back to: the site's default, else the package's own scale.
        public static Choice ResetTo(float siteDefault)
        {
            siteDefault = Valid(siteDefault);
            return IsSet(siteDefault) ? new Choice { Scale = siteDefault, Source = ScaleSource.SiteDefault }
                                      : new Choice { Scale = 1f, Source = ScaleSource.None };
        }

        /// The source a scale actually has: a factor of 1 is never a site default, and a set factor is never "none" (a
        /// scale carried from somewhere this code didn't see counts as the user's).
        public static ScaleSource Consistent(float k, ScaleSource source) =>
            !IsSet(k) ? (source == ScaleSource.User ? ScaleSource.User : ScaleSource.None)
                      : source == ScaleSource.None ? ScaleSource.User : source;

        /// context.scale_source: "site_default" | "user" | "none".
        public static string Wire(ScaleSource s) => s switch
        {
            ScaleSource.SiteDefault => "site_default",
            ScaleSource.User => "user",
            _ => "none",
        };

        public static string Factor(float k) => k.ToString("0.00", CultureInfo.InvariantCulture);

        /// Settings' status line for a scan at its default: "Scale ×1.63 · set for this scan".
        public static string DefaultLine(float k) => $"Scale ×{Factor(k)} · set for this scan";

        static float s_WordK = float.NaN;
        static string s_Word;

        /// The wrist strip's word for a scan at its default: "Scale ×1.63". The same string while the factor holds, so the
        /// strip doesn't recompose (it compares by reference).
        public static string DefaultWord(float k)
        {
            if (k != s_WordK || s_Word == null) { s_WordK = k; s_Word = $"Scale ×{Factor(k)}"; }
            return s_Word;
        }
    }
}
