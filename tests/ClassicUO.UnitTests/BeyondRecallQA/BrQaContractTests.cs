using System;
using System.IO;
using System.Linq;
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
            "{\"schemaVersion\":1,\"name\":\"contract\",\"timeoutSeconds\":301,\"actions\":[{\"type\":\"exit\"}]}"
        );
        Assert.Throws<InvalidDataException>(() => BrQaPlan.Load(fixture.Plan));
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
        Assert.Equal(2, second.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("settings-loaded", second.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("scenario-failed", third.RootElement.GetProperty("eventType").GetString());
        Assert.DoesNotContain("assets-loaded", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.DoesNotContain("account-authenticated", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output, "*.tmp-*"));
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

    private const string ValidPlan =
        "{\"schemaVersion\":1,\"name\":\"contract\",\"timeoutSeconds\":30,\"actions\":[{\"type\":\"connect\"},{\"type\":\"exit\"}]}";

    private sealed record Fixture(string Root, string Output, string Plan, string Secrets, string Secret)
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
            var secret = Path.Combine(secrets, "qa.secret");
            File.WriteAllText(plan, ValidPlan);
            File.WriteAllText(secret, "{\"schemaVersion\":1,\"username\":\"qa\",\"password\":\"fixture-only\"}");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new Fixture(root, output, plan, secrets, secret);
        }

        public string[] Arguments(string output = null, string plan = null) =>
        new[]
        {
            "-br-qa-plan", plan ?? Plan,
            "-br-qa-output", output ?? Output,
            "-br-qa-session", new string('a', 32),
            "-br-qa-secret-file", Secret,
            "-br-qa-secrets-root", Secrets,
            "-br-qa-data-identity", new string('b', 64),
            "-br-qa-preparation-build-identity", new string('c', 64),
            "-br-qa-exit-on-complete"
        };
    }
}
