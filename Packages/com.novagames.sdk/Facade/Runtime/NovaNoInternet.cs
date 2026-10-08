#nullable enable
using System;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>
    /// Bật/tắt popup "No Internet" và trạng thái của nó. Popup là prefab <c>NoInternet/Prefabs/NoInternetPopup</c>
    /// (prefab NovaSdk tự tạo); class này chỉ giữ cờ bật/tắt để game, Remote Config và nút test cùng điều khiển.
    /// <code>
    /// NovaNoInternet.Enabled = false;                   // tắt ở màn hình chơi offline được (vd. tutorial)
    /// NovaNoInternet.VisibilityChanged += shown => { };
    /// </code>
    /// </summary>
    public static class NovaNoInternet
    {
        static bool s_enabled = true;
        static bool s_simulateOffline;

        /// <summary>Cho phép hiện popup khi mất mạng. Tắt trong lúc popup đang hiện thì popup đóng ngay.</summary>
        public static bool Enabled
        {
            get => s_enabled;
            set
            {
                if (s_enabled == value) return;
                s_enabled = value;
                Invoke(EnabledChanged, value, "EnabledChanged");
            }
        }

        /// <summary>
        /// Giả lập mất mạng để test popup trong Editor/Development build (bản release luôn đọc mạng thật).
        /// </summary>
        public static bool SimulateOffline
        {
            get => s_simulateOffline && Debug.isDebugBuild;
            set => s_simulateOffline = value;
        }

        /// <summary>Thiết bị có kết nối mạng (Wi-Fi hoặc dữ liệu di động).</summary>
        public static bool IsInternetReachable =>
            !SimulateOffline && Application.internetReachability != NetworkReachability.NotReachable;

        /// <summary>Popup đang hiện (game đang bị dừng).</summary>
        public static bool IsShowing { get; private set; }

        /// <summary>Popup hiện (true) / đóng (false).</summary>
        public static event Action<bool>? VisibilityChanged;

        /// <summary>Cờ <see cref="Enabled"/> đổi.</summary>
        public static event Action<bool>? EnabledChanged;

        /// <summary>Popup gọi khi hiện/đóng. Game không cần gọi.</summary>
        public static void ReportVisibility(bool showing)
        {
            if (IsShowing == showing) return;
            IsShowing = showing;
            Invoke(VisibilityChanged, showing, "VisibilityChanged");
        }

        static void Invoke(Action<bool>? handlers, bool value, string name)
        {
            if (handlers is null) return;
            foreach (Action<bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(value);
                }
                catch (Exception e)
                {
                    Debug.LogError("[Nova] Game callback NovaNoInternet." + name + " threw");
                    Debug.LogException(e);
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            s_enabled = true;
            s_simulateOffline = false;
            IsShowing = false;
            VisibilityChanged = null;
            EnabledChanged = null;
        }
    }
}
