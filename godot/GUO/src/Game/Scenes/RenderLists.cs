// SPDX-License-Identifier: BSD-2-Clause
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
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
        /// is a less-than, so the first one queued wins -- and List.Sort is not
        /// stable, so without this a pile of objects on one tile would shuffle
        /// from frame to frame and shimmer.
        /// </remarks>
        private readonly struct Drawable(GameObject obj, float depth, int seq, MeshLayer mesh = null)
        {
            public readonly GameObject Object = obj;
            public readonly float Depth = depth;
            public readonly int Seq = seq;
            public readonly MeshLayer Mesh = mesh;
        }

        private readonly List<GameObject> _tiles = [];
        private readonly List<GameObject> _stretchedTiles = [];

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

        private int _queued;

        /// <summary>Land queued into <see cref="_world"/> this frame by CoverFromBelow.</summary>
        private readonly HashSet<Land> _covering = new(ReferenceEqualityComparer.Instance);

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

            if (ground == null || obj.Z >= ground.Z)
            {
                return;
            }

            // A sprite drops 4 pixels per z and a tile row is 22 pixels down
            // the screen; one more row for the height of the diamond itself.
            int reach = Math.Min(8, ((ground.Z - obj.Z) * 4 + 43) / 22 + 1);

            for (int dy = 0; dy <= reach; dy++)
            {
                for (int dx = 0; dx <= reach; dx++)
                {
                    Land land = dx == 0 && dy == 0 ? ground : LandAt(map, obj.X + dx, obj.Y + dy);

                    if (land == null || land.AlphaHue == 0 || _covering.Contains(land))
                    {
                        continue;
                    }

                    float landDepth = land.CalculateDepthZ();

                    if (landDepth > depth)
                    {
                        _covering.Add(land);
                        _world.Add(new Drawable(land, landDepth, _queued++));
                    }
                }
            }
        }

        private static Land LandAt(Map.Map map, int x, int y)
        {
            for (GameObject o = map.GetTile(x, y, false); o != null; o = o.TNext)
            {
                if (o is Land land)
                {
                    return land;
                }
            }

            return null;
        }

        private static readonly Comparison<Drawable> ByDepth = static (a, b) =>
        {
            int order = a.Depth.CompareTo(b.Depth);

            return order != 0 ? order : a.Seq.CompareTo(b.Seq);
        };
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

        public void Clear()
        {
            _tiles.Clear();
            _stretchedTiles.Clear();
            _world.Clear();
            _covering.Clear();
            _queued = 0;
            _transparentObjects.Clear();
            _gumps.Clear();
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
        public void Add(GameObject toRender, bool isTransparent = false)
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
                        _stretchedTiles.Add(toRender);
                    }
                    else
                    {
                        _tiles.Add(toRender);
                    }
                    break;

                case Static:
                case Multi:
                case Mobile:
                case Item:
                case GameEffect:
                    float depth = toRender.CalculateDepthZ();
                    _world.Add(new Drawable(toRender, depth, _queued++));
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
            _world.Add(new Drawable(obj, depth, _queued++, layer));
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

        public int DrawRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ)
        {
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

            // Build visible indices for all chunks (skip rebuild if visibility unchanged)
            foreach (var chunk in visibleChunks)
            {
                var mesh = chunk.Mesh;
                if (mesh.Land.Count > 0)
                    mesh.Land.BuildVisibleIndices();
            }

            // Draw chunk mesh land tiles from GPU buffers with per-frame visibility
            batcher.SetWorldOffset(offsetX, offsetY);
            // PORT DEVIATION (GUO): --merged-land (Epic B, B4) draws every
            // visible chunk's land as one mesh per texture; see MergedLand.
            if (MergedLand.Enabled)
            {
                _mergedLayers.Clear();
                foreach (var chunk in visibleChunks)
                    _mergedLayers.Add(chunk.Mesh.Land);
                result += _mergedLand.Draw(batcher, _mergedLayers);
            }
            else
            foreach (var chunk in visibleChunks)
                result += DrawMeshLayer(batcher, chunk.Mesh.Land);
            batcher.ResetWorldOffset();

            // Draw excluded land tiles (animated water, etc.)
            result += DrawRenderList(batcher, _tiles, maxGroundZ);
            result += DrawRenderList(batcher, _stretchedTiles, maxGroundZ);

            // Cached statics and ordinary sprites share one depth-sorted pass.
            _meshOffsetX = offsetX;
            _meshOffsetY = offsetY;
            result += DrawWorld(batcher, maxGroundZ);

            if (_transparentObjects.Count > 0 || _gumps.Count > 0)
            {
                //batcher.SetStencil(DepthStencilState.DepthRead);
                result += DrawRenderList(batcher, _transparentObjects, maxGroundZ);
                result += DrawGumps(batcher, _gumps);
                //batcher.SetStencil(null);
            }

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
            _world.Sort(ByDepth);

            int done = 0;

            var span = CollectionsMarshal.AsSpan(_world);

            for (int i = 0; i < span.Length; i++)
            {
                ref readonly Drawable next = ref span[i];
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

                if (next.Object is Land land && _covering.Contains(land))
                {
                    done += DrawCovering(batcher, land, next.Depth);
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
            return done;
        }

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
        private readonly MergedLand _mergedLand = new MergedLand(); // PORT DEVIATION (GUO): --merged-land
        private readonly List<MeshLayer> _mergedLayers = new List<MeshLayer>(); // PORT DEVIATION (GUO): --merged-land

        private static int DrawRenderList(UltimaBatcher2D batcher, List<GameObject> renderList, sbyte maxGroundZ)
        {
            int done = 0;

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

        private static int DrawGumps(UltimaBatcher2D batcher, List<NoAtlasGumpCommand> renderList)
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
                else if (cmd.Callback != null && cmd.Callback.Invoke(batcher))
                {
                    done++;
                }
            }

            return done;
        }
    }
}
