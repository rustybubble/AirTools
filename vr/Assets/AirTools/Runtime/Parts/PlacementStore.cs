using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Parts
{
    /// One saved placement ("A"): where the anchor is and how the part is turned, in package coordinates (the scene
    /// file's frame, so it survives a re-scale, a reload of the same site / revision and the tabletop), plus the model it
    /// was saved with and when.
    /// assetgen: a part's size (mm: width, height, depth) and finish (null: as listed) as the Size & finish panel set
    /// them: what an undo step, a saved placement and a notebook row keep.
    public struct PartLookState
    {
        public Vector3 DimsMm;
        public string Finish;

        public PartLookState(Vector3 dimsMm, string finish) { DimsMm = dimsMm; Finish = string.IsNullOrWhiteSpace(finish) ? null : finish.Trim(); }

        /// Same size to the half millimetre and the same finish (case aside).
        public bool Same(PartLookState o) =>
            Mathf.Abs(DimsMm.x - o.DimsMm.x) < 0.5f && Mathf.Abs(DimsMm.y - o.DimsMm.y) < 0.5f && Mathf.Abs(DimsMm.z - o.DimsMm.z) < 0.5f
            && string.Equals(Finish ?? "", o.Finish ?? "", StringComparison.OrdinalIgnoreCase);

        public PartDims Dims => new PartDims { w = DimsMm.x, h = DimsMm.y, d = DimsMm.z };

        public override string ToString() => $"{DimsMm.x:0} × {DimsMm.y:0} × {DimsMm.z:0} mm{(Finish != null ? " " + Finish : "")}";
    }

    public sealed class SavedPlacement
    {
        public string Slot;
        public string PartId;
        public Vector3 AnchorPkg;
        public Quaternion RotationPkg = Quaternion.identity;
        public PlacementAnchor Anchor;
        public string Site;
        public int Revision;
        /// Relative to the fit pose, for the chip's words and the notebook: (right, up, out) metres and (turn, tilt, roll)°.
        public Vector3 Offset, TurnTiltRoll;
        public DateTime At;
        /// assetgen: the part's size and finish when saved (Size & finish); null from before they could change.
        public PartLookState? Look;

        public SavedPlacement Copy() => (SavedPlacement)MemberwiseClone();

            public override string ToString() =>
            $"{Slot} {PartId} anchor={AnchorPkg.ToString("F3")} offset(mm)=({Offset.x * 1000f:0}, {Offset.y * 1000f:0}, {Offset.z * 1000f:0}) " +
            $"turn/tilt/roll=({TurnTiltRoll.x:0.#}, {TurnTiltRoll.y:0.#}, {TurnTiltRoll.z:0.#}) {Site}#r{Revision}" + (Look.HasValue ? $" look={Look.Value}" : "");   // assetgen: look
    }

    /// The saved placements of one spot (a cavity or a free placement), A–D, and which one is on.
    public sealed class PlacementSlots
    {
        public readonly List<SavedPlacement> Saved = new List<SavedPlacement>(4);
        public string Active;

        public SavedPlacement Get(string slot)
        {
            foreach (var s in Saved) if (s.Slot == slot) return s;
            return null;
        }

        public bool Has(string slot) => Get(slot) != null;
    }

    /// Every spot's saved placements this session, keyed by PlacementMath.Key (site, revision, target). Pure: the
    /// editor keeps one; tests build their own. Saves and their undo go through Put / Remove (each returns what it
    /// replaced, so an undo puts it back).
    public sealed class PlacementStore
    {
        readonly Dictionary<string, PlacementSlots> m_Spots = new Dictionary<string, PlacementSlots>();

        public int Count
        {
            get { int n = 0; foreach (var kv in m_Spots) n += kv.Value.Saved.Count; return n; }
        }

        public PlacementSlots Spot(string key, bool create = false)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (m_Spots.TryGetValue(key, out var s)) return s;
            if (!create) return null;
            s = new PlacementSlots();
            m_Spots[key] = s;
            return s;
        }

        public SavedPlacement Get(string key, string slot) => Spot(key)?.Get(slot);

        /// The slot that's on for this spot (the one loaded or saved last), if any.
        public SavedPlacement Active(string key)
        {
            var s = Spot(key);
            return s != null && s.Active != null ? s.Get(s.Active) : null;
        }

        /// Save (or overwrite) `p.Slot` and make it the active one. Returns the placement it replaced (null if new).
        public SavedPlacement Put(string key, SavedPlacement p)
        {
            if (p == null || string.IsNullOrEmpty(p.Slot)) return null;
            var spot = Spot(key, create: true);
            SavedPlacement old = null;
            for (int i = 0; i < spot.Saved.Count; i++)
                if (spot.Saved[i].Slot == p.Slot) { old = spot.Saved[i]; spot.Saved[i] = p; break; }
            if (old == null)
            {
                spot.Saved.Add(p);
                spot.Saved.Sort((a, b) => string.CompareOrdinal(a.Slot, b.Slot));
            }
            spot.Active = p.Slot;
            return old;
        }

        /// Forget a slot (a save undone). Returns what was there.
        public SavedPlacement Remove(string key, string slot)
        {
            var spot = Spot(key);
            if (spot == null) return null;
            for (int i = 0; i < spot.Saved.Count; i++)
            {
                if (spot.Saved[i].Slot != slot) continue;
                var old = spot.Saved[i];
                spot.Saved.RemoveAt(i);
                if (spot.Active == slot) spot.Active = null;
                if (spot.Saved.Count == 0 && spot.Active == null) m_Spots.Remove(key);
                return old;
            }
            return null;
        }

        public void SetActive(string key, string slot)
        {
            var spot = Spot(key, create: slot != null);
            if (spot != null) spot.Active = slot;
        }

        /// The slot a new save goes in: the first free of A–D, else the active one (overwritten), else D.
        public string NextSlot(string key)
        {
            var spot = Spot(key);
            string free = PlacementMath.NextSlot(s => spot != null && spot.Has(s));
            return free ?? spot?.Active ?? PlacementMath.Slots[PlacementMath.Slots.Length - 1];
        }

        public void Clear() => m_Spots.Clear();

        public IEnumerable<KeyValuePair<string, PlacementSlots>> All => m_Spots;
    }
}
