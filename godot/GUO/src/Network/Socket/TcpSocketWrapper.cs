using System;
using System.Net;
using System.Net.Sockets;
using GUO.Utility.Logging;

namespace GUO.Network.Socket;

sealed class TcpSocketWrapper : SocketWrapper
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private TcpClient _socket;

    public override bool IsConnected => _socket?.Client?.Connected ?? false;

    public override EndPoint LocalEndPoint => _socket?.Client?.LocalEndPoint;


    public override void Connect(Uri uri)
    {
        if (IsConnected)
            return;

        _socket = new TcpClient();
        _socket.NoDelay = true;

        try
        {
            // PORT DEVIATION (GUO): connect async and wait at most ConnectTimeout.
            // Upstream's blocking Connect holds the window for the OS timeout
            // (about 21 s on Windows) on a dead host. Callers still expect a
            // connected socket on return (the relay login checks IsConnected
            // right after), so the wait stays synchronous but bounded.
            var connect = _socket.ConnectAsync(uri.Host, uri.Port);

            if (!connect.Wait(ConnectTimeout))
            {
                Log.Error($"error while connecting: no answer from {uri.Host}:{uri.Port} in {ConnectTimeout.TotalSeconds} s");
                _socket.Dispose();
                _socket = null;
                InvokeOnError(SocketError.TimedOut);

                return;
            }

            if (!IsConnected)
            {
                InvokeOnError(SocketError.NotConnected);

                return;
            }

            InvokeOnConnected();
        }
        catch (AggregateException aggEx) when (aggEx.InnerException is SocketException socketEx)
        {
            Log.Error($"error while connecting {socketEx}");
            InvokeOnError(socketEx.SocketErrorCode);
        }
        catch (SocketException socketEx)
        {
            Log.Error($"error while connecting {socketEx}");
            InvokeOnError(socketEx.SocketErrorCode);
        }
        catch (Exception ex)
        {
            Log.Error($"error while connecting {ex}");
            InvokeOnError(SocketError.SocketError);
        }
    }

    public override void Send(byte[] buffer, int offset, int count)
    {
        var stream = _socket.GetStream();
        stream.Write(buffer, offset, count);
        stream.Flush();
    }

    public override int Read(byte[] buffer)
    {
        if (!IsConnected)
            return 0;

        // PORT DEVIATION (GUO): three fixes from 107c23b, each an upstream
        // bug candidate: a FIN with no data is a disconnect (below), a read
        // cut short returns what it got (`return done`), and Disconnect clears
        // _socket.
        // Connected records the last socket operation. A FIN has no payload,
        // so Available alone cannot distinguish it from an idle connection.
        if (_socket.Client.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
        {
            Disconnect();
            InvokeOnDisconnected();
            return 0;
        }

        var available = Math.Min(buffer.Length, _socket.Available);
        var done = 0;

        var stream = _socket.GetStream();

        while (done < available)
        {
            var toRead = Math.Min(buffer.Length, available - done);
            var read = stream.Read(buffer, done, toRead);

            if (read <= 0)
            {
                Disconnect();
                InvokeOnDisconnected();

                return done;
            }

            done += read;
        }

        return done;
    }

    public override void Disconnect()
    {
        _socket?.Close();
        Dispose();
    }

    public override void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }
}
