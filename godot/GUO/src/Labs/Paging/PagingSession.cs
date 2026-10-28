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
/// Main-thread only, apart from the verification tasks it starts.
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
        error = null;
        var page = Catalog.ByFacet(facet);
        if (page == null)
        {
            error = PageCatalog.IsPageFacet(facet)
                ? $"facet {facet} is not in the page manifest {Catalog.ManifestPath}."
                : $"facet {facet} is not a page facet ({PageCatalog.FirstFacet}-{PageCatalog.LastFacet}).";
            LastError = error;
            return false;
        }

        // A resident copy built from other content (new version, new hash or a
        // replaced file) is dropped and rebuilt rather than reused.
        if (_residentKey.TryGetValue(facet, out var loadedKey) && (loadedKey != page.ContentKey || !IsVerified(facet)))
            Drop(facet);

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
            Loads++;
        }

        _lru.Remove(facet);
        _lru.AddFirst(facet);
        Current = facet;

        while (_lru.Count > Cap)
        {
            // Oldest first; the current page is at the front and never reached.
            Drop(_lru.Last.Value);
            Evictions++;
        }

        LastError = null;
        Prefetch(page);
        return true;
    }

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
            if (facet != Current) Drop(facet);
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
