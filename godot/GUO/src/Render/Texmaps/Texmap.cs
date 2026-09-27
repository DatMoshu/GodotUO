using GUO.Compat;
using GUO.Assets;

namespace GUO.Renderer.Texmaps
{
    public sealed class Texmap
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly TexmapsLoader _texmapsLoader;

        // PORT DEVIATION (GUO): no GraphicsDevice; the atlas is created without one.
        public Texmap(TexmapsLoader texmapsLoader)
        {
            _texmapsLoader = texmapsLoader;
            _atlas = new TextureAtlas(2048, 2048);
            _spriteInfos = new SpriteInfo[texmapsLoader.File.Entries.Length];
        }

        public ref readonly SpriteInfo GetTexmap(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var texmapInfo = _texmapsLoader.GetTexmap(idx);
                if (!texmapInfo.Pixels.IsEmpty)
                {
                    spriteInfo.Texture = _atlas.AddSprite(
                        texmapInfo.Pixels,
                        texmapInfo.Width,
                        texmapInfo.Height,
                        out spriteInfo.UV
                    );

                    _picker.Set(idx, texmapInfo.Width, texmapInfo.Height, texmapInfo.Pixels);
                }
            }

            return ref spriteInfo;
        }
    }
}
