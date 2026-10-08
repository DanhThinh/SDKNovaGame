#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.RemoteConfig;
using UnityEditor;
using UnityEngine;

namespace NovaGames.Mobile.Samples.Editor
{
    public static class GameRemoteConfigContextMenu
    {
        const string AssetMenu = "Assets/NovaGames/Remote Config/Add Missing Enum Entries";
        const string InspectorMenu = "CONTEXT/GameRemoteConfig/Add Missing Enum Entries";

        [MenuItem(AssetMenu, false, 2000)]
        static void AddFromProjectWindow()
        {
            int assetCount = 0;
            int addedCount = 0;
            foreach (var config in Selection.GetFiltered<GameRemoteConfig>(SelectionMode.Assets))
            {
                assetCount++;
                addedCount += AddMissing(config);
            }

            Debug.Log($"[Remote Config] Added {addedCount} missing enum entr{(addedCount == 1 ? "y" : "ies")} " +
                      $"to {assetCount} asset{(assetCount == 1 ? string.Empty : "s")}.");
        }

        [MenuItem(AssetMenu, true)]
        static bool CanAddFromProjectWindow() =>
            Selection.GetFiltered<GameRemoteConfig>(SelectionMode.Assets).Length > 0;

        [MenuItem(InspectorMenu)]
        static void AddFromInspector(MenuCommand command)
        {
            if (command.context is not GameRemoteConfig config) return;
            int added = AddMissing(config);
            Debug.Log($"[Remote Config] Added {added} missing enum entr{(added == 1 ? "y" : "ies")} to '{config.name}'.", config);
        }

        // Kiểu + default khai báo bằng [RemoteDefault] trên enum RemoteKey; thiếu attribute thì String rỗng.
        static RemoteDefaultAttribute DefaultOf(RemoteKey key)
        {
            var field = typeof(RemoteKey).GetField(key.ToString());
            return field != null && Attribute.GetCustomAttribute(field, typeof(RemoteDefaultAttribute)) is RemoteDefaultAttribute attribute
                ? attribute
                : new RemoteDefaultAttribute(ConfigValueType.String, string.Empty);
        }

        static int AddMissing(GameRemoteConfig config)
        {
            var serialized = new SerializedObject(config);
            var entries = serialized.FindProperty("entries");
            var existing = new HashSet<int>();

            for (int i = 0; i < entries.arraySize; i++)
                existing.Add(entries.GetArrayElementAtIndex(i).FindPropertyRelative("key").intValue);

            Undo.RecordObject(config, "Add Missing Remote Config Entries");
            int added = 0;
            foreach (RemoteKey key in Enum.GetValues(typeof(RemoteKey)))
            {
                int numericKey = Convert.ToInt32(key);
                if (existing.Contains(numericKey)) continue;

                int index = entries.arraySize;
                entries.InsertArrayElementAtIndex(index);
                var entry = entries.GetArrayElementAtIndex(index);
                var defaults = DefaultOf(key);

                entry.FindPropertyRelative("key").intValue = numericKey;
                entry.FindPropertyRelative("type").enumValueIndex = (int)defaults.Type;
                entry.FindPropertyRelative("defaultValue").stringValue = defaults.Value;
                entry.FindPropertyRelative("remoteOverridable").boolValue = true;
                existing.Add(numericKey);
                added++;
            }

            if (added == 0) return 0;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            return added;
        }
    }
}
