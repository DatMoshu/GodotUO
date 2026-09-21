using GUO.Compat;
using GUO.Assets;

namespace GUO.Renderer.Lights
{
    public sealed class Light
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly LightsLoader _lightsLoader;

        public Light(LightsLoader lightsLoader)
        {
            _lightsLoader = lightsLoader;
            _atlas = new TextureAtlas(2048, 2048);
            _spriteInfos = new SpriteInfo[lightsLoader.File.Entries.Length];
        }

        public ref readonly SpriteInfo GetLight(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var lightInfo = _lightsLoader.GetLight(idx);
                if (!lightInfo.Pixels.IsEmpty)
                {
                    spriteInfo.Texture = _atlas.AddSprite(
                        lightInfo.Pixels,
                        lightInfo.Width,
                        lightInfo.Height,
                        out spriteInfo.UV
                    );
                }
            }

            return ref spriteInfo;
        }
    }
}
