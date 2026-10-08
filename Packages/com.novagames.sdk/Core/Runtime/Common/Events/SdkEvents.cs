#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile
{
    public interface ISdkEvent<out T>
    {
        IDisposable Subscribe(Action<T> handler);
    }

    public interface ISdkProperty<out T>
    {
        T Value { get; }
        IDisposable Subscribe(Action<T> handler, bool emitCurrent = true);
    }

    /// <summary>Copy-on-write handler array; Raise chỉ gọi trên main thread; mỗi handler bọc try/catch.</summary>
    public sealed class SdkEvent<T> : ISdkEvent<T>
    {
        static readonly Action<T>[] Empty = new Action<T>[0];

        readonly ISdkLogger? _log;
        Action<T>[] _handlers = Empty;

        public SdkEvent(ISdkLogger? log = null) { _log = log; }

        public int SubscriberCount => Volatile.Read(ref _handlers).Length;

        public IDisposable Subscribe(Action<T> handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            while (true)
            {
                var current = Volatile.Read(ref _handlers);
                var next = new Action<T>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = handler;
                if (Interlocked.CompareExchange(ref _handlers, next, current) == current) break;
            }
            return new Subscription(this, handler);
        }

        public void Raise(T value)
        {
            var handlers = Volatile.Read(ref _handlers);
            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i](value);
                }
                catch (Exception e)
                {
                    SdkEventErrors.Report(_log, typeof(T).Name, e);
                }
            }
        }

        public void Clear() => Volatile.Write(ref _handlers, Empty);

        void Unsubscribe(Action<T> handler)
        {
            while (true)
            {
                var current = Volatile.Read(ref _handlers);
                int index = Array.IndexOf(current, handler);
                if (index < 0) return;
                Action<T>[] next;
                if (current.Length == 1)
                {
                    next = Empty;
                }
                else
                {
                    next = new Action<T>[current.Length - 1];
                    Array.Copy(current, 0, next, 0, index);
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                }
                if (Interlocked.CompareExchange(ref _handlers, next, current) == current) return;
            }
        }

        sealed class Subscription : IDisposable
        {
            SdkEvent<T>? _owner;
            readonly Action<T> _handler;

            public Subscription(SdkEvent<T> owner, Action<T> handler) { _owner = owner; _handler = handler; }

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_handler);
        }
    }

    public sealed class SdkProperty<T> : ISdkProperty<T>
    {
        readonly SdkEvent<T> _changed;
        readonly IEqualityComparer<T> _comparer;
        T _value;

        public SdkProperty(T initial, ISdkLogger? log = null, IEqualityComparer<T>? comparer = null)
        {
            _value = initial;
            _changed = new SdkEvent<T>(log);
            _comparer = comparer ?? EqualityComparer<T>.Default;
        }

        public T Value => _value;

        public IDisposable Subscribe(Action<T> handler, bool emitCurrent = true)
        {
            var subscription = _changed.Subscribe(handler);
            if (emitCurrent)
            {
                try
                {
                    handler(_value);
                }
                catch (Exception e)
                {
                    SdkEventErrors.Report(null, typeof(T).Name, e);
                }
            }
            return subscription;
        }

        /// <summary>Trả về true nếu giá trị thay đổi và subscriber đã được thông báo.</summary>
        public bool Set(T value)
        {
            if (_comparer.Equals(_value, value)) return false;
            _value = value;
            _changed.Raise(value);
            return true;
        }

        public void ClearSubscribers() => _changed.Clear();
    }

    static class SdkEventErrors
    {
        public static void Report(ISdkLogger? log, string eventName, Exception e)
        {
            if (log != null) log.Error("Event handler for " + eventName + " threw", e);
            else SdkLogOutput.Exception(e);
        }
    }
}
