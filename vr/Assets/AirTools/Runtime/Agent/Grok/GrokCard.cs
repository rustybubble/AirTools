using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    public enum GrokCardKind { None, Postcard, Manual, Packet, Report, Share, Installers, Rules, Quote }

    /// The G3 card (lane G3 of the Grok integration, backend docs/api.md §5): one main-slot window (FloatingWindow /
    /// WindowSlot, 0.45 m, 20° down, one at a time) that shows whichever panel action came last — the "see it installed"
    /// postcard (pinch the picture for before / after), a manual answer with its quote and page, the job packet or the
    /// report as a QR code (encoded in the app), the booth-wall share with its hold-to-post ring, installers with their
    /// evidence links, the rules check's Permit / Money tabs, or the paper-quote check. Parts are built by
    /// GrokPanelsBuilder with UiBuild; this lays them out to fit (a vertical stack) and hangs behaviour on
    /// GlassButton.Clicked. Every backend honesty label is shown verbatim in the footer.
    public class GrokCard : MonoBehaviour
    {
        public FloatingWindow window;
        [Tooltip("The one glass panel, resized to the content.")]
        public GlassSurface panel;
        [Tooltip("Parent of everything laid out top-down (moved so the panel stays centred).")]
        public Transform stack;
        public TextMeshPro title;
        public GlassButton close;
        public GlassButton tabA, tabB;
        [Tooltip("Unit quad for pictures (postcard, booth card).")]
        public MeshRenderer image;
        [Tooltip("Ray + poke target over the picture: a pinch flips before / after.")]
        public GlassButton imageToggle;
        [Tooltip("Unit quad for the QR code.")]
        public MeshRenderer qr;
        public TextMeshPro body;
        public GrokCardRow[] rows = new GrokCardRow[0];
        public TextMeshPro notes;
        [Tooltip("The backend's honesty label, verbatim.")]
        public TextMeshPro footer;
        public GlassButton primary, secondary, more;
        [Tooltip("Hold-to-post (booth wall → X): the only way a post is sent.")]
        public GlassButton hold;
        public HoldToConfirm holdConfirm;
        public TextMeshPro holdCaption;
        public float width = 0.36f;
        public float qrSize = 0.15f;

        public GrokCardKind Kind { get; private set; }
        public bool IsOpen => window != null && window.IsOpen;
        public int Shows { get; private set; }
        /// Plain text of what the card shows (harness [AirTools.Check] lines).
        public string Summary { get; private set; } = "";
        public int Tab { get; private set; }
        public int Page { get; private set; }
        public int Pages { get; private set; } = 1;
        public bool ShowingBefore { get; private set; }
        public string QrText { get; private set; }
        public QrCode Qr { get; private set; }
        public float Height { get; private set; }
        public readonly JobPoll RulesPoll = new JobPoll { Interval = 3f, Timeout = 90f };

        PostcardView m_Postcard;
        ManualAnswerView m_Manual;
        PacketView m_Packet;
        ReportView m_Report;
        ShareView m_Share;
        InstallersView m_Installers;
        RulesView m_Rules;
        QuoteView m_Quote;
        string m_PostedUrl;
        bool m_Posting, m_PostDone;
        float m_ShareShownAt;
        float m_ImageAspect = 16f / 9f;
        string m_ImageUrl;
        Texture2D m_QrTexture;
        MaterialPropertyBlock m_Block;
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        // What the action buttons do for the current content.
        string m_PrimaryUrl, m_PrimaryWhat, m_PrimaryCommand;

        void OnEnable()
        {
            Services.Register(this);
            if (close != null) close.Clicked += Close;
            if (tabA != null) tabA.Clicked += OnTabA;
            if (tabB != null) tabB.Clicked += OnTabB;
            if (more != null) more.Clicked += NextPage;
            if (primary != null) primary.Clicked += OnPrimary;
            if (secondary != null) secondary.Clicked += Flip;
            if (imageToggle != null) imageToggle.Clicked += Flip;
            if (holdConfirm != null) holdConfirm.Confirmed += OnHoldPost;
            foreach (var r in rows) if (r != null) r.LinkClicked += OnRowLink;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (close != null) close.Clicked -= Close;
            if (tabA != null) tabA.Clicked -= OnTabA;
            if (tabB != null) tabB.Clicked -= OnTabB;
            if (more != null) more.Clicked -= NextPage;
            if (primary != null) primary.Clicked -= OnPrimary;
            if (secondary != null) secondary.Clicked -= Flip;
            if (imageToggle != null) imageToggle.Clicked -= Flip;
            if (holdConfirm != null) holdConfirm.Confirmed -= OnHoldPost;
            foreach (var r in rows) if (r != null) r.LinkClicked -= OnRowLink;
        }

        void OnDestroy()
        {
            if (m_QrTexture != null) Destroy(m_QrTexture);
        }

        static GrokClient Client => Services.TryGet<GrokClient>(out var c) ? c : null;
        static string Hex(ColorRole role) => GrokCardLayout.Hex(role);

        public void Close() => window?.Close();

        // ---------------- the actions ----------------

        public bool ShowPostcard(PostcardView v)
        {
            if (v == null) return false;
            m_Postcard = v;
            ShowingBefore = false;
            Open(GrokCardKind.Postcard);
            LoadImage(v.ImageUrl);
            return true;
        }

        public bool ShowManual(ManualAnswerView v)
        {
            if (v == null) return false;
            m_Manual = v;
            Open(GrokCardKind.Manual);
            return true;
        }

        public bool ShowPacket(PacketView v)
        {
            if (v == null) return false;
            m_Packet = v;
            GrokState.PacketId = v.PacketId;
            GrokState.PacketLink = v.Link(ServerConfig.Current);
            GrokState.PacketPublic = v.Public;
            SetQr(GrokState.PacketLink);
            Open(GrokCardKind.Packet);
            return Qr != null;
        }

        /// packet_revoked: the link is dead; take its QR down (whatever else the card shows stays).
        public bool RevokePacket(string packetId)
        {
            bool shown = Kind == GrokCardKind.Packet && m_Packet != null && (string.IsNullOrEmpty(packetId) || m_Packet.PacketId == packetId);
            if (string.IsNullOrEmpty(packetId) || GrokState.PacketId == packetId) { GrokState.PacketId = null; GrokState.PacketLink = null; }
            if (shown)
            {
                m_Packet = null;
                SetQr(null);
                Kind = GrokCardKind.None;
                Close();
            }
            UiToast.Show("Packet link taken down · it no longer opens", ColorRole.Accent);
            Log.Info($"G3 packet {packetId} revoked (QR {(shown ? "removed" : "not on screen")})");
            return true;
        }

        public bool ShowReport(ReportView v)
        {
            if (v == null) return false;
            m_Report = v;
            GrokState.ReportLink = v.Link(ServerConfig.Current);
            SetQr(GrokState.ReportLink);
            Open(GrokCardKind.Report);
            return Qr != null;
        }

        public bool ShowShare(ShareView v)
        {
            if (v == null) return false;
            m_Share = v;
            m_ShareShownAt = Time.unscaledTime;
            m_PostedUrl = null;
            m_Posting = false;
            m_PostDone = false;
            holdConfirm?.ResetHold();
            SetQr(null);
            Open(GrokCardKind.Share);
            LoadImage(v.CardUrl);
            return true;
        }

        public bool ShowInstallers(InstallersView v)
        {
            if (v == null) return false;
            m_Installers = v;
            Open(GrokCardKind.Installers);
            return true;
        }

        /// rules_started: say it's running and poll GET /rules/check/{id} every 3 s (a show_rules for the same check
        /// in this reply or the next one stops the poll).
        public bool StartRules(string checkId)
        {
            if (string.IsNullOrEmpty(checkId)) return false;
            if (m_Rules != null && m_Rules.CheckId == checkId && m_Rules.Done) return true;
            RulesPoll.Start(checkId, Time.unscaledTime);
            UiToast.Show("Checking permits and rebates…", ColorRole.Accent);
            Log.Info($"G3 rules check {checkId} running; polling");
            return true;
        }

        public bool ShowRules(RulesView v)
        {
            if (v == null) return false;
            if (!v.Done && !v.Failed && !string.IsNullOrEmpty(v.CheckId)) return StartRules(v.CheckId);   // still running
            if (RulesPoll.JobId == v.CheckId) RulesPoll.Cancel();
            m_Rules = v;
            GrokState.SetRules(v);
            Tab = 0;
            Open(GrokCardKind.Rules);
            return true;
        }

        public bool ShowQuote(QuoteView v)
        {
            if (v == null) return false;
            m_Quote = v;
            Open(GrokCardKind.Quote);
            return true;
        }

        void Open(GrokCardKind kind)
        {
            Kind = kind;
            Page = 0;
            Shows++;
            window?.Open();
            Render();
            Log.Info($"G3 card {kind}: {Summary}");
        }

        // ---------------- update: the rules poll ----------------

        void Update()
        {
            if (Kind == GrokCardKind.Share && hold != null && hold.gameObject.activeSelf && ShareExpired) Render();
            if (!RulesPoll.Due(Time.unscaledTime)) return;
            var client = Client;
            string id = RulesPoll.JobId;
            if (client == null) { RulesPoll.OnReply(0, null, Time.unscaledTime); return; }
            client.GetJson($"/rules/check/{Uri.EscapeDataString(id)}", (code, text) =>
            {
                if (RulesPoll.JobId != id) return;
                var o = GrokClient.Parse(text);
                var state = RulesPoll.OnReply(code, GrokText.Str(o, "status"), Time.unscaledTime);
                if (state == PollState.Done) ShowRules(RulesView.Parse(o));
                else if (state == PollState.Failed || state == PollState.TimedOut)
                    UiToast.Show("The permit and rebate check didn't finish · ask again in a minute", ColorRole.Warning);
            });
        }

        // ---------------- buttons ----------------

        void OnTabA() { if (Kind == GrokCardKind.Rules) { Tab = 0; Page = 0; Render(); } }
        void OnTabB() { if (Kind == GrokCardKind.Rules) { Tab = 1; Page = 0; Render(); } }

        public void NextPage()
        {
            if (Pages <= 1) return;
            Page = (Page + 1) % Pages;
            Render();
        }

        /// The postcard's before / after (a pinch on the picture, or the Before button).
        public void Flip()
        {
            if (Kind != GrokCardKind.Postcard || m_Postcard == null || !m_Postcard.CanFlip) return;
            ShowingBefore = !ShowingBefore;
            LoadImage(ShowingBefore ? m_Postcard.BeforeUrl : m_Postcard.ImageUrl);
            Render();
        }

        public void SelectTab(int tab)
        {
            if (tab == 0) OnTabA(); else OnTabB();
        }

        void OnPrimary()
        {
            if (!string.IsNullOrEmpty(m_PrimaryCommand))
            {
                // energy_star.missing → "find KUSAH121B" as a normal command (the agent searches for the other half).
                if (AppCommands.SendCommand(m_PrimaryCommand)) UiToast.Show($"Looking for {m_PrimaryCommand.Substring(5)}…", ColorRole.Accent);
                Log.Info($"G3 rules: sent \"{m_PrimaryCommand}\"");
                return;
            }
            if (!string.IsNullOrEmpty(m_PrimaryUrl)) GrokLinks.Open(m_PrimaryUrl, m_PrimaryWhat);
        }

        void OnRowLink(GrokCardRow row, int index)
        {
            if (row?.Row == null || index < 0 || index >= row.Row.Links.Count) return;
            var link = row.Row.Links[index];
            GrokLinks.Open(link.Url, link.Label);
        }

        /// Harness: press row `r`'s link `i` (the same path as a pinch).
        public bool PressRowLink(int r, int i) => r >= 0 && r < rows.Length && rows[r] != null && rows[r].PressLink(i);

        /// The completed 1 s hold on Post: the one path that sends POST /booth/x/confirm (a single-use token).
        /// The share's confirm token is past its 5 minutes: no ring (a hold would only get a 409).
        public bool ShareExpired => m_Share != null && !ShareView.TokenFresh(Time.unscaledTime - m_ShareShownAt);

        void OnHoldPost()
        {
            if (Kind != GrokCardKind.Share || m_Share == null || !m_Share.CanPost || m_Posting || m_PostDone || ShareExpired) return;
            var client = Client;
            if (client == null) { UiToast.Show("Couldn't reach the laptop · nothing was posted", ColorRole.Warning); return; }
            m_Posting = true;
            Render();
            client.BoothConfirm(m_Share.ConfirmToken, (code, text) =>
            {
                m_Posting = false;
                if (code >= 200 && code < 300)
                {
                    m_PostDone = true;
                    m_PostedUrl = ShareView.PostedUrl(text);
                    GrokState.PostedUrl = m_PostedUrl;
                    GrokState.PostsSent++;
                    SetQr(m_PostedUrl);
                    FeedbackEvents.Saved(transform.position);
                    UiToast.Show("Posted on X", ColorRole.Success);
                    Log.Info($"G3 booth: posted {m_PostedUrl}");
                }
                else
                {
                    if (code == 409) m_PostDone = true;   // expired or used: this token can't post again
                    UiToast.Show(ShareView.ConfirmError(code, PartsClient.ServerDetail(text)), ColorRole.Warning);
                    Log.Warn($"G3 booth: X confirm failed ({code}): {text}");
                }
                if (Kind == GrokCardKind.Share) Render();
            });
        }

        // ---------------- pictures ----------------

        void LoadImage(string url)
        {
            m_ImageUrl = url;
            SetImage(null);
            var client = Client;
            if (client == null || string.IsNullOrEmpty(url)) return;
            client.Texture(url, tex =>
            {
                if (m_ImageUrl != url) return;   // a newer picture was asked for meanwhile
                SetImage(tex);
                if (tex != null)
                {
                    float aspect = tex.height > 0 ? (float)tex.width / tex.height : m_ImageAspect;
                    if (Mathf.Abs(aspect - m_ImageAspect) > 0.02f) { m_ImageAspect = Mathf.Clamp(aspect, 0.75f, 2.4f); Render(); }
                }
            });
        }

        void SetImage(Texture2D tex) => SetTexture(image, tex);

        void SetTexture(MeshRenderer r, Texture2D tex)
        {
            if (r == null) return;
            m_Block ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(m_Block);
            if (tex != null) { m_Block.SetTexture(s_BaseMap, tex); m_Block.SetColor(s_BaseColor, Color.white); }
            else { m_Block.SetTexture(s_BaseMap, Texture2D.whiteTexture); m_Block.SetColor(s_BaseColor, UiTheme.Current.colors.surface); }
            r.SetPropertyBlock(m_Block);
        }

        /// Encode `text` as the card's QR (null clears it).
        void SetQr(string text)
        {
            QrText = text;
            Qr = string.IsNullOrEmpty(text) ? null : QrCode.Encode(text);
            if (m_QrTexture != null) { Destroy(m_QrTexture); m_QrTexture = null; }
            if (Qr != null) m_QrTexture = Qr.ToTexture(QrCode.QuietZone, 4);
            else if (!string.IsNullOrEmpty(text)) Log.Warn($"G3 QR: too long to encode ({text.Length} chars): {text}");
            SetTexture(qr, m_QrTexture);
        }

        // ---------------- content ----------------

        sealed class Content
        {
            public string Title = "", Subtitle, TabA, TabB, Body, Notes, Footer, Primary, Secondary, HoldCaption;
            public bool Image, Qr, Hold, HoldEnabled;
            public List<GrokRow> Rows = new List<GrokRow>();
            public int RowsPerPage = 4;
        }

        Content Compose()
        {
            var c = new Content();
            m_PrimaryUrl = m_PrimaryWhat = m_PrimaryCommand = null;
            string sec = Hex(ColorRole.TextSecondary);
            switch (Kind)
            {
                case GrokCardKind.Postcard:
                {
                    var v = m_Postcard;
                    c.Title = "See it installed";
                    c.Subtitle = PartName(v.PartId);
                    c.Image = true;
                    c.Body = v.CanFlip
                        ? (ShowingBefore ? "Before: the photo it was drawn on" : "After: your part in your space") + GrokText.Color(" · pinch the picture to flip", sec)
                        : null;
                    c.Secondary = v.CanFlip ? (ShowingBefore ? "Show after" : "Show before") : null;
                    c.Footer = GrokText.Esc(v.Label);
                    break;
                }
                case GrokCardKind.Manual:
                {
                    var v = m_Manual;
                    c.Title = "From the manual";
                    c.Subtitle = PartName(v.PartId);
                    c.Body = GrokText.Esc(v.Answer) + (v.Cited ? "\n" + GrokText.Color(v.Citation(), sec) : "");
                    if (!string.IsNullOrEmpty(v.PdfUrl)) { c.Primary = "Open manual"; m_PrimaryUrl = v.PdfUrl; m_PrimaryWhat = v.Page.HasValue ? $"the manual at page {v.Page}" : "the manual"; }
                    break;
                }
                case GrokCardKind.Packet:
                {
                    var v = m_Packet;
                    c.Title = "Job packet";
                    c.Qr = Qr != null;
                    c.Body = v.Details() + "\n" + GrokText.Color(GrokText.Esc(Short(QrText)), sec) + LoopbackNote(QrText, sec);
                    c.Footer = GrokText.Esc(v.Label);
                    c.Primary = "Open";
                    m_PrimaryUrl = QrText; m_PrimaryWhat = "the job packet";
                    break;
                }
                case GrokCardKind.Report:
                    c.Title = "Site report";
                    c.Qr = Qr != null;
                    c.Body = "Scan it with a phone, or open it here" + "\n" + GrokText.Color(GrokText.Esc(Short(QrText)), sec) + LoopbackNote(QrText, sec);
                    c.Primary = "Open";
                    m_PrimaryUrl = QrText; m_PrimaryWhat = "the report";
                    break;
                case GrokCardKind.Share:
                {
                    var v = m_Share;
                    c.Title = "Added to the booth wall";
                    if (m_PostDone && !string.IsNullOrEmpty(m_PostedUrl))
                    {
                        c.Qr = Qr != null;
                        c.Body = GrokText.Esc(v.Caption) + "\n" + GrokText.Color("Posted on X · " + GrokText.Esc(Short(m_PostedUrl)), sec);
                        c.Primary = "Open post";
                        m_PrimaryUrl = m_PostedUrl; m_PrimaryWhat = "the post";
                    }
                    else
                    {
                        c.Image = true;
                        c.Body = GrokText.Esc(v.Caption);
                        if (v.CanPost && !m_PostDone && !ShareExpired)
                        {
                            c.Notes = "Post on X: " + GrokText.Color("“" + GrokText.Esc(v.PreviewText) + "”", sec);
                            c.Hold = true;
                            c.HoldEnabled = !m_Posting;
                            c.HoldCaption = m_Posting ? "Posting…" : "Hold 1 s to post it on X";
                        }
                        else if (v.CanPost && !m_PostDone)
                            c.Notes = GrokText.Color("The post offer expired · say “add it to the wall” again to post", sec);
                        else if (!v.XReady) c.Notes = GrokText.Color("Posting to X isn't set up on the laptop", sec);
                    }
                    break;
                }
                case GrokCardKind.Installers:
                {
                    var v = m_Installers;
                    c.Title = "Installers near you";
                    c.Body = string.IsNullOrWhiteSpace(v.Summary) ? null : GrokText.Esc(v.Summary);
                    c.Rows = v.RowsFor(Hex);
                    c.RowsPerPage = 3;
                    c.Notes = GrokText.Color(InstallersView.Note, sec);
                    if (v.Rows.Count == 0) c.Body = (c.Body ?? "") + GrokText.Color("\nNo installer came back with evidence.", sec);
                    break;
                }
                case GrokCardKind.Rules:
                {
                    var v = m_Rules;
                    c.Title = "Permits and rebates";
                    c.Subtitle = v.Subtitle;
                    c.TabA = "Permit"; c.TabB = "Money";
                    c.Body = Tab == 0 ? v.PermitHeader(Hex) : v.MoneyHeader(Hex);
                    c.Rows = Tab == 0 ? v.PermitRows(Hex) : v.MoneyRows(Hex);
                    c.Footer = GrokText.Esc(v.Label);
                    if (Tab == 1 && v.FindCommand != null) { c.Primary = $"Find {v.EnergyStarMissing}"; m_PrimaryCommand = v.FindCommand; }
                    break;
                }
                case GrokCardKind.Quote:
                {
                    var v = m_Quote;
                    c.Title = "Quote check";
                    c.Subtitle = v.Subtitle;
                    c.Rows = v.Rows(Hex);
                    c.Notes = v.Notes(Hex);
                    c.Footer = GrokText.Esc(v.Label);
                    break;
                }
            }
            return c;
        }

        /// The server is the headset's USB tunnel (127.0.0.1): a phone can't open that address.
        static string LoopbackNote(string url, string sec) =>
            GrokUrls.IsLoopback(url) ? "\n" + GrokText.Color("This address is the USB tunnel: open it here or on the laptop, not on a phone", hex: Hex(ColorRole.Warning)) : "";

        static string Short(string url) => GrokText.Clip((url ?? "").Replace("https://", "").Replace("http://", ""), 48);

        /// The selected part's name when it's the one the action names, else the id.
        static string PartName(string partId)
        {
            if (string.IsNullOrEmpty(partId)) return null;
            if (Services.TryGet<PartTool>(out var tool) && tool.Selected != null && tool.Selected.Spec.id == partId)
                return GrokText.Clip(tool.Selected.Spec.name, 36);
            return partId;
        }

        // ---------------- layout ----------------

        /// Rebuild the card for the current content (text, rows, buttons) and resize the panel.
        public void Render()
        {
            if (Kind == GrokCardKind.None) return;
            var c = Compose();
            Pages = Mathf.Max(1, Mathf.CeilToInt(c.Rows.Count / (float)Mathf.Max(1, c.RowsPerPage)));
            Page = Mathf.Clamp(Page, 0, Pages - 1);
            float W = width, pad = 0.016f, inner = W - 2f * pad, x0 = -W * 0.5f + pad;
            float y = -0.012f;
            string sec = Hex(ColorRole.TextSecondary);

            // Header: title (+ subtitle at 70 % of the Heading size, the one allowed shrink) and Close.
            if (title != null)
            {
                title.text = string.IsNullOrWhiteSpace(c.Subtitle) ? c.Title : $"{c.Title} <size=70%><color=#{sec}>{GrokText.Esc(c.Subtitle)}</color></size>";
                title.overflowMode = TextOverflowModes.Ellipsis;
                title.textWrappingMode = TextWrappingModes.NoWrap;
                title.alignment = TextAlignmentOptions.Left;
                title.rectTransform.pivot = new Vector2(0f, 0.5f);
                title.rectTransform.sizeDelta = new Vector2(inner - 0.075f, 0.03f);
                title.transform.localPosition = new Vector3(x0, y - 0.017f, 0f);
            }
            if (close != null) close.transform.localPosition = new Vector3(W * 0.5f - pad - 0.03f, y - 0.017f, 0f);
            y -= 0.04f;

            // Tabs.
            bool tabs = c.TabA != null;
            SetActive(tabA, tabs); SetActive(tabB, tabs);
            if (tabs)
            {
                tabA.SetText(c.TabA); tabB.SetText(c.TabB);
                tabA.SetSelected(Tab == 0); tabB.SetSelected(Tab == 1);
                tabA.transform.localPosition = new Vector3(x0 + 0.05f, y - 0.015f, 0f);
                tabB.transform.localPosition = new Vector3(x0 + 0.154f, y - 0.015f, 0f);
                y -= 0.038f;
            }

            // Picture (and the pinch target over it).
            SetActive(image, c.Image); SetActive(imageToggle, c.Image && Kind == GrokCardKind.Postcard && m_Postcard != null && m_Postcard.CanFlip);
            if (c.Image)
            {
                float h = inner / m_ImageAspect;
                image.transform.localPosition = new Vector3(0f, y - h * 0.5f, -0.0005f);
                image.transform.localScale = new Vector3(inner, h, 1f);
                if (imageToggle != null && imageToggle.gameObject.activeSelf)
                {
                    imageToggle.transform.localPosition = new Vector3(0f, y - h * 0.5f, -0.001f);
                    imageToggle.transform.localScale = new Vector3(inner, h, 1f);   // built 1 × 1 m
                }
                y -= h + 0.008f;
            }

            // QR code.
            SetActive(qr, c.Qr);
            if (c.Qr)
            {
                qr.transform.localPosition = new Vector3(0f, y - qrSize * 0.5f, -0.0005f);
                qr.transform.localScale = new Vector3(qrSize, qrSize, 1f);
                y -= qrSize + 0.008f;
            }

            y = PlaceText(body, c.Body, x0, y, inner);

            // Rows (this page).
            var pageRows = c.Rows.Skip(Page * c.RowsPerPage).Take(c.RowsPerPage).ToList();
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                float h = rows[i].Show(i < pageRows.Count ? pageRows[i] : null, inner);
                if (h <= 0f) continue;
                rows[i].transform.localPosition = new Vector3(0f, y - h * 0.5f, 0f);
                y -= h + 0.004f;
            }
            if (pageRows.Count > 0) y -= 0.004f;

            y = PlaceText(notes, c.Notes, x0, y, inner);
            y = PlaceText(footer, c.Footer, x0, y, inner);

            // Action row: primary, secondary (left), More (right).
            bool anyButton = c.Primary != null || c.Secondary != null || Pages > 1;
            SetActive(primary, c.Primary != null); SetActive(secondary, c.Secondary != null); SetActive(more, Pages > 1);
            if (anyButton)
            {
                float bx = x0;
                if (c.Primary != null) { primary.SetText(c.Primary); primary.transform.localPosition = new Vector3(bx + 0.06f, y - 0.017f, 0f); bx += 0.128f; }
                if (c.Secondary != null) { secondary.SetText(c.Secondary); secondary.transform.localPosition = new Vector3(bx + 0.05f, y - 0.017f, 0f); }
                if (Pages > 1)
                {
                    more.SetText($"More · {Page + 1}/{Pages}");
                    more.transform.localPosition = new Vector3(W * 0.5f - pad - 0.045f, y - 0.017f, 0f);
                }
                y -= 0.04f;
            }

            // Hold-to-post.
            SetActive(hold, c.Hold);
            SetActive(holdCaption, c.Hold);
            if (c.Hold)
            {
                hold.SetInteractable(c.HoldEnabled);
                hold.transform.localPosition = new Vector3(0f, y - 0.036f, 0f);
                holdCaption.text = c.HoldCaption;
                holdCaption.alignment = TextAlignmentOptions.Center;
                holdCaption.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                holdCaption.rectTransform.sizeDelta = new Vector2(inner, 0.02f);
                holdCaption.transform.localPosition = new Vector3(0f, y - 0.08f, 0f);
                y -= 0.096f;
            }

            y -= 0.01f;
            Height = -y;
            if (stack != null) stack.localPosition = new Vector3(0f, Height * 0.5f, 0f);
            if (panel != null) panel.SetSize(new Vector2(W, Height));
            if (window != null && window.handle != null) window.handle.transform.localPosition = new Vector3(0f, -Height * 0.5f - 0.016f, 0f);
            Summary = Plain(c, pageRows);
        }

        static float PlaceText(TextMeshPro tmp, string text, float x0, float y, float width)
        {
            if (tmp == null) return y;
            float h = GrokCardLayout.TextHeight(tmp, text, width);
            GrokCardLayout.Place(tmp, text, new Vector2(x0, y), width, h);
            return string.IsNullOrEmpty(text) ? y : y - h - 0.008f;
        }

        static void SetActive(Component c, bool on)
        {
            if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
        }

        static readonly Regex s_Tags = new Regex("<[^>]*>");
        public static string Strip(string s) => string.IsNullOrEmpty(s) ? "" : s_Tags.Replace(s, "").Replace("\n", " | ");

        string Plain(Content c, List<GrokRow> pageRows)
        {
            var parts = new List<string> { Strip(c.Title) + (string.IsNullOrWhiteSpace(c.Subtitle) ? "" : $" ({Strip(c.Subtitle)})") };
            if (c.TabA != null) parts.Add($"tab={(Tab == 0 ? c.TabA : c.TabB)}");
            if (c.Image) parts.Add($"image={(ShowingBefore ? "before" : "after")}:{m_ImageUrl}");
            if (c.Qr) parts.Add($"qr=v{Qr?.Version}:{QrText}");
            if (!string.IsNullOrEmpty(c.Body)) parts.Add(Strip(c.Body));
            if (pageRows.Count > 0) parts.Add($"rows {Page * c.RowsPerPage + 1}-{Page * c.RowsPerPage + pageRows.Count}/{c.Rows.Count}: " + string.Join(" / ", pageRows.Select(r => Strip(r.Text) + (r.Links.Count > 0 ? $" [{string.Join(", ", r.Links.Select(l => l.Label))}]" : ""))));
            if (!string.IsNullOrEmpty(c.Notes)) parts.Add(Strip(c.Notes));
            if (!string.IsNullOrEmpty(c.Footer)) parts.Add("label: " + Strip(c.Footer));
            if (c.Primary != null) parts.Add($"[{c.Primary}]");
            if (c.Secondary != null) parts.Add($"[{c.Secondary}]");
            if (c.Hold) parts.Add($"[hold: {c.HoldCaption}]");
            return string.Join(" · ", parts);
        }
    }
}
