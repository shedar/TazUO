using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicUO.BeyondRecallQA;
using Xunit;

namespace ClassicUO.UnitTests.BeyondRecallQA;

public sealed class BrQaContractTests
{
    [Fact]
    public void OrdinaryClientArgumentsLeaveQaModeDisabled()
    {
        var config = BrQaConfig.Parse(new[] { "-settings", "/tmp/settings.json", "-reconnect", "false" });

        Assert.False(config.Enabled);
    }

    [Fact]
    public void QaArgumentsRejectUnknownDuplicateMissingAndOptionLikeValues()
    {
        Assert.Throws<ArgumentException>(() => BrQaConfig.Parse(new[] { "-br-qa-unknown", "value" }));
        Assert.Throws<ArgumentException>(() => BrQaConfig.Parse(new[] { "-br-qa-plan", "first", "-br-qa-plan", "second" }));
        Assert.Throws<ArgumentException>(() => BrQaConfig.Parse(new[] { "-br-qa-plan" }));
        Assert.Throws<ArgumentException>(() => BrQaConfig.Parse(new[] { "-br-qa-plan", "-br-qa-output" }));
    }

    [Fact]
    public void QaConfigRequiresSafePathsAndModeSixHundredSecret()
    {
        var fixture = Fixture.Create();
        var config = BrQaConfig.Parse(fixture.Arguments());

        Assert.True(config.Enabled);
        Assert.Equal(fixture.Output, config.OutputDirectory);
        Assert.Equal("127.0.0.1:2593", config.ServerEndpoint);
        Assert.Equal("7.0.116.0", config.ClientVersion);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Secret, UnixFileMode.UserRead | UnixFileMode.GroupRead);
            Assert.Throws<ArgumentException>(() => BrQaConfig.Parse(fixture.Arguments()));
        }
    }

    [Fact]
    public void QaConfigRejectsSymlinkedPathComponents()
    {
        if (OperatingSystem.IsWindows())
            return;

        var fixture = Fixture.Create();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var linked = Path.Combine(fixture.Root, "linked-output");
        Directory.CreateSymbolicLink(linked, outside);
        var plan = Path.Combine(linked, "plan.json");
        File.WriteAllText(Path.Combine(outside, "plan.json"), ValidPlan);

        Assert.Throws<ArgumentException>(
            () => BrQaConfig.Parse(fixture.Arguments(output: linked, plan: plan))
        );
    }

    [Fact]
    public void PlanSchemaIsStrictAndActionsAreBounded()
    {
        var fixture = Fixture.Create();
        var plan = BrQaPlan.Load(fixture.Plan);

        Assert.Equal("contract", plan.Name);
        Assert.Equal(new[] { "connect", "exit" }, plan.Actions.Select(action => action.Type));

        File.WriteAllText(fixture.Plan, ValidPlan.Replace("\"name\":\"contract\"", "\"name\":\"contract\",\"unknown\":true"));
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"contract\",\"timeoutSeconds\":901,\"actions\":[{\"type\":\"exit\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void PhaseZeroCActionsRequireAliasesAndRejectArbitraryServerCommands()
    {
        var fixture = Fixture.Create();
        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"phase0c\",\"timeoutSeconds\":60,\"actions\":[" +
            "{\"type\":\"use-skill\",\"skillIndex\":17}," +
            "{\"type\":\"wait-target-cursor\"}," +
            "{\"type\":\"target-serial\",\"target\":\"fixture\"}," +
            "{\"type\":\"vendor-buy\",\"containerTarget\":\"fixture\",\"target\":\"fixture\",\"amount\":1}," +
            "{\"type\":\"exit\"}]}"
        );

        Assert.Equal(5, BrQaPlan.Load(fixture.Plan).Actions.Count);

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"phase0c\",\"actions\":[{\"type\":\"target-serial\",\"serial\":1}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"phase0c\",\"actions\":[{\"type\":\"send-server-command\",\"command\":\"[Admin]\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void EveryPhaseZeroCActionShapeIsStrictlyValidated()
    {
        var fixture = Fixture.Create();
        string[] actions =
        {
            "{\"type\":\"walk-path\",\"target\":\"fixture\"}",
            "{\"type\":\"say-to-mobile\",\"target\":\"fixture\",\"message\":\"bank\"}",
            "{\"type\":\"wait-system-message\",\"contains\":\"accepted\"}",
            "{\"type\":\"invoke-qa-checkpoint\",\"target\":\"checkpoint-1\"}",
            "{\"type\":\"wait-gump\",\"target\":\"fixture\"}",
            "{\"type\":\"double-click-serial\",\"target\":\"fixture\"}",
            "{\"type\":\"single-click-serial\",\"target\":\"fixture\"}",
            "{\"type\":\"attack-serial\",\"target\":\"fixture\"}",
            "{\"type\":\"cast-spell\",\"spellIndex\":42}",
            "{\"type\":\"use-skill\",\"skillIndex\":17}",
            "{\"type\":\"wait-target-cursor\"}",
            "{\"type\":\"target-serial\",\"target\":\"fixture\"}",
            "{\"type\":\"target-location\",\"target\":\"fixture\"}",
            "{\"type\":\"open-context-menu\",\"target\":\"fixture\"}",
            "{\"type\":\"select-context-entry\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-container\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-item\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-mobile\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-property\",\"target\":\"fixture\",\"contains\":\"exceptional\"}",
            "{\"type\":\"wait-status\",\"field\":\"dead\",\"value\":\"false\"}",
            "{\"type\":\"wait-combat-delta\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-vendor-gump\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-trade-window\",\"target\":\"fixture\"}",
            "{\"type\":\"gump-set-text\",\"target\":\"fixture\",\"entryId\":1,\"text\":\"qa\"}",
            "{\"type\":\"gump-select-switch\",\"target\":\"fixture\",\"switchId\":1}",
            "{\"type\":\"gump-select-radio\",\"target\":\"fixture\",\"switchId\":1}",
            "{\"type\":\"gump-select-list-entry\",\"target\":\"fixture\",\"entry\":1}",
            "{\"type\":\"gump-click-button\",\"target\":\"fixture\"}",
            "{\"type\":\"drag-item\",\"target\":\"fixture\",\"amount\":1}",
            "{\"type\":\"drop-item\",\"target\":\"fixture\",\"containerTarget\":\"fixture\"}",
            "{\"type\":\"vendor-buy\",\"containerTarget\":\"fixture\",\"target\":\"fixture\",\"amount\":1}",
            "{\"type\":\"vendor-sell\",\"containerTarget\":\"fixture\",\"target\":\"fixture\",\"amount\":1}",
            "{\"type\":\"accept-trade\",\"target\":\"fixture\",\"accepted\":true}",
            "{\"type\":\"wait-duration\",\"durationMs\":50}",
            "{\"type\":\"wait-skill\",\"skillIndex\":17,\"minimum\":20.0}",
            "{\"type\":\"wait-house-customization\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-house-customization-exited\"}",
            "{\"type\":\"house-add-component\",\"graphic\":12788,\"x\":0,\"y\":0}",
            "{\"type\":\"house-remove-component\",\"graphic\":12788,\"x\":0,\"y\":0,\"z\":7}",
            "{\"type\":\"house-backup\"}",
            "{\"type\":\"house-restore\"}",
            "{\"type\":\"house-revert\"}",
            "{\"type\":\"house-commit\"}",
            "{\"type\":\"house-exit\"}"
        };

        foreach (string action in actions)
        {
            File.WriteAllText(
                fixture.Plan,
                "{\"schemaVersion\":2,\"name\":\"phase0c\",\"timeoutSeconds\":60,\"actions\":[" +
                action + "]}"
            );
            Assert.Single(BrQaPlan.Load(fixture.Plan).Actions);
        }

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"phase0c\",\"actions\":[{\"type\":\"wait-gump\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void TargetDataIsHashSessionBuildAndExpiryBound()
    {
        var fixture = Fixture.Create();
        var config = BrQaConfig.Parse(fixture.Arguments());
        var document = BrQaTargetDocument.Load(config);

        Assert.Equal(1u, document.Require("fixture").Serial);

        File.AppendAllText(fixture.Targets, " ");
        Assert.Throws<InvalidDataException>(() => BrQaTargetDocument.Load(config));
    }

    [Fact]
    public void EventsAreOrderedAtomicAndDoNotFabricateMilestones()
    {
        var fixture = Fixture.Create();
        var config = BrQaConfig.Parse(fixture.Arguments());
        using (var emitter = new BrQaEventEmitter(config))
        {
            emitter.ServerEndpoint = "127.0.0.1:2593";
            emitter.ClientVersion = "7.0.116.0";
            emitter.EmitProcessStarted();
            emitter.EmitSettingsLoaded();
            emitter.EmitScenarioFailed("action-timeout", 1, "connect");
        }

        var lines = File.ReadAllLines(Path.Combine(fixture.Output, "qa-events.jsonl"));
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        using var third = JsonDocument.Parse(lines[2]);
        Assert.Equal(1, first.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("process-started", first.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("127.0.0.1:2593", first.RootElement.GetProperty("serverEndpoint").GetString());
        Assert.Equal("7.0.116.0", first.RootElement.GetProperty("clientVersion").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("settings-loaded", second.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("scenario-failed", third.RootElement.GetProperty("eventType").GetString());
        Assert.DoesNotContain("assets-loaded", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.DoesNotContain("account-authenticated", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output, "*.tmp-*"));
    }

    [Fact]
    public void PhaseZeroCFunctionalEventsAreExplicitAndOrdered()
    {
        var fixture = Fixture.Create();
        var config = BrQaConfig.Parse(fixture.Arguments());
        using (var emitter = new BrQaEventEmitter(config))
        {
            emitter.EmitSkillUseRequested(17);
            emitter.EmitTargetSent("fixture");
            emitter.EmitVendorGumpOpened("fixture");
            emitter.EmitVendorBuyResponseSent("fixture", "fixture", 1);
            emitter.EmitContextMenuResponseSent("fixture", 1);
            emitter.EmitCombatActionSent("fixture");
            emitter.EmitTradeWindowOpened("fixture");
            emitter.EmitTradeResponseSent(true);
            emitter.EmitHouseCustomizationEntered("fixture");
            emitter.EmitHouseComponentAdded(12788, 0, 0);
            emitter.EmitHouseCustomizationCommitted();
        }

        string[] types = File.ReadAllLines(Path.Combine(fixture.Output, "qa-events.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("eventType").GetString())
            .ToArray();
        Assert.Equal(
            new[]
            {
                "skill-use-requested", "target-sent", "vendor-gump-opened", "vendor-buy-response-sent",
                "context-menu-response-sent", "combat-action-sent", "trade-window-opened",
                "trade-response-sent", "house-customization-entered", "house-component-added",
                "house-customization-committed"
            },
            types
        );
        Assert.DoesNotContain("trade-completed", types);
    }

    [Fact]
    public void SecretParserNeverEchoesSecretMaterialInFailures()
    {
        var fixture = Fixture.Create();
        const string marker = "DO-NOT-ECHO-THIS-PASSWORD";
        File.WriteAllText(fixture.Secret, "{\"schemaVersion\":1,\"username\":\"qa\",\"password\":\"" + marker + "\",\"unexpected\":true}");

        var exception = Assert.Throws<InvalidDataException>(() => BrQaSecret.Read(fixture.Secret));

        Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq", "fixture-only")]
    [InlineData("qa", "ppppppppppppppppppppppppppppppp")]
    [InlineData("qa user", "fixture-only")]
    public void SecretParserRejectsCredentialsThatCannotRoundTripThroughLoginPackets(string username, string password)
    {
        var fixture = Fixture.Create();
        File.WriteAllText(
            fixture.Secret,
            JsonSerializer.Serialize(new { schemaVersion = 1, username, password })
        );

        Assert.Throws<InvalidDataException>(() => BrQaSecret.Read(fixture.Secret));
    }

    private const string ValidPlan =
        "{\"schemaVersion\":2,\"name\":\"contract\",\"timeoutSeconds\":30,\"actions\":[{\"type\":\"connect\"},{\"type\":\"exit\"}]}";

    private sealed record Fixture(
        string Root,
        string Output,
        string Plan,
        string Secrets,
        string Secret,
        string Targets,
        string TargetIdentity
    )
    {
        public static Fixture Create()
        {
            var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
            var root = Path.Combine(temporaryRoot, "tazuo-br-qa-tests", Guid.NewGuid().ToString("N"));
            var output = Path.Combine(root, "output");
            var secrets = Path.Combine(root, "secrets");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(secrets);
            var plan = Path.Combine(output, "plan.json");
            var targets = Path.Combine(output, "targets.json");
            var secret = Path.Combine(secrets, "qa.secret");
            File.WriteAllText(plan, ValidPlan);
            File.WriteAllText(secret, "{\"schemaVersion\":1,\"username\":\"qa\",\"password\":\"fixture-only\"}");
            File.WriteAllText(
                targets,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        sessionToken = new string('a', 32),
                        deploymentId = new string('d', 64),
                        sourceIdentity = new string('e', 64),
                        manifestIdentity = new string('f', 64),
                        dataIdentity = new string('b', 64),
                        preparationBuildIdentity = new string('c', 64),
                        tazuoSourceCommit = BrQaBuildInfo.SourceCommit,
                        tazuoBuildIdentity = BrQaBuildInfo.BuildIdentity,
                        expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                        targets = new
                        {
                            fixture = new { serial = 1u, type = "Fixture" }
                        }
                    }
                )
            );
            string targetIdentity = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targets))).ToLowerInvariant();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new Fixture(root, output, plan, secrets, secret, targets, targetIdentity);
        }

        public string[] Arguments(string output = null, string plan = null) =>
        new[]
        {
            "-ip", "127.0.0.1",
            "-port", "2593",
            "-clientversion", "7.0.116.0",
            "-br-qa-plan", plan ?? Plan,
            "-br-qa-output", output ?? Output,
            "-br-qa-session", new string('a', 32),
            "-br-qa-secret-file", Secret,
            "-br-qa-secrets-root", Secrets,
            "-br-qa-data-identity", new string('b', 64),
            "-br-qa-preparation-build-identity", new string('c', 64),
            "-br-qa-target-data", Targets,
            "-br-qa-target-identity", TargetIdentity,
            "-br-qa-exit-on-complete"
        };
    }
}
