#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Notifications
{
    /// <summary>
    /// Thông báo local, không phụ thuộc vendor:
    /// <list type="bullet">
    /// <item>Lên lịch/hủy theo Id dạng chữ (cùng Id = thay thông báo cũ), lưu danh sách để giữ dưới giới hạn của iOS (64).</item>
    /// <item>Nhắc chơi: lên lịch lại mỗi lần mở game và mỗi lần vào nền, tính từ lúc người chơi vừa chơi.</item>
    /// <item>Mở game bằng thông báo: raise <see cref="Opened"/> + log analytics (cả khởi động lạnh lẫn đang chạy nền).</item>
    /// <item>Quay lại game: xóa thông báo đang hiện + badge, đọc lại quyền (người chơi có thể đổi trong Settings).</item>
    /// </list>
    /// </summary>
    public sealed class NotificationService : INotificationService, IDisposable
    {
        internal const string ReminderPrefix = "nova_reminder_";
        const string PayloadPrefix = "nova1|";

        readonly INotificationPlatform? _platform;
        readonly NotificationOptions _options;
        readonly ModuleContext _ctx;
        readonly NotificationDependencies _deps;
        readonly ISdkLogger _log;
        readonly TimeZoneInfo _timeZone;
        readonly SdkProperty<NotificationPermission> _permission;
        readonly SdkEvent<NotificationOpened> _opened;
        readonly Dictionary<string, Entry> _scheduled = new Dictionary<string, Entry>(StringComparer.Ordinal);

        Task<NotificationPermission>? _permissionRequest;
        IDisposable? _pauseSubscription;
        string? _lastOpenedPayload;
        bool _initialized;
        bool _supported;
        bool _disposed;

        public NotificationService(INotificationPlatform? platform, NotificationOptions options, ModuleContext ctx,
                                   NotificationDependencies? deps = null)
        {
            _platform = platform;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _deps = deps ?? new NotificationDependencies();
            _log = ctx.Logs.Create("notifications");
            _timeZone = _deps.TimeZone ?? TimeZoneInfo.Local;
            _permission = new SdkProperty<NotificationPermission>(NotificationPermission.NotDetermined, _log);
            _opened = new SdkEvent<NotificationOpened>(_log);
        }

        public ISdkProperty<NotificationPermission> Permission => _permission;
        public ISdkEvent<NotificationOpened> Opened => _opened;
        public NotificationOpened? LastOpened { get; private set; }
        public bool IsSupported => _supported;

        public IReadOnlyCollection<string> ScheduledIds
        {
            get
            {
                PruneFired();
                return new List<string>(_scheduled.Keys);
            }
        }

        /// <summary>Gọi một lần lúc khởi động: tạo kênh, đọc quyền, phát hiện mở game từ thông báo, lên lịch nhắc.</summary>
        public void Initialize()
        {
            if (_initialized || _disposed) return;
            _initialized = true;
            LoadRegistry();

            var platform = _platform;
            _supported = platform != null && SafeCall("Initialize", () => platform.Initialize(_options.Channel), false);
            if (!_supported)
            {
                _permission.Set(NotificationPermission.NotSupported);
                _log.Info("Local notifications are not supported here" + (platform is null ? " (adapter not installed)" : string.Empty));
                return;
            }

            RefreshPermission();
            ClearDelivered();
            _ = CheckOpenedAsync(isColdStart: true);
            RescheduleReminders();
            if (_deps.Lifecycle != null) _pauseSubscription = _deps.Lifecycle.PauseChanged.Subscribe(OnPauseChanged);
            if (_options.AskPermissionOnStartup && _permission.Value == NotificationPermission.NotDetermined)
                _ = RequestPermissionAsync(CancellationToken.None);
            _log.Info("Ready: permission " + _permission.Value + ", " + _scheduled.Count + " scheduled");
        }

        // ---------------- Permission ----------------

        public Task<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
            if (_disposed || !_supported) return Task.FromResult(_supported ? _permission.Value : NotificationPermission.NotSupported);
            if (_permission.Value == NotificationPermission.Granted) return Task.FromResult(NotificationPermission.Granted);
            return _permissionRequest ??= RequestPermissionCoreAsync(ct);
        }

        async Task<NotificationPermission> RequestPermissionCoreAsync(CancellationToken ct)
        {
            // Popup xin quyền làm app mất focus: không để app open ad hiện ngay khi người chơi trả lời.
            IDisposable? suppressed = null;
            try
            {
                suppressed = _deps.SuppressAppOpen?.Invoke("notification_permission");
            }
            catch (Exception e)
            {
                _log.Error("SuppressAppOpen threw", e);
            }
            try
            {
                var result = await _platform!.RequestPermissionAsync(ct);
                if (_disposed) return result;
                _permission.Set(result);
                _log.Info("Permission: " + result);
                return result;
            }
            catch (Exception e)
            {
                _log.Error("Requesting notification permission threw", e);
                return _permission.Value;
            }
            finally
            {
                _permissionRequest = null;
                suppressed?.Dispose();
            }
        }

        public void OpenSettings()
        {
            var platform = _platform;
            if (_supported && platform != null) _log.TryRun("OpenSettings", platform.OpenSettings);
        }

        // ---------------- Schedule / Cancel ----------------

        public SdkResult Schedule(string id, string title, string body, DateTime fireTimeUtc,
                                  NotificationRepeat repeat = NotificationRepeat.None, string? data = null)
        {
            if (_disposed) return SdkError.Disposed("notifications.schedule");
            if (string.IsNullOrEmpty(id) || id.IndexOf('|') >= 0 || id.IndexOf('\n') >= 0)
                return Invalid("Notification id must be non-empty and must not contain '|' or new lines: '" + id + "'");
            if (id.StartsWith(ReminderPrefix, StringComparison.Ordinal))
                return Invalid("Ids starting with '" + ReminderPrefix + "' are reserved for SDK reminders");
            if (!_supported) return new SdkError("notifications.not_supported", SdkErrorCategory.Unavailable,
                "Local notifications are not supported on this platform", false);

            var now = _ctx.Clock.UtcNow;
            fireTimeUtc = ToUtc(fireTimeUtc);
            if (repeat == NotificationRepeat.Daily)
            {
                while (fireTimeUtc <= now) fireTimeUtc = fireTimeUtc.AddDays(1);
            }
            else if (fireTimeUtc <= now)
            {
                return Invalid("Fire time of '" + id + "' is in the past");
            }

            var result = ScheduleCore(id, title ?? string.Empty, body ?? string.Empty, fireTimeUtc, repeat, data);
            SaveRegistry();
            return result;
        }

        public void Cancel(string id)
        {
            if (_disposed || string.IsNullOrEmpty(id)) return;
            CancelCore(id);
            SaveRegistry();
        }

        public void CancelAll()
        {
            if (_disposed) return;
            var platform = _platform;
            if (_supported && platform != null) _log.TryRun("CancelAll", platform.CancelAll);
            _scheduled.Clear();
            SaveRegistry();
            // Nhắc chơi là của SDK: lên lịch lại ở lần vào nền kế tiếp.
        }

        SdkResult ScheduleCore(string id, string title, string body, DateTime fireTimeUtc, NotificationRepeat repeat, string? data)
        {
            var notification = new ScheduledNotification(id, PlatformIdOf(id), title, body, fireTimeUtc, repeat, BuildPayload(id, data))
            {
                SmallIcon = _options.SmallIcon,
                LargeIcon = _options.LargeIcon,
            };
            if (!_log.TryRun("Schedule", () => _platform!.Schedule(notification)))
                return new SdkError("notifications.schedule.failed", SdkErrorCategory.Provider, "Platform failed to schedule '" + id + "'", false);

            _scheduled[id] = new Entry(fireTimeUtc, repeat);
            _log.Debug("Scheduled '" + id + "' at " + fireTimeUtc.ToString("u", CultureInfo.InvariantCulture) + (repeat == NotificationRepeat.Daily ? " (daily)" : string.Empty));
            return EnforceLimit(id);
        }

        void CancelCore(string id)
        {
            var platform = _platform;
            if (_supported && platform != null) _log.TryRun("Cancel", () => platform.Cancel(id, PlatformIdOf(id)));
            _scheduled.Remove(id);
        }

        // Vượt giới hạn: bỏ thông báo xa nhất (iOS tự bỏ thông báo vượt 64 mà không báo).
        SdkResult EnforceLimit(string justScheduled)
        {
            PruneFired();
            SdkResult result = SdkResult.Ok;
            while (_scheduled.Count > Math.Max(1, _options.MaxPending))
            {
                string? latest = null;
                DateTime latestTime = DateTime.MinValue;
                foreach (var pair in _scheduled)
                {
                    if (latest is null || pair.Value.FireTimeUtc > latestTime)
                    {
                        latest = pair.Key;
                        latestTime = pair.Value.FireTimeUtc;
                    }
                }
                _log.Warning("More than " + _options.MaxPending + " pending notifications: dropped '" + latest + "'");
                CancelCore(latest!);
                if (latest == justScheduled)
                    result = new SdkError("notifications.limit", SdkErrorCategory.Busy,
                        "Too many pending notifications: '" + justScheduled + "' is the furthest one and was not kept", false);
            }
            return result;
        }

        // ---------------- Reminders ----------------

        /// <summary>Hủy lịch nhắc cũ và lên lịch lại tính từ bây giờ.</summary>
        public void RescheduleReminders()
        {
            if (_disposed || !_supported) return;
            foreach (var id in new List<string>(_scheduled.Keys))
            {
                if (id.StartsWith(ReminderPrefix, StringComparison.Ordinal)) CancelCore(id);
            }

            var reminders = _options.Reminders;
            if (reminders.IsEnabled)
            {
                var nowLocal = ToLocal(_ctx.Clock.UtcNow);
                int hour, minute;
                if (reminders.Hour >= 0)
                {
                    hour = Clamp(reminders.Hour, 0, 23);
                    minute = Clamp(reminders.Minute, 0, 59);
                }
                else
                {
                    hour = Clamp(nowLocal.Hour, reminders.EarliestHour, reminders.LatestHour);
                    minute = hour == nowLocal.Hour ? nowLocal.Minute : 0;
                }

                var days = new SortedSet<int>();
                foreach (var day in reminders.Days)
                {
                    if (day >= 1 && day <= 365) days.Add(day);
                }
                foreach (var day in days)
                {
                    var fireLocal = nowLocal.Date.AddDays(day).AddHours(hour).AddMinutes(minute);
                    // Xoay vòng nội dung theo ngày để mỗi lần nhắc một câu khác nhau.
                    var message = reminders.Messages[(day + nowLocal.DayOfYear) % reminders.Messages.Count];
                    ScheduleCore(ReminderPrefix + day, message.Title, message.Body, ToUtcFromLocal(fireLocal), NotificationRepeat.None, null);
                }
            }
            SaveRegistry();
        }

        // ---------------- Lifecycle / opened ----------------

        void OnPauseChanged(bool paused)
        {
            if (_disposed) return;
            if (paused)
            {
                RescheduleReminders();
                return;
            }
            RefreshPermission();
            ClearDelivered();
            _ = CheckOpenedAsync(isColdStart: false);
        }

        void RefreshPermission()
        {
            var platform = _platform;
            if (platform is null) return;
            _permission.Set(SafeCall("ReadPermission", platform.ReadPermission, _permission.Value));
        }

        void ClearDelivered()
        {
            var platform = _platform;
            if (platform != null) _log.TryRun("ClearDelivered", platform.ClearDelivered);
        }

        async Task CheckOpenedAsync(bool isColdStart)
        {
            var platform = _platform;
            if (platform is null) return;
            string? payload;
            try
            {
                payload = await platform.ReadLastOpenedPayloadAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                _log.Error("ReadLastOpenedPayload threw", e);
                return;
            }
            if (_disposed) return;
            // Platform trả lại cùng payload cho tới khi có thông báo khác được bấm.
            if (payload is null || payload == _lastOpenedPayload || !TryParsePayload(payload, out var id, out var data)) return;
            _lastOpenedPayload = payload;

            var opened = new NotificationOpened(id, data, isColdStart);
            LastOpened = opened;
            _log.Info("Opened from notification '" + id + "'" + (isColdStart ? " (cold start)" : string.Empty));
            if (_options.OpenedEventName.Length > 0)
            {
                var logOpened = _deps.LogOpened;
                if (logOpened != null) _log.TryRun("Log notification open", () => logOpened(_options.OpenedEventName, opened));
            }
            _opened.Raise(opened);
        }

        // ---------------- Registry ----------------

        void LoadRegistry()
        {
            try
            {
                if (!_ctx.Store.TryGetString(StorageKeys.NotificationsScheduled, out var text)) return;
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Split(new[] { '|' }, 3);
                    if (parts.Length != 3) continue;
                    if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) continue;
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                    var repeat = parts[1] == "d" ? NotificationRepeat.Daily : NotificationRepeat.None;
                    if (parts[2].Length > 0) _scheduled[parts[2]] = new Entry(new DateTime(ticks, DateTimeKind.Utc), repeat);
                }
                PruneFired();
            }
            catch (Exception e)
            {
                _log.Error("Could not read scheduled notifications", e);
            }
        }

        void SaveRegistry()
        {
            try
            {
                var text = new StringBuilder();
                foreach (var pair in _scheduled)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(pair.Value.FireTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
                        .Append('|').Append(pair.Value.Repeat == NotificationRepeat.Daily ? "d" : "o")
                        .Append('|').Append(pair.Key);
                }
                _ctx.Store.SetString(StorageKeys.NotificationsScheduled, text.ToString());
                _ctx.Store.Flush();
            }
            catch (Exception e)
            {
                _log.Error("Could not save scheduled notifications", e);
            }
        }

        // Thông báo một lần đã tới giờ thì không còn chờ.
        void PruneFired()
        {
            var now = _ctx.Clock.UtcNow;
            List<string>? fired = null;
            foreach (var pair in _scheduled)
            {
                if (pair.Value.Repeat == NotificationRepeat.None && pair.Value.FireTimeUtc <= now) (fired ??= new List<string>()).Add(pair.Key);
            }
            if (fired is null) return;
            foreach (var id in fired) _scheduled.Remove(id);
        }

        // ---------------- Helpers ----------------

        /// <summary>Id số ổn định (FNV-1a) cho Android; cùng Id luôn ra cùng số giữa các lần chạy.</summary>
        internal static int PlatformIdOf(string id)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var c in id)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                int value = (int)(hash & 0x7FFFFFFF);
                return value == 0 ? 1 : value;
            }
        }

        internal static string BuildPayload(string id, string? data) => PayloadPrefix + id + "|" + (data ?? string.Empty);

        internal static bool TryParsePayload(string payload, out string id, out string? data)
        {
            id = string.Empty;
            data = null;
            if (!payload.StartsWith(PayloadPrefix, StringComparison.Ordinal)) return false;
            var rest = payload.Substring(PayloadPrefix.Length);
            int separator = rest.IndexOf('|');
            if (separator <= 0) return false;
            id = rest.Substring(0, separator);
            var value = rest.Substring(separator + 1);
            data = value.Length > 0 ? value : null;
            return true;
        }

        DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _timeZone);

        DateTime ToUtcFromLocal(DateTime local)
        {
            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _timeZone);
            }
            catch (ArgumentException)
            {
                // Giờ không tồn tại (chuyển giờ mùa hè): lùi một giờ.
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.AddHours(-1), DateTimeKind.Unspecified), _timeZone);
            }
        }

        DateTime ToUtc(DateTime time) => time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => ToUtcFromLocal(time),
        };

        static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        static SdkResult Invalid(string message) => new SdkError("notifications.invalid", SdkErrorCategory.Validation, message, false);

        T SafeCall<T>(string what, Func<T> call, T fallback)
        {
            try
            {
                return call();
            }
            catch (Exception e)
            {
                _log.Error(what + " threw", e);
                return fallback;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pauseSubscription?.Dispose();
            _opened.Clear();
            _permission.ClearSubscribers();
            _log.TryRun("Platform.Dispose", () => _platform?.Dispose());
        }

        readonly struct Entry
        {
            public Entry(DateTime fireTimeUtc, NotificationRepeat repeat)
            {
                FireTimeUtc = fireTimeUtc;
                Repeat = repeat;
            }

            public DateTime FireTimeUtc { get; }
            public NotificationRepeat Repeat { get; }
        }
    }
}
