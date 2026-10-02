# Glossary

| Term | Meaning |
|---|---|
| **ADR** | Architecture Decision Record, in `docs/architecture/`. A binding decision with its reasons. ADR-0001 governs the renderer. |
| **Audit** | `tools/port_audit`: measures which upstream files are present, by tier. Port progress is quoted from it, never estimated. |
| **ClassicUO (CUO)** | The open-source C# UO client GUO is ported from (BSD 2-Clause). Kept read-only under `sources/`. |
| **Client data** | Your UO install's `.uop`, `.mul` and `.idx` files. Proprietary; GUO reads them in place and never writes or ships them. |
| **Compat** | `GUO.Compat`: XNA value types (vectors, rectangles, colours) so shim files compile without FNA. Value types only, never engine behaviour. |
| **Dev shard** | A local ModernUO server for testing (`launchers\shard\`). |
| **Facet** | One of UO's maps: Felucca, Trammel, Ilshenar, Malas, Tokuno, Ter Mur. |
| **FNA** | The XNA reimplementation ClassicUO renders with. GUO exists to get off it. |
| **Gump** | A UO window or dialog: paperdoll, backpack, journal, options. |
| **Hue** | UO's palette remap for art: one item art, many colours. |
| **Land / statics** | The terrain tiles (stretched over corner heights), and the fixed objects placed on the map. |
| **Mobile** | A creature or character: players, NPCs, monsters. |
| **Multi** | A group of components placed as one object: houses, boats, castles. See [Building Multis](Building-Multis.md). |
| **Parity** | Behaving and looking identical to ClassicUO. Parity first, improvements after, and proposed separately. |
| **Plugin / assistant** | A third-party helper such as Razor, loaded through the plugin host (Windows). |
| **Probe** | A scripted client run that checks one thing and exits: `--touch-probe`, `--gamepad-probe` and so on ([Scripted Runs and Probes](Scripted-Runs-and-Probes.md)). |
| **`PORT DEVIATION (GUO)`** | The comment marking every place GUO's code intentionally differs from upstream's. |
| **Shard** | A UO server. ModernUO, ServUO and RunUO are server emulators. |
| **Stage / staged data set** | New UO data written beside the install and loaded on top of it, never into it (ADR-0022). |
| **Tiers** | How each upstream file is ported. `verbatim`: renamespaced only. `shim`: renamespaced plus compatibility imports. `rewrite`: reimplemented on Godot. |
| **Tiledata** | The client file describing every tile's flags (wall, surface, impassable) and height. |
| **Upstream** | ClassicUO, at the commit pinned in `docs/upstream/`. |
| **Z** | Height. The client's range is −128 to +127. |
