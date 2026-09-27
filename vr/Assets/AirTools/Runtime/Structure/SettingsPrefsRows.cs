using System;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Structure
{
    /// settings-assets: two segmented chip rows at the bottom of Settings (ScenePanel):
    /// - Talk: Hold · Toggle · Auto — how Talk listens (UserPrefs.Talk; default Toggle, see UserPrefs);
    /// - 3D models: HF · LLM+CAD · Auto — how the parts server makes models (UserPrefs.Assets, backend asset_mode): HF =
    ///   Hunyuan3D image-to-3D, LLM+CAD = Grok writes OpenSCAD; placed and held parts rebuild in the new mode
    ///   (Parts/AssetModeSwitcher).
    /// The chip of the current choice is selected (ink). Both are saved per device (not in DemoMode). Built by
    /// Editor/SettingsAssetsBuilder (AirTools ▸ Wire Main Scene); colours and sizes from UiTheme through UiBuild's chip
    /// style; behaviour on GlassButton.Clicked.
    public class SettingsPrefsRows : MonoBehaviour
    {
        public TextMeshPro talkCaption, modelsCaption;
        /// Hold, Toggle, Auto (TalkOrder).
        public GlassButton[] talk = Array.Empty<GlassButton>();
        /// HF, LLM+CAD, Auto (AirTools.Parts.AssetModes.Order).
        public GlassButton[] models = Array.Empty<GlassButton>();

        public const string TalkCaption = "Talk", ModelsCaption = "3D models";
        public static readonly TalkStyle[] TalkOrder = { TalkStyle.Hold, TalkStyle.Toggle, TalkStyle.Auto };
        public static AssetMode[] ModelOrder => AirTools.Parts.AssetModes.Order;

        Action[] m_TalkHandlers, m_ModelHandlers;

        void OnEnable()
        {
            m_TalkHandlers = Hook(talk, PressTalk);
            m_ModelHandlers = Hook(models, PressModels);
            UserPrefs.Changed += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            Unhook(talk, m_TalkHandlers);
            Unhook(models, m_ModelHandlers);
            UserPrefs.Changed -= Refresh;
        }

        static Action[] Hook(GlassButton[] chips, Func<int, bool> press)
        {
            var handlers = new Action[chips.Length];
            for (int i = 0; i < chips.Length; i++)
            {
                int k = i;
                handlers[i] = () => press(k);
                if (chips[i] != null) chips[i].Clicked += handlers[i];
            }
            return handlers;
        }

        static void Unhook(GlassButton[] chips, Action[] handlers)
        {
            if (handlers == null) return;
            for (int i = 0; i < chips.Length && i < handlers.Length; i++) if (chips[i] != null) chips[i].Clicked -= handlers[i];
        }

        /// Same path as a poke / ray pinch on Talk chip i (Hold, Toggle, Auto).
        public bool PressTalk(int i)
        {
            if (i < 0 || i >= TalkOrder.Length) return false;
            var s = TalkOrder[i];
            bool changed = UserPrefs.Talk != s;
            UserPrefs.Talk = s;
            if (changed)
            {
                Log.Info($"Settings: Talk {UserPrefs.Label(s)}");
                UiToast.Show(TalkHint(s), ColorRole.Info);
            }
            Refresh();
            return true;
        }

        /// Same path as a poke / ray pinch on 3D-models chip i (HF, LLM+CAD, Auto). The rebuild toast is the switcher's.
        public bool PressModels(int i)
        {
            if (i < 0 || i >= ModelOrder.Length) return false;
            if (UserPrefs.Assets != ModelOrder[i]) Log.Info($"Settings: 3D models {UserPrefs.Label(ModelOrder[i])} ({UserPrefs.Wire(ModelOrder[i])})");
            UserPrefs.Assets = ModelOrder[i];
            Refresh();
            return true;
        }

        /// The toast after a Talk choice: how to use it.
        public static string TalkHint(TalkStyle s) => s switch
        {
            TalkStyle.Hold => "Talk: hold while you speak, let go to send",
            TalkStyle.Toggle => "Talk: tap to start, tap again or stop talking to send",
            _ => "Talk: tap to toggle, or hold while you speak",
        };

        public void Refresh()
        {
            for (int i = 0; i < talk.Length && i < TalkOrder.Length; i++) if (talk[i] != null) talk[i].SetSelected(TalkOrder[i] == UserPrefs.Talk);
            for (int i = 0; i < models.Length && i < ModelOrder.Length; i++) if (models[i] != null) models[i].SetSelected(ModelOrder[i] == UserPrefs.Assets);
        }
    }
}
