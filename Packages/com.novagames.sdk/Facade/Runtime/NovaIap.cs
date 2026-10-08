#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// API mua hàng trong game (In-App Purchase): hàm tĩnh, sản phẩm gọi bằng Product Id trong IAP Config.
    /// <code>
    /// // Lúc khởi động (trước hoặc sau NovaSdk.InitializeAsync): cách trao consumable.
    /// NovaIap.SetConsumableHandler(grant => { Wallet.AddGems(GemsOf(grant.ProductId)); return true; });
    ///
    /// // Nút mua:
    /// NovaIap.Purchase("remove_ads", result => { if (result.IsSuccess) ShowThanks(); });
    /// priceLabel.text = NovaIap.GetPriceText("gem_pack_1", "...");
    /// </code>
    /// SDK tự kiểm tra receipt, chống trao trùng, confirm với store, gửi doanh thu tới Firebase/Adjust, chặn app open ad
    /// lúc thanh toán và bật remove_ads cho <see cref="NovaAds"/>. Gọi trên main thread; không throw; callback trên main thread.
    /// </summary>
    public static class NovaIap
    {
        static IIapService? s_iap;
        static ISdkLogger? s_log;
        static IDisposable? s_purchasedSubscription;
        static Func<IapGrant, bool>? s_consumableHandler;
        static IReceiptValidator? s_validator;

        /// <summary>
        /// Mỗi lần người chơi nhận một sản phẩm: mua mới, giao dịch chờ duyệt vừa xong, hoặc giao dịch cũ store gửi lại.
        /// Dùng để cập nhật UI shop. Consumable đã được trao qua <see cref="SetConsumableHandler"/> trước sự kiện này.
        /// </summary>
        public static event Action<IapGrant>? OnPurchased;

        /// <summary>Gắn service IAP. <see cref="NovaSdk.InitializeAsync"/> tự gọi.</summary>
        public static void Bind(IIapService iap, ISdkLogger log)
        {
            s_purchasedSubscription?.Dispose();
            s_iap = iap ?? throw new ArgumentNullException(nameof(iap));
            s_log = log ?? throw new ArgumentNullException(nameof(log));
            s_purchasedSubscription = iap.Purchased.Subscribe(RaisePurchased);
            if (s_validator != null) s_log.TryRun("SetReceiptValidator", () => iap.SetReceiptValidator(s_validator));
            if (s_consumableHandler != null) s_log.TryRun("SetConsumableHandler", () => iap.SetConsumableHandler(s_consumableHandler));
        }

        /// <summary>Gỡ service đã <see cref="Bind"/> (chỉ khi đúng service đó).</summary>
        public static void Unbind(IIapService iap)
        {
            if (!ReferenceEquals(s_iap, iap)) return;
            s_purchasedSubscription?.Dispose();
            s_purchasedSubscription = null;
            s_iap = null;
        }

        /// <summary>Đã kết nối store và có giá sản phẩm.</summary>
        public static bool IsReady => s_iap?.State.Value == IapState.Ready;

        /// <summary>API đầy đủ (Task, trạng thái, sự kiện). Null khi IAP chưa bật. Game thông thường không cần dùng.</summary>
        public static IIapService? Service => s_iap;

        /// <summary>Tất cả sản phẩm trong IAP Config kèm giá store (giá rỗng khi store chưa sẵn sàng).</summary>
        public static IReadOnlyList<IapProductInfo> Products =>
            Safe("Products", iap => iap.Products, (IReadOnlyList<IapProductInfo>)Array.Empty<IapProductInfo>());

        /// <summary>Thông tin một sản phẩm (giá, tên store). null nếu Product Id không có trong IAP Config.</summary>
        public static IapProductInfo? GetProduct(string productId) => Safe("GetProduct", iap => iap.GetProduct(productId), null);

        /// <summary>Giá bản địa hóa để hiện lên nút (vd. "22.000 ₫"). Trả <paramref name="fallback"/> khi store chưa sẵn sàng.</summary>
        public static string GetPriceText(string productId, string fallback = "")
        {
            var product = GetProduct(productId);
            return product != null && !string.IsNullOrEmpty(product.PriceText) ? product.PriceText : fallback;
        }

        /// <summary>Non-consumable đã mua hoặc subscription còn hạn. Có hiệu lực ngay lúc mở game, kể cả offline.</summary>
        public static bool IsOwned(string productId) => Safe("IsOwned", iap => iap.IsOwned(productId), false);

        /// <summary>Quyền lợi khai báo trong IAP Config đang có hiệu lực (vd. "vip", "remove_ads").</summary>
        public static bool HasEntitlement(string entitlement) =>
            !string.IsNullOrEmpty(entitlement) && Safe("HasEntitlement", iap => iap.IsActive(new EntitlementId(entitlement)), false);

        /// <summary>
        /// Mua sản phẩm. <paramref name="onDone"/> luôn được gọi đúng một lần (kể cả khi chưa init, sai id, người chơi hủy).
        /// <c>result.IsSuccess</c> = đã trao xong. Status Pending = chờ thanh toán; khi xong SDK trao qua handler/OnPurchased.
        /// </summary>
        public static void Purchase(string productId, Action<PurchaseResult>? onDone = null) => _ = PurchaseWithCallbackAsync(productId, onDone);

        /// <summary>Như <see cref="Purchase"/> nhưng dùng await.</summary>
        public static Task<PurchaseResult> PurchaseAsync(string productId, CancellationToken ct = default)
        {
            var iap = s_iap;
            if (iap is null) return Task.FromResult(NotReady(productId));
            try
            {
                return iap.PurchaseAsync(productId, ct);
            }
            catch (Exception e)
            {
                s_log?.Error("PurchaseAsync threw", e);
                return Task.FromResult(new PurchaseResult(productId ?? string.Empty, PurchaseStatus.Failed, Message: e.Message));
            }
        }

        /// <summary>
        /// Khôi phục giao dịch đã mua (bắt buộc có nút Restore trên iOS). Sản phẩm khôi phục (non-consumable, subscription)
        /// đọc bằng <see cref="IsOwned"/> / <see cref="HasEntitlement"/> trong <paramref name="onDone"/>; <see cref="OnPurchased"/>
        /// chỉ báo giao dịch mới. <paramref name="onDone"/>: true nếu store trả lời thành công.
        /// </summary>
        public static void Restore(Action<bool>? onDone = null) => _ = RestoreWithCallbackAsync(onDone);

        /// <summary>
        /// Hàm trao consumable (tiền, vật phẩm): cộng thưởng, lưu save game rồi trả true. Trả false hoặc throw thì SDK giữ giao
        /// dịch và gọi lại sau. Mỗi giao dịch chỉ được trao một lần. Nên gọi ngay lúc khởi động để nhận cả giao dịch
        /// chưa trao từ lần chơi trước. Gọi trước <see cref="NovaSdk.InitializeAsync"/> cũng được.
        /// </summary>
        public static void SetConsumableHandler(Func<IapGrant, bool>? handler)
        {
            s_consumableHandler = handler;
            var iap = s_iap;
            if (iap != null) s_log.TryRun("SetConsumableHandler", () => iap.SetConsumableHandler(handler));
        }

        /// <summary>Validator riêng (vd. gọi server của game). Mặc định SDK kiểm tra receipt trên máy.</summary>
        public static void SetReceiptValidator(IReceiptValidator? validator)
        {
            s_validator = validator;
            var iap = s_iap;
            if (iap != null) s_log.TryRun("SetReceiptValidator", () => iap.SetReceiptValidator(validator));
        }

        static async Task PurchaseWithCallbackAsync(string productId, Action<PurchaseResult>? onDone)
        {
            PurchaseResult result;
            try
            {
                result = await PurchaseAsync(productId);
            }
            catch (Exception e)
            {
                s_log?.Error("Purchase threw", e);
                result = new PurchaseResult(productId ?? string.Empty, PurchaseStatus.Failed, Message: e.Message);
            }
            Invoke(onDone, result, "onDone");
        }

        static async Task RestoreWithCallbackAsync(Action<bool>? onDone)
        {
            var iap = s_iap;
            bool success = false;
            if (iap != null)
            {
                try
                {
                    success = (await iap.RestoreAsync(CancellationToken.None)).IsSuccess;
                }
                catch (Exception e)
                {
                    s_log?.Error("Restore threw", e);
                }
            }
            Invoke(onDone, success, "onDone");
        }

        static PurchaseResult NotReady(string? productId) =>
            new PurchaseResult(productId ?? string.Empty, PurchaseStatus.NotReady,
                Message: "IAP is not enabled: call NovaSdk.InitializeAsync with an IAP Config");

        static T Safe<T>(string what, Func<IIapService, T> read, T fallback)
        {
            var iap = s_iap;
            if (iap is null) return fallback;
            try
            {
                return read(iap);
            }
            catch (Exception e)
            {
                s_log?.Error(what + " threw", e);
                return fallback;
            }
        }

        static void RaisePurchased(IapGrant grant)
        {
            var handlers = OnPurchased;
            if (handlers is null) return;
            foreach (Action<IapGrant> handler in handlers.GetInvocationList()) Invoke(handler, grant, "OnPurchased");
        }

        static void Invoke<T>(Action<T>? callback, T value, string name)
        {
            if (callback is null) return;
            try
            {
                callback(value);
            }
            catch (Exception e)
            {
                // Lỗi của game không được làm hỏng IAP; vẫn log để thấy.
                if (s_log != null) s_log.Error("Game callback " + name + " threw", e);
                else Debug.LogException(e);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_purchasedSubscription?.Dispose();
            s_purchasedSubscription = null;
            s_iap = null;
            s_log = null;
            s_consumableHandler = null;
            s_validator = null;
            OnPurchased = null;
        }
    }
}
