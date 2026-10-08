#nullable enable
using System;
using System.Threading;

namespace NovaGames.Mobile.Infrastructure
{
    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    // Dispatcher dựa trên SynchronizationContext của main thread (UnitySynchronizationContext).
    public sealed class SynchronizationContextDispatcher : IMainThreadDispatcher
    {
        readonly SynchronizationContext _context;
        readonly int _mainThreadId;
        readonly ISdkLogger _log;

        SynchronizationContextDispatcher(SynchronizationContext context, int mainThreadId, ISdkLogger log)
        {
            _context = context;
            _mainThreadId = mainThreadId;
            _log = log;
        }

        // Phải gọi trên main thread.
        public static SynchronizationContextDispatcher CaptureCurrent(ISdkLogger log)
        {
            var context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("No SynchronizationContext on the current thread; capture the dispatcher on Unity's main thread.");
            return new SynchronizationContextDispatcher(context, Thread.CurrentThread.ManagedThreadId, log);
        }

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void Post(Action action)
        {
            if (action is null) throw new ArgumentNullException(nameof(action));
            _context.Post(Execute, action);
        }

        void Execute(object? state)
        {
            try
            {
                ((Action)state!)();
            }
            catch (Exception e)
            {
                _log.Error("Dispatched action threw", e);
            }
        }
    }

    // Timer chạy trên thread pool, action được post về main thread.
    public sealed class TimerScheduler : IScheduler
    {
        static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

        readonly IMainThreadDispatcher _main;

        public TimerScheduler(IMainThreadDispatcher main) { _main = main; }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            if (action is null) throw new ArgumentNullException(nameof(action));
            var handle = new Handle(_main, action);
            // Delay vô hạn hoặc quá lớn: không bao giờ fire.
            if (delay == Timeout.InfiniteTimeSpan || delay > MaxDelay) return handle;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            handle.Start(delay);
            return handle;
        }

        sealed class Handle : IDisposable
        {
            readonly IMainThreadDispatcher _main;
            readonly Action _action;
            Timer? _timer;
            int _state; // 0 = pending, 1 = fired, 2 = disposed

            public Handle(IMainThreadDispatcher main, Action action) { _main = main; _action = action; }

            public void Start(TimeSpan delay)
            {
                _timer = new Timer(OnTimer, null, delay, Timeout.InfiniteTimeSpan);
            }

            void OnTimer(object? _)
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
                Interlocked.Exchange(ref _timer, null)?.Dispose();
                _main.Post(() =>
                {
                    if (Volatile.Read(ref _state) == 1) _action();
                });
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _state, 2);
                Interlocked.Exchange(ref _timer, null)?.Dispose();
            }
        }
    }
}
