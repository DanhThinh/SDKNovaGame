#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;
#if UNITY_ANDROID
using Unity.Notifications.Android;
#endif
#if UNITY_IOS
using Unity.Notifications.iOS;
#endif

namespace NovaGames.Mobile.Notifications
{
    /// <summary>
    /// Adapter cho Unity Mobile Notifications (Android + iOS). Chỉ chuyển SPI sang API vendor; lịch nhắc, giới hạn số
    /// lượng, phát hiện mở game do NotificationService xử lý. Trên Editor/Standalone: Initialize trả false.
    /// </summary>
    public sealed class UnityNotificationPlatform : INotificationPlatform
    {
        static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        readonly ModuleContext _ctx;
        readonly ISdkLogger _log;
#pragma warning disable CS0414 // chỉ dùng trên Android
        string _channelId = string.Empty;
#pragma warning restore CS0414

        public UnityNotificationPlatform(ModuleContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _log = ctx.Logs.Create("notifications.unity");
        }

        public string Id => NotificationPlatformIds.Unity;

        public bool Initialize(NotificationChannelSettings channel)
        {
            if (Application.isEditor) return false;
#if UNITY_ANDROID
            if (!AndroidNotificationCenter.Initialize()) return false;
            _channelId = channel.Id;
            AndroidNotificationCenter.RegisterNotificationChannel(new AndroidNotificationChannel
            {
                Id = channel.Id,
                Name = channel.Name,
                Description = channel.Description,
                Importance = Importance.Default,
                CanShowBadge = true,
            });
            return true;
#elif UNITY_IOS
            return true;
#else
            return false;
#endif
        }

        public NotificationPermission ReadPermission()
        {
#if UNITY_ANDROID
            return ToPermission(AndroidNotificationCenter.UserPermissionToPost);
#elif UNITY_IOS
            return ToPermission(iOSNotificationCenter.GetNotificationSettings().AuthorizationStatus);
#else
            return NotificationPermission.NotSupported;
#endif
        }

        public Task<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
#if UNITY_ANDROID
            var request = new PermissionRequest();
            return PollAsync(() => request.Status != PermissionStatus.RequestPending, () => ToPermission(request.Status), null, ct);
#elif UNITY_IOS
            var request = new AuthorizationRequest(AuthorizationOption.Alert | AuthorizationOption.Badge | AuthorizationOption.Sound, false);
            return PollAsync(() => request.IsFinished, ReadPermission, request.Dispose, ct);
#else
            return Task.FromResult(NotificationPermission.NotSupported);
#endif
        }

        public void Schedule(ScheduledNotification notification)
        {
#if UNITY_ANDROID
            var android = new AndroidNotification
            {
                Title = notification.Title,
                Text = notification.Body,
                FireTime = notification.FireTimeUtc.ToLocalTime(),
                IntentData = notification.Payload,
                ShowTimestamp = true,
            };
            if (notification.Repeat == NotificationRepeat.Daily) android.RepeatInterval = TimeSpan.FromDays(1);
            if (notification.SmallIcon.Length > 0) android.SmallIcon = notification.SmallIcon;
            if (notification.LargeIcon.Length > 0) android.LargeIcon = notification.LargeIcon;
            AndroidNotificationCenter.SendNotificationWithExplicitID(android, _channelId, notification.PlatformId);
#elif UNITY_IOS
            iOSNotificationTrigger trigger;
            if (notification.Repeat == NotificationRepeat.Daily)
            {
                var local = notification.FireTimeUtc.ToLocalTime();
                trigger = new iOSNotificationCalendarTrigger { Hour = local.Hour, Minute = local.Minute, Repeats = true };
            }
            else
            {
                var delay = notification.FireTimeUtc - _ctx.Clock.UtcNow;
                trigger = new iOSNotificationTimeIntervalTrigger
                {
                    TimeInterval = delay > TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1),
                    Repeats = false,
                };
            }
            iOSNotificationCenter.ScheduleNotification(new iOSNotification
            {
                Identifier = notification.Id,
                Title = notification.Title,
                Body = notification.Body,
                Data = notification.Payload,
                ShowInForeground = true,
                ForegroundPresentationOption = PresentationOption.Alert | PresentationOption.Sound,
                Badge = 1,
                Trigger = trigger,
            });
#endif
        }

        public void Cancel(string id, int platformId)
        {
#if UNITY_ANDROID
            AndroidNotificationCenter.CancelNotification(platformId);
#elif UNITY_IOS
            iOSNotificationCenter.RemoveScheduledNotification(id);
            iOSNotificationCenter.RemoveDeliveredNotification(id);
#endif
        }

        public void CancelAll()
        {
#if UNITY_ANDROID
            AndroidNotificationCenter.CancelAllScheduledNotifications();
#elif UNITY_IOS
            iOSNotificationCenter.RemoveAllScheduledNotifications();
#endif
        }

        public void ClearDelivered()
        {
#if UNITY_ANDROID
            AndroidNotificationCenter.CancelAllDisplayedNotifications();
#elif UNITY_IOS
            iOSNotificationCenter.RemoveAllDeliveredNotifications();
            iOSNotificationCenter.ApplicationBadge = 0;
#endif
        }

        public Task<string?> ReadLastOpenedPayloadAsync(CancellationToken ct)
        {
#if UNITY_ANDROID
            return Task.FromResult<string?>(AndroidNotificationCenter.GetLastNotificationIntent()?.Notification.IntentData);
#elif UNITY_IOS
            // Lúc khởi động lạnh iOS báo thông báo được bấm hơi trễ: chờ tới khi query có kết quả.
            var query = iOSNotificationCenter.QueryLastRespondedNotification();
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check()
            {
                try
                {
                    if (query.keepWaiting && !ct.IsCancellationRequested)
                    {
                        _ctx.Scheduler.Schedule(PollInterval, Check);
                        return;
                    }
                    tcs.TrySetResult(query.State == QueryLastRespondedNotificationState.HaveRespondedNotification
                        ? query.Notification?.Data
                        : null);
                }
                catch (Exception e)
                {
                    _log.Error("QueryLastRespondedNotification threw", e);
                    tcs.TrySetResult(null);
                }
            }
            Check();
            return tcs.Task;
#else
            return Task.FromResult<string?>(null);
#endif
        }

        public void OpenSettings()
        {
#if UNITY_ANDROID
            AndroidNotificationCenter.OpenNotificationSettings();
#elif UNITY_IOS
            iOSNotificationCenter.OpenNotificationSettings();
#endif
        }

        public void Dispose() { }

        // Popup xin quyền của vendor trả kết quả qua trạng thái cần đọc lại: hỏi mỗi 100 ms trên main thread.
        Task<NotificationPermission> PollAsync(Func<bool> isDone, Func<NotificationPermission> result, Action? cleanup, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<NotificationPermission>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check()
            {
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        cleanup?.Invoke();
                        tcs.TrySetResult(ReadPermission());
                    }
                    else if (isDone())
                    {
                        var permission = result();
                        cleanup?.Invoke();
                        tcs.TrySetResult(permission);
                    }
                    else
                    {
                        _ctx.Scheduler.Schedule(PollInterval, Check);
                    }
                }
                catch (Exception e)
                {
                    _log.Error("Permission request threw", e);
                    tcs.TrySetResult(NotificationPermission.Denied);
                }
            }
            Check();
            return tcs.Task;
        }

#if UNITY_ANDROID
        static NotificationPermission ToPermission(PermissionStatus status) => status switch
        {
            PermissionStatus.Allowed => NotificationPermission.Granted,
            PermissionStatus.NotRequested => NotificationPermission.NotDetermined,
            PermissionStatus.RequestPending => NotificationPermission.NotDetermined,
            _ => NotificationPermission.Denied,
        };
#endif

#if UNITY_IOS
        static NotificationPermission ToPermission(AuthorizationStatus status) => status switch
        {
            AuthorizationStatus.NotDetermined => NotificationPermission.NotDetermined,
            AuthorizationStatus.Denied => NotificationPermission.Denied,
            _ => NotificationPermission.Granted,
        };
#endif
    }
}
