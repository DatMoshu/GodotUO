// SPDX-License-Identifier: BSD-2-Clause
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Game.UI.Controls;
using GUO.Compat;
using GUO.Renderer;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GUO.Game.Scenes
{
    /// <summary>
    /// A queued draw into the non-atlas gump layer. Prefer the typed text path:
    /// store a reference to the <see cref="RenderedText"/> plus its draw parameters.
    /// This avoids allocating a closure per frame and makes it safe to skip entries
    /// whose text was destroyed/recycled (pooled via <see cref="RenderedText"/>'s
    /// internal pool) between queue and flush.
    ///
    /// Callers that need an arbitrary non-text draw (clipping, compound operations,
    /// solid color rectangles, etc.) use the <see cref="Callback"/> path.
    /// </summary>
    internal readonly struct NoAtlasGumpCommand
    {
        public readonly RenderedText Text;
        public readonly int X;
        public readonly int Y;
        public readonly float LayerDepth;
        public readonly float Alpha;
        public readonly ushort Hue;
        public readonly Func<UltimaBatcher2D, bool> Callback;

        // PORT DEVIATION (GUO): no new fields in this hot mixed-list element.
        // When both references are null, Hue tags a side-list index in Y.
        // X/LayerDepth keep their normal meanings; payloads hold destination Y.
        // Text commands always have Text; queued callbacks always have Callback.
        internal bool IsTexture => Text == null && Callback == null && Hue >= ushort.MaxValue - 1;
        internal int TextureIndex => Y;
        internal bool TextureTiled => Hue == ushort.MaxValue;

        private NoAtlasGumpCommand(int textureIndex, int x, float layerDepth, bool tiled) : this()
        {
            X = x;
            Y = textureIndex;
            LayerDepth = layerDepth;
            Hue = tiled ? ushort.MaxValue : (ushort)(ushort.MaxValue - 1);
        }

        internal static NoAtlasGumpCommand ForTexture(int index, int x, float depth, bool tiled)
            => new(index, x, depth, tiled);

        public NoAtlasGumpCommand(RenderedText text, int x, int y, float layerDepth, float alpha, ushort hue)
        {
            Text = text;
            X = x;
            Y = y;
            LayerDepth = layerDepth;
            Alpha = alpha;
            Hue = hue;
            Callback = null;
        }

        public NoAtlasGumpCommand(Func<UltimaBatcher2D, bool> callback)
        {
            Text = null;
            X = 0;
            Y = 0;
            LayerDepth = 0f;
            Alpha = 0f;
            Hue = 0;
            Callback = callback;
        }
    }

    // Payloads are allocated only by lists that actually queue these draws.
    // Snapshot every lambda local, but keep owner dimensions live until flush.
    internal readonly struct GumpTextureCommand
    {
        internal readonly Control SizeOwner;
        internal readonly Texture2D Texture;
        internal readonly int Y;
        internal readonly Rectangle Source;
        internal readonly Vector3 HueVector;

        internal GumpTextureCommand(Control sizeOwner, Texture2D texture, int y,
            Rectangle source, Vector3 hue)
        {
            SizeOwner = sizeOwner; Texture = texture; Y = y;
            Source = source; HueVector = hue;
        }

        internal Rectangle Destination(int x) => new(x, Y, SizeOwner.Width, SizeOwner.Height);

        internal void Draw(UltimaBatcher2D batcher, int x, float layerDepth, bool tiled)
        {
            // Do not add disposed/validity/geometry guards: the original
            // callbacks attempted the draw and returned true on normal return.
            if (tiled) batcher.DrawTiled(Texture, Destination(x), Source, HueVector, layerDepth);
            else batcher.Draw(Texture, Destination(x), Source, HueVector, layerDepth);
        }
    }

    /// <summary>
    /// Represents an ordered queue of GameObjects to be rendered.
    /// The order is determined by the draw order, not by the insertion order.
    /// Implementation for sorting and processing is passed as delegates.
    /// </summary>
    internal class RenderLists
    {
        /// <summary>
        /// One thing to draw, with the depth upstream would have written into
        /// the depth buffer and the order it was queued in.
        /// </summary>
        /// <remarks>
        /// The depth is taken when the object is queued rather than when it is
        /// drawn, because it is the sort key: taking it twice is waste, and
        /// taking it later would sort on one number and draw on another.
        ///
        /// <see cref="Seq"/> breaks ties. Two things at the same depth have to
        /// keep the order the render list put them in -- upstream's depth test
        /// is a less-than, so the first one queued wins -- and an unstable sort
        /// without this would shuffle a pile of objects on one tile from frame
        /// to frame and shimmer. Seq is the drawable's index in the list by
        /// construction (every Add passes the list's count), which is what the
        /// key sort in DrawWorld relies on to find a drawable from its key.
        /// </remarks>
        private readonly struct Drawable(GameObject obj, float depth, int seq, MeshLayer mesh = null,
            Mobile.DrawPass pass = Mobile.DrawPass.All, int slot = 0)
        {
            public readonly GameObject Object = obj;
            public readonly float Depth = depth;
            public readonly int Seq = seq;
            public readonly MeshLayer Mesh = mesh;
            public readonly Mobile.DrawPass Pass = pass;
            /// <summary>PORT DEVIATION (GUO): --gpu-world's tile slot (WorldDepthKey), from the walk's chain position.</summary>
            public readonly int Slot = slot;
        }

        // PORT DEVIATION (GUO): short-lived UI child lists have no land.
        // Allocate each bucket only when its first Land is actually queued.
        private List<GameObject> _tiles;
        private List<GameObject> _stretchedTiles;

        /// <summary>
        /// Statics, multis, items, mobiles, corpses and effects, together.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream keeps these in four lists and draws
        /// them one after another, which it can do because it draws the world
        /// with a depth buffer -- see the Hargreaves note it links in
        /// GameScene.DrawWorld -- so what paints over what is settled per
        /// pixel by CalculateDepthZ(), not by the order the lists are walked.
        ///
        /// Godot's 2D canvas has no depth buffer. Kept as four lists, the
        /// order became: every static, then every mobile, then every effect,
        /// and that is visibly wrong -- a chair inside a house painted over
        /// the roof of it, and a mobile behind a wall painted over the wall.
        /// One list sorted by the same depth upstream writes gives the same
        /// answer for the sprites UO has, whose alpha is all or nothing.
        /// </remarks>
        private readonly List<Drawable> _world = [];

        private readonly List<GameObject> _transparentObjects = [];

        /// <summary>
        /// Marks the land CoverFromBelow has queued into <see cref="_world"/>
        /// this frame, on the tile itself (<see cref="Land.CoverStamp"/>).
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): was a HashSet of the tiles. A dungeon frame
        /// queues six thousand covering tiles and asks about many more, and
        /// each ask hashed and probed; one integer on the tile answers the
        /// same question. The stamp advances in <see cref="Clear"/>, so last
        /// frame's tiles are never mistaken for this frame's, and no per-frame
        /// clearing of the tiles themselves is needed. See CoverStamp for why
        /// two owners and a fresh tile can never collide, and for the one
        /// lifecycle it supports: between this list's first cover mark and
        /// the DrawWorld that reads it, no other list marks cover land, which
        /// <see cref="_coverOwner"/> checks in debug builds.
        /// </remarks>
        private CoverStamp _coverStamp;

        /// <summary>Whether this list's cover marks are still the live ones; see <see cref="CoverOwner"/>.</summary>
        private CoverOwner _coverOwner;
        private CoverCandidates _coverCandidates; // lazy: UI lists allocate none
        // Probe-only A/B control; ordinary play always retains candidates.
        internal static bool RetainCoverCandidates = true;

        /// <summary>The keys of <see cref="_world"/>, sorted in place by DrawWorld (<see cref="DepthKey"/>).</summary>
        /// <remarks>
        /// Sized on first use: UI controls make a RenderLists per draw, and an
        /// eager 8 KB here was 48 KB of garbage a frame at the login screen.
        /// </remarks>
        private ulong[] _keys = Array.Empty<ulong>();
        // PORT DEVIATION (GUO): lazy bounded scratch; small UI world queues
        // allocate none. The scene releases its high-water array with terrain.
        private ulong[] _sortScratch;

        /// <summary>
        /// Land that has to be drawn over something below it, queued into the
        /// sorted pass as well as the chunk mesh.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): ADR-0004, amended 2026-09-25. Upstream never
        /// needs this: land, statics and items all write CalculateDepthZ() + 0.5
        /// into its depth buffer, so a cellar under the street loses to the
        /// street wherever the two overlap. Here the baked land is drawn before
        /// everything, and anything below it paints over it.
        ///
        /// Drawing the same land tile again, in the sorted pass at its own
        /// depth, gives the depth buffer's answer: it covers what has less
        /// depth than it and overlaps it, and is covered by everything with
        /// more. Upstream compares land against these objects by that same
        /// number, so the result is the depth buffer's, not a heuristic's; the
        /// only choice is which tiles to spend it on. Those are the land tiles
        /// in front of an object that sits below the land of its own tile -- in
        /// front, because only they can have more depth, and as far forward as
        /// its drop below the ground carries its sprite down the screen.
        /// Everything else keeps the bake.
        /// </remarks>
        private void CoverFromBelow(GameObject obj, float depth)
        {
            // Only what upstream draws at depth + 0.5, the same as land.
            // Mobiles and effects draw at + 1 there, a half nearer than this
            // sort puts them, so land in front of one by less than that half
            // does not cover it upstream; they are left out rather than hidden
            // wrongly. See ADR-0004's amendment.
            if (obj is Mobile or GameEffect)
            {
                return;
            }

            Map.Map map = obj.World?.Map;

            if (map == null)
            {
                return;
            }

            Land ground = LandAt(map, obj.X, obj.Y);

            if (ground == null)
            {
                return;
            }

            // Stretched land is drawn up to its corners, not at its own z: a
            // river bank rises from the water's z to the grass's across one
            // tile, over the water statics standing on it. The highest corner
            // of the ground and of the tile in front of it (whose corners reach
            // two tiles on) is how far up land here can cover an object.
            int top = HighestCorner(ground);

            if (ground.IsStretched)
            {
                Land front = LandAt(map, obj.X + 1, obj.Y + 1);

                if (front != null)
                {
                    top = Math.Max(top, HighestCorner(front));
                }
            }

            if (obj.Z >= top)
            {
                return;
            }

            // A sprite drops 4 pixels per z and a tile row is 22 pixels down
            // the screen; one more row for the height of the diamond itself.
            int rows = ((top - obj.Z) * 4 + 43) / 22 + 1;
            int reach = Math.Min(8, rows);
            int half = CoverHalfWidth(obj);

            // F6: the 8-tile square reaches 16 rows down its diagonal, about
            // 77 z. Deeper (a tower's z -128 foot under z 0 land) the sprite
            // hangs further down the screen than that, and the front course
            // showed through the land ClassicUO's depth buffer hides it under.
            // Past the square, only the tiles down the diagonal to the sprite's
            // foot (dx + dy rows down) and across its width can cover it.
            int far = Math.Max(reach, rows);

            // PORT DEVIATION (GUO): retain candidate identities only. The
            // ground, source dimensions and every cover decision stay live.
            // Dynamic objects keep the original walk.
            CoverCandidates cache = RetainCoverCandidates && obj is (Static or Multi)
                ? _coverCandidates ??= new CoverCandidates() : null;
            if (cache != null)
            {
                QueueCover(ground, depth, 0, half);
                if (cache.TryGet(map, Map.Map.CoverTableVersion,
                    new CoverCandidates.Key(obj.X, obj.Y, rows, half), out var candidates,
                    Map.Map.CoverTableIdentityVersion))
                {
                    if (CoverCandidateVerifier.Enabled)
                        CoverCandidateVerifier.Check(map, obj.X, obj.Y, rows, reach, far, half, candidates);
                    foreach (ref readonly var candidate in candidates)
                        QueueCover(candidate.Land, depth, candidate.Column, half);
                    return;
                }
            }

            // The square is walked a row at a time, so eight neighbours in a
            // row share a chunk: look it up when the chunk changes, not per tile.
            ChunkCursor<Chunk> cursor = default;

            for (int dy = 0; dy <= far; dy++)
            {
                for (int dx = 0; dx <= far; dx++)
                {
                    if (cache != null && dx == 0 && dy == 0) continue;
                    if ((dx > reach || dy > reach)
                        && (dx + dy > rows || (half >= 0 && Math.Abs(dx - dy) * 22 >= 22 + half)))
                    {
                        continue;
                    }

                    Land land;
                    if (cache == null)
                        land = dx == 0 && dy == 0 ? ground : LandAt(map, obj.X + dx, obj.Y + dy, ref cursor);
                    else
                    {
                        int x = obj.X + dx, y = obj.Y + dy;
                        Chunk chunk = cursor.At(x, y, map);
                        land = chunk?.GetFirstLand(x % 8, y % 8);
                        cache.Observe(chunk, x % 8, y % 8, land, dx - dy);
                    }
                    QueueCover(land, depth, dx - dy, half);
                }
            }
            cache?.Complete(Map.Map.CoverTableVersion, Map.Map.CoverTableIdentityVersion);
        }

        private void QueueCover(Land land, float depth, int column, int half)
        {
            if (land == null || land.AlphaHue == 0 || _coverStamp.Marked(land.CoverStamp)) return;
            float landDepth = land.CalculateDepthZ();
            if (!(landDepth > depth)) return;
            bool overlaps = half < 0 || Math.Abs(column) * 22 < 22 + half;
            CoverQueued++;
            if (overlaps) CoverOverlapping++;
            else if (CoverCull) return;
            _coverStamp.Mark(ref land.CoverStamp);
            _coverOwner.Marked();
            _world.Add(new Drawable(land, landDepth, _world.Count));
        }

        // PORT DEVIATION (GUO): Epic B, B4 fix 2 (docs/perf/2026-09-28_merged_land.md).
        // Covering tiles queued this frame, and how many of them overlap the
        // sprite of the object that queued them; --cover-cull queues only
        // those. A redrawn tile repaints the bake's own pixels, so one clear of
        // the object changes nothing unless another earlier sprite overlaps it
        // -- the case the five-frame parity check is there to catch.
        public static bool CoverCull;
        public static int CoverQueued, CoverOverlapping;
        public static (int Queued, int Overlapping) LastCover;

        /// <summary>
        /// Half the width of what <paramref name="obj"/> draws, centred on its
        /// tile's column (DrawStatic: x - (UV.Width / 2 - 22) from the tile's
        /// left corner), or -1 when that is not known exactly: items (stacks,
        /// corpses), animated or wet art (another frame, a scaled copy), and
        /// what may cast a skewed shadow or become a stump.
        /// </summary>
        private static int CoverHalfWidth(GameObject obj)
        {
            ushort graphic = obj.Graphic;
            ref GUO.Assets.StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];

            if (obj is not (Static or Multi) || data.IsAnimated || data.IsWet || data.IsFoliage
                || GUO.Game.Data.StaticFilters.IsTree(graphic, out _) || GUO.Game.Data.StaticFilters.IsRock(graphic))
            {
                return -1;
            }

            ref readonly var art = ref Client.Game.UO.Arts.GetArt(graphic);

            return art.Texture == null ? -1 : art.UV.Width >> 1;
        }
        // END PORT DEVIATION (GUO)

        /// <summary>The z of a land tile's highest drawn corner (ApplyStretch: YOffsets are z * 4).</summary>
        private static int HighestCorner(Land land)
        {
            if (!land.IsStretched)
            {
                return land.Z;
            }

            ref GUO.Renderer.UltimaBatcher2D.YOffsets o = ref land.YOffsets;

            return Math.Max(Math.Max(o.Top, o.Right), Math.Max(o.Left, o.Bottom)) >> 2;
        }

        private static Land LandAt(Map.Map map, int x, int y)
        {
            ChunkCursor<Chunk> cursor = default;

            return LandAt(map, x, y, ref cursor);
        }

        /// <summary>
        /// The land at (x, y), through a caller-held <see cref="ChunkCursor{T}"/>
        /// that looks the chunk up again only when (x, y) leaves it.
        /// </summary>
        private static Land LandAt(Map.Map map, int x, int y, ref ChunkCursor<Chunk> cursor)
        {
            Chunk chunk = cursor.At(x, y, map);

            if (chunk == null)
            {
                return null;
            }

            // PORT DEVIATION (GUO): cache only the original linked traversal's
            // first-Land identity. All cover decisions above remain live.
            return chunk.GetFirstLand(x % 8, y % 8);
        }

        // The sort itself: see DepthKey. The order it gives is the one
        // List.Sort with (Depth, Seq) gave; tools/render_sort_tests proves it.
        /// <summary>
        /// Every gump draw, in the order the control tree asked for it.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream keeps two queues -- one for the
        /// draws that come out of the gump atlas and one for the draws that do
        /// not -- and flushes the atlas one first, so the whole UI is drawn
        /// twice over. It can do that because UIManager.Draw runs with the
        /// depth buffer on and hands every control an ever-increasing
        /// layerDepth, so which queue a draw landed in makes no difference to
        /// what ends up on top.
        ///
        /// There is no depth buffer here, and the split is then plainly
        /// visible: journal text painted across a world map opened over it,
        /// and a status gump sandwiched between a map's picture and its own
        /// frame. The two queues become one, which needs no sort -- that
        /// layerDepth only ever goes up as the tree is walked, so the order the
        /// draws arrive in already is the order upstream resolves them to.
        /// </remarks>
        private readonly List<NoAtlasGumpCommand> _gumps = [];
        private List<GumpTextureCommand> _gumpTextures;

        public void Clear()
        {
            _coverCandidates?.NextFrame();
            _tiles?.Clear();
            _stretchedTiles?.Clear();
            _world.Clear();
            _coverStamp.NextFrame();
            _coverOwner.Release();
            LastCover = (CoverQueued, CoverOverlapping); // PORT DEVIATION (GUO): B4 fix 2 counters
            CoverQueued = CoverOverlapping = 0;
            _transparentObjects.Clear();
            _gumps.Clear();
            // List.Clear releases owner/texture references but retains capacity.
            // Do not clear after flush: callers can draw a queued list again.
            _gumpTextures?.Clear();
        }

        /// <remarks>
        /// PORT DEVIATION (GUO): upstream draws what is fading last, but still
        /// inside GameScene.DrawWorld's SetStencil(DepthStencilState.Default),
        /// so the depth test goes on hiding a see-through tree behind the
        /// mobile in front of it and behind the roof in front of it. Drawn last
        /// with no depth buffer it painted over both. Everything but land now
        /// joins the sorted pass at its own depth, which is the depth test's
        /// answer; a sprite that blends is still blended over whatever the
        /// sort already drew beneath it.
        /// </remarks>
        public void Add(GameObject toRender, bool isTransparent = false, int slot = 0)
        {
            if (isTransparent && toRender is Land)
            {
                _transparentObjects.Add(toRender);
                return;
            }

            switch (toRender)
            {
                case Land land:
                    if (land.IsStretched)
                    {
                        (_stretchedTiles ??= new List<GameObject>()).Add(toRender);
                    }
                    else
                    {
                        (_tiles ??= new List<GameObject>()).Add(toRender);
                    }
                    break;

                case Static:
                case Multi:
                case Mobile:
                case Item:
                case GameEffect:
                // PORT DEVIATION (GUO): staged splats sort and cover as statics do.
                case Game.GameObjects.SplatObject:
                // PORT DEVIATION (GUO): terrain overlay layers sort the same way.
                case Game.GameObjects.LayerObject:
#if GUO_LABS
                // PORT DEVIATION (GUO): UO+ surface decals use the existing depth queue.
                case GUO.Labs.Weather.WeatherSurface:
#endif
                    float depth = toRender.CalculateDepthZ();

                    if (toRender is Mobile)
                    {
                        // Upstream's MobileView draws the shadow at depth and
                        // the body at depth + 1, everything else here at + 0.5
                        // (ADR-0004's amendment): the body wins against what is
                        // up to half a step deeper -- a swimmer over the water
                        // statics in front of it -- and the shadow loses.
                        _world.Add(new Drawable(toRender, depth, _world.Count, null, Mobile.DrawPass.Shadow, slot));
                        _world.Add(new Drawable(toRender, depth + 0.5f, _world.Count, null, Mobile.DrawPass.Body, slot));
                    }
                    else
                    {
                        _world.Add(new Drawable(toRender, depth, _world.Count, null, Mobile.DrawPass.All, slot));
                    }

                    CoverFromBelow(toRender, depth);
                    break;

                default:
                    break;
            }
        }

        // Cached statics must share the painter's order with trees and mobiles:
        // a canvas has no depth buffer to reconcile separate mesh/sprite passes.
        public void AddMeshStatic(GameObject obj, MeshLayer layer)
        {
            float depth = obj.CalculateDepthZ();
            _world.Add(new Drawable(obj, depth, _world.Count, layer));
            CoverFromBelow(obj, depth);
        }

        /// <summary>
        /// This is an intermediate, crappy solution. Rewriting gump rendering would be way too much at this point.
        /// Adding gump elements that use atlas textures for efficient rendering.
        /// </summary>
        /// <param name="toRender"></param>
        public void AddGumpWithAtlas(Func<UltimaBatcher2D, bool> toRender)
        {
            AddGumpNoAtlas(toRender);
        }

        /// <summary>
        /// PORT DEVIATION (GUO): replaces simple texture closures without
        /// changing the mixed queue's insertion order or live dimension reads.
        /// Callers preserve their existing texture-selection/presence guards.
        /// </summary>
        public void AddGumpWithAtlas(Control sizeOwner, Texture2D texture, int x, int y,
            Rectangle source, Vector3 hue, float layerDepth, bool tiled = false)
        {
            // Start at one for fresh child lists containing only one texture.
            var payloads = _gumpTextures ??= new List<GumpTextureCommand>(1);
            int index = payloads.Count;
            payloads.Add(new GumpTextureCommand(sizeOwner, texture, y, source, hue));
            _gumps.Add(NoAtlasGumpCommand.ForTexture(index, x, layerDepth, tiled));
        }

        /// <summary>
        /// Queue a <see cref="RenderedText"/> draw into the non-atlas gump layer.
        /// This is the preferred path: allocation-free (struct value), insertion-order
        /// preserved alongside <see cref="AddGumpNoAtlas(Func{UltimaBatcher2D, bool})"/>
        /// fallback entries, and flushed with a validity guard against destroyed/recycled
        /// text references.
        /// </summary>
        public void AddGumpNoAtlas(RenderedText text, int x, int y, float layerDepth, float alpha = 1f, ushort hue = 0)
        {
            if (text == null)
            {
                return;
            }

            _gumps.Add(new NoAtlasGumpCommand(text, x, y, layerDepth, alpha, hue));
        }

        /// <summary>
        /// Fallback: queue an arbitrary draw closure into the non-atlas gump layer.
        /// Use this for compound operations (clipping, nested render lists, solid-color
        /// rectangles) that don't fit the <see cref="RenderedText"/> fast path. New code
        /// should prefer the typed overload when drawing text.
        /// </summary>
        public void AddGumpNoAtlas(Func<UltimaBatcher2D, bool> toRender)
        {
            if (toRender == null)
            {
                return;
            }

            _gumps.Add(new NoAtlasGumpCommand(toRender));
        }

        // Test accessors. Kept internal; allow unit tests to inspect what was queued
        // without requiring a live graphics device to invoke the flush path.
        internal int GumpTextsCount => _gumps.Count;
        internal NoAtlasGumpCommand PeekGumpText(int index) => _gumps[index];
        internal int GumpTextureCount => _gumpTextures?.Count ?? 0;
        internal int GumpTextureCapacity => _gumpTextures?.Capacity ?? 0;
        internal bool HasGumpTextureStorage => _gumpTextures != null;
        internal GumpTextureCommand PeekGumpTexture(int index) => _gumpTextures[index];

        /// <summary>Calls of the UI's overload below (UIManager, child lists), for the capture-marker regression.</summary>
        public static int ListDraws;

        public int DrawRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ)
        {
            ListDraws++;
            int result = DrawRenderList(batcher, _tiles, maxGroundZ) +
                   DrawRenderList(batcher, _stretchedTiles, maxGroundZ) +
                   DrawWorld(batcher, maxGroundZ);

            if (_transparentObjects.Count > 0 || _gumps.Count > 0)
            {
                result += DrawRenderList(batcher, _transparentObjects, maxGroundZ);
                result += DrawGumps(batcher, _gumps);
            }

            return result;
        }

        public int DrawRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ, List<Chunk> visibleChunks, int offsetX, int offsetY)
        {
            int result = 0;
            // PORT DEVIATION (GUO): stage timers for the perf probe (PERF-03).
            long stageAt = PerfStages.Now();

            // Build visible indices for all chunks (skip rebuild if visibility unchanged)
            foreach (var chunk in visibleChunks)
            {
                var mesh = chunk.Mesh;
                if (mesh.Land.Count > 0)
                    mesh.Land.BuildVisibleIndices();
            }

            // Draw chunk mesh land tiles from GPU buffers with per-frame visibility
            batcher.SetWorldOffset(offsetX, offsetY);
#if GUO_LABS
            // ADR-0043: consume each quad once. A mid-land rejection replays only
            // the captured prefix, then draws the remaining quads on canvas.
            if (batcher.OrderedWorldRecording)
            {
                foreach (var chunk in visibleChunks) result += batcher.DrawOrderedLand(chunk.Mesh.Land);
            }
            else
#endif
            // PORT DEVIATION (GUO): --merged-land (Epic B, B4) draws every
            // visible chunk's land as one mesh per texture; see MergedLand.
            if (MergedLand.Enabled)
            {
                result += (_mergedLand ??= new CachedLand()).Draw(batcher, visibleChunks);
            }
            else
            foreach (var chunk in visibleChunks)
                result += DrawMeshLayer(batcher, chunk.Mesh.Land);
            batcher.ResetWorldOffset();
            stageAt = PerfStages.Add(PerfStages.Stage.LandMesh, stageAt);

            // Draw excluded land tiles (animated water, etc.)
            result += DrawRenderList(batcher, _tiles, maxGroundZ);
            result += DrawRenderList(batcher, _stretchedTiles, maxGroundZ);
            stageAt = PerfStages.Add(PerfStages.Stage.LandLoose, stageAt);

            // Cached statics and ordinary sprites share one depth-sorted pass.
            _meshOffsetX = offsetX;
            _meshOffsetY = offsetY;
            result += DrawWorld(batcher, maxGroundZ);
            stageAt = PerfStages.Now();

            if (_transparentObjects.Count > 0 || _gumps.Count > 0)
            {
                //batcher.SetStencil(DepthStencilState.DepthRead);
                result += DrawRenderList(batcher, _transparentObjects, maxGroundZ);
                result += DrawGumps(batcher, _gumps);
                //batcher.SetStencil(null);
            }

            PerfStages.Add(PerfStages.Stage.Transparent, stageAt);

            return result;
        }

        /// <remarks>
        ///     PORT DEVIATION (GUO): ADR-0004 moved this whole body into the
        ///     batcher. Upstream flushes the layer's alpha changes, asks the
        ///     batcher for a dynamic index buffer, uploads the visible indices
        ///     into it, binds both buffers on the device and issues one indexed
        ///     draw per texture run. None of those five steps exists on Godot --
        ///     a canvas item has no buffers a caller binds -- so the layer builds
        ///     one ArrayMesh per run and the batcher draws them with
        ///     canvas_item_add_mesh. The call site, and the count it returns, are
        ///     unchanged.
        /// </remarks>
        private static int DrawMeshLayer(UltimaBatcher2D batcher, MeshLayer layer)
        {
            return batcher.DrawMeshLayer(layer);
        }

        /// <summary>
        /// Everything that is not land, drawn back to front.
        /// </summary>
        /// <remarks>
        /// This is the depth buffer upstream has, done on the CPU: the sort
        /// key is the very number upstream writes into it. It works because UO
        /// art is cut out rather than blended -- a pixel is opaque or it is not
        /// -- so ordering whole sprites gives the same picture as ordering
        /// pixels. Sprites that genuinely blend are queued as transparent and
        /// still come last, exactly as they did before.
        /// </remarks>
        private int DrawWorld(UltimaBatcher2D batcher, sbyte maxGroundZ)
        {
            long stageAt = PerfStages.Now();
            System.Diagnostics.Debug.Assert(_coverOwner.Intact, "another RenderLists queued cover land after this one did and before it drew; cover marks are one world list at a time (CoverOwner)");
            var span = CollectionsMarshal.AsSpan(_world);

            // PORT DEVIATION (GUO): sort 8-byte keys, not the drawables. Seq is
            // the index into _world (every Add queues one and counts one), so
            // the low half of a sorted key is where its drawable sits.
            if (_keys.Length < span.Length)
            {
                _keys = new ulong[Math.Max(span.Length, _keys.Length * 2)];
            }

            for (int i = 0; i < span.Length; i++)
            {
                System.Diagnostics.Debug.Assert(span[i].Seq == i, "a drawable's Seq must be its index");
                _keys[i] = DepthKey.Of(span[i].Depth, span[i].Seq);
            }

            WorldDepthSort.SortQueueKeys(_keys, span.Length, ref _sortScratch);
            stageAt = PerfStages.Add(PerfStages.Stage.Sort, stageAt);

            // PORT DEVIATION (GUO): --gpu-world (ADR-0007, GPU-02). The sorted
            // list is drawn through the world depth pass: every sprite the
            // batcher would put on the canvas becomes an instance with the
            // key its Drawable gives it, and the pass's depth buffer orders
            // them with the chunks' persistent statics. The keys are planned
            // first, so a frame the key cannot carry never starts; what the
            // plan cannot know (the pages) is checked at End, and a frame
            // refused there is drawn again on the canvas.
            // GpuWorld.LastFrameOnPass is the world frame's: GameScene clears
            // it when the world frame starts, and only the pass's success sets
            // it. The UI draws its own lists through this method after the
            // world (UIManager, child lists) and must not touch it.
            WorldDepthPass pass = GpuWorld.Pass;
            if (pass != null && pass.Began)
            {
                if (!pass.Failed)
                {
                    PlanGpuKeys(pass, span);
                }

                if (!pass.Failed)
                {
                    batcher.BeginCapture(pass, pass.Viewport.Size.X, pass.Viewport.Size.Y);
                    int captured = DrawSorted(batcher, span, maxGroundZ, true);
                    batcher.EndCapture();
                    pass.End();
                    if (!pass.Failed)
                    {
                        GpuWorld.FramesDrawn++;
                        GpuWorld.LastFrameOnPass = true;
                        return captured;
                    }
                }
                else
                {
                    pass.End();
                }

                GpuWorld.FellBack(pass.FailedWhy);

                // GPU-03: the walk left this frame's statics to the pass, so
                // the canvas path alone would draw a world without them. The
                // scene suspends the pass and draws the frame again.
                if (GpuWorld.InstancesShown > 0)
                {
                    GpuWorld.RedrawOnCanvas = true;
                    _coverOwner.Release();
                    return 0;
                }
            }

            return DrawSorted(batcher, span, maxGroundZ, false);
        }

        // The key parts of each Drawable in the sorted span, for the capture.
        private uint[] _gpuDepthAbs = Array.Empty<uint>();
        private uint[] _gpuTieAbs = Array.Empty<uint>();
        private int[] _gpuS = Array.Empty<int>();

        /// <summary>
        /// PORT DEVIATION (GUO): --gpu-world. The key parts of every Drawable
        /// (WorldDepthKey): the depth part from the Drawable's own depth, the
        /// tile from the tile CalculateDepthZ counted it on, the slot from
        /// the walk. Abandons the frame when something in the list has no
        /// key, when a key is outside the frame's range, or when the keys do
        /// not follow the sort.
        /// </summary>
        private void PlanGpuKeys(WorldDepthPass pass, ReadOnlySpan<Drawable> span)
        {
            if (_gpuDepthAbs.Length < span.Length)
            {
                int capacity = Math.Max(1024, Math.Max(span.Length, _gpuDepthAbs.Length * 2));
                Array.Resize(ref _gpuDepthAbs, capacity);
                Array.Resize(ref _gpuTieAbs, capacity);
                Array.Resize(ref _gpuS, capacity);
            }

            uint previous = 0;      // the largest key so far
            uint previousOther = 0; // the largest key of anything that is not covering land
            bool previousLand = false;
            float previousDepth = 0f;
            for (int i = 0; i < span.Length; i++)
            {
                // The sorted order lives in _keys (DepthKey); span itself is in queue order.
                ref readonly Drawable next = ref span[DepthKey.Seq(_keys[i])];
                GameObject obj = next.Object;

                if (next.Mesh != null)
                {
                    pass.Abandon("a baked static is in the sorted list");
                    return;
                }

                if (obj is Game.GameObjects.SplatObject || obj is Game.GameObjects.LayerObject
#if GUO_LABS
                    || obj is GUO.Labs.Weather.WeatherSurface
#endif
                    )
                {
                    pass.Abandon("staged content is in the sorted list");
                    return;
                }

                DepthTile(obj, out int x, out int y);
                int slot = obj is Land ? 0 : next.Slot;
                uint depthAbs = WorldDepthKey.DepthAbs(next.Depth);
                uint tieAbs = WorldDepthKey.TieAbs(x, y, slot);
                int s = x + y;
                if (!WorldDepthKey.Relative(depthAbs, tieAbs, s, pass.FrameDepthOrigin, pass.FrameMinDiff, out uint key))
                {
                    pass.Abandon("a queued sprite's key is outside the frame's range");
                    return;
                }

                bool land = obj is Land;
                if (i > 0 && key < previous)
                {
                    // GPU-03: covering land is queued in the cover walk's order,
                    // the key orders a diagonal in the tile walk's, and the
                    // two disagree for tiles at one depth. Tiles at one depth
                    // do not overlap (ChunkMesh's land order rests on the same
                    // fact), so a run of covering land at one depth may come
                    // out of the key in any order, as long as the run stays
                    // above everything else drawn before it; what follows the
                    // run is checked against the run's largest key.
                    bool landRun = land && previousLand && next.Depth == previousDepth && key >= previousOther;
                    if (!landRun)
                    {
                        if (!s_keyOrderReported)
                        {
                            s_keyOrderReported = true;
                            ref readonly Drawable prev = ref span[DepthKey.Seq(_keys[i - 1])];
                            DepthTile(prev.Object, out int px, out int py);
                            Godot.GD.Print($"[GUO] --gpu-world key order: #{i - 1} {Describe(prev, px, py)} then #{i} {Describe(next, x, y)} key {key:X8} < {previous:X8}");
                        }

                        pass.Abandon("the key does not reproduce the sort");
                        return;
                    }
                }
                else
                {
                    previous = key;
                }

                if (!land)
                {
                    previousOther = previous;
                }

                previousLand = land;
                previousDepth = next.Depth;
                _gpuDepthAbs[i] = depthAbs;
                _gpuTieAbs[i] = tieAbs;
                _gpuS[i] = s;
            }
        }

        private static bool s_keyOrderReported;

        private static string Describe(in Drawable d, int tx, int ty)
        {
            GameObject o = d.Object;
            return $"{o.GetType().Name} g={o.Graphic:X4} at ({o.X},{o.Y},{o.Z}) tile ({tx},{ty}) off=({o.Offset.X},{o.Offset.Y},{o.Offset.Z}) depth={d.Depth} seq={d.Seq} slot={(o is Land ? 0 : d.Slot)} pass={d.Pass}";
        }

        /// <summary>
        /// The tile CalculateDepthZ counts an object on: its own, or the one
        /// ahead of it while it walks (the same Offset cases, without z).
        /// </summary>
        private static void DepthTile(GameObject obj, out int x, out int y)
        {
            x = obj.X;
            y = obj.Y;
            Vector3 offset = obj.Offset;
            if (offset.X > 0 && offset.Y == 0)
            {
                x++; // Northeast
            }
            else if (offset.X > 0 && offset.Y > 0)
            {
                x++; // East
            }
            else if (offset.X == 0 && offset.Y > 0)
            {
                x++; // Southeast
                y++;
            }
            else if (offset.X < 0 && offset.Y > 0)
            {
                y++; // South
            }
            else if (offset.X < 0 && offset.Y == 0)
            {
                y++; // Southwest
            }
        }

        /// <summary>
        /// The sorted list, back to front. Capturing, each Drawable's key
        /// parts are set on the batcher before it draws (--gpu-world).
        /// </summary>
        private int DrawSorted(UltimaBatcher2D batcher, ReadOnlySpan<Drawable> span, sbyte maxGroundZ, bool capturing)
        {
            long stageAt = PerfStages.Now();
            bool timing = PerfStages.Enabled;
            PerfStages.Stage kind = PerfStages.Stage.Other;

            int done = 0;

            for (int i = 0; i < span.Length; i++)
            {
                ref readonly Drawable next = ref span[DepthKey.Seq(_keys[i])];
                if (capturing)
                {
                    batcher.CaptureDepthAbs = _gpuDepthAbs[i];
                    batcher.CaptureTieAbs = _gpuTieAbs[i];
                    batcher.CaptureS = _gpuS[i];
                }
                if (timing)
                {
                    PerfStages.Stage now = StageOf(next.Object);
                    if (now != kind)
                    {
                        stageAt = PerfStages.Add(kind, stageAt);
                        kind = now;
                    }

                    PerfStages.Count(now);
                }

                // PORT DEVIATION (GUO): whose pixels these are, for the
                // post-processing id buffer (ADR-0023); land here covers, so it
                // paints background. Read only while an id pass is on.
                batcher.CurrentObjectId = next.Object is Land ? -1 : Renderer.PostFx.PostFxIds.Of(next.Object);

                if (next.Mesh != null)
                {
                    if (next.Object.Z <= maxGroundZ)
                        done += batcher.DrawStaticMeshSprite(next.Mesh, next.Object.MeshSpriteIndex, _meshOffsetX, _meshOffsetY);
                    continue;
                }

                if (next.Object is Land land && _coverStamp.Marked(land.CoverStamp))
                {
                    done += DrawCovering(batcher, land, next.Depth);
                    continue;
                }

                if (next.Pass != Mobile.DrawPass.All)
                {
                    if (next.Object.Z <= maxGroundZ)
                    {
                        Mobile.Pass = next.Pass;
                        bool drawn = next.Object.Draw(
                            batcher,
                            next.Object.RealScreenPosition.X,
                            next.Object.RealScreenPosition.Y,
                            next.Pass == Mobile.DrawPass.Body ? next.Depth - 0.5f : next.Depth
                        );
                        Mobile.Pass = Mobile.DrawPass.All;

                        if (drawn && next.Pass == Mobile.DrawPass.Body)
                        {
                            done++;
                        }
                    }

                    continue;
                }

                if (next.Object.Z <= maxGroundZ
                    && next.Object.Draw(
                        batcher,
                        next.Object.RealScreenPosition.X,
                        next.Object.RealScreenPosition.Y,
                        next.Depth
                    ))
                {
                    done++;
                }
            }

            batcher.CurrentObjectId = 0; // PORT DEVIATION (GUO): ADR-0023
            _coverOwner.Release();
            PerfStages.Add(kind, stageAt);
            return done;
        }

        private static PerfStages.Stage StageOf(GameObject obj) => obj switch
        {
            Static => PerfStages.Stage.Statics,
            Mobile => PerfStages.Stage.Mobiles,
            Land => PerfStages.Stage.CoverLand,
            Multi => PerfStages.Stage.Multis,
            Item { IsMulti: false } => PerfStages.Stage.Items,
            _ => PerfStages.Stage.Other,
        };

        /// <summary>
        /// Land queued by <see cref="CoverFromBelow"/>, drawn again over what
        /// sits under it.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO), ADR-0004 amended 2026-09-25. A tile the chunk
        /// mesh holds is drawn from its own baked quad, so it matches the bake
        /// pixel for pixel, stretched or flat. One the mesh does not hold is
        /// drawn the ordinary way when flat; a stretched one outside the mesh
        /// cannot be drawn by the batcher at all and is left as the bake drew
        /// it.
        /// </remarks>
        private int DrawCovering(UltimaBatcher2D batcher, Land land, float depth)
        {
            if (land.InChunkMesh && land.MeshSpriteIndex >= 0)
            {
                Chunk chunk = land.World?.Map?.GetChunk(land.X, land.Y, false);

                if (chunk != null)
                {
                    return batcher.DrawMeshSprite(chunk.Mesh.Land, land.MeshSpriteIndex, _meshOffsetX, _meshOffsetY);
                }
            }

            if (land.IsStretched)
            {
                return 0;
            }

            return land.Draw(batcher, land.RealScreenPosition.X, land.RealScreenPosition.Y, depth) ? 1 : 0;
        }

        private int _meshOffsetX, _meshOffsetY;
        // PORT DEVIATION (GUO): UI controls also create RenderLists per draw;
        // allocate the persistent terrain cache only for the actual world pass.
        private CachedLand _mergedLand;

        internal void ReleaseTerrain()
        {
            _sortScratch = null;
            _coverCandidates?.Clear();
            _coverCandidates = null;
            _mergedLand?.Dispose(); // PORT DEVIATION (GUO): scene-owned mesh lifetime
            _mergedLand = null;
        }

        private static int DrawRenderList(UltimaBatcher2D batcher, List<GameObject> renderList, sbyte maxGroundZ)
        {
            int done = 0;

            // Null is the same empty bucket, including the id reset below.
            if (renderList != null)
            foreach (var obj in renderList)
            {
                if (obj.Z <= maxGroundZ)
                {
                    float depth = obj.CalculateDepthZ();
                    // PORT DEVIATION (GUO): the id buffer (ADR-0023); first-drawn land is background.
                    batcher.CurrentObjectId = obj is Land ? 0 : Renderer.PostFx.PostFxIds.Of(obj);

                    if (obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth))
                    {
                        done++;
                    }
                }
            }

            batcher.CurrentObjectId = 0; // PORT DEVIATION (GUO): ADR-0023
            return done;
        }

        private int DrawGumps(UltimaBatcher2D batcher, List<NoAtlasGumpCommand> renderList)
        {
            int done = 0;

            // AsSpan avoids the List<T> enumerator allocation on the hot path.
            var span = CollectionsMarshal.AsSpan(renderList);
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var cmd = ref span[i];

                if (cmd.Text != null)
                {
                    // Typed fast path. HasContent rejects destroyed/empty text, which is
                    // possible when the underlying instance was returned to the pool
                    // between queue and flush.
                    if (!cmd.Text.HasContent)
                    {
                        continue;
                    }

                    if (cmd.Text.Draw(batcher, cmd.X, cmd.Y, cmd.LayerDepth, cmd.Alpha, cmd.Hue))
                    {
                        done++;
                    }
                }
                else if (cmd.IsTexture)
                {
                    // Acquire only when reached, after any preceding state callback.
                    // Keep no payload span across callbacks that build child lists.
                    ref readonly var texture = ref CollectionsMarshal.AsSpan(_gumpTextures)[cmd.TextureIndex];
                    texture.Draw(batcher, cmd.X, cmd.LayerDepth, cmd.TextureTiled);
                    done++; // original texture callbacks returned true, even for no emitted geometry
                }
                else if (cmd.Callback != null && cmd.Callback.Invoke(batcher))
                {
                    done++;
                }
            }

            return done;
        }
    }
}
