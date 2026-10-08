#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Iap
{
    /// <summary>
    /// Store giả lập cho Editor/Development build: mọi lệnh mua thành công ở frame sau, không thanh toán thật, không gửi revenue.
    /// Bật bằng IAP Config (Test Store); bản release luôn dùng store thật.
    /// </summary>
    public sealed class TestStoreAdapter : IStoreAdapter
    {
        readonly IMainThreadDispatcher _main;
        readonly List<StoreProduct> _products = new List<StoreProduct>();
        IStoreListener? _listener;
        int _sequence;

        public TestStoreAdapter(IMainThreadDispatcher main)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));
        }

        public string Id => StoreAdapterIds.Test;
        public bool IsTestStore => true;
        public IReadOnlyList<StoreProduct> Products => _products;

        public Task<SdkResult> InitializeAsync(IReadOnlyList<IapProductDefinition> products, IStoreListener listener, CancellationToken ct)
        {
            _listener = listener;
            _products.Clear();
            foreach (var product in products)
            {
                var price = product.TestPrice > 0m ? product.TestPrice : 0.99m;
                _products.Add(new StoreProduct(product.StoreId, true, "$" + price.ToString("0.00", CultureInfo.InvariantCulture),
                    price, "USD", product.Id + " (test)", "Test store product"));
            }
            return Task.FromResult(SdkResult.Ok);
        }

        public void Purchase(string storeId)
        {
            var transaction = new StoreTransaction("test_" + (++_sequence) + "_" + Guid.NewGuid().ToString("N"), storeId, string.Empty);
            _main.Post(() => _listener?.OnPurchasePending(transaction));
        }

        public void Confirm(string transactionId) { }

        public Task<SdkResult> RestoreAsync(CancellationToken ct) => Task.FromResult(SdkResult.Ok);

        public void Dispose() => _listener = null;
    }
}
