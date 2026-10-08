#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Entitlements;
using UnityEngine;

namespace NovaGames.Mobile.Iap
{
    /// <summary>
    /// Danh sách sản phẩm IAP của game: <i>Create > NovaGames > IAP Config</i>, kéo vào ô IAP của NovaSdkSettings.
    /// </summary>
    [CreateAssetMenu(menuName = "NovaGames/IAP Config", fileName = "IapConfig", order = 20)]
    public sealed class IapConfig : ScriptableObject
    {
        [Tooltip("Sản phẩm bán trong game. Product Id là tên game dùng khi gọi NovaIap.Purchase(\"...\").")]
        [SerializeField] List<IapProductConfig> products = new List<IapProductConfig>();

        [Header("Receipt")]
        [Tooltip("Kiểm tra receipt trước khi trao thưởng. Android cần tạo key qua NovaGames > IAP > Google Play License Key; " +
                 "iOS dùng StoreKit 2 (tự kiểm tra).")]
        [SerializeField] bool validateReceipts = true;

        [Header("Test Store (không thanh toán thật)")]
        [Tooltip("Chạy trong Editor: mọi lệnh mua thành công ngay, không cần kết nối store.")]
        [SerializeField] bool testStoreInEditor = true;
        [Tooltip("Development build trên máy thật cũng dùng test store. Bản release luôn dùng store thật.")]
        [SerializeField] bool testStoreInDevelopmentBuild;

        public IReadOnlyList<IapProductConfig> ProductConfigs => products;

        public IapOptions ToOptions(bool isDevelopment, bool isIos, bool isEditor)
        {
            var definitions = new List<IapProductDefinition>();
            foreach (var product in products)
            {
                var definition = product?.ToDefinition(isIos);
                if (definition != null) definitions.Add(definition);
            }
            return new IapOptions(definitions)
            {
                ValidateReceipts = validateReceipts,
                UseTestStore = isEditor ? testStoreInEditor : isDevelopment && testStoreInDevelopmentBuild,
            };
        }

        /// <summary>Lỗi cấu hình (thiếu id, trùng id, ký tự không hợp lệ). Rỗng = hợp lệ.</summary>
        public IReadOnlyList<string> Validate(bool isDevelopment, bool isIos)
        {
            var issues = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var storeIds = new HashSet<string>(StringComparer.Ordinal);
            if (products.Count == 0) issues.Add("No products: IAP is disabled");
            for (int i = 0; i < products.Count; i++)
            {
                var product = products[i];
                if (product is null) continue;
                var id = product.Id;
                if (id.Length == 0)
                {
                    issues.Add("Product #" + (i + 1) + " has no Product Id");
                    continue;
                }
                if (id.IndexOf('|') >= 0 || id.IndexOf('\n') >= 0) issues.Add("Product Id '" + id + "' must not contain '|' or new lines");
                if (!ids.Add(id)) issues.Add("Duplicate Product Id '" + id + "'");
                if (product.RemoveAds && product.Type == IapProductType.Consumable)
                    issues.Add("Product '" + id + "' is Consumable: Remove Ads only works on Non Consumable/Subscription");
                var storeId = product.StoreIdFor(isIos);
                if (!storeIds.Add(storeId)) issues.Add("Duplicate " + (isIos ? "iOS" : "Android") + " store id '" + storeId + "'");
            }
            return issues;
        }
    }

    [Serializable]
    public sealed class IapProductConfig
    {
        [Tooltip("Tên game dùng khi gọi API, vd. \"remove_ads\", \"gem_pack_1\".")]
        [SerializeField] string productId = string.Empty;
        [Tooltip("Product ID trên Google Play Console. Để trống = dùng Product Id.")]
        [SerializeField] string androidStoreId = string.Empty;
        [Tooltip("Product ID trên App Store Connect. Để trống = dùng Product Id.")]
        [SerializeField] string iosStoreId = string.Empty;
        [SerializeField] IapProductType type = IapProductType.Consumable;
        [Tooltip("Mua gói này thì tắt interstitial, app open, banner, MREC (rewarded vẫn chạy). Chỉ cho Non Consumable/Subscription.")]
        [SerializeField] bool removeAds;
        [Tooltip("Quyền lợi khác game tự kiểm tra bằng NovaIap.HasEntitlement(\"...\") (vd. \"vip\").")]
        [SerializeField] string[] entitlements = Array.Empty<string>();
        [Tooltip("Giá (USD) hiển thị khi chạy test store.")]
        [SerializeField] float testPriceUsd = 0.99f;

        public IapProductConfig() { }

        public IapProductConfig(string productId, string androidStoreId, string iosStoreId, IapProductType type)
        {
            this.productId = productId ?? string.Empty;
            this.androidStoreId = androidStoreId ?? string.Empty;
            this.iosStoreId = iosStoreId ?? string.Empty;
            this.type = type;
        }

        public string Id => (productId ?? string.Empty).Trim();
        public IapProductType Type => type;
        public bool RemoveAds => removeAds;

        public string StoreIdFor(bool isIos)
        {
            var storeId = ((isIos ? iosStoreId : androidStoreId) ?? string.Empty).Trim();
            return storeId.Length > 0 ? storeId : Id;
        }

        internal IapProductDefinition? ToDefinition(bool isIos)
        {
            if (Id.Length == 0) return null;
            var granted = new List<EntitlementId>();
            if (removeAds && type != IapProductType.Consumable) granted.Add(EntitlementId.RemoveAds);
            if (type != IapProductType.Consumable && entitlements != null)
            {
                foreach (var entitlement in entitlements)
                {
                    if (!string.IsNullOrWhiteSpace(entitlement)) granted.Add(new EntitlementId(entitlement.Trim()));
                }
            }
            return new IapProductDefinition(Id, StoreIdFor(isIos), type)
            {
                Entitlements = granted,
                TestPrice = testPriceUsd > 0f ? (decimal)Math.Round(testPriceUsd, 2) : 0m,
            };
        }
    }
}
