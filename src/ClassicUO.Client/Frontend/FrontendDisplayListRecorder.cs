using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Frontend;

internal readonly record struct FrontendDisplayListMetrics(
    int PayloadBytes,
    int ResourceBytes,
    int CommandBytes,
    int ResourceRecords,
    int Commands
);

internal sealed class FrontendDisplayListRecorder : IRenderCommandSink, IDisposable
{
    private const int DisplayListVersion = 1;
    private const int PayloadHeaderSize = 32;
    private const int TextureTileSize = 128;
    private const int RefreshTileInterval = 30;
    private const int DisposedTextureSweepInterval = 300;
    private const byte TextureCreateResource = 1;
    private const byte TexturePatchResource = 2;
    private const byte TextureDeleteResource = 3;
    private const byte RenderTargetResourceFlag = 1;
    private const byte DepthResourceFlag = 2;
    private const byte StencilResourceFlag = 4;

    private readonly Dictionary<Texture2D, TextureResource> _textures = new(
        TextureReferenceComparer.Instance
    );
    private readonly List<ResourceRecord> _resources = new();
    private readonly Dictionary<ResourceRecordKey, ResourceRecord> _latestResources = new();
    private readonly MemoryStream _commands = new();
    private readonly BinaryWriter _commandWriter;
    private GraphicsDevice _graphicsDevice;
    private byte[] _completedCommands = Array.Empty<byte>();
    private int _frameWidth;
    private int _frameHeight;
    private int _frameNumber;
    private int _commandCount;
    private int _completedCommandCount;
    private int _nextTextureId;
    private int _nextResourceSequence;
    private int _acknowledgedResourceSequence;
    private int _lastSentResourceSequence;
    private int _hueTextureId;
    private int _lightTextureId;
    private bool _recording;
    private bool _disposed;

    public FrontendDisplayListRecorder()
    {
        _commandWriter = new BinaryWriter(_commands);
    }

    public int CompletedCommandCount => _completedCommandCount;
    public bool IsAwaitingResourceAcknowledgement =>
        Volatile.Read(ref _acknowledgedResourceSequence)
        < Volatile.Read(ref _lastSentResourceSequence);

    public void Initialize(GraphicsDevice graphicsDevice) =>
        _graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));

    public void BeginFrame(int width, int height)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FrontendDisplayListRecorder));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        _frameWidth = width;
        _frameHeight = height;
        _frameNumber++;

        if (_frameNumber % DisposedTextureSweepInterval == 0)
        {
            RemoveDisposedTextures();
        }

        _commandCount = 0;
        _commands.SetLength(0);
        _commands.Position = 0;
        _recording = true;
    }

    public void SetRenderTarget(Texture2D target)
    {
        if (!_recording)
        {
            return;
        }

        int targetId = target == null ? 0 : RegisterTexture(target).Id;
        WriteCommandHeader(RenderCommandKind.SetRenderTarget, 0, sizeof(int));
        _commandWriter.Write(targetId);
        _commandCount++;
    }

    public void Clear(in RenderClearCommand command)
    {
        if (!_recording)
        {
            return;
        }

        WriteCommandHeader(RenderCommandKind.Clear, (byte)command.Flags, 12);
        _commandWriter.Write(command.Color.R);
        _commandWriter.Write(command.Color.G);
        _commandWriter.Write(command.Color.B);
        _commandWriter.Write(command.Color.A);
        _commandWriter.Write(command.Depth);
        _commandWriter.Write(command.Stencil);
        _commandCount++;
    }

    public void DrawBatch(
        Texture2D texture,
        ReadOnlySpan<RenderSprite> sprites,
        in RenderBatchState state
    )
    {
        if (!_recording || texture == null || texture.IsDisposed || sprites.IsEmpty)
        {
            return;
        }

        TextureResource resource = RegisterTexture(texture);
        MarkTextureUsage(resource, sprites);

        byte flags = 0;

        if (state.ScissorEnabled)
        {
            flags |= 1;
        }

        if (state.DepthEnabled)
        {
            flags |= 2;
        }

        if (state.DepthWriteEnabled)
        {
            flags |= 4;
        }

        if (state.HasCustomEffect)
        {
            flags |= 8;
        }

        int payloadLength = checked(56 + sprites.Length * 4 * 11 * sizeof(float));
        WriteCommandHeader(RenderCommandKind.DrawBatch, flags, payloadLength);
        _commandWriter.Write(resource.Id);
        _commandWriter.Write(sprites.Length);
        WriteRectangle(state.Viewport.Bounds);
        WriteRectangle(state.Scissor);
        _commandWriter.Write((byte)state.ColorSourceBlend);
        _commandWriter.Write((byte)state.ColorDestinationBlend);
        _commandWriter.Write((byte)state.ColorBlendFunction);
        _commandWriter.Write((byte)state.AlphaSourceBlend);
        _commandWriter.Write((byte)state.AlphaDestinationBlend);
        _commandWriter.Write((byte)state.AlphaBlendFunction);
        _commandWriter.Write((byte)state.DepthFunction);
        _commandWriter.Write((byte)state.TextureFilter);
        _commandWriter.Write(state.BlendFactor.R);
        _commandWriter.Write(state.BlendFactor.G);
        _commandWriter.Write(state.BlendFactor.B);
        _commandWriter.Write(state.BlendFactor.A);
        _commandWriter.Write(state.Brightlight);

        var projection = Matrix.CreateOrthographicOffCenter(
            0f,
            state.Viewport.Width,
            state.Viewport.Height,
            0f,
            state.ProjectionNear,
            state.ProjectionFar
        );
        Matrix transform = state.Transform * projection;
        float halfPixelX = 0.5f / state.Viewport.Width;
        float halfPixelY = 0.5f / state.Viewport.Height;

        for (int i = 0; i < sprites.Length; i++)
        {
            ref readonly RenderSprite sprite = ref sprites[i];
            WriteVertex(
                sprite.Position0,
                sprite.Normal0,
                sprite.TextureCoordinate0,
                sprite.Hue0,
                transform,
                halfPixelX,
                halfPixelY
            );
            WriteVertex(
                sprite.Position1,
                sprite.Normal1,
                sprite.TextureCoordinate1,
                sprite.Hue1,
                transform,
                halfPixelX,
                halfPixelY
            );
            WriteVertex(
                sprite.Position2,
                sprite.Normal2,
                sprite.TextureCoordinate2,
                sprite.Hue2,
                transform,
                halfPixelX,
                halfPixelY
            );
            WriteVertex(
                sprite.Position3,
                sprite.Normal3,
                sprite.TextureCoordinate3,
                sprite.Hue3,
                transform,
                halfPixelX,
                halfPixelY
            );
        }

        _commandCount++;
    }

    public void EndFrame()
    {
        if (!_recording)
        {
            return;
        }

        _commandWriter.Flush();
        _completedCommands = _commands.ToArray();
        _completedCommandCount = _commandCount;
        _recording = false;
    }

    public FrontendWireFrame CreateWireFrame(
        long frameId,
        uint timestamp,
        out FrontendDisplayListMetrics metrics
    )
    {
        if (_graphicsDevice == null)
        {
            throw new InvalidOperationException("The display-list recorder has not been initialized.");
        }

        EnsureShaderTextures();
        CapturePendingTextureTiles();

        int acknowledged = Volatile.Read(ref _acknowledgedResourceSequence);
        int resourceCount = 0;
        int resourceBytes = 0;
        PruneAcknowledgedDeletes(acknowledged);

        for (int i = 0; i < _resources.Count; i++)
        {
            if (_resources[i].Sequence > acknowledged)
            {
                resourceCount++;
                resourceBytes += _resources[i].Bytes.Length;
            }
        }

        using var payload = new MemoryStream(
            checked(PayloadHeaderSize + resourceBytes + _completedCommands.Length)
        );
        using var writer = new BinaryWriter(payload);
        writer.Write("DLST"u8);
        writer.Write((ushort)DisplayListVersion);
        writer.Write((ushort)0);
        writer.Write(_nextResourceSequence);
        writer.Write(resourceCount);
        writer.Write(_completedCommandCount);
        writer.Write(_hueTextureId);
        writer.Write(_lightTextureId);
        writer.Write(_completedCommands.Length);

        for (int i = 0; i < _resources.Count; i++)
        {
            if (_resources[i].Sequence > acknowledged)
            {
                writer.Write(_resources[i].Bytes);
            }
        }

        writer.Write(_completedCommands);

        byte[] payloadBytes = payload.ToArray();
        metrics = new FrontendDisplayListMetrics(
            payloadBytes.Length,
            resourceBytes,
            _completedCommands.Length,
            resourceCount,
            _completedCommandCount
        );

        if (resourceCount > 0)
        {
            Volatile.Write(ref _lastSentResourceSequence, _nextResourceSequence);
        }

        return FrontendFrameProtocol.CreateDisplayListFrame(
            frameId,
            timestamp,
            _frameWidth,
            _frameHeight,
            payloadBytes
        );
    }

    public void AcknowledgeResources(int sequence)
    {
        if (sequence <= 0)
        {
            return;
        }

        int current;

        do
        {
            current = Volatile.Read(ref _acknowledgedResourceSequence);

            if (sequence <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(
                   ref _acknowledgedResourceSequence,
                   sequence,
                   current
               ) != current);
    }

    public void ResetResourceAcknowledgement()
    {
        Interlocked.Exchange(ref _acknowledgedResourceSequence, 0);
        Interlocked.Exchange(ref _lastSentResourceSequence, 0);
    }

    private void EnsureShaderTextures()
    {
        if (_hueTextureId == 0 && _graphicsDevice.Textures[1] is Texture2D hue)
        {
            TextureResource resource = RegisterTexture(hue);
            _hueTextureId = resource.Id;
            MarkWholeTexture(resource);
        }

        if (_lightTextureId == 0 && _graphicsDevice.Textures[2] is Texture2D light)
        {
            TextureResource resource = RegisterTexture(light);
            _lightTextureId = resource.Id;
            MarkWholeTexture(resource);
        }
    }

    private TextureResource RegisterTexture(Texture2D texture)
    {
        if (_textures.TryGetValue(texture, out TextureResource resource))
        {
            return resource;
        }

        var renderTarget = texture as RenderTarget2D;
        resource = new TextureResource(
            ++_nextTextureId,
            texture,
            renderTarget != null,
            Math.Max(1, (texture.Width + TextureTileSize - 1) / TextureTileSize)
        );
        _textures.Add(texture, resource);

        byte flags = 0;

        if (renderTarget != null)
        {
            flags |= RenderTargetResourceFlag;

            if (renderTarget.DepthStencilFormat != DepthFormat.None)
            {
                flags |= DepthResourceFlag;
            }

            if (renderTarget.DepthStencilFormat == DepthFormat.Depth24Stencil8)
            {
                flags |= StencilResourceFlag;
            }
        }

        AddResource(
            TextureCreateResource,
            flags,
            resource.Id,
            segment: -1,
            writer =>
            {
                writer.Write(texture.Width);
                writer.Write(texture.Height);
                writer.Write((int)texture.Format);
            }
        );

        return resource;
    }

    private void MarkTextureUsage(
        TextureResource resource,
        ReadOnlySpan<RenderSprite> sprites
    )
    {
        if (resource.IsRenderTarget)
        {
            return;
        }

        bool refresh = _frameNumber % RefreshTileInterval == 0;

        for (int i = 0; i < sprites.Length; i++)
        {
            ref readonly RenderSprite sprite = ref sprites[i];
            float minU = MathF.Min(
                MathF.Min(sprite.TextureCoordinate0.X, sprite.TextureCoordinate1.X),
                MathF.Min(sprite.TextureCoordinate2.X, sprite.TextureCoordinate3.X)
            );
            float maxU = MathF.Max(
                MathF.Max(sprite.TextureCoordinate0.X, sprite.TextureCoordinate1.X),
                MathF.Max(sprite.TextureCoordinate2.X, sprite.TextureCoordinate3.X)
            );
            float minV = MathF.Min(
                MathF.Min(sprite.TextureCoordinate0.Y, sprite.TextureCoordinate1.Y),
                MathF.Min(sprite.TextureCoordinate2.Y, sprite.TextureCoordinate3.Y)
            );
            float maxV = MathF.Max(
                MathF.Max(sprite.TextureCoordinate0.Y, sprite.TextureCoordinate1.Y),
                MathF.Max(sprite.TextureCoordinate2.Y, sprite.TextureCoordinate3.Y)
            );

            if (!float.IsFinite(minU)
                || !float.IsFinite(maxU)
                || !float.IsFinite(minV)
                || !float.IsFinite(maxV))
            {
                continue;
            }

            int left = Math.Clamp((int)MathF.Floor(minU * resource.Texture.Width) - 1, 0, resource.Texture.Width - 1);
            int top = Math.Clamp((int)MathF.Floor(minV * resource.Texture.Height) - 1, 0, resource.Texture.Height - 1);
            int right = Math.Clamp((int)MathF.Ceiling(maxU * resource.Texture.Width) + 1, left + 1, resource.Texture.Width);
            int bottom = Math.Clamp((int)MathF.Ceiling(maxV * resource.Texture.Height) + 1, top + 1, resource.Texture.Height);
            MarkTiles(resource, left, top, right, bottom, refresh);
        }
    }

    private static void MarkTiles(
        TextureResource resource,
        int left,
        int top,
        int right,
        int bottom,
        bool refresh
    )
    {
        int firstTileX = left / TextureTileSize;
        int lastTileX = (right - 1) / TextureTileSize;
        int firstTileY = top / TextureTileSize;
        int lastTileY = (bottom - 1) / TextureTileSize;

        for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                int tile = tileY * resource.TilesWide + tileX;

                if (refresh || !resource.TileHashes.ContainsKey(tile))
                {
                    resource.PendingTiles.Add(tile);
                }
            }
        }
    }

    private static void MarkWholeTexture(TextureResource resource) =>
        MarkTiles(
            resource,
            0,
            0,
            resource.Texture.Width,
            resource.Texture.Height,
            refresh: false
        );

    private void CapturePendingTextureTiles()
    {
        foreach (TextureResource resource in _textures.Values)
        {
            if (resource.IsRenderTarget || resource.PendingTiles.Count == 0)
            {
                continue;
            }

            int[] tiles = new int[resource.PendingTiles.Count];
            resource.PendingTiles.CopyTo(tiles);
            resource.PendingTiles.Clear();

            for (int i = 0; i < tiles.Length; i++)
            {
                int tile = tiles[i];
                int tileX = tile % resource.TilesWide;
                int tileY = tile / resource.TilesWide;
                var bounds = new Rectangle(
                    tileX * TextureTileSize,
                    tileY * TextureTileSize,
                    Math.Min(TextureTileSize, resource.Texture.Width - tileX * TextureTileSize),
                    Math.Min(TextureTileSize, resource.Texture.Height - tileY * TextureTileSize)
                );
                byte[] rgba = new byte[checked(bounds.Width * bounds.Height * 4)];

                try
                {
                    resource.Texture.GetData(0, bounds, rgba, 0, rgba.Length);
                }
                catch (Exception ex)
                {
                    resource.PendingTiles.Add(tile);

                    if (!resource.ReadFailureReported)
                    {
                        resource.ReadFailureReported = true;
                        Log.Warn(
                            $"Unable to capture remote-renderer texture {resource.Id} " +
                            $"({resource.Texture.Width}x{resource.Texture.Height}, {resource.Texture.Format}): {ex.Message}"
                        );
                    }

                    continue;
                }

                if (resource.Texture.Format == SurfaceFormat.ColorBgraEXT)
                {
                    for (int p = 0; p < rgba.Length; p += 4)
                    {
                        (rgba[p], rgba[p + 2]) = (rgba[p + 2], rgba[p]);
                    }
                }

                uint hash = Hash(rgba);

                if (resource.TileHashes.TryGetValue(tile, out uint previous) && previous == hash)
                {
                    continue;
                }

                resource.TileHashes[tile] = hash;
                AddResource(
                    TexturePatchResource,
                    0,
                    resource.Id,
                    segment: tile,
                    writer =>
                    {
                        writer.Write(bounds.X);
                        writer.Write(bounds.Y);
                        writer.Write(bounds.Width);
                        writer.Write(bounds.Height);
                        writer.Write(rgba.Length);
                        writer.Write(rgba);
                    }
                );
            }
        }
    }

    private void AddResource(
        byte kind,
        byte flags,
        int textureId,
        int segment,
        Action<BinaryWriter> writePayload
    )
    {
        int sequence = ++_nextResourceSequence;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(kind);
        writer.Write(flags);
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write(sequence);
        writer.Write(textureId);
        long payloadStart = stream.Position;
        writePayload(writer);
        long end = stream.Position;
        stream.Position = 4;
        writer.Write(checked((int)(end - payloadStart)));
        stream.Position = end;
        var key = new ResourceRecordKey(kind, textureId, segment);

        if (_latestResources.Remove(key, out ResourceRecord previous))
        {
            _resources.Remove(previous);
        }

        var record = new ResourceRecord(sequence, kind, textureId, key, stream.ToArray());
        _resources.Add(record);
        _latestResources.Add(key, record);
    }

    private void RemoveDisposedTextures()
    {
        List<Texture2D> disposed = null;

        foreach (Texture2D texture in _textures.Keys)
        {
            if (texture.IsDisposed)
            {
                (disposed ??= new List<Texture2D>()).Add(texture);
            }
        }

        if (disposed == null)
        {
            return;
        }

        for (int i = 0; i < disposed.Count; i++)
        {
            TextureResource resource = _textures[disposed[i]];
            _textures.Remove(disposed[i]);

            if (_hueTextureId == resource.Id)
            {
                _hueTextureId = 0;
            }

            if (_lightTextureId == resource.Id)
            {
                _lightTextureId = 0;
            }

            for (int recordIndex = _resources.Count - 1; recordIndex >= 0; recordIndex--)
            {
                ResourceRecord record = _resources[recordIndex];

                if (record.TextureId == resource.Id)
                {
                    _resources.RemoveAt(recordIndex);
                    _latestResources.Remove(record.Key);
                }
            }

            AddResource(
                TextureDeleteResource,
                0,
                resource.Id,
                segment: -1,
                _ => { }
            );
        }
    }

    private void PruneAcknowledgedDeletes(int acknowledged)
    {
        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            ResourceRecord record = _resources[i];

            if (record.Kind == TextureDeleteResource && record.Sequence <= acknowledged)
            {
                _resources.RemoveAt(i);
                _latestResources.Remove(record.Key);
            }
        }
    }

    private void WriteCommandHeader(RenderCommandKind kind, byte flags, int payloadLength)
    {
        _commandWriter.Write((byte)kind);
        _commandWriter.Write(flags);
        _commandWriter.Write((ushort)0);
        _commandWriter.Write(payloadLength);
    }

    private void WriteRectangle(Rectangle rectangle)
    {
        _commandWriter.Write(rectangle.X);
        _commandWriter.Write(rectangle.Y);
        _commandWriter.Write(rectangle.Width);
        _commandWriter.Write(rectangle.Height);
    }

    private void WriteVertex(
        Vector3 position,
        Vector3 normal,
        Vector3 textureCoordinate,
        Vector3 hue,
        Matrix transform,
        float halfPixelX,
        float halfPixelY
    )
    {
        var clip = Vector3.Transform(position, transform);
        _commandWriter.Write(clip.X - halfPixelX);
        _commandWriter.Write(clip.Y + halfPixelY);
        _commandWriter.Write(clip.Z * 2f - 1f);
        _commandWriter.Write(normal.X);
        _commandWriter.Write(normal.Y);
        _commandWriter.Write(normal.Z);
        _commandWriter.Write(textureCoordinate.X);
        _commandWriter.Write(textureCoordinate.Y);
        _commandWriter.Write(hue.X);
        _commandWriter.Write(hue.Y);
        _commandWriter.Write(hue.Z);
    }

    private static uint Hash(ReadOnlySpan<byte> bytes)
    {
        uint hash = 2166136261;

        for (int i = 0; i < bytes.Length; i++)
        {
            hash = (hash ^ bytes[i]) * 16777619;
        }

        return hash;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commandWriter.Dispose();
        _commands.Dispose();
        _textures.Clear();
        _resources.Clear();
        _latestResources.Clear();
        _completedCommands = Array.Empty<byte>();
    }

    private sealed class TextureResource
    {
        public TextureResource(int id, Texture2D texture, bool isRenderTarget, int tilesWide)
        {
            Id = id;
            Texture = texture;
            IsRenderTarget = isRenderTarget;
            TilesWide = tilesWide;
        }

        public int Id { get; }
        public Texture2D Texture { get; }
        public bool IsRenderTarget { get; }
        public int TilesWide { get; }
        public Dictionary<int, uint> TileHashes { get; } = new();
        public HashSet<int> PendingTiles { get; } = new();
        public bool ReadFailureReported { get; set; }
    }

    private readonly record struct ResourceRecordKey(byte Kind, int TextureId, int Segment);

    private readonly record struct ResourceRecord(
        int Sequence,
        byte Kind,
        int TextureId,
        ResourceRecordKey Key,
        byte[] Bytes
    );

    private sealed class TextureReferenceComparer : IEqualityComparer<Texture2D>
    {
        public static TextureReferenceComparer Instance { get; } = new();

        public bool Equals(Texture2D x, Texture2D y) => ReferenceEquals(x, y);

        public int GetHashCode(Texture2D obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
