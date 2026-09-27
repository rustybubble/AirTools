#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// The judge's walkthrough (SPEC §2 demo path) as one scripted run on the built-in synthetic facade, through the
    /// real input paths: the Enter world button, ToolInputHub injection (the path controller trigger / hand pinch
    /// take), the tool ring's own API (tap a side item, flick, spin, pinch the lens, Undo / Redo), AppCommands, the
    /// seller row's Pay button and the checkout's physical 1 s hold. One `[AirTools.Check] Demo.<beat>` line per beat.
    /// Start it at the beginning of a Play session with AgentHarness.RunDemo(); poll AgentHarness.DemoResult().
    /// Parts, sellers and checkout go to `server` (the mock on :8001 by default); the default server is restored after.
    public static class DemoWalkthrough
    {
        public class Beat { public string Id; public bool Pass; public string Detail; }

        public static readonly List<Beat> Beats = new List<Beat>();
        public static bool Running { get; private set; }
        public static string Current { get; private set; } = "";
        public static float StartedAt { get; private set; }
        public static float Seconds { get; private set; }
        public const string MockServer = "http://localhost:8001";

        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static float Now => Time.realtimeSinceStartup;
        static string s_Server, s_PrevServer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Beats.Clear(); Running = false; Current = ""; }

        public static string Start(string server = MockServer)
        {
            if (!Application.isPlaying) return "Play mode only";
            if (Running) return "already running — poll DemoResult()";
            Beats.Clear();
            s_Server = server;
            s_PrevServer = ServerConfig.OverrideUrl;   // put back on done (it used to clear a saved override)
            Running = true;
            Current = "starting";
            StartedAt = Now;
            if (!DemoRunner.Run(Walk(), OnError, OnDone)) { Running = false; return "could not start (another routine is running)"; }
            return "demo started — poll AgentHarness.DemoResult()";
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            int pass = Beats.Count(b => b.Pass && b.Id != "summary"), total = Beats.Count(b => b.Id != "summary");
            sb.Append(Running ? $"running ({Current}, {Now - StartedAt:0.0} s) " : $"done in {Seconds:0.0} s ");
            sb.Append($"{pass}/{total} beats passed");
            foreach (var b in Beats) sb.Append($"\n{(b.Pass ? "PASS" : "FAIL")} Demo.{b.Id}: {b.Detail}");
            return sb.ToString();
        }

        static void Add(string id, bool pass, string detail)
        {
            Beats.Add(new Beat { Id = id, Pass = pass, Detail = detail });
            Log.Check($"Demo.{id}", pass, detail);
        }

        static void OnError(Exception ex)
        {
            Add($"{(string.IsNullOrEmpty(Current) ? "run" : Current)}.exception", false, ex.GetType().Name + ": " + ex.Message);
            Debug.LogException(ex);
        }

        static void OnDone()
        {
            // Always hand everything back: the gesture-driven palm menu, the pointer, the hold, the default server.
            if (Services.TryGet<PalmMenu>(out var palm)) palm.Force(null);
            if (Services.TryGet<ToolInputHub>(out var hub)) hub.ClearOverrides();
            if (Services.TryGet<CheckoutPanel>(out var co) && co.hold != null) co.hold.PressedOverride = null;
            if (!string.IsNullOrEmpty(s_Server)) ServerConfig.SetOverride(s_PrevServer);
            int pass = Beats.Count(b => b.Pass), total = Beats.Count;
            Seconds = Now - StartedAt;
            Add("summary", total > 0 && pass == total, $"passed={pass} total={total} seconds={Seconds.ToString("0.0", C)}");
            Running = false;
            Current = "";
        }

        // ---------------- helpers ----------------

        /// Wait until cond() or timeout seconds (real time).
        static CustomYieldInstruction Until(Func<bool> cond, float timeout)
        {
            float t0 = Now;
            return new WaitUntil(() => cond() || Now - t0 > timeout);
        }

        static WaitForSecondsRealtime Secs(float s) => new WaitForSecondsRealtime(s);

        static int Index(ToolRing ring, string label)
        {
            for (int i = 0; i < ring.items.Length; i++) if (ring.items[i].label == label) return i;
            return -1;
        }

        static string Label(ToolRing ring, int i) => i >= 0 && i < ring.items.Length ? ring.items[i].label : "-";

        /// Palm menu open (forced: hands can't be simulated) and taking presses.
        static IEnumerator OpenMenu(PalmMenu palm)
        {
            palm.Force(true);
            yield return Until(() => palm.AcceptingPresses && palm.ring != null && palm.ring.isActiveAndEnabled, 2f);
        }

        /// Menu hand down; wait out the moment after closing in which tools ignore presses (PalmMenu.BlocksTools).
        static IEnumerator CloseMenu(PalmMenu palm)
        {
            palm.Force(false);
            yield return Until(() => !palm.IsOpen, 1f);
            yield return Secs(0.45f);
        }

        /// Wheel at rest and the settle delay passed (modes equip then).
        static IEnumerator Settle(ToolRing ring)
        {
            yield return Until(() => ring.Dial.Settled, 3f);
            yield return Secs(ring.settleDelay + 0.1f);
        }

        /// The receipt's "Take it home" (declutter S6, lane A) when this build has it and it's on screen: found by its
        /// label, so the walkthrough runs with or without it (without it, the demo leaves through Settings ▸ Exit world).
        static AirTools.UI.GlassButton TakeItHomeButton(CheckoutPanel checkout)
        {
            if (checkout == null || checkout.receipt == null || !checkout.receipt.activeInHierarchy) return null;
            foreach (var b in checkout.receipt.GetComponentsInChildren<AirTools.UI.GlassButton>())
                if (b.isActiveAndEnabled && b.interactable && string.Equals(b.Text, "Take it home", StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        /// A quick pinch at item i's place on the ring (the lens when i is selected).
        static void TapItem(ToolRing ring, int i) => ring.Tap(ring.ItemPos(ring.Dial.ItemAngle(i)));

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static string F(Vector3 v) => $"({v.x.ToString("0.000", C)}, {v.y.ToString("0.000", C)}, {v.z.ToString("0.000", C)})";
        static string F(float v, string fmt = "0.000") => v.ToString(fmt, C);
        static string F(double v, string fmt = "0.0000") => v.ToString(fmt, C);

        static bool SizeOk(PartInstance p, out string text)
        {
            var s = p.MeasuredSizeMm(); var d = p.Spec.dims_mm;
            text = $"{F(s.x, "0.0")}x{F(s.y, "0.0")}x{F(s.z, "0.0")} mm (dims w{F(d.w, "0")} h{F(d.h, "0")} d{F(d.d, "0")})";
            return Mathf.Abs(s.x - d.w) <= 1f && Mathf.Abs(s.y - d.h) <= 1f && Mathf.Abs(s.z - d.d) <= 1f;
        }

        // ---------------- the walkthrough ----------------

        static IEnumerator Walk()
        {
            Current = "setup";
            var root = Services.Get<SceneRoot>();
            var mode = Services.Get<ModeController>();
            var tools = Services.Get<ToolManager>();
            var hub = Services.Get<ToolInputHub>();
            var measure = Services.Get<MeasureTool>();
            var level = Services.Get<LevelTool>();
            var palm = Services.Get<PalmMenu>();
            var streamer = Services.Get<SceneStreamer>();
            var partTool = Services.Get<PartTool>();
            var browser = Services.Get<PartsBrowser>();
            var client = Services.Get<PartsClient>();
            var sellers = Services.Get<SellerPanel>();
            var checkout = Services.Get<CheckoutPanel>();
            var home = Services.Get<TakeItHome>();
            var table = Services.Get<TabletopController>();
            var nbCtl = Services.Get<NotebookController>();
            var nbPanel = Services.Get<NotebookPanel>();
            var missing = new List<string>();
            void Need(object o, string name) { if (o == null || (o is UnityEngine.Object u && u == null)) missing.Add(name); }
            Need(root, "SceneRoot"); Need(mode, "ModeController"); Need(tools, "ToolManager"); Need(hub, "ToolInputHub");
            Need(measure, "MeasureTool"); Need(level, "LevelTool"); Need(palm, "PalmMenu"); Need(streamer, "SceneStreamer");
            Need(partTool, "PartTool"); Need(browser, "PartsBrowser"); Need(client, "PartsClient"); Need(sellers, "SellerPanel");
            Need(checkout, "CheckoutPanel"); Need(home, "TakeItHome"); Need(table, "TabletopController"); Need(nbCtl, "NotebookController");
            Need(nbPanel, "NotebookPanel");
            if (missing.Count > 0) { Add("setup", false, "missing " + string.Join(", ", missing)); yield break; }
            var rt = root.transform;
            float session = Time.realtimeSinceStartup;

            // 1. The judge starts in passthrough.
            Current = "start.passthrough";
            bool fresh = AppState.Mode == AppMode.Passthrough;
            if (!fresh)
            {
                AppCommands.CloseChest();
                yield return Until(() => mode.VisualMode == AppMode.Passthrough && !mode.IsTransitioning, 3f);
            }
            Add("start.passthrough", fresh && mode.VisualMode == AppMode.Passthrough && !mode.IsTransitioning && !root.IsVisible,
                $"mode={AppState.Mode} visual={mode.VisualMode} scene_visible={root.IsVisible} session_age={F(session, "0.0")}s{(fresh ? "" : " (re-run: the world was open, closed it first)")}");

            // 2. The built-in synthetic facade (the kitchen auto-loads from the server at start; wait for it, then swap).
            Current = "scene.builtin";
            float tLoad = Now;
            yield return Until(() => !streamer.Loading, 60f);
            string was = root.Site ?? "built-in";
            streamer.LoadBuiltIn();
            yield return null;
            Physics.SyncTransforms();
            var rig = streamer.rig;
            float yaw = rig != null ? rig.eulerAngles.y : -1f;
            var boot = UnityEngine.Object.FindAnyObjectByType<AppBootstrap>();
            bool builtin = streamer.Site == null && !root.IsRuntimePackage && root.Content != null && root.Package != null && boot != null && root.Package == boot.scenePackage
                           && rig != null && Vector3.Distance(rig.position, S.SpawnPosition) < 1e-3f && Mathf.Abs(Mathf.DeltaAngle(yaw, S.SpawnYawDeg)) < 0.1f
                           && Mathf.Abs(rt.lossyScale.x - 1f) < 1e-5f && Mathf.Abs(SnapService.Scale - 1f) < 1e-5f && !root.IsVisible;
            Add("scene.builtin", builtin, $"was={was} (waited {F(Now - tLoad, "0.0")} s) package={root.Package?.name} site={root.Site} runtime={root.IsRuntimePackage} rig={F(rig != null ? rig.position : Vector3.zero)} yaw={F(yaw, "0.0")} scale={F(rt.lossyScale.x, "0.###")} snapScale={F(SnapService.Scale, "0.###")} scene_visible={root.IsVisible}");
            if (!string.IsNullOrEmpty(s_Server)) ServerConfig.SetOverride(s_Server);

            // 3. Enter world (the button in front of you) → World within 1.5 s.
            Current = "world.enter";
            var chest = UnityEngine.Object.FindAnyObjectByType<ChestController>();
            float tPress = Now;
            bool pressed = chest != null && chest.button != null && chest.button.isActiveAndEnabled && chest.button.Press();
            if (!pressed) AppCommands.OpenChest();
            yield return Until(() => AppState.Mode == AppMode.World && mode.VisualMode == AppMode.World && !mode.IsTransitioning, 3f);
            float enter = Now - tPress;
            Add("world.enter", AppState.Mode == AppMode.World && mode.VisualMode == AppMode.World && root.IsVisible && enter <= 1.5f,
                $"via={(pressed ? "Enter world button" : "AppCommands.OpenChest")} seconds={F(enter, "0.00")} budget=1.5 fade={F(mode.LastTransitionSeconds, "0.00")} scene_visible={root.IsVisible}");

            // 4. The world opens with Move in hand (D1, since 09-27; before, the tape).
            Current = "tool.default";
            Add("tool.default", tools.Active == ToolKind.Move && tools.Active == ToolManager.Default,
                $"tool={tools.Active} default={ToolManager.Default}");

            // 5. The ring opens on Move (D1); a pinch on Measure only previews it (D6: settling never equips), and a pinch on
            //    the lens then commits it: the tape in hand (Line mode) for the jambs.
            Current = "ring.measure";
            yield return OpenMenu(palm);
            var ring = palm.ring;
            // Ring v2 (declutter DC3): Move · Measure · Level · Notebook · Model view · Settings; catalog: + Catalog after Level.
            int iMove = Index(ring, "Move"), iMeasure = Index(ring, "Measure"), iLevel = Index(ring, "Level"), iNotebook = Index(ring, "Notebook"),
                iSettings = Index(ring, "Settings"), iTabletop = Index(ring, "Model view");
            int iCatalog = Index(ring, "Catalog");   // catalog
            string lens0 = Label(ring, ring.Selected);
            int ticks0 = ring.Ticks;
            TapItem(ring, iMeasure);
            string tap = ring.LastAction;
            yield return Settle(ring);
            var previewTool = tools.Active;
            string previewLens = Label(ring, ring.Selected);
            bool menuOpen = palm.IsOpen;
            TapItem(ring, ring.Selected);   // D6: pinch the lens to commit
            yield return Until(() => tools.Active == ToolKind.Measure, 1f);
            Add("ring.measure", menuOpen && lens0 == "Move" && previewLens == "Measure" && previewTool == ToolKind.Move
                                && tools.Active == ToolKind.Measure && !measure.AreaMode && ring.Selected == iMeasure,
                $"menu={menuOpen}({palm.OpenedBy}) lens_before={lens0} tap=\"{tap}\" preview={previewLens} tool_while_previewing={previewTool} lens={Label(ring, ring.Selected)} ticks={ring.Ticks - ticks0} tool={tools.Active} area={measure.AreaMode}");
            yield return CloseMenu(palm);

            // 6. Tape the window jambs (±3 cm aim), then finish with the other hand → 1.500 ± 0.010.
            Current = "measure.window";
            var scen = MeasureScenarios.M2();
            var win = scen.First(s => s.Id == "M2.measure.window.width");
            int shapes0 = measure.Shapes.Count, nb0 = Notebook.Entries.Count;
            var rw = MeasureScenarios.Run(win, measure, hub, rt, 1);
            var windowShape = measure.Shapes.Count > shapes0 ? measure.Shapes[measure.Shapes.Count - 1] : null;
            Add("measure.window", rw.Passed, rw.Detail);
            var last = Notebook.Last;
            Add("measure.finish", windowShape != null && measure.Session.Count == 0 && Notebook.Entries.Count == nb0 + 1 && last != null && last.Tool == "measure",
                $"shapes +{measure.Shapes.Count - shapes0} session={measure.Session.Count}pts notebook +{Notebook.Entries.Count - nb0} last=\"#{last?.Id} {last?.Label}\"");
            yield return Frames(3);

            // The gutter run (for the array later): both ends from the ground → 4.200 ± 0.015.
            Current = "measure.gutter";
            var rg = MeasureScenarios.Run(scen.First(s => s.Id == "M2.measure.gutter.run"), measure, hub, rt, 1);
            Add("measure.gutter", rg.Passed, rg.Detail);
            yield return Frames(3);

            // 7. The ring equips Level: a flick from Measure coasts one item on and settles there (a preview, D6);
            //    a pinch on the lens commits it.
            Current = "ring.level";
            yield return OpenMenu(palm);
            ticks0 = ring.Ticks;
            ring.Flick(-4f);
            yield return Settle(ring);
            var settledTool = tools.Active;
            TapItem(ring, ring.Selected);   // pinch the lens: commit
            yield return Until(() => tools.Active == ToolKind.Level, 1f);
            Add("ring.level", settledTool == ToolKind.Measure && tools.Active == ToolKind.Level && ring.Selected == iLevel,
                $"flick=-4 rad/s lens={Label(ring, ring.Selected)} ticks={ring.Ticks - ticks0} tool_after_settle={settledTool} tool={tools.Active}");
            yield return CloseMenu(palm);

            // 8. Level on the pitched ledge → 2.0° ± 0.2.
            Current = "level.ledge";
            var rl = LevelScenarios.Run(LevelScenarios.M3().First(s => s.Id == "M3.level.ledge"), level, hub, rt, 1);
            Add("level.ledge", rl.Passed, rl.Detail);
            yield return Frames(3);

            // 9. Notebook: ≥ 3 readings, each with an evidence photo whose camera sees its points; opened from the ring.
            Current = "notebook.evidence";
            var cams = root.CamerasInRootSpace();
            int withPoints = 0, seen = 0, thumbs = 0;
            foreach (var e in Notebook.Entries)
            {
                if (e.Points == null || e.Points.Length == 0) continue;
                withPoints++;
                var cam = Array.Find(cams, c => c.id == e.NearestCameraId);
                if (e.NearestCameraId >= 0 && CameraEvidence.ContainsAll(cam, e.Points)) seen++;
                if (nbCtl.ThumbnailFor(e.NearestCameraId) != null) thumbs++;
            }
            yield return OpenMenu(palm);
            ring.SpinTo(iNotebook);
            yield return Settle(ring);
            TapItem(ring, ring.Selected);   // pinch the lens: the Notebook action
            string nbTap = ring.LastAction;
            yield return Frames(2);
            int rows = nbPanel.rows != null ? nbPanel.rows.Count(r => r != null && r.gameObject.activeSelf) : 0;
            bool panelOpen = nbPanel.IsOpen;
            Add("notebook.evidence", withPoints >= 3 && seen == withPoints && thumbs == withPoints && panelOpen && rows >= 3,
                $"entries_with_points={withPoints} camera_sees_points={seen} thumbnails={thumbs} lens={Label(ring, ring.Selected)} tap=\"{nbTap}\" panel_open={panelOpen} rows_shown={rows} title=\"{nbPanel.title?.text}\"");

            // Export writes CSV + HTML (and uploads to the server).
            Current = "notebook.export";
            var dir = AppCommands.ExportNotebook();
            bool files = System.IO.File.Exists(nbCtl.LastExportCsv) && System.IO.File.Exists(nbCtl.LastExportHtml);
            int csvRows = files ? NotebookExporter.ParseCsv(System.IO.File.ReadAllText(nbCtl.LastExportCsv)).Count - 1 : -1;
            string html = files ? System.IO.File.ReadAllText(nbCtl.LastExportHtml) : "";
            const string rowTag = "<tr class=\"entry\">";
            int htmlRows = files ? (html.Length - html.Replace(rowTag, "").Length) / rowTag.Length : -1;
            yield return Until(() => nbCtl.ExportStatus != "exporting", 6f);
            Add("notebook.export", files && csvRows == Notebook.Entries.Count && htmlRows == Notebook.Entries.Count,
                $"csv_rows={csvRows} html_rows={htmlRows} entries={Notebook.Entries.Count} upload=\"{nbCtl.ExportStatus}\" dir={dir}");
            TapItem(ring, ring.Selected);   // pinch the lens again: notebook closed
            yield return CloseMenu(palm);

            // catalog: 9b. Ring ▸ Catalog (a lens pinch) opens the Catalog on the place's categories; no tape is needed for it.
            var catalogWindow = Services.Get<CatalogWindow>();
            if (catalogWindow != null && iCatalog >= 0)
            {
                Current = "ring.catalog";
                yield return OpenMenu(palm);
                ring.SpinTo(iCatalog);
                yield return Settle(ring);
                TapItem(ring, ring.Selected);   // pinch the lens: Catalog
                string catalogTap = ring.LastAction;
                yield return Frames(2);
                Add("ring.catalog", catalogWindow.IsOpen && catalogWindow.Model.CategoryCount > 0,
                    $"lens={Label(ring, ring.Selected)} tap=\"{catalogTap}\" open={catalogWindow.IsOpen} site={catalogWindow.Site} env={catalogWindow.Model.Environment} " +
                    $"categories={catalogWindow.Model.CategoryCount} source={catalogWindow.Model.Source}");
                yield return CloseMenu(palm);
            }

            // 10. Find parts "gutter hanger" → candidates.
            Current = "parts.find";
            int sc0 = browser.SearchCount;
            float tFind = Now;
            AppCommands.FindPart("gutter hanger");
            yield return Until(() => browser.SearchCount > sc0 && !browser.Searching, 30f);
            int iHanger = browser.Candidates.FindIndex(c => c.id == PartScenarios.Hanger);
            Add("parts.find", browser.SearchCount > sc0 && iHanger >= 0,
                $"source={browser.Source} seconds={F(Now - tFind, "0.0")} status=\"{browser.Status}\" candidates=[{string.Join(", ", browser.Candidates.Select(c => c.id))}] server={ServerConfig.Current}");

            // 11. Take it → the part in the hand at true size.
            Current = "parts.take";
            var prevLoaded = browser.LastLoaded;
            bool took = iHanger >= 0 && AppCommands.SelectCandidate(iHanger);
            yield return Until(() => !browser.Loading && browser.LastLoaded != prevLoaded, 45f);
            var hanger = browser.LastLoaded != prevLoaded ? browser.LastLoaded : null;
            string size = "-";
            bool takeOk = took && hanger != null && partTool.Held == hanger && hanger.Spec.id == PartScenarios.Hanger && tools.Active == ToolKind.Part && SizeOk(hanger, out size);
            Add("parts.take", takeOk, $"took={took} held={partTool.Held?.Spec.id ?? "-"} source={hanger?.Source ?? "-"} size={size} tool={tools.Active} status=\"{browser.Status}\"");
            if (hanger == null) yield break;

            // 12. Aim at the fascia (live preview), release → seated flat on it, green.
            Current = "parts.fascia";
            var target = new Vector3(0.3f, 6.15f, S.FasciaProud);
            var aimFrom = new Vector3(0.3f, 7.0f, 1.5f);
            var pose = ToolInputHub.RayPose(rt.TransformPoint(aimFrom), rt.TransformPoint(target));
            hub.SetPointerOverride(ToolHand.Right, pose);
            yield return Secs(0.3f);
            bool preview = partTool.OnSurface;
            string previewFit = hanger.Fit != null ? hanger.Fit.Status.ToString() : "-";
            hub.RaisePressStart(ToolHand.Right, pose);
            hub.RaisePressEnd(ToolHand.Right, pose);
            hub.SetPointerOverride(ToolHand.Right, null);
            Physics.SyncTransforms();
            var hb = PartScenarios.LocalBounds(hanger, rt);
            float backMm = Mathf.Abs(hb.min.z - S.FasciaProud) * 1000f;
            float tilt = Vector3.Angle(rt.InverseTransformDirection(hanger.WorldMountDirection), Vector3.back);
            bool green = hanger.Fit != null && hanger.Fit.Status == FitStatus.Green;
            Add("parts.fascia", hanger.Placed && preview && backMm <= 2f && tilt < 1f && green,
                $"preview_on_surface={preview} preview_fit={previewFit} placed={hanger.Placed} back_face_mm={F(backMm, "0.00")} tilt={F(tilt, "0.00")}° fit={hanger.Fit} last=\"{partTool.LastAction}\"");
            yield return Frames(3);

            // 13. Array along the 4.200 m gutter tape → 8, all green, 600 ± 2 mm.
            Current = "parts.array";
            bool arrayed = AppCommands.PlaceArray(null);
            var g = partTool.LastArray;
            var members = g != null ? PartTool.ArrayMembers(g) : new List<PartInstance>();
            int greens = members.Count(p => p.Fit != null && p.Fit.Status == FitStatus.Green);
            float worst = 0f;
            for (int k = 1; k < members.Count; k++)
                worst = Mathf.Max(worst, Mathf.Abs(Vector3.Distance(members[k].transform.position, members[k - 1].transform.position) / rt.lossyScale.x * 1000f - 600f));
            Add("parts.array", arrayed && members.Count == 8 && greens == 8 && worst <= 2f,
                $"count={members.Count} green={greens} worst_spacing_error_mm={F(worst, "0.00")} run={(g != null ? F(g.Plan.LengthM, "0.000") : "-")} m entry=\"{g?.Entry?.Label}\"");
            yield return Frames(3);

            // 14. Sellers open (the server re-ranks them).
            Current = "sellers";
            bool shown = AppCommands.ShowSellers("cheapest");
            yield return Until(() => !sellers.Refreshing, 25f);
            int rec = hanger.Spec.recommended_seller ?? -1;
            Add("sellers", shown && sellers.IsOpen && sellers.Order.Count > 0 && sellers.Quantity() == 8,
                $"open={sellers.IsOpen} order=[{string.Join(",", sellers.Order)}] qty={sellers.Quantity()} recommended={rec} reason=\"{sellers.reason?.text}\" sellers={hanger.Spec.sellers.Count}");

            // 15. Pay with Visa on the first row: opens checkout only (no request).
            Current = "checkout.open";
            int req0 = client.CheckoutRequests, log0 = AgentHarness.MockCheckoutCount();
            sellers.PayRow(0);
            yield return null;
            var hold = checkout.hold;
            Add("checkout.open", checkout.State == CheckoutState.Ready && checkout.Quantity == 8 && client.CheckoutRequests == req0 && hold != null,
                $"state={checkout.State} seller={checkout.SellerIndex} qty={checkout.Quantity} packs={checkout.Packs} total={F(checkout.Total, "0.00")} bom={(checkout.BomLines.Count)} requests={client.CheckoutRequests - req0}");
            if (hold == null) yield break;

            // 16. Hold Pay for 0.6 s and let go → nothing is sent.
            Current = "checkout.hold_0.6s";
            int conf0 = hold.ConfirmCount;
            hold.PressedOverride = true;
            float h0 = Now;
            yield return Secs(0.6f);
            float progress06 = hold.Progress;
            hold.PressedOverride = false;
            float held06 = Now - h0;
            yield return Secs(1.0f);   // anything sent would have reached the mock by now
            int log06 = AgentHarness.MockCheckoutCount();
            Add("checkout.hold_0.6s", hold.ConfirmCount == conf0 && client.CheckoutRequests == req0 && log06 == log0 && checkout.State == CheckoutState.Ready,
                $"held={F(held06, "0.00")}s progress={F(progress06, "0.00")} confirmed={hold.ConfirmCount - conf0} requests={client.CheckoutRequests - req0} mock_log={log06 - log0} state={checkout.State}");

            // 17. Hold the full second → exactly one request.
            Current = "checkout.hold_1.0s";
            hold.PressedOverride = true;
            h0 = Now;
            yield return Until(() => hold.ConfirmCount > conf0, 2.5f);
            float firedAt = Now - h0;
            yield return Secs(0.2f);   // the hand lets go a moment later
            hold.PressedOverride = false;
            yield return null;
            hold.PressedOverride = null;
            Add("checkout.hold_1.0s", hold.ConfirmCount == conf0 + 1 && client.CheckoutRequests == req0 + 1 && firedAt >= 0.98f && firedAt <= 1.3f,
                $"confirmed_after={F(firedAt, "0.00")}s confirmed={hold.ConfirmCount - conf0} requests={client.CheckoutRequests - req0} state={checkout.State}");

            // 18. Receipt + notebook purchase entry; the mock saw exactly one POST /checkout.
            Current = "checkout.receipt";
            yield return Until(() => checkout.State != CheckoutState.Paying, 30f);
            yield return Secs(0.3f);
            int sent = AgentHarness.MockCheckoutCount() - log0;
            var rcpt = checkout.Receipt;
            bool receiptOk = checkout.State == CheckoutState.Paid && rcpt != null && rcpt.Recorded && checkout.receipt != null && checkout.receipt.activeInHierarchy
                             && Notebook.Last != null && Notebook.Last.Tool == "purchase" && sent == 1 && home.Purchases.Any(b => b.Spec.id == PartScenarios.Hanger);
            Add("checkout.receipt", receiptOk,
                $"state={checkout.State} receipt={rcpt?.receipt_id} mode={rcpt?.mode ?? rcpt?.sandbox} qty={rcpt?.qty} total={(rcpt != null ? F(rcpt.total_usd, "0.00") : "-")} mock_requests={sent} notebook=\"{Notebook.Last?.Label}\" bought={home.Purchases.Count}");
            yield return Frames(3);

            // 19. Ring Undo ×3 steps back across tools (array → hanger → level); Redo ×3 puts it all back.
            Current = "ring.undo_redo";
            string Snapshot() => $"parts={partTool.PlacedParts.Count} shapes={measure.Shapes.Count} levels={level.Placements.Count} notebook=[{string.Join(",", Notebook.Entries.Select(e => e.Id).OrderBy(i => i))}]";
            yield return OpenMenu(palm);
            string before = Snapshot();
            int levels0 = level.Placements.Count, shapesBefore = measure.Shapes.Count;
            var steps = new List<string>();
            for (int k = 0; k < 3; k++) { ring.PressUndo(); steps.Add(ring.LastAction); yield return Frames(2); }
            string mid = Snapshot();
            bool acrossTools = partTool.PlacedParts.Count == 0 && level.Placements.Count == levels0 - 1 && measure.Shapes.Count == shapesBefore;
            for (int k = 0; k < 3; k++) { ring.PressRedo(); steps.Add(ring.LastAction); yield return Frames(2); }
            string after = Snapshot();
            var membersAfter = partTool.LastArray != null ? PartTool.ArrayMembers(partTool.LastArray) : new List<PartInstance>();
            int greensAfter = membersAfter.Count(p => p.gameObject.activeInHierarchy && p.Fit != null && p.Fit.Status == FitStatus.Green);
            Add("ring.undo_redo", acrossTools && after == before && membersAfter.Count == 8 && greensAfter == 8,
                $"steps=[{string.Join(", ", steps)}] before {before} | after 3 undos {mid} | after 3 redos {after} array={membersAfter.Count} green={greensAfter}");

            // 20. Leave the world (declutter DC3: Exit world left the ring). Ring ▸ Settings opens Settings by a lens pinch; then the
            //     receipt's Take it home when the build has it (declutter S6), else Settings ▸ Exit world, which asks twice.
            Current = "world.exit";
            var settings = Services.Get<AirTools.Structure.ScenePanel>();
            ring.SpinTo(iSettings);
            yield return Settle(ring);
            TapItem(ring, ring.Selected);   // pinch the lens: Settings
            string settingsTap = ring.LastAction;
            yield return Frames(2);
            bool settingsOpen = settings != null && settings.IsOpen;
            yield return CloseMenu(palm);
            var takeHome = TakeItHomeButton(checkout);
            string via, first = "-", second = "-";
            bool stillWorld = true, asked = true;
            float tExit = Now;
            if (takeHome != null)
            {
                via = "the receipt's Take it home";
                second = takeHome.Press() ? "pressed" : "refused";
            }
            else
            {
                via = "Settings ▸ Exit world";
                var exit = settings != null ? settings.exitWorld : null;
                bool fired = exit != null && exit.Press();   // the first tap only arms it ("Tap to leave")
                first = exit == null ? "no button" : fired ? "left at once" : exit.Armed ? $"armed \"{exit.label?.text}\"" : "refused";
                yield return Frames(3);
                stillWorld = AppState.Mode == AppMode.World;
                asked = exit != null && !fired && first.StartsWith("armed");
                yield return Secs(exit != null ? exit.cooldownSeconds + 0.05f : 0f);
                tExit = Now;
                second = exit != null && exit.Press() ? "pressed" : "refused";
            }
            yield return Until(() => mode.VisualMode == AppMode.Passthrough && !mode.IsTransitioning, 3f);
            Add("world.exit", settingsOpen && stillWorld && asked && AppState.Mode == AppMode.Passthrough && mode.VisualMode == AppMode.Passthrough && !root.IsVisible
                              && (settings == null || !settings.IsOpen),
                $"ring=\"{settingsTap}\" settings_open={settingsOpen} via={via} first={first} (still in world={stillWorld}) second={second} mode={AppState.Mode} settings_after={(settings != null && settings.IsOpen)} seconds={F(Now - tExit, "0.00")}");

            // 21. Take it home: the bought part on the table, at true size.
            Current = "take_home";
            var onTable = home.OnTable.FirstOrDefault(p => p != null && p.Spec.id == PartScenarios.Hanger);
            string homeDetail = "no hanger on the table";
            bool homeOk = false;
            if (onTable != null)
            {
                Bounds wb = default; bool any = false;
                foreach (var r in onTable.Model.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    if (!any) { wb = r.bounds; any = true; } else wb.Encapsulate(r.bounds);
                }
                var headT = home.head != null ? home.head : home.rig;
                float tableY = home.rig.position.y + home.tableHeight;
                var flat = Vector3.ProjectOnPlane(onTable.transform.position - headT.position, Vector3.up);
                bool sized = SizeOk(onTable, out var hs);
                bool scaleOne = Mathf.Abs(onTable.transform.lossyScale.x - 1f) < 1e-4f;
                homeOk = onTable.gameObject.activeInHierarchy && sized && scaleOne && any && Mathf.Abs(wb.min.y - tableY) <= 0.005f
                         && flat.magnitude > 0.2f && flat.magnitude < 0.8f && AppState.Mode == AppMode.Passthrough;
                homeDetail = $"parts_on_table={home.OnTable.Count} size={hs} lossyScale={F(onTable.transform.lossyScale.x, "0.0000")} bottom_y={F(wb.min.y)} table_y={F(tableY)} ahead={F(flat.magnitude, "0.00")} m mode={AppState.Mode}";
            }
            Add("take_home", homeOk, homeDetail);

            // 22. Tabletop 1:50 from the ring (Model view).
            Current = "tabletop.on";
            var worldPose = new Pose(rt.position, rt.rotation);
            yield return OpenMenu(palm);
            ring.SpinTo(iTabletop);
            yield return Settle(ring);
            TapItem(ring, ring.Selected);
            string ttTap = ring.LastAction;
            yield return Until(() => mode.VisualMode == AppMode.Tabletop && !mode.IsTransitioning, 3f);
            // modelview: the model is fitted to the table (1:N, longest side ≤ 0.8 m), no longer a fixed 1:50.
            Add("tabletop.on", AppState.Mode == AppMode.Tabletop && table.OnTable && table.Scale > 0f && Mathf.Abs(rt.lossyScale.x - table.Scale) < 1e-5f && Mathf.Abs(SnapService.Scale - table.Scale) < 1e-5f && root.IsVisible,
                $"tap=\"{ttTap}\" mode={AppState.Mode} scale=1:{F(1f / rt.lossyScale.x, "0")} snapScale={F(SnapService.Scale, "0.###")} ground_y={F(rt.position.y)} home_parts={home.OnTable.Count}");

            // 23. The tape still reads 1.500 m at 1:50: the reading taken at 1:1, and a new tape across the model.
            Current = "tabletop.tape";
            ring.SpinTo(iMeasure);
            yield return Settle(ring);
            if (tools.Active != ToolKind.Measure) TapItem(ring, ring.Selected);   // D6: pinch the lens to commit
            yield return Until(() => tools.Active == ToolKind.Measure, 1f);
            yield return CloseMenu(palm);
            double oldReading = windowShape != null ? windowShape.Measurement.Distance : double.NaN;
            string oldLabel = windowShape != null && windowShape.View != null && windowShape.View.ActiveLabelCount > 0 ? windowShape.View.Labels[0].Text.Replace("\n", " ") : "-";
            var rt50 = MeasureScenarios.Run(win, measure, hub, rt, 2);
            var tapeAt50 = measure.Shapes.Count > 0 ? measure.Shapes[measure.Shapes.Count - 1] : null;
            Add("tabletop.tape", tools.Active == ToolKind.Measure && Math.Abs(oldReading - S.WindowWidth) <= 0.010 && rt50.Passed,
                $"tool={tools.Active} reading_from_1:1={F(oldReading)} label=\"{oldLabel}\" | new tape at 1:50: {rt50.Detail}");

            // 24. Back to 1:1 (pinch Model view again).
            Current = "tabletop.off";
            yield return OpenMenu(palm);
            ring.SpinTo(iTabletop);
            yield return Settle(ring);
            TapItem(ring, ring.Selected);
            yield return Until(() => mode.VisualMode == AppMode.World && !mode.IsTransitioning, 3f);
            yield return CloseMenu(palm);
            double at50Now = tapeAt50 != null ? tapeAt50.Measurement.Distance : double.NaN;
            bool back = AppState.Mode == AppMode.World && !table.OnTable && Mathf.Abs(rt.lossyScale.x - 1f) < 1e-5f && Mathf.Abs(SnapService.Scale - 1f) < 1e-5f
                        && Vector3.Distance(rt.position, worldPose.position) < 1e-4f && Quaternion.Angle(rt.rotation, worldPose.rotation) < 0.01f
                        && Math.Abs(at50Now - S.WindowWidth) <= 0.010;
            Add("tabletop.off", back, $"mode={AppState.Mode} scale={F(rt.lossyScale.x, "0.###")} snapScale={F(SnapService.Scale, "0.###")} root_moved_mm={F(Vector3.Distance(rt.position, worldPose.position) * 1000f, "0.00")} tape_made_at_1:50_reads={F(at50Now)}");
            Current = "done";
        }
    }
}
#endif
