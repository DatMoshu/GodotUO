// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: reads the effective native map, plus current shard items/multis.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using N3 = System.Numerics.Vector3;
using System.Runtime.InteropServices;
using GUO.Assets;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Renderer.Lights;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>
/// A bounded, synchronous native producer. Call on the game thread: FileReader cursors,
/// item collections and art loaders are shared with native drawing. Nothing is selected
/// by AllowedToDraw, roof cutaway, draw lists or camera depth. No generated GLB is read.
/// Every capture owns its output; there are no live object references in the snapshot.
/// </summary>
internal static class NativePhysicalWorldProducer
{
    internal const int MaxSourceRecords = 131072;

    public static PhysicalWorldSnapshot Build(World world, PhysicalSnapshotKey key, PhysicalRect bounds,
        bool enumerationIncomplete = false)
        => new Capture(world, key, bounds, enumerationIncomplete).Run();

    // GetChunk(x,y,false) still reloads a destroyed chunk and touches its access
    // time. This pure table lookup neither streams source data nor pins pages.
    internal static void FillLoadedCoverage(World world, PhysicalRect bounds, Span<byte> loaded)
    {
        loaded = loaded[..checked(bounds.Width * bounds.Height)];
        loaded.Clear();
        if (world?.Map == null || world.Player == null) return;
        var sizes = Client.Game.UO.FileManager.Maps.MapBlocksSize;
        int width = sizes[world.Map.Index, 0], height = sizes[world.Map.Index, 1];
        int firstX = Math.Max(0, bounds.X >> 3), firstY = Math.Max(0, bounds.Y >> 3);
        int lastX = Math.Min(width - 1, (bounds.Right - 1) >> 3);
        int lastY = Math.Min(height - 1, (bounds.Bottom - 1) >> 3);
        for (int bx = firstX; bx <= lastX; bx++)
        for (int by = firstY; by <= lastY; by++)
        {
            var chunk = world.Map.GetChunk(bx * height + by);
            if (chunk == null || chunk.IsDestroyed) continue;
            int left = Math.Max(bounds.X, bx * 8), right = Math.Min(bounds.Right, bx * 8 + 8);
            int top = Math.Max(bounds.Y, by * 8), bottom = Math.Min(bounds.Bottom, by * 8 + 8);
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (Math.Abs(x - world.Player.X) <= world.ClientViewRange
                    && Math.Abs(y - world.Player.Y) <= world.ClientViewRange)
                    loaded[(y - bounds.Y) * bounds.Width + x - bounds.X] = 1;
        }
    }

    // NoShoot alone is not an opaque-volume recipe, but it is evidence that
    // silently excluding the object cannot certify an empty structural column.
    // Native 0x0648..0x064A tent roofs are real examples without the Roof flag.
    internal static bool RequiresStructuralClassification(TileFlag flags)
        => (flags & (TileFlag.Roof | TileFlag.Wall | TileFlag.Door | TileFlag.Window
            | TileFlag.Surface | TileFlag.Impassable | TileFlag.Bridge | TileFlag.NoShoot)) != 0;

    private readonly record struct Source(int X, int Y, int Z, ushort Graphic,
        PhysicalOwnerKey Owner, StaticTiles Tile, bool Moving = false);
    private readonly record struct Ground(ushort Graphic, int Z);

    private sealed class Capture
    {
        private readonly World _world;
        private readonly PhysicalSnapshotKey _key;
        private readonly PhysicalRect _bounds;
        private readonly PhysicalWorldBuilder _builder;
        private readonly Dictionary<(int, int), Ground> _ground = new();
        private readonly List<Source> _sources = new();
        private readonly Dictionary<(int, int), List<Source>> _roofs = new();
        private readonly Dictionary<ushort, PhysicalArtRecipe> _art = new();
        private ulong _fingerprint = 14695981039346656037UL;
        private int _blocks, _statics, _items, _multis, _excluded, _unsupported;
        private int _visitedRecords;
        private bool _recordLimit;

        public Capture(World world, PhysicalSnapshotKey key, PhysicalRect bounds, bool enumerationIncomplete)
        {
            _world = world;
            _key = key;
            _bounds = bounds;
            _builder = new PhysicalWorldBuilder(key, bounds);
            _recordLimit = enumerationIncomplete;
        }

        public PhysicalWorldSnapshot Run()
        {
            var timer = Stopwatch.StartNew();
            if (_recordLimit)
            {
                ForCells((x, y) => _builder.MarkUnknown(x, y, PhysicalUnknownReason.EnumerationLimit));
                return _builder.Seal();
            }
            if (_world?.Map == null || Client.Game?.UO?.FileManager == null || _world.Map.Index != _key.Facet)
                return _builder.Seal();
            Hash((ulong)_key.Facet);
            Hash(1); // Recipe/schema version; units remain native tile/tile/Z.
            ReadNativeBlocks();
            if (!_recordLimit) ReadLiveOverlay();
            // A truncated set cannot certify roof-neighbour uniqueness, either.
            // Publish explicit Unknown without inferred positive blocker geometry.
            if (!_recordLimit)
            {
                EmitTerrain();
                foreach (Source source in _sources)
                    if (_bounds.Contains(source.X, source.Y)) EmitStructure(source);
            }
            if (_recordLimit)
                ForCells((x, y) => _builder.MarkUnknown(x, y, PhysicalUnknownReason.EnumerationLimit));
            timer.Stop();
            return _builder.Seal(_fingerprint, new PhysicalWorldDiagnostics(_blocks, _statics,
                _items, _multis, _excluded, _unsupported, timer.Elapsed.TotalMilliseconds));
        }

        private unsafe void ReadNativeBlocks()
        {
            var maps = Client.Game.UO.FileManager.Maps;
            int width = maps.MapBlocksSize[_key.Facet, 0], height = maps.MapBlocksSize[_key.Facet, 1];
            // One tile halo supplies all four terrain corners and roof neighbours.
            int firstX = Math.Max(0, (_bounds.X - 1) >> 3), firstY = Math.Max(0, (_bounds.Y - 1) >> 3);
            int lastX = Math.Min(width - 1, _bounds.Right >> 3), lastY = Math.Min(height - 1, _bounds.Bottom >> 3);
            for (int bx = firstX; bx <= lastX; bx++)
            for (int by = firstY; by <= lastY; by++)
            {
                ref IndexMap index = ref _world.Map.GetIndex(bx, by);
                if (!index.IsValid() || index.MapFile == null || index.MapFile.Length < 196 || index.MapAddress > (ulong)(index.MapFile.Length - 196))
                    continue;
                index.MapFile.Seek((long)index.MapAddress, SeekOrigin.Begin);
                var block = index.MapFile.Read<MapBlock>();
                _blocks++;
                for (int i = 0; i < 64; i++)
                {
                    int x = bx * 8 + (i & 7), y = by * 8 + (i >> 3);
                    var cell = block.Cells[i];
                    _ground[(x, y)] = new Ground((ushort)(cell.TileID & 0x3FFF), cell.Z);
                    Hash((uint)x | ((ulong)(uint)y << 32));
                    Hash(cell.TileID | ((ulong)(byte)cell.Z << 16));
                    _builder.MarkComplete(x, y);
                }
                if (index.StaticAddress == 0 || index.StaticCount == 0) continue;
                long bytes = (long)index.StaticCount * 7;
                if (index.StaticFile == null ||
                    index.StaticAddress > (ulong)Math.Max(0, index.StaticFile.Length - bytes))
                {
                    MarkBlock(bx, by, PhysicalUnknownReason.InvalidSource);
                    continue;
                }
                int count = (int)Math.Min(index.StaticCount, (uint)(MaxSourceRecords - _visitedRecords));
                if (count == 0) { _recordLimit = true; return; }
                var records = new StaticsBlock[count];
                index.StaticFile.Seek((long)index.StaticAddress, SeekOrigin.Begin);
                if (index.StaticFile.Read(MemoryMarshal.AsBytes(records.AsSpan())) != (long)count * 7)
                {
                    MarkBlock(bx, by, PhysicalUnknownReason.InvalidSource);
                    continue;
                }
                for (int ordinal = 0; ordinal < records.Length; ordinal++)
                {
                    _visitedRecords++;
                    var record = records[ordinal];
                    if (record.Color == 0 || record.Color == ushort.MaxValue) continue;
                    if (record.X > 7 || record.Y > 7) { MarkBlock(bx, by, PhysicalUnknownReason.InvalidSource); continue; }
                    ulong blockIdentity = (ulong)(uint)(bx * height + by);
                    Add(new Source(bx * 8 + record.X, by * 8 + record.Y, record.Z, record.Color,
                        new PhysicalOwnerKey(PhysicalSourceKind.MapStatic, blockIdentity, (uint)ordinal), Tile(record.Color)));
                    _statics++;
                }
                if ((uint)count < index.StaticCount) { _recordLimit = true; return; }
            }
        }

        private void ReadLiveOverlay()
        {
            // Native base records have already been read. Loaded Static objects would duplicate
            // them and Static.Index is a tile-cell index, NOT a record ordinal.
            foreach (Item item in _world.Items.Values)
            {
                if (!VisitRecord()) return;
                if (item.IsDestroyed || !item.OnGround || item.IsMulti || !InHalo(item.X, item.Y)) continue;
                Add(new Source(item.X, item.Y, item.Z, item.Graphic,
                    new PhysicalOwnerKey(PhysicalSourceKind.Item, item.Serial), Tile(item.Graphic), Moving(item)));
                _items++;
            }
            foreach (House house in _world.HouseManager.Houses)
            {
                if (!VisitRecord()) return;
                if (house.Serial == 0) continue; // Local placement preview is not world structure.
                for (int i = 0; i < house.Components.Count; i++)
                {
                    if (!VisitRecord()) return;
                    Multi component = house.Components[i];
                    if (component.IsDestroyed || !InHalo(component.X, component.Y)) continue;
                    Add(new Source(component.X, component.Y, component.Z, component.Graphic,
                        new PhysicalOwnerKey(PhysicalSourceKind.Multi, house.Serial, (uint)i), Tile(component.Graphic), Moving(component)));
                    Hash(house.Revision);
                    _multis++;
                }
            }
            // Raw map data can extend beyond the live server window. That supplies useful known
            // casters, but cannot certify that an unreceived door/house is absent there.
            Span<byte> loaded = stackalloc byte[PhysicalWorldBuilder.MaxSide * PhysicalWorldBuilder.MaxSide];
            FillLoadedCoverage(_world, _bounds, loaded);
            for (int y = _bounds.Y; y < _bounds.Bottom; y++)
            for (int x = _bounds.X; x < _bounds.Right; x++)
                if (loaded[(y - _bounds.Y) * _bounds.Width + x - _bounds.X] == 0)
                    _builder.MarkUnknown(x, y, PhysicalUnknownReason.MissingLiveChunk);
        }

        private bool VisitRecord()
        {
            if (_visitedRecords >= MaxSourceRecords) { _recordLimit = true; return false; }
            _visitedRecords++;
            return true;
        }

        private StaticTiles Tile(ushort graphic)
        {
            ref var data = ref Client.Game.UO.FileManager.TileData.StaticData;
            return graphic < data.Length ? data[graphic] : default;
        }

        private bool InHalo(int x, int y) => x >= _bounds.X - 1 && x <= _bounds.Right && y >= _bounds.Y - 1 && y <= _bounds.Bottom;
        private static bool Moving(GameObject obj) => obj.Offset.X != 0 || obj.Offset.Y != 0 || obj.Offset.Z != 0;
        private void Add(Source source)
        {
            if (_sources.Count >= MaxSourceRecords) { _recordLimit = true; return; }
            if (source.Graphic >= Client.Game.UO.FileManager.TileData.StaticData.Length)
            {
                _builder.MarkUnknown(source.X, source.Y, PhysicalUnknownReason.InvalidSource);
                return;
            }
            _sources.Add(source);
            Hash((uint)source.X | ((ulong)(uint)source.Y << 32));
            Hash((uint)source.Z | ((ulong)source.Graphic << 32));
            Hash((ulong)source.Owner.Kind); Hash(source.Owner.Identity); Hash(source.Owner.Component);
            Hash((ulong)source.Tile.Flags); Hash(source.Tile.Height);
            Hash(source.Moving ? 1UL : 0UL);
            if (source.Tile.IsRoof && !source.Moving)
            {
                if (!_roofs.TryGetValue((source.X, source.Y), out List<Source> roofs))
                    _roofs[(source.X, source.Y)] = roofs = new List<Source>();
                roofs.Add(source);
            }
        }

        private void EmitTerrain()
        {
            var tiles = Client.Game.UO.FileManager.TileData.LandData;
            ForCells((x, y) =>
            {
                if (!_ground.TryGetValue((x, y), out Ground top) || top.Graphic <= 2 || top.Graphic >= tiles.Length)
                { _builder.MarkUnknown(x, y, PhysicalUnknownReason.MissingNativeBlock); return; }
                var tile = tiles[top.Graphic];
                bool stretched = !(tile.TexID == 0 && tile.IsWet) &&
                    Client.Game.UO.FileManager.Texmaps.File.GetValidRefEntry(tile.TexID).Length > 0;
                Ground right = top, left = top, bottom = top;
                if (stretched && (!_ground.TryGetValue((x + 1, y), out right) || !_ground.TryGetValue((x, y + 1), out left) ||
                    !_ground.TryGetValue((x + 1, y + 1), out bottom)))
                { _builder.MarkUnknown(x, y, PhysicalUnknownReason.Boundary); return; }
                var owner = new PhysicalOwnerKey(PhysicalSourceKind.Land, (uint)x | ((ulong)(uint)y << 32));
                var material = tile.IsWet ? PhysicalMaterial.Receiver : PhysicalMaterial.Terrain;
                N3 a = new(x, y, top.Z), b = new(x + 1, y, right.Z), c = new(x + 1, y + 1, bottom.Z), d = new(x, y + 1, left.Z);
                // ChunkMesh maps 0/1/2/3 to top/right/left/bottom; MeshLayer emits
                // 0,1,2 + 1,3,2. Match that actual rendered diagonal (right-left).
                // Land.AverageZ's opposite-corner choice is a movement rule instead.
                Quad(b, c, d, a, owner, material, PhysicalProvenance.NativeTerrain, top.Graphic);
                Hash((ulong)tile.Flags); Hash(tile.TexID);
            });
        }

        private void EmitStructure(Source source)
        {
            var tile = source.Tile;
            if (tile.IsWet || tile.IsFoliage) { _excluded++; return; }
            // A nonstructural emitting prop is decoration in this structure-only
            // contract, even if movement flags make its tiny base impassable.
            // Do not invent a solid volume for a candelabra, or mark its entire
            // source cell unknown and thereby prevent every finite lamp receipt.
            // Other unknown structures sharing this cell retain their own flags.
            if (tile.IsLight && !tile.IsWall && !tile.IsRoof && !tile.IsDoor && !tile.IsWindow
                && !tile.IsSurface && !tile.IsBridge && !tile.IsNoShoot) { _excluded++; return; }
            bool structural = RequiresStructuralClassification(tile.Flags);
            if (!structural) { _excluded++; return; }
            if (source.Moving) { Unknown(source, PhysicalUnknownReason.UnsupportedStructure); return; }
            if (tile.IsAnimated) { Unknown(source, PhysicalUnknownReason.UnsupportedStructure); return; }
            if (tile.IsWindow || tile.IsTransparent || tile.IsTranslucent)
            { Unknown(source, PhysicalUnknownReason.OpticalOpening); return; }
            if (tile.IsRoof)
            {
                if (!TryRoofCorner(source, 1, 0, out int east) || !TryRoofCorner(source, 0, 1, out int south) ||
                    !TryRoofCorner(source, 1, 1, out int southeast))
                { Unknown(source, PhysicalUnknownReason.UnsupportedRoof); return; }
                if (southeast != east + south - source.Z ||
                    !PhysicalReceiverRecipe.FitRoof(source.Graphic, east - source.Z, south - source.Z).Qualified)
                { Unknown(source, PhysicalUnknownReason.UnsupportedRoof); return; }
                // Four native corner samples; no art-lift twin or synthetic gable is admitted.
                Quad(new(source.X, source.Y, source.Z), new(source.X + 1, source.Y, east),
                    new(source.X + 1, source.Y + 1, southeast), new(source.X, source.Y + 1, south),
                    source.Owner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.NeighbourRoof, source.Graphic);
                return;
            }
            if (tile.IsWall || tile.IsDoor)
            {
                PhysicalArtRecipe art = Recipe(source.Graphic);
                if (tile.Height == 0 || art.Faces.Count == 0)
                { Unknown(source, PhysicalUnknownReason.UnsupportedWall); return; }
                bool emitted = EmitWallFace(source, art, art.Faces.First);
                if (art.Faces.Count == 2) emitted &= EmitWallFace(source, art, art.Faces.Second);
                if (!emitted) Unknown(source, PhysicalUnknownReason.OpticalOpening);
                return;
            }
            if (tile.IsSurface && tile.Height <= 4 && !tile.IsBridge)
            {
                PhysicalArtRecipe art = Recipe(source.Graphic);
                float z = source.Z + tile.Height;
                if (!art.FullFloor(tile.Height)) { Unknown(source, PhysicalUnknownReason.UnsupportedStructure); return; }
                Quad(new(source.X, source.Y, z), new(source.X + 1, source.Y, z),
                    new(source.X + 1, source.Y + 1, z), new(source.X, source.Y + 1, z),
                    source.Owner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.NativeFloor, source.Graphic);
                return;
            }
            Unknown(source, PhysicalUnknownReason.UnsupportedStructure);
        }

        private bool TryRoofCorner(Source source, int dx, int dy, out int z)
        {
            z = source.Z;
            if (!_roofs.TryGetValue((source.X + dx, source.Y + dy), out List<Source> neighbours)) return false;
            int matches = 0;
            foreach (Source neighbour in neighbours)
            {
                // Nearby stacked surfaces are ambiguous even if one is a slightly better fit.
                if (Math.Abs(neighbour.Z - source.Z) > 6) continue;
                if (matches > 0 && z != neighbour.Z) return false;
                z = neighbour.Z;
                matches++;
            }
            return matches > 0;
        }

        private bool EmitWallFace(Source source, PhysicalArtRecipe art, WallFaces.Face face)
        {
            // Recover the actual face endpoints from the fitted art-column interval, rather
            // than silently extending a short post or half-wall across the complete tile.
            if (!PhysicalReceiverRecipe.WallSpan(art, face, out float lo, out float hi)
                || !art.FullWall(face, lo, hi, source.Tile.Height)) return false;
            N3 a = face.AlongX ? new(source.X + lo, source.Y + face.Offset, source.Z) : new(source.X + face.Offset, source.Y + lo, source.Z);
            N3 b = face.AlongX ? new(source.X + hi, source.Y + face.Offset, source.Z) : new(source.X + face.Offset, source.Y + hi, source.Z);
            Quad(a, b, b + new N3(0, 0, source.Tile.Height), a + new N3(0, 0, source.Tile.Height),
                source.Owner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.ArtQualifiedWall, source.Graphic);
            return true;
        }

        private PhysicalArtRecipe Recipe(ushort graphic)
        {
            if (_art.TryGetValue(graphic, out PhysicalArtRecipe recipe)) return recipe;
            recipe = PhysicalArtRecipe.For(graphic);
            _art[graphic] = recipe;
            Hash(graphic); Hash((uint)recipe.Width | ((ulong)(uint)recipe.Height << 32));
            Hash(recipe.CoverageFingerprint);
            return recipe;
        }

        private void Unknown(Source source, PhysicalUnknownReason reason)
        {
            _unsupported++;
            _builder.MarkUnknown(source.X, source.Y, reason);
        }

        private void Quad(N3 a, N3 b, N3 c, N3 d, PhysicalOwnerKey owner,
            PhysicalMaterial material, PhysicalProvenance provenance, ushort graphic)
        {
            _builder.AddTriangle(new PhysicalTriangle(a, b, c, owner, material, provenance, graphic));
            _builder.AddTriangle(new PhysicalTriangle(a, c, d, owner, material, provenance, graphic));
        }

        private void MarkBlock(int bx, int by, PhysicalUnknownReason reason)
        {
            for (int y = by * 8; y < by * 8 + 8; y++)
            for (int x = bx * 8; x < bx * 8 + 8; x++) _builder.MarkUnknown(x, y, reason);
        }

        private void ForCells(Action<int, int> visit)
        {
            for (int y = _bounds.Y; y < _bounds.Bottom; y++)
            for (int x = _bounds.X; x < _bounds.Right; x++) visit(x, y);
        }

        private void Hash(ulong value)
        {
            // Deterministic effective-record receipt; never a path-based installation identity.
            for (int i = 0; i < 8; i++) { _fingerprint ^= (byte)value; _fingerprint *= 1099511628211UL; value >>= 8; }
        }
    }

}
