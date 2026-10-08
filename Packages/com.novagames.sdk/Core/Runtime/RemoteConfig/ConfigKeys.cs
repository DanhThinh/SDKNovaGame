#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace NovaGames.Mobile.RemoteConfig
{
    public interface IConfigValues
    {
        bool TryGetRaw(string key, [NotNullWhen(true)] out string? raw);
    }

    /// <summary>Base không generic để RemoteConfigService validate giá trị raw mà không cần generic method.</summary>
    public abstract class ConfigKey
    {
        protected ConfigKey(string name, bool remoteOverridable)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Config key name is required.", nameof(name));
            Name = name;
            RemoteOverridable = remoteOverridable;
        }

        public string Name { get; }
        public bool RemoteOverridable { get; }

        public abstract bool IsRawValid(string raw);

        public override string ToString() => Name;
    }

    public abstract class ConfigKey<T> : ConfigKey
    {
        protected ConfigKey(string name, T defaultValue, bool remoteOverridable) : base(name, remoteOverridable)
        {
            Default = defaultValue;
        }

        public T Default { get; }

        protected abstract bool TryParse(string raw, [MaybeNullWhen(false)] out T value);
        protected virtual bool IsValid(T value) => true;

        public sealed override bool IsRawValid(string raw) => TryParse(raw, out var value) && IsValid(value);

        public T Read(IConfigValues values) =>
            RemoteOverridable && values.TryGetRaw(Name, out var raw) && TryParse(raw, out var v) && IsValid(v)
                ? v : Default;
    }

    /// <summary>Quy tắc parse dùng chung cho ConfigKey và RemoteConfigDefinitions (invariant culture).</summary>
    public static class ConfigParsing
    {
        /// <summary>Khớp pattern boolean của Firebase Remote Config.</summary>
        public static bool TryParseBool(string raw, out bool value)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "t": case "yes": case "y": case "on":
                    value = true; return true;
                case "0": case "false": case "f": case "no": case "n": case "off":
                    value = false; return true;
                default:
                    value = false; return false;
            }
        }

        public static bool TryParseInt(string raw, out int value) =>
            int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryParseLong(string raw, out long value) =>
            long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryParseDouble(string raw, out double value) =>
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class BoolKey : ConfigKey<bool>
    {
        public BoolKey(string name, bool defaultValue, bool remoteOverridable = true)
            : base(name, defaultValue, remoteOverridable) { }

        protected override bool TryParse(string raw, out bool value) => ConfigParsing.TryParseBool(raw, out value);
    }

    public sealed class IntKey : ConfigKey<int>
    {
        public IntKey(string name, int defaultValue, int min = int.MinValue, int max = int.MaxValue, bool remoteOverridable = true)
            : base(name, defaultValue, remoteOverridable)
        {
            if (min > max) throw new ArgumentException("min > max", nameof(min));
            if (defaultValue < min || defaultValue > max) throw new ArgumentOutOfRangeException(nameof(defaultValue));
            Min = min;
            Max = max;
        }

        public int Min { get; }
        public int Max { get; }

        protected override bool TryParse(string raw, out int value) => ConfigParsing.TryParseInt(raw, out value);

        protected override bool IsValid(int value) => value >= Min && value <= Max;
    }

    public sealed class LongKey : ConfigKey<long>
    {
        public LongKey(string name, long defaultValue, long min = long.MinValue, long max = long.MaxValue, bool remoteOverridable = true)
            : base(name, defaultValue, remoteOverridable)
        {
            if (min > max) throw new ArgumentException("min > max", nameof(min));
            if (defaultValue < min || defaultValue > max) throw new ArgumentOutOfRangeException(nameof(defaultValue));
            Min = min;
            Max = max;
        }

        public long Min { get; }
        public long Max { get; }

        protected override bool TryParse(string raw, out long value) => ConfigParsing.TryParseLong(raw, out value);

        protected override bool IsValid(long value) => value >= Min && value <= Max;
    }

    public sealed class DoubleKey : ConfigKey<double>
    {
        public DoubleKey(string name, double defaultValue, double min = double.MinValue, double max = double.MaxValue, bool remoteOverridable = true)
            : base(name, defaultValue, remoteOverridable)
        {
            if (min > max) throw new ArgumentException("min > max", nameof(min));
            if (double.IsNaN(defaultValue) || defaultValue < min || defaultValue > max) throw new ArgumentOutOfRangeException(nameof(defaultValue));
            Min = min;
            Max = max;
        }

        public double Min { get; }
        public double Max { get; }

        protected override bool TryParse(string raw, out double value) => ConfigParsing.TryParseDouble(raw, out value);

        protected override bool IsValid(double value) => value >= Min && value <= Max;
    }

    public sealed class StringKey : ConfigKey<string>
    {
        public StringKey(string name, string defaultValue, bool allowEmpty = true, int maxLength = int.MaxValue, bool remoteOverridable = true)
            : base(name, defaultValue ?? throw new ArgumentNullException(nameof(defaultValue)), remoteOverridable)
        {
            AllowEmpty = allowEmpty;
            MaxLength = maxLength;
        }

        public bool AllowEmpty { get; }
        public int MaxLength { get; }

        protected override bool TryParse(string raw, out string value)
        {
            value = raw;
            return true;
        }

        protected override bool IsValid(string value) =>
            (AllowEmpty || value.Length > 0) && value.Length <= MaxLength;
    }

    public delegate bool ConfigParser<T>(string raw, [MaybeNullWhen(false)] out T value);

    /// <summary>JSON (hoặc format tùy ý) với parser do game cung cấp. Parser nên không throw; nếu throw coi như invalid.</summary>
    public sealed class JsonKey<T> : ConfigKey<T>
    {
        readonly ConfigParser<T> _parser;
        readonly Func<T, bool>? _validator;

        public JsonKey(string name, T defaultValue, ConfigParser<T> parser, Func<T, bool>? validator = null, bool remoteOverridable = true)
            : base(name, defaultValue, remoteOverridable)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _validator = validator;
        }

        protected override bool TryParse(string raw, [MaybeNullWhen(false)] out T value)
        {
            try
            {
                return _parser(raw, out value);
            }
            catch (Exception)
            {
                // Parser lỗi = giá trị invalid; RemoteConfigService log key bị loại.
                value = default;
                return false;
            }
        }

        protected override bool IsValid(T value)
        {
            if (_validator is null) return true;
            try
            {
                return _validator(value);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
