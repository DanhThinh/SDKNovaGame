#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Iap
{
    /// <summary>
    /// Điều phối IAP, không phụ thuộc vendor. Luồng một giao dịch:
    /// store gửi pending -> validate receipt -> trao (consumable qua handler của game, non-consumable/subscription ghi sở hữu)
    /// -> ghi journal -> confirm với store -> gửi revenue (một lần mỗi TransactionId) -> sự kiện Purchased.
    /// Receipt Invalid: đóng giao dịch, không trao. Indeterminate hoặc handler chưa trao: không confirm, store gửi lại sau.
    /// </summary>
    public sealed class IapService : IIapService, IStoreListener, IDisposable
    {
        const string InitOp = "iap.initialize";
        const string RestoreOp = "iap.restore";

        readonly IStoreAdapter? _store;
        readonly IapOptions _options;
        readonly ModuleContext _ctx;
        readonly IapDependencies _deps;
        readonly ISdkLogger _log;
        readonly IapJournal _journal;
        readonly List<IapProductDefinition> _definitions = new List<IapProductDefinition>();
        readonly Dictionary<string, IapProductDefinition> _byId = new Dictionary<string, IapProductDefinition>(StringComparer.Ordinal);
        readonly Dictionary<string, IapProductDefinition> _byStoreId = new Dictionary<string, IapProductDefinition>(StringComparer.Ordinal);
        readonly SdkProperty<IapState> _state;
        readonly SdkEvent<IapGrant> _purchased;
        readonly SdkEvent<EntitlementId> _entitlementChanged;
        readonly HashSet<string> _processing = new HashSet<string>(StringComparer.Ordinal);
        // Consumable đã thanh toán + validate nhưng game chưa trao (chưa có handler hoặc handler trả false).
        readonly Dictionary<string, (IapProductDefinition Product, StoreTransaction Transaction)> _waitingForHandler =
            new Dictionary<string, (IapProductDefinition, StoreTransaction)>(StringComparer.Ordinal);

        Func<IapGrant, bool>? _consumableHandler;
        IReceiptValidator? _validator;
        Task<SdkResult>? _storeInit;
        Task<SdkResult>? _init;
        Inflight? _inflight;
        bool _disposed;

        public IapService(IStoreAdapter? store, IapOptions options, ModuleContext ctx, IapDependencies? deps = null)
        {
            _store = store;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _deps = deps ?? new IapDependencies();
            _log = ctx.Logs.Create("iap");
            _state = new SdkProperty<IapState>(IapState.NotInitialized, _log);
            _purchased = new SdkEvent<IapGrant>(_log);
            _entitlementChanged = new SdkEvent<EntitlementId>(_log);

            foreach (var product in options.Products)
            {
                if (product is null || string.IsNullOrEmpty(product.Id) || string.IsNullOrEmpty(product.StoreId)) continue;
                if (_byId.ContainsKey(product.Id) || _byStoreId.ContainsKey(product.StoreId))
                {
                    _log.Error("Duplicate IAP product '" + product.Id + "' (store id " + product.StoreId + "): ignored");
                    continue;
                }
                _definitions.Add(product);
                _byId[product.Id] = product;
                _byStoreId[product.StoreId] = product;
            }

            // Sở hữu đã lưu có hiệu lực ngay (remove_ads khi offline), store xác nhận lại sau khi kết nối.
            _journal = new IapJournal(ctx.Store, _log);
            _journal.Load();
        }

        public ISdkProperty<IapState> State => _state;
        public ISdkEvent<IapGrant> Purchased => _purchased;
        public ISdkEvent<EntitlementId> Changed => _entitlementChanged;
        public IReadOnlyList<IapProductDefinition> Definitions => _definitions;

        public IReadOnlyList<IapProductInfo> Products
        {
            get
            {
                var list = new List<IapProductInfo>(_definitions.Count);
                foreach (var product in _definitions) list.Add(ToInfo(product));
                return list;
            }
        }

        public IapProductInfo? GetProduct(string productId) =>
            productId != null && _byId.TryGetValue(productId, out var product) ? ToInfo(product) : null;

        public bool IsOwned(string productId) =>
            productId != null && _byId.TryGetValue(productId, out var product) && IsOwned(product);

        public bool IsActive(EntitlementId id)
        {
            if (id is null) return false;
            foreach (var product in _definitions)
            {
                if (Contains(product.Entitlements, id) && IsOwned(product)) return true;
            }
            return false;
        }

        public void SetConsumableHandler(Func<IapGrant, bool>? handler)
        {
            _consumableHandler = handler;
            if (handler is null || _waitingForHandler.Count == 0 || _disposed) return;
            var waiting = new List<(IapProductDefinition Product, StoreTransaction Transaction)>(_waitingForHandler.Values);
            foreach (var item in waiting)
            {
                if (_journal.IsGranted(item.Transaction.TransactionId))
                {
                    _waitingForHandler.Remove(item.Transaction.TransactionId);
                    Confirm(item.Transaction);
                }
                else
                {
                    Deliver(item.Product, item.Transaction);
                }
            }
        }

        public void SetReceiptValidator(IReceiptValidator? validator) => _validator = validator;

        // ---------------- Initialize ----------------

        /// <summary>Kết nối store. Gọi nhiều lần trả về cùng task; lỗi/timeout thì lần gọi sau thử lại.</summary>
        public Task<SdkResult> InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(InitOp));
            if (_state.Value == IapState.Ready) return Task.FromResult(SdkResult.Ok);
            return _init ??= InitializeCoreAsync(ct);
        }

        async Task<SdkResult> InitializeCoreAsync(CancellationToken ct)
        {
            if (_store is null)
            {
                _state.Set(IapState.Unavailable);
                return new SdkError(InitOp + ".no_store", SdkErrorCategory.Configuration,
                    "No store adapter installed (Unity IAP 5 package / NOVA_IAP)", false);
            }

            _state.Set(IapState.Initializing);
            var storeInit = _storeInit ??= StartStore(_store);
            var op = new VendorOperation<bool>(InitOp, _ctx.Main, _ctx.Scheduler, _options.InitTimeout, ct);
            Forward(storeInit, op, InitOp);
            var result = (await op.Task).AsResult();
            if (_disposed) return SdkError.Disposed(InitOp);

            if (result.IsSuccess)
            {
                OnStoreReady();
            }
            else
            {
                _state.Set(IapState.Unavailable);
                _log.Warning("Store not available: " + result.Error);
                _init = null;
                if (storeInit.IsCompleted) _storeInit = null;
                else _ = WatchLateStoreInitAsync(storeInit);
            }
            return result;
        }

        Task<SdkResult> StartStore(IStoreAdapter store)
        {
            try
            {
                _log.Info("Connecting to store " + store.Id + " (" + _definitions.Count + " products)");
                return store.InitializeAsync(_definitions, this, CancellationToken.None);
            }
            catch (Exception e)
            {
                return Task.FromResult<SdkResult>(SdkError.FromException(InitOp, e, store.Id));
            }
        }

        // Store kết nối xong sau khi đã hết thời gian chờ: vẫn chuyển sang Ready.
        async Task WatchLateStoreInitAsync(Task<SdkResult> storeInit)
        {
            SdkResult result;
            try
            {
                result = await storeInit;
            }
            catch (Exception e)
            {
                result = SdkError.FromException(InitOp, e, _store?.Id);
            }
            if (_disposed) return;
            if (result.IsSuccess)
            {
                if (_state.Value != IapState.Ready) OnStoreReady();
            }
            else if (ReferenceEquals(_storeInit, storeInit))
            {
                _storeInit = null;
            }
        }

        void OnStoreReady()
        {
            _init = null;
            _state.Set(IapState.Ready);
            int available = 0;
            foreach (var product in _definitions)
            {
                var storeProduct = FindStoreProduct(product.StoreId);
                if (storeProduct is { IsAvailable: true }) available++;
                else _log.Warning("Product '" + product.Id + "' (" + product.StoreId + ") is not available in the store");
            }
            _log.Info("Store ready: " + available + "/" + _definitions.Count + " products available"
                      + (_store!.IsTestStore ? " (TEST STORE: no real payment)" : string.Empty));
        }

        // ---------------- Purchase / Restore ----------------

        public async Task<PurchaseResult> PurchaseAsync(string productId, CancellationToken ct)
        {
            productId ??= string.Empty;
            if (_disposed) return new PurchaseResult(productId, PurchaseStatus.NotReady, Message: "IAP is shut down");
            if (!_byId.TryGetValue(productId, out var product))
                return new PurchaseResult(productId, PurchaseStatus.Failed, Message: "Unknown product: add it to the IAP Config");
            if (product.Type != IapProductType.Consumable && IsOwned(product))
                return new PurchaseResult(productId, PurchaseStatus.AlreadyOwned);
            if (_inflight != null)
                return new PurchaseResult(productId, PurchaseStatus.Failed, Message: "Another purchase is in progress");
            if (ct.IsCancellationRequested) return new PurchaseResult(productId, PurchaseStatus.Cancelled, Message: "Cancelled by caller");

            // Giữ chỗ trước khi chờ init để bấm nút hai lần không mở hai luồng thanh toán.
            var inflight = new Inflight(product, SafeSuppress("purchase"));
            _inflight = inflight;

            if (_state.Value != IapState.Ready)
            {
                var init = await InitializeAsync(CancellationToken.None);
                if (!init.IsSuccess) Finish(inflight, PurchaseStatus.NotReady, null, "Store is not available: " + init.Error!.Message);
            }

            if (!inflight.IsDone)
            {
                var storeProduct = FindStoreProduct(product.StoreId);
                if (storeProduct is null || !storeProduct.IsAvailable)
                {
                    Finish(inflight, PurchaseStatus.Failed, null, "Product is not available in the store (check the store product id)");
                }
                else if (!_log.TryRun("Store.Purchase", () => _store!.Purchase(product.StoreId)))
                {
                    Finish(inflight, PurchaseStatus.Failed, null, "Store threw while starting the purchase");
                }
            }

            // Hủy chỉ dừng việc chờ; nếu sau đó store báo thanh toán xong, sản phẩm vẫn được trao qua Purchased.
            using (ct.Register(() => _ctx.Main.Post(() => Finish(inflight, PurchaseStatus.Cancelled, null, "Cancelled by caller"))))
            {
                return await inflight.Task;
            }
        }

        public async Task<SdkResult> RestoreAsync(CancellationToken ct)
        {
            if (_disposed) return SdkError.Disposed(RestoreOp);
            if (_state.Value != IapState.Ready)
            {
                var init = await InitializeAsync(CancellationToken.None);
                if (!init.IsSuccess) return init;
            }

            using (SafeSuppress("restore"))
            {
                Task<SdkResult> task;
                try
                {
                    task = _store!.RestoreAsync(ct);
                }
                catch (Exception e)
                {
                    task = Task.FromResult<SdkResult>(SdkError.FromException(RestoreOp, e, _store!.Id));
                }
                var op = new VendorOperation<bool>(RestoreOp, _ctx.Main, _ctx.Scheduler, _options.RestoreTimeout, ct);
                Forward(task, op, RestoreOp);
                var result = (await op.Task).AsResult();
                if (result.IsSuccess) _log.Info("Restore finished");
                else _log.Warning("Restore failed: " + result.Error);
                return result;
            }
        }

        // ---------------- IStoreListener ----------------

        void IStoreListener.OnPurchasePending(StoreTransaction transaction)
        {
            if (_disposed || transaction is null) return;
            if (string.IsNullOrEmpty(transaction.TransactionId))
            {
                _log.Error("Store delivered a purchase of '" + transaction.StoreId + "' without a transaction id: ignored");
                return;
            }
            if (!_byStoreId.TryGetValue(transaction.StoreId ?? string.Empty, out var product))
            {
                _log.Error("Store delivered a purchase of '" + transaction.StoreId + "' which is not in the IAP Config: left unconfirmed");
                return;
            }
            if (!_processing.Add(transaction.TransactionId)) return;
            _ = ProcessAsync(product, transaction);
        }

        void IStoreListener.OnPurchaseFailed(string storeId, StoreFailure reason, string message)
        {
            if (_disposed) return;
            var status = reason == StoreFailure.UserCancelled ? PurchaseStatus.Cancelled : PurchaseStatus.Failed;
            var text = reason + (string.IsNullOrEmpty(message) ? string.Empty : ": " + message);
            _log.Info("Purchase of '" + storeId + "' failed: " + text);
            if (storeId != null && _byStoreId.TryGetValue(storeId, out var product)) CompleteInflight(product, status, null, text);
            else if (_inflight != null) Finish(_inflight, status, null, text);
        }

        void IStoreListener.OnPurchaseDeferred(string storeId)
        {
            if (_disposed || storeId is null || !_byStoreId.TryGetValue(storeId, out var product)) return;
            _log.Info("Purchase of '" + product.Id + "' is waiting for payment approval");
            CompleteInflight(product, PurchaseStatus.Pending, null, "Waiting for payment approval");
        }

        void IStoreListener.OnOwnershipFetched(IReadOnlyList<StoreOwnership> owned)
        {
            if (_disposed || owned is null) return;
            var before = ActiveEntitlements();
            var subscriptions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in owned)
            {
                if (item?.StoreId is null || !_byStoreId.TryGetValue(item.StoreId, out var product)) continue;
                // Giao dịch chưa confirm mà SDK chưa trao (chưa kiểm tra receipt) thì chưa tính là sở hữu.
                if (item.PendingTransactionId != null && !_journal.IsGranted(item.PendingTransactionId)) continue;
                if (product.Type == IapProductType.NonConsumable)
                {
                    _journal.SetOwned(product.Id, DateTime.MaxValue);
                }
                else if (product.Type == IapProductType.Subscription)
                {
                    _journal.SetOwned(product.Id, item.ExpiresUtc ?? DateTime.MaxValue);
                    subscriptions.Add(product.Id);
                }
            }
            // Subscription lấy theo store: không còn trong danh sách = đã hết hạn/hủy.
            // Non-consumable không tự gỡ (store có thể trả danh sách thiếu khi lỗi tạm thời).
            foreach (var product in _definitions)
            {
                if (product.Type == IapProductType.Subscription && !subscriptions.Contains(product.Id)) _journal.RemoveOwned(product.Id);
            }
            _journal.Save();
            RaiseEntitlementChanges(before);
        }

        // ---------------- Transaction pipeline ----------------

        async Task ProcessAsync(IapProductDefinition product, StoreTransaction transaction)
        {
            try
            {
                if (_journal.IsGranted(transaction.TransactionId))
                {
                    // Đã trao ở lần trước nhưng chưa kịp confirm (thoát app, mất mạng): chỉ confirm, không trao lại.
                    _log.Info("Transaction for '" + product.Id + "' was already granted: confirming only");
                    Confirm(transaction);
                    CompleteInflight(product, PurchaseStatus.Completed, transaction.TransactionId, string.Empty);
                    return;
                }

                var validity = await ValidateAsync(transaction);
                if (_disposed) return;
                if (validity == ReceiptValidity.Invalid)
                {
                    _log.Warning("Receipt of '" + product.Id + "' is invalid: not granted, transaction closed");
                    Confirm(transaction);
                    CompleteInflight(product, PurchaseStatus.Failed, transaction.TransactionId, "Receipt validation failed");
                    return;
                }
                if (validity == ReceiptValidity.Indeterminate)
                {
                    _log.Warning("Receipt of '" + product.Id + "' could not be verified yet: left unconfirmed, the store will deliver it again");
                    CompleteInflight(product, PurchaseStatus.Pending, transaction.TransactionId, "Receipt could not be verified yet");
                    return;
                }

                Deliver(product, transaction);
            }
            catch (Exception e)
            {
                _log.Error("Processing the purchase of '" + product.Id + "' threw", e);
                CompleteInflight(product, PurchaseStatus.Failed, transaction.TransactionId, "Internal error");
            }
            finally
            {
                _processing.Remove(transaction.TransactionId);
            }
        }

        async Task<ReceiptValidity> ValidateAsync(StoreTransaction transaction)
        {
            if (!_options.ValidateReceipts || _store is null || _store.IsTestStore) return ReceiptValidity.Valid;
            var validator = _validator ?? _store as IReceiptValidator;
            if (validator is null) return ReceiptValidity.Valid;
            try
            {
                return await validator.ValidateAsync(transaction, CancellationToken.None);
            }
            catch (Exception e)
            {
                _log.Error("Receipt validator threw", e);
                return ReceiptValidity.Indeterminate;
            }
        }

        void Deliver(IapProductDefinition product, StoreTransaction transaction)
        {
            var grant = new IapGrant(product.Id, product.Type, transaction.TransactionId);
            var before = ActiveEntitlements();
            if (product.Type == IapProductType.Consumable)
            {
                if (!RunConsumableHandler(grant))
                {
                    _waitingForHandler[transaction.TransactionId] = (product, transaction);
                    CompleteInflight(product, PurchaseStatus.Pending, transaction.TransactionId, _consumableHandler is null
                        ? "Paid, waiting for NovaIap.SetConsumableHandler to grant it"
                        : "Paid, the consumable handler has not granted it yet");
                    return;
                }
                _waitingForHandler.Remove(transaction.TransactionId);
            }
            else
            {
                // Subscription: hạn thật được cập nhật ở lần lấy giao dịch từ store kế tiếp.
                _journal.SetOwned(product.Id, DateTime.MaxValue);
            }

            _journal.MarkGranted(transaction.TransactionId);
            _journal.Save();
            Confirm(transaction);
            ReportRevenue(product, transaction);
            _log.Info("Granted '" + product.Id + "'");
            _purchased.Raise(grant);
            RaiseEntitlementChanges(before);
            CompleteInflight(product, PurchaseStatus.Completed, transaction.TransactionId, string.Empty);
        }

        bool RunConsumableHandler(IapGrant grant)
        {
            var handler = _consumableHandler;
            if (handler is null)
            {
                _log.Warning("Consumable '" + grant.ProductId + "' is paid but no consumable handler is set: kept until NovaIap.SetConsumableHandler");
                return false;
            }
            try
            {
                return handler(grant);
            }
            catch (Exception e)
            {
                _log.Error("Consumable handler threw for '" + grant.ProductId + "': kept and retried later", e);
                return false;
            }
        }

        void Confirm(StoreTransaction transaction) =>
            _log.TryRun("Store.Confirm", () => _store?.Confirm(transaction.TransactionId));

        void ReportRevenue(IapProductDefinition product, StoreTransaction transaction)
        {
            var revenue = _deps.Revenue;
            if (revenue is null || _store is null || _store.IsTestStore) return;
            var storeProduct = FindStoreProduct(product.StoreId);
            if (storeProduct is null || storeProduct.Price <= 0m || string.IsNullOrEmpty(storeProduct.Currency))
            {
                _log.Warning("No store price for '" + product.Id + "': purchase revenue not reported");
                return;
            }
            var e = new PurchaseRevenueEvent(Guid.NewGuid(), transaction.TransactionId, product.Id,
                (double)storeProduct.Price, storeProduct.Currency);
            _log.TryRun("ReportPurchase", () => revenue.ReportPurchase(e));
        }

        // ---------------- Helpers ----------------

        bool IsOwned(IapProductDefinition product) =>
            product.Type != IapProductType.Consumable
            && _journal.Owned.TryGetValue(product.Id, out var expiresUtc)
            && expiresUtc > _ctx.Clock.UtcNow;

        StoreProduct? FindStoreProduct(string storeId)
        {
            if (_store is null) return null;
            try
            {
                foreach (var product in _store.Products)
                {
                    if (product != null && string.Equals(product.StoreId, storeId, StringComparison.Ordinal)) return product;
                }
            }
            catch (Exception e)
            {
                _log.Error("Store.Products threw", e);
            }
            return null;
        }

        IapProductInfo ToInfo(IapProductDefinition product)
        {
            var storeProduct = FindStoreProduct(product.StoreId);
            return storeProduct is null
                ? new IapProductInfo(product.Id, product.Type, false, string.Empty, 0m, string.Empty, product.Id, string.Empty)
                : new IapProductInfo(product.Id, product.Type, storeProduct.IsAvailable, storeProduct.PriceText, storeProduct.Price,
                    storeProduct.Currency, storeProduct.Title, storeProduct.Description);
        }

        HashSet<EntitlementId> ActiveEntitlements()
        {
            var active = new HashSet<EntitlementId>();
            foreach (var product in _definitions)
            {
                if (!IsOwned(product)) continue;
                foreach (var id in product.Entitlements) active.Add(id);
            }
            return active;
        }

        void RaiseEntitlementChanges(HashSet<EntitlementId> before)
        {
            var after = ActiveEntitlements();
            foreach (var id in after)
            {
                if (!before.Contains(id)) _entitlementChanged.Raise(id);
            }
            foreach (var id in before)
            {
                if (!after.Contains(id)) _entitlementChanged.Raise(id);
            }
        }

        static bool Contains(IReadOnlyList<EntitlementId> list, EntitlementId id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == id) return true;
            }
            return false;
        }

        IDisposable? SafeSuppress(string reason)
        {
            var suppress = _deps.SuppressAppOpen;
            if (suppress is null) return null;
            try
            {
                return suppress(reason);
            }
            catch (Exception e)
            {
                _log.Error("SuppressAppOpen threw", e);
                return null;
            }
        }

        void CompleteInflight(IapProductDefinition product, PurchaseStatus status, string? transactionId, string message)
        {
            var inflight = _inflight;
            if (inflight != null && ReferenceEquals(inflight.Product, product)) Finish(inflight, status, transactionId, message);
        }

        void Finish(Inflight inflight, PurchaseStatus status, string? transactionId, string message)
        {
            if (inflight.IsDone) return;
            inflight.IsDone = true;
            if (ReferenceEquals(_inflight, inflight)) _inflight = null;
            _log.TryRun("Release app open suppression", () => inflight.Suppression?.Dispose());
            var result = new PurchaseResult(inflight.Product.Id, status, transactionId, message);
            _log.Info("Purchase " + result);
            inflight.Complete(result);
        }

        // Kết quả task store -> VendorOperation (timeout/cancel, deliver trên main thread).
        static void Forward(Task<SdkResult> task, VendorOperation<bool> op, string operation)
        {
            task.ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion) op.Complete(VendorTask.FaultToError(t, operation));
                    else if (t.Result.IsSuccess) op.Complete(SdkResult<bool>.Ok(true));
                    else op.Complete(t.Result.Error!);
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var inflight = _inflight;
            if (inflight != null) Finish(inflight, PurchaseStatus.NotReady, null, "IAP is shut down");
            _log.TryRun("Store.Dispose", () => _store?.Dispose());
            _purchased.Clear();
            _entitlementChanged.Clear();
            _state.ClearSubscribers();
            _consumableHandler = null;
        }

        sealed class Inflight
        {
            readonly TaskCompletionSource<PurchaseResult> _tcs =
                new TaskCompletionSource<PurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Inflight(IapProductDefinition product, IDisposable? suppression)
            {
                Product = product;
                Suppression = suppression;
            }

            public IapProductDefinition Product { get; }
            public IDisposable? Suppression { get; }
            public bool IsDone { get; set; }
            public Task<PurchaseResult> Task => _tcs.Task;

            public void Complete(PurchaseResult result) => _tcs.TrySetResult(result);
        }
    }
}
