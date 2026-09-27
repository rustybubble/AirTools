using System;
using System.Collections.Generic;
using System.Text;
using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// Who wins the pool of world labels (docs/ux/declutter.md §5.2, DC7), highest first.
    public enum LabelClass
    {
        /// The coach's "Don't drill here" card; the fall-edge labels while Fall edges is on.
        Safety = 0,
        /// The Grok layer's honesty caption, the live or just-made shape, the held part's fit, a new ask pin.
        Focus = 1,
        /// The active task: the newest Grok layer's items or the B1 survey, whichever is newer.
        Task = 2,
        /// Saved measurements: one summary pill per shape.
        Saved = 3,
        /// Placed parts: one callout per part, one group label per array.
        Parts = 4,
        /// Readings: level, ladder.
        Readings = 5,
        /// Passive: the scan's labels (Settings ▸ Labels), ask pins older than 30 s.
        Passive = 6,
    }

    /// One producer's request to the pool: its class, labels that must count first (captions, safety), labels in the
    /// producer's own priority order, and when its newest label appeared (newer wins within a class).
    public struct LabelClaim
    {
        public LabelClass Class;
        /// Counted before every item, in class order, but still clipped to the pool.
        public int Mandatory;
        /// Shown in the producer's order; the pool gives it the first N.
        public int Items;
        /// Time (s) of the claim's newest label: newer first within a class.
        public float Newest;
        /// > 0: all-or-one. The whole set shows when this many slots are left (the focused shape's sides, angles and
        /// area count as one claim of up to 4); otherwise only its first label (the summary).
        public int CostCap;

        public LabelClaim(LabelClass cls, int mandatory, int items, float newest, int costCap = 0)
        {
            Class = cls;
            Mandatory = mandatory;
            Items = items;
            Newest = newest;
            CostCap = costCap;
        }

        public override string ToString() => $"{Class} m{Mandatory} i{Items} t{Newest:0.##}{(CostCap > 0 ? $" cap{CostCap}" : "")}";
    }

    /// The pool's answer to one claim.
    public struct LabelGrant
    {
        /// Mandatory labels that show.
        public int Mandatory;
        /// Items that show (the claim's first N); the rest drop to their dot or outline.
        public int Items;
        /// Pool slots this claim uses (a capped claim's whole set uses its cap).
        public int Used;

        public int Shown => Mandatory + Items;
        public override string ToString() => $"m{Mandatory} i{Items} used{Used}";
    }

    /// A producer of world labels (a Grok layer, the tapes, the survey, fall edges, part callouts, readings).
    public interface IWorldLabelSource
    {
        /// Append this producer's claims (none, or zero-count ones, when it shows nothing). Called on every reshare:
        /// no allocation, and the same number of claims ApplyLabels will read back.
        void ClaimLabels(List<LabelClaim> claims);

        /// The pool's answer for the claims appended, in order, at grants[first ...]: show the first Mandatory /
        /// Items labels, drop the rest to a dot or outline. Must not call WorldLabels.Changed.
        void ApplyLabels(LabelGrant[] grants, int first);
    }

    /// One pool of world labels across every overlay (declutter S10, DC7): at most 12 visible text labels and chips,
    /// shared out by class (safety → honesty and the live task → the active task → saved measurements → parts →
    /// readings → passive) and, within a class, newest first. Mandatory labels count first but are clipped to the pool
    /// too. Over the pool a label drops to its anchor dot or its outline, never to a smaller label. Freeze scope: every
    /// active label counts, not just what's in view (deterministic; in-view counting is W2.6). Producers call Changed
    /// when their labels appear, go or grow — never per frame — and the pool is shared out again at once
    /// (synchronous, so EditMode tests see the result). No allocation after warm-up.
    public static class WorldLabels
    {
        public const int Max = 12;

        // ---------------- the pure share-out ----------------

        static int[] s_Order = new int[32];

        /// Share `max` slots among `claims` into grants[0 .. claims.Count). Pure and deterministic: order by class,
        /// then newest first, then position in the list; mandatory labels first (clipped), then items in that order.
        /// Generalises LabelBudget.Split.
        public static void Allocate(IReadOnlyList<LabelClaim> claims, int max, LabelGrant[] grants)
        {
            int n = claims?.Count ?? 0;
            if (grants == null || grants.Length < n) throw new ArgumentException("grants needs one slot per claim");
            if (s_Order.Length < n) s_Order = new int[Math.Max(n, s_Order.Length * 2)];
            for (int i = 0; i < n; i++) { s_Order[i] = i; grants[i] = default; }
            // Insertion sort: n is small (a few claims per producer) and it allocates nothing.
            for (int i = 1; i < n; i++)
            {
                int k = s_Order[i];
                int j = i - 1;
                while (j >= 0 && Before(claims[k], k, claims[s_Order[j]], s_Order[j])) { s_Order[j + 1] = s_Order[j]; j--; }
                s_Order[j + 1] = k;
            }
            int left = Math.Max(0, max);
            for (int o = 0; o < n; o++)
            {
                int i = s_Order[o];
                int m = Math.Min(Math.Max(0, claims[i].Mandatory), left);
                grants[i].Mandatory = m;
                grants[i].Used = m;
                left -= m;
            }
            for (int o = 0; o < n && left > 0; o++)
            {
                int i = s_Order[o];
                var c = claims[i];
                int want = Math.Max(0, c.Items);
                if (want == 0) continue;
                if (c.CostCap > 0)
                {
                    int cost = Math.Min(want, c.CostCap);
                    int take = left >= cost ? want : 1;
                    int used = left >= cost ? cost : 1;
                    grants[i].Items = take;
                    grants[i].Used += used;
                    left -= used;
                }
                else
                {
                    int take = Math.Min(want, left);
                    grants[i].Items = take;
                    grants[i].Used += take;
                    left -= take;
                }
            }
        }

        /// The allocating form (tests, LabelBudget.Split).
        public static LabelGrant[] Allocate(IReadOnlyList<LabelClaim> claims, int max = Max)
        {
            var grants = new LabelGrant[claims?.Count ?? 0];
            Allocate(claims, max, grants);
            return grants;
        }

        static float Key(float t) => float.IsNaN(t) ? float.NegativeInfinity : t;

        static bool Before(in LabelClaim a, int ia, in LabelClaim b, int ib)
        {
            if (a.Class != b.Class) return a.Class < b.Class;
            float ta = Key(a.Newest), tb = Key(b.Newest);
            if (ta != tb) return ta > tb;
            return ia < ib;
        }

        // ---------------- the registry ----------------

        static readonly List<IWorldLabelSource> s_Sources = new List<IWorldLabelSource>(16);
        static readonly List<LabelClaim> s_Claims = new List<LabelClaim>(32);
        static readonly List<int> s_First = new List<int>(16);
        static LabelGrant[] s_Grants = new LabelGrant[32];
        static bool s_Busy, s_Again;

        /// Pool slots in use after the last share-out (≤ Max).
        public static int Used { get; private set; }
        /// Labels showing (a focused shape shows its whole set on a capped claim).
        public static int Shown { get; private set; }
        /// Labels the producers asked for.
        public static int Asked { get; private set; }
        /// Labels dropped to a dot or outline.
        public static int Dropped => Math.Max(0, Asked - Shown);
        /// Share-outs so far (the harness shows it: it must not climb every frame).
        public static int Reshares { get; private set; }
        public static int SourceCount => s_Sources.Count;
        /// switchclean: the producers registered now (the harness audits their claims after a world switch).
        public static IReadOnlyList<IWorldLabelSource> Sources => s_Sources;

        /// A producer's labels appeared, went or changed count: register it if it's new and share the pool out again.
        public static void Changed(IWorldLabelSource source)
        {
            if (source != null && !s_Sources.Contains(source)) s_Sources.Add(source);
            Reshare();
        }

        /// A producer leaves (disabled, destroyed): its labels stop counting and the others get the room.
        public static void Remove(IWorldLabelSource source)
        {
            if (source == null) return;
            int i = s_Sources.IndexOf(source);
            if (i < 0) return;
            s_Sources.RemoveAt(i);
            Reshare();
        }

        /// Share the pool out again now (the harness, before it counts).
        public static void Refresh() => Reshare();

        /// Forget every producer (tests; a Play session without a domain reload).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            s_Sources.Clear();
            s_Claims.Clear();
            s_First.Clear();
            Used = Shown = Asked = Reshares = 0;
            s_Busy = s_Again = false;
        }

        static bool Dead(IWorldLabelSource s) => s == null || (s is UnityEngine.Object o && o == null);

        static void Reshare()
        {
            if (s_Busy) { s_Again = true; return; }
            s_Busy = true;
            try
            {
                // A producer that changes while it applies its share gets one more round (bounded).
                for (int round = 0; round < 3; round++)
                {
                    s_Again = false;
                    Collect();
                    if (s_Grants.Length < s_Claims.Count) s_Grants = new LabelGrant[Math.Max(s_Claims.Count, s_Grants.Length * 2)];
                    Allocate(s_Claims, Max, s_Grants);
                    Totals();
                    for (int i = 0; i < s_Sources.Count; i++)
                    {
                        try { s_Sources[i].ApplyLabels(s_Grants, s_First[i]); }
                        catch (Exception ex) { Log.Warn($"World labels: {s_Sources[i].GetType().Name} couldn't apply its share: {ex.Message}"); }
                    }
                    Reshares++;
                    if (!s_Again) break;
                }
            }
            finally { s_Busy = false; }
        }

        static void Collect()
        {
            for (int i = s_Sources.Count - 1; i >= 0; i--) if (Dead(s_Sources[i])) s_Sources.RemoveAt(i);
            s_Claims.Clear();
            s_First.Clear();
            for (int i = 0; i < s_Sources.Count; i++)
            {
                int first = s_Claims.Count;
                s_First.Add(first);
                try { s_Sources[i].ClaimLabels(s_Claims); }
                catch (Exception ex)
                {
                    Log.Warn($"World labels: {s_Sources[i].GetType().Name} couldn't claim: {ex.Message}");
                    s_Claims.RemoveRange(first, s_Claims.Count - first);
                }
            }
        }

        static void Totals()
        {
            int used = 0, shown = 0, asked = 0;
            for (int i = 0; i < s_Claims.Count; i++)
            {
                used += s_Grants[i].Used;
                shown += s_Grants[i].Shown;
                asked += Math.Max(0, s_Claims[i].Mandatory) + Math.Max(0, s_Claims[i].Items);
            }
            Used = used;
            Shown = shown;
            Asked = asked;
        }

        /// One line for the harness and the census: "labels=9/12 shown=14 asked=30 dropped=16 | P0 3 · P1 4 · … |
        /// MeasureTool[P1 m0 i9 → i9 used4, …] …" (allocates: not for per-frame use).
        public static string Summary()
        {
            var perClass = new int[7];
            for (int i = 0; i < s_Claims.Count; i++)
            {
                int c = (int)s_Claims[i].Class;
                if (c >= 0 && c < perClass.Length) perClass[c] += s_Grants[i].Used;
            }
            var sb = new StringBuilder();
            sb.Append($"labels={Used}/{Max} shown={Shown} asked={Asked} dropped={Dropped} |");
            string[] names = { "safety", "focus", "task", "saved", "parts", "readings", "passive" };
            for (int c = 0; c < perClass.Length; c++) sb.Append($" P{c} {names[c]} {perClass[c]}{(c < perClass.Length - 1 ? " ·" : "")}");
            sb.Append(" |");
            for (int s = 0; s < s_Sources.Count && s < s_First.Count; s++)
            {
                int first = s_First[s];
                int end = s + 1 < s_First.Count ? s_First[s + 1] : s_Claims.Count;
                var src = s_Sources[s];
                sb.Append(' ').Append(Dead(src) ? "(gone)" : src is UnityEngine.Object uo ? $"{src.GetType().Name}:{uo.name}" : src.GetType().Name).Append('[');
                for (int i = first; i < end; i++)
                {
                    var c = s_Claims[i];
                    var g = s_Grants[i];
                    if (i > first) sb.Append(", ");
                    sb.Append($"P{(int)c.Class} m{c.Mandatory} i{c.Items} → m{g.Mandatory} i{g.Items} used{g.Used}");
                }
                sb.Append(']');
            }
            return sb.ToString();
        }
    }
}
