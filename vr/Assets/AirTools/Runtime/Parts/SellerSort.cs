using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AirTools.Parts
{
    /// Seller list ordering (SPEC M5; voice: "cheapest", "arrives soonest"). Pure logic.
    public static class SellerSort
    {
        public const string ByPrice = "price", ByEta = "eta";

        /// "price" (cheapest delivered first), "eta" (soonest first); anything else keeps the listing order.
        /// Returns seller indices into spec.sellers. Out-of-stock sellers go last.
        public static List<int> Order(PartSpec spec, string sort, DayOfWeek today)
        {
            var idx = new List<int>();
            for (int i = 0; i < spec.sellers.Count; i++) idx.Add(i);
            string s = Normalise(sort);
            idx.Sort((a, b) =>
            {
                var x = spec.sellers[a]; var y = spec.sellers[b];
                int stock = y.in_stock.CompareTo(x.in_stock);
                if (stock != 0) return stock;
                int c = 0;
                if (s == ByPrice) c = Delivered(x).CompareTo(Delivered(y));
                else if (s == ByEta) c = (x.eta_days ?? EtaDays(x.eta, today)).CompareTo(y.eta_days ?? EtaDays(y.eta, today));
                if (c == 0 && s == ByEta) c = Delivered(x).CompareTo(Delivered(y));
                return c != 0 ? c : a.CompareTo(b);
            });
            return idx;
        }

        public static string Normalise(string sort)
        {
            var t = (sort ?? "").Trim().ToLowerInvariant();
            if (t.Contains("cheap") || t.Contains("price") || t.Contains("cost")) return ByPrice;
            if (t.Contains("eta") || t.Contains("soon") || t.Contains("fast") || t.Contains("arriv") || t.Contains("deliver")) return ByEta;
            return "";
        }

        /// Price plus shipping ("free" = 0, "$5.99" = 5.99). Uses the server's total_usd when it sent one.
        public static float Delivered(PartSeller s) => s.total_usd ?? (s.price_usd + Shipping(s));

        /// Shipping cost: the structured shipping_usd, else parsed from the text.
        public static float Shipping(PartSeller s) => s.shipping_usd ?? Shipping(s.shipping);

        public static float Shipping(string shipping)
        {
            if (string.IsNullOrWhiteSpace(shipping)) return 0f;
            var m = Regex.Match(shipping, @"[0-9]+(\.[0-9]+)?");
            return m.Success ? float.Parse(m.Value, CultureInfo.InvariantCulture) : 0f;
        }

        static readonly string[] s_Days = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        /// Days until delivery: a weekday name ("Tue" = the next Tuesday, 7 if today), "today"/"tomorrow", or the first
        /// number in "2 days" / "3–5 days". Unknown = 99.
        public static int EtaDays(string eta, DayOfWeek today)
        {
            var t = (eta ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("today")) return 0;
            if (t.StartsWith("tomorrow")) return 1;
            for (int d = 0; d < 7; d++)
                if (t.StartsWith(s_Days[d]))
                {
                    int diff = (d - (int)today + 7) % 7;
                    return diff == 0 ? 7 : diff;
                }
            var m = Regex.Match(t, @"[0-9]+");
            return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 99;
        }
    }
}
