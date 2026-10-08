#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Firebase.Crashlytics;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Diagnostics;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    // Bọc static API của vendor để test thay thế được.
    internal interface IFirebaseCrashlyticsApi
    {
        void Configure(bool collectionEnabled, bool uncaughtExceptionsAsFatal);
        void Log(string message);
        void LogException(Exception exception);
        void SetCustomKey(string key, string value);
        void SetUserId(string id);
        void SetCollectionEnabled(bool enabled);
    }

    internal sealed class FirebaseCrashlyticsApi : IFirebaseCrashlyticsApi
    {
        public void Configure(bool collectionEnabled, bool uncaughtExceptionsAsFatal)
        {
            // Firebase lưu cờ này qua các lần mở app: chỉ ghi khi tắt, để lựa chọn tắt của người chơi
            // (NovaCrash.SetCollectionEnabled(false)) không bị bật lại ở lần mở sau.
            if (!collectionEnabled) Crashlytics.IsCrashlyticsCollectionEnabled = false;
            Crashlytics.ReportUncaughtExceptionsAsFatal = uncaughtExceptionsAsFatal;
        }

        public void Log(string message) => Crashlytics.Log(message);
        public void LogException(Exception exception) => Crashlytics.LogException(exception);
        public void SetCustomKey(string key, string value) => Crashlytics.SetCustomKey(key, value);
        public void SetUserId(string id) => Crashlytics.SetUserId(id);
        public void SetCollectionEnabled(bool enabled) => Crashlytics.IsCrashlyticsCollectionEnabled = enabled;
    }

    /// <summary>
    /// Firebase Crashlytics. Crash native và exception C# không bắt được do Crashlytics tự gửi ngay khi Firebase App sẵn
    /// sàng; reporter này thêm breadcrumb, non-fatal, custom key và user id. Lỗi gọi vendor chỉ log, không throw.
    /// </summary>
    public sealed class FirebaseCrashlyticsReporter : ICrashReporter
    {
        const string Op = "crashlytics.firebase";

        readonly FirebaseAppInitializer _app;
        readonly IFirebaseCrashlyticsApi _api;
        readonly ISdkLogger _log;
        Task<SdkResult>? _init;
        bool _ready;
        bool _disposed;

        public FirebaseCrashlyticsReporter(ModuleContext ctx)
            : this(FirebaseAppInitializer.Create(ctx), new FirebaseCrashlyticsApi(), ctx.Logs.Create("firebase.crashlytics"))
        {
        }

        internal FirebaseCrashlyticsReporter(FirebaseAppInitializer app, IFirebaseCrashlyticsApi api, ISdkLogger log)
        {
            _app = app;
            _api = api;
            _log = log;
        }

        public string Id => CrashReporterIds.FirebaseCrashlytics;
        public bool IsReady => _ready && !_disposed;

        public Task<SdkResult> InitializeAsync(CrashReportingOptions options, CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            if (_ready) return Task.FromResult(SdkResult.Ok);
            if (_init is null || _init.IsCompleted) _init = RunInitAsync(options);
            return SdkTasks.WaitAsync(_init, Op + ".init", ct);
        }

        async Task<SdkResult> RunInitAsync(CrashReportingOptions options)
        {
            var app = await _app.EnsureAsync(options.InitTimeout, CancellationToken.None);
            if (_disposed) return SdkError.Disposed(Op);
            if (!app.IsSuccess) return app;
            try
            {
                _api.Configure(options.CollectionEnabled, options.UncaughtExceptionsAsFatal);
            }
            catch (Exception e)
            {
                _log.Error("Configuring Crashlytics failed", e);
                return SdkError.FromException(Op, e, FirebaseProvider.Name);
            }
            _ready = true;
            _log.Info("Crashlytics ready (collection " + (options.CollectionEnabled ? "on" : "off") + ")");
            return SdkResult.Ok;
        }

        public void Log(string message) => Call("Log", () => _api.Log(message));
        public void RecordException(Exception exception) => Call("LogException", () => _api.LogException(exception));
        public void SetCustomKey(string key, string value) => Call("SetCustomKey", () => _api.SetCustomKey(key, value));
        public void SetUserId(string? id) => Call("SetUserId", () => _api.SetUserId(id ?? string.Empty));
        public void SetCollectionEnabled(bool enabled) => Call("SetCollectionEnabled", () => _api.SetCollectionEnabled(enabled));

        void Call(string what, Action action)
        {
            if (!IsReady) return;
            _log.TryRun("Crashlytics." + what, action);
        }

        public void Dispose() => _disposed = true;
    }

    static class FirebaseCrashlyticsRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() =>
            AdapterRegistry.RegisterCrashReporter(CrashReporterIds.FirebaseCrashlytics, ctx => new FirebaseCrashlyticsReporter(ctx));
    }
}
