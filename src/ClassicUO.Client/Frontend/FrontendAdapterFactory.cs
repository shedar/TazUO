namespace ClassicUO.Frontend;

internal static class FrontendAdapterFactory
{
    public static IFrontendAdapter Create(FrontendOptions options) => options.Mode switch
    {
        FrontendMode.Local => new LocalFrontendAdapter(),
        FrontendMode.Null => new NullFrontendAdapter(options.HideNativeWindow),
        FrontendMode.WebSocket => new WebSocketFrontendAdapter(options),
        _ => throw new System.ArgumentOutOfRangeException(nameof(options.Mode), options.Mode, null)
    };
}
