// SPDX-License-Identifier: BSD-2-Clause
using GUO.Renderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    // GUO_ATLAS_PAGE_1024 (TextureAtlas.SmallDynamicPages): only the atlases
    // marked dynamic take the smaller page, and only with the toggle on.
    private void VerifyAtlasPages1024()
    {
        bool was = TextureAtlas.SmallDynamicPages;
        try
        {
            TextureAtlas.SmallDynamicPages = false;
            using (var off = new TextureAtlas(4096, 4096, dynamicPages: true))
            {
                CheckThat("atlas 1024 off keeps 2048 pages", off.PageWidth == TextureAtlas.MaxPageSize, "dynamic page width");
            }

            TextureAtlas.SmallDynamicPages = true;
            using var dynamic = new TextureAtlas(4096, 4096, dynamicPages: true);
            using var fixedPages = new TextureAtlas(4096, 4096);
            using var small = new TextureAtlas(512, 512, dynamicPages: true);
            CheckThat("atlas 1024 caps the dynamic atlases", dynamic.PageWidth == TextureAtlas.DynamicPageSize, "dynamic page width");
            CheckThat("atlas 1024 leaves the others", fixedPages.PageWidth == TextureAtlas.MaxPageSize, "land/texmap/light page width");
            CheckThat("atlas 1024 never enlarges a page", small.PageWidth == 512, "requested 512");

            // A sprite larger than a page still gets a texture of its own.
            uint[] big = new uint[1100 * 4];
            var texture = dynamic.AddSprite(big, 1100, 4, out var uv);
            CheckThat("atlas 1024 oversized sprite", texture != null && uv.Width == 1100, "1100 px wide sprite");
        }
        finally
        {
            TextureAtlas.SmallDynamicPages = was;
        }
    }
}
