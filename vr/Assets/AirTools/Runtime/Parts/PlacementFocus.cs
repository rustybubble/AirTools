using System.Collections.Generic;
using AirTools.UI;

namespace AirTools.Parts
{
    /// While a part is being adjusted, the rest of the world's text steps back (the way Model view focuses the scene): this
    /// claims the whole world-label pool (WorldLabels, declutter §5) as one Safety-class claim that counts after every
    /// real safety label, so the coach's stop card and fall-edge labels still show and everything else — tape pills,
    /// other parts' callouts, Grok captions, scene labels — drops to its dot or outline. Lines and outlines stay.
    /// One flag through the one pool: when Model view's central suppression lands (modelview lane: a "ModelView" switch in
    /// WorldLabels), On can map straight onto it.
    public sealed class PlacementFocus : IWorldLabelSource
    {
        public static readonly PlacementFocus Instance = new PlacementFocus();

        public bool On { get; private set; }

        public void Set(bool on)
        {
            On = on;
            if (on) WorldLabels.Changed(this);   // registers (again, after a WorldLabels.Reset) and re-shares
            else WorldLabels.Remove(this);
        }

        public void ClaimLabels(List<LabelClaim> claims)
        {
            // Oldest possible safety claim: real safety labels (newer) are served first, then this takes the rest.
            if (On) claims.Add(new LabelClaim(LabelClass.Safety, WorldLabels.Max, 0, float.NegativeInfinity));
        }

        public void ApplyLabels(LabelGrant[] grants, int first) { }
    }
}
