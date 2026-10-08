#nullable enable
using System;

namespace NovaGames.Mobile.Connectivity
{
    /// <summary>
    /// Chống nháy: chỉ báo offline khi mất mạng liên tục <see cref="OfflineAfter"/>, báo online lại khi có mạng liên tục
    /// <see cref="OnlineAfter"/>. Mạng chập chờn (chuyển Wi-Fi sang 4G, vào thang máy) không làm popup bật/tắt liên tục.
    /// </summary>
    public sealed class OfflineDetector
    {
        DateTime? _changedSinceUtc;

        public OfflineDetector(TimeSpan offlineAfter, TimeSpan onlineAfter)
        {
            OfflineAfter = offlineAfter < TimeSpan.Zero ? TimeSpan.Zero : offlineAfter;
            OnlineAfter = onlineAfter < TimeSpan.Zero ? TimeSpan.Zero : onlineAfter;
        }

        public TimeSpan OfflineAfter { get; }
        public TimeSpan OnlineAfter { get; }

        /// <summary>Trạng thái đã chống nháy.</summary>
        public bool IsOffline { get; private set; }

        /// <summary>Cập nhật với trạng thái mạng hiện tại. Trả về <see cref="IsOffline"/>.</summary>
        public bool Update(bool reachable, DateTime nowUtc)
        {
            bool offlineNow = !reachable;
            if (offlineNow == IsOffline)
            {
                _changedSinceUtc = null;
                return IsOffline;
            }

            _changedSinceUtc ??= nowUtc;
            if (nowUtc - _changedSinceUtc.Value >= (offlineNow ? OfflineAfter : OnlineAfter))
            {
                IsOffline = offlineNow;
                _changedSinceUtc = null;
            }
            return IsOffline;
        }

        /// <summary>Đặt ngay theo trạng thái mạng hiện tại, bỏ qua thời gian chờ (vd. nút Retry).</summary>
        public void ForceUpdate(bool reachable)
        {
            IsOffline = !reachable;
            _changedSinceUtc = null;
        }
    }
}
