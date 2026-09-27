using GUO.Compat;
using GUO.Assets;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GUO.Renderer.MultiMaps
{
    public sealed class MultiMap
    {
        private readonly MultiMapLoader _multiMapLoader;

        // PORT DEVIATION (GUO): no GraphicsDevice; see GetMap.
        public MultiMap(MultiMapLoader multiMapLodaer)
        {
            _multiMapLoader = multiMapLodaer;
        }

        public SpriteInfo GetMap(int? facet, int width, int height, int startX, int startY, int endX, int endY)
        {
            var multiMapInfo = facet.HasValue && _multiMapLoader.HasFacet(facet.Value) ?
                _multiMapLoader.LoadFacet(facet.Value, width, height, startX, startY, endX, endY) :
                _multiMapLoader.LoadMap(width, height, startX, startY, endX, endY);

            if (multiMapInfo.Pixels.IsEmpty)
                return default;

            // PORT DEVIATION (GUO): upstream builds an XNA Texture2D from the
            // pixels; this builds a Godot ImageTexture, nearest-sampled.
            // Not atlased, unlike every other loader here: a facet map is
            // one large image asked for once, so it gets a texture of its own.
            // A uint is 0xAABBGGRR, whose bytes are already R,G,B,A -- the
            // order Rgba8 wants.
            var rgba = new byte[multiMapInfo.Width * multiMapInfo.Height * 4];
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(multiMapInfo.Pixels)
                .Slice(0, rgba.Length)
                .CopyTo(rgba);

            var texture = ImageTexture.CreateFromImage(
                Image.CreateFromData(
                    multiMapInfo.Width, multiMapInfo.Height, false, Image.Format.Rgba8, rgba));

            return new SpriteInfo()
            {
                Texture = texture,
                UV = new Rectangle(0, 0, multiMapInfo.Width, multiMapInfo.Height),
                Center = Point.Zero
            };
        }
    }
}
