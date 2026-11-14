// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039, drawing path retired 2026-11-14): the light-offer collection and
// tile-grid helpers that lighting speed mode (ADR-0048) shares, kept out of the ported GameScene.cs.

using System;
using Godot;
using GUO.Assets;
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Utility;

namespace GUO.Game.Scenes
{
    internal partial class GameScene
    {
        // PORT DEVIATION (GUO): light offers (ADR-0039/0048). The classic loop
        // collects light offers as screen positions; this path records the
        // same offers with their tile for lighting speed mode, which turns them
        // into GPU lights. Off (classic lighting): nothing is recorded.
        private RealtimeLightField _rtField;
        private RealtimeLightField.Source[] _rtSources;
        private int _rtSourceCount;

        /// <summary>Margin of tiles around the visible chunks, so a light just off screen still reaches in.</summary>
        private const int RtMargin = 8;

        /// <summary>Effective screen diameter per tile after corner averaging and falloff.</summary>
        private const float RadiusDiameter = 44f;

        /// <summary>Grid side cap, cells (a zoomed-out 4K view fits well inside).</summary>
        private const int RtMaxSide = 192;

        /// <summary>Existing world offers take priority; bursts use remaining budget.</summary>
        private const int RtSourceCapacity = RealtimeLightEffects.FrameLightCap;

        /// <summary>Per frame, before the world draw: the spell-burst table follows the Spell lighting option.</summary>
        private static void PrepareSpellLights()
        {
            if (!SpellLighting.Enabled(ProfileManager.CurrentProfile)) RealtimeLightEffects.ClearSpells();
            else RealtimeLightEffects.LimitSpells(SpellLighting.Budget(ProfileManager.CurrentProfile));
        }

        /// <summary>
        /// Called from AddLight once a light offer is accepted: keep its tile.
        /// <paramref name="graphic"/> is the lit item's graphic (the light
        /// profile file's key, with the light id).
        /// </summary>
        private void RecordRealtimeLight(GameObject obj, ushort graphic, ref LightData light)
        {
#if DEBUG
            ObserveAttributionLight(obj,"record-source-entry");
#endif
            if (!UseSpeedLights || obj == null) {
#if DEBUG
                ObserveAttributionLight(obj,"record-source-mode-null-reject");
#endif
                return;
            } // PORT DEVIATION (GUO): ADR-0048 same offers.
            _rtSources ??= new RealtimeLightField.Source[RtSourceCapacity];
            if (_rtSourceCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT) {
#if DEBUG
                ObserveAttributionLight(obj,"record-source-capacity-reject");
#endif
                return;
            }

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
#if DEBUG
            ObserveAttributionLight(obj,"record-source-added",lightId:light.ID);
#endif
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
