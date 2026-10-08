#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// API Ads cho game: hàm tĩnh, placement là string, kết quả qua callback.
    /// <para>
    /// Bọc <see cref="IAdsService"/> nên consent, capping, kill switch, remove_ads, retry và revenue vẫn áp dụng đầy đủ.
    /// Gọi trên main thread; không throw; callback luôn chạy trên main thread.
    /// </para>
    /// <para>
    /// Không show được (chưa init, chưa consent, chưa load, bị capping, remove_ads, ...): interstitial/app open vẫn gọi
    /// <c>onDone</c> để luồng game chạy tiếp, rewarded gọi <c>onFailed</c>. Placement là tên tùy ý (vd. "level_end");
    /// placement chưa khai báo trong Ads Config dùng ad unit của format đó.
    /// </para>
    /// </summary>
    public static class NovaAds
    {
        public const string DefaultInterstitial = "interstitial";
        public const string DefaultRewarded = "rewarded";
        public const string DefaultAppOpen = "app_open";
        public const string DefaultBanner = "banner";
        public const string DefaultMrec = "mrec";

        static readonly Dictionary<string, AdPlacement> Placements = new Dictionary<string, AdPlacement>(StringComparer.Ordinal);

        static IAdsService? s_ads;
        static IEntitlementProvider? s_entitlements;
        static ISdkLogger? s_log;
        static int? s_pendingLevel;
        static Action<IAdsService>? s_pendingBanner;
        static Action<IAdsService>? s_pendingMrec;
        // MREC game đang yêu cầu hiện (placement -> vị trí), để hiện lại sau khi bị ẩn tạm (popup che màn hình).
        static readonly Dictionary<string, MrecOptions> ShownMrecs = new Dictionary<string, MrecOptions>(StringComparer.Ordinal);
        // Số popup đang yêu cầu ẩn MREC (HideMRecsTemporarily lồng nhau): chỉ hiện lại khi popup cuối cùng đóng.
        static int s_mrecHideDepth;
        // Lệnh app open gọi trước khi Ads sẵn sàng: áp lúc Bind.
        static readonly List<PendingSuppression> PendingSuppressions = new List<PendingSuppression>();
        static (bool Enabled, string Placement)? s_pendingAutoShow;

        /// <summary>
        /// Gắn service Ads. <see cref="NovaSdk.InitializeAsync"/> tự gọi; chỉ gọi tay khi tự dựng AdsManager.
        /// Lệnh banner/MREC/level gọi trước đó được giữ lại và áp lúc này.
        /// </summary>
        public static void Bind(IAdsService ads, ISdkLogger log, IEntitlementProvider? entitlements = null)
        {
            s_ads = ads ?? throw new ArgumentNullException(nameof(ads));
            s_log = log ?? throw new ArgumentNullException(nameof(log));
            s_entitlements = entitlements;

            if (s_pendingLevel.HasValue) s_log.TryRun("SetLevel", () => ads.SetPlayerLevel(s_pendingLevel.Value));
            var banner = s_pendingBanner;
            var mrec = s_pendingMrec;
            s_pendingLevel = null;
            s_pendingBanner = null;
            s_pendingMrec = null;
            if (banner != null) s_log.TryRun("ShowBanner", () => banner(ads));
            if (mrec != null && s_mrecHideDepth == 0) s_log.TryRun("ShowMRec", () => mrec(ads));

            var autoShow = s_pendingAutoShow;
            s_pendingAutoShow = null;
            if (autoShow.HasValue) SetAutoShowAppOpen(autoShow.Value.Enabled, autoShow.Value.Placement);
            foreach (var pending in PendingSuppressions.ToArray()) pending.Attach(ads);
            PendingSuppressions.Clear();
        }

        /// <summary>Gỡ service đã <see cref="Bind"/> (chỉ khi đúng service đó).</summary>
        public static void Unbind(IAdsService ads)
        {
            if (!ReferenceEquals(s_ads, ads)) return;
            s_ads = null;
            s_entitlements = null;
        }

        /// <summary>true khi module Ads đã init xong và không bị chặn (consent, force update, ...).</summary>
        public static bool IsInitialized => s_ads?.State.Value == AdsModuleState.Ready;

        /// <summary>Người chơi đã mua gói xóa quảng cáo (interstitial, app open, banner, MREC bị tắt; rewarded vẫn chạy).</summary>
        public static bool IsRemoveAds => SafeIsActive(EntitlementId.RemoveAds);

        /// <summary>true khi ad full-screen (interstitial, rewarded, app open) đang hiển thị.</summary>
        public static bool IsShowingFullScreen { get; internal set; }

        /// <summary>
        /// API đầy đủ (Task, kết quả chi tiết, sự kiện availability/layout). Null trước khi Ads được tạo.
        /// Game thông thường không cần dùng.
        /// </summary>
        public static IAdsService? Service => s_ads;

        /// <summary>Level hiện tại của người chơi: interstitial chỉ show từ level <c>ad_inter_start_level</c> (Remote Config).</summary>
        public static void SetLevel(int level)
        {
            var ads = s_ads;
            if (ads is null) s_pendingLevel = level;
            else s_log.TryRun("SetLevel", () => ads.SetPlayerLevel(level));
        }

        // ---------------- Interstitial / Rewarded ----------------

        /// <summary>Interstitial show được ngay (đã tính capping, kill switch, remove_ads).</summary>
        public static bool IsInterReady(string placement = DefaultInterstitial) =>
            Availability(Get(placement, id => new InterstitialPlacement(id))) == AdAvailability.Ready;

        /// <summary>Rewarded show được ngay. Dùng để bật/tắt nút "xem quảng cáo nhận thưởng".</summary>
        public static bool IsRewardReady(string placement = DefaultRewarded) =>
            Availability(Get(placement, id => new RewardedPlacement(id))) == AdAvailability.Ready;

        /// <summary>
        /// Show interstitial. <paramref name="onDone"/> luôn được gọi đúng một lần: sau khi ad đóng, hoặc ngay lập tức
        /// nếu không show được (chưa load, capping, ...).
        /// </summary>
        public static void ShowInterstitial(string placement, Action? onDone = null)
        {
            var ads = s_ads;
            if (ads is null)
            {
                Invoke(onDone, "onDone");
                return;
            }
            _ = ShowInterstitialAsync(ads, Get(placement, id => new InterstitialPlacement(id)), onDone);
        }

        /// <summary>
        /// Show rewarded. <paramref name="onRewarded"/> khi người chơi xem đủ để nhận thưởng; ngược lại
        /// <paramref name="onFailed"/> (đóng sớm, không có ad, lỗi hiển thị, ...). Chỉ một trong hai được gọi.
        /// </summary>
        public static void ShowRewarded(string placement, Action onRewarded, Action? onFailed = null)
        {
            var ads = s_ads;
            if (ads is null)
            {
                Invoke(onFailed, "onFailed");
                return;
            }
            _ = ShowRewardedAsync(ads, Get(placement, id => new RewardedPlacement(id)), onRewarded, onFailed);
        }

        // ---------------- App open ----------------

        /// <summary>Show app open (vd. lúc vào game). <paramref name="onDone"/> luôn được gọi, kể cả khi không show.</summary>
        public static void ShowAppOpen(Action? onDone = null, string placement = DefaultAppOpen)
        {
            var ads = s_ads;
            if (ads is null)
            {
                Invoke(onDone, "onDone");
                return;
            }
            _ = ShowAppOpenAsync(ads, Get(placement, id => new AppOpenPlacement(id)), onDone);
        }

        /// <summary>
        /// Tự show app open mỗi khi người chơi quay lại game (đủ <c>ad_aoa_min_background</c> giây và không phải do
        /// ad/click quảng cáo gây ra).
        /// </summary>
        public static void SetAutoShowAppOpen(bool enabled, string placement = DefaultAppOpen)
        {
            var ads = s_ads;
            if (ads is null) {
                s_pendingAutoShow = (enabled, placement);
                return;
            }
            var target = enabled ? Get(placement, id => new AppOpenPlacement(id)) : null;
            s_log.TryRun("SetAutoShowOnResume", () => ads.AppOpen.SetAutoShowOnResume(target));
        }

        /// <summary>
        /// Chặn app open trong lúc mua hàng, hiện popup hệ thống, ... cho tới khi Dispose handle trả về:
        /// <c>using (NovaAds.SuppressAppOpen("purchase")) { ... }</c>.
        /// </summary>
        public static IDisposable SuppressAppOpen(string reason)
        {
            var ads = s_ads;
            if (ads is null) {
                // Ads chưa sẵn sàng: giữ lại, áp lúc Bind (app open có thể sẵn sàng ngay trong lúc popup còn mở).
                var pending = new PendingSuppression(reason);
                PendingSuppressions.Add(pending);
                return pending;
            }
            try
            {
                return ads.AppOpen.Suppress(reason);
            }
            catch (Exception e)
            {
                s_log?.Error("SuppressAppOpen threw", e);
                return NoopDisposable.Instance;
            }
        }

        // ---------------- Banner ----------------

        /// <summary>Hiện banner ở vị trí chọn trong Ads Config (mục Banner). Gọi trước khi SDK sẵn sàng thì tự hiện sau.</summary>
        public static void ShowBanner(string placement = DefaultBanner) =>
            ShowView(ref s_pendingBanner, "ShowBanner", ads => ads.Banners.Show(Get(placement, id => new BannerPlacement(id))));

        /// <summary>Hiện banner ở vị trí chỉ định (Top/Bottom).</summary>
        public static void ShowBanner(BannerPosition position, string placement = DefaultBanner) =>
            ShowView(ref s_pendingBanner, "ShowBanner",
                ads => ads.Banners.Show(Get(placement, id => new BannerPlacement(id)), new BannerOptions(position)));

        /// <summary>Ẩn banner (giữ lại view, hiện lại nhanh bằng ShowBanner).</summary>
        public static void HideBanner(string placement = DefaultBanner)
        {
            s_pendingBanner = null;
            var ads = s_ads;
            if (ads != null) s_log.TryRun("HideBanner", () => ads.Banners.Hide(Get(placement, id => new BannerPlacement(id))));
        }

        /// <summary>Hủy banner hẳn (giải phóng view).</summary>
        public static void DestroyBanner(string placement = DefaultBanner)
        {
            s_pendingBanner = null;
            var ads = s_ads;
            if (ads != null) s_log.TryRun("DestroyBanner", () => ads.Banners.Destroy(Get(placement, id => new BannerPlacement(id))));
        }

        /// <summary>Chiều cao banner hiện tại (pixel) để đẩy UI game lên/xuống; 0 khi chưa có banner.</summary>
        public static float BannerHeightPx(string placement = DefaultBanner)
        {
            var ads = s_ads;
            if (ads is null) return 0;
            try
            {
                return ads.Banners.GetLayout(Get(placement, id => new BannerPlacement(id))).HeightPx;
            }
            catch (Exception e)
            {
                s_log?.Error("BannerHeightPx threw", e);
                return 0;
            }
        }

        // ---------------- MREC ----------------

        /// <summary>Hiện MREC (300x250) ở giữa màn hình.</summary>
        public static void ShowMRec(string placement = DefaultMrec) => ShowMRec(MrecPosition.Centered, placement);

        /// <summary>Hiện MREC ở vị trí neo sẵn (TopCenter, Centered, BottomCenter).</summary>
        public static void ShowMRec(MrecPosition position, string placement = DefaultMrec) => ShowMRecWith(placement, new MrecOptions(position));

        /// <summary>Hiện MREC tại tọa độ pixel tính từ góc trên-trái màn hình.</summary>
        public static void ShowMRec(Vector2 pixelPosition, string placement = DefaultMrec) =>
            ShowMRecWith(placement, MrecOptions.AtPixels(pixelPosition.x, pixelPosition.y));

        static void ShowMRecWith(string placement, MrecOptions options)
        {
            var target = Get(placement, id => new MrecPlacement(id));
            ShownMrecs[target.Id] = options;
            // Đang có popup che màn hình: chỉ ghi nhận, MREC hiện khi popup đóng.
            if (s_mrecHideDepth > 0) return;
            ShowView(ref s_pendingMrec, "ShowMRec", ads => ads.Mrec.Show(target, options));
        }

        /// <summary>Ẩn MREC.</summary>
        public static void HideMRec(string placement = DefaultMrec)
        {
            s_pendingMrec = null;
            var target = Get(placement, id => new MrecPlacement(id));
            ShownMrecs.Remove(target.Id);
            var ads = s_ads;
            if (ads != null) s_log.TryRun("HideMRec", () => ads.Mrec.Hide(target));
        }

        /// <summary>
        /// Ẩn mọi MREC đang hiện (MREC là view native, luôn nằm trên UI Unity) cho tới khi Dispose handle trả về, rồi hiện
        /// lại đúng vị trí cũ. Dùng khi mở popup che màn hình: <c>using (NovaAds.HideMRecsTemporarily()) { ... }</c>.
        /// MREC game tự ẩn trong lúc đó thì không hiện lại; MREC game gọi hiện trong lúc đó sẽ hiện khi handle cuối cùng
        /// được Dispose (nhiều popup chồng nhau).
        /// </summary>
        public static IDisposable HideMRecsTemporarily()
        {
            if (s_mrecHideDepth++ == 0)
            {
                var ads = s_ads;
                if (ads != null)
                {
                    foreach (var pair in ShownMrecs)
                    {
                        var target = Get(pair.Key, id => new MrecPlacement(id));
                        s_log.TryRun("HideMRec", () => ads.Mrec.Hide(target));
                    }
                }
            }
            return new MrecRestore();
        }

        /// <summary>MREC đang hiện hoặc đang load để hiện.</summary>
        public static bool IsMRecShowing(string placement = DefaultMrec)
        {
            var ads = s_ads;
            if (ads is null) return s_pendingMrec != null;
            var state = ads.Mrec.GetState(Get(placement, id => new MrecPlacement(id)));
            return state == AdViewState.Visible || state == AdViewState.Loading;
        }

        /// <summary>Kích thước MREC (pixel); trước khi adapter đo được density thì là 0.</summary>
        public static Vector2 MRecPixelSize(string placement = DefaultMrec)
        {
            var ads = s_ads;
            if (ads is null) return Vector2.zero;
            var size = ads.Mrec.GetSize(Get(placement, id => new MrecPlacement(id)));
            return new Vector2(size.WidthPx, size.HeightPx);
        }

        /// <summary>Mở MAX Mediation Debugger / AdMob Ad Inspector. Chỉ hoạt động ở Development build.</summary>
        public static void OpenDebugger()
        {
            var ads = s_ads;
            if (ads != null) s_log.TryRun("OpenDebugger", ads.OpenDebugger);
        }

        // ---------------- Internals ----------------

        static async Task ShowInterstitialAsync(IAdsService ads, InterstitialPlacement placement, Action? onDone)
        {
            try
            {
                var result = await ads.FullScreen.ShowAsync(placement, CancellationToken.None);
                s_log?.Debug(placement + " -> " + result.Outcome);
            }
            catch (Exception e)
            {
                s_log?.Error("ShowInterstitial threw", e);
            }
            Invoke(onDone, "onDone");
        }

        static async Task ShowRewardedAsync(IAdsService ads, RewardedPlacement placement, Action onRewarded, Action? onFailed)
        {
            bool rewarded = false;
            try
            {
                var result = await ads.FullScreen.ShowAsync(placement, RewardedShowOptions.Immediate, CancellationToken.None);
                rewarded = result.IsRewarded;
                s_log?.Debug(placement + " -> " + result.Outcome);
            }
            catch (Exception e)
            {
                s_log?.Error("ShowRewarded threw", e);
            }
            if (rewarded) Invoke(onRewarded, "onRewarded");
            else Invoke(onFailed, "onFailed");
        }

        static async Task ShowAppOpenAsync(IAdsService ads, AppOpenPlacement placement, Action? onDone)
        {
            try
            {
                var result = await ads.AppOpen.TryShowAsync(placement, AppOpenTrigger.ColdStart, CancellationToken.None);
                s_log?.Debug(placement + " -> " + result.Outcome);
            }
            catch (Exception e)
            {
                s_log?.Error("ShowAppOpen threw", e);
            }
            Invoke(onDone, "onDone");
        }

        // Chưa Bind: giữ lệnh show cuối cùng, áp khi Bind.
        static void ShowView(ref Action<IAdsService>? pending, string what, Func<IAdsService, SdkResult> show)
        {
            var ads = s_ads;
            if (ads is null)
            {
                pending = a => show(a);
                return;
            }
            try
            {
                var result = show(ads);
                if (!result.IsSuccess) s_log?.Debug(what + ": " + result.Error);
            }
            catch (Exception e)
            {
                s_log?.Error(what + " threw", e);
            }
        }

        static AdAvailability Availability(FullScreenPlacement placement)
        {
            var ads = s_ads;
            if (ads is null) return AdAvailability.Blocked;
            try
            {
                return placement is AppOpenPlacement appOpen
                    ? ads.AppOpen.GetAvailability(appOpen)
                    : ads.FullScreen.GetAvailability(placement);
            }
            catch (Exception e)
            {
                s_log?.Error("GetAvailability threw", e);
                return AdAvailability.Blocked;
            }
        }

        // Cache placement theo format + tên: cùng tên dùng lại một object (availability event, revenue theo placement).
        static T Get<T>(string id, Func<string, T> create) where T : AdPlacement
        {
            if (string.IsNullOrEmpty(id)) id = DefaultName<T>();
            var key = typeof(T).Name + ":" + id;
            if (Placements.TryGetValue(key, out var existing)) return (T)existing;
            var placement = create(id);
            Placements[key] = placement;
            return placement;
        }

        static string DefaultName<T>() =>
            typeof(T) == typeof(InterstitialPlacement) ? DefaultInterstitial
            : typeof(T) == typeof(RewardedPlacement) ? DefaultRewarded
            : typeof(T) == typeof(AppOpenPlacement) ? DefaultAppOpen
            : typeof(T) == typeof(BannerPlacement) ? DefaultBanner
            : DefaultMrec;

        static bool SafeIsActive(EntitlementId id)
        {
            var entitlements = s_entitlements;
            if (entitlements is null) return false;
            try
            {
                return entitlements.IsActive(id);
            }
            catch (Exception e)
            {
                s_log?.Error("Entitlement provider threw", e);
                return false;
            }
        }

        static void Invoke(Action? callback, string name)
        {
            if (callback is null) return;
            try
            {
                callback();
            }
            catch (Exception e)
            {
                // Lỗi của game không được làm hỏng Ads; vẫn log để thấy.
                if (s_log != null) s_log.Error("Game callback " + name + " threw", e);
                else Debug.LogException(e);
            }
        }

        sealed class MrecRestore : IDisposable
        {
            bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (s_mrecHideDepth == 0 || --s_mrecHideDepth > 0) return;
                var ads = s_ads;
                if (ads is null) return;
                // Hiện lại MREC game vẫn đang muốn hiện, với vị trí mới nhất.
                foreach (var pair in ShownMrecs)
                {
                    var target = Get(pair.Key, id => new MrecPlacement(id));
                    var options = pair.Value;
                    s_log.TryRun("ShowMRec", () => ads.Mrec.Show(target, options));
                }
            }
        }

        // App open bị chặn trước khi Ads sẵn sàng: chặn thật khi Bind, gỡ khi Dispose.
        sealed class PendingSuppression : IDisposable
        {
            readonly string _reason;
            IDisposable? _inner;
            bool _disposed;

            public PendingSuppression(string reason) { _reason = reason; }

            public void Attach(IAdsService ads)
            {
                if (_disposed) return;
                try
                {
                    _inner = ads.AppOpen.Suppress(_reason);
                }
                catch (Exception e)
                {
                    s_log?.Error("SuppressAppOpen threw", e);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                PendingSuppressions.Remove(this);
                _inner?.Dispose();
                _inner = null;
            }
        }

        sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new NoopDisposable();
            public void Dispose() { }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_ads = null;
            s_entitlements = null;
            s_log = null;
            s_pendingLevel = null;
            s_pendingBanner = null;
            s_pendingMrec = null;
            Placements.Clear();
            ShownMrecs.Clear();
            s_mrecHideDepth = 0;
            IsShowingFullScreen = false;
            PendingSuppressions.Clear();
            s_pendingAutoShow = null;
        }
    }
}
