using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Frontend;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Frontend;

public sealed class FrontendWebSocketServerTests
{
    [Fact]
    public void ReleasesFrameWhenNoViewerIsAttached()
    {
        using var server = new FrontendWebSocketServer(0, _ => { });
        server.Start();
        int releases = 0;
        FrontendWireFrame frame = FrontendFrameProtocol.CreateRawRgbaFrame(
            1,
            2,
            2,
            2,
            new byte[32],
            payloadLength: 16,
            releasePayload: _ => releases++
        );

        FrontendPublishResult result = server.Publish(frame);

        result.Enqueued.Should().BeFalse();
        releases.Should().Be(1);
    }

    [Fact]
    public async Task ServesViewerAndExchangesLatestFrameAndInput()
    {
        var inputReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var server = new FrontendWebSocketServer(0, json => inputReceived.TrySetResult(json));
        server.Start();

        using var http = new HttpClient();
        string page = await http.GetStringAsync(server.ViewerUrl);
        page.Should().Contain("TazUO remote frontend");
        page.Should().Contain("/ws");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/ws"),
            CancellationToken.None
        );
        await WaitUntilAsync(() => server.IsConnected);

        byte[] payload = { 1, 2, 3, 4 };
        FrontendWireFrame wireFrame = FrontendFrameProtocol.CreatePngFrame(
            17,
            99,
            640,
            480,
            payload
        );
        FrontendPublishResult published = server.Publish(wireFrame);
        published.Enqueued.Should().BeTrue();

        byte[] received = await ReceiveMessageAsync(socket);
        received.Should().HaveCount(wireFrame.Length);
        FrontendFrameProtocol.TryReadHeader(received, out FrontendFrameHeader header)
            .Should()
            .BeTrue();
        header.FrameId.Should().Be(17);
        received.AsSpan(FrontendFrameProtocol.HeaderSize).ToArray().Should().Equal(payload);

        const string input = "{\"type\":\"pointerMove\",\"x\":1,\"y\":2}";
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(input),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );
        (await inputReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(input);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<byte[]> ReceiveMessageAsync(ClientWebSocket socket)
    {
        byte[] buffer = new byte[4096];
        using var output = new MemoryStream();

        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer.AsMemory(),
                CancellationToken.None
            );
            result.MessageType.Should().Be(WebSocketMessageType.Binary);
            output.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return output.ToArray();
            }
        }
    }
}
