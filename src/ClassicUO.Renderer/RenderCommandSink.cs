using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer;

public enum RenderCommandKind : byte
{
    SetRenderTarget = 1,
    Clear = 2,
    DrawBatch = 3
}

[Flags]
public enum RenderClearFlags : byte
{
    None = 0,
    Target = 1,
    Depth = 2,
    Stencil = 4
}

public readonly record struct RenderClearCommand(
    RenderClearFlags Flags,
    Color Color,
    float Depth,
    int Stencil
);

public readonly record struct RenderBatchState(
    Viewport Viewport,
    Rectangle Scissor,
    bool ScissorEnabled,
    Blend ColorSourceBlend,
    Blend ColorDestinationBlend,
    BlendFunction ColorBlendFunction,
    Blend AlphaSourceBlend,
    Blend AlphaDestinationBlend,
    BlendFunction AlphaBlendFunction,
    Color BlendFactor,
    bool DepthEnabled,
    bool DepthWriteEnabled,
    CompareFunction DepthFunction,
    TextureFilter TextureFilter,
    Matrix Transform,
    float ProjectionNear,
    float ProjectionFar,
    float Brightlight,
    bool HasCustomEffect
);

public interface IRenderCommandSink
{
    void BeginFrame(int width, int height);
    void SetRenderTarget(Texture2D target);
    void Clear(in RenderClearCommand command);
    void DrawBatch(
        Texture2D texture,
        ReadOnlySpan<RenderSprite> sprites,
        in RenderBatchState state
    );
    void EndFrame();
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RenderSprite : IVertexType
{
    public Vector3 Position0;
    public Vector3 Normal0;
    public Vector3 TextureCoordinate0;
    public Vector3 Hue0;

    public Vector3 Position1;
    public Vector3 Normal1;
    public Vector3 TextureCoordinate1;
    public Vector3 Hue1;

    public Vector3 Position2;
    public Vector3 Normal2;
    public Vector3 TextureCoordinate2;
    public Vector3 Hue2;

    public Vector3 Position3;
    public Vector3 Normal3;
    public Vector3 TextureCoordinate3;
    public Vector3 Hue3;

    VertexDeclaration IVertexType.VertexDeclaration => Declaration;

    private static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(sizeof(float) * 3, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(sizeof(float) * 6, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(sizeof(float) * 9, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 1)
    );

    public const int SizeInBytes = sizeof(float) * 12 * 4;
}
