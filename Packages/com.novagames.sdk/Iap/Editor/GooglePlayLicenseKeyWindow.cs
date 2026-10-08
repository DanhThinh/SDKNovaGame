#nullable enable
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NovaGames.Mobile.Editor
{
    /// <summary>
    /// Nhập Google Play license key (Play Console > Monetization setup > Licensing) để SDK kiểm tra receipt Android.
    /// Key được obfuscate vào Assets/NovaGames/Generated/NovaGooglePlayLicense.cs của game (key riêng từng game nên không nằm
    /// trong package SDK); file đó đăng ký key vào GooglePlayLicense lúc khởi động. Không lưu key gốc ở đâu khác.
    /// </summary>
    public sealed class GooglePlayLicenseKeyWindow : EditorWindow
    {
        const string FileName = "NovaGooglePlayLicense";
        const string DefaultPath = "Assets/NovaGames/Generated/" + FileName + ".cs";
        const int SliceSize = 20;

        string _key = string.Empty;
        string _status = string.Empty;

        [MenuItem("NovaGames/IAP/Google Play License Key")]
        static void Open()
        {
            var window = GetWindow<GooglePlayLicenseKeyWindow>(true, "Google Play License Key");
            window.minSize = new Vector2(480, 260);
            window.RefreshStatus();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Play Console > chọn app > Monetization setup > Licensing: copy \"Base64-encoded RSA public key\" rồi dán vào đây. " +
                "SDK dùng key này để kiểm tra receipt Google Play trước khi trao thưởng.", MessageType.Info);
            EditorGUILayout.LabelField("Trạng thái", _status);
            _key = EditorGUILayout.TextArea(_key, GUILayout.MinHeight(90));

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_key)))
            {
                if (GUILayout.Button("Generate")) Generate(_key.Trim());
            }
            if (GUILayout.Button("Clear key")) Clear();
        }

        void Generate(string base64)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                EditorUtility.DisplayDialog("Google Play License Key", "Key không phải chuỗi Base64 hợp lệ.", "OK");
                return;
            }
            var order = new int[bytes.Length / SliceSize + 1];
            var tangled = Obfuscate(bytes, order, out var key);
            Write(tangled, order, key);
            _key = string.Empty;
        }

        void Write(byte[] data, int[] order, int key)
        {
            var path = FindPath() ?? DefaultPath;
            var text =
                "// Sinh bởi NovaGames > IAP > Google Play License Key. Không sửa tay.\n" +
                "namespace NovaGames.Mobile.Iap.Generated\n" +
                "{\n" +
                "    static class " + FileName + "\n" +
                "    {\n" +
                "        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]\n" +
                "        static void Register() => NovaGames.Mobile.Iap.GooglePlayLicense.Register(\n" +
                "            System.Convert.FromBase64String(\"" + Convert.ToBase64String(data) + "\"),\n" +
                "            new int[] { " + string.Join(",", order.Select(i => i.ToString())) + " },\n" +
                "            " + key + ");\n" +
                "    }\n" +
                "}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path);
            DebugCustom.Log("[Nova] Google Play license key generated in " + path);
            RefreshStatus();
        }

        void Clear()
        {
            var path = FindPath();
            if (path != null && AssetDatabase.DeleteAsset(path)) DebugCustom.Log("[Nova] Google Play license key removed: " + path);
            RefreshStatus();
        }

        void RefreshStatus() =>
            _status = FindPath() is { } path ? "Đã có key (" + path + ")" : "Chưa có key (receipt Android KHÔNG được kiểm tra)";

        /// <summary>File key của game; null = chưa nhập key.</summary>
        internal static string? FindPath() =>
            AssetDatabase.FindAssets(FileName + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                                     Path.GetFileNameWithoutExtension(p) == FileName);

        // Cùng thuật toán với công cụ obfuscate của Unity IAP (Obfuscator.DeObfuscate giải ngược lại).
        static byte[] Obfuscate(byte[] data, int[] order, out int key)
        {
            var random = new System.Random();
            key = random.Next(2, 255);
            var result = new byte[data.Length];
            int slices = data.Length / SliceSize + 1;
            Array.Copy(data, result, data.Length);
            for (int i = 0; i < slices - 1; i++)
            {
                int j = random.Next(i, slices - 1);
                order[i] = j;
                var tmp = new byte[SliceSize];
                Array.Copy(result, i * SliceSize, tmp, 0, SliceSize);
                Array.Copy(result, j * SliceSize, result, i * SliceSize, SliceSize);
                Array.Copy(tmp, 0, result, j * SliceSize, SliceSize);
            }
            order[slices - 1] = slices - 1;
            for (int i = 0; i < result.Length; i++) result[i] = (byte)(result[i] ^ key);
            return result;
        }
    }
}
