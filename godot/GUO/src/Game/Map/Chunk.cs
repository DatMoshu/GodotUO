// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Assets;
using GUO.Utility;
using System;
using System.Runtime.InteropServices;
using System.Buffers;

namespace GUO.Game.Map
{
    internal sealed class Chunk
    {
        private static readonly Queue<Chunk> _pool = new Queue<Chunk>();

        private readonly World _world;

        public Chunk(World world)
        {
            _world = world;
        }

        public GameObject[,] Tiles { get; } = new GameObject[8, 8];
        // PORT DEVIATION (GUO): first-Land identity only, never visibility,
        // depth or cover winners. Main-thread tile mutations invalidate cells.
        private readonly Land[] _firstLand = new Land[64];
        private ulong _firstLandValid;
        private long _coverMembershipVersion;
        private ulong _coverMembershipDirty;
        // PORT DEVIATION (GUO): resolve pending identity/certification checks
        // before any retained plan compares its dependency. Main thread only.
        internal long CoverMembershipVersion
        {
            get
            {
                while (_coverMembershipDirty != 0)
                {
                    int cell = System.Numerics.BitOperations.TrailingZeroCount(_coverMembershipDirty);
                    GetFirstLand(cell & 7, cell >> 3);
                }
                return _coverMembershipVersion;
            }
        }
        internal static long FirstLandScans, FirstLandLookups, FirstLandNodes;

        internal void InvalidateFirstLand(int x, int y)
        {
            int cell = x + (y << 3);
            ulong bit = 1UL << cell;
            if ((_firstLandValid & bit) != 0)
                _coverMembershipDirty |= bit;
            else if ((_coverMembershipDirty & bit) == 0)
                _coverMembershipVersion++; // no certified baseline: stay conservative
            _firstLandValid &= ~bit;
            // A pending cell keeps its prior identity until the next scan.
            // Repeated invalidations must not overwrite that baseline.
        }

        private void InvalidateFirstLandCache()
        {
            _coverMembershipVersion++;
            _coverMembershipDirty = 0;
            _firstLandValid = 0;
            Array.Clear(_firstLand, 0, _firstLand.Length);
        }

        internal bool HasCertifiedFirstLand(int x, int y) =>
            (_firstLandValid & (1UL << (x + (y << 3)))) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Land GetFirstLand(int x, int y)
        {
            FirstLandLookups++;
            int cell = x + (y << 3);
            ulong bit = 1UL << cell;
            // A valid entry certifies a homogeneous chain. Every link/owner
            // mutation invalidates it, so hits need no anchor or owner query.
            if ((_firstLandValid & bit) != 0) return _firstLand[cell];
            Land first = ScanFirstLand(x, y, out bool cacheable);
            if ((_coverMembershipDirty & bit) != 0)
            {
                // Non-Land moves can change a legacy anchor's reachable Land
                // or introduce foreign ownership. Compare BOTH invariants;
                // an unchanged identity alone does not certify a plan.
                if (!cacheable || !ReferenceEquals(first, _firstLand[cell]))
                    _coverMembershipVersion++;
                _coverMembershipDirty &= ~bit;
            }
            if (cacheable)
            {
                _firstLand[cell] = first;
                _firstLandValid |= bit;
            }
            else _firstLand[cell] = null;
            return first;
        }

        private Land ScanFirstLand(int x, int y, out bool cacheable)
        {
            FirstLandScans++;
            cacheable = true;
            Land first = null;
            for (GameObject obj = GetHeadObject(x, y); obj != null; obj = obj.TNext)
            {
                FirstLandNodes++;
                // Scan the whole chain on a miss: insertion through a legacy
                // detached anchor can mix owners. Such a chain must stay live.
                cacheable &= obj.HasTileMembership(this, x, y);
                if (first == null && obj is Land land) first = land;
            }
            return first;
        }
        // END PORT DEVIATION (GUO)

        public bool IsDestroyed;
        public long LastAccessTime;
        public LinkedListNode<int> Node;
        public readonly ChunkMesh Mesh = new ChunkMesh();

        public int X;
        public int Y;

        // PORT DEVIATION (GUO): set when the tile walk skipped this chunk in a
        // frame that moved the view (GameScene.FillGameObjectList), so its
        // objects' screen positions are refreshed on the next visit.
        public bool ScreenPositionsStale;

        // PORT DEVIATION (GUO): ADR-0044 Depth World walk reuse
        // (GameScene.ChunkReuse.cs): bumped whenever an object enters or
        // leaves one of this chunk's tiles, and the last walk's record.
        internal int ObjectsVersion;
        internal ulong DirtyTiles; // one bit per tile, (x << 3) | y
        internal Scenes.GameScene.ChunkWalk Walk;


        public static Chunk Create(World world, int x, int y)
        {
            Chunk c;
            if (_pool.Count > 0)
            {
                c = _pool.Dequeue();
                c.IsDestroyed = false;
            }
            else
            {
                c = new Chunk(world);
            }

            // PORT DEVIATION (GUO): pool reuse must not retain cached Land identities.
            c.InvalidateFirstLandCache();
            c.LastAccessTime = Time.Ticks + Constants.CLEAR_TEXTURES_DELAY;
            c.X = x;
            c.Y = y;

            return c;
        }

        public static void ClearPool()
        {
            while (_pool.Count > 0)
            {
                var chunk = _pool.Dequeue();
                // PORT DEVIATION (GUO): release cached references with the pooled chunk.
                chunk.InvalidateFirstLandCache();
                chunk.Mesh.Clear();
            }
        }


        public unsafe void Load(int index)
        {
            IsDestroyed = false;

            Map map = _world.Map;

            ref var im = ref GetIndex(index);
            Load(map, ref im);
        }

        // PORT DEVIATION (GUO): shared load body also accepts a synthetic invalid
        // index in BatcherProbe, without a file manager or client installation.
        internal unsafe void Load(Map map, ref IndexMap im)
        {
            InvalidateFirstLandCache();
            IsDestroyed = false;

            if (!im.IsValid())
            {
                return;
            }

            im.MapFile.Seek((long)im.MapAddress, System.IO.SeekOrigin.Begin);
            var block = im.MapFile.Read<MapBlock>();

            var cells = block.Cells;
            int bx = X << 3;
            int by = Y << 3;

            for (int y = 0; y < 8; ++y)
            {
                int pos = y << 3;
                ushort tileY = (ushort)(by + y);

                for (int x = 0; x < 8; ++x, ++pos)
                {
                    ushort tileID = (ushort)(cells[pos].TileID & 0x3FFF);

                    sbyte z = cells[pos].Z;

                    Land land = Land.Create(_world, tileID);

                    ushort tileX = (ushort)(bx + x);

                    land.ApplyStretch(map, tileX, tileY, z);
                    land.X = tileX;
                    land.Y = tileY;
                    land.Z = z;
                    land.UpdateScreenPosition();

                    AddGameObject(land, x, y);
                }
            }

            if (im.StaticAddress != 0 && im.StaticCount > 0)
            {
                var staticsBlockBuffer = ArrayPool<StaticsBlock>.Shared.Rent((int)im.StaticCount);
                var staticsSpan = staticsBlockBuffer.AsSpan(0, (int)im.StaticCount);
                im.StaticFile.Seek((long)im.StaticAddress, System.IO.SeekOrigin.Begin);
                im.StaticFile.Read(MemoryMarshal.AsBytes(staticsSpan));

                foreach (ref var sb in staticsSpan)
                {
                    if (sb.Color != 0 && sb.Color != 0xFFFF)
                    {
                        int pos = (sb.Y << 3) + sb.X;

                        if (pos >= 64)
                        {
                            continue;
                        }

                        Static staticObject = Static.Create(_world, sb.Color, sb.Hue, pos);
                        staticObject.X = (ushort)(bx + sb.X);
                        staticObject.Y = (ushort)(by + sb.Y);
                        staticObject.Z = sb.Z;
                        staticObject.UpdateScreenPosition();

                        AddGameObject(staticObject, sb.X, sb.Y);
                    }
                }

                ArrayPool<StaticsBlock>.Shared.Return(staticsBlockBuffer);
            }
        }


        private ref IndexMap GetIndex(int map)
        {
            Client.Game.UO.FileManager.Maps.SanitizeMapIndex(ref map);

            return ref Client.Game.UO.FileManager.Maps.GetIndex(map, X, Y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject GetHeadObject(int x, int y)
        {
            GameObject obj = Tiles[x, y];

            while (obj?.TPrevious != null)
            {
                obj = obj.TPrevious;
            }

            return obj;
        }

        public void AddGameObject(GameObject obj, int x, int y)
        {
            ObjectsVersion++; DirtyTiles |= 1UL << ((x << 3) | y); // PORT DEVIATION (GUO): ChunkReuse
            Mesh.MarkDirtyIfNeeded(obj);
            obj.RemoveFromTile();
            // PORT DEVIATION (GUO): old ownership was invalidated by unlink;
            // destination must invalidate even when it previously cached null.
            InvalidateFirstLand(x, y);

            short priorityZ = obj.Z;
            sbyte state = -1;

            ushort graphic = obj.Graphic;

            switch (obj)
            {
                case Land tile:

                    if (tile.IsStretched)
                    {
                        priorityZ = (short) (tile.AverageZ - 1);
                    }
                    else
                    {
                        priorityZ--;
                    }

                    priorityZ -= 1;

                    state = 0;

                    break;

                case Mobile _:
                    priorityZ++;

                    break;

                case Item item:

                    if (item.IsCorpse)
                    {
                        priorityZ++;

                        break;
                    }
                    else if (item.IsMulti)
                    {
                        graphic = item.MultiGraphic;
                    }

                    goto default;

                case GameEffect _:
                    priorityZ += 2;

                    break;

                case Multi m:

                    state = 1;

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0)
                    {
                        priorityZ--;

                        break;
                    }

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) != 0)
                    {
                        state = 2;
                        priorityZ++;
                    }
                    //else if ((m.ItemData.Flags & TileFlag.StairRight) != 0)
                    //{
                    //    priorityZ++;
                    //}

                    //if (m.IsMovable)
                    //{
                    //    priorityZ += 1;
                    //}

                    goto default;

                default:
                    ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];

                    if (data.IsBackground)
                    {
                        priorityZ--;
                    }

                    //if (data.IsSurface)
                    //{
                    //    priorityZ--;
                    //}

                    if (data.Height != 0)
                    {
                        priorityZ++;
                    }

                    if (data.IsMultiMovable)
                    {
                        priorityZ++;
                    }

                    break;
            }

            obj.PriorityZ = priorityZ;
            // PORT DEVIATION (GUO): covers empty and existing-anchor returns too.
            obj.SetTileMembership(this, x, y);

            if (Tiles[x, y] == null)
            {
                Tiles[x, y] = obj;
                obj.TPrevious = null;
                obj.TNext = null;

                return;
            }


            GameObject o = Tiles[x, y];

            if (o == obj)
            {
                if (o.Previous != null)
                {
                    o = (GameObject) o.Previous;
                }
                else if (o.Next != null)
                {
                    o = (GameObject) o.Next;
                }
                else
                {
                    return;
                }
            }

            while (o?.TPrevious != null)
            {
                o = o.TPrevious;
            }

            GameObject found = null;
            GameObject start = o;

            while (o != null)
            {
                int testPriorityZ = o.PriorityZ;

                if (testPriorityZ > priorityZ || testPriorityZ == priorityZ && (state == 0 || state == 1 && !(o is Land)))
                {
                    break;
                }

                found = o;
                o = o.TNext;
            }

            if (found != null)
            {
                obj.TPrevious = found;
                GameObject next = found.TNext;
                obj.TNext = next;
                // PORT DEVIATION (GUO): aliased anchors can reach another
                // cell's chain; invalidate the owners of changed links too.
                found.InvalidateTileLand();
                next?.InvalidateTileLand();
                found.TNext = obj;

                if (next != null)
                {
                    next.TPrevious = obj;
                }
            }
            else if (start != null)
            {
                // PORT DEVIATION (GUO): preserve aliases without stale owners.
                start.InvalidateTileLand();
                obj.TNext = start;
                start.TPrevious = obj;
                obj.TPrevious = null;
            }
        }

        public void RemoveGameObject(GameObject obj, int x, int y)
        {
            ObjectsVersion++; DirtyTiles |= 1UL << ((x << 3) | y); // PORT DEVIATION (GUO): ChunkReuse
            Mesh.MarkDirtyIfNeeded(obj);
            ref GameObject firstNode = ref Tiles[x, y];

            if (firstNode == null || obj == null)
            {
                return;
            }

            // PORT DEVIATION (GUO): this path bypasses RemoveFromTile.
            obj.TPrevious?.InvalidateTileLand();
            obj.TNext?.InvalidateTileLand();
            obj.ClearTileMembership();
            InvalidateFirstLand(x, y);

            if (firstNode == obj)
            {
                firstNode = obj.TNext;
            }

            if (obj.TNext != null)
            {
                obj.TNext.TPrevious = obj.TPrevious;
            }

            if (obj.TPrevious != null)
            {
                obj.TPrevious.TNext = obj.TNext;
            }

            obj.TPrevious = null;
            obj.TNext = null;
        }


        public void Destroy()
        {
            // PORT DEVIATION (GUO): raw bulk detaches must drop cached references.
            InvalidateFirstLandCache();
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        // PORT DEVIATION (GUO): clear ownership even for the
                        // preserved Player and already-destroyed anchor aliases.
                        first.TPrevious?.InvalidateTileLand();
                        first.TNext?.InvalidateTileLand();
                        first.ClearTileMembership();
                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }

            if (Node != null)
            {
                if (Node.Next != null || Node.Previous != null)
                {
                    Node.List?.Remove(Node);
                }

                Node = null;
            }

            IsDestroyed = true;

            if (_pool.Count < Constants.PREDICTABLE_CHUNKS)
            {
                Mesh.SoftClear();
                _pool.Enqueue(this);
            }
            else
            {
                Mesh.Clear();
            }
        }

        public void Clear()
        {
            Destroy();
        }

        /// <summary>
        /// Clears the chunk's tile objects for an in-place reload (UltimaLive block
        /// update) and marks the mesh dirty so it rebuilds — WITHOUT destroying or
        /// pooling the chunk itself. The chunk stays owned by the map: its
        /// <see cref="_terrainChunks"/> slot and <see cref="Node"/> are left intact.
        /// Using <see cref="Destroy"/>/<see cref="Clear"/> here would enqueue this
        /// chunk to the shared pool while it is still referenced by the map, so it
        /// would later be handed out by <see cref="Create"/> for a different block
        /// (double ownership) — the original block then renders a stale/empty mesh
        /// (black, world-locked, permanent).
        /// </summary>
        public void ClearForReload()
        {
            // PORT DEVIATION (GUO): raw bulk detaches must drop cached references.
            InvalidateFirstLandCache();
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        // PORT DEVIATION (GUO): clear ownership even for the
                        // preserved Player and already-destroyed anchor aliases.
                        first.TPrevious?.InvalidateTileLand();
                        first.TNext?.InvalidateTileLand();
                        first.ClearTileMembership();
                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }

            Mesh.SoftClear();
        }

        public bool HasNoExternalData()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    for (GameObject obj = GetHeadObject(i, j); obj != null; obj = obj.TNext)
                    {
                        if (!(obj is Land) && !(obj is Static) /*&& !(obj is Multi)*/)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}
