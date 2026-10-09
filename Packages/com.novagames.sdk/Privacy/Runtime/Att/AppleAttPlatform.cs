#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace NovaGames.Mobile.Privacy.Att
{
    /// <summary>
    /// ATT của iOS qua plugin native <c>Plugins/iOS/NovaAtt.mm</c>. Chỉ đăng ký trên máy iOS thật; Editor và Android
    /// không có ATT nên NovaSdk coi như không áp dụng.
    /// </summary>
    sealed class AppleAttPlatform : IAttPlatform
    {
        // Callback native là static (IL2CPP không truyền được delegate instance): các lệnh hỏi đang chờ dùng chung một kết quả.
        static readonly List<Action<AttStatus>> s_pending = new List<Action<AttStatus>>();

        readonly IMainThreadDispatcher _main;
        readonly ISdkLogger _log;

        public AppleAttPlatform(ModuleContext ctx)
        {
            _main = ctx.Main;
            _log = ctx.Logs.Create("privacy.att");
        }

        public AttStatus Status
        {
            get
            {
                try
                {
                    return Map(NativeGetStatus());
                }
                catch (Exception e)
                {
                    _log.Error("Reading ATT status failed", e);
                    return AttStatus.NotApplicable;
                }
            }
        }

        public Task<AttStatus> RequestAsync(CancellationToken ct)
        {
            var current = Status;
            if (current != AttStatus.NotDetermined) return Task.FromResult(current);

            var tcs = new TaskCompletionSource<AttStatus>();
            bool first;
            lock (s_pending)
            {
                first = s_pending.Count == 0;
                s_pending.Add(status => _main.Post(() => tcs.TrySetResult(status)));
            }
            if (first)
            {
                _log.Info("Requesting App Tracking Transparency authorization");
                try
                {
                    NativeRequest();
                }
                catch (Exception e)
                {
                    _log.Error("Requesting ATT failed", e);
                    Complete(-1);
                }
            }
            if (ct.CanBeCanceled) ct.Register(() => _main.Post(() => tcs.TrySetResult(Status)));
            return tcs.Task;
        }

        [AOT.MonoPInvokeCallback(typeof(NativeCallback))]
        static void OnNativeResult(int status) => Complete(status);

        static void Complete(int status)
        {
            Action<AttStatus>[] callbacks;
            lock (s_pending)
            {
                callbacks = s_pending.ToArray();
                s_pending.Clear();
            }
            var mapped = Map(status);
            foreach (var callback in callbacks) callback(mapped);
        }

        internal static AttStatus Map(int status) => status switch
        {
            0 => AttStatus.NotDetermined,
            1 => AttStatus.Restricted,
            2 => AttStatus.Denied,
            3 => AttStatus.Authorized,
            _ => AttStatus.NotApplicable,
        };

        delegate void NativeCallback(int status);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int NovaAtt_GetStatus();
        [DllImport("__Internal")] static extern void NovaAtt_Request(NativeCallback callback);

        static int NativeGetStatus() => NovaAtt_GetStatus();
        static void NativeRequest() => NovaAtt_Request(OnNativeResult);
#else
        static int NativeGetStatus() => -1;
        static void NativeRequest() => OnNativeResult(-1);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            lock (s_pending) s_pending.Clear();
        }
    }
}
