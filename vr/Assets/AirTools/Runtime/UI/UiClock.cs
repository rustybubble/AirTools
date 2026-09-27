using System;
using UnityEngine;

namespace AirTools.UI
{
    /// The heads-up line's clock (declutter S3): unscaled time, or a test's own. EditMode tests can't advance
    /// Time.unscaledTime, so they set Override to step flashes and replies through their durations.
    public static class UiClock
    {
        public static Func<float> Override;

        public static float Now => Override != null ? Override() : Time.unscaledTime;
    }
}
