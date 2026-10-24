// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Game.GameObjects;
using GUO.Game.Map;

namespace GUO.Game.Scenes;

/// <summary>
/// PORT DEVIATION (GUO): retain only the ordered land identities discovered
/// after a static's live ground/height/width checks. Alpha, depth, cover stamps,
/// overlap and draw order remain live. Main-thread only; no rendering types.
/// </summary>
internal sealed class CoverCandidates
{
    internal readonly record struct Key(int X, int Y, int Rows, int Half);
    internal readonly record struct Candidate(Land Land, int Column);
    private readonly record struct Dependency(Chunk Chunk, long Version);
    private sealed class Entry(Candidate[] candidates, Dependency[] dependencies, long frame,
        long tableVersion, bool missingChunk)
    {
        internal readonly Candidate[] Candidates = candidates;
        internal readonly Dependency[] Dependencies = dependencies;
        internal long Frame = frame;
        internal readonly long TableVersion = tableVersion;
        internal readonly bool MissingChunk = missingChunk;
        internal int References => Candidates.Length + Dependencies.Length;
    }

    internal const int MaxEntries = 8192, MaxReferences = 262144, MaxPlan = 1024;
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly List<Candidate> _building = new();
    private readonly List<Dependency> _dependencies = new();
    private readonly List<Key> _expired = new();
    private object _map;
    private long _tableVersion, _tableIdentityVersion, _frame;
    private Key _key;
    private Chunk _lastChunk;
    private bool _retain, _missingChunk;
    internal int RetainedReferences { get; private set; }
    internal int Count => _entries.Count;
    internal static long Hits, Misses, Builds, Rejected, DependencyChecks;

    internal void NextFrame()
    {
        if (++_frame % 120 != 0) return;
        foreach (var pair in _entries)
            if (_frame - pair.Value.Frame >= 120) _expired.Add(pair.Key);
        foreach (var key in _expired) Remove(key);
        _expired.Clear();
    }

    internal bool TryGet(object map, long tableVersion, Key key, out ReadOnlySpan<Candidate> candidates,
        long tableIdentityVersion = 0)
    {
        if (!ReferenceEquals(map, _map) || tableIdentityVersion != _tableIdentityVersion)
        {
            Clear();
            _map = map;
            _tableIdentityVersion = tableIdentityVersion;
        }
        // Ordinary slot churn only invalidates plans that observed absent
        // chunks. Each entry owns its snapshot, so one hit cannot bless another.
        _tableVersion = tableVersion;
        if (_entries.TryGetValue(key, out var entry))
        {
            bool valid = !entry.MissingChunk || entry.TableVersion == tableVersion;
            if (valid)
            {
                foreach (var dependency in entry.Dependencies)
                {
                    DependencyChecks++;
                    if (dependency.Chunk.IsDestroyed || dependency.Version != dependency.Chunk.CoverMembershipVersion)
                    {
                        valid = false;
                        break;
                    }
                }
            }
            if (valid)
            {
                // Match the original lookup's residency side effect. Only
                // chunks consulted by the original walk are retained/touched.
                foreach (var dependency in entry.Dependencies)
                    dependency.Chunk.LastAccessTime = Time.Ticks;
                entry.Frame = _frame;
                candidates = entry.Candidates;
                Hits++;
                return true;
            }
            Remove(key);
        }
        Misses++;
        _key = key;
        // Validate/remove stale entries before admission: a full cache must
        // still serve hits, and removal can free the slot needed for this miss.
        _retain = _entries.Count < MaxEntries;
        _missingChunk = false;
        _lastChunk = null;
        _building.Clear();
        _dependencies.Clear();
        candidates = default;
        return false;
    }

    // Called during the original walk, after GetFirstLand and before processing
    // that land. Missing chunks need the ordinary slot generation as well as
    // the hard table/lifetime generation. Empty non-null chunks remain deps.
    internal void Observe(Chunk chunk, int x, int y, Land land, int column)
    {
        if (!_retain) return;
        if (chunk == null) { _missingChunk = true; return; }
        if (!chunk.HasCertifiedFirstLand(x, y))
        {
            _retain = false; // legacy foreign/detached chains must stay live
            return;
        }
        if (!ReferenceEquals(chunk, _lastChunk))
        {
            bool known = false;
            foreach (var dependency in _dependencies)
                if (ReferenceEquals(chunk, dependency.Chunk)) { known = true; break; }
            if (!known) _dependencies.Add(new Dependency(chunk, chunk.CoverMembershipVersion));
            _lastChunk = chunk;
        }
        if (land != null) _building.Add(new Candidate(land, column));
        if (_building.Count + _dependencies.Count > MaxPlan) _retain = false;
    }

    internal void Complete(long tableVersion, long tableIdentityVersion = 0)
    {
        if (!_retain)
        {
            Rejected++;
            ResetDiscovery();
            return;
        }
        // Reject any table change during discovery conservatively, even when
        // no null was seen. Never publish a mixed-generation plan.
        bool valid = tableVersion == _tableVersion && tableIdentityVersion == _tableIdentityVersion;
        foreach (var dependency in _dependencies)
            valid &= !dependency.Chunk.IsDestroyed && dependency.Version == dependency.Chunk.CoverMembershipVersion;
        if (valid)
        {
            int references = _building.Count + _dependencies.Count;
            // Keep warm entries at capacity instead of repeatedly discarding
            // them and allocating replacements. Expiration admits new regions.
            if (_entries.Count >= MaxEntries || RetainedReferences + references > MaxReferences)
            {
                Rejected++;
            }
            else
            {
                var entry = new Entry(_building.ToArray(), _dependencies.ToArray(), _frame,
                    _tableVersion, _missingChunk);
                _entries.Add(_key, entry);
                RetainedReferences += entry.References;
                Builds++;
            }
        }
        else Rejected++;
        ResetDiscovery();
    }

    private void ResetDiscovery()
    {
        _building.Clear();
        _dependencies.Clear();
        _lastChunk = null;
        _retain = false;
        _missingChunk = false;
    }

    private void Remove(Key key)
    {
        if (_entries.Remove(key, out var entry)) RetainedReferences -= entry.References;
    }

    internal void Clear()
    {
        _entries.Clear();
        _building.Clear();
        _dependencies.Clear();
        _expired.Clear();
        _lastChunk = null;
        _map = null;
        _retain = false;
        _missingChunk = false;
        RetainedReferences = 0;
    }
}
