#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;

namespace NovaGames.Mobile.Editor
{
    // Vendor cài qua .unitypackage không có versionDefines, nên probe assembly đã load
    // và set scripting define NOVA_* cho adapter asmdef (defineConstraints). Vendor bị gỡ thì gỡ define để không lỗi compile.
    // Define chỉ cho biết vendor đã CÀI; AdsMediationSelection mới quyết định format nào dùng provider nào.
    [InitializeOnLoad]
    static class VendorDefines
    {
        static readonly (string Define, string TypeName)[] Probes =
        {
            ("NOVA_MAX", "MaxSdk, MaxSdk.Scripts"),
            ("NOVA_ADMOB", "GoogleMobileAds.Api.MobileAds, GoogleMobileAds"),
            ("NOVA_ADJUST", "AdjustSdk.Adjust, AdjustSdk.Scripts"),
            ("NOVA_UMP", "GoogleMobileAds.Ump.Api.ConsentInformation, GoogleMobileAds.Ump"),
            ("NOVA_PLAY_REVIEW", "Google.Play.Review.ReviewManager, Google.Play.Review"),
        };

        static readonly NamedBuildTarget[] Targets =
        {
            NamedBuildTarget.Android,
            NamedBuildTarget.iOS,
            NamedBuildTarget.Standalone,
        };

        static VendorDefines()
        {
            EditorApplication.delayCall += Sync;
        }

        [MenuItem("NovaGames/Refresh Vendor Defines")]
        static void Sync()
        {
            foreach (var target in Targets)
            {
                var current = PlayerSettings.GetScriptingDefineSymbols(target);
                var defines = new List<string>(current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                bool changed = false;
                foreach (var (define, typeName) in Probes)
                {
                    bool installed = Type.GetType(typeName, throwOnError: false) != null;
                    if (installed && !defines.Contains(define))
                    {
                        defines.Add(define);
                        changed = true;
                    }
                    else if (!installed && defines.Remove(define))
                    {
                        changed = true;
                    }
                }
                if (!changed) continue;
                PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
                global::DebugCustom.Log("[Nova] Scripting defines for " + target.TargetName + ": " + string.Join(";", defines));
            }
        }
    }
}
