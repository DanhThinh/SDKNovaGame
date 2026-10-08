#nullable enable
using System;
using System.Collections.Generic;
using AdjustSdk;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Attribution.Tests
{
    sealed class FakeAdjustApi : IAdjustApi
    {
        public readonly List<string> Calls = new List<string>();
        public readonly List<AdjustEvent> Events = new List<AdjustEvent>();
        public readonly List<AdjustAdRevenue> AdRevenues = new List<AdjustAdRevenue>();
        public readonly List<AdjustThirdPartySharing> Sharing = new List<AdjustThirdPartySharing>();
        public readonly List<bool> MeasurementConsents = new List<bool>();
        public readonly List<string> ProcessedDeepLinks = new List<string>();
        public readonly List<Action<AdjustAttribution?>> AttributionRequests = new List<Action<AdjustAttribution?>>();

        public AdjustConfig? Config;
        public bool Throw;
        public bool ForwardsDeepLinks { get; set; }
        public string? LaunchUrl { get; set; }
        public Action<string>? DeepLinkHandler;

        public void InitSdk(AdjustConfig config) { Record("InitSdk"); Config = config; }
        public void TrackEvent(AdjustEvent adjustEvent) { Record("TrackEvent"); Events.Add(adjustEvent); }
        public void TrackAdRevenue(AdjustAdRevenue adRevenue) { Record("TrackAdRevenue"); AdRevenues.Add(adRevenue); }
        public void TrackThirdPartySharing(AdjustThirdPartySharing sharing) { Record("TrackThirdPartySharing"); Sharing.Add(sharing); }
        public void TrackMeasurementConsent(bool consent) { Record("TrackMeasurementConsent"); MeasurementConsents.Add(consent); }
        public void ProcessDeeplink(string url) { Record("ProcessDeeplink"); ProcessedDeepLinks.Add(url); }
        public void GetAttribution(Action<AdjustAttribution?> callback) { Record("GetAttribution"); AttributionRequests.Add(callback); }

        public IDisposable SubscribeDeepLinks(Action<string> handler)
        {
            DeepLinkHandler = handler;
            return new Unsubscribe(() => DeepLinkHandler = null);
        }

        void Record(string call)
        {
            if (Throw) throw new InvalidOperationException("vendor failure");
            Calls.Add(call);
        }

        sealed class Unsubscribe : IDisposable
        {
            readonly Action _action;
            public Unsubscribe(Action action) { _action = action; }
            public void Dispose() => _action();
        }
    }

    sealed class RecordingListener : IAttributionListener
    {
        public readonly List<AttributionData> Attributions = new List<AttributionData>();
        public readonly List<DeepLink> DeepLinks = new List<DeepLink>();

        public void OnAttribution(AttributionData data) => Attributions.Add(data);
        public void OnDeepLink(DeepLink link) => DeepLinks.Add(link);
    }
}
