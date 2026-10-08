#nullable enable
using System;

namespace NovaGames.Mobile.Infrastructure
{
    public interface IMainThreadDispatcher
    {
        bool IsMainThread { get; }

        /// <summary>Luôn enqueue, kể cả khi đang ở main thread.</summary>
        void Post(Action action);
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public interface IScheduler
    {
        /// <summary>Action chạy trên main thread sau `delay`. Dispose handle để hủy.</summary>
        IDisposable Schedule(TimeSpan delay, Action action);
    }
}
