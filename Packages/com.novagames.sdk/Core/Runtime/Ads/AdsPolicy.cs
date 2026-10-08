#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.RemoteConfig;

namespace NovaGames.Mobile.Ads
{
    /// <summary>Policy đã resolve từ Remote Config và đã kẹp bởi local safety floor (Clamp).</summary>
    public sealed record AdsPolicy
    {
        public static readonly TimeSpan MinInterstitialInterval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan MinAppOpenBackground = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan MinCollapsibleInterval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan MinRewardGrace = TimeSpan.FromMilliseconds(300);
        public static readonly TimeSpan MaxRewardGrace = TimeSpan.FromMilliseconds(3000);

        public static AdsPolicy Default { get; } = new AdsPolicy();

        public bool AdsEnabled { get; init; } = true;
        public bool InterstitialEnabled { get; init; } = true;
        public bool RewardedEnabled { get; init; } = true;
        public bool AppOpenEnabled { get; init; } = true;
        public bool BannerEnabled { get; init; } = true;
        public bool MrecEnabled { get; init; } = true;

        public TimeSpan InterstitialInterval { get; init; } = TimeSpan.FromSeconds(30);
        public int InterstitialStartLevel { get; init; } = 3;
        public TimeSpan InterstitialAfterRewarded { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>0 = không giới hạn.</summary>
        public int InterstitialMaxPerSession { get; init; }
        public int InterstitialMaxPerDay { get; init; }

        public TimeSpan AppOpenMinBackground { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan CollapsibleInterval { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan RewardGrace { get; init; } = TimeSpan.FromMilliseconds(1000);

        public bool IsFormatEnabled(AdFormat format)
        {
            if (!AdsEnabled) return false;
            return format switch
            {
                AdFormat.Interstitial => InterstitialEnabled,
                AdFormat.Rewarded => RewardedEnabled,
                AdFormat.AppOpen => AppOpenEnabled,
                AdFormat.Banner => BannerEnabled,
                AdFormat.MRec => MrecEnabled,
                _ => false,
            };
        }

        public AdsPolicy Clamp() => this with
        {
            InterstitialInterval = Max(InterstitialInterval, MinInterstitialInterval),
            InterstitialStartLevel = Math.Max(1, InterstitialStartLevel),
            InterstitialAfterRewarded = Max(InterstitialAfterRewarded, TimeSpan.Zero),
            InterstitialMaxPerSession = Math.Max(0, InterstitialMaxPerSession),
            InterstitialMaxPerDay = Math.Max(0, InterstitialMaxPerDay),
            AppOpenMinBackground = Max(AppOpenMinBackground, MinAppOpenBackground),
            CollapsibleInterval = Max(CollapsibleInterval, MinCollapsibleInterval),
            RewardGrace = RewardGrace < MinRewardGrace ? MinRewardGrace : RewardGrace > MaxRewardGrace ? MaxRewardGrace : RewardGrace,
        };

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    /// <summary>
    /// Tên key Remote Config mặc định của SDK, theo quy ước `ad_&lt;format&gt;_&lt;thuộc tính&gt;`, dùng khi game không có
    /// Remote Config Definitions. Cách chuẩn: khai báo key trong enum RemoteKey của game và gán qua
    /// <see cref="IAdsConfigKeysSource"/> (xem RemoteKeys.Ads trong Samples).
    /// </summary>
    public sealed record AdsConfigKeyNames
    {
        public string AdsEnabled { get; init; } = "ad_enabled";
        public string InterstitialEnabled { get; init; } = "ad_inter_enabled";
        public string RewardedEnabled { get; init; } = "ad_rewarded_enabled";
        public string AppOpenEnabled { get; init; } = "ad_aoa_enabled";
        public string BannerEnabled { get; init; } = "ad_banner_enabled";
        public string MrecEnabled { get; init; } = "ad_mrec_enabled";
        public string InterstitialInterval { get; init; } = "ad_inter_interval";
        public string InterstitialStartLevel { get; init; } = "ad_inter_start_level";
        public string InterstitialAfterRewarded { get; init; } = "ad_inter_after_rewarded";
        public string InterstitialMaxPerSession { get; init; } = "ad_inter_max_per_session";
        public string InterstitialMaxPerDay { get; init; } = "ad_inter_max_per_day";
        public string AppOpenMinBackground { get; init; } = "ad_aoa_min_background";
        public string CollapsibleInterval { get; init; } = "ad_collapsible_interval";
        public string RewardGrace { get; init; } = "ad_rewarded_grace";
    }

    /// <summary>
    /// Asset Remote Config Definitions của game cài interface này để NovaSdk tự lấy key Ads từ đó
    /// (vd. GameRemoteConfig =&gt; RemoteKeys.Ads(this)). Không cài thì Ads dùng tên/default mặc định của SDK.
    /// </summary>
    public interface IAdsConfigKeysSource
    {
        AdsConfigKeys AdsKeys { get; }
    }

    /// <summary>
    /// Typed key + đọc policy từ snapshot Remote Config. Giá trị sai kiểu/âm -&gt; default, sau đó kẹp safety floor.
    /// Game có danh sách key riêng (vd. RemoteConfigDefinitions&lt;RemoteKey&gt;) thì gán key từ đó qua init để tên, kiểu và
    /// default chỉ nằm một chỗ: `new AdsConfigKeys { InterstitialEnabled = defs.Bool(RemoteKey.x) }`. Key không gán
    /// dùng tên/default mặc định của SDK.
    /// </summary>
    public sealed class AdsConfigKeys
    {
        public static AdsConfigKeys Default { get; } = new AdsConfigKeys();

        /// <summary>Tên và default mặc định của SDK; gán từng key qua init để thay.</summary>
        public AdsConfigKeys() : this(new AdsConfigKeyNames()) { }

        public AdsConfigKeys(AdsConfigKeyNames names, AdsPolicy? defaults = null)
        {
            if (names is null) throw new ArgumentNullException(nameof(names));
            var d = defaults ?? AdsPolicy.Default;

            AdsEnabled = new BoolKey(names.AdsEnabled, d.AdsEnabled);
            InterstitialEnabled = new BoolKey(names.InterstitialEnabled, d.InterstitialEnabled);
            RewardedEnabled = new BoolKey(names.RewardedEnabled, d.RewardedEnabled);
            AppOpenEnabled = new BoolKey(names.AppOpenEnabled, d.AppOpenEnabled);
            BannerEnabled = new BoolKey(names.BannerEnabled, d.BannerEnabled);
            MrecEnabled = new BoolKey(names.MrecEnabled, d.MrecEnabled);
            InterstitialInterval = new IntKey(names.InterstitialInterval, Seconds(d.InterstitialInterval), min: 0);
            InterstitialStartLevel = new IntKey(names.InterstitialStartLevel, d.InterstitialStartLevel, min: 0);
            InterstitialAfterRewarded = new IntKey(names.InterstitialAfterRewarded, Seconds(d.InterstitialAfterRewarded), min: 0);
            InterstitialMaxPerSession = new IntKey(names.InterstitialMaxPerSession, d.InterstitialMaxPerSession, min: 0);
            InterstitialMaxPerDay = new IntKey(names.InterstitialMaxPerDay, d.InterstitialMaxPerDay, min: 0);
            AppOpenMinBackground = new IntKey(names.AppOpenMinBackground, Seconds(d.AppOpenMinBackground), min: 0);
            CollapsibleInterval = new IntKey(names.CollapsibleInterval, Seconds(d.CollapsibleInterval), min: 0);
            RewardGrace = new IntKey(names.RewardGrace, (int)d.RewardGrace.TotalMilliseconds, min: 0);
        }

        public BoolKey AdsEnabled { get; init; }
        public BoolKey InterstitialEnabled { get; init; }
        public BoolKey RewardedEnabled { get; init; }
        public BoolKey AppOpenEnabled { get; init; }
        public BoolKey BannerEnabled { get; init; }
        public BoolKey MrecEnabled { get; init; }
        public IntKey InterstitialInterval { get; init; }
        public IntKey InterstitialStartLevel { get; init; }
        public IntKey InterstitialAfterRewarded { get; init; }
        public IntKey InterstitialMaxPerSession { get; init; }
        public IntKey InterstitialMaxPerDay { get; init; }
        public IntKey AppOpenMinBackground { get; init; }
        public IntKey CollapsibleInterval { get; init; }
        public IntKey RewardGrace { get; init; }

        /// <summary>Đăng ký vào RemoteConfigService để validate giá trị remote.</summary>
        public IReadOnlyList<ConfigKey> All => new ConfigKey[]
        {
            AdsEnabled, InterstitialEnabled, RewardedEnabled, AppOpenEnabled, BannerEnabled, MrecEnabled,
            InterstitialInterval, InterstitialStartLevel, InterstitialAfterRewarded,
            InterstitialMaxPerSession, InterstitialMaxPerDay, AppOpenMinBackground, CollapsibleInterval, RewardGrace,
        };

        public AdsPolicy Read(IConfigValues values) => new AdsPolicy
        {
            AdsEnabled = AdsEnabled.Read(values),
            InterstitialEnabled = InterstitialEnabled.Read(values),
            RewardedEnabled = RewardedEnabled.Read(values),
            AppOpenEnabled = AppOpenEnabled.Read(values),
            BannerEnabled = BannerEnabled.Read(values),
            MrecEnabled = MrecEnabled.Read(values),
            InterstitialInterval = TimeSpan.FromSeconds(InterstitialInterval.Read(values)),
            InterstitialStartLevel = InterstitialStartLevel.Read(values),
            InterstitialAfterRewarded = TimeSpan.FromSeconds(InterstitialAfterRewarded.Read(values)),
            InterstitialMaxPerSession = InterstitialMaxPerSession.Read(values),
            InterstitialMaxPerDay = InterstitialMaxPerDay.Read(values),
            AppOpenMinBackground = TimeSpan.FromSeconds(AppOpenMinBackground.Read(values)),
            CollapsibleInterval = TimeSpan.FromSeconds(CollapsibleInterval.Read(values)),
            RewardGrace = TimeSpan.FromMilliseconds(RewardGrace.Read(values)),
        }.Clamp();

        static int Seconds(TimeSpan value) => (int)Math.Round(value.TotalSeconds);
    }
}
