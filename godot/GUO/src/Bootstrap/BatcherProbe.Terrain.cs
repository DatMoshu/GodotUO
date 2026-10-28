// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GUO.Game.Map;
using GUO.Renderer;

public partial class BatcherProbe
{
    private async Task VerifyMergedLand(UltimaBatcher2D batcher, SubViewport viewport)
    {
        VerifyLandPageScales();
        VerifyLandArrayFreshness();
        bool oldArray = MergedLand.Array, oldOrdered = MergedLand.Ordered;
        MergedLand.Array = MergedLand.Ordered = true;
        using var atlas = new TextureAtlas(16, 16);
        using var cache = new CachedLand();
        var chunks = new List<Chunk>();
        var allChunks = new List<Chunk>();
        using var standalone = BuildSolid(new byte[] { 80, 160, 240 });
        try
        {
            foreach (var at in new[] { (0, 0), (0, 1), (0, 3), (0, 4), (1, 0) })
            {
                var chunk = new Chunk(null) { X = at.Item1, Y = at.Item2 };
                chunks.Add(chunk);
                allChunks.Add(chunk);
                var layer = chunk.Mesh.Land;
                uint pixel = 0xFF000000u | (uint)(40 + chunks.Count * 30) | (uint)(240 - chunks.Count * 25) << 8 | 0x400000u;
                Texture2D texture = atlas.AddSprite(new[] { pixel }, 1, 1, out var rect);
                layer.Count = 1;
                Vector3 hue = ShaderHueTranslator.GetHueVector(0);
                hue.Y = ShaderHueTranslator.SHADER_LAND;
                layer.WriteQuadAt(0, texture, rect, 0, 0, hue, 0f);
                MeshQuad q = layer.Vertices[0];
                q.Position1.X = q.Position3.X = 8;
                q.Position2.Y = q.Position3.Y = 8;
                q.Normal1 = new Vector3(0, -1, 1);
                q.Normal2 = new Vector3(1, 0, 0.2f);
                layer.Vertices[0] = q;
                layer.SetVisible(0, 128);
                layer.BuildVisibleIndices();
            }
            atlas.Flush();

            async Task<byte[]> Render(bool merged)
            {
                batcher.BeginFrame();
                batcher.Begin();
                batcher.SetBrightlight(0.3f);
                if (merged)
                    cache.Draw(batcher, chunks);
                else
                    foreach (Chunk c in chunks)
                        batcher.DrawMeshLayer(c.Mesh.Land);
                batcher.End();
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using Image image = viewport.GetTexture().GetImage();
                return image.GetData();
            }

            async Task Parity(string name, bool persistent = true)
            {
                byte[] reference = await Render(false);
                byte[] actual = await Render(true);
                CheckThat(name, reference.AsSpan().SequenceEqual(actual), "cache pixels equal original per-chunk draw order");
                CheckThat(name + " backend", persistent ? cache.LastFallback == null : cache.LastFallback != null,
                    cache.LastFallback ?? "persistent route");
            }

            async Task<byte[]> RenderCover(bool merged)
            {
                bool previous = UltimaBatcher2D.MergedCover;
                try
                {
                    UltimaBatcher2D.MergedCover = merged;
                    batcher.BeginFrame();
                    batcher.Begin();
                    batcher.SetBrightlight(0.3f);
                    foreach (Chunk c in chunks)
                        batcher.DrawMeshSprite(c.Mesh.Land, 0, 0, 0);
                    batcher.End();
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    using Image image = viewport.GetTexture().GetImage();
                    return image.GetData();
                }
                finally
                {
                    UltimaBatcher2D.MergedCover = previous;
                }
            }

            async Task CoverParity(string name)
            {
                byte[] reference = await RenderCover(false);
                byte[] built = await RenderCover(true);
                int builds = CoverRuns.Builds;
                byte[] reused = await RenderCover(true);
                CheckThat(name, reference.AsSpan().SequenceEqual(built) && built.AsSpan().SequenceEqual(reused),
                    "built and cached covering meshes equal the original per-tile pixels");
                CheckThat(name + " reuses mesh", CoverRuns.Builds == builds, $"{CoverRuns.Builds - builds} rebuilds");
            }

            await CoverParity("cover handles preserve overlapping translucent slopes");
            await Parity("terrain chunk boundaries preserve overlapping translucent slopes");
            CheckThat("terrain uses five cached chunks in one draw", cache.CachedChunks == 5, $"{cache.CachedChunks}");
            long built = cache.Rebuilds;
            long indexBytes = cache.IndexBytes;
            await Render(true);
            CheckThat("stationary terrain rebuilds nothing", cache.Rebuilds == built, $"{cache.Rebuilds - built}");
            CheckThat("stationary terrain uploads no indices", cache.IndexBytes == indexBytes, $"{cache.IndexBytes-indexBytes}");
            CheckThat("terrain submits one draw", cache.LastDrawCalls == 1, $"{cache.LastDrawCalls}");

            chunks[1].Mesh.Land.SetVisible(0, 64);
            chunks[1].Mesh.Land.BuildVisibleIndices();
            await CoverParity("cover handles refresh after alpha rebuild");
            await Parity("terrain alpha change preserves pixels");
            CheckThat("one changed chunk rebuilds only its chunk", cache.Rebuilds == built + 1, $"{cache.Rebuilds - built}");

            chunks[3].Mesh.Land.ResetVisibility();
            chunks[3].Mesh.Land.BuildVisibleIndices();
            await Parity("empty terrain chunk does not draw stale geometry");
            CheckThat("invisible chunk is evicted", cache.CachedChunks == 4, $"{cache.CachedChunks}");
            chunks[3].Mesh.Land.SetVisible(0, 200);
            chunks[3].Mesh.Land.BuildVisibleIndices();
            await Parity("terrain chunk reappears with current alpha");

            built = cache.Rebuilds;
            LandPages.Reset();
            await Parity("atlas reset rebuilds layer indices");
            CheckThat("atlas reset invalidates all five chunks", cache.Rebuilds == built + 5, $"{cache.Rebuilds - built}");

            // A new layer index leaves existing indices and UV scales stable.
            using var anotherAtlas = new TextureAtlas(8, 8);
            built = cache.Rebuilds;
            int capacity = LandPages.Capacity;
            var pixels = new uint[49];
            Array.Fill(pixels, 0xFFFFFFFFu);
            // 7x7 cannot fit an 8x8 page's padding: each gets a new page.
            for (int i = 0; i < capacity; i++)
                LandPages.LayerOf(anotherAtlas.AddSprite(pixels, 7, 7, out _));
            anotherAtlas.Flush();
            await Parity("atlas growth preserves existing terrain");
            CheckThat("atlas growth reallocates the array", LandPages.Capacity > capacity, $"{capacity} -> {LandPages.Capacity}");
            CheckThat("atlas growth does not rebuild terrain", cache.Rebuilds == built, $"{cache.Rebuilds - built}");

            Chunk replaced = chunks[0];
            var replacement = new Chunk(null) { X = replaced.X, Y = replaced.Y };
            allChunks.Add(replacement);
            replacement.Mesh.Land.Count = 1;
            replacement.Mesh.Land.Vertices[0] = replaced.Mesh.Land.Vertices[0];
            replacement.Mesh.Land.Textures[0] = replaced.Mesh.Land.Textures[0];
            replacement.Mesh.Land.SetVisible(0, 90);
            replacement.Mesh.Land.BuildVisibleIndices();
            chunks[0] = replacement;
            built = cache.Rebuilds;
            await Parity("replacement layer at same coordinates and stamp invalidates its chunk");
            CheckThat("layer identity invalidates one chunk", cache.Rebuilds == built + 1, $"{cache.Rebuilds - built}");

            // Warm the ordinary chunk cache, then mutate while only the merged route draws.
            for (byte alpha = 70; alpha <= 90; alpha += 10)
            {
                replacement.Mesh.Land.SetVisible(0, alpha);
                replacement.Mesh.Land.BuildVisibleIndices();
                await Render(true);
            }
            await Parity("lazy chunk run mesh refreshes after merged-only changes");
            MeshQuad moved = replacement.Mesh.Land.Vertices[0];
            Texture2D movedTexture = replacement.Mesh.Land.Textures[0];
            replacement.Mesh.Land.SoftReset();
            replacement.Mesh.Land.Count = 1;
            moved.Position0.X += 2; moved.Position1.X += 2; moved.Position2.X += 2; moved.Position3.X += 2;
            replacement.Mesh.Land.Vertices[0] = moved;
            replacement.Mesh.Land.Textures[0] = movedTexture;
            replacement.Mesh.Land.SetVisible(0, 180);
            replacement.Mesh.Land.BuildVisibleIndices();
            await CoverParity("cover handles refresh after pooled layer geometry changes");
            await Parity("same pooled layer reload updates geometry and bounds");

            // Standalone art in one cache must retain ordered texture runs.
            chunks[2].Mesh.Land.Textures[0] = standalone;
            chunks[2].Mesh.Land.SetVisible(0, 128);
            chunks[2].Mesh.Land.BuildVisibleIndices();
            await Parity("non-atlas cache fallback preserves stream order", persistent:false);

            chunks[2].Mesh.Land.Textures[0] = movedTexture;
            chunks[2].Mesh.Land.SetVisible(0,128);
            chunks[2].Mesh.Land.BuildVisibleIndices();

            // A repeated chunk separated by another column is not regrouped.
            Chunk repeated = chunks[1];
            chunks.RemoveAt(1);
            chunks.Add(repeated);
            await Parity("reordered chunk input preserves painter order");

            chunks.Clear();
            await Render(true);
            CheckThat("empty view releases all terrain chunks", cache.CachedChunks == 0, $"{cache.CachedChunks}");

            var wide = new Chunk(null);
            allChunks.Add(wide); chunks.Add(wide);
            MeshLayer wideLayer = wide.Mesh.Land;
            wideLayer.EnsureCapacity(16384); wideLayer.Count = 16384;
            wideLayer.Vertices[0] = moved; wideLayer.Vertices[16383] = moved;
            wideLayer.Textures[0] = movedTexture; wideLayer.Textures[16383] = movedTexture;
            wideLayer.SetVisible(0, 90); wideLayer.SetVisible(16383, 170);
            wideLayer.BuildVisibleIndices();
            await Parity("persistent terrain indices cross 65535 without truncation");
            wideLayer.SoftReset(); wideLayer.Count = 1;
            wideLayer.Vertices[0] = moved; wideLayer.Textures[0] = movedTexture;
            wideLayer.SetVisible(0, 150); wideLayer.BuildVisibleIndices();
            await Parity("shrinking active indices clears the previous tail");
            wideLayer.SoftReset(); wideLayer.BuildVisibleIndices();
            await Parity("zero-count layer removes all old primitives");
        }
        finally
        {
            cache.Dispose();
            foreach (Chunk chunk in allChunks)
                chunk.Mesh.Clear();
            LandPages.Reset();
            MergedLand.Array = oldArray;
            MergedLand.Ordered = oldOrdered;
        }
    }

    private async System.Threading.Tasks.Task VerifyAtlasMetadata(UltimaBatcher2D batcher, SubViewport viewport)
    {
        using var atlas = new TextureAtlas(16, 8);
        Texture2D page = atlas.AddSprite(new uint[] { 0xFF0000FF }, 1, 1, out var region);
        var green = new uint[15 * 7]; System.Array.Fill(green, 0xFF00FF00u);
        Texture2D dedicated = atlas.AddSprite(green, 15, 7, out var largeRegion);
        CheckThat("page metadata is rectangular and keeps its RID",
            TextureAtlas.TryGetMetadata(page, out var metadata) && metadata.Width == 16 && metadata.Height == 8 && metadata.Rid == page.GetRid(), "16x8");
        CheckThat("dedicated sprite metadata uses actual dimensions",
            TextureAtlas.TryGetMetadata(dedicated, out var large) && large.Width == 15 && large.Height == 7 && large.Rid == dedicated.GetRid(), "15x7");
        atlas.Flush();
        CheckThat("upload preserves page metadata", TextureAtlas.TryGetMetadata(page, out var uploaded) && uploaded == metadata, "content-only update");
        batcher.BeginFrame(); batcher.Begin();
        batcher.Draw(page, Vector2.Zero, region, new Vector3(0, 0, 1), 0);
        batcher.Draw(dedicated, new Vector2(2, 0), largeRegion, new Vector3(0, 0, 1), 0);
        batcher.Draw(page, new Vector2(4, 0), region, new Vector3(0, 0, 1), 0);
        batcher.End();
        CheckThat("A/B/A atlas draws query no native dimensions or RID", batcher.NativeTextureMeasurements == 0, $"{batcher.NativeTextureMeasurements}");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using (var pixels = viewport.GetTexture().GetImage())
        {
            Check("cached first page keeps UVs", new byte[] { 255, 0, 0 }, pixels.GetPixel(0, 0));
            Check("cached dedicated page keeps UVs", new byte[] { 0, 255, 0 }, pixels.GetPixel(2, 0));
            Check("cached returning page keeps UVs", new byte[] { 255, 0, 0 }, pixels.GetPixel(4, 0));
        }
        using var image = Image.CreateEmpty(3, 5, false, Image.Format.Rgba8); image.Fill(Colors.White);
        using var resized = Image.CreateEmpty(7, 9, false, Image.Format.Rgba8); resized.Fill(Colors.Green);
        using var standalone = ImageTexture.CreateFromImage(image);
        CheckThat("standalone images are not registered", !TextureAtlas.TryGetMetadata(standalone, out _), "fresh query path");
        batcher.BeginFrame(); batcher.Begin();
        batcher.Draw(standalone, Vector2.Zero, null, new Vector3(0, 0, 1), 0); batcher.End();
        CheckThat("standalone first draw measures once", batcher.NativeTextureMeasurements == 1, $"{batcher.NativeTextureMeasurements}");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        standalone.SetImage(resized);
        batcher.BeginFrame(); batcher.Begin();
        batcher.Draw(standalone, Vector2.Zero, null, new Vector3(0, 0, 1), 0); batcher.End();
        CheckThat("resized standalone measures again next frame", batcher.NativeTextureMeasurements == 1, $"{batcher.NativeTextureMeasurements}");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using (var pixels = viewport.GetTexture().GetImage())
            Check("standalone resize changes drawn extent", new byte[] { 0, 255, 0 }, pixels.GetPixel(6, 6));
        batcher.BeginFrame();
        atlas.Dispose();
        CheckThat("dispose removes normal and dedicated metadata",
            !TextureAtlas.TryGetMetadata(page, out _) && !TextureAtlas.TryGetMetadata(dedicated, out _), "no stale owners or RIDs");
    }

    private void VerifyLandPageScales()
    {
        using var atlas = new TextureAtlas(16, 8);
        using var image = Image.CreateEmpty(3, 5, false, Image.Format.Rgba8);
        using var resized = Image.CreateEmpty(7, 9, false, Image.Format.Rgba8);
        using var standalone = ImageTexture.CreateFromImage(image);
        try
        {
            Texture2D page = atlas.AddSprite(new uint[] { 0xFFFFFFFF }, 1, 1, out _);
            LandPages.LayerOf(page);
            var expected = new Vector2(16f / TextureAtlas.MaxPageSize, 8f / TextureAtlas.MaxPageSize);
            CheckThat("atlas scale uses rectangular page dimensions", LandPages.ScaleOf(page) == expected, "16x8 page");
            atlas.AddSprite(new uint[] { 0xFF808080 }, 1, 1, out _);
            atlas.Flush();
            CheckThat("atlas content upload preserves cached scale", LandPages.ScaleOf(page) == expected, "dimensions stay fixed");

            // The padding makes a 15x7 sprite a dedicated texture, not a 16x8 page.
            Texture2D dedicated = atlas.AddSprite(new uint[15 * 7], 15, 7, out _);
            var dedicatedScale = new Vector2(15f / TextureAtlas.MaxPageSize, 7f / TextureAtlas.MaxPageSize);
            LandPages.LayerOf(dedicated);
            CheckThat("dedicated atlas sprite keeps its own dimensions", LandPages.ScaleOf(dedicated) == dedicatedScale, "15x7 texture");
            LandPages.Reset();
            LandPages.LayerOf(dedicated);
            LandPages.LayerOf(page);
            CheckThat("atlas reset and reordered registration preserve scales",
                LandPages.ScaleOf(page) == expected && LandPages.ScaleOf(dedicated) == dedicatedScale, "new layer indices");

            Vector2 before = LandPages.ScaleOf(standalone);
            standalone.SetImage(resized);
            CheckThat("unregistered texture scale follows resize without registering",
                before == new Vector2(3f / TextureAtlas.MaxPageSize, 5f / TextureAtlas.MaxPageSize)
                && LandPages.ScaleOf(standalone) == new Vector2(7f / TextureAtlas.MaxPageSize, 9f / TextureAtlas.MaxPageSize)
                && !LandPages.Holds(standalone), "standalone 3x5 -> 7x9");
        }
        finally
        {
            LandPages.Reset();
        }
    }

    // A sprite packed into a page the land array already holds, before the
    // page's GPU upload, must reach the array at its next Sync.
    private void VerifyLandArrayFreshness()
    {
        using var atlas = new TextureAtlas(16, 8);
        try
        {
            Texture2D page = atlas.AddSprite(new uint[] { 0xFF0000FF }, 1, 1, out _);
            atlas.Flush();
            int layer = LandPages.LayerOf(page);
            LandPages.Sync();
            atlas.AddSprite(new uint[] { 0xFF00FF00 }, 1, 1, out var added);
            LandPages.Sync();
            using Image copy = LandPages.Array.GetLayerData(layer);
            Color c = copy.GetPixel(added.X, added.Y);
            CheckThat("land array copies a sprite added to an existing page before its upload",
                c.G > 0.9f && c.R < 0.1f && c.A > 0.9f, $"{c}");
        }
        finally
        {
            LandPages.Reset();
        }
    }
}
