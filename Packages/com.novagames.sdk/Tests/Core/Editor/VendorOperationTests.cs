#nullable enable
using System;
using System.Threading;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class VendorOperationTests
    {
        TestHarness _h = null!;

        [SetUp] public void SetUp() => _h = new TestHarness();
        [TearDown] public void TearDown() => _h.Dispose();

        [Test]
        public void Complete_FirstCallerWins_AndDeliversOnMainThreadOnly()
        {
            var op = new VendorOperation<int>("op", _h.Main, _h.Scheduler, TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.IsTrue(op.Complete(SdkResult<int>.Ok(1)));
            Assert.IsFalse(op.Complete(SdkResult<int>.Ok(2)));
            Assert.IsFalse(op.Task.IsCompleted, "result must be dispatched, not completed inline");

            var result = _h.Run(op.Task);
            Assert.IsTrue(result.TryGetValue(out var value));
            Assert.AreEqual(1, value);
            Assert.AreEqual(0, _h.Scheduler.PendingCount, "timeout must be disposed");
        }

        [Test]
        public void MissingCallback_TimesOut()
        {
            var op = new VendorOperation<int>("op", _h.Main, _h.Scheduler, TimeSpan.FromSeconds(5), CancellationToken.None);

            _h.Scheduler.Advance(TimeSpan.FromSeconds(5));
            var result = _h.Run(op.Task);

            Assert.AreEqual(SdkErrorCategory.Timeout, result.Error!.Category);
            Assert.IsFalse(op.Complete(SdkResult<int>.Ok(1)), "late callback must be ignored");
        }

        [Test]
        public void Cancellation_CompletesWithCancelled()
        {
            using var cts = new CancellationTokenSource();
            var op = new VendorOperation<int>("op", _h.Main, _h.Scheduler, TimeSpan.FromSeconds(5), cts.Token);

            cts.Cancel();
            var result = _h.Run(op.Task);

            Assert.AreEqual(SdkErrorCategory.Cancelled, result.Error!.Category);
        }

        [Test]
        public void ObserveAsync_FaultedVendorTask_MapsToProviderError()
        {
            var vendor = System.Threading.Tasks.Task.FromException<int>(new InvalidOperationException("boom"));

            var task = VendorTask.ObserveAsync(vendor,
                t => t.IsFaulted ? VendorTask.FaultToError(t, "op", "vendor") : SdkResult<int>.Ok(t.Result),
                "op", _h.Main, _h.Scheduler, TimeSpan.FromSeconds(5), CancellationToken.None);
            var result = _h.Run(task);

            Assert.AreEqual(SdkErrorCategory.Provider, result.Error!.Category);
            Assert.AreEqual("vendor", result.Error.Provider);
            Assert.IsInstanceOf<InvalidOperationException>(result.Error.Exception);
        }
    }
}
