using System;
using AirTools.UI;

namespace AirTools.Agent.Grok
{
    /// Survey pin look by severity (docs/api.md §5 show_survey: severe red, moderate amber, minor yellow). Colours are
    /// theme roles, never literals; state is never colour alone (the pin's size and its label's words differ too).
    public static class SeverityStyle
    {
        public static string Normalise(string severity) => (severity ?? "").Trim().ToLowerInvariant();

        public static int Rank(string severity) => Normalise(severity) switch
        {
            "severe" => 3,
            "moderate" => 2,
            "minor" => 1,
            _ => 0,
        };

        public static ColorRole Role(string severity) => Normalise(severity) switch
        {
            "severe" => ColorRole.Danger,
            "moderate" => ColorRole.Warning,
            "minor" => Yellow,
            _ => ColorRole.TextSecondary,
        };

        /// Tape yellow: UX D3's `Ink` role where the theme has it (feat/d3-white-primary, #FFD23F, with caution moved to
        /// orange #FF9F43), else `Warning` (before D3 the amber #FFB529 is the yellowest role there is).
        public static ColorRole Yellow => Named("Ink", ColorRole.Warning);

        /// A theme role by name when this build's theme defines it (roles are being re-themed in parallel), else the fallback.
        public static ColorRole Named(string role, ColorRole fallback) =>
            !string.IsNullOrEmpty(role) && Enum.TryParse(role, false, out ColorRole r) && Enum.IsDefined(typeof(ColorRole), r) ? r : fallback;

        /// Pin size factor: the worse, the bigger (state by more than colour, docs/UI.md §6.5).
        public static float MarkerScale(string severity) => Normalise(severity) switch
        {
            "severe" => 1.35f,
            "moderate" => 1.1f,
            "minor" => 0.9f,
            _ => 0.8f,
        };
    }
}
