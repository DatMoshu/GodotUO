# Sample mod: shard announcements

The smallest useful packet mod (ADR-0038): the shard sends packet 0xF9, a
custom packet the client does not know, and the mod prints its text in the
journal in the colour the shard chose.

## The packet

Server to client, dynamic length, big-endian:

| Bytes | Field |
|---|---|
| 0 | `0xF9` |
| 1-2 | length of the whole packet |
| 3-4 | hue |
| 5.. | the text, UTF-8 |

Without the mod, GUO (like ClassicUO) frames 0xF9 by its own table as a
3-byte packet, reads the rest of the text as further packets and loses the
stream until the next login. With the mod on, the client frames it by the
mod's declared length and hands it to `AnnounceMod`.

## Build and install

```
dotnet build docs\modding\samples\announce\Announce.csproj -c Release
```

Copy `bin\Release\net8.0\Announce.dll` and `mod.json` into
`Data\Mods\announce\` beside the client. Open Options, tick **Load mods on
the next start**, and restart. The status line under it says what loaded.

The sample's `mod.json` lists `127.0.0.1` and `localhost` in `shards`, so it
is active only on a shard on this computer. Put your shard's login host there
for your own mod.

## What a shard sends

Five header bytes and the text. In C# (ModernUO, ServUO, RunUO), with `ns`
the player's connection:

```csharp
byte[] body = Encoding.UTF8.GetBytes(text);
byte[] packet = new byte[5 + body.Length];
packet[0] = 0xF9;
packet[1] = (byte)(packet.Length >> 8);
packet[2] = (byte)packet.Length;
packet[3] = (byte)(hue >> 8);
packet[4] = (byte)hue;
body.CopyTo(packet, 5);
ns.Send(packet);
```

On POL, send the same bytes as a raw packet from the script that announces.

Only send it to clients that have the mod: a client without it misreads the
stream. Ask the player to install it, or send it only after the client has
told the shard it can (a later protocol step, NET-02).

## Tests

`dotnet run --project tools/network_tests` loads this mod from its DLL and
checks it end to end; `ModsProbe` does the same inside the Godot process.
