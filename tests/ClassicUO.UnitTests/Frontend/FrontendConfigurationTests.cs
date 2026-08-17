using System;
using System.IO;
using ClassicUO.Frontend;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Frontend;

public sealed class FrontendConfigurationTests
{
    [Fact]
    public void DefaultsToLocalFrontend()
    {
        FrontendOptions options = FrontendConfiguration.Parse(Array.Empty<string>());

        options.Mode.Should().Be(FrontendMode.Local);
        options.FramesPerSecond.Should().Be(15);
        options.WebSocketPort.Should().Be(19870);
        options.HideNativeWindow.Should().BeTrue();
        options.FrameFormat.Should().Be(FrontendFrameFormat.RawRgba);
        options.InstrumentationPath.Should().BeNull();
    }

    [Fact]
    public void ParsesNullFrontendAndSharedOptions()
    {
        FrontendOptions options = FrontendConfiguration.Parse(
            new[]
            {
                "-frontend", "none",
                "-frontend_fps", "24",
                "-frontend-port", "21987",
                "-frontend-hide-window", "false",
                "-frontend-format", "png",
                "-frontend-instrumentation", "metrics.json"
            }
        );

        options.Mode.Should().Be(FrontendMode.Null);
        options.FramesPerSecond.Should().Be(24);
        options.WebSocketPort.Should().Be(21987);
        options.HideNativeWindow.Should().BeFalse();
        options.FrameFormat.Should().Be(FrontendFrameFormat.Png);
        options.InstrumentationPath.Should().Be(Path.GetFullPath("metrics.json"));
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("ws")]
    [InlineData("remote")]
    public void ParsesWebSocketAliases(string value)
    {
        FrontendOptions options = FrontendConfiguration.Parse(new[] { "-frontend", value });

        options.Mode.Should().Be(FrontendMode.WebSocket);
    }

    [Theory]
    [InlineData("-frontend", "unknown")]
    [InlineData("-frontend-fps", "0")]
    [InlineData("-frontend-fps", "61")]
    [InlineData("-frontend-port", "80")]
    [InlineData("-frontend-hide-window", "perhaps")]
    [InlineData("-frontend-frame-format", "jpeg")]
    public void RejectsInvalidValues(string option, string value)
    {
        Action parse = () => FrontendConfiguration.Parse(new[] { option, value });

        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RejectsMissingValues()
    {
        Action parse = () => FrontendConfiguration.Parse(new[] { "-frontend" });

        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LocalAndNullAdaptersExposeTheirAttachmentState()
    {
        using IFrontendAdapter local = FrontendAdapterFactory.Create(
            new FrontendOptions { Mode = FrontendMode.Local }
        );
        using IFrontendAdapter none = FrontendAdapterFactory.Create(
            new FrontendOptions { Mode = FrontendMode.Null }
        );

        local.IsAttached.Should().BeTrue();
        local.WantsFrame(0).Should().BeTrue();
        none.IsAttached.Should().BeFalse();
        none.WantsFrame(0).Should().BeFalse();
    }
}
