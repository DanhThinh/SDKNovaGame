#nullable enable
using System;
using System.Globalization;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Ads
{
    // Trạng thái capping interstitial: lưu persistent (khoảng cách tính cả qua lần mở app), đếm session in-memory.
    // Format lưu: "1|<inter close ticks>|<rewarded close ticks>|<yyyyMMdd UTC>|<count trong ngày>".
    internal sealed class AdsCapping
    {
        const string Key = StorageKeys.Prefix + "ads.capping";
        const string Version = "1";

        readonly IKeyValueStore _store;
        readonly IClock _clock;
        readonly ISdkLogger _log;

        DateTime _lastInterstitialClosedUtc = DateTime.MinValue;
        DateTime _lastRewardedClosedUtc = DateTime.MinValue;
        int _day;
        int _dayCount;
        int _sessionCount;

        public AdsCapping(IKeyValueStore store, IClock clock, ISdkLogger log)
        {
            _store = store;
            _clock = clock;
            _log = log;
            Load();
        }

        public int DayCount => DayKey(_clock.UtcNow) == _day ? _dayCount : 0;

        /// <summary>null = được phép show.</summary>
        public string? CheckInterstitial(AdsPolicy policy, int playerLevel)
        {
            var now = _clock.UtcNow;
            if (playerLevel < policy.InterstitialStartLevel) return "level " + playerLevel + " < " + policy.InterstitialStartLevel;
            if (Within(now, _lastInterstitialClosedUtc, policy.InterstitialInterval)) return "interval";
            if (Within(now, _lastRewardedClosedUtc, policy.InterstitialAfterRewarded)) return "after_rewarded";
            if (policy.InterstitialMaxPerSession > 0 && _sessionCount >= policy.InterstitialMaxPerSession) return "max_per_session";
            if (policy.InterstitialMaxPerDay > 0 && DayCount >= policy.InterstitialMaxPerDay) return "max_per_day";
            return null;
        }

        public void RecordInterstitialClosed()
        {
            var now = _clock.UtcNow;
            _lastInterstitialClosedUtc = now;
            int day = DayKey(now);
            _dayCount = day == _day ? _dayCount + 1 : 1;
            _day = day;
            _sessionCount++;
            Save();
        }

        public void RecordRewardedClosed()
        {
            _lastRewardedClosedUtc = _clock.UtcNow;
            Save();
        }

        // Đồng hồ lùi (đổi giờ máy) coi như đã đủ khoảng cách, tránh khóa ads vô thời hạn.
        static bool Within(DateTime now, DateTime last, TimeSpan window)
        {
            if (last == DateTime.MinValue || window <= TimeSpan.Zero) return false;
            var elapsed = now - last;
            return elapsed >= TimeSpan.Zero && elapsed < window;
        }

        static int DayKey(DateTime utc) => utc.Year * 10000 + utc.Month * 100 + utc.Day;

        void Load()
        {
            if (!_store.TryGetString(Key, out var raw)) return;
            var parts = raw.Split('|');
            if (parts.Length != 5 || parts[0] != Version
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inter)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rewarded)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                || inter < DateTime.MinValue.Ticks || inter > DateTime.MaxValue.Ticks
                || rewarded < DateTime.MinValue.Ticks || rewarded > DateTime.MaxValue.Ticks)
            {
                _log.Warning("Ignoring corrupt ads capping state");
                return;
            }
            _lastInterstitialClosedUtc = new DateTime(inter, DateTimeKind.Utc);
            _lastRewardedClosedUtc = new DateTime(rewarded, DateTimeKind.Utc);
            _day = day;
            _dayCount = Math.Max(0, count);
        }

        void Save()
        {
            var raw = string.Join("|", Version,
                _lastInterstitialClosedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                _lastRewardedClosedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                _day.ToString(CultureInfo.InvariantCulture),
                _dayCount.ToString(CultureInfo.InvariantCulture));
            try
            {
                _store.SetString(Key, raw);
            }
            catch (Exception e)
            {
                // Mất capping persistent chỉ làm khoảng cách reset ở lần mở app sau; không chặn gameplay.
                _log.Warning("Saving ads capping state failed", e);
            }
        }
    }
}
