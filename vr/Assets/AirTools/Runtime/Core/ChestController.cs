using AirTools.UI;
using UnityEngine;

namespace AirTools.Core
{
    /// The world button (was the ship's chest): pressing it opens the world, or closes it back to passthrough.
    /// Its label follows the app mode, so voice / other callers of AppCommands update it too.
    public class ChestController : MonoBehaviour
    {
        [Tooltip("The glass button (ISDK poke + ray).")]
        public GlassButton button;
        [Tooltip("Optional first-use hint shown under the button while in passthrough.")]
        public GameObject hint;
        [Tooltip("Hide the button while the world is open (leave from the palm menu instead).")]
        public bool hideInWorld;
        [Tooltip("Ignore further presses for this long after one registers (the fade takes ~1 s).")]
        public float cooldownSeconds = 1.2f;

        public int PressCount { get; private set; }
        float m_LastPress = -999f;
        bool? m_ShownOpen;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        /// Same path as a physical press (also used by tests and the Operator-free fallback).
        public void Press()
        {
            if (Time.unscaledTime - m_LastPress < cooldownSeconds) return;
            m_LastPress = Time.unscaledTime;
            PressCount++;
            Log.Info($"World button pressed (#{PressCount}) in {AppState.Mode}");
            AppCommands.ToggleChest();
        }

        bool? m_HintControllers;

        void Update()
        {
            // The hint names the gesture for what's in your hands (palm up / the left menu button).
            if (hint != null && hint.activeInHierarchy && m_HintControllers != AirTools.Input.InputMode.Controllers)
            {
                m_HintControllers = AirTools.Input.InputMode.Controllers;
                var t = hint.GetComponent<TMPro.TMP_Text>();
                if (t != null) t.text = m_HintControllers.Value ? "Then press the left menu button for tools" : "Then turn your left palm up for tools";
            }
            bool open = AppState.Mode != AppMode.Passthrough;
            if (m_ShownOpen == open) return;
            m_ShownOpen = open;
            if (button != null)
            {
                button.SetText(open ? "Exit world" : "Enter world");
                button.style = open ? ButtonStyle.Secondary : ButtonStyle.Primary;
            }
            if (hint != null) hint.SetActive(!open);
            if (hideInWorld && button != null) button.gameObject.SetActive(!open);
        }
    }
}
