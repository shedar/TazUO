using System;
using System.Collections.Generic;
using ClassicUO.Input;
using SDL3;

namespace ClassicUO.Frontend;

internal enum FrontendInputKind
{
    PointerMove,
    PointerDown,
    PointerUp,
    Wheel,
    KeyDown,
    KeyUp,
    Text,
    Resize
}

internal readonly record struct FrontendInputEvent(
    FrontendInputKind Kind,
    int X = 0,
    int Y = 0,
    MouseButtonType Button = MouseButtonType.None,
    float WheelY = 0,
    uint Key = 0,
    SDL.SDL_Keymod Modifiers = SDL.SDL_Keymod.SDL_KMOD_NONE,
    string Text = null,
    int Width = 0,
    int Height = 0,
    long FrameId = 0
);

internal sealed class FrontendInputMessage
{
    public string Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Button { get; set; }
    public double DeltaY { get; set; }
    public string Code { get; set; }
    public string Key { get; set; }
    public string Text { get; set; }
    public bool AltKey { get; set; }
    public bool CtrlKey { get; set; }
    public bool ShiftKey { get; set; }
    public bool MetaKey { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long FrameId { get; set; }
}

internal static class FrontendInputConverter
{
    public static bool TryConvert(FrontendInputMessage message, out FrontendInputEvent input)
    {
        input = default;

        if (message == null || string.IsNullOrWhiteSpace(message.Type))
        {
            return false;
        }

        int x = ClampCoordinate(message.X);
        int y = ClampCoordinate(message.Y);
        SDL.SDL_Keymod modifiers = GetModifiers(message);

        switch (message.Type)
        {
            case "pointerMove":
                input = new FrontendInputEvent(
                    FrontendInputKind.PointerMove,
                    x,
                    y,
                    FrameId: message.FrameId
                );
                return true;
            case "pointerDown":
            case "pointerUp":
                if (!TryGetMouseButton(message.Button, out MouseButtonType button))
                {
                    return false;
                }

                input = new FrontendInputEvent(
                    message.Type == "pointerDown"
                        ? FrontendInputKind.PointerDown
                        : FrontendInputKind.PointerUp,
                    x,
                    y,
                    button,
                    FrameId: message.FrameId
                );
                return true;
            case "wheel":
                if (!double.IsFinite(message.DeltaY) || message.DeltaY == 0)
                {
                    return false;
                }

                input = new FrontendInputEvent(
                    FrontendInputKind.Wheel,
                    x,
                    y,
                    WheelY: message.DeltaY < 0 ? 1 : -1,
                    FrameId: message.FrameId
                );
                return true;
            case "keyDown":
            case "keyUp":
                if (!FrontendKeyTranslator.TryTranslate(message.Code, out uint key))
                {
                    return false;
                }

                input = new FrontendInputEvent(
                    message.Type == "keyDown" ? FrontendInputKind.KeyDown : FrontendInputKind.KeyUp,
                    Key: key,
                    Modifiers: modifiers,
                    FrameId: message.FrameId
                );
                return true;
            case "text":
                if (string.IsNullOrEmpty(message.Text) || message.Text.Length > 64)
                {
                    return false;
                }

                input = new FrontendInputEvent(
                    FrontendInputKind.Text,
                    Text: message.Text,
                    FrameId: message.FrameId
                );
                return true;
            case "resize":
                if (message.Width is < 640 or > 4096 || message.Height is < 480 or > 4096)
                {
                    return false;
                }

                input = new FrontendInputEvent(
                    FrontendInputKind.Resize,
                    Width: message.Width,
                    Height: message.Height,
                    FrameId: message.FrameId
                );
                return true;
            default:
                return false;
        }
    }

    private static int ClampCoordinate(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(value), 0, 32767);
    }

    private static bool TryGetMouseButton(int browserButton, out MouseButtonType button)
    {
        button = browserButton switch
        {
            0 => MouseButtonType.Left,
            1 => MouseButtonType.Middle,
            2 => MouseButtonType.Right,
            3 => MouseButtonType.XButton1,
            4 => MouseButtonType.XButton2,
            _ => MouseButtonType.None
        };

        return button != MouseButtonType.None;
    }

    private static SDL.SDL_Keymod GetModifiers(FrontendInputMessage message)
    {
        SDL.SDL_Keymod modifiers = SDL.SDL_Keymod.SDL_KMOD_NONE;

        if (message.AltKey)
        {
            modifiers |= SDL.SDL_Keymod.SDL_KMOD_ALT;
        }

        if (message.CtrlKey)
        {
            modifiers |= SDL.SDL_Keymod.SDL_KMOD_CTRL;
        }

        if (message.ShiftKey)
        {
            modifiers |= SDL.SDL_Keymod.SDL_KMOD_SHIFT;
        }

        if (message.MetaKey)
        {
            modifiers |= SDL.SDL_Keymod.SDL_KMOD_GUI;
        }

        return modifiers;
    }
}

internal static class FrontendKeyTranslator
{
    private static readonly Dictionary<string, SDL.SDL_Keycode> _specialKeys = new(
        StringComparer.Ordinal
    )
    {
        ["Enter"] = SDL.SDL_Keycode.SDLK_RETURN,
        ["NumpadEnter"] = SDL.SDL_Keycode.SDLK_KP_ENTER,
        ["Escape"] = SDL.SDL_Keycode.SDLK_ESCAPE,
        ["Backspace"] = SDL.SDL_Keycode.SDLK_BACKSPACE,
        ["Tab"] = SDL.SDL_Keycode.SDLK_TAB,
        ["Space"] = SDL.SDL_Keycode.SDLK_SPACE,
        ["Insert"] = SDL.SDL_Keycode.SDLK_INSERT,
        ["Delete"] = SDL.SDL_Keycode.SDLK_DELETE,
        ["Home"] = SDL.SDL_Keycode.SDLK_HOME,
        ["End"] = SDL.SDL_Keycode.SDLK_END,
        ["PageUp"] = SDL.SDL_Keycode.SDLK_PAGEUP,
        ["PageDown"] = SDL.SDL_Keycode.SDLK_PAGEDOWN,
        ["ArrowLeft"] = SDL.SDL_Keycode.SDLK_LEFT,
        ["ArrowRight"] = SDL.SDL_Keycode.SDLK_RIGHT,
        ["ArrowUp"] = SDL.SDL_Keycode.SDLK_UP,
        ["ArrowDown"] = SDL.SDL_Keycode.SDLK_DOWN,
        ["ShiftLeft"] = SDL.SDL_Keycode.SDLK_LSHIFT,
        ["ShiftRight"] = SDL.SDL_Keycode.SDLK_RSHIFT,
        ["ControlLeft"] = SDL.SDL_Keycode.SDLK_LCTRL,
        ["ControlRight"] = SDL.SDL_Keycode.SDLK_RCTRL,
        ["AltLeft"] = SDL.SDL_Keycode.SDLK_LALT,
        ["AltRight"] = SDL.SDL_Keycode.SDLK_RALT,
        ["MetaLeft"] = SDL.SDL_Keycode.SDLK_LGUI,
        ["MetaRight"] = SDL.SDL_Keycode.SDLK_RGUI,
        ["CapsLock"] = SDL.SDL_Keycode.SDLK_CAPSLOCK,
        ["PrintScreen"] = SDL.SDL_Keycode.SDLK_PRINTSCREEN,
        ["ScrollLock"] = SDL.SDL_Keycode.SDLK_SCROLLLOCK,
        ["Pause"] = SDL.SDL_Keycode.SDLK_PAUSE,
        ["Backquote"] = SDL.SDL_Keycode.SDLK_GRAVE,
        ["Minus"] = SDL.SDL_Keycode.SDLK_MINUS,
        ["Equal"] = SDL.SDL_Keycode.SDLK_EQUALS,
        ["BracketLeft"] = SDL.SDL_Keycode.SDLK_LEFTBRACKET,
        ["BracketRight"] = SDL.SDL_Keycode.SDLK_RIGHTBRACKET,
        ["Backslash"] = SDL.SDL_Keycode.SDLK_BACKSLASH,
        ["Semicolon"] = SDL.SDL_Keycode.SDLK_SEMICOLON,
        ["Quote"] = SDL.SDL_Keycode.SDLK_APOSTROPHE,
        ["Comma"] = SDL.SDL_Keycode.SDLK_COMMA,
        ["Period"] = SDL.SDL_Keycode.SDLK_PERIOD,
        ["Slash"] = SDL.SDL_Keycode.SDLK_SLASH
    };

    public static bool TryTranslate(string code, out uint key)
    {
        key = 0;

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
        {
            char letter = code[3];

            if (letter is >= 'A' and <= 'Z')
            {
                key = char.ToLowerInvariant(letter);
                return true;
            }
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
        {
            char digit = code[5];

            if (digit is >= '0' and <= '9')
            {
                key = digit;
                return true;
            }
        }

        if (code.Length is 2 or 3
            && code[0] == 'F'
            && int.TryParse(code.AsSpan(1), out int functionKey)
            && functionKey is >= 1 and <= 24
            && Enum.TryParse($"SDLK_F{functionKey}", out SDL.SDL_Keycode parsedFunctionKey))
        {
            key = (uint)parsedFunctionKey;
            return true;
        }

        if (_specialKeys.TryGetValue(code, out SDL.SDL_Keycode specialKey))
        {
            key = (uint)specialKey;
            return true;
        }

        return false;
    }
}
