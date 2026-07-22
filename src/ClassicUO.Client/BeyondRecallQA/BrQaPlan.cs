using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaPlan
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 120;

        [JsonPropertyName("actions")]
        public List<BrQaAction> Actions { get; set; } = new List<BrQaAction>();

        public static BrQaPlan Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Beyond Recall QA plan file was not found.");

            BrQaPlan plan;
            try
            {
                plan = JsonSerializer.Deserialize(File.ReadAllText(path), BrQaPlanJsonContext.Default.BrQaPlan);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Beyond Recall QA plan JSON is invalid or contains unknown fields.", exception);
            }

            if (plan == null)
                throw new InvalidDataException("Beyond Recall QA plan was empty.");
            if (plan.SchemaVersion != 1)
                throw new InvalidDataException("Beyond Recall QA plan schema version must be 1.");
            if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 64 ||
                plan.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            {
                throw new InvalidDataException("Beyond Recall QA plan name is invalid.");
            }
            if (plan.TimeoutSeconds is < 1 or > 300)
                throw new InvalidDataException("Beyond Recall QA plan timeout must be between 1 and 300 seconds.");
            if (plan.Actions == null || plan.Actions.Count is < 1 or > 64)
                throw new InvalidDataException("Beyond Recall QA plan must contain between 1 and 64 actions.");

            for (var i = 0; i < plan.Actions.Count; i++)
                ValidateAction(plan.Actions[i], i);

            return plan;
        }

        private static void ValidateAction(BrQaAction action, int index)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Type))
                throw Error(index, "action type is required");
            if (action.TimeoutSeconds is < 1 or > 60)
                throw Error(index, "timeoutSeconds must be between 1 and 60");

            switch (action.Type)
            {
                case "connect":
                case "authenticate-from-secret":
                case "wait-enter-world":
                case "open-paperdoll":
                case "open-backpack":
                case "open-skills":
                case "logout":
                case "exit":
                    RequireNoPayload(action, index);
                    break;
                case "select-or-create-named-qa-character":
                    if (!IsSafeCharacterName(action.CharacterName))
                        throw Error(index, "characterName must be a bounded safe UO character name");
                    RequireEmpty(action.Direction, action.Message, action.Contains, action.Command);
                    if (action.Steps != 1 || action.GumpId != null)
                        throw Error(index, "unexpected fields for character selection");
                    break;
                case "walk-relative":
                    if (!Directions.Contains(action.Direction, StringComparer.OrdinalIgnoreCase))
                        throw Error(index, "direction is invalid");
                    if (action.Steps is < 1 or > 10)
                        throw Error(index, "steps must be between 1 and 10");
                    RequireEmpty(action.CharacterName, action.Message, action.Contains, action.Command);
                    if (action.GumpId != null)
                        throw Error(index, "unexpected gumpId for walk-relative");
                    break;
                case "speak":
                    if (!IsBoundedText(action.Message, 80))
                        throw Error(index, "message is required and may not exceed 80 characters");
                    RequireEmpty(action.CharacterName, action.Direction, action.Contains, action.Command);
                    if (action.Steps != 1 || action.GumpId != null)
                        throw Error(index, "unexpected fields for speak");
                    break;
                case "wait-journal":
                    if (!IsBoundedText(action.Contains, 80))
                        throw Error(index, "contains is required and may not exceed 80 characters");
                    RequireEmpty(action.CharacterName, action.Direction, action.Message, action.Command);
                    if (action.Steps != 1 || action.GumpId != null)
                        throw Error(index, "unexpected fields for wait-journal");
                    break;
                case "send-server-command":
                    if (!IsBoundedText(action.Command, 80) || !action.Command.StartsWith("[", StringComparison.Ordinal))
                        throw Error(index, "command must be a bounded bracket command");
                    RequireEmpty(action.CharacterName, action.Direction, action.Message, action.Contains);
                    if (action.Steps != 1 || action.GumpId != null)
                        throw Error(index, "unexpected fields for send-server-command");
                    break;
                case "wait-gump":
                    RequireEmpty(action.CharacterName, action.Direction, action.Message, action.Contains, action.Command);
                    if (action.Steps != 1)
                        throw Error(index, "unexpected steps for wait-gump");
                    break;
                default:
                    throw Error(index, "unknown action type");
            }
        }

        private static readonly string[] Directions =
        {
            "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"
        };

        private static bool IsSafeCharacterName(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 30 &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-');

        private static bool IsBoundedText(string value, int maximum) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
            !value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0');

        private static void RequireNoPayload(BrQaAction action, int index)
        {
            RequireEmpty(action.CharacterName, action.Direction, action.Message, action.Contains, action.Command);
            if (action.Steps != 1 || action.GumpId != null)
                throw Error(index, "action has unexpected payload fields");
        }

        private static void RequireEmpty(params string[] values)
        {
            if (values.Any(value => value != null))
                throw new InvalidDataException("Beyond Recall QA action has unexpected payload fields.");
        }

        private static InvalidDataException Error(int index, string detail) =>
            new InvalidDataException($"Beyond Recall QA action {index} {detail}.");
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class BrQaAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 30;

        [JsonPropertyName("characterName")]
        public string CharacterName { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; }

        [JsonPropertyName("steps")]
        public int Steps { get; set; } = 1;

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("contains")]
        public string Contains { get; set; }

        [JsonPropertyName("command")]
        public string Command { get; set; }

        [JsonPropertyName("gumpId")]
        public uint? GumpId { get; set; }
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BrQaPlan))]
    [JsonSerializable(typeof(BrQaAction))]
    internal partial class BrQaPlanJsonContext : JsonSerializerContext
    {
    }
}
