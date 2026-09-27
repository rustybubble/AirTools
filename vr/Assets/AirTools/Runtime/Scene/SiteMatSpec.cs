using System;
using System.Globalization;
using UnityEngine;

namespace AirTools.Scene
{
    /// The printed A3 site mat (presence.md S1/P2), read from its QR payload, e.g.
    /// `airtools:mat:v1;w=0.420;h=0.297;qr=0.120;pad=0.300,0.150` (optional `model=x,z` and `parts=x,z`).
    /// Mat frame: origin at the QR code's centre, x along the mat's long edge, z across it (away from you), y up.
    /// Assumed print layout: the QR sits in the mat's near-left corner, `margin` in from both edges, so the mat's
    /// near-left corner is at (−qr/2 − margin, 0, −qr/2 − margin). `pad` is the drone helipad centre (S2); the 1:50
    /// model stands at `model` and the bought parts at `parts` (to the right of the model, clear of the helipad).
    [Serializable]
    public class SiteMatSpec
    {
        public const string Prefix = "airtools:mat:";
        public float width = 0.420f, height = 0.297f, qr = 0.120f, margin = 0.015f;
        public Vector2 pad = new Vector2(0.300f, 0.150f);
        public Vector2 model = new Vector2(0.115f, 0.095f);
        public Vector2 parts = new Vector2(0.265f, 0.005f);

        public Vector3 ModelSpot => new Vector3(model.x, 0f, model.y);
        public Vector3 PartsSpot => new Vector3(parts.x, 0f, parts.y);
        public Vector3 PadSpot => new Vector3(pad.x, 0f, pad.y);
        /// The mat's near-left corner (where the printed 1 m scale bar starts along the table edge).
        public Vector3 Corner => new Vector3(-qr * 0.5f - margin, 0f, -qr * 0.5f - margin);

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// Parses a mat payload; unknown fields are ignored, missing ones keep their defaults. False for anything
        /// that isn't an AirTools mat (other QR codes in the room).
        public static bool TryParse(string payload, out SiteMatSpec spec)
        {
            spec = null;
            if (string.IsNullOrEmpty(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            var parts = payload.Substring(Prefix.Length).Split(';');
            if (parts.Length == 0 || !parts[0].StartsWith("v1", StringComparison.Ordinal)) return false;
            var s = new SiteMatSpec();
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = parts[i].Substring(0, eq).Trim(), val = parts[i].Substring(eq + 1).Trim();
                switch (key)
                {
                    case "w": if (!Num(val, out s.width)) return false; break;
                    case "h": if (!Num(val, out s.height)) return false; break;
                    case "qr": if (!Num(val, out s.qr)) return false; break;
                    case "margin": if (!Num(val, out s.margin)) return false; break;
                    case "pad": if (!Vec(val, out s.pad)) return false; break;
                    case "model": if (!Vec(val, out s.model)) return false; break;
                    case "parts": if (!Vec(val, out s.parts)) return false; break;
                }
            }
            if (!(s.width > 0f) || !(s.height > 0f) || !(s.qr > 0f)) return false;
            spec = s;
            return true;
        }

        static bool Num(string v, out float f) => float.TryParse(v, NumberStyles.Float, C, out f);

        static bool Vec(string v, out Vector2 p)
        {
            p = default;
            var xy = v.Split(',');
            if (xy.Length != 2 || !Num(xy[0], out float x) || !Num(xy[1], out float y)) return false;
            p = new Vector2(x, y);
            return true;
        }

        /// The level mat frame from a QR anchor pose whose axis convention isn't documented: "up" is whichever pose
        /// axis is most parallel to world up (the mat lies flat), x is the pose's right axis (its forward if right is
        /// the vertical one) flattened onto the table; the frame is then exactly level. False when no axis is within
        /// maxTiltDeg of vertical (a QR on a wall, not the mat).
        public static bool MatFrame(Pose qrPose, out Pose frame, float maxTiltDeg = 20f)
        {
            frame = default;
            var r = qrPose.rotation;
            Vector3[] axes = { r * Vector3.right, r * Vector3.up, r * Vector3.forward };
            int upAxis = 0; float best = -1f;
            for (int i = 0; i < 3; i++)
            {
                float d = Mathf.Abs(Vector3.Dot(axes[i], Vector3.up));
                if (d > best) { best = d; upAxis = i; }
            }
            if (Mathf.Acos(Mathf.Clamp(best, -1f, 1f)) * Mathf.Rad2Deg > maxTiltDeg) return false;
            var x = upAxis == 0 ? axes[2] : axes[0];
            x = Vector3.ProjectOnPlane(x, Vector3.up);
            if (x.sqrMagnitude < 1e-6f) return false;
            x.Normalize();
            var z = Vector3.Cross(x, Vector3.up);
            frame = new Pose(qrPose.position, Quaternion.LookRotation(z, Vector3.up));
            return true;
        }
    }
}
