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
    private sealed class Entry(Candidate[] candidates, Dependency[] dependencies, long frame)
    {
        internal readonly Candidate[] Candidates = candidates;
        internal readonly Dependency[] Dependencies = dependencies;
        internal long Frame = frame;
        internal int References => Candidates.Length + Dependencies.Length;
    }

    internal const int MaxEntries = 8192, MaxReferences = 262144, MaxPlan = 1024;
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly List<Candidate> _building = new();
    private readonly List<Dependency> _dependencies = new();
    private readonly List<Key> _expired = new();
    private object _map;
    private long _tableVersion, _frame;
    private Key _key;
    private Chunk _lastChunk;
    private bool _retain;
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

    internal bool TryGet(object map, long tableVersion, Key key, out ReadOnlySpan<Candidate> candidates)
    {
        if (!ReferenceEquals(map, _map) || tableVersion != _tableVersion)
        {
            Clear();
            _map = map;
            _tableVersion = tableVersion;
        }
        if (_entries.TryGetValue(key, out var entry))
        {
            bool valid = true;
            foreach (var dependency in entry.Dependencies)
            {
                DependencyChecks++;
                if (dependency.Chunk.IsDestroyed || dependency.Version != dependency.Chunk.CoverMembershipVersion)
                {
                    valid = false;
                    break;
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
        _retain = true;
        _lastChunk = null;
        _building.Clear();
        _dependencies.Clear();
        candidates = default;
        return false;
    }

    // Called during the original walk, after GetFirstLand and before processing
    // that land. A null chunk is covered by the shared map-table generation.
    internal void Observe(Chunk chunk, int x, int y, Land land, int column)
    {
        if (!_retain || chunk == null) return;
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

    internal void Complete(long tableVersion)
    {
        bool valid = _retain && tableVersion == _tableVersion;
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
                var entry = new Entry(_building.ToArray(), _dependencies.ToArray(), _frame);
                _entries.Add(_key, entry);
                RetainedReferences += entry.References;
                Builds++;
            }
        }
        else Rejected++;
        _building.Clear();
        _dependencies.Clear();
        _lastChunk = null;
        _retain = false;
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
        RetainedReferences = 0;
    }
}
