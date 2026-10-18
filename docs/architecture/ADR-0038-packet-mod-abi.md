# ADR-0038: Packet mods and the mod ABI

Status: Proposed (MOD-02, 2026-10-18). Needs the owner's decision: it adds a
settings field, an Options entry and two seams in ported files.

## Context

Shards with custom server packages (POL, ServUO and RunUO forks) send packets
ClassicUO does not know. Today their owners fork ClassicUO: a handler in
`PacketHandlers.cs`, a length in `PacketsTable.cs`. The modding survey
(`docs/modding/surface_2026-10-18.md`) shows why the plugin ABI cannot do it:
plugins see packets only after the client has framed them by its own length
table, so an unknown id desynchronises the stream before any plugin runs, and
plugins are Windows-only. A shard owner who wants to port their mod to GUO
needs a way to add a packet without forking `src/Network`.

ClassicUO would not ship this; it is a GUO client option (layer L1), off by
default, with the parity proof below.

## Decision

1. **The ABI is one small assembly, `GUO.ModApi`** (`godot/GUO.ModApi`,
   net8.0, no engine types). A mod references it and nothing else from GUO.
   It holds `IGuoMod` (the entry point), `IModRegistrar` (`AddPacket`),
   `PacketRegistration`, the `PacketHandler` delegate and `IModContext`, the
   only things a handler may do in the client: print a system message, send a
   packet through the normal send path, write to the log. `ModAbi.Version` is
   1. A wider context is a new ABI version, never a reach into the client.
2. **A mod is a folder the user installed**: `Data/Mods/<mod>/mod.json`
   (schema `guo/mod@1`, `docs/data_formats.md` section 41) and the mod's
   assembly. The client never fetches a mod, and a shard cannot push one.
   MOD-03 adds the same mod as a Store component; this ADR does not.
3. **Everything a mod touches is declared.** `mod.json` lists each packet id
   (or 0xBF subcommand), its length, and whether it overrides a packet the
   client already handles. A registration that is not declared, that differs
   from its declaration, that replaces a client handler without `override`,
   or that another mod already holds, refuses the whole mod. Unknown fields in
   `mod.json` refuse it too.
4. **Guards.** Each registration may carry a client-version range; outside it
   the registration is skipped. Each mod lists the shards it applies to
   (`shards`: login hosts or shard names, or `"*"`); on any other shard its
   packets are inactive and the client parses as if it were not installed.
   The shard is read in one adapter (`ClientMods.Shard`); NET-02's
   `ServerIdentity.Current` (`Host`, `ShardName`) replaces its two lines
   when it lands.
5. **Two seams, both marked `PORT DEVIATION (GUO)`.**
   `PacketsTable.GetPacketLength` asks the registry for a mod's length, and
   `PacketHandlers.AnalyzePacket` offers a framed packet to the registry before
   the client's handler table. Plugins still see every packet first, as they
   do today. A mod handler's exception drops that one packet and is logged.
   `PacketHandlers.HasHandler` lets the registry tell a new id from an
   override; `GameController` calls the loader once, after the plugins.
6. **The option.** `load_mods` in `settings.json`, false by default and for
   existing files, read at start. It lives beside `plugins` rather than in the
   profile because packets arrive before any profile is loaded. Options shows
   **Load mods on the next start** and one status line (what loaded, what was
   refused). Android and web builds do not offer it: they do not load
   assemblies from the user's files.
7. **Loading.** One `AssemblyLoadContext` per mod, the assembly read from a
   stream (the file is not held open), `GUO.ModApi` always resolved to the
   client's copy so the types match. A mod that fails at any step is skipped
   whole; nothing it registered is kept.

## Trust

A mod is code running in the client with the user's rights. Capability grants
like the script components' (`ScriptPackSession`) do not sandbox a managed
assembly, and this ADR does not pretend they do. The controls are: nothing
loads unless the user turned the option on, mods are only files the user
installed, every packet a mod touches is declared and enforced at
registration, and its packets apply only on the shards it names. MOD-03 adds
signing and the Store's review before approval. Users should install a mod
only from a shard they trust, as with an assistant plugin today.

## Consequences

- A shard's custom packet is a small mod instead of a client fork, and it
  keeps working across GUO updates as long as the ABI version holds.
- Off is upstream: with `load_mods` false the registry is null, both seams
  are a single null check, and parsing is ClassicUO's byte for byte.
- Custom gumps and UI driven by a packet still need a fork; a later ABI
  version may add a gump call to `IModContext`.
- The mods folder sits beside `Data/Plugins`, so the same install
  instructions cover both.

## Proof

- `dotnet run --project tools/network_tests`: the parse loop with both seams,
  the sample mod loaded from its DLL, off-state framing, the shard and
  client-version guards, 0xBF subcommands, every refusal and strict
  `mod.json` (33 checks).
- `godot/GUO/src/Network/Mods/ModsProbe.tscn` (smoke line, scenario
  `client.mods.probe`): inside the Godot process, the sample loads through
  the client's loader, a packet stream goes through the real `PacketsTable`
  and `PacketHandlers`, off and on, and the packet after the mod's reaches the
  client's handler intact.
- Not proven: a live shard sending the packet. The sample's README shows what
  a shard sends.

## Amendment: Mod packs, an exception to the L2 no-code rule

Status: Proposed (MOD-03, 2026-10-18). Needs the owner's explicit decision,
separately from the decision above: AGENTS.md says L2 packs carry typed Store
components only, with no executable code. A `mod` component is executable
code with the user's full rights. This amendment asks for one narrow
exception; without it, MOD-03 does not merge and mods stay folder-only.

**What is allowed.** One component type, `mod`, may carry one managed .NET
assembly built against `GUO.ModApi`, plus that assembly's own dependencies,
in a v2 pack. Nothing else in any pack may be a `.dll`, and no other
component type gains code.

**Why.** Shard owners with custom server packages need their players to have
the matching client mod. Without a Store kind they hand players a DLL to drop
into a folder, with no signature, no declared surface and no review. The
Store route is the safer of the two, not a new risk.

**Safeguards**, all required (`docs/asset_pack_ecosystem.md`, Mod
components):

- installing runs nothing; a mod loads only at start, with **Load mods** on
  (default off) and an approval the user gave;
- every mod component is signed by its publisher (Ed25519), and approval binds
  the key and the content hash, so an update or a new key asks again;
- the component declares every packet it touches; the installer and the
  loader both refuse anything undeclared, and GUO-reserved packets are
  refused outright;
- the client never fetches a mod by itself: not on login, not through shard
  content negotiation, not as an unrequested dependency;
- refused on Android and web, which load no mods;
- the review says plainly that a mod is code with the user's rights.

**What stays forbidden.** Code in any other component type or pack kind;
executing anything during installation; a shard pushing, installing or
enabling a mod; native code (`.so`, native `.dll` exports, `.exe`) in a mod;
mods in exported Android or web builds; loading a mod that was not approved
in this installation.

## ADR Dependencies

ADR-0019 (Store), ADR-0026 (shard content descriptors). Related: MOD-03
(`mod` Store component), NET-02 (protocol version handling).

## Engine Compatibility

Godot 4.7.2 .NET. Desktop (Windows, Linux) only; Android and web do not load
mods.
