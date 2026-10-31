// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GUO.Assets;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Managers;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>
/// Game-thread snapshot cache. Native loader revisions plus an exact bounded live
/// record sequence certify reuse; hashes alone never decide equality. New snapshots
/// own their arrays, so returning/replacing this cache cannot mutate an older ticket.
/// </summary>
internal sealed class NativePhysicalWorldCache
{
    private readonly List<ulong> _candidate = new(), _accepted = new();
    private World _world;
    private object _map, _files, _staticData, _landData;
    private PhysicalWorldSnapshot _snapshot;
    private PhysicalRect _bounds;
    private ulong _worldIdentity, _artRevision;
    private long _mapRevision;
    private int _themeRevision, _season, _facet;
    private bool _acceptedComplete;
    public long CacheHits { get; private set; }
    public long CacheBuilds { get; private set; }
    public double LastBuildMilliseconds => _snapshot?.Diagnostics.BuildMilliseconds ?? 0;

    public PhysicalWorldSnapshot Capture(World world, PhysicalSnapshotKey key, PhysicalRect bounds)
    {
        var files = Client.Game.UO.FileManager;
        bool signatureComplete = BuildLiveSignature(world, bounds, files.TileData);
        bool reusable = signatureComplete && _acceptedComplete && _snapshot != null
            && ReferenceEquals(_world, world) && ReferenceEquals(_map, world.Map)
            && ReferenceEquals(_files, files) && ReferenceEquals(_staticData, files.TileData.StaticData)
            && ReferenceEquals(_landData, files.TileData.LandData) && _worldIdentity == key.World && _facet == key.Facet
            && _bounds == bounds && _mapRevision == files.Maps.DataRevision && _artRevision == files.Arts.DataRevision
            && _themeRevision == ThemeManager.Revision && _season == (int)world.Season
            && CollectionsMarshal.AsSpan(_candidate).SequenceEqual(CollectionsMarshal.AsSpan(_accepted));
        if (reusable) { CacheHits++; return _snapshot; }

        PhysicalWorldSnapshot next = NativePhysicalWorldProducer.Build(world, key, bounds, !signatureComplete);
        _world = world; _map = world.Map; _files = files;
        _staticData = files.TileData.StaticData; _landData = files.TileData.LandData;
        _worldIdentity = key.World; _facet = key.Facet; _bounds = bounds;
        _mapRevision = files.Maps.DataRevision; _artRevision = files.Arts.DataRevision;
        _themeRevision = ThemeManager.Revision; _season = (int)world.Season;
        _accepted.Clear();
        if (signatureComplete) _accepted.AddRange(_candidate);
        _acceptedComplete = signatureComplete;
        _snapshot = next; CacheBuilds++;
        return next;
    }

    private bool BuildLiveSignature(World world, PhysicalRect bounds, TileDataLoader tileData)
    {
        _candidate.Clear();
        int visited = 0;
        foreach (Item item in world.Items.Values)
        {
            if (visited++ >= NativePhysicalWorldProducer.MaxSourceRecords) return false;
            if (item.IsDestroyed || !item.OnGround || item.IsMulti || !InHalo(bounds, item.X, item.Y)) continue;
            Record(1, item.Serial, 0, item, tileData);
        }
        foreach (House house in world.HouseManager.Houses)
        {
            if (visited++ >= NativePhysicalWorldProducer.MaxSourceRecords) return false;
            if (house.Serial == 0) continue;
            for (int i = 0; i < house.Components.Count; i++)
            {
                if (visited++ >= NativePhysicalWorldProducer.MaxSourceRecords) return false;
                Multi component = house.Components[i];
                if (component.IsDestroyed || !InHalo(bounds, component.X, component.Y)) continue;
                Record(2, house.Serial, (uint)i, component, tileData);
                _candidate.Add(house.Revision);
            }
        }
        _candidate.Add(ulong.MaxValue); // Terminate the overlay before the cell-coverage sequence.
        Span<byte> loaded = stackalloc byte[PhysicalWorldBuilder.MaxSide * PhysicalWorldBuilder.MaxSide];
        NativePhysicalWorldProducer.FillLoadedCoverage(world, bounds, loaded);
        ulong bits = 0; int bit = 0;
        for (int i = 0; i < bounds.Width * bounds.Height; i++)
        {
            if (loaded[i] != 0) bits |= 1UL << bit;
            if (++bit == 64) { _candidate.Add(bits); bits = 0; bit = 0; }
        }
        if (bit != 0) _candidate.Add(bits);
        return true;
    }

    private void Record(ulong kind, uint identity, uint component, GameObject obj, TileDataLoader data)
    {
        _candidate.Add(kind);
        _candidate.Add(identity | ((ulong)component << 32));
        _candidate.Add(obj.X | ((ulong)obj.Y << 16) | ((ulong)(byte)obj.Z << 32) | ((ulong)obj.Graphic << 40));
        // Fractional motion is currently explicitly unsupported by the physical producer.
        // Keep its actual state in the signature so a stopped door/boat qualifies immediately.
        _candidate.Add(BitConverter.SingleToUInt32Bits(obj.Offset.X) | ((ulong)BitConverter.SingleToUInt32Bits(obj.Offset.Y) << 32));
        _candidate.Add(BitConverter.SingleToUInt32Bits(obj.Offset.Z));
        if (obj.Graphic < data.StaticData.Length)
        {
            _candidate.Add((ulong)data.StaticData[obj.Graphic].Flags);
            _candidate.Add(data.StaticData[obj.Graphic].Height);
        }
        else { _candidate.Add(ulong.MaxValue); _candidate.Add(ulong.MaxValue); }
    }

    private static bool InHalo(PhysicalRect b, int x, int y) => x >= b.X - 1 && x <= b.Right && y >= b.Y - 1 && y <= b.Bottom;
}
