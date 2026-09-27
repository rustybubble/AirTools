using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AirTools.Core;
using AirTools.Notes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Parts
{
    /// Talks to the parts server (backend docs/api.md; tools/mock_parts_server.py mirrors it):
    /// POST /parts/search {query, measurement{label,value_m,axis}} → poll GET /parts/jobs/{id} → candidates (full Part
    /// objects); GET /parts/{id}/part.json (poll until asset.status == "ready") | model.glb | image.jpg (404 until
    /// fetched: fall back to the part's image_url); POST /parts/{id}/sellers?sort=; POST /parts/bom; POST /checkout.
    /// Searches and part files fall back to the shipped PartCatalog when the server is unreachable, so the demo never
    /// dead-ends. Payments never fall back.
    public class PartsClient : MonoBehaviour
    {
        public PartCatalog catalog;
        public float pollInterval = 0.5f;
        [Tooltip("Live searches take 5–30 s (backend api.md §8); cache hits are instant.")]
        public float searchTimeout = 45f;
        [Tooltip("AI meshes take ~15–20 s each, sequentially; after this the part loads as an exact-size proxy box.")]
        public float assetTimeout = 60f;
        [Tooltip("Skip the network and use the shipped catalog only.")]
        public bool forceOffline;

        public string LastError { get; private set; }
        /// HTTP status of the last request (0 = no response, −1 = none yet); display copy classifies errors with it.
        public long LastHttpCode { get; private set; } = -1;
        /// Spoken line from the last finished search job ("Three gutter hangers. The first fits with 4 mm to spare.").
        public string LastSummary { get; private set; }
        public string LastStage { get; private set; }
        /// Checkout requests actually sent (the hold-to-pay rule is tested against this).
        public int CheckoutRequests { get; private set; }

        public event Action<string> StageChanged;

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        static int Timeout => Services.TryGet<ServerConfig>(out var c) ? c.timeoutSeconds : 5;

        public static string Resolve(string url) =>
            string.IsNullOrEmpty(url) || url.StartsWith("http://") || url.StartsWith("https://") ? url : ServerConfig.Current + (url.StartsWith("/") ? url : "/" + url);

        // ---------------- search ----------------

        /// done(candidates, source) — source is "server", "server (cached)" or "offline catalog".
        public void Search(string query, Action<List<PartSummary>, string> done) => StartCoroutine(SearchRoutine(query, done, true));

        /// catalog: `useMeasurement` false searches without the tape / gap even when there is one (the Catalog's Fits chip
        /// is off: browsing, not fitting).
        public void Search(string query, Action<List<PartSummary>, string> done, bool useMeasurement) =>
            StartCoroutine(SearchRoutine(query, done, useMeasurement));

        [Tooltip("A search sent with the tape's measurement gives up after this long (s) and is retried without it.")]
        public float measuredSearchTimeout = 20f;

        /// How many searches this client retried without the tape's measurement (tests / the harness).
        public int MeasurementRetries { get; private set; }

        /// e2e: the last POST /parts/search body sent (the harness checks the gap's size went out).
        public string LastSearchBody { get; private set; }

        /// The attempts a search makes: with the tape's measurement (when there is one), then once without it — the
        /// laptop's warmed cache is keyed on the query alone, so a live search that fails (an error, 429, a timeout)
        /// can still hit it — before the offline catalog.
        public static bool[] SearchAttempts(bool hasMeasurement) => hasMeasurement ? new[] { true, false } : new[] { false };

        IEnumerator SearchRoutine(string query, Action<List<PartSummary>, string> done, bool useMeasurement)
        {
            if (!forceOffline)
            {
                var m = useMeasurement ? MeasurementContext() : null;   // catalog: no tape when Fits is off
                var cavity = useMeasurement ? AirTools.Scene.Gaps.CurrentContext() : null;   // e2e: a gap is open → its W × H × D go along
                var opening = useMeasurement ? Openings.CurrentContext() : null;   // assetgen: a taped opening (a window's W × H) goes along
                foreach (bool withMeasurement in SearchAttempts(m != null || cavity != null || opening != null))
                {
                    var body = new Dictionary<string, object> { ["query"] = query, ["max_candidates"] = 3 };
                    AssetModes.AddTo(body, UserPrefs.Assets);   // settings-assets: Settings ▸ 3D models
                    if (withMeasurement)
                    {
                        if (m != null) body["measurement"] = m;
                        if (cavity != null) body["cavity"] = cavity;   // e2e (ignored by servers without the e2e patch)
                        if (opening != null) body["opening"] = opening;   // assetgen (ignored by servers without the p4-asset patch)
                    }
                    else if (m != null || cavity != null || opening != null) { MeasurementRetries++; Log.Warn($"Parts search '{query}' with the tape failed ({LastError}); retrying without the measurement"); }
                    LastError = null;
                    LastSearchBody = JsonConvert.SerializeObject(body);   // e2e: the harness checks what went out
                    SearchJobResponse job = null;
                    yield return Send(Post("/parts/search", JsonConvert.SerializeObject(body)), text => job = PartSpec.ParseAny<SearchJobResponse>(text));
                    if (job != null && !string.IsNullOrEmpty(job.job_id))
                    {
                        if (job.status == "done" && job.candidates != null)
                        {
                            LastSummary = null;
                            done?.Invoke(ParseCandidates(job.candidates), "server (cached)");
                            yield break;
                        }
                        if (job.status == "failed" && LastError == null) LastError = "search failed";
                        List<PartSummary> result = null; string source = null;
                        if (job.status != "failed")
                            yield return PollJobRoutine(job.job_id, (c, s) => { result = c; source = s; }, withMeasurement ? measuredSearchTimeout : searchTimeout);
                        if (result != null) { done?.Invoke(result, source); yield break; }
                    }
                    else if (LastError == null) LastError = "search failed: no job";
                }
                Log.Warn($"Parts search '{query}' fell back to the offline catalog: {LastError}");
            }
            done?.Invoke(catalog != null ? catalog.Search(query) : new List<PartSummary>(), "offline catalog");
        }

        /// Follow a search job started elsewhere (the agent's search_started action). done(candidates or null, source).
        public void PollJob(string jobId, Action<List<PartSummary>, string> done) => StartCoroutine(PollJobRoutine(jobId, done, searchTimeout));

        IEnumerator PollJobRoutine(string jobId, Action<List<PartSummary>, string> done, float timeout)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < until)
            {
                SearchJobStatus status = null;
                yield return Send(UnityWebRequest.Get(Resolve($"/parts/jobs/{jobId}")), text => status = PartSpec.ParseAny<SearchJobStatus>(text));
                if (status == null) break;
                if (!string.IsNullOrEmpty(status.stage) && status.stage != LastStage) { LastStage = status.stage; StageChanged?.Invoke(status.stage); }
                if (status.Done)
                {
                    LastSummary = status.summary;
                    done?.Invoke(ParseCandidates(status.candidates), "server");
                    yield break;
                }
                if (status.Failed) { LastError = $"search failed: {status.error}"; break; }
                yield return new WaitForSecondsRealtime(pollInterval);
            }
            if (LastError == null) LastError = "search timed out";
            done?.Invoke(null, null);
        }

        /// Candidates are full Part objects on the real server (summaries on older mocks): keep the whole spec.
        public static List<PartSummary> ParseCandidates(JArray arr)
        {
            var list = new List<PartSummary>();
            if (arr == null) return list;
            foreach (var t in arr)
            {
                var json = t.ToString(Formatting.None);
                PartSummary s;
                try { s = PartSpec.Parse(json).ToSummary(); }
                catch (Exception) { s = PartSpec.ParseAny<PartSummary>(json); }
                if (s == null || string.IsNullOrEmpty(s.id)) continue;
                s.image_url ??= $"/parts/{s.id}/image.jpg";
                s.model_url ??= $"/parts/{s.id}/model.glb";
                s.part_url ??= $"/parts/{s.id}/part.json";
                list.Add(s);
            }
            return list;
        }

        /// The active tape reading as the backend's Measurement {label, value_m, axis} (null when there is none).
        public static Dictionary<string, object> MeasurementContext()
        {
            var e = LatestTape();
            if (e == null) return null;
            return new Dictionary<string, object> { ["label"] = $"tape #{e.Id}", ["value_m"] = Math.Round(e.ValueSI, 4), ["axis"] = TapeAxis(e.Points[0], e.Points[1]) };
        }

        /// Longest horizontal tape still treated as a size constraint (an opening, a door, a window); longer ones are runs.
        public const float MaxWidthTapeM = 1.5f;

        /// The contract's Measurement.axis for a tape (points in scene-root space, +Y up): "h" for a vertical tape
        /// (within 20° of up), "w" for a horizontal one up to 1.5 m (a width the part must fit), "length" for a longer
        /// run (a gutter: the server treats it as no size constraint). The server compares dims_mm on that axis.
        public static string TapeAxis(Vector3 a, Vector3 b)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 1e-6f) return "length";
            if (Mathf.Abs(d.y) / len >= Mathf.Cos(20f * Mathf.Deg2Rad)) return "h";
            return len <= MaxWidthTapeM ? "w" : "length";
        }

        // ---------------- part files ----------------

        /// done(spec, source): "server" | "catalog" | null source when neither has it. settings-assets: part.json?mode= — the
        /// part with that mode's model (`mode` null: Settings ▸ 3D models); a server that is still making it says pending.
        public void GetSpec(PartSummary s, Action<PartSpec, string> done, AssetMode? mode = null) => StartCoroutine(SpecRoutine(s, done, mode ?? UserPrefs.Assets));

        IEnumerator SpecRoutine(PartSummary s, Action<PartSpec, string> done, AssetMode mode)
        {
            PartSpec spec = null;
            if (!forceOffline)
                yield return Send(UnityWebRequest.Get(Resolve(AssetModes.WithMode(s.part_url ?? $"/parts/{s.id}/part.json", mode))), text =>
                {
                    try { spec = PartSpec.Parse(text); }
                    catch (Exception ex) { LastError = $"bad part.json: {ex.Message}"; }
                });
            if (spec != null) { done?.Invoke(spec, "server"); yield break; }
            spec = catalog != null ? catalog.Spec(s.id) : null;
            done?.Invoke(spec, spec != null ? "catalog" : null);
        }

        /// Poll part.json until its model is ready or failed (or the timeout). progress(status) while waiting;
        /// done(latest spec, ready).
        public void WaitForAsset(PartSpec spec, Action<string> progress, Action<PartSpec, bool> done, AssetMode? mode = null) =>
            StartCoroutine(WaitAssetRoutine(spec, progress, done, mode ?? UserPrefs.Assets));

        IEnumerator WaitAssetRoutine(PartSpec spec, Action<string> progress, Action<PartSpec, bool> done, AssetMode mode)
        {
            float t0 = Time.realtimeSinceStartup;
            var current = spec;
            while (!current.asset.Ready && !current.asset.Failed && Time.realtimeSinceStartup - t0 < assetTimeout)
            {
                progress?.Invoke($"Building the {(current.asset.tier == "ai_mesh" || mode == AssetMode.Hf ? "AI mesh" : "3D model")}… {Time.realtimeSinceStartup - t0:0} s");   // assetgen + settings-assets
                yield return new WaitForSecondsRealtime(1.5f);
                PartSpec fresh = null;
                yield return Send(UnityWebRequest.Get(Resolve(AssetModes.PartUrl(spec.id, mode))), text =>   // settings-assets
                {
                    try { fresh = PartSpec.Parse(text); } catch (Exception) { }
                });
                if (fresh != null) current = fresh;
            }
            done?.Invoke(current, current.asset.Ready);
        }

        public void GetBytes(string url, Action<byte[]> done) => StartCoroutine(BytesRoutine(url, done));

        IEnumerator BytesRoutine(string url, Action<byte[]> done)
        {
            if (forceOffline) { done?.Invoke(null); yield break; }
            using var req = UnityWebRequest.Get(Resolve(url));
            req.timeout = Math.Max(Timeout, 30);
            yield return HttpDeadline.Send(req, HttpDeadline.DownloadIdle);   // fix-ux: a model download: bytes must keep coming
            if (HttpDeadline.Ok(req)) { LastError = null; done?.Invoke(req.downloadHandler.data); }
            else { LastError = $"{req.url}: {HttpDeadline.Error(req)}"; done?.Invoke(null); }
        }

        /// Candidate photo: /parts/{id}/image.jpg, else (404 while the worker fetches it) the seller's image_url, else
        /// the catalog image.
        public void GetImage(PartSummary s, Action<Texture2D> done) => StartCoroutine(ImageRoutine(s, done));

        IEnumerator ImageRoutine(PartSummary s, Action<Texture2D> done)
        {
            if (!forceOffline)
                foreach (var url in new[] { s.image_url ?? $"/parts/{s.id}/image.jpg", s.remote_image_url ?? s.spec?.image_url })
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    using var req = UnityWebRequestTexture.GetTexture(Resolve(url));
                    req.timeout = Timeout;
                    yield return HttpDeadline.Send(req, HttpDeadline.DownloadIdle);   // fix-ux: our clock
                    if (HttpDeadline.Ok(req))
                    {
                        done?.Invoke(DownloadHandlerTexture.GetContent(req));
                        yield break;
                    }
                }
            done?.Invoke(catalog != null ? catalog.Find(s.id)?.image : null);
        }

        // ---------------- assetgen: made to size ----------------

        /// POST /parts/{id}/resize {w, h, d, finish?} (backend p4-asset): the part's model rebuilt at w × h × d mm — its
        /// template at the new size (source "template"), a new box ("box") or the mesh stretched ("scaled") — with the
        /// finish's re-textured variant when the server renders one. done(reply or null: no server, an old server, a
        /// refusal; LastError / LastHttpCode say which). Up to 60 s: a first finish is a Grok Imagine edit (~8 s).
        public void Resize(string partId, Vector3 dimsMm, string finish, Action<ResizeReply> done)
        {
            if (forceOffline || string.IsNullOrEmpty(partId)) { done?.Invoke(null); return; }
            var body = new Dictionary<string, object> { ["w"] = Math.Round(dimsMm.x, 1), ["h"] = Math.Round(dimsMm.y, 1), ["d"] = Math.Round(dimsMm.z, 1) };
            if (!string.IsNullOrEmpty(finish)) body["finish"] = finish;
            LastResizeBody = JsonConvert.SerializeObject(body);
            ResizeRequests++;
            PostJson($"/parts/{partId}/resize", LastResizeBody, 60, (code, text) =>
            {
                ResizeReply r = null;
                if (code >= 200 && code < 300) { try { r = PartSpec.ParseAny<ResizeReply>(text); } catch (Exception ex) { LastError = $"resize: {ex.Message}"; } }
                else LastError = $"resize {partId}: HTTP {code} {ServerDetail(text)}";
                done?.Invoke(r != null && !string.IsNullOrEmpty(r.model_url) ? r : null);
            });
        }

        /// The last resize body sent and how many were sent (the harness).
        public string LastResizeBody { get; private set; }
        public int ResizeRequests { get; private set; }

        // ---------------- sellers + BOM ----------------

        /// POST /parts/{id}/sellers?sort=cheapest|fastest|best → the part with sellers re-ranked (and, on the first call,
        /// possibly expanded). done(part or null). Indices into the returned sellers[] are what /checkout expects.
        public void Sellers(string partId, string sort, Action<PartSpec> done) => StartCoroutine(SellersRoutine(partId, sort, done));

        public static string ServerSort(string sort)
        {
            var s = SellerSort.Normalise(sort);
            if (s == SellerSort.ByEta) return "fastest";
            if ((sort ?? "").ToLowerInvariant().Contains("best")) return "best";
            return "cheapest";
        }

        IEnumerator SellersRoutine(string partId, string sort, Action<PartSpec> done)
        {
            if (forceOffline) { done?.Invoke(null); yield break; }
            PartSpec spec = null;
            yield return Send(Post($"/parts/{partId}/sellers?sort={ServerSort(sort)}", "{}"), text =>
            {
                try { spec = PartSpec.Parse(text); } catch (Exception ex) { LastError = $"sellers: {ex.Message}"; }
            }, timeoutOverride: 20);
            done?.Invoke(spec);
        }

        /// POST /parts/bom {part_ids, counts} ("what else do I need?").
        public void Bom(Dictionary<string, int> counts, Action<PartBom> done) => StartCoroutine(BomRoutine(counts, done));

        IEnumerator BomRoutine(Dictionary<string, int> counts, Action<PartBom> done)
        {
            if (forceOffline || counts == null || counts.Count == 0) { done?.Invoke(null); yield break; }
            PartBom bom = null;
            string body = JsonConvert.SerializeObject(new { part_ids = new List<string>(counts.Keys), counts });
            yield return Send(Post("/parts/bom", body), text => bom = PartSpec.ParseAny<PartBom>(text), timeoutOverride: 30);
            done?.Invoke(bom);
        }

        // ---------------- checkout ----------------

        /// POST /checkout — a payment authorization. Only CheckoutPanel calls this, and only after a completed
        /// physical hold; there is no offline fallback (no server, no payment). `qty` counts listings (packs), as the
        /// server charges price_usd × qty. Optional "what else do I need?" lines ride in the same cart.
        public void Checkout(string partId, int sellerIndex, int qty, Action<CheckoutReceipt, string> done) =>
            Checkout(partId, sellerIndex, qty, null, null, null, (r, e) => done?.Invoke(r, e?.Detail));

        public void Checkout(string partId, int sellerIndex, int qty, string bomId, IList<int> bomLines, Action<CheckoutReceipt, string> done) =>
            Checkout(partId, sellerIndex, qty, bomId, bomLines, null, (r, e) => done?.Invoke(r, e?.Detail));

        /// With `proof` (measured mandate, B3 hand-off §5.2): cart_hash + hold_nonce + hold_ms from the hold that just
        /// completed; `qty` must be the prepared packs and the BOM lines exactly what was prepared. done(receipt, error).
        public void Checkout(string partId, int sellerIndex, int qty, string bomId, IList<int> bomLines, HoldProof proof, Action<CheckoutReceipt, CheckoutError> done)
        {
            CheckoutRequests++;
            Log.Info($"Checkout request: {partId} seller {sellerIndex} × {qty}{(bomId != null ? $" + BOM {bomId} lines [{string.Join(",", bomLines ?? new int[0])}]" : "")}{(proof != null ? $" with hold proof ({proof.HeldMs} ms, cart …{Tail(proof.CartHash)})" : "")}");
            if (forceOffline) { done?.Invoke(null, new CheckoutError { Detail = "the laptop server is offline" }); return; }
            var body = new Dictionary<string, object> { ["part_id"] = partId, ["seller_idx"] = sellerIndex, ["qty"] = qty, ["session_id"] = SessionInfo.Id };
            if (!string.IsNullOrEmpty(bomId) && bomLines != null && bomLines.Count > 0) { body["bom_id"] = bomId; body["bom_lines"] = bomLines; }
            if (proof != null)
            {
                body["cart_hash"] = proof.CartHash;
                body["hold_nonce"] = proof.Nonce;
                body["hold_ms"] = proof.HeldMs;
            }
            LastProof = proof;
            LastCheckoutBody = JsonConvert.SerializeObject(body);
            PostJson("/checkout", LastCheckoutBody, 30, (code, text) =>
            {
                if (code >= 200 && code < 300)
                {
                    var receipt = PartSpec.ParseAny<CheckoutReceipt>(text);
                    if (receipt == null) done?.Invoke(null, new CheckoutError { Code = code, Detail = "receipt unreadable" });
                    else if (!receipt.Recorded) done?.Invoke(receipt, new CheckoutError { Code = code, Detail = $"declined ({receipt.status})" });
                    else done?.Invoke(receipt, null);
                }
                else done?.Invoke(null, CheckoutError.Parse(code, text, LastError ?? "no response"));
            });
        }

        /// The last /checkout body sent, and the hold proof it carried (null: none) — tests and the harness check them.
        public string LastCheckoutBody { get; private set; }
        public HoldProof LastProof { get; private set; }

        static string Tail(string s) => string.IsNullOrEmpty(s) || s.Length < 4 ? s : s.Substring(s.Length - 4);

        /// How a /checkout/prepare went: Prepared, Unsupported (an older server: 404/405, or no answer at all — the app
        /// then checks out exactly as before) or Refused (400/409/422: the checks or the request failed).
        public enum PrepareResult { Prepared, Unsupported, Refused }

        public int PrepareRequests { get; private set; }
        public string LastPrepareBody { get; private set; }

        /// POST /checkout/prepare (B3 hand-off §5.1): price the cart on the server, run the checks, issue the hold nonce.
        /// Never pays. done(result, prepared or null, error or null).
        public void Prepare(PrepareRequest req, Action<PrepareResult, CheckoutPrepared, CheckoutError> done)
        {
            PrepareRequests++;
            if (forceOffline) { done?.Invoke(PrepareResult.Unsupported, null, null); return; }
            LastPrepareBody = JsonConvert.SerializeObject(req, s_Json);
            PostJson("/checkout/prepare", LastPrepareBody, 0, (code, text) =>
            {
                if (code >= 200 && code < 300)
                {
                    var p = PartSpec.ParseAny<CheckoutPrepared>(text);
                    if (p != null && !string.IsNullOrEmpty(p.hold_nonce) && p.cart != null) { done?.Invoke(PrepareResult.Prepared, p, null); return; }
                    done?.Invoke(PrepareResult.Unsupported, null, new CheckoutError { Code = code, Detail = "prepare reply unreadable" });
                    return;
                }
                var e = CheckoutError.Parse(code, text, LastError ?? "no response");
                bool refused = code == 400 || code == 409 || code == 422;
                done?.Invoke(refused ? PrepareResult.Refused : PrepareResult.Unsupported, null, e);
            });
        }

        /// POST /commerce/limits from the panel (hand-off §6): the user's hand may loosen or clear (null) a limit.
        /// Only the keys present in `fields` change. done(result or null, error or null).
        public void Limits(Dictionary<string, object> fields, Action<LimitsResult, CheckoutError> done)
        {
            var body = new Dictionary<string, object> { ["session_id"] = SessionInfo.Id, ["source"] = "panel", ["text"] = "panel" };
            if (fields != null) foreach (var kv in fields) body[kv.Key] = kv.Value;
            if (forceOffline) { done?.Invoke(null, new CheckoutError { Detail = "offline" }); return; }
            PostJson("/commerce/limits", JsonConvert.SerializeObject(body), 0, (code, text) =>
            {
                if (code >= 200 && code < 300) done?.Invoke(PartSpec.ParseAny<LimitsResult>(text), null);
                else done?.Invoke(null, CheckoutError.Parse(code, text, LastError ?? "no response"));
            });
        }

        static readonly JsonSerializerSettings s_Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include };

        /// Tests (EditMode can't run coroutines): answer JSON POSTs synchronously, (path, body) → (status, body).
        /// Null = the network (Play mode) or no answer at all (edit mode: status 0).
        public Func<string, string, (long code, string body)> testTransport;

        /// POST a JSON body; done(status, response body) — status 0 when nothing answered.
        public void PostJson(string path, string json, int timeoutSeconds, Action<long, string> done)
        {
            if (testTransport != null)
            {
                var (code, body) = testTransport(path, json);
                LastHttpCode = code;
                LastError = code >= 200 && code < 300 ? null : $"POST {path}: HTTP {code}";
                done?.Invoke(code, body);
                return;
            }
            if (!Application.isPlaying) { LastError = "not running"; done?.Invoke(0, null); return; }
            StartCoroutine(PostJsonRoutine(path, json, timeoutSeconds, done));
        }

        IEnumerator PostJsonRoutine(string path, string json, int timeoutSeconds, Action<long, string> done)
        {
            using var req = Post(path, json);
            req.timeout = timeoutSeconds > 0 ? timeoutSeconds : Timeout;
            // fix-ux: our clock (checkout / prepare / booth: the server may think: no idle limit, timeout + 1 s). A call
            // our clock gave up on answers status 0, like no answer at all.
            yield return HttpDeadline.Send(req, idleSeconds: 0f);
            bool gaveUp = HttpDeadline.GaveUp(req) != null;
            LastHttpCode = gaveUp ? 0 : req.responseCode;
            string text = gaveUp ? null : req.downloadHandler?.text;
            if (!HttpDeadline.Ok(req)) LastError = $"{req.method} {req.url}: {HttpDeadline.Error(req)}";
            else LastError = null;
            done?.Invoke(LastHttpCode, text);
        }

        /// FastAPI errors are {"detail": "..."}.
        public static string ServerDetail(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try { return (string)JObject.Parse(body)["detail"] ?? body; } catch { return body.Length > 200 ? body.Substring(0, 200) : body; }
        }

        // ---------------- plumbing ----------------

        public static UnityWebRequest Post(string path, string json)
        {
            var req = new UnityWebRequest(Resolve(path), UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            req.SetRequestHeader("Content-Type", "application/json");
            return req;
        }

        IEnumerator Send(UnityWebRequest req, Action<string> onSuccess, int timeoutOverride = 0, Action<string> onError = null)
        {
            using (req)
            {
                req.timeout = timeoutOverride > 0 ? timeoutOverride : Timeout;
                // fix-ux: our clock. Quick calls (search start, job / part.json polls) must answer within SmallIdle; the
                // long ones (sellers, BOM: the server thinks) get their timeout + 1 s.
                yield return HttpDeadline.Send(req, timeoutOverride > 0 ? 0f : HttpDeadline.SmallIdle);
                bool gaveUp = HttpDeadline.GaveUp(req) != null;
                LastHttpCode = gaveUp ? 0 : req.responseCode;
                if (!HttpDeadline.Ok(req))
                {
                    LastError = $"{req.method} {req.url}: {HttpDeadline.Error(req)}";
                    onError?.Invoke(gaveUp ? null : req.downloadHandler?.text);
                    yield break;
                }
                LastError = null;
                try { onSuccess(req.downloadHandler.text); }
                catch (Exception ex) { LastError = $"{req.url}: {ex.Message}"; }
            }
        }

        static NotebookEntry LatestTape()
        {
            var entries = Notebook.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Tool == "measure" && entries[i].Points != null && entries[i].Points.Length == 2 && entries[i].OnCurrentSite) return entries[i];   // sitescope: this scan's tape
            return null;
        }
    }
}
