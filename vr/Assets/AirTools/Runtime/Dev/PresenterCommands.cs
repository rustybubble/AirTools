using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AirTools.Core;

namespace AirTools.Dev
{
    /// One command from the presenter relay (tools/presenter/presenter_server.py): {"id", "cmd", "arg"}.
    public readonly struct PresenterCommand
    {
        public readonly string Id, Cmd, Arg;

        public PresenterCommand(string id, string cmd, string arg)
        {
            Id = id ?? ""; Cmd = (cmd ?? "").Trim().ToLowerInvariant(); Arg = string.IsNullOrWhiteSpace(arg) ? null : arg.Trim().ToLowerInvariant();
        }

        public override string ToString() => Arg == null ? $"{Id} {Cmd}" : $"{Id} {Cmd} {Arg}";
    }

    /// D7 (UX W1.8): the presenter protocol on the headset side, pure (EditMode-tested offline). The same lists as the
    /// relay (tools/presenter/presenter_server.py COMMANDS / BEATS / REFUSED; its tests check they agree).
    /// The presenter never pays: a command or argument about paying, checkout or the hold is refused here before anything
    /// runs, as the relay already refuses it.
    public static class PresenterCommands
    {
        public const int DefaultPort = 8766;

        /// Everything the presenter may ask for.
        public static readonly string[] Commands = { "reset", "hint", "enter", "exit", "beat", "demo", "ping" };

        /// Skip to a beat of the judge's three minutes (docs/ux/README.md §2.7), in order. There is no pay beat: "sellers"
        /// opens the seller list, and Pay with Visa and the 1 s hold stay the judge's. e2e: "replace" (after the script)
        /// runs the whole replace flow on the loaded site (E2EHarness: take out → measure → find → put in → switch).
        public static readonly string[] Beats = { "measure", "find", "take", "place", "sellers", "home", "report", "replace" };

        public const string RefusedPattern = @"(^|[^a-z])(pay|checkout|check[ _-]?out|hold|buy|purchase|order|charge|visa|receipt)";

        public const string Refusal = "refused: the presenter never pays (paying is the judge's own 1 s hold on Pay)";

        static readonly Regex s_Refused = new Regex(RefusedPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsRefused(string cmd, string arg) =>
            (!string.IsNullOrEmpty(cmd) && s_Refused.IsMatch(cmd)) || (!string.IsNullOrEmpty(arg) && s_Refused.IsMatch(arg));

        /// Null when the command may run; else why not (the refusal first).
        public static string Validate(in PresenterCommand c)
        {
            if (IsRefused(c.Cmd, c.Arg)) return Refusal;
            switch (c.Cmd)
            {
                case "reset": case "hint": case "enter": case "exit": case "ping":
                    return c.Arg == null ? null : $"{c.Cmd} takes no argument";
                case "beat":
                    return Array.IndexOf(Beats, c.Arg) >= 0 ? null : $"beat needs one of {string.Join(", ", Beats)}";
                case "demo":
                    return c.Arg == "on" || c.Arg == "off" ? null : "demo needs on or off";
                default:
                    return $"unknown command '{c.Cmd}'";
            }
        }

        /// GET /presenter/next → the commands in order ({"commands": [{"id","cmd","arg"}…]}). Malformed JSON: none.
        public static List<PresenterCommand> ParseNext(string json)
        {
            var list = new List<PresenterCommand>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                if (o["commands"] is Newtonsoft.Json.Linq.JArray a)
                    foreach (var t in a)
                        if (t is Newtonsoft.Json.Linq.JObject c)
                            list.Add(new PresenterCommand((string)c["id"], (string)c["cmd"], c["arg"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)c["arg"] : null));
            }
            catch (Newtonsoft.Json.JsonException) { list.Clear(); }
            return list;
        }

        /// The relay next to the parts server: its scheme and host, the presenter port ("http://127.0.0.1:8000" →
        /// "http://127.0.0.1:8766": the USB tunnel; a hotspot IP stays that IP).
        public static string BaseUrl(string partsServer, int port = DefaultPort)
        {
            if (!string.IsNullOrEmpty(partsServer) && Uri.TryCreate(partsServer.Trim(), UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                return $"{u.Scheme}://{(u.HostNameType == UriHostNameType.IPv6 ? $"[{u.Host}]" : u.Host)}:{port}";
            return $"http://127.0.0.1:{port}";
        }

        /// Which of the judge's beats (§2.7) the guide rail's rule belongs to, for the presenter page ("0:20 measure").
        public static string BeatOf(RuleId rule, AppMode mode, bool everEntered)
        {
            switch (rule)
            {
                case RuleId.R05: return "0:00 start";
                case RuleId.R07: case RuleId.R46: case RuleId.R47: return "0:10 entering";
                case RuleId.R49: case RuleId.R50: case RuleId.R52: case RuleId.R53: case RuleId.R54:
                case RuleId.R30: case RuleId.R31: case RuleId.R32: return "0:20 measure";
                case RuleId.R42: case RuleId.R43: case RuleId.R44: return "0:40 find parts";
                case RuleId.R19: case RuleId.R20: case RuleId.R21: case RuleId.R22: case RuleId.R39: case RuleId.R40: case RuleId.R41:
                    return "0:55 take a part";
                case RuleId.R23: case RuleId.R24: case RuleId.R25: case RuleId.R26: case RuleId.R27: return "1:05 place";
                case RuleId.R34: case RuleId.R35: case RuleId.R36: case RuleId.R37: case RuleId.R38: return "1:20 fit";
                case RuleId.R28: case RuleId.R29: return "1:35 sellers";
                case RuleId.R10: case RuleId.R08: case RuleId.R09: return "1:50 judge pays";
                case RuleId.R11: case RuleId.R12: case RuleId.R33: return "2:10 home";
                case RuleId.R04: case RuleId.R03: return "2:30 report";
                case RuleId.R45: return "tabletop";
                default: return mode == AppMode.Passthrough ? (everEntered ? "2:30 report" : "0:00 start") : "exploring";
            }
        }
    }
}
