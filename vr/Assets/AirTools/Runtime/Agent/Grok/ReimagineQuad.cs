using System;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Video;

namespace AirTools.Agent.Grok
{
    /// The reimagine / walk-in quad (lane G3; backend docs/api.md show_reimagined, flythrough_started, show_video):
    /// - show_reimagined: the AI edit on a world quad at its capture camera's pose, 1.5 m out, so it lines up with the
    ///   scan behind it (CameraQuad: the api's recipe with the glTF→Unity X flip, on SceneRoot.Content so calibration
    ///   and the tabletop scale follow). A pinch on the picture flips to the original frame's thumb and back; the label
    ///   ("AI preview, not to scale") and the step / "say “undo”" line stay under it. No scene or camera: it floats in
    ///   front of the person instead.
    /// - flythrough_started: a spinner on the quad while GET /scene/flythrough/{job_id} is polled every 3 s; the poster
    ///   shows once the job names it; failed → the still with the backend's line.
    /// - show_video: the clip loops (VideoPlayer, URL source, into a RenderTexture) on the quad, floated in front of the
    ///   person (the clip starts at another camera's pose); the poster shows until the first frame.
    public class ReimagineQuad : MonoBehaviour
    {
        public Transform head;
        [Tooltip("Unit Quad (1 × 1 m) that shows the picture / video.")]
        public MeshRenderer image;
        [Tooltip("Ray + poke target over the picture (built 1 × 1 m): a pinch flips before / after.")]
        public GlassButton toggle;
        [Tooltip("Under the picture: the label pill, the honesty label and the state line.")]
        public Transform labelBar;
        public GlassSurface labelPill;
        public TextMeshPro label;
        public TextMeshPro state;
        public GlassButton flipButton, closeButton;
        public LineRenderer spinner;
        public VideoPlayer video;
        public float distance = CameraQuad.Distance;
        [Tooltip("Floating (no camera / the walk-in clip): width in metres.")]
        public float floatingWidth = 1.2f;

        public enum QuadMode { Hidden, Still, Rendering, Video }
        public QuadMode Mode { get; private set; } = QuadMode.Hidden;
        public bool Registered { get; private set; }
        public bool ShowingBefore { get; private set; }
        public ReimagineView Current { get; private set; }
        public VideoView CurrentVideo { get; private set; }
        public QuadPose Pose { get; private set; }
        public string ImageUrl { get; private set; }
        public string BeforeUrl { get; private set; }
        public string LastError { get; private set; }
        public int Shows { get; private set; }
        public bool VideoPlaying => video != null && video.isPlaying;
        public readonly JobPoll FlyPoll = new JobPoll { Interval = 3f, Timeout = 330f };

        QuadPose m_LocalPose;    // package space (registered) or world (floating)
        GameObject m_Content;    // the scan content it is registered to
        float m_Aspect = 16f / 9f;
        RenderTexture m_VideoTexture;
        MaterialPropertyBlock m_Block;
        string m_StateLine;
        float m_RenderStart;
        int m_ShownSecs = -1;
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        static GrokClient Client => Services.TryGet<GrokClient>(out var c) ? c : null;

        void OnEnable()
        {
            Services.Register(this);
            if (toggle != null) toggle.Clicked += Flip;
            if (flipButton != null) flipButton.Clicked += Flip;
            if (closeButton != null) closeButton.Clicked += Hide;
            if (video != null) { video.prepareCompleted += OnPrepared; video.errorReceived += OnVideoError; }
            AppState.Changed += OnMode;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (toggle != null) toggle.Clicked -= Flip;
            if (flipButton != null) flipButton.Clicked -= Flip;
            if (closeButton != null) closeButton.Clicked -= Hide;
            if (video != null) { video.prepareCompleted -= OnPrepared; video.errorReceived -= OnVideoError; }
            AppState.Changed -= OnMode;
        }

        void OnDestroy()
        {
            if (m_VideoTexture != null) { m_VideoTexture.Release(); Destroy(m_VideoTexture); }
            if (m_Thumb != null) Destroy(m_Thumb);
        }

        void Start() { if (Mode == QuadMode.Hidden) SetVisible(false); }

        /// Leaving the world takes the picture down (it belongs to the scan).
        void OnMode(AppMode from, AppMode to)
        {
            if (to == AppMode.Passthrough && Mode != QuadMode.Hidden) Hide();
        }

        // ---------------- the actions ----------------

        public bool ShowReimagined(ReimagineView v)
        {
            if (v == null) return false;
            MarkShown();   // sitescope
            FlyPoll.Cancel();
            StopVideo();
            Current = v;
            CurrentVideo = null;
            ShowingBefore = false;
            Shows++;
            ImageUrl = v.ImageUrl;
            var streamer = Services.TryGet<SceneStreamer>(out var s) ? s : null;
            // The loaded cameras.r<rev>.json entry (every intrinsic, the thumb path) when it is the edit's camera.
            var loaded = streamer != null && !string.IsNullOrEmpty(v.FrameId) ? streamer.FindCamera(v.FrameId) : null;
            var cam = CameraQuad.Match(loaded, v.Camera);
            BeforeUrl = v.IsOriginal ? null : ReimagineView.ThumbUrl(streamer?.Site, cam?.thumb, v.FrameId);
            Place(cam);
            m_StateLine = v.StateLine();
            SetMode(QuadMode.Still);
            Load(ImageUrl);
            Log.Info($"G3 reimagine: step {v.Step} frame {v.FrameId} {(Registered ? "at its camera" : "floating")} ({Pose.Width:0.00} × {Pose.Height:0.00} m) {ImageUrl}");
            return true;
        }

        /// flythrough_started {job_id}: spinner, then poll every 3 s.
        public bool StartFlythrough(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return false;
            MarkShown();   // sitescope
            if (Mode == QuadMode.Hidden) Float(16f / 9f);
            FlyPoll.Start(jobId, Time.unscaledTime);
            m_RenderStart = Time.unscaledTime;
            m_ShownSecs = -1;
            SetMode(QuadMode.Rendering);
            Log.Info($"G3 walk-in {jobId}: rendering, polling every {FlyPoll.Interval:0} s");
            return true;
        }

        public bool ShowVideo(VideoView v)
        {
            if (v == null) return false;
            if (CurrentVideo != null && v.VideoUrl == CurrentVideo.VideoUrl && Mode == QuadMode.Video) return true;   // the rider after our own poll
            MarkShown();   // sitescope (the walk-in poll waits while its site is parked, so this is the loaded site's)
            FlyPoll.Cancel();
            CurrentVideo = v;
            Current = null;
            ShowingBefore = false;
            Shows++;
            Float(848f / 480f);   // the clip starts at another camera's pose: float it in front of the person
            m_StateLine = "Walk-in preview" + (string.IsNullOrEmpty(v.ShareUrl) ? "" : " · shareable link ready");
            BeforeUrl = null;
            ImageUrl = v.PosterUrl;
            SetMode(QuadMode.Video);
            if (!string.IsNullOrEmpty(v.PosterUrl)) Load(v.PosterUrl);
            PlayVideo(v.VideoUrl);
            Log.Info($"G3 walk-in video: {v.VideoUrl} (poster {v.PosterUrl})");
            return true;
        }

        public void Hide()
        {
            FlyPoll.Cancel();
            StopVideo();
            SetMode(QuadMode.Hidden);
        }

        /// Before (the frame's thumb) ↔ after (the edit). Nothing to flip at step 0 or for a clip.
        public void Flip()
        {
            if (Mode != QuadMode.Still || string.IsNullOrEmpty(BeforeUrl)) return;
            ShowingBefore = !ShowingBefore;
            Load(ShowingBefore ? BeforeUrl : ImageUrl);
            Refresh();
        }

        // ---------------- polling ----------------

        void Update()
        {
            if (spinner != null && spinner.gameObject.activeSelf)
                spinner.transform.localRotation = Quaternion.Euler(0f, 0f, -Time.unscaledTime * 240f);
            if (Mode == QuadMode.Rendering && state != null)
            {
                int secs = Mathf.FloorToInt(Time.unscaledTime - m_RenderStart);
                if (secs != m_ShownSecs) { m_ShownSecs = secs; state.text = $"Rendering the walk-in… {secs} s"; }   // once a second, not per frame
            }
            if (Away) return;   // sitescope: the walk-in's poll waits for its site
            if (!FlyPoll.Due(Time.unscaledTime)) return;
            var client = Client;
            string id = FlyPoll.JobId;
            if (client == null) { FlyPoll.OnReply(0, null, Time.unscaledTime); return; }
            client.GetJson($"/scene/flythrough/{Uri.EscapeDataString(id)}", (code, text) =>
            {
                if (FlyPoll.JobId != id) return;
                var st = code >= 200 && code < 300 ? FlythroughStatus.Parse(text) : null;
                if (Away)   // sitescope: its site was left while this reply was in flight: ask again once it's back
                {
                    FlyPoll.OnReply(code, st != null && st.Status == "done" ? "running" : st?.Status, Time.unscaledTime);
                    return;
                }
                var ps = FlyPoll.OnReply(code, st?.Status, Time.unscaledTime);
                if (st != null && !string.IsNullOrEmpty(st.PosterUrl) && ImageUrl != st.PosterUrl && Mode == QuadMode.Rendering && Current == null)
                {
                    ImageUrl = st.PosterUrl;
                    Load(st.PosterUrl);
                }
                if (ps == PollState.Done && st != null) ShowVideo(st.ToVideo());
                else if (ps == PollState.Failed || ps == PollState.TimedOut)
                {
                    // The reimagined end frame is the still (poster_url stays valid on a failed render).
                    m_StateLine = st?.Spoken ?? "I couldn't render that; here's the still.";
                    SetMode(QuadMode.Still);
                    UiToast.Show(m_StateLine, ColorRole.Warning);
                    Log.Warn($"G3 walk-in {id}: {ps} {st?.Error}");
                }
            });
        }

        // ---------------- placement ----------------

        void Place(SceneCameraJson cam)
        {
            var root = Services.TryGet<SceneRoot>(out var r) ? r : null;
            var content = root != null && root.Content != null ? root.Content.transform : null;
            // `distance` scene metres out, in package units (the content may carry a "set scale" calibration).
            if (content != null && CameraQuad.TryCompute(cam, distance / (root.Calibration > 0f ? root.Calibration : 1f), out var local))
            {
                m_LocalPose = local;
                m_Content = root.Content;
                Registered = true;
                m_Aspect = local.Width / Mathf.Max(1e-4f, local.Height);
            }
            else Float(16f / 9f);
            ApplyPose();
        }

        void Float(float aspect)
        {
            var h = head != null ? head : Camera.main != null ? Camera.main.transform : null;
            var eye = h != null ? h.position : new Vector3(0f, 1.6f, 0f);
            var gaze = h != null ? h.forward : Vector3.forward;
            m_Aspect = aspect;
            m_LocalPose = CameraQuad.Floating(eye, gaze, distance, 8f, floatingWidth, aspect);
            Registered = false;
            ApplyPose();
        }

        void LateUpdate()
        {
            if (!ReferenceEquals(m_Site, SiteScope.Current)) FollowSite();   // sitescope
            if (Away) return;   // sitescope: parked with its site
            if (Mode != QuadMode.Hidden && Registered) ApplyPose();   // follows calibration and the tabletop scale
        }

        // ---------------- sitescope: the picture belongs to the site it was made for ----------------

        string m_Site, m_ShownSite;

        /// Parked: what it shows belongs to another site (hidden, its walk-in poll and video paused).
        public bool Away { get; private set; }
        /// The site the still, walk-in or clip was made for.
        public string ShownSite => m_ShownSite;

        void MarkShown() { m_Site = m_ShownSite = SiteScope.Current; Away = false; }

        /// Another site loaded: the quad hides (a still registered to the scan's camera, a floating render or clip)
        /// with its state kept; back on its site it shows again as it was, re-registered to the site's new content.
        void FollowSite()
        {
            m_Site = SiteScope.Current;
            if (Mode == QuadMode.Hidden || m_ShownSite == null) { Away = false; return; }
            bool away = m_Site != m_ShownSite;
            if (away == Away) return;
            Away = away;
            if (away)
            {
                SetVisible(false);
                if (video != null && video.isPlaying) video.Pause();
                Log.Info($"G3 reimagine: parked with {m_ShownSite}");
                return;
            }
            if (Registered && Services.TryGet<SceneRoot>(out var root) && root.Content != null) m_Content = root.Content;
            SetVisible(true);
            if (Mode == QuadMode.Video && video != null && video.isPrepared) video.Play();
            ApplyPose();
            Refresh();
            Log.Info($"G3 reimagine: back with {m_ShownSite}");
        }
        // end sitescope

        void ApplyPose()
        {
            var p = m_LocalPose;
            var root = Services.TryGet<SceneRoot>(out var r) ? r : null;
            if (Registered && (root == null || root.Content == null || root.Content != m_Content))
            {
                // The scan it was registered to went away (another site, a new revision): the picture belongs to it.
                Registered = false;
                Hide();
                return;
            }
            if (Registered)
            {
                var t = root.Content.transform;
                float s = t.lossyScale.x;
                p = new QuadPose
                {
                    Centre = t.TransformPoint(p.Centre),
                    Right = t.TransformDirection(p.Right), Up = t.TransformDirection(p.Up), Forward = t.TransformDirection(p.Forward),
                    Width = p.Width * s, Height = p.Height * s,
                };
            }
            Pose = p;
            if (p.Forward.sqrMagnitude < 1e-8f || p.Up.sqrMagnitude < 1e-8f) return;
            transform.SetPositionAndRotation(p.Centre, Quaternion.LookRotation(p.Forward, p.Up));
            if (image != null) image.transform.localScale = new Vector3(p.Width, p.Height, 1f);
            if (toggle != null) toggle.transform.localScale = new Vector3(p.Width, p.Height, 1f);   // built 1 × 1 m
            if (labelBar != null) labelBar.localPosition = new Vector3(0f, -p.Height * 0.5f - 0.02f, 0f);
            if (spinner != null) spinner.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        }

        // ---------------- pictures and video ----------------

        void Load(string url)
        {
            SetTexture(null);
            if (string.IsNullOrEmpty(url)) return;
            string want = url;
            void Show(Texture tex)
            {
                string now = ShowingBefore ? BeforeUrl : ImageUrl;
                if (now != want || Mode == QuadMode.Hidden) return;
                if (Mode == QuadMode.Video && VideoPlaying) return;   // the clip already runs
                SetTexture(tex);
            }
            // A scan thumb (the before frame, step 0) comes through the scene streamer's cache (works without the server).
            if (Services.TryGet<SceneStreamer>(out var streamer) && !string.IsNullOrEmpty(streamer.Site) && url.StartsWith($"/scenes/{streamer.Site}/"))
            {
                streamer.ThumbnailBytes(url.Substring($"/scenes/{streamer.Site}/".Length), bytes =>
                {
                    if (bytes == null) return;
                    if (m_Thumb == null) m_Thumb = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "ReimagineBefore", wrapMode = TextureWrapMode.Clamp };
                    if (m_Thumb.LoadImage(bytes, markNonReadable: false)) Show(m_Thumb);
                });
                return;
            }
            var client = Client;
            if (client != null) client.Texture(url, tex => Show(tex));
        }

        Texture2D m_Thumb;

        void SetTexture(Texture tex)
        {
            if (image == null) return;
            m_Block ??= new MaterialPropertyBlock();
            image.GetPropertyBlock(m_Block);
            if (tex != null) { m_Block.SetTexture(s_BaseMap, tex); m_Block.SetColor(s_BaseColor, Color.white); }
            else { m_Block.SetTexture(s_BaseMap, Texture2D.whiteTexture); m_Block.SetColor(s_BaseColor, UiTheme.Current.colors.surface); }
            image.SetPropertyBlock(m_Block);
        }

        void PlayVideo(string url)
        {
            if (video == null || string.IsNullOrEmpty(url)) return;
            video.Stop();
            video.source = VideoSource.Url;
            video.url = GrokLinks.Absolute(url);   // http mp4 on the LAN: Android needs usesCleartextTraffic (the app's manifest sets it)
            video.isLooping = true;
            video.playOnAwake = false;
            video.audioOutputMode = VideoAudioOutputMode.None;
            video.renderMode = VideoRenderMode.RenderTexture;
            video.skipOnDrop = true;
            LastError = null;
            video.Prepare();
        }

        void OnPrepared(VideoPlayer vp)
        {
            if (Mode != QuadMode.Video) return;
            int w = (int)Mathf.Max(16, vp.width), h = (int)Mathf.Max(16, vp.height);
            if (m_VideoTexture == null || m_VideoTexture.width != w || m_VideoTexture.height != h)
            {
                if (m_VideoTexture != null) { m_VideoTexture.Release(); Destroy(m_VideoTexture); }
                m_VideoTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "WalkInClip" };
            }
            vp.targetTexture = m_VideoTexture;
            vp.Play();
            SetTexture(m_VideoTexture);
            if (Mathf.Abs((float)w / h - m_Aspect) > 0.02f && !Registered) { Float((float)w / h); }
            Log.Info($"G3 walk-in playing {w}×{h}, looping");
        }

        void OnVideoError(VideoPlayer vp, string message)
        {
            LastError = message;
            Log.Warn($"G3 walk-in video error: {message}");
            m_StateLine = "Couldn't play the clip here · showing the still";
            if (Mode == QuadMode.Video) { SetMode(QuadMode.Still); if (!string.IsNullOrEmpty(ImageUrl)) Load(ImageUrl); }
        }

        void StopVideo()
        {
            if (video != null && (video.isPlaying || video.isPrepared)) video.Stop();
        }

        // ---------------- view ----------------

        void SetMode(QuadMode mode)
        {
            Mode = mode;
            if (mode != QuadMode.Video) StopVideo();
            SetVisible(mode != QuadMode.Hidden);
            Refresh();
        }

        void SetVisible(bool on)
        {
            if (on && Away) on = false;   // sitescope: parked with its site (a late video error, a mode change) stays hidden
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i).gameObject;
                if (child.activeSelf != on) child.SetActive(on);
            }
        }

        void Refresh()
        {
            if (Mode == QuadMode.Hidden) return;
            string lbl = Current?.Label ?? CurrentVideo?.Label ?? GrokLabels.AiPreview;
            if (label != null) label.text = GrokText.Esc(lbl);
            bool canFlip = Mode == QuadMode.Still && !string.IsNullOrEmpty(BeforeUrl);
            string st = Mode == QuadMode.Rendering ? "Rendering the walk-in…"
                : ShowingBefore ? "Before: the original photo · pinch the picture for the edit"
                : m_StateLine + (canFlip ? " · pinch the picture for before" : "");
            if (state != null) state.text = GrokText.Esc(st);
            if (spinner != null && spinner.gameObject.activeSelf != (Mode == QuadMode.Rendering)) spinner.gameObject.SetActive(Mode == QuadMode.Rendering);
            if (toggle != null && toggle.gameObject.activeSelf != canFlip) toggle.gameObject.SetActive(canFlip);
            if (flipButton != null)
            {
                if (flipButton.gameObject.activeSelf != canFlip) flipButton.gameObject.SetActive(canFlip);
                flipButton.SetText(ShowingBefore ? "Show edit" : "Show before");
            }
        }

        /// Plain text of what the quad shows (harness).
        public string Describe() =>
            $"mode={Mode} {(Registered ? "at camera" : "floating")} {Pose.Width:0.00}×{Pose.Height:0.00} m step={Current?.Step.ToString() ?? "-"} " +
            $"before={(ShowingBefore ? "yes" : "no")} image={ImageUrl} video={CurrentVideo?.VideoUrl ?? "-"} playing={VideoPlaying} poll={FlyPoll.State} " +
            $"label=\"{Current?.Label ?? CurrentVideo?.Label}\" state=\"{GrokCard.Strip(state != null ? state.text : m_StateLine)}\"";
    }
}
