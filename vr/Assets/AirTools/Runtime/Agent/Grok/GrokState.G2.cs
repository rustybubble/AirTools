namespace AirTools.Agent.Grok
{
    /// Grok integration state other lanes read (backend docs/api.md §6 context object). Each lane keeps its fields in
    /// one marked block; nothing here touches the scene.
    public static partial class GrokState
    {
        // Grok G2 (3D overlays) ---------------------------------------------------------------------------------------
        /// The condition survey whose pins are on screen (show_survey, or a polled survey): context.survey_id, so "find
        /// a fix for pin f1" names the right survey. Null until a survey is shown.
        public static string SurveyId;

        /// The placement plan on screen (show_plan). place_array {plan_id} places at its points only when it matches.
        public static string PlanId;

        /// Play mode keeps statics (no domain reload): forget the G2 fields when a session starts.
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetG2()
        {
            SurveyId = null;
            PlanId = null;
        }
        // end Grok G2 -------------------------------------------------------------------------------------------------
    }
}
