#nullable enable
namespace NovaGames.Mobile.Iap
{
    /// <summary>
    /// Google Play license key (đã obfuscate) của game, dùng để kiểm tra receipt Android.
    /// Key riêng từng game nên không nằm trong package SDK: menu NovaGames > IAP > Google Play License Key sinh file
    /// <c>NovaGooglePlayLicense.cs</c> trong Assets của game, file đó gọi <see cref="Register"/> lúc khởi động.
    /// </summary>
    public static class GooglePlayLicense
    {
        static byte[]? _data;
        static int[]? _order;
        static int _key;

        public static bool IsSet => _data is { Length: > 0 };

        /// <summary>Gọi từ file sinh tự động; không gọi tay.</summary>
        public static void Register(byte[] data, int[] order, int key)
        {
            _data = data;
            _order = order;
            _key = key;
        }

        /// <summary>Dữ liệu obfuscate để adapter IAP giải mã (Obfuscator.DeObfuscate của Unity IAP).</summary>
        public static bool TryGet(out byte[] data, out int[] order, out int key)
        {
            data = _data ?? System.Array.Empty<byte>();
            order = _order ?? System.Array.Empty<int>();
            key = _key;
            return IsSet;
        }
    }
}
