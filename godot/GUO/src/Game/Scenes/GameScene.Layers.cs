// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Compat;
using GUO.Game.GameObjects;
using GUO.Renderer;

namespace GUO.Game.Scenes
{
    /// <summary>
    /// Generated terrain layers (ComfyUI underlays/overlays) as world
    /// content. Overlays join the render lists at their centre depth, like
    /// splats; underlays draw once before everything, below even the land.
    /// Nothing without GUO_LAYER_DIR (or the default layers folder).
    /// </summary>
    internal partial class GameScene
    {
        private List<TerrainLayer> _terrainLayers;
        private bool _terrainLoaded;
        private readonly Dictionary<string, LayerObject> _layerObjects = new();

        private bool _layersDebugDone;

        /// <summary>
        /// Drops the boot-loaded manifest, textures and tile objects so the
        /// next frame re-reads layers.json (-relayers). Stale tile objects
        /// are parked invisible; the new rect mints fresh ones per tile.
        /// </summary>
        internal void ReloadTerrainLayers()
        {
            if (_terrainLayers != null)
            {
                foreach (TerrainLayer layer in _terrainLayers)
                {
                    layer.Texture?.Dispose();
                    layer.Texture = null;
                }
            }

            foreach (LayerObject o in _layerObjects.Values)
            {
                o.AllowedToDraw = false;
            }

            _layerObjects.Clear();
            _terrainLayers = null;
            _terrainLoaded = false;
        }

        private void EnsureTerrainLayers()
        {
            if (_terrainLoaded)
            {
                return;
            }

            _terrainLoaded = true;
            _terrainLayers = TerrainLayers.Load(out _);
            if (_terrainLayers.Count == 0)
            {
                return;
            }

            string dir = TerrainLayers.LayerDir();
            foreach (TerrainLayer layer in _terrainLayers)
            {
                layer.Texture ??= TerrainLayers.LoadTexture(dir, layer);
                GD.Print($"[GUO] terrain layer {layer.Id}: {layer.Kind} texture "
                    + (layer.Texture == null ? "MISSING" : "ok"));
            }

            GD.Print($"[GUO] terrain layers: {_terrainLayers.Count} layer(s) from {dir}");
        }

        private int TileZ(int x, int y)
        {
            try
            {
                return _world.Map.GetTileZ(x, y);
            }
            catch (System.Exception)
            {
                return 0;
            }
        }

        private bool LayerVisible(TerrainLayer layer, bool debug = false)
        {
            if (layer.Facet != _world.MapIndex)
            {
                if (debug)
                {
                    GD.Print($"[GUO] layer {layer.Id}: facet {layer.Facet} vs map {_world.MapIndex}");
                }

                return false;
            }

            int x0 = System.Math.Min(layer.X0, layer.X1), x1 = System.Math.Max(layer.X0, layer.X1);
            int y0 = System.Math.Min(layer.Y0, layer.Y1), y1 = System.Math.Max(layer.Y0, layer.Y1);
            // TilePx is world-absolute; the pixel bounds (like everything
            // the queue culls against) are camera-relative: same subtraction
            // UpdateRealScreenPosition applies per object.
            float ox = _offset.X, oy = _offset.Y;
            Vector2 a = TerrainLayers.TilePx(x0, y0, 0) - new Vector2(ox, oy);
            Vector2 b = TerrainLayers.TilePx(x1, y0, 0) - new Vector2(ox, oy);
            Vector2 c = TerrainLayers.TilePx(x0, y1, 0) - new Vector2(ox, oy);
            Vector2 d = TerrainLayers.TilePx(x1, y1, 0) - new Vector2(ox, oy);
            float lx = System.Math.Min(System.Math.Min(a.X, b.X), System.Math.Min(c.X, d.X)) - 256f;
            float rx = System.Math.Max(System.Math.Max(a.X, b.X), System.Math.Max(c.X, d.X)) + 256f;
            float ty = System.Math.Min(System.Math.Min(a.Y, b.Y), System.Math.Min(c.Y, d.Y)) - 256f;
            float by = System.Math.Max(System.Math.Max(a.Y, b.Y), System.Math.Max(c.Y, d.Y)) + 256f;
            bool hit = rx >= _minPixel.X && lx <= _maxPixel.X && by >= _minPixel.Y && ty <= _maxPixel.Y;
            if (debug && !hit)
            {
                GD.Print($"[GUO] layer {layer.Id}: culled box {lx:0},{ty:0}-{rx:0},{by:0} "
                    + $"vs pixels {_minPixel.X},{_minPixel.Y}-{_maxPixel.X},{_maxPixel.Y} off {ox:0},{oy:0}");
            }

            return hit;
        }

        /// <summary>
        /// Overlays join the sorted stream as one quad per layer (one image,
        /// one mesh, no per-tile cutting, so stitching cannot tear), sorted
        /// at the layer's height. A slab can sink under higher ground at its
        /// far corner; areas are small and flat by construction.
        /// </summary>
        private void QueueTerrainLayers()
        {
            EnsureTerrainLayers();
            if (_terrainLayers == null || _world?.Player == null)
            {
                return;
            }

            bool debug = !_layersDebugDone;
            _layersDebugDone = true;

            int queued = 0;
            foreach (TerrainLayer layer in _terrainLayers)
            {
                if (layer.Kind != LayerKind.Overlay || layer.Texture == null)
                {
                    continue;
                }

                if (!LayerVisible(layer, debug))
                {
                    continue;
                }

                string key = $"layer:{layer.Id}";
                if (!_layerObjects.TryGetValue(key, out LayerObject o))
                {
                    o = new LayerObject(_world, layer);
                    _layerObjects[key] = o;
                }

                o.Layer = layer;
                TerrainLayers.RefreshMesh(layer, TileZ, _offset.X, _offset.Y);
                o.X = (ushort)System.Math.Max(0, layer.CenterX);
                o.Y = (ushort)System.Math.Max(0, layer.CenterY);
                o.Z = (sbyte)System.Math.Max(-128, System.Math.Min(127, layer.CenterZ));
                o.PriorityZ = o.Z;
                PushToRenderQueue(o, true, false);
                queued++;
            }

            if (debug)
            {
                GD.Print($"[GUO] terrain layers: {queued} overlay(s) queued");
            }
        }

        private bool _underlaysDebugDone;

        /// <summary>Underlays draw before everything, below even the land.</summary>
        private void DrawUnderlays(UltimaBatcher2D batcher)
        {
            EnsureTerrainLayers();
            if (_terrainLayers == null)
            {
                return;
            }

            bool debug = !_underlaysDebugDone;
            _underlaysDebugDone = true;
            if (debug)
            {
                GD.Print($"[GUO] DrawUnderlays: {_terrainLayers.Count} layer(s)");
            }

            foreach (TerrainLayer layer in _terrainLayers)
            {
                try
                {
                    if (debug)
                    {
                        GD.Print($"[GUO] underlay check {layer.Id}: kind={layer.Kind} tex={(layer.Texture == null ? "null" : "ok")}");
                    }

                    if (layer.Kind != LayerKind.Underlay || layer.Texture == null)
                    {
                        continue;
                    }

                    bool visible = LayerVisible(layer, debug);
                    if (debug)
                    {
                        GD.Print($"[GUO] underlay {layer.Id}: visible={visible}");
                    }

                    if (!visible)
                    {
                        continue;
                    }

                    TerrainLayers.RefreshMesh(layer, TileZ, _offset.X, _offset.Y);
                    if (layer.Mesh != null)
                    {
                        if (debug)
                        {
                            var arrays = layer.Mesh.SurfaceGetArrays(0);
                            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                            GD.Print($"[GUO] underlay {layer.Id}: drawing {verts.Length} verts "
                                + $"({verts[0].X:0},{verts[0].Y:0})-({verts[4].X:0},{verts[4].Y:0})");
                        }

                        batcher.DrawLayerMesh(layer.Mesh, layer.Texture);
                    }
                    else if (debug)
                    {
                        GD.Print($"[GUO] underlay {layer.Id}: mesh null after refresh");
                    }
                }
                catch (System.Exception ex)
                {
                    GD.Print($"[GUO] underlay {layer.Id}: EX {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
