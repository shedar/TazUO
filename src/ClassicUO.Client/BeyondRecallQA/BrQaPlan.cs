// SPDX-License-Identifier: BSD-2-Clause

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
            if (plan.SchemaVersion != 2)
                throw new InvalidDataException("Beyond Recall QA plan schema version must be 2.");
            if (!IsSafeIdentifier(plan.Name, 96))
                throw new InvalidDataException("Beyond Recall QA plan name is invalid.");
            if (plan.TimeoutSeconds is < 1 or > 900)
                throw new InvalidDataException("Beyond Recall QA plan timeout must be between 1 and 900 seconds.");
            if (plan.Actions == null || plan.Actions.Count is < 1 or > 512)
                throw new InvalidDataException("Beyond Recall QA plan must contain between 1 and 512 actions.");

            for (var i = 0; i < plan.Actions.Count; i++)
                ValidateAction(plan.Actions[i], i);

            return plan;
        }

        private static void ValidateAction(BrQaAction action, int index)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Type))
                throw Error(index, "action type is required");
            if (action.TimeoutSeconds is < 1 or > 120)
                throw Error(index, "timeoutSeconds must be between 1 and 120");

            switch (action.Type)
            {
                case "connect":
                case "authenticate-from-secret":
                case "wait-enter-world":
                case "open-paperdoll":
                case "open-backpack":
                case "open-skills":
                case "wait-target-cursor":
                case "cancel-target":
                case "capture-frame":
                case "logout":
                case "exit":
                    RequireOnly(action, index);
                    break;
                case "select-or-create-named-qa-character":
                    if (!IsSafeCharacterName(action.CharacterName))
                        throw Error(index, "characterName must be a bounded safe UO character name");
                    RequireOnly(action, index, nameof(action.CharacterName));
                    break;
                case "walk-relative":
                    if (!IsDirection(action.Direction) || action.Steps is < 1 or > 10)
                        throw Error(index, "walk-relative direction/steps are invalid");
                    RequireOnly(action, index, nameof(action.Direction), nameof(action.Steps));
                    break;
                case "walk-path":
                    RequireAlias(action.Target, index);
                    RequireOnly(action, index, nameof(action.Target));
                    break;
                case "speak":
                    if (!IsBoundedText(action.Message, 80))
                        throw Error(index, "message is required and may not exceed 80 characters");
                    RequireOnly(action, index, nameof(action.Message));
                    break;
                case "say-to-mobile":
                    RequireAlias(action.Target, index);
                    if (!IsBoundedText(action.Message, 80))
                        throw Error(index, "message is required and may not exceed 80 characters");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.Message));
                    break;
                case "wait-journal":
                case "wait-system-message":
                    if (!IsBoundedText(action.Contains, 120))
                        throw Error(index, "contains is required and may not exceed 120 characters");
                    RequireOnly(action, index, nameof(action.Contains));
                    break;
                case "send-server-command":
                    if (!string.Equals(action.Command, "[BrQaGump", StringComparison.Ordinal))
                        throw Error(index, "send-server-command is restricted to the foundation BrQaGump command");
                    RequireOnly(action, index, nameof(action.Command));
                    break;
                case "invoke-qa-checkpoint":
                    RequireAlias(action.Target, index);
                    if (action.Target.Length > 30)
                        throw Error(index, "checkpoint alias may not exceed 30 characters");
                    RequireOnly(action, index, nameof(action.Target));
                    break;
                case "wait-gump":
                    ValidateGumpSelector(action, index);
                    RequireOnly(action, index, nameof(action.Target), nameof(action.GumpId));
                    break;
                case "double-click-serial":
                case "single-click-serial":
                case "attack-serial":
                case "target-serial":
                case "equip-item":
                case "open-context-menu":
                case "wait-container":
                case "wait-item":
                case "wait-mobile":
                case "wait-combat-delta":
                case "wait-vendor-gump":
                case "wait-trade-window":
                case "wait-player-location":
                    RequireAlias(action.Target, index);
                    RequireOnly(action, index, nameof(action.Target));
                    break;
                case "wait-item-parent":
                    RequireAlias(action.Target, index);
                    RequireAlias(action.ContainerTarget, index);
                    RequireOnly(action, index, nameof(action.Target), nameof(action.ContainerTarget));
                    break;
                case "use-skill":
                    if (action.SkillIndex is null or < 0 or > 57)
                        throw Error(index, "skillIndex must be between 0 and 57");
                    RequireOnly(action, index, nameof(action.SkillIndex));
                    break;
                case "cast-spell":
                    if (action.SpellIndex is null or < 0 or > 2048)
                        throw Error(index, "spellIndex must be between 0 and 2048");
                    RequireOnly(action, index, nameof(action.SpellIndex));
                    break;
                case "target-location":
                    RequireAlias(action.Target, index);
                    RequireOnly(action, index, nameof(action.Target));
                    break;
                case "select-context-entry":
                    RequireAlias(action.Target, index);
                    if (action.Entry is < 0 or > ushort.MaxValue)
                        throw Error(index, "entry must be between 0 and 65535");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.Entry));
                    break;
                case "wait-property":
                    RequireAlias(action.Target, index);
                    if (!IsBoundedText(action.Contains, 160))
                        throw Error(index, "contains is required and may not exceed 160 characters");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.Contains));
                    break;
                case "wait-status":
                    if (action.Target != null)
                        RequireAlias(action.Target, index);
                    if (!IsSafeIdentifier(action.Field, 32) || !IsBoundedText(action.Value, 64))
                        throw Error(index, "wait-status field/value are invalid");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.Field), nameof(action.Value));
                    break;
                case "gump-click-button":
                    ValidateGumpSelector(action, index);
                    if (action.ButtonId is < 0 || action.ButtonId == null && action.Target == null)
                        throw Error(index, "buttonId must be non-negative");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.GumpId), nameof(action.ButtonId));
                    break;
                case "gump-set-text":
                    ValidateGumpSelector(action, index);
                    if (action.EntryId is null or < 0 or > ushort.MaxValue || !IsBoundedText(action.Text, 160))
                        throw Error(index, "gump text entry payload is invalid");
                    RequireOnly(
                        action,
                        index,
                        nameof(action.Target),
                        nameof(action.GumpId),
                        nameof(action.EntryId),
                        nameof(action.Text)
                    );
                    break;
                case "gump-select-switch":
                case "gump-select-radio":
                case "gump-select-list-entry":
                    ValidateGumpSelector(action, index);
                    bool missingSelection = action.Type == "gump-select-list-entry"
                        ? action.Entry is null or < 0
                        : action.SwitchId is null or < 0 &&
                          (action.Type != "gump-select-radio" || action.Target == null);
                    if (missingSelection)
                        throw Error(index, "gump selection payload is invalid");
                    string selection = action.Type == "gump-select-list-entry"
                        ? nameof(action.Entry)
                        : nameof(action.SwitchId);
                    RequireOnly(action, index, nameof(action.Target), nameof(action.GumpId), selection);
                    break;
                case "drag-item":
                    RequireAlias(action.Target, index);
                    if (action.Amount is null or < 1 or > ushort.MaxValue)
                        throw Error(index, "amount must be between 1 and 65535");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.Amount));
                    break;
                case "drop-item":
                    RequireAlias(action.Target, index);
                    if (action.ContainerTarget != null)
                        RequireAlias(action.ContainerTarget, index);
                    if (action.X is < 0 or > 7168 || action.Y is < 0 or > 7168 || action.Z is < -128 or > 127)
                        throw Error(index, "drop coordinates are outside UO bounds");
                    RequireOnly(
                        action,
                        index,
                        nameof(action.Target),
                        nameof(action.ContainerTarget),
                        nameof(action.X),
                        nameof(action.Y),
                        nameof(action.Z)
                    );
                    break;
                case "vendor-buy":
                case "vendor-sell":
                    RequireAlias(action.ContainerTarget, index);
                    RequireAlias(action.Target, index);
                    if (action.Amount is null or < 1 or > ushort.MaxValue)
                        throw Error(index, "amount must be between 1 and 65535");
                    RequireOnly(
                        action,
                        index,
                        nameof(action.ContainerTarget),
                        nameof(action.Target),
                        nameof(action.Amount)
                    );
                    break;
                case "accept-trade":
                    RequireAlias(action.Target, index);
                    if (action.Accepted == null)
                        throw Error(index, "accepted is required");
                    RequireOnly(action, index, nameof(action.Target), nameof(action.Accepted));
                    break;
                case "wait-duration":
                    if (action.DurationMs is null or < 50 or > 10000)
                        throw Error(index, "durationMs must be between 50 and 10000");
                    RequireOnly(action, index, nameof(action.DurationMs));
                    break;
                case "wait-skill":
                    if (action.SkillIndex is null or < 0 or > 57 || action.Minimum is null or < 0 or > 120)
                        throw Error(index, "wait-skill requires skillIndex 0..57 and minimum 0..120");
                    RequireOnly(action, index, nameof(action.SkillIndex), nameof(action.Minimum));
                    break;
                case "wait-house-customization":
                    RequireAlias(action.Target, index);
                    RequireOnly(action, index, nameof(action.Target));
                    break;
                case "wait-house-customization-exited":
                case "house-backup":
                case "house-restore":
                case "house-revert":
                case "house-commit":
                case "house-exit":
                    RequireOnly(action, index);
                    break;
                case "house-add-component":
                    if (action.Graphic is null or < 1 or > ushort.MaxValue ||
                        action.X is null or < -64 or > 64 || action.Y is null or < -64 or > 64)
                    {
                        throw Error(index, "house-add-component requires graphic and relative x/y within -64..64");
                    }
                    RequireOnly(action, index, nameof(action.Graphic), nameof(action.X), nameof(action.Y));
                    break;
                case "house-remove-component":
                    if (action.Graphic is null or < 1 or > ushort.MaxValue ||
                        action.X is null or < -64 or > 64 || action.Y is null or < -64 or > 64 ||
                        action.Z is null or < -128 or > 127)
                    {
                        throw Error(index, "house-remove-component requires bounded graphic/x/y/z");
                    }
                    RequireOnly(
                        action,
                        index,
                        nameof(action.Graphic),
                        nameof(action.X),
                        nameof(action.Y),
                        nameof(action.Z)
                    );
                    break;
                default:
                    throw Error(index, "unknown action type");
            }
        }

        private static void ValidateGumpSelector(BrQaAction action, int index)
        {
            if (action.Target == null && action.GumpId == null)
                throw Error(index, "gump action requires target or gumpId");
            if (action.Target != null)
                RequireAlias(action.Target, index);
        }

        private static void RequireAlias(string alias, int index)
        {
            if (!BrQaTargetDocument.IsSafeAlias(alias))
                throw Error(index, "target alias is invalid");
        }

        private static void RequireOnly(BrQaAction action, int index, params string[] allowedNames)
        {
            var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
            bool invalid =
                (!allowed.Contains(nameof(action.CharacterName)) && action.CharacterName != null) ||
                (!allowed.Contains(nameof(action.Direction)) && action.Direction != null) ||
                (!allowed.Contains(nameof(action.Steps)) && action.Steps != 1) ||
                (!allowed.Contains(nameof(action.Message)) && action.Message != null) ||
                (!allowed.Contains(nameof(action.Contains)) && action.Contains != null) ||
                (!allowed.Contains(nameof(action.Command)) && action.Command != null) ||
                (!allowed.Contains(nameof(action.Target)) && action.Target != null) ||
                (!allowed.Contains(nameof(action.ContainerTarget)) && action.ContainerTarget != null) ||
                (!allowed.Contains(nameof(action.Field)) && action.Field != null) ||
                (!allowed.Contains(nameof(action.Value)) && action.Value != null) ||
                (!allowed.Contains(nameof(action.Text)) && action.Text != null) ||
                (!allowed.Contains(nameof(action.GumpId)) && action.GumpId != null) ||
                (!allowed.Contains(nameof(action.SkillIndex)) && action.SkillIndex != null) ||
                (!allowed.Contains(nameof(action.SpellIndex)) && action.SpellIndex != null) ||
                (!allowed.Contains(nameof(action.ButtonId)) && action.ButtonId != null) ||
                (!allowed.Contains(nameof(action.Entry)) && action.Entry != null) ||
                (!allowed.Contains(nameof(action.EntryId)) && action.EntryId != null) ||
                (!allowed.Contains(nameof(action.SwitchId)) && action.SwitchId != null) ||
                (!allowed.Contains(nameof(action.Amount)) && action.Amount != null) ||
                (!allowed.Contains(nameof(action.X)) && action.X != null) ||
                (!allowed.Contains(nameof(action.Y)) && action.Y != null) ||
                (!allowed.Contains(nameof(action.Z)) && action.Z != null) ||
                (!allowed.Contains(nameof(action.Accepted)) && action.Accepted != null) ||
                (!allowed.Contains(nameof(action.DurationMs)) && action.DurationMs != null) ||
                (!allowed.Contains(nameof(action.Minimum)) && action.Minimum != null) ||
                (!allowed.Contains(nameof(action.Graphic)) && action.Graphic != null);

            if (invalid)
                throw Error(index, "has unexpected payload fields");
        }

        internal static bool IsDirection(string direction) =>
            direction != null && Directions.Contains(direction, StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Directions =
        {
            "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"
        };

        private static bool IsSafeCharacterName(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 30 &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-');

        private static bool IsSafeIdentifier(string value, int maximum) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        private static bool IsBoundedText(string value, int maximum) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
            !value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0');

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

        [JsonPropertyName("target")]
        public string Target { get; set; }

        [JsonPropertyName("containerTarget")]
        public string ContainerTarget { get; set; }

        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("gumpId")]
        public uint? GumpId { get; set; }

        [JsonPropertyName("skillIndex")]
        public int? SkillIndex { get; set; }

        [JsonPropertyName("spellIndex")]
        public int? SpellIndex { get; set; }

        [JsonPropertyName("buttonId")]
        public int? ButtonId { get; set; }

        [JsonPropertyName("entry")]
        public int? Entry { get; set; }

        [JsonPropertyName("entryId")]
        public int? EntryId { get; set; }

        [JsonPropertyName("switchId")]
        public int? SwitchId { get; set; }

        [JsonPropertyName("amount")]
        public int? Amount { get; set; }

        [JsonPropertyName("x")]
        public int? X { get; set; }

        [JsonPropertyName("y")]
        public int? Y { get; set; }

        [JsonPropertyName("z")]
        public int? Z { get; set; }

        [JsonPropertyName("accepted")]
        public bool? Accepted { get; set; }

        [JsonPropertyName("durationMs")]
        public int? DurationMs { get; set; }

        [JsonPropertyName("minimum")]
        public double? Minimum { get; set; }

        [JsonPropertyName("graphic")]
        public int? Graphic { get; set; }
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BrQaPlan))]
    [JsonSerializable(typeof(BrQaAction))]
    internal partial class BrQaPlanJsonContext : JsonSerializerContext
    {
    }
}
