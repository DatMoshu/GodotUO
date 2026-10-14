// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Utility;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The parallel world-sized light viewport for realistic 2D lights.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO): the "AI Light" realistic-lighting path (Phase 1).
    /// World shaders stay <c>unshaded</c> — nothing in the world draw changes.
    /// Lights live here instead, in a world-sized <see cref="SubViewport"/>
    /// built on the <see cref="RenderTarget2D"/> pattern (nearest throughout,
    /// always-updated, no 3D), and its texture is composited where
    /// <c>LightRenderTarget</c> goes today with the same blend and hue.
    /// When the profile toggle is off this object is never built, so the
    /// off-state is pixel-identical to the classic path.
    /// </para>
    /// <para>
    /// Phase 0 finding (pinned Godot 4.7.2): a <see cref="SubViewport"/> with
    /// a white base rect, a <see cref="CanvasModulate"/> ambient, pooled
    /// <see cref="PointLight2D"/> nodes and rect <see cref="LightOccluder2D"/>
    /// nodes renders light with shadows into its texture, and compositing
    /// that texture with the darkness blend (world × light) leaves the world
    /// draw itself untouched. PointLight2D needs whole textures, so per-ID
    /// standalone textures are cached from <see cref="LightsLoader"/> — never
    /// atlas sub-rects.
    /// </para>
    /// </remarks>
    internal sealed class RealisticLightViewport : IDisposable
    {
        public const int MaxLights = 100;

        /// <summary>Hard cap on pooled occluders; the governor lowers it further.</summary>
        public const int MaxOccluders = 256;

        public readonly struct LightOffer
        {
            public readonly uint ID;
            public readonly ushort Color;
            public readonly bool IsHue;
            public readonly int DrawX;
            public readonly int DrawY;

            public LightOffer(uint id, ushort color, bool isHue, int drawX, int drawY)
            {
                ID = id;
                Color = color;
                IsHue = isHue;
                DrawX = drawX;
                DrawY = drawY;
            }
        }

        public readonly struct OccluderOffer
        {
            /// <summary>Tile centre in world pixels (RealScreenPosition + 22).</summary>
            public readonly float WorldX;
            public readonly float WorldY;
            /// <summary>Tiledata Height in Z units (1 Z = 4 px).</summary>
            public readonly byte Height;

            public OccluderOffer(float worldX, float worldY, byte height)
            {
                WorldX = worldX;
                WorldY = worldY;
                Height = height;
            }
        }

        private SubViewport _viewport;
        private CanvasModulate _modulate;
        private ColorRect _base;
        private Node2D _lightHost;
        private Node2D _occluderHost;

        private readonly PointLight2D[] _lights = new PointLight2D[MaxLights];
        private readonly LightOccluder2D[] _occluders = new LightOccluder2D[MaxOccluders];

        private readonly Dictionary<uint, Texture2D> _lightTextures = new Dictionary<uint, Texture2D>();
        private readonly LightsLoader _lightsLoader;

        private int _width;
        private int _height;
        private bool _disposed;

        // PORT DEVIATION (GUO): HD light Phase 1. HDR (Viewport use_hdr_2d,
        // verified on pinned 4.7.2 via --doctool: Viewport.use_hdr_2d +
        // rendering/viewport/hdr_2d + RenderingServer.viewport_set_use_hdr_2d)
        // and per-light TIME-hash torch flicker. Godot's Environment
        // tonemap/glow are 3D-only (Environment.xml: glow/tonemap members),
        // unavailable to canvas_item, so tonemapping is an ACES approx in the
        // hd_tonemap PostFx pass, not here.
        // PORT DEVIATION (GUO): HD light Phase 2 (normal-map pipeline). UO art
        // ships no normal maps and baking height-to-normal per unique graphic
        // (ArtLoader/Art cache) would need a second world-sized normal buffer
        // mirrored with every sprite draw; instead the normal is a cheap
        // screen-space luma-gradient Sobel in hd_tonemap (same 3x3 as
        // normal_light.gdshader), and the light direction is the light
        // buffer's own screen-space gradient (uphill = toward the torch).
        // World and lights live in separate viewports (this file vs the
        // batcher's world target), so PointLight2D's normal-map path cannot
        // apply; the composite N-dot-L term IS the per-light color/energy
        // response — the buffer already carries each light's color x HDR
        // energy x flicker, and facing pixels are warmed toward its hue.
        // No world-shader change, Nearest throughout, strength 0 = Phase 1.
        private bool _hdr;
        /// <summary>HD flicker on: per-light TIME hash on Energy (subtle, seeded per light).</summary>
        public bool HdFlicker { get; set; }
        /// <summary>Seconds for the flicker hash (GameScene feeds Time ticks).</summary>
        public double TimeSeconds { get; set; }
        // PORT DEVIATION (GUO): HD light Phase 5 (weather-aware light). The
        // GameScene feeds HdWeather.Flash/Dim here every frame; Update folds
        // them into the ambient (clouds dim, lightning lifts) and gives torch
        // energies a small flash lift, so thunder timing drives real light
        // energy, not just a tint. 0/0 is Phase 1-4, bit-identical.
        /// <summary>Lightning envelope 0..1 (HdWeather.Flash).</summary>
        public float WeatherFlash { get; set; }
        /// <summary>Cloud dim 0..1 (HdWeather.Dim).</summary>
        public float WeatherDim { get; set; }

        public RealisticLightViewport(LightsLoader lightsLoader)
        {
            _lightsLoader = lightsLoader;
        }

        public bool IsBuilt => _viewport != null && !_disposed;

        public Texture2D Texture => _viewport?.GetTexture();

        public int Width => _width;
        public int Height => _height;

        /// <summary>Build (or resize) the viewport under <paramref name="host"/>.</summary>
        /// <param name="hdr">HD Phase 1: UseHdr2D on, so light energies above 1 survive for the hd_* PostFx passes. LDR default keeps the realistic path unchanged.</param>
        public void EnsureSize(Node host, int width, int height, bool hdr = false)
        {
            if (width <= 0 || height <= 0 || host == null)
            {
                return;
            }

            if (_viewport != null)
            {
                if (_width == width && _height == height && _hdr == hdr && _viewport.IsInsideTree())
                {
                    return;
                }

                Teardown();
            }

            _width = width;
            _height = height;
            _hdr = hdr;

            _viewport = new SubViewport
            {
                Size = new Vector2I(width, height),
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Disable3D = true,
                CanvasItemDefaultTextureFilter =
                    Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };
            // HD Phase 1: HDR precision for >1 light energies. Nearest kept
            // above, so pixel-exactness is unchanged; LDR Classic output is
            // untouched because this viewport is never built when both toggles
            // are off (GameScene teardown).
            _viewport.Set("use_hdr_2d", hdr);

            _modulate = new CanvasModulate { Color = new Color(0.3f, 0.3f, 0.3f) };

            _base = new ColorRect
            {
                Color = Colors.White,
                Position = Vector2.Zero,
                Size = new Vector2(width, height),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };

            _lightHost = new Node2D();
            _occluderHost = new Node2D();

            _viewport.AddChild(_modulate);
            _viewport.AddChild(_base);
            _viewport.AddChild(_occluderHost);
            _viewport.AddChild(_lightHost);
            host.AddChild(_viewport);

            for (int i = 0; i < MaxLights; i++)
            {
                var light = new PointLight2D
                {
                    Enabled = true,
                    Visible = false,
                    Energy = 1f,
                    TextureScale = 1f,
                };
                light.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
                _lights[i] = light;
                _lightHost.AddChild(light);
            }

            for (int i = 0; i < MaxOccluders; i++)
            {
                var occluder = new LightOccluder2D { Visible = false };
                occluder.Occluder = new OccluderPolygon2D
                {
                    Closed = true,
                    // Pinned-build API (4.7.2): Polygon is Vector2[], and
                    // PackedVector2Array does not exist in GodotSharp here.
                    Polygon = new Vector2[]
                    {
                        new Vector2(-22, -22), new Vector2(22, -22),
                        new Vector2(22, 22), new Vector2(-22, 22),
                    },
                };
                _occluders[i] = occluder;
                _occluderHost.AddChild(occluder);
            }
        }

        public void Teardown()
        {
            _lightTextures.Clear();

            for (int i = 0; i < MaxLights; i++)
            {
                _lights[i] = null;
            }

            for (int i = 0; i < MaxOccluders; i++)
            {
                _occluders[i] = null;
            }

            _modulate = null;
            _base = null;
            _lightHost = null;
            _occluderHost = null;

            if (_viewport != null)
            {
                try
                {
                    _viewport.QueueFree();
                }
                catch
                {
                }

                _viewport = null;
            }

            _width = 0;
            _height = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Teardown();
        }

        /// <summary>
        /// Push one frame of offers into the pooled nodes. Screen-space:
        /// <paramref name="view"/> maps world pixels to viewport pixels.
        /// </summary>
        public void Update(
            LightOffer[] offers,
            int offerCount,
            OccluderOffer[] occluders,
            int occluderCount,
            float ambient,
            Transform2D view,
            int shadowBudget)
        {
            if (_viewport == null || _modulate == null || _base == null || offers == null || occluders == null)
            {
                return;
            }

            // HD blowout fix: null/size guard + opaque clear. This viewport
            // is TransparentBg, so any pixel the opaque white base does not
            // cover stays transparent (0,0,0,0): sampled as light_tex it
            // either darkens to a black box (rgb 0, alpha 1 on a null
            // texture) or skips darkening (alpha 0 guard in hd_tonemap).
            // The PostFx stack needs no such clear: its base is the world
            // texture itself, whose target clears opaque black. So the fix
            // belongs here: keep the white base opaque and viewport-sized.
            if (_viewport.Size.X <= 0 || _viewport.Size.Y <= 0)
            {
                return;
            }

            _base.Color = Colors.White;
            _base.Size = new Vector2(_viewport.Size.X, _viewport.Size.Y);

            float clamped = Math.Clamp(ambient, _hdr ? 0.12f : 0f, 1f);
            // HD night floor: IsometricLevel can sit near 0.06 at its darkest,
            // and the sun x blob double plus ACES crushes that to black. The
            // floor keeps torch pools over dim albedo instead of void. LDR
            // realistic (non-HD) keeps 0 so its path is identical; Classic
            // never builds this viewport either way.
            // HD Phase 5: clouds dim the ambient, lightning lifts it (thunder
            // timing drives real energy). 0/0 skips the math exactly.
            float dimmed = clamped;
            float flash = Math.Clamp(WeatherFlash, 0f, 1f);
            float dim = Math.Clamp(WeatherDim, 0f, 1f);
            if (flash > 0.001f || dim > 0.001f)
            {
                dimmed = Math.Clamp(clamped * (1f - 0.38f * dim) + flash * 0.9f, 0f, 1.5f);
            }
            _modulate.Color = new Color(dimmed, dimmed, dimmed);

            float z = view.X.X;
            if (z <= 0f || float.IsNaN(z) || float.IsInfinity(z))
            {
                z = 1f;
            }

            int count = Math.Min(offerCount, MaxLights);
            int shadows = Math.Clamp(shadowBudget, 0, MaxLights);

            for (int i = 0; i < MaxLights; i++)
            {
                PointLight2D light = _lights[i];

                if (light == null)
                {
                    continue;
                }

                if (i >= count)
                {
                    light.Visible = false;
                    continue;
                }

                LightOffer offer = offers[i];
                Texture2D texture = GetLightTexture(offer.ID);

                if (texture == null)
                {
                    light.Visible = false;
                    continue;
                }

                Vector2 screen = view * new Vector2(offer.DrawX, offer.DrawY);
                light.Position = screen;
                light.Texture = texture;
                light.TextureScale = z;
                light.Color = MapLightColor(offer.Color, offer.IsHue);
                // HD: torch flicker just above 1. Per-light TIME hash,
                // subtle (±3.5%), seeded by id + draw pos so torches never
                // pulse in sync. Energies above 1 survive only when the
                // viewport is HDR; LDR realistic path keeps Energy 1.
                // HD Phase 5: the seed is quantized to the tile (44 px), so a
                // held torch turning (direction offsets of 22-55 px) or a
                // torch swaying a pixel does not re-phase its flicker; and
                // lightning lifts torch energies a little with the ambient.
                float energy = HdFlicker
                    ? HdEnergy(offer, TimeSeconds)
                    : 1f;
                if (flash > 0.001f)
                {
                    energy *= 1f + 0.2f * flash;
                }
                light.Energy = energy;
                light.ShadowEnabled = i < shadows;
                light.Visible = true;
            }

            int occ = Math.Min(occluderCount, MaxOccluders);

            for (int i = 0; i < MaxOccluders; i++)
            {
                LightOccluder2D occluder = _occluders[i];

                if (occluder == null)
                {
                    continue;
                }

                if (i >= occ)
                {
                    occluder.Visible = false;
                    continue;
                }

                OccluderOffer offer = occluders[i];
                float wallPx = offer.Height * 4f;
                Vector2 screen = view * new Vector2(offer.WorldX, offer.WorldY);

                occluder.Position = screen;
                occluder.Scale = new Vector2(z, z);

                if (occluder.Occluder is OccluderPolygon2D poly)
                {
                    poly.Polygon = new Vector2[]
                    {
                        new Vector2(-22, -22 - wallPx),
                        new Vector2(22, -22 - wallPx),
                        new Vector2(22, 22),
                        new Vector2(-22, 22),
                    };
                }

                occluder.Visible = true;
            }
        }

        /// <summary>
        /// HD flicker: 1.05 base (just above 1 so only true hotspots cross
        /// the hd_bloom/hd_heat ~1.0 thresholds) modulated ±3.5% by two
        /// detuned sines hashed per light. Seeds mix id + draw pos; TIME
        /// comes from TimeSeconds (seconds).
        /// </summary>
        private static float HdEnergy(LightOffer offer, double time)
        {
            // Integer hash -> [0,1): torches don't share a phase. The position
            // seeds at tile granularity (Phase 5 flicker-sync fix): DrawX/Y
            // are world pixels, so raw pixels re-phase a torch whenever its
            // holder turns (direction offsets) or it sways a pixel.
            int tx = offer.DrawX / 44;
            int ty = offer.DrawY / 44;
            uint h = offer.ID * 2654435761u ^ ((uint)tx * 40503u) ^ ((uint)ty * 65599u);
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            float seed = (h & 0xFFFFFFu) / (float)0x1000000u; // 0..1
            float t = (float)time;
            float p1 = seed * 6.2831853f;
            float p2 = seed * 12.5663706f + 1.7f;
            // 7-11 Hz shimmer + slower 1-2 Hz breathing, both subtle.
            float n = MathF.Sin(t * 7.3f + p1) * 0.5f + MathF.Sin(t * 11.7f + p2) * 0.3f + MathF.Sin(t * 1.9f + p1 * 0.5f) * 0.2f;
            return 1.05f * (1f + 0.035f * n);
        }

        private Texture2D GetLightTexture(uint id)
        {
            if (_lightTextures.TryGetValue(id, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            if (_lightsLoader == null)
            {
                return null;
            }

            LightInfo info;

            try
            {
                info = _lightsLoader.GetLight(id);
            }
            catch
            {
                return null;
            }

            if (info.Pixels.IsEmpty || info.Width <= 0 || info.Height <= 0)
            {
                return null;
            }

            int pixelCount = checked(info.Width * info.Height);
            int copy = Math.Min(pixelCount, info.Pixels.Length);

            if (copy <= 0)
            {
                return null;
            }

            byte[] rgba = new byte[copy * 4];
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(info.Pixels.Slice(0, copy))
                .CopyTo(rgba);

            Image image = Image.CreateFromData(info.Width, info.Height, false, Image.Format.Rgba8, rgba);
            var texture = ImageTexture.CreateFromImage(image);
            _lightTextures[id] = texture;

            return texture;
        }

        private static Godot.Color MapLightColor(ushort color, bool isHue)
        {
            if (color == 0)
            {
                return Colors.White;
            }

            if (isHue)
            {
                try
                {
                    var hues = Client.Game?.UO?.FileManager?.Hues;

                    if (hues?.HuesRange != null && color < hues.HuesCount)
                    {
                        int c = color - 1;
                        int g = c >> 3;
                        int e = c % 8;

                        if (g >= 0 && g < hues.HuesRange.Length)
                        {
                            ushort c16 = hues.HuesRange[g].Entries[e].ColorTable[16];
                            uint c32 = HuesHelper.Color16To32(c16);
                            return new Godot.Color(
                                (c32 & 0xFF) / 255f,
                                ((c32 >> 8) & 0xFF) / 255f,
                                ((c32 >> 16) & 0xFF) / 255f);
                        }
                    }
                }
                catch
                {
                }

                return Colors.White;
            }

            // Light-shader palette (SHADER_LIGHTS): the default RGB table from
            // LightColors.MakeDefaultShaders. Curves are ignored — the light
            // texture already carries the falloff; this is the tint.
            uint rgb = color switch
            {
                1 => 0x00FF00,
                2 => 0x7F7FFF,
                6 => 0xFF00FF,
                10 => 0x3F3FFF,
                20 => 0x00FF00,
                30 => 0xFF7F00,
                31 => 0xFF7F00,
                32 => 0xFF00FF,
                40 => 0xFF0000,
                50 => 0xFFFF00,
                60 => 0xFFFF00,
                61 => 0xFFFF00,
                62 => 0xFFFFFF,
                63 => 0xFFFFFF,
                _ => 0xFFFFFF,
            };

            return new Godot.Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f);
        }
    }
}
