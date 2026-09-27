using System.Globalization;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Structure
{
    // Declutter S7 appends Home, Ladder, ExitWorld and FallEdges (appended: ScenePanelButton serialises the index).
    public enum ScenePanelAction { NextSite, BuiltIn, Reload, ToggleSnapping, ToggleStructure, Digit, Point, Backspace, SetScale, ResetScale, Ask, Close,
        Home, Ladder, ExitWorld, FallEdges }

    /// Settings (palm ring ▸ Settings, the gear; scalemodels: it was "More"; declutter DC3 / M7: the Scene window,
    /// renamed; the right side slot): settings, layers and recovery. Top to bottom:
    /// - the status: the loaded scene, its scale ("Scale ×1.63 · set for this scan"), the scan's credit when it needs
    ///   one, and "Models on the headset: 5 of 7";
    /// - snapping and the display settings;
    /// - "set scale from a known dimension": a keypad for the real length against the latest tape, in inches or cm,
    ///   whichever the units chip shows (D2); Reset goes back to the scan's default scale;
    /// - the layers: structure edges, the scan's labels, fall edges;
    /// - "What is this?" (/scene/ask + pin) and hold-to-talk for the voice quartermaster;
    /// - Home / Ladder / Exit world (tap twice);
    /// - the sites, and Take out.
    /// Picking Ladder or Exit world closes it.
    public class ScenePanel : MonoBehaviour
    {
        public const string Title = "Settings";
        /// settings-assets: the voice line at rest, in the words of Settings ▸ Talk.
        public const string TapAskLine = TalkButton.IdleText + " · ask for a part or a tool", HoldAskLine = TalkButton.HoldIdleText + " · ask for a part or a tool";

        public FloatingWindow window;
        public TextMeshPro title, status, scaleText, voiceText;
        public GlassButton snapping, structure, talk;
        public GlassButton home, ladder, exitWorld, fallEdges;
        [Tooltip("The sites row's Reload: reads Retry after a load failed or timed out (fix-ux).")]
        public GlassButton reload;

        public string Entry { get; private set; } = "";
        public bool IsOpen => window != null && window.IsOpen;
        int m_SiteIndex = -1;
        readonly TalkButton m_Talk = new TalkButton();   // voice

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open()
        {
            window?.Open();
            Services.Get<SceneStreamer>()?.ListSites(_ => Refresh());
            Refresh();
        }

        public void Close() => window?.Close();

        public void Do(ScenePanelAction a, string arg)
        {
            var streamer = Services.Get<SceneStreamer>();
            switch (a)
            {
                case ScenePanelAction.NextSite:
                    if (streamer == null) break;
                    streamer.ListSites(list =>
                    {
                        // scalemodels: with the laptop away, the next model that's on the headset.
                        var ids = list.Count > 0 ? list.ConvertAll(x => x.site) : streamer.OfflineSites();
                        if (ids.Count == 0) { UiToast.Show("No scenes on the laptop · check the connection", ColorRole.Warning); return; }
                        int cur = ids.IndexOf(streamer.Site);
                        m_SiteIndex = (cur + 1) % ids.Count;
                        AppCommands.LoadSite(ids[m_SiteIndex]);
                        UiToast.Show($"Loading the {Copy.SiteName(ids[m_SiteIndex])}…", ColorRole.Info);
                    });
                    break;
                case ScenePanelAction.BuiltIn: AppCommands.LoadSite("built-in"); break;
                // fix-ux: Reload is also the Retry after a failed / timed-out load (the site that failed, else the loaded
                // one, else the start site; the headset's cached package when the laptop is away).
                case ScenePanelAction.Reload:
                    if (streamer != null && !streamer.Retry()) UiToast.Show($"Still loading the {Copy.SiteName(streamer.LoadingSite)}…", ColorRole.Info);
                    break;
                case ScenePanelAction.ToggleSnapping: AppCommands.SetSnapping(!SnapService.Enabled); break;
                case ScenePanelAction.ToggleStructure:
                    var o = Services.Get<StructureOverlay>();
                    AppCommands.ShowStructure(o == null || !o.Visible);
                    break;
                case ScenePanelAction.Digit: if (Entry.Length < 7) Entry += arg; break;
                case ScenePanelAction.Point: if (!Entry.Contains(".")) Entry += Entry.Length == 0 ? "0." : "."; break;
                case ScenePanelAction.Backspace: if (Entry.Length > 0) Entry = Entry.Substring(0, Entry.Length - 1); break;
                case ScenePanelAction.SetScale:
                    if (double.TryParse(Entry, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) && typed > 0)
                    {
                        bool ok = AppCommands.SetScale(Copy.KeypadMetres(typed));   // D2: inches or cm, as the chip says
                        UiToast.Show(ok ? ScaleAppliedText() : ScaleRefusedText(ScaleCalibration.LastResult), ok ? ColorRole.Success : ColorRole.Warning);
                        if (ok) Entry = "";
                    }
                    else UiToast.Show($"Type the real size in {Copy.KeypadUnit}", ColorRole.Warning);
                    break;
                case ScenePanelAction.ResetScale:
                    // scalemodels: back to the scan's default scale (the kitchen's ×1.63), else the capture's own.
                    if (AppCommands.ResetScale())
                        UiToast.Show(streamer != null && streamer.ScaleSource == ScaleSource.SiteDefault
                            ? $"Scale back to ×{SiteScales.Factor(Services.Get<SceneRoot>()?.Calibration ?? 1f)} · set for this scan" : "Scale reset", ColorRole.Info);
                    break;
                case ScenePanelAction.Ask: if (!AppCommands.AskScene("What is this?")) UiToast.Show("Still answering…", ColorRole.Warning); break;
                case ScenePanelAction.Close: Close(); break;
                // Declutter M7: what left the ring. The toolbox actions log "Menu: <action>" (tools/demo/hcheck.py).
                case ScenePanelAction.Home: AirTools.Tools.ToolboxButton.Run(AirTools.Tools.ToolboxAction.Home); break;
                case ScenePanelAction.Ladder: AirTools.Tools.ToolboxButton.Run(AirTools.Tools.ToolboxAction.ToggleLadder); Close(); break;
                case ScenePanelAction.ExitWorld: AirTools.Tools.ToolboxButton.Run(AirTools.Tools.ToolboxAction.ToggleWorld); Close(); break;
                case ScenePanelAction.FallEdges: AirTools.Tools.ToolboxButton.Run(AirTools.Tools.ToolboxAction.ToggleFallEdges); break;
            }
            Refresh();
        }

        void Update()
        {
            // voice: tap or hold Talk (TalkButton → VoiceClient); driven while closed too, so closing releases a hold.
            m_Talk.Drive(talk, Services.Get<VoiceClient>(), IsOpen);
            if (!IsOpen) return;
            if (Time.frameCount % 15 == 0 || m_Talk.Changed) Refresh();
        }

        /// "✓ Scale ×1.49 · tape now reads 0.39 m".
        public static string ScaleAppliedText()
        {
            var root = Services.Get<SceneRoot>();
            var tape = ScaleCalibration.LatestTape();
            string f = root != null ? root.Calibration.ToString("0.00", CultureInfo.InvariantCulture) : "?";
            return tape != null ? $"✓ Scale ×{f} · tape now reads {Copy.Len(tape.ValueSI)}" : $"✓ Scale ×{f}";
        }

        /// ScaleCalibration.LastResult (raw) → what to do about it.
        public static string ScaleRefusedText(string raw)
        {
            raw ??= "";
            if (raw.Contains("no scene loaded")) return "Load a scene first";
            if (raw.Contains("measure a known dimension") || raw.Contains("no tape reading")) return "Tape something you know first";
            if (raw.Contains("enter the real length")) return $"Type the real size in {Copy.KeypadUnit}";
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"×([\d.]+) is out of range");
            if (m.Success) return $"That's {m.Groups[1].Value}× off · check it's in {Copy.KeypadUnit}";
            return "Couldn't set the scale · try again";
        }

        /// The scale chip (UX W0.1): is this place at true size? A package is at true size once "Set scale" was saved
        /// for it (a door taped and its real size typed), or at its site's default scale (scalemodels: "Scale ×1.63 · set
        /// for this scan", SiteScales); the built-in test facade is built to size.
        public static string ScaleChip(SceneRoot root) =>
            ScaleChip(root, Services.TryGet<SceneStreamer>(out var s) ? s.ScaleSource : ScaleSource.None);

        public static string ScaleChip(SceneRoot root, ScaleSource source)
        {
            if (root == null || root.Content == null) return "";
            if (!root.IsRuntimePackage) return "Scale ✓ built to size";
            if (SiteScales.Consistent(root.Calibration, source) == ScaleSource.SiteDefault) return SiteScales.DefaultLine(root.Calibration);   // scalemodels
            if (Mathf.Abs(root.Calibration - 1f) > 1e-4f)
            {
                string matched = ScaleCalibration.LastRealMetres > 0 ? $" · matched to a {Copy.Len(ScaleCalibration.LastRealMetres)} tape" : "";
                return $"Scale ✓ set (×{root.Calibration.ToString("0.00", CultureInfo.InvariantCulture)}){matched}";
            }
            return "Scale not set · tape a door to set it";
        }

        /// A load failed or timed out and its site isn't the one showing (GuideRail's SceneLoadFailed, same test).
        static bool LoadFailedShowing(SceneStreamer streamer, SceneRoot root) =>
            streamer != null && !streamer.Loading && streamer.LoadFailed
            && (root == null || !root.IsRuntimePackage || root.Site != streamer.FailedSite);

        public void Refresh()
        {
            var streamer = Services.Get<SceneStreamer>();
            var root = Services.Get<SceneRoot>();
            string site = Copy.SiteName(root != null && root.IsRuntimePackage ? root.Site : null);
            if (title != null && title.text != Title) title.text = Title;   // M8: the title row is the title and Close
            if (status != null)
            {
                string s;
                // DC5: the scan's credit stays here (and on the wrist strip) while its scene is loaded.
                string credit = SceneCredits.For(root != null && root.IsRuntimePackage ? root.Site : null);
                // fix-ux: the same state as the heads-up line — loading (the site being loaded), or a failed load with Retry.
                if (streamer != null && streamer.Loading) s = $"Loading the {Copy.SiteName(streamer.LoadingSite ?? streamer.Site ?? root?.Site)}…";
                else if (LoadFailedShowing(streamer, root))
                {
                    string warn = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.warning);
                    s = $"<color=#{warn}>Couldn't load the {Copy.SiteName(streamer.FailedSite)} · Retry</color>\n{Copy.Cap(site)} · {ScaleChip(root)}";
                }
                else
                {
                    string chip = ScaleChip(root);
                    bool calibrated = root != null && (!root.IsRuntimePackage || Mathf.Abs(root.Calibration - 1f) > 1e-4f);
                    string colour = ColorUtility.ToHtmlStringRGBA(calibrated ? UiTheme.Current.colors.success : UiTheme.Current.colors.warning);
                    s = $"{Copy.Cap(site)} · <color=#{colour}>{chip}</color>";
                    if (credit != null) s += "\n" + credit;
                    else s += root != null && root.Structure != null ? "\nSnaps to edges and corners" : "\nSnaps to surfaces";
                }
                // scalemodels: how many of Model view's models open with the laptop away (the prefetch fills it in).
                if (credit == null && streamer != null && streamer.KnownSites.Count > 0)
                {
                    streamer.HeadsetCount(out int on, out int total);
                    s += " · " + PrefetchPlan.CountText(on, total);
                }
                status.text = s;
            }
            if (reload != null)
            {
                string label = LoadFailedShowing(streamer, root) ? "Retry" : "Reload";
                if (reload.Text != label) reload.SetText(label);
            }
            if (snapping != null) snapping.SetSelected(SnapService.Enabled);
            if (structure != null) structure.SetSelected(Services.Get<StructureOverlay>()?.Visible ?? false);
            if (ladder != null) ladder.SetSelected(AirTools.Tools.ToolboxButton.IsOn(AirTools.Tools.ToolboxAction.ToggleLadder) == true);
            if (fallEdges != null) fallEdges.SetSelected(AirTools.Tools.ToolboxButton.IsOn(AirTools.Tools.ToolboxAction.ToggleFallEdges) == true);
            if (exitWorld != null)
            {
                string exit = AppState.Mode == AppMode.Passthrough ? "Enter world" : "Exit world";
                if (exitWorld.Text != exit) exitWorld.SetText(exit);
            }
            if (scaleText != null)
            {
                var tape = ScaleCalibration.LatestTape();
                scaleText.text = (tape != null ? $"Last tape {Copy.Len(tape.ValueSI)}" : "Tape something you know the size of") +
                                 $"\nReal size: {(Entry.Length > 0 ? Entry : "—")} {Copy.KeypadUnit}";
            }
            if (voiceText != null)
            {
                var v = Services.Get<VoiceClient>();
                var ask = Services.Get<SceneAsk>();
                voiceText.text = v != null && (v.Recording || !string.IsNullOrEmpty(v.Status)) ? Copy.VoiceStatus(v.Status)
                    : ask != null && ask.Busy ? "Looking at this spot…"
                    : ask?.LastAnswer != null ? Copy.Clip(Copy.Clean(ask.LastAnswer.answer), 80)
                    : UserPrefs.Talk == TalkStyle.Hold ? HoldAskLine : TapAskLine;   // settings-assets: follows Settings ▸ Talk
            }
        }
    }
}
