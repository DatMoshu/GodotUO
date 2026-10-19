// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): the Realtime lights option, kept out of the ported GameScene.cs.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Utility;
using GUO.Utility.Logging;

namespace GUO.Game.Scenes
{
    internal partial class GameScene
    {
        // PORT DEVIATION (GUO): Realtime lights (ADR-0039). The classic loop
        // collects light offers as screen positions; this path records the
        // same offers with their tile, lights a field on the tile grid and
        // draws it into a target of its own, which RenderTargets.Draw blends
        // where the classic light target goes. Off: none of this is built.
        private RealtimeLightField _rtField;
        private RealtimeLightTarget _rtTarget;
        private RealtimeLightField.Source[] _rtSources;
        private int _rtSourceCount;
        private readonly List<(float key, GameObject obj)> _rtSprites = new();
        private long _rtLogAt;
        private int _rtRoofs, _rtAbove;

        /// <summary>Margin of tiles around the visible chunks, so a light just off screen still reaches in.</summary>
        private const int RtMargin = 8;

        /// <summary>Tiles of reach per 44 px of light sprite width.</summary>
        private const float RadiusScale = 1.6f;

        /// <summary>Grid side cap, cells (a zoomed-out 4K view fits well inside).</summary>
        private const int RtMaxSide = 192;

        /// <summary>On, in game, and not during the black-and-white death frame (the classic path runs then).</summary>
        public bool UseRealtimeLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.RealtimeLights
            && _world.InGame
            && !(_world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect);

        /// <summary>The mode's light texture for this frame, or null for the classic target.</summary>
        internal Texture2D RealtimeLightTexture { get; private set; }

        /// <summary>Called from AddLight once a light offer is accepted: keep its tile.</summary>
        private void RecordRealtimeLight(GameObject obj, ref LightData light)
        {
            if (!UseRealtimeLights || obj == null) return;
            _rtSources ??= new RealtimeLightField.Source[LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT];
            if (_rtSourceCount >= _rtSources.Length) return;

            ref readonly var info = ref Client.Game.UO.Lights.GetLight(light.ID);
            // The sprite's reach in tiles, widened: a light.mul blob is drawn
            // additively over grey, the field is a smooth pool, so the same
            // width reads smaller here.
            int radius = info.Texture == null ? 4 : Math.Max(2, (int)MathF.Round(info.UV.Width * RadiusScale / 44f));
            _rtSources[_rtSourceCount++] = new RealtimeLightField.Source(
                obj.X, obj.Y, obj.Z, radius, RealtimeLightColor(light.Color, light.IsHue));
        }

        /// <summary>Classic clears its target; the mode's texture is the override.</summary>
        private bool PrepareRealtimeLightTarget(UltimaBatcher2D batcher, RenderTargets renderTargets)
        {
            if (!UseRealtimeLights)
            {
                if (_rtTarget != null) ResetRealtimeLights(renderTargets);
                return false;
            }

            batcher.SetRenderTarget(renderTargets.LightRenderTarget);
            batcher.Clear(new Godot.Color(0, 0, 0, 0));
            batcher.SetRenderTarget(null);
            return true;
        }

        private void ResetRealtimeLights(RenderTargets renderTargets)
        {
            _rtTarget?.Dispose();
            _rtTarget = null;
            _rtField = null;
            _rtSourceCount = 0;
            RealtimeLightTexture = null;
            if (renderTargets != null) renderTargets.LightOverride = null;
        }

        /// <summary>After the world draw: consume this frame's offers and draw the field.</summary>
        private void DrawRealtimeLights(RenderTargets renderTargets, ref Transform2D matrix)
        {
            if (!UseRealtimeLights)
            {
                return;
            }

            var lightTarget = renderTargets.LightRenderTarget;
            if (lightTarget == null || _visibleChunks.Count == 0)
            {
                renderTargets.LightOverride = null;
                RealtimeLightTexture = null;
                _rtSourceCount = 0;
                _lightCount = 0;
                return;
            }

            try
            {
                _rtField ??= new RealtimeLightField();
                _rtTarget ??= new RealtimeLightTarget();
                _rtTarget.EnsureSize(lightTarget.Parent, lightTarget.Width, lightTarget.Height);
                if (!_rtTarget.IsBuilt)
                {
                    renderTargets.LightOverride = null;
                    return;
                }

                BuildRealtimeGrid();
                _rtField.Compute(RealtimeAmbientLevel(),
                    new ReadOnlySpan<RealtimeLightField.Source>(_rtSources ?? Array.Empty<RealtimeLightField.Source>(), 0, _rtSourceCount));
                DrawRealtimeGeometry(ref matrix);

                RealtimeLightTexture = _rtTarget.Texture;
                renderTargets.LightOverride = RealtimeLightTexture;

                if (Time.Ticks - _rtLogAt > 5000)
                {
                    _rtLogAt = Time.Ticks;
                    string line = $"[realtime-lights] grid={_rtField.Width}x{_rtField.Height} lights={_rtSourceCount} " +
                              $"computed={_rtField.LastComputed} cached={_rtField.CacheCount} " +
                              $"quads={_rtTarget.LastQuads} sprites={_rtTarget.LastSprites} roofs={_rtRoofs} above={_rtAbove}";
                    Log.Trace(line);
                    // The editor's World tab has no client log file: its console gets the line.
                    if (Engine.IsEditorHint()) GD.Print(line);
                }
            }
            catch (Exception e)
            {
                Log.Error($"[realtime-lights] frame failed, classic lighting this frame: {e.Message}");
                renderTargets.LightOverride = null;
                RealtimeLightTexture = null;
            }
            finally
            {
                _rtSourceCount = 0;
                _lightCount = 0;
            }
        }

        /// <summary>
        /// The classic grey for this frame: the shard's levels with the profile's
        /// Light level override applied as the packet handler applies it, so the
        /// override also holds where no light packet has arrived (the editor's world).
        /// </summary>
        private float RealtimeAmbientLevel()
        {
            var profile = ProfileManager.CurrentProfile;
            int overall = _world.Light.Overall;
            int personal = _world.Light.Personal;
            if (profile != null && profile.UseCustomLightLevel)
            {
                overall = profile.LightLevelType == 1 ? Math.Min(overall, profile.LightLevel) : profile.LightLevel;
            }

            // IsometricLight.Recalculate: 0 is full light, 30 the darkest, with a remainder.
            return Math.Max(personal, 32 - overall) * 0.03125f;
        }

        /// <summary>Ground, blockers and roofs from the visible chunks (plus a margin).</summary>
        private void BuildRealtimeGrid()
        {
            int minCx = int.MaxValue, minCy = int.MaxValue, maxCx = int.MinValue, maxCy = int.MinValue;
            foreach (Chunk chunk in _visibleChunks)
            {
                if (chunk == null || chunk.IsDestroyed) continue;
                minCx = Math.Min(minCx, chunk.X); maxCx = Math.Max(maxCx, chunk.X);
                minCy = Math.Min(minCy, chunk.Y); maxCy = Math.Max(maxCy, chunk.Y);
            }

            if (minCx == int.MaxValue) return;

            int originX = minCx * 8 - RtMargin;
            int originY = minCy * 8 - RtMargin;
            int width = Math.Min(RtMaxSide, (maxCx - minCx + 1) * 8 + RtMargin * 2);
            int height = Math.Min(RtMaxSide, (maxCy - minCy + 1) * 8 + RtMargin * 2);
            _rtField.Begin(originX, originY, width, height);

            // The margin chunks are read straight from the map (loaded ones
            // only, never loading on the light's account).
            int c0x = (originX >> 3), c1x = ((originX + width - 1) >> 3);
            int c0y = (originY >> 3), c1y = ((originY + height - 1) >> 3);
            for (int cy = c0y; cy <= c1y; cy++)
            {
                for (int cx = c0x; cx <= c1x; cx++)
                {
                    Chunk chunk = _world.Map.GetChunk(cx * 8, cy * 8, false);
                    if (chunk == null || chunk.IsDestroyed) continue;
                    FillRealtimeChunk(chunk);
                }
            }
        }

        private void FillRealtimeChunk(Chunk chunk)
        {
            ref var statics = ref Client.Game.UO.FileManager.TileData.StaticData;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int tx = chunk.X * 8 + x, ty = chunk.Y * 8 + y;
                    sbyte groundZ = 0;
                    for (GameObject obj = chunk.Tiles[x, y]; obj != null; obj = obj.TNext)
                    {
                        switch (obj)
                        {
                            case Land land:
                                groundZ = land.AverageZ;
                                break;
                            case Static or Multi:
                            {
                                ushort graphic = obj.Graphic;
                                if (graphic >= statics.Length) break;
                                ref StaticTiles data = ref statics[graphic];
                                if (data.IsRoof && obj.Z > groundZ)
                                {
                                    _rtField.SetRoof(tx, ty, obj.Z);
                                }
                                else if (data.IsSurface && data.Height < 4 && obj.Z >= groundZ + RealtimeLightField.UpperFloorZ)
                                {
                                    // An upper storey's floor (or a flat rooftop) covers what is under it.
                                    _rtField.SetCover(tx, ty, obj.Z);
                                }
                                if (RealtimeLightField.Blocks(data.IsWall, data.IsImpassable, data.IsDoor, data.IsWindow, data.Height))
                                {
                                    _rtField.AddBlocker(tx, ty, obj.Z + data.Height);
                                }
                                break;
                            }
                        }
                    }
                    _rtField.SetGround(tx, ty, groundZ);
                }
            }
        }

        /// <summary>The land as lit quads, the tall statics as masked sprites.</summary>
        private void DrawRealtimeGeometry(ref Transform2D matrix)
        {
            _rtTarget.Begin(matrix, _rtField.Ambient);
            float zoom = matrix.X.X > 0.01f ? matrix.X.X : 1f;
            _rtSprites.Clear();
            _rtRoofs = 0;
            _rtAbove = 0;
            ref var statics = ref Client.Game.UO.FileManager.TileData.StaticData;

            foreach (Chunk chunk in _visibleChunks)
            {
                if (chunk == null || chunk.IsDestroyed) continue;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        for (GameObject obj = chunk.Tiles[x, y]; obj != null; obj = obj.TNext)
                        {
                            if (obj is Land land)
                            {
                                int ly = ((land.X + land.Y) * 22 - (land.Z << 2)) - _offset.Y - 22;
                                int lx = ((land.X - land.Y) * 22) - _offset.X - 22;
                                if (ly >= -100 && ly <= _rtTarget.Height / zoom + 100 && lx >= -60 && lx <= _rtTarget.Width / zoom + 60)
                                {
                                    AddRealtimeLand(land);
                                }
                                continue;
                            }

                            if (obj is not (Static or Multi or Item) || !obj.AllowedToDraw || obj.Z >= _maxZ)
                            {
                                continue;
                            }

                            ushort graphic = obj.Graphic;
                            if (graphic >= statics.Length) continue;
                            ref StaticTiles data = ref statics[graphic];
                            if (_noDrawRoofs && data.IsRoof) continue;
                            // Flat floor pieces take the ground's smooth light;
                            // anything that stands up takes its tile's. A flat
                            // piece above the lights (a floor used as a roof, an
                            // upper storey) is drawn, in the roof ambient.
                            bool flat = data.Height < 4 && !data.IsWall && !data.IsRoof && !data.IsFoliage;
                            if (flat && !_rtField.IsAboveLight(obj.X, obj.Y, obj.Z)) continue;

                            // Under a drawn roof or an upper floor the world shows
                            // the cover, not the furniture; no light sprite for what
                            // it covers (walls keep theirs: their outer face shows).
                            if (!_noDrawRoofs && !data.IsWall && !data.IsRoof)
                            {
                                short coverZ = _rtField.CoverZ(obj.X, obj.Y);
                                if (coverZ != RealtimeLightField.NoBlock && obj.Z < coverZ) continue;
                            }

                            // Only what the target can show.
                            int sy = ((obj.X + obj.Y) * 22 - (obj.Z << 2)) - _offset.Y - 22;
                            int sx = ((obj.X - obj.Y) * 22) - _offset.X - 22;
                            if (sy < -220 || sy > _rtTarget.Height / zoom + 220 || sx < -220 || sx > _rtTarget.Width / zoom + 220) continue;

                            // The world's own depth (ADR-0001: one computation), so
                            // a roof covers the furniture under it here as it does there.
                            _rtSprites.Add((obj.CalculateDepthZ(), obj));
                        }
                    }
                }
            }

            // Back to front, in the world's draw order.
            _rtSprites.Sort(static (a, b) => a.key.CompareTo(b.key));
            foreach (var (_, obj) in _rtSprites)
            {
                ref readonly SpriteInfo art = ref Client.Game.UO.Arts.GetArt(obj.Graphic);
                if (art.Texture == null) continue;
                int sx = ((obj.X - obj.Y) * 22) - _offset.X - 22;
                int sy = ((obj.X + obj.Y) * 22 - (obj.Z << 2)) - _offset.Y - 22;
                // DrawStatic's placement: centred on the tile, bottom at its base.
                int px = sx + 22 - (art.UV.Width >> 1);
                int py = sy + 44 - art.UV.Height;
                // A roof stands above every light on its tile: ambient only
                // (with the roof dim, like the cells it covers).
                bool roof = obj.Graphic < statics.Length && statics[obj.Graphic].IsRoof;
                if (roof) _rtRoofs++;
                else if (_rtField.IsAboveLight(obj.X, obj.Y, obj.Z)) _rtAbove++;
                _rtTarget.AddSprite(
                    new Rect2(px, py, art.UV.Width, art.UV.Height),
                    art.Texture,
                    art.UV.ToGodotF(),
                    roof ? _rtField.RoofAmbient : _rtField.CellAt(obj.X, obj.Y, obj.Z));
            }

            _rtTarget.End();
        }

        private void AddRealtimeLand(Land land)
        {
            int x = land.X, y = land.Y;
            int sx = ((x - y) * 22) - _offset.X - 22;
            Vector2 top, right, bottom, left;
            if (land.IsStretched)
            {
                // The chunk mesh's stretched quad: corners at their own Z.
                int y0 = ((x + y) * 22) - _offset.Y - 22;
                top = new Vector2(sx + 22, y0 - land.YOffsets.Top);
                right = new Vector2(sx + 44, y0 + 22 - land.YOffsets.Right);
                left = new Vector2(sx, y0 + 22 - land.YOffsets.Left);
                bottom = new Vector2(sx + 22, y0 + 44 - land.YOffsets.Bottom);
            }
            else
            {
                int sy = ((x + y) * 22 - (land.Z << 2)) - _offset.Y - 22;
                top = new Vector2(sx + 22, sy);
                right = new Vector2(sx + 44, sy + 22);
                bottom = new Vector2(sx + 22, sy + 44);
                left = new Vector2(sx, sy + 22);
            }

            _rtTarget.AddLand(top, right, bottom, left,
                _rtField.Corner(x, y), _rtField.Corner(x + 1, y), _rtField.Corner(x + 1, y + 1), _rtField.Corner(x, y + 1));
        }

        /// <summary>A light's tint: the classic light palette, or the hue when the offer is hued.</summary>
        private static Godot.Color RealtimeLightColor(ushort color, bool isHue)
        {
            if (color == 0) return RealtimeLightField.WarmLight;

            if (isHue)
            {
                var hues = Client.Game?.UO?.FileManager?.Hues;
                if (hues?.HuesRange != null && color < hues.HuesCount)
                {
                    int c = color - 1;
                    int g = c >> 3;
                    int e = c % 8;
                    if (g >= 0 && g < hues.HuesRange.Length)
                    {
                        uint c32 = HuesHelper.Color16To32(hues.HuesRange[g].Entries[e].ColorTable[16]);
                        return new Godot.Color((c32 & 0xFF) / 255f, ((c32 >> 8) & 0xFF) / 255f, ((c32 >> 16) & 0xFF) / 255f);
                    }
                }
                return Colors.White;
            }

            // The light-shader palette (LightColors.MakeDefaultShaders): the tint only.
            uint rgb = color switch
            {
                1 => 0x00FF00, 2 => 0x7F7FFF, 6 => 0xFF00FF, 10 => 0x3F3FFF, 20 => 0x00FF00,
                30 => 0xFF7F00, 31 => 0xFF7F00, 32 => 0xFF00FF, 40 => 0xFF0000,
                50 => 0xFFFF00, 60 => 0xFFFF00, 61 => 0xFFFF00, _ => 0xFFFFFF,
            };
            return new Godot.Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
        }
    }
}
