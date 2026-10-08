#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Notifications;

namespace NovaGames.Mobile.Testing
{
    // Platform giả: lưu thông báo đã lên lịch theo PlatformId như Android; test điều khiển quyền và payload mở app.
    public sealed class FakeNotificationPlatform : INotificationPlatform
    {
        public readonly Dictionary<string, ScheduledNotification> Scheduled = new Dictionary<string, ScheduledNotification>();
        public readonly List<string> Cancelled = new List<string>();
        public bool Supported = true;
        public NotificationPermission Current = NotificationPermission.NotDetermined;
        public TaskCompletionSource<NotificationPermission>? PermissionPrompt;
        public NotificationChannelSettings? Channel;
        public string? LastOpenedPayload;
        public int PermissionRequests;
        public int ClearDeliveredCalls;
        public int OpenSettingsCalls;
        public int CancelAllCalls;
        public bool Disposed;

        public string Id => "fake";

        public bool Initialize(NotificationChannelSettings channel)
        {
            Channel = channel;
            return Supported;
        }

        public NotificationPermission ReadPermission() => Current;

        public Task<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
            PermissionRequests++;
            PermissionPrompt = new TaskCompletionSource<NotificationPermission>();
            return PermissionPrompt.Task;
        }

        public void Schedule(ScheduledNotification notification) => Scheduled[notification.Id] = notification;

        public void Cancel(string id, int platformId)
        {
            Cancelled.Add(id);
            Scheduled.Remove(id);
        }

        public void CancelAll()
        {
            CancelAllCalls++;
            Scheduled.Clear();
        }

        public void ClearDelivered() => ClearDeliveredCalls++;
        public Task<string?> ReadLastOpenedPayloadAsync(CancellationToken ct) => Task.FromResult(LastOpenedPayload);
        public void OpenSettings() => OpenSettingsCalls++;
        public void Dispose() => Disposed = true;

        /// <summary>Giả lập người chơi bấm thông báo đã lên lịch.</summary>
        public void Tap(string id) => LastOpenedPayload = Scheduled[id].Payload;
    }
}
