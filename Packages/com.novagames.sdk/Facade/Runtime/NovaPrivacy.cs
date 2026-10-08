#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// Consent của người dùng (GDPR/US). Với Consent Source = Google UMP, NovaSdk tự hiện form lúc khởi động; game chỉ
    /// cần thêm nút "Privacy settings" (bắt buộc ở EEA/UK) trong màn Settings:
    /// <code>
    /// privacyButton.gameObject.SetActive(NovaPrivacy.IsPrivacyOptionsRequired);
    /// privacyButton.onClick.AddListener(() =&gt; NovaPrivacy.ShowPrivacyOptions());
    /// </code>
    /// Mọi API gọi trên main thread và không throw.
    /// </summary>
    public static class NovaPrivacy
    {
        static IConsentPlatform? s_platform;
        static ISdkLogger? s_log;
        static bool s_showing;

        /// <summary>Consent đang áp dụng (Unknown cho tới khi UMP trả lời hoặc game gọi <see cref="NovaSdk.SetConsent"/>).</summary>
        public static ConsentSnapshot Consent => NovaSdk.Consent;

        /// <summary>Được phép request ads chưa (theo consent). Ads tự chờ điều kiện này, game không cần kiểm tra.</summary>
        public static bool CanRequestAds => NovaSdk.Consent.CanRequestAds;

        /// <summary>true = người dùng ở vùng bắt buộc có nút "Privacy settings" để đổi lựa chọn consent.</summary>
        public static bool IsPrivacyOptionsRequired => s_platform?.IsPrivacyOptionsRequired == true;

        /// <summary>Gọi mỗi khi consent đổi (form UMP đóng, game gọi SetConsent, người dùng trả lời popup ATT).</summary>
        public static event Action<ConsentSnapshot>? ConsentChanged;

        /// <summary>
        /// iOS: người dùng có cho dùng IDFA (App Tracking Transparency) không. NotApplicable trên Android/Editor, iOS dưới 14
        /// hoặc khi SDK chưa khởi tạo.
        /// </summary>
        public static AttStatus TrackingStatus => NovaSdk.CurrentAttStatus;

        /// <summary>
        /// iOS: hiện popup ATT của hệ thống (chỉ hiện một lần, khi người dùng chưa trả lời). Mặc định SDK tự hỏi sau form
        /// consent lúc khởi động; chỉ cần gọi hàm này khi đã tắt <i>Request Att On Startup</i> để tự chọn thời điểm.
        /// <paramref name="onDone"/> nhận trạng thái sau khi người dùng trả lời (NotApplicable trên Android/Editor).
        /// </summary>
        public static void RequestTracking(Action<AttStatus>? onDone = null) => _ = RequestTrackingAsync(onDone);

        static async Task RequestTrackingAsync(Action<AttStatus>? onDone)
        {
            var status = AttStatus.NotApplicable;
            try
            {
                status = await NovaSdk.RequestAttAsync();
            }
            catch (Exception e)
            {
                Report("RequestTracking failed", e);
            }
            try
            {
                onDone?.Invoke(status);
            }
            catch (Exception e)
            {
                Report("RequestTracking callback threw", e);
            }
        }

        /// <summary>
        /// Hiện form đổi lựa chọn consent của UMP; <paramref name="onClosed"/> chạy khi form đóng (hoặc ngay lập tức nếu
        /// không hiện được). Consent mới được áp cho Ads/Analytics/Adjust tự động.
        /// </summary>
        public static void ShowPrivacyOptions(Action? onClosed = null)
        {
            if (s_platform is null)
            {
                Log("ShowPrivacyOptions: Consent Source is not Google UMP or the SDK is not initialized");
                Invoke(onClosed);
                return;
            }
            if (s_showing)
            {
                Log("ShowPrivacyOptions: a privacy form is already showing");
                Invoke(onClosed);
                return;
            }
            _ = ShowPrivacyOptionsAsync(s_platform, onClosed);
        }

        static async Task ShowPrivacyOptionsAsync(IConsentPlatform platform, Action? onClosed)
        {
            s_showing = true;
            try
            {
                var result = await platform.ShowPrivacyOptionsAsync(CancellationToken.None);
                // SDK có thể đã Shutdown trong lúc form mở: không áp consent của lần chạy cũ.
                if (platform != s_platform) return;
                if (result.TryGetValue(out var consent)) NovaSdk.SetConsent(consent);
                else Log("Privacy options form failed: " + result.Error);
            }
            catch (Exception e)
            {
                Report("ShowPrivacyOptions failed", e);
            }
            finally
            {
                s_showing = false;
                Invoke(onClosed);
            }
        }

        internal static void Bind(IConsentPlatform platform, ISdkLogger log)
        {
            s_platform = platform;
            s_log = log;
        }

        internal static void RaiseConsentChanged(ConsentSnapshot consent)
        {
            try
            {
                ConsentChanged?.Invoke(consent);
            }
            catch (Exception e)
            {
                Report("ConsentChanged handler threw", e);
            }
        }

        static void Invoke(Action? callback)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception e)
            {
                Report("ShowPrivacyOptions callback threw", e);
            }
        }

        static void Log(string message)
        {
            if (s_log != null) s_log.Warning(message);
            else Debug.LogWarning("[Nova] " + message);
        }

        static void Report(string message, Exception e)
        {
            if (s_log != null) s_log.Error(message, e);
            else Debug.LogException(e);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_platform = null;
            s_log = null;
            s_showing = false;
            ConsentChanged = null;
        }
    }
}
