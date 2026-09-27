using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Input;
using AirTools.Tools;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// Ground-truth level readings on the synthetic facade (SPEC M3): ledge 2.0°, sill 0.0°, wall and door plumb 0.0°.
    public class LevelScenario
    {
        public string Id;
        public string Description;
        public Vector3 RayOrigin;     // scene-root space
        public Vector3 Target;        // scene-root space
        public float Noise = 0.03f;   // ± aim noise in the surface plane (metres)
        public LevelMode ExpectMode;
        public double ExpectDegrees;
        public double Tol = 0.2;
    }

    public static class LevelScenarios
    {
        public static List<LevelScenario> M3() => new List<LevelScenario>
        {
            new LevelScenario
            {
                Id = "M3.level.ledge", Description = "Level on the pitched ledge → 2.0°",
                RayOrigin = new Vector3(S.LedgeCentreX, 1.7f, 1.5f),
                Target = new Vector3(S.LedgeCentreX, S.LedgeHeight - 0.005f, 0.15f),
                ExpectMode = LevelMode.Level, ExpectDegrees = S.LedgeTiltDeg,
            },
            new LevelScenario
            {
                Id = "M3.level.sill", Description = "Level on the window sill from a raised eye → 0.0°",
                RayOrigin = new Vector3(0f, 4.1f, 2.5f),
                Target = new Vector3(0f, S.SillHeight, 0.0f), Noise = 0.02f,
                ExpectMode = LevelMode.Level, ExpectDegrees = 0.0,
            },
            new LevelScenario
            {
                Id = "M3.level.wall.plumb", Description = "Level held on the brick wall → plumb 0.0°",
                RayOrigin = MeasureScenarios.SpawnEye,
                Target = new Vector3(-2.5f, 3.0f, 0f),
                ExpectMode = LevelMode.Plumb, ExpectDegrees = 0.0,
            },
            new LevelScenario
            {
                Id = "M3.level.door.plumb", Description = "Level held on the door → plumb 0.0°",
                RayOrigin = new Vector3(S.DoorCentreX, 1.6f, 3.0f),
                Target = new Vector3(S.DoorCentreX, 1.0f, 0.03f),
                ExpectMode = LevelMode.Plumb, ExpectDegrees = 0.0,
            },
        };

        /// Aim the level from the origin at the target (+ noise across the target), click, and check the reading.
        public static ScenarioResult Run(LevelScenario s, LevelTool tool, ToolInputHub hub, Transform frame, int seed, bool removeAfter = false)
        {
            var rng = new System.Random(seed);
            float U() => (float)(rng.NextDouble() * 2 - 1) * s.Noise;
            Vector3 W(Vector3 local) => frame != null ? frame.TransformPoint(local) : local;
            int before = tool.Placements.Count;
            var target = s.Target + new Vector3(U(), s.ExpectMode == LevelMode.Level ? 0f : U(), s.ExpectMode == LevelMode.Level ? U() : 0f);
            var pose = ToolInputHub.RayPose(W(s.RayOrigin), W(target));
            try
            {
                hub.SetPointerOverride(ToolHand.Right, pose);
                hub.RaisePressStart(ToolHand.Right, pose);
                hub.RaisePressEnd(ToolHand.Right, pose);
            }
            finally { hub.SetPointerOverride(ToolHand.Right, null); }

            if (tool.Placements.Count == before)
                return new ScenarioResult { Id = s.Id, Seed = seed, Passed = false, Detail = $"no reading ({tool.LastAction})" };
            var p = tool.Placements[tool.Placements.Count - 1];
            var r = p.Reading;
            var c = CultureInfo.InvariantCulture;
            bool ok = r.Mode == s.ExpectMode && Math.Abs(r.Degrees - s.ExpectDegrees) <= s.Tol;
            string detail = $"mode={r.Mode} value={r.Degrees.ToString("0.000", c)} expected={s.ExpectMode} {s.ExpectDegrees.ToString("0.0", c)}±{s.Tol.ToString("0.0", c)} " +
                            $"entry=#{p.Entry?.Id} cam={p.Entry?.NearestCameraId} label='{p.Entry?.Label}'";
            if (removeAfter) tool.Undo();
            return new ScenarioResult { Id = s.Id, Seed = seed, Passed = ok, Detail = detail };
        }
    }
}
