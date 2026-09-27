using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// assetgen: an opening taped with the real Measure tool — a window's (or a door's) width tape across its jambs and
    /// height tape from sill to head, or one rectangle shape round it. SceneRoot space: Centre on the wall's face
    /// (Openings refines it against the scan), Right / Up / Out the placement frame's axes (Out toward the viewer, out of
    /// the wall; Right = Cross(Out, Up)), W × H the tapes' readings in scene metres, D the depth when taped (NaN when not).
    public struct TapedOpening
    {
        public string Id;
        public Vector3 Centre, Right, Up, Out;
        public float W, H, D;
        /// "tapes" (a width and a height tape) or "shape" (a rectangle); "+ wall" once its plane is the wall's (OnFace).
        public string Source;
        /// The notebook entries it came from (the height tape second; 0 for a shape).
        public int EntryA, EntryB;
        /// The width and height tapes' midpoints and spans (end − start), SceneRoot space: OnFace measures W × H again
        /// along the wall (a tape whose ends snapped at different depths reads a little long).
        public Vector3 WidthMid, WidthSpan, HeightMid, HeightSpan;
        /// Its plane, Out and W × H come from the wall around it (Openings.RefinePlane), not the tapes alone.
        public bool OnWall;

        public bool Valid => W > 0f && H > 0f;
        public Vector3 SizeM => new Vector3(W, H, D);

        public override string ToString() =>
            $"{Id} {W * 1000f:0} × {H * 1000f:0} mm at {Centre.ToString("F3")} out {Out.ToString("F2")} ({Source})";
    }

    /// assetgen: the maths of a taped opening and a part going into it (docs: PlacementEditor "Openings"). Pure: struct
    /// maths only, runs in the offline test runner.
    /// - Recognising one: a near-horizontal and a near-vertical tape that cross the same hole (FromTapes), or a
    ///   rectangle shape standing on a wall (FromQuad).
    /// - Which parts it takes (ForOpening: a part at least 60 % of the opening each way is for it; smaller ones — a
    ///   window AC on the sill — are placed freely as before) and whether one fits properly (FitsProperly: no more than
    ///   3 mm over and no more than 2″ under on width and height). A part that fits properly snaps into the opening:
    ///   its front face in the wall's plane, centred, square to the opening.
    /// - Its fit in words (Fit: "✓ Fits the opening · ½″ spare", "Too wide by ⅜″", "Loose in the opening · 2¾″ spare")
    ///   and the size a made-to-measure part gets (FitToOpeningMm: the opening less ¼″ each side).
    public static class OpeningMath
    {
        /// A tape this close to horizontal is a width; this close to vertical a height.
        public const float AxisToleranceDeg = 20f;
        /// Where the two tapes may miss each other: the height tape off the width tape's span, and so on (metres).
        public const float CrossSlack = 0.15f;
        /// The two tapes' distance apart through the wall (metres): more is two different walls.
        public const float DepthSlack = 0.25f;
        public const float MinSize = 0.2f, MaxSize = 4f;

        /// Over by up to this on width or height still goes in (a tape reads ±3 mm).
        public const float TightMm = 3f;
        /// More than this under on width or height leaves a gap to fill: not made for the opening (2″). The server's
        /// search uses the same (search.OPENING_LOOSE_MM).
        public const float LooseMm = 50.8f;
        /// A made-to-measure part is this much under the opening on each side (¼″): ½″ in all, the usual allowance.
        public const float ClearancePerSideMm = 6.35f;
        /// A part at least this fraction of the opening's width and height is for it.
        public const float ForFraction = 0.6f;
        /// How far round the opening a part's box centre may be and still be "at" it (metres): the width / height slack
        /// and the depth either side of the wall's face.
        public const float RegionSlack = 0.1f, RegionDepth = 0.35f;
        /// The opening's volume for the fit check runs this deep behind the wall's face: no back wall to hit.
        public const float VolumeDepth = 1f;

        static Vector3 Flat(Vector3 v, Vector3 up) => v - up * Vector3.Dot(v, up);

        /// Whether a→b is a width (horizontal within AxisToleranceDeg) or a height (vertical within it) tape.
        public static bool IsHorizontal(Vector3 a, Vector3 b, Vector3 up)
        {
            var d = b - a;
            float len = d.magnitude;
            return len > 1e-4f && Mathf.Abs(Vector3.Dot(d, up)) / len <= Mathf.Sin(AxisToleranceDeg * Mathf.Deg2Rad);
        }

        public static bool IsVertical(Vector3 a, Vector3 b, Vector3 up)
        {
            var d = b - a;
            float len = d.magnitude;
            return len > 1e-4f && Mathf.Abs(Vector3.Dot(d, up)) / len >= Mathf.Cos(AxisToleranceDeg * Mathf.Deg2Rad);
        }

        /// The opening a width tape (w0 → w1) and a height tape (h0 → h1) measured, facing `viewer` (the side they
        /// were taped from). False when they aren't a width and a height of the same hole.
        public static bool FromTapes(Vector3 w0, Vector3 w1, Vector3 h0, Vector3 h1, Vector3 viewer, out TapedOpening o)
        {
            o = default;
            var up = Vector3.up;
            if (!IsHorizontal(w0, w1, up) || !IsVertical(h0, h1, up)) return false;
            var along = Flat(w1 - w0, up);
            if (along.sqrMagnitude < 1e-8f) return false;
            along.Normalize();
            var mid = (w0 + w1 + h0 + h1) * 0.25f;
            var outward = Vector3.Cross(along, up).normalized;
            if (Vector3.Dot(outward, viewer - mid) < 0f) outward = -outward;
            var right = Vector3.Cross(outward, up).normalized;
            float W = Vector3.Distance(w0, w1), H = Vector3.Distance(h0, h1);
            if (W < MinSize || W > MaxSize || H < MinSize || H > MaxSize) return false;

            // The height tape crosses the width tape's span, and the width tape the height tape's.
            float r0 = Vector3.Dot(w0, right), r1 = Vector3.Dot(w1, right);
            float hr = Vector3.Dot((h0 + h1) * 0.5f, right);
            if (hr < Mathf.Min(r0, r1) - CrossSlack || hr > Mathf.Max(r0, r1) + CrossSlack) return false;
            float u0 = Vector3.Dot(h0, up), u1 = Vector3.Dot(h1, up);
            float wu = Vector3.Dot((w0 + w1) * 0.5f, up);
            if (wu < Mathf.Min(u0, u1) - CrossSlack || wu > Mathf.Max(u0, u1) + CrossSlack) return false;
            float o1 = Vector3.Dot((w0 + w1) * 0.5f, outward), o2 = Vector3.Dot((h0 + h1) * 0.5f, outward);
            if (Mathf.Abs(o1 - o2) > DepthSlack) return false;

            float cr = (r0 + r1) * 0.5f, cu = (u0 + u1) * 0.5f, co = (o1 + o2) * 0.5f;
            o = new TapedOpening
            {
                Centre = right * cr + up * cu + outward * co,
                Right = right, Up = up, Out = outward, W = W, H = H, D = float.NaN, Source = "tapes",
                WidthMid = (w0 + w1) * 0.5f, WidthSpan = w1 - w0, HeightMid = (h0 + h1) * 0.5f, HeightSpan = h1 - h0,
            };
            return true;
        }

        /// A rectangle shape (4 corners in order) standing on a wall: its sides within 10° of square, two of them
        /// within AxisToleranceDeg of horizontal. W × H = the mean of the opposite sides.
        public static bool FromQuad(IReadOnlyList<Vector3> p, Vector3 viewer, out TapedOpening o)
        {
            o = default;
            if (p == null || p.Count != 4) return false;
            var up = Vector3.up;
            Vector3 e0 = p[1] - p[0], e1 = p[2] - p[1], e2 = p[3] - p[2], e3 = p[0] - p[3];
            float c0 = Vector3.Angle(e0, -e3), c1 = Vector3.Angle(e1, -e0);
            if (Mathf.Abs(c0 - 90f) > 10f || Mathf.Abs(c1 - 90f) > 10f) return false;
            Vector3 a0 = p[0], a1 = p[1], a2 = p[2], a3 = p[3];
            // The horizontal pair: e0 / e2, or e1 / e3.
            if (!IsHorizontal(a0, a1, up))
            {
                if (!IsHorizontal(a1, a2, up)) return false;
                (a0, a1, a2, a3) = (a1, a2, a3, a0);
            }
            if (!IsVertical(a1, a2, up)) return false;
            // A width tape along the middle of the rectangle, a height tape up its middle.
            var w0 = (a0 + a3) * 0.5f; var w1 = (a1 + a2) * 0.5f;
            var h0 = (a0 + a1) * 0.5f; var h1 = (a2 + a3) * 0.5f;
            if (!FromTapes(w0, w1, h0, h1, viewer, out o)) return false;
            o.W = (Vector3.Distance(a0, a1) + Vector3.Distance(a2, a3)) * 0.5f;
            o.H = (Vector3.Distance(a1, a2) + Vector3.Distance(a3, a0)) * 0.5f;
            o.Source = "shape";
            return true;
        }

        /// A point in the opening's (right, up, out) coordinates from its centre.
        public static Vector3 Local(TapedOpening o, Vector3 p)
        {
            var q = p - o.Centre;
            return new Vector3(Vector3.Dot(q, o.Right), Vector3.Dot(q, o.Up), Vector3.Dot(q, o.Out));
        }

        /// A part's box centre is at the opening: within its width and height (+ RegionSlack) and RegionDepth of the
        /// wall's face.
        public static bool AtOpening(TapedOpening o, Vector3 point)
        {
            var l = Local(o, point);
            return Mathf.Abs(l.x) <= o.W * 0.5f + RegionSlack && Mathf.Abs(l.y) <= o.H * 0.5f + RegionSlack && Mathf.Abs(l.z) <= RegionDepth;
        }

        /// A part of this size (metres: width, height) is for the opening (not a smaller thing placed near it).
        public static bool ForOpening(float partW, float partH, TapedOpening o) =>
            o.Valid && partW >= ForFraction * o.W - 1e-4f && partH >= ForFraction * o.H - 1e-4f;

        /// It goes in and is made for it: spare on width and height between −TightMm and +LooseMm (millimetres in).
        public static bool FitsProperly(float partWmm, float partHmm, float openWmm, float openHmm)
        {
            const float Eps = 0.05f;   // float noise at the limits
            float sw = openWmm - partWmm, sh = openHmm - partHmm;
            return sw >= -TightMm - Eps && sh >= -TightMm - Eps && sw <= LooseMm + Eps && sh <= LooseMm + Eps;
        }

        public static bool FitsProperly(PartDims d, TapedOpening o) => d != null && FitsProperly(d.w, d.h, o.W * 1000f, o.H * 1000f);

        /// The size a made-to-measure part gets: the opening less ClearancePerSideMm each side, its own depth (mm).
        public static Vector3 FitToOpeningMm(Vector3 openingMm, float depthMm) =>
            new Vector3(openingMm.x - 2f * ClearancePerSideMm, openingMm.y - 2f * ClearancePerSideMm, depthMm);

        /// The placement frame: origin at the centre of the opening on the wall's face, Up, Out toward the viewer.
        public static PlacementFrame Frame(TapedOpening o) => PlacementMath.Make(o.Centre, o.Up, o.Out, "opening");

        /// The opening as a cavity for PlacementMath.Clearance: VolumeDepth deep behind the wall's face.
        public static CavityVolume Volume(TapedOpening o) => new CavityVolume
        {
            Centre = o.Centre - o.Out * (VolumeDepth * 0.5f), Right = o.Right, Up = o.Up, Out = o.Out,
            Half = new Vector3(o.W * 0.5f, o.H * 0.5f, VolumeDepth * 0.5f), OpenTop = false,
        };

        /// The fit in the opening: PlacementMath.CavityFit's rules and words for "the opening", plus amber "Loose" for a
        /// part made for a smaller opening (more than LooseMm under on width or height).
        public static FitReport Fit(CavityClearance k)
        {
            var r = PlacementMath.CavityFit(k, noun: "opening");
            float loose = Mathf.Max(k.SpareW, k.SpareH);
            if (r.Status == FitStatus.Green && loose * 1000f > LooseMm)
            {
                string axis = k.SpareW >= k.SpareH ? "width" : "height";
                r.Add(FitStatus.Amber, $"Loose: {loose * 1000f:0} mm spare in {axis}");
                r.Say(FitStatus.Amber, $"Loose in the opening · {Copy.Gap(loose * 1000f)} spare", $"Made for a smaller opening · {axis}",
                    FitAction.SeeOthers, "See other models");
            }
            return r;
        }

        /// The best candidate for the opening: the first that fits properly (the search's own order), else the one over
        /// by least, else the first. −1 for none.
        public static int Best(IList<PartDims> parts, TapedOpening o)
        {
            if (parts == null || parts.Count == 0) return -1;
            int least = -1;
            float leastOver = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                var d = parts[i];
                if (d == null || d.w <= 0f || d.h <= 0f) continue;
                if (FitsProperly(d, o)) return i;
                float over = Mathf.Max(d.w - o.W * 1000f, d.h - o.H * 1000f);
                float miss = over > 0f ? over : -over + 1000f;   // too small ranks after anything that's over
                if (miss < leastOver) { leastOver = miss; least = i; }
            }
            return least >= 0 ? least : 0;
        }

        /// The wall face a taped opening sits in isn't where the tapes are: they land in the reveal (jambs, sill, head),
        /// often at different depths — the gate's live tapes: one width end at the face, the other 10 cm in, a 3.8° tilt.
        /// Openings.RefinePlane casts rays at the wall just outside the opening from both sides of the tapes; FitFace
        /// turns one side's hits into a plane, ChooseFace picks the face the tapes were taken at, OnFace puts the opening
        /// on it. All pure.

        /// A face more than this behind the tapes' centre (metres; the tapes would float in front of the wall) isn't
        /// theirs: they lie in its reveal, behind or on it.
        public const float BehindSlack = 0.05f;
        /// A face farther than this in front of the tapes' centre (a reveal deeper than 35 cm) is something else.
        public const float FaceReach = 0.35f;
        /// Hits on one face lie within this of their plane (metres; a scan's wall is a few mm rough).
        public const float FaceAgree = 0.025f;
        /// A hit whose normal is farther than this from the side the ray came from isn't the wall's face (a jamb, a sill).
        public const float FaceNormalDeg = 30f;

        /// One side's hits (points and normals, SceneRoot space; rays cast from the `side` direction) as a plane: its
        /// normal the mean of the hit normals facing `side` (made level: walls stand upright), its offset (dot with the
        /// normal) the mean of the hits within FaceAgree of their median. False with fewer than two agreeing hits.
        public static bool FitFace(Vector3[] pts, Vector3[] normals, int n, Vector3 side, out Vector3 normal, out float offset)
        {
            normal = default; offset = 0f;
            var up = Vector3.up;
            side = Flat(side, up).normalized;
            float cos = Mathf.Cos(FaceNormalDeg * Mathf.Deg2Rad);
            var sum = Vector3.zero;
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                var nn = Flat(normals[i], up);
                if (nn.sqrMagnitude < 1e-8f) continue;
                nn.Normalize();
                if (Vector3.Dot(nn, side) < cos) continue;
                sum += nn;
                s_Keep[m++] = i;
            }
            if (m < 2 || sum.sqrMagnitude < 1e-8f) return false;
            normal = sum.normalized;
            for (int k = 0; k < m; k++) s_Off[k] = Vector3.Dot(pts[s_Keep[k]], normal);
            Array.Sort(s_Off, 0, m);
            float median = s_Off[m / 2], total = 0f;
            int agree = 0;
            for (int k = 0; k < m; k++) if (Mathf.Abs(s_Off[k] - median) <= FaceAgree) { total += s_Off[k]; agree++; }
            if (agree < 2) return false;
            offset = total / agree;
            return true;
        }

        static readonly int[] s_Keep = new int[16];
        static readonly float[] s_Off = new float[16];

        /// Where the tapes' `centre` is from a face (normal, offset): + in front of it (the face behind the tapes), − behind
        /// it (the tapes in its reveal).
        public static float FromFace(Vector3 centre, Vector3 normal, float offset) => Vector3.Dot(centre, normal) - offset;

        /// Of the two faces found (either may be missing), the one the tapes were taken at: not more than BehindSlack
        /// behind their centre, not more than FaceReach in front of it, and the nearer of two.
        public static bool ChooseFace(bool front, Vector3 nF, float dF, bool back, Vector3 nB, float dB, Vector3 centre,
            out Vector3 normal, out float offset)
        {
            normal = default; offset = 0f;
            float fF = front ? FromFace(centre, nF, dF) : float.NaN, fB = back ? FromFace(centre, nB, dB) : float.NaN;
            bool okF = front && fF <= BehindSlack && fF >= -FaceReach, okB = back && fB <= BehindSlack && fB >= -FaceReach;
            if (!okF && !okB) return false;
            if (okF && (!okB || Mathf.Abs(fF) <= Mathf.Abs(fB))) { normal = nF; offset = dF; }
            else { normal = nB; offset = dB; }
            return true;
        }

        /// The opening on the wall's face: Out is the face's normal (toward the side it faces), Right = Out × Up, the
        /// centre across and up from the tapes' midpoints and on the face, W × H the tapes' spans along Right and Up.
        public static void OnFace(ref TapedOpening o, Vector3 normal, float offset)
        {
            var up = Vector3.up;
            var outward = Flat(normal, up).normalized;
            var right = Vector3.Cross(outward, up).normalized;
            o.Centre = right * Vector3.Dot(o.WidthMid, right) + up * Vector3.Dot(o.HeightMid, up) + outward * offset;
            o.Out = outward;
            o.Right = right;
            o.Up = up;
            if (o.WidthSpan.sqrMagnitude > 1e-8f) o.W = Mathf.Abs(Vector3.Dot(o.WidthSpan, right));
            if (o.HeightSpan.sqrMagnitude > 1e-8f) o.H = Mathf.Abs(Vector3.Dot(o.HeightSpan, up));
            o.OnWall = true;
            if (o.Source != null && !o.Source.EndsWith("+ wall")) o.Source += " + wall";
        }

        /// context.opening / the search's `opening` (backend models.Opening): the tapes' metres (real metres at the scene's
        /// calibration), to the millimetre.
        public static Dictionary<string, object> Context(TapedOpening o)
        {
            var d = new Dictionary<string, object>
            {
                ["w_m"] = Math.Round(o.W, 3), ["h_m"] = Math.Round(o.H, 3), ["label"] = "taped opening",
            };
            if (!float.IsNaN(o.D) && o.D > 0f) d["d_m"] = Math.Round(o.D, 3);
            return d;
        }

        /// "59 × 47¼″ opening" (the user's unit) for toasts and the panel.
        public static string Words(TapedOpening o) => $"{Units.FormatPair(o.W, o.H, UiSettings.UnitSystem)} opening";
    }
}
