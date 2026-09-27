using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace AirTools.Agent
{
    /// The survey card (show_survey, B1 hand-off §2–§3): the size groups the server made from the survey, most common
    /// first — "6 × 10⅜ × 11″" / "6 × 262 × 279 mm" (D2: the user's unit) with their object ids — amber for objects
    /// that didn't lock onto their corners, the answer to "which is the widest?" highlighted (focus), and what was
    /// skipped. A main-slot window; the focused objects also pulse in the scene and keep their labels.
    public class SurveyCard : MonoBehaviour
    {
        public FloatingWindow window;
        public TextMeshPro title;
        public TextMeshPro body;
        public TextMeshPro footer;
        public GlassButton close;

        public const int MaxRows = 6;

        public JObject LastArgs { get; private set; }
        public int Shows { get; private set; }
        public bool IsOpen => window != null && window.IsOpen;

        void OnEnable() { Services.Register(this); if (close != null) close.Clicked += Close; }
        void OnDisable() { Services.Unregister(this); if (close != null) close.Clicked -= Close; }

        public void Close() => window?.Close();

        /// show_survey {request_id, label, groups[{w_mm, h_mm, count, ids}], unverified[], skipped[], focus[]}.
        public bool Show(JObject args)
        {
            if (args == null) return false;
            LastArgs = args;
            Shows++;
            var text = Render(args);
            window?.Open();
            var focus = Ids(args["focus"]);
            if (Services.TryGet<MeasureTool>(out var tool)) tool.FocusSurvey(focus, (string)args["request_id"]);
            Log.Info($"Survey card: {Strip(text.title)} ({text.subtitle}) | {Strip(text.body).Replace("\n", " | ")} | {Strip(text.footer)}");
            return true;
        }

        (string title, string subtitle, string body, string footer) Render(JObject args)
        {
            var text = Compose(args);
            string sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
            if (title != null) title.text = $"{text.title} <size=70%><color=#{sec}>{text.subtitle}</color></size>";
            if (body != null) body.text = text.body;
            if (footer != null) footer.text = text.footer;
            return text;
        }

        /// D2: the unit changed — re-word the open card's sizes (no re-focus, no log).
        public void Relabel()
        {
            if (IsOpen && LastArgs != null) Render(LastArgs);
        }

        static string Strip(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "");

        public static List<string> Ids(JToken t) =>
            t is JArray a ? a.Select(x => x.Type == JTokenType.Object ? (string)x["id"] : (string)x).Where(x => !string.IsNullOrEmpty(x)).ToList() : new List<string>();

        /// Title ("18 cabinet doors"), subtitle ("8 sizes"), rows and footer (pure, for tests).
        public static (string title, string subtitle, string body, string footer) Compose(JObject args)
        {
            var col = UiTheme.Current.colors;
            string amber = ColorUtility.ToHtmlStringRGBA(col.warning), accent = ColorUtility.ToHtmlStringRGBA(col.ink),   // D3: the focused group is ink
                   sec = ColorUtility.ToHtmlStringRGBA(col.textSecondary);
            string label = (string)args["label"];
            var unverified = new HashSet<string>(Ids(args["unverified"]));
            var focus = new HashSet<string>(Ids(args["focus"]));
            var skipped = Ids(args["skipped"]);
            var groups = args["groups"] as JArray ?? new JArray();
            int total = groups.Sum(g => (int?)g["count"] ?? 0);
            string noun = Copy.SurveyNoun(label, total);
            string title = $"{total} {noun}";
            string subtitle = $"{groups.Count} {(groups.Count == 1 ? "size" : "sizes")}";
            var rows = new List<string>();
            int shown = 0;
            foreach (var g in groups)
            {
                if (shown++ >= MaxRows) break;
                var ids = Ids(g["ids"]);
                bool isFocus = ids.Any(focus.Contains);
                string size = Copy.WxH(((int?)g["w_mm"] ?? 0) / 1000.0, ((int?)g["h_mm"] ?? 0) / 1000.0);   // D2: the user's unit
                var idText = ids.Take(5).Select(i => unverified.Contains(i) ? $"<color=#{amber}>{i}</color>" : focus.Contains(i) ? $"<color=#{accent}>{i}</color>" : i).ToList();
                string more = ids.Count > 5 ? $" +{ids.Count - 5}" : "";
                string lead = isFocus ? $"<color=#{accent}>→</color> " : "";
                rows.Add($"{lead}{UiText.Tabular($"{(int?)g["count"] ?? ids.Count} ×")} {UiText.Tabular(size)}  <color=#{sec}>{string.Join(" ", idText)}{more}</color>");
            }
            if (groups.Count > MaxRows) rows.Add($"<color=#{sec}>+{groups.Count - MaxRows} more sizes</color>");
            if (groups.Count == 0) rows.Add($"<color=#{sec}>Nothing measured</color>");
            var foot = new List<string>();
            if (unverified.Count > 0) foot.Add($"<color=#{amber}>⚠ {unverified.Count} didn't lock onto {(unverified.Count == 1 ? "its" : "their")} corners</color>");
            if (skipped.Count > 0) foot.Add($"{skipped.Count} skipped");
            if (foot.Count == 0) foot.Add("Every corner locked on");
            return (title, subtitle, string.Join("\n", rows), string.Join(" · ", foot));
        }
    }
}
