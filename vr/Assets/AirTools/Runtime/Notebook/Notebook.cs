using System;
using System.Collections.Generic;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Notes
{
    /// One logged reading (SPEC §3.2 contract, as a class). ValueSI is the headline value: metres for a distance,
    /// m² for a 3–4 point shape (sides/angles in the extra fields). Points are in scene-root space.
    public sealed class NotebookEntry
    {
        public int Id { get; internal set; }
        public string Tool;
        public double ValueSI;
        public string Unit;
        public Vector3[] Points;
        public DateTime Time;
        public int NearestCameraId = -1;
        public string Label;
        public double[] Sides = Array.Empty<double>();
        public double[] Angles = Array.Empty<double>();
        public double PlanarityError;
        /// Display fields (UX W0.9 §3): set by producers that know better (parts, purchases…); null = derived by
        /// NotebookRow. Label (export, logs, the harness) never changes for display.
        public string DisplayTitle, DisplayValue, DisplayDetail;

        // Backend notebook fields (docs/api.md POST /notebook: the site report, job packet and booth wall read them).
        /// The scene package it was taken on (null: the built-in scene) and that revision's quality ("preview" / "full");
        /// stamped by NotebookController when the entry is added.
        public string Site, Quality;
        /// sitescope: the site key the entry was made on (SiteScope.Current at Notebook.Add: the site id, "built-in" for
        /// the built-in facade). The notebook keeps every site's rows; the panel, the context and the tape lookups read
        /// the current site's.
        public string SiteKey;
        /// sitescope: made on the site loaded now.
        public bool OnCurrentSite => SiteScope.IsCurrent(SiteKey);
        /// cameras.r&lt;rev&gt;.json id and thumb of <see cref="NearestCameraId"/> ("0042", "thumbs/0042.jpg").
        public string CameraKey, ThumbPath;
        /// Notes: goes in the job packet (the packet leaves private notes out).
        public bool Share;
        /// Notes from a survey pin (fix_pin's add_note): the pin and the photo it was found in.
        public string PinId, FrameId;
        /// Placements: the part, how many pieces this entry adds (an array adds its copies; the part it was made from
        /// has its own entry), the run's total, and where it went.
        public string PartId, Where;
        public int Count, Total;
        /// Orders: the "see it installed" picture on the receipt (AI preview, not to scale).
        public string PostcardUrl;
        // placement: a saved placement (PlacementEditor, type placement_pose): its slot ("A") and the part's origin pose in the
        // scene file's frame, glTF [x, y, z, qx, qy, qz, qw] (place_part's pose with anchor "origin").
        public string Slot;
        public double[] Pose;
        // assetgen: the part's size (mm: w, h, d) and finish when the placement was saved (Size & finish).
        public double[] DimsMm;
        public string Finish;

        public NotebookEntry(string tool, double valueSI, string unit, Vector3[] points, DateTime time, int nearestCameraId, string label)
        {
            Tool = tool; ValueSI = valueSI; Unit = unit; Points = points; Time = time; NearestCameraId = nearestCameraId; Label = label;
        }
    }

    public static class Notebook
    {
        static readonly List<NotebookEntry> s_Entries = new List<NotebookEntry>();
        static int s_NextId = 1;

        public static IReadOnlyList<NotebookEntry> Entries => s_Entries;
        public static event Action<NotebookEntry> Added;
        public static event Action<NotebookEntry> Updated;
        public static event Action<NotebookEntry> Removed;

        public static NotebookEntry Last => s_Entries.Count > 0 ? s_Entries[s_Entries.Count - 1] : null;

        public static void Add(NotebookEntry e)
        {
            e.SiteKey ??= SiteScope.Current;   // sitescope
            e.Id = s_NextId++;
            s_Entries.Add(e);
            Core.Log.Info($"Notebook #{e.Id} {e.Tool}: {e.Label}");
            Added?.Invoke(e);
        }

        /// Put back an entry that was removed (redo): same Id, back in Id order.
        public static void Restore(NotebookEntry e)
        {
            if (e == null || s_Entries.Contains(e)) return;
            int i = s_Entries.FindIndex(x => x.Id > e.Id);
            if (i < 0) s_Entries.Add(e); else s_Entries.Insert(i, e);
            Core.Log.Info($"Notebook #{e.Id} restored: {e.Label}");
            Added?.Invoke(e);
        }

        /// Re-publish an entry after its values changed (e.g. a measured point was dragged).
        public static void Update(NotebookEntry e)
        {
            if (!s_Entries.Contains(e)) return;
            Core.Log.Info($"Notebook #{e.Id} updated: {e.Label}");
            Updated?.Invoke(e);
        }

        public static bool Remove(NotebookEntry e)
        {
            if (!s_Entries.Remove(e)) return false;
            Removed?.Invoke(e);
            return true;
        }

        public static NotebookEntry Find(int id) => s_Entries.Find(x => x.Id == id);

        /// sitescope: the newest entry of `tool` made on the site loaded now (null: none).
        public static NotebookEntry LastOnSite(string tool)
        {
            for (int i = s_Entries.Count - 1; i >= 0; i--)
                if (s_Entries[i].Tool == tool && s_Entries[i].OnCurrentSite) return s_Entries[i];
            return null;
        }

        /// switchclean: the newest of `entries` made on the site loaded now (null: none) — the guide rail's last reading, so
        /// the pill never quotes another model's area or level. Pure.
        public static NotebookEntry NewestOnSite(IReadOnlyList<NotebookEntry> entries)
        {
            if (entries == null) return null;
            for (int i = entries.Count - 1; i >= 0; i--) if (entries[i] != null && entries[i].OnCurrentSite) return entries[i];
            return null;
        }

        /// sitescope: the entries made on `site` (null = the site loaded now), oldest first.
        public static List<NotebookEntry> OfSite(string site = null, List<NotebookEntry> into = null)
        {
            into ??= new List<NotebookEntry>();
            into.Clear();
            string key = string.IsNullOrEmpty(site) ? SiteScope.Current : site;
            foreach (var e in s_Entries) if ((e.SiteKey ?? ModelSites.BuiltIn) == key) into.Add(e);
            return into;
        }

        /// Bumped by Clear() (ids start at 1 again), so the server sync knows every later entry is new.
        public static int Generation { get; private set; }

        public static void Clear()
        {
            s_Entries.Clear();
            s_NextId = 1;
            Generation++;
        }

        // D7 (W1.8)
        /// AppCommands.ResetDemo: remove every entry of this session, newest first, announcing each (Removed), so the
        /// notebook window, the guide rail and the parts search see an empty notebook. Ids keep counting up (the laptop
        /// upload sends only ids it hasn't had), and the exported CSV / HTML files on disk are not touched.
        public static int ClearSession()
        {
            int n = 0;
            for (int i = s_Entries.Count - 1; i >= 0; i--)
            {
                if (i >= s_Entries.Count) continue;   // a Removed listener removed more than one
                var e = s_Entries[i];
                s_Entries.RemoveAt(i);
                n++;
                Removed?.Invoke(e);
            }
            return n;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad()
        {
            Clear();
            Added = null; Updated = null; Removed = null;
        }
    }

    /// Picks the source photo that best shows a reading: the nearest package camera whose view frustum contains
    /// every point (falls back to the nearest camera facing the points).
    public static class CameraEvidence
    {
        public static int Nearest(IReadOnlyList<Vector3> points, IReadOnlyList<SceneCameraInfo> cameras)
        {
            if (points == null || points.Count == 0 || cameras == null || cameras.Count == 0) return -1;
            var centroid = Vector3.zero;
            foreach (var p in points) centroid += p;
            centroid /= points.Count;

            int best = -1, fallback = -1;
            float bestD = float.MaxValue, fallbackD = float.MaxValue;
            foreach (var c in cameras)
            {
                float d = Vector3.Distance(c.position, centroid);
                bool facing = Vector3.Dot(c.rotation * Vector3.forward, centroid - c.position) > 0f;
                if (facing && d < fallbackD) { fallbackD = d; fallback = c.id; }
                if (d < bestD && ContainsAll(c, points)) { bestD = d; best = c.id; }
            }
            return best >= 0 ? best : fallback;
        }

        public static bool ContainsAll(SceneCameraInfo c, IReadOnlyList<Vector3> points)
        {
            float tanV = Mathf.Tan(c.verticalFovDeg * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * (c.aspect > 0 ? c.aspect : 1f);
            var inv = Quaternion.Inverse(c.rotation);
            foreach (var p in points)
            {
                var local = inv * (p - c.position);
                if (local.z <= 0.05f) return false;
                if (Mathf.Abs(local.x / local.z) > tanH || Mathf.Abs(local.y / local.z) > tanV) return false;
            }
            return true;
        }
    }
}
