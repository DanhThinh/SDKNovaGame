#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.Iap
{
    public static class StoreAdapterIds
    {
        public const string UnityIap = "unity_iap";
        public const string Test = "test";
    }

    /// <summary>Sản phẩm store trả về (giá đã bản địa hóa).</summary>
    public sealed record StoreProduct(
        string StoreId, bool IsAvailable, string PriceText, decimal Price, string Currency, string Title, string Description);

    /// <summary>Giao dịch store đã thanh toán nhưng chưa confirm. Receipt chỉ dùng để validate, không log.</summary>
    public sealed record StoreTransaction(string TransactionId, string StoreId, string Receipt);

    /// <summary>
    /// Sản phẩm store xác nhận người chơi đang sở hữu. ExpiresUtc chỉ có với subscription (null = không rõ).
    /// PendingTransactionId: giao dịch đã thanh toán nhưng chưa confirm xong; chỉ tính là sở hữu khi SDK đã trao nó.
    /// </summary>
    public sealed record StoreOwnership(string StoreId, DateTime? ExpiresUtc = null, string? PendingTransactionId = null);

    public enum StoreFailure : byte
    {
        UserCancelled,
        ProductUnavailable,
        PurchasingUnavailable,
        PurchaseInProgress,
        PaymentDeclined,
        Network,
        Unknown,
    }

    /// <summary>Adapter báo sự kiện store cho IapService. Mọi lời gọi phải trên main thread.</summary>
    public interface IStoreListener
    {
        /// <summary>Đã thanh toán, chờ trao thưởng rồi <see cref="IStoreAdapter.Confirm"/>. Store gửi lại nếu chưa confirm.</summary>
        void OnPurchasePending(StoreTransaction transaction);

        void OnPurchaseFailed(string storeId, StoreFailure reason, string message);

        /// <summary>Giao dịch chờ duyệt (Ask to Buy, thanh toán tiền mặt). Khi xong store gửi OnPurchasePending.</summary>
        void OnPurchaseDeferred(string storeId);

        /// <summary>Danh sách đầy đủ sản phẩm đang sở hữu (đã confirm) sau khi lấy giao dịch từ store hoặc restore.</summary>
        void OnOwnershipFetched(IReadOnlyList<StoreOwnership> owned);
    }

    /// <summary>
    /// SPI cho store (Unity IAP, test store). Adapter chỉ gọi API vendor và chuyển kết quả sang kiểu của SDK;
    /// validate, journal, trao thưởng, revenue do IapService xử lý.
    /// </summary>
    public interface IStoreAdapter : IDisposable
    {
        string Id { get; }

        /// <summary>Store giả lập: không validate receipt, không gửi revenue.</summary>
        bool IsTestStore { get; }

        /// <summary>Giá/tên sản phẩm store trả về sau khi init thành công.</summary>
        IReadOnlyList<StoreProduct> Products { get; }

        /// <summary>
        /// Kết nối store và lấy sản phẩm. Gọi lại khi đang chạy trả về cùng task; gọi lại sau khi lỗi thì thử lại.
        /// </summary>
        Task<SdkResult> InitializeAsync(IReadOnlyList<IapProductDefinition> products, IStoreListener listener, CancellationToken ct);

        /// <summary>Mở luồng thanh toán của store. Kết quả qua <see cref="IStoreListener"/>.</summary>
        void Purchase(string storeId);

        /// <summary>Đóng giao dịch (consume consumable / acknowledge non-consumable).</summary>
        void Confirm(string transactionId);

        /// <summary>Lấy lại giao dịch (iOS: RestoreTransactions; Android: fetch purchases). Kết quả qua listener.</summary>
        Task<SdkResult> RestoreAsync(CancellationToken ct);
    }

    public enum ReceiptValidity : byte
    {
        Valid,
        /// <summary>Receipt giả/sai: không trao thưởng, đóng giao dịch.</summary>
        Invalid,
        /// <summary>Chưa kiểm tra được (server lỗi, mạng): không trao, không confirm; store gửi lại sau.</summary>
        Indeterminate,
    }

    public interface IReceiptValidator
    {
        Task<ReceiptValidity> ValidateAsync(StoreTransaction transaction, CancellationToken ct);
    }
}
