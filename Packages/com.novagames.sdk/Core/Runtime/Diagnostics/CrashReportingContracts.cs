#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.Diagnostics
{
    public static class CrashReporterIds
    {
        public const string FirebaseCrashlytics = "firebase_crashlytics";
    }

    public sealed record CrashReportingOptions
    {
        public static CrashReportingOptions Default { get; } = new CrashReportingOptions();

        /// <summary>Gửi crash/non-fatal lên dashboard. Tắt = thu thập bị dừng (vd. người chơi từ chối trong Settings của game).</summary>
        public bool CollectionEnabled { get; init; } = true;

        /// <summary>Exception C# không được bắt báo là crash (fatal) thay vì non-fatal.</summary>
        public bool UncaughtExceptionsAsFatal { get; init; }

        public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// SPI cho dịch vụ báo crash (Firebase Crashlytics). Crash native và exception C# không bắt được vendor tự gửi;
    /// SPI này cho game ghi thêm breadcrumb, non-fatal, custom key và user id. Gọi trên main thread, sau InitializeAsync thành công.
    /// </summary>
    public interface ICrashReporter : IDisposable
    {
        string Id { get; }
        bool IsReady { get; }
        Task<SdkResult> InitializeAsync(CrashReportingOptions options, CancellationToken ct);

        /// <summary>Breadcrumb: dòng log đi kèm báo cáo crash tiếp theo.</summary>
        void Log(string message);

        /// <summary>Non-fatal: exception game đã bắt nhưng muốn thấy trên dashboard.</summary>
        void RecordException(Exception exception);

        void SetCustomKey(string key, string value);
        void SetUserId(string? id);
        void SetCollectionEnabled(bool enabled);
    }
}
