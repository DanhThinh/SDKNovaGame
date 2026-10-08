#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// Attribution và deep link từ Adjust (cần Adjust Config trong <see cref="NovaSdkSettings"/>).
    /// Event chạy trên main thread. Deep link tới trước khi game đăng ký <see cref="DeepLinkReceived"/> (vd. mở app từ
    /// link lúc cold start) được giữ lại và phát cho handler đầu tiên.
    /// </summary>
    public static class NovaAttribution
    {
        const int MaxPendingDeepLinks = 8;

        static readonly Queue<DeepLink> PendingDeepLinks = new Queue<DeepLink>();
        static Action<DeepLink>? s_deepLinkHandlers;
        static ISdkLogger? s_log;

        /// <summary>Attribution mới nhất (network, campaign, ...). null khi Adjust chưa trả về.</summary>
        public static AttributionData? Current { get; private set; }

        /// <summary>Gọi mỗi khi attribution thay đổi.</summary>
        public static event Action<AttributionData>? Changed;

        /// <summary>Deep link mở app (IsDeferred = false) hoặc deferred deep link sau khi cài (IsDeferred = true).</summary>
        public static event Action<DeepLink>? DeepLinkReceived
        {
            add
            {
                s_deepLinkHandlers += value;
                while (PendingDeepLinks.Count > 0 && s_deepLinkHandlers != null) Raise(PendingDeepLinks.Dequeue());
            }
            remove => s_deepLinkHandlers -= value;
        }

        internal static IAttributionListener Listener { get; } = new ListenerBridge();

        internal static void SetLogger(ISdkLogger log) => s_log = log;

        static void Raise(DeepLink link)
        {
            try
            {
                s_deepLinkHandlers?.Invoke(link);
            }
            catch (Exception e)
            {
                Report("DeepLinkReceived handler threw", e);
            }
        }

        static void Report(string message, Exception e)
        {
            if (s_log != null) s_log.Error(message, e);
            else Debug.LogException(e);
        }

        sealed class ListenerBridge : IAttributionListener
        {
            public void OnAttribution(AttributionData data)
            {
                Current = data;
                try
                {
                    Changed?.Invoke(data);
                }
                catch (Exception e)
                {
                    Report("Attribution Changed handler threw", e);
                }
            }

            public void OnDeepLink(DeepLink link)
            {
                if (s_deepLinkHandlers != null)
                {
                    Raise(link);
                    return;
                }
                if (PendingDeepLinks.Count == MaxPendingDeepLinks) PendingDeepLinks.Dequeue();
                PendingDeepLinks.Enqueue(link);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            Current = null;
            Changed = null;
            s_deepLinkHandlers = null;
            s_log = null;
            PendingDeepLinks.Clear();
        }
    }
}
