#nullable enable
using System;
using NovaGames.Mobile.Connectivity;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class OfflineDetectorTests
    {
        static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static OfflineDetector Create() => new OfflineDetector(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.5));

        [Test]
        public void ShortDrop_DoesNotReportOffline()
        {
            var detector = Create();

            Assert.IsFalse(detector.Update(false, T0));
            Assert.IsFalse(detector.Update(false, T0.AddSeconds(1.5)));
            Assert.IsFalse(detector.Update(true, T0.AddSeconds(1.8)), "network came back before the delay");
            Assert.IsFalse(detector.Update(false, T0.AddSeconds(3)), "the delay restarts");
            Assert.IsFalse(detector.Update(false, T0.AddSeconds(4.9)));
        }

        [Test]
        public void ContinuousDrop_ReportsOfflineThenOnlineAfterTheirDelays()
        {
            var detector = Create();

            detector.Update(false, T0);
            Assert.IsTrue(detector.Update(false, T0.AddSeconds(2)));
            Assert.IsTrue(detector.Update(true, T0.AddSeconds(3)), "online delay not reached yet");
            Assert.IsFalse(detector.Update(true, T0.AddSeconds(3.5)));
        }

        [Test]
        public void ForceUpdate_SkipsTheDelay()
        {
            var detector = Create();
            detector.Update(false, T0);
            detector.Update(false, T0.AddSeconds(5));
            Assert.IsTrue(detector.IsOffline);

            detector.ForceUpdate(true);

            Assert.IsFalse(detector.IsOffline);
            Assert.IsFalse(detector.Update(true, T0.AddSeconds(5.1)));
        }

        [Test]
        public void ZeroDelays_FollowTheNetworkImmediately()
        {
            var detector = new OfflineDetector(TimeSpan.Zero, TimeSpan.Zero);

            Assert.IsTrue(detector.Update(false, T0));
            Assert.IsFalse(detector.Update(true, T0));
        }
    }
}
