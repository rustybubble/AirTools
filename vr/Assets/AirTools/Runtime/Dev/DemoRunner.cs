#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Dev
{
    /// Hosts a scripted, multi-frame agent routine (the demo walkthrough) in Play mode. Nested IEnumerators are run
    /// inline, and an exception anywhere ends the routine through onError instead of silently stopping the coroutine,
    /// so the caller can always clean up (restore the server URL, release forced UI state).
    public class DemoRunner : MonoBehaviour
    {
        static DemoRunner s_Host;

        public static bool Busy { get; private set; }

        /// Start `routine`; false when not playing or another routine is running.
        public static bool Run(IEnumerator routine, Action<Exception> onError, Action onDone)
        {
            if (!Application.isPlaying || Busy || routine == null) return false;
            if (s_Host == null)
            {
                var go = new GameObject("DemoRunner") { hideFlags = HideFlags.DontSave };
                s_Host = go.AddComponent<DemoRunner>();
            }
            Busy = true;
            s_Host.StartCoroutine(s_Host.Drive(routine, onError, onDone));
            return true;
        }

        IEnumerator Drive(IEnumerator root, Action<Exception> onError, Action onDone)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved;
                Exception error = null;
                try { moved = top.MoveNext(); }
                catch (Exception ex) { moved = false; error = ex; }
                if (error != null)
                {
                    try { onError?.Invoke(error); } catch (Exception ex) { Debug.LogException(ex); }
                    break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return top.Current;
            }
            Busy = false;
            try { onDone?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        void OnDestroy() { if (s_Host == this) { s_Host = null; Busy = false; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Host = null; Busy = false; }
    }
}
#endif
