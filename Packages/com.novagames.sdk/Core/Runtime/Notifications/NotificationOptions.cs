#nullable enable
using System;
using System.Collections.Generic;

namespace NovaGames.Mobile.Notifications
{
    /// <summary>Một nội dung nhắc chơi (title + body).</summary>
    public sealed record ReminderMessage(string Title, string Body);

    /// <summary>
    /// Nhắc người chơi quay lại: mỗi lần mở game và mỗi lần game vào nền, SDK hủy lịch nhắc cũ và lên lịch lại tính từ
    /// lúc này (người chơi vừa chơi thì không cần nhắc sớm).
    /// </summary>
    public sealed record ReminderOptions(IReadOnlyList<ReminderMessage> Messages, IReadOnlyList<int> Days)
    {
        public static ReminderOptions Disabled { get; } = new ReminderOptions(Array.Empty<ReminderMessage>(), Array.Empty<int>());

        public bool IsEnabled => Messages.Count > 0 && Days.Count > 0;

        /// <summary>Giờ nhắc (0-23, giờ máy). -1 = cùng giờ người chơi vừa chơi.</summary>
        public int Hour { get; init; } = -1;
        public int Minute { get; init; }

        /// <summary>Khi nhắc theo giờ vừa chơi: kẹp vào khoảng này để không nhắc lúc đêm khuya.</summary>
        public int EarliestHour { get; init; } = 9;
        public int LatestHour { get; init; } = 21;
    }

    /// <summary>Cấu hình thông báo đã resolve theo platform (từ <see cref="NotificationConfig"/>).</summary>
    public sealed record NotificationOptions(NotificationChannelSettings Channel)
    {
        public static NotificationOptions Default { get; } =
            new NotificationOptions(new NotificationChannelSettings("nova_default", "Notifications", "Reminders and game events"));

        public ReminderOptions Reminders { get; init; } = ReminderOptions.Disabled;

        /// <summary>Hỏi quyền ngay khi SDK khởi động (nếu chưa hỏi). Tắt để game tự hỏi đúng lúc qua RequestPermission.</summary>
        public bool AskPermissionOnStartup { get; init; } = true;

        public string SmallIcon { get; init; } = string.Empty;
        public string LargeIcon { get; init; } = string.Empty;

        /// <summary>Số thông báo chờ tối đa. iOS giữ tối đa 64 nên mặc định 60; vượt thì bỏ thông báo xa nhất.</summary>
        public int MaxPending { get; init; } = 60;

        /// <summary>Event analytics khi người chơi mở game bằng thông báo (param notification_id). Rỗng = không log.</summary>
        public string OpenedEventName { get; init; } = "notification_open";
    }

    /// <summary>Phụ thuộc ngoài module thông báo; để trống thì tính năng tương ứng tắt.</summary>
    public sealed class NotificationDependencies
    {
        /// <summary>Raise vào nền/quay lại để lên lịch nhắc và phát hiện mở game từ thông báo khi đang chạy nền.</summary>
        public NovaGames.Mobile.Infrastructure.IApplicationLifecycle? Lifecycle { get; set; }

        /// <summary>Log analytics khi mở game bằng thông báo (vd. NovaAnalytics.LogEvent).</summary>
        public Action<string, NotificationOpened>? LogOpened { get; set; }

        /// <summary>Múi giờ dùng để tính giờ nhắc. null = giờ của máy.</summary>
        public TimeZoneInfo? TimeZone { get; set; }

        /// <summary>Chặn app open ad trong lúc popup xin quyền của hệ thống đang mở (vd. NovaAds.SuppressAppOpen).</summary>
        public Func<string, IDisposable>? SuppressAppOpen { get; set; }
    }
}
