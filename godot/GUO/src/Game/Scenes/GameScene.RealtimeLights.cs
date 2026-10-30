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
        private int _rtRoofs, _rtAbove, _rtDarkQuads, _rtWallFaces;
        private byte[] _rtFieldBytes;

        /// <summary>Margin of tiles around the visible chunks, so a light just off screen still reaches in.</summary>
        private const int RtMargin = 8;

        /// <summary>Effective screen diameter per tile after corner averaging and falloff.</summary>
        private const float RadiusDiameter = 44f;

        /// <summary>Grid side cap, cells (a zoomed-out 4K view fits well inside).</summary>
        private const int RtMaxSide = 192;

        /// <summary>Existing world offers take priority; bursts use remaining budget.</summary>
        private const int RtSourceCapacity = RealtimeLightEffects.FrameLightCap;

        /// <summary>On, in game, and not during the black-and-white death frame (the classic path runs then).</summary>
        public bool UseRealtimeLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.RealtimeLights
            && !Renderer.SpeedMode.SpeedModeDebug.Active // PORT DEVIATION (GUO): ADR-0048 replaces the CPU field.
            && _world.InGame
            && !(_world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect);

        /// <summary>The mode's light texture for this frame, or null for the classic target.</summary>
        internal Texture2D RealtimeLightTexture { get; private set; }

        /// <summary>
        /// Called from AddLight once a light offer is accepted: keep its tile.
        /// <paramref name="graphic"/> is the lit item's graphic (the light
        /// profile file's key, with the light id).
        /// </summary>
        private void RecordRealtimeLight(GameObject obj, ushort graphic, ref LightData light)
        {
            if ((!UseRealtimeLights && !UseSpeedLights) || obj == null) return; // PORT DEVIATION (GUO): ADR-0048 same offers.
            _rtSources ??= new RealtimeLightField.Source[RtSourceCapacity];
            if (_rtSourceCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT) return;

            ref readonly var info = ref Client.Game.UO.Lights.GetLight(light.ID);
            // Width is a diameter, not a radius. Widening it washed together
            // nearby carriers and lamps into one nearly saturated plaza.
            int radius = info.Texture == null ? 3 : Math.Max(2, (int)MathF.Round(info.UV.Width / RadiusDiameter));

            // The light profile file (section 45): unset fields keep the offer's.
            var profiles = RealtimeLightProfiles.Current;
            LightProfile p = profiles.Resolve(graphic, light.ID);
            if (p.Radius > 0) radius = p.Radius;
            if (p.RadiusScale > 0f) radius = Math.Max(1, (int)MathF.Round(radius * p.RadiusScale));
            Godot.Color color = p.Color ?? RealtimeLightColor(light.Color, light.IsHue);
            LightCurve curve = p.Curve ?? LightCurve.B;
            float energy = p.Energy > 0f ? p.Energy : RealtimeLightField.Energy;
            float[] custom = curve == LightCurve.Custom ? profiles.CustomCurve(p.CustomCurve) : null;
            // Invert the world's isometric projection of the walking offset.
            // The visibility grid stays tile based; radial falloff follows the
            // same continuous position as the mobile or moving effect sprite.
            float ox = (obj.Offset.X + obj.Offset.Y) / 44f;
            float oy = (obj.Offset.Y - obj.Offset.X) / 44f;
            _rtSources[_rtSourceCount++] = new RealtimeLightField.Source(
                obj.X, obj.Y, obj.Z, radius, color, ox, oy, curve, energy, custom);
        }

        /// <summary>Classic clears its target; the mode's texture is the override.</summary>
        private bool PrepareRealtimeLightTarget(UltimaBatcher2D batcher, RenderTargets renderTargets)
        {
            if (!SpellLighting.Enabled(ProfileManager.CurrentProfile)) RealtimeLightEffects.ClearSpells();
            else RealtimeLightEffects.LimitSpells(SpellLighting.Budget(ProfileManager.CurrentProfile));
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

            long t0 = RealtimeLightsDebug.Stamp();
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
                long t1 = RealtimeLightsDebug.Stamp();

                // Spell-effect lights (RealtimeLightEffects) after the offers.
                _rtSources ??= new RealtimeLightField.Source[RtSourceCapacity];
                int temporary = RealtimeLightEffects.Collect(Time.Ticks, _rtSources, _rtSourceCount,
                    SpellLighting.Enabled(ProfileManager.CurrentProfile));
                _rtSourceCount += temporary;
                var sources = new ReadOnlySpan<RealtimeLightField.Source>(_rtSources, 0, _rtSourceCount);
                _rtField.Compute(RealtimeAmbientLevel(), sources);
                long t2 = RealtimeLightsDebug.Stamp();
                DrawRealtimeGeometry(ref matrix);
                long t3 = RealtimeLightsDebug.Stamp();

                RealtimeLightsDebug.Record(new RealtimeLightsDebug.Budget(_rtSourceCount, temporary, _rtField.LastComputed,
                    RealtimeLightsDebug.Ms(t0, t1), RealtimeLightsDebug.Ms(t1, t2), RealtimeLightsDebug.Ms(t2, t3)));
                if (RealtimeLightsDebug.DumpDue())
                {
                    RealtimeLightsDebug.WriteDump(RealtimeLightDump(sources));
                    // The light target as drawn last frame, beside the text dump.
                    RealtimeLightsDebug.WriteTargetImage(_rtTarget.Texture);
                }

                RealtimeLightTexture = _rtTarget.Texture;
                renderTargets.LightOverride = RealtimeLightTexture;

                if (Time.Ticks - _rtLogAt > 5000)
                {
                    _rtLogAt = Time.Ticks;
                    string line = $"[realtime-lights] grid={_rtField.Width}x{_rtField.Height} lights={_rtSourceCount} " +
                              $"computed={_rtField.LastComputed} cached={_rtField.CacheCount} " +
                              $"quads={_rtTarget.LastQuads} sprites={_rtTarget.LastSprites} roofs={_rtRoofs} above={_rtAbove} darkQuads={_rtDarkQuads} faces={_rtWallFaces} fine={_rtField.FineScale} ambient={_rtField.Ambient}";
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
        /// The light under a world-target pixel (the space of
        /// <c>SelectedObject.TranslatedMousePositionByViewport</c>): the tile, read at
        /// that tile's ground Z, and the field there. Null when the mode has no field.
        /// </summary>
        internal string RealtimeLightReadout(int pixelX, int pixelY)
        {
            if (_rtField == null || _rtField.Width <= 1) return null;
            float u = pixelX + _offset.X, v = pixelY + _offset.Y;
            int z = _world.Player?.Z ?? 0;
            (int x, int y) = RealtimeLightField.ScreenToTile(u, v, z);
            // The ground's height moves the diamond: settle on the tile whose ground Z it is.
            for (int i = 0; i < 3; i++)
            {
                int gz = _rtField.GroundZ(x, y);
                if (gz == z) break;
                z = gz;
                (x, y) = RealtimeLightField.ScreenToTile(u, v, z);
            }

            Godot.Color c = _rtField.Cell(x, y);
            short block = _rtField.BlockTop(x, y), cover = _rtField.CoverZ(x, y);
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"pixel={pixelX},{pixelY} tile={x},{y},{z} light={c.R:0.000},{c.G:0.000},{c.B:0.000} " +
                $"block={(block == RealtimeLightField.NoBlock ? "-" : block.ToString())} " +
                $"cover={(cover == RealtimeLightField.NoBlock ? "-" : cover.ToString())} roofed={_rtField.IsRoofed(x, y)}");
        }

        /// <summary>The light dump for this frame: readouts, then the grid (visible chunks upper case).</summary>
        private string RealtimeLightDump(ReadOnlySpan<RealtimeLightField.Source> sources)
        {
            var visible = new HashSet<long>();
            foreach (Chunk chunk in _visibleChunks)
            {
                if (chunk != null && !chunk.IsDestroyed) visible.Add(((long)chunk.X << 32) | (uint)chunk.Y);
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var mouse = SelectedObject.TranslatedMousePositionByViewport;
            int px = _world.Player?.X ?? int.MinValue, py = _world.Player?.Y ?? int.MinValue;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Create(inv, $"# realtime-lights dump ticks={Time.Ticks} level={RealtimeAmbientLevel():0.000} offset={_offset.X},{_offset.Y} maxZ={_maxZ}"));
            sb.Append("# mouse ").AppendLine(RealtimeLightReadout(mouse.X, mouse.Y));
            if (_world.Player != null)
            {
                Godot.Color pc = _rtField.Cell(px, py);
                sb.AppendLine(string.Create(inv, $"# player tile={px},{py},{_world.Player.Z} light={pc.R:0.000},{pc.G:0.000},{pc.B:0.000}"));
            }
            sb.Append("# profiles ").AppendLine(RealtimeLightProfiles.Current.Source);
            sb.Append(_rtField.DumpAscii(sources, (x, y) => visible.Contains(((long)(x >> 3) << 32) | (uint)(y >> 3)), px, py));
            return sb.ToString();
        }

        /// <summary>
        /// The classic grey for this frame: the shard's levels with the profile's
        /// Light level override applied as the packet handler applies it, so the
        /// override also holds where no light packet has arrived (the editor's world).
        /// </summary>
        private float RealtimeAmbientLevel()
        {
            var profile = ProfileManager.CurrentProfile;
#if GUO_LABS
            // PORT DEVIATION (GUO): local lighting test clock never edits shard state.
            if (GUO.Labs.LightingClock.LightingTestClock.TryBrightness(profile, out float localBrightness)) return localBrightness;
#endif
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
                                    // An upper storey's floor (or a flat rooftop): covers
                                    // and shades what is under it, as a roof does.
                                    _rtField.SetRoof(tx, ty, obj.Z, roofFlag: false);
                                }
                                if (RealtimeLightField.Blocks(data.IsWall, data.IsImpassable, data.IsDoor, data.IsWindow, data.Height))
                                {
                                    // A wall stands on a line of its tile, read from
                                    // its art; light reaches each side of it, not
                                    // through it. Anything else blocks its whole tile.
                                    WallFaces faces = data.IsWall ? WallFaces.For(graphic) : default;
                                    if (faces.Count == 0)
                                    {
                                        _rtField.AddBlocker(tx, ty, obj.Z + data.Height);
                                    }
                                    else
                                    {
                                        _rtField.AddEdge(tx, ty, faces.First.AlongX, faces.First.Offset, obj.Z + data.Height);
                                        if (faces.Count > 1) _rtField.AddEdge(tx, ty, faces.Second.AlongX, faces.Second.Offset, obj.Z + data.Height);
                                    }
                                }
                                break;
                            }
                        }
                    }
                    _rtField.SetGround(tx, ty, groundZ);
                }
            }
        }

        /// <summary>The land as quads, the tall statics as masked sprites, each pixel lit from the field at its world point.</summary>
        private void DrawRealtimeGeometry(ref Transform2D matrix)
        {
            _rtTarget.Begin(matrix, _rtField.Ambient);
            int bytes = _rtField.FineWidth * _rtField.FineHeight * 4;
            if (_rtFieldBytes == null || _rtFieldBytes.Length != bytes) _rtFieldBytes = new byte[bytes];
            _rtField.FillTexture(_rtFieldBytes);
            _rtTarget.SetField(_rtFieldBytes, _rtField.FineWidth, _rtField.FineHeight,
                _rtField.OriginX, _rtField.OriginY, _rtField.Width, _rtField.Height, _rtField.RoofAmbient);

            float zoom = matrix.X.X > 0.01f ? matrix.X.X : 1f;
            _rtSprites.Clear();
            _rtRoofs = 0;
            _rtAbove = 0;
            _rtDarkQuads = 0;
            _rtWallFaces = 0;
            ref var statics = ref Client.Game.UO.FileManager.TileData.StaticData;

            // Light receivers must include cached chunk terrain and GPU
            // statics. Neither is guaranteed to appear in RenderLists' loose
            // tile lists or sorted world queue. Keep this traversal independent
            // of the world's submission backend and its cover-land duplicates.
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
                                if (RealtimeLandVisible(land, zoom))
                                {
                                    AddRealtimeLand(land);
                                }
                                continue;
                            }

                            if (!RealtimeSpriteCandidate(obj, zoom, statics)) continue;

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
                AddRealtimeSprite(obj, in art, ref statics[obj.Graphic]);
            }

            _rtTarget.End();
        }

        /// <summary>A land tile the light target can show. Generous: a hill's corners sit well above its tile's own Z.</summary>
        private bool RealtimeLandVisible(Land land, float zoom)
        {
            int ly = ((land.X + land.Y) * 22 - (land.Z << 2)) - _offset.Y - 22;
            int lx = ((land.X - land.Y) * 22) - _offset.X - 22;
            return ly >= -320 && ly <= _rtTarget.Height / zoom + 120 && lx >= -120 && lx <= _rtTarget.Width / zoom + 120;
        }

        /// <summary>Whether <paramref name="obj"/> gets a light sprite this frame (the pass's own filters).</summary>
        private bool RealtimeSpriteCandidate(GameObject obj, float zoom, StaticTiles[] statics)
        {
            if (obj is not (Static or Multi or Item) || !obj.AllowedToDraw || obj.Z >= _maxZ)
            {
                return false;
            }

            ushort graphic = obj.Graphic;
            if (graphic >= statics.Length) return false;
            ref StaticTiles data = ref statics[graphic];
            if (_noDrawRoofs && data.IsRoof) return false;
            // Flat pieces at ground level take the land's light
            // (it is under them); an upper floor is drawn (it is
            // cover) and takes its own light at its height.
            bool flat = data.Height < 4 && !data.IsWall && !data.IsRoof && !data.IsFoliage;
            if (flat && obj.Z < _rtField.GroundZ(obj.X, obj.Y) + RealtimeLightField.UpperFloorZ) return false;

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
                if (coverDrawn && obj.Z < coverZ) return false;
            }

            // Only what the target can show.
            int sy = ((obj.X + obj.Y) * 22 - (obj.Z << 2)) - _offset.Y - 22;
            int sx = ((obj.X - obj.Y) * 22) - _offset.X - 22;
            return !(sy < -220 || sy > _rtTarget.Height / zoom + 220 || sx < -220 || sx > _rtTarget.Width / zoom + 220);
        }

        /// <summary>Where a wall face's light is read: this far in front of its line, in tiles (clear of the samples behind it).</summary>
        private const float RtFaceFront = 0.3f;

        /// <summary>
        /// One static's sprite. A roof, or anything above every light on its
        /// tile, is flat roof ambient. A wall, window or door is lit along its
        /// visible face (<see cref="WallFaces"/>): each column reads the field
        /// just in front of the face at that point, so the face takes the light
        /// on its own side of the wall with the ground's falloff, and a corner
        /// piece is two quads. Anything else reads the field across its tile.
        /// Every pixel then fades toward the roof ambient with its height.
        /// </summary>
        private void AddRealtimeSprite(GameObject obj, in SpriteInfo art, ref StaticTiles sdata)
        {
            int sx = ((obj.X - obj.Y) * 22) - _offset.X - 22;
            int sy = ((obj.X + obj.Y) * 22 - (obj.Z << 2)) - _offset.Y - 22;
            int w = art.UV.Width, h = art.UV.Height;
            // DrawStatic's placement: centred on the tile, bottom at its base.
            int px = sx + 22 - (w >> 1);
            int py = sy + 44 - h;
            Rect2 source = art.UV.ToGodotF();

            bool roof = sdata.IsRoof;
            // Part of the cover over its tile (a roof piece without the roof
            // flag, an upper floor's edge): the light under it does not reach it.
            short coverZ = _rtField.CoverZ(obj.X, obj.Y);
            bool cover = !sdata.IsWall && !sdata.IsWindow && !sdata.IsDoor
                && coverZ != RealtimeLightField.NoBlock && obj.Z >= coverZ;
            if (roof || cover || _rtField.IsAboveLight(obj.X, obj.Y, obj.Z))
            {
                if (roof) _rtRoofs++;
                else _rtAbove++;
                Color flat = RealtimeLightTarget.Flat(_rtField.RoofAmbient);
                _rtTarget.AddSprite(new Rect2(px, py, w, h), art.Texture, source, flat, flat, flat, flat);
                return;
            }

            WallFaces faces = sdata.IsWall || sdata.IsWindow || sdata.IsDoor ? WallFaces.For(obj.Graphic) : default;
            if (faces.Count == 0)
            {
                AddRealtimeQuad(obj, art.Texture, source, px, py, sx, sy, 0, w, w, h, default, false);
                return;
            }

            _rtWallFaces += faces.Count;
            AddRealtimeQuad(obj, art.Texture, source, px, py, sx, sy, faces.First.From, faces.First.To, w, h, faces.First, true);
            if (faces.Count > 1)
            {
                AddRealtimeQuad(obj, art.Texture, source, px, py, sx, sy, faces.Second.From, faces.Second.To, w, h, faces.Second, true);
            }
        }

        /// <summary>Columns [from, to) of a sprite as one quad, each corner carrying its world point and height.</summary>
        private void AddRealtimeQuad(GameObject obj, Texture2D texture, Rect2 source, int px, int py, int sx, int sy,
            int from, int to, int w, int h, in WallFaces.Face face, bool isFace)
        {
            if (to <= from) return;
            float su = source.Size.X / w;
            var rect = new Rect2(px + from, py, to - from, h);
            var src = new Rect2(source.Position.X + from * su, source.Position.Y, (to - from) * su, source.Size.Y);
            float x0 = px + from - sx, x1 = px + to - sx, y0 = py - sy, y1 = py + h - sy;
            _rtTarget.AddSprite(rect, texture, src,
                RealtimeVertex(obj, x0, y0, in face, isFace),
                RealtimeVertex(obj, x1, y0, in face, isFace),
                RealtimeVertex(obj, x1, y1, in face, isFace),
                RealtimeVertex(obj, x0, y1, in face, isFace));
        }

        /// <summary>
        /// A sprite corner at tile-box pixel (bx, by): the world point its column
        /// stands on and its height above that point's base. On a face the point
        /// is on the face's line, pushed <see cref="RtFaceFront"/> to the side the
        /// camera sees; off a face it is on the line across the tile's middle.
        /// </summary>
        private Color RealtimeVertex(GameObject obj, float bx, float by, in WallFaces.Face face, bool isFace)
        {
            // Box: top corner (22, 0) is world (x, y); X - 22 = 22 (dx - dy), Y = 22 (dx + dy).
            float across = (bx - 22f) / 22f;
            // Standing above the ground (a gable, an upper wall): that much
            // higher above the light's floor, in pixels (4 per Z).
            float lift = Math.Max(0, obj.Z - _rtField.GroundZ(obj.X, obj.Y)) * 4f;
            if (!isFace)
            {
                return _rtTarget.At(obj.X + (1f + across) * 0.5f, obj.Y + (1f - across) * 0.5f, 44f - by + lift);
            }

            if (face.AlongX)
            {
                float dy = face.Offset;
                return _rtTarget.At(obj.X + across + dy, obj.Y + dy + RtFaceFront, bx + face.K - by + lift);
            }

            float dx = face.Offset;
            return _rtTarget.At(obj.X + dx + RtFaceFront, obj.Y + dx - across, face.K - bx - by + lift);
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

            if (_rtField.Cell(x, y).R < 0.05f) _rtDarkQuads++;
            // Each corner is its world point: the shader reads the field per pixel.
            _rtTarget.AddLand(top, right, bottom, left,
                _rtTarget.At(x, y), _rtTarget.At(x + 1, y), _rtTarget.At(x + 1, y + 1), _rtTarget.At(x, y + 1));
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
