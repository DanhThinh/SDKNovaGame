#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NovaGames.Mobile.RemoteConfig
{
    public enum ConfigValueType : byte { Bool, Int, Long, Double, String }

    /// <summary>
    /// Kiểu + default của một key, khai báo ngay trên enum key của game để tên, kiểu và default nằm một chỗ.
    /// Menu "Add Missing Enum Entries" của asset đọc attribute này khi tạo dòng mới.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RemoteDefaultAttribute : Attribute
    {
        public RemoteDefaultAttribute(ConfigValueType type, string value)
        {
            Type = type;
            Value = value ?? string.Empty;
        }

        public ConfigValueType Type { get; }
        public string Value { get; }
    }

    [Serializable]
    public sealed class ConfigEntry<TKey> where TKey : struct, Enum
    {
        [Tooltip("Tên enum = tên key trên Firebase Remote Config.")]
        [SerializeField] TKey key;

        [SerializeField] ConfigValueType type;

        [Tooltip("Giá trị mặc định, viết theo invariant culture. Bool: true/false, Double: 0.5.")]
        [SerializeField] string defaultValue = string.Empty;

        [Tooltip("Tắt để luôn dùng giá trị mặc định (safety floor), bỏ qua giá trị remote.")]
        [SerializeField] bool remoteOverridable = true;

        public ConfigEntry() { }

        public ConfigEntry(TKey key, ConfigValueType type, string defaultValue, bool remoteOverridable = true)
        {
            this.key = key;
            this.type = type;
            this.defaultValue = defaultValue ?? string.Empty;
            this.remoteOverridable = remoteOverridable;
        }

        public TKey Key => key;
        public ConfigValueType Type => type;
        public string DefaultValue => defaultValue ?? string.Empty;
        public bool RemoteOverridable => remoteOverridable;
    }

    /// <summary>Base không generic để asset/field khác (vd. NovaSdkSettings) tham chiếu được mọi RemoteConfigDefinitions&lt;TKey&gt;.</summary>
    public abstract class RemoteConfigDefinitions : ScriptableObject
    {
        /// <summary>Truyền vào RemoteConfigService để validate giá trị remote theo kiểu/khoảng đã khai báo.</summary>
        public abstract IReadOnlyList<ConfigKey> AllKeys { get; }

        /// <summary>Kiểu enum key của asset (vd. RemoteKey).</summary>
        public abstract Type KeyType { get; }
    }

    /// <summary>
    /// Danh sách key Remote Config dạng asset: enum là tên key, mỗi dòng có kiểu dữ liệu và giá trị mặc định.
    /// Game khai báo enum key và một lớp con không generic:
    ///   [CreateAssetMenu(menuName = "NovaGames/Remote Config Definitions")]
    ///   public sealed class GameRemoteConfig : RemoteConfigDefinitions&lt;RemoteKey&gt; { }
    /// Lưu ý: Unity lưu enum theo giá trị số, nên gán giá trị tường minh cho enum và không đổi số của key đã có.
    /// </summary>
    public abstract class RemoteConfigDefinitions<TKey> : RemoteConfigDefinitions where TKey : struct, Enum
    {
        [SerializeField] List<ConfigEntry<TKey>> entries = new List<ConfigEntry<TKey>>();

        Dictionary<TKey, ConfigKey>? _keys;
        ConfigKey[]? _all;
        List<string>? _errors;
        HashSet<string>? _reportedMisses;

        public IReadOnlyList<ConfigEntry<TKey>> Entries => entries;

        public override Type KeyType => typeof(TKey);

        public override IReadOnlyList<ConfigKey> AllKeys
        {
            get
            {
                EnsureBuilt();
                return _all!;
            }
        }

        public BoolKey Bool(TKey key) => Resolve(key, ConfigValueType.Bool, n => new BoolKey(n, false, remoteOverridable: false));
        public IntKey Int(TKey key) => Resolve(key, ConfigValueType.Int, n => new IntKey(n, 0, remoteOverridable: false));
        public LongKey Long(TKey key) => Resolve(key, ConfigValueType.Long, n => new LongKey(n, 0, remoteOverridable: false));
        public DoubleKey Double(TKey key) => Resolve(key, ConfigValueType.Double, n => new DoubleKey(n, 0, remoteOverridable: false));
        public StringKey String(TKey key) => Resolve(key, ConfigValueType.String, n => new StringKey(n, string.Empty, remoteOverridable: false));

        /// <summary>Lỗi cấu hình của asset (default sai kiểu, trùng key, range sai...). Rỗng = hợp lệ.</summary>
        public IReadOnlyList<string> Validate()
        {
            Invalidate();
            EnsureBuilt(log: false);
            return _errors!;
        }

        internal void SetEntries(IEnumerable<ConfigEntry<TKey>> values)
        {
            entries = new List<ConfigEntry<TKey>>(values);
            Invalidate();
        }

        void OnEnable() => Invalidate();

#if UNITY_EDITOR
        void OnValidate()
        {
            foreach (var error in Validate()) global::DebugCustom.LogWarning($"[{name}] {error}");
            Invalidate();
        }
#endif

        void Invalidate()
        {
            _keys = null;
            _all = null;
            _errors = null;
            _reportedMisses = null;
        }

        TConfigKey Resolve<TConfigKey>(TKey key, ConfigValueType expected, Func<string, TConfigKey> fallback)
            where TConfigKey : ConfigKey
        {
            EnsureBuilt();
            if (_keys!.TryGetValue(key, out var configKey) && configKey is TConfigKey typed) return typed;

            // Lỗi lập trình: không throw trong gameplay, trả về key mặc định và log một lần.
            var keyName = key.ToString();
            if ((_reportedMisses ??= new HashSet<string>()).Add(keyName))
            {
                global::DebugCustom.LogError(_keys.TryGetValue(key, out var other)
                    ? $"[{name}] Remote config key '{keyName}' is {TypeOf(other)}, not {expected}"
                    : $"[{name}] Remote config key '{keyName}' is not declared");
            }
            return fallback(keyName);
        }

        void EnsureBuilt(bool log = true)
        {
            if (_keys != null) return;

            var keys = new Dictionary<TKey, ConfigKey>();
            var all = new List<ConfigKey>();
            var errors = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            errors.AddRange(FindDuplicateEnumValues());

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry is null) continue;

                var keyName = entry.Key.ToString();
                if (!Enum.IsDefined(typeof(TKey), entry.Key))
                {
                    errors.Add($"Entry #{i}: enum value {keyName} no longer exists");
                    continue;
                }
                if (keys.ContainsKey(entry.Key) || !names.Add(keyName))
                {
                    errors.Add($"Entry #{i}: duplicate key '{keyName}', ignored");
                    continue;
                }

                var configKey = Create(entry, keyName, errors);
                keys.Add(entry.Key, configKey);
                all.Add(configKey);
            }

            _keys = keys;
            _all = all.ToArray();
            _errors = errors;
            if (log)
            {
                foreach (var error in errors) global::DebugCustom.LogError($"[{name}] {error}");
            }
        }

        // Default sai => báo lỗi và dùng 0/false/"" để không chặn startup.
        static ConfigKey Create(ConfigEntry<TKey> entry, string keyName, List<string> errors)
        {
            var raw = entry.DefaultValue;
            bool overridable = entry.RemoteOverridable;
            switch (entry.Type)
            {
                case ConfigValueType.Bool:
                {
                    if (!ConfigParsing.TryParseBool(raw, out var value)) errors.Add(InvalidDefault(keyName, raw, "Bool"));
                    return new BoolKey(keyName, value, overridable);
                }
                case ConfigValueType.Int:
                {
                    if (!ConfigParsing.TryParseInt(raw, out var value)) errors.Add(InvalidDefault(keyName, raw, "Int"));
                    return new IntKey(keyName, value, remoteOverridable: overridable);
                }
                case ConfigValueType.Long:
                {
                    if (!ConfigParsing.TryParseLong(raw, out var value)) errors.Add(InvalidDefault(keyName, raw, "Long"));
                    return new LongKey(keyName, value, remoteOverridable: overridable);
                }
                case ConfigValueType.Double:
                {
                    if (!ConfigParsing.TryParseDouble(raw, out var value)) errors.Add(InvalidDefault(keyName, raw, "Double"));
                    return new DoubleKey(keyName, value, remoteOverridable: overridable);
                }
                default:
                    return new StringKey(keyName, raw, remoteOverridable: overridable);
            }
        }

        // Hai tên enum cùng giá trị số => asset không phân biệt được key, ToString() có thể trả tên sai.
        static IEnumerable<string> FindDuplicateEnumValues()
        {
            var seen = new Dictionary<long, string>();
            foreach (var enumName in Enum.GetNames(typeof(TKey)))
            {
                long value = Convert.ToInt64(Enum.Parse(typeof(TKey), enumName), CultureInfo.InvariantCulture);
                if (seen.TryGetValue(value, out var first))
                    yield return $"Enum {typeof(TKey).Name}: '{first}' and '{enumName}' share value {value}; give each key a unique number";
                else
                    seen[value] = enumName;
            }
        }

        static string InvalidDefault(string keyName, string raw, string type) =>
            $"'{keyName}': default \"{raw}\" is not a valid {type}";

        static string TypeOf(ConfigKey key) => key switch
        {
            BoolKey _ => "Bool",
            IntKey _ => "Int",
            LongKey _ => "Long",
            DoubleKey _ => "Double",
            _ => "String",
        };
    }

    public static class RemoteConfigDefinitionsExtensions
    {
        public static bool GetBool<TKey>(this IRemoteConfigService service, RemoteConfigDefinitions<TKey> definitions, TKey key)
            where TKey : struct, Enum => service.Get(definitions.Bool(key));

        public static int GetInt<TKey>(this IRemoteConfigService service, RemoteConfigDefinitions<TKey> definitions, TKey key)
            where TKey : struct, Enum => service.Get(definitions.Int(key));

        public static long GetLong<TKey>(this IRemoteConfigService service, RemoteConfigDefinitions<TKey> definitions, TKey key)
            where TKey : struct, Enum => service.Get(definitions.Long(key));

        public static double GetDouble<TKey>(this IRemoteConfigService service, RemoteConfigDefinitions<TKey> definitions, TKey key)
            where TKey : struct, Enum => service.Get(definitions.Double(key));

        public static string GetString<TKey>(this IRemoteConfigService service, RemoteConfigDefinitions<TKey> definitions, TKey key)
            where TKey : struct, Enum => service.Get(definitions.String(key));
    }
}
