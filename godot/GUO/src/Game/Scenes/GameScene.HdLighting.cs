// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned; adapted from JarJar e4a3cd35 and 2259b5f7 (ADR-0027).
using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Renderer.PostFx;
using GUO.Utility.Logging;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Game.Scenes
{
    internal partial class GameScene
    {
        // PORT DEVIATION (GUO): realistic lights (PointLight2D + shadows in a
        // parallel world-sized viewport). Null until the toggle flips on;
        // off-state never builds it (pixel-identical). Hot-applies: built and
        // torn down here, no restart.
        private RealisticLightViewport _realisticLights;
        private RealisticLightViewport.LightOffer[] _realisticOffers;
        private RealisticLightViewport.OccluderOffer[] _realisticOccluders;
        private long _realisticLogAt;
        private int _lastRealisticOccluders;
        // PORT DEVIATION (GUO): HD light Phase 3 (sun + height buffer). The
        // buffer is never built unless an enabled pass declares height_tex
        // (hd_sun, or Phase 4 hd_gi for cascade occlusion);
        // off-state tears it down, pixel-identical.
        private HdHeightBuffer _hdHeight;
        private int _hdHeightFrame;
        private bool _hdSunBooted;
        private long _hdSunLastTicks;
        private long _hdSunLogAt;
        private int _lastHdStamps;
        // PORT DEVIATION (GUO): HD light Phase 1 (HDR + flicker + hd_* PostFx).
        // A superset of the realistic viewport path; off never builds anything.
        public bool UseHDLight { get; private set; }
        private bool _hdMissingReported;
        private RenderTargets _hdTargets;

        private void PrepareHdLighting(RenderTargets targets)
        {
            var profile = ProfileManager.CurrentProfile;
            var lighting = Client.Game?.UO?.FileManager?.Content?.Lighting;
            bool requested = profile?.UseHDLight ?? false;
            bool ready = lighting != null && (!PostFxStack.MobileTier || lighting.Preset.Passes.Count <= PostFxStack.MaxPassesMobile);
            bool active = HdLightingPolicy.Enabled(requested, ready, _world.Player.IsDead && profile?.EnableBlackWhiteEffect == true);
            if (requested && !ready && !_hdMissingReported)
            {
                _hdMissingReported = true;
                GameActions.Print(_world, "HD lighting needs a compatible activated lighting pack from the Store (at most four passes on mobile). Restart after activation. Classic lighting is active.");
            }
            if (!requested) _hdMissingReported = false;
            if (!active && UseHDLight) ResetHdLighting();
            _hdTargets = targets;
            UseHDLight = active;
            targets.HdEnabled = active;
            targets.RealisticLightOverride = null;
            PostFxStack.Instance.SetHdLighting(active ? lighting : null);
            if (active)
            {
                _realisticOffers ??= new RealisticLightViewport.LightOffer[RealisticLightViewport.MaxLights];
                _realisticOccluders ??= new RealisticLightViewport.OccluderOffer[RealisticLightViewport.MaxOccluders];
            }
        }

        private void ResetHdLighting()
        {
            if (_hdTargets != null)
            {
                _hdTargets.HdEnabled = false;
                _hdTargets.RealisticLightOverride = null;
                _hdTargets = null;
            }
            _realisticLights?.Dispose();
            _realisticLights = null;
            _hdHeight?.Dispose();
            _hdHeight = null;
            _realisticOffers = null;
            _realisticOccluders = null;
            _hdSunBooted = false;
            _hdHeightFrame = 0;
            UseHDLight = false;
            HdWeather.Reset();
            PostFxStack.Instance.HdHeightTex = null;
            PostFxStack.Instance.SetHdLighting(null);
        }

        private bool PrepareHdLightTarget(UltimaBatcher2D batcher, RenderTargets targets)
        {
            if (!UseHDLight) return false;
            batcher.SetRenderTarget(targets.LightRenderTarget);
            batcher.Clear(new Godot.Color(0, 0, 0, 0));
            batcher.SetRenderTarget(null);
            _lightCount = 0;
            return true;
        }
        private bool LightsNeeded => UseLights || UseAltLights || UseHDLight;

        private void AddHdLight(GameObject obj, GameObject lightObject, int x, int y)
        {
            if (
                _lightCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
                || !LightsNeeded
                || obj == null
            )
            {
                return;
            }

            bool canBeAdded = true;

            int testX = obj.X + 1;
            int testY = obj.Y + 1;

            GameObject tile = _world.Map.GetTile(testX, testY);

            sbyte z5 = (sbyte)(obj.Z + 5);

            if (tile != null)
            {
                for (GameObject o = tile; o != null; o = o.TNext)
                {
                    if (
                        (!(o is Static s) || s.ItemData.IsTransparent)
                            && (!(o is Multi m) || m.ItemData.IsTransparent)
                        || !o.AllowedToDraw
                        // PORT DEVIATION (GUO): a hidden roof must not
                        // occlude. Roofs over the player fade to AlphaHue 0
                        // (ProcessAlpha, via _maxZ or _noDrawRoofs) yet stay
                        // AllowedToDraw, so without this every torch under a
                        // hidden roof reads as "under opaque roof" and is
                        // culled: rooms stay dark at night while the street
                        // outside lights up. Invisible occludes nothing.
                        || o.AlphaHue == 0
                    )
                    {
                        continue;
                    }

                    if (o.Z < _maxZ && o.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded)
            {
                // PORT DEVIATION (GUO): staged splat walls/roofs occlude like
                // statics (anchor tile, same Z window). Hidden victims stay
                // skipped above, so without this light leaks through replacement
                // walls. Preview overlays never hide anything, so they never
                // block. Other facets never draw, so they never block.
                // Volumetric cover: one placement spans tiles past its
                // anchor, so the light's screen point inside its queued pick
                // rect covers those tiles too (empty rects fall back to the
                // anchor alone).
                foreach (SplatObject splat in _splatObjects.Values)
                {
                    // Same hidden rule as the static loop above: a faded
                    // splat (AlphaHue 0) is not drawn and occludes nothing.
                    if (splat == null || splat.IsDestroyed || !splat.AllowedToDraw || splat.AlphaHue == 0)
                    {
                        continue;
                    }

                    bool anchor = splat.X == testX && splat.Y == testY;
                    bool inside = false;
                    if (!anchor)
                    {
                        try
                        {
                            Rectangle r = splat.PickRect;
                            if (r.Width > 0 && r.Height > 0
                                && x >= r.X && x < r.X + r.Width
                                && y >= r.Y && y < r.Y + r.Height)
                            {
                                inside = true;
                            }
                        }
                        catch
                        {
                        }

                        if (!inside)
                        {
                            continue;
                        }
                    }

                    if (splat.Placement == null || splat.Placement.Facet != _world.MapIndex)
                    {
                        continue;
                    }

                    if (splat.Placement.Name != null && splat.Placement.Name.Contains("preview"))
                    {
                        continue;
                    }

                    bool transparent = false;

                    try
                    {
                        var statics = Client.Game.UO.FileManager.TileData.StaticData;

                        if (
                            splat.TryGetVictimGraphic(out ushort blockerGraphic)
                            && blockerGraphic != 0
                            && blockerGraphic < statics.Length
                        )
                        {
                            transparent = statics[blockerGraphic].IsTransparent;
                        }
                    }
                    catch
                    {
                    }

                    if (transparent)
                    {
                        continue;
                    }

                    if (splat.Z < _maxZ && splat.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded)
            {
                ref LightData light = ref _lights[_lightCount];

                ushort graphic = lightObject.Graphic;

                // PORT DEVIATION (GUO): splats carry Graphic 0x0001; the
                // victim's graphic decides the light shape below and the tint
                // after it, so a splat replacing a lamp keeps its lamp light.
                ushort lightGraphic = graphic;

                if (
                    lightObject is SplatObject splatGraphic
                    && splatGraphic.TryGetVictimGraphic(out ushort victimGraphic)
                    && victimGraphic != 0
                )
                {
                    lightGraphic = victimGraphic;
                }

                // PORT DEVIATION (GUO): unlit fixtures emit nothing in all
                // paths (classic + realistic + HD share _lights via this
                // method). See _unlitFixtureGraphics: IsLight is true on some
                // unlit graphics (0x0A28, 0x09FB, 0x1849) and the server keeps
                // LightID non-zero when doused, so neither gate distinguishes.
                if (HdLightingPolicy.IsUnlitFixture(lightGraphic))
                {
                    return;
                }

                if (
                    lightGraphic >= 0x3E02 && lightGraphic <= 0x3E0B
                    || lightGraphic >= 0x3914 && lightGraphic <= 0x3929
                    || lightGraphic == 0x0B1D
                )
                {
                    light.ID = 2;
                }
                else
                {
                    if (obj == lightObject && obj is Item item)
                    {
                        light.ID = item.LightID;
                    }
                    else if (lightObject is Item it)
                    {
                        light.ID = (byte)it.ItemData.LightIndex;

                        if (obj is Mobile mob)
                        {
                            switch (mob.Direction)
                            {
                                case Direction.Right:
                                    y += 33;
                                    x += 22;

                                    break;

                                case Direction.Left:
                                    y += 33;
                                    x -= 22;

                                    break;

                                case Direction.East:
                                    x += 22;
                                    y += 55;

                                    break;

                                case Direction.Down:
                                    y += 55;

                                    break;

                                case Direction.South:
                                    x -= 22;
                                    y += 55;

                                    break;
                            }
                        }
                    }
                    else if (obj is SplatObject splatObj)
                    {
                        // PORT DEVIATION (GUO): splats have no IsLight tiledata
                        // of their own, so the victim's tiledata decides. The
                        // victim's lamp light when it has one, else the
                        // standard point light (mobiles use 1): without this
                        // the static path below reads tile 0x0001's layer,
                        // which is no light at all, and lamp replacements
                        // would emit nothing.
                        byte id = 1;

                        try
                        {
                            var statics = Client.Game.UO.FileManager.TileData.StaticData;

                            if (
                                splatObj.TryGetVictimGraphic(out ushort splatVictim)
                                && splatVictim != 0
                                && splatVictim < statics.Length
                            )
                            {
                                byte v = statics[splatVictim].Layer;

                                if (v != 0 && v < LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                                {
                                    id = v;
                                }
                            }
                        }
                        catch
                        {
                        }

                        light.ID = id;
                    }
                    else if (obj is Mobile _)
                    {
                        light.ID = 1;
                    }
                    else
                    {
                        ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic];
                        light.ID = data.Layer;
                    }
                }

                light.Color = 0;
                light.IsHue = false;

                if (ProfileManager.CurrentProfile.UseColoredLights)
                {
                    if (light.ID > 200)
                    {
                        light.Color = (ushort)(light.ID - 200);
                        light.ID = 1;
                    }

                    if (LightColors.GetHue(lightGraphic, out ushort color, out bool ishue))
                    {
                        light.Color = color;
                        light.IsHue = ishue;
                    }
                }

                if (light.ID >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                {
                    return;
                }

                if (light.Color != 0)
                {
                    light.Color++;
                }

                light.DrawX = x;
                light.DrawY = y;
                _lightCount++;
            }
        }

        private void DrawHdLighting(RenderTargets renderTargets, ref Transform2D matrix)
        {
            if (!UseHDLight) return;
            // Current-frame weather and sun reach the same frame's lights.
            UpdateHdSun(renderTargets, ref matrix);
            UpdateRealisticLights(renderTargets, ref matrix);
            _lightCount = 0;
        }

        /// <summary>
        /// One frame of the realistic/HD path: same offers as the classic loop
        /// above (same order, same colors), ambient from IsometricLevel.
        /// HD adds HDR (UseHdr2D) + per-light TIME flicker; realistic stays LDR.
        /// </summary>
        private void UpdateRealisticLights(RenderTargets renderTargets, ref Transform2D matrix)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                if (renderTargets.LightRenderTarget == null)
                {
                    renderTargets.RealisticLightOverride = null;

                    return;
                }

                int width = renderTargets.LightRenderTarget.Width;
                int height = renderTargets.LightRenderTarget.Height;

                if (width <= 0 || height <= 0)
                {
                    renderTargets.RealisticLightOverride = null;

                    return;
                }

                bool hd = UseHDLight;
                _realisticLights ??= new RealisticLightViewport(Client.Game.UO.FileManager.Lights);
                _realisticLights.EnsureSize(Client.Game, width, height, hd);
                _realisticLights.HdFlicker = hd;
                _realisticLights.TimeSeconds = GUO.Time.Ticks / 1000.0;
                // PORT DEVIATION (GUO): HD light Phase 5. Thunder timing drives
                // real ambient/flash energy (see RealisticLightViewport).
                _realisticLights.WeatherFlash = HdWeather.Flash;
                _realisticLights.WeatherDim = HdWeather.Dim;

                if (!_realisticLights.IsBuilt)
                {
                    renderTargets.RealisticLightOverride = null;

                    return;
                }

                int count = Math.Min(_lightCount, RealisticLightViewport.MaxLights);

                for (int i = 0; i < count; i++)
                {
                    ref LightData l = ref _lights[i];
                    _realisticOffers[i] = new RealisticLightViewport.LightOffer(
                        l.ID, l.Color, l.IsHue, l.DrawX, l.DrawY);
                }

                float ambient = _world.Light.IsometricLevel;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    ambient -= 0.04f;
                }

                int shadowBudget = GetRealisticShadowBudget(count);
                int occluderBudget = GetRealisticOccluderBudget();
                int occluderCount = CollectRealisticOccluders(occluderBudget);
                _lastRealisticOccluders = occluderCount;

                _realisticLights.Update(
                    _realisticOffers, count,
                    _realisticOccluders, occluderCount,
                    ambient, matrix, shadowBudget);

                renderTargets.RealisticLightOverride = _realisticLights.Texture;
            }
            finally
            {
                // Measured cost, one line every 10 s while the toggle is on:
                // lights, shadows allowed, occluders, and the CPU update time.
                // The viewport itself renders on the GPU; this is the offer +
                // occluder scan on the main thread.
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _realisticLogAt > 10000)
                {
                    _realisticLogAt = now;
                    Log.Trace(
                        $"[realistic-lights] hd={UseHDLight} lights={Math.Min(_lightCount, RealisticLightViewport.MaxLights)} " +
                        $"shadows<={GetRealisticShadowBudget(Math.Min(_lightCount, RealisticLightViewport.MaxLights))} " +
                        $"occluders={_lastRealisticOccluders} " +
                        $"update={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}ms");
                }
            }
        }

        /// <summary>
        /// Zoom/device governor: how many of the pooled lights may cast
        /// shadows this frame. Mobile and web tiers allow fewer; extreme
        /// zooms halve the budget either way (large light rects cost more).
        /// Offer order decides who keeps a shadow: first N, same as _lights.
        /// </summary>
        private int GetRealisticShadowBudget(int lightCount)
        {
            bool mobile = PlatformDefaults.Platform != ProfilePlatform.Desktop;
            int budget = mobile ? 12 : 32;

            try
            {
                float zoom = Camera.Zoom;

                if (zoom < 0.6f || zoom > 1.0f)
                {
                    budget /= 2;
                }
            }
            catch
            {
            }

            return Math.Max(4, Math.Min(budget, RealisticLightViewport.MaxLights));
        }

        private int GetRealisticOccluderBudget()
        {
            bool mobile = PlatformDefaults.Platform != ProfilePlatform.Desktop;

            return mobile ? 128 : RealisticLightViewport.MaxOccluders;
        }

        /// <summary>
        /// Rect occluders from tiledata, reusing AddLight's occlusion
        /// predicates: skip transparent and undrawable victims, and keep the
        /// same Z window (below _maxZ) so eaves and floors above stop behaving
        /// like walls. Wall/Door/Roof/NoShoot tiles qualify; Height sizes the
        /// rect (1 Z = 4 px) AND the window: a wall's base sits at the light's
        /// own level, so the lower bound tests its top (Z + Height), not its
        /// base â€” testing the base like AddLight's diagonal-tile check does
        /// excludes every same-level wall and the night stays shadowless
        /// (measured: Britain street, 0 occluders before, full walls after).
        /// </summary>
        private int CollectRealisticOccluders(int budget)
        {
            int count = 0;

            try
            {
                if (_visibleChunks.Count == 0 || _world.Player == null)
                {
                    return 0;
                }

                int cap = Math.Min(budget, RealisticLightViewport.MaxOccluders);
                sbyte z5 = (sbyte)(_world.Player.Z + 5);
                var statics = Client.Game.UO.FileManager.TileData.StaticData;

                foreach (var chunk in _visibleChunks)
                {
                    if (chunk == null || chunk.IsDestroyed)
                    {
                        continue;
                    }

                    for (int x = 0; x < 8 && count < cap; x++)
                    {
                        for (int y = 0; y < 8 && count < cap; y++)
                        {
                            for (
                                GameObject obj = chunk.GetHeadObject(x, y);
                                obj != null && count < cap;
                                obj = obj.TNext)
                            {
                                // Invisible casts no shadow: hidden roofs (AlphaHue
                                // 0 via _maxZ/_noDrawRoofs) would otherwise
                                // shadow the very room the torch lights, the
                                // realistic form of the classic cull above.
                                if (obj.IsDestroyed || !obj.AllowedToDraw || obj.AlphaHue == 0)
                                {
                                    continue;
                                }

                                if (obj is Land || obj is Mobile)
                                {
                                    continue;
                                }

                                if (!(obj is Static) && !(obj is Multi) && !(obj is Item))
                                {
                                    continue;
                                }

                                ushort graphic = obj.Graphic;

                                if (graphic >= statics.Length)
                                {
                                    continue;
                                }

                                ref StaticTiles data = ref statics[graphic];

                                // Same Z window as AddLight's blockers, tested
                                // against the tile's top: a wall rises Height
                                // above its base, and its base is at the
                                // light's own level.
                                if (obj.Z + data.Height < z5 || obj.Z >= _maxZ)
                                {
                                    continue;
                                }

                                if (data.IsTransparent)
                                {
                                    continue;
                                }

                                if (!data.IsWall && !data.IsDoor && !data.IsRoof && !data.IsNoShoot && !data.IsWindow)
                                {
                                    continue;
                                }

                                // A wall that hosts its own torch must not
                                // shadow it: the PointLight2D sits inside the
                                // wall's rect occluder (same tile, torch
                                // Z-elevated so screen-distance checks miss)
                                // and Godot eclipses the whole room. Classic
                                // has no shadows, so carving this hole keeps
                                // the room lit like the classic path.
                                // Worn/held torches need the same hole: the
                                // light lives on the mobile's tile (floor),
                                // but torch direction offsets (+22/+55) can
                                // push it inside the neighbouring wall's rect.
                                // Same-tile statics alone miss that, so any
                                // current light offer inside this occluder's
                                // rect also carves it out.
                                bool hostsLight = false;

                                for (
                                    GameObject o = chunk.GetHeadObject(x, y);
                                    o != null;
                                    o = o.TNext)
                                {
                                    if (o.IsDestroyed || !o.AllowedToDraw || o.AlphaHue == 0)
                                    {
                                        continue;
                                    }

                                    ushort g = o.Graphic;
                                    bool isLight = false;

                                    // PORT DEVIATION (GUO): unlit fixtures host
                                    // no light (see _unlitFixtureGraphics), so a
                                    // wall sharing their tile must still occlude.
                                    if (!HdLightingPolicy.IsUnlitFixture(g))
                                    {
                                        if (g < statics.Length && statics[g].IsLight)
                                        {
                                            isLight = true;
                                        }
                                        else if (
                                            g >= 0x3E02 && g <= 0x3E0B
                                            || g >= 0x3914 && g <= 0x3929
                                            || g == 0x0B1D)
                                        {
                                            isLight = true;
                                        }
                                    }

                                    if (isLight)
                                    {
                                        hostsLight = true;

                                        break;
                                    }
                                }

                                if (!hostsLight && _lightCount > 0)
                                {
                                    float occlX = obj.RealScreenPosition.X + 22;
                                    float occlY = obj.RealScreenPosition.Y + 22;
                                    float wallPx = data.Height * 4f;
                                    int n = Math.Min(_lightCount, LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT);

                                    for (int li = 0; li < n; li++)
                                    {
                                        ref LightData l = ref _lights[li];
                                        float dx = l.DrawX - occlX;
                                        float dy = l.DrawY - occlY;

                                        if (dx >= -22 && dx <= 22 && dy >= -22 - wallPx && dy <= 22)
                                        {
                                            hostsLight = true;

                                            break;
                                        }
                                    }
                                }

                                if (hostsLight)
                                {
                                    continue;
                                }

                                _realisticOccluders[count++] = new RealisticLightViewport.OccluderOffer(
                                    obj.RealScreenPosition.X + 22,
                                    obj.RealScreenPosition.Y + 22,
                                    data.Height);
                            }
                        }
                    }

                    if (count >= cap)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }

            return count;
        }

        /// <summary>
        /// HD light Phase 3, one frame: advance the sun clock (profile hour,
        /// opt-in cycle), set the march governors, and refill
        /// the quarter-res height buffer on its throttle. The hd_sun uniforms
        /// themselves are pushed by the PostFx stack from HdSun statics, so
        /// the sun swings and recolors even on frames the buffer skips.
        /// </summary>
        private void UpdateHdSun(RenderTargets renderTargets, ref Transform2D matrix)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                Profile currentProfile = ProfileManager.CurrentProfile;

                if (!_hdSunBooted)
                {
                    _hdSunBooted = true;

                    HdSun.Hour = Math.Clamp(currentProfile?.HdSunHour ?? 12f, 0f, 23.99f);
                    _hdSunLastTicks = GUO.Time.Ticks;
                }

                bool auto = currentProfile?.HdSunAuto ?? false;
                HdSun.Auto = auto;

                long nowTicks = GUO.Time.Ticks;
                double dt = (nowTicks - _hdSunLastTicks) / 1000.0;
                _hdSunLastTicks = nowTicks;

                // PORT DEVIATION (GUO): HD light Phase 5. Weather follows the
                // world on every frame the HD foundation runs (both Draw
                // branches call here, even the sun-only noon one), feeding the
                // viewport ambient, the sun dim and the hd_wet/hd_flash/gi_wet
                // uniforms. Same dt as the sun clock; Classic never calls it.
                HdWeather.Update(_world, dt);

                if (dt > 0 && dt < 5.0)
                {
                    HdSun.Advance(dt);
                }

                if (!auto) HdSun.Hour = Math.Clamp(currentProfile?.HdSunHour ?? 12f, 0f, 23.99f);

                // Zoom/mobile governors: fewer march taps and a coarser,
                // lazier buffer off the desktop and at extreme zooms.
                bool mobile = PlatformDefaults.Platform != ProfilePlatform.Desktop;
                float zoom = 1f;

                try
                {
                    zoom = Camera.Zoom;
                }
                catch
                {
                }

                HdSun.Steps = mobile || zoom < 0.6f || zoom > 1.5f ? 6 : 10;
                HdSun.PxPerZ = 4f * Math.Clamp(zoom, 0.25f, 4f);
                // PORT DEVIATION (GUO): HD light Phase 4. Same tier rule for
                // the cascade rings (desktop 10: all 3, mobile/zoom 6:
                // near+mid); the shader gates rings on gi_steps, no recompile.
                HdGi.Steps = HdGi.For(mobile, zoom);

                bool sunPass = GUO.Renderer.PostFx.PostFxStack.Instance.ActivePreset.Passes.Exists(
                    p => p.Enabled && string.Equals(p.Shader, "hd_sun", StringComparison.OrdinalIgnoreCase));
                // Phase 4: hd_gi marches the same height buffer (occlusion),
                // so it keeps the buffer alive even with the sun pass off.
                bool giPass = GUO.Renderer.PostFx.PostFxStack.Instance.ActivePreset.Passes.Exists(
                    p => p.Enabled && string.Equals(p.Shader, "hd_gi", StringComparison.OrdinalIgnoreCase));

                if (!sunPass && !giPass)
                {
                    if (_hdHeight != null)
                    {
                        _hdHeight.Dispose();
                        _hdHeight = null;
                    }

                    GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = null;

                    return;
                }

                if (renderTargets.LightRenderTarget == null)
                {
                    return;
                }

                int width = renderTargets.LightRenderTarget.Width;
                int height = renderTargets.LightRenderTarget.Height;

                if (width <= 0 || height <= 0)
                {
                    return;
                }

                _hdHeight ??= new HdHeightBuffer();
                _hdHeight.EnsureSize(Client.Game, width, height, mobile);

                if (!_hdHeight.IsBuilt)
                {
                    GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = null;

                    return;
                }

                int interval = mobile ? 6 : 3;
                _hdHeightFrame++;

                if ((_hdHeightFrame % interval) != 1
                    && GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex != null)
                {
                    return;
                }

                _hdHeight.BeginFill();
                CollectHdHeight(matrix);
                _lastHdStamps = _hdHeight.LastStamps;
                GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = _hdHeight.Texture;
            }
            finally
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _hdSunLogAt > 10000)
                {
                    _hdSunLogAt = now;
                    HdSun.SunSample s = HdSun.Evaluate(HdSun.Hour);
                    Log.Trace(
                        $"[hd-sun] hour={HdSun.Hour:0.00}{(HdSun.Auto ? " auto" : "")} " +
                        $"rgb=({s.Color.R:0.00},{s.Color.G:0.00},{s.Color.B:0.00}) x{s.Energy:0.00} " +
                        $"steps={HdSun.Steps} stamps={_lastHdStamps} " +
                        $"gi={HdGi.Steps} " +
                        $"wet={HdWeather.Wetness:0.00} dim={HdWeather.Dim:0.00} flash={HdWeather.Flash:0.00} " +
                        $"update={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}ms");
                }
            }
        }

        /// <summary>
        /// Stamp the height buffer from what the frame already walks: land
        /// tiles as the base (their Z), then statics/multis/items with
        /// tiledata Height (Z + Height, wall-anchored rects like the
        /// occluders), splats last inside their PickRects (victim height).
        /// Hidden (AlphaHue 0) and transparent things stamp nothing, and
        /// mobiles are skipped: the buffer is terrain + structures.
        /// </summary>
        private void CollectHdHeight(Transform2D view)
        {
            if (_hdHeight == null || !_hdHeight.IsBuilt)
            {
                return;
            }

            try
            {
                float inv = 1f / _hdHeight.Scale;
                var statics = Client.Game.UO.FileManager.TileData.StaticData;

                foreach (var chunk in _visibleChunks)
                {
                    if (chunk == null || chunk.IsDestroyed)
                    {
                        continue;
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            for (
                                GameObject obj = chunk.GetHeadObject(x, y);
                                obj != null;
                                obj = obj.TNext)
                            {
                                if (obj.IsDestroyed || !obj.AllowedToDraw || obj.AlphaHue == 0)
                                {
                                    continue;
                                }

                                if (obj is Land)
                                {
                                    StampHdWorld(
                                        view, inv,
                                        obj.RealScreenPosition.X, obj.RealScreenPosition.Y,
                                        obj.RealScreenPosition.X + 44f, obj.RealScreenPosition.Y + 44f,
                                        obj.Z);

                                    continue;
                                }

                                if (obj is Mobile)
                                {
                                    continue;
                                }

                                if (!(obj is Static) && !(obj is Multi) && !(obj is Item))
                                {
                                    continue;
                                }

                                ushort graphic = obj.Graphic;

                                if (graphic >= statics.Length)
                                {
                                    continue;
                                }

                                ref StaticTiles data = ref statics[graphic];

                                if (data.IsTransparent || data.Height == 0 || obj.Z >= _maxZ)
                                {
                                    continue;
                                }

                                float cx = obj.RealScreenPosition.X + 22f;
                                float cy = obj.RealScreenPosition.Y + 22f;
                                float wallPx = data.Height * 4f;

                                StampHdWorld(
                                    view, inv,
                                    cx - 22f, cy - 22f - wallPx,
                                    cx + 22f, cy + 22f,
                                    obj.Z + data.Height);
                            }
                        }
                    }
                }

                foreach (SplatObject splat in _splatObjects.Values)
                {
                    if (splat == null || splat.IsDestroyed || !splat.AllowedToDraw || splat.AlphaHue == 0)
                    {
                        continue;
                    }

                    if (splat.Placement == null || splat.Placement.Facet != _world.MapIndex)
                    {
                        continue;
                    }

                    if (splat.Placement.Name != null && splat.Placement.Name.Contains("preview"))
                    {
                        continue;
                    }

                    Rectangle r = splat.PickRect;

                    if (r.Width <= 0 || r.Height <= 0)
                    {
                        continue;
                    }

                    int top = splat.Z + 8;

                    try
                    {
                        if (splat.TryGetVictimGraphic(out ushort victim) && victim != 0 && victim < statics.Length)
                        {
                            top = splat.Z + Math.Max((int)statics[victim].Height, 8);
                        }
                    }
                    catch
                    {
                    }

                    StampHdWorld(view, inv, r.X, r.Y, r.X + r.Width, r.Y + r.Height, top);
                }
            }
            catch
            {
            }
        }

        /// <summary>One world-pixel rect into buffer pixels through the view.</summary>
        private void StampHdWorld(Transform2D view, float inv, float x0, float y0, float x1, float y1, int topZ)
        {
            if (_hdHeight == null)
            {
                return;
            }

            Vector2 a = view * new Vector2(x0, y0);
            Vector2 b = view * new Vector2(x1, y1);

            _hdHeight.Stamp(
                new Rect2(
                    Math.Min(a.X, b.X) * inv, Math.Min(a.Y, b.Y) * inv,
                    Math.Abs(b.X - a.X) * inv, Math.Abs(b.Y - a.Y) * inv),
                topZ);
        }

    }
}
