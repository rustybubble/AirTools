using System;
using AirTools.Parts;
using Oculus.Interaction;
using UnityEngine;

namespace AirTools.UI
{
    /// Press-and-hold confirmation on a GlassButton: keep it pressed for Required seconds while a ring fills; letting
    /// go early cancels. Confirmed fires once per completed hold. This is the only way a payment is confirmed.
    public class HoldToConfirm : MonoBehaviour
    {
        public GlassButton button;
        public LineRenderer ring;
        public float radius = 0.03f;
        public float required = 1.0f;

        public event Action Confirmed;
        /// A new press began (the checkout re-prepares a stale nonce while the ring fills).
        public event Action HoldStarted;
        public float Progress => m_Timer.Progress;
        /// How long Pay was actually held when the last hold confirmed, in whole milliseconds (hand-off §5.2 hold_ms).
        public int HeldMs => UnityEngine.Mathf.RoundToInt(m_Timer.HeldSeconds * 1000f);
        public int ConfirmCount { get; private set; }
        /// Harness: force the pressed state (null = read the button).
        public bool? PressedOverride { get; set; }

        readonly HoldTimer m_Timer = new HoldTimer();
        const int Segments = 48;

        void Update()
        {
            m_Timer.Required = required;
            bool pressed = PressedOverride ?? (button != null && button.interactable && button.State == InteractableState.Select);
            bool wasHolding = m_Timer.Holding;
            if (m_Timer.Update(pressed, Time.unscaledTime)) Fire();
            else if (!wasHolding && m_Timer.Holding) HoldStarted?.Invoke();
            DrawRing(m_Timer.Progress);
        }

        void Fire()
        {
            ConfirmCount++;
            AirTools.Core.Log.Info($"Hold confirmed ({HeldMs} ms)");
            Confirmed?.Invoke();
        }

        /// Harness/tests: hold for `seconds` (simulated time) then release. True if that hold confirmed.
        public bool Simulate(float seconds)
        {
            m_Timer.Required = required;
            m_Timer.Reset();
            bool fired = false;
            float t0 = 1000f;
            for (float t = 0f; t <= seconds + 1e-4f; t += 0.02f)
                if (m_Timer.Update(true, t0 + t)) { fired = true; Fire(); }
            m_Timer.Update(false, t0 + seconds + 0.02f);
            return fired;
        }

        public void ResetHold() => m_Timer.Reset();

        /// switchclean: a press is under way and hasn't confirmed yet (the ring is filling); a world-model switch leaves a
        /// checkout up while this is true.
        public bool Holding => m_Timer.Holding;

        void DrawRing(float p)
        {
            if (ring == null) return;
            int n = Mathf.Max(0, Mathf.CeilToInt(Segments * p));
            ring.positionCount = n > 0 ? n + 1 : 0;
            for (int i = 0; i <= n && n > 0; i++)
            {
                float a = Mathf.PI * 0.5f - 2f * Mathf.PI * p * i / n;   // clockwise from 12 o'clock
                ring.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, -0.002f));
            }
        }
    }
}
