// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaTargetDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("sessionToken")]
        public string SessionToken { get; set; }

        [JsonPropertyName("deploymentId")]
        public string DeploymentId { get; set; }

        [JsonPropertyName("sourceIdentity")]
        public string SourceIdentity { get; set; }

        [JsonPropertyName("manifestIdentity")]
        public string ManifestIdentity { get; set; }

        [JsonPropertyName("dataIdentity")]
        public string DataIdentity { get; set; }

        [JsonPropertyName("preparationBuildIdentity")]
        public string PreparationBuildIdentity { get; set; }

        [JsonPropertyName("tazuoSourceCommit")]
        public string TazUoSourceCommit { get; set; }

        [JsonPropertyName("tazuoBuildIdentity")]
        public string TazUoBuildIdentity { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("targets")]
        public SortedDictionary<string, BrQaTarget> Targets { get; set; } =
            new SortedDictionary<string, BrQaTarget>(StringComparer.Ordinal);

        public static BrQaTargetDocument Load(BrQaConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            byte[] bytes = File.ReadAllBytes(config.TargetDataPath);
            string identity = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(identity),
                    System.Text.Encoding.ASCII.GetBytes(config.TargetDataIdentity)))
            {
                throw new InvalidDataException("Beyond Recall QA target data identity is invalid.");
            }

            BrQaTargetDocument document;
            try
            {
                document = JsonSerializer.Deserialize(bytes, BrQaTargetJsonContext.Default.BrQaTargetDocument);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Beyond Recall QA target data JSON is invalid or contains unknown fields.", exception);
            }

            if (document == null || document.SchemaVersion != 1 ||
                !string.Equals(document.SessionToken, config.SessionToken, StringComparison.Ordinal) ||
                !IsLowerHex(document.DeploymentId, 64) || !IsLowerHex(document.SourceIdentity, 40) ||
                !IsLowerHex(document.ManifestIdentity, 64) ||
                !string.Equals(document.DataIdentity, config.DataIdentity, StringComparison.Ordinal) ||
                !string.Equals(document.PreparationBuildIdentity, config.PreparationBuildIdentity, StringComparison.Ordinal) ||
                !string.Equals(document.TazUoSourceCommit, BrQaBuildInfo.SourceCommit, StringComparison.Ordinal) ||
                !string.Equals(document.TazUoBuildIdentity, BrQaBuildInfo.BuildIdentity, StringComparison.Ordinal) ||
                document.ExpiresAt <= DateTimeOffset.UtcNow || document.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(15) ||
                document.Targets == null || document.Targets.Count is < 1 or > 512)
            {
                throw new InvalidDataException("Beyond Recall QA target data provenance is malformed, stale, or mismatched.");
            }

            foreach (var pair in document.Targets)
            {
                if (!IsSafeAlias(pair.Key) || pair.Value == null)
                    throw new InvalidDataException("Beyond Recall QA target data contains an invalid target alias.");

                pair.Value.Validate(pair.Key);
            }

            return document;
        }

        public BrQaTarget Require(string alias)
        {
            if (!IsSafeAlias(alias) || !Targets.TryGetValue(alias, out BrQaTarget target))
                throw new InvalidDataException("Beyond Recall QA action references an unknown target alias.");

            return target;
        }

        private static bool IsLowerHex(string value, int length) =>
            value != null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        internal static bool IsSafeAlias(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 96 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaTarget
    {
        [JsonPropertyName("serial")]
        public uint? Serial { get; set; }

        [JsonPropertyName("x")]
        public ushort? X { get; set; }

        [JsonPropertyName("y")]
        public ushort? Y { get; set; }

        [JsonPropertyName("z")]
        public short? Z { get; set; }

        [JsonPropertyName("graphic")]
        public ushort? Graphic { get; set; }

        [JsonPropertyName("gumpId")]
        public uint? GumpId { get; set; }

        [JsonPropertyName("contextEntry")]
        public ushort? ContextEntry { get; set; }

        [JsonPropertyName("buttonId")]
        public int? ButtonId { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("path")]
        public List<BrQaPathStep> Path { get; set; } = new List<BrQaPathStep>();

        public uint RequireSerial()
        {
            if (Serial is not > 0)
                throw new InvalidDataException("Beyond Recall QA target does not provide a serial.");
            return Serial.Value;
        }

        public void Validate(string alias)
        {
            if (Serial == 0 || X > 7168 || Y > 7168 || Z is < -128 or > 127 || ButtonId < 0 ||
                string.IsNullOrWhiteSpace(Type) || Type.Length > 192 ||
                Type.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`')) ||
                (Map != null && (Map.Length > 32 || Map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ')))) ||
                Path == null || Path.Count > 256 ||
                (Serial == null && (X == null || Y == null) && Graphic == null && GumpId == null &&
                 ContextEntry == null && ButtonId == null && Path.Count == 0))
            {
                throw new InvalidDataException("Beyond Recall QA target '" + alias + "' is malformed or outside bounds.");
            }

            foreach (BrQaPathStep step in Path)
            {
                if (step == null)
                    throw new InvalidDataException("Beyond Recall QA target '" + alias + "' contains a null path step.");
                step.Validate(alias);
            }
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaPathStep
    {
        [JsonPropertyName("direction")]
        public string Direction { get; set; }

        [JsonPropertyName("steps")]
        public int Steps { get; set; }

        public void Validate(string alias)
        {
            if (!BrQaPlan.IsDirection(Direction) || Steps is < 1 or > 10)
                throw new InvalidDataException("Beyond Recall QA target '" + alias + "' contains an invalid path step.");
        }
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BrQaTargetDocument))]
    [JsonSerializable(typeof(BrQaTarget))]
    [JsonSerializable(typeof(BrQaPathStep))]
    internal partial class BrQaTargetJsonContext : JsonSerializerContext
    {
    }
}
