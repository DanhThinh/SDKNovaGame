#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Iap
{
    /// <summary>
    /// Lưu trên máy: TransactionId đã trao (chống trao trùng khi store gửi lại giao dịch) và sản phẩm đang sở hữu
    /// (remove_ads có hiệu lực ngay lúc mở game, kể cả offline). Chỉ dùng trên main thread.
    /// </summary>
    internal sealed class IapJournal
    {
        // Giữ đủ lâu để store không thể gửi lại giao dịch cũ hơn mốc này.
        internal const int MaxGrantedTransactions = 300;

        readonly IKeyValueStore _store;
        readonly ISdkLogger _log;
        readonly List<string> _grantedOrder = new List<string>();
        readonly HashSet<string> _granted = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, DateTime> _owned = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public IapJournal(IKeyValueStore store, ISdkLogger log)
        {
            _store = store;
            _log = log;
        }

        /// <summary>Sản phẩm đang sở hữu -> hết hạn (UTC). Non-consumable: DateTime.MaxValue.</summary>
        public IReadOnlyDictionary<string, DateTime> Owned => _owned;

        public void Load()
        {
            try
            {
                if (_store.TryGetString(StorageKeys.IapGrantedTransactions, out var granted))
                {
                    foreach (var line in granted.Split('\n'))
                    {
                        if (line.Length > 0 && _granted.Add(line)) _grantedOrder.Add(line);
                    }
                }
                if (_store.TryGetString(StorageKeys.IapOwnedProducts, out var owned))
                {
                    foreach (var line in owned.Split('\n'))
                    {
                        int separator = line.IndexOf('|');
                        if (separator <= 0 || separator == line.Length - 1) continue;
                        if (!long.TryParse(line.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) continue;
                        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                        _owned[line.Substring(separator + 1)] = new DateTime(ticks, DateTimeKind.Utc);
                    }
                }
            }
            catch (Exception e)
            {
                _log.Error("Could not read the IAP journal", e);
            }
        }

        public bool IsGranted(string transactionId) => _granted.Contains(transactionId);

        public void MarkGranted(string transactionId)
        {
            if (!IsStorable(transactionId) || !_granted.Add(transactionId)) return;
            _grantedOrder.Add(transactionId);
            while (_grantedOrder.Count > MaxGrantedTransactions)
            {
                _granted.Remove(_grantedOrder[0]);
                _grantedOrder.RemoveAt(0);
            }
        }

        public void SetOwned(string productId, DateTime expiresUtc)
        {
            if (IsStorable(productId)) _owned[productId] = expiresUtc;
        }

        public bool RemoveOwned(string productId) => _owned.Remove(productId);

        /// <summary>Ghi xuống storage. Lỗi ghi chỉ log: trạng thái trong bộ nhớ vẫn đúng cho phiên hiện tại.</summary>
        public void Save()
        {
            try
            {
                _store.SetString(StorageKeys.IapGrantedTransactions, string.Join("\n", _grantedOrder));
                var owned = new StringBuilder();
                foreach (var pair in _owned)
                {
                    if (owned.Length > 0) owned.Append('\n');
                    owned.Append(pair.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|').Append(pair.Key);
                }
                _store.SetString(StorageKeys.IapOwnedProducts, owned.ToString());
                _store.Flush();
            }
            catch (Exception e)
            {
                _log.Error("Could not save the IAP journal", e);
            }
        }

        // Định dạng lưu tách dòng bằng '\n'.
        static bool IsStorable(string value) => !string.IsNullOrEmpty(value) && value.IndexOf('\n') < 0;
    }
}
