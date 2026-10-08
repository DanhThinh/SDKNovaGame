#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace NovaGames.Mobile
{
    public readonly struct SdkResult
    {
        public SdkError? Error { get; }
        public bool IsSuccess => Error is null;

        SdkResult(SdkError? error) { Error = error; }

        public static SdkResult Ok => default;

        public static implicit operator SdkResult(SdkError error) => new SdkResult(error);

        public override string ToString() => IsSuccess ? "Ok" : Error!.ToString();
    }

    public readonly struct SdkResult<T>
    {
        readonly T _value;
        public SdkError? Error { get; }
        public bool IsSuccess => Error is null;

        SdkResult(T value, SdkError? error) { _value = value; Error = error; }

        public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
        public T GetValueOrDefault(T fallback) => IsSuccess ? _value : fallback;

        public static SdkResult<T> Ok(T value) => new SdkResult<T>(value, null);
        public static implicit operator SdkResult<T>(SdkError error) => new SdkResult<T>(default!, error);

        public SdkResult AsResult() => Error is null ? SdkResult.Ok : Error;

        public override string ToString() => IsSuccess ? "Ok(" + _value + ")" : Error!.ToString();
    }
}
