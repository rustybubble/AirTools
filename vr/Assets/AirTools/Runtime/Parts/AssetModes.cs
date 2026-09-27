using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Parts
{
    /// settings-assets: the parts server's asset modes (backend docs/api.md "Asset modes") on the app side, pure:
    /// - the URLs: `?mode=hf|llm_scad|auto` on a part's part.json and model.glb (finish / size variants and other files
    ///   keep theirs);
    /// - the request bodies: `asset_mode` on POST /parts/search (the agent context adds its own, AgentContext);
    /// - the spec card's tier badge from asset.tier + made_by + note: "AI mesh · Hunyuan", "CAD · Grok 4.20 · OpenSCAD",
    ///   "Template", with a short reason when the preferred tier didn't make it ("Template · no OpenSCAD");
    /// - cad: while Grok writes the CAD model (asset.cad.status "writing") the template's badge says so with the time
    ///   left: "Template · Grok is writing the CAD model… 2 min" (CadUpgrade swaps the CAD model in when it lands);
    /// - Compare: HF ⇄ LLM+CAD (two-way: LLM+CAD already shows the template until its CAD model lands).
    public static class AssetModes
    {
        /// Settings ▸ 3D models' chips, left to right.
        public static readonly AssetMode[] Order = { AssetMode.Hf, AssetMode.LlmScad, AssetMode.Auto };

        /// "/parts/{id}/model.glb" or ".../part.json" (with or without a query, absolute or relative).
        public static bool IsModeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string path = url.Split('?')[0];
            return path.Contains("/parts/") && (path.EndsWith("/model.glb", StringComparison.Ordinal) || path.EndsWith("/part.json", StringComparison.Ordinal));
        }

        /// The URL with `mode=<wire>` (any mode already on it replaced); unchanged when it isn't a part's model.glb /
        /// part.json (a finish variant model-bronze.glb keeps the model it was made from).
        public static string WithMode(string url, AssetMode mode)
        {
            if (!IsModeUrl(url)) return url;
            int q = url.IndexOf('?');
            string path = q < 0 ? url : url.Substring(0, q);
            var keep = new List<string>();
            if (q >= 0)
                foreach (var kv in url.Substring(q + 1).Split('&'))
                    if (kv.Length > 0 && !kv.StartsWith("mode=", StringComparison.Ordinal)) keep.Add(kv);
            keep.Add("mode=" + UserPrefs.Wire(mode));
            return path + "?" + string.Join("&", keep);
        }

        public static string ModelUrl(string partId, AssetMode mode) => WithMode($"/parts/{partId}/model.glb", mode);
        public static string PartUrl(string partId, AssetMode mode) => WithMode($"/parts/{partId}/part.json", mode);

        /// POST /parts/search's body gets the mode (a server without asset modes ignores the field).
        public static Dictionary<string, object> AddTo(Dictionary<string, object> body, AssetMode mode)
        {
            if (body != null) body["asset_mode"] = UserPrefs.Wire(mode);
            return body;
        }

        /// The mode that made this model (asset.mode), null from a server without asset modes.
        public static AssetMode? ModeOf(PartAsset a)
        {
            string m = a?.mode;
            if (string.IsNullOrEmpty(m)) return null;
            // The server's own words first (no allocation: the card and the chip ask every 0.2 s).
            if (m == "hf") return AssetMode.Hf;
            if (m == "llm_scad") return AssetMode.LlmScad;
            if (m == "auto") return AssetMode.Auto;
            return UserPrefs.ParseAssetMode(m);
        }

        /// Compare: HF shows the LLM+CAD model next, anything else HF.
        public static AssetMode CompareTarget(AssetMode shown) => shown == AssetMode.Hf ? AssetMode.LlmScad : AssetMode.Hf;

        /// What made the model, in two to four words: "AI mesh · Hunyuan", "CAD · Grok 4.20 · OpenSCAD", "Template",
        /// "Maker's CAD", "Box".
        public static string Badge(PartAsset a)
        {
            string tier = (a?.tier ?? "").Trim().ToLowerInvariant();
            string by = (a?.made_by ?? "").ToLowerInvariant();
            switch (tier)
            {
                case "ai_mesh": return by.Contains("hunyuan") || by.StartsWith("hf:", StringComparison.Ordinal) || by.Length == 0 ? "AI mesh · Hunyuan" : "AI mesh";
                case "scad": return CadBadge(a?.made_by);
                case "llm": return "Template";
                case "cad": return "Maker's CAD";
                case "library": return "Library model";
                case "proxy": return "Box";
                default: return "Model";
            }
        }

        // cad: the last made_by → badge (the card asks every 0.2 s; one model per scene part in practice).
        static string s_CadBy, s_CadBadge;

        /// "CAD · Grok 4.20 · OpenSCAD" from made_by "xai:grok-4.20-0309-non-reasoning + openscad" ("xai:grok-4.7 + openscad"
        /// → "CAD · Grok 4.7 · OpenSCAD"; another writer → "CAD · <model> · OpenSCAD"; none → "CAD · Grok · OpenSCAD").
        public static string CadBadge(string madeBy)
        {
            if (madeBy == s_CadBy && s_CadBadge != null) return s_CadBadge;
            s_CadBy = madeBy;
            return s_CadBadge = "CAD · " + WriterName(madeBy) + " · OpenSCAD";
        }

        /// The model that wrote the OpenSCAD, short: "Grok 4.20", "Grok 4.7", "Grok"; another provider's model id as is.
        public static string WriterName(string madeBy)
        {
            string by = (madeBy ?? "").Trim();
            int plus = by.IndexOf('+');
            if (plus >= 0) by = by.Substring(0, plus).Trim();
            int colon = by.IndexOf(':');
            string model = colon >= 0 ? by.Substring(colon + 1) : by;
            string lower = model.ToLowerInvariant();
            int g = lower.IndexOf("grok", StringComparison.Ordinal);
            if (g < 0) return model.Length == 0 || lower == "openscad" ? "Grok" : model;
            int i = g + 4;
            while (i < lower.Length && (lower[i] == '-' || lower[i] == ' ')) i++;
            int start = i;
            while (i < lower.Length && (char.IsDigit(lower[i]) || lower[i] == '.')) i++;
            string version = lower.Substring(start, i - start).TrimEnd('.');
            return version.Length > 0 ? "Grok " + version : "Grok";
        }

        /// Seconds left of Grok's run as the card should show it now: eta_s counted down from when the answer arrived.
        public static float CadSecondsLeft(PartCad cad, float now)
        {
            if (cad == null || !cad.eta_s.HasValue) return -1f;
            float gone = cad.receivedAt > 0f && now >= cad.receivedAt ? now - cad.receivedAt : 0f;
            return Math.Max(0f, cad.eta_s.Value - gone);
        }

        /// "almost done" (≤ 10 s), "40 s" (to 5 s), "1 min", "2 min"; "" when there's no estimate.
        public static string Eta(float seconds)
        {
            if (seconds < 0f) return "";
            if (seconds <= 10f) return "almost done";
            if (seconds < 60f) return $"{(int)(Math.Ceiling(seconds / 5f) * 5f)} s";
            return $"{Math.Max(1, (int)Math.Round(seconds / 60f))} min";
        }

        /// "Grok is writing the CAD model… 2 min" while asset.cad says writing and the template stands in; null otherwise.
        public static string CadLine(PartAsset a, float now)
        {
            var cad = a?.cad;
            if (cad == null || !cad.Writing || a.tier == "scad" || a.mode != "llm_scad") return null;
            string eta = Eta(CadSecondsLeft(cad, now));
            return eta.Length == 0 ? "Grok is writing the CAD model…" : "Grok is writing the CAD model… " + eta;
        }

        /// Why the preferred tier didn't make it, in a few words (asset.note → "no OpenSCAD"); null when it did.
        public static string ShortNote(PartAsset a)
        {
            string n = a?.note;
            if (string.IsNullOrWhiteSpace(n)) return null;
            string l = n.ToLowerInvariant();
            if (l.Contains("openscad isn't installed")) return "no OpenSCAD";
            if (l.Contains("still writing")) return "CAD on its way";
            if (l.Contains("didn't build")) return "CAD failed";
            if (l.Contains("quota")) return "HF quota spent";
            if (l.Contains("didn't match")) return "HF shape off";
            if (l.Contains("didn't answer")) return "HF no answer";
            if (l.Contains("no product photo")) return "no photo";
            if (l.Contains("offline")) return "offline";
            int colon = n.IndexOf(':');
            return colon > 0 ? n.Substring(0, colon) : n;
        }

        /// The spec card's tier line for a server that has asset modes: "AI mesh · Hunyuan", "Template · no OpenSCAD",
        /// "Template · Grok is writing the CAD model… 2 min"; null for an older server's asset (the card keeps
        /// Copy.TierLabel).
        public static string TierLine(PartAsset a) => TierLine(a, a?.cad != null && a.cad.receivedAt > 0f ? AppNow() : 0f);

        // Out of line: the offline test runner can't JIT a method that touches the engine's clock.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static float AppNow() => Time.realtimeSinceStartup;

        /// TierLine at app time `now` (the CAD countdown; tests pass it).
        public static string TierLine(PartAsset a, float now)
        {
            if (ModeOf(a) == null) return null;
            string cad = CadLine(a, now);
            if (cad != null) return $"{Badge(a)} · {cad}";
            string note = ShortNote(a);
            return note != null ? $"{Badge(a)} · {note}" : Badge(a);
        }

        /// The toast after a swap: "3D model: AI mesh · Hunyuan".
        public static string SwappedLine(PartAsset a) => "3D model: " + (TierLine(a) ?? Badge(a));
    }
}
