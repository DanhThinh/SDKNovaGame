#nullable enable
#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace NovaGames.Mobile.Editor
{
    /// <summary>
    /// Build iOS: link AppTrackingTransparency.framework (cho plugin NovaAtt.mm) và ghi câu giải thích ATT
    /// (NSUserTrackingUsageDescription) từ asset NovaSdkSettings vào Info.plist. Thiếu key này app crash khi hỏi ATT.
    /// </summary>
    sealed class AttBuildStep : IPostprocessBuildWithReport
    {
        const string UsageKey = "NSUserTrackingUsageDescription";

        // Chạy sau bước build của Adjust/Google Mobile Ads để câu giải thích trong NovaSdkSettings là bản cuối cùng.
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;
            var outputPath = report.summary.outputPath;

            var projectPath = PBXProject.GetPBXProjectPath(outputPath);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "AppTrackingTransparency.framework", true);
            project.WriteToFile(projectPath);

            var settings = NovaSettingsLocator.Find("ATT");
            if (settings == null)
            {
                Debug.LogWarning("[Nova] ATT: no NovaSdkSettings asset found; " + UsageKey + " is not written to Info.plist");
                return;
            }
            if (!settings.UsesAtt) return;

            var description = settings.AttUsageDescription;
            if (string.IsNullOrEmpty(description))
            {
                description = NovaSdkSettings.DefaultAttUsageDescription;
                Debug.LogWarning("[Nova] ATT: ATT Usage Description in NovaSdkSettings is empty; using the default text");
            }

            var plistPath = Path.Combine(outputPath, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var existing = plist.root.values.TryGetValue(UsageKey, out var element) ? element.AsString() : null;
            if (!string.IsNullOrEmpty(existing) && existing != description)
                Debug.Log("[Nova] ATT: replacing " + UsageKey + " '" + existing + "' with the text from NovaSdkSettings");
            plist.root.SetString(UsageKey, description);
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
