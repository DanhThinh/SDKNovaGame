#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Entitlements;

namespace NovaGames.Mobile.Iap
{
    public enum IapProductType : byte
    {
        /// <summary>Mua nhiều lần (gói tiền, vật phẩm). Game trao thưởng qua consumable handler.</summary>
        Consumable,
        /// <summary>Mua một lần, sở hữu vĩnh viễn (remove ads, mở khóa).</summary>
        NonConsumable,
        /// <summary>Gói thuê bao, sở hữu tới khi hết hạn.</summary>
        Subscription,
    }

    /// <summary>
    /// Một sản phẩm trong catalog của game. <see cref="Id"/> là tên game dùng khi gọi API (vd. "remove_ads"),
    /// <see cref="StoreId"/> là product ID trên Google Play / App Store của platform hiện tại.
    /// </summary>
    public sealed record IapProductDefinition(string Id, string StoreId, IapProductType Type)
    {
        /// <summary>Quyền lợi người chơi có khi sở hữu sản phẩm (vd. <see cref="EntitlementId.RemoveAds"/>).</summary>
        public IReadOnlyList<EntitlementId> Entitlements { get; init; } = Array.Empty<EntitlementId>();

        /// <summary>Giá (USD) hiển thị khi chạy test store. Store thật lấy giá từ Google Play / App Store.</summary>
        public decimal TestPrice { get; init; }
    }

    /// <summary>Thông tin sản phẩm để hiển thị trong shop. Giá/tên lấy từ store sau khi IAP sẵn sàng.</summary>
    public sealed record IapProductInfo(
        string Id, IapProductType Type, bool IsAvailable, string PriceText, decimal Price, string Currency,
        string Title, string Description);

    public enum IapState : byte
    {
        NotInitialized,
        Initializing,
        /// <summary>Đã kết nối store và lấy được danh sách sản phẩm.</summary>
        Ready,
        /// <summary>Không kết nối được store (offline, không có Google Play, chưa cài adapter). Lần mua sau sẽ thử lại.</summary>
        Unavailable,
    }

    public enum PurchaseStatus : byte
    {
        /// <summary>Đã mua và trao thưởng xong.</summary>
        Completed,
        /// <summary>Chờ thanh toán (Ask to Buy, thanh toán chậm) hoặc chờ game trao thưởng; khi xong sẽ báo qua sự kiện Purchased.</summary>
        Pending,
        /// <summary>Người chơi tự hủy.</summary>
        Cancelled,
        Failed,
        /// <summary>Non-consumable/subscription đã sở hữu, không mua lại.</summary>
        AlreadyOwned,
        /// <summary>Store chưa sẵn sàng (offline, chưa init).</summary>
        NotReady,
    }

    public sealed record PurchaseResult(string ProductId, PurchaseStatus Status, string? TransactionId = null, string Message = "")
    {
        public bool IsSuccess => Status == PurchaseStatus.Completed;

        public override string ToString() =>
            ProductId + ": " + Status + (Message.Length > 0 ? " (" + Message + ")" : string.Empty);
    }

    /// <summary>Một lần trao sản phẩm cho người chơi (mua mới hoặc giao dịch cũ store gửi lại).</summary>
    public sealed record IapGrant(string ProductId, IapProductType Type, string TransactionId);

    /// <summary>
    /// Service IAP: mua, khôi phục, kiểm tra sở hữu. Đồng thời là <see cref="IEntitlementProvider"/> để Ads biết remove_ads.
    /// Gọi trên main thread; không throw.
    /// </summary>
    public interface IIapService : IEntitlementProvider
    {
        ISdkProperty<IapState> State { get; }
        IReadOnlyList<IapProductInfo> Products { get; }
        IapProductInfo? GetProduct(string productId);

        /// <summary>Non-consumable đã mua hoặc subscription còn hạn. Consumable luôn false.</summary>
        bool IsOwned(string productId);

        Task<PurchaseResult> PurchaseAsync(string productId, CancellationToken ct);

        /// <summary>Lấy lại giao dịch đã mua từ store (bắt buộc có nút Restore trên iOS).</summary>
        Task<SdkResult> RestoreAsync(CancellationToken ct);

        /// <summary>Mỗi lần trao sản phẩm thành công, kể cả giao dịch cũ được store gửi lại lúc mở game.</summary>
        ISdkEvent<IapGrant> Purchased { get; }

        /// <summary>
        /// Hàm trao consumable của game: cộng tiền/vật phẩm rồi trả true. Trả false hoặc throw thì SDK giữ giao dịch và
        /// gọi lại sau (lần set handler tiếp theo hoặc lần mở game sau). Mỗi TransactionId chỉ được trao một lần.
        /// </summary>
        void SetConsumableHandler(Func<IapGrant, bool>? handler);

        /// <summary>Thay validator mặc định (vd. validator gọi server của game). null = dùng validator của store adapter.</summary>
        void SetReceiptValidator(IReceiptValidator? validator);
    }
}
