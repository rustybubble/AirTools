using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Notes;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// Seller list for the selected part (SPEC M5): sorted by price (delivered) or delivery date, the recommended
    /// seller tagged with its reason. Pay with Visa opens the checkout panel (it never pays by itself); Open at seller
    /// opens the real product page in the Quest browser.
    public class SellerPanel : MonoBehaviour
    {
        public FloatingWindow window;
        public TextMeshPro title;
        public TextMeshPro reason;
        public SellerRowView[] rows = new SellerRowView[0];
        public GlassButton sortPrice, sortEta;
        public PartTool tool;

        public PartInstance Part { get; private set; }
        public string Sort { get; private set; } = SellerSort.ByPrice;
        public List<int> Order { get; private set; } = new List<int>();
        public bool IsOpen => window != null && window.IsOpen;
        public string LastOpenedUrl { get; private set; }

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        /// Show sellers for a part (null = the selected part). sort: "price" / "eta" / anything voice says.
        public bool Show(PartInstance part, string sort)
        {
            var t = tool != null ? tool : Services.Get<PartTool>();
            part ??= t != null ? t.Selected : null;
            if (part == null) { UiToast.Show("Place or pick a part first", ColorRole.Warning); return false; }
            if (part.Spec.sellers.Count == 0 && part.Source == "catalog") { UiToast.Show("No sellers listed for this part", ColorRole.Warning); return false; }
            Part = part;
            var s = SellerSort.Normalise(sort);
            Sort = string.IsNullOrEmpty(s) ? SellerSort.ByPrice : s;
            Refresh();
            window?.Open();
            Log.Info($"Sellers for {part.Spec.id}, by {Sort}: {string.Join(", ", Order)}");
            RefreshFromServer(part, sort);
            return true;
        }

        public bool Refreshing { get; private set; }

        /// Server re-rank (POST /parts/{id}/sellers?sort=; the first call may add sellers). Its seller order is the one
        /// /checkout's seller_idx refers to, so the spec's list is replaced by it.
        void RefreshFromServer(PartInstance part, string sort)
        {
            if (!Application.isPlaying || part.Source == "catalog" || !Services.TryGet<PartsClient>(out var client)) return;
            Refreshing = true;
            if (reason != null && string.IsNullOrEmpty(reason.text)) reason.text = "Checking sellers…";
            client.Sellers(part.Spec.id, sort, fresh =>
            {
                Refreshing = false;
                if (fresh == null || Part != part) { Refresh(); return; }
                var spec = part.Spec;
                spec.sellers = fresh.sellers;
                spec.recommended_seller = fresh.recommended_seller;
                spec.recommendation_reason = fresh.recommendation_reason;
                spec.sellers_expanded = fresh.sellers_expanded;
                Refresh();
                Log.Info($"Sellers for {spec.id} re-ranked by the server ({PartsClient.ServerSort(sort)}): {spec.sellers.Count}, recommended {spec.recommended_seller}");
            });
        }

        public void Resort(string sort) { if (Part != null) Show(Part, sort); }

        public void Close() => window?.Close();

        public void Refresh()
        {
            if (Part == null) return;
            var spec = Part.Spec;
            Order = SellerSort.Order(spec, Sort, DateTime.Now.DayOfWeek);
            int qty = Quantity();
            string noun = Copy.Noun(spec, Part.SearchQuery);
            if (title != null) title.text = $"Compare prices <size=70%><color=#{ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary)}>{(qty > 1 ? $"{qty} × {Copy.Plural(noun)}" : Copy.Cap(noun))}</color></size>";
            int rec = spec.recommended_seller ?? -1;
            if (reason != null)
            {
                string why = Copy.Reason(spec.recommendation_reason);
                reason.text = rec >= 0 && rec < spec.sellers.Count
                    ? $"Best pick: {Copy.Clean(spec.sellers[rec].name)}{(string.IsNullOrEmpty(why) ? "" : " · " + why)}" : "";
            }
            for (int r = 0; r < rows.Length; r++)
            {
                int si = r < Order.Count ? Order[r] : -1;
                rows[r].Show(si >= 0 ? spec.sellers[si] : null, si, si == rec, qty);
            }
            if (sortPrice != null) sortPrice.SetSelected(Sort == SellerSort.ByPrice);
            if (sortEta != null) sortEta.SetSelected(Sort == SellerSort.ByEta);
        }

        public int Quantity()
        {
            var t = tool != null ? tool : Services.Get<PartTool>();
            return Part == null ? 1 : Mathf.Max(1, t != null ? t.QuantityOf(Part.Spec.id) : 1);
        }

        public void PayRow(int row)
        {
            if (row < 0 || row >= rows.Length || rows[row].SellerIndex < 0) return;
            AppCommands.StartCheckout(rows[row].SellerIndex);
        }

        public void OpenRow(int row)
        {
            if (Part == null || row < 0 || row >= rows.Length || rows[row].SellerIndex < 0) return;
            var url = Part.Spec.sellers[rows[row].SellerIndex].url;
            if (string.IsNullOrEmpty(url)) return;
            LastOpenedUrl = url;
            if (DemoMode.SafeLinks) { SaveLink(Part.Spec.sellers[rows[row].SellerIndex].name, url); return; }   // D7
            Log.Info($"Open at seller: {url}");
            UiToast.Show($"Opening {Copy.Clean(Part.Spec.sellers[rows[row].SellerIndex].name)} in the browser…", ColorRole.Info);
            if (Application.isPlaying && !Application.isEditor) Application.OpenURL(url);
        }

        // D7 (W1.8)
        /// Links saved instead of opened this session (DemoMode).
        public int SavedLinks { get; private set; }

        /// DemoMode: the seller's page goes into the notebook (and the laptop report) instead of the headset browser, which
        /// would take the judge out of the app mid-demo.
        public NotebookEntry SaveLink(string seller, string url)
        {
            var e = new NotebookEntry("note", 0, "", Array.Empty<Vector3>(), DateTime.Now, -1, $"Seller link · {seller}: {url}")
            {
                DisplayTitle = "Seller link",
                DisplayValue = Copy.Clean(seller),
                DisplayDetail = "saved for later · in the report",
            };
            Notebook.Add(e);
            SavedLinks++;
            Log.Info($"Open at seller (DemoMode): link saved to the notebook, not opened: {url}");
            UiToast.Show("Link saved to your notebook · open it after the demo", ColorRole.Info);
            return e;
        }
    }
}
