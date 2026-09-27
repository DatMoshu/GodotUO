using GUO.Compat;
using GUO.Assets;

namespace GUO.Renderer.Gumps
{
    public sealed class Gump
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly GumpsLoader _gumpsLoader;

        // PORT DEVIATION (GUO): no GraphicsDevice; the atlas is created without one.
        public Gump(GumpsLoader gumpsLoader)
        {
            _gumpsLoader = gumpsLoader;
            _atlas = new TextureAtlas(4096, 4096);
            _spriteInfos = new SpriteInfo[gumpsLoader.File.Entries.Length];
        }

        public ref readonly SpriteInfo GetGump(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var gumpInfo = _gumpsLoader.GetGump(idx);
                if (!gumpInfo.Pixels.IsEmpty)
                {
                    spriteInfo.Texture = _atlas.AddSprite(
                        gumpInfo.Pixels,
                        gumpInfo.Width,
                        gumpInfo.Height,
                        out spriteInfo.UV
                    );

                    _picker.Set(idx, gumpInfo.Width, gumpInfo.Height, gumpInfo.Pixels);
                }
            }

            return ref spriteInfo;
        }

        public bool PixelCheck(uint idx, int x, int y) => _picker.Get(idx, x, y);
    }
}
