#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.RemoteConfig;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NovaGames.Mobile.Editor
{
    /// <summary>
    /// Menu <i>NovaGames > Setup</i>: checklist tích hợp SDK vào game, bước nào chưa xong có nút làm luôn (tạo
    /// NovaSdkSettings, thêm prefab NovaSdk vào scene, tạo asset config, tạo script RemoteKey). Tự mở một lần khi project
    /// chưa có NovaSdkSettings của game.
    /// </summary>
    sealed class NovaSetupWindow : EditorWindow
    {
        const string PackageRoot = "Packages/com.novagames.sdk";
        const string PrefabPath = PackageRoot + "/Bootstrap/Prefabs/NovaSdk.prefab";
        const string GameFolder = "Assets/NovaGames";
        const string ScriptsFolder = GameFolder + "/Scripts";
        const string SampleAssembly = "NovaGames.Mobile.Samples";
        const string PendingRemoteConfigKey = "NovaGames.Setup.PendingRemoteConfigAsset";
        const string AddEntriesMenu = "Assets/NovaGames/Remote Config/Add Missing Enum Entries";

        sealed class Module
        {
            public Module(string name, string[] assemblies, string install, string? field = null, string? configType = null, string? extra = null)
            {
                Name = name;
                Assemblies = assemblies;
                Install = install;
                Field = field;
                ConfigType = configType;
                Extra = extra;
            }

            public string Name { get; }
            public string[] Assemblies { get; }
            public string Install { get; }
            public string? Field { get; }        // ô trong NovaSdkSettings
            public string? ConfigType { get; }   // tên class asset config (tìm qua TypeCache vì adapter có thể chưa compile)
            public string? Extra { get; }        // việc phải làm tay trong menu của vendor
        }

        static readonly Module[] Modules =
        {
            new Module("Analytics", new[] { "Firebase.App", "Firebase.Analytics" }, "com.google.firebase.app + com.google.firebase.analytics"),
            new Module("Crashlytics", new[] { "Firebase.App", "Firebase.Crashlytics" }, "com.google.firebase.app + com.google.firebase.crashlytics"),
            new Module("Remote Config", new[] { "Firebase.App", "Firebase.RemoteConfig" }, "com.google.firebase.app + com.google.firebase.remote-config", "remoteConfig"),
            new Module("Ads MAX", new[] { "MaxSdk.Scripts" }, "AppLovin MAX Unity plugin 8.x", "maxAds", "MaxAdsConfig", "SDK key: AppLovin > Integration Manager"),
            new Module("Ads AdMob", new[] { "GoogleMobileAds" }, "Google Mobile Ads Unity plugin 11.x", "admobAds", nameof(AdMobAdsConfig), "App ID: Assets > Google Mobile Ads > Settings"),
            new Module("Consent (UMP)", new[] { "GoogleMobileAds.Ump" }, "Google Mobile Ads Unity plugin 11.x (cần cả khi chỉ dùng MAX)", extra: "Publish message GDPR trên AdMob console"),
            new Module("Adjust", new[] { "AdjustSdk.Scripts" }, "Adjust Unity SDK 5.x", "adjust", "AdjustTrackingConfig"),
            new Module("IAP", new[] { "Unity.Purchasing" }, "com.unity.purchasing 5.x", "iap", nameof(IapConfig), "Android: NovaGames > IAP > Google Play License Key"),
            new Module("Notifications", new[] { "Unity.Notifications.Unified" }, "com.unity.mobile.notifications 2.x", "notifications", nameof(NotificationConfig)),
        };

        HashSet<string> _assemblies = new HashSet<string>();
        Vector2 _scroll;
        string _scriptNamespace = "Game";

        [MenuItem("NovaGames/Setup", priority = 0)]
        public static void Open()
        {
            var window = GetWindow<NovaSetupWindow>("NovaGames Setup");
            window.minSize = new Vector2(460, 420);
            window.Show();
        }

        [InitializeOnLoadMethod]
        static void OnEditorLoad() => EditorApplication.delayCall += () =>
        {
            CreatePendingRemoteConfigAsset();
            // Mở một lần cho mỗi project vừa cài SDK mà chưa có NovaSdkSettings của game.
            string key = "NovaGames.Setup.Shown." + Application.dataPath;
            if (EditorPrefs.GetBool(key) || FindGameSettings() != null) return;
            EditorPrefs.SetBool(key, true);
            Open();
        };

        void OnEnable() => Refresh();
        void OnFocus() => Refresh();

        void Refresh()
        {
            _assemblies = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name));
            Repaint();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var settings = FindGameSettings();

            Header("1. Bắt buộc");
            DrawSettingsStep(settings);
            DrawSceneStep(settings);

            Header("2. Module (chỉ làm module game cần dùng)");
            if (settings == null) EditorGUILayout.HelpBox("Tạo NovaSdkSettings trước.", MessageType.None);
            else foreach (var module in Modules) DrawModule(module, settings);
            DrawSampleWarning();

            Header("3. Trước khi build release");
            DrawReleaseStep(settings);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Vendor Defines")) EditorApplication.ExecuteMenuItem("NovaGames/Refresh Vendor Defines");
                if (GUILayout.Button("Mở hướng dẫn (Guide.md)"))
                    EditorUtility.OpenWithDefaultApp(Path.Combine(PackageDir(), "Docs/Guide.md"));
            }
            EditorGUILayout.EndScrollView();
        }

        // ---------- 1. Bắt buộc ----------

        void DrawSettingsStep(NovaSdkSettings? settings)
        {
            if (settings != null)
            {
                Row(true, "NovaSdkSettings: " + AssetDatabase.GetAssetPath(settings), "Chọn", () => Ping(settings));
                return;
            }
            Row(false, "Chưa có asset NovaSdkSettings của game", "Tạo", () => Ping(CreateSettings()));
            if (FindAnySettings() != null)
                EditorGUILayout.HelpBox("Asset NovaSdkSettings của sample Demo là cấu hình test, không dùng cho game.", MessageType.Warning);
        }

        void DrawSceneStep(NovaSdkSettings? settings)
        {
            var scene = SceneManager.GetActiveScene();
            var bootstrap = UnityEngine.Object.FindObjectsByType<NovaSdkBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (bootstrap == null)
            {
                Row(false, "Scene '" + scene.name + "' chưa có prefab NovaSdk", "Thêm vào scene", () => AddPrefab(settings), settings != null);
            }
            else
            {
                var serialized = new SerializedObject(bootstrap);
                var field = serialized.FindProperty("settings");
                bool assigned = field.objectReferenceValue != null;
                Row(assigned, assigned ? "Prefab NovaSdk có trong scene '" + scene.name + "'" : "Prefab NovaSdk chưa gán NovaSdkSettings",
                    assigned ? "Chọn" : "Gán", () =>
                    {
                        if (!assigned && settings != null)
                        {
                            field.objectReferenceValue = settings;
                            serialized.ApplyModifiedProperties();
                            EditorSceneManager.MarkSceneDirty(bootstrap.gameObject.scene);
                        }
                        Ping(bootstrap.gameObject);
                    }, assigned || settings != null);
            }

            var first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
            bool isFirst = first != null && first.path == scene.path;
            if (bootstrap != null && !isFirst && !string.IsNullOrEmpty(scene.path))
                Row(false, "Scene này chưa phải scene đầu tiên trong Build Settings (SDK nên khởi động ở scene đầu)",
                    "Đưa lên đầu", () => MakeFirstScene(scene.path));
        }

        // ---------- 2. Module ----------

        void DrawModule(Module module, NovaSdkSettings settings)
        {
            bool installed = module.Assemblies.All(_assemblies.Contains);
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(module.Name, EditorStyles.boldLabel);
            using var indent = new EditorGUI.IndentLevelScope();
            if (!installed)
            {
                Row(false, "Chưa cài: " + module.Install + " (không dùng thì bỏ qua)", null, null);
                return;
            }
            Row(true, "Đã cài vendor", null, null);

            if (module.Name == "Analytics") DrawFirebaseFiles();
            if (module.Name == "Consent (UMP)" && settings.ConsentSource != NovaConsentSource.GoogleUmp)
                EditorGUILayout.HelpBox("Consent Source trong NovaSdkSettings đang là " + settings.ConsentSource + ".", MessageType.Info);

            if (module.Field != null)
            {
                var serialized = new SerializedObject(settings);
                var field = serialized.FindProperty(module.Field);
                var current = field.objectReferenceValue;
                if (current != null) Row(true, "Config: " + current.name, "Chọn", () => Ping(current));
                else if (module.Field == "remoteConfig") DrawRemoteConfigMissing(settings);
                else
                {
                    var type = FindType(module.ConfigType!);
                    Row(false, "Chưa có config trong NovaSdkSettings", "Tạo " + module.ConfigType, () =>
                    {
                        var asset = CreateConfig(type!, module.ConfigType!);
                        Assign(settings, module.Field, asset);
                        Ping(asset);
                    }, type != null);
                    if (type == null) EditorGUILayout.HelpBox("Chạy Refresh Vendor Defines rồi chờ Unity compile để có class " + module.ConfigType + ".", MessageType.None);
                }
            }
            if (module.Extra != null) EditorGUILayout.LabelField("Làm tay: " + module.Extra, EditorStyles.miniLabel);
        }

        void DrawFirebaseFiles()
        {
            bool android = HasAsset("google-services.json");
            bool ios = HasAsset("GoogleService-Info.plist");
            Row(android, android ? "Có google-services.json" : "Thiếu Assets/google-services.json (tải từ Firebase console)", null, null);
            Row(ios, ios ? "Có GoogleService-Info.plist" : "Thiếu Assets/GoogleService-Info.plist (chỉ cần khi build iOS)", null, null);
        }

        void DrawRemoteConfigMissing(NovaSdkSettings settings)
        {
            var type = GameRemoteConfigType();
            if (type != null)
            {
                Row(false, "Chưa có asset Remote Config trong NovaSdkSettings", "Tạo " + type.Name, () => CreateRemoteConfigAsset(type, settings));
                return;
            }
            Row(false, "Game chưa có enum RemoteKey (khai báo key Remote Config)", "Tạo script", () => CreateRemoteKeyScripts(_scriptNamespace));
            _scriptNamespace = EditorGUILayout.TextField("Namespace cho script", _scriptNamespace);
        }

        void DrawSampleWarning()
        {
            if (!_assemblies.Contains(SampleAssembly) || !AssetDatabase.IsValidFolder("Assets/Samples")) return;
            EditorGUILayout.HelpBox("Sample Demo đang nằm trong Assets/Samples. Xem xong thì xóa đi để không trùng menu " +
                                    "Remote Config Definitions với script của game.", MessageType.Warning);
        }

        // ---------- 3. Release ----------

        void DrawReleaseStep(NovaSdkSettings? settings)
        {
            if (settings != null && settings.ConsentSource == NovaConsentSource.AssumeGrantedForTesting)
                Row(false, "Consent Source = Assume granted (chỉ để test)", "Chọn", () => Ping(settings));
            Row(true, "Chạy kiểm tra trước khi build Android/iOS", "Check Release Build",
                () => EditorApplication.ExecuteMenuItem("NovaGames/Check Release Build"));
        }

        // ---------- Actions ----------

        static NovaSdkSettings CreateSettings()
        {
            var asset = CreateInstance<NovaSdkSettings>();
            AssetDatabase.CreateAsset(asset, UniquePath(GameFolder, "NovaSdkSettings"));
            AssetDatabase.SaveAssets();
            return asset;
        }

        static void AddPrefab(NovaSdkSettings? settings)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[Nova] Setup: prefab not found at " + PrefabPath);
                return;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Add NovaSdk");
            var serialized = new SerializedObject(instance.GetComponent<NovaSdkBootstrap>());
            serialized.FindProperty("settings").objectReferenceValue = settings;
            serialized.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(instance.scene);
            Ping(instance);
        }

        static void MakeFirstScene(string path)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != path).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static ScriptableObject CreateConfig(Type type, string name)
        {
            var asset = CreateInstance(type);
            AssetDatabase.CreateAsset(asset, UniquePath(GameFolder, name));
            AssetDatabase.SaveAssets();
            return asset;
        }

        static void Assign(NovaSdkSettings settings, string field, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(settings);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        static void CreateRemoteConfigAsset(Type type, NovaSdkSettings settings)
        {
            var asset = CreateConfig(type, type.Name);
            Assign(settings, "remoteConfig", asset);
            // Thêm sẵn mọi key khai báo trong enum RemoteKey (menu do script GameRemoteConfigContextMenu của game cung cấp).
            Selection.activeObject = asset;
            EditorApplication.ExecuteMenuItem(AddEntriesMenu);
            Ping(asset);
        }

        // Copy 3 file mẫu của sample Demo vào game, đổi namespace. Sau khi Unity compile xong thì tự tạo asset.
        static void CreateRemoteKeyScripts(string ns)
        {
            ns = string.IsNullOrWhiteSpace(ns) ? "Game" : ns.Trim();
            var source = Path.Combine(PackageDir(), "Samples~/Demo");
            var files = new[]
            {
                ("Scripts/RemoteKey.cs", ScriptsFolder + "/RemoteKey.cs"),
                ("Scripts/GameRemoteConfig.cs", ScriptsFolder + "/GameRemoteConfig.cs"),
                ("Editor/GameRemoteConfigContextMenu.cs", ScriptsFolder + "/Editor/GameRemoteConfigContextMenu.cs"),
            };
            foreach (var (from, to) in files)
            {
                var fromPath = Path.Combine(source, from);
                if (!File.Exists(fromPath))
                {
                    Debug.LogError("[Nova] Setup: template not found: " + fromPath);
                    return;
                }
                if (File.Exists(to))
                {
                    Debug.LogWarning("[Nova] Setup: " + to + " already exists, kept as is");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.WriteAllText(to, File.ReadAllText(fromPath).Replace("NovaGames.Mobile.Samples", ns));
            }
            SessionState.SetBool(PendingRemoteConfigKey, true);
            AssetDatabase.Refresh();
            Debug.Log("[Nova] Setup: created RemoteKey scripts in " + ScriptsFolder + ". Add game keys to the RemoteKey enum.");
        }

        static void CreatePendingRemoteConfigAsset()
        {
            if (!SessionState.GetBool(PendingRemoteConfigKey, false)) return;
            var type = GameRemoteConfigType();
            var settings = FindGameSettings();
            if (type == null || settings == null) return;
            SessionState.EraseBool(PendingRemoteConfigKey);
            if (new SerializedObject(settings).FindProperty("remoteConfig").objectReferenceValue == null)
                CreateRemoteConfigAsset(type, settings);
        }

        // ---------- Lookup ----------

        static NovaSdkSettings? FindGameSettings() => FindSettings().FirstOrDefault(p => !IsSample(p)) is { } path
            ? AssetDatabase.LoadAssetAtPath<NovaSdkSettings>(path)
            : null;

        static NovaSdkSettings? FindAnySettings() => FindSettings().FirstOrDefault() is { } path
            ? AssetDatabase.LoadAssetAtPath<NovaSdkSettings>(path)
            : null;

        static IEnumerable<string> FindSettings() =>
            AssetDatabase.FindAssets("t:" + nameof(NovaSdkSettings)).Select(AssetDatabase.GUIDToAssetPath);

        static bool IsSample(string path) =>
            path.StartsWith("Assets/Samples/", StringComparison.Ordinal) || path.Contains("/NovaSdkSamples/");

        static Type? GameRemoteConfigType() =>
            TypeCache.GetTypesDerivedFrom<RemoteConfigDefinitions>()
                .FirstOrDefault(t => !t.IsAbstract && !t.IsGenericType && t.Assembly.GetName().Name != SampleAssembly);

        static Type? FindType(string name) =>
            TypeCache.GetTypesDerivedFrom<ScriptableObject>().FirstOrDefault(t => t.Name == name && !t.IsAbstract);

        static bool HasAsset(string fileName) =>
            AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName)).Select(AssetDatabase.GUIDToAssetPath)
                .Any(p => p.StartsWith("Assets/", StringComparison.Ordinal) && Path.GetFileName(p).Equals(fileName, StringComparison.OrdinalIgnoreCase));

        static string UniquePath(string folder, string name)
        {
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            return AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".asset");
        }

        // Thư mục thật của package (git package nằm trong Library/PackageCache, không phải Packages/).
        static string PackageDir() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageRoot)?.resolvedPath ?? Path.GetFullPath(PackageRoot);

        // ---------- UI ----------

        static void Header(string text)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(text, EditorStyles.largeLabel);
        }

        static void Row(bool done, string text, string? button, Action? action, bool enabled = true)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var icon = EditorGUIUtility.IconContent(done ? "TestPassed" : "console.warnicon.sml");
                GUILayout.Label(icon, GUILayout.Width(20), GUILayout.Height(18));
                EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);
                if (button == null || action == null) return;
                using (new EditorGUI.DisabledScope(!enabled))
                    if (GUILayout.Button(button, GUILayout.Width(150))) EditorApplication.delayCall += () => action();
            }
        }

        static void Ping(UnityEngine.Object target)
        {
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }
    }
}
