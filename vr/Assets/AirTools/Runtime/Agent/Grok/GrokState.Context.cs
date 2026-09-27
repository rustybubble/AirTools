using System;
using System.Reflection;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// State the Grok lanes share (backend integration/grok-all). A partial class: each lane keeps its members in its own
    /// file (G2 SurveyId / PlanId, G3 safety verdicts and links, G4 coach and drill state; this file: G1).
    public static partial class GrokState
    {
        // ---------------- Grok G1: context settings (docs/api.md §6) ----------------

        public const string DefaultLocation = "Atlanta, GA";
        const string LocationKey = "airtools.grok.location", AddressKey = "airtools.grok.address";
        static string s_Location, s_Address;

        /// context.location: free text the installer finder and the rules check use when the user names no place.
        public static string Location
        {
            get => s_Location ??= Pref(LocationKey, DefaultLocation);
            set { s_Location = string.IsNullOrWhiteSpace(value) ? DefaultLocation : value.Trim(); SetPref(LocationKey, s_Location); }
        }

        /// context.address: the job's street address for the rules check (Census picks the jurisdiction). Empty = none.
        public static string Address
        {
            get => s_Address ??= Pref(AddressKey, "");
            set { s_Address = (value ?? "").Trim(); SetPref(AddressKey, s_Address); }
        }

        /// context.placement: optional words for where the part goes ("replacing the steel sink"). Not persisted.
        public static string Placement { get; set; }

        static string Pref(string key, string fallback)
        {
            try { return PlayerPrefs.GetString(key, fallback); }
            catch (Exception) { return fallback; }   // outside the player (offline tests)
        }

        static void SetPref(string key, string value)
        {
            try { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); }
            catch (Exception) { }
        }

        /// Forget G1's session state (the location / address settings stay on the headset).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetG1()
        {
            Placement = null;
            s_Location = null;
            s_Address = null;
        }

        // ---------------- end Grok G1 ----------------
    }

    /// The other lanes' GrokState members G1 reads or writes, looked up by name, so this lane builds and runs with or
    /// without them merged (missing = nothing known): G2 SurveyId (context.survey_id); G3 VerdictFor / SafetyFor(..)
    /// .Headline / SetSafety / SafetyChanged (the checkout's recall line, the receipt's verdict); G4 CoachActive / CoachId
    /// (frame_jpg_b64 with every command while a coach runs), DrillActive / DrillPoint / DrillPx (drill_px on a drill
    /// step) and LastFrameId (the frame sent, for coach_check boxes). Once all four lanes are merged these can become
    /// direct member accesses.
    public static class GrokLanes
    {
        const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;

        static object Get(string name)
        {
            var t = typeof(GrokState);
            var f = t.GetField(name, Pub);
            if (f != null) return f.GetValue(null);
            var p = t.GetProperty(name, Pub);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(null) : null;
        }

        static bool Set(string name, object value)
        {
            var t = typeof(GrokState);
            var f = t.GetField(name, Pub);
            if (f != null && !f.IsInitOnly && !f.IsLiteral) { f.SetValue(null, value); return true; }
            var p = t.GetProperty(name, Pub);
            if (p != null && p.CanWrite) { p.SetValue(null, value); return true; }
            return false;
        }

        static object Call(string name, params object[] args)
        {
            var types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = typeof(string);
            var m = typeof(GrokState).GetMethod(name, Pub, null, types, null);
            return m?.Invoke(null, args);
        }

        // G2
        public static string SurveyId => Get("SurveyId") as string;

        // G3
        public static bool HasSafety => typeof(GrokState).GetMethod("VerdictFor", Pub, null, new[] { typeof(string) }, null) != null;
        public static string Verdict(string partId) => partId == null ? null : Call("VerdictFor", partId) as string;
        public static bool IsRecalled(string partId) => Verdict(partId) == "recalled";

        public static string Headline(string partId)
        {
            var v = partId == null ? null : Call("SafetyFor", partId);
            if (v == null) return null;
            var t = v.GetType();
            return (t.GetField("Headline")?.GetValue(v) ?? t.GetProperty("Headline")?.GetValue(v)) as string;
        }

        public static void SetSafety(string partId, string verdict, string headline) => Call("SetSafety", partId, verdict, headline);

        /// Subscribe / unsubscribe to G3's SafetyChanged(partId). False when G3 isn't there.
        public static bool OnSafetyChanged(Action<string> handler, bool subscribe)
        {
            var e = typeof(GrokState).GetEvent("SafetyChanged", Pub);
            if (e == null || handler == null || e.EventHandlerType != typeof(Action<string>)) return false;
            if (subscribe) e.AddEventHandler(null, handler); else e.RemoveEventHandler(null, handler);
            return true;
        }

        // G4
        public static bool CoachActive => (Get("CoachActive") is bool b && b) || !string.IsNullOrEmpty(Get("CoachId") as string);
        public static bool DrillActive => Get("DrillActive") is bool b && b;

        /// drill_px for a capture photo on a drill step (G4's GrokState.DrillPxIn for a scene camera): the crosshair's
        /// scene point (package space) projected into it and clamped to 0–1; G4's own [x, y] when the crosshair isn't on
        /// the scene; null when no drill step is up or the point is behind that camera.
        public static float[] DrillPxIn(AirTools.Scene.SceneCameraJson cam)
        {
            if (!DrillActive) return null;
            if (!(Get("DrillPoint") is Vector3 point)) return Get("DrillPx") as float[];
            return GrokContext.PointInFrame(cam, point, clamp: true);
        }

        /// The frame last sent as frame_jpg_b64 (G4 draws coach_check / coach_stop boxes from it).
        public static string LastFrameId
        {
            get => Get("LastFrameId") as string;
            set => Set("LastFrameId", value);
        }
    }
}
