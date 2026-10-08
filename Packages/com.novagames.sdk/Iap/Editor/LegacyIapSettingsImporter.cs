#nullable enable
using System.IO;
using NovaGames.Mobile.Iap;
using UnityEditor;
using UnityEngine;

namespace NovaGames.Mobile.Editor
{
    /// <summary>
    /// Chuyển danh sách sản phẩm từ asset IapSettings cũ (field iapDataProducts: androidId, iOSId, iapProductType) sang IapConfig.
    /// Product Id giữ bằng store id cũ để code game gọi theo id cũ vẫn chạy.
    /// </summary>
    static class LegacyIapSettingsImporter
    {
        [MenuItem("NovaGames/IAP/Import Products From Selected IapSettings")]
        static void Import()
        {
            var source = Selection.activeObject;
            var legacy = source != null ? new SerializedObject(source).FindProperty("iapDataProducts") : null;
            if (legacy is null || !legacy.isArray)
            {
                EditorUtility.DisplayDialog("Import IAP products", "Chọn asset IapSettings cũ (có danh sách iapDataProducts) trong Project rồi chạy lại.", "OK");
                return;
            }

            var sourcePath = AssetDatabase.GetAssetPath(source);
            var folder = string.IsNullOrEmpty(sourcePath) ? "Assets" : Path.GetDirectoryName(sourcePath)!.Replace('\\', '/');
            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/IapConfig.asset");
            var config = ScriptableObject.CreateInstance<IapConfig>();
            AssetDatabase.CreateAsset(config, path);

            var target = new SerializedObject(config);
            var products = target.FindProperty("products");
            products.arraySize = 0;
            for (int i = 0; i < legacy.arraySize; i++)
            {
                var item = legacy.GetArrayElementAtIndex(i);
                var androidId = item.FindPropertyRelative("androidId")?.stringValue ?? string.Empty;
                var iosId = item.FindPropertyRelative("iOSId")?.stringValue ?? string.Empty;
                var type = item.FindPropertyRelative("iapProductType")?.intValue ?? 0;
                var id = androidId.Length > 0 ? androidId : iosId;
                if (id.Length == 0) continue;

                products.arraySize++;
                var product = products.GetArrayElementAtIndex(products.arraySize - 1);
                product.FindPropertyRelative("productId").stringValue = id;
                product.FindPropertyRelative("androidStoreId").stringValue = androidId;
                product.FindPropertyRelative("iosStoreId").stringValue = iosId;
                product.FindPropertyRelative("type").enumValueIndex = Mathf.Clamp(type, 0, 2);
                product.FindPropertyRelative("removeAds").boolValue = false;
                product.FindPropertyRelative("testPriceUsd").floatValue = 0.99f;
            }
            target.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
            DebugCustom.Log("[Nova] Imported " + products.arraySize + " IAP products into " + path +
                            ". Tick Remove Ads on the remove-ads product and drag the asset into NovaSdkSettings > IAP.");
        }
    }
}
