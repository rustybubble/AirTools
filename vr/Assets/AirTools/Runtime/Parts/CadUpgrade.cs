using System;

namespace AirTools.Parts
{
    /// What a CAD poll means for a part (CadUpgrade.Decide).
    public enum CadStep
    {
        /// Grok is still writing: poll again (the badge's time left refreshed).
        Wait,
        /// The CAD model is ready: swap it in (same anchor).
        Swap,
        /// It won't come (failed, no OpenSCAD, too long, the server stopped answering): stop, the badge says why.
        Stop,
        /// The part no longer shows the LLM+CAD template (Compare flipped it, the setting changed, it's CAD already).
        Drop,
    }

    /// cad: the upgrade from the LLM+CAD template to Grok's CAD model, pure (AssetModeSwitcher runs it). A placed or held
    /// part whose model came from the "llm_scad" mode while Grok was still writing (backend asset.cad.status "writing") is
    /// watched: part.json?mode=llm_scad every 10–15 s (sooner when the server's eta says it's nearly done), and when the
    /// answer's tier is "scad" the CAD model is swapped in through the usual swap path (anchor kept). Failures and a
    /// quiet server stop the watch; flipping the part to HF drops it.
    public static class CadUpgrade
    {
        public const float MinPoll = 10f;
        public const float MaxPoll = 15f;
        /// A run still "writing" after this long isn't waited for (grok-4.7 at high effort took up to ~7 min).
        public const float GiveUpAfter = 20f * 60f;
        /// Answers in a row without news (no answer, or cad "none") before giving up.
        public const int MaxQuiet = 4;

        /// The part shows the LLM+CAD template while Grok writes its CAD model: watch it.
        public static bool ShouldWatch(PartAsset shown) =>
            shown != null && shown.mode == "llm_scad" && shown.tier != "scad" && shown.cad != null && shown.cad.Writing;

        /// Seconds to the next poll: 10–15 s, the eta (+2 s) when it lands inside that window.
        public static float NextPoll(PartCad cad)
        {
            float eta = cad?.eta_s ?? MaxPoll;
            return Math.Min(MaxPoll, Math.Max(MinPoll, eta + 2f));
        }

        /// A poll's answer (`fresh`, null when the server didn't answer) for a part that shows `shown`, watched for
        /// `watchedFor` seconds with `quiet` answers in a row that brought no news.
        public static CadStep Decide(PartAsset shown, PartAsset fresh, int quiet, float watchedFor)
        {
            if (shown == null || shown.mode != "llm_scad" || shown.tier == "scad") return CadStep.Drop;
            if (fresh != null && fresh.tier == "scad" && fresh.Ready) return CadStep.Swap;
            if (watchedFor > GiveUpAfter) return CadStep.Stop;
            var cad = fresh?.cad;
            if (cad != null && cad.IsFailed) return CadStep.Stop;
            if (cad != null && (cad.Writing || cad.IsReady)) return CadStep.Wait;   // ready: its variant is being rebuilt (seconds)
            return Quiet(fresh) && quiet + 1 >= MaxQuiet ? CadStep.Stop : CadStep.Wait;
        }

        /// An answer without news: none at all, or no CAD status (an older server; "none": the request restarts it).
        public static bool Quiet(PartAsset fresh) => fresh == null || fresh.cad == null || fresh.cad.status == "none" || string.IsNullOrEmpty(fresh.cad.status);

        /// One line for the harness / log: "writing 2 min" · "ready" · "failed: OpenSCAD isn't installed on the server".
        public static string Describe(PartCad cad, float now)
        {
            if (cad == null) return "-";
            if (cad.Writing) return "writing " + AssetModes.Eta(AssetModes.CadSecondsLeft(cad, now));
            if (cad.IsFailed) return "failed: " + (cad.reason ?? "?");
            return cad.status ?? "-";
        }
    }
}
