using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
    internal sealed class BrQaEvent
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }

        [JsonPropertyName("eventId")]
        public string EventId { get; set; }

        [JsonPropertyName("eventType")]
        public string EventType { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }

        [JsonPropertyName("sessionToken")]
        public string SessionToken { get; set; }

        [JsonPropertyName("tazuoSourceCommit")]
        public string TazUoSourceCommit { get; set; }

        [JsonPropertyName("tazuoBuildIdentity")]
        public string TazUoBuildIdentity { get; set; }

        [JsonPropertyName("preparationBuildIdentity")]
        public string PreparationBuildIdentity { get; set; }

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

    internal static class BrQaBuildInfo
    {
        public static string SourceCommit { get; } = Read("BeyondRecallSourceCommit");
        public static string BuildIdentity { get; } = Read("BeyondRecallBuildIdentity");

        private static string Read(string key) =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == key)?.Value ?? "not-injected";
    }

    internal sealed class BrQaEventEmitter : IDisposable
    {
        private readonly BrQaConfig _config;
        private readonly string _eventsPath;
        private readonly int _processId;
        private readonly string _processStartTime;
        private readonly object _lock = new object();
        private long _sequence;
        private DateTimeOffset _lastTimestamp;
        private bool _disposed;

        public string EventsPath => _eventsPath;
        public string ServerEndpoint { get; set; }
        public string ClientVersion { get; set; }

        public BrQaEventEmitter(BrQaConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _eventsPath = Path.Combine(config.OutputDirectory, "qa-events.jsonl");
            if (File.Exists(_eventsPath))
                throw new InvalidOperationException("Beyond Recall QA event stream already exists and will not be overwritten.");

            using var process = Process.GetCurrentProcess();
            _processId = process.Id;
            _processStartTime = process.StartTime.ToUniversalTime().ToString("O");
            ServerEndpoint = config.ServerEndpoint;
            ClientVersion = config.ClientVersion;
        }

        internal void Emit(string eventType, string detail = null)
        {
            if (_disposed)
                return;
            if (!BrQaEventTypes.IsKnown(eventType))
                throw new ArgumentException("Unknown Beyond Recall QA event type.", nameof(eventType));
            if (detail != null && (detail.Length > 256 || detail.Contains('\r') || detail.Contains('\n')))
                throw new ArgumentException("Beyond Recall QA event detail is not bounded.", nameof(detail));

            lock (_lock)
            {
                _lastTimestamp = NextTimestamp(_lastTimestamp, DateTimeOffset.UtcNow);
                var qaEvent = new BrQaEvent
                {
                    Sequence = ++_sequence,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = eventType,
                    Timestamp = _lastTimestamp.ToString("O"),
                    SessionToken = _config.SessionToken,
                    TazUoSourceCommit = BrQaBuildInfo.SourceCommit,
                    TazUoBuildIdentity = BrQaBuildInfo.BuildIdentity,
                    PreparationBuildIdentity = _config.PreparationBuildIdentity,
                    ProcessId = _processId,
                    ProcessStartTime = _processStartTime,
                    ServerEndpoint = ServerEndpoint,
                    ClientVersion = ClientVersion,
                    DataIdentity = _config.DataIdentity,
                    Detail = detail
                };

                var line = JsonSerializer.Serialize(qaEvent, BrQaJsonContext.Default.BrQaEvent) + Environment.NewLine;
                var temporary = _eventsPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (File.Exists(_eventsPath))
                        {
                            using var input = new FileStream(_eventsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            input.CopyTo(output);
                        }

                        var bytes = new UTF8Encoding(false).GetBytes(line);
                        output.Write(bytes, 0, bytes.Length);
                        output.Flush(flushToDisk: true);
                    }

                    File.Move(temporary, _eventsPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }

        internal static DateTimeOffset NextTimestamp(DateTimeOffset previous, DateTimeOffset observed)
        {
            if (observed > previous)
                return observed;
            if (previous == DateTimeOffset.MaxValue)
                return previous;

            return previous.AddTicks(1);
        }

        public void EmitProcessStarted() => Emit("process-started");
        public void EmitSettingsLoaded() => Emit("settings-loaded");
        public void EmitLegalDataOpened() => Emit("legal-data-opened");
        public void EmitAssetsLoaded() => Emit("assets-loaded");
        public void EmitWindowReady() => Emit("window-ready");
        public void EmitNetworkConnected() => Emit("network-connected");
        public void EmitAccountAuthenticated() => Emit("account-authenticated");
        public void EmitServerListReceived(int count) => Emit("server-list-received", "count=" + count);
        public void EmitCharacterListReceived(int count) => Emit("character-list-received", "count=" + count);
        public void EmitCharacterEnteredWorld() => Emit("character-entered-world");
        public void EmitMovementAcknowledged(byte sequence) => Emit("movement-acknowledged", "sequence=" + sequence);
        public void EmitGumpOpened(uint gumpId) => Emit("gump-opened", "gumpId=" + gumpId);
        public void EmitJournalObserved(string sha256) => Emit("journal-observed", "matched=true;sha256=" + sha256);
        public void EmitActionStarted(int index, string type, string target) =>
            Emit("scenario-action-started", $"index={index};type={type};target={target ?? "none"}");
        public void EmitActionCompleted(int index, string type, string target) =>
            Emit("scenario-action-completed", $"index={index};type={type};target={target ?? "none"}");
        public void EmitSkillUseRequested(int skillIndex) =>
            Emit("skill-use-requested", "skillIndex=" + skillIndex);
        public void EmitSkillThresholdObserved(int skillIndex, double minimum) =>
            Emit("skill-threshold-observed", $"skillIndex={skillIndex};minimum={minimum:R}");
        public void EmitSpellCastRequested(int spellIndex) =>
            Emit("spell-cast-requested", "spellIndex=" + spellIndex);
        public void EmitTargetSent(string target) => Emit("target-sent", "target=" + target);
        public void EmitTargetCancelled() => Emit("target-cancel-sent");
        public void EmitContainerOpened(string target) => Emit("container-opened", "target=" + target);
        public void EmitItemObserved(string target) => Emit("item-observed", "target=" + target);
        public void EmitMobileObserved(string target) => Emit("mobile-observed", "target=" + target);
        public void EmitPropertyObserved(string target) => Emit("property-observed", "target=" + target);
        public void EmitStatusObserved(string field) => Emit("status-observed", "field=" + field);
        public void EmitGumpResponseSent(uint gumpId, int buttonId) =>
            Emit("gump-response-sent", $"gumpId={gumpId};buttonId={buttonId}");
        public void EmitVendorGumpOpened(string target) => Emit("vendor-gump-opened", "target=" + target);
        public void EmitVendorBuyResponseSent(string vendor, string item, int amount) =>
            Emit("vendor-buy-response-sent", $"vendor={vendor};item={item};amount={amount}");
        public void EmitVendorSellResponseSent(string vendor, string item, int amount) =>
            Emit("vendor-sell-response-sent", $"vendor={vendor};item={item};amount={amount}");
        public void EmitContextMenuResponseSent(string target, int entry) =>
            Emit("context-menu-response-sent", $"target={target};entry={entry}");
        public void EmitCombatActionSent(string target) => Emit("combat-action-sent", "target=" + target);
        public void EmitCombatDeltaObserved(string target) => Emit("combat-delta-observed", "target=" + target);
        public void EmitItemDragged(string target, int amount) =>
            Emit("item-dragged", $"target={target};amount={amount}");
        public void EmitItemEquipped(string target) => Emit("item-equipped", "target=" + target);
        public void EmitItemDropped(string target, string container) =>
            Emit("item-dropped", $"target={target};container={container ?? "ground"}");
        public void EmitTradeWindowOpened(string target) => Emit("trade-window-opened", "target=" + target);
        public void EmitTradeResponseSent(bool accepted) =>
            Emit("trade-response-sent", "accepted=" + accepted.ToString().ToLowerInvariant());
        public void EmitHouseCustomizationEntered(string target) =>
            Emit("house-customization-entered", "target=" + target);
        public void EmitHouseComponentAdded(int graphic, int x, int y) =>
            Emit("house-component-added", $"graphic={graphic};x={x};y={y}");
        public void EmitHouseComponentRemoved(int graphic, int x, int y, int z) =>
            Emit("house-component-removed", $"graphic={graphic};x={x};y={y};z={z}");
        public void EmitHouseCustomizationOperation(string operation) =>
            Emit("house-customization-operation", "operation=" + operation);
        public void EmitHouseCustomizationCommitted() => Emit("house-customization-committed");
        public void EmitFrameCaptured(int width, int height, string sha256) =>
            Emit("frame-captured", $"width={width};height={height};sha256={sha256}");
        public void EmitProtocolDiagnostic(string kind, string packetId) =>
            Emit("protocol-diagnostic", $"kind={kind};packetId={packetId ?? "none"}");
        public void EmitScenarioActionFailed(string code, int actionIndex, string actionType) =>
            Emit(
                "scenario-action-failed",
                $"code={code};actionIndex={actionIndex};actionType={actionType ?? "none"}"
            );
        public void EmitScenarioCompleted(string name) => Emit("scenario-completed", "name=" + name);
        public void EmitClientExiting() => Emit("client-exiting");
        public void EmitClientExited() => Emit("client-exited");
        public void EmitScenarioFailed(string code, int actionIndex, string actionType) =>
            Emit("scenario-failed", $"code={code};actionIndex={actionIndex};actionType={actionType ?? "none"}");

        public void Dispose()
        {
            _disposed = true;
        }
    }

    internal static class BrQaEventTypes
    {
        private static readonly string[] Known =
        {
            "process-started", "settings-loaded", "legal-data-opened", "assets-loaded", "window-ready",
            "network-connected", "account-authenticated", "server-list-received", "character-list-received",
            "character-entered-world", "movement-acknowledged", "gump-opened", "journal-observed",
            "scenario-action-started", "scenario-action-completed", "skill-use-requested",
            "spell-cast-requested", "target-sent", "target-cancel-sent", "container-opened", "item-observed", "mobile-observed",
            "property-observed", "status-observed", "gump-response-sent", "vendor-gump-opened",
            "vendor-buy-response-sent", "vendor-sell-response-sent", "context-menu-response-sent",
            "combat-action-sent", "combat-delta-observed", "item-dragged", "item-equipped", "item-dropped",
            "trade-window-opened", "trade-response-sent", "skill-threshold-observed",
            "house-customization-entered", "house-component-added", "house-component-removed",
            "house-customization-operation", "house-customization-committed", "frame-captured",
            "protocol-diagnostic", "scenario-action-failed",
            "scenario-completed", "client-exiting", "client-exited", "scenario-failed"
        };

        public static bool IsKnown(string value) => Array.IndexOf(Known, value) >= 0;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
    [JsonSerializable(typeof(BrQaEvent))]
    internal partial class BrQaJsonContext : JsonSerializerContext
    {
    }
}
