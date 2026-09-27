#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AirTools.Agent;
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
    /// assetgen: the whole "measure a window, find a frame that fits, generate it, put it in, make it to size" flow
    /// against a live backend, through the real paths (the Measure tool on the scene, AgentClient.SendText → /agent/command
    /// and LocalIntents, the parts search, the server's asset pipeline, the part tool's held preview and release, the
    /// placement editor's buttons). Each step waits (with a timeout) and logs one [AirTools.Check] line:
    ///   asset.measure        the window's height then width taped (seeded aim noise); the opening they frame ≈ the truth
    ///                        (the built-in facade: 1.500 × 1.200 m at (0, 4.1, 0), facing +Z; a scan window: its rect)
    ///   asset.search         "find a window frame that fits" → candidates (each with a model URL)
    ///   asset.search.opening the opening's W × H went with it (context.opening; the search body when the headset searched)
    ///   asset.generate       the best-sized candidate's model built by the server's pipeline: tier, template, who made
    ///                        it (Groq, or Grok when Groq is rate-limited or answered poorly), server and wait seconds
    ///   asset.place.free     held over the wall away from the opening: a free placement, not pulled in
    ///   asset.place          released at the opening: snapped when it fits properly (front-face centre on the opening's
    ///                        centre ±1 cm, square ±1°, fit green / amber); a stock size that doesn't fit stays put, red
    ///   asset.size.step      Adjust ▸ Size & finish ▸ W +: one step wider, the model stretched, the fit re-checked
    ///   asset.size.fit       Fit to opening: the opening less ¼″ each side, snapped in, "✓ Fits the opening · ½″ spare"
    ///   asset.size.model     the made-to-size model from the server (POST /parts/{id}/resize) swapped in
    ///   asset.finish         a finish chip: the server's re-textured model (Imagine) or a tint; the model changed
    ///   asset.save           Save placement: a placement_pose row with the size and finish
    ///   asset.undo / .redo   Undo ×3 (save, finish, fit) back to the stepped size; Redo ×3 forward again
    /// and asset.summary. Run: AgentHarness.AssetE2E(site) then poll AgentHarness.AssetE2EResult(). It leaves the frame in
    /// the opening in adjust mode with the panel open (the Size & finish capture).
    public static class AssetE2E
    {
        public const float ReplyTimeout = 45f, SiteTimeout = 90f, SearchTimeout = 150f, ModelTimeout = 120f, LoadTimeout = 90f, WorldTimeout = 8f;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static float Now => Time.realtimeSinceStartup;

        static readonly List<string> s_Log = new List<string>();
        static bool s_Done, s_Pass;
        static int s_PassN, s_Total;
        static string s_Summary = "not run";
        static string s_Via = "-";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Log.Clear(); s_Done = s_Pass = false; s_PassN = s_Total = 0; s_Summary = "not run"; }

        public class Options
        {
            /// The scene package (null: the one loaded; "built-in": the synthetic facade baked into the app).
            public string Site;
            /// A scan's window: a structure object id ("o7") or "auto" (the house-window-sized rect nearest you); null: the
            /// built-in facade's.
            public string Window;
            public string Phrase = "find a window frame that fits";
            public bool Reset = true;
            public int Seed = 1;
            /// Leave the frame in adjust mode with the panel open (the capture).
            public bool KeepAdjusting = true;
        }

        public static string Start(Options o)
        {
            if (!Application.isPlaying) return "Play mode only";
            s_Log.Clear(); s_Done = s_Pass = false; s_PassN = s_Total = 0; s_Summary = "running…";
            if (!DemoRunner.Run(Flow(o ?? new Options(), (ok, d) => { s_Pass = ok; s_Summary = d; }), ex => { s_Log.Add($"exception: {ex}"); s_Summary = $"exception: {ex.Message}"; },
                    () => s_Done = true))
                return "another routine is running (DemoRunner busy)";
            return $"asset e2e started ({o?.Site ?? "loaded site"}) against {ServerConfig.Current} — poll AgentHarness.AssetE2EResult()";
        }

        public static string Result() => $"{(s_Done ? (s_Pass ? "PASS" : "FAIL") : "running…")} {s_Summary}\n{string.Join("\n", s_Log)}";

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_PassN++;
            Log.Check(id, ok, detail);
            s_Log.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static string Summary(string site)
        {
            string s = $"asset e2e {s_PassN}/{s_Total} passed on {site}";
            Log.Check("asset.summary", s_PassN == s_Total && s_Total > 0, $"passed={s_PassN} total={s_Total} site={site}");
            return s;
        }

        /// The window to tape (SceneRoot space): its four corners (bottom-left, bottom-right, top-right, top-left as you
        /// face it), the eye to tape from, and whether its true size is known.
        struct Window
        {
            public Vector3 BL, BR, TR, TL, Eye, Out;
            public bool Truth;
            public string Name;
            public Vector3 Centre => (BL + BR + TR + TL) * 0.25f;
            public float W => Vector3.Distance(BL, BR);
            public float H => Vector3.Distance(BR, TR);
        }

        static bool FindWindow(Options o, SceneRoot root, out Window w, out string why)
        {
            w = default; why = null;
            if (string.IsNullOrEmpty(o.Window))
            {
                // The built-in facade's window: 1.500 × 1.200 m, sill top 3.5 m, wall face z = 0, facing +Z.
                float hw = S.WindowWidth / 2f, y0 = S.SillHeight, y1 = S.SillHeight + S.WindowHeight;
                w = new Window
                {
                    BL = new Vector3(hw, y0, 0f), BR = new Vector3(-hw, y0, 0f), TR = new Vector3(-hw, y1, 0f), TL = new Vector3(hw, y1, 0f),
                    Eye = MeasureScenarios.SpawnEye, Out = Vector3.forward, Truth = true, Name = "facade window",
                };
                return true;
            }
            var layer = root != null ? root.Structure : null;
            if (layer == null) { why = "no structure layer to find a window in"; return false; }
            // "auto": the house-window-sized one (0.4–1.8 m wide, 0.5–2.0 m tall) nearest you (zabel-gymnasium: o7, 0.79 × 1.27 m).
            StructureObject? pick = null;
            var camRoot = Camera.main != null ? root.transform.InverseTransformPoint(Camera.main.transform.position) : Vector3.zero;
            float bestD = float.MaxValue;
            foreach (var ob in layer.Objects)
            {
                if (!string.Equals(ob.label, "window", StringComparison.OrdinalIgnoreCase) || ob.corners == null || ob.corners.Length < 4) continue;
                if (o.Window != "auto") { if (ob.id == o.Window) { pick = ob; break; } continue; }
                float cal = root.Calibration;
                if (ob.widthM * cal < 0.4f || ob.widthM * cal > 1.8f || ob.heightM * cal < 0.5f || ob.heightM * cal > 2.0f) continue;
                var c0 = root.PackageToRoot((ob.corners[0] + ob.corners[2]) * 0.5f);
                float dist = Vector3.Distance(c0, camRoot);
                if (dist < bestD) { bestD = dist; pick = ob; }
            }
            if (pick == null) { why = $"no window '{o.Window}' in the structure layer"; return false; }
            var c = pick.Value.corners;
            var p = new Vector3[4];
            for (int i = 0; i < 4; i++) p[i] = root.PackageToRoot(c[i]);
            var centre = (p[0] + p[1] + p[2] + p[3]) * 0.25f;
            var n = Vector3.Cross(p[1] - p[0], p[3] - p[0]).normalized;
            var cam = Camera.main;
            var viewer = cam != null ? root.transform.InverseTransformPoint(cam.transform.position) : centre + n;
            if (Vector3.Dot(n, viewer - centre) < 0f) n = -n;
            // Order the corners as the viewer sees them: bottom two by height, left / right by the viewer's right.
            var right = Vector3.Cross(n, Vector3.up).normalized;
            var sorted = p.OrderBy(q => q.y).ToArray();
            var bottom = sorted.Take(2).OrderBy(q => Vector3.Dot(q, right)).ToArray();
            var top = sorted.Skip(2).OrderBy(q => Vector3.Dot(q, right)).ToArray();
            w = new Window
            {
                BL = bottom[0], BR = bottom[1], TR = top[1], TL = top[0], Eye = centre + n * 1.6f, Out = n, Truth = false,
                Name = $"scan window {pick.Value.id} ({pick.Value.widthM:0.00} × {pick.Value.heightM:0.00} m)",
            };
            return true;
        }

        /// The height tape (sill to head, 60 % across) and the width tape (jamb to jamb, half way up), each target on the
        /// surface it measures (the facade: 5 cm into the reveal), aimed with seeded noise along that surface.
        static List<MeasureScenario> Tapes(Window w)
        {
            var right = (w.BR - w.BL).normalized;
            var up = (w.TL - w.BL).normalized;
            float recess = w.Truth ? 0.05f : 0f;   // the facade's reveal; a scan's rect is on its plane
            var heightA = Vector3.Lerp(w.BL, w.BR, 0.6f) - w.Out * recess;
            var heightB = Vector3.Lerp(w.TL, w.TR, 0.6f) - w.Out * recess;
            var widthA = Vector3.Lerp(w.BL, w.TL, 0.5f) - w.Out * recess;
            var widthB = Vector3.Lerp(w.BR, w.TR, 0.5f) - w.Out * recess;
            float U(System.Random r, float a) => (float)(r.NextDouble() * 2 - 1) * a;
            var raised = w.Centre + w.Out * 2.5f;   // level with the window: the sill top and the head's underside both in view
            return new List<MeasureScenario>
            {
                new MeasureScenario
                {
                    Id = "asset.window.height", Description = $"{w.Name}: sill to head", RayOrigin = raised,
                    Targets = new[] { heightA, heightB },
                    Noise = (r, i) => right * U(r, 0.02f) + w.Out * U(r, 0.015f),
                    ExpectDistance = w.Truth ? w.H : (double?)null, DistanceTol = 0.010,
                },
                new MeasureScenario
                {
                    Id = "asset.window.width", Description = $"{w.Name}: jamb to jamb", RayOrigin = w.Eye,
                    Targets = new[] { widthA, widthB },
                    Noise = (r, i) => up * U(r, 0.02f) + w.Out * U(r, 0.015f),
                    ExpectDistance = w.Truth ? w.W : (double?)null, DistanceTol = 0.010,
                },
            };
        }

        static IEnumerator Flow(Options o, Action<bool, string> done)
        {
            var agent = Services.Get<AgentClient>();
            var browser = Services.Get<PartsBrowser>();
            var client = Services.Get<PartsClient>();
            var tool = Services.Get<PartTool>();
            var measure = Services.Get<MeasureTool>();
            var hub = Services.Get<ToolInputHub>();
            var editor = PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();
            if (agent == null || browser == null || client == null || tool == null || measure == null || hub == null || editor == null)
            { done(false, "missing AgentClient / PartsBrowser / PartsClient / PartTool / MeasureTool / ToolInputHub / PlacementEditor"); yield break; }
            s_Log.Add($"server {ServerConfig.Current} session {SessionInfo.Id}");

            // ---- 0. a clean start in the world, on the site
            if (o.Reset) { AppCommands.ResetDemo(); yield return null; }
            yield return EnsureWorld();
            var root = Services.Get<SceneRoot>();
            var streamer = Services.Get<SceneStreamer>();
            if (!string.IsNullOrEmpty(o.Site) && root != null && (o.Site == "built-in" ? root.IsRuntimePackage : root.Site != o.Site))
            {
                AppCommands.LoadSite(o.Site);
                float t0 = Now;
                yield return null;
                while (Now - t0 < SiteTimeout && (streamer == null || streamer.Loading || (o.Site != "built-in" && root.Site != o.Site))) yield return null;
                yield return EnsureWorld();
            }
            string site = root != null ? root.Site ?? "built-in" : "-";
            if (root == null) { done(false, "no SceneRoot"); yield break; }
            root.SetVisible(true);
            Physics.SyncTransforms();
            if (string.IsNullOrEmpty(o.Window) && !AirTools.Structure.SupportEdges.IsSyntheticFacade(root))
            { Check("asset.measure", false, $"{site} isn't the built-in facade: run AssetE2E(\"built-in\"), or name a scan window (\"auto\" / \"o7\")"); done(false, Summary(site)); yield break; }
            if (!FindWindow(o, root, out var win, out string whyNot)) { Check("asset.measure", false, whyNot); done(false, Summary(site)); yield break; }
            s_Log.Add($"site {site}: {win.Name} centre {V(win.Centre)} {win.W:0.000} × {win.H:0.000} m out {V(win.Out)}");

            // ---- 1. tape the window's height, then its width (the real Measure tool, simulated aim)
            AppCommands.EquipTool("measure");
            yield return null;
            var frame = tool.frame != null ? tool.frame : root.transform;
            var results = new List<ScenarioResult>();
            foreach (var sc in Tapes(win)) results.Add(MeasureScenarios.Run(sc, measure, hub, frame, o.Seed));
            Openings.Invalidate();
            bool opened = Openings.TryCurrent(out var opening);
            float cOff = opened ? Vector3.Distance(opening.Centre, win.Centre) : float.NaN;
            float nAng = opened ? Vector3.Angle(opening.Out, win.Out) : float.NaN;
            bool sizeOk = opened && (!win.Truth || (Mathf.Abs(opening.W - win.W) <= 0.01f && Mathf.Abs(opening.H - win.H) <= 0.01f));
            bool placeOk = opened && (!win.Truth || (cOff <= 0.02f && nAng <= 2f));
            Check("asset.measure", results.All(r => r.Passed) && sizeOk && placeOk,
                $"{string.Join("; ", results.Select(r => $"{r.Id} {(r.Passed ? "ok" : "FAIL")} {r.Detail}"))} | opening {(opened ? opening.ToString() : "none")} " +
                $"vs {(win.Truth ? "truth" : "rect")} {win.W:0.000} × {win.H:0.000} m, centre off {F(cOff * 1000f)} mm, normal off {F(nAng)}° | {Openings.Report()}");
            if (!opened) { done(false, Summary(site)); yield break; }

            // ---- 2. "find a window frame that fits"
            int searches = browser.SearchCount;
            string bodyBefore = client.LastSearchBody;
            yield return Say(o.Phrase);
            float s0 = Now;
            while (Now - s0 < SearchTimeout && (browser.Searching || browser.SearchCount == searches)) yield return null;
            var cands = browser.Candidates.ToList();
            var dims = cands.Select(c => c.spec?.dims_mm ?? c.dims_mm).ToList();
            Check("asset.search", cands.Count > 0 && cands.All(c => !string.IsNullOrEmpty(c.model_url)),
                $"N={cands.Count} from {browser.Source ?? "-"} in {(Now - s0).ToString("0.0", C)} s for the {Mm(opening.W)} × {Mm(opening.H)} mm opening: " +
                string.Join("; ", cands.Select((c, i) => $"[{i}] {c.id} {Dim(dims[i])} {(OpeningMath.FitsProperly(dims[i], opening) ? "fits" : "doesn't fit")} {c.spec?.fit?.note}")) + $" via {s_Via}");
            bool ctxOpening = agent.LastContext != null && agent.LastContext.ContainsKey("opening");
            bool bodyOpening = client.LastSearchBody != bodyBefore && (client.LastSearchBody ?? "").Contains("\"opening\"");
            Check("asset.search.opening", ctxOpening && (s_Via == "server" || bodyOpening),
                $"context.opening={ctxOpening} ({(ctxOpening ? Newtonsoft.Json.JsonConvert.SerializeObject(agent.LastContext["opening"]) : "-")}) search body opening={(client.LastSearchBody == bodyBefore ? "(the server's search)" : bodyOpening.ToString())}");
            if (cands.Count == 0) { done(false, Summary(site)); yield break; }

            // ---- 3. the server builds the best-sized candidate's model (the card says "Building the 3D model…")
            int best = Mathf.Max(0, OpeningMath.Best(dims, opening));
            var pick = cands[best];
            string cardSaid = CardStatus(browser, pick);
            float g0 = Now;
            while (Now - g0 < ModelTimeout && pick.spec?.asset != null && !pick.spec.asset.Ready && !pick.spec.asset.Failed) yield return null;
            var asset = pick.spec?.asset;
            bool made = asset != null && asset.Ready && asset.tier != "proxy";
            Check("asset.generate", made,
                $"candidate {best} {pick.id}: tier {asset?.tier ?? "?"} template {asset?.template ?? "?"} made by {asset?.made_by ?? "? (an older server)"}" +
                $"{(asset?.retried == true ? " after a Grok retry" : "")}, server {(asset?.seconds.HasValue == true ? asset.seconds.Value.ToString("0.0", C) : "?")} s, waited {(Now - g0).ToString("0.0", C)} s; " +
                $"card said \"{cardSaid ?? "(ready already: cached)"}\" | {string.Join(" / ", browser.ModelLog)}");

            // ---- 4. take it; held off the opening it's free, at the opening it snaps in when it fits
            yield return EnsureWorld();
            AppCommands.SelectCandidate(best);
            float l0 = Now;
            yield return null;
            while (Now - l0 < LoadTimeout && browser.Loading) yield return null;
            var part = tool.Held;
            if (part == null) { Check("asset.place", false, $"nothing in hand after taking candidate {best}: {browser.Status}"); done(false, Summary(site)); yield break; }
            s_Log.Add($"  in hand: {part.Spec.id} from {part.Source} ({PartFormat.DimsMm(part.Spec.dims_mm)}) in {(Now - l0).ToString("0.0", C)} s");
            var right = Vector3.Cross(opening.Out, Vector3.up).normalized;
            var away = opening.Centre + right * (opening.W * 0.5f + 1.0f);   // the wall 1 m beside the opening
            var eyeW = frame.TransformPoint(opening.Centre + opening.Out * 2.5f);
            hub.SetPointerOverride(ToolHand.Right, ToolInputHub.RayPose(eyeW, frame.TransformPoint(away)));
            tool.Tick();
            yield return null;
            tool.Tick();
            float freeOff = Vector3.Distance(FrontCentre(part, frame), opening.Centre);
            bool free = tool.Held == part && tool.OnSurface && freeOff > 0.3f;
            Check("asset.place.free", free, $"held over the wall 1 m beside it: on a surface={tool.OnSurface}, front centre {F(freeOff * 1000f)} mm from the opening (not pulled in)");
            var aim = ToolInputHub.RayPose(eyeW, frame.TransformPoint(opening.Centre - opening.Out * 0.08f));
            hub.SetPointerOverride(ToolHand.Right, aim);
            tool.Tick();
            yield return null;
            bool released = tool.Release(aim);
            hub.SetPointerOverride(ToolHand.Right, null);
            yield return null;
            bool fits = OpeningMath.FitsProperly(part.Spec.dims_mm, opening);
            float off = Vector3.Distance(FrontCentre(part, frame), opening.Centre);
            float square = Square(part, frame, opening);
            var fit = part.Fit;
            bool snappedOk = released && fits && off <= 0.01f && square <= 1f && fit != null && fit.Status != FitStatus.Red;
            bool heldBack = released && !fits && off > 0.005f && fit != null && fit.Status == FitStatus.Red;
            Check("asset.place", snappedOk || heldBack,
                $"{part.Spec.id} {(fits ? "fits" : "doesn't fit as sold")} ({Dim(part.Spec.dims_mm)} in {Mm(opening.W)} × {Mm(opening.H)}): released={released}, front centre {F(off * 1000f)} mm from the opening centre, " +
                $"square {F(square)}°, \"{AirTools.UI.Copy.FitLine(fit)}\" | {tool.LastAction} | {editor.LastLook}");

            // ---- 5. Adjust ▸ Size & finish
            AppCommands.AdjustPlacement(true);
            yield return null;
            var panel = editor.panel;
            editor.Tick();
            var before = PlacementEditor.CurrentLook(part);
            string fitBefore = AirTools.UI.Copy.FitLine(part.Fit);
            string keyBefore = part.ModelKey;
            var scaleBefore = part.Model.localScale;
            Press(panel?.sizeSteps, 1, () => AppCommands.StepPlacementSize("w", +1));   // W +
            editor.Tick();
            yield return null;
            var stepped = PlacementEditor.CurrentLook(part);
            float step = PlacementMath.StepMetres(AirTools.UI.UiSettings.UnitSystem, editor.Fine) * 1000f;
            bool wider = Mathf.Abs(stepped.DimsMm.x - before.DimsMm.x - step) < 0.6f && Mathf.Abs(stepped.DimsMm.y - before.DimsMm.y) < 0.01f;
            Check("asset.size.step", wider && part.Model.localScale != scaleBefore && AirTools.UI.Copy.FitLine(part.Fit) != null,
                $"W {Mm1(before.DimsMm.x)} → {Mm1(stepped.DimsMm.x)} mm (step {Mm1(step)}), {part.SizeState}, model scale {V(scaleBefore)} → {V(part.Model.localScale)}, " +
                $"fit \"{fitBefore}\" → \"{AirTools.UI.Copy.FitLine(part.Fit)}\", panel \"{panel?.LastSize}\" | {editor.LastLook}");
            yield return WaitLook(editor);

            Press(panel != null && panel.fitToOpening != null ? new[] { panel.fitToOpening } : null, 0, () => AppCommands.FitPlacementToOpening());
            editor.Tick();
            yield return null;
            var fitted = PlacementEditor.CurrentLook(part);
            var want = OpeningMath.FitToOpeningMm(new Vector3(opening.W * 1000f, opening.H * 1000f, 0f), fitted.DimsMm.z);
            off = Vector3.Distance(FrontCentre(part, frame), opening.Centre);
            square = Square(part, frame, opening);
            fit = part.Fit;
            Check("asset.size.fit", Mathf.Abs(fitted.DimsMm.x - want.x) < 0.6f && Mathf.Abs(fitted.DimsMm.y - want.y) < 0.6f && off <= 0.01f && square <= 1f
                                     && fit != null && fit.Status == FitStatus.Green,
                $"{Mm1(fitted.DimsMm.x)} × {Mm1(fitted.DimsMm.y)} × {Mm1(fitted.DimsMm.z)} mm (want {Mm1(want.x)} × {Mm1(want.y)}), front centre {F(off * 1000f)} mm, square {F(square)}°, " +
                $"\"{AirTools.UI.Copy.FitLine(fit)}\" {fit?.Headline} | {editor.LastLook}");
            float m0 = Now;
            yield return WaitLook(editor);
            bool rebuildable = PartLookMath.CanRebuild(part, null) && !editor.ServerCantResize;
            string key = PartInstance.LookKey(fitted.Dims, null);
            string why = rebuildable ? "server rebuild" : editor.ServerCantResize ? "the server has no /resize (apply docs/handoff/p4-asset): stretched"
                : $"no server rebuild ({part.Spec.asset?.tier} / {part.Source}): stretched";
            Check("asset.size.model", rebuildable ? part.ModelKey == key && part.SizeState == "made to size" : part.SizeState == "resized",
                $"{why}: model {keyBefore} → {part.ModelKey} ({part.SizeState}) " +
                $"in {(Now - m0).ToString("0.0", C)} s, variants parked {part.VariantCount}, requests {editor.LookRequests}, body {client.LastResizeBody} | {editor.LastLook}");

            var choices = editor.FinishChoices(part);
            int chip = -1;
            for (int i = 0; i < choices.Count; i++) if (!PlacementEditor.FinishSelected(part, choices[i])) { chip = i; break; }
            string finishKey = part.ModelKey;
            var colorBefore = part.MaterialColor();
            if (chip >= 0)
            {
                Press(panel?.finishes, chip, () => AppCommands.SetPlacementFinish(choices[chip]));
                editor.Tick();
                yield return null;
                yield return WaitLook(editor);
            }
            string chosen = chip >= 0 ? choices[chip] : null;
            bool changed = part.ModelKey != finishKey || part.MaterialColor() != colorBefore || part.FinishColor.HasValue;
            Check("asset.finish", chip >= 0 && part.LookFinish == chosen && changed && part.Fit != null && part.Fit.Status == FitStatus.Green,
                $"chips [{string.Join(", ", choices)}] → \"{chosen}\": finish {part.LookFinish ?? "listed"} ({part.LookFinishSource ?? "tint"}), model {finishKey} → {part.ModelKey}, " +
                $"tint {(part.FinishColor.HasValue ? PartColor.ToHex(part.FinishColor.Value) : "-")}, fit \"{AirTools.UI.Copy.FitLine(part.Fit)}\" | {editor.LastLook}");

            // ---- 6. save, undo ×3, redo ×3
            int rows = Notebook.Entries.Count;
            AppCommands.SavePlacement(null);
            yield return null;
            var row = Notebook.Entries.Skip(rows).LastOrDefault(e => e.Tool == "placement_pose");
            bool saved = row != null && row.DimsMm != null && Mathf.Abs((float)row.DimsMm[0] - fitted.DimsMm.x) < 0.6f && row.Finish == chosen;
            Check("asset.save", saved, row == null ? "no placement_pose row" :
                $"row #{row.Id} slot {row.Slot}: \"{row.Label}\" dims_mm [{string.Join(", ", row.DimsMm?.Select(x => x.ToString("0.#", C)) ?? new string[0])}] finish {row.Finish ?? "-"} · \"{row.DisplayDetail}\"");
            var after = PlacementEditor.CurrentLook(part);
            for (int k = 0; k < 3; k++) { AppCommands.Undo(); yield return null; }
            yield return WaitLook(editor);
            var undone = PlacementEditor.CurrentLook(part);
            bool rowGone = row == null || !Notebook.Entries.Contains(row);
            Check("asset.undo", undone.Same(stepped) && rowGone,
                $"after 3 undos: {undone} (want the stepped {stepped}), save row gone={rowGone}, {part.SizeState}, model {part.ModelKey} | {editor.Report()}");
            for (int k = 0; k < 3; k++) { AppCommands.Redo(); yield return null; }
            yield return WaitLook(editor);
            var redone = PlacementEditor.CurrentLook(part);
            off = Vector3.Distance(FrontCentre(part, frame), opening.Centre);
            Check("asset.redo", redone.Same(after) && (row == null || Notebook.Entries.Contains(row)) && off <= 0.01f,
                $"after 3 redos: {redone} (want {after}), front centre {F(off * 1000f)} mm, row back={(row != null && Notebook.Entries.Contains(row))}, model {part.ModelKey} ({part.SizeState}) | {AirTools.UI.Copy.FitLine(part.Fit)}");

            if (!o.KeepAdjusting) AppCommands.AdjustPlacement(false);
            done(s_PassN == s_Total, Summary(site));
        }

        // ---------------- captures ----------------

        /// Step 1 alone (the tape capture): the facade window's height then width, taped with the Measure tool.
        public static string TapeFacadeWindow(int seed = 1)
        {
            var root = Services.Get<SceneRoot>();
            var measure = Services.Get<MeasureTool>();
            var hub = Services.Get<ToolInputHub>();
            var tool = Services.Get<PartTool>();
            if (root == null || measure == null || hub == null) return "no SceneRoot / MeasureTool / ToolInputHub";
            if (!AirTools.Structure.SupportEdges.IsSyntheticFacade(root))
            {
                AppCommands.LoadSite("built-in");   // the built-in facade (its window's size is known)
                if (!AirTools.Structure.SupportEdges.IsSyntheticFacade(root)) return "loading the built-in facade: call AssetTapes() again";
            }
            if (AppState.Mode != AppMode.World) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            AppCommands.EquipTool("measure");
            FindWindow(new Options(), root, out var win, out _);
            var frame = tool != null && tool.frame != null ? tool.frame : root.transform;
            var lines = Tapes(win).Select(sc => MeasureScenarios.Run(sc, measure, hub, frame, seed).ToString()).ToList();
            Openings.Invalidate();
            lines.Add(Openings.TryCurrent(out var o) ? $"opening: {o}" : "no opening");
            return string.Join("\n", lines);
        }

        static Pose? s_LookFrom;

        /// Move and turn the rig so the eye is at a shot's spot looking at the facade window (see AgentHarness.AssetLook).
        public static string Look(string shot)
        {
            var rigT = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>()?.transform;
            var cam = Camera.main;
            var root = Services.Get<SceneRoot>();
            if (rigT == null || cam == null || root == null) return "no rig / camera / SceneRoot";
            s_LookFrom ??= new Pose(rigT.position, rigT.rotation);
            var target = new Vector3(0f, S.SillHeight + S.WindowHeight * 0.5f, 0f);
            Vector3 eye;
            switch ((shot ?? "frame").Trim().ToLowerInvariant())
            {
                case "tape": eye = new Vector3(0f, 4.1f, 2.6f); break;
                case "panel": eye = new Vector3(0f, 4.35f, 2.0f); target += Vector3.down * 0.25f; break;
                default: eye = new Vector3(-0.8f, 4.4f, 1.6f); break;
            }
            var eyeW = root.transform.TransformPoint(eye);
            var targetW = root.transform.TransformPoint(target);
            // Turn about the eye to face the target, then move the rig so the eye lands on the spot.
            var want = Quaternion.LookRotation(targetW - cam.transform.position, Vector3.up);
            var turn = want * Quaternion.Inverse(cam.transform.rotation);
            var e = cam.transform.position;
            rigT.SetPositionAndRotation(e + turn * (rigT.position - e), turn * rigT.rotation);
            rigT.position += eyeW - cam.transform.position;
            var editor = PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();
            if (editor != null && editor.IsAdjusting && editor.panel != null) editor.panel.Open();   // in front of the new view
            return $"eye {V(eye)} looking at {V(target)} ({shot}) | {(editor != null ? editor.Report() : "-")}";
        }

        public static string LookReset()
        {
            var rigT = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>()?.transform;
            if (rigT == null || !s_LookFrom.HasValue) return "nothing to reset";
            rigT.SetPositionAndRotation(s_LookFrom.Value.position, s_LookFrom.Value.rotation);
            s_LookFrom = null;
            return "rig back";
        }

        /// A panel button pressed like a poke (its PlacementButton), else the command it runs.
        static void Press(AirTools.UI.GlassButton[] buttons, int i, Func<bool> fallback)
        {
            var b = buttons != null && i >= 0 && i < buttons.Length ? buttons[i] : null;
            var pb = b != null ? b.GetComponent<PlacementButton>() : null;
            if (pb != null && b.isActiveAndEnabled) pb.Press();
            else fallback();
        }

        static IEnumerator WaitLook(PlacementEditor e)
        {
            float t0 = Now;
            yield return null;
            while (e.LookBusy && Now - t0 < ModelTimeout) yield return null;
            yield return null;
        }

        static string CardStatus(PartsBrowser b, PartSummary s)
        {
            foreach (var card in b.cards) if (card != null && card.Summary == s) return card.ModelStatus;
            return null;
        }

        /// One phrase through the voice path's text twin (the reply's actions, then LocalIntents).
        static IEnumerator Say(string text)
        {
            var agent = Services.Get<AgentClient>();
            int runs = LocalIntents.Runs;
            bool done = false;
            AgentReply reply = null;
            float t0 = Now;
            while (agent.Busy && Now - t0 < ReplyTimeout) yield return null;
            t0 = Now;
            agent.SendText(text, r => { reply = r; done = true; });
            while (!done && Now - t0 < ReplyTimeout) yield return null;
            yield return null;
            s_Via = LocalIntents.Runs > runs ? "headset" : "server";
            string acts = reply?.actions != null ? string.Join(",", reply.actions.Select(a => a?.name)) : "-";
            s_Log.Add($"> \"{text}\" → \"{AirTools.UI.Copy.Clip(reply?.reply ?? "", 90)}\" [{acts}] via {s_Via} ({(Now - t0).ToString("0.0", C)} s)" +
                      (!done ? " TIMED OUT" : reply == null ? $" no reply: {agent.LastError}" : ""));
        }

        static IEnumerator EnsureWorld()
        {
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            else if (AppState.Mode == AppMode.Tabletop) AppCommands.SetTabletop(false);
            var mode = Services.Get<ModeController>();
            float t0 = Now;
            while (Now - t0 < WorldTimeout && (AppState.Mode != AppMode.World || (mode != null && (mode.IsTransitioning || mode.VisualMode != AppMode.World))))
                yield return null;
            var streamer = Services.Get<SceneStreamer>();
            while (streamer != null && streamer.Loading && Now - t0 < SiteTimeout) yield return null;
            Physics.SyncTransforms();
        }

        /// The part's front-face centre in its frame (SceneRoot) space.
        static Vector3 FrontCentre(PartInstance p, Transform frame)
        {
            var w = p.transform.TransformPoint(PlacementMath.AnchorLocal(p.LocalBox, PlacementAnchor.FrontCentre));
            return frame != null ? frame.InverseTransformPoint(w) : w;
        }

        /// How far from square to the opening the part is (degrees): its front (+Z) against Out, its up against Up.
        static float Square(PartInstance p, Transform frame, TapedOpening o)
        {
            var fwd = frame != null ? frame.InverseTransformDirection(p.transform.forward) : p.transform.forward;
            var up = frame != null ? frame.InverseTransformDirection(p.transform.up) : p.transform.up;
            return Mathf.Max(Vector3.Angle(fwd, o.Out), Vector3.Angle(up, o.Up));
        }

        static string V(Vector3 v) => $"({v.x.ToString("0.000", C)}, {v.y.ToString("0.000", C)}, {v.z.ToString("0.000", C)})";
        static string F(float v) => float.IsNaN(v) ? "—" : v.ToString("0.0", C);
        static string Mm(float metres) => (metres * 1000f).ToString("0", C);
        static string Mm1(float mm) => mm.ToString("0.0", C);
        static string Dim(PartDims d) => d == null ? "?" : $"{d.w.ToString("0", C)}×{d.h.ToString("0", C)}×{d.d.ToString("0", C)}";
    }
}
#endif
