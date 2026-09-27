using System;
using System.Collections;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Agent
{
    /// "Ask the scene" (backend docs/api.md /scene/ask): sends the (up to 3) package photos nearest to what you're
    /// looking at — the drone's own thumbs/NNNN.jpg, with the camera id as the frame id — and a question. The answer
    /// comes back with a box in one frame; the pin ray goes through the box centre using that camera's full-frame
    /// intrinsics (u = cx·w…, OpenCV → world by Rᵀ, then the X flip), and the pin lands where it hits the collider.
    /// Without a box (or if that ray misses) the answer is pinned on the spot that was asked about.
    /// The voice agent's scene_pin action uses the same Pin. part_query can feed a parts search.
    public class SceneAsk : MonoBehaviour, IWorldLabelSource, ISiteScoped   // sitescope: ISiteScoped
    {
        public SceneRoot sceneRoot;
        public MeasureStyle style = new MeasureStyle();
        public int timeoutSeconds = 30;
        [Tooltip("Pin marker size in metres (at 1 m; grows with distance so it stays visible).")]
        public float pinSizePerMetre = 0.012f;

        public bool Busy { get; private set; }
        public SceneAnswer LastAnswer { get; private set; }
        public string LastError { get; private set; }
        /// HTTP status of the last ask (0 = no response).
        public long LastHttpCode { get; private set; } = -1;
        public List<string> LastFrameIds { get; } = new List<string>();
        public List<Pin> Pins { get; } = new List<Pin>();

        public class Pin
        {
            public string frameId, text;
            public Vector3 packagePoint;
            public GameObject marker;
            public MeasureLabel label;
            /// When it was pinned (Time.unscaledTime): new for 30 s (Declutter C, the label pool).
            public float at;
        }

        void OnEnable() { Services.Register(this); RegisterSite(); }   // sitescope: RegisterSite
        void OnDisable() { Services.Unregister(this); WorldLabels.Remove(this); SiteScope.Unregister(this); }   // sitescope: Unregister

        // Declutter C (S10): an answer pin's label is P1 while new (30 s), then P6 (passive) in WorldLabels' pool;
        // over the pool it drops to the pin itself.
        public const float NewPinSeconds = 30f;
        readonly List<Pin> m_Claimed = new List<Pin>();
        float m_NextAge;

        void Update()
        {
            if (m_NextAge > 0f && Time.unscaledTime >= m_NextAge) { m_NextAge = 0f; WorldLabels.Changed(this); }
        }

        public void ClaimLabels(List<LabelClaim> claims)
        {
            m_Claimed.Clear();
            m_NextAge = 0f;
            float now = Time.unscaledTime;
            foreach (var p in Pins)
            {
                if (p.label == null || p.marker == null) continue;   // counted while the pin exists (hidden scans too)
                bool fresh = now - p.at < NewPinSeconds;
                if (fresh && (m_NextAge <= 0f || p.at + NewPinSeconds < m_NextAge)) m_NextAge = p.at + NewPinSeconds;
                m_Claimed.Add(p);
                claims.Add(new LabelClaim(fresh ? LabelClass.Focus : LabelClass.Passive, 0, 1, p.at));
            }
        }

        public void ApplyLabels(LabelGrant[] grants, int first)
        {
            for (int i = 0; i < m_Claimed.Count; i++)
            {
                var label = m_Claimed[i].label;
                if (label == null) continue;
                bool show = grants[first + i].Items > 0;
                if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
            }
        }
        // end Declutter C

        SceneRoot Root => sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();

        /// What the wearer is looking at: the gaze ray's hit on the scene, else the measure cursor, else null.
        public Vector3? FocusPoint()
        {
            var cam = Camera.main;
            if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out var rh, 60f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                return rh.point;
            if (Services.TryGet<MeasureTool>(out var mt) && mt.Cursor.HasValue) return mt.Cursor.Value.point;
            return null;
        }

        /// Up to n frames [{id, jpg_b64}] of the photos that see the focus point, nearest first.
        public void GatherFrames(Vector3? focusWorld, int n, Action<List<Dictionary<string, string>>> done) =>
            StartCoroutine(GatherRoutine(focusWorld, n, done));

        IEnumerator GatherRoutine(Vector3? focusWorld, int n, Action<List<Dictionary<string, string>>> done)
        {
            var frames = new List<Dictionary<string, string>>();
            LastFrameIds.Clear();
            var root = Root;
            var streamer = Services.Get<SceneStreamer>();
            if (root == null || root.Content == null || streamer == null || streamer.Cameras.Count == 0) { done(frames); yield break; }
            var content = root.Content.transform;
            var cams = streamer.Cameras;
            // No focus (or no photo sees it): the photos taken nearest to where you stand. Same pick as context.frame_id.
            var head = Camera.main != null ? content.InverseTransformPoint(Camera.main.transform.position) : Vector3.zero;
            var pick = SceneCameras.PickView(cams, focusWorld.HasValue ? content.InverseTransformPoint(focusWorld.Value) : (Vector3?)null, head, n);
            foreach (int i in pick)
            {
                var c = cams[i];
                if (string.IsNullOrEmpty(c.thumb)) continue;
                byte[] jpg = null;
                bool finished = false;
                streamer.ThumbnailBytes(c.thumb, b => { jpg = b; finished = true; });
                while (!finished) yield return null;
                if (jpg == null) continue;
                frames.Add(new Dictionary<string, string> { ["id"] = c.id, ["jpg_b64"] = Convert.ToBase64String(jpg) });
                LastFrameIds.Add(c.id);
            }
            done(frames);
        }

        /// Ask about what you're looking at; pins the answer when the model located it.
        public void Ask(string question, Action<SceneAnswer> done = null) => StartCoroutine(AskRoutine(question, FocusPoint(), done));

        public void AskAt(string question, Vector3 focusWorld, Action<SceneAnswer> done = null) => StartCoroutine(AskRoutine(question, focusWorld, done));

        IEnumerator AskRoutine(string question, Vector3? focus, Action<SceneAnswer> done)
        {
            Busy = true; LastError = null; LastAnswer = null;
            string site = SiteScope.Current;   // sitescope: the answer pins on the scan it was asked about
            List<Dictionary<string, string>> frames = null;
            yield return GatherRoutine(focus, 3, f => frames = f);
            if (frames == null || frames.Count == 0)
            {
                Busy = false; LastError = "no scene photos for this view";
                UiToast.Show("No photos of this spot · look at something else", ColorRole.Warning);
                done?.Invoke(null);
                yield break;
            }
            string body = JsonConvert.SerializeObject(new { question, frames });
            using (var req = AirTools.Parts.PartsClient.Post("/scene/ask", body))
            {
                req.timeout = timeoutSeconds;
                yield return HttpDeadline.Send(req, idleSeconds: 0f, label: "POST /scene/ask");   // fix-ux: our clock (a vision call: no idle limit)
                bool ok = HttpDeadline.Ok(req);
                if (ok)
                {
                    try { LastAnswer = JsonConvert.DeserializeObject<SceneAnswer>(req.downloadHandler.text); }
                    catch (Exception ex) { LastError = $"answer unreadable: {ex.Message}"; }
                }
                LastHttpCode = req.responseCode;
                if (!ok) LastError = req.responseCode == 503 ? "Scene questions need the live model (the laptop is offline)"
                    : $"/scene/ask: {HttpDeadline.Error(req)} {AirTools.Parts.PartsClient.ServerDetail(req.downloadHandler?.text)}";
            }
            Busy = false;
            if (LastAnswer == null)
            {
                UiToast.Show(LastError == null ? "No answer came back · try again" : Copy.Error(ErrorSurface.Ask, LastError, LastHttpCode), ColorRole.Warning);
                done?.Invoke(null);
                yield break;
            }
            if (!SiteScope.IsCurrent(site))   // sitescope: another model loaded meanwhile: no pin on it
            {
                // switchclean: and no answer card either — it's about the model you left (the switch closed its windows).
                Log.Info($"Scene ask \"{question}\" answered after {site} was left: not shown or pinned on {SiteScope.Current} (\"{LastAnswer.answer}\")");
                done?.Invoke(LastAnswer);
                yield break;
            }
            UiToast.Reply(Copy.Clip(Copy.Clean(LastAnswer.answer), 80));
            Log.Info($"Scene ask \"{question}\" → \"{LastAnswer.answer}\" frame {LastAnswer.frame_id} box [{(LastAnswer.box != null ? string.Join(",", LastAnswer.box) : "-")}] query \"{LastAnswer.part_query}\"");
            bool located = LastAnswer.box != null && !string.IsNullOrEmpty(LastAnswer.frame_id)
                           && PinFromFrame(LastAnswer.frame_id, LastAnswer.box, LastAnswer.answer);
            if (!located && focus.HasValue) PinAtFocus(focus.Value, LastAnswer.answer);
            done?.Invoke(LastAnswer);
        }

        /// Cast the pin ray through the centre of `box` (normalised [x0,y0,x1,y1], top-left origin) in camera
        /// `frameId`'s photo, and pin `text` where it hits the scene. False if the camera or the hit is missing.
        public bool PinFromFrame(string frameId, float[] box, string text)
        {
            var root = Root;
            var streamer = Services.Get<SceneStreamer>();
            var cam = streamer != null ? streamer.FindCamera(frameId) : null;
            if (cam == null || root == null || root.Content == null || box == null || box.Length < 4)
            {
                LastError = $"no camera '{frameId}' in this scene package";
                Log.Warn($"Scene pin: {LastError}");
                return false;
            }
            var content = root.Content.transform;
            // The camera ray show_labels uses too (Grok G2: one helper for both).
            if (!AirTools.Agent.Grok.FrameRay.TryCast(content, cam, box, 500f, out var hit))
            {
                LastError = "the pin ray misses the scene";
                Log.Warn($"Scene pin: {LastError} (frame {frameId})");
                return false;
            }
            AddPin(frameId, content.InverseTransformPoint(hit.point), hit.normal, text);
            return true;
        }

        /// The model answered without locating it in a photo (the live vision model often leaves frame_id / box empty),
        /// or its pin ray missed: pin the answer on the spot the wearer asked about. Its frame id is "focus".
        public bool PinAtFocus(Vector3 focusWorld, string text)
        {
            var root = Root;
            if (root == null || root.Content == null) return false;
            var normal = Camera.main != null ? (Camera.main.transform.position - focusWorld).normalized : Vector3.up;
            if (SnapService.TrySnap(focusWorld, out var hit, 0.05f * root.transform.lossyScale.x, features: false))
            {
                focusWorld = hit.point;
                normal = hit.normal;
            }
            AddPin("focus", root.Content.transform.InverseTransformPoint(focusWorld), normal, text);
            return true;
        }

        void AddPin(string frameId, Vector3 packagePoint, Vector3 normalWorld, string text)
        {
            var content = Root.Content.transform;
            var go = new GameObject($"Pin {Pins.Count + 1}");
            go.transform.SetParent(content, false);   // follows calibration and the tabletop scale
            go.transform.localPosition = packagePoint;
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyImmediateSafe(head.GetComponent<Collider>());
            head.name = "Head";
            head.transform.SetParent(go.transform, false);
            var world = content.TransformPoint(packagePoint);
            var cam = Camera.main;
            float d = cam != null ? Vector3.Distance(cam.transform.position, world) : 2f;
            float size = pinSizePerMetre * Mathf.Max(d, 0.5f);
            head.transform.position = world + normalWorld.normalized * size * 2.5f;
            head.transform.localScale = Vector3.one * size / Mathf.Max(content.lossyScale.x, 1e-4f);
            var mr = head.GetComponent<MeshRenderer>();
            if (style.markerMaterial != null) mr.sharedMaterial = style.markerMaterial;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", UiTheme.Current.colors.info);   // D3: an answer pin is information (blue)
            mr.SetPropertyBlock(block);
            MeasureLabel label = null;
            if (!string.IsNullOrEmpty(text))
            {
                label = MeasureLabel.Create(go.transform, style, "Label");
                label.tabular = false;
                label.Priority = 3;
                label.Set(Copy.Clip(Copy.Clean(text), 60), 1.1f);
                label.SetAnchorWorld(world + normalWorld.normalized * size * 6f + Vector3.up * size * 2f);
            }
            Pins.Add(new Pin { frameId = frameId, text = text, packagePoint = packagePoint, marker = go, label = label, at = Time.unscaledTime });
            Log.Info($"Scene pin #{Pins.Count} from frame {frameId} at package {packagePoint:F3}: {text}");
            WorldLabels.Changed(this);   // Declutter C
        }

        // ---------------- sitescope: answer pins belong to the site they were pinned on ----------------

        /// One site's pins while another site is loaded: parked under an inactive holder (they live in the package frame,
        /// under the content a fresh load replaces), and put back under the site's content when it loads again.
        sealed class SiteState { public readonly List<Pin> Pins = new List<Pin>(); }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);
        Transform m_Parking;

        Transform Parking
        {
            get
            {
                if (m_Parking != null) return m_Parking;
                var go = new GameObject("ParkedPins");
                go.SetActive(false);
                go.transform.SetParent(transform, false);
                return m_Parking = go.transform;
            }
        }

        public int ParkedPins(string site) => Sites.Peek(site)?.Pins.Count ?? 0;

        public void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderGrok);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            SiteState park = null;
            for (int i = Pins.Count - 1; i >= 0; i--) if (Pins[i].marker == null) Pins.RemoveAt(i);
            if (Pins.Count > 0)
            {
                park = new SiteState();
                park.Pins.AddRange(Pins);
                foreach (var p in Pins) p.marker.transform.SetParent(Parking, false);   // local = package space: kept
                Pins.Clear();
            }
            var back = Sites.Swap(to, park);
            var root = Root;
            if (back != null)
                foreach (var p in back.Pins)
                {
                    if (p.marker == null) continue;
                    if (root != null && root.Content != null) p.marker.transform.SetParent(root.Content.transform, false);
                    Pins.Add(p);
                }
            m_Claimed.Clear();
            WorldLabels.Changed(this);
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll())
                foreach (var p in st.Pins) if (p.marker != null) Destroy(p.marker);
        }
        // end sitescope

        public void ClearPins()
        {
            foreach (var p in Pins) if (p.marker != null) Destroy(p.marker);
            Pins.Clear();
            WorldLabels.Changed(this);   // Declutter C
        }

        static void DestroyImmediateSafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }
    }
}
