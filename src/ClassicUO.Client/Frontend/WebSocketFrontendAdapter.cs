using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using ClassicUO.Utility.Logging;
using SDL3;

namespace ClassicUO.Frontend;

internal sealed class WebSocketFrontendAdapter : IFrontendAdapter
{
    private const int MaxQueuedInputEvents = 4096;
    private const int MaxInputEventsPerUpdate = 512;

    private readonly FrontendOptions _options;
    private readonly ConcurrentQueue<FrontendInputEvent> _input = new();
    private FrontendWebSocketServer _server;
    private int _queuedInputEvents;
    private uint _nextFrameAt;
    private bool _wasAttached;

    public WebSocketFrontendAdapter(FrontendOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public FrontendMode Mode => FrontendMode.WebSocket;
    public bool IsAttached => _server?.IsConnected == true;
    public bool OwnsPointer => IsAttached;
    public bool ReduceUpdatesWhenNativeWindowInactive => false;

    public void Initialize(GameController game)
    {
        _server = new FrontendWebSocketServer(_options.WebSocketPort, QueueInput);
        _server.Start();

        if (_options.HideNativeWindow)
        {
            SDL.SDL_HideWindow(game.Window.Handle);
        }

        Log.Info($"Remote frontend listening at {_server.ViewerUrl}");
    }

    public bool WantsFrame(uint timestamp)
    {
        bool attached = IsAttached;

        if (!attached)
        {
            _wasAttached = false;
            return false;
        }

        if (!_wasAttached)
        {
            _wasAttached = true;
            _nextFrameAt = timestamp;
        }

        if (unchecked((int)(timestamp - _nextFrameAt)) < 0)
        {
            return false;
        }

        _nextFrameAt = timestamp + (uint)Math.Max(1, 1000 / _options.FramesPerSecond);
        return true;
    }

    public void DrainInput(Action<FrontendInputEvent> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!IsAttached)
        {
            ClearQueuedInput();
            return;
        }

        for (int i = 0; i < MaxInputEventsPerUpdate && _input.TryDequeue(out FrontendInputEvent input); i++)
        {
            Interlocked.Decrement(ref _queuedInputEvents);
            dispatch(input);
        }
    }

    private void ClearQueuedInput()
    {
        while (_input.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queuedInputEvents);
        }
    }

    public FrontendPresentResult Present(in FrontendFrame frame)
    {
        if (!IsAttached || frame.Texture == null || frame.Texture.IsDisposed)
        {
            return default;
        }

        long started = Stopwatch.GetTimestamp();
        FrontendWireFrame wireFrame;

        if (_options.FrameFormat == FrontendFrameFormat.RawRgba)
        {
            int payloadLength = checked(frame.Texture.Width * frame.Texture.Height * 4);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(payloadLength);

            try
            {
                frame.Texture.GetData(rgba, 0, payloadLength);
                wireFrame = FrontendFrameProtocol.CreateRawRgbaFrame(
                    frame.FrameId,
                    frame.Timestamp,
                    frame.Texture.Width,
                    frame.Texture.Height,
                    rgba,
                    payloadLength,
                    ReturnPayload
                );
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rgba);
                throw;
            }
        }
        else
        {
            byte[] png;

            using (var stream = new MemoryStream())
            {
                frame.Texture.SaveAsPng(stream, frame.Texture.Width, frame.Texture.Height);
                png = stream.ToArray();
            }

            wireFrame = FrontendFrameProtocol.CreatePngFrame(
                frame.FrameId,
                frame.Timestamp,
                frame.Texture.Width,
                frame.Texture.Height,
                png
            );
        }

        double captureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        int encodedBytes = wireFrame.Length;
        FrontendPublishResult result = _server.Publish(wireFrame);

        return new FrontendPresentResult(
            encodedBytes,
            captureMilliseconds,
            result.Enqueued,
            result.Dropped
        );
    }

    private static void ReturnPayload(byte[] payload) => ArrayPool<byte>.Shared.Return(payload);

    private void QueueInput(string json)
    {
        try
        {
            FrontendInputMessage message = JsonSerializer.Deserialize(
                json,
                FrontendJsonContext.Default.FrontendInputMessage
            );

            if (!FrontendInputConverter.TryConvert(message, out FrontendInputEvent input))
            {
                return;
            }

            if (Interlocked.Increment(ref _queuedInputEvents) > MaxQueuedInputEvents)
            {
                Interlocked.Decrement(ref _queuedInputEvents);
                return;
            }

            _input.Enqueue(input);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Ignored malformed remote frontend input: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;

        ClearQueuedInput();
        Interlocked.Exchange(ref _queuedInputEvents, 0);
    }
}
