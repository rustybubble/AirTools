using UnityEngine;

namespace AirTools.Parts
{
    public struct ArrayPlan
    {
        public int Count;
        public float SpacingM;
        public float LengthM;
        public Vector3 Direction;
        public Vector3[] Positions;
    }

    /// Where an array of parts goes (SPEC M5): along the last tape segment at spacing_mm, quantity ⌊L/s⌋ + 1,
    /// centred on the run. The row runs parallel to the tape through the reference part, so it keeps that part's
    /// offset from the tape (hangers stay on the fascia above a gutter tape). A run within LevelDeg of level is
    /// level: its ends often snap to different corners of a gutter lip (top at one end, bottom at the other), and a
    /// row following that slope drifts off the fascia into the gutter.
    public static class ArrayPlanner
    {
        /// Tape noise must not drop a part: a run within 3% of a spacing short of the next whole count still gets it.
        public const float Tolerance = 0.03f;

        /// Runs this close to level (degrees) are treated as level. A 12.7 cm gutter lip taped corner to opposite
        /// corner over 2.4 m is 3.0°; deliberate slopes on walls (ramp rails 4.8°, stairs 30°+) stay above it.
        public const float LevelDeg = 3f;

        public static int Quantity(float lengthM, float spacingM) =>
            spacingM <= 1e-4f ? 1 : Mathf.FloorToInt(lengthM / spacingM + Tolerance) + 1;

        public static ArrayPlan Plan(Vector3 tapeA, Vector3 tapeB, Vector3 reference, Vector3 surfaceNormal, float spacingM) =>
            Plan(tapeA, tapeB, reference, surfaceNormal, spacingM, Vector3.up);

        /// `up`: the scene's up (the tabletop model stays upright, so world up in practice).
        public static ArrayPlan Plan(Vector3 tapeA, Vector3 tapeB, Vector3 reference, Vector3 surfaceNormal, float spacingM, Vector3 up)
        {
            var run = tapeB - tapeA;
            var dir = surfaceNormal.sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(run, surfaceNormal) : run;
            if (dir.sqrMagnitude < 1e-8f) dir = run;
            dir = dir.normalized;
            up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            float rise = Vector3.Dot(dir, up);
            if (Mathf.Abs(rise) < Mathf.Sin(LevelDeg * Mathf.Deg2Rad))
            {
                var level = dir - up * rise;
                if (level.sqrMagnitude > 1e-8f) dir = level.normalized;
            }
            float length = Vector3.Dot(run, dir);
            if (length < 0f) { dir = -dir; length = -length; (tapeA, tapeB) = (tapeB, tapeA); }
            int n = Quantity(length, spacingM);
            float margin = (length - (n - 1) * spacingM) * 0.5f;
            // The row's t = 0 is the tape start projected onto the line through the reference part.
            var start = reference - dir * Vector3.Dot(reference - tapeA, dir);
            var pos = new Vector3[n];
            for (int k = 0; k < n; k++) pos[k] = start + dir * (margin + k * spacingM);
            return new ArrayPlan { Count = n, SpacingM = spacingM, LengthM = length, Direction = dir, Positions = pos };
        }
    }
}
