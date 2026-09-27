using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AirTools.Scene
{
    /// scalemodels: the headset's copy of the scene packages, kept under `root` (SceneStreamer: persistentDataPath/scenes).
    /// - **Layout.** `<root>/<site>/<file>`. Revision-named files never change. `scene.json` is the last good manifest,
    ///   written only once every file it names is in.
    /// - **Listing.** `<root>/listing.json` is the last good GET /scenes: the Model view switcher shows it at start and
    ///   while the laptop is away.
    /// - **Picture.** `<root>/<site>/site-picture.r<rev>.jpg` is the switcher card's picture.
    ///
    /// A site is **on the headset** when its package opens with the laptop away: scene.json plus every file the load
    /// would fetch (PackageFiles) are in the cache. Pure file logic on a root directory: the EditMode tests use a temp dir.
    public static class SceneCache
    {
        public const string ManifestFile = "scene.json";
        public const string ListingFile = "listing.json";
        const string PicturePrefix = "site-picture.r", PictureSuffix = ".jpg";

        public static string SiteDir(string root, string site) => Path.Combine(root, site);
        public static string PathOf(string root, string site, string file) => Path.Combine(root, site, file.Replace('/', Path.DirectorySeparatorChar));
        public static string ListingPath(string root) => Path.Combine(root, ListingFile);
        public static string PictureName(int revision) => $"{PicturePrefix}{Math.Max(0, revision)}{PictureSuffix}";

        // ---------------- the files of a package ----------------

        /// The files a load of `m` fetches besides scene.json, small first (the order the prefetch downloads them):
        /// - with scene parts (`doc`: its parts file, read): the parts file, cameras, structure, the split collision, the
        ///   cavities, the split mesh;
        /// - else in one piece (no parts, or the parts file not read yet): cameras, structure, collision, mesh.
        public static List<string> PackageFiles(SceneManifest m, ScenePartsDoc doc)
        {
            var files = new List<string>();
            if (m == null) return files;
            var split = m.HasParts && doc != null ? ScenePartsFiles.For(m, doc) : default;
            bool isSplit = m.HasParts && doc != null && !string.IsNullOrEmpty(split.Mesh);
            if (isSplit) Add(files, m.PartsFile);
            Add(files, m.cameras);
            if (m.HasStructure) Add(files, m.StructureFile);
            if (isSplit) { Add(files, split.Collision); Add(files, split.Cavities); Add(files, split.Mesh); }
            else { Add(files, m.CollisionFile); Add(files, m.MeshFile); }
            return files;
        }

        static void Add(List<string> files, string f)
        {
            if (!string.IsNullOrEmpty(f) && !files.Contains(f)) files.Add(f);
        }

        /// The files of `files` not yet in the cache, in order: what a resumed download still needs.
        public static List<string> Missing(IReadOnlyList<string> files, Func<string, bool> cached)
        {
            var missing = new List<string>();
            if (files == null) return missing;
            foreach (var f in files) if (!string.IsNullOrEmpty(f) && (cached == null || !cached(f))) missing.Add(f);
            return missing;
        }

        /// Where the load keeps a package file: revision-named files under their own name, others per revision
        /// (SceneStreamer.CacheName); either spelling counts as cached.
        public static bool FileCached(string root, string site, string file, int revision)
        {
            if (string.IsNullOrEmpty(file)) return false;
            if (NonEmpty(PathOf(root, site, file))) return true;
            return !SceneStreamer.IsRevisionNamed(file) && revision > 0 && NonEmpty(PathOf(root, site, SceneStreamer.CacheName(file, revision)));
        }

        static bool NonEmpty(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists && fi.Length > 0; }
            catch (Exception) { return false; }
        }

        /// The cached manifest of `site` (null: none, or unreadable).
        public static SceneManifest CachedManifest(string root, string site)
        {
            try
            {
                var p = PathOf(root, site, ManifestFile);
                return File.Exists(p) ? SceneManifest.Parse(File.ReadAllText(p)) : null;
            }
            catch (Exception) { return null; }
        }

        /// The cached parts file of `m` (null: none named, not cached, or unreadable).
        public static ScenePartsDoc CachedParts(string root, string site, SceneManifest m)
        {
            if (m == null || !m.HasParts) return null;
            try
            {
                var p = PathOf(root, site, m.PartsFile);
                return File.Exists(p) ? ScenePartsDoc.Parse(File.ReadAllText(p)) : null;
            }
            catch (Exception) { return null; }
        }

        /// The site's package opens with the laptop away: its scene.json and every file the load would fetch are cached.
        /// `revision`: the cached revision (0 = none).
        public static bool IsComplete(string root, string site, out int revision)
        {
            revision = 0;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(site)) return false;
            var m = CachedManifest(root, site);
            if (m == null) return false;
            revision = m.revision;
            var files = PackageFiles(m, CachedParts(root, site, m));
            int rev = m.revision;
            return Missing(files, f => FileCached(root, site, f, rev)).Count == 0;
        }

        /// The sites with a cached scene.json (whatever their state), A–Z.
        public static List<string> CachedSites(string root)
        {
            var sites = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return sites;
                foreach (var dir in Directory.GetDirectories(root))
                    if (File.Exists(Path.Combine(dir, ManifestFile))) sites.Add(Path.GetFileName(dir));
            }
            catch (Exception) { }
            sites.Sort(string.CompareOrdinal);
            return sites;
        }

        // ---------------- the last good listing ----------------

        public static string ListingJson(IEnumerable<SceneListing> list) =>
            JsonConvert.SerializeObject(list ?? new List<SceneListing>(), Formatting.Indented);

        /// listing.json → the sites (empty for none or garbage; entries without a site dropped).
        public static List<SceneListing> ParseListing(string json)
        {
            var list = new List<SceneListing>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var parsed = SceneManifest.ParseAny<List<SceneListing>>(json);
                if (parsed != null) foreach (var s in parsed) if (s != null && !string.IsNullOrWhiteSpace(s.site)) list.Add(s);
            }
            catch (Exception) { }
            return list;
        }

        public static List<SceneListing> ReadListing(string root)
        {
            try { var p = ListingPath(root); return File.Exists(p) ? ParseListing(File.ReadAllText(p)) : new List<SceneListing>(); }
            catch (Exception) { return new List<SceneListing>(); }
        }

        /// Keep a good listing (an empty one isn't kept: it would forget every model).
        public static bool WriteListing(string root, IReadOnlyCollection<SceneListing> list)
        {
            if (string.IsNullOrEmpty(root) || list == null || list.Count == 0) return false;
            return WriteAtomic(ListingPath(root), System.Text.Encoding.UTF8.GetBytes(ListingJson(list)));
        }

        /// Every site the app knows, the listing's first (its order), then cached sites it doesn't name (a package loaded
        /// before listings were kept); no duplicates, the `ignore`d ones out.
        public static List<SceneListing> Known(IEnumerable<SceneListing> listing, IEnumerable<string> cachedSites, ICollection<string> ignore = null)
        {
            var known = new List<SceneListing>();
            var seen = new HashSet<string>();
            if (listing != null)
                foreach (var s in listing)
                    if (s != null && !string.IsNullOrWhiteSpace(s.site) && (ignore == null || !ignore.Contains(s.site)) && seen.Add(s.site)) known.Add(s);
            if (cachedSites != null)
                foreach (var site in cachedSites)
                    if (!string.IsNullOrWhiteSpace(site) && (ignore == null || !ignore.Contains(site)) && seen.Add(site))
                        known.Add(new SceneListing { site = site, revision = 0 });
            return known;
        }

        // ---------------- writing ----------------

        /// Write through a temp file and a rename, so a crash or a full disk never leaves a half file that looks cached.
        public static bool WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(tmp, data ?? Array.Empty<byte>());
                Promote(tmp, path);
                return true;
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return false;
            }
        }

        /// A finished download (`tmp`) becomes `path`.
        public static void Promote(string tmp, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// The newest site picture on the headset for `site` at `revision` or older (null: none).
        public static string PicturePath(string root, string site, int revision)
        {
            try
            {
                var dir = SiteDir(root, site);
                if (!Directory.Exists(dir)) return null;
                if (revision > 0 && NonEmpty(Path.Combine(dir, PictureName(revision)))) return Path.Combine(dir, PictureName(revision));
                string best = null;
                int bestRev = -1;
                foreach (var f in Directory.GetFiles(dir, PicturePrefix + "*" + PictureSuffix))
                {
                    var name = Path.GetFileName(f);
                    if (!int.TryParse(name.Substring(PicturePrefix.Length, name.Length - PicturePrefix.Length - PictureSuffix.Length), out int r)) continue;
                    if ((revision <= 0 || r <= revision) && r > bestRev && NonEmpty(f)) { best = f; bestRev = r; }
                }
                return best;
            }
            catch (Exception) { return null; }
        }
    }
}
