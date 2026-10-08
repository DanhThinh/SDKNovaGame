#nullable enable
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace NovaGames.Mobile.Rating
{
    /// <summary>
    /// Popup mời đánh giá (5 sao). Kéo prefab <c>Rating/Prefabs/RatingPopup</c> vào scene cần hỏi rồi gọi
    /// <c>NovaRating.ShowIfEligible(level)</c>. Luồng:
    /// <list type="number">
    /// <item>Chọn sao (sáng dần). Đủ <see cref="storeReviewMinStars"/> sao: mở hộp thoại review của store
    /// (tự mở ngay nếu bật Auto Rate On Max Stars).</item>
    /// <item>Ít sao: chuyển sang bước 2 "No thanks / Send feedback" (không đẩy người chơi không hài lòng lên store).</item>
    /// <item>Later: hỏi lại sau thời gian chờ. Never / No thanks / Feedback / đã lên store: không hỏi lại.</item>
    /// </list>
    /// Sửa chữ, màu, ảnh trực tiếp trong prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RatingPopup : MonoBehaviour
    {
        [Header("Scene references")]
        [SerializeField] GameObject? panel;
        [SerializeField] GameObject? stepStars;
        [SerializeField] GameObject? stepFeedback;
        [Tooltip("5 nút sao, theo thứ tự trái sang phải.")]
        [SerializeField] Button[] stars = Array.Empty<Button>();
        [SerializeField] Button? rateButton;
        [SerializeField] Button? laterButton;
        [SerializeField] Button? neverButton;
        [SerializeField] Button? noThanksButton;
        [SerializeField] Button? feedbackButton;

        [Header("Stars")]
        [SerializeField] Color starOnColor = new Color(1f, 0.76f, 0.15f, 1f);
        [SerializeField] Color starOffColor = new Color(0.78f, 0.80f, 0.85f, 1f);
        [Tooltip("Thời gian giữa hai sao khi sáng dần (giây).")]
        [SerializeField, Min(0f)] float starAnimationStep = 0.08f;

        [Header("Rules")]
        [Tooltip("Chỉ hỏi từ level này. NovaRating.MinLevel (vd. từ Remote Config) ghi đè giá trị này.")]
        [SerializeField, Min(0)] int minLevel = 3;
        [Tooltip("Sau khi bấm Later, chờ bao nhiêu giờ mới hỏi lại.")]
        [SerializeField, Min(0f)] float laterCooldownHours = 24f;
        [Tooltip("Số lần hỏi tối đa (0 = không giới hạn).")]
        [SerializeField, Min(0)] int maxPrompts;
        [Tooltip("Số sao tối thiểu để mở hộp thoại review của store.")]
        [SerializeField, Range(1, 5)] int storeReviewMinStars = 5;
        [Tooltip("Chọn 5 sao thì mở store ngay, không cần bấm Rate.")]
        [SerializeField] bool autoRateOnMaxStars = true;

        [Header("Store")]
        [Tooltip("Apple App ID (số trong link App Store) để mở trang đánh giá khi iOS không hiện được hộp thoại.")]
        [SerializeField] string appleAppId = string.Empty;
        [Tooltip("Email nhận góp ý khi người chơi chọn ít sao. Để trống = nút Send Feedback chỉ đóng popup.")]
        [SerializeField] string feedbackEmail = string.Empty;
        [Tooltip("Giữ popup qua mọi scene (DontDestroyOnLoad). Tắt nếu chỉ đặt trong một scene.")]
        [SerializeField] bool keepAcrossScenes;

        int _stars;
        bool _open;
        Coroutine? _animation;
        IDisposable? _mrecHidden;
        IDisposable? _appOpenSuppressed;

        /// <summary>Luật hiện popup từ Inspector.</summary>
        public RatingOptions Options => new RatingOptions
        {
            MinLevel = minLevel,
            LaterCooldown = TimeSpan.FromHours(laterCooldownHours),
            MaxPrompts = maxPrompts,
        };

        public bool IsOpen => _open;

        void Awake()
        {
            if (keepAcrossScenes)
            {
                transform.SetParent(null);
                DontDestroyOnLoad(gameObject);
            }
            if (panel != null) panel.SetActive(false);
            if (appleAppId.Length > 0 && NovaRating.AppleAppId.Length == 0) NovaRating.AppleAppId = appleAppId.Trim();

            for (int i = 0; i < stars.Length; i++)
            {
                int count = i + 1;
                if (stars[i] != null) stars[i].onClick.AddListener(() => ChooseStars(count));
            }
            rateButton?.onClick.AddListener(Submit);
            laterButton?.onClick.AddListener(() => Close(RatingResult.Later, markDone: false));
            neverButton?.onClick.AddListener(() => Close(RatingResult.Never, markDone: true));
            noThanksButton?.onClick.AddListener(() => Close(RatingResult.NoThanks, markDone: true));
            feedbackButton?.onClick.AddListener(SendFeedback);
        }

        // Popup mới nhất trong scene là popup được dùng.
        void OnEnable() => NovaRating.Register(this);
        void OnDestroy() => NovaRating.Unregister(this);

        /// <summary>Mở popup (NovaRating.Show gọi). Không kiểm tra điều kiện.</summary>
        internal void Open()
        {
            if (_open) return;
            _open = true;
            _stars = 0;
            if (panel != null) panel.SetActive(true);
            if (stepStars != null) stepStars.SetActive(true);
            if (stepFeedback != null) stepFeedback.SetActive(false);
            PaintStars(0);
            if (rateButton != null) rateButton.interactable = false;
            // MREC/app open là view native nằm trên UI Unity.
            _mrecHidden = NovaAds.HideMRecsTemporarily();
            _appOpenSuppressed = NovaAds.SuppressAppOpen("rating");
        }

        void ChooseStars(int count)
        {
            _stars = count;
            if (_animation != null) StopCoroutine(_animation);
            _animation = StartCoroutine(AnimateStars(count));
        }

        IEnumerator AnimateStars(int count)
        {
            PaintStars(0);
            for (int i = 1; i <= count; i++)
            {
                PaintStars(i);
                if (starAnimationStep > 0f) yield return new WaitForSecondsRealtime(starAnimationStep);
            }
            _animation = null;
            if (rateButton != null) rateButton.interactable = true;
            if (autoRateOnMaxStars && count >= stars.Length && count >= storeReviewMinStars) Submit();
        }

        void Submit()
        {
            if (!_open || _stars == 0) return;
            if (_stars >= storeReviewMinStars)
            {
                Close(RatingResult.StoreReview, markDone: true);
                NovaRating.RequestStoreReview();
                return;
            }
            if (stepStars != null) stepStars.SetActive(false);
            if (stepFeedback != null) stepFeedback.SetActive(true);
        }

        void SendFeedback()
        {
            if (feedbackEmail.Length > 0)
            {
                var subject = Uri.EscapeDataString(Application.productName + " feedback (" + Application.version + ", " + _stars + " stars)");
                Application.OpenURL("mailto:" + feedbackEmail.Trim() + "?subject=" + subject);
            }
            Close(RatingResult.Feedback, markDone: true);
        }

        void Close(RatingResult result, bool markDone)
        {
            if (!_open) return;
            _open = false;
            if (_animation != null)
            {
                StopCoroutine(_animation);
                _animation = null;
            }
            if (markDone) NovaRating.MarkDone();
            if (panel != null) panel.SetActive(false);
            _appOpenSuppressed?.Dispose();
            _appOpenSuppressed = null;
            _mrecHidden?.Dispose();
            _mrecHidden = null;
            NovaRating.ReportClosed(result, _stars);
        }

        void PaintStars(int lit)
        {
            for (int i = 0; i < stars.Length; i++)
            {
                var image = stars[i] != null ? stars[i].targetGraphic : null;
                if (image != null) image.color = i < lit ? starOnColor : starOffColor;
            }
        }

        void OnDisable()
        {
            // Popup bị tắt/đổi scene khi đang mở: trả lại MREC/app open. Popup đang tắt không nhận lệnh mở (sẽ không hiện).
            if (_open) Close(RatingResult.Later, markDone: false);
            NovaRating.Unregister(this);
        }
    }
}
