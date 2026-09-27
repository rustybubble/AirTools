#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;

namespace AirTools.Dev
{
    /// Declutter census (S2): samples UiCensus every frame in Play mode and development builds, so
    /// AgentHarness.Surfaces() can say how many heads-up and head-relative surfaces were up at most since
    /// SurfacesReset(). Property reads only: no allocation per frame.
    public class UiCensusWatch : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (FindAnyObjectByType<UiCensusWatch>() != null) return;
            var go = new GameObject("UiCensusWatch") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<UiCensusWatch>();
            UiCensus.ResetMax();
        }

        void LateUpdate() => UiCensus.Tick();
    }
}
#endif
