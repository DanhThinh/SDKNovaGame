#nullable enable
using System;
using System.Globalization;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Ads.AdMob
{
    // Phần full-screen của GMA mà cascade cần (theo từng tier); adapter dùng GMA thật, test dùng fake.
    internal interface IAdMobFullScreenApi
    {
        void Load(AdUnit unit);
        bool IsReady(AdUnit unit);
    }

    // Bid floor cascade của một unit main. AdsManager chỉ thấy unit main: một Load() là một lượt HIGH -> MEDIUM -> MAIN
    // (tuần tự, mỗi lúc chỉ một tier đang load), kết thúc bằng đúng một OnLoaded hoặc OnLoadFailed. Mỗi lượt luôn bắt đầu
    // từ HIGH; tier giá cao fill trễ được dùng ngay, fill sót của tier thấp hơn chỉ dùng khi tới lượt tier đó. Retry giữa
    // các lượt, TTL và load timeout tổng do AdUnitSlot của AdsManager lo. Chỉ gọi trên main thread (adapter đã chuyển
    // callback GMA về main).
    internal sealed class AdMobFloorCascade : IDisposable
    {
        enum Phase : byte { Idle, Loading, Ready, Showing }

        readonly AdUnit[] _tiers;
        readonly IAdMobFullScreenApi _api;
        readonly IClock _clock;
        readonly IScheduler _scheduler;
        readonly ISdkLogger _log;
        readonly TimeSpan _floorTimeout;
        readonly Action<AdUnit> _loaded;
        readonly Action<AdUnit, AdLoadError> _failed;

        Phase _phase;
        int _tier;
        DateTime _tierStartedUtc;
        IDisposable? _timer;

        // tiers: [HIGH, MEDIUM, ..., MAIN]; phần tử cuối là unit mà AdsManager biết.
        public AdMobFloorCascade(AdUnit[] tiers, IAdMobFullScreenApi api, IClock clock, IScheduler scheduler, ISdkLogger log,
                               TimeSpan floorTimeout, Action<AdUnit> loaded, Action<AdUnit, AdLoadError> failed)
        {
            if (tiers is null || tiers.Length < 2) throw new ArgumentException("Cascade needs at least 2 tiers", nameof(tiers));
            _tiers = tiers;
            _api = api;
            _clock = clock;
            _scheduler = scheduler;
            _log = log;
            _floorTimeout = floorTimeout;
            _loaded = loaded;
            _failed = failed;
        }

        public AdUnit Unit => _tiers[_tiers.Length - 1];
        internal bool IsLoading => _phase == Phase.Loading;
        internal int CurrentTier => _tier;

        // Có ad ở bất kỳ tier nào (kể cả fill trễ adapter còn giữ).
        public bool IsReady => ReadyTier() >= 0;

        public void Load()
        {
            // AdsManager chỉ Load lại sau khi show kết thúc; callback hidden có thể chưa tới.
            if (_phase == Phase.Showing) _phase = Phase.Idle;
            // Lượt đang chạy sẽ tự báo kết quả (vd. AdsManager timeout rồi retry trong lúc MAIN vẫn đang load).
            if (_phase == Phase.Loading) return;

            // Fill trễ của lượt trước (adapter còn giữ ad) vẫn dùng được, ưu tiên tier giá cao nhất.
            int ready = ReadyTier();
            if (ready >= 0)
            {
                _tier = ready;
                _phase = Phase.Ready;
                _log.Debug(this + " " + Label(ready) + " already has an ad");
                _loaded(Unit);
                return;
            }
            StartTier(0);
        }

        void StartTier(int index)
        {
            DisposeTimer();
            _tier = index;
            _phase = Phase.Loading;
            _tierStartedUtc = _clock.UtcNow;
            var tier = _tiers[index];
            if (SafeIsReady(tier))
            {
                OnTierReady(index);
                return;
            }
            if (index < _tiers.Length - 1)
            {
                _timer = _scheduler.Schedule(_floorTimeout, () =>
                {
                    _timer = null;
                    if (_phase == Phase.Loading && _tier == index)
                        Advance(index, new AdLoadError(AdLoadFailure.Timeout, "floor tier load timed out"));
                });
            }
            try
            {
                _api.Load(tier);
            }
            catch (Exception e)
            {
                Advance(index, new AdLoadError(AdLoadFailure.Provider, e.GetType().Name + ": " + e.Message));
            }
        }

        public void OnTierLoaded(string adUnitId)
        {
            int index = IndexOf(adUnitId);
            // Chỉ nhận tier đang load hoặc tier giá cao hơn fill trễ. Tier thấp hơn (còn sót từ lượt trước) adapter giữ ad;
            // tới lượt tier đó thì StartTier thấy ready ngay.
            if (index < 0 || _phase != Phase.Loading || index > _tier) return;
            OnTierReady(index);
        }

        void OnTierReady(int index)
        {
            DisposeTimer();
            _tier = index;
            _phase = Phase.Ready;
            _log.Debug(this + " " + Label(index) + " filled in " + Elapsed());
            _loaded(Unit);
        }

        public void OnTierLoadFailed(string adUnitId, AdLoadError error)
        {
            int index = IndexOf(adUnitId);
            if (index < 0 || _phase != Phase.Loading || index != _tier) return;
            Advance(index, error);
        }

        void Advance(int index, AdLoadError error)
        {
            DisposeTimer();
            int next = index + 1;
            if (next < _tiers.Length)
            {
                _log.Debug(this + " " + Label(index) + " failed (" + error.Kind + ": " + error.Message + ") in " + Elapsed()
                           + " -> " + Label(next));
                StartTier(next);
                return;
            }
            _phase = Phase.Idle;
            _log.Debug(this + " all tiers failed; last " + Label(index) + ": " + error.Kind + ": " + error.Message);
            _failed(Unit, error);
        }

        // Unit cần show: tier giá cao nhất đang có ad.
        public AdUnit BeginShow()
        {
            DisposeTimer();
            int ready = ReadyTier();
            if (ready >= 0) _tier = ready;
            _phase = Phase.Showing;
            return _tiers[_tier];
        }

        // Hidden / display failed. Lượt load sau (AdsManager gọi Load) bắt đầu lại từ HIGH.
        public void EndShow()
        {
            if (_phase == Phase.Showing) _phase = Phase.Idle;
        }

        // AdsManager bỏ ad (TTL, consent, remove_ads, kill switch): dừng lượt đang chạy; adapter destroy ad của mọi tier.
        public void Discard()
        {
            if (_phase == Phase.Showing) return;
            DisposeTimer();
            _phase = Phase.Idle;
        }

        public void Dispose() => DisposeTimer();

        int ReadyTier()
        {
            for (int i = 0; i < _tiers.Length; i++)
            {
                if (SafeIsReady(_tiers[i])) return i;
            }
            return -1;
        }

        bool SafeIsReady(AdUnit tier)
        {
            try
            {
                return _api.IsReady(tier);
            }
            catch (Exception e)
            {
                _log.Error(tier.AdUnitId + " IsReady threw", e);
                return false;
            }
        }

        int IndexOf(string adUnitId)
        {
            for (int i = 0; i < _tiers.Length; i++)
            {
                if (_tiers[i].AdUnitId == adUnitId) return i;
            }
            return -1;
        }

        void DisposeTimer()
        {
            _timer?.Dispose();
            _timer = null;
        }

        string Label(int index) =>
            (index == _tiers.Length - 1 ? "main" : index == 0 ? "high" : index == 1 ? "medium" : "floor" + index)
            + "(" + _tiers[index].AdUnitId + ")";

        string Elapsed() =>
            (_clock.UtcNow - _tierStartedUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        public override string ToString() => Unit + "[floor]";
    }
}
