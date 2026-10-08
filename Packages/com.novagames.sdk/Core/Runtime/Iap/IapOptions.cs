#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Iap
{
    /// <summary>Cấu hình IAP đã resolve theo platform (từ <see cref="IapConfig"/>).</summary>
    public sealed record IapOptions(IReadOnlyList<IapProductDefinition> Products)
    {
        public static IapOptions Disabled { get; } = new IapOptions(Array.Empty<IapProductDefinition>());

        public bool IsEnabled => Products.Count > 0;

        /// <summary>Thời gian chờ kết nối store; hết giờ thì IAP ở trạng thái Unavailable và thử lại ở lần mua sau.</summary>
        public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(20);

        public TimeSpan RestoreTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Kiểm tra receipt trước khi trao thưởng (Google Play: chữ ký với license key; iOS: StoreKit 2 tự kiểm tra).</summary>
        public bool ValidateReceipts { get; init; } = true;

        /// <summary>Dùng store giả lập: mọi lệnh mua thành công, không thanh toán thật.</summary>
        public bool UseTestStore { get; init; }
    }

    /// <summary>Phụ thuộc ngoài module IAP; để trống thì tính năng tương ứng tắt.</summary>
    public sealed class IapDependencies
    {
        /// <summary>Doanh thu purchase gửi Firebase/Adjust (một lần mỗi TransactionId).</summary>
        public IRevenuePipeline? Revenue { get; set; }

        /// <summary>Chặn app open ad khi mở popup thanh toán (vd. NovaAds.SuppressAppOpen).</summary>
        public Func<string, IDisposable>? SuppressAppOpen { get; set; }
    }
}
