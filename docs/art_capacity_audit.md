# Art capacity audit and compatibility matrix

Source audit: 2026-10-28, GUO base `6bcc3d6f`. Layer: L4 contract experiment.
Status: implementation design, **not expanded-art runtime support**. The
[Proposed ADR](architecture/ADR-0047-bounded-art-capacity.md) describes the candidate.
Only public source and the current checkout support the findings below. No private
implementation is incorporated. No real assets or private-server changes were tested.

## Client path, current checkout

Paths below are relative to `godot/GUO/src/`; line numbers refer to the base above.

| Boundary | Source evidence | Consequence for wider item art |
|---|---|---|
| Namespace and art loading | `Assets/ArtLoader.cs:16`, `:17`, `:113`, `:118`, `:427`, `:463` | Land is 0x4000. The misleadingly named static constant 0x14000 is used as a combined boundary; loose static gathering subtracts land, giving 65536 slots. GetArt takes uint but eventually converts to int for archive lookup. A wide API alone is insufficient. |
| Art indices | `IO/UOFileMul.cs:25`, `IO/UOFile.cs:22` | MUL slots derive from 12-byte index entries; valid-reference lookup bounds-checks the int index. Extent/overflow validation must precede conversion and pointer reads. Preserve absent positions. |
| Sparse UOP addressing | `IO/UOFileUop.cs:143` | Candidate numeric filenames are enumerated across `max(total,65535)+0x4000`, where total is a stored-count input. An isolated high logical ID can be outside this range. Declared logical capacity must drive bounded enumeration or a verified explicit index. |
| Tiledata | `Assets/TileDataLoader.cs:37`, `:55`, `:58`, `:67` | Chooses old/new metadata by client version; static groups derive from remaining file length. IDs are positional. High Seas groups are 4+32*41 bytes. Animation/hue fields remain ushort, height byte; this is separate from item art width. Reject trailing partial groups and over-capacity counts in a new loader. |
| Statics and patches | `Assets/MapLoader.cs:315`, `:543`, `:643`; `Game/Map/Chunk.cs:229`, `:245` | 12-byte static indices, packed 7-byte statics: ushort Color (art), byte X/Y, sbyte Z, ushort hue. Count/address calculation, map/stadif patch selection, pooled reads and object construction all share this assumption. Art-only becomes 9 bytes; 10 bytes also widens Z. |
| Multis | `Assets/MultiLoader.cs:83`, `:127`, `:139`, `:149`; `Game/GameObjects/Item.cs:269` | MUL version selects 12/16-byte stride, UOP uses its own header/count/attributes. Component IDs and MultiInfo.ID are ushort; coordinates/Z already short. World construction narrows summed Z to sbyte. Art-only 14/18-byte MUL records do not solve world Z. UOP needs a distinct contract. |
| Object and hold types | `Game/GameObjects/GameObject.cs:71`, `:79`; `Item.cs:66`, `:73`, `:106`, `:151`; `Static.cs:28`, `:40`; `Multi.cs:28`, `:45`; `Game/ItemHold.cs:25` | Graphic, displayed/original graphics, constructors, drag state and metadata lookup remain narrow. The shared GameObject also represents mobile bodies: split namespaces before widening, rather than widening every body/animation reference. |
| Remaps and filters | `Game/Data/StaticFilters.cs:26`, `:28`, `:161`; `Game/Managers/SeasonManager.cs:17`, `:39` | Capacity-sized arrays coexist with ushort lists, parsers and replacements. Widen keys/outputs selectively and validate metadata availability. A bigger filter array does not prove more art works. |
| Sprite/HD/bounds caches | `Render/Arts/Art.cs:47`, `:79`, `:82`, `:93`, `:102`, `:166`, `:214`, `:276`, `:411` | Native sprite/bounds arrays derive from archive entries; uint GetArt/GetRealArtBounds coexist with ushort HD cache keys and refresh APIs. Cache epochs must cover manifest, reload, bounds, HD failure entries, wall-face/light invalidation and picking masks. |
| Atlas and picking | `Render/TextureAtlas.cs:61`, `:106`; `Render/Arts/Art.cs:234`, `:254`, `:416`; `Render/PixelPicker.cs:10`; `Game/GameObjects/GameObject.cs:261` | Atlas accepts pixels/dimensions, not an item-art namespace. PixelPicker already has ulong keys, but callers may have narrowed before lookup. Exercise a high ID through the actual draw and hit-test callers; the synthetic cache does not prove GPU or picking correctness. Preserve nearest sampling and draw depth. |

Masks are contextual. `Game/Map/Chunk.cs:209` masks terrain to 0x3FFF;
`Render/Arts/Art.cs:79` clears the land/static marker in land lookup. Packet
serial/coordinate, hue and animation masks have different meanings. Do not remove
these globally. Validate expanded static IDs against both declared capacity and
actual art/metadata presence; 262144 slots is not 262144 populated assets.

## Game packet inventory

`Network/PacketHandlers.cs` and `Network/OutgoingPackets.cs` are referenced as
PH and OP. `Network/PacketsTable.cs` (PT) supplies framing before dispatch.
None of these production files changes in this experiment. The proposed local
GAF1 frame is **not** one of these packets and assigns no opcode.

| Family | Current source boundary | Work before live extension |
|---|---|---|
| 0x25 contained update | PH:1600, :1608; PT:315 | ushort graphic + unsigned offset byte; conditional grid. Existing totals 20/21. Widening only graphic adds 2 bytes; removing offsets or adding an envelope changes that delta. |
| 0x3C contents | PH:2129, :2141, :2158 | ushort count; each 19/20-byte entry has ushort+offset, optional grid. First entry clears container. Whole-snapshot validation and explicit chunk semantics required; repeated snapshots cannot simply be concatenated. |
| 0x2E equipment | PH:1847, :1871; PT:58 | ushort art + signed offset, layer, owner and hue. Legacy length 15. Art-only field widening with offset retained would be 17. |
| 0x78 mobile/equipment | PH:2878, :2954, :2959 | Initial graphic is a mobile body. Equipment serial zero ends list; equipment art ushort. Before 7.0.33.1 art bit 0x8000 signals optional hue and is stripped; later hue is unconditional. New equipment encoding needs explicit flags, exact terminator and full-message bounds. |
| 0xF3, nested 0xF7 | PH:5756, :5766, :5772, :5976; PT:365 | Type discriminator plus ushort art, increment byte, sbyte Z, version-dependent trailer. F7 dispatches nested SA records. High Seas length 26, earlier 24. Art-only widening is separate from signed-short Z and variant/trailer changes. |
| 0x6C targeting | OP:1686, :1734, :1781; PT:120 | Current 19-byte target reply ends in ushort graphic; XYZ uses signed-short Z on wire already. APIs still take ushort art. Validate namespace/location on authoritative server, rather than trusting a supplied art ID. |
| 0x70/0xC0/0xC7 effects | PH:2486, :2504, :2528; PT:124, :204, :211 | Main graphic ushort, position Z sbyte. Particle auxiliary tile/explosion/sound IDs also ushort but are distinct namespaces. Current lengths 28/36/49; widening art alone must not import packet sizes that also widen Z or other fields. |
| 0x23 drag | PH:1255; PT:47 | ushort graphic + increment; two sbyte Z values. Current 26 bytes. Art-only is +2; widening both Z values is another +2. |
| 0x9E vendor sell | PH:3394, :3422 | ushort art per entry; variable names/prices/counts. Version and bound entire list and per-recipient cached packets. |
| 0x7C menu | PH:3007, :3030 | ushort item graphics and hue; length-limited text. Preserve unrelated menu/body semantics. |
| 0xBF spellbook | PH:4564; OP:1198 | Subcommand 0x1B content supplies ushort spellbook graphic. Do not widen all BF payloads. |
| 0x1A world item | PH:815 (UpdateItem), ModernUO OutgoingItemPackets.cs:32 | Graphic/flag overlays and legacy masks require an explicit new kind/flag encoding. Include this path even if F3 works. |
| 0xD8/D7 houses | PH:5310; OP:4112, :4151, :4192 | D8 planes/compression use their own art strides and packed lengths. D7 art arguments are ushort APIs written into uint32 fields already. Reversion must preserve codec limits; do not widen an already-wide wire field. |

No legacy client-version threshold proves support for expanded art. Both peers,
their fixed/variable length tables, compressed/nested readers, broadcasts, plugin
interceptors and per-recipient caches must agree before a new layout is sent.
The offline frame test proves its own complete-buffer validation, not TCP
reassembly, ClassicUO handler safety, container atomicity or any live writer.

## Public compatibility matrix

Classifications: **requires changes** means a concrete inspected blocker;
**partial** means one path already handles a larger count, not complete support;
**unproven** means no matching source/runtime proof. Revisions are snapshots,
not claims about the latest upstream release. Links are the exact files inspected.

| Participant / inspected revision | Art and metadata | Wire, editing and Z | Classification and required work |
|---|---|---|---|
| GUO / `6bcc3d6f` | uint low-level lookup; narrow object/remap/HD paths above | Legacy game framing; sbyte world Z | **Requires changes.** Full item namespace migration and supported file profile, all enabled packet families, cache/pick proof. This branch adds only offline contracts. |
| ClassicUO / local upstream pin in `docs/upstream/UPSTREAM_PIN.json` | `src/ClassicUO.Assets/ArtLoader.cs:16`, `TileDataLoader.cs:55`, `MultiLoader.cs:127`; `src/ClassicUO.Client/Game/GameObjects/GameObject.cs:71` | Same narrow base model, separate body/art semantics | **Requires changes.** Inspected local upstream loaders/object fields; deployment support and custom forks **unproven**. Do not advertise vanilla interoperability. |
| ModernUO / `5570537aa0416a350b7ace08e7e45adf6481fb48` | [TileData.cs:289](https://github.com/modernuo/ModernUO/blob/5570537aa0416a350b7ace08e7e45adf6481fb48/Projects/Server/TileData.cs#L289) fixes item table at 0x10000; item length selected by version-size thresholds at :317. [TileMatrix.cs:377](https://github.com/modernuo/ModernUO/blob/5570537aa0416a350b7ace08e7e45adf6481fb48/Projects/Server/TileMatrix/TileMatrix.cs#L377) divides by 7, :568 stores ushort ID/sbyte Z. | [OutgoingEntityPackets.cs:109](https://github.com/modernuo/ModernUO/blob/5570537aa0416a350b7ace08e7e45adf6481fb48/Projects/Server/Network/Packets/OutgoingEntityPackets.cs#L109) masks graphics, :132 writes ushort; [OutgoingContainerPackets.cs:155](https://github.com/modernuo/ModernUO/blob/5570537aa0416a350b7ace08e7e45adf6481fb48/Projects/Server/Network/Packets/OutgoingContainerPackets.cs#L155), :211 narrow item graphics. | **Requires changes.** Table, statics, outgoing framing/per-session caches, incoming validation and persistence audit. Wider art does not imply wider world Z. Expanded save/restart and all uninspected server paths **unproven**. |
| ServUO pub57 / `d76bf4443cf76d081ddaf8f57c87ff33749256af` | [Server/TileData.cs:224](https://github.com/ServUO/ServUO/blob/d76bf4443cf76d081ddaf8f57c87ff33749256af/Server/TileData.cs#L224) allocates 0x10000; older branches :267/:292 smaller. | [Packets.cs:231](https://github.com/ServUO/ServUO/blob/d76bf4443cf76d081ddaf8f57c87ff33749256af/Server/Network/Packets.cs#L231), :247/:1176 write short art, :1197 masks 0x3FFF, :4167 masks 0x7FFF. [PacketHandlers.cs:90](https://github.com/ServUO/ServUO/blob/d76bf4443cf76d081ddaf8f57c87ff33749256af/Server/Network/PacketHandlers.cs#L90) registers target length 19. | **Requires changes.** Metadata, enabled writers/targets/housing and per-peer negotiation; statics/multi persistence wider layouts **unproven** in this inspection. Signed int server APIs must reject negative/out-of-profile values. |
| UOFiddler / `610c930833dac8d158f08a0af36da43dd90d08b4` | [Ultima/Art.cs:15](https://github.com/polserver/UOFiddler/blob/610c930833dac8d158f08a0af36da43dd90d08b4/Ultima/Art.cs#L15), :47/:129/:147 combine 0x14000 setup, narrow removed tracking, 0xFFDC maximum and ushort legal-ID helper. [TileData.cs:1361](https://github.com/polserver/UOFiddler/blob/610c930833dac8d158f08a0af36da43dd90d08b4/Ultima/TileData.cs#L1361) derives positional rows. | [Multis.cs:411](https://github.com/polserver/UOFiddler/blob/610c930833dac8d158f08a0af36da43dd90d08b4/Ultima/Multis.cs#L411) reads ushort graphics; [RadarCol.cs:45](https://github.com/polserver/UOFiddler/blob/610c930833dac8d158f08a0af36da43dd90d08b4/Ultima/RadarCol.cs#L45) derives color count (ushort color values stay narrow). | Metadata **partial**, art/tool chain **requires changes**: selectors, caches, modified/removed state, saves/imports, sparse high slots and explicit multi codec. High-ID save/reopen **unproven**. |
| CentrED# / `3a97e74a9bd277553eff8af7b83ad9e4f7c0967f` | [Shared/BaseTile.cs:10](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Shared/BaseTile.cs#L10) ushort; [StaticBlock.cs:28](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Shared/StaticBlock.cs#L28) divides by 7; [TileDataProvider.cs:11](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Shared/TileDataProvider.cs#L11) infers layout by extent, :33 derives static rows. | [StaticInfo.cs:7](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Shared/Network/StaticInfo.cs#L7) narrow operation descriptor; [server handlers:14](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Server/Map/ServerLandscapePacketHandlers.cs#L14) and [client packets:95](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Client/Packets.cs#L95) fix edit widths; [bulk edits:90](https://github.com/kaczy93/centredsharp/blob/3a97e74a9bd277553eff8af7b83ad9e4f7c0967f/Server/Map/LargeScaleOperations.cs#L90) subtract 0x4000 and cast ushort. | Metadata **partial**, both editor peers/shared models **require changes**. Art-only static records 7→9, operation descriptor 9→11; insert/delete 10→12, elevate 11→13, move 14→16, hue 12→14 only when ID is sole widened field. History/block/bulk round trips **unproven**. |

These are source classifications, not executed compatibility tests. No listed
participant has qualified end-to-end expanded support here. ModernUO/ServUO
model/persistence APIs and other tool plugins need follow-up beyond the cited
blockers. A server-side int alone does not establish safe 32-bit coverage.

## Proof boundary and remaining acceptance

The synthetic contract covers 65535/65536, 262143/262144, overflow/negative inputs,
invalid extents/counts/offsets, explicit 9/14/18-byte selection, positional metadata,
sparse indices, equipment terminators/flags, manifest/version/capacity mismatch,
and bounded CPU cache eviction/invalidation. It preserves sbyte static Z and
short multi Z. A 10-byte static variant is deliberately rejected, not inferred.

Production high-ID rendering, picking, cache invalidation, UOP decoding, world
collision, targets, equipment/container/effect/housing messages, editor exports,
save migration and an isolated client/server round trip remain **unproven**.
They must pass before acceptance or integration into live loaders/packet handlers.
An unqualified profile must fail before world entry, without installing content
or substituting arbitrary IDs. Raw UO assets are never test fixtures.
