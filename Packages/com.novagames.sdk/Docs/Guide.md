# NovaGames Mobile SDK — Hướng dẫn sử dụng

Tài liệu cho dev Unity tích hợp SDK vào game. Cấu trúc code và chức năng từng script: xem `Overview.md`.

## Bắt đầu nhanh — import xong làm gì?

Làm lần lượt từ trên xuống. Bước nào thuộc module game không dùng thì bỏ qua: SDK vẫn compile và chạy, module đó tự tắt.

**A. Bắt buộc (mọi game)**

1. Thêm package vào `Packages/manifest.json` (mục 2), mở Unity, chờ compile xong, không có lỗi đỏ.
2. Mở **NovaGames > Setup** (lần đầu tự mở). Cửa sổ này là checklist: dòng nào có dấu cảnh báo thì bấm nút bên cạnh.
3. Bấm **Tạo** ở dòng *NovaSdkSettings* → được `Assets/NovaGames/NovaSdkSettings.asset`.
4. Mở scene đầu tiên của game, bấm **Thêm vào scene** → prefab `NovaSdk` được thêm và gán sẵn settings. (Làm tay: kéo `Packages/NovaGames Mobile SDK/Bootstrap/Prefabs/NovaSdk` vào scene, kéo `NovaSdkSettings` vào ô *Settings*.)
5. Play. Console có log `[Nova]` và không có lỗi `Settings:` là xong. Prefab đã lo: khởi tạo SDK, popup mất mạng, popup đánh giá, EventSystem. Mọi API `Nova*` gọi được ở bất kỳ scene nào.

**B. Chọn module cần dùng → cài vendor → điền config**

Phần *Module* trong cửa sổ Setup cho biết vendor nào đã cài; cài xong thì bấm **Tạo …Config** để tạo asset và gán vào `NovaSdkSettings`, rồi điền ID vào asset đó.

| Muốn dùng | Cài vendor (mục 2) | Asset config | Việc làm tay |
|---|---|---|---|
| Firebase (Analytics, Crashlytics) | Firebase App + Analytics/Crashlytics | — | Thả `google-services.json` / `GoogleService-Info.plist` vào `Assets/` |
| Remote Config | Firebase App + Remote Config | `GameRemoteConfig` (mục 7) | Setup bấm **Tạo script** để sinh `RemoteKey.cs`; thêm key của game vào enum |
| Ads MAX | AppLovin MAX + Google Mobile Ads (cho UMP) | `MaxAdsConfig` | SDK key trong *AppLovin > Integration Manager* |
| Ads AdMob | Google Mobile Ads | `AdMobAdsConfig` | App ID trong *Assets > Google Mobile Ads > Settings* |
| Consent GDPR (UMP) | Google Mobile Ads | — | Publish message trên AdMob console (mục 5) |
| Adjust | Adjust Unity SDK | `AdjustTrackingConfig` | — |
| IAP | `com.unity.purchasing` | `IapConfig` | Android: *NovaGames > IAP > Google Play License Key* |
| Notifications | `com.unity.mobile.notifications` | `NotificationConfig` | Chỉnh *Project Settings > Mobile Notifications* (mục 2) |
| Popup mất mạng / Đánh giá | — (Rating Android: Play In-App Review, tùy chọn) | — | Có sẵn trong prefab `NovaSdk` (mục 12, 13) |

Sau khi cài vendor bằng `.unitypackage`: bấm **Refresh Vendor Defines** (trong Setup hoặc menu NovaGames).

**C. Trước khi build release**

1. Bấm **Check Release Build** (trong Setup hoặc menu NovaGames), sửa hết lỗi.
2. Bỏ tick *Use Google Test Ids* trong `AdMobAdsConfig`; `consentSource` không để *Assume granted*.
3. Test mua IAP thật, test ads trên máy thật.

**Sample Demo dùng để làm gì?** Chỉ để xem cách gọi API. Không kéo asset trong `Data/` của sample vào game (đó là cấu hình test: *Assume granted*, ad unit test). Xem xong thì **xóa thư mục sample** khỏi `Assets/Samples`, nếu không sẽ có 2 menu *Remote Config Definitions* trùng nhau.

## Mục lục

1. [Nguyên tắc](#1-nguyên-tắc)
2. [Cài SDK và vendor](#2-cài-sdk-và-vendor)
3. [Tạo asset cấu hình](#3-tạo-asset-cấu-hình)
4. [Khởi tạo SDK](#4-khởi-tạo-sdk)
5. [Consent và ATT — NovaPrivacy](#5-consent-và-att--novaprivacy)
6. [Quảng cáo — NovaAds](#6-quảng-cáo--novaads)
7. [Remote Config — NovaRemoteConfig](#7-remote-config--novaremoteconfig)
8. [Analytics — NovaAnalytics](#8-analytics--novaanalytics)
9. [Attribution và deep link — NovaAttribution (Adjust)](#9-attribution-và-deep-link--novaattribution-adjust)
10. [Mua hàng — NovaIap](#10-mua-hàng--novaiap)
11. [Thông báo local — NovaNotifications](#11-thông-báo-local--novanotifications)
12. [Popup mất mạng — NovaNoInternet](#12-popup-mất-mạng--novanointernet)
13. [Đánh giá app — NovaRating](#13-đánh-giá-app--novarating)
14. [Crashlytics — NovaCrash](#14-crashlytics--novacrash)
15. [Menu Editor và kiểm tra trước khi build](#15-menu-editor-và-kiểm-tra-trước-khi-build)
16. [Scene Demo](#16-scene-demo)
17. [Lỗi thường gặp](#17-lỗi-thường-gặp)

---

## 1. Nguyên tắc

- Game chỉ gọi các class tĩnh `Nova*` (namespace `NovaGames.Mobile`): `NovaSdk`, `NovaAds`, `NovaRemoteConfig`, `NovaAnalytics`, `NovaAttribution`, `NovaIap`, `NovaNotifications`, `NovaPrivacy`, `NovaNoInternet`, `NovaRating`, `NovaCrash`. Không `using` namespace của Firebase/MAX/AdMob/Adjust/Unity IAP.
- Mọi hàm gọi trên main thread, **không throw**, callback chạy trên main thread.
- Gọi trước khi SDK sẵn sàng vẫn an toàn: lệnh được giữ lại và chạy khi module sẵn sàng, hoặc callback "thất bại" vẫn được gọi để game chạy tiếp.
- Toàn bộ cấu hình nằm trong **một** asset `NovaSdkSettings`. Ô nào để trống thì module đó tắt.
- Module nào chưa cài vendor thì tự tắt (chỉ log cảnh báo), game vẫn compile và chạy.

## 2. Cài SDK và vendor

### Thêm package SDK

SDK là Unity package `com.novagames.sdk`. Mỗi game khai báo **một phiên bản cố định** trong `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.novagames.sdk": "<git-url-repo-SDK>?path=/Packages/com.novagames.sdk#v1.0.0",
    ...
  }
}
```

- Code SDK trong game là read-only (*Packages > NovaGames Mobile SDK*). Không sửa code SDK trong game; sửa ở repo SDK rồi phát hành phiên bản mới.
- **Nâng phiên bản:** đổi tag (`#v1.0.0` → `#v1.0.1`) trong `manifest.json` của game cần nâng, mở Unity để resolve lại, build thử rồi commit cả `manifest.json` lẫn `packages-lock.json`. Game nào chưa đổi tag thì vẫn giữ bản cũ.
- **Lùi phiên bản:** đổi tag về bản cũ.
- **Sample:** *Window > Package Manager > NovaGames Mobile SDK > Samples > Demo > Import* (copy vào `Assets/Samples/NovaGames Mobile SDK/<version>/Demo`).
- **Sửa SDK song song với một game:** tạm trỏ thẳng vào thư mục package trên máy, sửa xong đổi lại tag trước khi commit game:

```json
"com.novagames.sdk": "file:D:/NovaGames/SDK/Packages/com.novagames.sdk"
```

Quy ước phiên bản: **patch** (1.0.0 → 1.0.1) sửa lỗi; **minor** (1.0 → 1.1) thêm tính năng, không phá API cũ; **major** (1.x → 2.0) đổi hoặc xóa API (API cũ được đánh `[Obsolete]` ít nhất một bản minor trước khi xóa).

### Cài vendor

Vendor plugin không nằm trong package: game tự cài theo bảng dưới.

| Module | Vendor | Define (tự bật) |
|---|---|---|
| Firebase core | `com.google.firebase.app` ≥ 13 (bắt buộc nếu dùng Firebase) | `NOVA_FIREBASE_APP` |
| Analytics | `com.google.firebase.analytics` | `NOVA_FIREBASE_ANALYTICS` |
| Remote Config | `com.google.firebase.remote-config` | `NOVA_FIREBASE_REMOTE_CONFIG` |
| Crashlytics | `com.google.firebase.crashlytics` | `NOVA_FIREBASE_CRASHLYTICS` |
| Ads MAX | AppLovin MAX Unity plugin 8.x | `NOVA_MAX` |
| Ads AdMob + UMP | Google Mobile Ads Unity plugin 11.x | `NOVA_ADMOB`, `NOVA_UMP` |
| Adjust | Adjust Unity SDK 5.x | `NOVA_ADJUST` |
| IAP | `com.unity.purchasing` 5.x | `NOVA_IAP` |
| Notifications | `com.unity.mobile.notifications` 2.x | `NOVA_NOTIFICATIONS` |
| In-App Review (Android) | Google Play In-App Review (`Assets/GooglePlayPlugins/com.google.play.review`) | `NOVA_PLAY_REVIEW` |

- Package cài qua UPM: define lấy từ `versionDefines` của asmdef.
- Vendor cài qua `.unitypackage`: `Core/Editor/VendorDefines.cs` dò assembly khi Editor load và tự set/gỡ `NOVA_MAX`, `NOVA_ADMOB`, `NOVA_ADJUST`, `NOVA_UMP`, `NOVA_PLAY_REVIEW`. Quét lại bằng menu **NovaGames > Refresh Vendor Defines**.
- **UMP cần plugin Google Mobile Ads, kể cả khi chỉ dùng MAX.**
- Dùng cả MAX và AdMob: GMA plugin và AdMob network adapter của MAX dùng chung GMA native, phải giữ version khớp nhau.

Thiết lập riêng của từng vendor:

- **Firebase:** `Assets/google-services.json` (Android), `Assets/GoogleService-Info.plist` (iOS). Android chạy *External Dependency Manager > Force Resolve*; iOS dùng CocoaPods. Consent Mode mặc định "denied" được tự ghi vào AndroidManifest/Info.plist lúc build.
- **MAX:** SDK key đặt trong *AppLovin > Integration Manager*. **Tắt** Terms & Privacy Policy Flow của AppLovin (consent lấy từ UMP hoặc từ game).
- **AdMob:** App ID đặt trong *Assets > Google Mobile Ads > Settings*. Không hỗ trợ Google Ad Manager.
- **Adjust:** không kéo prefab Adjust vào scene. Permission, ATT usage, URL scheme/universal link đặt ở *Assets > Adjust > Settings*.
- **Notifications:** *Project Settings > Mobile Notifications*: iOS **tắt** "Request Authorization on App Launch"; Android **bật** "Reschedule on Device Restart", thêm small icon nếu cần.

## 3. Tạo asset cấu hình

Chuột phải trong Project > **Create > NovaGames > …**

| Asset | Menu | Dùng cho |
|---|---|---|
| `NovaSdkSettings` | SDK Settings | Asset gốc, giữ tham chiếu tới mọi asset bên dưới |
| `GameRemoteConfig` | Remote Config Definitions | Danh sách key Remote Config + default (mục 7) |
| `MaxAdsConfig` | Ads Config (MAX) | Ad unit ID MAX, bid floor test |
| `AdMobAdsConfig` | Ads Config (AdMob) | Ad unit ID AdMob, collapsible banner, bid floor test |
| `AdjustTrackingConfig` | Adjust Config | App token, event token |
| `IapConfig` | IAP Config | Danh sách sản phẩm |
| `NotificationConfig` | Notification Config | Nhắc chơi, channel Android |

Các mục trong Inspector của `NovaSdkSettings`:

- **Remote Config:** `remoteConfig`.
- **Ads:** `maxAds`, `admobAds`, `adsMediation` (chọn `Max | AdMob | None` cho từng format: interstitial, rewarded, appOpen, banner, mrec; mặc định Max), `pauseGameDuringFullScreenAds` (mặc định bật: `timeScale = 0` và tắt âm khi ad full-screen hiện).
- **Crashlytics:** `crashReporting` (mặc định bật), `uncaughtExceptionsAsFatal`.
- **Tracking:** `adjust`.
- **IAP:** `iap`. **Notifications:** `notifications`.
- **Consent:** `consentSource` — *Google UMP* (khuyến nghị), *Game calls NovaSdk.SetConsent*, *Assume granted* (chỉ để test, bản release tự chuyển sang UMP); `underAgeOfConsent`; `debugGeography` và `consentTestDeviceHashedIds` (chỉ có tác dụng ở Development build).
- **iOS ATT:** `requestAttOnStartup` (mặc định bật, hỏi ngay sau form consent), `attUsageDescription` (build iOS tự ghi vào Info.plist).

Lúc khởi động, SDK log các lỗi cấu hình với prefix `Settings:` (asset kéo nhầm ô MAX/AdMob, thiếu ID, test ID trong bản release, ATT Usage Description rỗng…).

## 4. Khởi tạo SDK

**Cách 1 — prefab (khuyến nghị):** kéo `Bootstrap/Prefabs/NovaSdk` (trong *Packages > NovaGames Mobile SDK*) vào scene đầu tiên, gán `NovaSdkSettings` vào ô *Settings* (hoặc bấm *Thêm vào scene* trong **NovaGames > Setup**). Prefab:

- gọi `NovaSdk.InitializeAsync(settings)` một lần, sống qua mọi scene (`DontDestroyOnLoad`); đặt prefab ở nhiều scene cũng chỉ giữ một bản;
- tạo popup mất mạng và popup đánh giá (để trống ô prefab tương ứng nếu không dùng);
- tạo `EventSystem` khi scene chưa có (scene có EventSystem riêng thì bản của SDK tự tắt);
- áp Remote Config: key Bool `no_internet_popup_on` bật/tắt popup mất mạng, key Int `level_show_rate` → `NovaRating.MinLevel`. Đổi tên key ngay trên Inspector; asset Remote Config không có key đó thì bỏ qua.
- chứa các module SDK là GameObject con (`Firebase Remote Config`, `AppLovin MAX`, `AdMob`, `Adjust`, `Google UMP`, `Unity IAP`…), mỗi vendor một con. Không cần thêm tay: mở scene là Editor tự thêm module của vendor đã cài, gỡ vendor thì module mất script được tự xóa; lúc build tự bỏ module không dùng cho nền tảng đó (vd. ATT khi build Android). Lưu scene sau khi module được thêm (nếu quên, lúc build và lúc Play vẫn tự thêm). Module có trong scene nên khi build IL2CPP không bị strip.

Scene đầu chuyển sang scene chính sau khi SDK sẵn sàng:

```csharp
async void Start()
{
    await NovaSdk.WhenReady;
    SceneManager.LoadScene("Home");
}
```

**Cách 2 — tự viết script:**

Script khởi động vẫn cần component **Nova Modules** (*Add Component > NovaGames > Nova Modules*) trên một GameObject trong scene đầu tiên để Editor thêm các module vào đó; thiếu thì SDK log lỗi "no NovaModules in the scene" và không vendor nào chạy.

```csharp
using NovaGames.Mobile;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class Boot : MonoBehaviour
{
    [SerializeField] NovaSdkSettings settings;

    async void Start()
    {
        await NovaSdk.InitializeAsync(settings); // gọi 1 lần trên main thread; gọi lại trả cùng Task
        SceneManager.LoadScene("Home");
    }
}
```

- Scene khác có thể chờ `await NovaSdk.WhenReady;` (chờ được cả trước khi init).
- Trạng thái: `NovaSdk.State` (`NotInitialized` / `Initializing` / `Ready`), `NovaSdk.IsReady`.
- **Ready** nghĩa là Remote Config đã fetch xong (hoặc hết timeout, khi đó dùng cache/default) và tracking đã init. Ads, IAP và form consent tiếp tục chạy ở nền; lỗi của bất kỳ module nào cũng không chặn Ready.
- `Debug.isDebugBuild` quyết định chế độ Development hay Production (log level, Adjust Sandbox/Production, fetch interval Remote Config…).
- `NovaSdk.Shutdown()` giải phóng mọi module (dùng cho test hoặc reload toàn bộ game).

Thứ tự khởi tạo bên trong: Crashlytics → ATT (đọc trạng thái) → consent → IAP → Notifications → Remote Config (nạp cache) + tracking (Firebase/Adjust) song song → chờ Remote Config → Ads (nền) → Ready.

## 5. Consent và ATT — NovaPrivacy

**Google UMP (mặc định):**

- Trên AdMob console > *Privacy & messaging*: publish message GDPR và US states.
- SDK tự hiện form. Consent của lần chạy trước được áp ngay; nếu UMP lỗi/timeout (10 s) thì dùng consent đã lưu và hỏi lại sau 30 s / 60 s / 2 phút / 5 phút hoặc khi người chơi quay lại app.
- Game chỉ cần nút Privacy (bắt buộc ở EEA/UK):

```csharp
privacyButton.gameObject.SetActive(NovaPrivacy.IsPrivacyOptionsRequired);
privacyButton.onClick.AddListener(() => NovaPrivacy.ShowPrivacyOptions(onClosed: RefreshUi));
```

**Game tự quản consent (CMP riêng):** đặt `consentSource = Game` rồi gọi `NovaSdk.SetConsent(snapshot)` mỗi lần consent đổi. Ads chỉ load khi `snapshot.CanRequestAds`; Adjust chỉ khởi động sau consent đầu tiên. Trên iOS, SDK tự thay `Att` bằng trạng thái thật của máy.

**Đọc trạng thái:** `NovaPrivacy.Consent`, `NovaPrivacy.CanRequestAds`, sự kiện `NovaPrivacy.ConsentChanged`.

**ATT (iOS):**

```csharp
bool idfa = NovaPrivacy.TrackingStatus == AttStatus.Authorized;
NovaPrivacy.RequestTracking(status => { }); // chỉ cần khi đã tắt requestAttOnStartup
```

- Build iOS tự link `AppTrackingTransparency.framework` và ghi `NSUserTrackingUsageDescription`.
- Game dưới tuổi không bao giờ hỏi ATT. Android, Editor và iOS < 14: `TrackingStatus = NotApplicable`.
- Popup ATT chỉ hiện một lần; muốn thấy lại phải cài lại app.

**Lưu ý:**

- Chưa giải mã chuỗi GPP nên `UsDoNotSell` luôn false: MAX và Adjust chưa nhận opt-out "do not sell" của người dùng Mỹ.
- Editor không có chuỗi IAB nên consent luôn ra None/Unknown.

## 6. Quảng cáo — NovaAds

### Cấu hình

1. Tạo `MaxAdsConfig` và/hoặc `AdMobAdsConfig`, điền ad unit ID Android/iOS cho Interstitial, Rewarded, App Open, Banner, MREC. Để trống = không dùng format đó.
2. Mục **Banner**: vị trí mặc định (Top/Bottom) cho `ShowBanner()` không truyền vị trí.
3. **AdMob:** *Use Google Test Ids* dùng test ID của Google ở **mọi** bản build kể cả release — bỏ tick trước khi phát hành. `testDeviceIds` và `verboseLogging` chỉ áp dụng ở Development. Mục *Bid Floor Test* xem bên dưới.
4. **MAX:** `testDeviceIds` (GAID/IDFA), `verboseLogging` chỉ áp dụng ở Development; mục *Bid Floor Test* xem bên dưới.
5. Kéo asset vào `NovaSdkSettings` > Ads, chọn provider cho từng format trong *Ads Mediation*.

### Gọi từ game

```csharp
NovaAds.SetLevel(level);                                  // interstitial chỉ show từ ad_inter_start_level
NovaAds.ShowInterstitial("level_end", onDone: NextLevel); // onDone luôn được gọi đúng 1 lần
NovaAds.ShowRewarded("revive", onRewarded: Revive, onFailed: ShowToast); // chỉ 1 trong 2 callback
reviveBtn.interactable = NovaAds.IsRewardReady("revive"); // đã tính capping, kill switch, remove_ads
bool interReady = NovaAds.IsInterReady("level_end");

NovaAds.ShowBanner();                                     // gọi trước khi Ads sẵn sàng vẫn được
NovaAds.ShowBanner(BannerPosition.Top, "home_banner");
NovaAds.HideBanner(); NovaAds.DestroyBanner();
float h = NovaAds.BannerHeightPx();                       // đẩy UI tránh banner

NovaAds.ShowMRec(MrecPosition.BottomCenter);
NovaAds.ShowMRec(new Vector2(x, y));                      // pixel, gốc trên-trái
NovaAds.HideMRec();
using (NovaAds.HideMRecsTemporarily()) { /* popup che màn hình */ }

NovaAds.ShowAppOpen(onDone);
NovaAds.SetAutoShowAppOpen(true);                         // tự show khi quay lại game
using (NovaAds.SuppressAppOpen("purchase")) { /* mua hàng, mở link ngoài */ }

NovaAds.OpenDebugger();                                   // MAX Mediation Debugger / AdMob Ad Inspector, chỉ Development
```

- Thuộc tính: `IsInitialized`, `IsRemoveAds`, `IsShowingFullScreen`, `IsMRecShowing()`, `MRecPixelSize()`.
- API nâng cao (Task, sự kiện) qua `NovaAds.Service` (`IAdsService`), ví dụ `Service.FullScreen.AvailabilityChanged` để bật/tắt nút rewarded thay vì polling, `Service.Banners.LayoutChanged`.
- Placement là chuỗi tùy ý; mọi placement dùng ad unit duy nhất của format đó, tên placement được gửi kèm revenue và log.
- Không có `ignoreCapping`: capping, kill switch và `remove_ads` luôn được áp.

### Remote Config cho Ads

Asset Remote Config của game cài `IAdsConfigKeysSource` để map key (mẫu: `Scripts/RemoteKey.cs` trong sample Demo → `RemoteKeys.Ads`, ở đó bật/tắt từng format dùng tên riêng như `inter_ad_on_off`). Không cài thì SDK dùng tên mặc định dưới đây. Hai cờ floor luôn đọc theo đúng tên mặc định.

| Key mặc định | Default | Giới hạn an toàn |
|---|---|---|
| `ad_enabled`, `ad_inter_enabled`, `ad_rewarded_enabled`, `ad_aoa_enabled`, `ad_banner_enabled`, `ad_mrec_enabled` | true | — |
| `ad_inter_interval` (s) | 30 | ≥ 30 |
| `ad_inter_start_level` | 3 | ≥ 1 |
| `ad_inter_after_rewarded` (s) | 60 | ≥ 0 |
| `ad_inter_max_per_session`, `ad_inter_max_per_day` | 0 (không giới hạn) | ≥ 0 |
| `ad_aoa_min_background` (s) | 5 | ≥ 5 |
| `ad_collapsible_interval` (s) | 30 | ≥ 30 |
| `ad_rewarded_grace` (ms) | 1000 | 300–3000 |
| `ad_inter_floor_enabled`, `ad_rewarded_floor_enabled` (MAX, AdMob) | false | đọc một lần lúc adapter init |

- Giá trị sai kiểu/âm → dùng default, sau đó mới kẹp theo giới hạn.
- Config activate giữa session được áp ngay; riêng 2 cờ floor có hiệu lực từ lần mở app sau.
- Capping lưu bền (key `novagames.mobile.v1.ads.capping`), tính cả qua các lần mở app.

### Bid floor test (MAX và AdMob, Interstitial/Rewarded)

- **Trên MAX dashboard:** mỗi format có 3 unit — HIGH (floor theo geo, chỉ bidder), MEDIUM (½ HIGH, chỉ bidder), MAIN (không floor, waterfall đầy đủ). Cả 3 dùng chung network placement ID; không thêm unit nào khác cho format đó.
- **Trong `MaxAdsConfig`:** `interstitial`/`rewarded` là unit MAIN; điền HIGH/MEDIUM vào mục *Bid Floor Test*; `floorTierLoadTimeoutSeconds` mặc định 15.
- **Trên AdMob:** mỗi format có 3 ad unit — HIGH (eCPM floor cao), MEDIUM (½ HIGH), MAIN (không floor). Trong `AdMobAdsConfig` điền giống MAX: `interstitial`/`rewarded` là MAIN, HIGH/MEDIUM ở mục *Bid Floor Test*. Đang bật *Use Google Test Ids* thì không cascade. Ad AdMob dùng một lần: tier nào fill trễ được giữ lại và show ở lần sau; hết TTL thì bỏ ad của mọi tier.
- Hai cờ dùng chung cho MAX và AdMob: format chạy mediation nào thì cờ áp cho mediation đó.
- **Bật theo nhóm** bằng Firebase A/B trên `ad_inter_floor_enabled` / `ad_rewarded_floor_enabled`. Session đầu chưa có cache thường rơi vào nhóm control.
- Khi bật, mỗi lượt load đi HIGH → MEDIUM → MAIN; show dùng tier giá cao nhất đang có ad; revenue mang `AdUnitId` của tier thật sự show để tách doanh thu theo tier.
- Cấu hình sai (unit floor trùng ID, unit main trống) → cascade đó bị bỏ qua và log warning.

### Hành vi cần biết

- **Gate:** Ads chỉ init/load khi có consent (`CanRequestAds`); trên iOS chờ quyết định ATT tối đa 30 s nếu SDK tự hỏi ATT. Consent bị thu hồi thì bỏ ad đã preload và destroy banner/MREC.
- **Init lỗi tạm thời:** retry theo backoff 2, 4, 8… s (trần 64 s).
- **Under-age:** MAX không init; AdMob dùng `AgeRestrictedTreatment = Teen`.
- **Show:** mỗi lúc chỉ một full-screen (gọi thêm nhận `Busy`). Reward chỉ tính từ callback reward của vendor; nếu close đến trước thì chờ thêm `ad_rewarded_grace`.
- **remove_ads** (mua qua NovaIap): tắt interstitial, app open, banner, MREC ngay lập tức; rewarded vẫn chạy.
- **Mất mạng:** không load; khoảng 5 s sau khi có mạng lại thì tự load.
- **App open:** không show khi lần resume do chính ad gây ra, hoặc khi popup ATT / xin quyền thông báo đang mở.
- **Collapsible banner:** chỉ AdMob hỗ trợ. Với MAX, `CollapsiblePolicy.Required` trả `Unsupported`, `Preferred` hiện banner thường.
- **TTL:** AdMob 4 giờ (app open) / 1 giờ (inter/rewarded). MAX tự reload ad hết hạn nên SDK không áp TTL.

## 7. Remote Config — NovaRemoteConfig

### Khai báo key

Bấm **Tạo script** ở dòng *Remote Config* trong **NovaGames > Setup**: SDK sinh `Assets/NovaGames/Scripts/RemoteKey.cs`, `GameRemoteConfig.cs` và `Editor/GameRemoteConfigContextMenu.cs` (namespace nhập trong cửa sổ, mặc định `Game`), compile xong tự tạo asset `GameRemoteConfig`, thêm sẵn mọi key và gán vào `NovaSdkSettings`. Sau đó chỉ cần sửa enum `RemoteKey`. (Làm tay: copy 3 file trên từ sample Demo, đổi namespace `NovaGames.Mobile.Samples`.)

```csharp
public enum RemoteKey
{
    [RemoteDefault(ConfigValueType.Int, "3")] level_show_rate = 102,           // luôn gán số tường minh
    [RemoteDefault(ConfigValueType.Bool, "true")] no_internet_popup_on = 201,
    [RemoteDefault(ConfigValueType.Int, "5")] start_lives = 300,               // key mới của game: số mới
}
```

1. Tên enum = tên key trên Firebase console.
2. Luôn gán số tường minh; chỉ thêm key mới, **không đổi số của key cũ** (Unity lưu enum theo số).
3. Tạo asset *Create > NovaGames > Remote Config Definitions*, chuột phải asset > *Add Missing Enum Entries* để thêm dòng từ enum.
4. Mỗi dòng: kiểu (Bool/Int/Long/Double/String), default (invariant culture), `remoteOverridable` (false = luôn dùng default).
5. Gán asset vào `NovaSdkSettings` > Remote Config.

### Đọc giá trị

```csharp
int lives = NovaRemoteConfig.GetInt(RemoteKey.start_lives); // GetBool/GetLong/GetDouble/GetString
NovaRemoteConfig.Updated += ApplyConfig;                     // có bộ giá trị remote mới
bool fetched = await NovaRemoteConfig.FetchAsync();
var source = NovaRemoteConfig.Source;                        // Default / Cache / Remote
```

- Luôn có giá trị, theo thứ tự remote → cache lần trước → default trong asset.
- Giá trị remote sai kiểu hoặc ngoài khoảng bị loại (dùng giá trị hợp lệ gần nhất, không có thì default).
- Lúc khởi động SDK chờ init Firebase tối đa 5 s + fetch tối đa *Fetch Timeout Seconds* (mặc định 3 s) rồi Ready. Fetch chậm hơn vẫn chạy nền (tối đa 60 s) và được áp khi về: `NovaRemoteConfig.Updated` bắn, nên đọc lại giá trị trong handler. `FetchAsync()` cũng vậy: trả `false` khi hết giờ chờ nhưng giá trị về sau vẫn được áp.
- Lỗi tạm thời (mất mạng, timeout, Play services đang cập nhật) tự thử lại sau 10 s / 30 s / 2 phút; quay lại app mà vẫn chưa có giá trị remote thì fetch lại. `NovaRemoteConfig.LastError` giữ lý do lỗi gần nhất (null khi đã có giá trị remote).
- Minimum fetch interval: Development 0; release theo *Release Fetch Interval Minutes* trong `NovaSdkSettings` (mặc định 720 = 12 giờ, mặc định của Firebase). Trong khoảng này Firebase trả bộ giá trị đã fetch trước đó, nên đổi giá trị trên console có thể chưa tới máy đang chạy bản release.

## 8. Analytics — NovaAnalytics

```csharp
NovaAnalytics.LogEvent("level_complete", ("level", 3), ("time", 42.5f));
NovaAnalytics.SetUserProperty("player_segment", "whale");
NovaAnalytics.SetUserId("u123");                             // Crashlytics cũng nhận
NovaAnalytics.LogPurchase(txId, "remove_ads", 2.99, "USD");  // chỉ khi KHÔNG mua qua NovaIap
```

- Event gọi trước khi sink sẵn sàng được giữ lại, tối đa 100 lệnh mỗi sink (đầy thì bỏ lệnh cũ nhất).
- Event gửi tới Firebase; Adjust chỉ nhận event có token trong Adjust Config.
- **Luật tên Firebase** (sai luật thì bỏ kèm warning, không throw):
  - Tên event/param ≤ 40 ký tự, bắt đầu bằng chữ cái, chỉ `[A-Za-z0-9_]`, không prefix `firebase_` / `google_` / `ga_`.
  - Event reserved (`first_open`, `session_start`, `in_app_purchase`…) bị bỏ.
  - Tối đa 25 param; string ≤ 100 ký tự (bị cắt); NaN/Infinity bị bỏ.
  - User property: tên ≤ 24, giá trị ≤ 36 ký tự; user id ≤ 256.
- **`ad_impression`** được SDK tự gửi cho mỗi impression có doanh thu: `ad_platform` (`max`/`admob`), `ad_source`, `ad_format`, `ad_unit_name`, `currency`, `value`.
- Purchase: Android không gửi (Firebase tự thu từ Play Billing); iOS gọi `LogAppleTransactionAsync`.
- Nếu app AdMob đã link AdMob ↔ Firebase thì AdMob tự log `ad_impression` → bị trùng (chưa có cơ chế tắt).

## 9. Attribution và deep link — NovaAttribution (Adjust)

### Cấu hình `AdjustTrackingConfig`

- `androidAppToken`, `iosAppToken`: 12 ký tự `[a-z0-9]`.
- `events`: danh sách {eventName, android, ios}, token 6 ký tự.
- `purchaseEventName` (mặc định `purchase`): phải có token trong bảng thì purchase mới gửi Adjust.
- `releaseLogLevel` (Development luôn Verbose), `sendInBackground`, `costDataInAttribution`, `defaultTracker`, `attConsentWaitingIntervalSeconds` (0–360, chỉ iOS).
- Gán vào `NovaSdkSettings` > Tracking > Adjust. Development build = Sandbox, release = Production.

### Gọi từ game

```csharp
NovaAttribution.DeepLinkReceived += link => Open(link.Url); // link.IsDeferred
NovaAttribution.Changed += data => { /* data.Network, data.Campaign */ };
var current = NovaAttribution.Current;
```

- Deep link đến trước khi có handler được giữ lại (tối đa 8) cho handler đầu tiên.
- Adjust không tự mở deferred deep link; game tự xử lý qua `DeepLinkReceived`.

### Consent

- Adjust chỉ `InitSdk` sau khi có consent với vùng đã biết (vì mặc định Adjust chia sẻ dữ liệu cho partner).
- Third-party sharing (kèm `google_dma`) và measurement consent được gửi trước InitSdk và gửi lại khi consent đổi. Sharing bị tắt khi: US do-not-sell, trẻ em, hoặc GDPR từ chối `ad_user_data`.
- Under-age lúc init thì bật COPPA (+ Play Store Kids trên Android).
- Trong Editor, Adjust là no-op.

## 10. Mua hàng — NovaIap

### Cấu hình

1. Tạo `IapConfig`. Mỗi sản phẩm: Product Id, Android/iOS Store Id (trống = dùng Product Id), Type (Consumable / NonConsumable / Subscription), Remove Ads, Entitlements, Test Price Usd.
2. Tùy chọn chung: Validate Receipts, Test Store In Editor (mặc định bật), Test Store In Development Build.
3. Gán vào `NovaSdkSettings` > IAP.
4. **Android:** menu **NovaGames > IAP > Google Play License Key**, dán "Base64-encoded RSA public key" (Play Console > Monetization setup > Licensing) rồi bấm Generate. Key được lưu (đã obfuscate) vào `Assets/NovaGames/Generated/NovaGooglePlayLicense.cs` của game — commit file này. Key sai → giao dịch không được trao/confirm và Google tự hoàn tiền sau 3 ngày, nên luôn test mua thật trước khi phát hành.
5. **iOS:** StoreKit 2 tự kiểm tra receipt, không cần key.

### Gọi từ game

```csharp
NovaIap.SetConsumableHandler(g => { AddGems(g.ProductId); Save(); return true; }); // đăng ký sớm; false/throw = gọi lại sau
NovaIap.Purchase("remove_ads", r => { if (r.IsSuccess) ShowThanks(); });
var result = await NovaIap.PurchaseAsync("gem_pack_1");
priceLabel.text = NovaIap.GetPriceText("gem_pack_1", "...");
bool noAds = NovaIap.IsOwned("remove_ads");
bool vip = NovaIap.HasEntitlement("vip");
NovaIap.OnPurchased += g => RefreshShop();
NovaIap.Restore(ok => { });                                                       // iOS bắt buộc có nút Restore
```

- `PurchaseResult.Status`: `Completed`, `Pending`, `Cancelled`, `Failed`, `AlreadyOwned`, `NotReady`.
- Khác: `NovaIap.Products`, `GetProduct`, `IsReady`, `SetReceiptValidator` (kiểm tra receipt bằng server).
- Doanh thu được SDK tự gửi tới Firebase/Adjust — **không** gọi thêm `NovaAnalytics.LogPurchase`.
- Mỗi thời điểm chỉ một giao dịch.
- Test store lưu giao dịch vào PlayerPrefs (`novagames.mobile.v1.iap.*`): đã mua `remove_ads` trong Editor thì Ads bị tắt ở các lần chạy sau. Xóa bằng *Edit > Clear All PlayerPrefs*.
- Chuyển từ asset IapSettings cũ: chọn asset rồi chạy **NovaGames > IAP > Import Products From Selected IapSettings**, sau đó tick Remove Ads cho gói tắt quảng cáo.
- "Product not available in the store" = sai store id hoặc sản phẩm chưa active trên console.

## 11. Thông báo local — NovaNotifications

### Cấu hình `NotificationConfig`

- Ask Permission On Startup.
- Reminder Messages (trống = tắt nhắc chơi), Reminder Days, Reminder Hour/Minute (-1 = cùng giờ người chơi vừa chơi).
- Channel Id/Name/Description — **không đổi Channel Id sau khi phát hành**.
- Small/Large Icon, Opened Event Name (mặc định `notification_open`).

### Gọi từ game

```csharp
NovaNotifications.Schedule("energy_full", "Energy full!", "Come back", TimeSpan.FromHours(4)); // cùng id = thay thế
NovaNotifications.ScheduleAt("event", "Event", "Join now", localTime, data: "event_screen");
NovaNotifications.ScheduleDaily("daily", "Daily reward", "Gift waiting", hour: 19);
NovaNotifications.Cancel("energy_full"); NovaNotifications.CancelAll();
NovaNotifications.Opened += n => { if (n.Data == "event_screen") OpenEvent(); };
NovaNotifications.RequestPermission(allowed => { });
if (NovaNotifications.Permission == NotificationPermission.Denied) NovaNotifications.OpenSettings();
```

- Khác: `IsAllowed`, `LastOpened`, `ScheduledIds`.
- Thông báo mở game lúc khởi động được giữ lại cho handler `Opened` đầu tiên.
- Giới hạn pending: iOS 60, Android 200 (vượt thì bỏ thông báo xa nhất).
- Nhắc chơi dùng id `nova_reminder_<ngày>` (prefix dành riêng cho SDK), được lên lịch lại mỗi lần mở game hoặc vào nền.
- Android chỉ hiện khi game ở nền; iOS hiện cả khi đang mở game. Editor chỉ log.

## 12. Popup mất mạng — NovaNoInternet

- Prefab `NovaSdk` (mục 4) tự tạo popup từ `NoInternet/Prefabs/NoInternetPopup.prefab`. Đổi giao diện: tạo Prefab Variant trong Assets rồi kéo vào ô *No Internet Popup Prefab* của `NovaSdk`. Không dùng prefab `NovaSdk` thì kéo popup vào scene đầu tiên (scene cần có `EventSystem`). Popup tự `DontDestroyOnLoad`, chỉ giữ một instance.
- Field trên prefab: Show After Seconds (2 s), Hide After Seconds (0.5 s), Check Interval, Pause Game, Keep Across Scenes, Still Offline Message.

```csharp
NovaNoInternet.Enabled = false;          // tắt ở màn chơi offline; đang hiện thì đóng ngay
NovaNoInternet.SimulateOffline = true;   // chỉ có tác dụng ở Editor/Development build
NovaNoInternet.VisibilityChanged += showing => { };
```

- Khác: `IsInternetReachable`, `IsShowing`, sự kiện `EnabledChanged`.
- Bật/tắt từ xa: Remote Config `no_internet_popup_on` (prefab `NovaSdk` tự áp).
- Đang hiện ad full-screen thì popup đợi ad đóng; MREC bị ẩn khi popup hiện và hiện lại đúng vị trí cũ.
- Phát hiện mạng dựa trên `Application.internetReachability`: Wi-Fi cần đăng nhập (captive portal) vẫn bị coi là có mạng.
- Nút Settings: Android mở cài đặt mạng; iOS chỉ mở được cài đặt của app.

## 13. Đánh giá app — NovaRating

- Prefab `NovaSdk` (mục 4) tự tạo popup từ `Rating/Prefabs/RatingPopup.prefab`, dùng được ở mọi scene. Đổi giao diện: Prefab Variant rồi kéo vào ô *Rating Popup Prefab*. Cũng có thể đặt popup riêng trong scene (GameObject active, scene có `EventSystem`): popup bật sau cùng được dùng.
- Android: dùng Google Play In-App Review nếu có package, không thì mở trang store. iOS: điền Apple App Id trên component.
- Field trên prefab: Min Level, Later Cooldown Hours, Max Prompts, Store Review Min Stars, Auto Rate On Max Stars, Feedback Email, màu sao, Keep Across Scenes.

```csharp
NovaRating.ShowIfEligible(level);                                   // màn thắng level
NovaRating.Show();                                                  // nút "Rate us", bỏ qua điều kiện
NovaRating.MinLevel = NovaRemoteConfig.GetInt(RemoteKey.level_show_rate);
NovaRating.Closed += result => { };                                 // StoreReview, Feedback, NoThanks, Later, Never
NovaRating.RequestStoreReview();                                    // mở thẳng hộp thoại của store
NovaRating.ResetForTesting();
```

- Khác: `OpenStorePage()`, `AppleAppId`, `IsRated`, `HasPopup`, `CheckEligibility(level)`.
- Event analytics: `rating_shown`, `rating_result`.
- `Show()` trả false khi không có popup trong scene hoặc popup đang tắt.
- Play In-App Review chỉ hiện với app tải từ Google Play (vd. internal track); bản cài tay sẽ mở trang store.

## 14. Crashlytics — NovaCrash

```csharp
NovaCrash.Log("enter level 12");
NovaCrash.SetCustomKey("level", 12);
try { /* ... */ } catch (Exception e) { NovaCrash.LogException(e); }
NovaCrash.SetCollectionEnabled(false);   // người chơi tắt thu thập, giữ cả ở lần mở sau
NovaCrash.TestNonFatal(); NovaCrash.TestCrash(); // TestCrash chỉ chạy ở Development build trên máy thật
```

- Crash được gửi tự động. Lệnh gọi trước khi Ready được giữ (tối đa 64 log, 16 exception).
- Init lỗi có thể retry thì SDK thử lại sau 5 s / 15 s / 30 s / 1 phút.
- IL2CPP symbols cần upload thủ công: `firebase crashlytics:symbols:upload`.

## 15. Menu Editor và kiểm tra trước khi build

| Menu | Chức năng |
|---|---|
| **NovaGames > Setup** | Checklist tích hợp, nút tạo settings/config/script RemoteKey, thêm prefab NovaSdk vào scene |
| **NovaGames > Check Release Build** | Kiểm tra cấu hình release |
| **NovaGames > Refresh Vendor Defines** | Quét lại vendor và set define `NOVA_*` |
| **NovaGames > Show SDK Logs In Build** | Bật log `[Nova][module]` trong bản build (define `NOVA_SDK_LOG`); Editor luôn có log |
| **NovaGames > IAP > Google Play License Key** | Nhập license key, sinh file obfuscate |
| **NovaGames > IAP > Import Products From Selected IapSettings** | Chuyển sản phẩm từ asset cũ |

Khi build Android/iOS **release** (không tick Development Build), các lỗi sau **chặn build**: test ad unit của Google, App ID mẫu của AdMob, thiếu SDK key MAX, bật IAP mà thiếu Google Play license key, thiếu/sai `google-services.json` / `GoogleService-Info.plist`, manifest có `android:debuggable="true"`. Build Development chỉ cảnh báo. Bỏ qua bằng define `NOVA_SKIP_RELEASE_CHECKS` (không khuyến nghị).

## 16. Scene Demo

- Import sample Demo (mục 2), mở `Scenes/Demo.unity` rồi Play. Trong project SDK, sample nằm ở `Assets/NovaSdkSamples/Demo`.
- Prefab `NovaSdk` (giống hệt cách game tích hợp, mục 4) khởi động SDK với `Data/NovaSdkSettings.asset` của sample (consent *Google UMP*, toàn bộ ads chạy AdMob bằng test ID của Google); module SDK là GameObject con của `NovaSdk`; popup mất mạng và popup đánh giá do prefab tạo.
- `SdkDemoPanel` có nút cho privacy, Remote Config, analytics, 5 loại ads, level ±1, notifications, No Internet, rating, Crashlytics, IAP. Status cập nhật mỗi 0,5 s, log giữ 6 dòng gần nhất.
- Thêm nút: viết hàm public trong `SdkDemoPanel`, nhân bản một nút có sẵn trong scene và gán `OnClick` tới hàm đó.

## 17. Lỗi thường gặp

| Triệu chứng | Nguyên nhân / cách xử lý |
|---|---|
| Log "settings is null" / "kéo asset NovaSdkSettings vào ô Settings" | Chưa kéo `NovaSdkSettings` vào prefab `NovaSdk` (hoặc script tự viết). SDK vẫn Ready nhưng mọi API là no-op. |
| "must be called on Unity's main thread" | `InitializeAsync` gọi từ thread khác. |
| "no NovaModules in the scene" / "adapter is not installed" | Scene gọi `InitializeAsync` không có prefab `NovaSdk` hoặc component *Nova Modules*. Thêm prefab (hoặc component), mở scene để Editor thêm các module con rồi lưu. |
| "Remote Config is not ready…" / "uses Remote Config Definitions of X, not Y" | Chưa gán Remote Config Definitions, hoặc gọi `GetInt` với enum khác enum của asset. |
| Code game không thấy `RemoteKey` | Assembly của sample không được auto-reference; tạo script bằng **NovaGames > Setup** (mục 7). |
| Ads không load | Chưa có consent (Consent Source = Game mà chưa `SetConsent`; UMP chưa cài), adapter chưa cài, hoặc mediation = None. Xem log `Ads:` lúc khởi động. |
| Interstitial không hiện | Level < `ad_inter_start_level`, đang capping, kill switch, hoặc đã mua `remove_ads`. |
| Ads tắt hẳn trong Editor | Đã mua `remove_ads` bằng test store — *Edit > Clear All PlayerPrefs*. |
| Log "IAP uses the TEST STORE" | Đang bật Test Store trong `IapConfig`. |
| Adjust không gửi event | Event chưa có token trong `AdjustTrackingConfig`, hoặc chưa có consent. |
