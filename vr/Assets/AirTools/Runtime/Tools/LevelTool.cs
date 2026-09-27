using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Tools
{
    /// A placed level reading: its reading, notebook entry and gizmo.
    public class LevelPlacement
    {
        public LevelReading Reading;     // point/normal in frame space
        public NotebookEntry Entry;
        public LevelGizmo Gizmo;
    }

    /// Level (SPEC M3): point at a surface; a level bar sits on it showing the tilt live. Click to place the reading
    /// and log it. Near-horizontal surfaces read slope from level; walls read deviation from plumb.
    public class LevelTool : MonoBehaviour, IEditable, ISiteScoped   // sitescope: ISiteScoped
    {
        [Tooltip("Scene root: readings are taken relative to its up axis and stored in its space.")]
        public Transform frame;
        public Transform viewRoot;
        public MeasureStyle style = new MeasureStyle();
        public float maxRayDistance = 40f;

        public bool Equipped { get; private set; }
        public LevelReading? Live { get; private set; }
        public IReadOnlyList<LevelPlacement> Placements => m_Placements;
        public string LastAction { get; private set; } = "";
        public event Action<LevelPlacement> Placed;

        readonly List<LevelPlacement> m_Placements = new List<LevelPlacement>();
        readonly List<float> m_Times = new List<float>();
        readonly List<(LevelPlacement p, float at)> m_Redo = new List<(LevelPlacement, float)>();
        IToolInput m_Input;
        LevelGizmo m_Ghost;

        void OnEnable()
        {
            Services.Register(this);
            EditHistory.Register(this);
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            AppState.Changed += OnModeChanged;
            RegisterSite();   // sitescope
        }

        void OnDisable()
        {
            SetInput(null);
            Services.Unregister(this);
            EditHistory.Unregister(this);
            AppState.Changed -= OnModeChanged;
            SiteScope.Unregister(this);   // sitescope
        }

        void Start()
        {
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            OnModeChanged(AppState.Mode, AppState.Mode);
        }

        void OnModeChanged(AppMode _, AppMode mode)
        {
            if (viewRoot != null) viewRoot.gameObject.SetActive(mode != AppMode.Passthrough);
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null) { m_Input.PressStart -= OnPressStart; m_Input.PressEnd -= OnPressEnd; m_Input.ButtonDown -= OnButton; }
            m_Input = input;
            if (m_Input != null) { m_Input.PressStart += OnPressStart; m_Input.PressEnd += OnPressEnd; m_Input.ButtonDown += OnButton; }
        }

        public void Equip(bool on)
        {
            if (Equipped == on) return;
            Equipped = on;
            if (!on) { Live = null; m_PressReading = null; }
            RefreshGhost();
            Log.Info($"Level tool {(on ? "equipped" : "put away")}");
        }

        void Update()
        {
            if (Equipped && m_Input != null) Tick();
        }

        /// Per-frame: live reading under the pointer (tests call this directly).
        public void Tick()
        {
            if (m_Input == null) return;
            var hand = m_Input.LastActiveHand;
            Live = m_Input.HasPointer(hand) && TryRead(m_Input.GetPointer(hand), out var r) ? r : (LevelReading?)null;
            if (m_PressReading.HasValue && m_Input.HasPointer(m_PressHand)
                && PointerHistory.SameAim(m_PressPointer, m_Input.GetPointer(m_PressHand), PointerHistory.SeatDegrees))
                Live = m_PressReading;   // W1.2: the ghost holds the reading the release will place
            RefreshGhost();
        }

        // W1.2: a live press takes the reading the level showed ~90 ms before the pinch onset; the release places it
        // unless the ray has since moved more than PointerHistory.SeatDegrees (then it reads at the release, as before).
        LevelReading? m_PressReading;
        Pose m_PressPointer;
        ToolHand m_PressHand;

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            m_PressReading = null;
            if (!Equipped || !ToolInputHub.TryGetCommitPointer(m_Input, hand, out var commit) || !TryRead(commit, out var r)) return;
            m_PressReading = r; m_PressPointer = commit; m_PressHand = hand;
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (!Equipped) return;
            var held = m_PressReading;
            m_PressReading = null;
            if (held.HasValue && m_PressHand == hand && PointerHistory.SameAim(m_PressPointer, pointer, PointerHistory.SeatDegrees))
            {
                Place(held.Value);   // W1.2
                return;
            }
            if (TryRead(pointer, out var reading)) Place(reading);
            else
            {
                LastAction = "miss";
                AirTools.UI.InputHints.Say("level.miss", "Point at a flat surface");
            }
        }

        void OnButton(ToolHand hand, ToolButton button)
        {
            if (!Equipped) return;
            if (button == ToolButton.Undo) Undo();
            else if (button == ToolButton.Redo) Redo();
            else if (button == ToolButton.Clear) ClearAll();
        }

        /// Reading in frame space for a pointer ray (no feature snapping: a level sits on the face you point at).
        public bool TryRead(Pose pointer, out LevelReading reading)
        {
            reading = default;
            var ray = new Ray(pointer.position, pointer.forward);
            if (!SnapService.TryRaySnap(ray, out var hit, maxRayDistance, features: false)) return false;
            var normal = LevelMath.SampleNormal(ray, hit, 0.03f * (frame != null ? frame.lossyScale.x : 1f));
            var up = frame != null ? frame.up : Vector3.up;
            var world = LevelMath.Read(hit.point, normal, up);
            reading = new LevelReading(world.Mode, world.Degrees, ToFrame(hit.point), ToFrameDir(normal), ToFrameDir(world.Downhill));
            return true;
        }

        /// The scene's calibration changed by `factor` (SceneRoot.Rescaled): placed levels move with the scene
        /// (angles are unchanged by a uniform scale).
        public void RescaleAll(float factor)
        {
            foreach (var p in m_Placements)
            {
                var r = p.Reading;
                p.Reading = new LevelReading(r.Mode, r.Degrees, r.Point * factor, r.Normal, r.Downhill);
                p.Gizmo?.Show(p.Reading, ghost: false);
                if (p.Entry != null) { p.Entry.Points = new[] { p.Reading.Point }; Notebook.Update(p.Entry); }
            }
        }

        public LevelPlacement Place(LevelReading r)
        {
            var c = CultureInfo.InvariantCulture;
            string label = r.Mode == LevelMode.Level
                ? $"Level {Units.FormatAngle(r.Degrees)} ({r.PercentGrade.ToString("0.0", c)}% slope)"
                : $"Plumb {Units.FormatAngle(r.Degrees)} off vertical";
            var entry = new NotebookEntry("level", r.Degrees, "°", new[] { r.Point }, DateTime.Now, -1, label);
            var root = frame != null ? frame.GetComponent<SceneRoot>() : null;
            if (root != null) entry.NearestCameraId = CameraEvidence.Nearest(entry.Points, root.CamerasInRootSpace());
            Notebook.Add(entry);
            EnsureViewRoot();
            var gizmo = LevelGizmo.Create(viewRoot, style, $"Level{m_Placements.Count + 1}");
            gizmo.Show(r, ghost: false);
            var p = new LevelPlacement { Reading = r, Entry = entry, Gizmo = gizmo };
            m_Placements.Add(p);
            m_Times.Add(EditHistory.Stamp());
            DropRedo();
            EditHistory.Edited(this);
            LastAction = $"placed #{entry.Id}: {label}";
            AirTools.UI.InputHints.Succeeded();
            AirTools.UI.FeedbackEvents.Level(frame != null ? frame.TransformPoint(r.Point) : r.Point);
            Placed?.Invoke(p);
            return p;
        }

        public void Undo()
        {
            if (m_Placements.Count == 0) return;
            var p = m_Placements[m_Placements.Count - 1];
            m_Placements.RemoveAt(m_Placements.Count - 1);
            if (m_Times.Count > 0) m_Times.RemoveAt(m_Times.Count - 1);
            Notebook.Remove(p.Entry);
            if (p.Gizmo != null) p.Gizmo.gameObject.SetActive(false);
            m_Redo.Add((p, EditHistory.Stamp()));
            LastAction = "undo level";
            EditHistory.NotifyChanged();
        }

        public void Redo()
        {
            if (m_Redo.Count == 0) return;
            var (p, _) = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            m_Placements.Add(p);
            m_Times.Add(EditHistory.Stamp());
            Notebook.Restore(p.Entry);
            if (p.Gizmo != null) p.Gizmo.gameObject.SetActive(true);
            LastAction = "redo level";
            EditHistory.NotifyChanged();
        }

        public bool CanUndo => m_Placements.Count > 0;
        public bool CanRedo => m_Redo.Count > 0;
        public float LastEditAt => m_Times.Count > 0 ? m_Times[m_Times.Count - 1] : float.NegativeInfinity;
        public float LastUndoAt => m_Redo.Count > 0 ? m_Redo[m_Redo.Count - 1].at : float.NegativeInfinity;

        public void DropRedo()
        {
            foreach (var (p, _) in m_Redo) if (p.Gizmo != null) DestroyObj(p.Gizmo.gameObject);
            m_Redo.Clear();
        }

        public void ClearAll()
        {
            foreach (var p in m_Placements)
            {
                Notebook.Remove(p.Entry);
                if (p.Gizmo != null) DestroyObj(p.Gizmo.gameObject);
            }
            m_Placements.Clear();
            m_Times.Clear();
            DropRedo();
            LastAction = "clear";
            EditHistory.NotifyChanged();
        }

        /// Pulse the gizmo of the placement that produced this entry (notebook "show").
        public bool Highlight(NotebookEntry entry, float seconds = 3f)
        {
            foreach (var p in m_Placements)
                if (p.Entry == entry && p.Gizmo != null) { p.Gizmo.Pulse(seconds); return true; }
            return false;
        }

        void RefreshGhost()
        {
            EnsureViewRoot();
            if (m_Ghost == null) m_Ghost = LevelGizmo.Create(viewRoot, style, "LevelGhost");
            if (Equipped && Live.HasValue) m_Ghost.Show(Live.Value, ghost: true);
            else m_Ghost.Hide();
        }

        void EnsureViewRoot()
        {
            if (viewRoot != null) return;
            var go = new GameObject("Levels");
            if (frame != null) go.transform.SetParent(frame, false);
            viewRoot = go.transform;
        }

        Vector3 ToFrame(Vector3 world) => frame != null ? frame.InverseTransformPoint(world) : world;
        Vector3 ToFrameDir(Vector3 world) => frame != null ? frame.InverseTransformDirection(world) : world;

        static void DestroyObj(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // ---------------- sitescope: every level belongs to the site it was placed on ----------------

        /// One site's levels while another site is loaded: placements, undo / redo stacks and the gizmos it hid.
        sealed class SiteState
        {
            public readonly List<LevelPlacement> Placements = new List<LevelPlacement>();
            public readonly List<float> Times = new List<float>();
            public readonly List<(LevelPlacement p, float at)> Redo = new List<(LevelPlacement, float)>();
            public readonly ParkedObjects Hidden = new ParkedObjects();
        }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);

        public string LiveSite => Sites.Live;
        public int ParkedPlacements(string site) => Sites.Peek(site)?.Placements.Count ?? 0;

        public void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderLevel);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            m_PressReading = null;
            SiteState park = null;
            if (m_Placements.Count > 0 || m_Redo.Count > 0)
            {
                park = new SiteState();
                park.Placements.AddRange(m_Placements);
                park.Times.AddRange(m_Times);
                park.Redo.AddRange(m_Redo);
                foreach (var p in m_Placements) park.Hidden.Hide(p.Gizmo);
                m_Placements.Clear(); m_Times.Clear(); m_Redo.Clear();
            }
            var back = Sites.Swap(to, park);
            if (back != null)
            {
                m_Placements.AddRange(back.Placements);
                m_Times.AddRange(back.Times);
                m_Redo.AddRange(back.Redo);
                back.Hidden.ShowAll();
                if (Mathf.Abs(factor - 1f) > 1e-6f)
                {
                    RescaleAll(factor);
                    foreach (var (p, _) in m_Redo)
                    {
                        var r = p.Reading;
                        p.Reading = new LevelReading(r.Mode, r.Degrees, r.Point * factor, r.Normal, r.Downhill);
                        if (p.Entry != null) p.Entry.Points = new[] { p.Reading.Point };
                        if (p.Gizmo == null) continue;
                        p.Gizmo.Show(p.Reading, ghost: false);   // Show activates it: an undone level stays hidden for Redo
                        p.Gizmo.gameObject.SetActive(false);
                    }
                }
            }
            LastAction = $"site {to}";
            Live = null;      // switchclean: the ghost's reading was on the scan being left (Tick reads the arriving one)
            RefreshGhost();   // switchclean
            EditHistory.NotifyChanged();
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll())
            {
                foreach (var p in st.Placements)
                {
                    Notebook.Remove(p.Entry);
                    if (p.Gizmo != null) DestroyObj(p.Gizmo.gameObject);
                }
                foreach (var (p, _) in st.Redo) if (p.Gizmo != null) DestroyObj(p.Gizmo.gameObject);
                st.Hidden.Clear();
            }
        }
        // end sitescope
    }
}
