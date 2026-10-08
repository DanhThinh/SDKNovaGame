#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;

namespace NovaGames.Mobile.Editor
{
    // Chọn trước khi build: có ghi log SDK trong bản build hay không (define NOVA_SDK_LOG, xem SdkLogOutput).
    // Editor luôn có log. Mức log theo RuntimeSdkSettings.LogLevel (Development: Debug, Production: Warning).
    // CI có thể truyền define qua BuildPlayerOptions.extraScriptingDefines thay cho menu này.
    static class SdkLogDefine
    {
        const string Define = "NOVA_SDK_LOG";
        const string MenuPath = "NovaGames/Show SDK Logs In Build";

        static readonly NamedBuildTarget[] Targets =
        {
            NamedBuildTarget.Android,
            NamedBuildTarget.iOS,
            NamedBuildTarget.Standalone,
        };

        [MenuItem(MenuPath, priority = 100)]
        static void Toggle()
        {
            bool enable = !IsEnabled(ActiveTarget());
            foreach (var target in Targets) Set(target, enable);
            global::DebugCustom.Log("[Nova] SDK logs in build: " + (enable ? "ON (" + Define + ")" : "OFF"));
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, IsEnabled(ActiveTarget()));
            return true;
        }

        static NamedBuildTarget ActiveTarget() =>
            NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

        static bool IsEnabled(NamedBuildTarget target) => Defines(target).Contains(Define);

        static void Set(NamedBuildTarget target, bool enable)
        {
            var defines = Defines(target);
            bool changed = enable ? AddIfMissing(defines) : defines.Remove(Define);
            if (changed) PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
        }

        static bool AddIfMissing(List<string> defines)
        {
            if (defines.Contains(Define)) return false;
            defines.Add(Define);
            return true;
        }

        static List<string> Defines(NamedBuildTarget target) =>
            new List<string>(PlayerSettings.GetScriptingDefineSymbols(target)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
