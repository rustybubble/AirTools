using System;
using System.Collections.Generic;
using System.Text;
using AirTools.Core;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Scene
{
    /// The removable components of the loaded scene package (backend docs/api.md "parts.r&lt;rev&gt;.json"): SceneStreamer
    /// loads the split mesh (`part_<id>` + `background`), the split collision (same names) and the cavities
    /// (`cavity_<id>`), and binds them here. This keeps the id → (visual node, collision node, cavity node) lookup;
    /// cavities start hidden. Lives on the SceneStreamer's GameObject (wired by Wire Main Scene; added at runtime
    /// otherwise).
    ///
    /// Taking a part out is one undoable edit (EditHistory) of three steps: hide the visual `part_<id>`, hide the
    /// collision `part_<id>` (so the tape and snapping don't stop on a ghost), show `cavity_<id>` — plus the cavity's
    /// outline and "W × H × D · estimated" label (CavityView, a task claim in WorldLabels). Putting it back is the
    /// reverse. Hands: Settings ▸ Take out (ScenePartsRow); voice: the remove_component action (ScenePartsActions); both go
    /// through AppCommands.RemoveComponent / RestoreComponent / ToggleComponent. DemoReset puts everything back.
    public class SceneParts : MonoBehaviour, IEditable
    {
        public SceneRoot sceneRoot;

        /// A component's three nodes; any may be null (a component with no faces has no mesh node; a package without
        /// cavities has no cavity node).
        public struct Nodes
        {
            public Transform visual, collision, cavity;
            public override string ToString() => $"visual={State(visual)} collision={State(collision)} cavity={State(cavity)}";
            static string State(Transform t) => t == null ? "-" : t.gameObject.activeSelf ? "on" : "off";
        }

        public ScenePartsDoc Doc { get; private set; }
        public string Site { get; private set; }
        public int Revision { get; private set; }
        /// Bumped on every bind, unbind, removal and restore (the Settings row redraws when it moves).
        public int Version { get; private set; }
        public event Action Changed;
        public string LastAction { get; private set; } = "";

        /// The package has components the user may take out.
        public bool HasParts => Doc != null && m_Removable.Count > 0;
        public IReadOnlyList<ScenePartComponent> Removable => m_Removable;
        /// Node names the bind looked for and didn't find ("visual part_dw1", …).
        public IReadOnlyList<string> Missing => m_Missing;
        /// Ids that are out, oldest first.
        public IReadOnlyList<string> RemovedIds => m_Out;
        public int RemovedCount => m_Out.Count;
        public bool IsRemoved(string id) => !string.IsNullOrEmpty(id) && m_Out.Contains(id);
        /// The part taken out most recently and still out (place_part without a pose goes into its cavity).
        public ScenePartComponent LastRemoved => m_Out.Count > 0 ? Doc?.Find(m_Out[m_Out.Count - 1]) : null;

        readonly Dictionary<string, Nodes> m_Nodes = new Dictionary<string, Nodes>();
        readonly List<ScenePartComponent> m_Removable = new List<ScenePartComponent>();
        readonly List<string> m_Missing = new List<string>();
        readonly List<string> m_Out = new List<string>();
        readonly Dictionary<string, CavityView> m_Views = new Dictionary<string, CavityView>();
        /// Undo / redo: (id, removed = the state that edit put it in, when).
        readonly List<(string id, bool removed, float at)> m_History = new List<(string, bool, float)>();
        readonly List<(string id, bool removed, float at)> m_Redo = new List<(string, bool, float)>();
        SceneRoot m_Hooked;

        SceneRoot Root => sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();

        void OnEnable()
        {
            Services.Register(this);
            EditHistory.Register(this);
        }

        void OnDisable()
        {
            Services.Unregister(this);
            EditHistory.Unregister(this);
            Hook(null);
        }

        // ---------------- binding ----------------

        /// A new package (or revision) is in: look up every component's nodes and hide the cavities. A revision swapped
        /// in place (same site) keeps what was out. `visual` / `collision` / `cavities` are the instantiated GLB holders
        /// (collision may be a copy of the split mesh when the package has no collision mesh; cavities may be null).
        public void Bind(string site, int revision, ScenePartsDoc doc, GameObject visual, GameObject collision, GameObject cavities)
        {
            var keep = site == Site && Doc != null ? new List<string>(m_Out) : null;
            // sitescope: a revision in place keeps its undo / redo too; a site loaded again gets back what was out.
            var saved = keep != null ? Snapshot() : TakeSaved(site);
            if (keep == null && saved != null) keep = saved.Out;
            ClearBinding();
            Doc = doc;
            Site = site;
            Revision = revision;
            Hook(Root);
            if (doc != null)
            {
                foreach (var c in doc.components)
                {
                    var n = new Nodes
                    {
                        visual = Look(visual, c.VisualNode, "visual"),
                        collision = Look(collision, c.CollisionNode, "collision"),
                        cavity = Look(cavities, c.CavityNode, "cavity"),
                    };
                    if (n.cavity != null) n.cavity.gameObject.SetActive(false);
                    m_Nodes[c.id] = n;
                    if (c.removable) m_Removable.Add(c);
                }
                Log.Info($"SceneParts: {site} r{revision}: {m_Removable.Count} removable of {doc.components.Count} " +
                         $"[{string.Join(", ", m_Removable.ConvertAll(c => $"{c.id} {c.DisplayName}"))}]" +
                         (m_Missing.Count > 0 ? $"; missing nodes: {string.Join(", ", m_Missing)}" : ""));
                if (keep != null)
                    foreach (var id in keep)
                        if (doc.Find(id) is ScenePartComponent c && c.removable && c.id == id) Apply(c, true, quiet: true);
                if (saved != null) { m_History.AddRange(saved.History); m_Redo.AddRange(saved.Redo); }   // sitescope
            }
            Bump();
            if (saved != null) EditHistory.NotifyChanged();   // sitescope
        }

        Transform Look(GameObject holder, string name, string what)
        {
            if (holder == null) return null;
            var t = FindNode(holder.transform, name);
            if (t == null) m_Missing.Add($"{what} {name}");
            return t;
        }

        /// The package is gone (another site, the built-in scene, a load without parts).
        public void Unbind()
        {
            bool had = Doc != null;
            SaveSite();   // sitescope: what's out (and its undo / redo) waits for this site's next load
            ClearBinding();
            Doc = null;
            Site = null;
            Revision = 0;
            if (had) { Bump(); EditHistory.NotifyChanged(); }
        }

        void ClearBinding()
        {
            foreach (var v in m_Views.Values)
                if (v != null) { if (Application.isPlaying) Destroy(v.gameObject); else DestroyImmediate(v.gameObject); }
            m_Views.Clear();
            m_Nodes.Clear();
            m_Removable.Clear();
            m_Missing.Clear();
            m_Out.Clear();
            m_History.Clear();
            m_Redo.Clear();
            OnStructureEdited();
        }

        void Hook(SceneRoot root)
        {
            if (m_Hooked == root) return;
            if (m_Hooked != null) m_Hooked.Rescaled -= OnRescaled;
            m_Hooked = root;
            if (m_Hooked != null) m_Hooked.Rescaled += OnRescaled;
        }

        void OnRescaled(float factor) => Relabel();

        void Bump()
        {
            Version++;
            Changed?.Invoke();
        }

        public ScenePartComponent Find(string idOrLabel) => Doc?.Find(idOrLabel);

        public bool TryNodes(string id, out Nodes nodes)
        {
            nodes = default;
            return !string.IsNullOrEmpty(id) && m_Nodes.TryGetValue(id, out nodes);
        }

        public CavityView ViewOf(string id) => !string.IsNullOrEmpty(id) && m_Views.TryGetValue(id, out var v) ? v : null;

        // ---------------- out / back ----------------

        /// Take a part out (by id, label or class): one undoable edit. True when it is out (already out counts: a
        /// repeated voice command is not an error).
        public bool Remove(string idOrLabel) => Set(idOrLabel, removed: true);

        /// Put a part back: one undoable edit.
        public bool Restore(string idOrLabel) => Set(idOrLabel, removed: false);

        /// Out if it's in, back if it's out (the Settings chips).
        public bool Toggle(string idOrLabel)
        {
            var c = Find(idOrLabel);
            return c != null && Set(c.id, !IsRemoved(c.id));
        }

        bool Set(string idOrLabel, bool removed)
        {
            var c = Find(idOrLabel);
            if (c == null) { LastAction = $"no component '{idOrLabel}'"; return false; }
            if (!c.removable) { LastAction = $"{c.id} is not removable"; return false; }
            if (IsRemoved(c.id) == removed) { LastAction = $"{c.id} already {(removed ? "out" : "in")}"; return true; }
            Apply(c, removed);
            m_History.Add((c.id, removed, AirTools.Tools.EditHistory.Stamp()));
            DropRedo();
            EditHistory.Edited(this);
            Say(c, removed);
            return true;
        }

        /// The three steps (and the cavity's outline + label), with no history.
        void Apply(ScenePartComponent c, bool removed, bool quiet = false)
        {
            m_Nodes.TryGetValue(c.id, out var n);
            if (n.visual != null) n.visual.gameObject.SetActive(!removed);
            if (n.collision != null) n.collision.gameObject.SetActive(!removed);
            if (n.cavity != null) n.cavity.gameObject.SetActive(removed);
            m_Out.Remove(c.id);
            if (removed) m_Out.Add(c.id);
            var view = removed ? View(c) : ViewOf(c.id);
            if (view != null) view.Show(removed);
            Physics.SyncTransforms();
            OnStructureEdited();
            var size = view != null ? view.SizeScene : Vector3.zero;
            LastAction = removed
                ? $"removed {c.id}: {n}" + (view != null ? $"; gap {Units.TripleMm(size.x, size.y, size.z)} (estimated)" : "")
                : $"restored {c.id}: {n}";
            if (!quiet) Log.Info($"SceneParts: {LastAction}");
            Bump();
        }

        CavityView View(ScenePartComponent c)
        {
            if (m_Views.TryGetValue(c.id, out var v) && v != null) return v;
            var root = Root;
            if (root == null || root.StructureSpace == null) return null;
            if (!CavityBox.TryFrom(c, out var box, out var why)) { Log.Warn($"SceneParts: {c.id} has no usable cavity ({why}); no outline"); return null; }
            var style = Services.Get<MeasureTool>()?.style;
            v = CavityView.Create(root.StructureSpace, c, box, style, root);
            m_Views[c.id] = v;
            return v;
        }

        void Say(ScenePartComponent c, bool removed)
        {
            var view = ViewOf(c.id);
            if (!removed) { UiToast.Show(Copy.PartBack(c.DisplayName), ColorRole.Info); return; }
            if (view == null) { UiToast.Show(Copy.PartOut(c.DisplayName, null, null, null), ColorRole.Info); return; }
            var s = view.SizeScene;
            UiToast.Show(Copy.PartOut(c.DisplayName, s.x, s.y, s.z), ColorRole.Info);
        }

        /// Everything back in, no history kept (DemoReset, a new judge).
        public void ResetSession()
        {
            m_Saved.Clear();   // sitescope: every site's parts back in
            for (int i = m_Out.Count - 1; i >= 0; i--)
                if (Doc?.Find(m_Out[i]) is ScenePartComponent c) Apply(c, false, quiet: true);
            m_Out.Clear();
            m_History.Clear();
            m_Redo.Clear();
            OnStructureEdited();
            LastAction = "reset: every part back";
            Bump();
            EditHistory.NotifyChanged();
        }

        /// Re-word the cavity labels (a units switch, a new calibration).
        public void Relabel()
        {
            foreach (var v in m_Views.Values) if (v != null) v.Relabel();
        }

        /// The structure layer tools snap to follows what is out: the published layer minus the removed components'
        /// features, plus their cavities' faces, edges and corners (ScenePartsLayer), so a tape across an opening snaps
        /// to its jambs. Nothing out → the published layer. The structure overlay (Settings ▸ Show edges) redraws.
        void OnStructureEdited()
        {
            var root = Root;
            if (root == null) return;
            StructureLayer edited = null;
            if (Doc != null && m_Out.Count > 0)
            {
                var removed = new List<(ScenePartComponent, CavityBox)>(m_Out.Count);
                foreach (var id in m_Out)
                    if (Doc.Find(id) is ScenePartComponent c && CavityBox.TryFrom(c, out var box, out _)) removed.Add((c, box));
                edited = ScenePartsLayer.Compose(root.BaseStructure, removed);
            }
            if (root.Structure == (edited ?? root.BaseStructure)) return;
            root.SetEditedStructure(edited);
            if (Services.TryGet<AirTools.Structure.StructureOverlay>(out var overlay) && overlay.Visible) overlay.Rebuild();
        }

        /// The layer tools snap to right now (the edited one while parts are out).
        public StructureLayer ActiveStructure => Root != null ? Root.Structure : null;

        // ---------------- undo / redo (EditHistory) ----------------

        public bool CanUndo => m_History.Count > 0;
        public bool CanRedo => m_Redo.Count > 0;
        public float LastEditAt => m_History.Count > 0 ? m_History[m_History.Count - 1].at : float.NegativeInfinity;
        public float LastUndoAt => m_Redo.Count > 0 ? m_Redo[m_Redo.Count - 1].at : float.NegativeInfinity;

        public void Undo()
        {
            if (m_History.Count == 0) return;
            var op = m_History[m_History.Count - 1];
            m_History.RemoveAt(m_History.Count - 1);
            if (Doc?.Find(op.id) is ScenePartComponent c)
            {
                Apply(c, !op.removed);
                Say(c, !op.removed);
            }
            m_Redo.Add((op.id, op.removed, AirTools.Tools.EditHistory.Stamp()));
        }

        public void Redo()
        {
            if (m_Redo.Count == 0) return;
            var op = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            if (Doc?.Find(op.id) is ScenePartComponent c)
            {
                Apply(c, op.removed);
                Say(c, op.removed);
            }
            m_History.Add((op.id, op.removed, AirTools.Tools.EditHistory.Stamp()));
        }

        public void DropRedo() => m_Redo.Clear();

        // ---------------- sitescope: what's out belongs to its site ----------------

        /// One site's removals while another site is loaded (SceneStreamer unbinds before every fresh load, so the state
        /// is saved in Unbind and given back in Bind; the cavity views are rebuilt then).
        sealed class SavedSite
        {
            public readonly List<string> Out = new List<string>();
            public readonly List<(string id, bool removed, float at)> History = new List<(string, bool, float)>();
            public readonly List<(string id, bool removed, float at)> Redo = new List<(string, bool, float)>();
        }

        readonly Dictionary<string, SavedSite> m_Saved = new Dictionary<string, SavedSite>();

        /// Sites whose removals are saved (not the bound one).
        public int SavedSites => m_Saved.Count;

        /// Ids saved as out for `site` (empty when none or it's the bound one).
        public IReadOnlyList<string> SavedRemoved(string site) =>
            site != null && m_Saved.TryGetValue(site, out var s) ? s.Out : (IReadOnlyList<string>)Array.Empty<string>();

        SavedSite Snapshot()
        {
            var s = new SavedSite();
            s.Out.AddRange(m_Out);
            s.History.AddRange(m_History);
            s.Redo.AddRange(m_Redo);
            return s;
        }

        void SaveSite()
        {
            if (Doc == null || string.IsNullOrEmpty(Site)) return;
            if (m_Out.Count == 0 && m_History.Count == 0 && m_Redo.Count == 0) { m_Saved.Remove(Site); return; }
            m_Saved[Site] = Snapshot();
        }

        SavedSite TakeSaved(string site)
        {
            if (string.IsNullOrEmpty(site) || !m_Saved.TryGetValue(site, out var s)) return null;
            m_Saved.Remove(site);
            return s;
        }
        // end sitescope

        // ---------------- lookup ----------------

        /// A node by name under `root`: Transform.Find for a direct child (the names use `_`, never `/`, so Find reads
        /// them as one name), else breadth-first through the hierarchy (glTFast may put the nodes under a scene root,
        /// and trimesh exports a "world" parent).
        public static Transform FindNode(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            var direct = root.Find(name);
            if (direct != null) return direct;
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name == name) return c;
                    queue.Enqueue(c);
                }
            }
            return null;
        }

        /// One line per component for the harness: "dw1 Dishwasher out visual=off collision=off cavity=on".
        public string Report()
        {
            if (Doc == null) return "no scene parts";
            var sb = new StringBuilder($"{Site} r{Revision}: {m_Removable.Count} removable, {m_Out.Count} out");
            foreach (var c in Doc.components)
            {
                m_Nodes.TryGetValue(c.id, out var n);
                var v = ViewOf(c.id);
                sb.Append($"\n{c.id} {c.DisplayName}{(c.removable ? "" : " (fixed)")} {(IsRemoved(c.id) ? "out" : "in")} {n}");
                if (v != null && v.Shown) sb.Append($" label=\"{v.LabelText}\"{(v.LabelShown ? "" : " (over the pool)")}");
            }
            if (m_Missing.Count > 0) sb.Append($"\nmissing: {string.Join(", ", m_Missing)}");
            return sb.ToString();
        }
    }
}
