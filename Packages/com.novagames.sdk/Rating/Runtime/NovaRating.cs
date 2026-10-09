#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Rating;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>Hộp thoại review của store (Play In-App Review). done(true) = flow chạy xong, false = lỗi.</summary>
    public interface IInAppReviewProvider
    {
        void RequestReview(Action<bool> done);
    }

    /// <summary>
    /// Mời người chơi đánh giá game. Popup là prefab <c>Rating/Prefabs/RatingPopup</c>: kéo vào scene cần hỏi (vd. màn
    /// thắng level). Gọi lúc phù hợp:
    /// <code>
    /// NovaRating.ShowIfEligible(currentLevel);   // chỉ hiện khi đủ level, chưa đánh giá, hết thời gian chờ
    /// </code>
    /// Chọn đủ sao (mặc định 5) thì mở hộp thoại review của store; ít sao thì cảm ơn + góp ý (không đẩy lên store).
    /// Gọi trên main thread; không throw.
    /// </summary>
    public static class NovaRating
    {
        static RatingState? s_state;
        static RatingPopup? s_popup;
        static readonly List<RatingPopup> Popups = new List<RatingPopup>();
        static IInAppReviewProvider? s_androidReview;

        /// <summary>
        /// Level tối thiểu để hỏi; ghi đè Min Level của popup (vd. lấy từ Remote Config:
        /// <c>NovaRating.MinLevel = NovaRemoteConfig.GetInt(RemoteKey.level_show_rate)</c>). -1 = dùng giá trị của popup.
        /// </summary>
        public static int MinLevel { get; set; } = -1;

        /// <summary>Apple App ID (số trong link App Store) để mở trang đánh giá khi iOS không hiện được hộp thoại.</summary>
        public static string AppleAppId { get; set; } = string.Empty;

        /// <summary>Người chơi đã đánh giá hoặc từ chối hẳn.</summary>
        public static bool IsRated => State.Done;

        /// <summary>Có popup trong scene đang mở.</summary>
        public static bool HasPopup => s_popup != null;

        /// <summary>Popup đóng (kèm kết quả).</summary>
        public static event Action<RatingResult>? Closed;

        static RatingState State => s_state ??= Load();

        /// <summary>Lý do hiện/không hiện được popup ở level này.</summary>
        public static RatingEligibility CheckEligibility(int level) =>
            RatingPolicy.Check(State, CurrentOptions(), level, DateTime.UtcNow);

        /// <summary>Hiện popup nếu đủ điều kiện. true = đã hiện.</summary>
        public static bool ShowIfEligible(int level)
        {
            var eligibility = CheckEligibility(level);
            if (eligibility != RatingEligibility.Eligible) return false;
            return Show();
        }

        /// <summary>Hiện popup ngay, bỏ qua điều kiện (vd. nút "Rate us" trong Settings). false = chưa có popup trong scene.</summary>
        public static bool Show()
        {
            var popup = s_popup;
            if (popup == null)
            {
                Debug.LogWarning("[Nova][rating] No RatingPopup in the scene: drag Rating/Prefabs/RatingPopup into it");
                return false;
            }
            if (!popup.isActiveAndEnabled)
            {
                Debug.LogWarning("[Nova][rating] RatingPopup is inactive (its GameObject or a parent is disabled): not shown");
                return false;
            }
            var state = State;
            state.PromptCount++;
            state.LastPromptUtc = DateTime.UtcNow;
            Save();
            popup.Open();
            NovaAnalytics.LogEvent("rating_shown", ("prompt_count", state.PromptCount));
            return true;
        }

        /// <summary>
        /// Mở thẳng hộp thoại review của store, không qua popup sao. Android: Play In-App Review (lỗi thì mở trang store);
        /// iOS: hộp thoại của Apple. Store tự giới hạn số lần hiện, có thể không hiện gì.
        /// </summary>
        public static void RequestStoreReview(Action? onDone = null)
        {
            MarkDone();
            var suppression = NovaAds.SuppressAppOpen("rating");
            void Finish()
            {
                suppression.Dispose();
                if (onDone == null) return;
                try
                {
                    onDone();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                if (!UnityEngine.iOS.Device.RequestStoreReview()) OpenStorePage();
                Finish();
#elif UNITY_ANDROID && !UNITY_EDITOR
                var review = s_androidReview;
                if (review == null)
                {
                    OpenStorePage();
                    Finish();
                    return;
                }
                review.RequestReview(ok =>
                {
                    if (!ok) OpenStorePage();
                    Finish();
                });
#else
                Debug.Log("[Nova][rating] [Editor] Store review dialog only shows on device");
                Finish();
#endif
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Finish();
            }
        }

        /// <summary>Mở trang game trên store (Google Play / App Store).</summary>
        public static void OpenStorePage()
        {
#if UNITY_IOS
            if (string.IsNullOrEmpty(AppleAppId))
            {
                Debug.LogWarning("[Nova][rating] AppleAppId is not set: cannot open the App Store page");
                return;
            }
            Application.OpenURL("itms-apps://itunes.apple.com/app/id" + AppleAppId + "?action=write-review");
#else
            Application.OpenURL("market://details?id=" + Application.identifier);
#endif
        }

        /// <summary>Xóa trạng thái đánh giá (chỉ để test).</summary>
        public static void ResetForTesting()
        {
            s_state = new RatingState();
            Save();
        }

        /// <summary>Adapter Play In-App Review tự gọi khi được cài. Game không cần gọi.</summary>
        public static void SetInAppReviewProvider(IInAppReviewProvider? provider) => s_androidReview = provider;

        // Popup bật sau cùng được dùng; popup đó tắt/hủy thì quay về popup bật trước đó còn sống (vd. popup của prefab NovaSdk).
        internal static void Register(RatingPopup popup)
        {
            Popups.Remove(popup);
            Popups.Add(popup);
            s_popup = popup;
        }

        internal static void Unregister(RatingPopup popup)
        {
            Popups.Remove(popup);
            Popups.RemoveAll(p => p == null);
            if (ReferenceEquals(s_popup, popup)) s_popup = Popups.Count > 0 ? Popups[Popups.Count - 1] : null;
        }

        internal static void MarkDone()
        {
            State.Done = true;
            Save();
        }

        internal static void ReportClosed(RatingResult result, int stars)
        {
            NovaAnalytics.LogEvent("rating_result", ("result", result.ToString()), ("stars", stars));
            var handlers = Closed;
            if (handlers is null) return;
            foreach (Action<RatingResult> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(result);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        static RatingOptions CurrentOptions()
        {
            var options = s_popup != null ? s_popup.Options : RatingOptions.Default;
            return MinLevel >= 0 ? options with { MinLevel = MinLevel } : options;
        }

        static RatingState Load()
        {
            try
            {
                return RatingState.Parse(PlayerPrefs.GetString(StorageKeys.RatingState, string.Empty));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return new RatingState();
            }
        }

        static void Save()
        {
            try
            {
                PlayerPrefs.SetString(StorageKeys.RatingState, State.Serialize());
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            s_state = null;
            s_popup = null;
            Popups.Clear();
            MinLevel = -1;
            Closed = null;
            // s_androidReview giữ nguyên: module PlayInAppReview đăng ký lại khi NovaSdk init.
        }
    }
}
