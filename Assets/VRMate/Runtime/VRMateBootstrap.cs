using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Creates the VRMate components from code instead of placing them in the upstream scene, so that
    /// merging upstream scene changes does not conflict with ours.
    /// </summary>
    public static class VRMateBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateComponents()
        {
            if (BrainLink.Instance != null || Object.FindFirstObjectByType<BrainLink>() != null)
            {
                return;
            }

            var root = new GameObject("VRMate");
            Object.DontDestroyOnLoad(root);
            root.AddComponent<SpeechPlayer>();
            root.AddComponent<BrainLink>();
        }
    }
}
