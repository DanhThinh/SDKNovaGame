#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Firebase.RemoteConfig;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    /// <summary>Adapter Firebase cho IRemoteConfigSource. Default, cache và validation thuộc RemoteConfigService (Core).</summary>
    public sealed class FirebaseRemoteConfigSource : IRemoteConfigSource
    {
        const string Op = "remote_config.firebase";

        readonly FirebaseAppInitializer _app;
        readonly IFirebaseRemoteConfigApi _api;
        readonly IMainThreadDispatcher _main;
        readonly IScheduler _scheduler;
        readonly IClock _clock;
        readonly ISdkLogger _log;
        readonly RemoteConfigOptions _settings;

        Task<SdkResult>? _init;
        bool _initialized;
        bool _disposed;

        public FirebaseRemoteConfigSource(ModuleContext ctx)
            : this(FirebaseAppInitializer.Create(ctx), new FirebaseRemoteConfigApi(), ctx.Main, ctx.Scheduler,
                   ctx.Clock, ctx.Logs.Create("firebase.remote_config"), ctx.Settings.RemoteConfig)
        {
        }

        internal FirebaseRemoteConfigSource(
            FirebaseAppInitializer app, IFirebaseRemoteConfigApi api, IMainThreadDispatcher main,
            IScheduler scheduler, IClock clock, ISdkLogger log, RemoteConfigOptions settings)
        {
            _app = app;
            _api = api;
            _main = main;
            _scheduler = scheduler;
            _clock = clock;
            _log = log;
            _settings = settings;
        }

        public string Id => RemoteConfigSourceIds.Firebase;

        public Task<SdkResult> InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            if (_initialized) return Task.FromResult(SdkResult.Ok);
            return SdkTasks.WaitAsync(StartInit(), Op + ".init", ct);
        }

        public async Task<SdkResult<RemoteConfigFetchResult>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_disposed) return SdkError.Disposed(Op);
            var deadline = _clock.UtcNow + timeout;

            if (!_initialized)
            {
                var init = await SdkTasks.WaitAsync(StartInit(), Op + ".init", Remaining(deadline), _scheduler, ct);
                if (!init.IsSuccess) return init.Error!;
            }

            Task fetchTask;
            try
            {
                fetchTask = _api.FetchAsync(_settings.MinimumFetchInterval);
            }
            catch (Exception e)
            {
                return SdkError.FromException(Op + ".fetch", e, FirebaseProvider.Name);
            }

            var fetched = await VendorTask.ObserveVoidAsync(fetchTask, MapFetch, Op + ".fetch",
                _main, _scheduler, Remaining(deadline), ct, FirebaseProvider.Name);
            if (_disposed) return SdkError.Disposed(Op);
            if (!fetched.IsSuccess) return RefineFetchError(fetched.Error!);

            // Task fetch có thể complete bình thường nhưng Info báo Failure.
            var info = SafeGetInfo();
            if (info.HasValue && info.Value.Status == LastFetchStatus.Failure)
                return FetchFailure(info.Value, null);

            Task<bool> activateTask;
            try
            {
                activateTask = _api.ActivateAsync();
            }
            catch (Exception e)
            {
                return SdkError.FromException(Op + ".activate", e, FirebaseProvider.Name);
            }

            var activated = await VendorTask.ObserveAsync(activateTask, MapActivate, Op + ".activate",
                _main, _scheduler, Remaining(deadline), ct, FirebaseProvider.Name);
            if (_disposed) return SdkError.Disposed(Op);
            if (!activated.TryGetValue(out bool changed)) return activated.Error!;

            IReadOnlyDictionary<string, string> values;
            try
            {
                values = _api.GetRemoteValues();
            }
            catch (Exception e)
            {
                return SdkError.FromException(Op + ".read", e, FirebaseProvider.Name);
            }

            info = SafeGetInfo();
            DateTime? fetchTime = info.HasValue && info.Value.Status == LastFetchStatus.Success
                ? info.Value.FetchTime.ToUniversalTime()
                : (DateTime?)null;

            _log.Debug($"Fetched {values.Count} remote values (activated new: {changed})");
            foreach (var pair in values)
                _log.Debug($"Remote config '{pair.Key}' = '{pair.Value}'");
            return SdkResult<RemoteConfigFetchResult>.Ok(new RemoteConfigFetchResult(values, changed, fetchTime));
        }

        Task<SdkResult> StartInit()
        {
            var task = _init;
            if (task is null || task.IsCompleted)
            {
                task = RunInitAsync(_settings.InitTimeout);
                _init = task;
            }
            return task;
        }

        // Init dùng chung, không bị hủy bởi token của caller.
        async Task<SdkResult> RunInitAsync(TimeSpan timeout)
        {
            var deadline = _clock.UtcNow + timeout;
            var app = await _app.EnsureAsync(timeout, CancellationToken.None);
            if (!app.IsSuccess) return app;

            Task ensureTask;
            try
            {
                ensureTask = _api.EnsureInitializedAsync();
            }
            catch (Exception e)
            {
                return SdkError.FromException(Op + ".init", e, FirebaseProvider.Name);
            }

            var ensured = await VendorTask.ObserveVoidAsync(ensureTask, MapInit, Op + ".init",
                _main, _scheduler, Remaining(deadline), CancellationToken.None, FirebaseProvider.Name);
            if (!ensured.IsSuccess)
            {
                _log.Warning("EnsureInitializedAsync failed: " + ensured.Error);
                return ensured.AsResult();
            }

            _initialized = !_disposed;
            return SdkResult.Ok;
        }

        SdkError RefineFetchError(SdkError error)
        {
            if (error.Category == SdkErrorCategory.Timeout || error.Category == SdkErrorCategory.Cancelled) return error;
            var info = SafeGetInfo();
            return info.HasValue ? FetchFailure(info.Value, error.Exception) : error;
        }

        static SdkError FetchFailure(RemoteFetchInfo info, Exception? exception)
        {
            if (info.FailureReason == FetchFailureReason.Throttled)
            {
                return new SdkError(Op + ".throttled", SdkErrorCategory.Unavailable,
                    "Fetch throttled until " + info.ThrottledEndTime.ToUniversalTime().ToString("o"),
                    false, FirebaseProvider.Name, exception);
            }
            return new SdkError(Op + ".fetch_failed", SdkErrorCategory.Network,
                "Fetch failed: " + info.FailureReason, true, FirebaseProvider.Name, exception);
        }

        RemoteFetchInfo? SafeGetInfo()
        {
            try
            {
                return _api.GetInfo();
            }
            catch (Exception e)
            {
                _log.Warning("Reading remote config info failed", e);
                return null;
            }
        }

        TimeSpan Remaining(DateTime deadline) => SdkTasks.Remaining(deadline, _clock);

        static SdkResult<bool> MapInit(Task task) =>
            task.IsFaulted || task.IsCanceled ? VendorTask.FaultToError(task, Op + ".init", FirebaseProvider.Name) : SdkResult<bool>.Ok(true);

        static SdkResult<bool> MapFetch(Task task) =>
            task.IsFaulted || task.IsCanceled ? VendorTask.FaultToError(task, Op + ".fetch", FirebaseProvider.Name) : SdkResult<bool>.Ok(true);

        static SdkResult<bool> MapActivate(Task<bool> task) =>
            task.IsFaulted || task.IsCanceled ? VendorTask.FaultToError(task, Op + ".activate", FirebaseProvider.Name) : SdkResult<bool>.Ok(task.Result);

        public void Dispose()
        {
            // Firebase Remote Config không có callback/listener nào được đăng ký bởi adapter.
            _disposed = true;
            _initialized = false;
        }
    }

    static class FirebaseRemoteConfigRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() =>
            AdapterRegistry.RegisterRemoteConfig(RemoteConfigSourceIds.Firebase, ctx => new FirebaseRemoteConfigSource(ctx));
    }
}
