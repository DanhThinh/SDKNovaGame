#nullable enable
using System;

namespace NovaGames.Mobile
{
    public enum SdkErrorCategory : byte
    {
        Configuration,
        NotInitialized,
        ConsentRequired,
        Blocked,
        Unavailable,
        Network,
        Timeout,
        Cancelled,
        Busy,
        Provider,
        Validation,
        Conflict,
        Internal
    }

    public sealed record SdkError(
        string Code, SdkErrorCategory Category, string Message, bool IsRetryable,
        string? Provider = null, Exception? Exception = null)
    {
        public static SdkError Timeout(string op) =>
            new SdkError(op + ".timeout", SdkErrorCategory.Timeout, op + " timed out", true);

        public static SdkError Cancelled(string op) =>
            new SdkError(op + ".cancelled", SdkErrorCategory.Cancelled, op + " cancelled", false);

        public static SdkError Disposed(string op) =>
            new SdkError(op + ".disposed", SdkErrorCategory.Cancelled, op + " disposed", false);

        public static SdkError NotInitialized(string op) =>
            new SdkError(op + ".not_initialized", SdkErrorCategory.NotInitialized, op + " not initialized", false);

        /// <summary>Exception vendor được bắt tại adapter boundary và map sang Category = Provider.</summary>
        public static SdkError FromException(string op, Exception exception, string? provider = null) =>
            new SdkError(op + ".exception", SdkErrorCategory.Provider,
                op + " failed: " + exception.GetType().Name + ": " + exception.Message,
                false, provider, exception);

        public override string ToString() =>
            Provider is null ? $"[{Category}] {Code}: {Message}" : $"[{Category}] {Code} ({Provider}): {Message}";
    }
}
