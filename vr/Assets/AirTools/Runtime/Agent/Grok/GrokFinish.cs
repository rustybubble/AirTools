using System;
using AirTools.Parts;

namespace AirTools.Agent.Grok
{
    /// Finish names (backend docs/api.md §5 set_finish, POST /parts/{id}/finish): the server says "black" matches
    /// "Matte Black". Pure.
    public static class GrokFinish
    {
        /// The listed finish a spoken name means: an exact (case-insensitive) match, else a listed name that contains it
        /// ("black" → "Matte Black"), else one it contains ("matte black" → "Black"). Null when none is listed.
        public static string Match(PartSpec spec, string name)
        {
            if (spec?.finishes == null || string.IsNullOrWhiteSpace(name)) return null;
            string n = Norm(name);
            foreach (var f in spec.finishes) if (f != null && Norm(f.name) == n) return f.name;
            foreach (var f in spec.finishes) if (f != null && !string.IsNullOrEmpty(f.name) && Norm(f.name).Contains(n)) return f.name;
            foreach (var f in spec.finishes) if (f != null && !string.IsNullOrEmpty(f.name) && n.Contains(Norm(f.name))) return f.name;
            return null;
        }

        static string Norm(string s) => string.Join(" ", (s ?? "").ToLowerInvariant().Replace('-', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
