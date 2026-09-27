using System;
using System.Collections.Generic;
using AirTools.Parts;
using AirTools.Scene;

namespace AirTools
{
    /// switchclean: a world-model switch closes every UI that is open in front of you (the user, Sat 09-26: "any open UIs
    /// automatically close if we switch world models around"). One hook: SiteScope.Leaving, raised by SiteScope.Switch
    /// once per real site change — a Model view wheel pick, show_model / next_model, Settings ▸ Next scene, LoadSite and
    /// the agent jobs that change the site all land in SceneRoot.SetContent / SetRuntimeContent → SiteScope.Switch — and
    /// before any owner parks, so a window that commits on its way out (Adjust) commits on the site being left.
    /// AppCommands.CloseAllForSwitch holds the list (Catalog + keyboard, Settings, Notebook, Adjust with Size & finish,
    /// the spec card, Find parts and its results, Sellers, Checkout, the Grok cards, the ladder card, the toast and the
    /// answer card, a running job, the palm ring) and logs "Switched to <site>: closed [...]".
    /// - **A running job** ("do the whole job", the autonomous replace) stops on the headset when the person switches
    ///   (no more polls or steps, the strip ends cancelled, a quiet toast); a switch the job asked for itself (a token,
    ///   JobSiteGuard) keeps it going on the new model.
    /// - **Stays:** the Model view wheel and its chips (the switcher itself; they are not in the list).
    /// - **Checkout:** never closed while a payment is being authorized (Paying): it stays up through the answer and its
    ///   receipt / "didn't go through" line stays until you close it. Mid-hold (the Pay ring filling) it stays too and
    ///   closes right after the hold ends without a payment; a hold that pays is a payment (stays).
    /// This file is the pure part (the run over the list, the checkout rules, the log line: offline-tested) and the hook.
    public static class SwitchClose
    {
        /// One surface a switch closes. `Open` says whether it's up; `Close` closes it; `Keep` (optional) gives a reason
        /// to leave it up now (null: close it); `Kept` (optional) runs when it was kept (the checkout: close after the hold).
        public readonly struct Surface
        {
            public readonly string Name;
            public readonly Func<bool> Open;
            public readonly Action Close;
            public readonly Func<string> Keep;
            public readonly Action<string> Kept;

            public Surface(string name, Func<bool> open, Action close, Func<string> keep = null, Action<string> kept = null)
            {
                Name = name; Open = open; Close = close; Keep = keep; Kept = kept;
            }
        }

        /// The surfaces the last switch closed and kept, its site, and how many switches closed anything (the harness).
        public static readonly List<string> LastClosed = new List<string>();
        public static readonly List<string> LastKept = new List<string>();
        public static string LastSite { get; private set; }
        public static string LastLine { get; private set; } = "";
        public static int Runs { get; private set; }

        /// Close every open surface of `surfaces` in order, except the ones Keep holds up. `closed` / `kept` get their
        /// names ("Checkout (paying)" for a kept one). A surface that throws while closing counts as closed and the run goes
        /// on (`failed` gets "Name: message"). Pure.
        public static void Run(IReadOnlyList<Surface> surfaces, List<string> closed, List<string> kept, List<string> failed = null)
        {
            closed?.Clear(); kept?.Clear(); failed?.Clear();
            if (surfaces == null) return;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                bool open;
                try { open = s.Open != null && s.Open(); }
                catch (Exception ex) { failed?.Add($"{s.Name}: {ex.Message}"); continue; }
                if (!open) continue;
                string why = null;
                try { why = s.Keep?.Invoke(); }
                catch (Exception ex) { failed?.Add($"{s.Name}: {ex.Message}"); }
                if (why != null)
                {
                    kept?.Add($"{s.Name} ({why})");
                    try { s.Kept?.Invoke(why); } catch (Exception ex) { failed?.Add($"{s.Name}: {ex.Message}"); }
                    continue;
                }
                try { s.Close?.Invoke(); }
                catch (Exception ex) { failed?.Add($"{s.Name}: {ex.Message}"); }
                closed?.Add(s.Name);
            }
        }

        /// "Switched to zabel-gymnasium: closed [Catalog, Settings]" (+ "; kept [Checkout (paying)]"). Pure.
        public static string Line(string site, IReadOnlyList<string> closed, IReadOnlyList<string> kept)
        {
            string s = $"Switched to {(string.IsNullOrEmpty(site) ? ModelSites.BuiltIn : site)}: closed [{Join(closed)}]";
            if (kept != null && kept.Count > 0) s += $"; kept [{Join(kept)}]";
            return s;
        }

        static string Join(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return "";
            var arr = new string[names.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = names[i];
            return string.Join(", ", arr);
        }

        /// Remember a run (CloseAllForSwitch): the site, what closed and what stayed.
        public static string Record(string site, IReadOnlyList<string> closed, IReadOnlyList<string> kept)
        {
            LastSite = site;
            LastClosed.Clear(); if (closed != null) LastClosed.AddRange(closed);
            LastKept.Clear(); if (kept != null) LastKept.AddRange(kept);
            LastLine = Line(site, closed, kept);
            Runs++;
            return LastLine;
        }

        // ---------------- the checkout (pure) ----------------

        public const string Paying = "paying", HoldingPay = "holding Pay";

        /// Why a checkout must stay up through a switch (null: close it): a payment being authorized, or the Pay ring
        /// filling (the hold is under way on a checkout that can still pay).
        public static string CheckoutKeep(CheckoutState state, bool holding)
        {
            if (state == CheckoutState.Paying) return Paying;
            if ((state == CheckoutState.Ready || state == CheckoutState.Failed) && holding) return HoldingPay;
            return null;
        }

        public enum AfterHold { Wait, Close, Stay }

        /// A checkout kept mid-hold by a switch, one frame later: still holding → Wait; the hold ended without a payment
        /// (let go, or refused before anything was sent) → Close; it paid or is paying (the receipt / the answer is
        /// yours to see) or it was closed meanwhile → Stay (the deferred close is dropped). Pure.
        public static AfterHold StepAfterHold(CheckoutState state, bool holding)
        {
            switch (state)
            {
                case CheckoutState.Paying:
                case CheckoutState.Paid:
                case CheckoutState.Closed:
                    return AfterHold.Stay;
                default:
                    return holding ? AfterHold.Wait : AfterHold.Close;
            }
        }

        // ---------------- the palm ring and agent replies (pure) ----------------

        /// PalmMenu.Update's hand verdict after a switch shut the ring: shut while the palm is still up (the gesture must
        /// close once), then as usual.
        public static bool PalmAfterDismiss(bool handOpen, ref bool dismissed)
        {
            if (!dismissed) return handOpen;
            if (!handOpen) dismissed = false;
            return false;
        }

        /// The actions of an agent reply that arrives after its model was left that still run (AgentClient): the switch
        /// the person asked for. Everything else was for the model left (a tape, a survey, a pin, a part, a card).
        public static bool RunsAfterSwitch(string action) => action == "show_model" || action == "next_model";

        // ---------------- the hook ----------------

        /// Subscribe to SiteScope.Leaving (idempotent). Runs before the first scene loads each Play session, after
        /// SiteScope.Reset (SubsystemRegistration); EditMode scene tests call it after their own SiteScope.Reset.
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Install()
        {
            SiteScope.Leaving -= OnLeaving;
            SiteScope.Leaving += OnLeaving;
        }

        public static void Uninstall() => SiteScope.Leaving -= OnLeaving;

        static void OnLeaving(string from, string to) => AppCommands.CloseAllForSwitch(to);

        /// Tests: forget the last run.
        public static void ResetStats()
        {
            LastClosed.Clear(); LastKept.Clear();
            LastSite = null; LastLine = ""; Runs = 0;
        }
    }
}
