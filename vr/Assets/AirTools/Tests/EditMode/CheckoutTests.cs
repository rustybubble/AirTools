using System;
using System.IO;
using System.Linq;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    public class ArrayPlannerTests
    {
        [Test]
        public void QuantityIsFloorPlusOneWithTapeTolerance()
        {
            Assert.AreEqual(8, ArrayPlanner.Quantity(4.200f, 0.6f), "4.200 m at 600 mm");
            Assert.AreEqual(8, ArrayPlanner.Quantity(4.190f, 0.6f), "a tape 1 cm short still gets the last hanger");
            Assert.AreEqual(7, ArrayPlanner.Quantity(4.100f, 0.6f));
            Assert.AreEqual(1, ArrayPlanner.Quantity(0.3f, 0.6f));
        }

        [Test]
        public void RowIsCentredAndKeepsTheReferenceOffset()
        {
            // Gutter tape along x at the gutter front; the reference hanger sits higher, on the fascia.
            var a = new Vector3(-2.1f, 6.1f, 0.15f); var b = new Vector3(2.1f, 6.1f, 0.15f);
            var reference = new Vector3(0.37f, 6.15f, 0.025f);
            var plan = ArrayPlanner.Plan(a, b, reference, Vector3.forward, 0.6f);
            Assert.AreEqual(8, plan.Count);
            Assert.AreEqual(-2.1f, plan.Positions[0].x, 1e-4f);
            Assert.AreEqual(2.1f, plan.Positions[7].x, 1e-4f);
            for (int k = 1; k < plan.Count; k++) Assert.AreEqual(0.6f, plan.Positions[k].x - plan.Positions[k - 1].x, 1e-4f);
            foreach (var p in plan.Positions) { Assert.AreEqual(6.15f, p.y, 1e-4f); Assert.AreEqual(0.025f, p.z, 1e-4f); }
            var rev = ArrayPlanner.Plan(b, a, reference, Vector3.forward, 0.6f);
            Assert.AreEqual(8, rev.Count, "tape direction doesn't matter");
        }

        [Test]
        public void NearLevelRunIsLevel()
        {
            // Gutter ends snapped to opposite corners of the 12.7 cm lip (bottom left, top right): 1.6° off level.
            var a = new Vector3(-2.1f, 5.986f, 0.152f); var b = new Vector3(2.1f, 6.1f, 0.152f);
            var reference = new Vector3(0.3f, 6.15f, 0.025f);
            var plan = ArrayPlanner.Plan(a, b, reference, Vector3.forward, 0.6f);
            Assert.AreEqual(8, plan.Count);
            foreach (var p in plan.Positions) Assert.AreEqual(6.15f, p.y, 1e-4f, "the row stays at the hanger's height on the fascia");
            Assert.AreEqual(-2.1f, plan.Positions[0].x, 1e-3f);
            Assert.AreEqual(2.1f, plan.Positions[7].x, 1e-3f);
        }

        [Test]
        public void SlopedRunIsFollowed()
        {
            // Stair rail brackets (30°) and a 1:12 ramp rail (4.8°) keep their slope.
            foreach (float deg in new[] { 30f, 4.8f })
            {
                var dir = Quaternion.Euler(0f, 0f, deg) * Vector3.right;
                var plan = ArrayPlanner.Plan(Vector3.zero, dir * 3f, new Vector3(0f, 0.9f, 0f), Vector3.forward, 0.6f);
                Assert.AreEqual(6, plan.Count, $"{deg}°");
                var run = plan.Positions[plan.Count - 1] - plan.Positions[0];
                Assert.AreEqual(deg, Vector3.Angle(run, Vector3.right), 0.01f, $"{deg}° run followed");
            }
        }
    }

    public class SellerSortTests
    {
        static PartSpec Hanger() => PartSpec.Parse(File.ReadAllText($"{PartCatalogBuilder.PartsFolder}/hidden-hanger-5k/part.json"));
        static PartSpec Ac() => PartSpec.Parse(File.ReadAllText($"{PartCatalogBuilder.PartsFolder}/window-ac-small/part.json"));

        [Test]
        public void PriceIncludesShipping()
        {
            // Home Depot 4.27 free · Lowe's 4.48 free · Amazon 3.99 + 5.99 shipping.
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, SellerSort.Order(Hanger(), "cheapest", DayOfWeek.Sunday));
            Assert.AreEqual(9.98f, SellerSort.Delivered(Hanger().sellers[2]), 1e-4f);
        }

        [Test]
        public void EtaByNextWeekday()
        {
            // From Sunday: Mon (Lowe's) = 1, Tue (Home Depot) = 2, Thu (Amazon) = 4.
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, SellerSort.Order(Hanger(), "arrives soonest", DayOfWeek.Sunday));
            Assert.AreEqual(7, SellerSort.EtaDays("Tue", DayOfWeek.Tuesday), "same weekday = next week");
            Assert.AreEqual(1, SellerSort.EtaDays("tomorrow", DayOfWeek.Monday));
            Assert.AreEqual(3, SellerSort.EtaDays("3–5 days", DayOfWeek.Monday));
            Assert.AreEqual(99, SellerSort.EtaDays("?", DayOfWeek.Monday));
        }

        [Test]
        public void OutOfStockGoesLast()
        {
            // AC: Best Buy 189.99, Walmart 179.00, Home Depot 199.00 (out of stock).
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, SellerSort.Order(Ac(), "price", DayOfWeek.Sunday));
        }
    }

    public class HoldTimerTests
    {
        [Test]
        public void ShortHoldNeverConfirms()
        {
            var t = new HoldTimer { Required = 1f };
            for (float s = 0f; s <= 0.6f; s += 0.02f) Assert.IsFalse(t.Update(true, s));
            Assert.IsFalse(t.Update(false, 0.62f));
            Assert.AreEqual(0f, t.Progress);
        }

        [Test]
        public void FullHoldConfirmsExactlyOnce()
        {
            var t = new HoldTimer { Required = 1f };
            int fired = 0;
            for (float s = 0f; s <= 2.0f; s += 0.02f) if (t.Update(true, s)) fired++;
            Assert.AreEqual(1, fired, "keeping it held doesn't pay twice");
            t.Update(false, 2.1f);
            for (float s = 3f; s <= 4.1f; s += 0.02f) if (t.Update(true, s)) fired++;
            Assert.AreEqual(2, fired, "a new, full hold can confirm again (e.g. retry after a failure)");
        }
    }

    public class ArrayAndCheckoutTests : FacadeToolFixture
    {
        GameObject m_Parts;
        PartTool m_Tool;
        PartLoader m_Loader;
        PartsClient m_Client;
        CheckoutPanel m_Checkout;

        static readonly Vector3 Ladder = new Vector3(0.5f, 7.0f, 1.5f);

        [SetUp]
        public void Build()
        {
            AppCommands.ClearBom();
            Tool.Equip(false);
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            m_Parts = new GameObject("m5");
            m_Loader = m_Parts.AddComponent<PartLoader>();
            m_Loader.catalog = catalog;
            m_Tool = m_Parts.AddComponent<PartTool>();
            m_Tool.SetInput(Hub);
            m_Tool.Equip(true);
            m_Client = m_Parts.AddComponent<PartsClient>();
            m_Checkout = m_Parts.AddComponent<CheckoutPanel>();
            m_Checkout.client = m_Client;
            m_Checkout.hold = m_Parts.AddComponent<HoldToConfirm>();
            // OnEnable doesn't run in edit mode: register and subscribe by hand.
            Services.Register(m_Tool); Services.Register(m_Client); Services.Register(m_Checkout);
            m_Checkout.hold.Confirmed += () => typeof(CheckoutPanel).GetMethod("OnHoldConfirmed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(m_Checkout, null);
        }

        [TearDown]
        public void Destroy()
        {
            m_Tool.ClearAll();
            Services.Unregister(m_Tool); Services.Unregister(m_Client); Services.Unregister(m_Checkout);
            UnityEngine.Object.DestroyImmediate(m_Parts);
        }

        PartInstance HangerOnFascia(float x)
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            m_Tool.Hold(p);
            Assert.IsTrue(m_Tool.Release(ToolInputHub.RayPose(Ladder, new Vector3(x, 6.15f, S.FasciaProud))), m_Tool.LastAction);
            return p;
        }

        static void GutterTape() =>
            Notebook.Add(new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) }, DateTime.Now, -1, "gutter"));

        /// What a live tape often gives: the ends snapped to opposite corners of the gutter lip (seen in the demo run).
        static void DiagonalGutterTape() =>
            Notebook.Add(new NotebookEntry("measure", 4.2015, "m", new[] { new Vector3(-2.1f, 5.986f, 0.152f), new Vector3(2.1f, 6.1f, 0.152f) }, DateTime.Now, -1, "gutter (diagonal)"));

        [Test]
        public void ArrayOnADiagonalGutterTapeStaysLevelAndGreen()
        {
            var reference = HangerOnFascia(0.3f);
            float y = reference.transform.position.y;
            DiagonalGutterTape();
            var g = m_Tool.PlaceArray();
            Assert.IsNotNull(g, m_Tool.LastAction);
            var members = PartTool.ArrayMembers(g);
            Assert.AreEqual(8, members.Count);
            for (int k = 0; k < members.Count; k++)
            {
                var p = members[k];
                Assert.AreEqual(FitStatus.Green, p.Fit.Status, $"#{k} {p.Fit}");
                Assert.AreEqual(y, p.transform.position.y, 0.001f, $"#{k} level with the reference");
                if (k > 0) Assert.AreEqual(600f, Vector3.Distance(p.transform.position, members[k - 1].transform.position) * 1000f, 2f, $"spacing #{k}");
            }
        }

        [Test]
        public void UndoRedoPutsTheWholeArrayBack()
        {
            var reference = HangerOnFascia(0.37f);
            var before = reference.transform.position;
            GutterTape();
            var g = m_Tool.PlaceArray();
            var placed = PartTool.ArrayMembers(g).Select(p => p.transform.position).ToList();
            int entry = Notebook.Last.Id;

            m_Tool.Undo();
            Assert.AreEqual(1, m_Tool.PlacedParts.Count);
            Assert.AreEqual(before, reference.transform.position, "reference back where it was");
            Assert.IsNull(Notebook.Find(entry));
            Assert.IsTrue(m_Tool.CanRedo);
            Assert.IsTrue(g.Clones.All(c => c != null && !c.gameObject.activeSelf), "clones hidden, kept for redo");

            m_Tool.Redo();
            Assert.AreEqual(8, m_Tool.PlacedParts.Count);
            Assert.AreSame(g, m_Tool.LastArray);
            var members = PartTool.ArrayMembers(g);
            for (int k = 0; k < members.Count; k++)
            {
                Assert.That(Vector3.Distance(placed[k], members[k].transform.position), Is.LessThan(1e-4f), $"#{k} back in its slot");
                Assert.AreEqual(FitStatus.Green, members[k].Fit.Status, $"#{k} {members[k].Fit}");
            }
            Assert.AreEqual("array", Notebook.Find(entry)?.Tool, "the array entry comes back with its id");
            Assert.AreEqual(8, m_Tool.QuantityOf(PartScenarios.Hanger));
            Assert.IsFalse(m_Tool.CanRedo);

            m_Tool.Undo();
            Assert.AreEqual(1, m_Tool.PlacedParts.Count, "and it undoes again");
        }

        [Test]
        public void ANewPlacementAfterUndoingAnArrayDropsItsClones()
        {
            HangerOnFascia(0.37f);
            GutterTape();
            var clones = m_Tool.PlaceArray().Clones.ToList();
            m_Tool.Undo();
            HangerOnFascia(-1.0f);
            Assert.IsFalse(m_Tool.CanRedo);
            Assert.IsTrue(clones.All(c => c == null), "hidden clones are destroyed once they can't come back");
        }

        [Test]
        public void ArrayAlongTheGutterTapeIsEightGreenAt600()
        {
            var reference = HangerOnFascia(0.37f);
            GutterTape();
            var g = m_Tool.PlaceArray();
            Assert.IsNotNull(g, m_Tool.LastAction);
            var members = PartTool.ArrayMembers(g);
            Assert.AreEqual(8, members.Count);
            Assert.Contains(reference, members, "the placed hanger becomes one of the array");
            for (int k = 0; k < members.Count; k++)
            {
                var p = members[k];
                Assert.AreEqual(FitStatus.Green, p.Fit.Status, $"#{k} {p.Fit}");
                Assert.AreEqual(S.FasciaProud, p.Box.bounds.min.z, 0.002f, $"#{k} on the fascia");
                if (k > 0) Assert.AreEqual(600f, (p.transform.position.x - members[k - 1].transform.position.x) * 1000f, 2f, $"spacing #{k}");
            }
            Assert.AreEqual(8, m_Tool.QuantityOf(PartScenarios.Hanger));
            Assert.AreEqual("array", Notebook.Last.Tool);
            Assert.AreEqual(8, Notebook.Last.ValueSI);
            StringAssert.StartsWith("Array: 8 ×", Notebook.Last.Label);
        }

        [Test]
        public void UndoRemovesTheWholeArrayAndRestoresTheReference()
        {
            var reference = HangerOnFascia(0.37f);
            var before = reference.transform.position;
            GutterTape();
            m_Tool.PlaceArray();
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(1, m_Tool.PlacedParts.Count);
            Assert.AreEqual(before, reference.transform.position);
            Assert.AreEqual(0, Notebook.Entries.Count(e => e.Tool == "array"));
        }

        [Test]
        public void ArrayNeedsATapeAndAPart()
        {
            Assert.IsNull(m_Tool.PlaceArray());
            StringAssert.Contains("place one part first", m_Tool.LastAction);
            HangerOnFascia(0.37f);
            Assert.IsNull(m_Tool.PlaceArray());
            StringAssert.Contains("measure the run first", m_Tool.LastAction);
        }

        [Test]
        public void StartCheckoutOnlyOpensThePanel()
        {
            HangerOnFascia(0.37f);
            GutterTape();
            m_Tool.PlaceArray();
            Assert.IsTrue(AppCommands.StartCheckout(0));
            Assert.AreEqual(CheckoutState.Ready, m_Checkout.State);
            Assert.AreEqual(8, m_Checkout.Quantity, "quantity from the array");
            Assert.AreEqual(8 * 4.27f, m_Checkout.Total, 1e-3f,
                $"price {m_Checkout.Spec.sellers[m_Checkout.SellerIndex].price_usd} × packs {m_Checkout.Packs} + shipping {SellerSort.Shipping(m_Checkout.Spec.sellers[m_Checkout.SellerIndex])} + bom lines {m_Checkout.BomLines.Count} (seller {m_Checkout.SellerIndex})");
            Assert.AreEqual(0, m_Client.CheckoutRequests, "opening never pays");
            Assert.IsFalse(m_Checkout.hold.Simulate(0.6f));
            Assert.AreEqual(0, m_Client.CheckoutRequests, "a 0.6 s hold sends nothing");
            Assert.IsTrue(m_Checkout.hold.Simulate(1.0f));
            Assert.AreEqual(1, m_Client.CheckoutRequests, "a full 1 s hold sends exactly one request");
        }

        [Test]
        public void OnlyTheCheckoutPanelCanSendAPayment()
        {
            // Source guard: PartsClient.Checkout is called from CheckoutPanel (its hold) and nowhere else — voice goes
            // through AppCommands, which can only open the panel.
            var callers = Directory.GetFiles("Assets/AirTools/Runtime", "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains(".Checkout("))
                .Select(Path.GetFileName).ToList();
            CollectionAssert.AreEquivalent(new[] { "CheckoutPanel.cs" }, callers);
            StringAssert.DoesNotContain("Checkout(", File.ReadAllText("Assets/AirTools/Runtime/Voice/AppCommands.cs").Replace("StartCheckout(", ""));
        }
    }
}
