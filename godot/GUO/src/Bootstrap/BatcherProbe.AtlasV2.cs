// SPDX-License-Identifier: BSD-2-Clause
using GUO.Renderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    // GUO_ATLAS_V2 (TextureAtlas.V2.cs): the page cap by owner, and the
    // budget taking the oldest dirty pages first and leaving the rest dirty.
    private void VerifyAtlasV2()
    {
        bool was = TextureAtlas.V2;
        try
        {
            TextureAtlas.V2 = false;
            using (var off = new TextureAtlas(4096, 4096, "art"))
            {
                off.AddSprite(new uint[4], 2, 2, out _);
                CheckThat("atlas v2 off keeps 2048 pages", off.ProbePage(0).Width == TextureAtlas.MaxPageSize, "art page width");
            }

            TextureAtlas.V2 = true;
            using var art = new TextureAtlas(4096, 4096, "art");
            using var gump = new TextureAtlas(4096, 4096, "gump");
            using var land = new TextureAtlas(4096, 4096, "land");
            TextureAtlas.FlushAll(); // not in the world: unbudgeted, every page clean
            art.AddSprite(new uint[4], 2, 2, out _);
            land.AddSprite(new uint[4], 2, 2, out _);
            gump.AddSprite(new uint[4], 2, 2, out _);
            CheckThat("atlas v2 caps the dynamic atlases", art.ProbePage(0).Width == TextureAtlas.V2PageSize
                && gump.ProbePage(0).Width == TextureAtlas.V2PageSize && land.ProbePage(0).Width == TextureAtlas.MaxPageSize,
                "art and gump 1024, land 2048");

            long uploads = TextureAtlas.PageUploads;
            int budget = 2;
            TextureAtlas.UploadOldest(ref budget);
            CheckThat("atlas v2 budget uploads the two oldest pages", budget == 0 && TextureAtlas.PageUploads - uploads == 2
                && !art.ProbePage(0).Dirty && !land.ProbePage(0).Dirty && gump.ProbePage(0).Dirty,
                "art then land uploaded, gump waits");

            art.AddSprite(new uint[4], 2, 2, out _); // dirty again, now newer than the waiting gump page
            budget = 1;
            TextureAtlas.UploadOldest(ref budget);
            CheckThat("atlas v2 a waiting page goes before a newly dirtied one", !gump.ProbePage(0).Dirty && art.ProbePage(0).Dirty,
                "oldest-dirty order");
        }
        finally
        {
            TextureAtlas.V2 = was;
        }
    }
}
