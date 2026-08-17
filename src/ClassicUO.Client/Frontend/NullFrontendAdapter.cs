using SDL3;

namespace ClassicUO.Frontend;

internal sealed class NullFrontendAdapter : IFrontendAdapter
{
    private readonly bool _hideNativeWindow;

    public NullFrontendAdapter(bool hideNativeWindow)
    {
        _hideNativeWindow = hideNativeWindow;
    }

    public FrontendMode Mode => FrontendMode.Null;
    public bool IsAttached => false;
    public bool OwnsPointer => false;
    public bool ReduceUpdatesWhenNativeWindowInactive => false;

    public void Initialize(GameController game)
    {
        if (_hideNativeWindow)
        {
            SDL.SDL_HideWindow(game.Window.Handle);
        }
    }

    public bool WantsFrame(uint timestamp) => false;

    public void DrainInput(System.Action<FrontendInputEvent> dispatch)
    {
    }

    public FrontendPresentResult Present(in FrontendFrame frame) => default;

    public void Dispose()
    {
    }
}
