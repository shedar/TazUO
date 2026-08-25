using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Frontend;

internal readonly record struct FrontendPublishResult(bool Enqueued, bool Dropped);

internal sealed class FrontendWebSocketServer : IDisposable
{
    private const int MaxHttpHeaderBytes = 16 * 1024;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<string> _onInput;
    private readonly Action _onConnected;
    private readonly object _clientTasksGate = new();
    private readonly HashSet<Task> _clientTasks = new();
    private Task _acceptLoop;
    private FrontendWebSocketConnection _connection;
    private int _started;

    public FrontendWebSocketServer(int port, Action<string> onInput, Action onConnected = null)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _listener = new TcpListener(IPAddress.Loopback, port);
        _onInput = onInput ?? throw new ArgumentNullException(nameof(onInput));
        _onConnected = onConnected;
    }

    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;
    public string ViewerUrl => $"http://127.0.0.1:{Port}/";
    public bool IsConnected => Volatile.Read(ref _connection)?.IsOpen == true;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
    }

    public FrontendPublishResult Publish(FrontendWireFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        FrontendWebSocketConnection connection = Volatile.Read(ref _connection);

        if (connection == null)
        {
            frame.Dispose();
            return default;
        }

        return connection.Publish(frame);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                TrackClient(HandleClientAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Frontend listener failed: {ex}");
        }
    }

    private void TrackClient(Task task)
    {
        lock (_clientTasksGate)
        {
            _clientTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_clientTasksGate)
                {
                    _clientTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            string requestText = await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);

            if (!TryParseRequest(requestText, out string path, out Dictionary<string, string> headers))
            {
                await WriteResponseAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", "Bad request", cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (path == "/" || path == "/index.html")
            {
                await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", FrontendViewerPage.Content, cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (path == "/health")
            {
                await WriteResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", IsConnected ? "attached" : "waiting", cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (path != "/ws"
                || !headers.TryGetValue("Upgrade", out string upgrade)
                || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
                || !headers.TryGetValue("Sec-WebSocket-Key", out string key))
            {
                await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found", cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid))
            );
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);

            WebSocket socket = WebSocket.CreateFromStream(
                stream,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(20)
            );
            var connection = new FrontendWebSocketConnection(client, socket, _onInput, cancellationToken);
            FrontendWebSocketConnection previous = Interlocked.Exchange(ref _connection, connection);
            previous?.Dispose();
            _onConnected?.Invoke();

            try
            {
                await connection.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _connection, null, connection);
                connection.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            client.Dispose();
            Log.Warn($"Frontend HTTP/WebSocket connection ended: {ex.Message}");
        }
    }

    private static async Task<string> ReadHttpRequestAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(MaxHttpHeaderBytes);
        int length = 0;

        try
        {
            while (length < MaxHttpHeaderBytes)
            {
                int read = await stream.ReadAsync(
                    bytes.AsMemory(length, MaxHttpHeaderBytes - length),
                    cancellationToken
                ).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                length += read;

                for (int i = Math.Max(3, length - read - 3); i < length; i++)
                {
                    if (i >= 3
                        && bytes[i - 3] == '\r'
                        && bytes[i - 2] == '\n'
                        && bytes[i - 1] == '\r'
                        && bytes[i] == '\n')
                    {
                        return Encoding.ASCII.GetString(bytes, 0, i + 1);
                    }
                }
            }

            throw new InvalidDataException("Frontend HTTP header was incomplete or too large.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static bool TryParseRequest(
        string request,
        out string path,
        out Dictionary<string, string> headers
    )
    {
        path = null;
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = request.Split("\r\n", StringSplitOptions.None);

        if (lines.Length == 0)
        {
            return false;
        }

        string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (requestLine.Length != 3 || requestLine[0] != "GET")
        {
            return false;
        }

        int queryIndex = requestLine[1].IndexOf('?');
        path = queryIndex >= 0 ? requestLine[1][..queryIndex] : requestLine[1];

        for (int i = 1; i < lines.Length; i++)
        {
            int separator = lines[i].IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        return true;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        string status,
        string contentType,
        string body,
        CancellationToken cancellationToken
    )
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _lifetime.Cancel();
        _listener.Stop();
        Interlocked.Exchange(ref _connection, null)?.Dispose();

        try
        {
            _acceptLoop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        Task[] clientTasks;

        lock (_clientTasksGate)
        {
            clientTasks = new Task[_clientTasks.Count];
            _clientTasks.CopyTo(clientTasks);
        }

        try
        {
            Task.WhenAll(clientTasks).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }
}

internal sealed class FrontendWebSocketConnection : IDisposable
{
    private const int MaxInputMessageBytes = 64 * 1024;

    private readonly TcpClient _client;
    private readonly WebSocket _socket;
    private readonly Action<string> _onInput;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private readonly object _frameGate = new();
    private FrontendWireFrame _latestFrame;
    private bool _frameSignalPending;
    private int _disposed;

    public FrontendWebSocketConnection(
        TcpClient client,
        WebSocket socket,
        Action<string> onInput,
        CancellationToken parentToken
    )
    {
        _client = client;
        _socket = socket;
        _onInput = onInput;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    }

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _socket.State == WebSocketState.Open;

    public FrontendPublishResult Publish(FrontendWireFrame frame)
    {
        lock (_frameGate)
        {
            if (!IsOpen)
            {
                frame.Dispose();
                return default;
            }

            FrontendWireFrame droppedFrame = _latestFrame;
            _latestFrame = frame;
            droppedFrame?.Dispose();

            if (!_frameSignalPending)
            {
                _frameSignalPending = true;
                _frameReady.Release();
            }

            return new FrontendPublishResult(true, droppedFrame != null);
        }
    }

    public async Task RunAsync()
    {
        Task send = SendLoopAsync(_lifetime.Token);
        Task receive = ReceiveLoopAsync(_lifetime.Token);
        await Task.WhenAny(send, receive).ConfigureAwait(false);
        _lifetime.Cancel();
        _socket.Abort();

        try
        {
            await Task.WhenAll(send, receive).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _frameReady.WaitAsync(cancellationToken).ConfigureAwait(false);
            FrontendWireFrame frame;

            lock (_frameGate)
            {
                frame = _latestFrame;
                _latestFrame = null;
                _frameSignalPending = false;
            }

            if (frame == null)
            {
                continue;
            }

            try
            {
                await _socket.SendAsync(
                    frame.Header,
                    WebSocketMessageType.Binary,
                    endOfMessage: false,
                    cancellationToken
                ).ConfigureAwait(false);
                await _socket.SendAsync(
                    frame.Payload.AsMemory(0, frame.PayloadLength),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            using var message = new MemoryStream();

            while (!cancellationToken.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(
                    buffer.AsMemory(),
                    cancellationToken
                ).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage)
                    {
                        message.SetLength(0);
                    }

                    continue;
                }

                if (message.Length + result.Count > MaxInputMessageBytes)
                {
                    throw new InvalidDataException("Frontend input message exceeded the size limit.");
                }

                message.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                {
                    continue;
                }

                _onInput(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        lock (_frameGate)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        _socket.Abort();
        _socket.Dispose();
        _client.Dispose();
        _lifetime.Dispose();
    }
}
