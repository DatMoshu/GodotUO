// GUO-owned (not a ported file): the shard over a WebSocket, in a browser.
//
// Upstream's WebSocketWrapper cannot run in a page: it opens its own TCP
// socket inside a SocketsHttpHandler (a browser has neither), and Connect
// blocks on .Wait() on what is the browser's main thread, where the socket
// can never finish opening. Godot's WebSocketPeer is the browser's WebSocket,
// and it is polled, which is how the client already drives its socket:
// NetClient.CollectAvailableData reads once a frame (GameController.Update).
//
// Like upstream's wrapper, IsConnected is true while the socket is still
// connecting; the relay path (LoginScene.HandleRelayServerPacket) sends the
// seed and second login right after Connect returns, so sends made before
// the socket opens are queued and flushed when it does, and Connected is
// raised from the read poll at that moment. The other end is tools/ws_bridge
// (or any websockify-style bridge in front of a shard). ADR-0008.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Godot;
using GUO.Utility.Logging;

namespace GUO.Network.Socket;

sealed class GodotWebSocketWrapper : SocketWrapper
{
    private const int BUFFER_SIZE = 1024 * 1024; // upstream's per-message cap

    private WebSocketPeer _peer;
    private bool _opened;
    private readonly Queue<byte[]> _pending = new();
    private byte[] _carry;
    private int _carryPos;

    public override bool IsConnected =>
        _peer != null && _peer.GetReadyState() is WebSocketPeer.State.Connecting or WebSocketPeer.State.Open;

    // No local address in a page; NetClient.LocalIP falls back to 127.0.0.1,
    // as it does for any socket without one.
    public override EndPoint LocalEndPoint => null;

    public override void Connect(Uri uri)
    {
        Disconnect();
        _peer = new WebSocketPeer
        {
            InboundBufferSize = BUFFER_SIZE,
            OutboundBufferSize = BUFFER_SIZE,
            // websockify-style bridges answer with it when it is offered.
            SupportedProtocols = new[] { "binary" },
        };

        Error err = _peer.ConnectToUrl(uri.ToString());
        if (err != Error.Ok)
        {
            Log.Error($"WebSocket connect to {uri} failed at once: {err}");
            _peer = null;
            InvokeOnError(SocketError.NotConnected);
            return;
        }

        Log.Trace($"WebSocket connecting to {uri}");
    }

    public override void Send(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);

        if (_opened && _peer != null)
        {
            _peer.Send(copy, WebSocketPeer.WriteMode.Binary);
        }
        else
        {
            _pending.Enqueue(copy);
        }
    }

    public override int Read(byte[] buffer)
    {
        if (_peer == null)
        {
            return 0;
        }

        _peer.Poll();
        WebSocketPeer.State state = _peer.GetReadyState();

        if (state == WebSocketPeer.State.Open && !_opened)
        {
            _opened = true;
            while (_pending.Count > 0)
            {
                _peer.Send(_pending.Dequeue(), WebSocketPeer.WriteMode.Binary);
            }

            Log.Trace("WebSocket open");
            InvokeOnConnected();
        }

        int written = 0;
        while (written < buffer.Length)
        {
            if (_carry == null)
            {
                if (_peer == null || _peer.GetAvailablePacketCount() == 0)
                {
                    break;
                }

                _carry = _peer.GetPacket();
                _carryPos = 0;
            }

            int n = Math.Min(buffer.Length - written, _carry.Length - _carryPos);
            Buffer.BlockCopy(_carry, _carryPos, buffer, written, n);
            written += n;
            _carryPos += n;
            if (_carryPos >= _carry.Length)
            {
                _carry = null;
            }
        }

        if (written == 0 && state == WebSocketPeer.State.Closed)
        {
            bool wasOpen = _opened;
            Log.Trace($"WebSocket closed: {_peer.GetCloseCode()} {_peer.GetCloseReason()}");
            Reset();
            if (wasOpen)
            {
                InvokeOnDisconnected();
            }
            else
            {
                InvokeOnError(SocketError.ConnectionRefused);
            }
        }

        return written;
    }

    public override void Disconnect()
    {
        _peer?.Close();
        Reset();
    }

    public override void Dispose()
    {
        Disconnect();
    }

    private void Reset()
    {
        _peer = null;
        _opened = false;
        _pending.Clear();
        _carry = null;
        _carryPos = 0;
    }
}
