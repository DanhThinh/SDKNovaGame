#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// API tracking cho game: gửi event, user property, purchase tới mọi tracking sink đang bật
    /// (Firebase Analytics luôn nhận; Adjust chỉ nhận event có token trong Adjust Config).
    /// <para>
    /// Gọi lúc nào cũng được, kể cả trước <see cref="NovaSdk.InitializeAsync"/>: event được giữ lại (tối đa
    /// <see cref="MaxPendingPerSink"/> mỗi sink) và gửi khi sink sẵn sàng. Không throw; tên/param sai luật Firebase
    /// bị sink bỏ qua kèm log cảnh báo.
    /// </para>
    /// </summary>
    public static class NovaAnalytics
    {
        public const int MaxPendingPerSink = 100;

        static TrackingRouter s_router = new TrackingRouter();

        internal static TrackingRouter Router => s_router;

        /// <summary>Gửi event không có param.</summary>
        public static void LogEvent(string name) => LogEvent(new TrackingEvent(name));

        /// <summary>
        /// Gửi event kèm param: <c>NovaAnalytics.LogEvent("level_complete", ("level", 3), ("time", 42.5f), ("mode", "hard"))</c>.
        /// Giá trị hỗ trợ: string, bool, số nguyên, số thực, enum (gửi tên); kiểu khác gửi bằng ToString().
        /// </summary>
        public static void LogEvent(string name, params (string Name, object? Value)[] parameters)
        {
            if (parameters is null || parameters.Length == 0)
            {
                LogEvent(name);
                return;
            }
            // Param không tên bị bỏ (không throw vào game).
            var converted = new List<TrackingParam>(parameters.Length);
            foreach (var (paramName, value) in parameters)
            {
                if (string.IsNullOrEmpty(paramName)) continue;
                converted.Add(ToParam(paramName, value));
            }
            LogEvent(new TrackingEvent(name, converted.ToArray()));
        }

        /// <summary>Gửi event đã dựng sẵn (param typed, không boxing).</summary>
        public static void LogEvent(TrackingEvent trackingEvent)
        {
            if (trackingEvent is null || string.IsNullOrEmpty(trackingEvent.Name)) return;
            s_router.Send(SinkCapabilities.Events, sink => sink.Send(trackingEvent));
        }

        /// <summary>User property (vd. "player_segment"). null = xóa. Firebase giới hạn tên 24 ký tự, giá trị 36 ký tự.</summary>
        public static void SetUserProperty(string name, string? value)
        {
            if (string.IsNullOrEmpty(name)) return;
            s_router.Send(SinkCapabilities.UserProperties, sink => (sink as IUserPropertySink)?.SetUserProperty(name, value));
        }

        /// <summary>User id của game (không dùng email/số điện thoại hay dữ liệu cá nhân). null = xóa. Crashlytics cũng nhận id này.</summary>
        public static void SetUserId(string? id)
        {
            s_router.Send(SinkCapabilities.UserProperties, sink => (sink as IUserPropertySink)?.SetUserId(id));
            NovaCrash.SetUserId(id);
        }

        /// <summary>
        /// Doanh thu in-app purchase đã xác nhận. Adjust nhận nếu Adjust Config có token cho event purchase;
        /// Firebase tự ghi purchase trên Android, iOS dùng transaction id.
        /// </summary>
        public static void LogPurchase(string transactionId, string productId, double value, string currency = "USD", int quantity = 1)
        {
            if (string.IsNullOrEmpty(transactionId)) return;
            var purchase = new PurchaseRevenueEvent(Guid.NewGuid(), transactionId, productId ?? string.Empty, value, currency, quantity);
            s_router.ReportPurchase(purchase);
        }

        internal static TrackingParam ToParam(string name, object? value) => value switch
        {
            null => TrackingParam.Of(name, string.Empty),
            string s => TrackingParam.Of(name, s),
            bool b => TrackingParam.Of(name, b),
            int i => TrackingParam.Of(name, i),
            long l => TrackingParam.Of(name, l),
            short s16 => TrackingParam.Of(name, s16),
            byte u8 => TrackingParam.Of(name, u8),
            uint u32 => TrackingParam.Of(name, (long)u32),
            float f => TrackingParam.Of(name, (double)f),
            double d => TrackingParam.Of(name, d),
            decimal m => TrackingParam.Of(name, (double)m),
            Enum e => TrackingParam.Of(name, e.ToString()),
            _ => TrackingParam.Of(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset() => s_router = new TrackingRouter();
    }

    // Chuyển event/revenue tới các tracking sink. Sink chưa sẵn sàng (đang init, chờ consent) thì giữ lại tối đa
    // MaxPendingPerSink lệnh và gửi theo thứ tự khi sink sẵn sàng. Trước khi NovaSdk gắn sink, lệnh được giữ chung rồi
    // phát lại cho từng sink lúc gắn. Chỉ gọi trên main thread.
    internal sealed class TrackingRouter : IRevenuePipeline
    {
        sealed class Route
        {
            public Route(ITrackingSink sink) { Sink = sink; }
            public ITrackingSink Sink { get; }
            public readonly Queue<Action<ITrackingSink>> Pending = new Queue<Action<ITrackingSink>>();
            public bool WarnedOverflow;
        }

        readonly List<Route> _routes = new List<Route>();
        readonly List<(SinkCapabilities Required, Action<ITrackingSink> Send)> _beforeAttach =
            new List<(SinkCapabilities, Action<ITrackingSink>)>();
        ISdkLogger? _log;
        bool _attached;

        public IReadOnlyList<ITrackingSink> Sinks
        {
            get
            {
                var sinks = new List<ITrackingSink>(_routes.Count);
                foreach (var route in _routes) sinks.Add(route.Sink);
                return sinks;
            }
        }

        // NovaSdk gắn các sink đã tạo; lệnh gửi trước đó được phát lại cho từng sink.
        public void Attach(IReadOnlyList<ITrackingSink> sinks, ISdkLogger log)
        {
            _log = log;
            foreach (var sink in sinks) _routes.Add(new Route(sink));
            _attached = true;
            foreach (var (required, send) in _beforeAttach) Send(required, send);
            _beforeAttach.Clear();
        }

        // Theo dõi lượt init của sink: xong thì gửi phần đang giữ. Init lỗi thì vẫn giữ (sink có thể sẵn sàng sau, vd.
        // Firebase xong check dependency muộn, Adjust chờ consent); hàng đợi có trần nên không tốn bộ nhớ vô hạn.
        public async Task WatchInitialization(ITrackingSink sink, Task<SdkResult> initialization)
        {
            SdkResult result;
            try
            {
                result = await initialization;
            }
            catch (Exception e)
            {
                _log?.Error(sink.Id + " initialization threw", e);
                return;
            }
            if (!result.IsSuccess) _log?.Warning(sink.Id + " not ready: " + result.Error);
            var route = _routes.Find(r => r.Sink == sink);
            if (route != null) Drain(route);
        }

        public void Send(SinkCapabilities required, Action<ITrackingSink> send)
        {
            if (!_attached)
            {
                if (_beforeAttach.Count >= NovaAnalytics.MaxPendingPerSink) _beforeAttach.RemoveAt(0);
                _beforeAttach.Add((required, send));
                return;
            }

            foreach (var route in _routes)
            {
                if ((route.Sink.Capabilities & required) != required) continue;
                if (route.Sink.IsReady)
                {
                    Drain(route);
                    Deliver(route.Sink, send);
                    continue;
                }
                if (route.Pending.Count >= NovaAnalytics.MaxPendingPerSink)
                {
                    route.Pending.Dequeue();
                    if (!route.WarnedOverflow)
                    {
                        route.WarnedOverflow = true;
                        _log?.Warning(route.Sink.Id + " is not ready; dropping oldest pending tracking calls");
                    }
                }
                route.Pending.Enqueue(send);
            }
        }

        public void ReportAdRevenue(AdRevenueEvent e) => Send(SinkCapabilities.AdRevenue, sink => sink.SendAdRevenue(e));

        public void ReportPurchase(PurchaseRevenueEvent e) => Send(SinkCapabilities.Purchase, sink => sink.SendPurchase(e));

        void Drain(Route route)
        {
            if (!route.Sink.IsReady) return;
            while (route.Pending.Count > 0) Deliver(route.Sink, route.Pending.Dequeue());
        }

        void Deliver(ITrackingSink sink, Action<ITrackingSink> send)
        {
            try
            {
                send(sink);
            }
            catch (Exception e)
            {
                _log?.Error(sink.Id + " threw while sending", e);
            }
        }
    }
}
