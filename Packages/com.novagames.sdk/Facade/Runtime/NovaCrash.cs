#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using NovaGames.Mobile.Diagnostics;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// Báo crash qua Firebase Crashlytics. Crash native và exception C# không bắt được được gửi tự động; dùng class này để
    /// thêm thông tin giúp đọc crash trên dashboard:
    /// <code>
    /// NovaCrash.Log("enter level 12");                 // breadcrumb, hiện kèm crash tiếp theo
    /// NovaCrash.SetCustomKey("level", 12);              // giá trị mới nhất hiện trên mỗi crash
    /// try { LoadSave(); } catch (Exception e) { NovaCrash.LogException(e); }   // non-fatal
    /// </code>
    /// Gọi trước khi SDK sẵn sàng thì được giữ lại và gửi sau. Gọi trên main thread; không throw.
    /// </summary>
    public static class NovaCrash
    {
        internal const int MaxPendingLogs = 64;
        internal const int MaxPendingExceptions = 16;

        static readonly Queue<string> PendingLogs = new Queue<string>();
        static readonly Queue<Exception> PendingExceptions = new Queue<Exception>();
        static readonly Dictionary<string, string> PendingKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        static ICrashReporter? s_reporter;
        static ISdkLogger? s_log;
        static bool s_hasPendingUserId;
        static string? s_pendingUserId;
        static bool? s_pendingCollection;

        /// <summary>Crashlytics đã sẵn sàng.</summary>
        public static bool IsReady => s_reporter?.IsReady == true;

        /// <summary>Gắn reporter đã init. <see cref="NovaSdk.InitializeAsync"/> tự gọi. Gửi các lệnh đã giữ lại.</summary>
        public static void Bind(ICrashReporter reporter, ISdkLogger log)
        {
            s_reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
            s_log = log ?? throw new ArgumentNullException(nameof(log));

            if (s_pendingCollection.HasValue) Run("SetCollectionEnabled", r => r.SetCollectionEnabled(s_pendingCollection.Value));
            if (s_hasPendingUserId) Run("SetUserId", r => r.SetUserId(s_pendingUserId));
            foreach (var pair in PendingKeys) Run("SetCustomKey", r => r.SetCustomKey(pair.Key, pair.Value));
            while (PendingLogs.Count > 0)
            {
                var message = PendingLogs.Dequeue();
                Run("Log", r => r.Log(message));
            }
            while (PendingExceptions.Count > 0)
            {
                var exception = PendingExceptions.Dequeue();
                Run("LogException", r => r.RecordException(exception));
            }
            ClearPending();
        }

        /// <summary>Gỡ reporter đã <see cref="Bind"/> (chỉ khi đúng reporter đó).</summary>
        public static void Unbind(ICrashReporter reporter)
        {
            if (ReferenceEquals(s_reporter, reporter)) s_reporter = null;
        }

        /// <summary>Breadcrumb: dòng log hiện kèm crash tiếp theo trên dashboard. Không ghi dữ liệu cá nhân.</summary>
        public static void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (TryRun("Log", r => r.Log(message))) return;
            Enqueue(PendingLogs, message, MaxPendingLogs);
        }

        /// <summary>Non-fatal: exception đã bắt nhưng muốn thấy trên dashboard (mục Non-fatals).</summary>
        public static void LogException(Exception exception)
        {
            if (exception is null) return;
            if (TryRun("LogException", r => r.RecordException(exception))) return;
            Enqueue(PendingExceptions, exception, MaxPendingExceptions);
        }

        /// <summary>Giá trị gắn vào mọi crash sau đó (vd. "level", "mode"). Gọi lại để cập nhật.</summary>
        public static void SetCustomKey(string key, object? value)
        {
            if (string.IsNullOrEmpty(key)) return;
            var text = value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
            if (TryRun("SetCustomKey", r => r.SetCustomKey(key, text))) return;
            PendingKeys[key] = text;
        }

        /// <summary>User id của game (không dùng email/số điện thoại). <see cref="NovaAnalytics.SetUserId"/> tự gọi hàm này.</summary>
        public static void SetUserId(string? id)
        {
            if (TryRun("SetUserId", r => r.SetUserId(id))) return;
            s_hasPendingUserId = true;
            s_pendingUserId = id;
        }

        /// <summary>Bật/tắt thu thập crash (vd. người chơi tắt trong Settings của game). Có hiệu lực từ lần mở app sau trên một số platform.</summary>
        public static void SetCollectionEnabled(bool enabled)
        {
            if (TryRun("SetCollectionEnabled", r => r.SetCollectionEnabled(enabled))) return;
            s_pendingCollection = enabled;
        }

        /// <summary>Gửi một non-fatal thử để kiểm tra dashboard (mục Non-fatals, xuất hiện sau vài phút).</summary>
        public static void TestNonFatal() =>
            LogException(new InvalidOperationException("NovaCrash test non-fatal " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)));

        /// <summary>
        /// Làm app crash thật để kiểm tra Crashlytics (chỉ Development build; mở lại app để báo cáo được gửi).
        /// Bản release và Editor bỏ qua.
        /// </summary>
        public static void TestCrash()
        {
            if (Application.isEditor || !Debug.isDebugBuild)
            {
                Debug.LogWarning("[Nova][crash] TestCrash only runs in a Development build on device");
                return;
            }
            Log("NovaCrash.TestCrash");
            UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.Abort);
        }

        static bool TryRun(string what, Action<ICrashReporter> action)
        {
            var reporter = s_reporter;
            if (reporter is null) return false;
            Run(what, action);
            return true;
        }

        static void Run(string what, Action<ICrashReporter> action)
        {
            var reporter = s_reporter;
            if (reporter is null) return;
            try
            {
                action(reporter);
            }
            catch (Exception e)
            {
                s_log?.Error("Crash reporter " + what + " threw", e);
            }
        }

        static void Enqueue<T>(Queue<T> queue, T item, int max)
        {
            if (queue.Count >= max) queue.Dequeue();
            queue.Enqueue(item);
        }

        static void ClearPending()
        {
            PendingLogs.Clear();
            PendingExceptions.Clear();
            PendingKeys.Clear();
            s_hasPendingUserId = false;
            s_pendingUserId = null;
            s_pendingCollection = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_reporter = null;
            s_log = null;
            ClearPending();
        }
    }
}
