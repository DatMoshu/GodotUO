// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.IO;
using Godot;
using GUO.Assets;
using Environment = System.Environment;

namespace GUO.Labs.Paging;

/// <summary>
/// Headless contract for L4 paging: off without its variable, then the real
/// PagedMapLoader against the user's install and a synthetic page pack
/// written to a temporary folder (flat land, no statics; no game data is
/// written or copied). Needs UO_CLIENT_DATA; no shard, no login.
/// </summary>
public partial class PagingProbe : Node
{
    public override void _Ready()
    {
        string root = Path.Combine(Path.GetTempPath(), "guo-paging-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Off state: with the variable unset, the World seam returns before
            // touching the client and leaves the requested facet alone.
            string saved = Environment.GetEnvironmentVariable(PagingLab.ManifestVariable);
            Environment.SetEnvironmentVariable(PagingLab.ManifestVariable, null);
            int facet = 32;
            PagingLab.BeforeMapChange(null, ref facet);
            Require(!PagingLab.Enabled && facet == 32 && PagingLab.Session == null, "paging must be inert while its variable is unset");
            Environment.SetEnvironmentVariable(PagingLab.ManifestVariable, saved);

            string data = Environment.GetEnvironmentVariable("UO_CLIENT_DATA");
            Require(!string.IsNullOrWhiteSpace(data) && Directory.Exists(data), "UO_CLIENT_DATA must name the UO install (" + PagingLab.Doctor + ")");
            using var files = new UOFileManager(GUO.Host.UoDataProbe.ParseVersion(Environment.GetEnvironmentVariable("UO_CLIENT_VERSION") ?? "7.0.107.76"), data);
            files.Load(false, "enu");
            int stockCount = MapLoader.MAPS_COUNT;
            files.Maps.LoadMap(0);
            var stockIndex = files.Maps.GetIndex(0, 180, 210);

            var loader = new PagedMapLoader(files);
            loader.Load();
            Require(MapLoader.MAPS_COUNT >= PageCatalog.LastFacet + 1, "the paged loader must cover facets 32-79");
            Require(loader.MapsDefaultSize[0, 0] == files.Maps.MapsDefaultSize[0, 0] && loader.MapsDefaultSize[0, 1] == files.Maps.MapsDefaultSize[0, 1],
                "stock facet sizes must be unchanged");
            loader.LoadMap(0);
            var pagedIndex = loader.GetIndex(0, 180, 210);
            Require(pagedIndex.MapAddress == stockIndex.MapAddress && pagedIndex.StaticAddress == stockIndex.StaticAddress &&
                    pagedIndex.StaticCount == stockIndex.StaticCount, "stock facet 0 must index exactly as the stock loader does");
            for (int i = PageCatalog.FirstFacet; i <= PageCatalog.LastFacet; i++)
                Require(loader.BlockData[i] == null && loader.GetMapFile(i) == null, $"facet {i} must have no files before a page is loaded");

            string manifest = PageFixture.WritePages(root, 64, 48, 32, 33, 34);
            var session = new PagingSession(PageCatalog.Load(manifest), loader, cap: 2);
            Require(session.Enter(32, out string error), "enter 32: " + error);
            Require(loader.BlockData[32]?.Length == (64 >> 3) * (48 >> 3) && loader.MapBlocksSize[32, 0] == 8 && loader.MapBlocksSize[32, 1] == 6,
                "page 32 block index must match its manifest dimensions");
            ref var cell = ref loader.GetIndex(32, 7, 5);
            cell.MapFile.Seek((long)cell.MapAddress + 4, SeekOrigin.Begin);
            Require(cell.MapFile.ReadUInt16() == PageFixture.Land && cell.MapFile.ReadInt8() == 0 && cell.StaticCount == 0,
                "page 32 terrain must read from the page file");

            Require(session.Enter(33, out error) && session.Enter(34, out error), "enter 33 and 34: " + error);
            Require(loader.BlockData[32] == null && loader.GetMapFile(32) == null && loader.BlockData[33] != null && loader.BlockData[34] != null,
                "cap 2 must evict the oldest page and close its files");
            Require(loader.OpenPages == 2 && session.Resident.Count == 2, "open pages must stay within the cap");
            Require(loader.GetIndex(0, 180, 210).MapAddress == stockIndex.MapAddress, "page traffic must not disturb stock facet 0");

            File.WriteAllBytes(Path.Combine(root, "map32.mul"), new byte[10]);
            Require(!session.Enter(32, out error) && error.Contains("map file is") && loader.BlockData[32] == null,
                "a truncated page must be refused and stay unloaded");
            Require(!session.Enter(40, out error) && loader.BlockData[40] == null, "a facet missing from the manifest must be refused");

            session.Clear();
            Require(loader.OpenPages == 0 && loader.BlockData[33] == null && loader.BlockData[34] == null, "clearing must close every page");
            MapLoader.MAPS_COUNT = stockCount;
            GD.Print("[paging] PASS: inert when unset, stock facet unchanged, page index from verified files, cap eviction closes files, truncated/unknown pages refused");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[paging] FAIL: " + ex.Message + " See " + PagingLab.Doctor + ".\n" + ex);
            GetTree().Quit(1);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
#endif
