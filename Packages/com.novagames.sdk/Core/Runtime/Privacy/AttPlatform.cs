#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.Privacy
{
    /// <summary>
    /// App Tracking Transparency (iOS 14.5+): hỏi người dùng có cho app dùng IDFA để quảng cáo cá nhân hóa không.
    /// Chỉ có trên iOS; nền tảng khác không đăng ký adapter này.
    /// </summary>
    public interface IAttPlatform
    {
        /// <summary>Trạng thái hiện tại, đọc ngay. NotApplicable = iOS dưới 14.</summary>
        AttStatus Status { get; }

        /// <summary>
        /// Hiện popup ATT của hệ thống (chỉ hiện khi Status = NotDetermined) và trả trạng thái sau khi người dùng trả lời.
        /// Không throw. Gọi trên main thread.
        /// </summary>
        Task<AttStatus> RequestAsync(CancellationToken ct);
    }

    public static class AttPlatformIds
    {
        public const string Apple = "apple_att";
    }
}
