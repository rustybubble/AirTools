using UnityEngine;

namespace AirTools.Scene
{
    /// modelwheel: where Model view puts things (pure: the offline runner tests it; rotations are built from their
    /// components, no engine calls). The user's call (Sat 09-26): the model floats centred in your view, not on a table
    /// at your waist, and the switcher is a wide endless wheel right under it, facing your face.
    /// - The model: its bounding box centred straight ahead at eye height (a few cm below), ~1 m away, fitted so it spans
    ///   ~40–50° of view (a round 1:N with its longest side ≤ 0.9 m). Placed once from where you stand and look (the
    ///   anchor: your eye and flat heading), world-locked; it doesn't follow your head. A very tall model (a tower) rises
    ///   just enough that the wheel still fits under it within 34° of the eye line.
    /// - The wheel: its lens (the centre card) under the model's lowest visible edge, `WheelDistance` from the eye along
    ///   that line of sight, the wheel's plane square to it (tilted back toward the eyes), 16–34° below the eye line.
    /// - Recentre: when you've walked more than 1.5 m from where it was placed and the model is more than 1.5 m away, or
    ///   the model is more than 60° off where you're facing, for RecentreDwell seconds.
    public static class ModelViewLayout
    {
        /// The model's centre: this far ahead (flat) by default; pushed back so its near edge stays NearClearance ahead;
        /// never nearer than MinDistance or further than MaxDistance.
        public const float Distance = 1.0f, MinDistance = 0.9f, MaxDistance = 1.2f, NearClearance = 0.55f;
        /// The model's centre sits this far below the eye.
        public const float DropBelowEye = 0.06f;
        /// A floating model's longest side (≈ 0.67–0.9 m at 1 m: 37–48° of view).
        public const float FitMax = 0.9f;

        /// The wheel: the lens this far from the eye (0.85 m keeps the arc's settled cards within 35° of azimuth even
        /// when it's 34° down: the plane's lower parts come toward you); 16–34° below the eye line; its top this many
        /// degrees under the model's lowest visible edge. On a table (the other placement) the lens sits
        /// TableWheelDownDeg below the eye.
        public const float WheelDistance = 0.85f, WheelMinDownDeg = 16f, WheelMaxDownDeg = 34f, WheelGapDeg = 3f, TableWheelDownDeg = 24f;
        /// The lens card's top above the lens centre (m; ModelWheel.LensTop as built: 0.118 / 2 × 1.12).
        public const float WheelLensTop = 0.066f;
        /// The wheel's circle (m) and its cards' spacing and visible half arc (degrees): five cards 20° apart show, the
        /// ones at ±60° fade in and out (a ~110° arc).
        public const float WheelRadius = 0.55f, WheelSpacingDeg = 20f, WheelVisibleHalfDeg = 54f, WheelFadeBandDeg = 12f;
        /// A pinch that moves less than this on the wheel's plane (m) and ends within the time is a tap (a ray's hit
        /// point wobbles more than a fingertip at the ring's 3 cm).
        public const float WheelTapMove = 0.03f, WheelTapSeconds = 0.4f;

        /// Recentre: walked this far (m) from where it was placed and the model is this far away; or the model this many
        /// degrees off your heading; for this long (s).
        public const float RecentreWalk = 1.5f, RecentreTurnDeg = 60f, RecentreDwell = 0.75f;
        /// Bought parts ("take it home") stand on the floor at least this far right of the model's centre (their left
        /// edge ≥ ~37° of azimuth at 1 m: clear of the wheel's right end at ≤ 35°).
        public const float PartsClearance = 0.7f;

        public enum Recentre { None, Walked, Turned }

        // ---------------- the anchor ----------------

        /// Your flat heading (degrees about up, 0 = +Z, 90 = +X) from a view direction; `fallbackDeg` when you look
        /// (nearly) straight up or down.
        public static float HeadingDeg(Vector3 forward, float fallbackDeg = 0f)
        {
            var f = new Vector3(forward.x, 0f, forward.z);
            return f.sqrMagnitude > 1e-4f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : fallbackDeg;
        }

        /// The flat unit direction of a heading.
        public static Vector3 Dir(float headingDeg)
        {
            float r = headingDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }

        /// Rotation of `deg` about world up, then `downDeg` about the local right (pitch down): the frame of a surface
        /// facing the eye along that line (Quaternion.Euler(downDeg, deg, 0), built without an engine call).
        public static Quaternion YawPitch(float deg, float downDeg)
        {
            float h = deg * 0.5f * Mathf.Deg2Rad, p = downDeg * 0.5f * Mathf.Deg2Rad;
            var yaw = new Quaternion(0f, Mathf.Sin(h), 0f, Mathf.Cos(h));
            var pitch = new Quaternion(Mathf.Sin(p), 0f, 0f, Mathf.Cos(p));
            return yaw * pitch;
        }

        // ---------------- the model ----------------

        /// How far ahead (flat) the model's centre goes for a half-depth (m, at its scale).
        public static float CentreDistance(float halfDepth) =>
            Mathf.Clamp(Mathf.Max(Distance, Mathf.Max(0f, halfDepth) + NearClearance), MinDistance, MaxDistance);

        /// The steepest the model's lowest visible edge may sit below the eye line so the wheel still fits under it
        /// (≈ 26°: 34° less the gap and the lens card's top).
        public static float MaxModelBottomDeg =>
            WheelMaxDownDeg - WheelGapDeg - Mathf.Atan2(WheelLensTop, WheelDistance) * Mathf.Rad2Deg;

        /// How far below the eye the model's centre goes (m): DropBelowEye, less for a model so tall its bottom would
        /// be steeper than MaxModelBottomDeg (negative: above the eye).
        public static float Drop(Vector3 halfSize)
        {
            float near = Mathf.Max(0.05f, CentreDistance(halfSize.z) - Mathf.Max(0f, halfSize.z));
            float room = near * Mathf.Tan(MaxModelBottomDeg * Mathf.Deg2Rad) - Mathf.Max(0f, halfSize.y);
            return Mathf.Min(DropBelowEye, room);
        }

        /// Where the model's bounding-box centre goes: straight ahead of the anchor eye, Drop under it.
        public static Vector3 ModelCentre(Vector3 eye, float headingDeg, Vector3 halfSize) =>
            eye + Dir(headingDeg) * CentreDistance(halfSize.z) + Vector3.down * Drop(halfSize);

        /// The scene root's pose that puts the model's box centre (`boxCentreLocal`, scene-root space) at ModelCentre at
        /// `scale` (`halfSize`: its half extents at that scale), its front (+Z, the side the scan was viewed from) toward
        /// you. `centre` is where the box centre lands.
        public static Pose ModelPose(Vector3 eye, float headingDeg, Vector3 boxCentreLocal, float scale, Vector3 halfSize, out Vector3 centre)
        {
            centre = ModelCentre(eye, headingDeg, halfSize);
            var rot = TabletopFit.Yaw(headingDeg + 180f);
            return new Pose(centre - rot * (boxCentreLocal * scale), rot);
        }

        /// Degrees below the eye line of the model's lowest visible edge: its bottom at its near side (a box `halfSize`
        /// around `centre`, square to your heading).
        public static float ModelBottomDownDeg(Vector3 eye, float headingDeg, Vector3 centre, Vector3 halfSize)
        {
            var flat = new Vector3(centre.x - eye.x, 0f, centre.z - eye.z);
            float ahead = Vector3.Dot(flat, Dir(headingDeg)) - Mathf.Max(0f, halfSize.z);
            float below = eye.y - (centre.y - Mathf.Max(0f, halfSize.y));
            return Mathf.Atan2(below, Mathf.Max(0.05f, ahead)) * Mathf.Rad2Deg;
        }

        // ---------------- the wheel ----------------

        /// How far below the eye line the wheel's lens goes: its top (`lensTop` m above the lens centre, in its plane)
        /// WheelGapDeg under the model's lowest edge, at least `minDownDeg` (16°; lower under a live status line,
        /// StatusClearDownDeg), at most 34°.
        public static float WheelDownDeg(float modelBottomDownDeg, float lensTop, float distance = WheelDistance, float minDownDeg = WheelMinDownDeg)
        {
            float top = Mathf.Atan2(Mathf.Max(0f, lensTop), Mathf.Max(0.05f, distance)) * Mathf.Rad2Deg;
            return Mathf.Clamp(Mathf.Max(modelBottomDownDeg + WheelGapDeg + top, minDownDeg), WheelMinDownDeg, WheelMaxDownDeg);
        }

        /// While the status line is up (the guide rail on, or a job's progress) it floats `statusBelowGazeDeg` under your
        /// gaze, ±`statusHalfDeg` tall: looking at the model (its centre `gazeDownDeg` below the eye line) it would lie
        /// over the lens card, so the lens steps down until the card's top is just under the line's bottom edge.
        public static float StatusClearDownDeg(float gazeDownDeg, float statusBelowGazeDeg, float statusHalfDeg, float lensTop, float distance = WheelDistance)
        {
            float top = Mathf.Atan2(Mathf.Max(0f, lensTop), Mathf.Max(0.05f, distance)) * Mathf.Rad2Deg;
            return gazeDownDeg + statusBelowGazeDeg + Mathf.Max(0f, statusHalfDeg) + WheelGapDeg * 0.5f + top;
        }

        /// The wheel's frame: the lens (its origin) `distance` from the eye along a line `downDeg` below your heading,
        /// the plane square to that line (+Z away from you, so its cards face your eyes).
        public static Pose WheelPose(Vector3 eye, float headingDeg, float downDeg, float distance = WheelDistance)
        {
            var rot = YawPitch(headingDeg, downDeg);
            return new Pose(eye + rot * Vector3.forward * distance, rot);
        }

        // ---------------- recentring ----------------

        /// Should the model come back in front of you: walked away (more than `walk` from where it was placed and the
        /// model more than `walk` away — walking round it doesn't count), or turned away (the model more than `turnDeg`
        /// off your heading; not while you're looking straight down or leaning over it).
        public static Recentre Why(Vector3 head, Vector3 forward, Vector3 placedFrom, Vector3 modelCentre,
            float walk = RecentreWalk, float turnDeg = RecentreTurnDeg)
        {
            var fromPlace = new Vector2(head.x - placedFrom.x, head.z - placedFrom.z);
            var toModel = new Vector2(modelCentre.x - head.x, modelCentre.z - head.z);
            if (fromPlace.magnitude > walk && toModel.magnitude > walk) return Recentre.Walked;
            var f = new Vector2(forward.x, forward.z);
            if (f.magnitude < 0.3f || toModel.magnitude < 0.25f) return Recentre.None;
            return Vector2.Angle(f, toModel) > turnDeg ? Recentre.Turned : Recentre.None;
        }

        /// Ease between two anchors (a recentre glides): position lerped, heading the short way round; `t` 0…1, smoothed.
        public static void Glide(Vector3 fromEye, float fromDeg, Vector3 toEye, float toDeg, float t, out Vector3 eye, out float deg)
        {
            float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            eye = Vector3.Lerp(fromEye, toEye, s);
            deg = fromDeg + Mathf.DeltaAngle(fromDeg, toDeg) * s;
        }

        // ---------------- the lens copy ----------------

        /// The line under the lens card's name (≤ ~24 characters: it's read at ~0.8 m): the model's scale when it's known,
        /// then what a pinch does, or its state.
        public static string LensDetail(string scale, ModelCardState state, bool controllers)
        {
            string act = controllers ? "Trigger" : "Pinch";
            string what = state switch
            {
                ModelCardState.Current => "On view",
                ModelCardState.Loading => "Loading…",
                ModelCardState.Queued => "Up next",
                ModelCardState.Failed => $"{act} to retry",
                ModelCardState.NotDownloaded => "Needs the laptop",
                _ => $"{act} to open",
            };
            bool plain = string.IsNullOrEmpty(scale) || state == ModelCardState.NotDownloaded || state == ModelCardState.Failed;
            return plain ? what : $"{scale} · {what}";
        }
    }
}
