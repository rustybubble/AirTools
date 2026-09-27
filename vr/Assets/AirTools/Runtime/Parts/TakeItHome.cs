using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// "Take it home" (SPEC M7): after a purchase, leaving the world puts one of each bought part on the table in
    /// front of you, in passthrough, at true size, labelled with the quantity and total. Re-entering the world
    /// clears the table. presence.md S1: the table point comes from RealTable (site mat, raycast, else tableHeight
    /// above the floor); when the building shrinks back onto the table (Tabletop) the parts stand beside the 1:50
    /// model — on the mat's parts spot, or to the model's right — so the tiny building and the real-size part sit
    /// together. With a TransitionDirector animating the change they appear when it finishes.
    /// modelwheel: with the model floating in front of you (Model view's default) the parts stand on the floor to its
    /// right, clear of the wheel under it (ModelViewLayout.PartsClearance), at true size, and they move with it when it
    /// recentres or re-fits (a new model) — quietly, without the toast again.
    public class TakeItHome : MonoBehaviour
    {
        public Transform rig;
        public Transform head;
        public PartTool tool;
        public PartLoader loader;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("Without a RealTable: the table height above the floor (m).")]
        public float tableHeight = 0.75f;
        public float distance = 0.45f;
        public RealTable table;
        public TabletopController tabletop;
        public TransitionDirector director;
        [Tooltip("Gap between the model and the first part, and between parts (m).")]
        public float gap = 0.06f;

        /// Where the parts went last time ("front" in passthrough, "beside model" in tabletop) and the table source.
        public string LastPlacement { get; private set; } = "";
        public bool Pending { get; private set; }

        public class Bought { public PartSpec Spec; public int Qty; public float Total; public string Seller; }

        public IReadOnlyList<Bought> Purchases => m_Bought;
        public IReadOnlyList<PartInstance> OnTable => m_Home;

        readonly List<Bought> m_Bought = new List<Bought>();
        readonly List<PartInstance> m_Home = new List<PartInstance>();
        readonly List<GameObject> m_Labels = new List<GameObject>();
        /// modelview: the "Bought · …" labels (Model view hides them; the parts stay).
        public IReadOnlyList<GameObject> Labels => m_Labels;
        CheckoutPanel m_Checkout;
        Transform m_Anchor;

        void OnEnable()
        {
            Services.Register(this);
            AppState.Changed += OnMode;
            if (director != null) director.Finished += OnTransitionFinished;
            if (tabletop != null) { tabletop.Recentred += OnModelMoved; tabletop.Refitted += OnModelMoved; }   // modelwheel
        }

        void OnDisable()
        {
            Services.Unregister(this);
            AppState.Changed -= OnMode;
            if (director != null) director.Finished -= OnTransitionFinished;
            if (tabletop != null) { tabletop.Recentred -= OnModelMoved; tabletop.Refitted -= OnModelMoved; }
            if (m_Checkout != null) m_Checkout.Purchased -= OnPurchased;
            m_Checkout = null;
        }

        void Update()
        {
            if (m_Checkout == null && Services.TryGet<CheckoutPanel>(out var c)) { m_Checkout = c; c.Purchased += OnPurchased; }
            // Fallback for a deferred show (e.g. the transition was cut short): once nothing is animating.
            if (Pending && (director == null || !director.Playing)) ShowPending();
        }

        public void OnPurchased(CheckoutReceipt r, PartSpec spec)
        {
            var b = m_Bought.Find(x => x.Spec.id == spec.id);
            if (b == null) { b = new Bought { Spec = spec }; m_Bought.Add(b); }
            b.Qty += Mathf.Max(1, r.qty);
            b.Total += r.total_usd;
            b.Seller = r.seller;
        }

        void OnMode(AppMode from, AppMode to)
        {
            if (to == AppMode.World) { Pending = false; ClearTable(); return; }
            // Passthrough (left the world or the table) or Tabletop (the model on the table): parts on the table —
            // after the animation when there is one, so they land with the building.
            if (director != null && (director.Playing || director.WouldPlay(from, to))) { Pending = true; ClearTable(); return; }
            ShowOnTable();
        }

        /// modelwheel: Model view recentred or a new model re-fitted: the parts go beside it again.
        void OnModelMoved()
        {
            if (m_Home.Count > 0 && AppState.Mode == AppMode.Tabletop && !Pending) ShowOnTable(false);
        }

        void OnTransitionFinished(TransitionKind kind, AppMode from, AppMode to)
        {
            if (Pending && to == AppState.Mode) ShowPending();
        }

        void ShowPending()
        {
            Pending = false;
            if (AppState.Mode != AppMode.World) ShowOnTable();
        }

        /// One of each bought part, side by side on the table, standing upright and facing you: in front of you in
        /// passthrough, beside the model in tabletop.
        public void ShowOnTable() => ShowOnTable(true);

        /// `announce`: the "on the table · real size" toast (not when they only move with the model).
        public void ShowOnTable(bool announce)
        {
            ClearTable();
            if (m_Bought.Count == 0 || rig == null) return;
            EnsureAnchor();
            var h = head != null ? head : rig;
            var fwd = Vector3.ProjectOnPlane(h.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : rig.forward;
            Vector3 centre;
            bool besideModel = false;
            float modelHalf = 0f;
            string source;
            if (AppState.Mode == AppMode.Tabletop && tabletop != null && tabletop.OnTable && tabletop.root != null)
            {
                // Beside the model: on the mat's parts spot, or just right of the model's footprint.
                var rt = tabletop.root.transform;
                var b = tabletop.FootprintLocal(out bool any);
                var modelCentre = rt.TransformPoint(any ? new Vector3(b.center.x, 0f, b.center.z) : Vector3.zero);
                var toModel = Vector3.ProjectOnPlane(modelCentre - h.position, Vector3.up);
                if (toModel.sqrMagnitude > 1e-4f) fwd = toModel.normalized;
                source = tabletop.TableSource;
                if (source == RealTable.SourceMat && table != null) centre = table.PartsSpot(tabletop.TableFrame);
                else
                {
                    besideModel = true;
                    modelHalf = any ? new Vector2(b.extents.x, b.extents.z).magnitude * rt.lossyScale.x : 0.1f;
                    centre = modelCentre;
                    if (tabletop.Floating)
                    {
                        // modelwheel: the model floats at eye height: the parts stand on the floor beside it, right of the wheel.
                        centre = tabletop.ModelCentreWorld;
                        centre.y = rig.position.y;
                        modelHalf = Mathf.Max(modelHalf, ModelViewLayout.PartsClearance);
                        source = "floor";
                    }
                }
            }
            else
            {
                var near = h.position + fwd * distance;
                if (table != null && table.TryGetTableFrame(near, out var frame, out source))
                    centre = source == RealTable.SourceMat ? table.PartsSpot(frame) : frame.position;
                else
                {
                    source = RealTable.SourceAssumed;
                    centre = near;
                    centre.y = rig.position.y + tableHeight;
                }
            }
            var right = Vector3.Cross(Vector3.up, fwd);
            var rot = Quaternion.LookRotation(-fwd, Vector3.up);   // part front (+Z) toward you

            var parts = new List<PartInstance>();
            var bought = new List<Bought>();
            foreach (var b in m_Bought)
            {
                PartInstance src = null;
                if (tool != null) foreach (var p in tool.PlacedParts) if (p != null && p.Spec.id == b.Spec.id) { src = p; break; }
                // sitescope: bought on a site that isn't loaded now: its parked part is still the model (the table is the room's)
                if (src == null && tool != null) foreach (var p in tool.AllParkedParts()) if (p != null && p.Spec.id == b.Spec.id) { src = p; break; }
                PartInstance item = src != null ? src.Clone(m_Anchor, style) : (loader != null ? loader.LoadFromCatalog(b.Spec.id) : null);
                if (item == null) continue;
                item.transform.SetParent(m_Anchor, true);
                item.transform.localScale = Vector3.one;   // true size, whatever the scene's scale
                item.ClearFit();
                if (item.Box != null) item.Box.enabled = false;   // not part of fit checks or grabbing
                parts.Add(item);
                bought.Add(b);
            }
            // Lay them out left to right, bottoms on the table: centred, or starting just right of the model.
            float total = 0f;
            foreach (var p in parts) total += p.Spec.dims_mm.w * 0.001f;
            total += gap * (parts.Count - 1);
            float x = besideModel ? modelHalf + gap : -total * 0.5f;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                float w = p.Spec.dims_mm.w * 0.001f;
                p.transform.rotation = rot;
                var box = p.LocalBox;
                var basePoint = centre + right * (x + w * 0.5f);
                // Origin = mounting face centre; shift so the box's bottom-centre sits on the table point.
                var bottomCentre = new Vector3(box.center.x, box.min.y, box.center.z);
                p.transform.position = basePoint - rot * bottomCentre;
                x += w + gap;
                m_Home.Add(p);
                var b = bought[i];
                m_Labels.Add(Label(p, $"Bought · {b.Qty} × {(b.Qty == 1 ? Copy.Noun(b.Spec, p.SearchQuery) : Copy.Plural(Copy.Noun(b.Spec, p.SearchQuery)))}\n{PartFormat.Price(b.Total)} · {Copy.Clean(b.Seller)}"));
            }
            LastPlacement = $"{(AppState.Mode == AppMode.Tabletop ? "beside model" : "front")} ({source})";
            Log.Info($"Take it home: {m_Home.Count} part(s) on the table, {LastPlacement}");
            if (!announce) return;
            if (m_Home.Count == 1) UiToast.Show($"Your {Copy.Noun(m_Home[0].Spec, m_Home[0].SearchQuery)} is on the table · real size", ColorRole.Success);
            else if (m_Home.Count > 1) UiToast.Show("Your parts are on the table · real size", ColorRole.Success);
        }

        // D7 (W1.8): AppCommands.ResetDemo
        /// Forget this judge's purchases and clear the table (call before leaving the world, so nothing is put out).
        public void ResetSession()
        {
            m_Bought.Clear();
            Pending = false;
            LastPlacement = "";
            ClearTable();
        }

        public void ClearTable()
        {
            foreach (var p in m_Home) if (p != null) { p.gameObject.SetActive(false); Kill(p.gameObject); }
            foreach (var l in m_Labels) if (l != null) Kill(l);
            m_Home.Clear();
            m_Labels.Clear();
        }

        GameObject Label(PartInstance p, string text)
        {
            var go = new GameObject("HomeLabel");
            go.transform.SetParent(m_Anchor, false);
            var top = p.transform.TransformPoint(p.LocalBox.center) + Vector3.up * (p.Spec.dims_mm.h * 0.0005f + 0.05f);
            go.transform.position = top;
            var look = Vector3.ProjectOnPlane(top - (head != null ? head.position : rig.position), Vector3.up);
            if (look.sqrMagnitude > 1e-6f) go.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            var t = UiText.Create(go.transform, "Text", text, TypeRole.Caption, UiText.HandDistance, align: TextAlignmentOptions.Center);
            t.rectTransform.sizeDelta = new Vector2(0.3f, 0.04f);
            return go;
        }

        void EnsureAnchor()
        {
            if (m_Anchor != null) return;
            m_Anchor = new GameObject("TakeItHome").transform;
            m_Anchor.SetParent(rig, false);
        }

        static void Kill(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
