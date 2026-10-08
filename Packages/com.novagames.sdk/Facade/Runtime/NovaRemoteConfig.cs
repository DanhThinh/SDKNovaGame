#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// Đọc Firebase Remote Config theo enum key của game (khai báo trong asset Remote Config Definitions gán ở
    /// <see cref="NovaSdkSettings"/>): <c>int interval = NovaRemoteConfig.GetInt(RemoteKey.ad_inter_interval);</c>
    /// <para>
    /// Luôn trả về giá trị: giá trị remote đã fetch, nếu chưa có thì bản đã lưu từ lần trước, cuối cùng là default trong
    /// asset. Giá trị remote sai kiểu bị bỏ qua. Không throw.
    /// </para>
    /// </summary>
    public static class NovaRemoteConfig
    {
        static RemoteConfigService? s_service;
        static RemoteConfigDefinitions? s_definitions;
        static ISdkLogger? s_log;
        static IDisposable? s_subscription;
        static TimeSpan s_fetchTimeout = TimeSpan.FromSeconds(3);
        static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Gọi khi bộ giá trị đang dùng thay đổi (fetch xong có giá trị mới). Chạy trên main thread.</summary>
        public static event Action? Updated;

        /// <summary>Nguồn của bộ giá trị đang dùng: Default (chưa có gì), Cache (lần trước), Remote (vừa fetch).</summary>
        public static ConfigSource Source => s_service?.Current.Value.Source ?? ConfigSource.Default;

        public static bool GetBool<TKey>(TKey key) where TKey : struct, Enum => Read(key, (d, k) => d.Bool(k), false);
        public static int GetInt<TKey>(TKey key) where TKey : struct, Enum => Read(key, (d, k) => d.Int(k), 0);
        public static long GetLong<TKey>(TKey key) where TKey : struct, Enum => Read(key, (d, k) => d.Long(k), 0L);
        public static double GetDouble<TKey>(TKey key) where TKey : struct, Enum => Read(key, (d, k) => d.Double(k), 0d);
        public static string GetString<TKey>(TKey key) where TKey : struct, Enum => Read(key, (d, k) => d.String(k), string.Empty);

        /// <summary>
        /// Fetch lại ngay (vẫn tuân theo minimum fetch interval của Firebase). true = đã có bộ giá trị remote mới;
        /// false = lỗi/timeout, tiếp tục dùng giá trị hiện tại.
        /// </summary>
        public static async Task<bool> FetchAsync()
        {
            var service = s_service;
            if (service is null) return false;
            var result = await service.FetchAndActivateAsync(s_fetchTimeout, CancellationToken.None);
            return result.IsSuccess;
        }

        internal static void Attach(RemoteConfigService service, RemoteConfigDefinitions? definitions, TimeSpan fetchTimeout, ISdkLogger log)
        {
            s_service = service;
            s_definitions = definitions;
            s_fetchTimeout = fetchTimeout;
            s_log = log;
            s_subscription?.Dispose();
            s_subscription = service.Current.Subscribe(_ => Updated?.Invoke(), emitCurrent: false);
        }

        // Đọc theo tên key cho component của SDK (NovaSdkBootstrap) không biết enum của game. false = chưa init hoặc
        // asset Remote Config Definitions không khai báo key này với kiểu T.
        internal static bool TryGet<T>(string name, out T value)
        {
            value = default!;
            if (s_definitions == null || string.IsNullOrEmpty(name)) return false;
            foreach (var key in s_definitions.AllKeys)
            {
                if (key.Name != name || key is not ConfigKey<T> typed) continue;
                value = s_service != null ? s_service.Get(typed) : typed.Default;
                return true;
            }
            return false;
        }

        static T Read<TKey, T>(TKey key, Func<RemoteConfigDefinitions<TKey>, TKey, ConfigKey<T>> pick, T missing)
            where TKey : struct, Enum
        {
            var definitions = s_definitions as RemoteConfigDefinitions<TKey>;
            if (definitions == null)
            {
                ReportOnce(typeof(TKey).Name, s_definitions == null
                    ? "Remote Config is not ready: call NovaSdk.InitializeAsync with Remote Config Definitions assigned in NovaSdkSettings"
                    : "NovaSdkSettings uses Remote Config Definitions of " + s_definitions.KeyType.Name + ", not " + typeof(TKey).Name);
                return missing;
            }
            var configKey = pick(definitions, key);
            return s_service != null ? s_service.Get(configKey) : configKey.Default;
        }

        static void ReportOnce(string id, string message)
        {
            if (!Reported.Add(id)) return;
            if (s_log != null) s_log.Error(message);
            else Debug.LogError("[Nova] " + message);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_subscription?.Dispose();
            s_subscription = null;
            s_service = null;
            s_definitions = null;
            s_log = null;
            Updated = null;
            Reported.Clear();
        }
    }
}
