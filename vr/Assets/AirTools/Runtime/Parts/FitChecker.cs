using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    public enum FitStatus { None, Green, Amber, Red }

    /// A tape reading the fit check compares against (a 2-point measurement, world space).
    public struct TapeReading
    {
        public float LengthMm;
        public Vector3 A, B;
        public int EntryId;
        public Vector3 Direction => (B - A).normalized;
    }

    /// The one next step a fit card offers (W1.4 builds the button; every command goes through AppCommands).
    public enum FitAction { None, ComparePrices, PlaceAll, MeasureIt, MoveIt, SeeOthers, FindBigger, FindSmaller }

    public class FitReport
    {
        public FitStatus Status = FitStatus.Green;
        /// Diagnostic line (logs, tests, the harness, hcheck.py): never drawn. The display uses Verdict / Reason.
        public string Headline = "Fits";
        /// Display (UX W0.9 §4): verdict (no glyph; Copy.Glyph adds it), reason in the user's units, one action.
        public string Verdict = "Fits here";
        public string Reason = "Sits flat · nothing in the way";
        public FitAction Action = FitAction.ComparePrices;
        public string ActionLabel = "Compare prices";
        /// FindPart query / PlaceArray count for the action.
        public string ActionArg;
        /// Where the part sits ("the door"); null when unknown.
        public string Surface;
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Collisions = new List<string>();
        public readonly List<string> ClearanceBlocked = new List<string>();
        public float? OpeningMm;
        public string OpeningSource;
        public float? SpareMm;
        public int SupportedSamples = -1;

        string m_FitsLine;

        public void Add(FitStatus s, string line)
        {
            Lines.Add(line);
            if (s == FitStatus.Green) { m_FitsLine ??= line; if (Status == FitStatus.Green) Headline = m_FitsLine; return; }
            if (s > Status) { Status = s; Headline = line; }
        }

        FitStatus m_SaidStatus = FitStatus.Green;
        bool m_SaidGreen, m_Said;

        /// The display half of Add: same precedence (the first message at the worst status wins; for Green the first
        /// Green message wins). Every branch that calls Add also calls Say.
        public void Say(FitStatus s, string verdict, string reason, FitAction action, string label, string arg = null)
        {
            if (s == FitStatus.Green)
            {
                if (m_SaidGreen) return;
                m_SaidGreen = true;
                if (Status != FitStatus.Green || m_SaidStatus != FitStatus.Green) return;
            }
            else if (s <= m_SaidStatus) return;
            else m_SaidStatus = s;
            m_Said = true;
            Verdict = verdict; Reason = reason ?? ""; Action = action; ActionLabel = label ?? ""; ActionArg = arg;
        }

        /// Nothing was said (no constraint applied): the default verdict.
        public bool SaidAnything => m_Said;

        public override string ToString() => $"{Status}: {Headline}";
    }

    /// Does the part fit where it is? (SPEC M4)
    ///  red   — the true-size box (dims_mm, shrunk 1 mm) overlaps the scene or another part; or a spec constraint
    ///          (window width range, tape length) is violated
    ///  amber — the clearance zones (clearance_mm per face) are blocked, or the mounting face isn't supported
    ///  green — otherwise; with a tape reading nearby it also says how much room is spare.
    public static class FitChecker
    {
        const float Shrink = 0.001f;
        static readonly Collider[] s_Hits = new Collider[32];
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static FitReport Check(PartInstance part, TapeReading? tape = null, bool checkSupport = true, bool scanOpening = true)
        {
            var r = new FitReport();
            Physics.SyncTransforms();
            var spec = part.Spec;
            var t = part.transform;
            var box = PartMath.LocalBox(spec);

            r.Surface = SurfaceNames.ForPart(part);
            string noun = Copy.Noun(spec, part.SearchQuery);

            // 1. Collisions of the true-size box.
            s_Cols.Clear();
            foreach (var name in Overlaps(t, box.center, box.size - Vector3.one * (2f * Shrink), part, s_Cols))
                if (!r.Collisions.Contains(name)) r.Collisions.Add(name);
            if (r.Collisions.Count > 0)
            {
                r.Add(FitStatus.Red, $"Hits {string.Join(", ", r.Collisions)}");
                SayCollision(r, part, noun, s_Cols.Count > 0 ? s_Cols[0] : null);
            }

            // 2. Clearance zones, one slab per face.
            var c = spec.clearance_mm;
            CheckSlab(r, part, box, Vector3.up, c.top, "above");
            CheckSlab(r, part, box, Vector3.down, c.bottom, "below");
            CheckSlab(r, part, box, Vector3.forward, c.front, "in front");
            CheckSlab(r, part, box, Vector3.back, c.back, "behind");
            CheckSlab(r, part, box, Vector3.right, c.right, "to the right");
            CheckSlab(r, part, box, Vector3.left, c.left, "to the left");

            // 3. Mounting face resting on a surface.
            if (checkSupport)
            {
                int n = SupportSamples(part);
                r.SupportedSamples = n;
                string target = Copy.MountTarget(spec);
                if (n == 0)
                {
                    r.Add(FitStatus.Amber, "Not mounted on a surface");
                    r.Say(FitStatus.Amber, $"Not flat on {target}", $"Press it flat against {target}", FitAction.MoveIt, "Move it");
                }
                else if (n < 2)
                {
                    r.Add(FitStatus.Amber, "Overhangs: only the centre of the mounting face touches");
                    r.Say(FitStatus.Amber, "Hangs over the edge", "Slide it onto the flat part", FitAction.MoveIt, "Move it");
                }
            }

            // 4. Window width range (e.g. a window AC) against the tape, else a scan of the opening.
            if (spec.HasWindowRange)
            {
                float? opening = null;
                if (tape.HasValue) { opening = tape.Value.LengthMm; r.OpeningSource = $"tape #{tape.Value.EntryId}"; }
                else if (scanOpening && ScanOpening(part, out float scanned)) { opening = scanned; r.OpeningSource = "scan"; }
                r.OpeningMm = opening;
                if (opening.HasValue)
                {
                    float o = opening.Value;
                    float min = spec.min_window_width_mm ?? 0f, max = spec.max_window_width_mm ?? float.PositiveInfinity;
                    string range = spec.max_window_width_mm.HasValue ? $"{Mm(min)}–{Mm(max)} mm" : $"≥ {Mm(min)} mm";
                    string query = string.IsNullOrEmpty(part.SearchQuery) ? "window ac" : part.SearchQuery;
                    if (o > max + 1f)
                    {
                        r.Add(FitStatus.Red, $"Window too wide by {Mm(o - max)} mm\n{Mm(o)} mm opening, unit fits {range}");
                        r.Say(FitStatus.Red, "Too small for this window", $"Window {Copy.Len(o / 1000f)} · unit fits up to {Copy.Len(max / 1000f)}",
                            FitAction.FindBigger, "Find a bigger unit", query);
                    }
                    else if (o < min - 1f)
                    {
                        r.Add(FitStatus.Red, $"Window too narrow by {Mm(min - o)} mm\n{Mm(o)} mm opening, unit fits {range}");
                        r.Say(FitStatus.Red, "Too big for this window", $"Window {Copy.Len(o / 1000f)} · unit needs at least {Copy.Len(min / 1000f)}",
                            FitAction.FindSmaller, "Find a smaller unit", query);
                    }
                    else
                    {
                        r.SpareMm = float.IsInfinity(max) ? o - min : max - o;
                        r.Add(FitStatus.Green, $"Fits the {Mm(o)} mm window ({range})");
                        r.Say(FitStatus.Green, "Fits this window", $"Window {Copy.Len(o / 1000f)} · unit fits {Copy.Range(spec.min_window_width_mm, spec.max_window_width_mm)}",
                            FitAction.ComparePrices, "Compare prices", "price");
                    }
                }
                else
                {
                    r.Add(FitStatus.Amber, "Measure the window width to check the fit");
                    r.Say(FitStatus.Amber, "Measure the window", "The fit needs the window width", FitAction.MeasureIt, "Measure it", "measure");
                }
            }
            // 5. Otherwise, the part's extent along a nearby tape.
            else if (tape.HasValue)
            {
                var d = tape.Value.Direction;
                var size = box.size;
                float extent = (Mathf.Abs(Vector3.Dot(t.right, d)) * size.x + Mathf.Abs(Vector3.Dot(t.up, d)) * size.y
                                + Mathf.Abs(Vector3.Dot(t.forward, d)) * size.z) * 1000f;
                float spare = tape.Value.LengthMm - extent;
                string tapeLen = Copy.Len(tape.Value.LengthMm / 1000f);
                if (spare < -1f)
                {
                    r.Add(FitStatus.Red, $"Too wide by {Mm(-spare)} mm for the {Mm(tape.Value.LengthMm)} mm tape");
                    string adj = PartsClient.TapeAxis(tape.Value.A, tape.Value.B) switch { "h" => "tall", "length" => "long", _ => "wide" };
                    r.Say(FitStatus.Red, $"{Copy.Gap(-spare)} too {adj} for your tape", $"The {noun} is {Copy.Size(extent)} · your tape is {tapeLen}",
                        FitAction.SeeOthers, $"See other {Copy.Plural(noun)}", part.SearchQuery);
                }
                else
                {
                    r.SpareMm = spare;
                    r.Add(FitStatus.Green, $"Fits, {Mm(spare)} mm spare");
                    if (spec.spacing_mm.HasValue && spec.spacing_mm.Value >= 10f)
                    {
                        int n = ArrayPlanner.Quantity(tape.Value.LengthMm / 1000f, spec.spacing_mm.Value / 1000f);
                        r.Say(FitStatus.Green, FitsOn(r.Surface), $"{n} needed along the {tapeLen} run · every {Copy.Gap(spec.spacing_mm.Value)}",
                            FitAction.PlaceAll, $"Place all {n}", n.ToString(C));
                    }
                    else if (Copy.KindOf(spec, part.SearchQuery) == PartKind.Spanning)
                        r.Say(FitStatus.Green, $"Fits · {Copy.Gap(spare)} to spare", $"The {noun} is {Copy.Size(extent)} · your tape is {tapeLen}",
                            FitAction.ComparePrices, "Compare prices", "price");
                    else
                        r.Say(FitStatus.Green, FitsOn(r.Surface), "Sits flat · nothing in the way", FitAction.ComparePrices, "Compare prices", "price");
                }
            }

            // Nothing constrained it: the default verdict (FT0).
            if (!r.SaidAnything)
            {
                if (spec.spacing_mm.HasValue && spec.spacing_mm.Value >= 10f && !tape.HasValue)
                    r.Say(FitStatus.Green, FitsOn(r.Surface), "Measure the run to place the rest", FitAction.MeasureIt, "Measure the run", "measure");
                else
                    r.Say(FitStatus.Green, FitsOn(r.Surface), r.SupportedSamples == -1 ? "Nothing in the way" : "Sits flat · nothing in the way",
                        FitAction.ComparePrices, "Compare prices", "price");
            }
            return r;
        }

        static readonly List<Collider> s_Cols = new List<Collider>();

        static string FitsOn(string surface) => string.IsNullOrEmpty(surface) ? "Fits here" : $"Fits {surface}";

        /// FT1a–d: what the part runs into, in words.
        static void SayCollision(FitReport r, PartInstance part, string noun, Collider col)
        {
            var other = col != null ? col.GetComponentInParent<PartInstance>() : null;
            if (other != null)
            {
                string otherNoun = Copy.Noun(other.Spec, other.SearchQuery);
                r.Say(FitStatus.Red, otherNoun == noun ? $"Overlaps another {noun}" : $"Overlaps the {otherNoun}", "Give it a little more room", FitAction.MoveIt, "Move it");
                return;
            }
            if (col != null && SnapService.IsScan(col))
            {
                if (r.Surface == "the toe-kick")
                {
                    string reason = $"The {noun} is {Copy.Size(part.Spec.dims_mm.h)} tall";
                    r.Say(FitStatus.Red, "Too tall for the toe-kick", reason, FitAction.SeeOthers, $"See other {Copy.Plural(noun)}", part.SearchQuery);
                    return;
                }
                r.Say(FitStatus.Red, $"Runs into {CollisionWhere(part, r.Surface)}", "", FitAction.MoveIt, "Move it");
                return;
            }
            r.Say(FitStatus.Red, $"Hits {SurfaceNames.FromPath(col != null ? SurfaceName(col) : null)}", "", FitAction.MoveIt, "Move it");
        }

        /// FT1c: the floor when the box dips below it, else where it sits, else the place.
        static string CollisionWhere(PartInstance part, string surface)
        {
            if (Services.TryGet<SceneRoot>(out var root))
            {
                float floor = SurfaceNames.FloorY(root);
                if (!float.IsNaN(floor))
                {
                    var b = part.LocalBox;
                    float lowest = float.PositiveInfinity;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                        lowest = Mathf.Min(lowest, root.transform.InverseTransformPoint(part.transform.TransformPoint(corner)).y);
                    }
                    if (lowest < floor + 0.01f) return "the floor";
                }
                if (!string.IsNullOrEmpty(surface)) return surface;
                return "the " + Copy.SiteName(root.Site);
            }
            return string.IsNullOrEmpty(surface) ? "something" : surface;
        }

        /// The most recent 2-point tape whose segment passes within maxDistance of the part (frame = the space
        /// notebook points are stored in, i.e. the scene root; null = world).
        public static TapeReading? FindTape(PartInstance part, Transform frame, float maxDistance = 1.0f)
        {
            var centre = part.WorldBoxCentre;
            var entries = Notebook.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Tool != "measure" || e.Points == null || e.Points.Length != 2) continue;
                if (!e.OnCurrentSite) continue;   // sitescope: another scan's tape
                var a = frame != null ? frame.TransformPoint(e.Points[0]) : e.Points[0];
                var b = frame != null ? frame.TransformPoint(e.Points[1]) : e.Points[1];
                if (PartMath.DistanceToSegment(centre, a, b) > maxDistance) continue;
                return new TapeReading { LengthMm = (float)e.ValueSI * 1000f, A = a, B = b, EntryId = e.Id };
            }
            return null;
        }

        /// Opening width across the part's left/right axis at the height of the box centre (scene surfaces only),
        /// tried at the back, middle and front of the part (a window unit sits in the opening at its back).
        public static bool ScanOpening(PartInstance part, out float widthMm, float maxDistance = 3f)
        {
            widthMm = 0f;
            var t = part.transform;
            var box = part.LocalBox;
            foreach (float z in new[] { box.min.z + 0.01f, box.center.z, box.max.z - 0.01f })
            {
                var o = t.TransformPoint(new Vector3(box.center.x, box.center.y, z));
                if (!Physics.Raycast(o, t.right, out var right, maxDistance, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) continue;
                if (!Physics.Raycast(o, -t.right, out var left, maxDistance, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) continue;
                widthMm = (right.distance + left.distance) * 1000f;
                return true;
            }
            return false;
        }

        /// How many of 5 sample points on the mounting face (centre + 4 at ±35%) sit on a scene surface.
        public static int SupportSamples(PartInstance part)
        {
            var t = part.transform;
            var spec = part.Spec;
            var mount = PartMath.FaceDirection(spec.mount.face);
            var size = spec.dims_mm.Metres;
            // Two in-plane axes of the mounting face.
            Vector3 u = Mathf.Abs(mount.x) > 0.5f ? Vector3.forward : Vector3.right;
            Vector3 v = Mathf.Abs(mount.y) > 0.5f ? Vector3.forward : Vector3.up;
            float su = Vector3.Scale(u, size).magnitude * 0.35f, sv = Vector3.Scale(v, size).magnitude * 0.35f;
            var offsets = new[] { Vector3.zero, u * su + v * sv, u * su - v * sv, -u * su + v * sv, -u * su - v * sv };
            var dirWorld = t.TransformDirection(mount);
            float scale = t.lossyScale.x;
            int n = 0;
            foreach (var off in offsets)
            {
                var start = t.TransformPoint(off - mount * 0.005f);
                // Same as a raycast on scene surfaces; on a package with a structure layer the fitted plane under the
                // mounting face also counts (the decimated collision mesh can have holes where the surface is real).
                if (SnapService.TryRaySnap(new Ray(start, dirWorld), out _, 0.015f * scale, features: false)) n++;
            }
            return n;
        }

        static void CheckSlab(FitReport r, PartInstance part, Bounds box, Vector3 dir, float mm, string where)
        {
            if (mm < 3f) return;
            float m = mm * 0.001f;
            var ext = Vector3.Scale(box.extents, new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z))).magnitude;
            var centre = box.center + dir * (ext + m * 0.5f);
            var size = box.size;
            for (int i = 0; i < 3; i++) if (Mathf.Abs(dir[i]) > 0.5f) size[i] = m;
            s_SlabCols.Clear();
            var names = Overlaps(part.transform, centre, size - Vector3.one * (2f * Shrink), part, s_SlabCols);
            if (names.Count == 0) return;
            foreach (var n in names) if (!r.ClearanceBlocked.Contains(n)) r.ClearanceBlocked.Add(n);
            r.Add(FitStatus.Amber, $"Needs {Mm(mm)} mm clear {where} ({string.Join(", ", names)})");
            var col = s_SlabCols.Count > 0 ? s_SlabCols[0] : null;
            var other = col != null ? col.GetComponentInParent<PartInstance>() : null;
            string blocker = other != null ? $"another {Copy.Noun(other.Spec, other.SearchQuery)}"
                : col != null && SnapService.IsScan(col) ? "something"
                : SurfaceNames.FromPath(col != null ? SurfaceName(col) : null);
            r.Say(FitStatus.Amber, $"Too tight {where}", $"Needs {Copy.Gap(mm)} clear · {blocker} is in the way", FitAction.MoveIt, "Move it");
        }

        static readonly List<Collider> s_SlabCols = new List<Collider>();

        /// Names of what the box overlaps; `colliders` (optional) gets the collider behind each new name, in order.
        static List<string> Overlaps(Transform t, Vector3 localCentre, Vector3 localSize, PartInstance self, List<Collider> colliders = null)
        {
            var names = new List<string>();
            var half = Vector3.Scale(Vector3.Max(localSize, Vector3.one * 1e-4f) * 0.5f, t.lossyScale);
            int n = Physics.OverlapBoxNonAlloc(t.TransformPoint(localCentre), half, s_Hits, t.rotation, PartLayers.FitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = s_Hits[i];
                if (col == null || col.transform.IsChildOf(t)) continue;
                var other = col.GetComponentInParent<PartInstance>();
                if (other != null && other.Held) continue;
                if (other == null && SnapService.IsScan(col) && !HitsDeeper(col, t, localCentre, localSize, SnapService.ScanToleranceWorld)) continue;
                string name = other != null ? other.DisplayName : SurfaceName(col);
                if (!names.Contains(name)) { names.Add(name); colliders?.Add(col); }
            }
            return names;
        }

        static readonly Collider[] s_Deep = new Collider[32];

        /// A scan collider only counts where the box goes deeper into it than the scan's noise (`tol`, world metres):
        /// re-test with the box shrunk by tol on every side.
        public static bool HitsDeeper(Collider col, Transform t, Vector3 localCentre, Vector3 localSize, float tol)
        {
            float s = Mathf.Max(t.lossyScale.x, 1e-6f);
            var shrunk = Vector3.Max(localSize - Vector3.one * (2f * tol / s), Vector3.one * 1e-4f);
            var half = Vector3.Scale(shrunk * 0.5f, t.lossyScale);
            int n = Physics.OverlapBoxNonAlloc(t.TransformPoint(localCentre), half, s_Deep, t.rotation, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (s_Deep[i] == col) return true;
            return false;
        }

        /// "Gutter/Back" style names for scene colliders (parent/child), so callouts say what was hit.
        public static string SurfaceName(Collider c)
        {
            // The scan's collision mesh has glTF node names ("Collision r1/geometry_0"): call it what it is.
            if (SnapService.IsScan(c) && AirTools.Core.Services.TryGet<SceneRoot>(out var root))
                return string.IsNullOrEmpty(root.Site) ? "the scan" : $"the {root.Site} scan";
            // Path below the content root (the object directly under SceneRoot, or a top-level object).
            string name = c.transform.name;
            for (var p = c.transform.parent; p != null && p.parent != null && p.parent.GetComponent<SceneRoot>() == null; p = p.parent)
                name = p.name + "/" + name;
            return name;
        }

        static string Mm(float mm) => Mathf.Round(mm).ToString("0", C);
    }
}
