// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Linq;
using GUO.Game;
using GUO.Game.Data;
using GUO.Utility.Logging;

namespace GUO.Labs.Paging;

/// <summary>
/// L4 map paging. Off unless GUO_LABS_PAGING names a page manifest; while it
/// is unset nothing here runs beyond one environment read, and the stock map
/// loader stays in place. With it set, the first move to a page facet swaps
/// in <see cref="PagedMapLoader"/> and every page facet is verified and
/// loaded before the client builds its map. See README.md.
/// </summary>
internal static class PagingLab
{
    internal const string ManifestVariable = "GUO_LABS_PAGING";
    internal const string ResidentVariable = "GUO_LABS_PAGING_RESIDENT";
    internal const string Doctor = @"launchers\dev\paging_doctor.bat";

    private static PagingSession _session;
    private static PagedMapLoader _loader;

    internal static string ManifestPath => Environment.GetEnvironmentVariable(ManifestVariable);
    internal static bool Enabled => !string.IsNullOrWhiteSpace(ManifestPath);
    internal static PagingSession Session => _session;

    /// <summary>
    /// World.MapIndex calls this before the stock loader sees a new facet.
    /// A page facet that cannot be made ready is refused out loud and the
    /// client continues on facet 0, which is what the stock client does with
    /// any facet it has no files for; only a server can refuse the move itself.
    /// </summary>
    internal static void BeforeMapChange(World world, ref int value)
    {
        if (_loader == null && (!PageCatalog.IsPageFacet(value) || !Enabled)) return;

        if (_loader != null && !ReferenceEquals(Client.Game.UO.FileManager.Maps, _loader))
        {
            // UltimaLive (or anything else) replaced the loader; pages are gone with it.
            Report(world, "map paging stopped: another map loader replaced it (UltimaLive?).");
            _session = null;
            _loader = null;
            if (PageCatalog.IsPageFacet(value)) value = 0;
            return;
        }

        if (!PageCatalog.IsPageFacet(value))
        {
            _session?.Leave();
            return;
        }

        if (!EnsureStarted(world, out string error) || !_session.Enter(value, out error))
        {
            Report(world, $"page facet {value} refused: {error} Continuing on facet 0. Run {Doctor}.");
            value = 0;
            return;
        }

        Log.Info($"[paging] facet {value} ready (page {_session.Catalog.ByFacet(value).Id}); " +
                 $"resident {string.Join(",", _session.Resident)} of cap {_session.Cap}, generation {_session.Generation}");
    }

    private static bool EnsureStarted(World world, out string error)
    {
        error = null;
        if (_session != null) return true;

        var stock = Client.Game.UO.FileManager.Maps;
        if (!string.IsNullOrEmpty(stock.MapsLayouts))
        {
            error = "the maps_layouts setting is in use; paging needs the stock facet layout.";
            return false;
        }
        if (stock is GUO.Game.UltimaLive.ULMapLoader)
        {
            error = "this shard uses UltimaLive maps; paging cannot share the loader.";
            return false;
        }

        PageCatalog catalog;
        try
        {
            catalog = PageCatalog.Load(ManifestPath);
        }
        catch (PageManifestException e)
        {
            error = e.Message;
            return false;
        }

        int cap = int.TryParse(Environment.GetEnvironmentVariable(ResidentVariable), out int c) ? c : PagingSession.DefaultCap;
        var loader = new PagedMapLoader(Client.Game.UO.FileManager) { MapsLayouts = stock.MapsLayouts };
        loader.Load();

        // Stock facets the old loader had indexed are indexed again by the new
        // one before it replaces the old, so nothing holding a stock facet
        // ever reads an empty index.
        bool felucca = world.ClientFeatures.Flags.HasFlag(CharacterListFlags.CLF_UNLOCK_FELUCCA_AREAS);
        for (int i = 0; i < Math.Min(stock.BlockData.Length, PageCatalog.FirstFacet); i++)
            if (stock.BlockData[i] != null) loader.LoadMap(i, felucca);

        Client.Game.UO.FileManager.Maps = loader;
        stock.Dispose();
        _loader = loader;
        _session = new PagingSession(catalog, loader, cap);
        Log.Info($"[paging] started: {catalog.Pages.Count} page(s) from {catalog.ManifestPath}, cap {_session.Cap}");
        return true;
    }

    /// <summary>-paging [status|reload|verify]</summary>
    internal static void Command(World world, string[] args)
    {
        string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        if (!Enabled)
        {
            GameActions.Print(world, $"Map paging is off. Set {ManifestVariable} to a page manifest; see {Doctor}.");
            return;
        }

        switch (verb)
        {
            case "reload":
                if (_session == null)
                {
                    GameActions.Print(world, "Map paging has not started yet; the manifest is read on the first page facet.");
                    return;
                }
                try
                {
                    var changed = _session.Reload(PageCatalog.Load(ManifestPath));
                    GameActions.Print(world, changed.Count == 0
                        ? "Page manifest reloaded; no resident page changed."
                        : $"Page manifest reloaded; revised resident facets {string.Join(",", changed)} dropped " +
                          "(the current page is rebuilt when next entered).");
                }
                catch (PageManifestException e)
                {
                    Report(world, $"page manifest reload refused: {e.Message} The previous manifest stays in use.");
                }
                return;

            case "verify":
                try
                {
                    var catalog = PageCatalog.Load(ManifestPath);
                    int bad = 0;
                    foreach (var page in catalog.Pages)
                    {
                        string problem = PageVerifier.Verify(page);
                        if (problem == null) continue;
                        bad++;
                        Report(world, problem);
                    }
                    GameActions.Print(world, $"{catalog.Pages.Count - bad} of {catalog.Pages.Count} page(s) verified.");
                }
                catch (PageManifestException e)
                {
                    Report(world, e.Message);
                }
                return;

            default:
                GameActions.Print(world, Status());
                return;
        }
    }

    internal static string Status()
    {
        if (_session == null) return $"Map paging is armed ({ManifestPath}); no page facet entered yet.";
        string stale = string.Join(",", _session.Resident.Where(_session.IsStale));
        return $"Map paging: {_session.Catalog.Pages.Count} page(s); current {(_session.Current < 0 ? "stock facet" : _session.Current)}; " +
               $"resident {string.Join(",", _session.Resident)} (cap {_session.Cap}, open {_loader?.OpenPages ?? 0}); " +
               $"loads {_session.Loads}, evictions {_session.Evictions}, late results dropped {_session.StaleResultsDropped}" +
               (stale.Length > 0 ? $"; stale {stale}" : "");
    }

    private static void Report(World world, string message)
    {
        Log.Error("[paging] " + message);
        if (world?.Player != null) GameActions.Print(world, message, 0x0021);
    }
}
#endif
