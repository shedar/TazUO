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
        local.Resources.EnableAudio.Should().BeTrue();
        local.Resources.EnableVoiceRecognition.Should().BeTrue();
        local.Resources.EnableNativeInput.Should().BeTrue();
        local.Resources.EnableRenderLoop.Should().BeTrue();
        local.Resources.RequiresComposedFramebuffer.Should().BeTrue();
        local.Resources.PresentNativeFramebuffer.Should().BeTrue();
        none.Resources.EnableAudio.Should().BeFalse();
        none.Resources.EnableVoiceRecognition.Should().BeFalse();
        none.Resources.EnableNativeInput.Should().BeFalse();
        none.Resources.EnableRenderLoop.Should().BeFalse();
        none.Resources.RequiresComposedFramebuffer.Should().BeFalse();
        none.Resources.PresentNativeFramebuffer.Should().BeFalse();
    }

    [Theory]
    [InlineData("display-list")]
    [InlineData("displaylist")]
    [InlineData("commands")]
    [InlineData("draw")]
    public void ParsesDisplayListAliases(string value)
    {
        FrontendOptions options = FrontendConfiguration.Parse(
            new[] { "-frontend-frame-format", value }
        );

        options.FrameFormat.Should().Be(FrontendFrameFormat.DisplayList);
    }

    [Fact]
    public void DisplayListWebSocketAdapterDoesNotRequireAComposedFramebuffer()
    {
        using IFrontendAdapter displayList = FrontendAdapterFactory.Create(
            new FrontendOptions
            {
                Mode = FrontendMode.WebSocket,
                FrameFormat = FrontendFrameFormat.DisplayList,
                HideNativeWindow = true
            }
        );
        using IFrontendAdapter png = FrontendAdapterFactory.Create(
            new FrontendOptions
            {
                Mode = FrontendMode.WebSocket,
                FrameFormat = FrontendFrameFormat.Png,
                HideNativeWindow = true
            }
        );

        displayList.Resources.EnableAudio.Should().BeFalse();
        displayList.Resources.EnableVoiceRecognition.Should().BeFalse();
        displayList.Resources.EnableNativeInput.Should().BeFalse();
        displayList.Resources.EnableRenderLoop.Should().BeTrue();
        displayList.Resources.RequiresComposedFramebuffer.Should().BeFalse();
        displayList.Resources.PresentNativeFramebuffer.Should().BeFalse();
        displayList.RenderCommandSink.Should().NotBeNull();
        png.Resources.RequiresComposedFramebuffer.Should().BeTrue();
        png.RenderCommandSink.Should().BeNull();
    }
}
