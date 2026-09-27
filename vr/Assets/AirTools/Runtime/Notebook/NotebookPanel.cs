using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Scene;   // sitescope
using UnityEngine;

namespace AirTools.Notes
{
    /// Paging for the wrist notebook: newest entries first, fixed page size. Pure logic (tested in EditMode).
    public class NotebookPage
    {
        public int PageSize { get; }
        public int Page { get; private set; }
        public NotebookPage(int pageSize) { PageSize = Mathf.Max(1, pageSize); }

        public int PageCount(int entryCount) => Mathf.Max(1, (entryCount + PageSize - 1) / PageSize);
        public void Next(int entryCount) => Page = Mathf.Min(Page + 1, PageCount(entryCount) - 1);
        public void Prev() => Page = Mathf.Max(0, Page - 1);
        public void Clamp(int entryCount) => Page = Mathf.Clamp(Page, 0, PageCount(entryCount) - 1);
        public void First() => Page = 0;

        /// Entries shown in row order (row 0 = newest on this page); null for empty rows.
        public NotebookEntry[] Rows(IReadOnlyList<NotebookEntry> entries)
        {
            var rows = new NotebookEntry[PageSize];
            for (int r = 0; r < PageSize; r++)
            {
                int i = entries.Count - 1 - (Page * PageSize + r);
                rows[r] = i >= 0 ? entries[i] : null;
            }
            return rows;
        }

        /// "{Title} · {Value}" (≤ 34 chars, the value never cut) — see NotebookRow.
        public static string RowTitle(NotebookEntry e, int maxChars = 34) => NotebookRow.RowTitle(e, maxChars);

        /// "10:07 AM · 1 × hinge · no charge" — see NotebookRow.
        public static string RowDetail(NotebookEntry e) => NotebookRow.RowDetail(e);
    }

    /// Notebook panel (SPEC M3): opens in front of you (lower half of the view) and stays put until you turn away,
    /// lists readings with their evidence photo.
    /// Poke SHOW to flash a reading's points in the scene, PREV/NEXT to page, EXPORT to write CSV + HTML
    /// (and POST to the laptop). Toggled from the toolbox NOTES button or AppCommands.ShowNotebook.
    public class NotebookPanel : MonoBehaviour, AirTools.UI.IDockTarget
    {
        [Tooltip("Optional: follow this (e.g. a wrist) instead of floating in front of the head.")]
        public Transform anchor;
        public Transform head;
        public Vector3 anchorOffset = new Vector3(0f, 0.16f, 0f);
        [Tooltip("Floating placement (UX W0.7): distance ahead, degrees below the eye line, and how far the head may turn before it re-centres.")]
        public float distance = 0.45f;
        public float downDeg = 20f;
        public float recentreDeg = 40f;
        Vector3 m_Home;
        bool m_HomeSet, m_Placed;
        public GameObject content;
        public TMPro.TextMeshPro title;
        public TMPro.TextMeshPro status;
        public NotebookRowView[] rows;
        [Tooltip("Grab bar; once the window has been moved it stays where it was put until closed.")]
        public AirTools.UI.WindowHandle handle;

        public bool IsOpen { get; private set; }
        public NotebookPage Paging { get; private set; }
        AirTools.UI.GlassSurface m_Panel;
        bool m_PanelSearched;

        /// Declutter M3: where the status line docks — the centre of the panel's top edge, live.
        public bool TryPanelTop(out Vector3 top, out Quaternion rotation)
        {
            if (m_Panel == null && !m_PanelSearched) { m_Panel = AirTools.UI.FloatingWindow.FindPanel(content != null ? content.transform : transform); m_PanelSearched = true; }
            return AirTools.UI.FloatingWindow.PanelTop(IsOpen, m_Panel, out top, out rotation);
        }
        bool m_Dirty = true;

        void Awake() => Paging = new NotebookPage(rows != null && rows.Length > 0 ? rows.Length : 4);

        void OnEnable()
        {
            Services.Register(this);
            Notebook.Added += OnAdded; Notebook.Updated += OnChanged; Notebook.Removed += OnChanged;
            if (Services.TryGet<NotebookController>(out var nb)) nb.ExportStatusChanged += OnExportStatus;
            AppState.Changed += OnMode;
            SetOpen(IsOpen);
        }

        void OnDisable()
        {
            Services.Unregister(this);
            Notebook.Added -= OnAdded; Notebook.Updated -= OnChanged; Notebook.Removed -= OnChanged;
            if (Services.TryGet<NotebookController>(out var nb)) nb.ExportStatusChanged -= OnExportStatus;
            AppState.Changed -= OnMode;
        }

        void OnMode(AppMode from, AppMode to) { if (to == AppMode.Passthrough && IsOpen) SetOpen(false); }

        void OnAdded(NotebookEntry e)
        {
            Paging?.First();   // jump to the newest reading
            m_Dirty = true;
        }

        void OnChanged(NotebookEntry e) => m_Dirty = true;

        void OnExportStatus(string s)
        {
            if (status != null) status.text = AirTools.UI.Copy.ExportStatus(s);
        }

        public void SetOpen(bool open)
        {
            if (open && !IsOpen) { m_HomeSet = false; m_Placed = false; if (handle != null) handle.WasMoved = false; }
            IsOpen = open;
            // One window in front of you (UX W0.7): the notebook shares the main slot with Sellers / Checkout.
            if (open) AirTools.UI.WindowSlot.Claim(this, () => SetOpen(false));
            else AirTools.UI.WindowSlot.Release(this);
            if (content != null) content.SetActive(open);
            m_Dirty = true;
        }

        public void Toggle() => SetOpen(!IsOpen);

        // ---------------- sitescope: the notebook lists the loaded site's rows ----------------

        [Tooltip("sitescope: the All sites toggle (off: the loaded site's readings only; the notebook keeps every row).")]
        public AirTools.UI.GlassButton allSitesToggle;

        /// Every site's rows (off by default: the loaded site's).
        public bool AllSites { get; private set; }

        readonly List<NotebookEntry> m_SiteRows = new List<NotebookEntry>();
        string m_Site;

        /// The rows the panel lists: every row with All sites on, else the loaded site's (NotebookEntry.SiteKey).
        public IReadOnlyList<NotebookEntry> Visible => AllSites ? Notebook.Entries : Notebook.OfSite(null, m_SiteRows);

        public void SetAllSites(bool on)
        {
            if (AllSites == on) return;
            AllSites = on;
            Paging?.First();
            m_Dirty = true;
            Log.Info($"Notebook: {(on ? "all sites" : "this site only")}");
        }

        public void ToggleAllSites() => SetAllSites(!AllSites);

        /// The title's summary with the site it covers: "Kitchen · 2 tapes", "All sites · 5 readings".
        string SiteLine(string summary)
        {
            string site = AllSites ? "All sites" : AirTools.Scene.ModelSites.Name(SiteScope.Current);
            return string.IsNullOrEmpty(summary) ? site : $"{site} · {summary}";
        }
        // end sitescope

        public void Next() { Paging.Next(Visible.Count); m_Dirty = true; }   // sitescope: was Notebook.Entries
        public void Prev() { Paging.Prev(); m_Dirty = true; }

        public NotebookEntry RowEntry(int row)
        {
            var r = Paging.Rows(Visible);   // sitescope: was Notebook.Entries
            return row >= 0 && row < r.Length ? r[row] : null;
        }

        public bool Show(int row)
        {
            var e = RowEntry(row);
            bool ok = e != null && Services.TryGet<NotebookController>(out var nb) && nb.Highlight(e);
            if (ok) Log.Info($"Notebook row {row} highlighted");
            return ok;
        }

        public void Export()
        {
            if (Services.TryGet<NotebookController>(out var nb)) nb.Export();
        }

        void LateUpdate()
        {
            if (head != null && !(handle != null && (handle.Dragging || handle.WasMoved)))
            {
                Vector3 pos;
                if (anchor != null) pos = anchor.position + anchorOffset;
                else
                {
                    var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                    fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                    var home = AirTools.UI.HeadAnchor.PoseFor(head.position, head.forward, distance, downDeg).position;
                    var toHome = Vector3.ProjectOnPlane(m_Home - head.position, Vector3.up);
                    if (!m_HomeSet || Vector3.Angle(toHome, fwd) > recentreDeg || toHome.magnitude > distance * 1.8f) { m_Home = home; m_HomeSet = true; }
                    pos = Vector3.Lerp(transform.position, m_Home, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
                    if (!m_Placed) { pos = m_Home; m_Placed = true; }
                }
                transform.position = pos;
                var toPanel = pos - head.position;
                if (toPanel.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(toPanel.normalized, Vector3.up);
            }
            if (!ReferenceEquals(m_Site, SiteScope.Current)) { m_Site = SiteScope.Current; Paging?.First(); m_Dirty = true; }   // sitescope
            if (IsOpen && m_Dirty) Refresh();
        }

        public void Refresh()
        {
            m_Dirty = false;
            var entries = Visible;   // sitescope: the loaded site's rows (All sites: every row); was Notebook.Entries
            Paging.Clamp(entries.Count);
            if (allSitesToggle != null) allSitesToggle.SetSelected(AllSites);   // sitescope
            if (title != null)
            {
                int pages = Paging.PageCount(entries.Count);
                string summary = entries.Count == 0 ? "No readings yet" : AirTools.UI.Copy.NotebookSummary(entries) + (pages > 1 ? $" · {Paging.Page + 1}/{pages}" : "");
                summary = SiteLine(summary);   // sitescope
                title.text = $"Notebook <color=#{UnityEngine.ColorUtility.ToHtmlStringRGBA(AirTools.UI.UiTheme.Current.colors.textSecondary)}><size=70%>{summary}</size></color>";
            }
            if (entries.Count == 0 && status != null && string.IsNullOrEmpty(status.text)) status.text = "No readings yet · pick Measure on the ring";
            var page = Paging.Rows(entries);
            var nb = Services.Get<NotebookController>();
            for (int i = 0; rows != null && i < rows.Length; i++)
                rows[i].Show(page[i], page[i] != null && nb != null && page[i].OnCurrentSite ? nb.ThumbnailFor(page[i].NearestCameraId) : null);   // sitescope: another scan's camera ids aren't this one's
        }
    }
}
