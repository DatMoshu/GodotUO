r"""A WebSocket-to-TCP bridge in front of a UO shard, for the web client.

    launchers\web\ws_bridge.bat            serve: ws://127.0.0.1:UO_WS_BRIDGE_PORT
                                           -> UO_SHARD_HOST:UO_SHARD_PORT
    python tools\ws_bridge\run.py serve [--listen H] [--port P] [--target H:P]
                                        [--allow-origin URL|*]...
    python tools\ws_bridge\run.py test  [--fake]

A browser cannot open a TCP socket, and ModernUO speaks nothing else. This is
the same job as upstream's test proxy (sources/ClassicUO/tools/ws/proxy.mjs,
websockify from 127.0.0.1:2594 to 127.0.0.1:2593) in standard-library Python,
so it needs neither Node nor a pip install. Each binary WebSocket message
carries raw UO bytes; nothing is parsed, so the client's encryption and the
server's compression pass through untouched.

The client side is upstream's own: an `ip` beginning with ws:// makes
NetClient use WebSocketWrapper, and `ignore_relay_ip` makes it reconnect to
the same address after the login server's relay packet (0xA8/0x8C) instead
of the shard's raw TCP address. That only works while the login and game
server are one endpoint, which is true of ModernUO and of this bridge.

LAN MODE (--lan, or UO_WEB_LAN=1): for a phone on the same network. The
bridge then listens on this PC's private LAN address only, speaks wss with
the local certificate tools\web serve --lan uses (guo.lan), lets in the page
origins of that server, and drops any connection from an address that is not
private or loopback. Off by default.

SAFETY. It listens on loopback unless told otherwise. Any web page can open a
WebSocket to 127.0.0.1, so a browser connection is refused unless its Origin
is the local web server's (tools\web serve, UO_WEB_PORT) or one given with
--allow-origin. Clients that send no Origin (the desktop client, tests) are
not browsers and are let through.

`test` starts a bridge on a free port and relays a real login through it: the
0xEF seed and a 0x80 account login as UO_SHARD_OWNER, and passes when the
shard answers with a server list (0xA8) or a login denial (0x82) -- either is
the shard speaking through the bridge. `test --fake` runs the same exchange
against an in-process stand-in shard, for machines without one (CI).
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import hashlib
import os
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import lan  # noqa: E402
from guo.config import load_config  # noqa: E402

GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
MAX_HEADER = 16 * 1024
MAX_MESSAGE = 1024 * 1024  # the client's own receive cap (WebSocketWrapper)

OP_CONT, OP_TEXT, OP_BINARY, OP_CLOSE, OP_PING, OP_PONG = 0x0, 0x1, 0x2, 0x8, 0x9, 0xA


def log(msg: str) -> None:
    print(f"[ws_bridge] {msg}", flush=True)


# --------------------------------------------------------------------------
# RFC 6455 framing
# --------------------------------------------------------------------------

def accept_key(key: str) -> str:
    return base64.b64encode(hashlib.sha1(key.encode("ascii") + GUID).digest()).decode("ascii")


def encode_frame(opcode: int, payload: bytes, mask: bool = False) -> bytes:
    head = bytearray([0x80 | opcode])
    n = len(payload)
    mbit = 0x80 if mask else 0
    if n < 126:
        head.append(mbit | n)
    elif n < 1 << 16:
        head.append(mbit | 126)
        head += struct.pack(">H", n)
    else:
        head.append(mbit | 127)
        head += struct.pack(">Q", n)
    if not mask:
        return bytes(head) + payload
    key = os.urandom(4)
    return bytes(head) + key + bytes(b ^ key[i & 3] for i, b in enumerate(payload))


class ProtocolError(Exception):
    pass


async def read_frame(reader: asyncio.StreamReader, expect_masked: bool) -> tuple[bool, int, bytes]:
    b0, b1 = await reader.readexactly(2)
    if b0 & 0x70:
        raise ProtocolError("reserved bits set")
    fin, opcode = bool(b0 & 0x80), b0 & 0x0F
    masked, n = bool(b1 & 0x80), b1 & 0x7F
    if masked != expect_masked:
        raise ProtocolError("client frames must be masked" if expect_masked else "server frames must not be masked")
    if n == 126:
        (n,) = struct.unpack(">H", await reader.readexactly(2))
    elif n == 127:
        (n,) = struct.unpack(">Q", await reader.readexactly(8))
    if n > MAX_MESSAGE:
        raise ProtocolError(f"frame of {n} bytes")
    key = await reader.readexactly(4) if masked else b""
    data = await reader.readexactly(n)
    if masked:
        data = bytes(b ^ key[i & 3] for i, b in enumerate(data))
    return fin, opcode, data


async def read_message(reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
                       expect_masked: bool) -> tuple[int, bytes] | None:
    """One whole data message; answers pings on the way. None on close."""
    parts: list[bytes] = []
    first_op = None
    while True:
        fin, op, data = await read_frame(reader, expect_masked)
        if op == OP_PING:
            writer.write(encode_frame(OP_PONG, data, mask=not expect_masked))
            continue
        if op == OP_PONG:
            continue
        if op == OP_CLOSE:
            return None
        if op == OP_CONT:
            if first_op is None:
                raise ProtocolError("continuation without a start")
        else:
            if first_op is not None:
                raise ProtocolError("new message inside a fragmented one")
            first_op = op
        parts.append(data)
        if sum(map(len, parts)) > MAX_MESSAGE:
            raise ProtocolError("message too large")
        if fin:
            return first_op, b"".join(parts)


# --------------------------------------------------------------------------
# The bridge
# --------------------------------------------------------------------------

async def read_http_head(reader: asyncio.StreamReader) -> tuple[str, dict[str, str]]:
    raw = await reader.readuntil(b"\r\n\r\n")
    if len(raw) > MAX_HEADER:
        raise ProtocolError("header too large")
    lines = raw.decode("latin-1").split("\r\n")
    headers = {}
    for line in lines[1:]:
        if ":" in line:
            k, v = line.split(":", 1)
            headers[k.strip().lower()] = v.strip()
    return lines[0], headers


def http_error(writer: asyncio.StreamWriter, status: str) -> None:
    writer.write(f"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".encode("ascii"))


class Bridge:
    def __init__(self, target: tuple[str, int], origins: set[str], trace: bool = False, private_only: bool = False):
        self.target = target
        self.origins = origins  # "*" = any
        self.trace = trace
        self.private_only = private_only  # LAN mode: only private and loopback peers

    def traced(self, direction: str, data: bytes) -> None:
        if self.trace:
            log(f"{direction} {len(data):6d} bytes  {data[:24].hex(' ')}")

    def origin_ok(self, origin: str | None) -> bool:
        if origin is None:
            return True  # not a browser
        return "*" in self.origins or origin.rstrip("/") in self.origins

    async def handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer = writer.get_extra_info("peername")
        if self.private_only and not (peer and lan.is_private(str(peer[0]))):
            log(f"{peer}: refused, not a private address")
            writer.close()
            return
        try:
            request, headers = await asyncio.wait_for(read_http_head(reader), 10)
            method = request.split(" ", 1)[0]
            key = headers.get("sec-websocket-key")
            if method != "GET" or "websocket" not in headers.get("upgrade", "").lower() or not key:
                http_error(writer, "400 Bad Request")
                return
            origin = headers.get("origin")
            if not self.origin_ok(origin):
                log(f"{peer}: refused origin {origin!r}")
                http_error(writer, "403 Forbidden")
                return
            try:
                up_reader, up_writer = await asyncio.wait_for(asyncio.open_connection(*self.target), 10)
            except (OSError, asyncio.TimeoutError) as e:
                log(f"{peer}: shard {self.target[0]}:{self.target[1]} unreachable: {e}")
                http_error(writer, "502 Bad Gateway")
                return
            reply = [
                "HTTP/1.1 101 Switching Protocols",
                "Upgrade: websocket",
                "Connection: Upgrade",
                f"Sec-WebSocket-Accept: {accept_key(key)}",
            ]
            # websockify's clients ask for "binary"; echo it when offered.
            offered = [p.strip() for p in headers.get("sec-websocket-protocol", "").split(",") if p.strip()]
            if "binary" in offered:
                reply.append("Sec-WebSocket-Protocol: binary")
            writer.write(("\r\n".join(reply) + "\r\n\r\n").encode("ascii"))
            await writer.drain()
            log(f"{peer}: open -> {self.target[0]}:{self.target[1]}")
            await self.pump(reader, writer, up_reader, up_writer)
            log(f"{peer}: closed")
        except (asyncio.IncompleteReadError, asyncio.LimitOverrunError, asyncio.TimeoutError,
                ConnectionError, ProtocolError) as e:
            log(f"{peer}: {type(e).__name__} {e}")
        finally:
            writer.close()

    async def pump(self, ws_r, ws_w, tcp_r, tcp_w) -> None:
        async def ws_to_tcp():
            while True:
                msg = await read_message(ws_r, ws_w, expect_masked=True)
                if msg is None:
                    ws_w.write(encode_frame(OP_CLOSE, struct.pack(">H", 1000)))
                    return
                self.traced("client -> shard", msg[1])
                tcp_w.write(msg[1])
                await tcp_w.drain()

        async def tcp_to_ws():
            while True:
                data = await tcp_r.read(65536)
                if not data:
                    ws_w.write(encode_frame(OP_CLOSE, struct.pack(">H", 1000)))
                    return
                self.traced("shard -> client", data)
                ws_w.write(encode_frame(OP_BINARY, data))
                await ws_w.drain()

        tasks = [asyncio.create_task(ws_to_tcp()), asyncio.create_task(tcp_to_ws())]
        try:
            done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
            for t in pending:
                t.cancel()
            for t in done:
                if t.exception() and not isinstance(t.exception(), (asyncio.IncompleteReadError, ConnectionError)):
                    raise t.exception()
        finally:
            tcp_w.close()
            try:
                await ws_w.drain()
            except ConnectionError:
                pass


async def start_bridge(listen: str, port: int, target: tuple[str, int], origins: set[str],
                       trace: bool = False, ssl_context=None, private_only: bool = False) -> asyncio.base_events.Server:
    bridge = Bridge(target, origins, trace, private_only)
    return await asyncio.start_server(bridge.handle, listen, port, limit=MAX_HEADER, ssl=ssl_context)


def default_origins(web_port: int) -> set[str]:
    return {f"http://127.0.0.1:{web_port}", f"http://localhost:{web_port}"}


def lan_origins(address: str, web_port: int) -> set[str]:
    """The page origins of tools/web serve --lan: https on the LAN address and this PC's names."""
    return {f"https://{name}:{web_port}" for name in lan.host_names(address)}


# --------------------------------------------------------------------------
# A minimal client, for the test
# --------------------------------------------------------------------------

class Client:
    def __init__(self, reader, writer):
        self.r, self.w = reader, writer

    @classmethod
    async def connect(cls, host: str, port: int, origin: str | None = None) -> "Client":
        r, w = await asyncio.open_connection(host, port)
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        lines = [f"GET / HTTP/1.1", f"Host: {host}:{port}", "Upgrade: websocket", "Connection: Upgrade",
                 f"Sec-WebSocket-Key: {key}", "Sec-WebSocket-Version: 13"]
        if origin:
            lines.append(f"Origin: {origin}")
        w.write(("\r\n".join(lines) + "\r\n\r\n").encode("ascii"))
        status, headers = await read_http_head(r)
        if " 101 " not in status + " ":
            w.close()
            raise ProtocolError(status)
        if headers.get("sec-websocket-accept") != accept_key(key):
            raise ProtocolError("bad Sec-WebSocket-Accept")
        return cls(r, w)

    async def send(self, data: bytes) -> None:
        self.w.write(encode_frame(OP_BINARY, data, mask=True))
        await self.w.drain()

    async def recv(self) -> bytes | None:
        msg = await read_message(self.r, self.w, expect_masked=False)
        return None if msg is None else msg[1]

    def close(self) -> None:
        self.w.write(encode_frame(OP_CLOSE, struct.pack(">H", 1000), mask=True))
        self.w.close()


def login_bytes(account: str, password: str, version: str) -> bytes:
    """0xEF seed packet then 0x80 account login, unencrypted, as the client sends them."""
    seed = 0x7F000001
    v = [int(x) for x in (version.split(".") + ["0"] * 4)[:4]]
    ef = struct.pack(">BI4I", 0xEF, seed, *v)
    acct = account.encode("ascii")[:30].ljust(30, b"\0")
    pw = password.encode("ascii")[:30].ljust(30, b"\0")
    return ef + b"\x80" + acct + pw + b"\x5D"


async def fake_shard() -> tuple[asyncio.base_events.Server, int]:
    """Stands in for ModernUO: reads the 21 + 62 login bytes, answers with a
    0x82 denial (reason 0x03, 'bad password'), then a 70 KB burst to check
    large and fragmented relaying."""
    async def serve(r, w):
        try:
            await r.readexactly(21 + 62)
            w.write(b"\x82\x03")
            w.write(bytes(range(256)) * 280)
            await w.drain()
            await r.read(1)
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        finally:
            w.close()
    server = await asyncio.start_server(serve, "127.0.0.1", 0)
    return server, server.sockets[0].getsockname()[1]


async def run_test(cfg, fake: bool) -> int:
    fake_server = None
    if fake:
        fake_server, fake_port = await fake_shard()
        target = ("127.0.0.1", fake_port)
    else:
        target = (cfg.shard_host, cfg.shard_port)
    server = await start_bridge("127.0.0.1", 0, target, default_origins(cfg.web_port))
    port = server.sockets[0].getsockname()[1]
    log(f"test bridge ws://127.0.0.1:{port} -> {target[0]}:{target[1]}{' (fake shard)' if fake else ''}")
    failures = 0

    def check(ok: bool, what: str) -> None:
        nonlocal failures
        print(f"  {'ok  ' if ok else 'FAIL'} {what}", flush=True)
        failures += 0 if ok else 1

    # A foreign web page must be refused.
    try:
        c = await Client.connect("127.0.0.1", port, origin="https://example.invalid")
        c.close()
        check(False, "a foreign Origin is refused")
    except ProtocolError as e:
        check("403" in str(e), f"a foreign Origin is refused ({e})")

    # The local web page's origin is let through; the login relays.
    try:
        c = await Client.connect("127.0.0.1", port, origin=f"http://127.0.0.1:{cfg.web_port}")
        check(True, "the local web page's Origin is accepted")
        await c.send(login_bytes(cfg.shard_owner, cfg.shard_owner_password, cfg.client_version))
        first = await asyncio.wait_for(c.recv(), 10)
        check(first is not None and first[:1] in (b"\xA8", b"\x82"),
              f"the shard answered the login through the bridge: packet 0x{first[0]:02X}, {len(first)} bytes"
              if first else "the shard answered the login through the bridge")
        if fake:
            got = first[2:] if first else b""
            while len(got) < 256 * 280:
                more = await asyncio.wait_for(c.recv(), 10)
                if more is None:
                    break
                got += more
            check(got == bytes(range(256)) * 280, f"a 70 KB burst arrived intact ({len(got)} bytes)")
        c.close()
    except (ProtocolError, OSError, asyncio.TimeoutError, asyncio.IncompleteReadError) as e:
        check(False, f"login through the bridge: {type(e).__name__} {e}")

    server.close()
    await server.wait_closed()
    if fake_server:
        fake_server.close()
    print("PASS" if failures == 0 else f"FAIL ({failures})", flush=True)
    return 0 if failures == 0 else 1


# --------------------------------------------------------------------------

def parse_target(s: str) -> tuple[str, int]:
    host, _, port = s.rpartition(":")
    return host, int(port)


def main() -> int:
    cfg = load_config()
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("serve")
    s.add_argument("--listen", default="127.0.0.1")
    s.add_argument("--port", type=int, default=cfg.ws_bridge_port)
    s.add_argument("--target", type=parse_target, default=(cfg.shard_host, cfg.shard_port))
    s.add_argument("--allow-origin", action="append", default=[],
                   help="another page origin allowed to connect, or * for any")
    s.add_argument("--trace", action="store_true", help="log every message: direction, size, first bytes")
    s.add_argument("--lan", action="store_true", default=cfg.web_lan,
                   help="for a phone on this network: wss on the LAN address, private peers only (UO_WEB_LAN=1)")
    t = sub.add_parser("test")
    t.add_argument("--fake", action="store_true", help="use an in-process stand-in shard")
    args = ap.parse_args()

    if args.cmd == "test":
        return asyncio.run(run_test(cfg, args.fake))

    origins = default_origins(cfg.web_port) | {o.rstrip("/") for o in args.allow_origin}
    context, scheme = None, "ws"
    if args.lan:
        try:
            args.listen = lan.lan_address(cfg.web_lan_host)
        except ValueError as e:
            log(str(e))
            return 2
        _, cert, key = lan.ensure_certificates(cfg.build / "web" / "lan_certs", args.listen)
        context, scheme = lan.server_context(cert, key), "wss"
        origins |= lan_origins(args.listen, cfg.web_port)

    async def serve_forever():
        server = await start_bridge(args.listen, args.port, args.target, origins, args.trace,
                                    context, private_only=args.lan)
        if args.lan:
            log("LAN mode: other devices on this network can connect; addresses that are not private are refused")
        elif args.listen not in ("127.0.0.1", "localhost", "::1"):
            log(f"WARNING: listening on {args.listen}, reachable from other machines")
        log(f"{scheme}://{args.listen}:{args.port} -> {args.target[0]}:{args.target[1]}; "
            f"browser origins: {', '.join(sorted(origins))}")
        log('client settings: "ip": "%s://%s:%d", "ignore_relay_ip": true. Ctrl+C stops.'
            % (scheme, args.listen, args.port))
        async with server:
            await server.serve_forever()

    try:
        asyncio.run(serve_forever())
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
