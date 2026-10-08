#nullable enable
using System.Diagnostics.CodeAnalysis;
using UnityEngine;

namespace NovaGames.Mobile.Infrastructure
{
    /// <summary>Store không nhạy cảm. Chỉ gọi trên main thread.</summary>
    public interface IKeyValueStore
    {
        bool TryGetString(string key, [NotNullWhen(true)] out string? value);
        void SetString(string key, string value);
        void Delete(string key);
        void Flush();
    }

    public static class StorageKeys
    {
        public const string Prefix = "novagames.mobile.v1.";
        public const string RemoteConfigLastGood = Prefix + "remote_config.last_good";
        public const string IapGrantedTransactions = Prefix + "iap.granted";
        public const string IapOwnedProducts = Prefix + "iap.owned";
        public const string NotificationsScheduled = Prefix + "notifications.scheduled";
        public const string RatingState = Prefix + "rating.state";
    }

    public sealed class PlayerPrefsKeyValueStore : IKeyValueStore
    {
        public bool TryGetString(string key, [NotNullWhen(true)] out string? value)
        {
            if (!PlayerPrefs.HasKey(key))
            {
                value = null;
                return false;
            }
            value = PlayerPrefs.GetString(key);
            return true;
        }

        public void SetString(string key, string value) => PlayerPrefs.SetString(key, value);
        public void Delete(string key) => PlayerPrefs.DeleteKey(key);
        public void Flush() => PlayerPrefs.Save();
    }
}
