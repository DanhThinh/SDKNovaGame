#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Ads
{
    internal enum AdUnitState : byte { Idle, Loading, Ready, Showing, NoFill, Failed, Expired, Destroyed }

    // State machine của một full-screen ad unit: load, retry backoff + jitter, load timeout, TTL.
    // Chỉ gọi trên main thread. Adapter chỉ thực hiện Load/Discard thô.
    internal sealed class AdUnitSlot : IDisposable
    {
        readonly IAdsAdapter _adapter;
        readonly IMainThreadDispatcher _main;
        readonly IClock _clock;
        readonly IScheduler _scheduler;
        readonly ISdkLogger _log;
        readonly Func<bool> _canLoad;
        readonly Func<double> _random;
        readonly Action<AdUnitSlot> _changed;
        readonly TimeSpan _ttl;
        readonly TimeSpan _loadTimeout;
        readonly TimeSpan _maxRetryDelay;
        readonly List<Waiter> _waiters = new List<Waiter>();

        DateTime _loadedUtc;
        int _attempt;
        IDisposable? _retry;
        IDisposable? _loadTimer;
        IDisposable? _ttlTimer;

        public AdUnitSlot(AdUnit unit, IAdsAdapter adapter, IMainThreadDispatcher main, IClock clock, IScheduler scheduler, ISdkLogger log,
                          Func<bool> canLoad, Func<double> random, Action<AdUnitSlot> changed,
                          TimeSpan ttl, TimeSpan loadTimeout, TimeSpan maxRetryDelay)
        {
            Unit = unit;
            _adapter = adapter;
            _main = main;
            _clock = clock;
            _scheduler = scheduler;
            _log = log;
            _canLoad = canLoad;
            _random = random;
            _changed = changed;
            _ttl = ttl;
            _loadTimeout = loadTimeout;
            _maxRetryDelay = maxRetryDelay;
        }

        public AdUnit Unit { get; }
        public AdUnitState State { get; private set; } = AdUnitState.Idle;

        public bool IsReady
        {
            get
            {
                if (State != AdUnitState.Ready) return false;
                if (IsExpired)
                {
                    Expire();
                    return false;
                }
                return SafeIsReady();
            }
        }

        bool IsExpired => _ttl > TimeSpan.Zero && _clock.UtcNow - _loadedUtc >= _ttl;

        /// <summary>Load nếu đang rảnh và điều kiện cho phép. Retry đang chờ thì không load sớm hơn.</summary>
        public void EnsureLoaded()
        {
            if (State == AdUnitState.Ready)
            {
                if (IsExpired) Expire();
                return;
            }
            if (State == AdUnitState.Loading || State == AdUnitState.Showing || State == AdUnitState.Destroyed) return;
            if (_retry != null || !_canLoad()) return;
            StartLoad();
        }

        void StartLoad()
        {
            SetState(AdUnitState.Loading);
            // Vendor vẫn giữ ad đã load (MAX không hủy được ad sau Discard/timeout): dùng luôn, không load lại.
            if (SafeIsReady())
            {
                OnLoaded();
                return;
            }
            _loadTimer = _scheduler.Schedule(_loadTimeout, () =>
            {
                _loadTimer = null;
                // Callback loaded có thể còn xếp hàng sau timer (Android: Unity pause trong lúc load, timer chạy trên
                // thread pool): hỏi vendor trước khi coi là timeout.
                if (State == AdUnitState.Loading && SafeIsReady())
                {
                    OnLoaded();
                    return;
                }
                OnLoadFailed(new AdLoadError(AdLoadFailure.Timeout, "load timed out"));
            });
            try
            {
                _adapter.Load(Unit);
            }
            catch (Exception e)
            {
                OnLoadFailed(new AdLoadError(AdLoadFailure.Provider, e.GetType().Name + ": " + e.Message));
            }
        }

        public void OnLoaded()
        {
            if (State != AdUnitState.Loading)
            {
                // Callback trễ sau Discard/timeout: không giữ ad ngoài state machine.
                if (State != AdUnitState.Ready && State != AdUnitState.Showing) SafeDiscard();
                return;
            }
            DisposeTimer(ref _loadTimer);
            _attempt = 0;
            _loadedUtc = _clock.UtcNow;
            if (_ttl > TimeSpan.Zero) _ttlTimer = _scheduler.Schedule(_ttl, () => { _ttlTimer = null; Expire(); });
            SetState(AdUnitState.Ready);
            CompleteWaiters(true);
        }

        public void OnLoadFailed(AdLoadError error)
        {
            if (State != AdUnitState.Loading) return;
            DisposeTimer(ref _loadTimer);
            // Request cũ của vendor có thể vẫn chạy sau timeout: discard trước khi retry; adapter phải bỏ qua
            // callback của request cũ đó.
            if (error.Kind == AdLoadFailure.Timeout) SafeDiscard();
            SetState(error.Kind == AdLoadFailure.NoFill ? AdUnitState.NoFill : AdUnitState.Failed);
            CompleteWaiters(false);
            ScheduleRetry(error);
        }

        void ScheduleRetry(AdLoadError error)
        {
            _attempt++;
            // 2, 4, 8, ... giây, trần MaxRetryDelay, jitter ±20%.
            double seconds = Math.Min(_maxRetryDelay.TotalSeconds, Math.Pow(2, Math.Min(_attempt, 16)));
            var delay = TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * _random()));
            _log.Debug(Unit + " load failed (" + error.Kind + ": " + error.Message + "), retry #" + _attempt + " in "
                       + delay.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s");
            _retry = _scheduler.Schedule(delay, () =>
            {
                _retry = null;
                // Offline/background chỉ là tạm thời: tiếp tục retry (có trần) để slot tự hồi phục kể cả khi game
                // không hỏi availability và không có lifecycle callback nào.
                if (!_canLoad())
                {
                    ScheduleRetry(error);
                    return;
                }
                EnsureLoaded();
            });
        }

        public void BeginShow()
        {
            DisposeTimer(ref _ttlTimer);
            SetState(AdUnitState.Showing);
        }

        /// <summary>Sau show (close hoặc display fail) ad đã dùng hết: về Idle và tự load lại.</summary>
        public void EndShow()
        {
            if (State != AdUnitState.Showing) return;
            SetState(AdUnitState.Idle);
            EnsureLoaded();
        }

        /// <summary>Bỏ ad đã load / dừng retry (TTL, remove_ads, consent thu hồi, kill switch).</summary>
        public void Discard()
        {
            if (State == AdUnitState.Showing || State == AdUnitState.Destroyed) return;
            DisposeTimer(ref _retry);
            DisposeTimer(ref _loadTimer);
            DisposeTimer(ref _ttlTimer);
            if (State == AdUnitState.Ready || State == AdUnitState.Loading) SafeDiscard();
            _attempt = 0;
            SetState(AdUnitState.Idle);
            CompleteWaiters(false);
        }

        void Expire()
        {
            if (State != AdUnitState.Ready) return;
            DisposeTimer(ref _ttlTimer);
            _log.Debug(Unit + " expired after " + _ttl);
            SafeDiscard();
            SetState(AdUnitState.Expired);
            EnsureLoaded();
        }

        /// <summary>true = Ready trong thời gian chờ.</summary>
        public Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (IsReady) return Task.FromResult(true);
            if (timeout <= TimeSpan.Zero || ct.IsCancellationRequested || State == AdUnitState.Destroyed) return Task.FromResult(false);

            var waiter = new Waiter();
            _waiters.Add(waiter);
            waiter.Timer = _scheduler.Schedule(timeout, () => Resolve(waiter, false));
            // Token có thể cancel từ thread khác: chuyển về main thread trước khi đụng scheduler/_waiters.
            if (ct.CanBeCanceled) waiter.Registration = ct.Register(() => _main.Post(() => Resolve(waiter, false)));
            return waiter.Tcs.Task;
        }

        void Resolve(Waiter waiter, bool ready)
        {
            if (!waiter.Tcs.TrySetResult(ready)) return;
            _waiters.Remove(waiter);
            waiter.Timer?.Dispose();
            waiter.Registration.Dispose();
        }

        sealed class Waiter
        {
            public readonly TaskCompletionSource<bool> Tcs =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public IDisposable? Timer;
            public CancellationTokenRegistration Registration;
        }

        public void Dispose()
        {
            if (State == AdUnitState.Destroyed) return;
            DisposeTimer(ref _retry);
            DisposeTimer(ref _loadTimer);
            DisposeTimer(ref _ttlTimer);
            if (State == AdUnitState.Ready) SafeDiscard();
            State = AdUnitState.Destroyed;
            CompleteWaiters(false);
        }

        void CompleteWaiters(bool ready)
        {
            if (_waiters.Count == 0) return;
            foreach (var waiter in _waiters.ToArray()) Resolve(waiter, ready);
        }

        void SetState(AdUnitState state)
        {
            if (State == state) return;
            State = state;
            _changed(this);
        }

        bool SafeIsReady()
        {
            try
            {
                return _adapter.IsReady(Unit);
            }
            catch (Exception e)
            {
                _log.Error(Unit + " IsReady threw", e);
                return false;
            }
        }

        void SafeDiscard()
        {
            try
            {
                _adapter.Discard(Unit);
            }
            catch (Exception e)
            {
                _log.Error(Unit + " Discard threw", e);
            }
        }

        static void DisposeTimer(ref IDisposable? timer)
        {
            timer?.Dispose();
            timer = null;
        }
    }
}
