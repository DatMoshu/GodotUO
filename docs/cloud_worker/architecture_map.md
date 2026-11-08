# Architecture and subsystem map

Read `docs/port_plan.md` for why the port is a transplant, and
`docs/architecture/README.md` for the ADR index. This page is the map a
cloud worker can use without re-deriving that history. Counts below are
either cited or marked as this review's file census.

## Languages and engine

| Piece | What it is on this baseline |
|---|---|
| Engine | Godot 4.7.2 stable, mono/.NET. `godot/GUO/GUO.csproj` sets `Godot.NET.Sdk/4.7.2` |
| Client | C#. Desktop `TargetFramework` is `net8.0`. Android and web conditions use `net9.0`. `LangVersion` is `latest` |
| Shaders | Godot shader includes under `src/Render`, plus embedded `*.glsl.txt` for speed mode and Depth World |
| Tools | Python 3.12 (`tools/guo` and one folder per job) |
| GDScript | 6 files: five probes in `godot/GUO/dev/` and `addons/guo_posture/plugin.gd`. The game is not written in GDScript |
| Upstream | ClassicUO at `docs/upstream/UPSTREAM_PIN.json`, commit `007ef8c3e13dec13fcc02387fe4817ef6f371c85`. Not vendored |

File census on the review VM: 1210 `*.cs` under `godot/GUO`, 59 shader
sources, 359 Python files under `tools/`. Census only; not an audit tier.

`docs/port_status.md` (generated 2026-10-28) says the ported tree is
396/396 upstream files. `docs/port_plan.md` and the wiki still quote the
older 247/116/37 tier table from 2026-09-23. Use `port_status.md` when a
number matters, and treat it as stale until someone re-runs the audit
with `sources/ClassicUO` present. This review did not.

## Subsystems

| Area | Owns | Start here |
|---|---|---|
| Compat | XNA value types only. Not textures, not a device | `godot/GUO/src/Compat/`, `src/Platform/Sdl/` |
| IO and assets | `.mul` / `.uop` readers and loaders. The install stays outside the repo | `src/IO/`, `src/Assets/UOFileManager.cs` |
| Canvas world | Ordered 2D batcher, the classic path and the fallback | `src/Render/UltimaBatcher2D.cs` |
| Depth World | RenderingDevice world using ClassicUO depth rules (ADR-0044, accepted) | `src/Render/DepthWorld/` |
| Speed mode | Opt-in screen-space lighting on the Depth World (ADR-0048, Proposed, default off) | `src/Render/SpeedMode/`, `Profile.LightingSpeedMode` |
| Network | TCP or WebSocket, encryption, Huffman, packet table, plugin host | `src/Network/NetClient.cs`, `PacketHandlers.cs`, `Encryption/` |
| Game | Ported scenes, world, gumps | `src/Client/GameController.cs`, `src/Game/` |
| Input | Godot events shaped like the SDL events the port expects; touch is separate | `src/Input/GodotInput.cs`, `src/Input/Touch/` |
| Configuration | Profiles and platform defaults. Workspace files live under `UO_WORKSPACE_DIR`, not in git | `src/Configuration/Profile.cs`, `tools/guo/config.py` |
| Labs | Experimental client code. `GuoLabs=true` and Debug, otherwise not compiled, never an export | `src/Labs/`, `GUO.csproj` |
| Editor | `#if TOOLS` add-on. AI features go through `AiFeatures` | `addons/guo_editor/` |
| Shard | Local ModernUO. DFI patches default on for that dev shard (`UO_SHARD_DFI=1`) and are server content | `tools/modernuo/`, `launchers/shard/` |
| Store and mods | Packs and the mod assembly. No executable code installed as a pack | `src/Store/`, `godot/GUO.ModApi/` |

## Contracts that bind a change

1. **Off means classic.** `LightingSpeedMode` defaults to false and the
   comment says no GPU resources are created while it is disabled
   (`Profile.cs`). `SpeedModeDebug.Active` is request and Depth World
   support. Sub-flags such as reflections default true and do nothing
   until the parent is on.
2. **Reflection height.** `speed_reflection` admits a tap when
   `height` is within `0.125` of `distance_art / 4`. That is the current
   shader. ADR-0048's 31 October note matches it. This review did not
   render it.
3. **Daylight.** `CloudLighting.FromMinute` is `0.1 + 0.9 * clamp(sin((minute-360)*pi/720), 0, 1)`.
   A valid shard clock wins over the brightness approximation. A Labs
   test clock exists only inside `GUO_LABS`.
4. **Sun cover.** Upright static stamps use realtime `CoverZ` and
   `TryKeepAbove`. Hidden roofs are not a licence for an indoor static
   to stamp. The per-pixel inverse mask is still described as unwired
   in `ReceiverContract.md`.
5. **Nearest filtering.** `default_texture_filter=0`. A new viewport or
   material keeps nearest-neighbour sampling unless an ADR says otherwise.
   None does.
6. **Config order, launchers.** Environment, then `config.local`, then
   `config.bat` / `config.sh`. `common.sh` also sources `UO_COMMON_CONFIG`
   first when it is set. `tools/guo/config.py` documents the first three
   and does not read `UO_COMMON_CONFIG`. See finding F6.
7. **Privacy.** No client data, no machine paths, no shard passwords in
   git. CI's guard job runs `tools/privacy_scan`. Generated fact output
   has to stay in the same boundary.
8. **Layers.** L0 port parity, L1 player options off by default, L2 packs,
   L3 editor-only, L4 Labs. A new chat command or profile field starts
   as a discussion, not as a drive-by edit.

## ADR index caution

The index in `docs/architecture/README.md` is how numbers are reserved.
It is not a complete list of files in that folder. On this baseline the
table skips 0039, 0042, 0043 and 0045 even though those files exist.
ADR-0045's header says Accepted (2026-10-27). ADR-0039 and ADR-0042 still
say Proposed. ADR-0007's original batching proposal remains "recommend
reject"; the 2026-10-22 acceptance in that file is the depth-buffer
amendment, not a reversal of the measurement. Do not "fix" that by
marking the original proposal Accepted.

The wiki Architecture table stops at ADR-0017 and points at the index for
the rest. Later decisions are real; the wiki table is a short tour.
