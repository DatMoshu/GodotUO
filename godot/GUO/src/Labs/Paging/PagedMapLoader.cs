// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using GUO.Assets;
using GUO.IO;

namespace GUO.Labs.Paging;

/// <summary>
/// The stock map loader with room for page facets, the way upstream's
/// UltimaLive loader widens it. Stock facets load exactly as MapLoader loads
/// them. A page facet has no files until the session hands it a verified
/// page; its block index is then built by MapLoader.LoadMap itself, and
/// dropping the page releases the index and closes its files.
/// </summary>
internal sealed class PagedMapLoader : MapLoader, IPageBackend
{
    internal PagedMapLoader(UOFileManager fileManager) : base(fileManager)
    {
    }

    internal int OpenPages { get; private set; }

    public override void Load()
    {
        MAPS_COUNT = Math.Max(MAPS_COUNT, PageCatalog.LastFacet + 1);
        int[,] stock = MapsDefaultSize;
        base.Load();

        var sizes = new int[MAPS_COUNT, 2];
        for (int i = 0; i < Math.Min(MAPS_COUNT, stock.GetLength(0)); i++)
        {
            sizes[i, 0] = stock[i, 0];
            sizes[i, 1] = stock[i, 1];
        }
        // base.Load may adjust the first two facets for old clients.
        sizes[0, 0] = MapsDefaultSize[0, 0];
        sizes[1, 0] = MapsDefaultSize[1, 0];
        MapsDefaultSize = sizes;

        // Only a verified page pack supplies a page facet: anything the stock
        // probe found under those numbers in the UO install is closed again.
        for (int i = PageCatalog.FirstFacet; i <= PageCatalog.LastFacet; i++) ClearSlot(i);
    }

    public void LoadPage(PageEntry page)
    {
        int i = page.Facet;
        ClearSlot(i);

        var map = new UOFileMul(page.MapPath);
        var idx = new UOFileMul(page.StaidxPath);
        var statics = new UOFileMul(page.StaticsPath);
        // The X readers are the same files: a page has no separate Felucca
        // variant, and LoadMap's X-file comparison must not rebuild the index.
        _filesMap[i] = _filesMapX[i] = map;
        _filesIdxStatics[i] = _filesIdxStaticsX[i] = idx;
        _filesStatics[i] = _filesStaticsX[i] = statics;
        _currentMapFiles[i] = map;
        _currentIdxStaticsFiles[i] = idx;
        _currentStaticsFiles[i] = statics;
        MapsDefaultSize[i, 0] = page.Width;
        MapsDefaultSize[i, 1] = page.Height;
        BlockData[i] = null;
        LoadMap(i);
        OpenPages++;
    }

    public void UnloadPage(int facet)
    {
        if (_filesMap[facet] != null) OpenPages--;
        ClearSlot(facet);
        NotifyDataChanged();
    }

    private void ClearSlot(int i)
    {
        BlockData[i] = null;
        // A stock probe may have opened map/statics files under this number;
        // each reader is closed once even where two slots share it.
        Close(_filesMap[i], _filesMapX[i], _filesIdxStatics[i], _filesIdxStaticsX[i], _filesStatics[i], _filesStaticsX[i]);
        _filesMap[i] = _filesMapX[i] = null;
        _filesIdxStatics[i] = _filesIdxStaticsX[i] = null;
        _filesStatics[i] = _filesStaticsX[i] = null;
        _currentMapFiles[i] = _currentIdxStaticsFiles[i] = _currentStaticsFiles[i] = null;
    }

    private static void Close(params FileReader[] readers)
    {
        for (int a = 0; a < readers.Length; a++)
        {
            if (readers[a] == null) continue;
            bool seen = false;
            for (int b = 0; b < a; b++) seen |= ReferenceEquals(readers[a], readers[b]);
            if (!seen) readers[a].Dispose();
        }
    }
}
#endif
