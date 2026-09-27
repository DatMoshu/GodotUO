using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using GUO.Utility.Logging;
using TcpSocket = System.Net.Sockets.Socket;
using static System.Buffers.ArrayPool<byte>;

namespace GUO.Network.Socket;

/// <summary>
/// Handles websocket connections to shards that support it. `ws(s)://[hostname]` as the ip in settings.json.
/// For testing see `tools/ws/README.md` 
/// </summary>
sealed class WebSocketWrapper : SocketWrapper
{
    private const int MAX_RECEIVE_BUFFER_SIZE = 1024 * 1024; // 1MB
    private const int WS_KEEP_ALIVE_INTERVAL = 5;            // seconds

    private ClientWebSocket _webSocket;
    private TcpSocket _rawSocket;

    public override bool IsConnected => _webSocket?.State is WebSocketState.Connecting or WebSocketState.Open;
    public override EndPoint LocalEndPoint => _rawSocket?.LocalEndPoint;
    public bool IsCanceled => _tokenSource.IsCancellationRequested;

    private CancellationTokenSource _tokenSource = new();
    private CircularBuffer _receiveStream;

    public override void Connect(Uri uri) => ConnectAsync(uri, _tokenSource).Wait();

    public override void Send(byte[] buffer, int offset, int count)
    {
        var copy = Shared.Rent(count);
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        SendCopyAsync(copy, count);
    }

    private async void SendCopyAsync(byte[] copy, int count)
    {
        try
        {
            await _webSocket.SendAsync(copy.AsMemory().Slice(0, count), WebSocketMessageType.Binary, true, _tokenSource.Token);
        }
        finally
        {
            Shared.Return(copy);
        }
    }

    public override int Read(byte[] buffer)
    {
        lock (_receiveStream)
        {
            return _receiveStream.Dequeue(buffer, 0, buffer.Length);
        }
    }

    public async Task ConnectAsync(Uri uri, CancellationTokenSource tokenSource = null)
    {
        if (IsConnected)
            return;

        _tokenSource = tokenSource ?? new CancellationTokenSource();
        _receiveStream = new CircularBuffer();

        try
        {
            await ConnectWebSocketAsyncCore(uri);

            if (IsConnected)
                InvokeOnConnected();
            else
                InvokeOnError(SocketError.NotConnected);
        }
        catch (WebSocketException ex)
        {
            SocketError error = ex.InnerException?.InnerException switch
            {
                SocketException socketException => socketException.SocketErrorCode,
                _ => SocketError.SocketError
            };

            Log.Error($"Error {ex.GetType().Name} {error} while connecting to {uri} {ex}");
            InvokeOnError(error);
        }
        catch (Exception ex)
        {
            Log.Error($"Unknown Error {ex.GetType().Name} while connecting to {uri} {ex}");
            InvokeOnError(SocketError.SocketError);
        }
    }


    private async Task ConnectWebSocketAsyncCore(Uri uri)
    {
        // Take control of creating the raw socket and turn off Nagle.
        _rawSocket = new TcpSocket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        _webSocket = new ClientWebSocket();
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(WS_KEEP_ALIVE_INTERVAL); // ping/pong

        using var httpClient = new HttpClient
        (
            new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    try
                    {
                        await _rawSocket.ConnectAsync(context.DnsEndPoint, token);

                        return new NetworkStream(_rawSocket, ownsSocket: true);
                    }
                    catch
                    {
                        _rawSocket?.Dispose();
                        _rawSocket = null;
                        _webSocket?.Dispose();
                        _webSocket = null;

                        throw;
                    }
                }
            }
        );


        await _webSocket.ConnectAsync(uri, httpClient, _tokenSource.Token);

        Log.Trace($"Connected WebSocket: {uri}");

        // Kicks off the async receiving loop 
        StartReceiveAsync().ConfigureAwait(false);
    }

    private async Task StartReceiveAsync()
    {
        var buffer = Shared.Rent(4096);
        // PORT DEVIATION (GUO): upstream sizes its buffer from the raw TCP
        // socket's Available, which says nothing about a WebSocket message
        // (and does not exist in a browser); messages are assembled whole
        // here, a close mid-message publishes nothing, and the 1 MB cap is
        // kept (107c23b). An upstream bug candidate. The web client
        // (ADR-0008) depends on it.
        // MemoryStream preserves the prefix when growing. TCP Available is
        // unrelated to a WebSocket message's size (especially over TLS).
        using var message = new MemoryStream();

        try
        {
            while (IsConnected)
            {
                var receiveResult = await _webSocket.ReceiveAsync(buffer.AsMemory(0, 4096), _tokenSource.Token);

                // A close can interrupt a fragmented message; never publish
                // its incomplete prefix. Text messages are not UO packet data.
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                    break;
                if (receiveResult.MessageType != WebSocketMessageType.Binary)
                    continue;

                if (message.Length + receiveResult.Count > MAX_RECEIVE_BUFFER_SIZE)
                    throw new SocketException((int)SocketError.MessageSize);

                message.Write(buffer, 0, receiveResult.Count);

                if (!receiveResult.EndOfMessage)
                    continue;

                lock (_receiveStream)
                {
                    _receiveStream.Enqueue(message.GetBuffer(), 0, (int)message.Length);
                }

                message.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Trace("WebSocket OperationCanceledException on websocket " + (IsCanceled ? "(was requested)" : "(remote cancelled)"));
        }
        catch (Exception e)
        {
            Log.Trace($"WebSocket error in StartReceiveAsync {e}");
            InvokeOnError(SocketError.SocketError);
        }
        finally
        {
            Shared.Return(buffer);
        }

        if (!IsCanceled)
            InvokeOnError(SocketError.ConnectionReset);
    }

    public override void Disconnect()
    {
        if (!IsConnected)
            return;

        try
        {
            _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None)
                .ContinueWith(_ => _tokenSource?.Cancel());
        }
        catch
        {
            _tokenSource?.Cancel();
        }
    }

    public override void Dispose()
    {
    }
}
