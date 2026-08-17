using System;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Frontend;

internal readonly record struct FrontendFrame(
    long FrameId,
    uint Timestamp,
    Texture2D Texture
);

internal readonly record struct FrontendPresentResult(
    int EncodedBytes,
    double CaptureMilliseconds,
    bool Enqueued,
    bool Dropped,
    int UncompressedBytes = 0,
    int DisplayListResourceBytes = 0,
    int DisplayListCommandBytes = 0,
    int DisplayListResourceRecords = 0,
    int DisplayListCommands = 0
);

internal readonly record struct FrontendResourcePolicy(
    bool EnableAudio,
    bool EnableVoiceRecognition,
    bool EnableNativeInput,
    bool EnableRenderLoop,
    bool RequiresComposedFramebuffer,
    bool PresentNativeFramebuffer
);

internal interface IFrontendAdapter : IDisposable
{
    FrontendMode Mode { get; }
    bool IsAttached { get; }
    bool OwnsPointer { get; }
    bool ReduceUpdatesWhenNativeWindowInactive { get; }
    FrontendResourcePolicy Resources { get; }
    IRenderCommandSink RenderCommandSink { get; }

    void Initialize(GameController game);
    bool WantsFrame(uint timestamp);
    void DrainInput(Action<FrontendInputEvent> dispatch);
    FrontendPresentResult Present(in FrontendFrame frame);
}
