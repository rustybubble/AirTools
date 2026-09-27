using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    /// G1's agent actions (backend docs/api.md §5), registered over AgentActions' built-in cases:
    /// - set_finish {name, model_url?, label?}: with model_url, the server's re-textured model of the same part goes
    ///   on the held / selected part and its array copies in place (PartLoader.LoadVariant → PartInstance.SwapModels);
    ///   without it, the listed finish tints the part as before (AppCommands.SetFinish, "black" matching "Matte Black").
    ///   `label` ("Not a listed finish") shows under the spec card.
    /// - add_note {text, pin_id?, frame_id?, share?}: a notebook note; survey-pin notes and notes for the contractor are
    ///   marked "share": true for the job packet (other notes stay private).
    public static class G1Actions
    {
        /// What the last set_finish did (the harness logs it).
        public static string LastFinish { get; private set; } = "";

        [AgentAction("set_finish")]
        static bool SetFinish(AgentAction a, string reply)
        {
            string name = a.Str("name"), url = a.Str("model_url"), label = a.Str("label");
            if (string.IsNullOrWhiteSpace(label)) label = null;
            var tool = Services.Get<PartTool>();
            var part = tool != null ? (tool.Held != null ? tool.Held : tool.Selected) : null;
            if (part == null || part.Spec == null)
            {
                LastFinish = $"no part selected for '{name}'";
                Log.Warn($"set_finish: {LastFinish}");
                return false;
            }
            foreach (var p in tool.FinishGroup(part)) if (p != null) p.FinishLabel = label;
            if (string.IsNullOrEmpty(url) || !Services.TryGet<PartLoader>(out var loader)) return Tint(part, name, label);

            LastFinish = $"loading {url} for {part.Spec.id}";
            Log.Info($"set_finish: {LastFinish}");
            loader.LoadVariant(part.Spec, url, (model, owner) =>
            {
                if (part == null)
                {
                    // Removed while the model downloaded.
                    if (model != null) UnityEngine.Object.Destroy(model);
                    owner?.Dispose();
                    LastFinish = $"part gone before {url} arrived";
                    return;
                }
                if (model == null)
                {
                    Log.Warn($"set_finish: {url} didn't load ({loader.LastError}); tinting instead");
                    Tint(part, name, label);
                    return;
                }
                var group = tool.FinishGroup(part);
                group.RemoveAll(p => p == null);
                PartInstance.SwapModels(group, model, owner, name, url);
                foreach (var p in group) p.FinishLabel = label;
                LastFinish = $"swapped {url} onto {group.Count} part(s) of {part.Spec.id}";
                Log.Info($"set_finish: {LastFinish}");
            });
            return true;
        }

        static bool Tint(PartInstance part, string name, string label)
        {
            string listed = GrokFinish.Match(part.Spec, name);
            bool ok = listed != null && AppCommands.SetFinish(listed);
            if (label != null) part.FinishLabel = label;   // SetFinish clears it for a listed finish
            LastFinish = ok ? $"tinted {part.Spec.id} {listed}" : $"no listed finish '{name}' on {part.Spec.id}";
            if (!ok) Log.Warn($"set_finish: {LastFinish}");
            return ok;
        }

        [AgentAction("add_note")]
        static bool AddNote(AgentAction a, string reply)
        {
            string text = a.Str("text") ?? "";
            string pin = a.Str("pin_id"), frame = a.Str("frame_id");
            bool? explicitShare = a.args?["share"]?.Type == JTokenType.Boolean ? (bool)a.args["share"] : (bool?)null;
            bool share = Shareable(text, pin, explicitShare);
            if (Services.TryGet<NotebookController>(out var nb)) nb.AddNote(text, share, pin, frame);
            else Notebook.Add(new NotebookEntry("note", 0, "", System.Array.Empty<UnityEngine.Vector3>(), System.DateTime.Now, -1, text) { Share = share, PinId = pin, FrameId = frame });
            return true;
        }

        static readonly Regex s_ForContractor = new Regex(@"\b(?:contractor|installer|for the (?:job|packet|crew)|share (?:it|this|that)|to share)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// Whether a note goes in the job packet (the packet's privacy rule: only notes marked "share": true). The action
        /// says so, else a survey finding (it has a pin) or a note addressed to the contractor / installer does.
        public static bool Shareable(string text, string pinId, bool? explicitShare)
        {
            if (explicitShare.HasValue) return explicitShare.Value;
            if (!string.IsNullOrEmpty(pinId)) return true;
            return !string.IsNullOrEmpty(text) && s_ForContractor.IsMatch(text);
        }
    }
}
