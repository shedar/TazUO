using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using SDL3;

namespace ClassicUO.Frontend;

internal sealed class WebSocketFrontendAdapter : IFrontendAdapter
{
    private const int MaxQueuedInputEvents = 4096;
    private const int MaxInputEventsPerUpdate = 512;
    private const uint ResourceAcknowledgementRetryMilliseconds = 2000;

    private readonly FrontendOptions _options;
    private readonly ConcurrentQueue<FrontendInputEvent> _input = new();
    private readonly FrontendDisplayListRecorder _displayList;
    private FrontendWebSocketServer _server;
    private int _queuedInputEvents;
    private uint _nextFrameAt;
    private uint _resourceRetryAt;
    private bool _wasAttached;

    public WebSocketFrontendAdapter(FrontendOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.FrameFormat == FrontendFrameFormat.DisplayList)
        {
            _displayList = new FrontendDisplayListRecorder();
        }
    }

    public FrontendMode Mode => FrontendMode.WebSocket;
    public bool IsAttached => _server?.IsConnected == true;
    public bool OwnsPointer => IsAttached;
    public bool ReduceUpdatesWhenNativeWindowInactive => false;
    public FrontendResourcePolicy Resources => new(
        EnableAudio: false,
        EnableVoiceRecognition: false,
        EnableNativeInput: !_options.HideNativeWindow,
        EnableRenderLoop: true,
        RequiresComposedFramebuffer: _options.FrameFormat != FrontendFrameFormat.DisplayList,
        PresentNativeFramebuffer: !_options.HideNativeWindow
    );
    public IRenderCommandSink RenderCommandSink => _displayList;

    public void Initialize(GameController game)
    {
        _displayList?.Initialize(game.GraphicsDevice);
        _server = new FrontendWebSocketServer(
            _options.WebSocketPort,
            QueueInput,
            _displayList == null ? null : _displayList.ResetResourceAcknowledgement
        );
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
            _resourceRetryAt = timestamp;
        }

        if (_displayList?.IsAwaitingResourceAcknowledgement == true
            && unchecked((int)(timestamp - _resourceRetryAt)) < 0)
        {
            return false;
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
        if (!IsAttached)
        {
            return default;
        }

        long started = Stopwatch.GetTimestamp();
        FrontendWireFrame wireFrame;
        FrontendDisplayListMetrics displayListMetrics = default;
        int uncompressedBytes;

        if (_options.FrameFormat == FrontendFrameFormat.DisplayList)
        {
            wireFrame = _displayList.CreateWireFrame(
                frame.FrameId,
                frame.Timestamp,
                out displayListMetrics
            );
            uncompressedBytes = FrontendFrameProtocol.HeaderSize + displayListMetrics.PayloadBytes;

            if (displayListMetrics.ResourceRecords > 0)
            {
                _resourceRetryAt = frame.Timestamp + ResourceAcknowledgementRetryMilliseconds;
            }
        }
        else if (frame.Texture == null || frame.Texture.IsDisposed)
        {
            return default;
        }
        else if (_options.FrameFormat == FrontendFrameFormat.RawRgba)
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
                uncompressedBytes = wireFrame.Length;
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
            uncompressedBytes = checked(
                FrontendFrameProtocol.HeaderSize + frame.Texture.Width * frame.Texture.Height * 4
            );
        }

        double captureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        int encodedBytes = wireFrame.Length;
        FrontendPublishResult result = _server.Publish(wireFrame);

        return new FrontendPresentResult(
            encodedBytes,
            captureMilliseconds,
            result.Enqueued,
            result.Dropped,
            uncompressedBytes,
            displayListMetrics.ResourceBytes,
            displayListMetrics.CommandBytes,
            displayListMetrics.ResourceRecords,
            displayListMetrics.Commands
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
                if (message?.Type == "resourceAck")
                {
                    _displayList?.AcknowledgeResources(message.ResourceSequence);
                }
                else if (message?.Type == "viewerReady")
                {
                    _displayList?.ResetResourceAcknowledgement();
                }

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
        _displayList?.Dispose();

        ClearQueuedInput();
        Interlocked.Exchange(ref _queuedInputEvents, 0);
    }
}
