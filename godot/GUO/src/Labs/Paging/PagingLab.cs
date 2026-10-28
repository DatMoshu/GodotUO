// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Diagnostics;
using System.Linq;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;
using GUO.Network;
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
    /// World.MapIndex calls this before the stock loader sees a new facet and
    /// builds no map when it returns false. A page facet that cannot be made
    /// ready is never replaced by another facet: the server has already put
    /// the player there, so the client disconnects and says why on the login
    /// screen instead of playing on with a map the server does not agree with.
    /// </summary>
    internal static bool BeforeMapChange(World world, ref int value)
    {
        if (_loader == null && (!PageCatalog.IsPageFacet(value) || !Enabled)) return true;

        if (_loader != null && !ReferenceEquals(Client.Game.UO.FileManager.Maps, _loader))
        {
            // UltimaLive (or anything else) replaced the loader; pages are gone with it.
            _session = null;
            _loader = null;
            if (!PageCatalog.IsPageFacet(value)) return true;
            Refuse(world, $"page facet {value} refused: another map loader replaced map paging (UltimaLive?).");
            return false;
        }

        if (!PageCatalog.IsPageFacet(value))
        {
            _session?.Leave();
            return true;
        }

        var timer = Stopwatch.StartNew();
        if (!EnsureStarted(world, out string error) || !_session.Enter(value, out error))
        {
            Refuse(world, $"page facet {value} refused: {error}");
            return false;
        }

        timer.Stop();
        var page = _session.Catalog.ByFacet(value);
        using var process = Process.GetCurrentProcess();
        // Observations for the evidence log, not measurements of anything.
        Log.Info($"[paging] facet {value} ready (page {page.Id}, version {page.Version}) in {timer.ElapsedMilliseconds} ms; " +
                 $"resident {string.Join(",", _session.Resident)} of cap {_session.Cap}, open {_loader.OpenPages}, " +
                 $"generation {_session.Generation}; working set {process.WorkingSet64 >> 20} MiB, managed heap {GC.GetTotalMemory(false) >> 20} MiB");
        return true;
    }

    /// <summary>
    /// The client cannot follow the server onto this facet: disconnect, as
    /// upstream does when the server agrees to a logout (0x1D), and show the
    /// reason on the login screen. Nothing of the game world stays playable.
    /// </summary>
    private static void Refuse(World world, string reason)
    {
        string message = $"{reason} Disconnected so the client never plays on a map the server did not choose. Run {Doctor}.";
        Log.Error("[paging] " + message);
        LastRefusal = message;
        NetClient.Socket.Disconnect();
        var login = Client.Game.GetScene<LoginScene>();
        if (login == null)
        {
            login = new LoginScene(world);
            Client.Game.SetScene(login);
        }
        login.PopupMessage = message;
        login.CurrentLoginStep = LoginSteps.PopUpMessage;
    }

    internal static string LastRefusal { get; private set; }

    /// <summary>
    /// WorldMapGump.ChangeMap calls this before the stock loader is asked for
    /// <paramref name="facet"/>, and keeps its current map when it returns
    /// false. A page facet is loaded for the view (within the cap) and pinned
    /// until <see cref="AfterWorldMap"/>'s render task ends; one that cannot be
    /// loaded is named in the journal with the reason instead.
    /// </summary>
    internal static bool BeforeWorldMap(World world, int facet)
    {
        if (!PageCatalog.IsPageFacet(facet) || (_session == null && !Enabled)) return true;

        string error = null;
        if (_loader != null && !ReferenceEquals(Client.Game.UO.FileManager.Maps, _loader))
            error = "another map loader replaced map paging.";
        else if (_session == null)
            error = "map paging has not started; enter a page facet first.";
        else if (_session.View(facet, out error))
        {
            var page = _session.Catalog.ByFacet(facet);
            Log.Info($"[paging] world map on facet {facet} (page {page.Id}): loaded and pinned; resident {string.Join(",", _session.Resident)} " +
                     $"of cap {_session.Cap}, pinned {_session.PinnedCount}, open {_loader.OpenPages}");
            return true;
        }

        string message = $"World map: page facet {facet} is not loaded: {error} See {Doctor}.";
        Log.Warn("[paging] " + message);
        if (world?.Player != null) GameActions.Print(world, message, 0x0021);
        return false;
    }

    /// <summary>Unpins a viewed page once the world map's render task for it ends.</summary>
    internal static void AfterWorldMap(int facet, System.Threading.Tasks.Task render)
    {
        var session = _session;
        if (session == null || !PageCatalog.IsPageFacet(facet) || render == null) return;
        render.ContinueWith(_ =>
        {
            session.Unpin(facet);
            Log.Info($"[paging] world map render for facet {facet} ended ({render.Status}); unpinned");
        });
    }

    private static bool EnsureStarted(World world, out string error)
    {
        error = null;
        if (_session != null) return true;

        var stock = Client.Game.UO.FileManager.Maps;
        // Every refusal is decided here, before anything is replaced: the
        // stock loader, with whatever the shard has patched, stays as it is.
        error = Preflight(stock);
        if (error != null) return false;

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

    /// <summary>Why paging cannot replace <paramref name="stock"/>, or null.</summary>
    internal static string Preflight(Assets.MapLoader stock)
    {
        if (!string.IsNullOrEmpty(stock.MapsLayouts))
            return "the maps_layouts setting is in use; paging needs the stock facet layout.";
        if (stock is GUO.Game.UltimaLive.ULMapLoader)
            return "this shard uses UltimaLive maps; paging cannot share the loader.";
        string patched = PagedMapLoader.AppliedPatch(stock);
        return patched == null ? null
            : $"the shard has applied map patches ({patched}); paging cannot carry them across its loader yet.";
    }

    /// <summary>-paging [status|reload|verify|check|worldmap facet]</summary>
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

            case "check":
                GameActions.Print(world, Check(world));
                return;

            case "worldmap":
                // Test hook for the evidence run: open the world map, then
                // point its free view at any facet, evicted pages included.
                // Open first and let it finish: the gump's own load reads its
                // map field late, so switching mid-load mixes facets (upstream).
                var gump = UIManager.GetGump<WorldMapGump>();
                if (gump == null)
                {
                    gump = new WorldMapGump(world);
                    UIManager.Add(gump);
                }
                if (args.Length < 3 || !int.TryParse(args[2], out int facet))
                {
                    Log.Info($"[paging] world map open on facet {world.MapIndex}");
                    return;
                }
                gump.FreeView = true;
                Log.Info($"[paging] world map free view -> facet {facet}; resident {string.Join(",", _session?.Resident ?? Array.Empty<int>())}");
                gump.ChangeMap(facet);
                Log.Info($"[paging] world map free view on facet {facet} returned; {Check(world)}");
                return;

            default:
                GameActions.Print(world, Status());
                return;
        }
    }

    /// <summary>
    /// The residency invariants, logged and printed: the current facet is
    /// indexed, every page outside the resident set has no index and no
    /// open files, and the open count matches the resident count.
    /// </summary>
    internal static string Check(World world)
    {
        if (_session == null || _loader == null) return "[paging] check: paging not started";
        // Pages a finished world map kept past the cap go now, as on a crossing.
        _session.Trim();
        var problems = new System.Collections.Generic.List<string>();
        int current = world.MapIndex;
        if (PageCatalog.IsPageFacet(current) && (_loader.BlockData[current] == null || !_session.Resident.Contains(current)))
            problems.Add($"current facet {current} is not resident");
        for (int f = PageCatalog.FirstFacet; f <= PageCatalog.LastFacet; f++)
        {
            bool resident = _session.Resident.Contains(f);
            if (!resident && (_loader.BlockData[f] != null || _loader.GetMapFile(f) != null))
                problems.Add($"evicted facet {f} still has an index or open files");
            if (resident && _loader.BlockData[f] == null)
                problems.Add($"resident facet {f} has no index");
        }
        if (_loader.OpenPages != _session.Resident.Count)
            problems.Add($"open pages {_loader.OpenPages} != resident {_session.Resident.Count}");
        if (_session.Resident.Count > _session.Cap + _session.PinnedCount)
            problems.Add($"resident {_session.Resident.Count} over cap {_session.Cap} with {_session.PinnedCount} pinned");
        string result = problems.Count == 0
            ? $"[paging] check OK: current {current}, resident {string.Join(",", _session.Resident)} (cap {_session.Cap}, pinned {_session.PinnedCount}), open {_loader.OpenPages}"
            : "[paging] check FAILED: " + string.Join("; ", problems);
        Log.Info(result);
        return result;
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
