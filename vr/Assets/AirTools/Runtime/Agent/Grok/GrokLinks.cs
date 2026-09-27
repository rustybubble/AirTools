using System;
using System.Reflection;
using AirTools.Core;
using AirTools.Notes;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Opens a link from a G3 panel (recall notice, manual page, packet, report, installer evidence, price compare) in
    /// the Quest browser. Relative server paths get the server base URL. In DemoMode (D7) links are saved to the
    /// notebook instead, so a judge isn't taken out of the app mid-demo.
    public static class GrokLinks
    {
        public static string LastOpened { get; private set; }
        public static int Opened { get; private set; }

        /// D7's DemoMode.SafeLinks. TODO(G3/D7): call AirTools.Core.DemoMode.SafeLinks directly once
        /// feat/d7-demo-controls is merged (this base doesn't have it yet); until then it's read by reflection, false
        /// when the type isn't there.
        public static bool SafeLinks
        {
            get
            {
                if (s_SafeLinks == null)
                {
                    var t = typeof(GrokLinks).Assembly.GetType("AirTools.Core.DemoMode");
                    s_SafeLinks = t?.GetProperty("SafeLinks", BindingFlags.Public | BindingFlags.Static) ?? (object)false;
                }
                return s_SafeLinks is PropertyInfo p && p.GetValue(null) is bool on && on;
            }
        }
        static object s_SafeLinks;

        /// Tests / the harness: force SafeLinks (null = DemoMode decides).
        public static bool? SafeLinksOverride;

        public static string Absolute(string url) => GrokUrls.Absolute(url, ServerConfig.Current);

        /// Open `url` (what = "the recall notice", for the toast). False when there is no link.
        public static bool Open(string url, string what)
        {
            var abs = Absolute(url);
            if (abs == null)
            {
                UiToast.Show("No link for that", ColorRole.Warning);
                return false;
            }
            LastOpened = abs;
            Opened++;
            if (SafeLinksOverride ?? SafeLinks)
            {
                Save(what, abs);
                return true;
            }
            Log.Info($"G3 open link ({what}): {abs}");
            UiToast.Show($"Opening {what} in the browser…", ColorRole.Accent);
            if (Application.isPlaying && !Application.isEditor) Application.OpenURL(abs);
            return true;
        }

        /// DemoMode: the link goes into the notebook (and the laptop report) instead of the headset browser.
        static void Save(string what, string url)
        {
            var e = new NotebookEntry("note", 0, "", Array.Empty<Vector3>(), DateTime.Now, -1, $"Link · {what}: {url}")
            {
                DisplayTitle = "Saved link",
                DisplayValue = Copy.Cap(what ?? "link"),
                DisplayDetail = "saved for later · in the report",
            };
            Notebook.Add(e);
            GrokState.SavedLinks.Add(url);
            Log.Info($"G3 link saved to the notebook (DemoMode), not opened: {url}");
            UiToast.Show("Link saved to your notebook · open it after the demo", ColorRole.Accent);
        }
    }
}
