// SPDX-License-Identifier: BSD-2-Clause
// Beyond Recall QA evidence mode - JSONL event emitter with atomic writes.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
    internal sealed class BrQaEvent
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("eventId")]
        public string EventId { get; set; }

        [JsonPropertyName("eventType")]
        public string EventType { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }

        [JsonPropertyName("sessionToken")]
        public string SessionToken { get; set; }

        [JsonPropertyName("processId")]
        public int ProcessId { get; set; }

        [JsonPropertyName("processStartTime")]
        public string ProcessStartTime { get; set; }

        [JsonPropertyName("serverEndpoint")]
        public string ServerEndpoint { get; set; }

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; }

        [JsonPropertyName("dataIdentity")]
        public string DataIdentity { get; set; }

        [JsonPropertyName("detail")]
        public string Detail { get; set; }
    }

    internal sealed class BrQaEventEmitter : IDisposable
    {
        private readonly string _outputDirectory;
        private readonly string _sessionToken;
        private readonly string _eventsPath;
        private readonly int _processId;
        private readonly string _processStartTime;
        private readonly object _lock = new object();
        private bool _disposed;

        public string ServerEndpoint { get; set; }
        public string ClientVersion { get; set; }
        public string DataIdentity { get; set; }

        public BrQaEventEmitter(string outputDirectory, string sessionToken)
        {
            _outputDirectory = outputDirectory;
            _sessionToken = sessionToken;
            _eventsPath = Path.Combine(outputDirectory, "qa-events.jsonl");

            Directory.CreateDirectory(outputDirectory);

            using var process = Process.GetCurrentProcess();
            _processId = process.Id;
            _processStartTime = process.StartTime.ToUniversalTime().ToString("O");
        }

        public void Emit(string eventType, string detail = null)
        {
            if (_disposed)
                return;

            var qaEvent = new BrQaEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventType = eventType,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                SessionToken = _sessionToken,
                ProcessId = _processId,
                ProcessStartTime = _processStartTime,
                ServerEndpoint = ServerEndpoint,
                ClientVersion = ClientVersion,
                DataIdentity = DataIdentity,
                Detail = detail
            };

            var json = JsonSerializer.Serialize(qaEvent, BrQaJsonContext.Default.BrQaEvent);

            lock (_lock)
            {
                var tempPath = _eventsPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    if (File.Exists(_eventsPath))
                        File.Copy(_eventsPath, tempPath, overwrite: true);

                    File.AppendAllText(tempPath, json + Environment.NewLine, new UTF8Encoding(false));
                    File.Move(tempPath, _eventsPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }
        }

        public void EmitProcessStarted() => Emit("process-started");
        public void EmitSettingsLoaded() => Emit("settings-loaded");
        public void EmitLegalDataOpened() => Emit("legal-data-opened");
        public void EmitAssetsLoaded() => Emit("assets-loaded");
        public void EmitWindowReady() => Emit("window-ready");
        public void EmitNetworkConnected(string endpoint) => Emit("network-connected", endpoint);
        public void EmitServerListReceived(int count) => Emit("server-list-received", "count=" + count);
        public void EmitAccountAuthenticated() => Emit("account-authenticated");
        public void EmitCharacterListReceived(int count) => Emit("character-list-received", "count=" + count);
        public void EmitCharacterEnteredWorld(string name) => Emit("character-entered-world", name);
        public void EmitMovementAcknowledged(string direction) => Emit("movement-acknowledged", direction);
        public void EmitGumpOpened(uint gumpId) => Emit("gump-opened", "gumpId=" + gumpId);
        public void EmitJournalObserved(string text) => Emit("journal-observed", text);
        public void EmitScenarioCompleted(string name) => Emit("scenario-completed", name);
        public void EmitClientExiting() => Emit("client-exiting");
        public void EmitClientExited() => Emit("client-exited");
        public void EmitScenarioFailed(string reason) => Emit("scenario-failed", reason);

        public void Dispose()
        {
            _disposed = true;
        }
    }

    [JsonSerializable(typeof(BrQaEvent))]
    internal partial class BrQaJsonContext : JsonSerializerContext
    {
    }
}
