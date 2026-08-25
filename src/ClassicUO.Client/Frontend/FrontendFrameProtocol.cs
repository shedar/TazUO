using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ClassicUO.Frontend;

internal readonly record struct FrontendFrameHeader(
    byte Version,
    byte MessageType,
    byte Flags,
    long FrameId,
    uint Timestamp,
    int Width,
    int Height,
    int PayloadLength
);

internal sealed class FrontendWireFrame : IDisposable
{
    private readonly Action<byte[]> _releasePayload;
    private int _disposed;

    public FrontendWireFrame(
        byte[] header,
        byte[] payload,
        int payloadLength,
        Action<byte[]> releasePayload = null
    )
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));

        if (payloadLength < 0 || payloadLength > payload.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
        }

        PayloadLength = payloadLength;
        _releasePayload = releasePayload;
    }

    public byte[] Header { get; }
    public byte[] Payload { get; }
    public int PayloadLength { get; }
    public int Length => Header.Length + PayloadLength;

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _releasePayload?.Invoke(Payload);
        }
    }
}

internal static class FrontendFrameProtocol
{
    public const int HeaderSize = 32;
    public const byte CurrentVersion = 1;
    public const byte PngFrameMessageType = 1;
    public const byte RawRgbaFrameMessageType = 2;
    public const byte DisplayListFrameMessageType = 3;
    public const byte CompressedPayloadFlag = 1;

    private static ReadOnlySpan<byte> Magic => "TUOF"u8;

    public static FrontendWireFrame CreatePngFrame(
        long frameId,
        uint timestamp,
        int width,
        int height,
        byte[] png
    )
    {
        return CreateFrame(frameId, timestamp, width, height, PngFrameMessageType, png);
    }

    public static FrontendWireFrame CreateRawRgbaFrame(
        long frameId,
        uint timestamp,
        int width,
        int height,
        byte[] rgba,
        int payloadLength = -1,
        Action<byte[]> releasePayload = null
    )
    {
        ArgumentNullException.ThrowIfNull(rgba);

        long expectedLength = (long)width * height * 4;
        int actualLength = payloadLength < 0 ? rgba.Length : payloadLength;

        if (expectedLength > int.MaxValue
            || actualLength != expectedLength
            || actualLength > rgba.Length)
        {
            throw new ArgumentException(
                "Raw RGBA payload length must equal width times height times four.",
                nameof(rgba)
            );
        }

        return CreateFrame(
            frameId,
            timestamp,
            width,
            height,
            RawRgbaFrameMessageType,
            rgba,
            actualLength,
            releasePayload
        );
    }

    public static FrontendWireFrame CreateDisplayListFrame(
        long frameId,
        uint timestamp,
        int width,
        int height,
        byte[] displayList
    )
    {
        ArgumentNullException.ThrowIfNull(displayList);

        byte[] compressed = Compress(displayList);

        if (compressed.Length < displayList.Length)
        {
            return CreateFrame(
                frameId,
                timestamp,
                width,
                height,
                DisplayListFrameMessageType,
                compressed,
                flags: CompressedPayloadFlag
            );
        }

        return CreateFrame(
            frameId,
            timestamp,
            width,
            height,
            DisplayListFrameMessageType,
            displayList
        );
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();

        using (var compressor = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(payload);
        }

        return output.ToArray();
    }

    private static FrontendWireFrame CreateFrame(
        long frameId,
        uint timestamp,
        int width,
        int height,
        byte messageType,
        byte[] payload,
        int payloadLength = -1,
        Action<byte[]> releasePayload = null,
        byte flags = 0
    )
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        int actualLength = payloadLength < 0 ? payload.Length : payloadLength;
        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = CurrentVersion;
        header[5] = messageType;
        header[6] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), frameId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), actualLength);

        return new FrontendWireFrame(header, payload, actualLength, releasePayload);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> bytes, out FrontendFrameHeader header)
    {
        header = default;

        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual(Magic))
        {
            return false;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));

        byte messageType = bytes[5];
        byte flags = bytes[6];

        if (bytes[4] != CurrentVersion
            || messageType is not (
                PngFrameMessageType
                or RawRgbaFrameMessageType
                or DisplayListFrameMessageType
            )
            || bytes[7] != 0
            || (flags & ~CompressedPayloadFlag) != 0
            || (flags != 0 && messageType != DisplayListFrameMessageType)
            || payloadLength < 0
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        if (messageType == RawRgbaFrameMessageType
            && ((long)width * height * 4 > int.MaxValue || payloadLength != width * height * 4))
        {
            return false;
        }

        header = new FrontendFrameHeader(
            bytes[4],
            bytes[5],
            flags,
            BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4)),
            width,
            height,
            payloadLength
        );
        return true;
    }
}
