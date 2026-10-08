#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.Notifications
{
    public enum NotificationPermission : byte
    {
        /// <summary>Chưa hỏi người chơi (Android 13+ / iOS).</summary>
        NotDetermined,
        Granted,
        /// <summary>Người chơi từ chối hoặc tắt trong Settings: chỉ bật lại được qua <c>NovaNotifications.OpenSettings</c>.</summary>
        Denied,
        /// <summary>Platform không hỗ trợ (Editor, Standalone) hoặc chưa cài adapter.</summary>
        NotSupported,
    }

    public enum NotificationRepeat : byte
    {
        None,
        /// <summary>Lặp mỗi ngày cùng giờ.</summary>
        Daily,
    }

    /// <summary>Người chơi mở game bằng cách bấm vào thông báo.</summary>
    /// <param name="Id">Id lúc gọi Schedule (vd. "daily_reward", reminder: "nova_reminder_3").</param>
    /// <param name="Data">Dữ liệu tùy ý truyền lúc Schedule (vd. tên màn hình cần mở), null nếu không có.</param>
    /// <param name="IsColdStart">true = game được khởi động từ thông báo; false = game đang chạy nền và được đưa lên.</param>
    public sealed record NotificationOpened(string Id, string? Data, bool IsColdStart);

    /// <summary>Thông báo đã resolve, gửi xuống platform adapter.</summary>
    /// <param name="PlatformId">Id số ổn định suy ra từ <paramref name="Id"/> (Android cần id kiểu int).</param>
    /// <param name="Payload">Chuỗi gắn vào thông báo, trả lại khi người chơi bấm (chứa Id + Data).</param>
    public sealed record ScheduledNotification(
        string Id, int PlatformId, string Title, string Body, DateTime FireTimeUtc, NotificationRepeat Repeat, string Payload)
    {
        /// <summary>Tên icon nhỏ Android khai báo trong Project Settings > Mobile Notifications. Rỗng = icon app.</summary>
        public string SmallIcon { get; init; } = string.Empty;

        /// <summary>Tên icon lớn Android. Rỗng = không có.</summary>
        public string LargeIcon { get; init; } = string.Empty;
    }

    /// <summary>Kênh thông báo Android (người chơi thấy tên kênh trong Settings của app).</summary>
    public sealed record NotificationChannelSettings(string Id, string Name, string Description);

    public static class NotificationPlatformIds
    {
        public const string Unity = "unity_notifications";
    }

    /// <summary>
    /// SPI cho platform thông báo (Unity Mobile Notifications). Adapter chỉ gọi API vendor; id, lịch nhắc, giới hạn số
    /// lượng và phát hiện mở game do NotificationService xử lý. Mọi lời gọi trên main thread.
    /// </summary>
    public interface INotificationPlatform : IDisposable
    {
        string Id { get; }

        /// <summary>Tạo kênh Android, chuẩn bị platform. false = platform không hỗ trợ.</summary>
        bool Initialize(NotificationChannelSettings channel);

        /// <summary>Quyền hiện tại, không hiện popup.</summary>
        NotificationPermission ReadPermission();

        /// <summary>Hiện popup xin quyền (nếu chưa hỏi). Kết quả = quyền sau khi người chơi trả lời.</summary>
        Task<NotificationPermission> RequestPermissionAsync(CancellationToken ct);

        /// <summary>Lên lịch; cùng PlatformId thì thay thông báo cũ.</summary>
        void Schedule(ScheduledNotification notification);

        void Cancel(string id, int platformId);

        /// <summary>Hủy mọi thông báo đã lên lịch của app.</summary>
        void CancelAll();

        /// <summary>Xóa thông báo đang hiện trên thanh thông báo và badge iOS.</summary>
        void ClearDelivered();

        /// <summary>Payload của thông báo người chơi bấm gần nhất để mở app, null nếu không có (iOS trả lời bất đồng bộ).</summary>
        Task<string?> ReadLastOpenedPayloadAsync(CancellationToken ct);

        /// <summary>Mở màn hình cài đặt thông báo của app (khi người chơi đã từ chối quyền).</summary>
        void OpenSettings();
    }

    /// <summary>Service thông báo local. Gọi trên main thread; không throw.</summary>
    public interface INotificationService
    {
        ISdkProperty<NotificationPermission> Permission { get; }
        Task<NotificationPermission> RequestPermissionAsync(CancellationToken ct);

        /// <summary>Lên lịch (thay thông báo cùng Id nếu đã có).</summary>
        SdkResult Schedule(string id, string title, string body, DateTime fireTimeUtc,
                           NotificationRepeat repeat = NotificationRepeat.None, string? data = null);

        void Cancel(string id);
        void CancelAll();
        void OpenSettings();

        /// <summary>Id các thông báo đang chờ do SDK lên lịch.</summary>
        IReadOnlyCollection<string> ScheduledIds { get; }

        ISdkEvent<NotificationOpened> Opened { get; }

        /// <summary>Thông báo mở game gần nhất trong phiên này (null nếu không có).</summary>
        NotificationOpened? LastOpened { get; }
    }
}
