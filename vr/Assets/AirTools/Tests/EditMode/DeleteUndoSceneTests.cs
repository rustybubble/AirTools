using System.Linq;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// delete-undo (Editor gate: transforms, colliders, raycasts, the shipped catalog): every way a placed part is removed
    /// is one undo step that brings it back exactly — pose, finish, fit, selection and its notebook row — and Redo removes
    /// it again: the spec card's Remove, voice delete_part, an array member, Clear; a removed part is kept while it can
    /// come back and destroyed once it can't (a new placement after its undo, ClearAll); a rescale moves it too. The
    /// context menu's Delete is EditViewSceneTests; a model swapped in a gap and "put it back" are DeleteUndoGapTests. The
    /// pure parts (EditSteps, the step walk, the copy) are DeleteUndoTests (offline too).
    public class DeleteUndoSceneTests : FacadeToolFixture
    {
        static PartCatalog s_Catalog;
        GameObject m_Go;
        PartTool m_Parts;
        PartLoader m_Loader;
        PartsButton m_Remove;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 3) s_Catalog = PartCatalogBuilder.Build();
            PartCatalogBuilder.EnsurePartsLayer();
        }

        [SetUp]
        public void Create()
        {
            Tool.Equip(false);
            m_Go = new GameObject("deleteundo-test");
            m_Loader = m_Go.AddComponent<PartLoader>();
            m_Loader.catalog = s_Catalog;
            m_Parts = m_Go.AddComponent<PartTool>();
            m_Parts.SetInput(Hub);
            m_Parts.Equip(true);
            // EditMode runs no OnEnable for AddComponent: register by hand.
            Services.Register(m_Parts);
            EditHistory.Register(m_Parts);
            // The spec card's Remove as Wire builds it (PartsAction.Remove); Press() is what its GlassButton's second tap calls.
            var b = new GameObject("Remove");
            b.transform.SetParent(m_Go.transform, false);
            m_Remove = b.AddComponent<PartsButton>();
            m_Remove.action = PartsAction.Remove;
        }

        [TearDown]
        public void Destroy()
        {
            m_Parts.ClearAll();
            Services.Unregister(m_Parts);
            EditHistory.Unregister(m_Parts);
            foreach (var p in Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            Object.DestroyImmediate(m_Go);
            Physics.SyncTransforms();
        }

        /// A catalog hanger released onto the fascia at x, from a raised vantage straight out (the gutter's lip hides the
        /// fascia from the ground).
        PartInstance Hanger(float x)
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            Assert.IsNotNull(p, "catalog has the hanger");
            m_Parts.Hold(p);
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(new Vector3(x, 7.0f, 1.5f), new Vector3(x, 6.15f, S.FasciaProud))), m_Parts.LastAction);
            return p;
        }

        static void GutterTape() =>
            Notebook.Add(new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) }, System.DateTime.Now, -1, "gutter"));

        /// What Undo must bring back.
        struct Was
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public string Finish, Colour, Headline;
            public FitStatus Status;
            public NotebookEntry Row;
        }

        static Was Of(PartInstance p, NotebookEntry row) => new Was
        {
            Position = p.transform.position, Rotation = p.transform.rotation, Finish = p.FinishName,
            Colour = p.MaterialColor().HasValue ? PartColor.ToHex(p.MaterialColor().Value) : null,
            Headline = p.Fit?.Headline, Status = p.Fit?.Status ?? FitStatus.None, Row = row,
        };

        void AssertBack(PartInstance p, Was was, string how)
        {
            Assert.IsTrue(p != null, $"{how}: kept, not destroyed");
            Assert.IsTrue(m_Parts.PlacedParts.Contains(p), $"{how}: placed again");
            Assert.IsTrue(p.Placed && p.gameObject.activeSelf, $"{how}: in the scene");
            Assert.That(Vector3.Distance(was.Position, p.transform.position), Is.LessThan(1e-5f), $"{how}: same place");
            Assert.That(Quaternion.Angle(was.Rotation, p.transform.rotation), Is.LessThan(1e-3f), $"{how}: same turn");
            Assert.AreEqual(was.Finish, p.FinishName, $"{how}: same finish");
            Assert.AreEqual(was.Colour, p.MaterialColor().HasValue ? PartColor.ToHex(p.MaterialColor().Value) : null, $"{how}: same colour");
            Assert.AreEqual(was.Status, p.Fit?.Status ?? FitStatus.None, $"{how}: same fit");
            Assert.AreEqual(was.Headline, p.Fit?.Headline, $"{how}: same fit words");
            if (was.Row != null) Assert.AreSame(was.Row, Notebook.Find(was.Row.Id), $"{how}: its notebook row, same id");
        }

        void AssertGone(PartInstance p, Was was, string how)
        {
            Assert.IsTrue(p != null, $"{how}: kept for Undo, not destroyed");
            Assert.IsFalse(m_Parts.PlacedParts.Contains(p), $"{how}: not placed");
            Assert.IsFalse(p.Placed, how);
            Assert.IsFalse(p.gameObject.activeSelf, $"{how}: out of the scene (no collider for tapes or fits)");
            if (was.Row != null) Assert.IsNull(Notebook.Find(was.Row.Id), $"{how}: its notebook row goes too");
        }

        // ---------------- the spec card's Remove ----------------

        [Test]
        public void SpecCardRemove_UndoBringsItBackExactly_RedoRemovesItAgain()
        {
            var p = Hanger(0.4f);
            Assert.IsTrue(AppCommands.SetFinish("brown"));
            var row = Notebook.Last;
            Assert.AreEqual("part", row.Tool);
            var was = Of(p, row);
            Assert.AreEqual("#5A3E2B", was.Colour);
            Assert.AreSame(p, m_Parts.Selected);

            m_Remove.Press();
            AssertGone(p, was, "removed");
            Assert.AreNotSame(p, m_Parts.Selected, "the card moves off it");
            Assert.IsTrue(EditHistory.CanUndo, "the ring's Undo lights up");

            Assert.IsTrue(EditHistory.Undo());
            AssertBack(p, was, "undo");
            Assert.AreSame(p, m_Parts.Selected, "selected again, as it was");

            Assert.IsTrue(EditHistory.Redo());
            AssertGone(p, was, "redo");
            Assert.IsTrue(EditHistory.Undo());
            AssertBack(p, was, "undo again");
        }

        [Test]
        public void SpecCardRemove_APartInHandGoesBackToFindParts()
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            m_Parts.Hold(p);
            m_Remove.Press();
            Assert.IsNull(m_Parts.Held);
            Assert.IsTrue(p != null, "not destroyed");
            Assert.AreSame(p, m_Parts.Parked, "back on its Find parts card");
            Assert.IsTrue(m_Parts.TryUnpark(PartScenarios.Hanger), "Take it again");
            Assert.AreSame(p, m_Parts.Held);
        }

        [Test]
        public void SpecCardRemove_NothingSelected_DoesNothing()
        {
            Assert.IsFalse(AppCommands.RemoveSelectedPart());
            Assert.IsFalse(m_Parts.CanUndo);
        }

        // ---------------- voice ----------------

        [Test]
        public void VoiceDeletePart_IsOneUndoStep()
        {
            var p = Hanger(0.4f);
            var was = Of(p, Notebook.Last);
            Assert.IsTrue(AgentActions.Execute(new AgentAction { name = "delete_part", args = new JObject() }), AgentActions.LastResult);
            AssertGone(p, was, "delete_part");
            Assert.IsTrue(EditHistory.Undo());
            AssertBack(p, was, "undo");
            Assert.IsTrue(EditHistory.Redo());
            AssertGone(p, was, "redo");
        }

        // ---------------- selection ----------------

        [Test]
        public void Undo_SelectsWhatWasSelectedWhenItWent()
        {
            var a = Hanger(0.4f);
            var b = Hanger(-1.0f);
            m_Parts.Select(b);
            Assert.IsTrue(m_Parts.Delete(a));
            Assert.AreSame(b, m_Parts.Selected);
            Assert.IsTrue(EditHistory.Undo());
            Assert.AreSame(b, m_Parts.Selected, "a wasn't selected when it went: b stays");
            m_Parts.Select(a);
            Assert.IsTrue(m_Parts.Delete(a));
            Assert.AreSame(b, m_Parts.Selected, "the card moves to what's left");
            Assert.IsTrue(EditHistory.Undo());
            Assert.AreSame(a, m_Parts.Selected, "a was selected: it is again");
        }

        // ---------------- an array member ----------------

        [Test]
        public void ArrayMember_OneCopyGoes_UndoPutsItBackInItsSlot()
        {
            var reference = Hanger(0.37f);
            GutterTape();
            var g = m_Parts.PlaceArray();
            Assert.IsNotNull(g, m_Parts.LastAction);
            int n = m_Parts.PlacedParts.Count;
            Assert.AreEqual(8, n);
            var clone = g.Clones[2];
            var was = Of(clone, null);
            m_Parts.Select(clone);

            m_Remove.Press();
            AssertGone(clone, was, "one copy removed");
            Assert.AreEqual(n - 1, m_Parts.QuantityOf(PartScenarios.Hanger), "the checkout counts what's placed");
            Assert.AreSame(g, m_Parts.ArrayOf(reference), "the rest are still the array");
            Assert.AreSame(g.Entry, Notebook.Find(g.Entry.Id), "the array's row stays");

            Assert.IsTrue(EditHistory.Undo());
            AssertBack(clone, was, "undo");
            Assert.AreEqual(n, m_Parts.PlacedParts.Count);
            Assert.AreSame(g, m_Parts.ArrayOf(clone));
            Assert.AreSame(clone, m_Parts.Selected);
            Assert.IsTrue(EditHistory.Redo());
            Assert.AreEqual(n - 1, m_Parts.PlacedParts.Count, "redo: removed again");
            Assert.IsTrue(EditHistory.Undo());
            Assert.AreEqual(n, m_Parts.PlacedParts.Count);

            Assert.IsTrue(EditHistory.Undo(), "the step before it: the array");
            Assert.AreEqual(1, m_Parts.PlacedParts.Count);
            Assert.IsTrue(EditHistory.Redo());
            Assert.AreEqual(n, m_Parts.PlacedParts.Count, "the array back, that copy with it");
            Assert.IsTrue(clone.gameObject.activeSelf);
        }

        // ---------------- Clear ----------------

        [Test]
        public void Clear_IsOneUndoStep_ForEveryPartAndRow()
        {
            var reference = Hanger(0.37f);
            var refRow = Notebook.Last;
            GutterTape();
            var g = m_Parts.PlaceArray();
            Assert.IsNotNull(g, m_Parts.LastAction);
            m_Parts.Select(reference);
            var placed = m_Parts.PlacedParts.ToList();
            var at = placed.Select(p => p.transform.position).ToList();

            Hub.RaiseButton(ToolHand.Right, ToolButton.Clear);
            Assert.AreEqual(0, m_Parts.PlacedParts.Count);
            Assert.IsTrue(placed.All(p => p != null && !p.gameObject.activeSelf && !p.Placed), "hidden, kept for Undo");
            Assert.IsNull(Notebook.Find(refRow.Id), "the part's row");
            Assert.IsNull(Notebook.Find(g.Entry.Id), "the array's row");
            Assert.IsNull(m_Parts.LastArray);
            Assert.IsNull(m_Parts.Selected);
            Assert.AreEqual(placed.Count, m_Parts.RemovedKept);

            Assert.IsTrue(EditHistory.Undo(), "one step");
            CollectionAssert.AreEqual(placed, m_Parts.PlacedParts.ToList(), "all of them, in order");
            for (int k = 0; k < placed.Count; k++) Assert.That(Vector3.Distance(at[k], placed[k].transform.position), Is.LessThan(1e-5f), $"#{k} in its place");
            Assert.AreSame(refRow, Notebook.Find(refRow.Id));
            Assert.AreSame(g.Entry, Notebook.Find(g.Entry.Id));
            Assert.AreSame(g, m_Parts.LastArray, "the checkout's array evidence");
            Assert.AreSame(reference, m_Parts.Selected);
            Assert.AreEqual(placed.Count, m_Parts.QuantityOf(PartScenarios.Hanger));

            Assert.IsTrue(EditHistory.Redo());
            Assert.AreEqual(0, m_Parts.PlacedParts.Count, "redo: cleared again");
            Assert.IsTrue(EditHistory.Undo());
            Assert.AreEqual(placed.Count, m_Parts.PlacedParts.Count);
        }

        [Test]
        public void Clear_NothingPlaced_IsNoStep_AndThePartInHandStays()
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            m_Parts.Hold(p);
            Assert.AreEqual(0, m_Parts.RemoveAll());
            Assert.AreSame(p, m_Parts.Held);
            m_Parts.Undo();   // puts the part in hand back
            Assert.IsFalse(m_Parts.CanUndo);
        }

        // ---------------- kept while it can come back, dropped once it can't ----------------

        [Test]
        public void ADeletedPart_IsKeptWhileItsDeleteCanBeUndone()
        {
            var a = Hanger(0.4f);
            var was = Of(a, Notebook.Last);
            Assert.IsTrue(m_Parts.Delete(a));
            var b = Hanger(-1.0f);
            Assert.IsTrue(a != null, "a new placement doesn't touch the undo stack");
            Assert.AreEqual(1, m_Parts.RemovedKept);
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsFalse(b.gameObject.activeSelf, "b's placement first");
            Assert.IsTrue(EditHistory.Undo());
            AssertBack(a, was, "then the delete");
        }

        [Test]
        public void ADeleteThenANewPlacement_DropsTheRedoBranch_AndThePartForGood()
        {
            var a = Hanger(0.4f);
            Assert.IsTrue(m_Parts.Delete(a));
            Assert.IsTrue(EditHistory.Undo(), "a back");
            Assert.IsTrue(EditHistory.Undo(), "a's placement undone: hidden, redoable");
            Assert.IsTrue(m_Parts.CanRedo);
            Assert.IsTrue(a != null);
            Hanger(-1.0f);
            Assert.IsFalse(m_Parts.CanRedo, "a new edit ends the redo branch");
            Assert.IsTrue(a == null, "destroyed: nothing can bring it back");
        }

        [Test]
        public void AnUndoneDelete_ThenANewPlacement_KeepsThePartPlaced()
        {
            var a = Hanger(0.4f);
            Assert.IsTrue(m_Parts.Delete(a));
            Assert.IsTrue(EditHistory.Undo());
            Hanger(-1.0f);
            Assert.IsFalse(m_Parts.CanRedo, "its redo (the delete) is gone");
            Assert.IsTrue(a != null && m_Parts.PlacedParts.Contains(a) && a.gameObject.activeSelf, "a stays where Undo put it");
        }

        [Test]
        public void ClearAll_TheHardReset_DestroysRemovedParts()
        {
            var a = Hanger(0.4f);
            var b = Hanger(-1.0f);
            Assert.IsTrue(m_Parts.Delete(a));
            Assert.AreEqual(1, m_Parts.RemoveAll());
            Assert.AreEqual(2, m_Parts.RemovedKept);
            m_Parts.ClearAll();
            Assert.IsTrue(a == null && b == null, "nothing kept");
            Assert.AreEqual(0, m_Parts.RemovedKept);
            Assert.IsFalse(m_Parts.CanUndo);
            Assert.IsFalse(m_Parts.CanRedo);
        }

        // ---------------- sitescope: a removed part belongs to its site ----------------

        [Test]
        public void ADeletedPart_ParksWithItsSite_AndItsUndoComesBackWithIt()
        {
            string here = SiteScope.Current, other = here == "kitchen" ? "zabel-gymnasium" : "kitchen";
            var a = Hanger(0.4f);
            var was = Of(a, Notebook.Last);
            Assert.IsTrue(m_Parts.Delete(a));
            m_Parts.SwitchSite(here, other, 1f);
            Assert.IsFalse(m_Parts.CanUndo, "on another scan its undo isn't reachable");
            Assert.IsTrue(a != null && !a.gameObject.activeSelf, "parked, hidden");
            CollectionAssert.Contains(m_Parts.AllParkedParts().ToList(), a);
            m_Parts.SwitchSite(other, here, 1f);
            Assert.IsFalse(a.gameObject.activeSelf, "still deleted when its site is back");
            Assert.IsTrue(EditHistory.Undo());
            AssertBack(a, was, "undo on its own site");
        }

        [Test]
        public void ClearParked_DestroysAParkedSitesDeletedParts()
        {
            string here = SiteScope.Current, other = here == "kitchen" ? "zabel-gymnasium" : "kitchen";
            var a = Hanger(0.4f);
            Assert.IsTrue(m_Parts.Delete(a));
            m_Parts.SwitchSite(here, other, 1f);
            m_Parts.ClearParked();
            Assert.IsTrue(a == null, "Demo reset: nothing kept on any site");
            m_Parts.SwitchSite(other, here, 1f);
        }

        [Test]
        public void ARescale_MovesARemovedPartToo()
        {
            var a = Hanger(0.4f);
            var at = a.transform.position;
            Assert.IsTrue(m_Parts.Delete(a));
            m_Parts.RescaleAll(2f);   // no frame here: world positions scale about the origin
            Assert.IsTrue(EditHistory.Undo());
            Assert.That(Vector3.Distance(at * 2f, a.transform.position), Is.LessThan(1e-4f), "back where the scene put it");
        }
    }

    /// delete-undo (Editor gate): a model in a removed part's gap — "put it back" takes the model out and the part in as
    /// one step, and a place_part into an occupied gap swaps the models as one step; Undo / Redo take both halves.
    public class DeleteUndoGapTests
    {
        GameObject m_App, m_RootGo, m_Content, m_ToolGo;
        SceneRoot m_Root;
        SceneParts m_Parts;
        PartTool m_Tool;

        static GameObject Node(Transform parent, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            return go;
        }

        [SetUp]
        public void Build()
        {
            AirTools.UI.WorldLabels.Reset();
            ModelCycler.Reset();
            Notebook.Clear();
            m_RootGo = new GameObject("SceneRoot");
            m_Root = m_RootGo.AddComponent<SceneRoot>();
            m_Content = new GameObject("kitchen");
            m_Content.transform.SetParent(m_RootGo.transform, false);
            m_Root.SetRuntimeContent("kitchen", SceneManifest.Parse(ScenePartsParseTests.ApiMdScene), null, m_Content, null);
            Services.Register(m_Root);
            var visual = new GameObject("Mesh r1"); visual.transform.SetParent(m_Content.transform, false);
            var collision = new GameObject("Collision r1"); collision.transform.SetParent(m_Content.transform, false);
            var cavities = new GameObject("Cavities r1"); cavities.transform.SetParent(m_Content.transform, false);
            foreach (var id in new[] { "dw1", "bc1", "rg1", "fr1" })
            {
                Node(visual.transform, $"part_{id}");
                Node(collision.transform, $"part_{id}");
                Node(cavities.transform, $"cavity_{id}");
            }
            m_App = new GameObject("App");
            m_Parts = m_App.AddComponent<SceneParts>();
            m_Parts.sceneRoot = m_Root;
            Services.Register(m_Parts);
            EditHistory.Register(m_Parts);
            m_Parts.Bind("kitchen", 1, ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4), visual, collision, cavities);
            m_ToolGo = new GameObject("PartTool");
            m_Tool = m_ToolGo.AddComponent<PartTool>();
            m_Tool.frame = m_RootGo.transform;
            Services.Register(m_Tool);
            EditHistory.Register(m_Tool);
        }

        [TearDown]
        public void Teardown()
        {
            m_Tool.ClearAll();
            EditHistory.Unregister(m_Tool);
            EditHistory.Unregister(m_Parts);
            Services.Unregister(m_Tool);
            Services.Unregister(m_Parts);
            Services.Unregister(m_Root);
            foreach (var go in new[] { m_ToolGo, m_App, m_RootGo }) if (go != null) Object.DestroyImmediate(go);
            ModelCycler.Reset();
            Notebook.Clear();
            AirTools.UI.WorldLabels.Reset();
        }

        /// A dishwasher-sized box of a part, in the gap at dw1's insert (place_part's pose without a pose).
        PartInstance PlaceInGap(string id)
        {
            var spec = new PartSpec { id = id, name = "Dishwasher", dims_mm = new PartDims { w = 420f, d = 430f, h = 580f }, mount = new PartMount { face = "-y" } };
            var model = GameObject.CreatePrimitive(PrimitiveType.Cube);
            model.transform.localScale = spec.dims_mm.Metres;
            var part = PartInstance.Create(spec, model, m_RootGo.transform, new MeasureStyle(), "test");
            Assert.IsTrue(AppCommands.TryPlaceTarget(new PlacePartArgs { ComponentId = "dw1" }, m_Root, out var target, out var why), why);
            m_Tool.PlaceAt(part, PlacePartMath.OriginFor(target.Anchor, target.Rotation, PartMath.LocalBox(spec)), target.Rotation);
            return part;
        }

        PartInstance InGap() => Gaps.TryGet(out var gap, "dw1") ? Gaps.ModelIn(gap) : null;

        /// Out of the gap and the placed list, hidden, and still there (Undo can bring it back).
        bool Hidden(PartInstance p) => p != null && !p.gameObject.activeSelf && !m_Tool.PlacedParts.Contains(p);

        [Test]
        public void PutItBack_TheModelOutAndThePartIn_AreOneUndoStep()
        {
            Assert.IsTrue(m_Parts.Remove("dw1"));
            var model = PlaceInGap("dw-a");
            Assert.AreSame(model, InGap());
            var pos = model.transform.position;

            Assert.IsTrue(AppCommands.RestoreComponent("dw1"));
            Assert.IsFalse(m_Parts.IsRemoved("dw1"), "the dishwasher is back");
            Assert.IsTrue(Hidden(model), "the model left the gap, kept for Undo");

            Assert.IsTrue(EditHistory.Undo());
            Assert.IsTrue(m_Parts.IsRemoved("dw1"), "one Undo: the gap is open again…");
            Assert.AreSame(model, InGap(), "… with the model back in it");
            Assert.That(Vector3.Distance(pos, model.transform.position), Is.LessThan(1e-5f));

            Assert.IsTrue(EditHistory.Redo());
            Assert.IsFalse(m_Parts.IsRemoved("dw1"));
            Assert.IsTrue(Hidden(model), "one Redo: both again");

            Assert.IsTrue(EditHistory.Undo());
            Assert.AreSame(model, InGap());
            Assert.IsTrue(EditHistory.Undo(), "then the model's placement");
            Assert.IsNull(InGap());
            Assert.IsTrue(m_Parts.IsRemoved("dw1"), "the dishwasher still out: that's the step before");
        }

        /// place_part into a gap with a model in it (AppCommands.PlacePart: PlaceAt, then ModelCycler.Adopt, in one Step).
        [Test]
        public void PlacePartIntoAnOccupiedGap_SwapsTheModelsAsOneUndoStep()
        {
            Assert.IsTrue(m_Parts.Remove("dw1"));
            var first = PlaceInGap("dw-a");
            PartInstance second;
            using (EditHistory.Step())
            {
                second = PlaceInGap("dw-b");
                ModelCycler.Adopt(second, "dw1");
            }
            Assert.IsTrue(Hidden(first), "the model before left the gap, kept for Undo");
            Assert.AreSame(second, InGap());

            Assert.IsTrue(EditHistory.Undo());
            Assert.AreSame(first, InGap(), "one Undo: the model before is back…");
            Assert.IsTrue(Hidden(second), "… and the new one gone");
            Assert.IsTrue(m_Parts.IsRemoved("dw1"));

            Assert.IsTrue(EditHistory.Redo());
            Assert.AreSame(second, InGap());
            Assert.IsTrue(Hidden(first));
        }
    }
}
