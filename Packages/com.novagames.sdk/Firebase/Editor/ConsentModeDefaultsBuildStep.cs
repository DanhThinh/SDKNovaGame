#nullable enable
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

namespace NovaGames.Mobile.Firebase.Editor
{
    /// <summary>
    /// Firebase Consent Mode v2: khai báo mặc định "denied" cho analytics_storage, ad_storage, ad_user_data,
    /// ad_personalization trong AndroidManifest (Android) và Info.plist (iOS). Firebase không thu thập đầy đủ cho tới khi
    /// SDK áp consent của người dùng (UMP hoặc NovaSdk.SetConsent). Game đã tự khai báo key nào thì giữ nguyên key đó.
    /// </summary>
    static class ConsentModeDefaults
    {
        public static readonly string[] Types = { "analytics_storage", "ad_storage", "ad_user_data", "ad_personalization" };
    }

#if UNITY_ANDROID
    sealed class ConsentModeDefaultsAndroid : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 1000;

        // path = thư mục unityLibrary của Gradle project.
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[Nova] Consent Mode defaults: " + manifestPath + " not found");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var application = doc.SelectSingleNode("/manifest/application") as XmlElement;
            if (application is null)
            {
                Debug.LogWarning("[Nova] Consent Mode defaults: <application> not found in " + manifestPath);
                return;
            }

            int added = 0;
            foreach (var type in ConsentModeDefaults.Types)
            {
                var name = "google_analytics_default_allow_" + type;
                if (HasMetaData(application, name)) continue;
                var meta = doc.CreateElement("meta-data");
                meta.SetAttribute("name", AndroidNs, name);
                meta.SetAttribute("value", AndroidNs, "false");
                application.AppendChild(meta);
                added++;
            }
            if (added > 0) doc.Save(manifestPath);
        }

        static bool HasMetaData(XmlElement application, string name)
        {
            foreach (XmlNode node in application.ChildNodes)
            {
                if (node is XmlElement element && element.Name == "meta-data" && element.GetAttribute("name", AndroidNs) == name)
                    return true;
            }
            return false;
        }
    }
#endif

#if UNITY_IOS
    sealed class ConsentModeDefaultsIos : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;
            var plistPath = Path.Combine(report.summary.outputPath, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            foreach (var type in ConsentModeDefaults.Types)
            {
                var key = "GOOGLE_ANALYTICS_DEFAULT_ALLOW_" + type.ToUpperInvariant();
                if (!plist.root.values.ContainsKey(key)) plist.root.SetBoolean(key, false);
            }
            plist.WriteToFile(plistPath);
        }
    }
#endif
}
