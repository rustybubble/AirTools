using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// settings-assets: placed and held parts follow Settings ▸ 3D models (backend asset modes: HF image-to-3D, LLM +
    /// OpenSCAD, Auto), and the card's Compare chip flips one placed part between its HF and LLM+CAD models.
    ///
    /// A change of the setting reloads every server part in the scene (placed, held, their array copies) in the new mode:
    /// part.json?mode= (the server makes it if it has to: a Hunyuan mesh ~20 s, pending meanwhile) → model.glb?mode= →
    /// PlacementEditor.SwapModelInPlace, which keeps each placed part's anchor. While it loads the card's tier line reads
    /// "Rebuilding the 3D model…"; then the new model's badge ("AI mesh · Hunyuan", "Template · no OpenSCAD"). Parts from
    /// the shipped catalog (offline) have one model and stay. One load per part id; a newer switch
    /// for the same part supersedes the running one (its model is dropped when it lands).
    /// cad: a part that shows the LLM+CAD template while Grok writes its CAD model ("Template · Grok is writing the CAD
    /// model… 2 min") is watched (CadUpgrade): placed and held parts only, part.json?mode=llm_scad every 10–15 s, and the
    /// CAD model is swapped in (same anchor, same swap path) when it lands.
    public class AssetModeSwitcher : MonoBehaviour
    {
        public PartTool tool;
        public PartLoader loader;
        public PartsClient client;
        public PlacementEditor editor;

        public const string Rebuilding = "Rebuilding the 3D model…";

        public static AssetModeSwitcher Current { get; private set; }

        /// What the last switch did (the harness logs it).
        public string LastSwitch { get; private set; } = "";
        public int Swaps { get; private set; }
        public int Failures { get; private set; }
        public int Started { get; private set; }
        /// Loads still running.
        public int Pending => m_Jobs.Count;

        sealed class Job
        {
            public string Id;
            public AssetMode Mode;
            public int Token;
            public PartInstance Part;
            public bool CompareOnly;
        }

        readonly Dictionary<string, Job> m_Jobs = new Dictionary<string, Job>();
        int m_Token;
        AssetMode m_Applied;
        float m_LastFailToast = -10f;

        // cad: the CAD upgrade watches, one per part id.
        sealed class Watch
        {
            public string Id;
            public string Name;
            public float Since;
            public float NextAt;
            public int Quiet;
            public int Polls;
            public bool InFlight;
        }

        readonly Dictionary<string, Watch> m_Watches = new Dictionary<string, Watch>();
        readonly List<Watch> m_Due = new List<Watch>();
        float m_NextTick;

        /// cad: parts waiting for their CAD model, upgrades done, and the last thing the watch did (the harness logs it).
        public int CadWatching => m_Watches.Count;
        public int CadUpgrades { get; private set; }
        public int CadPolls { get; private set; }
        public string LastCad { get; private set; } = "";

        PartTool Tool => tool != null ? tool : Services.Get<PartTool>();
        PartLoader Loader => loader != null ? loader : Services.Get<PartLoader>();
        PartsClient Client => client != null ? client : Services.Get<PartsClient>();
        PlacementEditor Editor => editor != null ? editor : Services.Get<PlacementEditor>();

        void OnEnable()
        {
            Services.Register(this);
            Current = this;
            m_Applied = UserPrefs.Assets;
            UserPrefs.Changed += OnPrefsChanged;
        }

        void OnDisable()
        {
            UserPrefs.Changed -= OnPrefsChanged;
            Services.Unregister(this);
            if (Current == this) Current = null;
        }

        void OnPrefsChanged()
        {
            var m = UserPrefs.Assets;
            if (m == m_Applied) return;
            m_Applied = m;
            RebuildAll(m);
        }

        /// A part the laptop made (glTF from the parts server): it has a model per mode.
        public static bool IsServerPart(PartInstance p) =>
            p != null && p.Spec != null && !string.IsNullOrEmpty(p.Spec.id) && p.Source == "server";

        /// The mode that made the model the part shows (its part.json's asset.mode; the setting for an older server).
        public static AssetMode ShownMode(PartInstance p) => AssetModes.ModeOf(p?.Spec?.asset) ?? UserPrefs.Assets;

        /// The part shows another mode's model than the setting's (Compare is on).
        public static bool Comparing(PartInstance p) => IsServerPart(p) && AssetModes.ModeOf(p.Spec.asset) is AssetMode m && m != UserPrefs.Assets;

        /// "Rebuilding the 3D model…" while this part's model is loading; null otherwise.
        public string StatusFor(PartInstance p) => p != null && p.Spec != null && m_Jobs.ContainsKey(p.Spec.id) ? Rebuilding : null;

        /// The server parts in the scene: placed ones and the one in hand, one per part id.
        public List<PartInstance> ServerParts()
        {
            var list = new List<PartInstance>();
            var seen = new HashSet<string>();
            var t = Tool;
            if (t == null) return list;
            void Add(PartInstance p) { if (IsServerPart(p) && seen.Add(p.Spec.id)) list.Add(p); }
            Add(t.Held);
            foreach (var p in t.PlacedParts) Add(p);
            return list;
        }

        /// Every server part in the scene reloads its model in `mode` (Settings ▸ 3D models did this). The count started.
        public int RebuildAll(AssetMode mode)
        {
            int n = 0;
            foreach (var p in ServerParts()) if (Switch(p, mode, compareOnly: false)) n++;
            string label = UserPrefs.Label(mode);
            UiToast.Show(n == 0 ? $"3D models: {label} · parts you add load that way" : $"{Rebuilding} ({n} part{(n == 1 ? "" : "s")}, {label})", ColorRole.Info);
            LastSwitch = $"setting {UserPrefs.Wire(mode)}: {n} part(s) reloading";
            Log.Info($"Asset mode: {LastSwitch}");
            return n;
        }

        /// Compare (the card's chip): the selected (or held) part flips between its HF and LLM+CAD models in place.
        public bool Compare(PartInstance part = null)
        {
            var t = Tool;
            if (part == null && t != null) part = t.Selected != null ? t.Selected : t.Held;
            if (part == null) { UiToast.Show("Select a placed part to compare its 3D models", ColorRole.Info); return false; }
            if (!IsServerPart(part)) { UiToast.Show("Only parts from the laptop have both 3D models", ColorRole.Info); return false; }
            var target = AssetModes.CompareTarget(ShownMode(part));
            bool ok = Switch(part, target, compareOnly: true);
            if (ok) UiToast.Show($"{Rebuilding} ({UserPrefs.Label(target)})", ColorRole.Info);
            return ok;
        }

        /// Load `part`'s model made in `mode` and swap it in (compareOnly: this part and its array copies; else every
        /// server part in the scene with the same id). False when there's nothing to do. cad: `force` reloads a mode the
        /// part already shows (its CAD model landed).
        public bool Switch(PartInstance part, AssetMode mode, bool compareOnly, bool force = false)
        {
            if (!IsServerPart(part)) return false;
            string id = part.Spec.id;
            if (!force && !compareOnly && AssetModes.ModeOf(part.Spec.asset) == mode && !m_Jobs.ContainsKey(id)) return false;   // already that model
            var c = Client;
            var l = Loader;
            if (c == null || l == null) return false;
            var job = new Job { Id = id, Mode = mode, Token = ++m_Token, Part = part, CompareOnly = compareOnly };
            m_Jobs[id] = job;   // supersedes a running one
            Started++;
            var summary = new PartSummary { id = id, name = part.Spec.name };
            c.GetSpec(summary, (spec, source) =>
            {
                if (!Live(job)) return;
                if (spec == null || source != "server") { Fail(job, "the laptop isn't answering"); return; }
                if (AssetModes.ModeOf(spec.asset) == null) { Fail(job, "the laptop's parts server has no 3D-model modes yet"); return; }
                if (spec.asset.Ready) { Fetch(job, spec); return; }
                c.WaitForAsset(spec, null, (fresh, ready) =>
                {
                    if (!Live(job)) return;
                    if (ready) Fetch(job, fresh);
                    else Fail(job, fresh.asset.Failed ? "the server couldn't make it" : "it took too long");
                }, mode);
            }, mode);
            return true;
        }

        bool Live(Job job) => m_Jobs.TryGetValue(job.Id, out var j) && j.Token == job.Token;

        void Fetch(Job job, PartSpec spec)
        {
            var l = Loader;
            if (l == null) { Fail(job, "no loader"); return; }
            l.LoadVariant(spec, AssetModes.ModelUrl(job.Id, job.Mode), (model, owner) =>
            {
                if (!Live(job)) { Release(model, owner); return; }
                m_Jobs.Remove(job.Id);
                if (model == null) { Fail(job, "its model didn't load", removed: true); return; }
                var group = Group(job);
                if (group.Count == 0) { Release(model, owner); LastSwitch = $"{job.Id}: gone before its model arrived"; return; }
                if (spec.asset.cad != null) spec.asset.cad.receivedAt = Time.realtimeSinceStartup;   // cad: the countdown
                foreach (var p in group) if (p.Spec != null) p.Spec.asset = spec.asset;
                var e = Editor;
                int n = e != null ? e.SwapModelInPlace(group, model, owner, UserPrefs.Wire(job.Mode)) : SwapPlain(group, model, owner);
                Swaps++;
                LastSwitch = $"{job.Id} → {UserPrefs.Wire(job.Mode)}: {spec.asset.tier} by {spec.asset.made_by ?? "-"} on {n} part(s)" +
                             (string.IsNullOrEmpty(spec.asset.note) ? "" : $" ({spec.asset.note})");
                Log.Info($"Asset mode: {LastSwitch}");
                UiToast.Show(AssetModes.SwappedLine(spec.asset), ColorRole.Success);
            });
        }

        static int SwapPlain(List<PartInstance> group, GameObject model, IDisposable owner)
        {
            PartInstance.SwapModels(group, model, owner, group.Count > 0 ? group[0].FinishName : null, null);
            return group.Count;
        }

        /// The parts that get the new model: the part and its array copies (Compare), or every server part in the scene
        /// with this id and their copies (the setting). Skips destroyed ones.
        List<PartInstance> Group(Job job)
        {
            var group = new List<PartInstance>();
            var t = Tool;
            void AddWithCopies(PartInstance p)
            {
                if (p == null) return;
                if (t == null) { if (!group.Contains(p)) group.Add(p); return; }
                foreach (var q in t.FinishGroup(p)) if (q != null && !group.Contains(q)) group.Add(q);
            }
            if (job.CompareOnly || t == null) AddWithCopies(job.Part);
            else
            {
                if (t.Held != null && t.Held.Spec != null && t.Held.Spec.id == job.Id) AddWithCopies(t.Held);
                foreach (var p in t.PlacedParts) if (p != null && p.Spec != null && p.Spec.id == job.Id) AddWithCopies(p);
            }
            return group;
        }

        void Fail(Job job, string why, bool removed = false)
        {
            if (!removed) m_Jobs.Remove(job.Id);
            Failures++;
            LastSwitch = $"{job.Id} → {UserPrefs.Wire(job.Mode)}: {why}";
            Log.Warn($"Asset mode: {LastSwitch}");
            if (Time.unscaledTime - m_LastFailToast > 3f)
            {
                m_LastFailToast = Time.unscaledTime;
                UiToast.Show($"Couldn't rebuild the 3D model · {why}", ColorRole.Warning);
            }
        }

        static void Release(GameObject model, IDisposable owner)
        {
            if (model != null)
            {
                if (Application.isPlaying) Destroy(model);
                else DestroyImmediate(model);
            }
            owner?.Dispose();
        }

        // --- cad: the CAD upgrade (CadUpgrade) ------------------------------------------------------------------------

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < m_NextTick) return;
            m_NextTick = now + 1f;
            ScanForCad(now);
            PollDueCad(now);
        }

        /// Placed and held parts that show the LLM+CAD template while Grok writes get a watch (no allocation per tick).
        void ScanForCad(float now)
        {
            var t = Tool;
            if (t == null) return;
            ConsiderCad(t.Held, now);
            var placed = t.PlacedParts;
            for (int i = 0; i < placed.Count; i++) ConsiderCad(placed[i], now);
        }

        void ConsiderCad(PartInstance p, float now)
        {
            if (!IsServerPart(p) || !CadUpgrade.ShouldWatch(p.Spec.asset)) return;
            string id = p.Spec.id;
            if (m_Watches.ContainsKey(id) || m_Jobs.ContainsKey(id)) return;
            var cad = p.Spec.asset.cad;
            if (cad.receivedAt <= 0f) cad.receivedAt = now;
            m_Watches[id] = new Watch { Id = id, Name = p.Spec.name, Since = now, NextAt = now + CadUpgrade.NextPoll(cad) };
            LastCad = $"{id}: watching (Grok {CadUpgrade.Describe(cad, now)})";
            Log.Info($"Asset mode: {LastCad}");
        }

        void PollDueCad(float now)
        {
            if (m_Watches.Count == 0) return;
            m_Due.Clear();
            foreach (var w in m_Watches.Values) if (!w.InFlight && now >= w.NextAt) m_Due.Add(w);
            var c = Client;
            if (c == null) return;
            for (int i = 0; i < m_Due.Count; i++)
            {
                var w = m_Due[i];
                w.InFlight = true;
                w.Polls++;
                CadPolls++;
                c.GetSpec(new PartSummary { id = w.Id, name = w.Name }, (spec, source) => OnCadPoll(w, spec, source), AssetMode.LlmScad);
            }
        }

        void OnCadPoll(Watch w, PartSpec spec, string source)
        {
            w.InFlight = false;
            if (!m_Watches.TryGetValue(w.Id, out var live) || live != w) return;
            float now = Time.realtimeSinceStartup;
            var shown = ShownPart(w.Id);
            var fresh = source == "server" ? spec?.asset : null;
            if (fresh?.cad != null) fresh.cad.receivedAt = now;
            var step = CadUpgrade.Decide(shown?.Spec?.asset, fresh, w.Quiet, now - w.Since);
            switch (step)
            {
                case CadStep.Swap:
                    m_Watches.Remove(w.Id);
                    CadUpgrades++;
                    LastCad = $"{w.Id}: CAD model ready ({fresh.made_by ?? "-"}) after {now - w.Since:0} s, {w.Polls} poll(s): swapping in";
                    Log.Info($"Asset mode: {LastCad}");
                    Switch(shown, AssetMode.LlmScad, compareOnly: Comparing(shown), force: true);
                    break;
                case CadStep.Stop:
                    m_Watches.Remove(w.Id);
                    if (fresh != null) ApplyCad(w.Id, fresh);
                    LastCad = $"{w.Id}: stopped waiting for the CAD model ({CadUpgrade.Describe(fresh?.cad, now)}, {now - w.Since:0} s)";
                    Log.Warn($"Asset mode: {LastCad}");
                    break;
                case CadStep.Drop:
                    m_Watches.Remove(w.Id);
                    LastCad = $"{w.Id}: no longer shows the LLM+CAD template";
                    break;
                default:
                    w.Quiet = CadUpgrade.Quiet(fresh) ? w.Quiet + 1 : 0;
                    if (fresh?.cad != null) ApplyCad(w.Id, fresh);
                    w.NextAt = now + CadUpgrade.NextPoll(fresh?.cad);
                    LastCad = $"{w.Id}: Grok {CadUpgrade.Describe(fresh?.cad, now)} (poll {w.Polls})";
                    break;
            }
        }

        /// A scene part with this id that still shows its LLM+CAD template (held first, then placed).
        PartInstance ShownPart(string id)
        {
            var t = Tool;
            if (t == null) return null;
            if (IsServerPart(t.Held) && t.Held.Spec.id == id) return t.Held;
            var placed = t.PlacedParts;
            for (int i = 0; i < placed.Count; i++)
                if (IsServerPart(placed[i]) && placed[i].Spec.id == id) return placed[i];
            return null;
        }

        /// The poll's CAD status and note onto every part with this id that shows the LLM+CAD template (the card's badge).
        void ApplyCad(string id, PartAsset fresh)
        {
            var t = Tool;
            if (t == null) return;
            void Set(PartInstance p)
            {
                if (!IsServerPart(p) || p.Spec.id != id || p.Spec.asset == null || p.Spec.asset.mode != "llm_scad" || p.Spec.asset.tier == "scad") return;
                p.Spec.asset.cad = fresh.cad;
                if (fresh.tier == p.Spec.asset.tier) p.Spec.asset.note = fresh.note;
            }
            Set(t.Held);
            var placed = t.PlacedParts;
            for (int i = 0; i < placed.Count; i++) Set(placed[i]);
        }

        /// cad: one line per watch for the harness.
        public string DescribeCad()
        {
            float now = Time.realtimeSinceStartup;
            var sb = new System.Text.StringBuilder($"cad: watching={CadWatching} polls={CadPolls} upgrades={CadUpgrades} last=\"{LastCad}\"");
            foreach (var w in m_Watches.Values)
            {
                var p = ShownPart(w.Id);
                sb.Append($"\n  {w.Id} {CadUpgrade.Describe(p?.Spec?.asset?.cad, now)} for {now - w.Since:0} s, next poll in {Math.Max(0f, w.NextAt - now):0} s, quiet={w.Quiet}");
            }
            return sb.ToString();
        }

        /// One line for the harness.
        public string Describe() =>
            $"setting={UserPrefs.Wire(UserPrefs.Assets)} pending={Pending} started={Started} swaps={Swaps} failures={Failures} last=\"{LastSwitch}\"" +
            $" | cad watching={CadWatching} upgrades={CadUpgrades}";
    }
}
