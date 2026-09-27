using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Core;
using AirTools.Input;
using AirTools.Tools;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// A ground-truth measuring task on the synthetic facade: aim a pointer ray from an origin at each target
    /// (plus random aiming noise), click, finish, and compare the notebook result with the known answer.
    /// Shared by the EditMode tests and the in-Simulator AgentHarness so both exercise the same flow.
    public class MeasureScenario
    {
        public string Id;
        public string Description;
        public Vector3 RayOrigin;           // scene-root space
        public Vector3[] Targets;           // scene-root space
        /// Aiming noise for target i (seeded RNG). Default: none.
        public Func<System.Random, int, Vector3> Noise = (_, __) => Vector3.zero;
        /// Ray origin for target i (scene-root space), drawn before its noise; null = RayOrigin for every target
        /// (UX W1.2: <see cref="MeasureScenarios.M2Near"/> aims each point from its own spot).
        public Func<System.Random, int, Vector3> OriginFor;
        public double? ExpectDistance;
        public double? ExpectArea;
        public double[] ExpectAngles;
        public double DistanceTol = 0.010, AreaTol = 0.03, AngleTol = 1.0;
    }

    public struct ScenarioResult
    {
        public string Id;
        public int Seed;
        public bool Passed;
        public string Detail;
        public override string ToString() => $"{(Passed ? "PASS" : "FAIL")} {Id} seed={Seed} {Detail}";
    }

    public static class MeasureScenarios
    {
        static string V(Vector3 v) => $"{v.x.ToString("0.000", CultureInfo.InvariantCulture)},{v.y.ToString("0.000", CultureInfo.InvariantCulture)},{v.z.ToString("0.000", CultureInfo.InvariantCulture)}";
        static float U(System.Random r, float a) => (float)(r.NextDouble() * 2 - 1) * a;

        public static readonly Vector3 SpawnEye = new Vector3(0f, 1.7f, 4.0f);

        public static List<MeasureScenario> M2()
        {
            float gutterY = S.FasciaBottom + 0.04f, gutterZ = S.FasciaProud + S.GutterWidth, halfRun = S.GutterRun / 2f;
            var list = new List<MeasureScenario>
            {
                new MeasureScenario
                {
                    Id = "M2.measure.window.width",
                    Description = "Left and right window jambs from the spawn eye, ±3 cm aim noise → 1.500 m",
                    RayOrigin = SpawnEye,
                    Targets = new[] { new Vector3(-S.WindowWidth / 2, 4.1f, -0.05f), new Vector3(S.WindowWidth / 2, 4.1f, -0.05f) },
                    Noise = (r, i) => new Vector3(U(r, 0.03f), U(r, 0.03f), U(r, 0.03f)),
                    ExpectDistance = S.WindowWidth, DistanceTol = 0.010,
                },
                new MeasureScenario
                {
                    Id = "M2.measure.gutter.run",
                    Description = "Both gutter ends from the spawn eye (aim up to 3 cm in from each end, ±2 cm on the 12 cm lip) → 4.200 m",
                    RayOrigin = SpawnEye,
                    Targets = new[] { new Vector3(-halfRun, gutterY, gutterZ), new Vector3(halfRun, gutterY, gutterZ) },
                    // Outer ends: noise points inward (aiming past the end means aiming at the sky). The lip is only 12 cm
                    // tall and seen from below, so vertical noise is ±2 cm.
                    // Aim 1–3 cm in from the end: within ~5 mm of an end the oblique ray reaches the face past the end.
                    Noise = (r, i) => new Vector3((i == 0 ? 1 : -1) * (0.01f + (float)r.NextDouble() * 0.02f), U(r, 0.02f), 0f),
                    ExpectDistance = S.GutterRun, DistanceTol = 0.015,
                },
                new MeasureScenario
                {
                    Id = "M2.measure.window.area",
                    Description = "Four glass corners from a raised eye (0, 4.1, 2.5), aim up to 3 cm inside each corner → 1.80 m², four 90° corners",
                    RayOrigin = new Vector3(0f, 4.1f, 2.5f),
                    Targets = new[]
                    {
                        new Vector3(-0.75f, 4.7f, -0.1f), new Vector3(0.75f, 4.7f, -0.1f),
                        new Vector3(0.75f, 3.5f, -0.1f), new Vector3(-0.75f, 3.5f, -0.1f),
                    },
                    // Up to 3 cm in from each glass corner (aiming outward means aiming at the jamb/sill/head instead).
                    Noise = (r, i) => new Vector3((i == 0 || i == 3 ? 1 : -1) * (float)r.NextDouble() * 0.03f,
                                                  (i < 2 ? -1 : 1) * (float)r.NextDouble() * 0.03f, 0f),
                    ExpectArea = S.WindowWidth * S.WindowHeight, AreaTol = 0.03,
                    ExpectAngles = new double[] { 90, 90, 90, 90 }, AngleTol = 1.0,
                },
                new MeasureScenario
                {
                    Id = "M2.measure.door.width",
                    Description = "Door head corners, ±3 cm aim noise → 0.914 m",
                    RayOrigin = new Vector3(S.DoorCentreX, 1.6f, 3.0f),
                    Targets = new[]
                    {
                        new Vector3(S.DoorCentreX - S.DoorWidth / 2, S.DoorHeight, 0.03f),
                        new Vector3(S.DoorCentreX + S.DoorWidth / 2, S.DoorHeight, 0.03f),
                    },
                    Noise = (r, i) => new Vector3(U(r, 0.03f), U(r, 0.03f), 0f),
                    ExpectDistance = S.DoorWidth, DistanceTol = 0.010,
                },
                LedgeTriangle(),
            };
            return list;
        }

        /// UX W1.2: the M2 scenarios at arm's length with hand-ray noise. Each point is aimed from its own spot, a seeded
        /// minDistance–maxDistance from it on the original line of sight (so the same faces are in view), with angular
        /// aim noise: each scenario's own noise pattern — inward-only at outer corners and ends, the gutter's 1 cm
        /// inset — with amplitude d × tan(noiseDegrees) instead of fixed centimetres. Same answers and tolerances.
        public static List<MeasureScenario> M2Near(float minDistance = 0.6f, float maxDistance = 2f, float noiseDegrees = 1f)
        {
            float tan = Mathf.Tan(noiseDegrees * Mathf.Deg2Rad);
            var list = new List<MeasureScenario>();
            foreach (var s in M2())
            {
                var src = s;
                var d = new float[s.Targets.Length];
                list.Add(new MeasureScenario
                {
                    Id = s.Id + ".near",
                    Description = $"{s.Description}; each point from {minDistance:0.0}–{maxDistance:0.0} m with ±{noiseDegrees:0.#}° aim noise",
                    RayOrigin = s.RayOrigin,
                    Targets = s.Targets,
                    OriginFor = (r, i) =>
                    {
                        d[i] = Mathf.Lerp(minDistance, maxDistance, (float)r.NextDouble());
                        return src.Targets[i] + (src.RayOrigin - src.Targets[i]).normalized * d[i];
                    },
                    Noise = (r, i) => NearNoise(src.Id, r, i, d[i] * tan),
                    ExpectDistance = s.ExpectDistance, ExpectArea = s.ExpectArea, ExpectAngles = s.ExpectAngles,
                    DistanceTol = s.DistanceTol, AreaTol = s.AreaTol, AngleTol = s.AngleTol,
                });
            }
            return list;
        }

        /// M2's noise patterns with amplitude `a` (metres).
        static Vector3 NearNoise(string id, System.Random r, int i, float a)
        {
            float R() => (float)r.NextDouble();
            switch (id)
            {
                case "M2.measure.window.width": return new Vector3(U(r, a), U(r, a), U(r, a));
                // ±1° only (the README's "±1° noise"): M2's extra 1 cm inward bias on top of d·tan 1° reached 4.5 cm at 2 m, past
                // the 4 cm end-edge radius, and failed identically with W1.2 on and off (10/300, seeds 4, 31, …).
                case "M2.measure.gutter.run": return new Vector3((i == 0 ? 1 : -1) * R() * a, U(r, Mathf.Min(a, 0.02f)), 0f);
                case "M2.measure.window.area": return new Vector3((i == 0 || i == 3 ? 1 : -1) * R() * a, (i < 2 ? -1 : 1) * R() * a, 0f);
                case "M2.measure.door.width": return new Vector3(U(r, a), U(r, a), 0f);
                case "M2.measure.ledge.triangle":
                    return i == 0 ? new Vector3(R() * a, 0f, U(r, a)) : i == 1 ? new Vector3(-R() * a, 0f, U(r, a)) : new Vector3(-R() * a, 0f, -R() * a);
                default: return Vector3.zero;
            }
        }

        /// Three corners of the tilted ledge top (right angle at the far wall corner): 2.0 × 0.3 m → 0.30 m²,
        /// angles 8.53° / 90° / 81.47°. Exercises inside corners (ledge × wall) and the 2° tilt.
        static MeasureScenario LedgeTriangle()
        {
            var pivot = new Vector3(S.LedgeCentreX, S.LedgeHeight, 0f);
            var rot = Quaternion.Euler(S.LedgeTiltDeg, 0f, 0f);
            float h = S.LedgeLength / 2f;
            var a = pivot + rot * new Vector3(-h, 0f, 0f);
            var b = pivot + rot * new Vector3(h, 0f, 0f);
            var c = pivot + rot * new Vector3(h, 0f, S.LedgeDepth);
            double angleA = Math.Atan2(S.LedgeDepth, S.LedgeLength) * 180 / Math.PI;
            return new MeasureScenario
            {
                Id = "M2.measure.ledge.triangle",
                Description = "Ledge top: two wall-side corners + one front corner, ±2 cm aim noise → 0.30 m², right angle",
                RayOrigin = new Vector3(S.LedgeCentreX, 2.3f, 1.3f),
                Targets = new[] { a, b, c },
                // Outer corners: noise stays on the ledge (inward in x, and inward in z for the front corner);
                // the wall-side corners may be aimed slightly into the wall (inside corner).
                Noise = (r, i) => i switch
                {
                    0 => new Vector3((float)r.NextDouble() * 0.02f, 0f, U(r, 0.02f)),
                    1 => new Vector3(-(float)r.NextDouble() * 0.02f, 0f, U(r, 0.02f)),
                    _ => new Vector3(-(float)r.NextDouble() * 0.02f, 0f, -(float)r.NextDouble() * 0.02f),
                },
                ExpectArea = S.LedgeLength * S.LedgeDepth * 0.5, AreaTol = 0.01,
                ExpectAngles = new[] { angleA, 90.0, 90.0 - angleA }, AngleTol = 1.0,
            };
        }

        /// Runs one scenario through the real tool + input hub (the same path controller/hand input takes).
        /// The tool must be equipped. The finished shape is left in the tool unless removeAfter is set.
        public static ScenarioResult Run(MeasureScenario s, MeasureTool tool, ToolInputHub hub, Transform frame, int seed, bool removeAfter = false)
        {
            var rng = new System.Random(seed);
            tool.Session.Clear();
            bool prevArea = tool.AreaMode;
            tool.AreaMode = s.Targets.Length > 2;   // D4: Line mode saves at point 2; shapes need Area mode
            int before = tool.Shapes.Count;
            Vector3 W(Vector3 local) => frame != null ? frame.TransformPoint(local) : local;
            var origin = W(s.RayOrigin);
            var placed = new StringBuilder();
            try
            {
                for (int i = 0; i < s.Targets.Length; i++)
                {
                    var from = s.OriginFor != null ? W(s.OriginFor(rng, i)) : origin;   // W1.2: M2Near
                    var target = W(s.Targets[i] + s.Noise(rng, i));
                    var pose = ToolInputHub.RayPose(from, target);
                    hub.SetPointerOverride(ToolHand.Right, pose);
                    hub.RaisePressStart(ToolHand.Right, pose);
                    hub.RaisePressEnd(ToolHand.Right, pose);
                    if (tool.LastPlaced.HasValue)
                    {
                        var lp = tool.LastPlaced.Value;
                        var loc = frame != null ? frame.InverseTransformPoint(lp.point) : lp.point;
                        var raw = frame != null ? frame.InverseTransformPoint(lp.rawPoint) : lp.rawPoint;
                        placed.Append($"{lp.kind.ToString()[0]}[{V(loc)} raw {V(raw)} n {V(lp.normal)} on {(lp.collider != null ? lp.collider.name : "-")}] ");
                    }
                }
                if (tool.Shapes.Count == before) hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            }
            finally
            {
                hub.SetPointerOverride(ToolHand.Right, null);
                tool.AreaMode = prevArea;
            }

            if (tool.Shapes.Count == before)
                return new ScenarioResult { Id = s.Id, Seed = seed, Passed = false, Detail = $"no shape finished ({tool.LastAction})" };

            var shape = tool.Shapes[tool.Shapes.Count - 1];
            var m = shape.Measurement;
            var c = CultureInfo.InvariantCulture;
            bool ok = m.PointCount == s.Targets.Length;
            var detail = new StringBuilder($"snaps={placed} ");
            if (s.ExpectDistance.HasValue)
            {
                bool d = Math.Abs(m.Distance - s.ExpectDistance.Value) <= s.DistanceTol;
                ok &= d;
                detail.Append($"distance={m.Distance.ToString("0.0000", c)} expected={s.ExpectDistance.Value.ToString("0.000", c)}±{s.DistanceTol.ToString("0.000", c)} ");
            }
            if (s.ExpectArea.HasValue)
            {
                bool d = Math.Abs(m.Area - s.ExpectArea.Value) <= s.AreaTol;
                ok &= d;
                detail.Append($"area={m.Area.ToString("0.0000", c)} expected={s.ExpectArea.Value.ToString("0.000", c)}±{s.AreaTol.ToString("0.000", c)} ");
            }
            if (s.ExpectAngles != null)
            {
                bool all = m.Angles.Length == s.ExpectAngles.Length;
                for (int i = 0; all && i < m.Angles.Length; i++) all &= Math.Abs(m.Angles[i] - s.ExpectAngles[i]) <= s.AngleTol;
                ok &= all;
                detail.Append($"angles={string.Join("/", m.Angles.Select(a => a.ToString("0.0", c)))} expected={string.Join("/", s.ExpectAngles.Select(a => a.ToString("0.0", c)))}±{s.AngleTol.ToString("0.0", c)} ");
            }
            detail.Append($"entry=#{shape.Entry?.Id} cam={shape.Entry?.NearestCameraId}");
            if (removeAfter) tool.Undo();
            return new ScenarioResult { Id = s.Id, Seed = seed, Passed = ok, Detail = detail.ToString() };
        }
    }
}
