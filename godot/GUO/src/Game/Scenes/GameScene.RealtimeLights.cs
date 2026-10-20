// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): the Realtime lights option, kept out of the ported GameScene.cs.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Configuration;
using GUO.Game.Data;
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
        private readonly List<RtCaster> _rtCasters = new();
        private readonly List<int> _rtShadowLights = new();
        private long _rtLogAt;
        private double _rtLastMs, _rtGridMs, _rtFieldMs, _rtMsSum;
        private int _rtMsFrames;
        private int _rtRoofs, _rtAbove, _rtDarkQuads;

        /// <summary>Margin of tiles around the visible chunks, so a light just off screen still reaches in.</summary>
        private const int RtMargin = 8;

        /// <summary>
        /// Light sprite pixels per tile of radius. A tile of distance is about
        /// 31 px across the screen, so a sprite W wide reaches W/62 tiles; the
        /// smooth falloff shows little of its last stretch, hence a little
        /// more. (44 suited the corner-averaged field, which blurred a pool's
        /// edge inward; with exact corners it made pools 1.4x the classic size.)
        /// </summary>
        private const float RadiusDiameter = 54f;

        /// <summary>Grid side cap, cells (a zoomed-out 4K view fits well inside).</summary>
        private const int RtMaxSide = 192;

        /// <summary>
        /// Realtime shadows are drawn this frame and the classic static
        /// shadows (trees, foliage, rocks) step aside for them, unless the
        /// profile keeps both. Read by the world draw (StaticView, the sort).
        /// </summary>
        internal static bool RealtimeReplacesClassicShadows
        {
            get
            {
                GameScene scene = _rtScene;
                var profile = ProfileManager.CurrentProfile;
                return scene != null && !scene.IsDestroyed && profile != null && scene._rtFrameActive
                    && profile.RealtimeShadows && !profile.RealtimeClassicShadows && scene.UseRealtimeLights;
            }
        }

        /// <summary>The scene drawing Realtime lights; cleared when it unloads (a new scene starts classic).</summary>
        private static GameScene _rtScene;

        /// <summary>
        /// This frame is drawn by Realtime lights: set before the world draw,
        /// when the classic light pass is skipped, and held until the next.
        /// </summary>
        private bool _rtFrameActive;

        /// <summary>A classic shadow is black at 40% (the hue shader's SHADOW): the ambient shadow takes the same share.</summary>
        private const float AmbientShadowShare = 0.4f;

        /// <summary>Share of a light a shadow takes at its base; less toward the tip.</summary>
        private const float LightShadowShare = 1f, LightShadowTip = 0.7f;

        /// <summary>The further share a light's shadow takes at its foot, easing to 40 % of it at the tip (see FillShadowGrid).</summary>
        private const float ContactShadow = 0.45f;

        /// <summary>Lights that cast shadows in one frame, the strongest.</summary>
        private const int MaxShadowLights = 24;

        /// <summary>
        /// A sprite that casts shadows: what it looks like (its art at screen
        /// Px, Py, mirrored when Flip), and where its foot stands, on the
        /// ground in tile units (WX, WY at Z) and on the screen (Foot).
        /// </summary>
        private readonly struct RtCaster
        {
            public readonly Texture2D Texture;
            public readonly Rect2 Source;
            public readonly float Px, Py;
            public readonly bool Flip;
            public readonly float WX, WY;
            public readonly int Z;
            public readonly Vector2 Foot;
            public readonly bool IsMobile;
            /// <summary>Screen pixels the sprite is lifted by (a mobile stepping up or down).</summary>
            public readonly float Lift;
            /// <summary>The atlas page, for grouping draws.</summary>
            public readonly ulong Page;

            public RtCaster(Texture2D texture, Rect2 source, float px, float py, bool flip, float wx, float wy, int z, Vector2 foot, bool isMobile = false, float lift = 0f)
            {
                Texture = texture;
                Source = source;
                Px = px;
                Py = py;
                Flip = flip;
                WX = wx;
                WY = wy;
                Z = z;
                Foot = foot;
                IsMobile = isMobile;
                Lift = lift;
                Page = texture.GetRid().Id;
            }
        }

        /// <summary>Mobile body frames the world draw handed over this frame in place of their classic shadow.</summary>
        private readonly List<RtCaster> _rtMobileCasters = new();

        /// <summary>
        /// MobileView, where it would draw a classic shadow while Realtime
        /// shadows replace them: the frame it would have shadowed, at its
        /// draw position, with the screen point it stands on.
        /// </summary>
        /// <returns>
        /// Whether the realtime pass took it. Not for a mobile under a roof
        /// or off the ground (a floor, a dock, a rooftop): the masks fall on
        /// land only, so there the classic shadow is drawn as before.
        /// </returns>
        internal static bool RecordRealtimeMobileShadow(Mobile owner, Texture2D texture, GUO.Compat.Rectangle uv, int x, int y, bool flip, int footX, int footY)
        {
            GameScene scene = _rtScene;
            if (scene == null || texture == null || owner == null) return false;
            RealtimeLightField field = scene._rtField;
            if (field == null || !field.Contains(owner.X, owner.Y) || field.IsRoofed(owner.X, owner.Y)
                || Math.Abs(owner.Z - field.GroundZ(owner.X, owner.Y)) > 2 || scene._rtMobileCasters.Count >= 4096)
            {
                return false;
            }

            float ox = (owner.Offset.X + owner.Offset.Y) / 44f;
            float oy = (owner.Offset.Y - owner.Offset.X) / 44f;
            scene._rtMobileCasters.Add(new RtCaster(texture, uv.ToGodotF(), x, y, flip,
                owner.X + ox, owner.Y + oy, owner.Z, new Vector2(footX, footY), true, owner.Offset.Z));
            return true;
        }

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
            // Width is a diameter, not a radius. Widening it washed together
            // nearby carriers and lamps into one nearly saturated plaza.
            int radius = info.Texture == null ? 3 : Math.Max(2, (int)MathF.Round(info.UV.Width / RadiusDiameter));
            // Invert the world's isometric projection of the walking offset.
            // The visibility grid stays tile based; radial falloff follows the
            // same continuous position as the mobile or moving effect sprite.
            float ox = (obj.Offset.X + obj.Offset.Y) / 44f;
            float oy = (obj.Offset.Y - obj.Offset.X) / 44f;
            _rtSources[_rtSourceCount++] = new RealtimeLightField.Source(
                obj.X, obj.Y, obj.Z, radius, RealtimeLightColor(light.Color, light.IsHue), ox, oy, RealtimeFlicker(obj, light.IsHue));
        }

        /// <summary>
        /// A flame breathes: two slow sines a few percent deep, phased by the
        /// source so neighbouring torches do not pulse together. A hued light
        /// (a spell, a magic lamp) holds steady.
        /// </summary>
        private static float RealtimeFlicker(GameObject obj, bool isHue)
        {
            if (isHue) return 1f;
            uint id = obj is Entity e ? e.Serial : (uint)(obj.X * 73856093 ^ obj.Y * 19349663);
            float phase = (id % 1000) * 0.00628f;
            // Wrapped before the float: an hour of game clock keeps the sine smooth.
            float t = (Time.Ticks % 3_600_000u) / 1000f;
            return 1f + 0.07f * MathF.Sin(t * 7.3f + phase) + 0.04f * MathF.Sin(t * 17.9f + phase * 2.3f);
        }

        /// <summary>Classic clears its target; the mode's texture is the override.</summary>
        private bool PrepareRealtimeLightTarget(UltimaBatcher2D batcher, RenderTargets renderTargets)
        {
            _rtScene = this;
            // Runs before the world draw (GameScene.Draw: PrepareLightsRendering,
            // then DrawWorld), which fills this list for DrawRealtimeLights.
            _rtMobileCasters.Clear();
            _rtFrameActive = false;
            if (!UseRealtimeLights)
            {
                if (_rtTarget != null) ResetRealtimeLights(renderTargets);
                return false;
            }

            // Built here, before the classic pass is skipped: a target that
            // cannot be built leaves the whole frame classic, lamps included
            // (DrawRealtimeLights then leaves the classic offers alone).
            var lightTarget = renderTargets.LightRenderTarget;
            if (lightTarget == null) return false;
            _rtTarget ??= new RealtimeLightTarget();
            _rtTarget.EnsureSize(lightTarget.Parent, lightTarget.Width, lightTarget.Height);
            if (!_rtTarget.IsBuilt)
            {
                renderTargets.LightOverride = null;
                RealtimeLightTexture = null;
                return false;
            }

            batcher.SetRenderTarget(lightTarget);
            batcher.Clear(new Godot.Color(0, 0, 0, 0));
            batcher.SetRenderTarget(null);
            _rtFrameActive = true;
            return true;
        }

        /// <summary>The scene unloads: classic shadows are no longer this scene's to replace.</summary>
        private void UnloadRealtimeLights()
        {
            if (ReferenceEquals(_rtScene, this)) _rtScene = null;
            _rtTarget?.Dispose();
            _rtTarget = null;
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

            if (!_rtFrameActive)
            {
                // A classic frame (the target would not build): its light
                // offers stay for the classic pass; only ours are dropped.
                _rtSourceCount = 0;
                _rtMobileCasters.Clear();
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

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Built and sized by PrepareRealtimeLightTarget this frame.
                _rtField ??= new RealtimeLightField();

                BuildRealtimeGrid();
                long tGrid = System.Diagnostics.Stopwatch.GetTimestamp();
                _rtField.Compute(RealtimeAmbientLevel(),
                    new ReadOnlySpan<RealtimeLightField.Source>(_rtSources ?? Array.Empty<RealtimeLightField.Source>(), 0, _rtSourceCount));
                long tField = System.Diagnostics.Stopwatch.GetTimestamp();
                DrawRealtimeGeometry(ref matrix);
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _rtGridMs = (tGrid - started) * f;
                _rtFieldMs = (tField - tGrid) * f;

                RealtimeLightTexture = _rtTarget.Texture;
                renderTargets.LightOverride = RealtimeLightTexture;
                _rtLastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _rtMsSum += _rtLastMs;
                _rtMsFrames++;

                if (Time.Ticks - _rtLogAt > 5000 || (Engine.IsEditorHint() && _rtMsFrames == 30))
                {
                    _rtLogAt = Time.Ticks;
                    string line = $"[realtime-lights] grid={_rtField.Width}x{_rtField.Height} lights={_rtSourceCount} " +
                              $"computed={_rtField.LastComputed} cached={_rtField.CacheCount} " +
                              $"quads={_rtTarget.LastQuads} sprites={_rtTarget.LastSprites} roofs={_rtRoofs} above={_rtAbove} darkQuads={_rtDarkQuads} " +
                              $"casters={_rtCasters.Count} shadowLights={_rtShadowLights.Count} shadows={_rtTarget.LastShadows} ms={_rtLastMs:0.00} avg={_rtMsSum / Math.Max(1, _rtMsFrames):0.00}/{_rtMsFrames} (grid {_rtGridMs:0.00} field {_rtFieldMs:0.00} draw {_rtLastMs - _rtGridMs - _rtFieldMs:0.00}) ambient={_rtField.Ambient}";
                    _rtMsSum = 0;
                    _rtMsFrames = 0;
                    Log.Trace(line);
                    // The editor's World tab has no client log file: its console gets the line.
                    if (Engine.IsEditorHint()) GD.Print(line);
                }
            }
            catch (Exception e)
            {
                Log.Error($"[realtime-lights] frame failed, unlit this frame: {e.Message}");
                renderTargets.LightOverride = null;
                RealtimeLightTexture = null;
            }
            finally
            {
                _rtMobileCasters.Clear();
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
            bool shadows = ProfileManager.CurrentProfile.RealtimeShadows;
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
                                    // An upper storey's floor (or a flat rooftop): covers
                                    // and shades what is under it, as a roof does.
                                    _rtField.SetRoof(tx, ty, obj.Z, roofFlag: false);
                                }
                                if (RealtimeLightField.Blocks(data.IsWall, data.IsImpassable, data.IsDoor, data.IsWindow, data.Height)
                                    && (data.IsWall || !shadows || !CastsRealtimeShadowOnly(graphic, ref data)))
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

        /// <summary>
        /// A tree, a bush, a rock or a post narrower than half a tile: it
        /// does not darken whole tiles of the grid (that cut blocky wedges
        /// out of the pools); its sprite casts a shadow instead.
        /// </summary>
        private static bool CastsRealtimeShadowOnly(ushort graphic, ref StaticTiles data)
        {
            // Asked for every impassable static of the grid every frame: the
            // answer is cached per graphic once its art is known (an art not
            // loaded yet is not cached, so it cannot flip the blocker hash later).
            if (graphic < _rtShadowOnly.Length && _rtShadowOnly[graphic] != 0) return _rtShadowOnly[graphic] == 2;
            bool only;
            if (data.IsFoliage || StaticFilters.IsTree(graphic, out _) || StaticFilters.IsRock(graphic))
            {
                only = true;
            }
            else
            {
                ref readonly SpriteInfo art = ref Client.Game.UO.Arts.GetArt(graphic);
                if (art.Texture == null) return false;
                only = art.UV.Width < 24;
            }

            if (graphic < _rtShadowOnly.Length) _rtShadowOnly[graphic] = (byte)(only ? 2 : 1);
            return only;
        }

        /// <summary>Per static graphic: 0 not asked yet, 1 blocks the grid, 2 casts a shadow only.</summary>
        private static readonly byte[] _rtShadowOnly = new byte[0x10000];

        /// <summary>Whether a drawn static casts realtime shadows: it stands up and is not a wall, door, window or roof.</summary>
        private static bool CastsRealtimeShadow(ushort graphic, ref StaticTiles data)
        {
            if (data.IsWall || data.IsRoof || data.IsDoor || data.IsWindow || data.IsSurface && data.Height < 6) return false;
            return data.IsFoliage || StaticFilters.IsTree(graphic, out _) || StaticFilters.IsRock(graphic) || data.Height >= 6;
        }

        /// <summary>The land as lit quads, the tall statics as masked sprites.</summary>
        private void DrawRealtimeGeometry(ref Transform2D matrix)
        {
            _rtTarget.Begin(matrix, _rtField.Ambient, AmbientShadowShare);
            float zoom = matrix.X.X > 0.01f ? matrix.X.X : 1f;
            _rtSprites.Clear();
            _rtCasters.Clear();
            _rtRoofs = 0;
            _rtAbove = 0;
            _rtDarkQuads = 0;
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
                                // Generous: a hill's corners sit well above its tile's own Z.
                                if (ly >= -320 && ly <= _rtTarget.Height / zoom + 120 && lx >= -120 && lx <= _rtTarget.Width / zoom + 120)
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
                            // Flat pieces at ground level take the land's smooth
                            // light; an upper floor is drawn (it is cover) and takes
                            // its own cell's light at its height.
                            bool flat = data.Height < 4 && !data.IsWall && !data.IsRoof && !data.IsFoliage;
                            if (flat && obj.Z < _rtField.GroundZ(obj.X, obj.Y) + RealtimeLightField.UpperFloorZ) continue;

                            // Under drawn cover (a roof, an upper floor below the
                            // view's cut) the world shows the cover, not what is
                            // beneath; no light sprite for that (walls keep theirs:
                            // their outer face shows). Standing on the cover is not
                            // under it, and a viewer under it sees no cover at all.
                            if (!data.IsWall && !data.IsRoof)
                            {
                                short coverZ = _rtField.CoverZ(obj.X, obj.Y);
                                bool coverDrawn = coverZ != RealtimeLightField.NoBlock && coverZ < _maxZ
                                    && !(_noDrawRoofs && _rtField.CoverIsRoofFlag(obj.X, obj.Y));
                                if (coverDrawn && obj.Z < coverZ) continue;
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
                ref StaticTiles sdata = ref statics[obj.Graphic];
                bool roof = sdata.IsRoof;
                if (roof) _rtRoofs++;
                else if (_rtField.IsAboveLight(obj.X, obj.Y, obj.Z)) _rtAbove++;
                // A roof is under the open sky: the open ambient (the roof dim is
                // for what it covers, and darkened every roof by day).
                Color bottom = roof ? _rtField.Ambient : _rtField.CellAt(obj.X, obj.Y, obj.Z);
                Color sky = _rtField.AmbientAt(obj.X, obj.Y, obj.Z);
                // Light falls off going up: a wall's top course is lit less by a
                // lamp at its foot. Flat pieces and roofs are one colour.
                float up = roof || sdata.Height < 4 ? 0f : Math.Min(1f, sdata.Height / 20f) * 0.75f;
                Color left = bottom, right = bottom;
                // A tree, a rock, a post in the open: the side toward a light
                // is brighter, the light half a tile to either side of its foot
                // (a wall keeps one colour: half a tile off is its other face).
                if (!roof && !sdata.IsWall && !sdata.IsDoor && !sdata.IsWindow && sdata.Height >= 4
                    && !_rtField.IsRoofed(obj.X, obj.Y) && !_rtField.IsAboveLight(obj.X, obj.Y, obj.Z))
                {
                    // Only ever brighter: a roofed neighbour's dim must not darken a side.
                    left = Brighter(bottom, bottom.Lerp(RealtimeLightField.Scale(_rtField.RawAt(obj.X - 0.5f, obj.Y + 0.5f)), 0.5f));
                    right = Brighter(bottom, bottom.Lerp(RealtimeLightField.Scale(_rtField.RawAt(obj.X + 0.5f, obj.Y - 0.5f)), 0.5f));
                }
                _rtTarget.AddSprite(
                    new Rect2(px, py, art.UV.Width, art.UV.Height),
                    art.Texture,
                    art.UV.ToGodotF(),
                    left, right, left.Lerp(sky, up), right.Lerp(sky, up));

                if (!roof && CastsRealtimeShadow(obj.Graphic, ref sdata))
                {
                    _rtCasters.Add(new RtCaster(art.Texture, art.UV.ToGodotF(), px, py, false,
                        obj.X, obj.Y, obj.Z, RealtimeScreen(obj.X, obj.Y, obj.Z) + new Vector2(0f, 12f)));
                }
            }

            _rtShadowLights.Clear();
            _rtCasters.AddRange(_rtMobileCasters);
            _rtMobileCasters.Clear();
            if (ProfileManager.CurrentProfile.RealtimeShadows)
            {
                // One draw per atlas page rather than per caster.
                // Stable within a page (by ground position), so overlapping
                // shadows of one light keep their order from frame to frame.
                _rtCasters.Sort(static (a, b) =>
                {
                    int c = a.Page.CompareTo(b.Page);
                    if (c != 0) return c;
                    c = a.WX.CompareTo(b.WX);
                    if (c != 0) return c;
                    c = a.WY.CompareTo(b.WY);
                    if (c != 0) return c;
                    // Stacked casters on one tile: down to the art and its place.
                    c = a.Z.CompareTo(b.Z);
                    if (c != 0) return c;
                    c = a.IsMobile.CompareTo(b.IsMobile);
                    if (c != 0) return c;
                    c = a.Source.Position.X.CompareTo(b.Source.Position.X);
                    if (c != 0) return c;
                    c = a.Source.Position.Y.CompareTo(b.Source.Position.Y);
                    if (c != 0) return c;
                    c = a.Px.CompareTo(b.Px);
                    if (c != 0) return c;
                    c = a.Py.CompareTo(b.Py);
                    if (c != 0) return c;
                    c = a.Flip.CompareTo(b.Flip);
                    if (c != 0) return c;
                    c = a.Lift.CompareTo(b.Lift);
                    if (c != 0) return c;
                    c = a.Foot.X.CompareTo(b.Foot.X);
                    return c != 0 ? c : a.Foot.Y.CompareTo(b.Foot.Y);
                });
                AddRealtimeShadows();
            }

            _rtTarget.End();
        }

        /// <summary>The screen point of a ground point in tile units (cell centres at integers) at height z.</summary>
        private Vector2 RealtimeScreen(float wx, float wy, int z)
            => new((wx - wy) * 22f - _offset.X, (wx + wy) * 22f - (z << 2) - _offset.Y);

        /// <summary>
        /// The shadows, into the mask the land is capped by: first the
        /// ambient's (the classic shadow's shape and share, so a day frame
        /// keeps the shadows it had), then each light's, weakest first, so
        /// where two overlap the stronger one is what shows.
        /// </summary>
        private void AddRealtimeShadows()
        {
            // The ambient's: the classic DrawShadow quad, the sprite squashed
            // to half height and sheared up and to the right, 10 px above its foot.
            // Not under a roof: the sky does not reach there.
            // They go to a mask of their own that multiplies, so two overlapping
            // ambient shadows compound as classic's do, and a light's shadow
            // drawn over one does not erase it.
            // It replaces the classic static shadow, so it follows the same
            // Options (Shadows, Statics shadows); the lights' shadows do not.
            // A mobile's frame only arrives when its classic shadow was due
            // (Shadows on), so the statics' setting gates the statics alone.
            var profile = ProfileManager.CurrentProfile;
            bool sky = profile.ShadowsEnabled && profile.ShadowsStatics;
            foreach (RtCaster c in _rtCasters)
            {
                if (!sky && !c.IsMobile) continue;
                if (_rtField.IsRoofed((int)MathF.Floor(c.WX + 0.5f), (int)MathF.Floor(c.WY + 0.5f))) continue;
                float w = c.Source.Size.X, h = c.Source.Size.Y * 0.5f;
                float ty = c.Py + h - 10f, ratio = h / w;
                Vector2 tl = new(c.Px + w * ratio, ty), tr = new(c.Px + w * (ratio + 1f), ty);
                Vector2 br = new(c.Px + w, ty + h), bl = new(c.Px, ty + h);
                // A mirrored frame: the same quad, its texture columns reversed.
                if (c.Flip) _rtTarget.AddAmbientShadow(c.Texture, c.Source, tr, tl, bl, br);
                else _rtTarget.AddAmbientShadow(c.Texture, c.Source, tl, tr, br, bl);
            }

            // The lights', the strongest few, weakest first.
            // Ranked without the flicker (it would reshuffle equal lamps every
            // frame), ties by position, so the chosen set and its order hold still.
            if (_rtStrength.Length < _rtField.LitCount) _rtStrength = new float[_rtField.LitCount * 2];
            for (int n = 0; n < _rtField.LitCount; n++)
            {
                RealtimeLightField.Source ls = _rtField.LitSource(n);
                _rtStrength[n] = ls.Radius * Math.Max(ls.Color.R, Math.Max(ls.Color.G, ls.Color.B));
                _rtShadowLights.Add(n);
            }
            _rtShadowLights.Sort(_rtStrengthOrder ??= Comparer<int>.Create((a, b) =>
            {
                int c = _rtStrength[a].CompareTo(_rtStrength[b]);
                if (c != 0) return c;
                RealtimeLightField.Source la = _rtField.LitSource(a), lb = _rtField.LitSource(b);
                return la.X != lb.X ? la.X.CompareTo(lb.X) : la.Y != lb.Y ? la.Y.CompareTo(lb.Y) : a.CompareTo(b);
            }));
            if (_rtShadowLights.Count > MaxShadowLights) _rtShadowLights.RemoveRange(0, _rtShadowLights.Count - MaxShadowLights);

            foreach (int n in _rtShadowLights)
            {
                RealtimeLightField.Source s = _rtField.LitSource(n);
                float lx = s.PosX, ly = s.PosY;
                foreach (RtCaster c in _rtCasters)
                {
                    float dx = c.WX - lx, dy = c.WY - ly;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    // Its own lamp post, or past the light's reach.
                    if (d < 0.75f || d >= s.Radius) continue;
                    // A light standing well above the caster's top throws no shadow on the ground beside it.
                    if (s.Z > c.Z + 30) continue;
                    Color blocked = _rtField.SourceAt(n, c.WX, c.WY);
                    if (blocked.R + blocked.G + blocked.B < 0.02f) continue;

                    // Away from the light along the ground, in screen space;
                    // the sprite's columns run across it, its rows along it.
                    Vector2 u = new Vector2((dx - dy) * 22f, (dx + dy) * 22f).Normalized();
                    // The sprite's left column on the screen's left, whichever
                    // way the shadow falls, so a sign does not cast its mirror.
                    Vector2 v = new(u.Y, -u.X);
                    if (v.X > 0f) v = -v;
                    if (c.Flip) v = -v;
                    float w = c.Source.Size.X, h = c.Source.Size.Y;
                    // Short near the light, longer toward the pool's edge, and
                    // about classic's half height at most: a full-height canopy
                    // laid down from a torch covers the whole lawn and reads
                    // as dimness rather than as a shadow.
                    // From the steady radius: the flame's breathing must not stretch it.
                    float length = h * Math.Clamp(0.25f + 0.45f * d / s.Radius, 0.25f, 0.6f);
                    Vector2 foot = c.Foot;
                    Vector2 tip = foot + u * length;
                    Vector2 half = v * (w * 0.5f), spread = v * (w * 0.55f);
                    FillShadowGrid(tip + spread, tip - spread, foot + half, foot - half, in c, n, LightShadowShare);
                    _rtTarget.AddShadow(c.Texture, c.Source, _rtShadowPoints, _rtShadowShades);
                }
            }

        }

        private float[] _rtStrength = Array.Empty<float>();
        private Comparer<int> _rtStrengthOrder;

        private readonly Vector2[] _rtShadowPoints = new Vector2[RealtimeLightTarget.ShadowGrid * RealtimeLightTarget.ShadowGrid];
        private readonly Color[] _rtShadowShades = new Color[RealtimeLightTarget.ShadowGrid * RealtimeLightTarget.ShadowGrid];

        /// <summary>
        /// A shadow's grid between its four screen corners (the sprite's top
        /// row on <paramref name="topLeft"/>..<paramref name="topRight"/>), and
        /// at each vertex the light left on the ground there: without light
        /// <paramref name="light"/>'s share, fading toward the sprite's top row.
        /// </summary>
        private void FillShadowGrid(Vector2 topLeft, Vector2 topRight, Vector2 bottomLeft, Vector2 bottomRight,
            in RtCaster caster, int light, float share)
        {
            // Contact darkening: classic's shadow is 40 % black at any hour;
            // a light's shadow takes its light and a further ContactShadow of
            // what is left at the foot (40 % of that at the tip), as strongly
            // as the light strikes the caster, so it still reads where it
            // runs past the bright core of the pool.
            Color atFoot = _rtField.SourceAt(light, caster.WX, caster.WY);
            float strike = Math.Clamp((atFoot.R * 0.2126f + atFoot.G * 0.7152f + atFoot.B * 0.0722f) / 0.3f, 0f, 1f);
            Vector2 origin = RealtimeScreen(caster.WX, caster.WY, caster.Z) - new Vector2(0f, caster.Lift);
            const int g = RealtimeLightTarget.ShadowGrid, cells = RealtimeLightTarget.ShadowCells;
            for (int j = 0; j < g; j++)
            {
                float fj = (float)j / cells;
                Vector2 left = topLeft.Lerp(bottomLeft, fj), right = topRight.Lerp(bottomRight, fj);
                // Fades from the base (row cells) to the tip (row 0).
                float k = share * (LightShadowTip + (1f - LightShadowTip) * fj);
                for (int i = 0; i < g; i++)
                {
                    Vector2 p = left.Lerp(right, (float)i / cells);
                    _rtShadowPoints[j * g + i] = p;
                    // The screen offset back to the ground, in tile units.
                    Vector2 off = p - origin;
                    float wx = caster.WX + (off.X + off.Y) / 44f, wy = caster.WY + (off.Y - off.X) / 44f;
                    Color blocked = Share(_rtField.SourceAt(light, wx, wy), k);
                    Color kept = RealtimeLightField.Without(_rtField.RawAt(wx, wy), blocked);
                    _rtShadowShades[j * g + i] = Share(kept, 1f - ContactShadow * strike * (0.4f + 0.6f * fj));
                }
            }
        }

        private static Color Share(Color c, float k) => new(c.R * k, c.G * k, c.B * k);

        private static Color Brighter(Color a, Color b) => new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

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

            Color c0 = _rtField.Corner(x, y), c1 = _rtField.Corner(x + 1, y), c2 = _rtField.Corner(x + 1, y + 1), c3 = _rtField.Corner(x, y + 1);
            if (Math.Min(Math.Min(c0.R, c1.R), Math.Min(c2.R, c3.R)) < 0.05f) _rtDarkQuads++;
            Vector2 centre = (top + right + bottom + left) * 0.25f;
            _rtTarget.AddLand(top, right, bottom, left, centre, c0, c1, c2, c3, _rtField.Cell(x, y));
        }

        /// <summary>A light's tint: the classic light palette, or the hue when the offer is hued.</summary>
        private static Godot.Color RealtimeLightColor(ushort color, bool isHue)
        {
            if (color == 0) return RealtimeLightField.NeutralLight;

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
