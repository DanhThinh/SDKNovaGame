#nullable enable
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.RemoteConfig;

namespace NovaGames.Mobile.Samples
{
    // Nguồn duy nhất của key Remote Config trong game: tên enum = tên key trên Firebase, [RemoteDefault] = kiểu + default
    // khi tạo dòng mới trong asset GameRemoteConfig (menu Add Missing Enum Entries). Mọi chỗ đọc config (game, Ads SDK)
    // đều đi qua enum này; không viết tên key dạng string ở nơi khác.
    // Unity lưu enum trong asset theo giá trị số: luôn gán số tường minh, chỉ thêm key mới, không đổi số của key cũ.
    public enum RemoteKey
    {
        // ---- Ads: bật/tắt ----
        [RemoteDefault(ConfigValueType.Bool, "true")] ad_enabled = 5,
        [RemoteDefault(ConfigValueType.Bool, "true")] inter_ad_on_off = 0,
        [RemoteDefault(ConfigValueType.Bool, "true")] reward_ad_on_off = 4,
        [RemoteDefault(ConfigValueType.Bool, "true")] open_ad_on_off = 2,
        [RemoteDefault(ConfigValueType.Bool, "true")] banner_ad_on_off = 3,
        [RemoteDefault(ConfigValueType.Bool, "true")] ad_mrec_enabled = 6,
        // Bid floor cascade 3 ID (A/B) của MAX và AdMob, adapter đọc lúc init theo tên; khai báo ở đây để
        // asset có default và Remote Config validate được.
        [RemoteDefault(ConfigValueType.Bool, "false")] ad_inter_floor_enabled = 7,
        [RemoteDefault(ConfigValueType.Bool, "false")] ad_rewarded_floor_enabled = 8,

        // ---- Ads: capping (giây, trừ khi ghi khác; giá trị remote quá nhỏ/âm bị SDK kẹp về mức tối thiểu an toàn trong AdsPolicy) ----
        [RemoteDefault(ConfigValueType.Int, "60")] ad_inter_interval = 1,
        [RemoteDefault(ConfigValueType.Int, "3")] ad_inter_start_level = 103,
        [RemoteDefault(ConfigValueType.Int, "60")] ad_inter_after_rewarded = 104,
        [RemoteDefault(ConfigValueType.Int, "0")] ad_inter_max_per_session = 105, // 0 = không giới hạn
        [RemoteDefault(ConfigValueType.Int, "0")] ad_inter_max_per_day = 106,     // 0 = không giới hạn
        [RemoteDefault(ConfigValueType.Int, "5")] ad_aoa_min_background = 107,
        [RemoteDefault(ConfigValueType.Int, "30")] ad_collapsible_interval = 108,
        [RemoteDefault(ConfigValueType.Int, "1000")] ad_rewarded_grace = 109,     // mili giây

        // ---- Game ----
        [RemoteDefault(ConfigValueType.Int, "30")] inter_ad_capping_time = 100,
        [RemoteDefault(ConfigValueType.Int, "5")] open_ad_capping_time = 101,
        [RemoteDefault(ConfigValueType.Int, "3")] level_show_rate = 102,
        [RemoteDefault(ConfigValueType.String, "")] force_update_game_info = 200,
        [RemoteDefault(ConfigValueType.Bool, "true")] no_internet_popup_on = 201,   // false = không chặn khi mất mạng
    }

    public static class RemoteKeys
    {
        // Key Ads SDK đọc, lấy từ asset (tên, kiểu, default). Đổi key nào thì chỉ sửa ở đây.
        public static AdsConfigKeys Ads(GameRemoteConfig definitions) => new AdsConfigKeys
        {
            AdsEnabled = definitions.Bool(RemoteKey.ad_enabled),
            InterstitialEnabled = definitions.Bool(RemoteKey.inter_ad_on_off),
            RewardedEnabled = definitions.Bool(RemoteKey.reward_ad_on_off),
            AppOpenEnabled = definitions.Bool(RemoteKey.open_ad_on_off),
            BannerEnabled = definitions.Bool(RemoteKey.banner_ad_on_off),
            MrecEnabled = definitions.Bool(RemoteKey.ad_mrec_enabled),
            InterstitialInterval = definitions.Int(RemoteKey.ad_inter_interval),
            InterstitialStartLevel = definitions.Int(RemoteKey.ad_inter_start_level),
            InterstitialAfterRewarded = definitions.Int(RemoteKey.ad_inter_after_rewarded),
            InterstitialMaxPerSession = definitions.Int(RemoteKey.ad_inter_max_per_session),
            InterstitialMaxPerDay = definitions.Int(RemoteKey.ad_inter_max_per_day),
            AppOpenMinBackground = definitions.Int(RemoteKey.ad_aoa_min_background),
            CollapsibleInterval = definitions.Int(RemoteKey.ad_collapsible_interval),
            RewardGrace = definitions.Int(RemoteKey.ad_rewarded_grace),
        };
    }
}
