// SPDX-License-Identifier: BSD-2-Clause
// Beyond Recall QA evidence mode - typed plan engine with bounded timeouts.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicUO.BeyondRecallQA
{
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
                throw new FileNotFoundException("BR QA plan file not found: " + path);

            var json = File.ReadAllText(path);
            var plan = JsonSerializer.Deserialize(json, BrQaPlanJsonContext.Default.BrQaPlan);

            if (plan == null)
                throw new InvalidDataException("BR QA plan file was empty or invalid.");

            if (plan.SchemaVersion != 1)
                throw new InvalidDataException("BR QA plan schema version must be 1, got: " + plan.SchemaVersion);

            if (string.IsNullOrWhiteSpace(plan.Name))
                throw new InvalidDataException("BR QA plan must have a name.");

            if (plan.TimeoutSeconds <= 0)
                throw new InvalidDataException("BR QA plan timeout must be positive.");

            foreach (var action in plan.Actions)
                ValidateAction(action);

            return plan;
        }

        private static void ValidateAction(BrQaAction action)
        {
            if (string.IsNullOrWhiteSpace(action.Type))
                throw new InvalidDataException("BR QA action must have a type.");

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
                    break;

                case "select-or-create-named-qa-character":
                    if (string.IsNullOrWhiteSpace(action.CharacterName))
                        throw new InvalidDataException("select-or-create-named-qa-character requires characterName.");
                    break;

                case "walk-relative":
                    if (string.IsNullOrWhiteSpace(action.Direction))
                        throw new InvalidDataException("walk-relative requires direction.");
                    if (action.Steps <= 0)
                        throw new InvalidDataException("walk-relative requires positive steps.");
                    break;

                case "speak":
                    if (string.IsNullOrWhiteSpace(action.Message))
                        throw new InvalidDataException("speak requires message.");
                    break;

                case "wait-journal":
                    if (string.IsNullOrWhiteSpace(action.Contains))
                        throw new InvalidDataException("wait-journal requires contains.");
                    break;

                case "send-server-command":
                    if (string.IsNullOrWhiteSpace(action.Command))
                        throw new InvalidDataException("send-server-command requires command.");
                    break;

                case "wait-gump":
                    break;

                case "capture-rendered-frame":
                    break;

                default:
                    throw new InvalidDataException("Unknown BR QA action type: " + action.Type);
            }

            if (action.TimeoutSeconds < 0)
                throw new InvalidDataException("BR QA action timeout cannot be negative.");
        }
    }

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

    [JsonSerializable(typeof(BrQaPlan))]
    [JsonSerializable(typeof(BrQaAction))]
    internal partial class BrQaPlanJsonContext : JsonSerializerContext
    {
    }
}
