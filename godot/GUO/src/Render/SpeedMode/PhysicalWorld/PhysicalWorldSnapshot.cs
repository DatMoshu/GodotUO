// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: native structural geometry for the optional enhanced lighting route.
using System;
using System.Collections.Generic;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

internal enum PhysicalSourceKind : byte { Land, MapStatic, Item, Multi }
internal readonly record struct PhysicalOwnerKey(PhysicalSourceKind Kind, ulong Identity, uint Component = 0);
internal readonly record struct PhysicalSnapshotKey(ulong World, ulong Generation, int Facet);
internal readonly record struct PhysicalRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

[Flags]
internal enum PhysicalMaterial : byte
{
    None = 0, BlocksSun = 1, BlocksLocalLight = 2, BlocksWeather = 4, Receiver = 8,
    OpaqueStructure = BlocksSun | BlocksLocalLight | BlocksWeather | Receiver,
    Terrain = BlocksSun | BlocksLocalLight | Receiver
}

internal enum PhysicalProvenance : byte { NativeTerrain, NativeFloor, ArtQualifiedWall, NeighbourRoof }
internal enum PhysicalCellState : byte { Unknown, Complete }

[Flags]
internal enum PhysicalUnknownReason : ushort
{
    None = 0, MissingNativeBlock = 1, MissingLiveChunk = 2, UnsupportedRoof = 4,
    OpticalOpening = 8, UnsupportedWall = 16, UnsupportedStructure = 32,
    EnumerationLimit = 64, InvalidSource = 128, Boundary = 256
}

/// <summary>Coordinates are X/Y tiles and native UO Z; no presentation/camera lift.</summary>
internal readonly record struct PhysicalTriangle(N3 A, N3 B, N3 C,
    PhysicalOwnerKey Owner, PhysicalMaterial Material, PhysicalProvenance Provenance, ushort Graphic = 0);

internal readonly record struct PhysicalWorldDiagnostics(int NativeBlocks, int NativeStatics,
    int LiveItems, int MultiComponents, int ExcludedDecorative, int UnknownStructures,
    double BuildMilliseconds);

/// <summary>
/// Immutable, owned arrays: safe to retain until the frame's rendering ticket retires.
/// SourceFingerprint describes effective records read for this snapshot, not a portable
/// installation checksum. Generation and world identity remain part of every cache key.
/// Unknown cells must never be treated as empty space or unoccluded light.
/// </summary>
internal sealed class PhysicalWorldSnapshot
{
    private readonly PhysicalTriangle[] _triangles;
    private readonly PhysicalUnknownReason[] _unknown;
    internal PhysicalWorldSnapshot(PhysicalSnapshotKey key, PhysicalRect bounds,
        PhysicalTriangle[] triangles, PhysicalUnknownReason[] unknown,
        ulong sourceFingerprint, PhysicalWorldDiagnostics diagnostics, bool overflow)
    {
        Key = key;
        Bounds = bounds;
        _triangles = triangles;
        _unknown = unknown;
        SourceFingerprint = sourceFingerprint;
        Diagnostics = diagnostics;
        Overflow = overflow;
        N3 min = new(bounds.X, bounds.Y, 0), max = new(bounds.Right, bounds.Bottom, 0);
        foreach (PhysicalTriangle triangle in triangles)
        {
            min = N3.Min(min, N3.Min(triangle.A, N3.Min(triangle.B, triangle.C)));
            max = N3.Max(max, N3.Max(triangle.A, N3.Max(triangle.B, triangle.C)));
        }
        Min = min;
        Max = max;
        int complete = 0;
        foreach (PhysicalUnknownReason reason in unknown)
            if (reason == PhysicalUnknownReason.None) complete++;
        CompleteCellCount = overflow ? 0 : complete;
    }

    public PhysicalSnapshotKey Key { get; }
    public PhysicalRect Bounds { get; }
    public N3 Min { get; }
    public N3 Max { get; }
    public ulong SourceFingerprint { get; }
    public PhysicalWorldDiagnostics Diagnostics { get; }
    public bool Overflow { get; }
    public int CompleteCellCount { get; }
    public bool IsComplete => !Overflow && CompleteCellCount == Bounds.Width * Bounds.Height;
    public ReadOnlyMemory<PhysicalTriangle> Triangles => _triangles;
    public ReadOnlyMemory<PhysicalUnknownReason> UnknownCells => _unknown;
    public PhysicalUnknownReason UnknownAt(int x, int y) => !Bounds.Contains(x, y)
        ? PhysicalUnknownReason.Boundary
        : Overflow ? PhysicalUnknownReason.EnumerationLimit
        : _unknown[(y - Bounds.Y) * Bounds.Width + x - Bounds.X];
    public bool IsCellComplete(int x, int y) => UnknownAt(x, y) == PhysicalUnknownReason.None;
}

/// <summary>Bounded authoring helper. Seal transfers copies; subsequent edits cannot alter a ticket.</summary>
internal sealed class PhysicalWorldBuilder
{
    public const int MaxSide = 64;
    public const int MaxTriangles = 131072;
    private readonly PhysicalSnapshotKey _key;
    private readonly PhysicalRect _bounds;
    private readonly List<PhysicalTriangle> _triangles = new();
    private readonly PhysicalUnknownReason[] _unknown;
    private bool _overflow;

    public PhysicalWorldBuilder(PhysicalSnapshotKey key, PhysicalRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > MaxSide || bounds.Height > MaxSide)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        _key = key;
        _bounds = bounds;
        _unknown = new PhysicalUnknownReason[bounds.Width * bounds.Height];
        Array.Fill(_unknown, PhysicalUnknownReason.MissingNativeBlock);
    }

    public void MarkComplete(int x, int y)
    {
        if (_bounds.Contains(x, y)) _unknown[(y - _bounds.Y) * _bounds.Width + x - _bounds.X] = PhysicalUnknownReason.None;
    }

    public void MarkUnknown(int x, int y, PhysicalUnknownReason reason)
    {
        if (_bounds.Contains(x, y)) _unknown[(y - _bounds.Y) * _bounds.Width + x - _bounds.X] |= reason;
    }

    public bool AddTriangle(PhysicalTriangle triangle)
    {
        if (_triangles.Count >= MaxTriangles) { _overflow = true; return false; }
        if (!Finite(triangle.A) || !Finite(triangle.B) || !Finite(triangle.C))
        { _overflow = true; return false; }
        _triangles.Add(triangle);
        return true;
    }

    private static bool Finite(N3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public PhysicalWorldSnapshot Seal(ulong sourceFingerprint = 0, PhysicalWorldDiagnostics diagnostics = default)
        => new(_key, _bounds, _triangles.ToArray(), (PhysicalUnknownReason[])_unknown.Clone(), sourceFingerprint, diagnostics, _overflow);
}
