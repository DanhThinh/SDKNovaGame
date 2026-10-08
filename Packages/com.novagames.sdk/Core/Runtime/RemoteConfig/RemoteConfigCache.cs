#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;

namespace NovaGames.Mobile.RemoteConfig
{
    // Last-known-good cache: schema version + corruption fallback.
    internal static class RemoteConfigCache
    {
        const int SchemaVersion = 1;

        [Serializable]
        sealed class Dto
        {
            public int schemaVersion;
            public long activatedUtcTicks;
            public string[] keys = Array.Empty<string>();
            public string[] values = Array.Empty<string>();
        }

        public static bool TryLoad(IKeyValueStore store, ISdkLogger log,
            [NotNullWhen(true)] out Dictionary<string, string>? values, out DateTime activatedUtc)
        {
            values = null;
            activatedUtc = DateTime.MinValue;
            if (!store.TryGetString(StorageKeys.RemoteConfigLastGood, out var json)) return false;

            Dto? dto;
            try
            {
                dto = JsonUtility.FromJson<Dto>(json);
            }
            catch (Exception e)
            {
                Discard(store, log, "unreadable JSON", e);
                return false;
            }

            if (dto is null || dto.schemaVersion != SchemaVersion || dto.keys is null || dto.values is null
                || dto.keys.Length != dto.values.Length
                || dto.activatedUtcTicks < DateTime.MinValue.Ticks || dto.activatedUtcTicks > DateTime.MaxValue.Ticks)
            {
                Discard(store, log, "invalid schema", null);
                return false;
            }

            var result = new Dictionary<string, string>(dto.keys.Length, StringComparer.Ordinal);
            for (int i = 0; i < dto.keys.Length; i++)
            {
                var key = dto.keys[i];
                if (string.IsNullOrEmpty(key) || dto.values[i] is null)
                {
                    Discard(store, log, "invalid entry", null);
                    return false;
                }
                result[key] = dto.values[i];
            }

            values = result;
            activatedUtc = new DateTime(dto.activatedUtcTicks, DateTimeKind.Utc);
            return true;
        }

        public static void Save(IKeyValueStore store, ISdkLogger log, RemoteConfigSnapshot snapshot)
        {
            var dto = new Dto
            {
                schemaVersion = SchemaVersion,
                activatedUtcTicks = snapshot.ActivatedUtc.Ticks,
                keys = new string[snapshot.Values.Count],
                values = new string[snapshot.Values.Count],
            };
            int i = 0;
            foreach (var pair in snapshot.Values)
            {
                dto.keys[i] = pair.Key;
                dto.values[i] = pair.Value;
                i++;
            }

            try
            {
                store.SetString(StorageKeys.RemoteConfigLastGood, JsonUtility.ToJson(dto));
                store.Flush();
            }
            catch (Exception e)
            {
                log.Error("Failed to persist remote config last-known-good", e);
            }
        }

        static void Discard(IKeyValueStore store, ISdkLogger log, string reason, Exception? e)
        {
            log.Warning("Discarding remote config cache: " + reason, e);
            try
            {
                store.Delete(StorageKeys.RemoteConfigLastGood);
            }
            catch (Exception deleteError)
            {
                log.Error("Failed to delete corrupted remote config cache", deleteError);
            }
        }
    }
}
