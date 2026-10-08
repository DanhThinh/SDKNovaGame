#nullable enable
using System;
using AdjustSdk;
using UnityEngine;

namespace NovaGames.Mobile.Attribution
{
    // Bọc static AdjustSdk.Adjust + hook deep link của Unity để test bằng fake. Gọi trên main thread.
    internal interface IAdjustApi
    {
        // Android: Unity bridge không tự đưa deep link mở app vào Adjust (prefab Adjust mới làm, SDK không dùng prefab).
        // iOS: AdjustUnityAppDelegate đã swizzle openURL/continueUserActivity nên không gọi lại.
        bool ForwardsDeepLinks { get; }

        void InitSdk(AdjustConfig config);
        void TrackEvent(AdjustEvent adjustEvent);
        void TrackAdRevenue(AdjustAdRevenue adRevenue);
        void TrackThirdPartySharing(AdjustThirdPartySharing thirdPartySharing);
        void TrackMeasurementConsent(bool consent);
        void ProcessDeeplink(string url);
        void GetAttribution(Action<AdjustAttribution?> callback);

        // URL đã mở app (cold start); null nếu không có.
        string? LaunchUrl { get; }
        IDisposable SubscribeDeepLinks(Action<string> handler);
    }

    internal sealed class AdjustApi : IAdjustApi
    {
        public bool ForwardsDeepLinks => Application.platform == RuntimePlatform.Android;

        public void InitSdk(AdjustConfig config) => Adjust.InitSdk(config);
        public void TrackEvent(AdjustEvent adjustEvent) => Adjust.TrackEvent(adjustEvent);
        public void TrackAdRevenue(AdjustAdRevenue adRevenue) => Adjust.TrackAdRevenue(adRevenue);
        public void TrackThirdPartySharing(AdjustThirdPartySharing thirdPartySharing) => Adjust.TrackThirdPartySharing(thirdPartySharing);
        public void TrackMeasurementConsent(bool consent) => Adjust.TrackMeasurementConsent(consent);
        public void ProcessDeeplink(string url) => Adjust.ProcessDeeplink(new AdjustDeeplink(url));
        public void GetAttribution(Action<AdjustAttribution?> callback) => Adjust.GetAttribution(a => callback(a));

        public string? LaunchUrl => string.IsNullOrEmpty(Application.absoluteURL) ? null : Application.absoluteURL;

        public IDisposable SubscribeDeepLinks(Action<string> handler)
        {
            Application.deepLinkActivated += handler;
            return new Unsubscriber(() => Application.deepLinkActivated -= handler);
        }

        sealed class Unsubscriber : IDisposable
        {
            Action? _dispose;
            public Unsubscriber(Action dispose) { _dispose = dispose; }
            public void Dispose()
            {
                var dispose = _dispose;
                _dispose = null;
                dispose?.Invoke();
            }
        }
    }
}
