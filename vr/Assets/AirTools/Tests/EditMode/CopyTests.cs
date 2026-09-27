using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Editor;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// UX W0.9 "say it like a tradesperson" (docs/ux/specs/W0.9-copy.md): the display scrub, part nouns, surface
    /// names, error copy, notebook rows and the A17 sweep (no developer text in anything drawn).
    public class CopyTests
    {
        /// D2: these pin metric (today's strings); UnitsCopyTests covers imperial.
        [SetUp]
        public void MetricCopy() => UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);

        static PartSpec Fixture(string id) =>
            PartSpec.Parse(System.IO.File.ReadAllText($"{PartCatalogBuilder.PartsFolder}/{id}/part.json"));

        [TestCase("Home Depot (mock)", "Home Depot")]
        [TestCase("mock fixture (SPEC §5.3)", "")]
        [TestCase("https://example.com/specs/hh5k", "")]
        [TestCase("Hits Wall/Above, Gutter/Back", "Hits the wall, the gutter")]
        [TestCase("Offline receipt — no payment was authorized (sandbox not configured)", "Offline receipt · no payment was authorized")]
        [TestCase("Doesn't fit · Hits the kitchen scan", "Doesn't fit · Hits the kitchen")]
        [TestCase("Saved · Distance 0.26 m · 10¼″", "Saved · Distance 0.26 m · 10¼″")]
        [TestCase("<color=#FF382E>by 500 mm</color>", "<color=#FF382E>by 500 mm</color>")]
        [TestCase(null, "")]
        public void Clean(string input, string expected) => Assert.AreEqual(expected, Copy.Clean(input));

        [Test]
        public void Helpers()
        {
            Assert.AreEqual("1A2B3C", Copy.ApprovalCode("MOCK1A2B3C"));
            Assert.AreEqual("Home Depot listing", Copy.SourceLabel("https://www.homedepot.com/p/Everbilt-Hinge/302857903"));
            Assert.AreEqual("Google Shopping", Copy.SourceLabel("https://www.google.com/search?ibp=oshop&q=cabinet%20hinge"));
            Assert.AreEqual("", Copy.SourceLabel("https://example.com/specs/hh5k"));
            Assert.AreEqual("", Copy.SourceLabel("mock://fixtures"));
            Assert.AreEqual("in stock", Copy.Reason("best match, in stock, 4406.0★ (1904 reviews)"));
            Assert.AreEqual("cheapest in stock, $7.59 with free delivery", Copy.Reason("cheapest in stock, $7.59 with free delivery"));
            Assert.AreEqual("in stock, rated 4.6", Copy.Reason("in stock, 4.6★ (120 reviews)"));
            Assert.AreEqual("exact-size box", Copy.TierBadge("proxy"));
            Assert.AreEqual("AI model · exact size", Copy.TierBadge("ai_mesh"));
            string clipped = Copy.Clip("Three gutter hangers fit your run and the first one is the cheapest by far today", 40);
            Assert.LessOrEqual(clipped.Length, 40);
            StringAssert.EndsWith("…", clipped);
            Assert.AreEqual("✓", Copy.Glyph(FitStatus.Green));
            Assert.AreEqual("!", Copy.Glyph(FitStatus.Amber));
            Assert.AreEqual("✗", Copy.Glyph(FitStatus.Red), "✗: Inter has no ✕ (U+2715)");
            Assert.AreEqual("2 measurements · 1 level · 1 part", Copy.NotebookSummary(new List<NotebookEntry>
            {
                new NotebookEntry("measure", 1, "m", null, DateTime.Now, -1, "a"), new NotebookEntry("measure", 1, "m", null, DateTime.Now, -1, "b"),
                new NotebookEntry("level", 0, "°", null, DateTime.Now, -1, "Level 0.0°"), new NotebookEntry("part", 0, "", null, DateTime.Now, -1, "p"),
            }));
            Assert.AreEqual("test facade", Copy.SiteName(null));
            Assert.AreEqual("kitchen", Copy.SiteName("kitchen"));
        }

        [Test]
        public void NounsKindsAndMountTargets()
        {
            var hanger = Fixture("hidden-hanger-5k");
            var ac = Fixture("window-ac-small");
            Assert.AreEqual("hanger", Copy.Noun(hanger));
            Assert.AreEqual("AC unit", Copy.Noun(ac));
            Assert.AreEqual("hinge", Copy.Noun(new PartSpec { name = "RightSet Hardware" }, "cabinet hinge"), "a store name as the part name: the chip names it");
            Assert.AreEqual("slide", Copy.Noun(new PartSpec { name = "Full extension drawer slide with bracket" }, "drawer slide"));
            Assert.AreEqual("part", Copy.Noun(new PartSpec { name = "Widget" }));
            Assert.AreEqual("shelves", Copy.Plural("shelf"));
            Assert.AreEqual("hinges", Copy.Plural("hinge"));
            Assert.AreEqual(PartKind.Fastener, Copy.KindOf(hanger));
            Assert.AreEqual(PartKind.WindowUnit, Copy.KindOf(ac));
            Assert.AreEqual(PartKind.Spanning, Copy.KindOf(new PartSpec { name = "Drawer slide 18 in" }));
            Assert.AreEqual(PartKind.Fastener, Copy.KindOf(new PartSpec { name = "Mystery gizmo" }), "unknown hardware is never 'spare'");
            Assert.AreEqual("the door", Copy.MountTarget(new PartSpec { mount = new PartMount { surface = "Cabinet Door" } }));
            Assert.AreEqual("the cabinet side", Copy.MountTarget(new PartSpec { mount = new PartMount { surface = "side" } }));
            Assert.AreEqual("a surface", Copy.MountTarget(new PartSpec { mount = new PartMount() }));
        }

        [TestCase("Cannot connect to destination host", -1L, ErrorKind.Offline)]
        [TestCase("POST http://127.0.0.1:8000/checkout: Cannot connect to destination host", -1L, ErrorKind.Offline)]
        [TestCase("search timed out", -1L, ErrorKind.Timeout)]
        [TestCase("HTTP/1.1 429 Too Many Requests", -1L, ErrorKind.Busy)]
        [TestCase("anything", 503L, ErrorKind.Unavailable)]
        [TestCase("Voice is offline on the laptop (no speech-to-text) — use the buttons", -1L, ErrorKind.Unavailable)]
        [TestCase("declined (DECLINED)", -1L, ErrorKind.Declined)]
        [TestCase("HTTP/1.1 502 Bad Gateway", -1L, ErrorKind.Server)]
        [TestCase("bad part.json: unexpected token", -1L, ErrorKind.Other)]
        [TestCase("", 0L, ErrorKind.Offline)]
        public void ClassifiesErrors(string raw, long http, ErrorKind kind) => Assert.AreEqual(kind, Copy.Classify(raw, http));

        [Test]
        public void ErrorCopyIsHuman()
        {
            Assert.AreEqual("Couldn't reach the laptop · nothing was charged", Copy.Error(ErrorSurface.Checkout, "POST http://127.0.0.1:8000/checkout: Cannot connect to destination host"));
            Assert.AreEqual("Search took too long · try a part below", Copy.Error(ErrorSurface.Search, "search timed out"));
            Assert.AreEqual("Voice is offline · use the buttons", Copy.Error(ErrorSurface.Voice, "Voice is offline on the laptop (no speech-to-text) — use the buttons"));
            Assert.AreEqual("Can't answer now · the assistant is offline", Copy.Error(ErrorSurface.Ask, "Scene questions need the live model (the laptop is offline)", 503));
            Assert.AreEqual("✓ Sent to the laptop", Copy.ExportStatus("uploaded"));
            Assert.AreEqual("Saved on the headset · laptop not connected", Copy.ExportStatus("saved (server offline)"));
            Assert.AreEqual("Checking specs…", Copy.Stage("checking the spec sheets"));
            Assert.AreEqual("Allow the mic, then tap Talk again", Copy.VoiceStatus("Allow the microphone, then tap Talk again"));
            Assert.AreEqual("Listening… tap to send", Copy.VoiceStatus("Listening… tap to send"));
            Assert.AreEqual("Didn't catch that · tap Talk and speak", Copy.VoiceStatus("Didn't catch that"));
            Assert.AreEqual("“find a hinge” · Three hinges.", Copy.VoiceStatus("“find a hinge” → Three hinges."));
        }

        [TestCase("Wall/Above", "the wall")]
        [TestCase("Gutter/Back", "the gutter")]
        [TestCase("Fascia", "the fascia")]
        [TestCase("Sill", "the sill")]
        [TestCase("Window/Glass", "the window")]
        [TestCase("Ledge/Slab", "the ledge")]
        [TestCase("Ground", "the ground")]
        [TestCase("Obstacle", "the building")]
        public void FacadePaths(string path, string name) => Assert.AreEqual(name, SurfaceNames.FromPath(path));

        [Test]
        public void ScanSurfaces()
        {
            SurfaceSample S(float up, float h, string obj = null, bool onObj = false, string site = "kitchen") =>
                new SurfaceSample { IsScan = true, UpDot = up, HeightAboveFloorM = h, ObjectLabel = obj, OnObjectPlane = onObj, Site = site };
            Assert.AreEqual("the door", SurfaceNames.Classify(S(0f, 0.5f, "cabinet_door")));
            Assert.AreEqual("the drawer", SurfaceNames.Classify(S(0f, 0.5f, "drawer")));
            Assert.AreEqual("the cabinet", SurfaceNames.Classify(S(0f, 0.5f, "panel")));
            Assert.AreEqual("the toe-kick", SurfaceNames.Classify(S(0.02f, 0.07f)));
            Assert.AreEqual("the base of the wall", SurfaceNames.Classify(S(0f, 0.07f, site: "house")));
            Assert.AreEqual("the counter", SurfaceNames.Classify(S(0.99f, 0.62f)));
            Assert.AreEqual("the top of the cabinet", SurfaceNames.Classify(S(0.99f, 2.1f)));
            Assert.AreEqual("the floor", SurfaceNames.Classify(S(1f, 0.01f)));
            Assert.AreEqual("the underside", SurfaceNames.Classify(S(-1f, 0.8f)));
            Assert.AreEqual("the cabinet", SurfaceNames.Classify(S(0f, 0.6f, onObj: true)));
            Assert.AreEqual("the wall", SurfaceNames.Classify(S(0f, 1.6f)));
            Assert.AreEqual("the surface", SurfaceNames.Classify(S(0.6f, 1f)));
            Assert.AreEqual("the kitchen", SurfaceNames.Classify(new SurfaceSample { IsScan = true, UpDot = float.NaN, HeightAboveFloorM = float.NaN, Site = "kitchen" }));
            Assert.AreEqual("the wall", SurfaceNames.Classify(new SurfaceSample { IsScan = false, Path = "Wall/Left" }));
            var quad = new[] { new Vector3(0, 0, 0), new Vector3(0.4f, 0, 0), new Vector3(0.4f, 0.7f, 0), new Vector3(0, 0.7f, 0) };
            Assert.IsTrue(SurfaceNames.InQuad(quad, new Vector3(0.2f, 0.3f, 0.01f), 0.02f, 0.01f));
            Assert.IsTrue(SurfaceNames.InQuad(quad, new Vector3(-0.005f, 0.3f, 0f), 0.02f, 0.01f), "expanded by 1 cm");
            Assert.IsFalse(SurfaceNames.InQuad(quad, new Vector3(0.2f, 0.3f, 0.05f), 0.02f, 0.01f), "off the plane");
            Assert.IsFalse(SurfaceNames.InQuad(quad, new Vector3(0.6f, 0.3f, 0f), 0.02f, 0.01f), "outside");
        }

        [Test]
        public void NotebookRowsAndSaveToasts()
        {
            var width = new NotebookEntry("measure", 0.26, "m", new[] { Vector3.zero, new Vector3(0.26f, 0f, 0f) }, DateTime.Now, -1, "Distance 0.26 m · 10¼″");
            Assert.AreEqual("✓ Saved · Width 0.26 m", NotebookRow.SavedToast(width));
            Assert.AreEqual("✓ Saved · Door width 0.26 m", NotebookRow.SavedToast(width, "Door"));
            var diag = new NotebookEntry("measure", 1, "m", new[] { Vector3.zero, new Vector3(1f, 1f, 0f) }, DateTime.Now, -1, "Distance");
            Assert.AreEqual("Distance", NotebookRow.Title(diag));
            var rect = new NotebookEntry("measure", 0.09, "m²", new Vector3[4], DateTime.Now, -1, "Quad …")
            { Sides = new[] { 0.3, 0.3, 0.3, 0.3 }, Angles = new[] { 90.0, 90.0, 90.0, 90.0 } };
            Assert.AreEqual("Rectangle", NotebookRow.Title(rect));
            StringAssert.StartsWith("✓ Saved · Rectangle 0.09 m²", NotebookRow.SavedToast(rect));
            var tri = new NotebookEntry("measure", 0.5, "m²", new Vector3[3], DateTime.Now, -1, "Triangle …") { Sides = new[] { 1.0, 1.0, 1.4 }, Angles = new[] { 45.0, 45.0, 90.0 } };
            Assert.AreEqual("Triangle", NotebookRow.Title(tri));
            Assert.AreEqual("✓ Saved · Level", NotebookRow.SavedToast(new NotebookEntry("level", 0.1, "°", null, DateTime.Now, -1, "Level 0.1° (0.2% slope)")));
            Assert.AreEqual("✓ Saved · Slope 2.0° · 3.5% slope", NotebookRow.SavedToast(new NotebookEntry("level", 2.0, "°", null, DateTime.Now, -1, "Level 2.0° (3.5% slope)")));
            Assert.AreEqual("✓ Saved · Out of plumb 1.3°", NotebookRow.SavedToast(new NotebookEntry("level", 1.3, "°", null, DateTime.Now, -1, "Plumb 1.3° off vertical")));
            Assert.AreEqual("✓ Note saved", NotebookRow.SavedToast(new NotebookEntry("note", 0, "", null, DateTime.Now, -1, "check the flashing")));
            var order = new NotebookEntry("purchase", 7.59, "USD", null, new DateTime(2026, 9, 26, 10, 9, 0), -1, "Bought 1 × Hinge from Hinge Outlet — $7.59 …")
            { DisplayTitle = "Order · Hinge Outlet", DisplayValue = "$7.59", DisplayDetail = "1 × hinge · no charge" };
            Assert.AreEqual("Order · Hinge Outlet · $7.59", NotebookRow.RowTitle(order));
            Assert.AreEqual("10:09 AM · 1 × hinge · no charge", NotebookRow.RowDetail(order));
        }

        [Test]
        public void SellerRowUsesTheDeliveredPackTotal()
        {
            // S8: a 10-pack at $4.27 with $5 shipping, 8 needed → one pack: $9.27, not $4.27 × 8.
            var s = new PartSeller { name = "Home Depot (mock)", price_usd = 4.27f, pack_qty = 10, shipping_usd = 5f, eta = null, rating = 4406f, in_stock = true };
            Assert.AreEqual(9.27f, SellerRowView.Delivered(s, 8), 1e-4f);
            var text = SellerRowView.Compose(s, 8);
            Assert.AreEqual("Home Depot", text.name);
            StringAssert.Contains("9</mspace>.<mspace=0.58em>27", text.price);
            StringAssert.Contains("pack of", text.price);
            Assert.AreEqual("$5.00 shipping", text.detail, "no empty 'arrives', no impossible rating, no 'In stock'");
            var single = new PartSeller { name = "Hinge Outlet", price_usd = 7.59f, shipping_usd = 0f, eta = "Tue", rating = 4.6f };
            Assert.AreEqual("Free shipping · arrives Tue · rated 4.6", SellerRowView.Compose(single, 1).detail);
            // Same sum as the checkout (without "what else" lines).
            var spec = new PartSpec { id = "x", name = "Hinge", sellers = new List<PartSeller> { s } };
            float checkout = s.price_usd * s.PacksFor(8) + SellerSort.Shipping(s);
            Assert.AreEqual(checkout, SellerRowView.Delivered(s, 8), 1e-4f);
            Assert.IsNotNull(spec);
        }

        [Test]
        public void CheckoutAndReceiptCopy()
        {
            var s = new PartSeller { name = "Hinge Outlet", price_usd = 7.59f, shipping_usd = 0f };
            var spec = new PartSpec { id = "hinge", name = "Pivot Door Hinges - Rear Pivoting", sellers = new List<PartSeller> { s } };
            var text = CheckoutPanel.Compose(spec, 0, 1, 1, 7.59f, query: "cabinet hinge");
            StringAssert.Contains("Shipping Free", text.lines);
            StringAssert.Contains("Total", text.lines);
            Assert.AreEqual("Visa •••• 1111 · test card, no real charge", text.card);
            var offline = new CheckoutReceipt { status = "OFFLINE", mode = "offline", total_usd = 7.59f, qty = 1, seller = "Hinge Outlet (mock)", receipt_id = "R-1",
                label = "Offline receipt — no payment was authorized (sandbox not configured)" };
            string receipt = CheckoutPanel.ReceiptText(offline, spec, "cabinet hinge");
            StringAssert.StartsWith("Receipt saved · $7.59\n1 × hinge · Hinge Outlet", receipt);
            StringAssert.Contains("Offline receipt — no payment was authorized (sandbox not configured)", receipt, "the honesty label, verbatim (api.md §7)");
            var sandbox = new CheckoutReceipt { status = "AUTHORIZED", total_usd = 7.59f, approval_code = "MOCK1A2B3C", card_last4 = "1111", qty = 1, seller = "Hinge Outlet" };
            StringAssert.Contains("Approval 1A2B3C · Visa •••• 1111", CheckoutPanel.ReceiptText(sandbox, spec));
        }

        static readonly Regex s_DevText = new Regex(@"(?i)\(mock\)|SPEC\s*§|https?://|mock://|example\.com|exception|/geometry_|\b(Wall|Gutter|Window|Ledge)/|\b[a-z0-9]+(-[a-z0-9]+){2,}\b");

        /// A17: nothing drawn carries developer text — fixtures, server-shaped data, raw errors, collider paths.
        [Test]
        public void NoDeveloperText()
        {
            var shown = new List<string>();
            foreach (var id in new[] { "hidden-hanger-5k", "window-ac-small" })
            {
                var spec = Fixture(id);
                shown.Add(Copy.Clean(spec.name));
                foreach (var seller in spec.sellers)
                {
                    var t = SellerRowView.Compose(seller, 3);
                    shown.Add(t.name); shown.Add(t.detail);
                }
                shown.Add(Copy.Clean(spec.manufacturer));
                foreach (var c in spec.citations) shown.Add(Copy.SourceLabel(c));
                shown.Add(Copy.SourceLabel(spec.spec_url));
                shown.Add(Copy.Reason(spec.recommendation_reason));
                shown.Add(CheckoutPanel.Compose(spec, 0, 2, spec.sellers[0].PacksFor(2), 10f).lines);
            }
            var server = new PartSpec { id = "cabinet-hinge-3", name = "Pivot Door Hinges - Rear Pivoting - High Quality Steel", recommendation_reason = "best match, in stock, 4406.0★ (1904 reviews)",
                citations = new List<string> { "https://www.google.com/search?ibp=oshop&q=cabinet%20hinge", "https://www.homedepot.com/p/x/302857903" },
                sellers = new List<PartSeller> { new PartSeller { name = "Hinge Outlet", price_usd = 7.59f, eta = null } } };
            shown.Add(Copy.Reason(server.recommendation_reason));
            foreach (var c in server.citations) shown.Add(Copy.SourceLabel(c));
            shown.Add(SellerRowView.Compose(server.sellers[0], 1).detail);
            foreach (var raw in new[]
                     {
                         "search failed: worker crashed", "search timed out", "bad part.json: Unexpected character", "POST http://127.0.0.1:8000/checkout: Cannot connect to destination host",
                         "sellers: Exception of type 'System.Exception' was thrown", "the laptop server is offline", "no response", "declined (DECLINED)", "not running",
                         "/scene/ask: HTTP/1.1 500 Internal Server Error {detail}", "/agent/command: Cannot resolve destination host",
                     })
                foreach (ErrorSurface where in Enum.GetValues(typeof(ErrorSurface))) shown.Add(Copy.Error(where, raw));
            shown.Add(Copy.Clean("Hits Wall/Above, Gutter/Back, Collision r1/geometry_0"));
            shown.Add(Copy.Clean("Offline receipt — no payment was authorized (sandbox not configured)"));
            shown.Add(Copy.Clean("mock fixture (SPEC §5.3)\nhttps://example.com/specs/hh5k"));
            foreach (var s in shown)
                Assert.IsFalse(s_DevText.IsMatch(s ?? ""), $"developer text on screen: \"{s}\"");
        }
    }
}
