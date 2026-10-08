#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.Privacy
{
    /// <summary>Giả lập vùng địa lý khi test form consent (chỉ Development build). Other = vùng không bắt buộc consent.</summary>
    public enum ConsentDebugGeography : byte { Disabled, Eea, RegulatedUsState, Other }

    /// <summary>Tùy chọn khi thu thập consent.</summary>
    public sealed record ConsentGatherOptions(bool IsUnderAge, bool IsDevelopment)
    {
        public ConsentDebugGeography DebugGeography { get; init; } = ConsentDebugGeography.Disabled;

        /// <summary>Device ID dạng hash (lấy từ log của UMP) để giả lập vùng địa lý trên máy thật.</summary>
        public IReadOnlyList<string> TestDeviceHashedIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// SPI cho nền tảng consent (CMP), vd. Google UMP. Adapter hiện form khi cần và đổi kết quả thành
    /// <see cref="ConsentSnapshot"/>. Không throw; lỗi mạng vẫn trả snapshot từ consent đã lưu lần trước.
    /// </summary>
    public interface IConsentPlatform : IDisposable
    {
        string Id { get; }

        /// <summary>
        /// Consent đã lưu từ lần chạy trước, đọc ngay (không gọi mạng). Nếu <c>CanRequestAds</c> thì có thể load ads
        /// song song trong lúc <see cref="GatherAsync"/> cập nhật.
        /// </summary>
        ConsentSnapshot ReadStored(ConsentGatherOptions options);

        /// <summary>Cập nhật trạng thái consent và hiện form nếu người dùng cần trả lời (lần đầu ở EEA/UK, ...).</summary>
        Task<SdkResult<ConsentSnapshot>> GatherAsync(ConsentGatherOptions options, CancellationToken ct);

        /// <summary>Game phải có nút "Privacy settings" để người dùng đổi lựa chọn (bắt buộc ở EEA/UK).</summary>
        bool IsPrivacyOptionsRequired { get; }

        /// <summary>Hiện form đổi lựa chọn consent; trả snapshot mới sau khi form đóng.</summary>
        Task<SdkResult<ConsentSnapshot>> ShowPrivacyOptionsAsync(CancellationToken ct);
    }

    public static class ConsentPlatformIds
    {
        public const string GoogleUmp = "ump";
    }
}
