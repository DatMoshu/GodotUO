// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Assets;
using GUO.Renderer.Lights;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

// Derived recipes only: native pixel arrays are read transiently and never retained.
internal sealed class PhysicalArtRecipe
{
    private static readonly Dictionary<ushort, PhysicalArtRecipe> Cache = new();
    private static object _source;
    private static ulong _revision;
    private readonly ArtLoader _loader;
    private readonly ushort _graphic;
    private readonly Dictionary<float, bool> _floors = new();
    private readonly Dictionary<(WallFaces.Face Face, float Lo, float Hi, float Height), bool> _walls = new();
    public readonly int Width, Height;
    public readonly WallFaces Faces;
    public readonly ulong CoverageFingerprint;

    // Game-thread-only, like the native art loader and producer.
    internal static PhysicalArtRecipe For(ushort graphic)
    {
        var source = Client.Game.UO.FileManager.Arts;
        if (!ReferenceEquals(_source, source) || _revision != source.DataRevision)
        { Cache.Clear(); _source = source; _revision = source.DataRevision; }
        if (Cache.TryGetValue(graphic, out var recipe)) return recipe;
        if (Cache.Count >= 256) Cache.Clear();
        var art = source.GetArt((uint)(graphic + ArtLoader.MAX_LAND_DATA_INDEX_COUNT));
        recipe = new PhysicalArtRecipe(source, graphic, art);
        Cache[graphic] = recipe;
        return recipe;
    }

    private PhysicalArtRecipe(ArtLoader loader, ushort graphic, ArtInfo art)
    {
        _loader = loader; _graphic = graphic;
        Width = art.Width; Height = art.Height;
        Faces = WallFaces.Classify(art.Pixels, Width, Height);
        ulong hash = 14695981039346656037UL;
        foreach (uint pixel in art.Pixels) { hash ^= pixel == 0 ? 0UL : 1UL; hash *= 1099511628211UL; }
        CoverageFingerprint = hash;
    }

    internal ArtInfo ReadArt() => _loader.GetArt((uint)(_graphic + ArtLoader.MAX_LAND_DATA_INDEX_COUNT));

    internal static bool Opaque(ReadOnlySpan<uint> pixels, int width, int height, float x, float y, float z)
    {
        int column = (int)MathF.Floor((width >> 1) + 22 * (x - y));
        int row = (int)MathF.Floor(height - 44 + 22 * (x + y) - 4 * z);
        return column >= 0 && column < width && row >= 0 && row < height &&
            row * width + column < pixels.Length && pixels[row * width + column] != 0;
    }

    public bool FullFloor(float z)
    {
        if (_floors.TryGetValue(z, out bool result)) return result;
        if (_floors.Count >= 8) _floors.Clear();
        result = CheckFloor(ReadArt(), z);
        _floors[z] = result;
        return result;
    }

    internal static bool CheckFloor(ArtInfo art, float z)
    {
        int checkedPixels = 0;
        int width = art.Width, height = art.Height;
        if (art.Pixels.Length < width * height) return false;
        // Enumerate the full projected floor, not just the part inside the art
        // image. A cropped but opaque sprite must not certify a full tile.
        int firstColumn = (width >> 1) - 22, lastColumn = (width >> 1) + 22;
        int firstRow = (int)MathF.Floor(height - 44 - 4 * z), lastRow = (int)MathF.Ceiling(height - 4 * z);
        for (int row = firstRow; row < lastRow; row++)
        for (int column = firstColumn; column < lastColumn; column++)
        {
            float diff = (column + .5f - (width >> 1)) / 22;
            float sum = (row + .5f - height + 44 + 4 * z) / 22;
            float x = (sum + diff) * .5f, y = (sum - diff) * .5f;
            if (x <= .025f || x >= .975f || y <= .025f || y >= .975f) continue;
            if (column < 0 || column >= width || row < 0 || row >= height || art.Pixels[row * width + column] == 0) return false;
            checkedPixels++;
        }
        return checkedPixels > 0 && Opaque(art.Pixels,width,height,.05f,.05f,z)
            && Opaque(art.Pixels,width,height,.95f,.95f,z) && Opaque(art.Pixels,width,height,.05f,.95f,z)
            && Opaque(art.Pixels,width,height,.95f,.05f,z);
    }

    public bool FullWall(WallFaces.Face face, float lo, float hi, float height)
    {
        var key = (face,lo,hi,height);
        if (_walls.TryGetValue(key,out bool result)) return result;
        if (_walls.Count >= 16) _walls.Clear();
        result = CheckWall(ReadArt(),face,lo,hi,height);
        _walls[key] = result;
        return result;
    }

    private static bool CheckWall(ArtInfo art, WallFaces.Face face, float lo, float hi, float height)
    {
        int checkedPixels = 0;
        int width = art.Width, artHeight = art.Height;
        if (art.Pixels.Length < width * artHeight) return false;
        for (int row = 0; row < artHeight; row++)
        for (int column = 0; column < width; column++)
        {
            float diff = (column + .5f - (width >> 1)) / 22;
            float along = face.AlongX ? face.Offset + diff : face.Offset - diff;
            float z = (artHeight - 44 + 22 * (along + face.Offset) - row - .5f) / 4;
            if (along <= lo + .025f || along >= hi - .025f || z <= .125f || z >= height - .125f) continue;
            if (art.Pixels[row * width + column] == 0) return false;
            checkedPixels++;
        }
        float first = lo + .05f * (hi - lo), last = hi - .05f * (hi - lo);
        return checkedPixels >= (hi - lo) * 22 * height * 4 * .75f &&
            Corner(art,face,first,.25f) && Corner(art,face,last,.25f) &&
            Corner(art,face,first,height-.25f) && Corner(art,face,last,height-.25f);
    }

    private static bool Corner(ArtInfo art, WallFaces.Face face, float along, float z) =>
        Opaque(art.Pixels,art.Width,art.Height,face.AlongX ? along : face.Offset,face.AlongX ? face.Offset : along,z);
}
