#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NovaGames.Mobile.Notifications
{
    /// <summary>
    /// Cấu hình thông báo local: <i>Create > NovaGames > Notification Config</i>, kéo vào ô Notifications của NovaSdkSettings.
    /// </summary>
    [CreateAssetMenu(menuName = "NovaGames/Notification Config", fileName = "NotificationConfig", order = 30)]
    public sealed class NotificationConfig : ScriptableObject
    {
        [Header("Permission")]
        [Tooltip("Hỏi quyền thông báo ngay khi SDK khởi động. Tắt nếu muốn hỏi đúng lúc (vd. sau level 3) bằng " +
                 "NovaNotifications.RequestPermission — tỉ lệ đồng ý thường cao hơn.")]
        [SerializeField] bool askPermissionOnStartup = true;

        [Header("Reminders (nhắc người chơi quay lại)")]
        [Tooltip("Nội dung nhắc, mỗi lần nhắc lấy một câu (xoay vòng). Để trống = tắt nhắc chơi.")]
        [SerializeField] List<ReminderMessageConfig> reminderMessages = new List<ReminderMessageConfig>();
        [Tooltip("Nhắc sau bao nhiêu ngày kể từ lần chơi gần nhất (1-365). Mỗi lần mở/vào nền game, lịch được tính lại.")]
        [SerializeField] int[] reminderDays = { 1, 2, 3, 5, 7, 14, 30 };
        [Tooltip("Giờ nhắc (0-23, giờ máy). -1 = cùng giờ người chơi vừa chơi (kẹp trong khoảng 9h-21h).")]
        [SerializeField, Range(-1, 23)] int reminderHour = -1;
        [SerializeField, Range(0, 59)] int reminderMinute;

        [Header("Android")]
        [Tooltip("Id kênh thông báo. Không đổi sau khi phát hành (Android giữ cài đặt của người chơi theo id).")]
        [SerializeField] string channelId = "nova_default";
        [Tooltip("Tên kênh người chơi thấy trong Settings > Notifications của app.")]
        [SerializeField] string channelName = "Notifications";
        [SerializeField] string channelDescription = "Reminders and game events";
        [Tooltip("Tên icon nhỏ (trắng, nền trong suốt) khai báo trong Project Settings > Mobile Notifications. Rỗng = icon app.")]
        [SerializeField] string smallIcon = string.Empty;
        [Tooltip("Tên icon lớn khai báo trong Project Settings > Mobile Notifications. Rỗng = không có.")]
        [SerializeField] string largeIcon = string.Empty;

        [Header("Analytics")]
        [Tooltip("Event gửi Firebase khi người chơi mở game bằng thông báo (param notification_id). Rỗng = không gửi.")]
        [SerializeField] string openedEventName = "notification_open";

        public NotificationOptions ToOptions(bool isIos)
        {
            var messages = new List<ReminderMessage>();
            foreach (var message in reminderMessages)
            {
                if (message != null && (message.Title.Length > 0 || message.Body.Length > 0))
                    messages.Add(new ReminderMessage(message.Title, message.Body));
            }

            return new NotificationOptions(new NotificationChannelSettings(
                string.IsNullOrWhiteSpace(channelId) ? NotificationOptions.Default.Channel.Id : channelId.Trim(),
                string.IsNullOrWhiteSpace(channelName) ? NotificationOptions.Default.Channel.Name : channelName.Trim(),
                channelDescription ?? string.Empty))
            {
                Reminders = new ReminderOptions(messages, reminderDays ?? Array.Empty<int>())
                {
                    Hour = reminderHour,
                    Minute = reminderMinute,
                },
                AskPermissionOnStartup = askPermissionOnStartup,
                SmallIcon = (smallIcon ?? string.Empty).Trim(),
                LargeIcon = (largeIcon ?? string.Empty).Trim(),
                MaxPending = isIos ? 60 : 200,
                OpenedEventName = (openedEventName ?? string.Empty).Trim(),
            };
        }

        /// <summary>Lỗi cấu hình. Rỗng = hợp lệ.</summary>
        public IReadOnlyList<string> Validate()
        {
            var issues = new List<string>();
            if (reminderMessages.Count > 0 && (reminderDays == null || reminderDays.Length == 0))
                issues.Add("Reminder messages are set but Reminder Days is empty: no reminder is scheduled");
            if (reminderDays != null)
            {
                foreach (var day in reminderDays)
                {
                    if (day < 1 || day > 365) issues.Add("Reminder day " + day + " is outside 1-365 and is ignored");
                }
                if (reminderDays.Length > 30) issues.Add("More than 30 reminder days: iOS keeps at most 64 pending notifications");
            }
            if (string.IsNullOrWhiteSpace(channelId)) issues.Add("Android channel id is empty: using '" + NotificationOptions.Default.Channel.Id + "'");
            return issues;
        }
    }

    [Serializable]
    public sealed class ReminderMessageConfig
    {
        [SerializeField] string title = string.Empty;
        [SerializeField, TextArea(1, 3)] string body = string.Empty;

        public ReminderMessageConfig() { }

        public ReminderMessageConfig(string title, string body)
        {
            this.title = title ?? string.Empty;
            this.body = body ?? string.Empty;
        }

        public string Title => (title ?? string.Empty).Trim();
        public string Body => (body ?? string.Empty).Trim();
    }
}
