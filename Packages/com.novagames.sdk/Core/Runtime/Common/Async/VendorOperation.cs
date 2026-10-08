#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile
{
    /// <summary>Exactly-once cho vendor callback: callback/timeout/cancel đầu tiên thắng, kết quả deliver trên main thread.</summary>
    public sealed class VendorOperation<T> : IDisposable
    {
        readonly TaskCompletionSource<SdkResult<T>> _tcs =
            new TaskCompletionSource<SdkResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly IMainThreadDispatcher _main;
        readonly string _name;
        readonly IDisposable _timeout;
        readonly CancellationTokenRegistration _ctr;
        int _done;

        public VendorOperation(string name, IMainThreadDispatcher main, IScheduler scheduler,
                               TimeSpan timeout, CancellationToken ct)
        {
            _name = name;
            _main = main;
            _timeout = scheduler.Schedule(timeout, () => Complete(SdkError.Timeout(name)));
            _ctr = ct.CanBeCanceled ? ct.Register(() => Complete(SdkError.Cancelled(name))) : default;
        }

        public string Name => _name;
        public bool IsCompleted => Volatile.Read(ref _done) != 0;
        public Task<SdkResult<T>> Task => _tcs.Task;

        /// <summary>Gọi từ thread bất kỳ; caller đầu tiên thắng; duplicate trả false.</summary>
        public bool Complete(SdkResult<T> result)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return false;
            _main.Post(() =>
            {
                _timeout.Dispose();
                _ctr.Dispose();
                _tcs.TrySetResult(result);
            });
            return true;
        }

        public void Dispose() => Complete(SdkError.Cancelled(_name));
    }

    /// <summary>Bọc Task của vendor (thường complete trên background thread) thành VendorOperation có timeout + cancel.</summary>
    public static class VendorTask
    {
        /// <summary>`map` chạy trên thread hoàn tất task vendor (không phải main thread): không gọi Unity API trong đó.</summary>
        public static Task<SdkResult<TOut>> ObserveAsync<TIn, TOut>(
            Task<TIn> vendorTask, Func<Task<TIn>, SdkResult<TOut>> map, string operation,
            IMainThreadDispatcher main, IScheduler scheduler, TimeSpan timeout, CancellationToken ct,
            string? provider = null)
        {
            var op = new VendorOperation<TOut>(operation, main, scheduler, timeout, ct);
            vendorTask.ContinueWith(t => op.Complete(SafeMap(t, map, operation, provider)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return op.Task;
        }

        public static Task<SdkResult<TOut>> ObserveVoidAsync<TOut>(
            Task vendorTask, Func<Task, SdkResult<TOut>> map, string operation,
            IMainThreadDispatcher main, IScheduler scheduler, TimeSpan timeout, CancellationToken ct,
            string? provider = null)
        {
            var op = new VendorOperation<TOut>(operation, main, scheduler, timeout, ct);
            vendorTask.ContinueWith(t => op.Complete(SafeMap(t, map, operation, provider)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return op.Task;
        }

        /// <summary>Lỗi chung cho task vendor bị fault/cancel.</summary>
        public static SdkError FaultToError(Task task, string operation, string? provider = null)
        {
            if (task.IsCanceled) return SdkError.Cancelled(operation) with { Provider = provider };
            var inner = task.Exception?.GetBaseException();
            return inner is null
                ? new SdkError(operation + ".failed", SdkErrorCategory.Provider, operation + " failed", false, provider)
                : SdkError.FromException(operation, inner, provider);
        }

        static SdkResult<TOut> SafeMap<TTask, TOut>(TTask task, Func<TTask, SdkResult<TOut>> map, string operation, string? provider)
            where TTask : Task
        {
            try
            {
                return map(task);
            }
            catch (Exception e)
            {
                return SdkError.FromException(operation, e, provider);
            }
        }
    }

    public static class SdkTasks
    {
        /// <summary>Chỉ hủy việc chờ của caller; task dùng chung vẫn tiếp tục chạy.</summary>
        public static async Task<SdkResult<T>> WaitAsync<T>(Task<SdkResult<T>> task, string operation, CancellationToken ct)
        {
            if (task.IsCompleted || !ct.CanBeCanceled) return await task;
            if (ct.IsCancellationRequested) return SdkError.Cancelled(operation);

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                var winner = await Task.WhenAny(task, cancelled.Task);
                if (winner != task) return SdkError.Cancelled(operation);
            }
            return await task;
        }

        public static async Task<SdkResult> WaitAsync(Task<SdkResult> task, string operation, CancellationToken ct)
        {
            if (task.IsCompleted || !ct.CanBeCanceled) return await task;
            if (ct.IsCancellationRequested) return SdkError.Cancelled(operation);

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                var winner = await Task.WhenAny(task, cancelled.Task);
                if (winner != task) return SdkError.Cancelled(operation);
            }
            return await task;
        }

        /// <summary>Chờ task dùng chung tối đa `timeout`; hết giờ trả Timeout, task vẫn tiếp tục chạy.</summary>
        public static async Task<SdkResult> WaitAsync(Task<SdkResult> task, string operation, TimeSpan timeout,
            IScheduler scheduler, CancellationToken ct)
        {
            if (task.IsCompleted) return await task;
            if (ct.IsCancellationRequested) return SdkError.Cancelled(operation);

            var gate = new TaskCompletionSource<SdkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (scheduler.Schedule(timeout, () => gate.TrySetResult(SdkError.Timeout(operation))))
            using (ct.Register(() => gate.TrySetResult(SdkError.Cancelled(operation))))
            {
                var winner = await Task.WhenAny(task, gate.Task);
                return winner == task ? await task : await gate.Task;
            }
        }

        public static TimeSpan Remaining(DateTime deadlineUtc, IClock clock)
        {
            var remaining = deadlineUtc - clock.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
