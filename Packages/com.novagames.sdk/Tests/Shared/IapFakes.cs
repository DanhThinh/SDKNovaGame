#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Iap;

namespace NovaGames.Mobile.Testing
{
    // Store giả do test điều khiển: test tự gọi Deliver/Fail/Defer thay cho store thật.
    public sealed class FakeStoreAdapter : IStoreAdapter, IReceiptValidator
    {
        public readonly List<StoreProduct> StoreProducts = new List<StoreProduct>();
        public readonly List<string> Purchases = new List<string>();
        public readonly List<string> Confirmed = new List<string>();
        public IStoreListener? Listener;
        public int InitCalls;
        public int RestoreCalls;
        public bool Disposed;

        /// <summary>null = init thành công ngay.</summary>
        public Func<Task<SdkResult>>? InitResult;
        public Task<SdkResult> RestoreResult = Task.FromResult(SdkResult.Ok);
        public ReceiptValidity Validity = ReceiptValidity.Valid;
        public bool IsValidator = true;

        public string Id => "fake";
        public bool IsTestStore { get; set; }
        public IReadOnlyList<StoreProduct> Products => StoreProducts;

        public Task<SdkResult> InitializeAsync(IReadOnlyList<IapProductDefinition> products, IStoreListener listener, CancellationToken ct)
        {
            InitCalls++;
            Listener = listener;
            if (StoreProducts.Count == 0)
            {
                foreach (var product in products)
                    StoreProducts.Add(new StoreProduct(product.StoreId, true, "$1.99", 1.99m, "USD", product.Id, string.Empty));
            }
            return InitResult?.Invoke() ?? Task.FromResult(SdkResult.Ok);
        }

        public void Purchase(string storeId) => Purchases.Add(storeId);
        public void Confirm(string transactionId) => Confirmed.Add(transactionId);

        public Task<SdkResult> RestoreAsync(CancellationToken ct)
        {
            RestoreCalls++;
            return RestoreResult;
        }

        public Task<ReceiptValidity> ValidateAsync(StoreTransaction transaction, CancellationToken ct) =>
            IsValidator ? Task.FromResult(Validity) : throw new InvalidOperationException("not a validator");

        public void Deliver(string transactionId, string storeId) =>
            Listener!.OnPurchasePending(new StoreTransaction(transactionId, storeId, "receipt"));

        public void Fail(string storeId, StoreFailure reason) => Listener!.OnPurchaseFailed(storeId, reason, "test");
        public void Defer(string storeId) => Listener!.OnPurchaseDeferred(storeId);
        public void Owned(params StoreOwnership[] owned) => Listener!.OnOwnershipFetched(owned);

        public void Dispose() => Disposed = true;
    }

    public sealed class FakeReceiptValidator : IReceiptValidator
    {
        public ReceiptValidity Result = ReceiptValidity.Valid;
        public readonly List<string> Validated = new List<string>();

        public Task<ReceiptValidity> ValidateAsync(StoreTransaction transaction, CancellationToken ct)
        {
            Validated.Add(transaction.TransactionId);
            return Task.FromResult(Result);
        }
    }

    public sealed class CountingSuppression
    {
        public int Active;
        public int Total;

        public IDisposable Suppress(string reason)
        {
            Active++;
            Total++;
            return new Handle(this);
        }

        sealed class Handle : IDisposable
        {
            CountingSuppression? _owner;
            public Handle(CountingSuppression owner) { _owner = owner; }

            public void Dispose()
            {
                if (_owner != null) _owner.Active--;
                _owner = null;
            }
        }
    }
}
