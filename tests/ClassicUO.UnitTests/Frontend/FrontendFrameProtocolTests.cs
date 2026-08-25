using System;
using System.IO;
using System.IO.Compression;
using ClassicUO.Frontend;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Frontend;

public sealed class FrontendFrameProtocolTests
{
    [Fact]
    public void RoundTripsPngFrameHeader()
    {
        byte[] png = { 137, 80, 78, 71, 1, 2, 3 };
        FrontendWireFrame frame = FrontendFrameProtocol.CreatePngFrame(
            42,
            1234,
            800,
            600,
            png
        );

        FrontendFrameProtocol.TryReadHeader(frame.Header, out FrontendFrameHeader header)
            .Should()
            .BeTrue();
        header.Version.Should().Be(FrontendFrameProtocol.CurrentVersion);
        header.MessageType.Should().Be(FrontendFrameProtocol.PngFrameMessageType);
        header.FrameId.Should().Be(42);
        header.Timestamp.Should().Be(1234);
        header.Width.Should().Be(800);
        header.Height.Should().Be(600);
        header.PayloadLength.Should().Be(png.Length);
        frame.Payload.Should().BeSameAs(png);
        frame.Length.Should().Be(FrontendFrameProtocol.HeaderSize + png.Length);
    }

    [Fact]
    public void RoundTripsRawRgbaFrameHeader()
    {
        byte[] rgba = new byte[2 * 3 * 4];
        FrontendWireFrame frame = FrontendFrameProtocol.CreateRawRgbaFrame(
            7,
            88,
            2,
            3,
            rgba
        );

        FrontendFrameProtocol.TryReadHeader(frame.Header, out FrontendFrameHeader header)
            .Should()
            .BeTrue();
        header.MessageType.Should().Be(FrontendFrameProtocol.RawRgbaFrameMessageType);
        header.FrameId.Should().Be(7);
        header.PayloadLength.Should().Be(rgba.Length);
        frame.Payload.Should().BeSameAs(rgba);
    }

    [Fact]
    public void RejectsRawRgbaPayloadWithWrongLength()
    {
        FluentActions.Invoking(
                () => FrontendFrameProtocol.CreateRawRgbaFrame(1, 2, 3, 4, new byte[12])
            )
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void ReleasesPooledRawPayloadExactlyOnce()
    {
        byte[] pooled = new byte[128];
        int releases = 0;
        FrontendWireFrame frame = FrontendFrameProtocol.CreateRawRgbaFrame(
            1,
            2,
            4,
            4,
            pooled,
            payloadLength: 64,
            releasePayload: _ => releases++
        );

        frame.Payload.Should().BeSameAs(pooled);
        frame.PayloadLength.Should().Be(64);
        frame.Length.Should().Be(FrontendFrameProtocol.HeaderSize + 64);

        frame.Dispose();
        frame.Dispose();

        releases.Should().Be(1);
    }

    [Fact]
    public void RejectsUnknownOrTruncatedHeaders()
    {
        FrontendFrameProtocol.TryReadHeader(new byte[4], out _).Should().BeFalse();

        FrontendWireFrame frame = FrontendFrameProtocol.CreatePngFrame(1, 2, 3, 4, new byte[1]);
        frame.Header[0] = (byte)'X';
        FrontendFrameProtocol.TryReadHeader(frame.Header, out _).Should().BeFalse();

        frame = FrontendFrameProtocol.CreatePngFrame(1, 2, 3, 4, new byte[1]);
        frame.Header[5] = 99;
        FrontendFrameProtocol.TryReadHeader(frame.Header, out _).Should().BeFalse();
    }

    [Fact]
    public void CompressesAndRoundTripsDisplayListPayload()
    {
        byte[] displayList = new byte[16 * 1024];
        Array.Fill(displayList, (byte)0x5a);

        using FrontendWireFrame frame = FrontendFrameProtocol.CreateDisplayListFrame(
            99,
            456,
            1512,
            916,
            displayList
        );

        FrontendFrameProtocol.TryReadHeader(frame.Header, out FrontendFrameHeader header)
            .Should()
            .BeTrue();
        header.MessageType.Should().Be(FrontendFrameProtocol.DisplayListFrameMessageType);
        header.Flags.Should().Be(FrontendFrameProtocol.CompressedPayloadFlag);
        frame.PayloadLength.Should().BeLessThan(displayList.Length);

        using var compressed = new MemoryStream(frame.Payload, 0, frame.PayloadLength);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        inflater.CopyTo(inflated);
        inflated.ToArray().Should().Equal(displayList);
    }

    [Fact]
    public void RejectsCompressionFlagOnFramebufferMessages()
    {
        using FrontendWireFrame frame = FrontendFrameProtocol.CreatePngFrame(
            1,
            2,
            3,
            4,
            new byte[1]
        );
        frame.Header[6] = FrontendFrameProtocol.CompressedPayloadFlag;

        FrontendFrameProtocol.TryReadHeader(frame.Header, out _).Should().BeFalse();
    }
}
