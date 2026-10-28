// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS || GUO_PAGING_TESTS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GUO.Labs.Paging;

/// <summary>What the session asks of the map loader: build or drop one facet's block index.</summary>
internal interface IPageBackend
{
    void LoadPage(PageEntry page);
    void UnloadPage(int facet);
}

/// <summary>
/// Page identity is the manifest; residency is this session. A page becomes
/// resident (its block index built) only when the player enters its facet,
/// after its files are verified. At most <see cref="Cap"/> pages stay
/// resident; the current page is never evicted. Neighbours of the current
/// page are verified in the background but not loaded. Every entry bumps a
/// generation, and background results from an older generation are dropped.
/// The world map may also view a page (<see cref="View"/>): it is loaded
/// within the cap and pinned until its render ends, so nothing evicts,
/// replaces or rebuilds it mid-read. A pinned page that goes stale keeps its
/// copy until <see cref="Unpin"/>, which then replaces or reclaims it at once.
/// The bound is <see cref="Bound"/>: the cap, or the pinned pages plus the
/// current one when more than the cap are pinned. Main-thread only, apart
/// from the verification tasks it starts.
/// </summary>
internal sealed class PagingSession
{
    internal const int DefaultCap = 3;
    internal const int MaxCap = 5;

    private readonly IPageBackend _backend;
    private readonly Func<Func<string>, Task<string>> _run;
    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, string> _residentKey = new();
    private readonly ConcurrentDictionary<int, (string key, string fingerprint)> _verified = new();
    private readonly HashSet<int> _prefetching = new();
    private readonly Dictionary<int, int> _pinned = new();
    private readonly Dictionary<int, (PageEntry page, string fingerprint)> _residentCopy = new();
    private int _generation;

    /// <param name="run">Runs a verification off the main thread; tests pass a deterministic runner.</param>
    internal PagingSession(PageCatalog catalog, IPageBackend backend, int cap = DefaultCap,
        Func<Func<string>, Task<string>> run = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Cap = Math.Clamp(cap, 1, MaxCap);
        _run = run ?? (work => Task.Run(work));
    }

    internal PageCatalog Catalog { get; private set; }
    internal int Cap { get; }
    internal int Current { get; private set; } = -1;
    internal int Generation => _generation;
    internal IReadOnlyCollection<int> Resident => _lru;
    internal int Loads { get; private set; }
    internal int Evictions { get; private set; }
    internal int StaleResultsDropped { get; private set; }
    internal string LastError { get; private set; }
    /// <summary>Entries that kept a pinned page's previous copy instead of rebuilding it.</summary>
    internal int DeferredReplacements { get; private set; }

    internal bool IsVerified(int facet)
    {
        var page = Catalog.ByFacet(facet);
        return page != null && _verified.TryGetValue(facet, out var v) && v.key == page.ContentKey &&
               v.fingerprint == PageVerifier.Fingerprint(page);
    }

    /// <summary>
    /// Makes <paramref name="facet"/> ready before the client switches to it.
    /// Returns false, with a plain reason, when the facet cannot be used.
    /// </summary>
    internal bool Enter(int facet, out string error)
    {
        _generation++;
        if (!Ready(facet, false, out var page, out error)) return false;

        _lru.Remove(facet);
        _lru.AddFirst(facet);
        Current = facet;
        Trim();

        LastError = null;
        Prefetch(page);
        return true;
    }

    /// <summary>
    /// Makes <paramref name="facet"/> readable for the world map without the
    /// player entering it, and pins it until <see cref="Unpin"/>. A page that
    /// is not resident is verified and loaded only when the cap has room or a
    /// page other than the current or a pinned one can make room; otherwise,
    /// or when it fails verification, it is refused with a plain reason and
    /// nothing changes.
    /// </summary>
    internal bool View(int facet, out string error)
    {
        error = null;
        if (facet == Current)
        {
            // The player's page is already in use; a view never rebuilds or drops it.
            lock (_pinned) _pinned[facet] = _pinned.TryGetValue(facet, out int c) ? c + 1 : 1;
            return true;
        }
        // A resident copy, stale or not, needs no room: it is reused or replaced in place.
        bool resident = _residentKey.ContainsKey(facet);
        if (!resident && Catalog.ByFacet(facet) != null && _lru.Count >= Cap && !_lru.Any(f => f != facet && Evictable(f)))
        {
            error = $"page {Catalog.ByFacet(facet).Id} (facet {facet}) is not loaded: all {Cap} resident page(s) are in use " +
                    $"(current or on a world map); raise the cap or travel there.";
            return false;
        }
        if (!Ready(facet, true, out _, out error)) return false;

        _lru.Remove(facet);
        // Behind the current page, so a view never outranks where the player is.
        if (Current >= 0 && _lru.First?.Value == Current) _lru.AddAfter(_lru.First, facet);
        else _lru.AddFirst(facet);
        lock (_pinned) _pinned[facet] = _pinned.TryGetValue(facet, out int n) ? n + 1 : 1;
        Trim();
        return true;
    }

    /// <summary>
    /// Ends one <see cref="View"/> (main thread). When the last pin on a page
    /// goes, a copy that went stale while pinned is dropped now unless it is
    /// the current page (rebuilt on its next entry, as after a reload), and
    /// residency is trimmed back to the cap without waiting for a crossing.
    /// </summary>
    internal void Unpin(int facet)
    {
        lock (_pinned)
        {
            if (!_pinned.TryGetValue(facet, out int n)) return;
            if (n > 1)
            {
                _pinned[facet] = n - 1;
                return;
            }
            _pinned.Remove(facet);
        }
        if (facet != Current && _residentKey.ContainsKey(facet) && Outdated(facet)) Drop(facet);
        Trim();
    }

    /// <summary>
    /// The most pages that can be resident: the cap, or, when more pages are
    /// pinned than that, every pinned page plus the current one. Trim reaches
    /// it after any entry, view or unpin.
    /// </summary>
    internal int Bound
    {
        get
        {
            int others;
            lock (_pinned) others = _pinned.Keys.Count(f => f != Current);
            return Math.Max(Cap, others + (Current >= 0 ? 1 : 0));
        }
    }

    internal bool IsPinned(int facet)
    {
        lock (_pinned) return _pinned.ContainsKey(facet);
    }

    internal int PinnedCount
    {
        get { lock (_pinned) return _pinned.Count; }
    }

    /// <summary>
    /// Evicts least recently used pages down to the cap, never the current or
    /// a pinned one, so it stops at <see cref="Bound"/> while pages are pinned.
    /// </summary>
    internal void Trim()
    {
        while (_lru.Count > Cap)
        {
            int victim = -1;
            for (var node = _lru.Last; node != null; node = node.Previous)
            {
                if (Evictable(node.Value))
                {
                    victim = node.Value;
                    break;
                }
            }
            if (victim < 0) return;
            Drop(victim);
            Evictions++;
        }
    }

    private bool Evictable(int facet) => facet != Current && !IsPinned(facet);

    /// <summary>Verifies <paramref name="facet"/> and builds its index if it is not resident.</summary>
    private bool Ready(int facet, bool viewing, out PageEntry page, out string error)
    {
        error = null;
        page = Catalog.ByFacet(facet);
        if (page == null)
        {
            error = PageCatalog.IsPageFacet(facet)
                ? $"facet {facet} is not in the page manifest {Catalog.ManifestPath}."
                : $"facet {facet} is not a page facet ({PageCatalog.FirstFacet}-{PageCatalog.LastFacet}).";
            LastError = error;
            return false;
        }

        // A resident copy built from other content (new version, new hash or a
        // replaced file) is dropped and rebuilt rather than reused, except
        // while a world map render reads it: then its own files must still be
        // what was loaded, a view is refused, and an entry keeps the previous
        // copy until the pin ends (Unpin drops it; the next entry rebuilds).
        if (_residentKey.TryGetValue(facet, out var loadedKey) && (loadedKey != page.ContentKey || !IsVerified(facet)))
        {
            if (!IsPinned(facet))
            {
                Drop(facet);
            }
            else if (CopyChangedOnDisk(facet))
            {
                error = $"page {page.Id} (facet {facet}) changed on disk while the world map reads it; it cannot be used until that ends.";
                LastError = error;
                return false;
            }
            else if (viewing)
            {
                error = $"page {page.Id} (facet {facet}) has a new version and the world map is still drawing the previous one; " +
                        "open it again when that finishes.";
                LastError = error;
                return false;
            }
            else
            {
                DeferredReplacements++;
                return true;
            }
        }

        if (!IsVerified(facet))
        {
            string fingerprint = PageVerifier.Fingerprint(page);
            string problem = PageVerifier.Verify(page);
            if (problem != null)
            {
                _verified.TryRemove(facet, out _);
                error = problem;
                LastError = error;
                return false;
            }
            _verified[facet] = (page.ContentKey, fingerprint);
        }

        if (!_residentKey.ContainsKey(facet))
        {
            _backend.LoadPage(page);
            _residentKey[facet] = page.ContentKey;
            _residentCopy[facet] = (page, _verified[facet].fingerprint);
            Loads++;
        }
        return true;
    }

    /// <summary>The resident copy is not the manifest's current content, or its files changed.</summary>
    private bool Outdated(int facet) => IsStale(facet) || Catalog.ByFacet(facet) == null || CopyChangedOnDisk(facet);

    private bool CopyChangedOnDisk(int facet) =>
        _residentCopy.TryGetValue(facet, out var copy) && PageVerifier.Fingerprint(copy.page) != copy.fingerprint;

    /// <summary>The client left the page range (a stock facet); pages stay resident within the cap.</summary>
    internal void Leave()
    {
        _generation++;
        Current = -1;
    }

    /// <summary>
    /// Replaces the manifest. Resident pages whose content changed or which
    /// disappeared are dropped now unless current; the current one is rebuilt
    /// on its next entry. Returns the facets whose content changed.
    /// </summary>
    internal IReadOnlyList<int> Reload(PageCatalog catalog)
    {
        _generation++;
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        var changed = new List<int>();
        foreach (var facet in _residentKey.Keys.ToArray())
        {
            var page = catalog.ByFacet(facet);
            if (page != null && page.ContentKey == _residentKey[facet]) continue;
            changed.Add(facet);
            if (facet != Current && !IsPinned(facet)) Drop(facet);
        }
        foreach (var facet in _verified.Keys.ToArray())
        {
            var page = catalog.ByFacet(facet);
            if (page == null || page.ContentKey != _verified[facet].key) _verified.TryRemove(facet, out _);
        }
        return changed;
    }

    /// <summary>Drops every resident page (shutdown or loader replaced).</summary>
    internal void Clear()
    {
        _generation++;
        foreach (var facet in _lru.ToArray()) Drop(facet);
        Current = -1;
    }

    internal bool IsStale(int facet) =>
        _residentKey.TryGetValue(facet, out var key) && Catalog.ByFacet(facet)?.ContentKey != key;

    private void Drop(int facet)
    {
        _backend.UnloadPage(facet);
        _residentKey.Remove(facet);
        _residentCopy.Remove(facet);
        _lru.Remove(facet);
    }

    private void Prefetch(PageEntry page)
    {
        int generation = _generation;
        foreach (var dir in PageCatalog.Directions)
        {
            if (!page.Neighbors.TryGetValue(dir, out var id)) continue;
            var neighbor = Catalog.ById(id);
            if (neighbor == null || IsVerified(neighbor.Facet)) continue;
            lock (_prefetching)
            {
                if (!_prefetching.Add(neighbor.Facet)) continue;
            }

            string key = neighbor.ContentKey;
            int facet = neighbor.Facet;
            _run(() =>
            {
                string fingerprint = PageVerifier.Fingerprint(neighbor);
                string problem = PageVerifier.Verify(neighbor);
                return problem == null ? fingerprint : null;
            }).ContinueWith(t =>
            {
                lock (_prefetching) _prefetching.Remove(facet);
                // Only a result for the generation that asked is kept: a newer
                // crossing or reload may have changed what this facet means.
                // (IsVerified also compares the content key, so a late write
                // for replaced content can never make it count as verified.)
                if (t.Status != TaskStatus.RanToCompletion || t.Result == null) return;
                if (Volatile.Read(ref _generation) != generation)
                {
                    lock (_prefetching) StaleResultsDropped++;
                    return;
                }
                _verified[facet] = (key, t.Result);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
#endif
