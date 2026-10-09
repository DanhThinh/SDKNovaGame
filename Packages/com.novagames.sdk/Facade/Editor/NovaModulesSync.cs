#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NovaGames.Mobile.Bootstrap;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NovaGames.Mobile.Editor
{
    /// <summary>
    /// Giữ GameObject con của <see cref="NovaModules"/> khớp với vendor đang cài: thêm module còn thiếu, xóa component mất
    /// script (vendor bị gỡ hoặc define bị tắt), bỏ module trùng. Chạy khi mở scene và khi hierarchy đổi; lúc build và lúc
    /// vào Play chạy lại trên từng scene, khi build còn bỏ module không compile cho nền tảng đó (vd. ATT trên Android).
    /// </summary>
    [InitializeOnLoad]
    static class NovaModulesSync
    {
        static List<Type>? s_installed;
        static bool s_queued;
        static readonly HashSet<int> Warned = new HashSet<int>();

        static NovaModulesSync()
        {
            EditorSceneManager.sceneOpened += (_, _) => Queue();
            EditorApplication.hierarchyChanged += Queue;
            Queue();
        }

        /// <summary>Loại module có trong project (assembly của vendor đã compile), sắp theo tên.</summary>
        internal static IReadOnlyList<Type> InstalledModules() =>
            s_installed ??= TypeCache.GetTypesDerivedFrom<NovaModule>()
                .Where(t => !t.IsAbstract && !t.IsGenericType && !(t.Assembly.GetName().Name ?? string.Empty).Contains(".Tests"))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

        static void Queue()
        {
            if (s_queued) return;
            s_queued = true;
            EditorApplication.delayCall += () =>
            {
                s_queued = false;
                SyncOpenScenes();
            };
        }

        static void SyncOpenScenes()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer) return;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var dirty = new HashSet<Scene>();

            foreach (var bootstrap in Object.FindObjectsByType<NovaSdkBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsSceneObject(bootstrap, stage) || bootstrap.GetComponent<NovaModules>() != null) continue;
                bootstrap.gameObject.AddComponent<NovaModules>();
                dirty.Add(bootstrap.gameObject.scene);
            }
            foreach (var host in Object.FindObjectsByType<NovaModules>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsSceneObject(host, stage) && Sync(host, InstalledModules())) dirty.Add(host.gameObject.scene);
            }
            foreach (var scene in dirty) EditorSceneManager.MarkSceneDirty(scene);
        }

        // Bỏ qua asset prefab và Prefab Mode: prefab NovaSdk của package không chứa module (package read-only ở game).
        static bool IsSceneObject(Component component, PrefabStage? stage) =>
            !EditorUtility.IsPersistent(component) && component.gameObject.scene.IsValid()
            && (stage == null || component.gameObject.scene != stage.scene);

        /// <summary>Đồng bộ mọi NovaModules trong scene với danh sách module cần có.</summary>
        internal static void SyncScene(Scene scene, IReadOnlyCollection<Type> wanted)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var bootstrap in root.GetComponentsInChildren<NovaSdkBootstrap>(true))
                    if (bootstrap.GetComponent<NovaModules>() == null) bootstrap.gameObject.AddComponent<NovaModules>();
                foreach (var host in root.GetComponentsInChildren<NovaModules>(true)) Sync(host, wanted);
            }
        }

        /// <summary>Trả về true nếu có thay đổi.</summary>
        internal static bool Sync(NovaModules host, IReadOnlyCollection<Type> wanted)
        {
            var changes = new List<string>();

            // 1. Component mất script trên con trực tiếp: vendor bị gỡ hoặc define bị tắt.
            for (int i = host.transform.childCount - 1; i >= 0; i--)
            {
                var child = host.transform.GetChild(i).gameObject;
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child) == 0) continue;
                if (!CanEdit(child))
                {
                    WarnLocked(child);
                    continue;
                }
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child);
                changes.Add("removed missing script on '" + child.name + "'");
                if (IsEmpty(child)) Object.DestroyImmediate(child);
            }

            // 2. Module trùng loại, hoặc không có trong danh sách (không compile cho nền tảng đang build).
            var present = new HashSet<Type>();
            foreach (var module in host.GetComponentsInChildren<NovaModule>(true))
            {
                var type = module.GetType();
                if (wanted.Contains(type) && present.Add(type)) continue;
                if (!CanEdit(module.gameObject))
                {
                    WarnLocked(module.gameObject);
                    present.Add(type);
                    continue;
                }
                var go = module.gameObject;
                Object.DestroyImmediate(module);
                changes.Add("removed " + type.Name);
                if (go != host.gameObject && IsEmpty(go)) Object.DestroyImmediate(go);
            }

            // 3. Module của vendor đã cài nhưng chưa có: mỗi module một GameObject con.
            foreach (var type in wanted)
            {
                if (present.Contains(type)) continue;
                var go = new GameObject(DisplayName(type));
                go.transform.SetParent(host.transform, false);
                go.AddComponent(type);
                changes.Add("added " + type.Name);
            }

            if (changes.Count > 0) Debug.Log("[Nova] Modules on '" + host.name + "': " + string.Join(", ", changes), host);
            return changes.Count > 0;
        }

        // Tên GameObject = phần cuối của AddComponentMenu ("NovaGames/Modules/Firebase Remote Config").
        static string DisplayName(Type type)
        {
            var menu = type.GetCustomAttribute<AddComponentMenu>()?.componentMenu;
            if (string.IsNullOrEmpty(menu)) return ObjectNames.NicifyVariableName(type.Name);
            int slash = menu!.LastIndexOf('/');
            return slash >= 0 ? menu.Substring(slash + 1) : menu;
        }

        static bool IsEmpty(GameObject go) => go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1;

        // Object thuộc asset prefab (không phải object thêm vào instance) thì không sửa được từ scene.
        static bool CanEdit(GameObject go) =>
            !PrefabUtility.IsPartOfPrefabInstance(go) || PrefabUtility.IsAddedGameObjectOverride(go);

        static void WarnLocked(GameObject go)
        {
            if (!Warned.Add(go.GetInstanceID())) return;
            var path = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(go));
            Debug.LogWarning("[Nova] Modules: '" + go.name + "' is part of prefab '" + path +
                             "', so it cannot be fixed from the scene. Open that prefab and remove/add the module there.", go);
        }

        sealed class BuildStep : IProcessSceneWithReport
        {
            public int callbackOrder => 0;

            // report == null khi vào Play: dùng mọi module đã cài. Khi build: chỉ module compile cho nền tảng đang build.
            public void OnProcessScene(Scene scene, BuildReport? report)
            {
                IReadOnlyCollection<Type> wanted = InstalledModules();
                if (report != null)
                {
                    var player = new HashSet<string>(
                        CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name),
                        StringComparer.Ordinal);
                    wanted = InstalledModules().Where(t => player.Contains(t.Assembly.GetName().Name ?? string.Empty)).ToList();
                }
                SyncScene(scene, wanted);
            }
        }
    }
}
