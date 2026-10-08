#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Notifications;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// Thông báo local (Android/iOS): hàm tĩnh, Id là chuỗi tùy ý của game.
    /// <code>
    /// NovaNotifications.Schedule("energy_full", "Energy is full!", "Come back and play", TimeSpan.FromHours(4));
    /// NovaNotifications.ScheduleDaily("daily_reward", "Daily reward", "Your gift is waiting", hour: 19, minute: 0);
    /// NovaNotifications.Cancel("energy_full");
    /// NovaNotifications.Opened += n => { if (n.Id == "daily_reward") OpenDailyReward(); };
    /// </code>
    /// Nhắc người chơi quay lại (khai báo trong Notification Config) được SDK tự lên lịch. Cùng Id = thay thông báo cũ.
    /// Gọi trên main thread; không throw; gọi trước khi SDK sẵn sàng thì lệnh được giữ lại và chạy sau.
    /// </summary>
    public static class NovaNotifications
    {
        static readonly List<Action<INotificationService>> Pending = new List<Action<INotificationService>>();

        static INotificationService? s_service;
        static ISdkLogger? s_log;
        static IDisposable? s_openedSubscription;
        static Action<NotificationOpened>? s_opened;
        static NotificationOpened? s_unhandledOpened;

        /// <summary>
        /// Người chơi mở game bằng thông báo. Thông báo mở game lúc khởi động được giữ lại và gửi cho handler đầu tiên
        /// đăng ký, nên đăng ký ở scene nào cũng nhận được.
        /// </summary>
        public static event Action<NotificationOpened>? Opened
        {
            add
            {
                s_opened += value;
                var unhandled = s_unhandledOpened;
                if (unhandled is null || value is null) return;
                s_unhandledOpened = null;
                Invoke(value, unhandled, "Opened");
            }
            remove => s_opened -= value;
        }

        /// <summary>Gắn service. <see cref="NovaSdk.InitializeAsync"/> tự gọi sau khi service khởi tạo.</summary>
        public static void Bind(INotificationService service, ISdkLogger log)
        {
            s_openedSubscription?.Dispose();
            s_service = service ?? throw new ArgumentNullException(nameof(service));
            s_log = log ?? throw new ArgumentNullException(nameof(log));
            s_openedSubscription = service.Opened.Subscribe(RaiseOpened);
            if (service.LastOpened != null) RaiseOpened(service.LastOpened);

            var pending = Pending.ToArray();
            Pending.Clear();
            foreach (var action in pending) s_log.TryRun("Pending notification call", () => action(service));
        }

        /// <summary>Gỡ service đã <see cref="Bind"/> (chỉ khi đúng service đó).</summary>
        public static void Unbind(INotificationService service)
        {
            if (!ReferenceEquals(s_service, service)) return;
            s_openedSubscription?.Dispose();
            s_openedSubscription = null;
            s_service = null;
        }

        /// <summary>Quyền hiện tại. NotSupported trong Editor/Standalone hoặc khi chưa bật Notification Config.</summary>
        public static NotificationPermission Permission => s_service?.Permission.Value ?? NotificationPermission.NotSupported;

        /// <summary>true khi người chơi cho phép thông báo.</summary>
        public static bool IsAllowed => Permission == NotificationPermission.Granted;

        /// <summary>Thông báo đã mở game trong phiên này (null nếu không có).</summary>
        public static NotificationOpened? LastOpened => s_service?.LastOpened;

        /// <summary>Id các thông báo đang chờ do SDK lên lịch (gồm cả nhắc chơi "nova_reminder_*").</summary>
        public static IReadOnlyCollection<string> ScheduledIds => s_service?.ScheduledIds ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        /// <summary>
        /// Hiện popup xin quyền thông báo (Android 13+, iOS). Đã từ chối thì popup không hiện lại: dùng <see cref="OpenSettings"/>.
        /// <paramref name="onDone"/>: true nếu được phép.
        /// </summary>
        public static void RequestPermission(Action<bool>? onDone = null) => _ = RequestPermissionWithCallbackAsync(onDone);

        /// <summary>Mở cài đặt thông báo của app (khi người chơi đã từ chối quyền).</summary>
        public static void OpenSettings() => Run("OpenSettings", service => service.OpenSettings());

        /// <summary>Thông báo sau <paramref name="delay"/> kể từ bây giờ. <paramref name="data"/> được trả lại trong <see cref="Opened"/>.</summary>
        public static void Schedule(string id, string title, string body, TimeSpan delay, string? data = null)
        {
            var fireTimeUtc = DateTime.UtcNow + delay;
            Run("Schedule", service => Report(service.Schedule(id, title, body, fireTimeUtc, NotificationRepeat.None, data)));
        }

        /// <summary>Thông báo vào một thời điểm (giờ máy người chơi).</summary>
        public static void ScheduleAt(string id, string title, string body, DateTime localTime, string? data = null)
        {
            var fireTimeUtc = localTime.Kind == DateTimeKind.Utc ? localTime : DateTime.SpecifyKind(localTime, DateTimeKind.Local).ToUniversalTime();
            Run("ScheduleAt", service => Report(service.Schedule(id, title, body, fireTimeUtc, NotificationRepeat.None, data)));
        }

        /// <summary>Thông báo mỗi ngày lúc <paramref name="hour"/>:<paramref name="minute"/> (giờ máy người chơi).</summary>
        public static void ScheduleDaily(string id, string title, string body, int hour, int minute = 0, string? data = null)
        {
            var today = DateTime.Now.Date.AddHours(Mathf.Clamp(hour, 0, 23)).AddMinutes(Mathf.Clamp(minute, 0, 59));
            var fireTimeUtc = DateTime.SpecifyKind(today, DateTimeKind.Local).ToUniversalTime();
            Run("ScheduleDaily", service => Report(service.Schedule(id, title, body, fireTimeUtc, NotificationRepeat.Daily, data)));
        }

        public static void Cancel(string id) => Run("Cancel", service => service.Cancel(id));

        /// <summary>Hủy mọi thông báo đang chờ (nhắc chơi được SDK lên lịch lại khi game vào nền).</summary>
        public static void CancelAll() => Run("CancelAll", service => service.CancelAll());

        static async Task RequestPermissionWithCallbackAsync(Action<bool>? onDone)
        {
            var service = s_service;
            bool allowed = false;
            if (service != null)
            {
                try
                {
                    allowed = await service.RequestPermissionAsync(CancellationToken.None) == NotificationPermission.Granted;
                }
                catch (Exception e)
                {
                    s_log?.Error("RequestPermission threw", e);
                }
            }
            if (onDone != null) Invoke(onDone, allowed, "onDone");
        }

        static void Run(string what, Action<INotificationService> action)
        {
            var service = s_service;
            if (service is null)
            {
                Pending.Add(action);
                return;
            }
            s_log.TryRun(what, () => action(service));
        }

        static void Report(SdkResult result)
        {
            if (!result.IsSuccess) s_log?.Warning("Notification not scheduled: " + result.Error);
        }

        static void RaiseOpened(NotificationOpened opened)
        {
            var handlers = s_opened;
            if (handlers is null)
            {
                s_unhandledOpened = opened;
                return;
            }
            foreach (Action<NotificationOpened> handler in handlers.GetInvocationList()) Invoke(handler, opened, "Opened");
        }

        static void Invoke<T>(Action<T> callback, T value, string name)
        {
            try
            {
                callback(value);
            }
            catch (Exception e)
            {
                if (s_log != null) s_log.Error("Game callback " + name + " threw", e);
                else Debug.LogException(e);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_openedSubscription?.Dispose();
            s_openedSubscription = null;
            s_service = null;
            s_log = null;
            s_opened = null;
            s_unhandledOpened = null;
            Pending.Clear();
        }
    }
}
