#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Dev
{
    // catalog: the Catalog in the running app, through the real paths (the ring's item, the keyboard's own key buttons, a
    // card's button, the part tool). unity command eval --code 'return AirTools.Dev.AgentHarness.CatalogCheck();' then poll
    // AgentHarness.CatalogResult(). Results are [AirTools.Check] catalog.* lines.
    public static partial class AgentHarness
    {
        static CatalogWindow CatalogW => Services.Get<CatalogWindow>();

        /// Open (the ring's path: AppCommands.ToggleCatalog's open) or close the Catalog.
        public static string Catalog(bool open)
        {
            if (open) AppCommands.ShowCatalog(); else AppCommands.HideCatalog();
            return CatalogState();
        }

        /// show_catalog's path: a category and / or a query typed in.
        public static string CatalogShow(string category, string query = null)
        {
            AppCommands.ShowCatalog(category, query);
            return CatalogState();
        }

        /// One line: open, site, where the catalog came from, mode, category, text, page, the cards, Fits, keyboard, the
        /// laptop's half of the search.
        public static string CatalogState()
        {
            var w = CatalogW;
            if (w == null) return "no CatalogWindow";
            var m = w.Model;
            var sb = new StringBuilder();
            sb.Append($"open={w.IsOpen} showing={w.Showing} site={m.Site} env=\"{m.Environment}\" source={m.Source} fetches={w.Fetches}");
            if (w.LastFetchError != null) sb.Append($" fetchError=\"{w.LastFetchError}\"");
            sb.Append($" categories={m.CategoryCount} [{string.Join(", ", (m.Data?.categories ?? new List<CatalogCategory>()).Select(c => $"{c.Title}:{c.items.Count}"))}]");
            sb.Append($" mode={m.Mode} category={m.CurrentCategory?.id ?? "-"} query=\"{m.Field.Text}\" page={m.Page + 1}/{m.PageCount} visible={m.Visible.Count} local={m.LocalCount}");
            sb.Append($" server={m.Server}(+{m.ServerAdded}, gen {m.Debounce.Generation}, sent {m.Debounce.Sent}, replies {w.ServerReplies}, stale {w.StaleReplies})");
            sb.Append($" fits={(m.Fit.Available ? m.Fit.Label(m.LengthUnits) : "-")}{(m.FitsOn ? " on" : "")} keyboard={(w.keyboard != null && w.keyboard.IsOpen)} picked={m.Picked ?? "-"}");
            sb.Append($" status=\"{(w.status != null ? w.status.text : "")}\"");
            sb.Append($" cards=[{string.Join(" | ", Enumerable.Range(0, CatalogModel.CardsPerPage).Select(i => m.PageItem(i)).Where(h => h != null).Select(h => $"{h.Id} {CatalogText.Price(h.Summary.price_usd)}{(h.ModelReady ? " 3D" : "")}{(h.FromServer ? " (laptop)" : "")}"))}]");
            return sb.ToString();
        }

        /// Type `text` on the glass keyboard, one key button press every `interval` s (opens the keyboard first by pressing
        /// the search field). Async: poll CatalogState().
        public static string CatalogType(string text, float interval = 0.13f)
        {
            var w = CatalogW;
            if (w == null) return "no CatalogWindow";
            if (!Application.isPlaying) return "Play mode only";
            if (!w.IsOpen) AppCommands.ShowCatalog();
            w.StartCoroutine(TypeRoutine(w, text ?? "", interval, null));
            return $"typing \"{text}\" on the keyboard… | {CatalogState()}";
        }

        /// One key ("a", "space", "back", "clear", "enter") through its button.
        public static string CatalogKey(string key)
        {
            var w = CatalogW;
            if (w == null) return "no CatalogWindow";
            var b = w.keyboard != null ? w.keyboard.Find(key) : null;
            if (b != null && b.isActiveAndEnabled) b.Press(); else w.Key(key);
            return CatalogState();
        }

        /// Press card `i` on the page (its glass button: the same path as a poke or a ray pinch).
        public static string CatalogPick(int i)
        {
            var w = CatalogW;
            if (w == null) return "no CatalogWindow";
            if (i < 0 || i >= w.cards.Length || w.cards[i] == null || w.cards[i].Hit == null) return $"no card {i} | {CatalogState()}";
            bool ok = w.cards[i].button != null && w.cards[i].button.isActiveAndEnabled ? w.cards[i].button.Press() : w.Pick(i);
            return $"{(ok ? "took" : "refused")} {w.cards[i].ItemId} | {Parts()}";
        }

        /// Answer /catalog and /catalog/search from Assets/AirTools/Fixtures/Catalog (until catalog-backend is on the
        /// server) — on — or from the server again — off; then asks again for the loaded site.
        public static string CatalogFixture(bool on)
        {
            var w = CatalogW;
            var c = Services.Get<CatalogClient>();
            if (w == null || c == null) return "no CatalogWindow / CatalogClient";
            c.testTransport = on ? CatalogFixtures.FromProject : null;
            w.Fetch(w.Site, force: true);
            return $"catalog answers from {(on ? "the fixtures" : ServerConfig.Current)} | {CatalogState()}";
        }

        // ---------------- CatalogCheck ----------------

        static readonly List<string> s_CatalogLog = new List<string>();
        static bool s_CatalogDone, s_CatalogPass;
        static int s_CatalogPassN, s_CatalogTotal;
        static string s_CatalogSummary = "not run";

        /// The Catalog end to end (Play mode; poll CatalogResult()): a clean start with no tape (`reset`: a demo reset
        /// first) → open it from the ring → the site's categories (from the laptop; `fixture`: from the fixtures) → type
        /// `query` on the keyboard (default by site: "dishw" in the kitchen) → local results at once, the laptop's after the
        /// debounce → pick the first card → the part in hand → place it where the eye ray meets the scene.
        public static string CatalogCheck(bool reset = true, bool fixture = false, string query = null)
        {
            if (!Application.isPlaying) return "Play mode only";
            s_CatalogLog.Clear(); s_CatalogDone = s_CatalogPass = false; s_CatalogPassN = s_CatalogTotal = 0; s_CatalogSummary = "running…";
            if (!DemoRunner.Run(CatalogFlow(reset, fixture, query), ex => { s_CatalogLog.Add($"exception: {ex}"); s_CatalogSummary = $"exception: {ex.Message}"; },
                    () => s_CatalogDone = true))
                return "another routine is running (DemoRunner busy)";
            return $"catalog check started (reset={reset}, fixture={fixture}) against {ServerConfig.Current} — poll AgentHarness.CatalogResult()";
        }

        public static string CatalogResult() =>
            $"{(s_CatalogDone ? (s_CatalogPass ? "PASS" : "FAIL") : "running…")} {s_CatalogSummary}\n{string.Join("\n", s_CatalogLog)}";

        static void CatalogCheckLine(string id, bool ok, string detail)
        {
            s_CatalogTotal++;
            if (ok) s_CatalogPassN++;
            Log.Check(id, ok, detail);
            s_CatalogLog.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static float CNow => Time.realtimeSinceStartup;

        static IEnumerator CatalogWait(System.Func<bool> cond, float timeout)
        {
            float t0 = CNow;
            while (!cond() && CNow - t0 < timeout) yield return null;
        }

        static IEnumerator TypeRoutine(CatalogWindow w, string text, float interval, List<float> keyTimes)
        {
            if (!w.Model.KeyboardOpen)
            {
                if (w.field != null && w.field.isActiveAndEnabled) w.field.Press(); else w.ToggleKeyboard();
                yield return null;
                yield return null;   // rendered: the keys are live (their CatalogButtons subscribed)
            }
            foreach (char ch in text)
            {
                string key = ch == ' ' ? CatalogTextField.Space : char.ToLowerInvariant(ch).ToString();
                var b = w.keyboard != null ? w.keyboard.Find(key) : null;
                if (b != null && b.isActiveAndEnabled) b.Press(); else w.Key(key);
                keyTimes?.Add(CNow);
                yield return new WaitForSecondsRealtime(interval);
            }
        }

        static string DefaultQuery(string site) => CatalogFallback.KindOf(site) switch
        {
            SiteKind.Kitchen => "dishw",
            SiteKind.Roof => "gutt",
            _ => "hang",
        };

        static IEnumerator CatalogFlow(bool reset, bool fixture, string query)
        {
            var w = CatalogW;
            var tools = Tools;
            if (w == null || tools == null || Browser == null || PartTool == null) { s_CatalogSummary = "no CatalogWindow / ToolManager / PartsBrowser / PartTool"; yield break; }
            var client = Services.Get<CatalogClient>();
            if (client != null) client.testTransport = fixture ? CatalogFixtures.FromProject : null;
            int answered0 = w.FetchesAnswered;
            w.Fetch(w.Site, force: true);   // this run's source, now

            // 1. A clean start: no tape anywhere (a demo reset), in the world.
            if (reset)
            {
                AppCommands.ResetDemo();
                yield return null;
            }
            if (AppState.Mode != AppMode.World) AppCommands.OpenChest();
            yield return CatalogWait(() => AppState.Mode == AppMode.World, 8f);
            yield return new WaitForSecondsRealtime(1f);
            int tapes = Notebook.Entries.Count(e => e.Tool == "measure");
            if (w.IsOpen) AppCommands.HideCatalog();
            yield return null;
            CatalogCheckLine("catalog.no_tape", !reset || tapes == 0, $"tapes={tapes} reset={reset} mode={AppState.Mode}");

            // 2. Open it from the ring: spin to Catalog, pinch the lens (the palm menu forced open: hands can't be simulated).
            var palm = Services.Get<PalmMenu>();
            var ring = palm != null ? palm.ring : null;
            int iCatalog = ring != null ? System.Array.FindIndex(ring.items, it => it.label == "Catalog") : -1;
            string via;
            if (ring != null && iCatalog >= 0)
            {
                palm.Force(true);
                yield return CatalogWait(() => palm.AcceptingPresses && ring.isActiveAndEnabled, 2f);
                ring.SpinTo(iCatalog);
                yield return CatalogWait(() => ring.Dial.Settled, 3f);
                yield return new WaitForSecondsRealtime(ring.settleDelay + 0.1f);
                ring.Tap(ring.ItemPos(ring.Dial.ItemAngle(ring.Selected)));   // the lens: Catalog
                via = $"ring (lens={ring.items[ring.Selected].label}, \"{ring.LastAction}\")";
                palm.Force(null);
            }
            else
            {
                ToolboxButton.Run(ToolboxAction.ToggleCatalog);
                via = "ToolboxAction.ToggleCatalog (no ring)";
            }
            yield return null;
            yield return null;
            CatalogCheckLine("catalog.open", w.IsOpen && w.Showing, $"via {via} open={w.IsOpen} showing={w.Showing} tapes={tapes}");

            // 3. The site's categories, from the laptop (or the fixtures).
            yield return CatalogWait(() => w.Model.Source == CatalogSource.Server || w.FetchesAnswered > answered0, 10f);
            var m = w.Model;
            string cats = string.Join(", ", (m.Data?.categories ?? new List<CatalogCategory>()).Select(c => $"{c.Title}:{c.items.Count}"));
            CatalogCheckLine("catalog.categories", m.CategoryCount > 0, $"site={m.Site} env=\"{m.Environment}\" source={m.Source} [{cats}]");
            CatalogCheckLine("catalog.dynamic", m.Source == CatalogSource.Server,
                m.Source == CatalogSource.Server ? $"GET /catalog?site={m.Site} answered ({(fixture ? "fixtures" : ServerConfig.Current)})"
                                                 : $"no /catalog from {ServerConfig.Current} ({w.LastFetchError}); showing {m.Source} — try CatalogCheck(fixture: true)");

            // 4. Type on the keyboard: local results at once, the laptop's after the pause.
            string q = string.IsNullOrWhiteSpace(query) ? DefaultQuery(m.Site) : query.Trim().ToLowerInvariant();
            var keys = new List<float>();
            int replies0 = w.ServerReplies;
            yield return TypeRoutine(w, q, 0.13f, keys);
            float lastKey = keys.Count > 0 ? keys[keys.Count - 1] : CNow;
            int localNow = m.LocalCount;
            var serverNow = m.Server;
            CatalogCheckLine("catalog.local", m.Mode == CatalogMode.Search && m.Field.Text == q && localNow > 0 && serverNow != CatalogServerState.Answered,
                $"typed \"{m.Field.Text}\" by {keys.Count} key presses; {localNow} local results ~0.13 s after the last key, laptop {serverNow} | status \"{w.status?.text}\"");
            float asked = -1f;
            float t0 = CNow;
            while (CNow - t0 < 8f && m.Server != CatalogServerState.Answered && m.Server != CatalogServerState.Failed)
            {
                if (asked < 0f && m.Server == CatalogServerState.Asking) asked = CNow;
                yield return null;
            }
            if (asked < 0f && m.Debounce.Sent > 0) asked = CNow;   // answered within the frame it was sent (the fixtures)
            float waited = asked >= 0f ? asked - lastKey : -1f;
            CatalogCheckLine("catalog.server", m.Server == CatalogServerState.Answered && w.ServerReplies > replies0 && waited >= w.debounceSeconds - 0.1f,
                $"laptop {m.Server} +{m.ServerAdded} (asked {waited:0.00} s after the last key; debounce {w.debounceSeconds} s) sent gen {m.Debounce.Sent} of {m.Debounce.Generation}, stale {w.StaleReplies}");

            // 5. Pick the first card (its button) → the part in the hand.
            var first = w.cards.Length > 0 ? w.cards[0] : null;
            string id = first != null ? first.ItemId : null;
            bool pressed = first != null && first.button != null && first.button.isActiveAndEnabled && first.button.Press();
            yield return null;
            yield return CatalogWait(() => !Browser.Loading, 60f);   // the part.json, then the model (an AI mesh may take a while)
            // edit6dof: a Catalog take opens the Edit view first (orient and colour it); its Place puts it in the hand.
            var edit = AirTools.Parts.EditView.Current;
            if (edit != null && edit.Active && edit.Kind == AirTools.Parts.EditKind.New)
            {
                yield return CatalogWait(() => edit.Phase == AirTools.Parts.EditPhase.Orient, 3f);
                CatalogCheckLine("catalog.edit_view", edit.Place(), $"the Edit view opened for {edit.Part?.Spec.id ?? "-"}, then Place | {edit.Report()}");
                yield return null;
            }
            var held = PartTool.Held;
            CatalogCheckLine("catalog.take", pressed && held != null && held.Spec.id == id,
                $"card0={id ?? "-"} pressed={pressed} held={held?.Spec.id ?? "-"} source={held?.Source ?? "-"} tool={tools.Active} status=\"{Browser.Status}\"");
            if (held == null) { Finish(); yield break; }

            // 6. Place it where the eye's ray, 35° down, meets the scene.
            var cam = Camera.main;
            bool placed = false;
            string where = "no surface in view";
            if (cam != null && Hub != null)
            {
                var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                int mask = AirTools.Scene.SceneLayers.SceneSurfaceMask;   // the scan, never a part
                // Play Without XR leaves the camera at the rig's feet (no head tracking): aim from a standing eye instead.
                var eye = cam.transform.position + (cam.transform.localPosition.y < 0.5f ? Vector3.up * 1.6f : Vector3.zero);
                // 35° down first; a scan that doesn't reach the floor at your feet (the kitchen covers the counter side only)
                // gets shallower rays until one lands on it.
                RaycastHit hit = default;
                bool any = false;
                foreach (float down in new[] { 35f, 28f, 22f, 16f, 10f })
                {
                    var dir = Quaternion.AngleAxis(down, Vector3.Cross(Vector3.up, fwd)) * fwd;
                    if (Physics.Raycast(eye, dir, out hit, 8f, mask, QueryTriggerInteraction.Ignore)) { any = true; break; }
                }
                if (any)
                {
                    var pose = ToolInputHub.RayPose(eye, hit.point);
                    Hub.SetPointerOverride(ToolHand.Right, pose);
                    yield return new WaitForSecondsRealtime(0.3f);
                    Hub.RaisePressStart(ToolHand.Right, pose);
                    Hub.RaisePressEnd(ToolHand.Right, pose);
                    Hub.SetPointerOverride(ToolHand.Right, null);
                    yield return null;
                    placed = held.Placed;
                    where = $"{hit.collider.name} at {F(ToRoot(hit.point))} fit={(held.Fit != null ? held.Fit.Status.ToString() : "-")}";
                }
            }
            CatalogCheckLine("catalog.place", placed, $"{held.Spec.id} placed={placed} on {where} last=\"{PartTool.LastAction}\"");
            Finish();

            void Finish()
            {
                s_CatalogPass = s_CatalogPassN == s_CatalogTotal;
                s_CatalogSummary = $"catalog: {s_CatalogPassN}/{s_CatalogTotal} | {CatalogState()}";
                Log.Check("catalog.check", s_CatalogPass, s_CatalogSummary);
            }
        }
    }
}
#endif
