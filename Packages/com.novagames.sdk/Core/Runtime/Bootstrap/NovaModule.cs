#nullable enable
using UnityEngine;

namespace NovaGames.Mobile.Bootstrap
{
    /// <summary>
    /// Một module SDK (Firebase, MAX, AdMob, Adjust, UMP, IAP...) là component trên GameObject con của <see cref="NovaModules"/>.
    /// Component nằm trong scene nên IL2CPP luôn giữ assembly adapter khi strip code. Editor tự thêm module của vendor đã
    /// cài và xóa module mất script khi vendor bị gỡ; game không cần thêm tay.
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class NovaModule : MonoBehaviour
    {
        /// <summary>Đăng ký factory adapter vào <see cref="AdapterRegistry"/>. NovaSdk gọi trước khi khởi tạo.</summary>
        protected abstract void Register();

        // Đăng ký mọi module trong các scene đang mở (kể cả GameObject đang tắt). Trả về số module.
        internal static int RegisterLoaded()
        {
            var modules = FindObjectsByType<NovaModule>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var module in modules) module.Register();
            return modules.Length;
        }
    }
}
