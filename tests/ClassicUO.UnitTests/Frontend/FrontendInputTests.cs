using ClassicUO.Frontend;
using ClassicUO.Input;
using FluentAssertions;
using SDL3;
using Xunit;

namespace ClassicUO.UnitTests.Frontend;

public sealed class FrontendInputTests
{
    [Fact]
    public void ConvertsBrowserPointerButtonAndFrameIdentity()
    {
        var message = new FrontendInputMessage
        {
            Type = "pointerDown",
            X = 12.6,
            Y = 44.2,
            Button = 2,
            FrameId = 91
        };

        FrontendInputConverter.TryConvert(message, out FrontendInputEvent input).Should().BeTrue();
        input.Kind.Should().Be(FrontendInputKind.PointerDown);
        input.X.Should().Be(13);
        input.Y.Should().Be(44);
        input.Button.Should().Be(MouseButtonType.Right);
        input.FrameId.Should().Be(91);
    }

    [Fact]
    public void ConvertsKeyboardCodeAndModifiers()
    {
        var message = new FrontendInputMessage
        {
            Type = "keyDown",
            Code = "KeyA",
            CtrlKey = true,
            ShiftKey = true
        };

        FrontendInputConverter.TryConvert(message, out FrontendInputEvent input).Should().BeTrue();
        input.Key.Should().Be((uint)SDL.SDL_Keycode.SDLK_A);
        input.Modifiers.Should().HaveFlag(SDL.SDL_Keymod.SDL_KMOD_CTRL);
        input.Modifiers.Should().HaveFlag(SDL.SDL_Keymod.SDL_KMOD_SHIFT);
    }

    [Theory]
    [InlineData("ArrowLeft", SDL.SDL_Keycode.SDLK_LEFT)]
    [InlineData("F12", SDL.SDL_Keycode.SDLK_F12)]
    [InlineData("Digit7", SDL.SDL_Keycode.SDLK_7)]
    [InlineData("BracketRight", SDL.SDL_Keycode.SDLK_RIGHTBRACKET)]
    public void TranslatesRepresentativeBrowserCodes(string code, SDL.SDL_Keycode expected)
    {
        FrontendKeyTranslator.TryTranslate(code, out uint key).Should().BeTrue();
        key.Should().Be((uint)expected);
    }

    [Fact]
    public void RejectsMalformedOrUnboundedInput()
    {
        FrontendInputConverter.TryConvert(
            new FrontendInputMessage { Type = "pointerDown", Button = 99 },
            out _
        ).Should().BeFalse();
        FrontendInputConverter.TryConvert(
            new FrontendInputMessage { Type = "resize", Width = 100, Height = 100 },
            out _
        ).Should().BeFalse();
        FrontendInputConverter.TryConvert(
            new FrontendInputMessage { Type = "text", Text = new string('x', 65) },
            out _
        ).Should().BeFalse();
        FrontendKeyTranslator.TryTranslate("Unidentified", out _).Should().BeFalse();
    }
}
