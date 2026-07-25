using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicUO.BeyondRecallQA;
using ClassicUO.Configuration;
using ClassicUO.Network.PacketHandlers;
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
    public void QaProfileDisablesBackgroundCorpseOpening()
    {
        var profile = new Profile
        {
            AutoOpenCorpses = true,
            AutoOpenOwnCorpse = true
        };

        BrQaSession.ApplyDeterministicProfileSettings(profile);

        Assert.False(profile.AutoOpenCorpses);
        Assert.False(profile.AutoOpenOwnCorpse);
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
    public void FoundationCommandUsesModernUoPrefixSyntaxWithoutClosingBracket()
    {
        var fixture = Fixture.Create();
        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"foundation\",\"actions\":[" +
            "{\"type\":\"send-server-command\",\"command\":\"[BrQaGump\"}]}"
        );

        Assert.Single(BrQaPlan.Load(fixture.Plan).Actions);

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"foundation\",\"actions\":[" +
            "{\"type\":\"send-server-command\",\"command\":\"[BrQaGump]\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void ReportFrameCaptureIsAParameterlessOptInQaAction()
    {
        var fixture = Fixture.Create();
        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"capture\",\"actions\":[{\"type\":\"capture-frame\"}]}"
        );

        Assert.Single(BrQaPlan.Load(fixture.Plan).Actions);

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"capture\",\"actions\":[" +
            "{\"type\":\"capture-frame\",\"target\":\"fixture\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void TargetCancelIsAParameterlessOptInQaAction()
    {
        var fixture = Fixture.Create();
        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"target-cancel\",\"actions\":[{\"type\":\"cancel-target\"}]}"
        );

        Assert.Single(BrQaPlan.Load(fixture.Plan).Actions);

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"target-cancel\",\"actions\":[" +
            "{\"type\":\"cancel-target\",\"target\":\"fixture\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void ModernUoServerChangePacketHasAnExplicitHandler()
    {
        Assert.Contains(
            PacketHandlerRegistry.GetHandlers(),
            entry => entry.Id == 0x76 && entry.Handler.Method.DeclaringType == typeof(ServerChange)
        );
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
            "{\"type\":\"wait-item-parent\",\"target\":\"fixture\",\"containerTarget\":\"fixture\"}",
            "{\"type\":\"wait-mobile\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-property\",\"target\":\"fixture\",\"contains\":\"exceptional\"}",
            "{\"type\":\"wait-status\",\"field\":\"dead\",\"value\":\"false\"}",
            "{\"type\":\"wait-status\",\"field\":\"map-index\",\"value\":\"0\"}",
            "{\"type\":\"wait-player-location\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-combat-delta\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-vendor-gump\",\"target\":\"fixture\"}",
            "{\"type\":\"wait-trade-window\",\"target\":\"fixture\"}",
            "{\"type\":\"gump-set-text\",\"target\":\"fixture\",\"entryId\":1,\"text\":\"qa\"}",
            "{\"type\":\"gump-select-switch\",\"target\":\"fixture\",\"switchId\":1}",
            "{\"type\":\"gump-select-radio\",\"target\":\"fixture\",\"switchId\":1}",
            "{\"type\":\"gump-select-radio\",\"target\":\"fixture\"}",
            "{\"type\":\"gump-select-list-entry\",\"target\":\"fixture\",\"entry\":1}",
            "{\"type\":\"gump-click-button\",\"target\":\"fixture\"}",
            "{\"type\":\"equip-item\",\"target\":\"fixture\"}",
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

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"phase0c\",\"actions\":[" +
            "{\"type\":\"wait-item-parent\",\"target\":\"fixture\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
    }

    [Fact]
    public void EquipActionRemainsAliasBoundAndRejectsPacketShapingFields()
    {
        var fixture = Fixture.Create();
        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"equip\",\"actions\":[" +
            "{\"type\":\"equip-item\",\"target\":\"fixture\"}]}"
        );
        Assert.Single(BrQaPlan.Load(fixture.Plan).Actions);

        File.WriteAllText(
            fixture.Plan,
            "{\"schemaVersion\":2,\"name\":\"equip\",\"actions\":[" +
            "{\"type\":\"equip-item\",\"target\":\"fixture\",\"layer\":10}]}"
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
    public void CheckpointCommandUsesModernUoPrefixSyntaxWithoutClosingBracket()
    {
        Assert.Equal(
            $"[BrQaScenario {new string('a', 32)} checkpoint-1",
            BrQaSession.BuildCheckpointCommand(new string('a', 32), "checkpoint-1")
        );
    }

    [Fact]
    public void SecureTradeAliasBindsExactlyOneObservedWindow()
    {
        var target = new BrQaTarget { GumpId = 1, Type = "secure_trade" };

        Assert.False(BrQaSession.TryBindSingleTradeSerial(target, Array.Empty<uint>(), out _));
        Assert.True(BrQaSession.TryBindSingleTradeSerial(target, new uint[] { 0x40000001 }, out uint serial));
        Assert.Equal(0x40000001u, serial);
        Assert.Equal(serial, target.Serial);

        var ambiguous = new BrQaTarget { GumpId = 1, Type = "secure_trade" };
        Assert.Throws<InvalidDataException>(
            () => BrQaSession.TryBindSingleTradeSerial(
                ambiguous,
                new uint[] { 0x40000001, 0x40000002 },
                out _
            )
        );
    }

    [Fact]
    public void SecureTradeResponseUsesLocalContainerInsteadOfWindowAlias()
    {
        const uint peerMobileWindowSerial = 0x00011395;
        const uint localTradeContainerSerial = 0x40054321;

        Assert.Equal(
            localTradeContainerSerial,
            BrQaSession.TradeResponseContainerSerial(peerMobileWindowSerial, localTradeContainerSerial)
        );
        Assert.Throws<InvalidDataException>(() => BrQaSession.TradeResponseContainerSerial(0, localTradeContainerSerial));
        Assert.Throws<InvalidDataException>(
            () => BrQaSession.TradeResponseContainerSerial(peerMobileWindowSerial, 0)
        );
    }

    [Fact]
    public void LogoutClearsHandshakeStateForRoleBoundReconnect()
    {
        bool authenticated = true;
        bool serverSelected = true;
        bool enteredWorld = true;

        BrQaSession.ResetLoginStateAfterLogout(ref authenticated, ref serverSelected, ref enteredWorld);

        Assert.False(authenticated);
        Assert.False(serverSelected);
        Assert.False(enteredWorld);
    }

    [Fact]
    public void JournalBarriersCannotReuseACompletedObservation()
    {
        Assert.True(BrQaSession.ClearsJournalExpectation("wait-journal"));
        Assert.True(BrQaSession.ClearsJournalExpectation("wait-system-message"));
        Assert.False(BrQaSession.ClearsJournalExpectation("speak"));
        Assert.False(BrQaSession.ClearsJournalExpectation("invoke-qa-checkpoint"));
    }

    [Fact]
    public void JournalHistoryBridgesCrossClientRacesWithoutReusingAMatch()
    {
        var history = new BrQaJournalHistory(capacity: 3);
        history.Add("unrelated");
        history.Add("br-qa-housing-door-ready");

        Assert.True(history.TryConsumeMatch("br-qa-housing-door-ready", out string match));
        Assert.Equal("br-qa-housing-door-ready", match);
        Assert.False(history.TryConsumeMatch("br-qa-housing-door-ready", out _));

        history.Add("first");
        history.Add("second");
        history.Add("third");
        history.Add("bounded");
        Assert.False(history.TryConsumeMatch("first", out _));
        history.Add("bounded");
        Assert.True(history.TryConsumeMatch("bounded", out _));
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
        Assert.True(
            DateTimeOffset.Parse(second.RootElement.GetProperty("timestamp").GetString()!) >
            DateTimeOffset.Parse(first.RootElement.GetProperty("timestamp").GetString()!)
        );
        Assert.True(
            DateTimeOffset.Parse(third.RootElement.GetProperty("timestamp").GetString()!) >
            DateTimeOffset.Parse(second.RootElement.GetProperty("timestamp").GetString()!)
        );
        Assert.DoesNotContain("assets-loaded", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.DoesNotContain("account-authenticated", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output, "*.tmp-*"));
    }

    [Fact]
    public void EvidenceTimestampRemainsStrictlyIncreasingAcrossWallClockRegression()
    {
        var previous = DateTimeOffset.Parse("2026-07-24T14:57:38.8559050Z");
        var regressed = DateTimeOffset.Parse("2026-07-24T14:57:38.7946670Z");
        var advanced = previous.AddMilliseconds(10);

        Assert.Equal(previous.AddTicks(1), BrQaEventEmitter.NextTimestamp(previous, regressed));
        Assert.Equal(advanced, BrQaEventEmitter.NextTimestamp(previous, advanced));
        Assert.Equal(DateTimeOffset.MaxValue, BrQaEventEmitter.NextTimestamp(DateTimeOffset.MaxValue, regressed));
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
            emitter.EmitTargetCancelled();
            emitter.EmitVendorGumpOpened("fixture");
            emitter.EmitVendorBuyResponseSent("fixture", "fixture", 1);
            emitter.EmitContextMenuResponseSent("fixture", 1);
            emitter.EmitCombatActionSent("fixture");
            emitter.EmitItemEquipped("fixture");
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
                "skill-use-requested", "target-sent", "target-cancel-sent", "vendor-gump-opened", "vendor-buy-response-sent",
                "context-menu-response-sent", "combat-action-sent", "item-equipped", "trade-window-opened",
                "trade-response-sent", "house-customization-entered", "house-component-added",
                "house-customization-committed"
            },
            types
        );
        Assert.DoesNotContain("trade-completed", types);
    }

    [Fact]
    public void CaptureAndProtocolDiagnosticsAreBoundedTypedEvents()
    {
        var fixture = Fixture.Create();
        var config = BrQaConfig.Parse(fixture.Arguments());
        using (var emitter = new BrQaEventEmitter(config))
        {
            emitter.EmitFrameCaptured(800, 600, new string('a', 64));
            emitter.EmitProtocolDiagnostic("packet-handler-exception", "0xDD");
        }

        string[] lines = File.ReadAllLines(Path.Combine(fixture.Output, "qa-events.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"eventType\":\"frame-captured\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("width=800;height=600;sha256=", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"eventType\":\"protocol-diagnostic\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("kind=packet-handler-exception;packetId=0xDD", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("password", string.Join('\n', lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObservedActionsRetryRequestsAtABoundedCadence()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        DateTimeOffset retryAfter = default;
        var requests = 0;

        Assert.False(
            BrQaSession.AwaitObservationWithRetry(
                observed: false,
                requestIssued: false,
                now,
                ref retryAfter,
                () => requests++
            )
        );
        Assert.Equal(1, requests);
        Assert.Equal(now.AddSeconds(1), retryAfter);

        Assert.False(
            BrQaSession.AwaitObservationWithRetry(
                observed: false,
                requestIssued: true,
                now.AddMilliseconds(999),
                ref retryAfter,
                () => requests++
            )
        );
        Assert.Equal(1, requests);

        Assert.False(
            BrQaSession.AwaitObservationWithRetry(
                observed: false,
                requestIssued: true,
                now.AddSeconds(1),
                ref retryAfter,
                () => requests++
            )
        );
        Assert.Equal(2, requests);

        Assert.True(
            BrQaSession.AwaitObservationWithRetry(
                observed: true,
                requestIssued: true,
                now.AddSeconds(2),
                ref retryAfter,
                () => requests++
            )
        );
        Assert.Equal(2, requests);
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
                        sourceIdentity = new string('e', 40),
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
