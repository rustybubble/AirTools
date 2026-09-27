using UnityEngine;

namespace AirTools.Core
{
    /// The kinds of animated mode change the <see cref="TransitionDirector"/> plays (presence.md S1).
    public enum TransitionKind
    {
        None,
        /// Passthrough → World: the world pours out of a sphere 1.2 m ahead, then the sky closes over the room.
        PourIn,
        /// World → Passthrough: the mirror of PourIn.
        PourOut,
        /// Tabletop → World: the 1:50 model grows around you, anchored under your feet; the sky closes last.
        GrowIn,
        /// World → Tabletop: the sky opens, then the building shrinks back onto the table.
        ShrinkOut,
        /// Passthrough → Tabletop: the model scans in on the table.
        Materialize,
        /// Tabletop → Passthrough: the model scans out.
        Dematerialize,
    }

    /// What a transition is doing right now (TransitionStatus / tests).
    public enum TransitionPhase { Idle, Pour, Grow, Sky, Shrink, Scan }

    /// Pure maths for the grow / pour transitions: easing, the log-linear scale, the anchored grow pose, and the
    /// reveal schedules. No Unity state, so every curve is EditMode-testable.
    public static class TransitionMath
    {
        // ---------------- durations (s) ----------------
        public const float PourSeconds = 1.2f;
        public const float GrowSeconds = 1.4f;
        /// Shrink-out: the sky opens first, then the scale runs.
        public const float ShrinkSkySeconds = 0.4f;
        public const float ShrinkScaleSeconds = 1.2f;
        public const float ScanSeconds = 0.8f;
        public const float UnscanSeconds = 0.6f;

        /// Grow: the room stays visible for the first 60 %, then the sky closes.
        public const float GrowSkyStart = 0.6f;
        /// Pour: the sky starts closing once the nearby building is in (R ≈ 6 m).
        public const float PourSkyStart = 0.35f;

        /// Pour-in sphere: centre this far ahead of the eyes; radius 0.05 → 90 m.
        public const float PourAhead = 1.2f;
        public const float PourMaxRadius = 90f;
        public const float BandMetres = 0.04f;

        public static float Duration(TransitionKind kind) => kind switch
        {
            TransitionKind.PourIn or TransitionKind.PourOut => PourSeconds,
            TransitionKind.GrowIn => GrowSeconds,
            TransitionKind.ShrinkOut => ShrinkSkySeconds + ShrinkScaleSeconds,
            TransitionKind.Materialize => ScanSeconds,
            TransitionKind.Dematerialize => UnscanSeconds,
            _ => 0f,
        };

        /// Smootherstep: e(t) = t³(t(6t − 15) + 10), clamped to [0, 1].
        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t * (t * (6f * t - 15f) + 10f);
        }

        /// Cubic smoothstep of t in [a, b] (0 before a, 1 after b).
        public static float Smooth(float a, float b, float t)
        {
            if (b <= a) return t >= b ? 1f : 0f;
            float x = Mathf.Clamp01((t - a) / (b - a));
            return x * x * (3f - 2f * x);
        }

        /// Log-linear scale between the table and world scales: s = s_table^(1−e) · s_world^e (constant zoom speed).
        public static float ScaleAt(float e, float tableScale, float worldScale)
        {
            e = Mathf.Clamp01(e);
            return Mathf.Pow(Mathf.Max(tableScale, 1e-6f), 1f - e) * Mathf.Pow(Mathf.Max(worldScale, 1e-6f), e);
        }

        /// The SceneRoot-local point that ends up under the feet at 1:1: inverse(worldPose) · feet.
        public static Vector3 AnchorLocal(Pose worldPose, float worldScale, Vector3 feetWorld) =>
            Quaternion.Inverse(worldPose.rotation) * (feetWorld - worldPose.position) / Mathf.Max(worldScale, 1e-6f);

        /// Where the anchor is in the world at eased progress e: from its spot on the table model to the feet.
        public static Vector3 AnchorWorld(float e, Pose tablePose, float tableScale, Vector3 anchorLocal, Vector3 feetWorld) =>
            Vector3.Lerp(tablePose.position + tablePose.rotation * (anchorLocal * tableScale), feetWorld, Mathf.Clamp01(e));

        /// The SceneRoot pose and uniform scale at eased progress e (0 = on the table, 1 = at full size where it was).
        /// rot = slerp(table, world, e); pos = anchorWorld − rot · (anchorLocal · s). Exactly the table pose at 0 and
        /// exactly the world pose at 1.
        public static Pose GrowPose(float e, Pose tablePose, float tableScale, Pose worldPose, float worldScale,
            Vector3 anchorLocal, Vector3 feetWorld, out float scale)
        {
            e = Mathf.Clamp01(e);
            scale = ScaleAt(e, tableScale, worldScale);
            var rot = Quaternion.Slerp(tablePose.rotation, worldPose.rotation, e);
            var anchor = AnchorWorld(e, tablePose, tableScale, anchorLocal, feetWorld);
            return new Pose(anchor - rot * (anchorLocal * scale), rot);
        }

        // ---------------- reveal schedules ----------------

        /// Pour-in sphere radius at progress t (0..1): R = 0.05 + 90 · t^2.2 m.
        public static float PourRadius(float t) => 0.05f + PourMaxRadius * Mathf.Pow(Mathf.Clamp01(t), 2.2f);

        /// The accent scan band grows with the sphere so it stays visible far away: 0.04 · max(1, R / 5) m.
        public static float PourBand(float radius) => BandMetres * Mathf.Max(1f, radius / 5f);

        /// Sky closing angle (radians from the reveal direction, 0 = none, π = the whole dome) at progress t for a sky
        /// that starts closing at `start`.
        public static float SkyAngle(float t, float start) => Mathf.PI * Smooth(start, 1f, t);

        /// Grow sky, as the spec's sphere schedule (centre = feet + 1.6 up): R = t &lt; 0.6 ? 0 : 90 · smoothstep((t − 0.6)/0.4).
        public static float GrowSkyRadius(float t) => t < GrowSkyStart ? 0f : PourMaxRadius * Smooth(GrowSkyStart, 1f, t);

        /// Scan-in radius around the model on the table (0 → the model's radius) at progress t.
        public static float ScanRadius(float t, float modelRadius) => modelRadius * Ease(t);

        /// The reveal sphere test the SceneReveal shader does (clip(R − d)): inside or on the surface.
        public static bool InsideSphere(Vector3 p, Vector3 centre, float radius) => radius - Vector3.Distance(p, centre) >= 0f;

        /// Band glow the shader uses at a visible point: 1 on the sphere's surface, 0 one band-width inside.
        public static float BandGlow(Vector3 p, Vector3 centre, float radius, float band) =>
            Mathf.Clamp01(1f - (radius - Vector3.Distance(p, centre)) / Mathf.Max(band, 1e-5f));

        /// A sky dome direction is revealed when it is within `angle` of the reveal direction.
        public static bool InsideCone(Vector3 dir, Vector3 revealDir, float angle) =>
            angle >= Mathf.PI - 1e-4f || Vector3.Angle(dir, revealDir) * Mathf.Deg2Rad <= angle;
    }
}
