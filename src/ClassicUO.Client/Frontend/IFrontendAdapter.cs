using System;
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
    bool Dropped
);

internal interface IFrontendAdapter : IDisposable
{
    FrontendMode Mode { get; }
    bool IsAttached { get; }
    bool OwnsPointer { get; }

    void Initialize(GameController game);
    bool WantsFrame(uint timestamp);
    FrontendPresentResult Present(in FrontendFrame frame);
}
