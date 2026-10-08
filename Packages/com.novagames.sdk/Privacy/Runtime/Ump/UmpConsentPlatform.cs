#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile.Privacy.Ump
{
    /// <summary>
    /// Thu thập consent bằng Google UMP (message cấu hình ở AdMob &gt; Privacy &amp; messaging) và đổi kết quả thành
    /// <see cref="ConsentSnapshot"/>. Game không gọi trực tiếp: chọn Consent Source = Google UMP trong NovaSdkSettings,
    /// NovaSdk tự gather lúc khởi động; nút "Privacy settings" gọi NovaPrivacy.ShowPrivacyOptions.
    /// </summary>
    public sealed class UmpConsentPlatform : IConsentPlatform
    {
        const string Op = "privacy.ump";
        static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(10);

        readonly IUmpApi _api;
        readonly IMainThreadDispatcher _main;
        readonly IScheduler _scheduler;
        readonly IClock _clock;
        readonly ISdkLogger _log;
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        // Mỗi lúc chỉ một việc với UMP (gather hoặc form privacy options); gather gọi lặp thì dùng chung.
        Task<SdkResult<ConsentSnapshot>>? _running;
        bool _runningIsGather;
        bool _isUnderAge;
        bool _disposed;

        public UmpConsentPlatform(ModuleContext ctx)
            : this(new GoogleUmpApi(), ctx.Main, ctx.Scheduler, ctx.Clock, ctx.Logs.Create(Op)) { }

        internal UmpConsentPlatform(IUmpApi api, IMainThreadDispatcher main, IScheduler scheduler, IClock clock, ISdkLogger log)
        {
            _api = api;
            _main = main;
            _scheduler = scheduler;
            _clock = clock;
            _log = log;
        }

        public string Id => ConsentPlatformIds.GoogleUmp;

        public bool IsPrivacyOptionsRequired
        {
            get
            {
                if (_disposed) return false;
                try
                {
                    return _api.IsPrivacyOptionsRequired;
                }
                catch (Exception e)
                {
                    _log.Error("Reading privacy options requirement failed", e);
                    return false;
                }
            }
        }

        public ConsentSnapshot ReadStored(ConsentGatherOptions options)
        {
            _isUnderAge = options.IsUnderAge;
            return Snapshot();
        }

        public Task<SdkResult<ConsentSnapshot>> GatherAsync(ConsentGatherOptions options, CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult<ConsentSnapshot>>(SdkError.Disposed(Op));
            if (_running is { IsCompleted: false })
            {
                return _runningIsGather
                    ? SdkTasks.WaitAsync(_running, Op + ".gather", ct)
                    : Task.FromResult<SdkResult<ConsentSnapshot>>(Busy());
            }
            _isUnderAge = options.IsUnderAge;
            _runningIsGather = true;
            _running = RunGatherAsync(options);
            return SdkTasks.WaitAsync(_running, Op + ".gather", ct);
        }

        public Task<SdkResult<ConsentSnapshot>> ShowPrivacyOptionsAsync(CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult<ConsentSnapshot>>(SdkError.Disposed(Op));
            if (_running is { IsCompleted: false }) return Task.FromResult<SdkResult<ConsentSnapshot>>(Busy());
            _runningIsGather = false;
            _running = RunPrivacyOptionsAsync();
            return SdkTasks.WaitAsync(_running, Op + ".privacy_options", ct);
        }

        async Task<SdkResult<ConsentSnapshot>> RunGatherAsync(ConsentGatherOptions options)
        {
            // 1. Hỏi server UMP người dùng có cần trả lời form không (theo vùng địa lý, consent đã lưu, message mới).
            var update = await CallAsync(Op + ".update", UpdateTimeout, done => _api.RequestConsentInfoUpdate(options, done));
            if (_disposed) return SdkError.Disposed(Op);
            if (!update.IsSuccess)
            {
                // Mất mạng / hết giờ: UMP vẫn giữ consent của lần trước, dùng luôn.
                _log.Warning("Consent info update failed (" + update.Error + "); using consent stored by UMP");
                return SdkResult<ConsentSnapshot>.Ok(Snapshot());
            }

            // 2. Hiện form nếu cần; người dùng trả lời bao lâu cũng được nên không có timeout.
            var form = await CallAsync(Op + ".form", Timeout.InfiniteTimeSpan, done => _api.LoadAndShowConsentFormIfRequired(done));
            if (_disposed) return SdkError.Disposed(Op);
            if (!form.IsSuccess) _log.Warning("Consent form failed: " + form.Error);

            var snapshot = Snapshot();
            _log.Info("Consent " + snapshot.Jurisdiction + ", can request ads: " + snapshot.CanRequestAds);
            return SdkResult<ConsentSnapshot>.Ok(snapshot);
        }

        async Task<SdkResult<ConsentSnapshot>> RunPrivacyOptionsAsync()
        {
            var form = await CallAsync(Op + ".privacy_options", Timeout.InfiniteTimeSpan, done => _api.ShowPrivacyOptionsForm(done));
            if (_disposed) return SdkError.Disposed(Op);
            if (!form.IsSuccess) return form.Error!;
            return SdkResult<ConsentSnapshot>.Ok(Snapshot());
        }

        // Bọc API callback của UMP thành Task: kết quả về main thread, callback lặp bị bỏ qua, Dispose hủy việc chờ.
        Task<SdkResult<bool>> CallAsync(string operation, TimeSpan timeout, Action<Action<string?>> start)
        {
            var op = new VendorOperation<bool>(operation, _main, _scheduler, timeout, _lifetime.Token);
            try
            {
                start(error => op.Complete(error is null
                    ? SdkResult<bool>.Ok(true)
                    : new SdkError(operation + ".failed", SdkErrorCategory.Provider, error, true, Id)));
            }
            catch (Exception e)
            {
                op.Complete(SdkError.FromException(operation, e, Id));
            }
            return op.Task;
        }

        ConsentSnapshot Snapshot()
        {
            try
            {
                return UmpConsentMapper.Map(_api.ConsentStatus, _api.CanRequestAds, _api.ReadIabData(), _isUnderAge, _clock.UtcNow);
            }
            catch (Exception e)
            {
                _log.Error("Reading UMP consent failed", e);
                return ConsentSnapshot.Unknown with { IsUnderAge = _isUnderAge };
            }
        }

        SdkError Busy() =>
            new SdkError(Op + ".busy", SdkErrorCategory.Busy, "A consent form is already in progress", true, Id);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
    }

    static class UmpRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() =>
            AdapterRegistry.RegisterConsentPlatform(ConsentPlatformIds.GoogleUmp, ctx => new UmpConsentPlatform(ctx));
    }
}
