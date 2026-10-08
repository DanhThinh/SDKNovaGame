#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class NotificationServiceTests
    {
        static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        TestHarness _h = null!;
        FakeNotificationPlatform _platform = null!;
        FakeLifecycle _lifecycle = null!;
        readonly List<(string Event, NotificationOpened Opened)> _logged = new List<(string, NotificationOpened)>();
        readonly List<NotificationService> _services = new List<NotificationService>();

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _h.Clock.UtcNow = Start;
            _platform = new FakeNotificationPlatform();
            _lifecycle = new FakeLifecycle();
            _logged.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var service in _services) service.Dispose();
            _services.Clear();
            _h.Dispose();
        }

        static NotificationOptions Options(ReminderOptions? reminders = null, bool askOnStartup = false, int maxPending = 60) =>
            NotificationOptions.Default with
            {
                Reminders = reminders ?? ReminderOptions.Disabled,
                AskPermissionOnStartup = askOnStartup,
                MaxPending = maxPending,
            };

        static ReminderOptions Reminders(params int[] days) => new ReminderOptions(
            new[] { new ReminderMessage("A", "a"), new ReminderMessage("B", "b") }, days);

        NotificationService Create(NotificationOptions? options = null, INotificationPlatform? platform = null, bool usePlatform = true)
        {
            var service = new NotificationService(usePlatform ? platform ?? _platform : null, options ?? Options(), _h.Context,
                new NotificationDependencies
                {
                    Lifecycle = _lifecycle,
                    LogOpened = (name, opened) => _logged.Add((name, opened)),
                    TimeZone = TimeZoneInfo.Utc,
                });
            _services.Add(service);
            return service;
        }

        NotificationService CreateReady(NotificationOptions? options = null)
        {
            var service = Create(options);
            service.Initialize();
            return service;
        }

        [Test]
        public void Initialize_CreatesTheChannelReadsPermissionAndClearsDelivered()
        {
            _platform.Current = NotificationPermission.Granted;

            var service = CreateReady();

            Assert.AreEqual(NotificationOptions.Default.Channel, _platform.Channel);
            Assert.AreEqual(NotificationPermission.Granted, service.Permission.Value);
            Assert.AreEqual(1, _platform.ClearDeliveredCalls);
            Assert.AreEqual(0, _platform.PermissionRequests);
        }

        [Test]
        public void NoPlatform_IsNotSupportedAndScheduleFails()
        {
            var service = Create(usePlatform: false);
            service.Initialize();

            Assert.AreEqual(NotificationPermission.NotSupported, service.Permission.Value);
            Assert.IsFalse(service.Schedule("a", "t", "b", Start.AddHours(1)).IsSuccess);
        }

        [Test]
        public void UnsupportedPlatform_IsNotSupported()
        {
            _platform.Supported = false;
            var service = CreateReady();

            Assert.AreEqual(NotificationPermission.NotSupported, service.Permission.Value);
            Assert.AreEqual(NotificationPermission.NotSupported,
                _h.Run(service.RequestPermissionAsync(CancellationToken.None)));
        }

        [Test]
        public void AskOnStartup_RequestsPermissionOnlyWhenNotDetermined()
        {
            var service = CreateReady(Options(askOnStartup: true));
            Assert.AreEqual(1, _platform.PermissionRequests);

            _platform.PermissionPrompt!.SetResult(NotificationPermission.Granted);
            _h.Main.Drain();
            Assert.AreEqual(NotificationPermission.Granted, service.Permission.Value);

            _platform = new FakeNotificationPlatform { Current = NotificationPermission.Denied };
            CreateReady(Options(askOnStartup: true));
            Assert.AreEqual(0, _platform.PermissionRequests, "denied: the prompt would not show again");
        }

        [Test]
        public void RequestPermission_ConcurrentCallsShareOnePrompt()
        {
            var service = CreateReady();

            var first = service.RequestPermissionAsync(CancellationToken.None);
            var second = service.RequestPermissionAsync(CancellationToken.None);
            _platform.PermissionPrompt!.SetResult(NotificationPermission.Denied);

            Assert.AreEqual(NotificationPermission.Denied, _h.Run(first));
            Assert.AreEqual(NotificationPermission.Denied, _h.Run(second));
            Assert.AreEqual(1, _platform.PermissionRequests);
            Assert.AreEqual(NotificationPermission.Denied, service.Permission.Value);
        }

        [Test]
        public void Schedule_SameIdReplacesWithAStablePlatformId()
        {
            var service = CreateReady();

            Assert.IsTrue(service.Schedule("energy", "Full", "Play", Start.AddHours(2), data: "shop").IsSuccess);
            Assert.IsTrue(service.Schedule("energy", "Full!", "Play now", Start.AddHours(3)).IsSuccess);

            var scheduled = _platform.Scheduled["energy"];
            Assert.AreEqual("Full!", scheduled.Title);
            Assert.AreEqual(Start.AddHours(3), scheduled.FireTimeUtc);
            Assert.AreEqual(NotificationService.PlatformIdOf("energy"), scheduled.PlatformId);
            Assert.Greater(scheduled.PlatformId, 0);
            CollectionAssert.AreEqual(new[] { "energy" }, service.ScheduledIds);
        }

        [Test]
        public void Schedule_RejectsInvalidIdsAndPastTimes()
        {
            var service = CreateReady();

            Assert.IsFalse(service.Schedule("", "t", "b", Start.AddHours(1)).IsSuccess);
            Assert.IsFalse(service.Schedule("a|b", "t", "b", Start.AddHours(1)).IsSuccess);
            Assert.IsFalse(service.Schedule("nova_reminder_1", "t", "b", Start.AddHours(1)).IsSuccess, "reserved prefix");
            Assert.IsFalse(service.Schedule("late", "t", "b", Start.AddMinutes(-1)).IsSuccess);
            CollectionAssert.IsEmpty(_platform.Scheduled);
        }

        [Test]
        public void ScheduleDaily_InThePast_MovesToTheNextDay()
        {
            var service = CreateReady();

            Assert.IsTrue(service.Schedule("daily", "t", "b", Start.AddHours(-5), NotificationRepeat.Daily).IsSuccess);

            Assert.AreEqual(Start.AddHours(19), _platform.Scheduled["daily"].FireTimeUtc);
            Assert.AreEqual(NotificationRepeat.Daily, _platform.Scheduled["daily"].Repeat);
        }

        [Test]
        public void Cancel_And_CancelAll()
        {
            var service = CreateReady();
            service.Schedule("a", "t", "b", Start.AddHours(1));
            service.Schedule("b", "t", "b", Start.AddHours(2));

            service.Cancel("a");
            CollectionAssert.AreEqual(new[] { "b" }, service.ScheduledIds);
            CollectionAssert.Contains(_platform.Cancelled, "a");

            service.CancelAll();
            CollectionAssert.IsEmpty(service.ScheduledIds);
            Assert.AreEqual(1, _platform.CancelAllCalls);
        }

        [Test]
        public void Reminders_FollowThePlayTimeClampedToDaytime()
        {
            _h.Clock.UtcNow = Start.AddHours(23).AddMinutes(10);   // chơi lúc 23:10 -> nhắc lúc 21:00
            CreateReady(Options(Reminders(1, 2, 0, 400)));

            CollectionAssert.AreEquivalent(new[] { "nova_reminder_1", "nova_reminder_2" }, _platform.Scheduled.Keys);
            Assert.AreEqual(Start.AddDays(1).AddHours(21), _platform.Scheduled["nova_reminder_1"].FireTimeUtc);
            Assert.AreEqual(Start.AddDays(2).AddHours(21), _platform.Scheduled["nova_reminder_2"].FireTimeUtc);
            Assert.AreNotEqual(_platform.Scheduled["nova_reminder_1"].Title, _platform.Scheduled["nova_reminder_2"].Title,
                "messages rotate");

            _h.Clock.UtcNow = Start.AddHours(14).AddMinutes(25);
            _platform.Scheduled.Clear();
            _services[0].RescheduleReminders();
            Assert.AreEqual(Start.AddDays(1).AddHours(14).AddMinutes(25), _platform.Scheduled["nova_reminder_1"].FireTimeUtc,
                "daytime play keeps the same time of day");
        }

        [Test]
        public void Reminders_AtAFixedHour()
        {
            CreateReady(Options(Reminders(2) with { Hour = 19, Minute = 30 }));

            Assert.AreEqual(Start.AddDays(2).AddHours(19).AddMinutes(30), _platform.Scheduled["nova_reminder_2"].FireTimeUtc);
        }

        [Test]
        public void Reminders_AreRescheduledWhenTheGameGoesToBackground()
        {
            var service = CreateReady(Options(Reminders(1) with { Hour = 10 }));
            service.Schedule("game_event", "t", "b", Start.AddDays(5));

            _h.Clock.UtcNow = Start.AddDays(3).AddHours(12);
            _lifecycle.SetPaused(true);

            Assert.AreEqual(Start.AddDays(4).AddHours(10), _platform.Scheduled["nova_reminder_1"].FireTimeUtc);
            Assert.IsTrue(_platform.Scheduled.ContainsKey("game_event"), "game notifications are kept");
            CollectionAssert.AreEquivalent(new[] { "nova_reminder_1", "game_event" }, service.ScheduledIds);
        }

        [Test]
        public void ColdStart_FromANotification_RaisesOpenedAndLogsIt()
        {
            _platform.LastOpenedPayload = NotificationService.BuildPayload("daily_reward", "chest");
            var service = Create();
            var opened = new List<NotificationOpened>();
            service.Opened.Subscribe(opened.Add);

            service.Initialize();

            Assert.AreEqual(new NotificationOpened("daily_reward", "chest", true), opened.Single());
            Assert.AreEqual(opened[0], service.LastOpened);
            Assert.AreEqual(("notification_open", opened[0]), _logged.Single());
        }

        [Test]
        public void ResumeFromANotification_IsReportedOncePerTap()
        {
            var service = CreateReady();
            var opened = new List<NotificationOpened>();
            service.Opened.Subscribe(opened.Add);
            service.Schedule("energy", "t", "b", Start.AddHours(1));

            _lifecycle.SetPaused(true);
            _platform.Tap("energy");
            _lifecycle.SetPaused(false);
            _lifecycle.SetPaused(true);
            _lifecycle.SetPaused(false);

            Assert.AreEqual(new NotificationOpened("energy", null, false), opened.Single());
            Assert.AreEqual(1, _logged.Count);
        }

        [Test]
        public void ForeignPayload_IsIgnored()
        {
            _platform.LastOpenedPayload = "something else";
            var service = CreateReady();

            Assert.IsNull(service.LastOpened);
            CollectionAssert.IsEmpty(_logged);
        }

        [Test]
        public void Resume_ReadsPermissionAgainAndClearsDelivered()
        {
            _platform.Current = NotificationPermission.Granted;
            var service = CreateReady();

            _lifecycle.SetPaused(true);
            _platform.Current = NotificationPermission.Denied;   // tắt trong Settings
            _lifecycle.SetPaused(false);

            Assert.AreEqual(NotificationPermission.Denied, service.Permission.Value);
            Assert.AreEqual(2, _platform.ClearDeliveredCalls);
        }

        [Test]
        public void PendingLimit_DropsTheFurthestNotification()
        {
            var service = CreateReady(Options(maxPending: 2));
            service.Schedule("a", "t", "b", Start.AddHours(1));
            service.Schedule("c", "t", "b", Start.AddHours(3));

            Assert.IsTrue(service.Schedule("b", "t", "b", Start.AddHours(2)).IsSuccess);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, service.ScheduledIds);

            Assert.IsFalse(service.Schedule("z", "t", "b", Start.AddHours(9)).IsSuccess, "the new one is the furthest");
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, _platform.Scheduled.Keys);
        }

        [Test]
        public void Registry_SurvivesRestart_AndForgetsFiredNotifications()
        {
            var first = CreateReady();
            first.Schedule("soon", "t", "b", Start.AddHours(1));
            first.Schedule("later", "t", "b", Start.AddDays(2));
            first.Schedule("daily", "t", "b", Start.AddHours(1), NotificationRepeat.Daily);
            first.Dispose();

            _h.Clock.UtcNow = Start.AddHours(5);
            var second = CreateReady();

            CollectionAssert.AreEquivalent(new[] { "later", "daily" }, second.ScheduledIds);
        }

        [Test]
        public void OpenSettings_IsForwarded()
        {
            var service = CreateReady();

            service.OpenSettings();

            Assert.AreEqual(1, _platform.OpenSettingsCalls);
        }

        [Test]
        public void Payload_RoundTrips()
        {
            Assert.IsTrue(NotificationService.TryParsePayload(NotificationService.BuildPayload("id", "a|b"), out var id, out var data));
            Assert.AreEqual("id", id);
            Assert.AreEqual("a|b", data);
            Assert.IsTrue(NotificationService.TryParsePayload(NotificationService.BuildPayload("id", null), out _, out var none));
            Assert.IsNull(none);
            Assert.IsFalse(NotificationService.TryParsePayload("nova1|", out _, out _));
        }

        [Test]
        public void Dispose_StopsListeningToTheLifecycle()
        {
            var service = CreateReady(Options(Reminders(1)));
            service.Dispose();
            _platform.Scheduled.Clear();

            _lifecycle.SetPaused(true);

            CollectionAssert.IsEmpty(_platform.Scheduled);
            Assert.IsTrue(_platform.Disposed);
        }
    }
}
