# NovaGames Mobile SDK — Tổng quan dự án

Tài liệu cho người đọc/sửa code SDK: kiến trúc, cấu trúc thư mục, assembly và chức năng từng script. Cách dùng SDK trong game: xem `Guide.md`.

## 1. SDK làm gì

SDK Unity (Unity 6) gom các dịch vụ mobile mà game nào cũng cần sau một API tĩnh duy nhất (`Nova*`):

| Mảng | Vendor | Facade cho game |
|---|---|---|
| Khởi tạo, cấu hình | — | `NovaSdk`, `NovaSdkSettings` |
| Quảng cáo | AppLovin MAX 8.x, Google Mobile Ads 11.x (dùng một hoặc cả hai) | `NovaAds` |
| Remote Config | Firebase Remote Config | `NovaRemoteConfig` |
| Analytics | Firebase Analytics (+ Adjust cho event có token) | `NovaAnalytics` |
| Attribution, deep link | Adjust 5.x | `NovaAttribution` |
| Crash | Firebase Crashlytics | `NovaCrash` |
| Consent, ATT | Google UMP, Apple ATT | `NovaPrivacy` |
| Mua hàng | Unity IAP 5 | `NovaIap` |
| Thông báo local | Unity Mobile Notifications 2.x | `NovaNotifications` |
| Popup mất mạng | — | `NovaNoInternet` |
| Đánh giá app | Google Play In-App Review, iOS StoreKit | `NovaRating` |

## 2. Kiến trúc

```
Game code
   │  chỉ gọi Nova* (static, không throw, gọi trước init vẫn an toàn)
   ▼
Facade (NovaGames.Mobile.Facade)      ← NovaSdk dựng mọi module theo NovaSdkSettings
   │
   ▼
Core (NovaGames.Mobile.Core)          ← logic nghiệp vụ trung lập vendor: AdsManager, RemoteConfigService,
   │                                     IapService, NotificationService, policy, contract/SPI
   │  SPI (IAdsAdapter, ITrackingSink, IRemoteConfigSource, IStoreAdapter, IConsentPlatform, ...)
   ▼
Adapter (mỗi vendor một assembly)     ← chỉ dịch qua lại API vendor, tự đăng ký vào AdapterRegistry
   ▼
Vendor SDK (MAX, GMA, Firebase, Adjust, Unity IAP, ...)
```

Nguyên tắc chính:

- **Core không biết vendor.** Core chỉ chứa contract/SPI và service trung lập. Logic chỉ một vendor cần (vd. bid floor cascade của MAX) nằm trong adapter của vendor đó.
- **Adapter tự đăng ký.** Mỗi adapter có hàm `[RuntimeInitializeOnLoadMethod(AfterAssembliesLoaded)]` gọi `AdapterRegistry.RegisterXxx(id, factory)`. `NovaSdk` lấy `AdapterRegistry.Snapshot()` và chỉ tạo adapter cho module đang bật. Registry reset ở `SubsystemRegistration` nên vẫn đúng khi tắt Domain Reload.
- **Assembly adapter có define constraint** (`NOVA_MAX`, `NOVA_FIREBASE_APP`, …). Chưa cài vendor → assembly không compile → module tắt, game vẫn chạy.
- **Kết quả, không exception.** API async trả `SdkResult` / `SdkResult<T>`; lỗi là `SdkError(Code, Category, Message, IsRetryable, Provider, Exception)`. Exception của vendor bị bắt tại biên adapter.
- **Exactly-once với callback vendor.** Mọi callback vendor đi qua `VendorOperation<T>` hoặc `VendorTask.ObserveAsync`: callback, timeout hay cancel — cái nào đến trước thì thắng, kết quả luôn trả về main thread.
- **Hạ tầng tiêm qua `ModuleContext`:** `IMainThreadDispatcher`, `IClock`, `IScheduler`, `IKeyValueStore`, `ISdkLoggerFactory`. Module không dùng trực tiếp `DateTime.UtcNow`, `Task.Delay` hay `Time.*`, nên test chạy deterministic bằng fake trong `Tests/Shared`.
- **Event:** `SdkEvent<T>` (handler lỗi chỉ bị log, không làm vỡ SDK) và `SdkProperty<T>` (giữ giá trị hiện tại, subscribe nhận luôn giá trị đó).
- **Consent là một nguồn duy nhất:** `ConsentSnapshot` được đẩy tới mọi `IConsentApplier` (sink tracking, ads) trước khi init và mỗi khi đổi.
- **Tracking qua router:** `TrackingRouter` (trong `NovaAnalytics`) buffer tối đa 100 lệnh mỗi sink cho tới khi sink Ready, đồng thời là `IRevenuePipeline` cho Ads/IAP. Ads và IAP không gọi vendor tracking trực tiếp.
- **Storage:** PlayerPrefs qua `IKeyValueStore`, mọi key có prefix `novagames.mobile.v1.` (khai báo trong `StorageKeys`).
- **Log:** `[Nova][module]`, Development = Debug, Production = Warning; trong bản build chỉ ghi khi có define `NOVA_SDK_LOG`.

Thứ tự khởi tạo trong `NovaSdk.InitializeAsync`: Crashlytics → ATT (đọc trạng thái) → consent (UMP / Game / Assume) → IAP → Notifications → Remote Config nạp cache + tracking init song song → chờ Remote Config (có timeout) → Ads (nền) → chờ tracking → **Ready**. Lỗi của module nào cũng không chặn Ready.

## 3. Cấu trúc thư mục

```
NovaGames-SDK/                         repo + Unity project dùng để phát triển SDK
├── Packages/com.novagames.sdk/        package phát hành cho game (các thư mục module bên dưới)
├── Assets/NovaSdkSamples/Demo/        bản làm việc của sample: scene Demo, asset mẫu, RemoteKey, GameRemoteConfig
├── Assets/ (còn lại)                  vendor plugin + file Firebase để chạy thử SDK
└── ci/                                release-sdk.ps1 (phát hành phiên bản), run-editmode-tests.ps1

Packages/com.novagames.sdk/
├── package.json          Tên, phiên bản, dependency, khai báo sample
├── Samples~/Demo/        Bản copy sample lúc phát hành (Unity bỏ qua thư mục có dấu ~)
├── Core/                 Lõi trung lập vendor
│   ├── Runtime/          Contract, SPI, service (Ads, IAP, Notifications, Remote Config, Tracking, Privacy, ...)
│   └── Editor/           Tự set define vendor, menu log, property drawer
├── Facade/               API tĩnh Nova* cho game + NovaSdkSettings + build step/validator
├── Ads.Max/              Adapter AppLovin MAX (+ bid floor cascade)
├── Ads.AdMob/            Adapter Google Mobile Ads
├── Firebase/             Adapter Firebase: App, Analytics, Remote Config, Crashlytics + build step Consent Mode
├── Attribution/          Adapter Adjust (tracking sink + attribution/deep link)
├── Privacy/              Adapter Google UMP và Apple ATT (kèm plugin native iOS)
├── Iap/                  Adapter Unity IAP 5 + công cụ license key
├── Notifications/        Adapter Unity Mobile Notifications
├── NoInternet/           Popup mất mạng (prefab + MonoBehaviour)
├── Rating/               Popup đánh giá + Google Play In-App Review
├── Bootstrap/            Prefab NovaSdk: kéo vào scene đầu là khởi động SDK + popup
├── Tests/                Test EditMode (NUnit) + fake dùng chung
└── Docs/                 Guide.md, Overview.md, tài liệu PDF của AppLovin về bid floor
```

Đường dẫn file ở mục 4–5 tính từ `Packages/com.novagames.sdk/`, riêng phần Samples tính từ `Assets/NovaSdkSamples/Demo/`.

## 4. Assembly (asmdef)

| Assembly | Thư mục | Tham chiếu chính | Điều kiện compile |
|---|---|---|---|
| `NovaGames.Mobile.Core` | `Core/Runtime` | `Unity.Serialization` | luôn có |
| `NovaGames.Mobile.Core.Editor` | `Core/Editor` | Core | Editor |
| `NovaGames.Mobile.Facade` | `Facade/Runtime` | Core | luôn có (autoReferenced) |
| `NovaGames.Mobile.Facade.Editor` | `Facade/Editor` | Core, Facade, Bootstrap | Editor |
| `NovaGames.Mobile.Ads.Max` | `Ads.Max/Runtime` | Core, `MaxSdk.Scripts` | `NOVA_MAX` (`com.applovin.mediation.ads` ≥ 8.0.0) |
| `NovaGames.Mobile.Ads.AdMob` | `Ads.AdMob/Runtime` | Core, `GoogleMobileAds*.dll` | `NOVA_ADMOB` (`com.google.ads.mobile` ≥ 11.0.0) |
| `NovaGames.Mobile.Firebase` | `Firebase/Runtime/Core` | Core, Firebase.App | `NOVA_FIREBASE_APP` |
| `NovaGames.Mobile.Firebase.Analytics` | `Firebase/Runtime/Analytics` | Core, Firebase | `NOVA_FIREBASE_APP` + `NOVA_FIREBASE_ANALYTICS` |
| `NovaGames.Mobile.Firebase.RemoteConfig` | `Firebase/Runtime/RemoteConfig` | Core, Firebase | `NOVA_FIREBASE_APP` + `NOVA_FIREBASE_REMOTE_CONFIG` |
| `NovaGames.Mobile.Firebase.Crashlytics` | `Firebase/Runtime/Crashlytics` | Core, Firebase | `NOVA_FIREBASE_APP` + `NOVA_FIREBASE_CRASHLYTICS` |
| `NovaGames.Mobile.Firebase.Editor` | `Firebase/Editor` | — | Editor + `NOVA_FIREBASE_ANALYTICS` |
| `NovaGames.Mobile.Attribution.Adjust` | `Attribution/Runtime/Adjust` | Core, `AdjustSdk.Scripts` | `NOVA_ADJUST` (`com.adjust.sdk` ≥ 5.0.0) |
| `NovaGames.Mobile.Privacy.Ump` | `Privacy/Runtime/Ump` | Core, `GoogleMobileAds.Ump.dll` | `NOVA_UMP` |
| `NovaGames.Mobile.Privacy.Att` | `Privacy/Runtime/Att` | Core | Editor/iOS |
| `NovaGames.Mobile.Iap.UnityIap` | `Iap/Runtime/UnityIap` | Core, Unity.Purchasing | `NOVA_IAP` (`com.unity.purchasing` ≥ 5.0.0) |
| `NovaGames.Mobile.Iap.Editor` | `Iap/Editor` | Core | Editor |
| `NovaGames.Mobile.Notifications.Unity` | `Notifications/Runtime/Unity` | Core, Unity.Notifications | `NOVA_NOTIFICATIONS` (`com.unity.mobile.notifications` ≥ 2.0.0) |
| `NovaGames.Mobile.NoInternet` | `NoInternet/Runtime` | Core, Facade, UI | luôn có (autoReferenced) |
| `NovaGames.Mobile.Rating` | `Rating/Runtime` | Core, Facade, UI | luôn có (autoReferenced) |
| `NovaGames.Mobile.Rating.PlayReview` | `Rating/Runtime/PlayReview` | Rating, Google.Play.Review | `NOVA_PLAY_REVIEW`, Android/Editor |
| `NovaGames.Mobile.Bootstrap` | `Bootstrap/Runtime` | Core, Facade, NoInternet, Rating, UI, Unity.InputSystem (nếu có) | luôn có (autoReferenced) |
| `NovaGames.Mobile.Samples` | `Samples/Scripts` | Core, Facade, NoInternet, Rating | không autoReferenced |
| `NovaGames.Mobile.Samples.Editor` | `Samples/Editor` | Samples, Core | Editor |
| `NovaGames.Mobile.Testing` | `Tests/Shared` | Core | `UNITY_INCLUDE_TESTS` |
| `NovaGames.Mobile.*.Tests.Editor` | `Tests/<Module>/Editor` | module tương ứng + Testing + NUnit | `UNITY_INCLUDE_TESTS` + define của vendor |

`NOVA_MAX`, `NOVA_ADMOB`, `NOVA_ADJUST`, `NOVA_UMP`, `NOVA_PLAY_REVIEW` còn được `Core/Editor/VendorDefines.cs` tự set khi vendor được cài bằng `.unitypackage`.

## 5. Chức năng từng script

### Core/Runtime — gốc, Bootstrap, Common

| File | Chức năng |
|---|---|
| `AssemblyInfo.cs` | `InternalsVisibleTo` cho các assembly test. |
| `Bootstrap/AdapterRegistry.cs` | Registry tĩnh các factory adapter (RemoteConfig, TrackingSink, Ads, Consent, Store, Notifications, CrashReporter, ATT); `AdapterRegistrySnapshot` bất biến, `CreateAds` chọn một adapter hoặc `RoutingAdsAdapter`. |
| `Bootstrap/ModuleContext.cs` | `RuntimeSdkSettings` (Dev/Prod, log level, options từng module, sink settings) và `ModuleContext` (gói hạ tầng Main/Clock/Scheduler/Store/Logs; `CreateDefault` dựng bản Unity). |
| `Common/IsExternalInit.cs` | Polyfill để dùng `record`/`init` trên .NET Standard 2.1. |
| `Common/Async/VendorOperation.cs` | `VendorOperation<T>` (hoàn tất đúng một lần: callback/timeout/cancel, trả về main thread), `VendorTask` (bọc Task vendor, map lỗi), `SdkTasks` (chờ task dùng chung có cancel/timeout). |
| `Common/Events/SdkEvents.cs` | `SdkEvent<T>` (handler copy-on-write, mỗi handler bọc try/catch) và `SdkProperty<T>` (giá trị hiện tại + sự kiện đổi). |
| `Common/Results/SdkError.cs` | `SdkErrorCategory` và record `SdkError` với các factory Timeout/Cancelled/Disposed/NotInitialized/FromException. |
| `Common/Results/SdkResult.cs` | Struct `SdkResult` và `SdkResult<T>` (Ok hoặc Error). |

### Core/Runtime/Ads

| File | Chức năng |
|---|---|
| `AdFormat.cs` | Enum `AdFormat`, flags `AdCapability`, extension `IsFullScreen`/`ToCapability`. |
| `AdPlacements.cs` | Placement có kiểu: `InterstitialPlacement`, `RewardedPlacement`, `AppOpenPlacement`, `BannerPlacement`, `MrecPlacement`. |
| `AdsAdapterSpi.cs` | SPI giữa `AdsManager` và adapter: `IAdsAdapter`, `IAdsAdapterListener`, `AdUnit`, `AdLoadError`, `AdPaidValue`, `AdViewRequest`, `AdsAdapterInitOptions`, `AdProviderIds`. |
| `AdsCapping.cs` | (internal) Lưu bền trạng thái capping interstitial (khoảng cách, số lần/ngày); đếm số lần/phiên trong bộ nhớ. |
| `AdsConfig.cs` | `AdsConfig` (ScriptableObject nền của asset ad unit), `PlatformAdUnitId`, `AdsConfigOptions`, `GoogleAdUnitIds` (test ID của Google, nhận diện ID AdMob). |
| `AdMobAdsConfig.cs` | Asset *Ads Config (AdMob)*: ID 5 format, vị trí banner, Use Google Test Ids, test device; validate ID AdMob. |
| `AdsMediation.cs` | `AdsMediationSelection` (provider cho từng format) và `AdsMediation.ToOptions/Validate` ghép asset MAX + AdMob thành `AdsOptions`. |
| `AdsOptions.cs` | `AdsProvider`, `AdPlacementBinding`, `AdsProviderOptions`, `AdsOptions` (unit, binding, timeout, TTL, `Validate`). |
| `AdsPolicy.cs` | `AdsPolicy` (policy đã kẹp giới hạn an toàn), `AdsConfigKeyNames` (tên key `ad_*`), `AdsConfigKeys`, `IAdsConfigKeysSource`. |
| `AdsResults.cs` | Kết quả show (`InterstitialResult`, `RewardedResult`, `AppOpenResult`), `AdReward`, sự kiện `AdAvailabilityChanged`, `FullScreenAdPresentation`, `AdImpression`. |
| `AdsServices.cs` | Interface public `IAdsService`, `IFullScreenAds`, `IBannerAds`, `IMrecAds`, `IAppOpenAds`, `IAdPresentationHandler`. |
| `AdsStates.cs` | Enum `AdsModuleState`, `AdAvailability`, `ShowOutcome`. |
| `AdViewTypes.cs` | Kiểu cho banner/MREC: vị trí, kích thước, `CollapsiblePolicy`, layout, `AdViewState`. |
| `AdUnitSlot.cs` | (internal) State machine một unit full-screen: load, retry backoff có jitter, load timeout, TTL. |
| `AdsManager.cs` | `AdsDependencies` và phần lõi `AdsManager` (field, constructor, `SetPlayerLevel`, `OpenDebugger`, `Dispose`). |
| `AdsManager.Lifecycle.cs` | Init adapter (timeout + retry), gate consent/ATT/force update, áp policy Remote Config, kill switch, remove_ads, mạng, pause/resume. |
| `AdsManager.FullScreen.cs` | Availability, preload, show (một full-screen tại một thời điểm), display timeout, reward grace, presentation. |
| `AdsManager.AdViews.cs` | Banner/MREC: show/hide/destroy, giữ yêu cầu khi chưa Ready, retry, collapsible interval, layout. |
| `AdsManager.AppOpen.cs` | `Suppress` (chặn app open tạm thời) và `SetAutoShowOnResume`. |
| `AdsManager.Callbacks.cs` | Chuyển callback adapter về main thread; loaded/failed; revenue (chống trùng, gửi `AdRevenueEvent`). |
| `RoutingAdsAdapter.cs` | Dùng cả MAX và AdMob trong một build: chuyển từng unit tới adapter của provider phục vụ nó, init song song. |

### Core/Runtime — các module khác

| File | Chức năng |
|---|---|
| `Connectivity/OfflineDetector.cs` | Chống nháy trạng thái mạng (chỉ đổi offline/online sau một khoảng liên tục). |
| `Diagnostics/CrashReportingContracts.cs` | `CrashReporterIds`, `CrashReportingOptions`, SPI `ICrashReporter`. |
| `Entitlements/EntitlementContracts.cs` | `EntitlementId` (`RemoveAds`), `IEntitlementProvider`, `NoEntitlements`. |
| `Iap/IapConfig.cs` | Asset *IAP Config* + `IapProductConfig`: danh sách sản phẩm, cờ receipt/test store; `ToOptions`, `Validate`. |
| `Iap/GooglePlayLicense.cs` | Giữ license key Google Play (đã obfuscate) do file sinh trong Assets của game đăng ký; adapter Unity IAP đọc để kiểm tra receipt. |
| `Iap/IapContracts.cs` | `IapProductType`, `IapProductDefinition`, `IapProductInfo`, `PurchaseStatus`, `PurchaseResult`, `IapGrant`, `IIapService`. |
| `Iap/IapJournal.cs` | (internal) Lưu TransactionId đã trao (tối đa 300) và sản phẩm đang sở hữu (kèm hạn). |
| `Iap/IapOptions.cs` | `IapOptions` (sản phẩm, timeout, validate, test store) và `IapDependencies`. |
| `Iap/IapService.cs` | Điều phối init, purchase (một giao dịch một lúc), restore; pipeline validate → trao → journal → confirm → revenue → `Purchased`; cung cấp entitlement. |
| `Iap/IapStoreSpi.cs` | SPI store: `IStoreAdapter`, `IStoreListener`, `StoreProduct`, `StoreTransaction`, `IReceiptValidator`, `StoreAdapterIds`. |
| `Iap/TestStoreAdapter.cs` | Store giả lập cho Editor/Development: mua thành công ở frame sau. |
| `Infrastructure/Lifecycle/ApplicationLifecycle.cs` | `IApplicationLifecycle`, `INetworkStatus`, `UnityNetworkStatus`, `ApplicationLifecycleHost` (MonoBehaviour ẩn phát PauseChanged). |
| `Infrastructure/Logging/DebugCustom.cs` | Helper log toàn cục (global namespace), chỉ chạy trong Editor nhờ `[Conditional]`. |
| `Infrastructure/Logging/Logging.cs` | `SdkLogLevel`, `ISdkLogger`/`ISdkLoggerFactory`, extension `TryRun`, `UnitySdkLoggerFactory` (prefix `[Nova][module]`). |
| `Infrastructure/Storage/KeyValueStore.cs` | `IKeyValueStore`, `StorageKeys` (prefix `novagames.mobile.v1.`), `PlayerPrefsKeyValueStore`. |
| `Infrastructure/Threading/Threading.cs` | Interface `IMainThreadDispatcher`, `IClock`, `IScheduler`. |
| `Infrastructure/Threading/UnityThreading.cs` | `SystemClock`, `SynchronizationContextDispatcher`, `TimerScheduler` (timer thread pool, post về main thread). |
| `Notifications/LogOnlyNotificationPlatform.cs` | Platform giả cho Editor: chỉ ghi log. |
| `Notifications/NotificationConfig.cs` | Asset *Notification Config* + `ReminderMessageConfig`; `ToOptions`, `Validate`. |
| `Notifications/NotificationContracts.cs` | `NotificationPermission`, `NotificationOpened`, `ScheduledNotification`, SPI `INotificationPlatform`, `INotificationService`. |
| `Notifications/NotificationOptions.cs` | `ReminderOptions`, `NotificationOptions`, `NotificationDependencies`. |
| `Notifications/NotificationService.cs` | Lên lịch theo id chữ (hash sang id số), giới hạn pending, nhắc chơi tự lên lịch lại, phát hiện mở game từ thông báo, quyền. |
| `Privacy/AttPlatform.cs` | SPI `IAttPlatform` + `AttPlatformIds`. |
| `Privacy/Consent.cs` | `ConsentState`, `AttStatus`, `Jurisdiction`, record `ConsentSnapshot`, `IConsentApplier`. |
| `Privacy/ConsentPlatform.cs` | `ConsentDebugGeography`, `ConsentGatherOptions`, SPI `IConsentPlatform`, `ConsentPlatformIds`. |
| `Rating/RatingPolicy.cs` | `RatingOptions`, `RatingState`, `RatingPolicy.Check` (điều kiện hiện popup). |
| `RemoteConfig/ConfigKeys.cs` | `IConfigValues`, `ConfigKey<T>` có validate min/max, `ConfigParsing`. |
| `RemoteConfig/RemoteConfigCache.cs` | (internal) Cache giá trị hợp lệ gần nhất (JSON + schemaVersion). |
| `RemoteConfig/RemoteConfigContracts.cs` | `ConfigSource`, `RemoteConfigSnapshot`, `IRemoteConfigService`, SPI `IRemoteConfigSource`, `RemoteConfigOptions`. |
| `RemoteConfig/RemoteConfigDefinitions.cs` | `RemoteDefaultAttribute`, `ConfigEntry<TKey>`, ScriptableObject `RemoteConfigDefinitions<TKey>` (enum làm key), extension `GetInt/...`. |
| `RemoteConfig/RemoteConfigService.cs` | Default → cache → fetch (single-flight) → validate → activate nguyên khối. |
| `Tracking/AttributionContracts.cs` | `AttributionData`, `DeepLink`, `IAttributionListener`, `IAttributionSink`. |
| `Tracking/TrackingContracts.cs` | `TrackingEvent`, `TrackingParam`, `AdRevenueEvent`, `PurchaseRevenueEvent`, `SinkCapabilities`, SPI `ITrackingSink`, `IRevenuePipeline`, `TrackingSinkIds`, `AnalyticsOptions`. |
| `Tracking/TrackingSinkConfig.cs` | ScriptableObject nền cho asset cấu hình sink (vd. Adjust). |

### Core/Editor

| File | Chức năng |
|---|---|
| `VendorDefines.cs` | `[InitializeOnLoad]` + menu *Refresh Vendor Defines*: dò assembly vendor để set/gỡ `NOVA_MAX`, `NOVA_ADMOB`, `NOVA_ADJUST`, `NOVA_UMP`, `NOVA_PLAY_REVIEW`. |
| `SdkLogDefine.cs` | Menu *Show SDK Logs In Build*: bật/tắt define `NOVA_SDK_LOG`. |
| `Ads/PlatformAdUnitIdDrawer.cs` | PropertyDrawer cho `PlatformAdUnitId` (2 dòng Android/iOS). |

### Facade

| File | Chức năng |
|---|---|
| `Runtime/AssemblyInfo.cs` | `InternalsVisibleTo` cho assembly test và editor. |
| `Runtime/NovaSdk.cs` | Điểm vào SDK: `InitializeAsync` dựng mọi module theo thứ tự ở mục 2; `SetConsent`, `WhenReady`, `Shutdown`; retry consent/Crashlytics; dừng game khi ad full-screen hiện. |
| `Runtime/NovaSdkSettings.cs` | Asset cấu hình duy nhất, enum `NovaConsentSource`, `NovaSdkSetup` (cấu hình đã resolve theo platform), `Validate`. |
| `Runtime/NovaAds.cs` | Facade Ads: interstitial, rewarded, app open, banner, MREC, `SuppressAppOpen`; giữ lệnh gọi trước khi Ads sẵn sàng. |
| `Runtime/NovaAnalytics.cs` | Facade tracking + `TrackingRouter` (buffer theo sink, `IRevenuePipeline`). |
| `Runtime/NovaAttribution.cs` | Attribution và deep link; giữ deep link đến sớm cho handler đầu tiên. |
| `Runtime/NovaCrash.cs` | Facade Crashlytics: breadcrumb, non-fatal, custom key, user id, test crash. |
| `Runtime/NovaIap.cs` | Facade IAP: mua, restore, giá, sở hữu, entitlement, handler consumable, validator receipt. |
| `Runtime/NovaNoInternet.cs` | Cờ bật/tắt, trạng thái popup mất mạng, giả lập offline. |
| `Runtime/NovaNotifications.cs` | Facade thông báo local: lên lịch, hủy, xin quyền, sự kiện `Opened`. |
| `Runtime/NovaPrivacy.cs` | Consent UMP (trạng thái, form Privacy options) và ATT iOS. |
| `Runtime/NovaRemoteConfig.cs` | Đọc Remote Config theo enum key, `FetchAsync`, sự kiện `Updated`; `TryGet` (internal) đọc theo tên key cho prefab NovaSdk. |
| `Editor/AttBuildStep.cs` | Post-build iOS: link `AppTrackingTransparency.framework`, ghi `NSUserTrackingUsageDescription`. |
| `Editor/NovaSettingsLocator.cs` | Tìm asset `NovaSdkSettings` cho bước build (ưu tiên asset của game hơn asset của sample). |
| `Editor/NovaSetupWindow.cs` | Menu *NovaGames > Setup*: checklist tích hợp, nút tạo NovaSdkSettings / asset config / script RemoteKey, thêm prefab NovaSdk vào scene; tự mở một lần khi project chưa có NovaSdkSettings. |
| `Editor/ReleaseBuildValidator.cs` | Kiểm tra trước khi build Android/iOS, chặn bản release cấu hình sai; menu *Check Release Build*. |

### Ads.Max, Ads.AdMob

| File | Chức năng |
|---|---|
| `Ads.Max/Runtime/AssemblyInfo.cs` | `InternalsVisibleTo` cho test Ads.Max. |
| `Ads.Max/Runtime/IsExternalInit.cs` | Polyfill `record`/`init` cho assembly này. |
| `Ads.Max/Runtime/MaxAdsAdapter.cs` | Adapter MAX: consent, selective init, tham số bid floor, full-screen/banner/MREC, chuẩn hóa callback, revenue; `MaxRegistration` đăng ký id `max`. |
| `Ads.Max/Runtime/MaxAdsConfig.cs` | Asset *Ads Config (MAX)*: ID 5 format, banner, unit floor, timeout tier, test device; validate; sinh `MaxAdsSettings`. |
| `Ads.Max/Runtime/MaxFloorSettings.cs` | `MaxFloorCascadeSettings`, `MaxAdsSettings`, `MaxFloorKeys` (cờ Remote Config), `MaxFloorPlan` (tính selective init, B2B, auto-retry). |
| `Ads.Max/Runtime/MaxFloorCascade.cs` | `IMaxFullScreenApi` và state machine HIGH → MEDIUM → MAIN cho một unit main. |
| `Ads.AdMob/Runtime/AdMobAdsAdapter.cs` | Adapter Google Mobile Ads: RequestConfiguration, full-screen dùng một lần, banner/MREC, collapsible banner, revenue micros → USD, Ad Inspector; `AdMobRegistration` đăng ký id `admob`. |

### Firebase

| File | Chức năng |
|---|---|
| `Editor/ConsentModeDefaultsBuildStep.cs` | Ghi mặc định Consent Mode v2 "denied" vào AndroidManifest và Info.plist (giữ key game đã khai báo). |
| `Runtime/Core/AssemblyInfo.cs` | `InternalsVisibleTo` cho assembly Firebase con và test. |
| `Runtime/Core/FirebaseAppInitializer.cs` | `CheckAndFixDependenciesAsync` dùng chung cả process, có timeout, không cache kết quả lỗi. |
| `Runtime/Analytics/AssemblyInfo.cs` | `InternalsVisibleTo` cho test. |
| `Runtime/Analytics/FirebaseAnalyticsApi.cs` | Wrapper `FirebaseAnalytics` tĩnh để test thay bằng fake. |
| `Runtime/Analytics/FirebaseAnalyticsRules.cs` | Luật tên event/param/user property, tên reserved, `ad_format`; `FirebaseConsentMapper` (ConsentSnapshot → Consent Mode v2). |
| `Runtime/Analytics/FirebaseAnalyticsSink.cs` | Tracking sink Firebase: event, `ad_impression`, purchase iOS, user property/id, consent; đăng ký id `firebase`. |
| `Runtime/Crashlytics/FirebaseCrashlyticsReporter.cs` | `ICrashReporter` cho Crashlytics: breadcrumb, non-fatal, custom key, user id, bật/tắt thu thập. |
| `Runtime/RemoteConfig/AssemblyInfo.cs` | `InternalsVisibleTo` cho test. |
| `Runtime/RemoteConfig/FirebaseRemoteConfigApi.cs` | Wrapper `FirebaseRemoteConfig.DefaultInstance`, chỉ trả giá trị có nguồn remote. |
| `Runtime/RemoteConfig/FirebaseRemoteConfigSource.cs` | `IRemoteConfigSource`: init → fetch → activate chung một deadline, map lỗi throttled/network. |

### Attribution (Adjust)

| File | Chức năng |
|---|---|
| `Runtime/Adjust/AdjustApi.cs` | Wrapper `AdjustSdk.Adjust` + hook deep link của Unity. |
| `Runtime/Adjust/AdjustMapping.cs` | Map nguồn ad revenue, param, `AdjustAttribution` → `AttributionData`; `AdjustConsentState` (third-party sharing, Google DMA, measurement consent). |
| `Runtime/Adjust/AdjustSink.cs` | Tracking + attribution sink: chờ consent rồi mới `InitSdk`, event theo token, ad revenue, purchase, deep link; đăng ký id `adjust`. |
| `Runtime/Adjust/AdjustSinkSettings.cs` | Record cấu hình đã resolve theo platform/build. |
| `Runtime/Adjust/AdjustTrackingConfig.cs` | Asset *Adjust Config* (token, event token, tùy chọn SDK) + `AdjustConfigRules` (validate token). |
| `Runtime/Adjust/AssemblyInfo.cs` | `InternalsVisibleTo` cho test. |
| `Runtime/Adjust/IsExternalInit.cs` | Polyfill `record`/`init`. |

### Privacy, Iap, Notifications, NoInternet, Rating, Bootstrap

| File | Chức năng |
|---|---|
| `Privacy/Runtime/Ump/UmpApi.cs` | `IUmpApi` + `GoogleUmpApi` (bọc UMP) và `IabStorage` (đọc chuỗi IAB TCF/GPP). |
| `Privacy/Runtime/Ump/UmpConsentMapper.cs` | Hàm thuần: trạng thái UMP + chuỗi IAB → `ConsentSnapshot`. |
| `Privacy/Runtime/Ump/UmpConsentPlatform.cs` | `IConsentPlatform`: đọc consent đã lưu, gather với timeout 10 s, form privacy options. |
| `Privacy/Runtime/Ump/AssemblyInfo.cs`, `IsExternalInit.cs` | `InternalsVisibleTo` cho test; polyfill `record`/`init`. |
| `Privacy/Runtime/Att/AppleAttPlatform.cs` | `IAttPlatform` gọi plugin native; chỉ đăng ký trên iOS thật. |
| `Privacy/Runtime/Att/Plugins/iOS/NovaAtt.mm` | Native gọi `ATTrackingManager`, chờ app active rồi hỏi (thử lại tối đa 3 lần). |
| `Iap/Runtime/UnityIap/UnityIapStoreAdapter.cs` | Adapter Unity IAP 5 cho `IStoreAdapter` + `IReceiptValidator` (receipt Google Play bằng license key); đăng ký id `unity_iap`. |
| `Iap/Editor/GooglePlayLicenseKeyWindow.cs` | Cửa sổ nhập license key, sinh `Assets/NovaGames/Generated/NovaGooglePlayLicense.cs` trong game (file đó gọi `GooglePlayLicense.Register` lúc khởi động). |
| `Iap/Editor/LegacyIapSettingsImporter.cs` | Chuyển sản phẩm từ asset IapSettings cũ sang `IapConfig`. |
| `Notifications/Runtime/Unity/UnityNotificationPlatform.cs` | Adapter Mobile Notifications 2.x: channel, quyền, lên lịch, hủy, thông báo đã mở game; đăng ký id `unity_notifications`. |
| `NoInternet/Runtime/NoInternetPopup.cs` | Popup mất mạng: che màn hình, pause game, ẩn MREC, chặn app open, nút Settings/Retry. |
| `Rating/Runtime/NovaRating.cs` | Facade tĩnh `NovaRating` + interface `IInAppReviewProvider`. |
| `Rating/Runtime/RatingPopup.cs` | Popup 5 sao: đủ sao → store, ít sao → góp ý, Later/Never. |
| `Rating/Runtime/PlayReview/PlayInAppReviewProvider.cs` | Google Play In-App Review, tự gắn vào `NovaRating`. |
| `Bootstrap/Runtime/NovaSdkBootstrap.cs` | Component của prefab NovaSdk: gọi `NovaSdk.InitializeAsync`, tạo popup mất mạng + đánh giá, tạo EventSystem khi scene chưa có, áp Remote Config (`no_internet_popup_on`, `level_show_rate`) theo tên key; `DontDestroyOnLoad`, giữ một bản. |

Asset kèm module: `NoInternet/Prefabs/NoInternetPopup.prefab` (Canvas sortingOrder 30000), `Rating/Prefabs/RatingPopup.prefab` (sortingOrder 29000), `Rating/Prefabs/Star.png`, `Bootstrap/Prefabs/NovaSdk.prefab`.

### Samples

| File | Chức năng |
|---|---|
| `Scripts/RemoteKey.cs` | Enum key Remote Config mẫu (`[RemoteDefault]`) và `RemoteKeys.Ads` nối key Ads. |
| `Scripts/GameRemoteConfig.cs` | Asset Remote Config Definitions theo `RemoteKey`, cài `IAdsConfigKeysSource`. |
| `Scripts/SampleAds.cs` | Hằng số tên placement mẫu (`Placements`). |
| `Scripts/SdkDemoPanel.cs` | Panel demo: status, log, mỗi nút gọi một API `Nova*`. |
| `Editor/GameRemoteConfigContextMenu.cs` | Menu *Add Missing Enum Entries*: thêm dòng còn thiếu từ enum vào asset. |

Asset mẫu: `Scenes/Demo.unity` (khởi động bằng component `NovaSdkBootstrap` trên GameObject `NovaSdk`); `Data/NovaSdkSettings.asset` (consent Assume granted, ads chạy AdMob), `GameRemoteConfig.asset`, `MaxAdsConfig.asset`, `AdMobAdsConfig.asset`, `AdjustTrackingConfig.asset`, `IapConfig.asset` (remove_ads, gem_pack_1), `NotificationConfig.asset`.

### Tests

| File | Nội dung |
|---|---|
| `Shared/TestInfrastructure.cs` | Dispatcher/SynchronizationContext deterministic, FakeClock, scheduler theo clock, store và logger in-memory, harness tạo `ModuleContext`. |
| `Shared/AdsFakes.cs` | `FakeAdsAdapter`, `FakeLifecycle`, `FakeNetwork`, fake entitlement/revenue. |
| `Shared/CrashFakes.cs`, `IapFakes.cs`, `NotificationFakes.cs` | Fake crash reporter, store, notification platform. |
| `Core/Editor/AdsManagerTests.cs` | Init/gate, capping, rewarded exactly-once, load state machine, entitlement/consent/kill switch, banner/MREC, app open, revenue. |
| `Core/Editor/AdsMediationTests.cs` | Dùng cả MAX và AdMob qua `RoutingAdsAdapter`. |
| `Core/Editor/ConfigKeyTests.cs`, `RemoteConfigDefinitionsTests.cs`, `RemoteConfigServiceTests.cs` | Parse/validate key, asset definitions, cache/fetch/single-flight. |
| `Core/Editor/IapServiceTests.cs` | Purchase, replay, receipt, consumable handler, ownership, restore, revenue. |
| `Core/Editor/NotificationServiceTests.cs` | Schedule/cancel, reminder, payload, quyền, giới hạn. |
| `Core/Editor/OfflineDetectorTests.cs`, `RatingPolicyTests.cs`, `VendorOperationTests.cs` | Chống nháy mạng, điều kiện rating, exactly-once/timeout/cancel. |
| `Facade/Editor/NovaAdsTests.cs`, `NovaSdkTests.cs` | Facade Ads; tích hợp NovaSdk với fake (consent, IAP, notifications, Crashlytics, ATT, deep link, Shutdown). |
| `Ads.Max/Editor/MaxFloorCascadeTests.cs` | Cascade bid floor (tuần tự, timeout, fill trễ, show) và `MaxFloorPlan`. |
| `Firebase/Editor/*` | Luật tên Analytics, sink Analytics, Remote Config source, fake API Firebase. |
| `Attribution.Adjust/Editor/*` | `AdjustSink` (consent, DMA, COPPA, token, revenue, deep link) và config rules. |
| `Privacy.Ump/Editor/*` | Mapper consent UMP và luồng gather/privacy options. |

Chạy test trong project SDK: *Window > General > Test Runner > EditMode* (`Packages/manifest.json` khai báo `"testables": ["com.novagames.sdk"]`), hoặc `ci/run-editmode-tests.ps1` khi Unity đang đóng.

## 6. Phát hành phiên bản

1. Sửa code trong `Packages/com.novagames.sdk/` (sample sửa ở `Assets/NovaSdkSamples/Demo/`), chạy test, commit.
2. Chạy `./ci/release-sdk.ps1 -Version 1.0.1`: script ghi version vào `package.json`, copy sample sang `Samples~/Demo`, commit `SDK v1.0.1` và tạo tag `v1.0.1`.
3. `git push; git push origin v1.0.1`.
4. Game nào cần bản mới thì đổi tag trong `Packages/manifest.json` của game đó (`...#v1.0.1`); game khác giữ nguyên.

Quy ước: patch = sửa lỗi; minor = thêm tính năng, không phá API; major = đổi/xóa API (đánh `[Obsolete]` trước ít nhất một bản minor). Không bao giờ sửa lại một tag đã push — có lỗi thì ra bản mới.

## 7. Mở rộng SDK

**Thêm adapter vendor mới:**

1. Tạo assembly riêng, chỉ reference Core + vendor, đặt `defineConstraints` + `versionDefines` theo package vendor.
2. Cài SPI tương ứng trong Core (`IAdsAdapter`, `ITrackingSink`, `IRemoteConfigSource`, `IStoreAdapter`, `INotificationPlatform`, `ICrashReporter`, `IConsentPlatform`, `IAttPlatform`).
3. Đăng ký factory ở `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]` bằng `AdapterRegistry.RegisterXxx(id, ctx => ...)`.
4. Callback vendor đi qua `VendorOperation<T>`/`VendorTask`; dùng `ctx.Main`, `ctx.Scheduler`, `ctx.Clock`; bắt mọi exception tại biên.
5. Không giữ logic nghiệp vụ chung (cache, journal, capping thuộc service Core); logic chỉ vendor này cần thì để trong adapter.
6. Bọc API tĩnh của vendor sau interface nhỏ để viết test trong `Tests/<Module>/Editor`.

**Thêm API cho game:** thêm hàm tĩnh vào class `Nova*` tương ứng — tham số đơn giản (string/enum + callback), không throw, gọi trước init vẫn an toàn, có `///` XML doc tiếng Việt — rồi cập nhật `Guide.md`.

## 8. Việc còn lại

**Tracking**

- Chốt schema `ad_impression` với BI: `ad_platform` đang là `max`/`admob`, thiếu `placement`, `precision`, context game (`level`, `play_mode`).
- Tránh gửi trùng `ad_impression` khi app AdMob đã link AdMob ↔ Firebase (chọn Auto/Manual theo app).
- Consent gate theo mục đích cho sink không có Consent Mode (Adjust); chống trùng revenue theo `EventId`/`TransactionId` ở router.
- Funnel ads: sự kiện trung lập trong AdsManager + bridge sang tên event cấu hình được (`inters_call_show`, `inter_show_success/failed`, `rewarded_call_show`…).
- Adjust milestone: đếm interstitial lifetime → event `ad_inter_displayed_<N>` có token.
- Phân loại organic/paid theo Remote Config `attribution_paid_networks` (lưu kết quả đầu tiên) + segment cho Ads policy (key `<key>_paid`).
- User property vòng đời (`days_played`, `retent_type`); thời gian xem ad trong level; event kết quả ATT.
- Chốt taxonomy event với BI, helper tiền ảo `earn/spend_virtual_currency`.
- Remote Config real-time + QA override cho Development.

**Ads**

- Contract test `AdsAdapterContract`; capping theo placement.
- `AdMobAdsAdapter.InitializeAsync` giữ mãi task init đã lỗi nên không init lại được (MAX thì retry đúng theo SPI) — cần sửa.
- Xác minh trên thiết bị: TTL, collapsible banner, thứ tự reward/close; vận hành bid floor test trên MAX.
- v1.1: Native, Rewarded Interstitial, SSV.

**Khác**

- Force Update (hiện chỉ có hook chặn Ads).
- Báo cáo khởi tạo (module Degraded/Skipped), secure storage.
- Build: strip provider không dùng, kiểm Mobile Notifications settings, kiểm trùng Billing Library, tự upload IL2CPP symbols Crashlytics.
- IAP: cảnh báo giao dịch chưa confirm gần mốc 3 ngày của Google Play, promoted purchase iOS, thu hồi non-consumable khi hoàn tiền, server receipt validator.
- Rating: kill switch từ xa cho popup.
- Privacy: giải mã chuỗi GPP để có `UsDoNotSell` thật.
- Contract test cho từng SPI, test PlayMode/thiết bị, CI build (AAB 16 KB, iOS Archive).
- Thay ad unit test bằng ID production; review default Remote Config.
- Dọn API thừa: `DebugCustom` (nhiều hàm không dùng, kéo theo reference `Unity.Serialization`), `AdsOptions.OptionsFor`, `NovaSdkSetup.IsDevelopment`.
