using System.Linq;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.UI;
using UnityEngine;
using AirTools.Tools;

namespace AirTools
{
    /// The one command surface. Voice and UI call the SAME methods (plan §3: voice never has its own code path).
    /// Commands for later milestones log and do nothing until their milestone lands.
    public static class AppCommands
    {
        // M1
        public static void OpenChest() => AppState.Set(AppMode.World);
        public static void CloseChest() => AppState.Set(AppMode.Passthrough);

        /// Enter world / Exit world. Leaving a world you stepped into from the table model shrinks it back onto the
        /// table (presence.md S1 shrink out) unless Reduce motion is on (then: the fade to passthrough, as before).
        public static void ToggleChest()
        {
            if (AppState.Mode == AppMode.Passthrough) OpenChest();
            else if (AppState.Mode == AppMode.World && AppState.Previous == AppMode.Tabletop && !UiSettings.ReducedMotion) StepOut();
            else CloseChest();
        }

        // presence.md S1
        /// Step in: from the model on the table, grow it around you (Tabletop → World); from passthrough, pour the
        /// world in. Already inside: nothing to do.
        public static bool StepIn()
        {
            switch (AppState.Mode)
            {
                case AppMode.Tabletop: AppState.Set(AppMode.World); return true;
                case AppMode.Passthrough: OpenChest(); return true;
                default: Log.Info("AppCommands.StepIn: already in the world"); return false;
            }
        }

        /// Step out: the sky opens and the building shrinks back onto the table, the parts you bought beside it
        /// (World → Tabletop).
        public static bool StepOut()
        {
            if (AppState.Mode != AppMode.World) { Log.Info("AppCommands.StepOut: not in the world"); return false; }
            AppState.Set(AppMode.Tabletop);
            return true;
        }

        // M2+
        /// "measure" (aliases: tape, ruler, area, angle…), "level" (M3), "none".
        public static bool EquipTool(string tool)
        {
            if (!ToolManager.TryParse(tool, out var kind))
            {
                Log.Warn($"AppCommands.EquipTool: unknown tool '{tool}'");
                return false;
            }
            var tools = Services.Get<ToolManager>();
            if (tools == null) { Log.Warn("AppCommands.EquipTool: no ToolManager in scene"); return false; }
            // "area" / "angle" / "protractor": a shape (with D4's auto-save, a plain tape saves at its 2nd point).
            if (tools.measure != null && kind == ToolKind.Measure)
            {
                var n = (tool ?? "").Trim().ToLowerInvariant();
                tools.measure.AreaMode = n == "area" || n == "angle" || n == "protractor";
            }
            tools.Equip(kind);
            return true;
        }
        // M3
        public static void AddNote(string text)
        {
            if (Services.TryGet<NotebookController>(out var nb)) nb.AddNote(text);
            else Notebook.Add(new NotebookEntry("note", 0, "", System.Array.Empty<UnityEngine.Vector3>(), System.DateTime.Now, -1, text ?? ""));
        }

        /// Writes CSV + HTML to the headset and POSTs to the laptop if it's reachable.
        public static string ExportNotebook() => Services.Get<NotebookController>()?.Export();

        public static void ShowNotebook(bool open)
        {
            if (Services.TryGet<NotebookPanel>(out var panel)) panel.SetOpen(open);
        }
        // M4
        /// Search the parts server (offline catalog fallback); candidates appear as cards on the crate menu.
        public static bool FindPart(string query)
        {
            if (!Services.TryGet<PartsBrowser>(out var browser)) { Log.Warn("AppCommands.FindPart: no PartsBrowser in scene"); return false; }
            if (AppState.Mode != AppMode.World) OpenChest();
            browser.Search(query);
            return true;
        }

        /// Take candidate i (0-based) into the hand.
        public static bool SelectCandidate(int i) => Services.TryGet<PartsBrowser>(out var browser) && browser.Select(i);

        /// Recolour the selected part to one of its listed finishes ("white", "brown"…).
        public static bool SetFinish(string name)
        {
            bool ok = Services.TryGet<PartTool>(out var tool) && tool.SetFinish(name);
            if (!ok) Log.Warn($"AppCommands.SetFinish: no finish '{name}' on the selected part");
            return ok;
        }
        // P6/P7 (presence.md S4)
        /// Fall-edge overlay: edges 1.8 m+ above the ground glow red / white with "fall protection" (1926.501(b)(1)).
        public static bool ShowFallEdges(bool on)
        {
            if (!Services.TryGet<AirTools.Structure.FallEdges>(out var edges)) { Log.Warn("AppCommands.ShowFallEdges: no FallEdges in scene"); return false; }
            edges.SetVisible(on, toast: true);
            return true;
        }

        /// "Find this ladder": the parts search for the ladder the check sized ("28 ft extension ladder").
        public static bool FindLadder(int sizeFt) => FindPart(LadderMath.SearchQuery(sizeFt));
        // M7
        /// The scene as a 1:50 model on the table (true) or back to full size (false). From passthrough, opens it.
        public static void SetTabletop(bool on)
        {
            if (on) AppState.Set(AppMode.Tabletop);
            else if (AppState.Mode == AppMode.Tabletop) AppState.Set(AppMode.World);
        }

        public static void ToggleTabletop() => SetTabletop(AppState.Mode != AppMode.Tabletop);

        // modelview
        /// Model view with `site` on the table ("show me the gym model"; the show_model action): a site id or a spoken
        /// name (ModelSites.Match over the server's listing and the built-in facade), or "next" / "previous" to step through
        /// the switcher; empty = just Model view. Enters Model view first (the building shrinks onto the table, or appears
        /// on it from passthrough); the model loads once the table has settled, and it's the world you step into next.
        /// False when there's no switcher or nothing by that name.
        public static bool ShowModel(string site)
        {
            if (!Services.TryGet<AirTools.Scene.ModelSwitcher>(out var switcher)) { Log.Warn("AppCommands.ShowModel: no model switcher in the scene"); return false; }
            string key = (site ?? "").Trim().ToLowerInvariant();
            SetTabletop(true);
            switch (key)
            {
                case "": case "model": case "model view": case "models": return true;
                case "next": case "next model": return switcher.Step(+1) != null;
                case "previous": case "prev": case "previous model": case "back": return switcher.Step(-1) != null;
                default: return switcher.ShowNamed(site.Trim());
            }
        }
        // end modelview

        // M5
        /// Copies of the selected placed part along the last tape every spacingMm (default: the listing's spacing).
        public static bool PlaceArray(float? spacingMm)
        {
            if (!Services.TryGet<PartTool>(out var tool)) return false;
            var g = tool.PlaceArray(spacingMm);
            if (g == null) UiToast.Show(ArrayRefusal(tool), ColorRole.Warning);
            return g != null;
        }

        /// Why an array was refused, in words (LastAction stays raw for the tests and the log).
        static string ArrayRefusal(PartTool tool)
        {
            var reference = tool.Selected != null && tool.Selected.Placed ? tool.Selected : tool.PlacedParts.Count > 0 ? tool.PlacedParts[tool.PlacedParts.Count - 1] : null;
            string noun = reference != null ? Copy.Noun(reference.Spec, reference.SearchQuery) : "part";
            string a = tool.LastAction ?? "";
            if (a.Contains("place one part first")) return $"Place one {(tool.Held != null ? Copy.Noun(tool.Held.Spec, tool.Held.SearchQuery) : noun)} first, then fill the run";
            if (a.Contains("single unit")) return $"No spacing listed · place each {noun} by hand";
            if (a.Contains("measure the run first"))
                return AirTools.Input.InputMode.Controllers ? "Measure the run first · trigger both ends" : "Measure the run first · pinch both ends";
            return a.Replace("array: ", "");
        }

        /// Seller list for the selected part: "price" / "cheapest", "eta" / "soonest".
        public static bool ShowSellers(string sort) => Services.TryGet<SellerPanel>(out var panel) && panel.Show(null, sort);

        /// Opens the checkout panel only; payment needs the physical 1 s hold on its Pay button (no code path here pays).
        public static bool StartCheckout(int sellerIndex)
        {
            if (!Services.TryGet<PartTool>(out var tool) || tool.Selected == null) return false;
            var spec = tool.Selected.Spec;
            int qty = Mathf.Max(1, tool.QuantityOf(spec.id));
            if (!Services.TryGet<CheckoutPanel>(out var panel)) return false;
            panel.SearchQuery = tool.Selected.SearchQuery;
            if (!panel.Open(spec, sellerIndex, qty)) return false;
            if (CurrentBom != null) panel.SetBom(CurrentBom);
            return true;
        }

        // Backend integration (airtools-drone-backend docs/api.md)

        /// "What else do I need?" lines from the agent's show_bom action; the next checkout adds them to the same cart.
        public static PartBom CurrentBom { get; private set; }

        /// Forget the "what else do I need?" lines (tests; Play mode doesn't reload the domain, so statics persist).
        public static void ClearBom() => CurrentBom = null;

        /// search_started {job_id}: follow a search the agent started; candidates appear on the Find parts menu.
        public static bool FollowSearch(string jobId)
        {
            if (string.IsNullOrEmpty(jobId) || !Services.TryGet<PartsBrowser>(out var browser)) return false;
            if (AppState.Mode != AppMode.World) OpenChest();
            browser.FollowJob(jobId);
            return true;
        }

        /// show_bom {bom_id, lines, total_usd}: show the extra items; they ride along in the next checkout.
        public static bool ShowBom(PartBom bom)
        {
            if (bom == null || bom.lines == null) return false;
            CurrentBom = bom;
            var names = new System.Collections.Generic.List<string>();
            foreach (var l in bom.lines) names.Add(l.qty > 1 ? $"{l.qty} × {l.name}" : l.name);
            var shown = new System.Collections.Generic.List<string>();
            for (int i = 0; i < names.Count && i < 2; i++) shown.Add(Copy.Clean(names[i]));
            string list = string.Join(", ", shown) + (names.Count > 2 ? $" +{names.Count - 2} more" : "");
            UiToast.Show(names.Count == 0 ? "Nothing else needed" : $"You'll also need: {list} · {PartFormat.Price(bom.total_usd)}", ColorRole.Info);
            Notebook.Add(new NotebookEntry("bom", bom.total_usd, "USD", System.Array.Empty<Vector3>(), System.DateTime.Now, -1,
                $"What else: {string.Join(", ", names)} ({PartFormat.Price(bom.total_usd)})") { DisplayTitle = "Also needed", DisplayDetail = list });
            if (Services.TryGet<CheckoutPanel>(out var panel) && panel.State == CheckoutState.Ready) panel.SetBom(bom);
            return true;
        }

        /// scene_pin {frame_id, box}: project the box centre from that drone photo onto the scene and pin it.
        public static bool PinFromFrame(string frameId, float[] box, string text) =>
            Services.TryGet<AirTools.Agent.SceneAsk>(out var ask) && ask.PinFromFrame(frameId, box, text);

        /// Ask a question about what you're looking at (POST /scene/ask with the nearest drone photos).
        public static bool AskScene(string question)
        {
            if (!Services.TryGet<AirTools.Agent.SceneAsk>(out var ask) || ask.Busy) return false;
            ask.Ask(string.IsNullOrWhiteSpace(question) ? "What is this?" : question);
            return true;
        }

        /// A typed command for the quartermaster (POST /agent/command); its actions run like button taps.
        public static bool SendCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !Services.TryGet<AirTools.Agent.AgentClient>(out var agent)) return false;
            agent.SendText(text);
            return true;
        }

        /// Set scale from a known dimension: the latest tape reading is really `realMetres` long.
        public static bool SetScale(double realMetres) => AirTools.Structure.ScaleCalibration.Apply(realMetres);

        public static bool ResetScale() => AirTools.Structure.ScaleCalibration.Reset();

        /// Snapping on/off (off: points land exactly where the ray hits).
        public static void SetSnapping(bool on) { AirTools.Scene.SnapService.Enabled = on; Log.Info($"Snapping {(on ? "on" : "off")}"); }

        /// Draw the structure layer's edges and corners over the scene.
        public static bool ShowStructure(bool on)
        {
            if (!Services.TryGet<AirTools.Structure.StructureOverlay>(out var o)) return false;
            o.SetVisible(on);
            return true;
        }

        /// Load a scene package from the laptop server ("" or "built-in" = the scene baked into the app).
        public static bool LoadSite(string site)
        {
            if (!Services.TryGet<AirTools.Scene.SceneStreamer>(out var s)) return false;
            if (string.IsNullOrEmpty(site) || site == "built-in") { s.LoadBuiltIn(); return true; }
            return s.Load(site);
        }

        // B1 / B3 hand-off: the agent measures for you, and the measured mandate

        static AirTools.Agent.SurveyRunner Runner()
        {
            if (Services.TryGet<AirTools.Agent.SurveyRunner>(out var r)) return r;
            var tool = Services.Get<MeasureTool>();
            if (tool == null) return null;
            r = tool.gameObject.AddComponent<AirTools.Agent.SurveyRunner>();
            Services.Register(r);
            return r;
        }

        /// survey {label, where, measure, request_id}: tape every matching structure object with the real measure tool,
        /// one after another, then report once to /agent/observe.
        public static bool Survey(string label, string where, string measure, string requestId)
        {
            var r = Runner();
            if (r == null) { Log.Warn("AppCommands.Survey: no measure tool"); return false; }
            if (AppState.Mode == AppMode.Passthrough) OpenChest();
            return r.Run(label, where, measure, requestId);
        }

        /// stop_survey: abort a running survey (completed entries stay). Nothing running: nothing to do.
        public static void StopSurvey()
        {
            if (Services.TryGet<AirTools.Agent.SurveyRunner>(out var r) && r.Running) r.Abort();
        }

        /// check_slope {target, request_id, edge_ids}: a 2-point tape along the structure edge → slope_result.
        public static bool CheckSlope(string target, string requestId, System.Collections.Generic.IList<string> edgeIds)
        {
            var r = Runner();
            if (r == null) return false;
            if (AppState.Mode == AppMode.Passthrough) OpenChest();
            return r.CheckSlope(target, requestId, edgeIds);
        }

        /// measure_edges {label, segments / edge_ids}: tape the structure's segments with the real tape (a roof end to end,
        /// a platform's height), one notebook entry each titled label. The number is the tape's.
        public static bool MeasureEdges(AirTools.Agent.MeasureEdgesActions.Args args)   // measure-edges
        {
            var r = Runner();
            if (r == null || args == null) return false;
            if (AppState.Mode == AppMode.Passthrough) OpenChest();
            return r.MeasureSegments(args) > 0;
        }

        /// equip_tool {tool: "tape", label}: the next tape the user finishes is titled `label` in the notebook ("Roof
        /// length"), after "Measure's ready: pinch one end of the roof, then the other". Null clears it.
        public static void SetNextMeasureLabel(string label)   // measure-edges
        {
            if (Services.TryGet<MeasureTool>(out var t)) t.NextLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        }

        /// show_survey: the survey card (size groups, amber unverified, highlighted focus).
        public static bool ShowSurvey(Newtonsoft.Json.Linq.JObject args)
        {
            if (Services.TryGet<AirTools.Agent.SurveyCard>(out var card)) return card.Show(args);
            // No card in the scene (tests): still highlight the focus in the scene.
            if (args != null && Services.TryGet<MeasureTool>(out var tool)) tool.FocusSurvey(AirTools.Agent.SurveyCard.Ids(args["focus"]), (string)args["request_id"]);
            return args != null;
        }

        /// show_limits: the wrist chip ("≤ $40 · by Fri · fastest"); an open checkout re-checks against the new limits.
        public static bool ShowLimits(MandateIntent intent, bool refused)
        {
            if (intent == null) return false;
            LimitsChip.Set(intent, refused);
            UiToast.Show(refused ? $"Limits kept · {MandateText.Limits(intent)} · raise them on the panel" : $"Limits · {MandateText.Limits(intent)}", ColorRole.Info);
            if (Services.TryGet<CheckoutPanel>(out var panel) && (panel.State == CheckoutState.Ready || panel.State == CheckoutState.Failed)) panel.Prepare();
            return true;
        }

        // W1.3 guide rail (docs/ux/specs/W1.3-nextstep.md §3.5): the commands the Next-step pill, voice and the presenter
        // share (NextStepActions.Run is their one dispatcher). None of them pays.

        /// W1.3: save the tape / shape in progress — the same finish the other hand's pinch or B gives.
        public static bool FinishShape() => Services.TryGet<MeasureTool>(out var measure) && measure.Finish() != null;

        /// W1.3: global undo / redo — the newest edit in any tool, in any mode (EditHistory).
        public static bool Undo() => EditHistory.Undo();
        public static bool Redo() => EditHistory.Redo();

        /// W1.3: show Find parts in front of you (it stays hidden until the first reading, UX W0.7); opens the world first.
        /// catalog: the Catalog, with the Fits chip on (the pill offers it after a tape).
        public static bool ShowFindParts()
        {
            if (Services.TryGet<CatalogWindow>(out _)) return ShowCatalog(fromTape: true);   // catalog
            if (!Services.TryGet<PartsBrowser>(out var browser)) { Log.Warn("AppCommands.ShowFindParts: no PartsBrowser in scene"); return false; }
            if (AppState.Mode != AppMode.World) OpenChest();
            if (!browser.Revealed) browser.Reveal();
            else
            {
                if (browser.content != null) browser.content.SetActive(true);
                browser.Place();
            }
            return true;
        }

        // catalog ---------------------------------------------------------------------------------------------------------
        // The Catalog (Parts/CatalogWindow): the ring's Catalog item, voice (show_catalog, and "find a …" with no search from
        // the server: LocalIntents), the harness. It needs no tape. None of it pays.

        /// Open the Catalog in the left side slot: the place's categories; `category` ("dishwashers") shows that one and
        /// `query` goes in the search field as if typed (the loaded items filter at once, the laptop joins after a pause).
        /// `fromTape` (the tape's "Find parts for …" pill) starts the Fits chip on. From passthrough or Model view it enters
        /// the world first (parts are taken and placed there). False without a Catalog in the scene.
        public static bool ShowCatalog(string category = null, string query = null, bool fromTape = false)
        {
            if (!Services.TryGet<CatalogWindow>(out var catalog)) { Log.Warn("AppCommands.ShowCatalog: no CatalogWindow in scene"); return false; }
            if (AppState.Mode == AppMode.Tabletop) SetTabletop(false);
            else if (AppState.Mode != AppMode.World) OpenChest();
            catalog.Show(category, query, fromTape);
            return true;
        }

        public static bool HideCatalog()
        {
            if (!Services.TryGet<CatalogWindow>(out var catalog)) return false;
            catalog.Hide();
            return true;
        }

        /// The ring's Catalog: open it, or close it when it's up in front of you (from passthrough / Model view it opens).
        public static bool ToggleCatalog() => Services.TryGet<CatalogWindow>(out var c) && c.Showing ? HideCatalog() : ShowCatalog();
        // end catalog -----------------------------------------------------------------------------------------------------

        /// W1.3 "Keep working" / "Cancel": close the windows in front of you — sellers, checkout (never while a payment is
        /// in flight), notebook, Scene window, and every other main-slot card (declutter S1: the Grok card, the overlay
        /// card, the survey card and the ladder card).
        public static void CloseWindows()
        {
            if (Services.TryGet<SellerPanel>(out var sellers)) sellers.Close();
            if (Services.TryGet<CheckoutPanel>(out var checkout) && checkout.State != CheckoutState.Paying) checkout.Close();
            if (Services.TryGet<NotebookPanel>(out var notebook)) notebook.SetOpen(false);
            if (Services.TryGet<AirTools.Structure.ScenePanel>(out var scene)) scene.Close();
            if (Services.TryGet<AirTools.Agent.Grok.GrokCard>(out var grokCard)) grokCard.Close();
            if (Services.TryGet<AirTools.Agent.Grok.GrokOverlayCard>(out var overlayCard)) overlayCard.Close();
            if (Services.TryGet<AirTools.Agent.SurveyCard>(out var survey)) survey.Close();
            if (Services.TryGet<LadderCard>(out var ladder)) ladder.Close();
            if (Services.TryGet<PlacementPanel>(out var placement)) placement.Close();   // placement: the adjust panel (adjust mode ends)
        }

        // switchclean -----------------------------------------------------------------------------------------------------
        // A world-model switch closes every UI in front of you (SwitchClose: the hook on SiteScope.Leaving, the pure run
        // and the checkout rules). The Model view wheel and its chips stay: they are the switcher.

        /// Close everything open for a switch to `site` (SiteScope.Leaving, before any owner parks): the Catalog and its
        /// keyboard, Settings, the Notebook, Adjust (with Size & finish; its session commits on the site being left), the
        /// spec card (the selection), Find parts with its old results, Sellers, Checkout (not while Pay is held or a payment
        /// is being authorized: SwitchClose.CheckoutKeep), the Grok card, the overlay card (plan / survey findings), the
        /// survey card, the install coach card, the ladder card, the toast / answer card / status flash, and the palm ring.
        /// Logs "Switched to <site>: closed [...]" and returns that line.
        public static string CloseAllForSwitch(string site)
        {
            var list = SwitchSurfaces(site);
            var closed = new System.Collections.Generic.List<string>();
            var kept = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();
            SwitchClose.Run(list, closed, kept, failed);
            // The notebook opens on the arriving model's readings next time (All sites off): another model's tapes aren't
            // listed unless you ask for them there.
            if (Services.TryGet<NotebookPanel>(out var notebook) && notebook.AllSites) notebook.SetAllSites(false);
            string line = SwitchClose.Record(site, closed, kept);
            Log.Info(line);
            foreach (var f in failed) Log.Warn($"Switch close: {f}");
            return line;
        }

        /// The person switched to `site` mid-run: the job stops on the headset (no more polls or steps), its strip ends
        /// cancelled, and a quiet toast says so ("Stopped the dishwasher job: you switched to the Zabel gym").
        public static bool StopJobForSwitch(string site)
        {
            if (!AirTools.Agent.Grok.GrokRails.CancelForSwitch(site, Time.realtimeSinceStartupAsDouble, out string toast)) return false;
            Log.Info($"Job {AirTools.Agent.Grok.GrokRails.Job?.RunId} stopped on the headset: switched to {site} (POST /job/run/…/cancel sent)");
            UiToast.Show(toast, ColorRole.Info);
            return true;
        }

        /// The surfaces a switch closes, in closing order (the keyboard before its catalog, Adjust before the spec card).
        public static System.Collections.Generic.List<SwitchClose.Surface> SwitchSurfaces(string site = null)
        {
            var l = new System.Collections.Generic.List<SwitchClose.Surface>(20);
            if (Services.TryGet<CatalogWindow>(out var catalog))
            {
                l.Add(new SwitchClose.Surface("Keyboard", () => catalog.Model.KeyboardOpen || (catalog.keyboard != null && catalog.keyboard.IsOpen),
                    () => { catalog.Model.SetKeyboard(false); if (catalog.keyboard != null) catalog.keyboard.Show(false); }));
                l.Add(new SwitchClose.Surface("Catalog", () => catalog.IsOpen || catalog.Model.IsOpen, catalog.Hide));
            }
            if (Services.TryGet<AirTools.Structure.ScenePanel>(out var settings))
                l.Add(new SwitchClose.Surface("Settings", () => settings.IsOpen, settings.Close));
            if (Services.TryGet<NotebookPanel>(out var notebook))
                l.Add(new SwitchClose.Surface("Notebook", () => notebook.IsOpen, () => notebook.SetOpen(false)));
            if (Services.TryGet<EditView>(out var editView))   // edit6dof: before Adjust — the view's edits are kept for the site left
                l.Add(new SwitchClose.Surface("Edit view", () => editView.Active, () => editView.Finish(keep: true)));
            var editor = PlacementEditor.Current;
            Services.TryGet<PlacementPanel>(out var adjust);
            if (editor != null || adjust != null)
                l.Add(new SwitchClose.Surface("Adjust", () => (editor != null && editor.IsAdjusting) || (adjust != null && adjust.IsOpen), () =>
                {
                    if (editor != null && editor.IsAdjusting) editor.Exit();   // the session so far: one undo step of the site left
                    if (adjust != null && adjust.IsOpen) adjust.Close();
                }));
            if (Services.TryGet<PartTool>(out var parts))
                // The spec card is the selection's: deselected before PartTool parks, so coming back doesn't reopen it.
                l.Add(new SwitchClose.Surface("Spec card", () => parts.Selected != null, () => parts.Select(null)));
            if (Services.TryGet<PartsBrowser>(out var browser))
                // Find parts and what it still holds for the site left: its search, candidates and "N matches for your
                // tape" line, the Catalog's store results and Fits (ResetSession; a search or load in flight is dropped).
                l.Add(new SwitchClose.Surface("Find parts", () => (browser.catalog == null && browser.content != null && browser.content.activeSelf)
                                                                   || browser.Candidates.Count > 0 || browser.LastQuery != null || browser.Searching || browser.Loading
                                                                   || (browser.catalog != null && browser.catalog.Model.HasResults),
                    browser.ResetSession));
            if (Services.TryGet<SellerPanel>(out var sellers))
                l.Add(new SwitchClose.Surface("Sellers", () => sellers.IsOpen, sellers.Close));
            if (Services.TryGet<CheckoutPanel>(out var checkout))
                l.Add(new SwitchClose.Surface("Checkout", () => checkout.State != CheckoutState.Closed || (checkout.window != null && checkout.window.IsOpen),
                    checkout.Close,
                    () => SwitchClose.CheckoutKeep(checkout.State, checkout.hold != null && checkout.hold.Holding),
                    why => { if (why == SwitchClose.HoldingPay) checkout.CloseWhenHoldEnds(); }));
            if (Services.TryGet<AirTools.Agent.Grok.GrokCard>(out var grokCard))
                l.Add(new SwitchClose.Surface("Grok card", () => grokCard.IsOpen, grokCard.Close));
            if (Services.TryGet<AirTools.Agent.Grok.GrokOverlayCard>(out var overlayCard))
                l.Add(new SwitchClose.Surface("Overlay card", () => overlayCard.IsOpen, overlayCard.Close));
            if (Services.TryGet<AirTools.Agent.SurveyCard>(out var survey))
                l.Add(new SwitchClose.Surface("Survey card", () => survey.IsOpen, survey.Close));
            if (Services.TryGet<AirTools.Agent.Grok.CoachRailView>(out var coach))
                l.Add(new SwitchClose.Surface("Coach card", () => coach.IsOpen, coach.Dismiss));
            if (Services.TryGet<LadderCard>(out var ladder))
                l.Add(new SwitchClose.Surface("Ladder card", () => ladder.IsOpen, ladder.Close));
            l.Add(new SwitchClose.Surface("Toast", UiToast.AnyShowing, () => UiToast.DismissAll()));
            // A running job ("do the whole job", the autonomous replace): the person's switch stops it on the headset (after
            // the toast is cleared, so its quiet line shows); a switch the job asked for itself keeps it going there.
            l.Add(new SwitchClose.Surface("Job", () => AirTools.Agent.Grok.GrokRails.Running, () => StopJobForSwitch(site),
                () => AirTools.Agent.Grok.GrokRails.OnSwitch(site, Time.realtimeSinceStartupAsDouble) == AirTools.Agent.Grok.JobSwitchVerdict.JobSwitch ? "its own switch" : null));
            if (Services.TryGet<AirTools.Input.PalmMenu>(out var palm))
                l.Add(new SwitchClose.Surface("Palm ring", () => palm.IsOpen, () => palm.Dismiss()));
            return l;
        }
        // end switchclean -------------------------------------------------------------------------------------------------

        /// W1.3: open (or close) the Scene window — "Set scale" lives there.
        public static bool ShowScenePanel(bool open)
        {
            if (!Services.TryGet<AirTools.Structure.ScenePanel>(out var panel)) return false;
            if (open) panel.Open();
            else panel.Close();
            return true;
        }

        /// W1.6 (not built yet): after a failed hold in DemoMode, a receipt labelled "OFFLINE DEMO · not authorized" that
        /// never calls PartsClient.Checkout. Until W1.6 lands it does nothing, and the rail never offers it.
        public static bool UseOfflineReceipt()
        {
            NotYet(nameof(UseOfflineReceipt), "W1.6");
            return false;
        }

        static void NotYet(string command, string milestone) => Log.Info($"AppCommands.{command}: not implemented until {milestone}");

        // D7 (UX W1.8): demo controls — DemoMode (Core/DemoMode.cs), the reset gestures (Input/DemoResetGesture.cs) and the
        // presenter page (Dev/PresenterLink.cs, tools/presenter/). None of them pays.

        /// Back to the first-run state for the next judge, without restarting: passthrough, no world, no tapes / levels /
        /// ladders / parts / arrays, the notebook's session entries, checkout and BOM cleared, Undo / Redo empty, guide
        /// rail and coach reset, no tool (entering equips ToolManager.Default, D1). Exported notebook files on disk stay.
        /// False (nothing changed) while a payment is being authorized. Details: DemoReset.
        public static bool ResetDemo() => DemoReset.Run(out _);

        /// DemoMode on or off (the expo build starts with it on).
        public static void SetDemoMode(bool on) => DemoMode.On = on;

        // ---------------- scene parts (backend docs/api.md parts.r<rev>.json) ----------------
        // Hands (Settings ▸ Take out) and voice (remove_component) both come here.

        /// Take a scene part out (id "dw1", or its label / class, "dishwasher"): its mesh and collider hide, its cavity
        /// shows with the "W × H × D · estimated" label. One undoable edit. False when the scene has no such part.
        public static bool RemoveComponent(string idOrLabel) => ScenePartsFor(idOrLabel, out var parts) && parts.Remove(idOrLabel);

        /// Put a scene part back. e2e: a model standing in its gap (ModelCycler) goes too — "put the dishwasher back"
        /// means the original, alone (the Settings chip's toggle doesn't come here).
        public static bool RestoreComponent(string idOrLabel)
        {
            if (!ScenePartsFor(idOrLabel, out var parts)) return false;
            using (EditHistory.Step())   // delete-undo: the model out and the part back are one Undo step
            {
                // e2e: the model standing in its gap goes (whoever put it there). delete-undo: undoably (Undo puts it back).
                if (parts.Find(idOrLabel) is AirTools.Scene.ScenePartComponent c && AirTools.Scene.Gaps.TryGet(out var gap, c.id)
                    && AirTools.Scene.Gaps.ModelIn(gap) is PartInstance model && Services.TryGet<PartTool>(out var tool))
                {
                    if (ModelCycler.ComponentId == c.id) ModelCycler.Clear(removeModel: false);
                    tool.Delete(model);
                }
                return parts.Restore(idOrLabel);
            }
        }

        /// Out if it's in, back if it's out (a Settings chip).
        public static bool ToggleComponent(string idOrLabel) => ScenePartsFor(idOrLabel, out var parts) && parts.Toggle(idOrLabel);

        /// place_part ("measure and replace" by voice, a backend draft): load the part (part.json + model_url, else the
        /// GLB alone at its own size), put it at `pose` (the scene / structure frame: glTF, X flipped, gravity-aligned;
        /// its front-bottom-centre unless the pose says "origin") — or, without a pose, into the cavity of the part taken
        /// out most recently (or `component_id`'s): front-bottom-centre at cavity.insert.p, facing out of the opening —
        /// and show the server's fits / clearance_mm with the usual fit colours and callout. One undoable placement.
        /// True when the load started (the result arrives as a toast / status flash).
        public static bool PlacePart(PlacePartArgs a)
        {
            if (a == null || (string.IsNullOrEmpty(a.PartId) && string.IsNullOrEmpty(a.ModelUrl)))
            {
                UiToast.Show("Which part? None was named", ColorRole.Warning);
                Log.Warn("AppCommands.PlacePart: no part_id or model_url");
                return false;
            }
            if (!Services.TryGet<PartLoader>(out var loader) || !Services.TryGet<PartTool>(out var tool)
                || !Services.TryGet<AirTools.Scene.SceneRoot>(out var root) || root.Content == null)
            {
                Log.Warn("AppCommands.PlacePart: needs a PartLoader, a PartTool and a loaded scene");
                return false;
            }
            if (!TryPlaceTarget(a, root, out var target, out string why))
            {
                UiToast.Show(why, ColorRole.Warning);
                Log.Warn($"AppCommands.PlacePart: {why} ({a})");
                return false;
            }
            var fit = PlacePartFit.From(a.Fits, a.Clearance);
            Log.Info($"AppCommands.PlacePart: {a} → {target.Source}, anchor {target.Anchor.ToString("F3")} (root space)");
            string site = AirTools.Scene.SiteScope.Current;   // sitescope: the part is for this site
            loader.LoadForPlacement(a.PartId, a.ModelUrl, a.Name, part =>
            {
                if (part == null)
                {
                    UiToast.Show(Copy.Error(ErrorSurface.PartLoad, loader.LastError), ColorRole.Warning);
                    Log.Warn($"AppCommands.PlacePart: {a.PartId ?? a.ModelUrl} didn't load: {loader.LastError}");
                    return;
                }
                if (!AirTools.Scene.SiteScope.IsCurrent(site))   // sitescope: another model is loaded now; it doesn't go there
                {
                    part.gameObject.SetActive(false);
                    Object.Destroy(part.gameObject);
                    Log.Warn($"AppCommands.PlacePart: {a.PartId ?? a.ModelUrl} loaded after {site} was left; not placed on {AirTools.Scene.SiteScope.Current}");
                    return;
                }
                // The scene may have been re-scaled while it loaded: work the pose out again from the package numbers.
                if (!TryPlaceTarget(a, root, out var now, out _)) now = target;
                var position = PlacePartMath.OriginFor(now.Anchor, now.Rotation, PartMath.LocalBox(part.Spec), now.AtOrigin);
                // e2e: into a gap with no fit from the server → the part against the gap on all three axes.
                if (fit == null && now.ComponentId != null && AirTools.Scene.Gaps.TryGet(out var gap, now.ComponentId))
                    fit = CavityFit.Report(part.Spec.dims_mm, AirTools.Scene.Gaps.FitSizeM(gap) * 1000f);
                using (EditHistory.Step())   // delete-undo: the new model in and the one it replaces out are one Undo step
                {
                    tool.PlaceAt(part, position, now.Rotation, fit);
                    if (now.ComponentId != null) ModelCycler.Adopt(part, now.ComponentId);   // e2e: one model per gap (switching)
                }
                var shown = part.Fit;
                string name = Copy.Clip(string.IsNullOrEmpty(part.Spec.name) ? part.Spec.id : part.Spec.name, 28);
                UiToast.Show($"{name} · {Copy.FitLine(shown)}".Trim(' ', '·'),
                    shown == null ? ColorRole.Info : shown.Status == FitStatus.Red ? ColorRole.Danger : shown.Status == FitStatus.Amber ? ColorRole.Warning : ColorRole.Success);
            });
            return true;
        }

        /// Where place_part puts a part, in SceneRoot space: the anchor (front-bottom-centre, or the model's origin), the
        /// upright rotation (+Z out, +Y up) and what it came from.
        public struct PlaceTarget
        {
            public Vector3 Anchor;
            public Quaternion Rotation;
            public bool AtOrigin;
            public string Source;
            /// e2e: the component whose gap it goes into (the cavity insert, or a pose with component_id); null otherwise.
            public string ComponentId;
        }

        public static bool TryPlaceTarget(PlacePartArgs a, AirTools.Scene.SceneRoot root, out PlaceTarget target, out string why)
        {
            target = default;
            why = null;
            var toRoot = root.Content.transform.localRotation;   // package → SceneRoot directions (the scale is uniform)
            var parts = Services.Get<AirTools.Scene.SceneParts>();
            var comp = parts == null ? null : !string.IsNullOrEmpty(a.ComponentId) ? parts.Find(a.ComponentId) : parts.LastRemoved;
            bool hasBox = AirTools.Scene.CavityBox.TryFrom(comp, out var box, out _);
            if (a.Pose.Valid)
            {
                var anchor = root.PackageToRoot(a.Pose.Position);
                Vector3 outward;
                if (a.Pose.HasRotation) outward = toRoot * (a.Pose.Rotation * Vector3.forward);
                else if (hasBox) outward = toRoot * box.D;
                else
                {
                    var cam = Camera.main;
                    outward = cam != null ? root.transform.InverseTransformPoint(cam.transform.position) - anchor : Vector3.forward;
                }
                target = new PlaceTarget { Anchor = anchor, Rotation = PlacePartMath.UprightFacing(outward, Vector3.up), AtOrigin = a.Pose.AtOrigin, Source = "pose",
                    ComponentId = hasBox && !string.IsNullOrEmpty(a.ComponentId) ? comp.id : null };   // e2e
                return true;
            }
            if (!hasBox)
            {
                why = comp == null ? "Take a part out first, or say where it goes" : $"The {comp.DisplayName.ToLowerInvariant()} has no gap to fill";
                return false;
            }
            target = new PlaceTarget
            {
                Anchor = root.PackageToRoot(box.P), Rotation = PlacePartMath.UprightFacing(toRoot * box.D, Vector3.up), AtOrigin = false,
                Source = $"cavity {comp.id} insert",
                ComponentId = comp.id,   // e2e
            };
            return true;
        }

        // ---------------- e2e: take out → measure the gap → find one that fits → put it in → switch models ----------------
        // (docs/demo-prompts.md.) Voice: the e2e backend patch sends remove_component / measure_cavity / search_started /
        // place_part / cycle_model; when a reply has none of them, LocalIntents calls these same commands on the headset.
        // "Next one" / "option 2" are the placement editor's NextPlacedModel / ShowPlacedModel (below; ModelCycler when the
        // scene has no PlacementEditor). PlaceBestInGap puts the first model in the gap; place_part's swap is ModelCycler.Adopt.

        /// "Measure the gap": the real tape across the removed part's cavity — height, depth, then width (the latest
        /// tape is the width the parts search fits against) — saved to the notebook; one Undo takes all three off.
        public static bool MeasureCavity(string idOrLabel = null)
        {
            if (!AirTools.Scene.Gaps.TryGet(out var gap, idOrLabel))
            {
                UiToast.Show(Copy.NoGap, ColorRole.Warning);
                Log.Warn($"AppCommands.MeasureCavity: no gap open{(idOrLabel != null ? $" for '{idOrLabel}'" : "")}");
                return false;
            }
            if (AppState.Mode == AppMode.Passthrough) OpenChest();
            int n = AirTools.Scene.CavityTapes.Run(gap, out _, out _);
            if (n == 0) { UiToast.Show(Copy.GapNotTaped, ColorRole.Warning); return false; }
            var size = AirTools.Scene.Gaps.FitSizeM(gap);   // the tapes' readings (the file's where one didn't take)
            UiToast.Show(Copy.GapMeasured(gap.Noun, size.x, size.y, size.z), ColorRole.Success);
            return true;
        }

        /// "The opening is 34½ inches tall" / "the gap is 24 inches wide": the scene's scale from the gap's tape on that axis
        /// ("w" / "h" / "d"; tapes the gap first when it hasn't been) — ScaleCalibration, as the Scene window's Set scale
        /// on that tape. The heads-up line says what was assumed: "Scale set from the gap's height · 34½″ · ×1.48".
        public static bool ScaleFromGap(string axis, double realMetres)
        {
            if (!AirTools.Scene.Gaps.TryGet(out var gap)) { UiToast.Show(Copy.NoGap, ColorRole.Warning); return false; }
            axis = (axis ?? "h").Trim().ToLowerInvariant();
            axis = axis.StartsWith("w") ? "w" : axis.StartsWith("d") ? "d" : "h";
            var tape = AirTools.Scene.Gaps.TapeOf(gap.Id, axis);
            if (tape == null && MeasureCavity(gap.Id)) tape = AirTools.Scene.Gaps.TapeOf(gap.Id, axis);
            string word = AirTools.Scene.CavityTapes.AxisWord(axis);
            if (tape == null) { UiToast.Show($"Tape the gap's {word} first", ColorRole.Warning); return false; }
            var root = Services.Get<AirTools.Scene.SceneRoot>();
            float before = root != null ? root.Calibration : 1f;
            if (!AirTools.Structure.ScaleCalibration.Apply(realMetres, tape))
            {
                UiToast.Show($"Scale not set · {AirTools.Structure.ScaleCalibration.LastResult}", ColorRole.Warning);
                return false;
            }
            float factor = root != null && before > 0f ? root.Calibration / before : 1f;
            UiToast.Show(Copy.GapScale(word, realMetres, factor), ColorRole.Success);
            Log.Info($"AppCommands.ScaleFromGap: {gap.Id} {word} = {realMetres:0.0000} m → {AirTools.Structure.ScaleCalibration.LastResult}");
            return true;
        }

        /// "Find one that fits": a parts search for `query`, else the removed part's label ("dishwasher"); it goes out with
        /// the gap's W × H × D (context `cavity`, search body `cavity`) and the width tape as the measurement.
        public static bool FindForGap(string query = null)
        {
            string q = !string.IsNullOrWhiteSpace(query) ? query.Trim() : AirTools.Scene.Gaps.TryGet(out var gap) ? gap.Noun : null;
            if (string.IsNullOrWhiteSpace(q)) { UiToast.Show("Which part? Say its name", ColorRole.Warning); return false; }
            return FindPart(q);
        }

        /// "Put it in there": the best Find parts candidate for the open gap (the first that fits it, else the one that
        /// overruns least) loads and stands in it, with its fit.
        public static bool PlaceBestInGap() => ModelCycler.PlaceBest();

        // ---------------- end e2e ----------------

        // placement -----------------------------------------------------------------------------------------------------
        // The placement editor (Parts/PlacementEditor): adjust a placed part in 6 DoF, save its placements (A–D) and swap
        // its model in place. The Adjust chip, the adjust panel, voice (adjust_placement / save_placement / load_placement /
        // cycle_model, Agent/PlacementActions) and the harness all come here. None of them pays.

        static PlacementEditor Placement => PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();

        /// Adjust mode on (the selected placed part, else the last placed) or off (the session becomes one undo step).
        public static bool AdjustPlacement(bool on)
        {
            var e = Placement;
            if (e == null) { Log.Warn("AppCommands.AdjustPlacement: no PlacementEditor in scene"); return false; }
            return on ? e.Enter() : e.Exit() || true;
        }

        /// The Adjust chip: on if off, off if on.
        public static bool ToggleAdjust() => Placement is PlacementEditor e && e.Toggle();

        /// Move the part by (right, up, out) millimetres in its frame (the cavity's, the surface's, or up + your facing)
        /// and turn it by turn / tilt / roll degrees about its anchor (turn: clockwise seen from above; tilt: the top away
        /// from you; roll: clockwise as you face it). One undo step, or part of the adjust session.
        public static bool NudgePlacement(float dxMm, float dyMm, float dzMm, float yawDeg = 0f, float pitchDeg = 0f, float rollDeg = 0f) =>
            Placement is PlacementEditor e && e.Nudge(new Vector3(dxMm, dyMm, dzMm) * 0.001f, new Vector3(yawDeg, pitchDeg, rollDeg));

        /// Save the placement in `slot` ("A"–"D"; null: the next free one). A notebook row and an undo step.
        public static bool SavePlacement(string slot = null) => Placement is PlacementEditor e && e.SavePlacement(slot);

        /// Move the part to its saved placement `slot`.
        public static bool LoadPlacement(string slot) => Placement is PlacementEditor e && e.LoadPlacement(slot);

        /// Back to the auto-placement pose (the cavity's insert, or where it was placed).
        public static bool ResetPlacement() => Placement is PlacementEditor e && e.ResetToFit();

        /// The next (+1) / previous (−1) candidate's model at the placed part's placement, anchor on anchor (a cavity:
        /// front-bottom-centre; elsewhere bottom-back-centre); with only a part in hand, the next candidate into the hand.
        /// True when the swap is done or loading (PlacementEditor.SwapBusy / LastSwap). Stable API (e2e lane).
        public static bool NextPlacedModel(int delta = 1) =>
            Placement is PlacementEditor e ? e.CycleModel(delta == 0 ? 1 : delta) : ModelCycler.Next(delta);   // e2e: ModelCycler without an editor

        /// Candidate i's (0-based, Find parts' order) model at the placed part's placement.
        public static bool ShowPlacedModel(int i) => Placement is PlacementEditor e ? e.ShowModel(i) : ModelCycler.Show(i);   // e2e: ditto
        // end placement -------------------------------------------------------------------------------------------------

        // edit6dof ------------------------------------------------------------------------------------------------------
        // The Edit view (Parts/EditView, docs/edit-view.md): a part's isolated edit — orient it with arrows, shade it,
        // move it — and a placed part's context menu (Edit / View similar / Delete). The card's Edit chip, voice
        // (adjust_placement on, edit_part, delete_part, view_similar) and the harness come here. None of it pays.

        /// The Edit view on a placed part (default: the selected one, else the last placed); without one in the scene, the
        /// adjust panel (AdjustPlacement).
        public static bool EditPart(PartInstance part = null) =>
            Services.TryGet<EditView>(out var view) ? view.OpenPlaced(part) : AdjustPlacement(true);

        /// Save / Cancel the Edit view (a placed part: its edits, one undo step / back as it was).
        public static bool SaveEdit() => Services.TryGet<EditView>(out var view) && view.Save();
        public static bool CancelEdit() => Services.TryGet<EditView>(out var view) && view.Cancel();

        /// Delete a placed part (undoable: Undo brings it back where it was). Default: the selected one.
        public static bool DeletePart(PartInstance part = null)
        {
            if (!Services.TryGet<PartTool>(out var tool)) return false;
            part = part != null ? part : tool.Selected;
            if (part == null || !part.Placed) return false;
            if (Services.TryGet<EditView>(out var view)) return view.Delete(part);
            // delete-undo: no Edit view in the scene: the same step and the same words.
            string noun = Copy.Noun(part.Spec, part.SearchQuery);
            if (!tool.Delete(part)) return false;
            UiToast.Show(Copy.PartRemoved(noun), ColorRole.Info);
            return true;
        }

        // delete-undo ---------------------------------------------------------------------------------------------------
        /// The spec card's Remove (tap twice): the selected placed part goes as one undo step — Undo on the ring (or the
        /// "Deleted · Undo" chip where it stood) brings it back with its pose, finish, fit, selection and notebook row. A
        /// part in hand goes back to its Find parts card instead (Take it again). False with nothing selected.
        public static bool RemoveSelectedPart()
        {
            if (!Services.TryGet<PartTool>(out var tool) || tool.Selected == null) return false;
            var part = tool.Selected;
            if (part == tool.Held) return tool.PutBack();
            return DeletePart(part);
        }

        /// Clear every placed part as one undo step ("3 parts cleared · Undo on the ring"); how many went.
        public static int ClearParts() => Services.TryGet<PartTool>(out var tool) ? tool.RemoveAll() : 0;
        // end delete-undo -----------------------------------------------------------------------------------------------

        /// The Catalog on parts like this one (its kind; Fits on when it's in a gap).
        public static bool ViewSimilar(PartInstance part = null)
        {
            if (!Services.TryGet<PartTool>(out var tool)) return false;
            part = part != null ? part : tool.Selected;
            return part != null && Services.TryGet<EditView>(out var view) && view.ViewSimilar(part);
        }
        // end edit6dof --------------------------------------------------------------------------------------------------

        // assetgen ------------------------------------------------------------------------------------------------------
        // Size & finish (PlacementEditor.Look): the adjust panel's section, and the same for voice and the harness. The part
        // changes size at once (stretched: "resized"); a parametric part from the server comes back made to that size.
        // One undo step each. None of them pays.

        /// The placed part at width × height × depth millimetres (clamped to 0.5–2× the listing).
        public static bool ResizePlacement(float wMm, float hMm, float dMm) =>
            Placement is PlacementEditor e && e.SetSize(new Vector3(wMm, hMm, dMm), "resize");

        /// One step bigger (+1) or smaller (−1) on "w", "h" or "d".
        public static bool StepPlacementSize(string axis, int dir)
        {
            string a = (axis ?? "w").Trim().ToLowerInvariant();
            int i = a.StartsWith("h") ? 1 : a.StartsWith("d") ? 2 : 0;
            return Placement is PlacementEditor e && e.StepSize(i, dir);
        }

        /// "Fit to opening": sized to the taped opening less ¼″ each side, and into it.
        public static bool FitPlacementToOpening() => Placement is PlacementEditor e && e.FitToOpening();

        /// A finish on the placed part (null or its listed one: as listed): the server's re-textured model when it renders
        /// it, else a tint (never the glass).
        public static bool SetPlacementFinish(string name) => Placement is PlacementEditor e && e.SetLookFinish(name);
        // end assetgen --------------------------------------------------------------------------------------------------

        static bool ScenePartsFor(string idOrLabel, out AirTools.Scene.SceneParts parts)
        {
            if (!Services.TryGet(out parts) || !parts.HasParts)
            {
                UiToast.Show("Nothing in this scene comes out", ColorRole.Warning);
                Log.Warn($"AppCommands: no removable scene parts for '{idOrLabel}'");
                return false;
            }
            var c = parts.Find(idOrLabel);
            if (c == null || !c.removable)
            {
                UiToast.Show(string.IsNullOrWhiteSpace(idOrLabel) ? "Which part? Say its name" : $"No {idOrLabel.Replace('_', ' ')} to take out here", ColorRole.Warning);
                Log.Warn($"AppCommands: no removable scene part '{idOrLabel}' (have {string.Join(", ", parts.Removable.Select(x => x.id))})");
                return false;
            }
            return true;
        }
    }
}
