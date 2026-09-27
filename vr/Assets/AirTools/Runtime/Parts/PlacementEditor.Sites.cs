using System.Collections.Generic;
using AirTools.Scene;
using AirTools.Tools;

namespace AirTools.Parts
{
    /// sitescope: the placement editor's history belongs to the site its parts are on. A site change closes Adjust (the
    /// session so far is one undo step of the site left; it switches after PartTool, so the part is already parked and no
    /// fit is re-checked against the scene being left), drops a model swap or resize in flight, and parks the undo /
    /// redo stacks and the swapped-out models with that site; the arriving site's come back. Spots (package-space poses)
    /// are keyed by the part, so a parked part keeps its own; saved placements are keyed by site and revision already
    /// (PlacementStore).
    public partial class PlacementEditor : ISiteScoped
    {
        sealed class SiteState
        {
            public readonly List<Op> Undo = new List<Op>();
            public readonly List<Op> Redo = new List<Op>();
            public readonly List<PartInstance> Hidden = new List<PartInstance>();
        }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);

        public string LiveSite => Sites.Live;
        /// Undo steps parked for `site`.
        public int ParkedUndo(string site) => Sites.Peek(site)?.Undo.Count ?? 0;

        void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderPlacement);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            m_Grab = default;   // a grab in progress is dropped (no release snap or seat against a scene that's going):
                                // Reapply puts the part back on its spot when the site returns
            if (Adjusting != null) Exit();
            m_Session++;          // a model swap or resize still loading lands nowhere
            SwapBusy = false;
            SiteState park = null;
            if (m_Undo.Count > 0 || m_Redo.Count > 0 || m_Hidden.Count > 0)
            {
                park = new SiteState();
                park.Undo.AddRange(m_Undo);
                park.Redo.AddRange(m_Redo);
                park.Hidden.AddRange(m_Hidden);
                m_Undo.Clear(); m_Redo.Clear(); m_Hidden.Clear();
            }
            var back = Sites.Swap(to, park);
            if (back != null)
            {
                m_Undo.AddRange(back.Undo);
                m_Redo.AddRange(back.Redo);
                m_Hidden.AddRange(back.Hidden);
            }
            m_Reapply = true;
            SlotsVersion++;
            EditHistory.NotifyChanged();
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll())
                foreach (var p in st.Hidden) DestroyPart(p);
        }

        /// PartTool's Clear clears the live site: the spots of parts parked with other sites stay (their way back).
        void ClearLiveSpots()
        {
            var t = Tool;
            if ((t == null || t.ParkedSiteCount == 0) && Sites.Count == 0) { m_Spots.Clear(); return; }
            var parked = t != null ? new HashSet<PartInstance>(t.AllParkedParts()) : new HashSet<PartInstance>();
            foreach (var st in Sites.All)   // the swapped-out models and history of parked sites keep theirs too
            {
                foreach (var p in st.Hidden) parked.Add(p);
                foreach (var op in st.Undo) { if (op.PartBefore != null) parked.Add(op.PartBefore); if (op.PartAfter != null) parked.Add(op.PartAfter); }
                foreach (var op in st.Redo) { if (op.PartBefore != null) parked.Add(op.PartBefore); if (op.PartAfter != null) parked.Add(op.PartAfter); }
            }
            var drop = new List<PartInstance>();
            foreach (var kv in m_Spots) if (kv.Key == null || !parked.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (var p in drop) m_Spots.Remove(p);
        }
    }
}
