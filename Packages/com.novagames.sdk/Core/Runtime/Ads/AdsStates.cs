#nullable enable

namespace NovaGames.Mobile.Ads
{
    /// <summary>Trạng thái của cả module Ads.</summary>
    public enum AdsModuleState : byte
    {
        NotStarted,
        Initializing,
        Ready,
        /// <summary>Chờ consent/ATT/force update; tự init khi gate mở.</summary>
        Blocked,
        /// <summary>Không format nào chọn provider, hoặc adapter chưa cài.</summary>
        Disabled,
        /// <summary>Cấu hình sai hoặc adapter init lỗi không thử lại được.</summary>
        Failed,
        Disposed,
    }

    /// <summary>Một placement full-screen có show được ngay không, và nếu không thì vì sao.</summary>
    public enum AdAvailability : byte
    {
        /// <summary>Show được ngay.</summary>
        Ready,
        /// <summary>Đang load.</summary>
        Loading,
        /// <summary>Chưa có ad (load lỗi/no fill, đang chờ retry).</summary>
        NotReady,
        /// <summary>Bị giới hạn tần suất (interval, level bắt đầu, số lần/phiên/ngày).</summary>
        Capped,
        /// <summary>Tắt bởi cấu hình hoặc kill switch Remote Config.</summary>
        Disabled,
        /// <summary>Provider đang dùng không hỗ trợ format này.</summary>
        Unsupported,
        /// <summary>Người chơi đã mua remove_ads.</summary>
        RemovedByEntitlement,
        /// <summary>Consent/ATT/force update, app open đang bị chặn tạm, module chưa sẵn sàng hoặc đang có ad khác.</summary>
        Blocked,
    }

    /// <summary>Kết quả cuối của một lần show full-screen.</summary>
    public enum ShowOutcome : byte
    {
        /// <summary>Interstitial/app open đã hiển thị và đóng.</summary>
        Shown,
        /// <summary>Rewarded: người chơi được thưởng.</summary>
        Rewarded,
        /// <summary>Rewarded: đóng trước khi đủ điều kiện nhận thưởng.</summary>
        ClosedWithoutReward,
        NotReady,
        Capped,
        Disabled,
        Unsupported,
        RemovedByEntitlement,
        Blocked,
        /// <summary>Đang có full-screen ad khác.</summary>
        Busy,
        /// <summary>Vendor báo lỗi hiển thị hoặc không hiển thị trong DisplayTimeout.</summary>
        DisplayFailed,
        /// <summary>Token bị hủy trước khi show.</summary>
        Cancelled,
    }
}
