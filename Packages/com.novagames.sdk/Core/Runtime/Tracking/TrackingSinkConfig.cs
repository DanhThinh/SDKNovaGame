#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace NovaGames.Mobile.Tracking
{
    /// <summary>
    /// Asset cấu hình của một tracking sink (vd. AdjustTrackingConfig). Lớp con nằm trong assembly adapter;
    /// game chỉ cần field kiểu này để kéo asset vào rồi đưa ToSettings vào RuntimeSdkSettings.WithSinkSettings.
    /// </summary>
    public abstract class TrackingSinkConfig : ScriptableObject
    {
        public abstract string SinkId { get; }

        public abstract ITrackingSinkSettings ToSettings(bool isDevelopment, bool isIos);

        /// <summary>Lỗi cấu hình theo platform (thiếu app token, token sai định dạng, ...).</summary>
        public abstract IReadOnlyList<string> Validate(bool isDevelopment, bool isIos);
    }
}
