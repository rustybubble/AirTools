using System.Collections.Generic;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Parts;
using AirTools.Structure;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// settings-assets (pure, offline too): Settings ▸ Talk — the press state machine per style (Hold / Toggle / Auto) and
    /// the whole capture pipeline per style; the settings persist (a store instead of PlayerPrefs); Settings ▸ 3D models —
    /// asset_mode in the context and the search body, the ?mode= URLs, the card's badge, Compare's target.
    /// The Unity-side checks (Main.unity's rows, AgentContext.Build, the swap keeping the anchor) are SettingsAssetsGateTests.
    public class SettingsAssetsTests
    {
        sealed class MemoryStore : IPrefsStore
        {
            public readonly Dictionary<string, int> Values = new Dictionary<string, int>();
            public int Saves;
            public bool Has(string key) => Values.ContainsKey(key);
            public int GetInt(string key, int fallback) => Values.TryGetValue(key, out var v) ? v : fallback;
            public void SetInt(string key, int value) => Values[key] = value;
            public void Save() => Saves++;
        }

        MemoryStore m_Store;
        bool m_Persist;

        [SetUp]
        public void UseMemoryStore()
        {
            m_Store = new MemoryStore();
            m_Persist = true;
            UserPrefs.Store = m_Store;
            UserPrefs.PersistOverride = () => m_Persist;
            UserPrefs.ResetCache();
        }

        [TearDown]
        public void RestorePrefs()
        {
            UserPrefs.Store = null;
            UserPrefs.PersistOverride = null;
            UserPrefs.ResetCache();
        }

        static VoiceSettings Set(TalkStyle s) => new VoiceSettings { style = s };

        // ---------------- the press, per style ----------------

        [Test]
        public void Toggle_APressStartsListening_TheReleaseDoesNothing_ASecondPressSends()
        {
            var g = new TalkGesture(Set(TalkStyle.Toggle));
            Assert.AreEqual(TalkAction.Start, g.Down(0f));
            Assert.AreEqual(TalkPhase.Toggled, g.Phase, "listening from the press on");
            Assert.AreEqual(TalkMode.Tap, g.Mode);
            Assert.AreEqual(TalkAction.None, g.Up(2.0f, speakingNow: true), "a long press is still a toggle");
            Assert.AreEqual(2.0f, g.PressSeconds, 1e-4f, "…its length is logged");
            Assert.AreEqual(TalkStop.None, g.Tick(2.5f, true, 0.2f));
            Assert.AreEqual(TalkAction.Stop, g.Down(3f), "the second press sends");
            Assert.AreEqual(TalkStop.SecondTap, g.LastStop);
            Assert.IsFalse(g.Listening);
        }

        [Test]
        public void Toggle_AFlickerIsNotASecondPress_TheEndOfSpeechStillSends()
        {
            var set = Set(TalkStyle.Toggle);
            var g = new TalkGesture(set);
            g.Down(0f);
            g.Up(0.1f, false);
            Assert.AreEqual(TalkAction.None, g.Down(set.minToggleSeconds - 0.1f), "the pinch flickering");
            Assert.AreEqual(TalkAction.None, g.Up(set.minToggleSeconds, false));
            Assert.IsTrue(g.Listening);
            Assert.AreEqual(TalkStop.None, g.Tick(2f, true, set.endSilenceSeconds - 0.1f));
            Assert.AreEqual(TalkStop.EndOfSpeech, g.Tick(2.2f, true, set.endSilenceSeconds));
        }

        [Test]
        public void Toggle_NoSpeech_GivesUp()
        {
            var set = Set(TalkStyle.Toggle);
            var g = new TalkGesture(set);
            g.Down(0f);
            Assert.AreEqual(TalkStop.None, g.Tick(set.noSpeechSeconds - 0.1f, false, 0f));
            Assert.AreEqual(TalkStop.NoSpeech, g.Tick(set.noSpeechSeconds, false, 0f));
        }

        [Test]
        public void Hold_ListensWhilePressed_NeverStopsAtTheEndOfSpeech_TheReleaseSends()
        {
            var set = Set(TalkStyle.Hold);
            var g = new TalkGesture(set);
            Assert.AreEqual(TalkAction.Start, g.Down(0f));
            Assert.AreEqual(TalkPhase.Holding, g.Phase, "holding from the press on (no tap window)");
            Assert.AreEqual(TalkMode.Hold, g.Mode);
            Assert.AreEqual(TalkStop.None, g.Tick(6f, true, set.endSilenceSeconds * 3f), "silence doesn't stop a hold");
            Assert.AreEqual(TalkStop.None, g.Tick(set.noSpeechSeconds + 1f, false, 0f), "nor does no speech");
            Assert.AreEqual(TalkAction.Stop, g.Up(8f, speakingNow: false));
            Assert.AreEqual(TalkStop.Release, g.LastStop);
            Assert.AreEqual(8f, g.PressSeconds, 1e-4f);
        }

        [Test]
        public void Hold_AQuickTapIsAShortHold()
        {
            var g = new TalkGesture(Set(TalkStyle.Hold));
            g.Down(0f);
            Assert.AreEqual(TalkAction.Stop, g.Up(0.1f, false), "the release ends it: no toggle in Hold");
            Assert.AreEqual(TalkMode.Hold, g.Mode);
        }

        [Test]
        public void Hold_ReleasedMidWord_FinishesTheWord_AndThePinchCanComeBack()
        {
            var set = Set(TalkStyle.Hold);
            var g = new TalkGesture(set);
            g.Down(0f);
            Assert.AreEqual(TalkAction.None, g.Up(2f, speakingNow: true));
            Assert.AreEqual(TalkPhase.Releasing, g.Phase);
            Assert.AreEqual(TalkAction.None, g.Down(2.1f), "the pinch came back: still holding");
            Assert.AreEqual(TalkPhase.Holding, g.Phase);
        }

        [Test]
        public void Auto_IsTheTapOrHold()
        {
            var set = Set(TalkStyle.Auto);
            var g = new TalkGesture(set);
            g.Down(0f);
            Assert.AreEqual(TalkPhase.Pressed, g.Phase, "not known yet");
            g.Up(set.tapSeconds * 0.5f, false);
            Assert.AreEqual(TalkMode.Tap, g.Mode);
            g.Reset();
            g.Down(0f);
            Assert.AreEqual(TalkAction.Stop, g.Up(set.tapSeconds + 1f, false));
            Assert.AreEqual(TalkMode.Hold, g.Mode);
        }

        [Test]
        public void TheStyleIsReadWhenListeningStarts()
        {
            var set = Set(TalkStyle.Toggle);
            var g = new TalkGesture(set);
            g.Down(0f);
            set.style = TalkStyle.Hold;   // changed in Settings mid-utterance
            Assert.AreEqual(TalkAction.None, g.Up(1f, false), "this one is still a toggle");
            Assert.AreEqual(TalkStyle.Toggle, g.Style);
            g.Down(2f);   // second press sends
            Assert.AreEqual(TalkAction.Start, g.Down(3f));
            Assert.AreEqual(TalkStyle.Hold, g.Style, "the next one holds");
        }

        [Test]
        public void EveryStyle_ThroughTheWholePipeline()
        {
            var checks = BackendHarness.TalkStyleChecks();
            Assert.GreaterOrEqual(checks.Count, 9);
            foreach (var (id, ok, detail) in checks) Assert.IsTrue(ok, $"{id}: {detail}");
        }

        [Test]
        public void TheTalkWordsFollowTheStyle()
        {
            Assert.AreEqual("Hold to talk", TalkButton.Idle(TalkStyle.Hold));
            Assert.AreEqual("Tap to talk", TalkButton.Idle(TalkStyle.Toggle));
            Assert.AreEqual("Tap to talk", TalkButton.Idle(TalkStyle.Auto));
            StringAssert.Contains("let go to send", SettingsPrefsRows.TalkHint(TalkStyle.Hold));
            StringAssert.Contains("tap again", SettingsPrefsRows.TalkHint(TalkStyle.Toggle));
            UserPrefs.Use(TalkStyle.Hold, AssetMode.Auto);
            Assert.AreEqual("Didn't catch that · hold Talk and speak", AirTools.UI.Copy.VoiceStatus("Didn't catch that"));
            UserPrefs.Use(TalkStyle.Toggle, AssetMode.Auto);
            Assert.AreEqual("Didn't catch that · tap Talk and speak", AirTools.UI.Copy.VoiceStatus("Didn't catch that"));
        }

        // ---------------- the settings persist ----------------

        [Test]
        public void Defaults_AreToggleAndAuto()
        {
            Assert.AreEqual(TalkStyle.Toggle, UserPrefs.DefaultTalk, "ray-pinch holds dropped on the headset");
            Assert.AreEqual(TalkStyle.Toggle, UserPrefs.Talk);
            Assert.AreEqual(AssetMode.Auto, UserPrefs.Assets);
            Assert.AreEqual("auto", UserPrefs.AssetModeWire);
        }

        [Test]
        public void BothSettings_AreSavedAndReadBack()
        {
            int changed = 0;
            void Count() => changed++;
            UserPrefs.Changed += Count;
            try
            {
                UserPrefs.Talk = TalkStyle.Hold;
                UserPrefs.Assets = AssetMode.Hf;
                UserPrefs.Assets = AssetMode.Hf;   // no change
            }
            finally { UserPrefs.Changed -= Count; }
            Assert.AreEqual(2, changed);
            Assert.AreEqual((int)TalkStyle.Hold, m_Store.Values[UserPrefs.KeyTalk]);
            Assert.AreEqual((int)AssetMode.Hf, m_Store.Values[UserPrefs.KeyAssets]);
            Assert.GreaterOrEqual(m_Store.Saves, 2, "saved at once (the headset can be taken off any time)");
            UserPrefs.ResetCache();   // a relaunch
            Assert.AreEqual(TalkStyle.Hold, UserPrefs.Talk);
            Assert.AreEqual(AssetMode.Hf, UserPrefs.Assets);
            Assert.AreEqual("hf", UserPrefs.AssetModeWire);
        }

        [Test]
        public void InDemoMode_NothingIsSavedOrRead()
        {
            m_Store.Values[UserPrefs.KeyTalk] = (int)TalkStyle.Hold;
            m_Persist = false;
            UserPrefs.ResetCache();
            Assert.AreEqual(TalkStyle.Toggle, UserPrefs.Talk, "each judge starts with the default");
            UserPrefs.Assets = AssetMode.LlmScad;
            Assert.IsFalse(m_Store.Values.ContainsKey(UserPrefs.KeyAssets));
        }

        [Test]
        public void AStoredValueOutOfRange_IsTheDefault()
        {
            m_Store.Values[UserPrefs.KeyTalk] = 42;
            m_Store.Values[UserPrefs.KeyAssets] = -3;
            Assert.AreEqual(TalkStyle.Toggle, UserPrefs.Talk);
            Assert.AreEqual(AssetMode.Auto, UserPrefs.Assets);
        }

        [TestCase("hf", AssetMode.Hf)]
        [TestCase("HF", AssetMode.Hf)]
        [TestCase("hunyuan", AssetMode.Hf)]
        [TestCase("llm_scad", AssetMode.LlmScad)]
        [TestCase("LLM+CAD", AssetMode.LlmScad)]
        [TestCase("llm-scad", AssetMode.LlmScad)]
        [TestCase("auto", AssetMode.Auto)]
        [TestCase("", AssetMode.Auto)]
        [TestCase(null, AssetMode.Auto)]
        public void AssetMode_ParsesAndRoundTrips(string s, AssetMode want)
        {
            Assert.AreEqual(want, UserPrefs.ParseAssetMode(s));
            Assert.AreEqual(want, UserPrefs.ParseAssetMode(UserPrefs.Wire(want)));
        }

        [Test]
        public void TalkStyle_Parses()
        {
            Assert.AreEqual(TalkStyle.Hold, UserPrefs.ParseTalk("Hold"));
            Assert.AreEqual(TalkStyle.Toggle, UserPrefs.ParseTalk("tap"));
            Assert.AreEqual(TalkStyle.Auto, UserPrefs.ParseTalk("auto"));
            Assert.IsNull(UserPrefs.ParseTalk("shout"));
        }

        // ---------------- 3D models: context, body, URLs ----------------

        [Test]
        public void TheContextAndTheSearchBody_CarryAssetMode()
        {
            var ctx = AgentContext.AddAssetMode(new Dictionary<string, object> { ["site"] = "kitchen" }, AssetMode.LlmScad);
            Assert.AreEqual("llm_scad", ctx["asset_mode"]);
            Assert.AreEqual("kitchen", ctx["site"]);
            Assert.AreEqual("hf", AgentContext.AddAssetMode(new Dictionary<string, object>(), AssetMode.Hf)["asset_mode"]);
            var body = AssetModes.AddTo(new Dictionary<string, object> { ["query"] = "dishwasher" }, AssetMode.Hf);
            Assert.AreEqual("hf", body["asset_mode"]);
            Assert.AreEqual("auto", AssetModes.AddTo(new Dictionary<string, object>(), AssetMode.Auto)["asset_mode"]);
        }

        [Test]
        public void ModelAndPartUrls_GetTheMode_VariantsKeepTheirs()
        {
            Assert.AreEqual("/parts/dw-1/model.glb?mode=hf", AssetModes.ModelUrl("dw-1", AssetMode.Hf));
            Assert.AreEqual("/parts/dw-1/part.json?mode=llm_scad", AssetModes.PartUrl("dw-1", AssetMode.LlmScad));
            Assert.AreEqual("/parts/dw-1/model.glb?mode=auto", AssetModes.WithMode("/parts/dw-1/model.glb?mode=hf", AssetMode.Auto), "replaced, not doubled");
            Assert.AreEqual("http://10.0.0.2:8009/parts/dw-1/model.glb?v=2&mode=hf", AssetModes.WithMode("http://10.0.0.2:8009/parts/dw-1/model.glb?v=2", AssetMode.Hf));
            Assert.AreEqual("/parts/dw-1/model-bronze.glb", AssetModes.WithMode("/parts/dw-1/model-bronze.glb", AssetMode.Hf), "a finish variant keeps its model");
            Assert.AreEqual("/parts/dw-1/image.jpg", AssetModes.WithMode("/parts/dw-1/image.jpg", AssetMode.Hf));
            Assert.IsNull(AssetModes.WithMode(null, AssetMode.Hf));
        }

        [Test]
        public void TheCardBadge_SaysWhatMadeTheModel()
        {
            PartAsset A(string tier, string by = null, string note = null, string mode = "hf") =>
                new PartAsset { tier = tier, made_by = by, note = note, mode = mode, status = "ready" };
            Assert.AreEqual("AI mesh · Hunyuan", AssetModes.TierLine(A("ai_mesh", "hf:tencent/Hunyuan3D-2.1")));
            Assert.AreEqual("CAD · Grok 4.7 · OpenSCAD", AssetModes.TierLine(A("scad", "xai:grok-4.7 + openscad", mode: "llm_scad")));   // cad
            Assert.AreEqual("Template · no OpenSCAD",
                AssetModes.TierLine(A("llm", "groq:qwen/qwen3.8-27b", "OpenSCAD isn't installed on the server: the LLM template instead", "llm_scad")));
            Assert.AreEqual("Template · HF quota spent", AssetModes.TierLine(A("llm", note: "Hunyuan's ZeroGPU quota is spent for now: the LLM template instead")));
            Assert.AreEqual("Template · HF shape off", AssetModes.TierLine(A("llm", note: "Hunyuan's shape didn't match the listed size (74% off): the LLM template instead")));
            Assert.AreEqual("Template · CAD on its way", AssetModes.TierLine(A("llm", note: "Grok is still writing the OpenSCAD model: the LLM template instead", mode: "llm_scad")));
            Assert.AreEqual("Box", AssetModes.TierLine(A("proxy", mode: "auto")));
            Assert.IsNull(AssetModes.TierLine(new PartAsset { tier = "llm" }), "an older server: the card keeps Copy.TierLabel");
            Assert.AreEqual("3D model: AI mesh · Hunyuan", AssetModes.SwappedLine(A("ai_mesh", "hf:tencent/Hunyuan3D-2.1")));
        }

        [Test]
        public void Compare_FlipsBetweenHfAndLlmCad()
        {
            Assert.AreEqual(AssetMode.LlmScad, AssetModes.CompareTarget(AssetMode.Hf));
            Assert.AreEqual(AssetMode.Hf, AssetModes.CompareTarget(AssetMode.LlmScad));
            Assert.AreEqual(AssetMode.Hf, AssetModes.CompareTarget(AssetMode.Auto));
            Assert.AreEqual(AssetMode.Hf, AssetModes.ModeOf(new PartAsset { mode = "hf" }));
            Assert.IsNull(AssetModes.ModeOf(new PartAsset { tier = "llm" }));
        }

        [Test]
        public void TheSettingsChips_AreInOrder()
        {
            CollectionAssert.AreEqual(new[] { TalkStyle.Hold, TalkStyle.Toggle, TalkStyle.Auto }, SettingsPrefsRows.TalkOrder);
            CollectionAssert.AreEqual(new[] { AssetMode.Hf, AssetMode.LlmScad, AssetMode.Auto }, SettingsPrefsRows.ModelOrder);
            Assert.AreEqual("LLM+CAD", UserPrefs.Label(AssetMode.LlmScad));
            Assert.AreEqual("HF", UserPrefs.Label(AssetMode.Hf));
            Assert.AreEqual("Toggle", UserPrefs.Label(TalkStyle.Toggle));
        }
    }
}
