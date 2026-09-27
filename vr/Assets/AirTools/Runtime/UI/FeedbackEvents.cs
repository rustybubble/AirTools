using System;
using AirTools.Parts;
using UnityEngine;

namespace AirTools.UI
{
    /// Every kind of answer the app gives an action (UX §2.5 feedback grammar).
    public enum Feedback { Press, Hover, SnapTick, Point, Saved, Level, PartSeated, Undo, Redo, Miss, Paid, Teleport }

    /// The one feedback hub (UX W0.5): behaviour code says what happened, this maps it to sound (SfxPlayer, spatialised
    /// at the spot) and controller haptics. Hands get the sound and the visuals. Every call is counted (even in EditMode,
    /// where nothing plays) so tests can check that each commit answers (acceptance A11).
    public static class FeedbackEvents
    {
        static readonly int[] s_Counts = new int[Enum.GetValues(typeof(Feedback)).Length];

        /// Raised for every event (after it's counted).
        public static event Action<Feedback> Fired;

        public static int Count(Feedback f) => s_Counts[(int)f];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetCounts() { Array.Clear(s_Counts, 0, s_Counts.Length); }

        static SfxPlayer Emit(Feedback f)
        {
            s_Counts[(int)f]++;
            Fired?.Invoke(f);
            return Application.isPlaying ? SfxPlayer.Instance : null;
        }

        static Vector3 Head => Camera.main != null ? Camera.main.transform.position + Camera.main.transform.forward * 0.4f : Vector3.zero;

        /// A UI button (poke or ray) pressed.
        public static void Press(Vector3 at, bool controllerHaptic = true)
        {
            var p = Emit(Feedback.Press);
            if (p == null) return;
            p.Play(p.press != null ? p.press : SfxClips.Press, at, 0.8f);
            if (controllerHaptic) p.Haptic(HapticPattern.Press);
        }

        /// Hover on a control (controllers: a light buzz and a quiet hover sound).
        public static void Hover()
        {
            var p = Emit(Feedback.Hover);
            if (p == null) return;
            if (AirTools.Input.InputMode.Controllers && p.hover != null) p.Play(p.hover, Head, 0.25f);
            p.Haptic(HapticPattern.Hover);
        }

        /// The snap cursor acquired an edge / corner / midpoint (corners tick higher).
        public static void SnapTick(bool corner)
        {
            var p = Emit(Feedback.SnapTick);
            if (p == null) return;
            p.Play(corner ? SfxClips.CornerTick : SfxClips.Tick, Head, 0.35f);
            p.Haptic(HapticPattern.Tick);
        }

        /// A measure point placed (the n-th of its shape): a pip on the surface, rising with each point.
        public static void Point(Vector3 at, int n)
        {
            var p = Emit(Feedback.Point);
            if (p == null) return;
            p.Play(SfxClips.Pip(n), at);
            p.Haptic(HapticPattern.Press);
        }

        /// A tape / shape saved to the notebook.
        public static void Saved(Vector3 at)
        {
            var p = Emit(Feedback.Saved);
            if (p == null) return;
            p.Play(SfxClips.Saved, at);
            p.Haptic(HapticPattern.Success);
        }

        /// A level reading placed.
        public static void Level(Vector3 at)
        {
            var p = Emit(Feedback.Level);
            if (p == null) return;
            p.Play(SfxClips.Level, at);
            p.Haptic(HapticPattern.Press);
        }

        /// A part seated on a surface: a clunk, and the fit on the controller (never a buzzer for a good fit).
        public static void PartSeated(Vector3 at, FitStatus status, OVRInput.Controller controller = OVRInput.Controller.None)
        {
            var p = Emit(Feedback.PartSeated);
            if (p == null) return;
            p.Play(p.release != null ? p.release : SfxClips.Clunk, at);
            if (status == FitStatus.Red) p.Play(SfxClips.Miss, at, 0.6f);
            p.Haptic(status == FitStatus.Red ? HapticPattern.Error : status == FitStatus.Amber ? HapticPattern.Warning : HapticPattern.Success, controller);
        }

        public static void Undo()
        {
            var p = Emit(Feedback.Undo);
            if (p == null) return;
            p.Play(SfxClips.Undo, Head, 0.8f);
            p.Haptic(HapticPattern.Tick);
        }

        public static void Redo()
        {
            var p = Emit(Feedback.Redo);
            if (p == null) return;
            p.Play(SfxClips.Redo, Head, 0.8f);
            p.Haptic(HapticPattern.Tick);
        }

        /// An input that did nothing (a miss, a refused teleport…): a low double pulse. InputHints adds the words.
        public static void Miss()
        {
            var p = Emit(Feedback.Miss);
            if (p == null) return;
            p.Play(SfxClips.Miss, Head, 0.7f);
            p.Haptic(HapticPattern.Error);
        }

        /// Payment authorised / receipt saved.
        public static void Paid(Vector3 at)
        {
            var p = Emit(Feedback.Paid);
            if (p == null) return;
            p.Play(SfxClips.Chime, at, 1.2f);
            p.Haptic(HapticPattern.Success);
        }

        /// A teleport landed.
        public static void Teleport(Vector3 at)
        {
            var p = Emit(Feedback.Teleport);
            if (p == null) return;
            p.Play(SfxClips.Teleport, at, 0.6f);
        }
    }
}
