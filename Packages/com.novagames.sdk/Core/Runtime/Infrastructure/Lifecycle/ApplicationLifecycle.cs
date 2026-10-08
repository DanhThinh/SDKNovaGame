#nullable enable
using UnityEngine;

namespace NovaGames.Mobile.Infrastructure
{
    public interface IApplicationLifecycle
    {
        bool IsPaused { get; }

        /// <summary>true = vào background, false = quay lại. Raise trên main thread.</summary>
        ISdkEvent<bool> PauseChanged { get; }
    }

    public interface INetworkStatus
    {
        bool IsReachable { get; }
    }

    public sealed class UnityNetworkStatus : INetworkStatus
    {
        public bool IsReachable => Application.internetReachability != NetworkReachability.NotReachable;
    }

    /// <summary>Host ẩn, DontDestroyOnLoad, chuyển OnApplicationPause thành PauseChanged.</summary>
    public sealed class ApplicationLifecycleHost : MonoBehaviour, IApplicationLifecycle
    {
        readonly SdkEvent<bool> _pauseChanged = new SdkEvent<bool>();

        public bool IsPaused { get; private set; }
        public ISdkEvent<bool> PauseChanged => _pauseChanged;

        /// <summary>Phải gọi trên main thread.</summary>
        public static ApplicationLifecycleHost Create()
        {
            var go = new GameObject("[Nova] ApplicationLifecycle") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            return go.AddComponent<ApplicationLifecycleHost>();
        }

        void OnApplicationPause(bool paused)
        {
            if (IsPaused == paused) return;
            IsPaused = paused;
            _pauseChanged.Raise(paused);
        }

        void OnDestroy() => _pauseChanged.Clear();
    }
}
