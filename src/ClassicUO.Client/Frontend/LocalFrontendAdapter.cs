namespace ClassicUO.Frontend;

internal sealed class LocalFrontendAdapter : IFrontendAdapter
{
    public FrontendMode Mode => FrontendMode.Local;
    public bool IsAttached => true;
    public bool OwnsPointer => false;
    public bool ReduceUpdatesWhenNativeWindowInactive => true;

    public void Initialize(GameController game)
    {
    }

    public bool WantsFrame(uint timestamp) => true;

    public void DrainInput(System.Action<FrontendInputEvent> dispatch)
    {
    }

    public FrontendPresentResult Present(in FrontendFrame frame) => default;

    public void Dispose()
    {
    }
}
