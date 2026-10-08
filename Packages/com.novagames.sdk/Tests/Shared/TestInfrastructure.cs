#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Testing
{
    public sealed class FakeClock : IClock
    {
        public FakeClock(DateTime? start = null)
        {
            UtcNow = start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        public DateTime UtcNow { get; set; }

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    // Dispatcher kiêm SynchronizationContext: action/continuation chỉ chạy khi test gọi Drain/RunUntilCompleted.
    public sealed class QueueDispatcher : SynchronizationContext, IMainThreadDispatcher
    {
        readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new Queue<(SendOrPostCallback, object?)>();
        readonly object _gate = new object();
        readonly int _threadId = Thread.CurrentThread.ManagedThreadId;

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _threadId;

        public int PendingCount
        {
            get { lock (_gate) return _queue.Count; }
        }

        public void Post(Action action) => Post(state => ((Action)state!)(), action);

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate) _queue.Enqueue((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;

        public bool RunOne()
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (_gate)
            {
                if (_queue.Count == 0) return false;
                item = _queue.Dequeue();
            }
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                item.Callback(item.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
            return true;
        }

        public int Drain(int maxSteps = 10000)
        {
            int steps = 0;
            while (steps < maxSteps && RunOne()) steps++;
            return steps;
        }

        public bool RunUntilCompleted(Task task, int maxSteps = 10000)
        {
            int steps = 0;
            while (!task.IsCompleted && steps < maxSteps && RunOne()) steps++;
            return task.IsCompleted;
        }
    }

    // Scheduler theo FakeClock: action đến hạn được post về dispatcher khi Advance/FireDue.
    public sealed class ManualScheduler : IScheduler
    {
        readonly FakeClock _clock;
        readonly IMainThreadDispatcher _main;
        readonly List<Entry> _entries = new List<Entry>();

        public ManualScheduler(FakeClock clock, IMainThreadDispatcher main)
        {
            _clock = clock;
            _main = main;
        }

        public int PendingCount => _entries.Count;

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var entry = new Entry(this, action, delay == Timeout.InfiniteTimeSpan
                ? DateTime.MaxValue
                : _clock.UtcNow + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay));
            _entries.Add(entry);
            return entry;
        }

        public void Advance(TimeSpan by)
        {
            _clock.Advance(by);
            FireDue();
        }

        public void FireDue()
        {
            var due = _entries.FindAll(e => e.DueUtc <= _clock.UtcNow);
            due.Sort((a, b) => a.DueUtc.CompareTo(b.DueUtc));
            foreach (var entry in due)
            {
                _entries.Remove(entry);
                _main.Post(entry.Action);
            }
        }

        sealed class Entry : IDisposable
        {
            readonly ManualScheduler _owner;

            public Entry(ManualScheduler owner, Action action, DateTime dueUtc)
            {
                _owner = owner;
                Action = action;
                DueUtc = dueUtc;
            }

            public Action Action { get; }
            public DateTime DueUtc { get; }

            public void Dispose() => _owner._entries.Remove(this);
        }
    }

    public sealed class InMemoryKeyValueStore : IKeyValueStore
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool FailWrites { get; set; }
        public IReadOnlyDictionary<string, string> Values => _values;

        public bool TryGetString(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);

        public void SetString(string key, string value)
        {
            if (FailWrites) throw new InvalidOperationException("Injected write failure");
            _values[key] = value;
        }

        public void Delete(string key) => _values.Remove(key);
        public void Flush() { }
    }

    public sealed class TestLogger : ISdkLogger, ISdkLoggerFactory
    {
        public readonly List<(SdkLogLevel Level, string Message)> Entries = new List<(SdkLogLevel, string)>();

        public bool IsEnabled(SdkLogLevel level) => true;
        public void Log(SdkLogLevel level, string message, Exception? exception = null) => Entries.Add((level, message));
        public ISdkLogger Create(string module) => this;

        public int Count(SdkLogLevel level) => Entries.FindAll(e => e.Level == level).Count;
    }

    // Hạ tầng deterministic dùng chung cho test: set SynchronizationContext = dispatcher khi tạo, khôi phục khi Dispose.
    public sealed class TestHarness : IDisposable
    {
        readonly SynchronizationContext? _previousContext;

        public TestHarness(RuntimeSdkSettings? settings = null)
        {
            _previousContext = SynchronizationContext.Current;
            Main = new QueueDispatcher();
            Clock = new FakeClock();
            Scheduler = new ManualScheduler(Clock, Main);
            Store = new InMemoryKeyValueStore();
            Log = new TestLogger();
            Context = new ModuleContext(settings ?? RuntimeSdkSettings.Development, Main, Clock, Scheduler, Store, Log);
            SynchronizationContext.SetSynchronizationContext(Main);
        }

        public QueueDispatcher Main { get; }
        public FakeClock Clock { get; }
        public ManualScheduler Scheduler { get; }
        public InMemoryKeyValueStore Store { get; }
        public TestLogger Log { get; }
        public ModuleContext Context { get; }

        public T Run<T>(Task<T> task)
        {
            if (!Main.RunUntilCompleted(task))
                throw new InvalidOperationException("Task did not complete; pending main-thread items: " + Main.PendingCount
                                                    + ", scheduled: " + Scheduler.PendingCount);
            return task.Result;
        }

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previousContext);
    }
}
