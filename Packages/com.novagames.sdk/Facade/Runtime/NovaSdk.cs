#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Diagnostics;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile
{
    public enum NovaSdkState : byte { NotInitialized, Initializing, Ready }

    /// <summary>
    /// Điểm vào duy nhất của SDK. Gọi một lần lúc khởi động game:
    /// <code>
    /// await NovaSdk.InitializeAsync(settings);     // settings: asset NovaSdkSettings
    /// </code>
    /// Consent: mặc định SDK tự chạy Google UMP (xem <see cref="NovaPrivacy"/>); dùng CMP khác thì chọn
    /// Consent Source = Game và gọi <see cref="SetConsent"/>.
    /// Sau đó dùng <see cref="NovaAds"/>, <see cref="NovaRemoteConfig"/>, <see cref="NovaAnalytics"/>,
    /// <see cref="NovaAttribution"/>, <see cref="NovaPrivacy"/>, <see cref="NovaIap"/>, <see cref="NovaNotifications"/>, <see cref="NovaCrash"/>. Mọi API gọi trên main thread và không throw.
    /// </summary>
    public static class NovaSdk
    {
        static NovaSdkState s_state;
        static Task? s_initialization;
        static TaskCompletionSource<bool> s_ready = NewReady();
        static SdkProperty<ConsentSnapshot> s_consent = new SdkProperty<ConsentSnapshot>(ConsentSnapshot.Unknown);
        static bool s_consentSet;
        static NovaRuntime? s_runtime;

        public static NovaSdkState State => s_state;

        /// <summary>true khi Remote Config và Analytics đã khởi tạo xong (thành công hoặc chạy bằng giá trị mặc định).</summary>
        public static bool IsReady => s_state == NovaSdkState.Ready;

        /// <summary>
        /// Hoàn thành khi SDK sẵn sàng (Remote Config đã fetch hoặc hết thời gian chờ, Analytics đã init).
        /// Ads tiếp tục khởi tạo ở nền; kiểm tra bằng <see cref="NovaAds.IsInterReady"/> / <see cref="NovaAds.IsRewardReady"/>.
        /// Chờ được cả trước khi gọi <see cref="InitializeAsync"/>.
        /// </summary>
        public static Task WhenReady => s_ready.Task;

        /// <summary>Consent đang áp dụng (Unknown cho tới khi UMP trả lời hoặc game gọi <see cref="SetConsent"/>).</summary>
        public static ConsentSnapshot Consent => s_consent.Value;

        /// <summary>
        /// Khởi tạo SDK từ asset <see cref="NovaSdkSettings"/>. Gọi trên main thread. Gọi nhiều lần trả về cùng một task.
        /// Không throw: module lỗi được log và chạy ở chế độ giảm (vd. Remote Config dùng giá trị mặc định).
        /// </summary>
        public static Task InitializeAsync(NovaSdkSettings settings)
        {
            if (s_initialization != null) return s_initialization;
            if (settings == null)
            {
                Debug.LogError("[Nova] NovaSdk.InitializeAsync: settings is null. Create one via Create > NovaGames > SDK Settings.");
                // Không để màn loading chờ WhenReady mãi: game chạy tiếp, mọi API Nova* là no-op.
                s_ready.TrySetResult(true);
                return Task.CompletedTask;
            }
            if (SynchronizationContext.Current == null)
            {
                Debug.LogError("[Nova] NovaSdk.InitializeAsync must be called on Unity's main thread.");
                s_ready.TrySetResult(true);
                return Task.CompletedTask;
            }

            bool isDevelopment = Debug.isDebugBuild;
            bool isIos = Application.platform == RuntimePlatform.IPhonePlayer;
            var setup = settings.ToSetup(isDevelopment, isIos, Application.isEditor);
            var runtimeSettings = isDevelopment ? RuntimeSdkSettings.Development : RuntimeSdkSettings.Production;
            if (setup.AdjustSettings != null && settings.Adjust != null)
                runtimeSettings = runtimeSettings.WithSinkSettings(settings.Adjust.SinkId, setup.AdjustSettings);

            // Module (Firebase, Ads...) là component con của NovaModules trong scene: đăng ký adapter trước khi lấy snapshot.
            NovaModule.RegisterLoaded();
            if (UnityEngine.Object.FindAnyObjectByType<NovaModules>(FindObjectsInactive.Include) == null)
                Debug.LogError("[Nova] NovaSdk.InitializeAsync: no NovaModules in the scene, so no vendor module (Firebase, ads...) " +
                               "is registered. Use the NovaSdk prefab, or add the NovaGames/Nova Modules component to a GameObject.");

            var ctx = ModuleContext.CreateDefault(runtimeSettings);
            var lifecycle = ApplicationLifecycleHost.Create();
            s_initialization = InitializeAsync(setup, ctx, AdapterRegistry.Snapshot(), lifecycle, new UnityNetworkStatus(), lifecycle);
            return s_initialization;
        }

        /// <summary>
        /// Truyền consent của người dùng khi Consent Source = Game (CMP riêng của game). Gọi trước hoặc sau
        /// <see cref="InitializeAsync"/> đều được; mỗi lần consent đổi thì gọi lại. Ads chỉ load khi
        /// <c>consent.CanRequestAds</c>; Adjust chỉ khởi động sau consent đầu tiên. Với Google UMP, SDK tự gọi hàm này.
        /// Trên iOS, <c>consent.Att</c> được SDK thay bằng trạng thái ATT thật của máy.
        /// </summary>
        public static void SetConsent(ConsentSnapshot consent)
        {
            if (consent is null)
            {
                Debug.LogError("[Nova] NovaSdk.SetConsent: consent is null");
                return;
            }
            consent = WithAtt(consent);
            s_consentSet = true;
            s_consent.Set(consent);
            NovaPrivacy.RaiseConsentChanged(consent);
            var runtime = s_runtime;
            if (runtime?.GameConsentWaitingForAtt == true)
            {
                // CMP có thể cập nhật snapshot trong lúc popup ATT đang mở. Giữ bản mới nhất nhưng chưa đánh thức
                // tracking cho tới khi ATT trả lời.
                return;
            }
            if (runtime?.RequestAttAfterGameConsent == true)
            {
                runtime.RequestAttAfterGameConsent = false;
                runtime.GameConsentWaitingForAtt = true;
                _ = FinishGameConsentAfterAttAsync(runtime);
                return;
            }
            runtime?.ApplyConsent(consent);
        }

        static async Task FinishGameConsentAfterAttAsync(NovaRuntime runtime)
        {
            await RequestAttAsync();
            if (!ReferenceEquals(s_runtime, runtime)) return;
            runtime.GameConsentWaitingForAtt = false;
            // Áp snapshot mới nhất của CMP cùng trạng thái ATT vừa nhận vào Ads/tracking.
            SetConsent(s_consent.Value);
        }

        /// <summary>Tắt SDK và giải phóng mọi module (dùng cho test hoặc khi reload toàn bộ game). Sau đó có thể init lại.</summary>
        public static void Shutdown()
        {
            s_runtime?.Dispose();
            Reset();
        }

        // Tách khỏi Unity (ModuleContext, registry, lifecycle truyền vào) để test được với fake.
        internal static Task InitializeAsync(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry,
                                             IApplicationLifecycle? lifecycle, INetworkStatus? network, Component? host = null)
        {
            if (s_initialization != null) return s_initialization;
            s_initialization = RunAsync(setup, ctx, registry, lifecycle, network, host);
            return s_initialization;
        }

        static async Task RunAsync(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry,
                                   IApplicationLifecycle? lifecycle, INetworkStatus? network, Component? host)
        {
            s_state = NovaSdkState.Initializing;
            var log = ctx.Logs.Create("sdk");
            var runtime = new NovaRuntime(host, log);
            s_runtime = runtime;
            try
            {
                foreach (var issue in setup.Issues) log.Warning("Settings: " + issue);

                // Crashlytics trước tiên để bắt cả crash trong lúc khởi tạo các module sau.
                StartCrashReporting(setup, ctx, registry, runtime, log);

                // ATT (iOS): đọc trạng thái ngay; popup được hỏi sau form consent (xem StartConsent).
                StartAtt(setup, ctx, registry, runtime, lifecycle, log);

                // 0. Consent: form UMP hiện song song, không chặn Ready (Ads/Adjust tự chờ consent).
                StartConsent(setup, ctx, registry, runtime, lifecycle, log);

                // IAP: sở hữu đã lưu (remove_ads) có hiệu lực ngay; kết nối store chạy nền, không chặn Ready.
                StartIap(setup, ctx, registry, runtime, log);

                // Thông báo local: tạo kênh, phát hiện mở game từ thông báo, lên lịch nhắc chơi.
                StartNotifications(setup, ctx, registry, runtime, lifecycle, log);

                // 1. Remote Config: giá trị đã lưu dùng được ngay, fetch chạy song song với tracking.
                registry.RemoteConfigSources.TryGetValue(RemoteConfigSourceIds.Firebase, out var sourceFactory);
                runtime.RemoteConfig = new RemoteConfigService(sourceFactory?.Invoke(ctx), ctx.Store, ctx.Clock,
                    ctx.Logs.Create("remote_config"), ctx.Settings.RemoteConfig, setup.RemoteKeys);
                runtime.RemoteConfig.LoadCache();
                NovaRemoteConfig.Attach(runtime.RemoteConfig, setup.RemoteConfig, ctx.Settings.RemoteConfig.FetchTimeout,
                    ctx.Logs.Create("remote_config"));
                if (sourceFactory is null) log.Warning("Firebase Remote Config adapter is not installed; using defaults");

                // 2. Tracking: Firebase Analytics + Adjust (nếu có Adjust Config). Event gửi trước đó được phát lại.
                var analyticsInit = StartTracking(setup, ctx, registry, runtime, log);

                // 3. Chờ Remote Config (có timeout) trước khi tạo Ads: Ads đọc kill switch/capping và cờ của adapter.
                var config = await runtime.RemoteConfig.InitializeAsync(CancellationToken.None);
                NovaRemoteConfig.ReportResult(config.Error);
                if (!config.IsSuccess) log.Warning("Remote Config running on " + runtime.RemoteConfig.Current.Value.Source + ": " + config.Error);

                // 4. Ads: khởi tạo ở nền (chờ consent, init SDK mediation); NovaAds dùng được ngay.
                StartAds(setup, ctx, registry, runtime, lifecycle, network, log);

                if (analyticsInit != null) await analyticsInit;
            }
            catch (Exception e)
            {
                log.Error("Initialization failed unexpectedly", e);
            }
            finally
            {
                s_state = NovaSdkState.Ready;
                s_ready.TrySetResult(true);
                log.Info("Ready (Remote Config: " + runtime.RemoteConfig?.Current.Value.Source
                         + ", sinks: " + string.Join(", ", runtime.SinkIds()) + ", ads: " + (runtime.Ads?.State.Value.ToString() ?? "off") + ")");
            }
        }

        static void StartConsent(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry, NovaRuntime runtime,
                                 IApplicationLifecycle? lifecycle, ISdkLogger log)
        {
            switch (setup.ConsentSource)
            {
                case NovaConsentSource.AssumeGrantedForTesting:
                    if (!s_consentSet)
                    {
                        log.Warning("Consent Source is 'Assume granted (testing only)': every purpose is treated as granted");
                        SetConsent(ConsentSnapshot.AllGranted);
                    }
                    if (setup.RequestAttOnStartup) _ = RequestAttAsync();
                    return;
                case NovaConsentSource.Game:
                    if (!s_consentSet) log.Info("Waiting for NovaSdk.SetConsent: ads and Adjust start after the game passes consent");
                    if (setup.RequestAttOnStartup)
                    {
                        // CMP của game phải hoàn tất trước popup ATT. Nếu consent đã được truyền trước init thì hỏi ngay;
                        // nếu chưa, SetConsent sẽ tiếp tục luồng này sau snapshot đầu tiên.
                        if (s_consentSet)
                        {
                            runtime.GameConsentWaitingForAtt = true;
                            _ = FinishGameConsentAfterAttAsync(runtime);
                        }
                        else runtime.RequestAttAfterGameConsent = true;
                    }
                    return;
            }

            if (!registry.ConsentPlatforms.TryGetValue(ConsentPlatformIds.GoogleUmp, out var factory))
            {
                log.Error("Consent Source is Google UMP but the UMP module is not installed (NOVA_UMP, Google Mobile Ads plugin): " +
                          "ads stay blocked until NovaSdk.SetConsent is called");
                if (setup.RequestAttOnStartup) _ = RequestAttAsync();
                return;
            }

            var platform = factory(ctx);
            runtime.ConsentPlatform = platform;
            NovaPrivacy.Bind(platform, ctx.Logs.Create("privacy"));

            // Consent của lần chạy trước: bắt đầu load ads ngay trong lúc UMP cập nhật (cách Google khuyến nghị).
            if (!s_consentSet)
            {
                var stored = platform.ReadStored(setup.ConsentOptions);
                if (stored.CanRequestAds) SetConsent(stored);
            }
            var gather = new ConsentGather(platform, setup.ConsentOptions, ctx.Scheduler, log);
            runtime.ConsentGather = gather;
            // UMP chưa trả lời được (mất mạng, server chậm): hỏi lại khi người chơi quay lại game.
            if (lifecycle != null)
                runtime.Subscriptions.Add(lifecycle.PauseChanged.Subscribe(paused => { if (!paused) RetryConsent(gather); }));
            gather.RequestAttOnSuccess = setup.RequestAttOnStartup;
            _ = GatherConsentAsync(gather);
        }

        // Một lần thu thập consent bằng UMP; giữ trạng thái để hỏi lại khi lần trước chưa biết được vùng của người dùng.
        sealed class ConsentGather
        {
            public ConsentGather(IConsentPlatform platform, ConsentGatherOptions options, IScheduler scheduler, ISdkLogger log)
            {
                Platform = platform;
                Options = options;
                Scheduler = scheduler;
                Log = log;
            }

            public readonly IConsentPlatform Platform;
            public readonly ConsentGatherOptions Options;
            public readonly IScheduler Scheduler;
            public readonly ISdkLogger Log;
            public bool Running;
            public bool NeedsRetry;
            public bool RequestAttOnSuccess;
            public int Attempts;
            public IDisposable? RetryTimer;
        }

        static readonly TimeSpan[] ConsentRetryDelays = { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) };

        static void RetryConsent(ConsentGather gather)
        {
            if (!gather.NeedsRetry || gather.Running || s_runtime?.ConsentGather != gather) return;
            gather.RetryTimer?.Dispose();
            gather.RetryTimer = null;
            gather.Log.Info("Retrying consent gathering (attempt " + (gather.Attempts + 1) + ")");
            _ = GatherConsentAsync(gather);
        }

        static async Task GatherConsentAsync(ConsentGather gather)
        {
            var platform = gather.Platform;
            var log = gather.Log;
            gather.Running = true;
            gather.NeedsRetry = false;
            gather.Attempts++;
            try
            {
                var result = await platform.GatherAsync(gather.Options, CancellationToken.None);
                gather.Running = false;
                // SDK có thể đã Shutdown trong lúc form mở: không áp consent của lần chạy cũ.
                if (s_runtime?.ConsentPlatform != platform) return;

                // ATT hỏi sau form consent (Apple/Google khuyến nghị) và trước khi áp consent, để Ads/Adjust khởi động
                // lần đầu đã biết người dùng có cho dùng IDFA không.
                if (gather.RequestAttOnSuccess)
                {
                    gather.RequestAttOnSuccess = false;
                    await RequestAttAsync();
                    if (s_runtime?.ConsentPlatform != platform) return;
                }

                if (result.TryGetValue(out var consent)) SetConsent(consent);
                else log.Warning("Consent not gathered: " + result.Error);

                // Chưa biết người dùng ở vùng nào (UMP lỗi/timeout ở lần mở đầu tiên): Ads/Adjust vẫn chờ, hỏi lại sau.
                bool unknown = !result.TryGetValue(out var gathered) || gathered.Jurisdiction == Jurisdiction.Unknown;
                if (unknown && !gather.Options.IsUnderAge)
                    ScheduleConsentRetry(gather, "Consent region unknown (UMP unreachable?)");
            }
            catch (Exception e)
            {
                gather.Running = false;
                log.Error("Consent gathering failed unexpectedly", e);
                // Vendor có thể throw thay vì trả SdkResult lỗi. Vẫn đưa vào cùng retry pipeline để Ads/Adjust
                // không bị khóa vĩnh viễn trong phiên hiện tại.
                if (s_runtime?.ConsentGather == gather && !gather.Options.IsUnderAge)
                    ScheduleConsentRetry(gather, "Consent gathering threw");
            }
        }

        static void ScheduleConsentRetry(ConsentGather gather, string reason)
        {
            gather.NeedsRetry = true;
            var delay = ConsentRetryDelays[Math.Min(gather.Attempts - 1, ConsentRetryDelays.Length - 1)];
            gather.RetryTimer?.Dispose();
            gather.RetryTimer = gather.Scheduler.Schedule(delay, () => RetryConsent(gather));
            gather.Log.Warning(reason + ": ads and Adjust wait; retrying in " + delay.TotalSeconds + " s or on resume");
        }

        static void StartAtt(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry, NovaRuntime runtime,
                             IApplicationLifecycle? lifecycle, ISdkLogger log)
        {
            // Chỉ máy iOS thật có adapter ATT; Editor/Android không có ATT.
            if (!registry.AttPlatforms.TryGetValue(AttPlatformIds.Apple, out var factory)) return;
            try
            {
                runtime.Att = factory(ctx);
            }
            catch (Exception e)
            {
                log.Error("Creating the ATT adapter failed", e);
                return;
            }
            runtime.AttAllowed = setup.AttAllowed;
            log.Info("ATT status: " + runtime.Att.Status + (setup.RequestAttOnStartup ? "" : " (not requested on startup)"));

            // Người dùng có thể đổi quyền tracking trong Settings của iOS: đọc lại khi quay về game.
            if (lifecycle != null)
                runtime.Subscriptions.Add(lifecycle.PauseChanged.Subscribe(paused => { if (!paused) RefreshAtt(); }));
            // Game đã gọi SetConsent trước InitializeAsync: thay Att bằng trạng thái thật.
            RefreshAtt();
        }

        // Single-flight: nhiều lệnh hỏi cùng lúc dùng chung một popup.
        internal static Task<AttStatus> RequestAttAsync()
        {
            var runtime = s_runtime;
            var att = runtime?.Att;
            if (runtime is null || att is null) return Task.FromResult(AttStatus.NotApplicable);
            if (!runtime.AttAllowed)
            {
                runtime.Log.Warning("ATT is not requested: the game is set as under age of consent");
                return Task.FromResult(SafeAttStatus(att));
            }
            if (runtime.AttRequest is { IsCompleted: false } pending) return pending;
            // iOS chỉ hiện popup một lần: người dùng đã trả lời thì đổi được trong Settings, không hỏi lại.
            var current = SafeAttStatus(att);
            if (current != AttStatus.NotDetermined) return Task.FromResult(current);
            return runtime.AttRequest = RunAttRequestAsync(runtime, att);
        }

        static async Task<AttStatus> RunAttRequestAsync(NovaRuntime runtime, IAttPlatform att)
        {
            AttStatus status;
            try
            {
                // Popup ATT làm app mất focus: không để app open ad hiện ngay khi người dùng trả lời.
                using (NovaAds.SuppressAppOpen("att")) status = await att.RequestAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                runtime.Log.Error("ATT request failed", e);
                status = SafeAttStatus(att);
            }
            // SDK có thể đã Shutdown trong lúc popup mở.
            if (s_runtime == runtime)
            {
                runtime.Log.Info("ATT status: " + status);
                RefreshAtt();
            }
            return status;
        }

        /// <summary>Trạng thái ATT của máy; NotApplicable khi không phải iOS hoặc SDK chưa khởi tạo.</summary>
        internal static AttStatus CurrentAttStatus => s_runtime?.Att is { } att ? SafeAttStatus(att) : AttStatus.NotApplicable;

        static AttStatus SafeAttStatus(IAttPlatform att)
        {
            try
            {
                return att.Status;
            }
            catch (Exception e)
            {
                s_runtime?.Log.Error("Reading ATT status failed", e);
                return AttStatus.NotApplicable;
            }
        }

        static ConsentSnapshot WithAtt(ConsentSnapshot consent)
        {
            var att = s_runtime?.Att;
            if (att is null) return consent;
            var status = SafeAttStatus(att);
            return consent.Att == status ? consent : consent with { Att = status };
        }

        // Áp lại consent hiện tại khi trạng thái ATT đổi (người dùng trả lời popup hoặc đổi trong Settings).
        static void RefreshAtt()
        {
            if (!s_consentSet) return;
            var current = s_consent.Value;
            if (!ReferenceEquals(WithAtt(current), current)) SetConsent(current);
        }

        static Task<SdkResult>? StartTracking(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry,
                                              NovaRuntime runtime, ISdkLogger log)
        {
            Task<SdkResult>? analyticsInit = null;
            if (registry.TrackingSinks.TryGetValue(TrackingSinkIds.Firebase, out var firebase))
                runtime.Sinks.Add(firebase(ctx));
            else
                log.Warning("Firebase Analytics adapter is not installed");

            if (setup.AdjustSettings != null)
            {
                if (registry.TrackingSinks.TryGetValue(TrackingSinkIds.Adjust, out var adjust)) runtime.Sinks.Add(adjust(ctx));
                else log.Warning("Adjust Config is assigned but the Adjust adapter is not installed (NOVA_ADJUST)");
            }

            NovaAttribution.SetLogger(ctx.Logs.Create("attribution"));
            foreach (var sink in runtime.Sinks)
            {
                if (sink is IAttributionSink attribution) attribution.SetListener(NovaAttribution.Listener);
                // Chỉ áp consent game đã truyền: Adjust coi consent đầu tiên là tín hiệu để khởi động.
                if (s_consentSet && !runtime.GameConsentWaitingForAtt) sink.Apply(s_consent.Value);
            }
            NovaAnalytics.Router.Attach(runtime.Sinks, ctx.Logs.Create("tracking"));

            foreach (var sink in runtime.Sinks)
            {
                var init = sink.InitializeAsync(CancellationToken.None);
                _ = NovaAnalytics.Router.WatchInitialization(sink, init);
                // Adjust chờ consent nên không chặn Ready; Firebase Analytics có timeout riêng.
                if (sink.Id == TrackingSinkIds.Firebase) analyticsInit = init;
            }
            return analyticsInit;
        }

        static void StartAds(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry, NovaRuntime runtime,
                             IApplicationLifecycle? lifecycle, INetworkStatus? network, ISdkLogger log)
        {
            if (setup.Ads.UsedProviders.Count == 0)
            {
                log.Info("Ads disabled: no ad format has a mediation with an ad unit ID");
                return;
            }

            // Format nào chạy mediation nào, test ID hay ID thật (test ID trong release đã được báo ở Settings).
            var summary = "Ads:";
            foreach (var unit in setup.Ads.AdUnits) summary += " " + unit.Format + "=" + unit.Provider;
            log.Info(summary + (setup.UsesGoogleTestIds ? " (AdMob: Google TEST ad unit IDs)" : " (real ad unit IDs)"));

            var adapter = registry.CreateAds(setup.Ads, ctx, out var missing);
            foreach (var provider in missing)
                log.Warning("Ads adapter for " + provider + " is not installed; its formats are disabled");

            runtime.Ads = new AdsManager(adapter, setup.Ads, ctx, new AdsDependencies(s_consent)
            {
                RemoteConfig = runtime.RemoteConfig,
                ConfigKeys = setup.AdsKeys,
                Entitlements = (IEntitlementProvider?)runtime.Iap ?? NoEntitlements.Instance,
                Revenue = NovaAnalytics.Router,
                Lifecycle = lifecycle,
                Network = network,
                IsIos = setup.IsIos,
                // Chờ ATT chỉ khi SDK tự hỏi lúc khởi động; game tự hỏi sau thì Ads không đợi.
                WaitForAtt = setup.IsIos && setup.RequestAttOnStartup && runtime.Att != null,
            });
            runtime.Ads.SetPresentationHandler(new PauseGameDuringAds(setup.PauseGameDuringAds));
            NovaAds.Bind(runtime.Ads, ctx.Logs.Create("ads.facade"), runtime.Iap);
            _ = LogAdsInitAsync(runtime.Ads, log);
        }

        static void StartCrashReporting(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry, NovaRuntime runtime,
                                        ISdkLogger log)
        {
            if (setup.CrashReporting is null) return;
            if (!registry.CrashReporters.TryGetValue(CrashReporterIds.FirebaseCrashlytics, out var factory))
            {
                log.Warning("Crashlytics is on but the adapter is not installed (com.google.firebase.crashlytics, NOVA_FIREBASE_CRASHLYTICS)");
                return;
            }
            try
            {
                runtime.CrashReporter = factory(ctx);
            }
            catch (Exception e)
            {
                log.Error("Creating the Crashlytics adapter failed", e);
                return;
            }
            var binding = new CrashReporterBinding(runtime, runtime.CrashReporter, setup.CrashReporting, ctx.Scheduler,
                ctx.Logs.Create("crash.facade"), log);
            runtime.CrashBinding = binding;
            _ = BindCrashReporterAsync(binding);
        }

        static readonly TimeSpan[] CrashRetryDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1) };

        sealed class CrashReporterBinding
        {
            public CrashReporterBinding(NovaRuntime runtime, ICrashReporter reporter, CrashReportingOptions options,
                                        IScheduler scheduler, ISdkLogger facadeLog, ISdkLogger log)
            {
                Runtime = runtime;
                Reporter = reporter;
                Options = options;
                Scheduler = scheduler;
                FacadeLog = facadeLog;
                Log = log;
            }

            public readonly NovaRuntime Runtime;
            public readonly ICrashReporter Reporter;
            public readonly CrashReportingOptions Options;
            public readonly IScheduler Scheduler;
            public readonly ISdkLogger FacadeLog;
            public readonly ISdkLogger Log;
            public int Attempts;
            public IDisposable? RetryTimer;
        }

        static async Task BindCrashReporterAsync(CrashReporterBinding binding)
        {
            try
            {
                binding.Attempts++;
                var result = await binding.Reporter.InitializeAsync(binding.Options, CancellationToken.None);
                // SDK có thể đã Shutdown trong lúc chờ: không gắn reporter của lần chạy cũ.
                if (!ReferenceEquals(s_runtime, binding.Runtime) || binding.Runtime.CrashBinding != binding) return;
                if (result.IsSuccess)
                {
                    binding.RetryTimer?.Dispose();
                    binding.RetryTimer = null;
                    NovaCrash.Bind(binding.Reporter, binding.FacadeLog);
                }
                else
                {
                    binding.Log.Warning("Crashlytics not ready: " + result.Error);
                    if (result.Error?.IsRetryable == true) ScheduleCrashRetry(binding);
                }
            }
            catch (Exception e)
            {
                binding.Log.Error("Crashlytics initialization failed unexpectedly", e);
                if (ReferenceEquals(s_runtime, binding.Runtime) && binding.Runtime.CrashBinding == binding)
                    ScheduleCrashRetry(binding);
            }
        }

        static void ScheduleCrashRetry(CrashReporterBinding binding)
        {
            var delay = CrashRetryDelays[Math.Min(binding.Attempts - 1, CrashRetryDelays.Length - 1)];
            binding.RetryTimer?.Dispose();
            binding.RetryTimer = binding.Scheduler.Schedule(delay, () =>
            {
                binding.RetryTimer = null;
                if (!ReferenceEquals(s_runtime, binding.Runtime) || binding.Runtime.CrashBinding != binding) return;
                binding.Log.Info("Retrying Crashlytics initialization (attempt " + (binding.Attempts + 1) + ")");
                _ = BindCrashReporterAsync(binding);
            });
        }

        static void StartIap(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry, NovaRuntime runtime, ISdkLogger log)
        {
            if (!setup.Iap.IsEnabled) return;

            IStoreAdapter? store = null;
            if (setup.Iap.UseTestStore)
            {
                store = new TestStoreAdapter(ctx.Main);
                log.Warning("IAP uses the TEST STORE: every purchase succeeds, no real payment (IAP Config > Test Store)");
            }
            else if (registry.StoreAdapters.TryGetValue(StoreAdapterIds.UnityIap, out var factory))
            {
                try
                {
                    store = factory(ctx);
                }
                catch (Exception e)
                {
                    log.Error("Creating the Unity IAP adapter failed: IAP disabled", e);
                }
            }
            else
            {
                log.Error("IAP Config is assigned but the Unity IAP adapter is not installed (com.unity.purchasing 5.x, NOVA_IAP)");
            }

            runtime.Iap = new IapService(store, setup.Iap, ctx, new IapDependencies
            {
                Revenue = NovaAnalytics.Router,
                SuppressAppOpen = NovaAds.SuppressAppOpen,
            });
            NovaIap.Bind(runtime.Iap, ctx.Logs.Create("iap.facade"));
            _ = LogIapInitAsync(runtime.Iap, log);
        }

        static void StartNotifications(NovaSdkSetup setup, ModuleContext ctx, AdapterRegistrySnapshot registry, NovaRuntime runtime,
                                       IApplicationLifecycle? lifecycle, ISdkLogger log)
        {
            if (setup.Notifications is null) return;

            INotificationPlatform? platform = null;
            if (setup.IsEditor)
            {
                platform = new LogOnlyNotificationPlatform(ctx.Logs.Create("notifications"));
            }
            else if (registry.NotificationPlatforms.TryGetValue(NotificationPlatformIds.Unity, out var factory))
            {
                try
                {
                    platform = factory(ctx);
                }
                catch (Exception e)
                {
                    log.Error("Creating the notification adapter failed: notifications disabled", e);
                }
            }
            else
            {
                log.Error("Notification Config is assigned but the adapter is not installed (com.unity.mobile.notifications, NOVA_NOTIFICATIONS)");
            }

            runtime.Notifications = new NotificationService(platform, setup.Notifications, ctx, new NotificationDependencies
            {
                Lifecycle = lifecycle,
                SuppressAppOpen = NovaAds.SuppressAppOpen,
                LogOpened = (eventName, opened) => NovaAnalytics.LogEvent(eventName,
                    ("notification_id", opened.Id), ("cold_start", opened.IsColdStart)),
            });
            runtime.Notifications.Initialize();
            NovaNotifications.Bind(runtime.Notifications, ctx.Logs.Create("notifications.facade"));
        }

        static async Task LogIapInitAsync(IapService iap, ISdkLogger log)
        {
            var result = await iap.InitializeAsync(CancellationToken.None);
            if (!result.IsSuccess) log.Warning("IAP not ready yet: " + result.Error);
        }

        static async Task LogAdsInitAsync(AdsManager ads, ISdkLogger log)
        {
            var result = await ads.InitializeAsync(CancellationToken.None);
            if (result.IsSuccess) log.Info("Ads ready");
            else log.Warning("Ads not ready yet: " + result.Error);
        }

        static TaskCompletionSource<bool> NewReady() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            s_state = NovaSdkState.NotInitialized;
            s_initialization = null;
            s_ready = NewReady();
            s_consent = new SdkProperty<ConsentSnapshot>(ConsentSnapshot.Unknown);
            s_consentSet = false;
            s_runtime = null;
            NovaAds.Reset();
            NovaAnalytics.Reset();
            NovaRemoteConfig.Reset();
            NovaAttribution.Reset();
            NovaPrivacy.Reset();
            NovaIap.Reset();
            NovaNotifications.Reset();
            NovaCrash.Reset();
        }

        // Các module đã tạo trong một lần khởi tạo.
        sealed class NovaRuntime : IDisposable
        {
            readonly Component? _host;
            readonly ISdkLogger _log;

            public NovaRuntime(Component? host, ISdkLogger log)
            {
                _host = host;
                _log = log;
            }

            public RemoteConfigService? RemoteConfig;
            public readonly List<ITrackingSink> Sinks = new List<ITrackingSink>();
            public AdsManager? Ads;
            public IConsentPlatform? ConsentPlatform;
            public IapService? Iap;
            public NotificationService? Notifications;
            public ICrashReporter? CrashReporter;
            public CrashReporterBinding? CrashBinding;
            public ConsentGather? ConsentGather;
            public IAttPlatform? Att;
            public bool AttAllowed;
            public bool RequestAttAfterGameConsent;
            public bool GameConsentWaitingForAtt;
            public Task<AttStatus>? AttRequest;
            public readonly List<IDisposable> Subscriptions = new List<IDisposable>();

            public ISdkLogger Log => _log;

            public IEnumerable<string> SinkIds()
            {
                foreach (var sink in Sinks) yield return sink.Id + (sink.IsReady ? "" : " (pending)");
            }

            public void ApplyConsent(ConsentSnapshot consent)
            {
                foreach (var sink in Sinks)
                {
                    try
                    {
                        sink.Apply(consent);
                    }
                    catch (Exception e)
                    {
                        _log.Error(sink.Id + " threw while applying consent", e);
                    }
                }
            }

            public void Dispose()
            {
                foreach (var subscription in Subscriptions) subscription.Dispose();
                ConsentGather?.RetryTimer?.Dispose();
                ConsentPlatform?.Dispose();
                if (Ads != null) NovaAds.Unbind(Ads);
                Ads?.Dispose();
                if (Iap != null) NovaIap.Unbind(Iap);
                Iap?.Dispose();
                if (Notifications != null) NovaNotifications.Unbind(Notifications);
                Notifications?.Dispose();
                CrashBinding?.RetryTimer?.Dispose();
                if (CrashReporter != null) NovaCrash.Unbind(CrashReporter);
                CrashReporter?.Dispose();
                foreach (var sink in Sinks) sink.Dispose();
                RemoteConfig?.Dispose();
                if (_host != null) UnityEngine.Object.Destroy(_host.gameObject);
            }
        }

        // Báo NovaAds.IsShowingFullScreen; dừng game nếu bật pauseGameDuringFullScreenAds.
        sealed class PauseGameDuringAds : IAdPresentationHandler
        {
            readonly bool _pause;
            float _timeScale = 1f;
            bool _audioPaused;

            public PauseGameDuringAds(bool pause) { _pause = pause; }

            public void OnFullScreenShowing(AdFormat format)
            {
                NovaAds.IsShowingFullScreen = true;
                if (!_pause) return;
                _timeScale = Time.timeScale;
                _audioPaused = AudioListener.pause;
                Time.timeScale = 0f;
                AudioListener.pause = true;
            }

            public void OnFullScreenClosed(AdFormat format)
            {
                NovaAds.IsShowingFullScreen = false;
                if (!_pause) return;
                // Khôi phục đúng trạng thái trước ad (vd. game đang ở menu pause đã tắt âm thì giữ nguyên).
                Time.timeScale = _timeScale;
                AudioListener.pause = _audioPaused;
            }
        }
    }
}
