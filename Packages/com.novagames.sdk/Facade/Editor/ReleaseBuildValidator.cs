#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NovaGames.Mobile.Ads;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NovaGames.Mobile.Editor
{
    /// <summary>
    /// Kiểm tra cấu hình SDK trước khi build Android/iOS. Bản release (không tick Development Build) có lỗi nghiêm trọng thì
    /// dừng build: test ad của Google, App ID mẫu của AdMob, thiếu license key Google Play, thiếu/sai file cấu hình
    /// Firebase, manifest debuggable, thiếu SDK key của MAX. Chạy tay: menu <i>NovaGames > Check Release Build</i>.
    /// Bỏ qua (không khuyến nghị): thêm scripting define <c>NOVA_SKIP_RELEASE_CHECKS</c>.
    /// </summary>
    sealed class ReleaseBuildValidator : IPreprocessBuildWithReport
    {
        const string GoogleSampleAppId = "ca-app-pub-3940256099942544";

        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var target = report.summary.platform;
            if (target != BuildTarget.Android && target != BuildTarget.iOS) return;
            bool development = (report.summary.options & BuildOptions.Development) != 0;

            var (errors, warnings) = Check(target);
            foreach (var warning in warnings) Debug.LogWarning("[Nova] Build check: " + warning);
            if (errors.Count == 0) return;
            if (development)
            {
                foreach (var error in errors) Debug.LogWarning("[Nova] Build check (blocks release builds): " + error);
                return;
            }
#if NOVA_SKIP_RELEASE_CHECKS
            foreach (var error in errors) Debug.LogError("[Nova] Build check (skipped by NOVA_SKIP_RELEASE_CHECKS): " + error);
#else
            foreach (var error in errors) Debug.LogError("[Nova] Build check: " + error);
            throw new BuildFailedException("NovaGames SDK: release build stopped, fix these first (details in the Console):\n- "
                                           + string.Join("\n- ", errors));
#endif
        }

        [MenuItem("NovaGames/Check Release Build")]
        static void CheckFromMenu()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android && target != BuildTarget.iOS)
            {
                Debug.LogWarning("[Nova] Build check: switch the build target to Android or iOS first");
                return;
            }
            var (errors, warnings) = Check(target);
            foreach (var error in errors) Debug.LogError("[Nova] Build check: " + error);
            foreach (var warning in warnings) Debug.LogWarning("[Nova] Build check: " + warning);
            if (errors.Count == 0) Debug.Log("[Nova] Build check: no blocking issue for a " + target + " release build"
                                             + (warnings.Count > 0 ? " (" + warnings.Count + " warning(s))" : ""));
        }

        static (List<string> Errors, List<string> Warnings) Check(BuildTarget target)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            bool ios = target == BuildTarget.iOS;

            var settings = NovaSettingsLocator.Find("Build check");
            if (settings == null)
            {
                warnings.Add("no NovaSdkSettings asset found (Create > NovaGames > SDK Settings)");
                return (errors, warnings);
            }

            var setup = settings.ToSetup(isDevelopment: false, isIos: ios);
            warnings.AddRange(setup.Issues);

            var providers = setup.Ads.UsedProviders;
            if (setup.UsesGoogleTestIds)
                errors.Add("AdMob Ads Config uses Google TEST ad unit IDs (untick 'Use Google Test Ids', fill real IDs): no revenue");

            // Google Mobile Ads cần App ID thật (AdMob hoặc UMP). Thiếu App ID iOS thì app crash ngay khi mở.
            if (providers.Contains(AdsProvider.AdMob) || setup.ConsentSource == NovaConsentSource.GoogleUmp)
            {
                var appId = ReadYaml("GoogleMobileAdsSettings", ios ? "adMobIOSAppId" : "adMobAndroidAppId");
                if (appId is null)
                    warnings.Add("Google Mobile Ads settings asset not found (Assets > Google Mobile Ads > Settings)");
                else if (appId.Length == 0)
                    errors.Add("Google Mobile Ads " + (ios ? "iOS" : "Android") + " App ID is empty (Assets > Google Mobile Ads > Settings)");
                else if (appId.StartsWith(GoogleSampleAppId, StringComparison.Ordinal))
                    errors.Add("Google Mobile Ads App ID is Google's sample ID " + appId + ": use the game's App ID from the AdMob console");
            }

            if (providers.Contains(AdsProvider.Max))
            {
                var sdkKey = ReadYaml("AppLovinSettings", "sdkKey");
                if (sdkKey != null && sdkKey.Length == 0)
                    errors.Add("AppLovin MAX SDK key is empty (AppLovin > Integration Manager)");
            }

            if (!ios && setup.Iap.IsEnabled && !IsGooglePlayKeySet())
                errors.Add("IAP is on but the Google Play license key is missing (NovaGames > IAP > Google Play License Key): " +
                           "receipts would not be verified");

            if (IsFirebaseInstalled()) CheckFirebaseConfig(ios, errors);

            if (!ios)
            {
                const string manifest = "Assets/Plugins/Android/AndroidManifest.xml";
                if (File.Exists(manifest) && File.ReadAllText(manifest).Contains("android:debuggable=\"true\""))
                    errors.Add(manifest + " has android:debuggable=\"true\": Google Play rejects debuggable builds, remove the attribute");
            }
            return (errors, warnings);
        }

        static void CheckFirebaseConfig(bool ios, List<string> errors)
        {
            var name = ios ? "GoogleService-Info" : "google-services";
            var extension = ios ? ".plist" : ".json";
            var path = AssetDatabase.FindAssets(name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(name + extension, StringComparison.OrdinalIgnoreCase));
            if (path is null)
            {
                errors.Add("Firebase config " + name + extension + " is missing in Assets (download it from the Firebase console)");
                return;
            }
            // File của app khác: Firebase không khởi tạo được (mất Analytics, Remote Config, Crashlytics).
            var bundleId = PlayerSettings.GetApplicationIdentifier(ios ? UnityEditor.Build.NamedBuildTarget.iOS : UnityEditor.Build.NamedBuildTarget.Android);
            if (!File.ReadAllText(path).Contains("\"" + bundleId + "\"") && !File.ReadAllText(path).Contains(">" + bundleId + "<"))
                errors.Add(path + " does not contain the app id '" + bundleId + "': it belongs to another Firebase app");
        }

        static bool IsFirebaseInstalled() =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Firebase.App");

        // File key sinh bởi NovaGames > IAP > Google Play License Key, nằm trong Assets của game.
        static bool IsGooglePlayKeySet() =>
            AssetDatabase.FindAssets("NovaGooglePlayLicense t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                .Any(p => p.StartsWith("Assets/", StringComparison.Ordinal) && Path.GetFileName(p) == "NovaGooglePlayLicense.cs");

        // Đọc một field dạng chuỗi trong asset YAML của vendor. null = không tìm thấy asset.
        static string? ReadYaml(string assetName, string field)
        {
            var path = AssetDatabase.FindAssets(assetName).Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(assetName + ".asset", StringComparison.Ordinal));
            if (path is null) return null;
            var match = Regex.Match(File.ReadAllText(path), @"^\s*" + Regex.Escape(field) + @":[ \t]*(.*)$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : string.Empty;
        }
    }
}
