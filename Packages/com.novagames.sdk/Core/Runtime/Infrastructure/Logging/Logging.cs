#nullable enable
using System;

namespace NovaGames.Mobile.Infrastructure
{
    public enum SdkLogLevel : byte { Debug, Info, Warning, Error, None }

    public interface ISdkLogger
    {
        bool IsEnabled(SdkLogLevel level);
        void Log(SdkLogLevel level, string message, Exception? exception = null);
    }

    public interface ISdkLoggerFactory
    {
        ISdkLogger Create(string module);
    }

    public static class SdkLoggerExtensions
    {
        public static void Debug(this ISdkLogger log, string message) => log.Log(SdkLogLevel.Debug, message);
        public static void Info(this ISdkLogger log, string message) => log.Log(SdkLogLevel.Info, message);
        public static void Warning(this ISdkLogger log, string message, Exception? exception = null) => log.Log(SdkLogLevel.Warning, message, exception);
        public static void Error(this ISdkLogger log, string message, Exception? exception = null) => log.Log(SdkLogLevel.Error, message, exception);

        /// <summary>
        /// Chạy <paramref name="action"/>, bắt mọi exception và log Error "<paramref name="what"/> threw".
        /// Dùng ở ranh giới gọi API vendor hoặc callback của game để lỗi không lan ra ngoài SDK. true = chạy không lỗi.
        /// Gọi được cả khi <paramref name="log"/> null (chỉ bỏ qua việc log).
        /// </summary>
        public static bool TryRun(this ISdkLogger? log, string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                log?.Error(what + " threw", e);
                return false;
            }
        }
    }

    public sealed class UnitySdkLoggerFactory : ISdkLoggerFactory
    {
        readonly SdkLogLevel _minimumLevel;

        public UnitySdkLoggerFactory(SdkLogLevel minimumLevel) { _minimumLevel = minimumLevel; }

        public ISdkLogger Create(string module) => new UnitySdkLogger(module, _minimumLevel);

        sealed class UnitySdkLogger : ISdkLogger
        {
            readonly string _prefix;
            readonly SdkLogLevel _minimumLevel;

            public UnitySdkLogger(string module, SdkLogLevel minimumLevel)
            {
                _prefix = "[Nova][" + module + "] ";
                _minimumLevel = minimumLevel;
            }

            public bool IsEnabled(SdkLogLevel level) =>
                SdkLogOutput.Enabled && level >= _minimumLevel && level != SdkLogLevel.None;

            public void Log(SdkLogLevel level, string message, Exception? exception = null)
            {
                if (!IsEnabled(level)) return;
                SdkLogOutput.Write(level, exception is null ? _prefix + message : _prefix + message + "\n" + exception);
            }
        }
    }

    // Đích ghi log của SDK (Unity console / logcat / Xcode). Editor luôn ghi; bản build chỉ ghi khi có scripting
    // define NOVA_SDK_LOG (menu NovaGames > Show SDK Logs In Build, hoặc BuildPlayerOptions.extraScriptingDefines).
    // Không có define: logger tắt hẳn, không ghép chuỗi. Mức log vẫn theo RuntimeSdkSettings.LogLevel.
    internal static class SdkLogOutput
    {
        public static readonly bool Enabled =
#if UNITY_EDITOR || NOVA_SDK_LOG
            true;
#else
            false;
#endif

        public static void Write(SdkLogLevel level, string text)
        {
            if (!Enabled) return;
            switch (level)
            {
                case SdkLogLevel.Error: UnityEngine.Debug.LogError(text); break;
                case SdkLogLevel.Warning: UnityEngine.Debug.LogWarning(text); break;
                default: UnityEngine.Debug.Log(text); break;
            }
        }

        public static void Exception(Exception exception)
        {
            if (Enabled) UnityEngine.Debug.LogException(exception);
        }
    }
}
