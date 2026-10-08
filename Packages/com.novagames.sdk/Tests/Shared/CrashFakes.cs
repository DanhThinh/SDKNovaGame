#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Diagnostics;

namespace NovaGames.Mobile.Testing
{
    // Reporter giả: ghi lại mọi lệnh; test điều khiển kết quả init.
    public sealed class FakeCrashReporter : ICrashReporter
    {
        public readonly List<string> Logs = new List<string>();
        public readonly List<Exception> Exceptions = new List<Exception>();
        public readonly Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<string?> UserIds = new List<string?>();
        public readonly List<bool> Collection = new List<bool>();
        public CrashReportingOptions? Options;
        public Task<SdkResult> InitResult = Task.FromResult(SdkResult.Ok);
        public int InitCount;
        public bool Ready;
        public bool Disposed;

        public string Id => CrashReporterIds.FirebaseCrashlytics;
        public bool IsReady => Ready;

        public Task<SdkResult> InitializeAsync(CrashReportingOptions options, CancellationToken ct)
        {
            InitCount++;
            Options = options;
            Ready = true;
            return InitResult;
        }

        public void Log(string message) => Logs.Add(message);
        public void RecordException(Exception exception) => Exceptions.Add(exception);
        public void SetCustomKey(string key, string value) => Keys[key] = value;
        public void SetUserId(string? id) => UserIds.Add(id);
        public void SetCollectionEnabled(bool enabled) => Collection.Add(enabled);
        public void Dispose() => Disposed = true;
    }
}
