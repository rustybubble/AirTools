using System;

namespace AirTools.Core
{
    /// The session id the server keys agent memory, notebooks and receipts by (backend docs/api.md §6: keep it
    /// constant for the whole session). One per app run.
    public static class SessionInfo
    {
        static string s_Id;

        public static string Id => s_Id ??= "quest-" + Guid.NewGuid().ToString("N").Substring(0, 10);

        /// Tests: pin a known id.
        public static void Set(string id) => s_Id = id;

        // D7 (W1.8)
        /// AppCommands.ResetDemo: a new id for the next judge, so the server's agent memory, notebook and limits for this
        /// person don't carry over. Returns it.
        public static string Renew()
        {
            s_Id = null;
            return Id;
        }
    }
}
