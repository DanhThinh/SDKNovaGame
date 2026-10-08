#nullable enable
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NovaGames.Mobile.Editor
{
    /// <summary>Tìm asset NovaSdkSettings dùng cho bản build: ưu tiên asset của game, sau đó mới tới asset của sample Demo.</summary>
    static class NovaSettingsLocator
    {
        public static NovaSdkSettings? Find(string purpose)
        {
            var paths = AssetDatabase.FindAssets("t:" + nameof(NovaSdkSettings))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => IsSample(path) ? 1 : 0)
                .ToArray();
            if (paths.Length == 0) return null;
            if (paths.Length > 1) Debug.Log("[Nova] " + purpose + ": found " + paths.Length + " NovaSdkSettings assets; using " + paths[0]);
            return AssetDatabase.LoadAssetAtPath<NovaSdkSettings>(paths[0]);
        }

        // Sample import qua Package Manager nằm ở Assets/Samples/NovaGames Mobile SDK/<version>/Demo; project SDK để ở Assets/NovaSdkSamples.
        static bool IsSample(string path) =>
            path.StartsWith("Assets/Samples/", System.StringComparison.Ordinal) || path.Contains("/NovaSdkSamples/");
    }
}
