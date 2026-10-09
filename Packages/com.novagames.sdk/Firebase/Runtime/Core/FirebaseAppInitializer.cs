#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Firebase;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    internal static class FirebaseProvider
    {
        public const string Name = "firebase";
    }

    // Bọc static API của vendor để test driver thay thế được.
    internal interface IFirebaseAppApi
    {
        Task<DependencyStatus> CheckAndFixDependenciesAsync();
    }

    internal sealed class FirebaseAppApi : IFirebaseAppApi
    {
        // FirebaseApp là singleton process-wide của vendor: cache task để Remote Config và Analytics
        // không chạy CheckAndFixDependencies song song. Kết quả lỗi không được cache để lần sau thử lại.
        static readonly object Gate = new object();
        static Task<DependencyStatus>? s_check;

        public Task<DependencyStatus> CheckAndFixDependenciesAsync()
        {
            lock (Gate)
            {
                var check = s_check;
                if (check is null || check.IsFaulted || check.IsCanceled
                    || (check.Status == TaskStatus.RanToCompletion && check.Result != DependencyStatus.Available))
                {
                    check = FirebaseApp.CheckAndFixDependenciesAsync();
                    s_check = check;
                }
                return check;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            lock (Gate) s_check = null;
        }
    }

    // Kiểm tra dependency của Firebase: phải hoàn tất trước khi gọi bất kỳ Firebase API nào khác.
    internal sealed class FirebaseAppInitializer
    {
        const string Op = "firebase.app.dependencies";

        readonly IFirebaseAppApi _api;
        readonly IMainThreadDispatcher _main;
        readonly IScheduler _scheduler;
        readonly ISdkLogger _log;

        public FirebaseAppInitializer(IFirebaseAppApi api, IMainThreadDispatcher main, IScheduler scheduler, ISdkLogger log)
        {
            _api = api;
            _main = main;
            _scheduler = scheduler;
            _log = log;
        }

        public static FirebaseAppInitializer Create(ModuleContext ctx) =>
            new FirebaseAppInitializer(new FirebaseAppApi(), ctx.Main, ctx.Scheduler, ctx.Logs.Create("firebase.app"));

        public async Task<SdkResult> EnsureAsync(TimeSpan timeout, CancellationToken ct)
        {
            Task<DependencyStatus> check;
            try
            {
                check = _api.CheckAndFixDependenciesAsync();
            }
            catch (Exception e)
            {
                _log.Error("CheckAndFixDependenciesAsync threw", e);
                return SdkError.FromException(Op, e, FirebaseProvider.Name);
            }

            var result = await VendorTask.ObserveAsync(check, MapStatus, Op, _main, _scheduler, timeout, ct, FirebaseProvider.Name);
            if (!result.IsSuccess) _log.Warning("Firebase not available: " + result.Error);
            return result.AsResult();
        }

        static SdkResult<DependencyStatus> MapStatus(Task<DependencyStatus> task)
        {
            if (task.IsFaulted || task.IsCanceled) return VendorTask.FaultToError(task, Op, FirebaseProvider.Name);
            var status = task.Result;
            if (status == DependencyStatus.Available) return SdkResult<DependencyStatus>.Ok(status);

            // Đang cập nhật Google Play services: thử lại sau có thể thành công.
            bool retryable = status == DependencyStatus.UnavailableUpdating || status == DependencyStatus.UnavailableUpdaterequired;
            return new SdkError("firebase.app.unavailable", SdkErrorCategory.Unavailable,
                "Firebase dependencies unavailable: " + status, retryable, FirebaseProvider.Name);
        }
    }
}
