// SPDX-License-Identifier: BSD-2-Clause

using GUO.Configuration;
using GUO.Input;
using GUO.Renderer;
using GUO.Compat;
using Godot;

// Compat's Color is the byte-packed XNA one, which is what
// GenerateBlendedCircleColors builds. Both are in scope; say which.
using Color = GUO.Compat.Color;
using System;

namespace GUO.Game.Managers
{
    sealed class Aura : IDisposable
    {
        private static readonly Lazy<BlendState> _blend = new Lazy<BlendState>
        (
            () => new BlendState
            {
                ColorSourceBlend = Blend.SourceAlpha,
                AlphaSourceBlend = Blend.SourceAlpha,
                ColorDestinationBlend = Blend.InverseSourceAlpha,
                AlphaDestinationBlend = Blend.InverseSourceAlpha
            }
        );

        private Texture2D _texture;
        private readonly int _radius;

        public Aura(int radius)
        {
            _radius = radius;
        }

        public void Draw(UltimaBatcher2D batcher, int x, int y, ushort hue, float depth)
        {
            int yBump = -5;

            if (_texture == null)
            {
                var w = _radius * 2;
                var h = _radius * 2;
                var data = GenerateBlendedCircleColors(_radius);

                // PORT DEVIATION (GUO): upstream builds an XNA Texture2D from
                // the device and calls SetData. Godot's Texture2D is a
                // resource, not a device object, so the bytes go through an
                // Image. The pixels are premultiplied here rather than by the
                // driver, because upstream's `Color.White * opacityFactor`
                // already is -- XNA's operator scales all four channels.
                var bytes = new byte[data.Length * 4];

                for (int i = 0; i < data.Length; i++)
                {
                    bytes[i * 4 + 0] = data[i].R;
                    bytes[i * 4 + 1] = data[i].G;
                    bytes[i * 4 + 2] = data[i].B;
                    bytes[i * 4 + 3] = data[i].A;
                }

                _texture = ImageTexture.CreateFromImage(
                    Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes));
            }

            x -= (_texture.GetWidth() >> 1);
            y -= (_texture.GetHeight() >> 1);

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, false, 1);

            batcher.SetBlendState(_blend.Value);
            batcher.Draw(_texture, new Rectangle(x, y + yBump, _radius * 2, _radius - yBump), new Rectangle(0, 0, _radius * 2, _radius - yBump), hueVec, depth + 1f);
            batcher.Draw(_texture, new Rectangle(x, y + _radius, _radius * 2, _radius + yBump), new Rectangle(0, _radius - yBump, _radius * 2, _radius + yBump), hueVec, depth + 1.49f);
            batcher.SetBlendState(BlendState.AlphaBlend);
        }

        private Color[] GenerateBlendedCircleColors(int radius)
        {
            var width = radius * 2;
            var height = radius * 2;

            var blendedColors = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var distance = (int)Math.Sqrt(Math.Pow(x - radius, 2) + Math.Pow(y - radius, 2));

                    if (distance > radius)
                    {
                        blendedColors[x + y * width] = Color.Transparent;
                        continue;
                    }

                    var opacityFactor = 1f - distance / (float)radius;

                    blendedColors[x + y * width] = Color.White * opacityFactor;
                }
            }

            return blendedColors;
        }

        public void Dispose()
        {
            _texture?.Dispose();
            _texture = null;
        }
    }

    internal sealed class AuraManager
    {
        private readonly World _world;
        private readonly Aura _aura;
        private int _saveAuraUnderFeetType;

        public AuraManager(World world)
        {
            _world = world;
            _aura = new Aura(40);
        }

        public bool IsEnabled
        {
            get
            {
                if (ProfileManager.CurrentProfile == null)
                {
                    return false;
                }

                switch (ProfileManager.CurrentProfile.AuraUnderFeetType)
                {
                    default:
                    case 0: return false;

                    case 1 when _world.Player != null && _world.Player.InWarMode: return true;
                    case 2 when Keyboard.Ctrl && Keyboard.Shift: return true;
                    case 3: return true;
                }
            }
        }

        public void ToggleVisibility()
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            if (!IsEnabled)
            {
                _saveAuraUnderFeetType = currentProfile.AuraUnderFeetType;
                currentProfile.AuraUnderFeetType = 3;
            }
            else
            {
                currentProfile.AuraUnderFeetType = _saveAuraUnderFeetType;
            }
        }

        public void Draw(UltimaBatcher2D batcher, int x, int y, ushort hue, float depth)
        {
            _aura.Draw(batcher, x, y, hue, depth);
        }
    }
}