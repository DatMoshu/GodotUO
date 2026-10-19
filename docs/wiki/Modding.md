# Modding

How to write a GUO mod, test it, ship it to your players, and what to do when
a mod makes the game slow.

A GUO **mod** teaches the client a packet your shard sends that ClassicUO
does not know. Shards with their own server packages (POL, ServUO and RunUO
forks) used to need a forked client for that. With a mod, players keep the
normal GUO and add your mod. The design is ADR-0038; what else you can change
without a fork (assistant plugins, scripts, packs, server gumps) is listed in
the modding survey, `docs/modding/surface_2026-10-18.md`.

What a mod is, plainly: a small .NET assembly that runs inside GUO with the
player's rights. GUO only loads it on a desktop (Windows or Linux), only when
the player has turned **Load mods** on, and only on the shards the mod names.
It never downloads one by itself.

## What a mod can do today

- Add a packet the client does not know, with its length (fixed, or dynamic
  with the length in bytes 1-2).
- Take a 0xBF extended-command subcommand.
- Replace one of the client's own packet handlers, when it says so
  (`"override": true`).
- In its handler: print a system message, send a packet to the server, write
  to the client log.

Not yet: opening a gump or changing the world directly (a later ABI version),
and anything on Android or the web build. GUO keeps 0xBF subcommand 0x4755
for itself (the shard offer); no mod can take it.

## Write one

Start from the sample, `docs/modding/samples/announce`: it adds packet 0xF9,
a shard announcement shown in the journal, and its README shows the bytes a
shard sends.

1. Make a .NET 8 class library that references `godot/GUO.ModApi`
   (`GUO.ModApi.csproj`, with `Private="false"` so the client's copy is used).
   That assembly is everything a mod sees of GUO.
2. Write a public class with a parameterless constructor that implements
   `IGuoMod`. In `Register`, call `registrar.AddPacket` once per packet:

   ```csharp
   public void Register(IModRegistrar registrar) =>
       registrar.AddPacket(new PacketRegistration { Id = 0xF9, Length = -1, Handler = OnAnnounce });
   ```

   `Register` runs once at start. Do nothing else there: no threads, files or
   sockets.
3. The handler gets the whole packet, id and length bytes included, on the
   game thread. If it throws, that one packet is dropped and the log says so;
   the game carries on.
4. Write `mod.json` next to the assembly. Every packet you register must be
   declared there, with the same length and override; anything undeclared is
   refused. `shards` lists your login host or shard name (or `"*"`). The
   fields are in `docs/data_formats.md`, section 42.

A registration can carry `MinClientVersion` / `MaxClientVersion` (encode a
version with `ModAbi.Encode(7, 0, 15, 1)`) when a packet differs between
client versions.

## Test it

Unit-test your handler by calling it with bytes, the way
`tools/network_tests` does for the sample:

```bat
dotnet run --project tools\network_tests
```

Then try it in the client:

1. Build your mod, and copy the `.dll` and `mod.json` into
   `Data\Mods\<your-mod>\` beside the client.
2. Options > **Load mods on the next start**, then restart. The line under
   the box says how many mods loaded and how many were refused.
3. A refused mod's reason is in the client log, as `[mods] refused ...`: an
   undeclared packet, a packet another mod already has, a typo in `mod.json`,
   the wrong ABI.
4. For one run without changing your settings, start the client with
   `--load-mods`, and `--mods-dir FOLDER` to load from another folder.

Your shard then has to send the packet. Send it only to players who have the
mod: a client without it misreads the stream.

## Ship it

Two ways, both installed by the player:

- **A folder.** Give players the `.dll` and `mod.json` to put in
  `Data\Mods\<your-mod>\`. Simple, but unsigned: they have to trust where the
  files came from.
- **A Store pack.** Publish the mod as a `mod` component in a content pack
  (the contract is in `docs/asset_pack_ecosystem.md`, Mod components). The
  pack is signed with your publisher key (`tools/asset_store`), lists the
  packets it touches, and installing it runs nothing. The player sees your key
  fingerprint, the packets and the shards in the Store, and approves the mod;
  it loads on the next start with Load mods on. An update or a new key asks
  again. Publish your key fingerprint where your players can check it. Store
  mod packs need the owner's decision first (ADR-0038's amendment), so this
  route may not be open yet.

## A mod made the game slow

Measure it with the perf lab (`launchers\dev\perf_lab.bat`, on the
Performance page once it is published), once without the mod and once with
it, on the same machine at the same size:

```bat
launchers\dev\perf_lab.bat --label without
launchers\dev\perf_lab.bat --label with --args "--load-mods --mods-dir C:\path\to\mods"
```

The lab plays on the local dev shard, which does not send your shard's
packets, so it shows the cost of having the mod loaded, not of handling your
traffic. If the drop only happens on your shard, say so, and say which packet
the mod handles and how often your shard sends it.

Report it with both report files the lab names on its last line (`share
this file`), the mod's name and version, and its `mod.json`. They carry no
paths or names. Post it where you report other GUO problems
([Community and Support](Community-and-Support.md)).

## See also

- [Asset Store](Asset-Store.md): installing and approving packs.
- [Server Compatibility](Server-Compatibility.md): which shards GUO plays.
- [Scripted Runs and Probes](Scripted-Runs-and-Probes.md): `--load-mods`,
  `--mods-dir` and `--mods-probe`.
