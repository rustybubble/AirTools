using System;
using System.Collections.Generic;
using System.Reflection;
using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent
{
    /// Marks a static `bool Handler(AgentAction a, string reply)` as the handler for an agent action. Feature files add
    /// actions this way (the Grok integration lanes, backend docs/api.md §5) instead of growing the switch below; a
    /// registered handler takes precedence over the built-in case of the same name.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class AgentActionAttribute : Attribute
    {
        public readonly string Name;
        public AgentActionAttribute(string name) { Name = name; }
    }

    /// Executes the agent's actions[] exactly like button taps (backend docs/api.md §5; SPEC §3.2: voice and UI call
    /// the same AppCommands methods). start_checkout only opens the hold-to-pay panel — no action can pay.
    public static class AgentActions
    {
        /// Every action name in the contract, in the §5 table's order.
        public static readonly string[] Known =
        {
            "search_started", "select_candidate", "set_finish", "place_array", "show_sellers", "start_checkout",
            "equip_tool", "add_note", "scene_pin", "show_bom",
            // B1 / B3 hand-off §2
            "survey", "check_slope", "show_tape_survey", "show_survey", "stop_survey", "show_limits",
        };

        public delegate bool Handler(AgentAction a, string reply);
        static Dictionary<string, Handler> s_Handlers;

        /// Handlers from [AgentAction] methods in this assembly (found once, by reflection).
        public static IReadOnlyDictionary<string, Handler> Handlers
        {
            get
            {
                if (s_Handlers != null) return s_Handlers;
                var map = new Dictionary<string, Handler>();
                foreach (var t in typeof(AgentActions).Assembly.GetTypes())
                    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        foreach (var attr in m.GetCustomAttributes<AgentActionAttribute>())
                        {
                            var h = (Handler)Delegate.CreateDelegate(typeof(Handler), m, false);
                            if (h == null) { Log.Warn($"[AgentAction(\"{attr.Name}\")] {t.Name}.{m.Name} has the wrong signature (bool (AgentAction, string))"); continue; }
                            if (map.ContainsKey(attr.Name)) Log.Warn($"[AgentAction(\"{attr.Name}\")] registered twice; {t.Name}.{m.Name} wins");
                            map[attr.Name] = h;
                        }
                return s_Handlers = map;
            }
        }

        /// Every action the app handles: the built-in switch plus the registered handlers.
        public static bool IsKnown(string name) => Array.IndexOf(Known, name) >= 0 || Handlers.ContainsKey(name ?? "");

        public static string LastResult { get; private set; } = "";
        /// autonomy: every action as it's carried out (reply or poll), with whether it worked — the harness records a job's
        /// whole sequence (History keeps only the last 50 names).
        public static event Action<AgentAction, bool> Ran;
        public static readonly List<string> History = new List<string>();
        /// Actions executed since start-up (History keeps only the last 50; diff this to count a batch).
        public static long Executed { get; private set; }

        /// Every action of a reply, in order (a reply can carry several: survey_started + show_survey, search_started +
        /// add_note, and a slow job's result leading the next command's own actions). One failing action never stops
        /// the rest, and a handler that starts another command can't disturb the list being run. Returns how many
        /// were carried out.
        public static int ExecuteAll(IEnumerable<AgentAction> actions, string reply = null)
        {
            if (actions == null) return 0;
            var list = new List<AgentAction>(actions);
            int ok = 0;
            for (int i = 0; i < list.Count; i++)
            {
                try { if (Execute(list[i], reply)) ok++; }
                catch (Exception e) { Log.Warn($"Agent action {list[i]?.name} ({i + 1}/{list.Count}) threw: {e.Message}"); }
            }
            if (list.Count > 1) Log.Info($"Agent actions: {ok}/{list.Count} carried out [{string.Join(", ", list.ConvertAll(a => a?.name ?? "?"))}]");
            return ok;
        }

        /// True if the action was recognised and carried out. `reply` is the agent's spoken line (scene_pin carries only
        /// {frame_id, box}; the answer to label the pin with is the reply).
        public static bool Execute(AgentAction a, string reply = null)
        {
            if (a == null || string.IsNullOrEmpty(a.name)) return false;
            bool ok;
            if (Handlers.TryGetValue(a.name, out var handler))
            {
                try { ok = handler(a, reply); }
                catch (Exception e) { ok = false; Log.Warn($"Agent action {a.name} threw: {e.Message}"); }
            }
            else switch (a.name)
            {
                case "search_started":
                    ok = AppCommands.FollowSearch(a.Str("job_id"));
                    break;
                case "select_candidate":
                    ok = a.Int("index") is int i && AppCommands.SelectCandidate(i);
                    break;
                case "set_finish":
                    ok = AppCommands.SetFinish(a.Str("name"));
                    break;
                case "place_array":
                    ok = AppCommands.PlaceArray(a.Float("spacing_mm"));
                    break;
                case "show_sellers":
                    ok = AppCommands.ShowSellers(a.Str("sort") ?? "cheapest");
                    break;
                case "start_checkout":
                    // Opens the panel only; the charge needs the physical 1 s hold on Pay.
                    ok = AppCommands.StartCheckout(a.Int("seller_index") ?? 0);
                    break;
                case "equip_tool":
                    ok = EquipTool(a.Str("tool"));
                    if (ok && a.Str("label") != null) AppCommands.SetNextMeasureLabel(a.Str("label"));   // measure-edges
                    break;
                case "add_note":
                    AppCommands.AddNote(a.Str("text") ?? "");
                    ok = true;
                    break;
                case "scene_pin":
                    ok = AppCommands.PinFromFrame(a.Str("frame_id"), Box(a.args?["box"]), a.Str("label") ?? a.Str("answer") ?? reply);
                    break;
                case "show_bom":
                    ok = AppCommands.ShowBom(a.args?.ToObject<PartBom>());
                    break;
                case "survey":
                    // The headset measures every matching structure object with its real tape, then reports once.
                    ok = AppCommands.Survey(a.Str("label"), a.Str("where"), a.Str("measure"), a.Str("request_id"));
                    break;
                case "check_slope":
                    ok = AppCommands.CheckSlope(a.Str("target"), a.Str("request_id"), Strings(a.args?["edge_ids"]));
                    break;
                case "show_tape_survey":
                    // The door / window survey card (hand-off p4-grok 0009: renamed from show_survey, which F10's
                    // condition survey owns on integration/grok-all).
                    ok = AppCommands.ShowSurvey(a.args);
                    break;
                case "show_survey":
                    // Older servers sent the tape survey as show_survey {groups, …}. F10's {survey_id, pins, label} is drawn
                    // by a registered [AgentAction] handler when one exists; without it, it must not become an empty card.
                    if (a.args?["groups"] != null) ok = AppCommands.ShowSurvey(a.args);
                    else
                    {
                        ok = false;
                        UiToast.Show("Can't show that survey yet", ColorRole.Warning);
                        Log.Warn($"Agent action show_survey without groups (condition survey?) has no handler: {a}");
                    }
                    break;
                case "stop_survey":
                    AppCommands.StopSurvey();
                    ok = true;
                    break;
                case "show_limits":
                    ok = AppCommands.ShowLimits(Limits(a), a.args?["refused"] is JArray refused && refused.Count > 0);
                    break;
                default:
                    ok = false;
                    UiToast.Show("Can't do that yet", ColorRole.Warning);
                    Log.Warn($"Agent action refused (unknown): {a}");
                    break;
            }
            LastResult = $"{a.name}: {(ok ? "ok" : "failed")}";
            Executed++;
            try { Ran?.Invoke(a, ok); } catch (Exception e) { Log.Warn($"AgentActions.Ran listener threw: {e.Message}"); }   // autonomy
            History.Add(LastResult);
            if (History.Count > 50) History.RemoveAt(0);
            Log.Info($"Agent action {a} → {(ok ? "ok" : "failed")}");
            return ok;
        }

        /// The agent's tool names (tape / level / protractor / plumb / area / notebook / part) onto the app's tools.
        public static bool EquipTool(string tool)
        {
            switch ((tool ?? "").Trim().ToLowerInvariant())
            {
                case "notebook": AppCommands.ShowNotebook(true); return true;
                case "plumb": return AppCommands.EquipTool("level");
                case "part": case "parts":
                    AppCommands.EquipTool("part");
                    return true;
                default: return AppCommands.EquipTool(tool);
            }
        }

        public static List<string> Strings(JToken t)
        {
            var list = new List<string>();
            if (t is JArray a) foreach (var x in a) if (x != null && x.Type != JTokenType.Null) list.Add((string)x);
            return list;
        }

        /// show_limits {intent_id, max_total_usd, deliver_by, seller_policy, refused} → the intent.
        public static MandateIntent Limits(AgentAction a) => new MandateIntent
        {
            id = a.Str("intent_id") ?? a.Str("id"),
            max_total_usd = a.Float("max_total_usd"),
            deliver_by = a.Str("deliver_by"),
            seller_policy = a.Str("seller_policy"),
            source = a.Str("source") ?? "voice",
            text = a.Str("text"),
        };

        public static float[] Box(JToken t)
        {
            if (!(t is JArray a) || a.Count < 4) return null;
            return new[] { (float)a[0], (float)a[1], (float)a[2], (float)a[3] };
        }
    }
}
