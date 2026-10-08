#nullable enable
using System;
using Google.Play.Review;
using UnityEngine;

// Package adapters can be used only through runtime registration, without a scene reference.
[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]

namespace NovaGames.Mobile.Rating
{
    /// <summary>
    /// Google Play In-App Review: lấy ReviewInfo rồi mở hộp thoại review ngay trong game. Google tự giới hạn số lần hiện
    /// và không cho biết người chơi có gửi đánh giá hay không. Lỗi (không có Play Store, test ngoài Play) -> done(false)
    /// để NovaRating mở trang store.
    /// </summary>
    public sealed class PlayInAppReviewProvider : IInAppReviewProvider
    {
        ReviewManager? _manager;

        public void RequestReview(Action<bool> onDone)
        {
            // done luôn được gọi đúng một lần, kể cả khi API của Play throw trong callback (để trả lại app open ad).
            bool called = false;
            void done(bool ok)
            {
                if (called) return;
                called = true;
                onDone(ok);
            }

            try
            {
                _manager ??= new ReviewManager();
                var request = _manager.RequestReviewFlow();
                request.Completed += requestOperation =>
                {
                    try
                    {
                        if (requestOperation.Error != ReviewErrorCode.NoError)
                        {
                            Debug.LogWarning("[Nova][rating] Play review flow request failed: " + requestOperation.Error);
                            done(false);
                            return;
                        }
                        var launch = _manager.LaunchReviewFlow(requestOperation.GetResult());
                        launch.Completed += launchOperation =>
                        {
                            if (launchOperation.Error != ReviewErrorCode.NoError)
                                Debug.LogWarning("[Nova][rating] Play review flow launch failed: " + launchOperation.Error);
                            done(launchOperation.Error == ReviewErrorCode.NoError);
                        };
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        done(false);
                    }
                };
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                done(false);
            }
        }
    }

    static class PlayInAppReviewRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() => NovaRating.SetInAppReviewProvider(new PlayInAppReviewProvider());
    }
}
