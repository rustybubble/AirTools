using System;
using System.Collections.Generic;

namespace AirTools.Scene
{
    /// An owner of world items that belong to the site they were made in (sitescope): tapes, levels, ladders, placed parts
    /// and generated assets, Grok pins and plans, fall edges, scene-part removals.
    public interface ISiteScoped
    {
        /// The loaded site changed from `from` to `to`.
        /// - **Park `from`.** Its items go out of the live lists, the undo / redo stacks and the agent context, and are
        ///   hidden: inactive, so their colliders are off and no tool snaps to them. Nothing is destroyed.
        /// - **Bring back `to`.** Its parked items return exactly as they were. Their scene-root-space points are
        ///   multiplied by `factor` (the calibration now / when `to` was left), and the next SceneRoot.Rescaled takes
        ///   them to the calibration the site loads with. Items stored in the package frame need no factor.
        void SwitchSite(string from, string to, float factor);

        /// Forget every parked site's items: destroy them and drop their stacks (Demo reset clears all sites; the live
        /// site is cleared by the owner's own ClearAll).
        void ClearParked();

        /// Sites this owner has something parked for (0 after ClearParked; DemoReset.Verify).
        int ParkedCount { get; }
    }

    /// Which site the world items being made now belong to, and the switch that parks one site's items and brings back
    /// another's (sitescope; pure, EditMode-tested).
    /// - **The key.** The loaded package's site id, or ModelSites.BuiltIn ("built-in") for the facade built into the app.
    ///   Revisions of a site share its items.
    /// - **When.** SceneRoot parks the site left before it takes the new content (the owners still see the old frame and
    ///   Current is still the site left), and brings the arriving site's items back before it raises ContentChanged (a
    ///   fresh load of another site, the built-in fallback). A revision swap in place keeps the site.
    /// - **Order.** Owners register with an order class (OrderGrok … OrderOther): the Grok layer and a survey let go first,
    ///   then the tools in CalibrationSync's order, then the placement editor.
    /// - **Calibration.** Every site's items are stored in SceneRoot space at the calibration it had when it was left.
    ///   A fresh load starts at ×1 and SceneStreamer then restores the site's scale. It holds the arrival around that
    ///   (HoldArrival / Arrive): the owners park the site left, sit on an empty site while the scale is restored, then
    ///   bring the arriving site's items back with factor = calibration now / at leave (1 when it loads at the scale it
    ///   was left at, so nothing is re-solved). Without a hold (the built-in facade, tests) the switch is immediate and
    ///   the factor is taken against the scale at the switch; a later SceneRoot.Rescaled moves them like any live item.
    /// - **Undo.** Each owner parks its undo / redo stacks with the site (per-site stacks). EditHistory only sees the live
    ///   site's edits, and a redo can never bring an item back into another scan.
    public static class SiteScope
    {
        /// The site the live items belong to.
        public static string Current { get; private set; } = ModelSites.BuiltIn;
        /// Site changes since start (the harness logs it).
        public static int Switches { get; private set; }

        /// Raised after every owner has switched: (from, to).
        public static event Action<string, string> Changed;

        /// switchclean: raised once per real switch, before any owner parks (Current is still `from`): (from, to). The
        /// windows in front of you close here (AppCommands.CloseAllForSwitch via SwitchClose), so a commit a window makes
        /// on its way out (Adjust's session) lands on the site being left. Not raised when `to` already is the site.
        public static event Action<string, string> Leaving;

        static readonly List<ISiteScoped> s_Owners = new List<ISiteScoped>();
        static readonly Dictionary<ISiteScoped, int> s_Order = new Dictionary<ISiteScoped, int>();

        /// Registration order classes (lower switches first, both steps): the Grok layer and a running survey let go
        /// before the tools move (a deselect mustn't rebuild a plan's ghosts; a survey stops before its tape parks); the
        /// tools in CalibrationSync's order (tapes before parts, so a part coming back at another scale is fit-checked
        /// against rescaled tapes); the placement editor after the parts (Adjust closes on a parked part: its session is
        /// one undo step of the site left and no fit is re-checked against a scene that's going).
        public const int OrderGrok = 0, OrderSurvey = 5, OrderMeasure = 10, OrderLevel = 11, OrderParts = 12, OrderLadder = 13,
            OrderPlacement = 14, OrderOther = 20;
        /// Each site's calibration, the last time it was current (scene metres per package unit).
        static readonly Dictionary<string, float> s_Calibration = new Dictionary<string, float>();

        public static IReadOnlyList<ISiteScoped> Owners => s_Owners;

        /// The site key of a loaded scene: its site id for a runtime package, else the built-in facade.
        public static string Key(bool runtimePackage, string site) => ModelSites.Current(runtimePackage, site);

        public static string KeyOf(SceneRoot root) => root == null ? ModelSites.BuiltIn : Key(root.IsRuntimePackage, root.Site);

        public static bool IsCurrent(string site) => string.Equals(Normal(site), Current, StringComparison.Ordinal);

        static string Normal(string site) => string.IsNullOrEmpty(site) ? ModelSites.BuiltIn : site;

        public static void Register(ISiteScoped owner, int order = OrderOther)
        {
            if (owner == null || s_Owners.Contains(owner)) return;
            s_Order[owner] = order;
            int i = s_Owners.Count;
            while (i > 0 && s_Order[s_Owners[i - 1]] > order) i--;   // stable: equal orders keep registration order
            s_Owners.Insert(i, owner);
        }

        public static void Unregister(ISiteScoped owner)
        {
            if (owner != null && s_Owners.Remove(owner)) s_Order.Remove(owner);
        }

        /// The current site's calibration changed (SceneRoot.Rescale): remember it for the way back.
        public static void NoteCalibration(float calibration)
        {
            if (calibration > 0f && !float.IsInfinity(calibration) && !float.IsNaN(calibration)) s_Calibration[Current] = calibration;
        }

        /// The calibration `site` had when it was last current (0 = never seen).
        public static float CalibrationOf(string site) => s_Calibration.TryGetValue(Normal(site), out var c) ? c : 0f;

        /// What `site`'s parked root-space points are multiplied by when it comes back at `calibrationNow`.
        public static float FactorFor(string site, float calibrationNow) => Factor(CalibrationOf(site), calibrationNow);

        /// The owners' live site between parking the site left and bringing the arriving one back (and while an arrival
        /// is held): empty, so a calibration restored in between rescales nothing.
        public const string Arriving = "(arriving)";

        static int s_Hold;
        static string s_PendingFrom, s_PendingTo;
        static float s_PendingWas;

        /// An arrival is held (between SceneRoot.SetRuntimeContent and the site's scale being restored).
        public static bool Pending => s_PendingTo != null;

        /// `to` is loaded now, at `calibrationNow`. Parks the current site's items and brings `to`'s back (every
        /// registered owner, in registration order). False when `to` already is the current site.
        /// While HoldArrival is in effect only the park happens; Arrive brings `to`'s items back once its scale is set.
        public static bool Switch(string to, float calibrationNow)
        {
            to = Normal(to);
            if (Pending && s_Hold == 0) Arrive(calibrationNow);   // a hold that was never released
            if (to == Current) return false;
            string from = Current;
            if (Pending) from = s_PendingFrom;   // switching again inside a hold: the owners are on Arriving
            float was = CalibrationOf(to);
            Switches++;
            RaiseLeaving(from, to);   // switchclean: windows close first, while Current is still the site left
            // Two steps, always: every owner parks `from` first, with Current still `from` (a commit an owner makes on the
            // way out, e.g. an adjust session, lands on `from`'s stacks and rows and can't drop the arriving site's redo),
            // then every owner brings `to` back.
            if (!Pending) Notify(from, Arriving, 1f);
            Current = to;
            if (s_Hold > 0)
            {
                s_PendingFrom = from; s_PendingTo = to; s_PendingWas = was;
                NoteCalibration(calibrationNow);
                return true;
            }
            s_PendingFrom = s_PendingTo = null;
            NoteCalibration(calibrationNow);
            Notify(Arriving, to, Factor(was, calibrationNow));
            Changed?.Invoke(from, to);
            return true;
        }

        /// SceneStreamer, before a fresh load's SetRuntimeContent: the site's items wait until its scale is restored
        /// (Arrive), so they are never rescaled on the way in (a ladder would re-solve against a scene with no collider).
        public static void HoldArrival() => s_Hold++;

        /// The held arrival completes at `calibrationNow` (the site's restored scale): the arriving site's owners bring
        /// its items back with factor = calibration now / at leave (1 when it loads at the scale it was left at).
        public static void Arrive(float calibrationNow)
        {
            if (s_Hold > 0) s_Hold--;
            if (!Pending || s_Hold > 0) return;
            string from = s_PendingFrom, to = s_PendingTo;
            float was = s_PendingWas;
            s_PendingFrom = s_PendingTo = null;
            NoteCalibration(calibrationNow);
            Notify(Arriving, to, Factor(was, calibrationNow));
            Changed?.Invoke(from, to);
        }

        /// switchclean: Leaving's listeners, each on its own (one that throws never stops the switch).
        static void RaiseLeaving(string from, string to)
        {
            var handlers = Leaving;
            if (handlers == null) return;
            s_Notifying++;
            try
            {
                foreach (var d in handlers.GetInvocationList())
                {
                    try { ((Action<string, string>)d)(from, to); }
                    catch (Exception ex)
                    {
                        try { AirTools.Core.Log.Warn($"SiteScope: a Leaving listener failed on {from} → {to}: {ex.Message}"); }
                        catch { /* a logger that throws (no Unity) mustn't stop the switch */ }
                    }
                }
            }
            finally { s_Notifying--; }
        }

        static int s_Notifying;

        /// switchclean: a switch is being carried out right now (its windows closing, its owners parking or bringing a site
        /// back). What an owner raises meanwhile is the switch, not the user: a window must not open for it (the ladder
        /// card for the arriving site's last ladder).
        public static bool Switching => s_Notifying > 0;

        static float Factor(float was, float now)
        {
            if (!(was > 0f) || !(now > 0f) || float.IsInfinity(now)) return 1f;
            float k = now / was;
            return float.IsNaN(k) || float.IsInfinity(k) ? 1f : k;
        }

        static void Notify(string from, string to, float factor)
        {
            List<Exception> failed = null;
            s_Notifying++;   // switchclean
            try
            {
                foreach (var owner in s_Owners.ToArray())   // a copy: an owner may register or unregister meanwhile (a switch is rare)
                {
                    try { owner.SwitchSite(from, to, factor); }
                    catch (Exception ex) { (failed ??= new List<Exception>()).Add(ex); }
                }
            }
            finally { s_Notifying--; }   // switchclean
            if (failed != null)
                foreach (var ex in failed)
                    try { AirTools.Core.Log.Warn($"SiteScope: an owner failed to switch {from} → {to}: {ex.Message}"); }
                    catch { /* a logger that throws (no Unity) mustn't stop the switch */ }
        }

        /// Parked sites summed over the owners (0: nothing of another site is kept).
        public static int ParkedCount
        {
            get { int n = 0; foreach (var o in s_Owners) n += o.ParkedCount; return n; }
        }

        /// Demo reset: every owner forgets its parked sites (their live site is cleared by their own ClearAll).
        public static void ClearParked()
        {
            foreach (var owner in s_Owners.ToArray()) owner.ClearParked();
            float now = CalibrationOf(Current);
            s_Calibration.Clear();
            if (now > 0f) s_Calibration[Current] = now;
        }

        /// Back to the start: the built-in facade, no owners, nothing remembered (tests; a new Play session).
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            Current = ModelSites.BuiltIn;
            Switches = 0;
            s_Hold = 0;
            s_PendingFrom = s_PendingTo = null;
            s_PendingWas = 0f;
            s_Owners.Clear();
            s_Order.Clear();
            s_Calibration.Clear();
            Changed = null;
            Leaving = null;   // switchclean: SwitchClose.Install subscribes again (BeforeSceneLoad; tests call it)
            s_Notifying = 0;   // switchclean
        }
    }
}
