namespace ClassicUO.Frontend;

internal sealed class LocalFrontendAdapter : IFrontendAdapter
{
    public FrontendMode Mode => FrontendMode.Local;
    public bool IsAttached => true;
    public bool OwnsPointer => false;

    public void Initialize(GameController game)
    {
    }

    public bool WantsFrame(uint timestamp) => true;

    public FrontendPresentResult Present(in FrontendFrame frame) => default;

    public void Dispose()
    {
    }
}
