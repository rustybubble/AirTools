using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Notes;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    public enum CheckoutState { Closed, Ready, Paying, Paid, Failed }

    /// Where the measured mandate is (B3 hand-off §5): nothing yet, asking the server, prepared (checks + nonce), an
    /// older server without /checkout/prepare (Legacy: pay exactly as before, no proof), or refused (a check failed).
    public enum MandateState { None, Pending, Prepared, Legacy, Refused }

    /// Checkout (SPEC M5 + the measured mandate, B3): quantity (from the array), the server's price and checks, the
    /// Visa test card •••• 1111 and a hold-to-pay button — keep it pressed for 1 s while the ring fills. Opening the
    /// panel, a seller / quantity / BOM change and a nonce older than ~100 s call POST /checkout/prepare; its checks
    /// render as rows (✓ ok · ⚠ warn · ✗ fail) and a failing one disables Pay. Only a completed physical hold calls
    /// POST /checkout, with cart_hash + hold_nonce + hold_ms (Open() never pays, and voice can only open this panel).
    /// Then a receipt with the mandate chain (Intent → Cart → Authorization → Evidence), a chime and a notebook entry.
    /// Against a server without /checkout/prepare the panel behaves exactly as before (no checks, no proof).
    public class CheckoutPanel : MonoBehaviour
    {
        public FloatingWindow window;
        public PartsClient client;
        public HoldToConfirm hold;
        public TextMeshPro title;
        public TextMeshPro lines;
        public TextMeshPro card;
        public TextMeshPro status;
        public GameObject receipt;
        public TextMeshPro receiptText;
        public GlassButton qtyMinus, qtyPlus;
        [Tooltip("B3: the server's checks as rows (✓ / ⚠ / ✗), then the receipt's mandate chain.")]
        public TextMeshPro checks;
        [Tooltip("B3: the session's limits (≤ $40 · by Fri · fastest).")]
        public TextMeshPro limits;
        [Tooltip("B3: the evidence photo on the receipt (the tape's drone thumbnail).")]
        public MeshRenderer evidenceThumb;
        [Tooltip("Declutter M3 (the W1.6 CTA): the receipt's own primary — leave the world with the bought parts on the table. The Next-step pill yields to this window, so the judge's path stays at 10 actions.")]
        public GlassButton takeHome;

        public CheckoutState State { get; private set; } = CheckoutState.Closed;
        public PartSpec Spec { get; private set; }
        public int SellerIndex { get; private set; }
        public int Quantity { get; private set; } = 1;
        public CheckoutReceipt Receipt { get; private set; }
        public NotebookEntry Entry { get; private set; }
        /// An authorized purchase (receipt, part).
        public event Action<CheckoutReceipt, PartSpec> Purchased;

        // ---- measured mandate ----
        public MandateState Mandate { get; private set; } = MandateState.None;
        /// The last successful prepare (its nonce is spent by the next /checkout attempt).
        public CheckoutPrepared Prepared { get; private set; }
        /// What that prepare asked for: /checkout must send exactly its BOM lines, and qty = the prepared packs.
        public PrepareRequest PreparedRequest { get; private set; }
        /// Rows on the panel: the prepare's checks, or a 422's failing checks.
        public List<CheckoutCheck> Checks { get; private set; } = new List<CheckoutCheck>();
        public CheckoutError LastRefusal { get; private set; }
        /// Realtime the nonce was issued (−1: none).
        public float PreparedAt { get; private set; } = -1f;
        /// Prepare requests this panel sent (tests: a quantity change re-prepares).
        public int PrepareCount { get; private set; }
        /// Holds refused without sending anything (Pay off, or still checking).
        public int RefusedHolds { get; private set; }
        /// Re-prepare after this many seconds (the server's nonce lives 120 s).
        public static float NonceRefreshSeconds = 100f;
        int m_PrepareSeq;
        string m_Notice;

        PartsClient Client => client != null ? client : Services.Get<PartsClient>();

        void OnEnable()
        {
            Services.Register(this);
            AppState.Changed += OnMode;
            AirTools.Agent.Grok.GrokLanes.OnSafetyChanged(OnSafety, true);
            if (hold != null) { hold.Confirmed += OnHoldConfirmed; hold.HoldStarted += OnHoldStarted; }
            if (qtyMinus != null) qtyMinus.Clicked += Minus;
            if (qtyPlus != null) qtyPlus.Clicked += Plus;
            if (takeHome != null) takeHome.Clicked += OnTakeHome;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            AppState.Changed -= OnMode;
            AirTools.Agent.Grok.GrokLanes.OnSafetyChanged(OnSafety, false);
            if (hold != null) { hold.Confirmed -= OnHoldConfirmed; hold.HoldStarted -= OnHoldStarted; }
            if (qtyMinus != null) qtyMinus.Clicked -= Minus;
            if (qtyPlus != null) qtyPlus.Clicked -= Plus;
            if (takeHome != null) takeHome.Clicked -= OnTakeHome;
        }

        /// The receipt's Take it home: the same step action as the pill's (NextStepActions → AppCommands.ToggleChest),
        /// only once paid and inside the world. It leaves the world; it never pays.
        public bool TakeHome()
        {
            if (State != CheckoutState.Paid || AppState.Mode == AppMode.Passthrough) return false;
            return NextStepActions.Run(new StepAction("Take it home", StepCommand.TakeHome));
        }

        void OnTakeHome() => TakeHome();

        void OnMode(AppMode from, AppMode to) { if (to == AppMode.Passthrough && State != CheckoutState.Closed) Close(); }

        /// A verdict for the part on the panel arrived (show_safety): show the recall line.
        void OnSafety(string partId) { if (Spec != null && partId == Spec.id && State != CheckoutState.Closed) Refresh(keepStatus: true); }

        /// While Pay is held: "Keep holding…" (status only; the 1 s hold contract is HoldToConfirm's). A nonce close to
        /// its 2 min expiry is renewed while the panel waits.
        void Update()
        {
            if (m_CloseAfterHold) StepCloseAfterHold();   // switchclean
            if (State == CheckoutState.Ready && NonceStale) Prepare();
            // The evidence photo downloads on demand: show it once it's in.
            if (State == CheckoutState.Paid && evidenceThumb != null && !evidenceThumb.gameObject.activeSelf && Time.frameCount % 15 == 0) ShowEvidenceThumb(true);
            if (status == null || hold == null || State != CheckoutState.Ready || !CanPay) return;
            string want = hold.Progress > 0.001f ? "Keep holding…" : m_Notice ?? $"Hold Pay for a second to pay {PartFormat.Price(DisplayTotal)}";
            if (status.text != want) status.text = want;
        }

        bool NonceStale => Mandate == MandateState.Prepared && PreparedAt >= 0f && Time.realtimeSinceStartup - PreparedAt > NonceRefreshSeconds;

        void OnHoldStarted() { if (Editable && NonceStale) Prepare(); }

        /// Show the order for review and prepare it on the server. Never pays.
        public bool Open(PartSpec spec, int sellerIndex, int qty)
        {
            if (spec == null || sellerIndex < 0 || sellerIndex >= spec.sellers.Count) return false;
            Spec = spec; SellerIndex = sellerIndex; Quantity = Mathf.Clamp(qty, 1, 999);
            Receipt = null; Entry = null;
            m_CloseAfterHold = false;   // switchclean: a new order isn't the one a switch left up
            LoadPostcard(null);
            State = CheckoutState.Ready;
            hold?.ResetHold();
            ClearMandate();
            Refresh();
            window?.Open();
            Log.Info($"Checkout opened: {spec.id} from {spec.sellers[sellerIndex].name} × {Quantity}");
            Prepare();
            return true;
        }

        public void Close()
        {
            window?.Close();
            if (State != CheckoutState.Paying) State = CheckoutState.Closed;
            m_PrepareSeq++;   // a prepare still in flight no longer matters
        }

        // switchclean: a world-model switch never closes a checkout mid-hold or mid-payment (SwitchClose.CheckoutKeep).
        bool m_CloseAfterHold;

        /// A switch came while Pay was held: close once the hold ends without a payment (SwitchClose.StepAfterHold).
        public bool CloseAfterHold => m_CloseAfterHold;

        public void CloseWhenHoldEnds()
        {
            m_CloseAfterHold = true;
            Log.Info("Checkout: kept through a world switch while Pay is held; closes when the hold ends without paying");
        }

        /// One frame of the deferred close (Update; tests call it).
        public void StepCloseAfterHold()
        {
            if (!m_CloseAfterHold) return;
            switch (SwitchClose.StepAfterHold(State, hold != null && hold.Holding))
            {
                case SwitchClose.AfterHold.Wait: return;
                case SwitchClose.AfterHold.Close:
                    m_CloseAfterHold = false;
                    hold?.ResetHold();
                    Close();
                    Log.Info("Checkout: closed after the hold ended without paying (world switch)");
                    return;
                default:
                    m_CloseAfterHold = false;   // it paid (or is paying), or it was closed meanwhile: yours to close
                    return;
            }
        }
        // end switchclean

        /// Back to the seller list (the checkout had taken its slot).
        public void Back()
        {
            Close();
            if (Services.TryGet<SellerPanel>(out var sellers)) sellers.Show(sellers.Part, sellers.Sort);
        }

        bool Editable => State == CheckoutState.Ready || State == CheckoutState.Failed;
        void Minus() { if (Editable) SetQuantity(Quantity - 1); }
        void Plus() { if (Editable) SetQuantity(Quantity + 1); }

        /// The −/+ buttons: pieces needed; the server re-prices the packs.
        public void SetQuantity(int qty)
        {
            int q = Mathf.Clamp(qty, 1, 999);
            if (q == Quantity || !Editable) return;
            Quantity = q;
            Refresh();
            Prepare();
        }

        /// Listings to buy: a listing may be a pack (pack_qty units), and the server charges price_usd × qty per listing.
        public int Packs => Spec == null ? 1 : Spec.sellers[SellerIndex].PacksFor(Quantity);

        /// "What else do I need?" lines riding in the same cart (AppCommands.CurrentBom, only its priced lines).
        public PartBom Bom { get; private set; }
        public List<int> BomLines { get; } = new List<int>();
        float BomTotal { get { float t = 0; if (Bom != null) foreach (var l in Bom.lines) if (BomLines.Contains(l.idx) && l.seller != null) t += l.seller.price_usd * Mathf.Max(1, l.qty); return t; } }

        /// The search that found the part (names it: "hinge").
        public string SearchQuery { get; set; }

        /// The local estimate (older servers, and until the prepare answers).
        public float Total => Spec == null ? 0f : Spec.sellers[SellerIndex].price_usd * Packs + SellerSort.Shipping(Spec.sellers[SellerIndex]) + BomTotal;

        /// What the panel shows: the server's price once prepared.
        public float DisplayTotal => Prepared?.cart != null && (Mandate == MandateState.Prepared || Mandate == MandateState.Refused) ? Prepared.cart.total_usd : Total;

        /// Add the priced lines of a BOM to this cart (null clears it).
        public void SetBom(PartBom bom)
        {
            Bom = bom;
            BomLines.Clear();
            if (bom != null) foreach (var l in bom.lines) if (l.seller != null && l.seller.Priced) BomLines.Add(l.idx);
            Refresh();
            if (Editable && Spec != null && Mandate != MandateState.None) Prepare();
        }

        // ---------------- prepare ----------------

        void ClearMandate()
        {
            Mandate = MandateState.None;
            Prepared = null; PreparedRequest = null; PreparedAt = -1f;
            Checks = new List<CheckoutCheck>();
            LastRefusal = null; m_Notice = null;
        }

        /// The /checkout/prepare body for the current order: units_needed = pieces, the BOM lines in the cart, and the
        /// measurement evidence from PartTool's array group (its tape, spacing, count and photo).
        public PrepareRequest BuildPrepareRequest()
        {
            var req = new PrepareRequest
            {
                session_id = SessionInfo.Id, part_id = Spec.id, seller_idx = SellerIndex, units_needed = Quantity,
                bom_id = BomLines.Count > 0 ? Bom?.Id : null,
            };
            if (req.bom_id != null) req.bom_lines.AddRange(BomLines);
            var tool = Services.TryGet<PartTool>(out var t) ? t : null;
            var root = Services.TryGet<AirTools.Scene.SceneRoot>(out var r) ? r : null;
            req.evidence = MandateEvidence.For(tool, Spec.id, root);
            return req;
        }

        /// POST /checkout/prepare for the order as it is now (on open, seller / quantity / BOM change, a stale nonce, and
        /// after any refused /checkout). A newer prepare supersedes one still in flight.
        public void Prepare()
        {
            if (Spec == null) return;
            var c = Client;
            if (c == null) { Mandate = MandateState.Legacy; Refresh(); return; }
            int seq = ++m_PrepareSeq;
            var req = BuildPrepareRequest();
            if (Mandate != MandateState.Legacy) Mandate = MandateState.Pending;
            m_Notice = null;
            PrepareCount++;
            Refresh();
            c.Prepare(req, (result, prepared, error) => OnPrepared(seq, req, result, prepared, error));
        }

        void OnPrepared(int seq, PrepareRequest req, PartsClient.PrepareResult result, CheckoutPrepared prepared, CheckoutError error)
        {
            if (seq != m_PrepareSeq || !Editable) return;
            switch (result)
            {
                case PartsClient.PrepareResult.Prepared:
                    Prepared = prepared; PreparedRequest = req; PreparedAt = Time.realtimeSinceStartup;
                    Checks = prepared.checks ?? new List<CheckoutCheck>();
                    Mandate = prepared.all_ok ? MandateState.Prepared : MandateState.Refused;
                    Log.Info($"Checkout prepared: cart {prepared.cart?.id} {prepared.cart?.total_usd.ToString("0.00", CultureInfo.InvariantCulture)} USD, {prepared.Packs} packs, " +
                             $"checks [{string.Join(", ", Checks.ConvertAll(k => $"{k.id}:{k.Status}"))}] all_ok={prepared.all_ok}");
                    break;
                case PartsClient.PrepareResult.Refused:
                    Mandate = MandateState.Refused;
                    Prepared = null; PreparedRequest = null; PreparedAt = -1f;
                    Checks = error?.Checks ?? new List<CheckoutCheck>();
                    LastRefusal = error;
                    Log.Warn($"Checkout prepare refused: {error}");
                    break;
                default:
                    // An older server (404 / no answer): check out exactly as before — no checks, no proof, no error spam.
                    if (Mandate != MandateState.Legacy) Log.Info($"Checkout: no measured mandate on this server ({error?.ToString() ?? "no answer"}); paying as before");
                    Mandate = MandateState.Legacy;
                    Prepared = null; PreparedRequest = null; PreparedAt = -1f;
                    Checks = new List<CheckoutCheck>();
                    break;
            }
            Refresh();
        }

        /// Pay is live: a priced seller, and either an older server (legacy) or a prepared cart whose checks all pass.
        public bool CanPay
        {
            get
            {
                if (Spec == null || !Spec.sellers[SellerIndex].Priced || !Editable) return false;
                if (Mandate == MandateState.Legacy) return true;
                return Mandate == MandateState.Prepared && Prepared != null && Prepared.all_ok && !string.IsNullOrEmpty(Prepared.hold_nonce);
            }
        }

        /// The first failing check's words (why Pay is off), or null.
        public string FailingDetail
        {
            get
            {
                foreach (var k in Checks) if (k != null && k.Fails) return k.detail;
                return LastRefusal?.Detail;
            }
        }

        // ---------------- pay ----------------

        /// HoldToConfirm.Confirmed — the only code path that may send a hold nonce (B3 hand-off §5.2).
        void OnHoldConfirmed()
        {
            if (!Editable) return;
            if (!CanPay)
            {
                // Nothing is sent: the server is still checking, or a check failed.
                RefusedHolds++;
                m_Notice = Mandate == MandateState.Pending ? Copy.MandateStillChecking : Copy.MandateBlocked(FailingDetail);
                Log.Info($"Checkout hold refused, nothing sent ({Mandate}: {FailingDetail ?? "still checking"})");
                hold?.ResetHold();
                Refresh();
                return;
            }
            if (Mandate == MandateState.Legacy) { Pay(null); return; }
            Pay(new HoldProof { CartHash = Prepared.cart_hash, Nonce = Prepared.hold_nonce, HeldMs = hold != null ? hold.HeldMs : 0 });
        }

        void Pay(HoldProof proof)
        {
            var c = Client;
            if (c == null) { Fail("no parts client"); return; }   // → "Payment didn't go through · nothing was charged"
            State = CheckoutState.Paying;
            m_Notice = null;
            Refresh();
            if (proof != null)
            {
                // Exactly what was prepared: qty = the prepared packs, the prepared BOM lines. The nonce is spent now.
                var req = PreparedRequest;
                int packs = Prepared.Packs;
                Prepared = null; PreparedAt = -1f;
                c.Checkout(Spec.id, SellerIndex, packs, req?.bom_id, req?.bom_id != null ? req.bom_lines : null, proof, OnResult);
            }
            else c.Checkout(Spec.id, SellerIndex, Packs, BomLines.Count > 0 ? Bom?.Id : null, BomLines.Count > 0 ? BomLines : null, null, OnResult);
        }

        void OnResult(CheckoutReceipt r, CheckoutError error)
        {
            if (error != null)
            {
                bool mandateRefusal = error.Code == 400 || error.Code == 409 || error.Code == 422;
                if (mandateRefusal && Mandate != MandateState.Legacy) { Refused(error); return; }
                Fail(error.Detail);
                if (Mandate != MandateState.Legacy) Prepare();   // the nonce may be spent: a fresh one for the retry
                return;
            }
            Receipt = r;
            State = CheckoutState.Paid;
            var inv = CultureInfo.InvariantCulture;
            // The receipt's own honesty label goes in verbatim (backend api.md §7): "Offline receipt — no payment was authorized…".
            string verb = r.Authorized ? "Bought" : "Ordered (offline)";
            string label = $"{verb} {r.qty} × {Spec.name} from {r.seller} — {PartFormat.Price(r.total_usd)} (Visa •••• {r.card_last4}{(string.IsNullOrEmpty(r.approval_code) ? "" : ", " + r.approval_code)}, {r.receipt_id})" +
                           (string.IsNullOrEmpty(r.label) ? "" : $" · {r.label}") +
                           (r.mandate?.cart != null ? $" · mandate cart {r.mandate.cart.id} …{Tail(r.mandate.cart.hash)}" : "");
            int cam = -1;
            if (r.mandate?.evidence != null && r.mandate.evidence.Count > 0 && r.mandate.evidence[0].camera_id.HasValue) cam = r.mandate.evidence[0].camera_id.Value;
            // Grok G1: the receipt's recall verdict and "see it installed" picture (with its honesty label).
            foreach (var extra in ReceiptExtras(r)) label += $" · {extra}";
            string safetyId = r.part_id ?? Spec.id;
            if (!string.IsNullOrEmpty(r.safety_verdict) && r.safety_verdict != "unknown" && AirTools.Agent.Grok.GrokLanes.Verdict(safetyId) != r.safety_verdict)
                AirTools.Agent.Grok.GrokLanes.SetSafety(safetyId, r.safety_verdict, AirTools.Agent.Grok.GrokLanes.Headline(safetyId));
            Entry = new NotebookEntry("purchase", r.total_usd, "USD", Array.Empty<Vector3>(), DateTime.Now, cam, label)
            {
                DisplayTitle = $"Order · {Copy.Clean(r.seller)}",
                DisplayValue = PartFormat.Price(r.total_usd),
                DisplayDetail = $"{r.qty} × {Copy.Noun(Spec, SearchQuery)} · {(r.Authorized ? "test card" : DemoMode.On ? DemoMode.OfflineReceiptDetail : "no charge")}",   // D7
                PostcardUrl = string.IsNullOrEmpty(r.postcard_url) ? null : r.postcard_url,
            };
            LoadPostcard(r.postcard_url);
            Notebook.Add(Entry);
            UiFeedback.Chime(transform.position);
            UiToast.Show(r.Authorized ? $"✓ Paid {PartFormat.Price(r.total_usd)} · {Copy.Clean(r.seller)}" : $"Receipt saved · {PartFormat.Price(r.total_usd)} · no charge", ColorRole.Success);
            Log.Info($"Checkout authorized: {r.receipt_id} {r.total_usd.ToString("0.00", inv)} USD" +
                     (r.mandate != null ? $" mandate {r.mandate.authorization?.mode}/{r.mandate.authorization?.status} hold {r.mandate.authorization?.hold_ms} ms" : ""));
            Refresh();
            Purchased?.Invoke(r, Spec);
        }

        static string Tail(string s) => string.IsNullOrEmpty(s) || s.Length < 4 ? s ?? "" : s.Substring(s.Length - 4);

        /// 400 / 409 / 422 from /checkout (hand-off §5.4): back to Ready, say why, prepare again (a 422's rows show
        /// until the new prepare answers).
        void Refused(CheckoutError error)
        {
            State = CheckoutState.Ready;
            LastRefusal = error;
            Log.Warn($"Checkout refused: {error}");
            if (error.Code == 422 && error.Checks != null) Checks = error.Checks;
            string why = Copy.MandateRefusal(error.Code, error.Code == 422 ? FailingDetail ?? error.Detail : error.Detail);
            hold?.ResetHold();
            Prepare();
            m_Notice = why;
            Refresh();
        }

        void Fail(string error)
        {
            State = CheckoutState.Failed;
            Log.Warn($"Checkout failed: {error}");
            // Raw error stays in the log ("Checkout failed:" — hcheck.py); the panel says it like a person.
            if (status != null) status.text = $"<color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.danger)}>{Copy.Error(ErrorSurface.Checkout, error)}</color>\nHold Pay to try again";
            hold?.ResetHold();
            Refresh(keepStatus: true);
        }

        // ---------------- limits ----------------

        /// Panel limits (hand-off §6; the user's hand may loosen or clear): POST /commerce/limits, then re-prepare.
        /// Only the keys present change; a null value clears that limit.
        public void SetLimits(Dictionary<string, object> fields, Action<LimitsResult> done = null)
        {
            var c = Client;
            if (c == null) { done?.Invoke(null); return; }
            c.Limits(fields, (result, error) =>
            {
                if (result?.intent != null)
                {
                    Log.Info($"Limits (panel): {MandateText.Limits(result.intent)} changed [{string.Join(",", result.changed ?? new List<string>())}]");
                    LimitsChip.Set(result.intent);
                }
                else Log.Warn($"Limits (panel) failed: {error}");
                if (Editable && Spec != null) Prepare();
                done?.Invoke(result);
            });
        }

        // ---------------- display ----------------

        /// Order lines and card line (docs/ux/specs/W0.9-copy.md C2–C7), pure for tests.
        public static (string lines, string card) Compose(PartSpec spec, int sellerIndex, int quantity, int packs, float total,
            PartBom bom = null, List<int> bomLines = null, float bomTotal = 0f, string query = null)
        {
            var s = spec.sellers[sellerIndex];
            float ship = SellerSort.Shipping(s);
            var inv = CultureInfo.InvariantCulture;
            string shipping = ship > 0 ? UiText.Tabular(PartFormat.Price(ship)) : "Free";
            string qtyLine = s.pack_qty > 1
                ? $"{UiText.Tabular(quantity.ToString(inv))} needed · {UiText.Tabular(packs.ToString(inv))} × pack of {s.pack_qty} at {UiText.Tabular(PartFormat.Price(s.price_usd))}   ·   Shipping {shipping}"
                : $"{UiText.Tabular(quantity.ToString(inv))} × {UiText.Tabular(PartFormat.Price(s.price_usd))}   ·   Shipping {shipping}";
            string extra = "";
            if (bom != null && bomLines != null && bomLines.Count > 0)
            {
                var names = new List<string>();
                foreach (var l in bom.lines) if (bomLines.Contains(l.idx)) names.Add(l.qty > 1 ? $"{l.qty} × {Copy.Clean(l.name)}" : Copy.Clean(l.name));
                extra = $"\nAlso: {string.Join(", ", names)} · {UiText.Tabular(PartFormat.Price(bomTotal))}";
            }
            string lines = $"{SpecCard.Wrap(Copy.Clean(spec.name), 44, 2)}\n{qtyLine}{extra}\n<size=130%>Total {UiText.Tabular(PartFormat.Price(total))}</size>";
            string card = s.Priced ? "Visa •••• 1111 · test card, no real charge" : "No price listed · pick another seller";
            return (lines, card);
        }

        /// The receipt card: approved (sandbox) or saved (the offline receipt: the planned stage path, neutral). The
        /// receipt's label goes in verbatim; a recall verdict and the postcard's "AI preview, not to scale" follow it.
        public static string ReceiptText(CheckoutReceipt r, PartSpec spec, string query = null)
        {
            string sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
            // Verbatim (backend docs/api.md §7): "Offline receipt — no payment was authorized (sandbox not configured)".
            string honesty = string.IsNullOrEmpty(r.label) ? "" : $"\n<color=#{sec}>{r.label}</color>";
            string head = r.Authorized
                ? $"<color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.success)}>✓ Payment approved</color> · {PartFormat.Price(r.total_usd)}\nApproval {Copy.ApprovalCode(r.approval_code)} · Visa •••• {r.card_last4}"
                : $"Receipt saved · {PartFormat.Price(r.total_usd)}\n{r.qty} × {Copy.Noun(spec, query)} · {Copy.Clean(r.seller)}";
            // D7 (W1.8): in DemoMode an offline receipt says plainly that it's a demo and nothing was paid.
            string demo = DemoMode.ReceiptLabel(r.Authorized);
            if (demo != null) head = $"<color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.warning)}>{demo}</color>\n" + head;
            string safety = Copy.ReceiptSafety(r.safety_verdict);
            if (safety.Length > 0)
                safety = $"\n<color=#{ColorUtility.ToHtmlStringRGBA(r.safety_verdict == "recalled" ? UiTheme.Current.colors.danger : UiTheme.Current.colors.warning)}>{safety}</color>";
            string postcard = string.IsNullOrEmpty(r.postcard_url) ? "" : $"\n<color=#{sec}>Picture: {Copy.PostcardLabel}</color>";
            return head + honesty + safety + postcard + "\nSaved to your notebook";
        }

        /// Grok G1: the receipt's extra facts in words (pure): the recall verdict (recalled / caution) and the postcard's
        /// honesty label when the receipt carries one.
        public static List<string> ReceiptExtras(CheckoutReceipt r)
        {
            var extras = new List<string>();
            if (r == null) return extras;
            string safety = Copy.ReceiptSafety(r.safety_verdict);
            if (safety.Length > 0) extras.Add(safety);
            if (!string.IsNullOrEmpty(r.postcard_url)) extras.Add($"postcard: {Copy.PostcardLabel}");
            return extras;
        }

        /// The rows block: the receipt's mandate chain once paid, else the checks (or "Checking your order…"). A part the
        /// safety radar found recalled (G3's GrokState verdict, from show_safety) gets the recall line on top: the agent
        /// said it, the pay panel still opens.
        public string ChecksText()
        {
            string recall = "";
            if (State != CheckoutState.Paid && Spec != null && AirTools.Agent.Grok.GrokLanes.IsRecalled(Spec.id))
                recall = $"<color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.danger)}>{Copy.CheckoutRecall(AirTools.Agent.Grok.GrokLanes.Headline(Spec.id))}</color>";
            string rows = ChecksRows();
            return recall.Length == 0 ? rows : rows.Length == 0 ? recall : recall + "\n" + rows;
        }

        string ChecksRows()
        {
            if (State == CheckoutState.Paid) return Receipt?.mandate != null ? MandateText.Chain(Receipt.mandate) : "";
            if (Mandate == MandateState.Pending && Checks.Count == 0) return $"<color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary)}>{Copy.MandateChecking}</color>";
            return Mandate == MandateState.Legacy ? "" : MandateText.Rows(Checks);
        }

        /// "Limits · ≤ $40 · by Fri · fastest" ("Limits · No limits" when none are set); empty on older servers.
        public string LimitsText()
        {
            if (State == CheckoutState.Paid) return Receipt?.mandate != null ? $"Limits · {MandateText.Limits(Receipt.mandate.intent)}" : "";
            return Prepared != null && Mandate != MandateState.Legacy ? $"Limits · {MandateText.Limits(Prepared.intent)}" : "";
        }

        public void Refresh(bool keepStatus = false)
        {
            if (Spec == null) return;
            var s = Spec.sellers[SellerIndex];
            var text = Compose(Spec, SellerIndex, Quantity, Packs, DisplayTotal, Bom, BomLines, BomTotal, SearchQuery);
            if (title != null) title.text = $"Review order <size=70%><color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary)}>{Copy.Clean(s.name)}</color></size>";
            if (lines != null) lines.text = text.lines;
            if (card != null) card.text = text.card;
            if (checks != null) checks.text = ChecksText();
            if (limits != null) limits.text = LimitsText();
            if (hold != null && hold.button != null) hold.button.SetInteractable(CanPay || State == CheckoutState.Paying);
            bool paid = State == CheckoutState.Paid;
            if (receipt != null) receipt.SetActive(paid);
            if (hold != null && hold.button != null) hold.button.gameObject.SetActive(!paid);
            if (paid && receiptText != null && Receipt != null) receiptText.text = ReceiptText(Receipt, Spec, SearchQuery);
            ShowEvidenceThumb(paid);
            if (keepStatus || status == null) return;
            string danger = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.danger);
            status.text = State switch
            {
                CheckoutState.Ready when m_Notice != null => m_Notice,
                CheckoutState.Ready when Mandate == MandateState.Pending => Copy.MandateChecking,
                CheckoutState.Ready when Mandate == MandateState.Refused => $"<color=#{danger}>{Copy.MandateBlocked(FailingDetail)}</color>",
                CheckoutState.Ready => $"Hold Pay for a second to pay {PartFormat.Price(DisplayTotal)}",
                CheckoutState.Paying => "Authorizing…",
                CheckoutState.Paid => "",
                _ => status.text,
            };
        }

        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap"), s_BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock m_Block;

        /// Grok G1: the receipt's "see it installed" picture (receipt.postcard_url), downloaded once it's paid.
        public Texture2D Postcard { get; private set; }
        string m_PostcardUrl;

        void LoadPostcard(string url)
        {
            if (Postcard != null) { Destroy(Postcard); Postcard = null; }
            m_PostcardUrl = url;
            if (string.IsNullOrEmpty(url) || !Application.isPlaying) return;
            var c = Client;
            if (c == null) return;
            c.GetBytes(url, bytes =>
            {
                if (bytes == null || m_PostcardUrl != url || this == null) return;
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "Postcard" };
                if (!tex.LoadImage(bytes, markNonReadable: true)) { Destroy(tex); Log.Warn($"Checkout: postcard {url} isn't an image"); return; }
                Postcard = tex;
                Log.Info($"Checkout: postcard {url} ({tex.width}×{tex.height}) on the receipt");
                if (State == CheckoutState.Paid) ShowEvidenceThumb(true);
            });
        }

        /// The receipt's evidence photo: the tape's drone thumbnail (by camera id); without one, the postcard (the
        /// receipt text carries its "AI preview, not to scale" label); hidden when there is neither.
        void ShowEvidenceThumb(bool paid)
        {
            if (evidenceThumb == null) return;
            Texture2D tex = null;
            var ev = paid && Receipt?.mandate?.evidence != null && Receipt.mandate.evidence.Count > 0 ? Receipt.mandate.evidence[0] : null;
            if (ev?.camera_id != null && Services.TryGet<NotebookController>(out var nb)) tex = nb.ThumbnailFor(ev.camera_id.Value);
            if (tex == null && paid && Postcard != null && Receipt != null && Receipt.postcard_url == m_PostcardUrl) tex = Postcard;
            evidenceThumb.gameObject.SetActive(tex != null);
            if (tex == null) return;
            m_Block ??= new MaterialPropertyBlock();
            evidenceThumb.GetPropertyBlock(m_Block);
            m_Block.SetTexture(s_BaseMap, tex);
            m_Block.SetColor(s_BaseColor, Color.white);
            evidenceThumb.SetPropertyBlock(m_Block);
        }
    }
}
