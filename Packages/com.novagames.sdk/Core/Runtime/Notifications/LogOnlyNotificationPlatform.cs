#nullable enable
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Notifications
{
    /// <summary>
    /// Platform giả lập cho Editor: quyền luôn Granted, thông báo chỉ được ghi log (không hiện). Giúp test luồng
    /// Schedule/Cancel/nhắc chơi trong Editor; thông báo thật chỉ hiện trên máy Android/iOS.
    /// </summary>
    public sealed class LogOnlyNotificationPlatform : INotificationPlatform
    {
        readonly ISdkLogger _log;

        public LogOnlyNotificationPlatform(ISdkLogger log)
        {
            _log = log;
        }

        public string Id => "log_only";
        public bool Initialize(NotificationChannelSettings channel) => true;
        public NotificationPermission ReadPermission() => NotificationPermission.Granted;
        public Task<NotificationPermission> RequestPermissionAsync(CancellationToken ct) => Task.FromResult(NotificationPermission.Granted);

        public void Schedule(ScheduledNotification notification) =>
            _log.Info("[Editor] Would show '" + notification.Title + "' at " +
                      notification.FireTimeUtc.ToLocalTime().ToString("g", CultureInfo.InvariantCulture) +
                      (notification.Repeat == NotificationRepeat.Daily ? " (daily)" : string.Empty) + " [" + notification.Id + "]");

        public void Cancel(string id, int platformId) { }
        public void CancelAll() { }
        public void ClearDelivered() { }
        public Task<string?> ReadLastOpenedPayloadAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public void OpenSettings() => _log.Info("[Editor] OpenSettings: only available on device");
        public void Dispose() { }
    }
}
