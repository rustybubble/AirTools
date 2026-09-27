using System;
using System.Globalization;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// Ground-truth placement tasks on the synthetic facade (SPEC M4), with random targets and vantage points.
    /// Shared by the EditMode stress tests and the in-Simulator AgentHarness.RunM4. Points are scene-root space.
    public static class PartScenarios
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static float U(System.Random r, float a) => (float)(r.NextDouble() * 2 - 1) * a;
        static float R(System.Random r, float lo, float hi) => lo + (float)r.NextDouble() * (hi - lo);

        public const string Hanger = "hidden-hanger-5k";
        public const string Ac = "window-ac-small";

        /// Hanger onto the fascia above the gutter. proximity: the pointer starts 4–6 cm in front of the fascia and
        /// points along it (release within 15 cm); otherwise the ray comes from a raised vantage 1–2 m out.
        public static ScenarioResult HangerOnFascia(PartTool tool, PartLoader loader, Transform frame, int seed, bool proximity, bool removeAfter = true)
        {
            var rng = new System.Random(seed * 7919 + (proximity ? 1 : 0));
            string id = proximity ? "M4.hanger.fascia.near" : "M4.hanger.fascia.ray";
            // Near release: stay closer to the fascia than to the gutter's top edges (which are also within 15 cm).
            var target = new Vector3(R(rng, -1.8f, 1.8f), proximity ? R(rng, 6.15f, 6.17f) : R(rng, 6.13f, 6.17f), S.FasciaProud);
            Pose pointer;
            if (proximity)
            {
                var origin = new Vector3(target.x - 0.15f, target.y + U(rng, 0.005f), S.FasciaProud + R(rng, 0.04f, 0.06f));
                pointer = ToolInputHub.RayPose(W(frame, origin), W(frame, origin + new Vector3(1f, 0f, U(rng, 0.05f))));
            }
            else
            {
                var origin = new Vector3(target.x + U(rng, 0.5f), R(rng, 6.8f, 7.3f), R(rng, 1.2f, 2.0f));
                pointer = ToolInputHub.RayPose(W(frame, origin), W(frame, target));
            }
            return Place(tool, loader, frame, Hanger, pointer, id, seed, removeAfter, part =>
            {
                var b = LocalBounds(part, frame);
                float back = Mathf.Abs(b.min.z - S.FasciaProud) * 1000f;
                float tilt = Vector3.Angle(Dir(frame, part.WorldMountDirection), Vector3.back);
                bool ok = back <= 2f && tilt < 1f && part.Fit.Status == FitStatus.Green && SizeOk(part);
                return (ok, $"target={V(target)} back_face_mm={back.ToString("0.00", C)} tilt={tilt.ToString("0.00", C)}° fit={part.Fit}");
            });
        }

        /// AC unit onto the sill of the 1.500 m window. withTape: a 1.500 m tape (±5 mm) across the window first.
        public static ScenarioResult AcOnSill(PartTool tool, PartLoader loader, Transform frame, int seed, bool withTape, bool removeAfter = true)
        {
            var rng = new System.Random(seed * 104729 + (withTape ? 1 : 0));
            string id = withTape ? "M4.ac.window.tape" : "M4.ac.window.scan";
            NotebookEntry tape = null;
            if (withTape)
            {
                float y = R(rng, 3.55f, 4.5f);
                var a = new Vector3(-S.WindowWidth / 2 + U(rng, 0.0025f), y, -0.05f);
                var b = new Vector3(S.WindowWidth / 2 + U(rng, 0.0025f), y, -0.05f);
                tape = new NotebookEntry("measure", Vector3.Distance(a, b), "m", new[] { a, b }, DateTime.Now, -1, "tape (scenario)");
                Notebook.Add(tape);
            }
            var target = new Vector3(R(rng, -0.45f, 0.45f), S.SillHeight, R(rng, -0.08f, 0.03f));
            var origin = new Vector3(target.x + U(rng, 0.3f), R(rng, 4.0f, 4.6f), R(rng, 0.9f, 1.6f));
            var pointer = ToolInputHub.RayPose(W(frame, origin), W(frame, target));
            var result = Place(tool, loader, frame, Ac, pointer, id, seed, removeAfter, part =>
            {
                var b = LocalBounds(part, frame);
                float seat = Mathf.Abs(b.min.y - S.SillHeight) * 1000f;
                var f = part.Fit;
                float opening = f.OpeningMm ?? -1f;
                // A tape nearby wins over the scan (the app's rule) — including one left by an earlier M2 run — so the
                // tolerance follows the source actually used: a tape reading ±10 mm (M2 tolerance), the scan ±3 mm.
                bool fromTape = f.OpeningSource != null && f.OpeningSource.StartsWith("tape");
                bool mismatch = f.Status == FitStatus.Red && f.Headline.StartsWith("Window too wide by ")
                                && Mathf.Abs(opening - 1500f) <= (fromTape ? 10f : 3f) && f.Headline.Contains("unit fits 590–1000 mm");
                bool ok = seat <= 2f && b.min.z >= -S.WindowRecess - 0.001f && f.Collisions.Count == 0 && mismatch && SizeOk(part);
                return (ok, $"target={V(target)} seat_mm={seat.ToString("0.00", C)} opening={opening.ToString("0", C)} ({f.OpeningSource}) fit={f.Status}: {f.Headline.Replace('\n', ' ')}");
            });
            if (tape != null && removeAfter) Notebook.Remove(tape);
            return result;
        }

        static ScenarioResult Place(PartTool tool, PartLoader loader, Transform frame, string partId, Pose pointer, string id, int seed,
            bool removeAfter, Func<PartInstance, (bool ok, string detail)> check)
        {
            var part = loader.LoadFromCatalog(partId);
            if (part == null) return new ScenarioResult { Id = id, Seed = seed, Detail = $"{partId} not in catalog" };
            tool.Hold(part);
            bool placed = tool.Release(pointer);
            (bool ok, string detail) = placed ? check(part) : (false, $"not placed: {tool.LastAction}");
            if (removeAfter) { if (placed) tool.Remove(part); else tool.ClearAll(); }
            return new ScenarioResult { Id = id, Seed = seed, Passed = ok, Detail = detail };
        }

        static bool SizeOk(PartInstance p)
        {
            var s = p.MeasuredSizeMm();
            var d = p.Spec.dims_mm;
            return Mathf.Abs(s.x - d.w) <= 1f && Mathf.Abs(s.y - d.h) <= 1f && Mathf.Abs(s.z - d.d) <= 1f;
        }

        /// The part's collider bounds in scene-root space (axis-aligned there; parts are placed square to the facade).
        public static Bounds LocalBounds(PartInstance p, Transform frame)
        {
            var box = p.LocalBox;
            var b = new Bounds(Pt(frame, p.transform.TransformPoint(box.center)), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var e = box.extents;
                var c = box.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                b.Encapsulate(Pt(frame, p.transform.TransformPoint(c)));
            }
            return b;
        }

        static Vector3 W(Transform frame, Vector3 local) => frame != null ? frame.TransformPoint(local) : local;
        static Vector3 Pt(Transform frame, Vector3 world) => frame != null ? frame.InverseTransformPoint(world) : world;
        static Vector3 Dir(Transform frame, Vector3 world) => frame != null ? frame.InverseTransformDirection(world) : world;
        static string V(Vector3 v) => $"{v.x.ToString("0.000", C)},{v.y.ToString("0.000", C)},{v.z.ToString("0.000", C)}";
    }
}
