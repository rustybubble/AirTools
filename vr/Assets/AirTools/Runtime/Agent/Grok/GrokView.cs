using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Input;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// What the wearer is looking at and aiming at right now, gathered from the running app into a
    /// <see cref="ContextSnapshot"/> (the pure <see cref="GrokContext.Build"/> turns it into the agent context).
    /// Never throws and never waits: the thumb for frame_jpg_b64 comes from SceneStreamer's cache (a miss starts the
    /// download and the command goes without it).
    public static class GrokView
    {
        public static ContextSnapshot Snapshot(IList<Dictionary<string, string>> frames = null, string text = null)
        {
            var s = new ContextSnapshot
            {
                Frames = frames,
                Location = GrokState.Location,
                Address = GrokState.Address,
                Placement = GrokState.Placement,
                SurveyId = GrokLanes.SurveyId,
            };
            try { s.Measurement = PartsClient.MeasurementContext(); } catch (Exception e) { Log.Warn($"Context: measurement skipped ({e.Message})"); }

            if (Services.TryGet<PartTool>(out var tool))
            {
                // A part can lose its spec (e.g. after a script reload in Play mode): skip it rather than throw.
                if (tool.Selected != null && tool.Selected.Spec != null) s.SelectedPartId = tool.Selected.Spec.id;
                foreach (var p in tool.PlacedParts)
                    if (p != null && p.Spec != null) s.Placed.Add(new KeyValuePair<string, int>(p.Spec.id, tool.QuantityOf(p.Spec.id)));
            }
            if (Services.TryGet<PartsBrowser>(out var browser))
                foreach (var c in browser.Candidates) if (c != null && !string.IsNullOrEmpty(c.id)) s.CandidateIds.Add(c.id);
            if (Services.TryGet<ToolManager>(out var tm)) s.Tool = ToolName(tm.Active);
            try { s.Cavity = Gaps.CurrentContext(); s.Removed = Gaps.RemovedIds(); }   // e2e
            catch (Exception e) { Log.Warn($"Context: cavity skipped ({e.Message})"); }
            try { s.Opening = Openings.CurrentContext(); }   // assetgen: the opening taped last
            catch (Exception e) { Log.Warn($"Context: opening skipped ({e.Message})"); }

            var root = Services.TryGet<SceneRoot>(out var r) ? r : null;
            if (root == null || !root.IsRuntimePackage || string.IsNullOrEmpty(root.Site) || root.Content == null) return s;
            s.Site = root.Site;
            s.Scale = root.Calibration;
            s.ScaleSource = SiteScales.Wire(SiteScales.Consistent(root.Calibration,
                Services.TryGet<SceneStreamer>(out var scaleFrom) ? scaleFrom.ScaleSource : AirTools.Scene.ScaleSource.None));   // scalemodels
            s.SceneInView = AppState.Mode != AppMode.Passthrough && root.IsVisible;
            if (!s.SceneInView) return s;

            try { AddView(s, root, tool, text); }
            catch (Exception e) { Log.Warn($"Context: view fields skipped ({e.Message})"); }
            return s;
        }

        static void AddView(ContextSnapshot s, SceneRoot root, PartTool tool, string text)
        {
            var content = root.Content.transform;
            var aimed = AimedPoint();
            if (aimed.HasValue) s.PointerPackage = content.InverseTransformPoint(aimed.Value);

            var streamer = Services.Get<SceneStreamer>();
            if (streamer == null || streamer.Cameras.Count == 0) return;
            var cam = ViewCamera(streamer, content, s.Frames);
            if (cam == null) return;
            s.FrameId = cam.id;
            // On an install coach's drill step, G4's crosshair (its scene point in this photo); else the aimed point.
            if (GrokLanes.DrillActive) s.DrillPx = GrokLanes.DrillPxIn(cam);
            else if (s.PointerPackage.HasValue) s.DrillPx = GrokContext.PointInFrame(cam, s.PointerPackage.Value);
            var part = tool != null ? tool.Selected : null;
            if (part != null && part.Spec != null)
            {
                var corners = GrokContext.Corners(part.LocalBox);
                for (int i = 0; i < corners.Length; i++) corners[i] = content.InverseTransformPoint(part.transform.TransformPoint(corners[i]));
                s.PlacedBox = GrokContext.BoxInFrame(cam, corners);
            }
            if (WantsFrame(text))
            {
                s.FrameJpgB64 = FrameB64(streamer, cam, s.Frames);
                if (s.FrameJpgB64 != null) GrokLanes.LastFrameId = cam.id;   // G4 draws coach_check boxes on this frame
            }
        }

        /// A command sends the view's photo when it reads it ("what am I looking at?", "check this quote", "check it"),
        /// and every command (typed or spoken) while an install coach runs: check_step reads frame_jpg_b64.
        public static bool WantsFrame(string text)
        {
            bool coach = GrokLanes.CoachActive;
            return coach || GrokContext.NeedsFrame(text, coach);
        }

        /// The photo's original JPEG bytes as base64 (never re-encoded: the server's offline replay keys on their SHA1):
        /// the copy already going out in `frames` when it's that photo, else the downloaded thumb; null until downloaded.
        static string FrameB64(SceneStreamer streamer, SceneCameraJson cam, IList<Dictionary<string, string>> frames)
        {
            if (frames != null)
                foreach (var f in frames)
                    if (f != null && f.TryGetValue("id", out var id) && id == cam.id && f.TryGetValue("jpg_b64", out var b64) && !string.IsNullOrEmpty(b64))
                        return b64;
            return streamer.TryGetThumbBytes(cam.thumb, out var jpg) ? Convert.ToBase64String(jpg) : null;
        }

        /// The photo nearest the view: frames[0] when frames go with the command (the same pick), else
        /// SceneCameras.PickView from the gaze focus and the head.
        public static SceneCameraJson ViewCamera(SceneStreamer streamer, Transform content, IList<Dictionary<string, string>> frames = null)
        {
            if (streamer == null || content == null) return null;
            if (frames != null && frames.Count > 0 && frames[0] != null && frames[0].TryGetValue("id", out var id))
            {
                var sent = streamer.FindCamera(id);
                if (sent != null) return sent;
            }
            var focus = FocusPoint();
            var head = Camera.main != null ? content.InverseTransformPoint(Camera.main.transform.position) : Vector3.zero;
            var pick = SceneCameras.PickView(streamer.Cameras, focus.HasValue ? content.InverseTransformPoint(focus.Value) : (Vector3?)null, head, 1);
            return pick.Count > 0 ? streamer.Cameras[pick[0]] : null;
        }

        /// The gaze focus /scene/ask and voice frames use (SceneAsk.FocusPoint): the gaze hit, else the measure cursor.
        public static Vector3? FocusPoint()
        {
            if (Services.TryGet<SceneAsk>(out var ask)) return ask.FocusPoint();
            var cam = Camera.main;
            if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out var rh, 60f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                return rh.point;
            return null;
        }

        /// Where the right-hand tool ray meets the scene (world): the measure tool's snapped cursor while it shows one,
        /// else the right pointer's raycast. Null when nothing is aimed.
        public static Vector3? AimedPoint()
        {
            if (Services.TryGet<MeasureTool>(out var mt) && mt.Equipped && mt.Cursor.HasValue) return mt.Cursor.Value.point;
            if (Services.TryGet<IToolInput>(out var input) && input.HasPointer(ToolHand.Right))
            {
                var p = input.GetPointer(ToolHand.Right);
                if (Physics.Raycast(new Ray(p.position, p.forward), out var hit, 60f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                    return hit.point;
            }
            return null;
        }

        /// Keep the thumb of the photo nearest the view downloaded, so a "what am I looking at?" never waits for it.
        public static void Prefetch()
        {
            if (AppState.Mode == AppMode.Passthrough || !Services.TryGet<SceneRoot>(out var root) || !root.IsRuntimePackage || root.Content == null) return;
            if (!Services.TryGet<SceneStreamer>(out var streamer) || streamer.Cameras.Count == 0) return;
            var cam = ViewCamera(streamer, root.Content.transform);
            if (cam != null && !string.IsNullOrEmpty(cam.thumb)) streamer.TryGetThumbBytes(cam.thumb, out _);
        }

        public static string ToolName(ToolKind k) => k switch
        {
            ToolKind.Measure => "tape",
            ToolKind.Level => "level",
            ToolKind.Part => "part",
            ToolKind.Move => "move",
            _ => "none",
        };
    }
}
