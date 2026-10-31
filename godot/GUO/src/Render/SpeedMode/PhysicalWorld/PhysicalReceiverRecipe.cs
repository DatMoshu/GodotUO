// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: confidence of native pixels' physical receiver correspondence.
using System;
using System.Collections.Generic;
using GUO.Game.GameObjects;
using GUO.Renderer.Lights;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

internal static class PhysicalReceiverRecipe
{
    internal readonly record struct RoofFit(bool Qualified, float Lift, float IntersectionOverUnion, float Coverage);
    private static readonly Dictionary<(ushort Graphic, int X, int Y), RoofFit> RoofFits = new();
    private static object _artSource;
    private static ulong _artRevision;

    // Map casters/terrain use raw native records; a seasonal or numeric-theme
    // replacement is not automatically the same physical pixel recipe. Live
    // Item/Multi sources already use their current Graphic in the producer.
    internal static bool NativePlacement(GameObject obj) => obj != null
        && obj.Offset.X == 0 && obj.Offset.Y == 0 && obj.Offset.Z == 0 && obj.ThemedVariant == null
        && (obj is not Static mapStatic || mapStatic.Graphic == mapStatic.OriginalGraphic)
        && (obj is not Land land || land.Graphic == land.OriginalGraphic);

    internal static bool Flat(GameObject obj, int height)
    {
        if (!NativePlacement(obj)) return false;
        if (obj is Land land) return land.Graphic > 2 && !land.TileData.IsWet;
        if (obj is not (Static or Multi or Item) || height != 0) return false;
        ref var tiles = ref Client.Game.UO.FileManager.TileData.StaticData;
        if (obj.Graphic >= tiles.Length) return false;
        ref var tile = ref tiles[obj.Graphic];
        if (!tile.IsSurface || tile.IsBridge || tile.IsRoof || tile.IsWall || tile.IsWindow
            || tile.IsDoor || tile.IsWet || tile.IsTransparent || tile.IsTranslucent || tile.IsAnimated) return false;
        // Surface metadata is also produced when optional lighting is off.
        // An unavailable recipe must never stop the native colour draw.
        try { return PhysicalArtRecipe.For(obj.Graphic).FullFloor(0); }
        catch (Exception) { return false; }
    }

    internal static bool Wall(GameObject obj, out bool alongX, out byte offsetCode)
    {
        alongX = false; offsetCode = 0;
        if (obj is not (Static or Multi or Item) || !NativePlacement(obj)) return false;
        ref var tiles = ref Client.Game.UO.FileManager.TileData.StaticData;
        if (obj.Graphic >= tiles.Length) return false;
        ref var tile = ref tiles[obj.Graphic];
        if ((!tile.IsWall && !tile.IsDoor) || tile.IsRoof || tile.IsWindow || tile.IsWet || tile.IsFoliage
            || tile.IsTransparent || tile.IsTranslucent || tile.IsAnimated || tile.Height == 0) return false;
        try
        {
            var art = PhysicalArtRecipe.For(obj.Graphic);
            if (art.Faces.Count != 1) return false;
            var face = art.Faces.First;
            // The single face covers the sprite's complete column interval.
            // Native alpha and those raster bounds provide its partial-width
            // clipping; corner/two-face art needs richer metadata.
            if (face.From != 0 || face.To != art.Width || !TryEncodeWallOffset(face.Offset, out offsetCode)
                || !WallSpan(art, face, out float lo, out float hi) || !art.FullWall(face, lo, hi, tile.Height)) return false;
            alongX = face.AlongX;
            return true;
        }
        catch (Exception) { return false; }
    }

    internal static bool WallSpan(PhysicalArtRecipe art, WallFaces.Face face, out float lo, out float hi)
    {
        // Shared with the structural producer: fitted art columns map to the
        // physical along-face interval. Do not expand a short post to a tile.
        float left = 22 - (art.Width >> 1);
        float p = left + face.From, q = left + face.To;
        lo = face.AlongX ? face.Offset + (p - 22) / 22 : face.Offset - (q - 22) / 22;
        hi = face.AlongX ? face.Offset + (q - 22) / 22 : face.Offset - (p - 22) / 22;
        lo = Math.Clamp(lo, 0, 1); hi = Math.Clamp(hi, 0, 1);
        return hi - lo >= .1f;
    }

    internal static bool TryEncodeWallOffset(float offset, out byte code)
    {
        code = 0;
        if (!float.IsFinite(offset) || offset < 0 || offset > 1) return false;
        int value = (int)MathF.Round(offset * 88);
        // WallFaces' median K is half-integral: (K+22)/44 or(K-22)/44.
        // Every native fitted offset is therefore integer/88. Insist on exact
        // float equality, not a tolerance that changes the producer's plane.
        if (value < 0 || value > 88 || value / 88f != offset) return false;
        code = (byte)value;
        return true;
    }

    internal static N3 InverseWall(N2 localPixel, bool alongX, float offset)
    {
        // localPixel=(column-W/2,row-H+44); base instance Z is added by the
        // caller. This is the same plane inverse used by both surface shaders.
        float difference = localPixel.X / 22;
        float x = alongX ? difference + offset : offset;
        float y = alongX ? offset : offset - difference;
        return new(x, y, ((x + y) * 22 - localPixel.Y) / 4);
    }

    internal static bool PlanarRoof(GameObject obj, out int gradientX, out int gradientY, out byte liftCode)
    {
        gradientX = gradientY = 0; liftCode = 0;
        if (obj is not (Static or Multi or Item) || !NativePlacement(obj)) return false;
        ref var tiles = ref Client.Game.UO.FileManager.TileData.StaticData;
        if (obj.Graphic >= tiles.Length) return false;
        ref var tile = ref tiles[obj.Graphic];
        if (!tile.IsRoof || tile.IsWet || tile.IsWindow || tile.IsTransparent || tile.IsTranslucent || tile.IsAnimated
            || !RoofCorner(obj, 1, 0, out int east) || !RoofCorner(obj, 0, 1, out int south)
            || !RoofCorner(obj, 1, 1, out int southeast) || southeast != east + south - obj.Z) return false;
        gradientX = east - obj.Z; gradientY = south - obj.Z;
        RoofFit fit;
        try { fit = FitRoof(obj.Graphic, gradientX, gradientY); }
        catch (Exception) { return false; }
        if (!fit.Qualified) return false;
        // Roofs do not use tiledata height in their receiver inverse. Raw kind8
        // reserves that byte for the fitted half-Z lift, -4 through15.5 native Z.
        liftCode = (byte)MathF.Round((fit.Lift + 4) * 2);
        return true;
    }

    private static bool RoofCorner(GameObject obj, int dx, int dy, out int z)
    {
        z = obj.Z; bool found = false;
        for (var next = obj.World?.Map?.GetTile(obj.X + dx, obj.Y + dy, false); next != null; next = next.TNext)
        {
            if (next.IsDestroyed || next is not (Static or Multi or Item)
                || next.Offset.X != 0 || next.Offset.Y != 0 || next.Offset.Z != 0) continue;
            if (Math.Abs(next.Z - obj.Z) > 6) continue;
            // A changed map graphic could hide a raw roof or introduce a roof
            // that the native source does not contain. Do not infer this corner.
            if (next is Static mapStatic && mapStatic.Graphic != mapStatic.OriginalGraphic) return false;
            ref var tiles = ref Client.Game.UO.FileManager.TileData.StaticData;
            if (next.Graphic >= tiles.Length || !tiles[next.Graphic].IsRoof) continue;
            if (!NativePlacement(next)) return false;
            if (found && z != next.Z) return false;
            z = next.Z; found = true;
        }
        return found;
    }

    internal static RoofFit FitRoof(ushort graphic, int gradientX, int gradientY)
    {
        var arts = Client.Game.UO.FileManager.Arts;
        if (!ReferenceEquals(arts, _artSource) || _artRevision != arts.DataRevision)
        { RoofFits.Clear(); _artSource = arts; _artRevision = arts.DataRevision; }
        var key = (graphic, gradientX, gradientY);
        if (RoofFits.TryGetValue(key, out var cached)) return cached;
        if (RoofFits.Count >= 1024) RoofFits.Clear();
        var recipe = PhysicalArtRecipe.For(graphic);
        // Read native coverage only for a new graphic/slope recipe. The cache
        // retains a four-scalar fit receipt, never another copy of art pixels.
        var art = arts.GetArt((uint)(graphic + GUO.Assets.ArtLoader.MAX_LAND_DATA_INDEX_COUNT));
        RoofFit fit = FitRoofPixels(art.Pixels, recipe.Width, recipe.Height, gradientX, gradientY);
        RoofFits[key] = fit;
        return fit;
    }

    /// <summary>
    /// A high-confidence classic-view fit of an inferred planar roof, adapted
    /// from Sosaria's inspected fit_corner method. This establishes visual
    /// correspondence, not an independent measurement of unseen3D structure.
    /// </summary>
    internal static RoofFit FitRoofPixels(ReadOnlySpan<uint> pixels, int width, int height, int gx, int gy)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height || Math.Abs(gx) > 6 || Math.Abs(gy) > 6
            || MathF.Abs(1 - (gx + gy) / 11f) < .2f) return default;
        int opaque = 0;
        foreach (uint pixel in pixels[..(width * height)]) if (pixel != 0) opaque++;
        if (opaque < 128) return default;
        RoofFit best = default;
        float runnerUp = 0;
        for (int liftHalf = -8; liftHalf < 32; liftHalf++)
        {
            float lift = liftHalf * .5f;
            int intersection = 0, projected = 0, interior = 0, interiorHoles = 0;
            float north = height - 44 - 4 * lift, east = north + 22 - 4 * gx;
            float south = north + 44 - 4 * (gx + gy), west = north + 22 - 4 * gy;
            int firstRow = (int)MathF.Floor(MathF.Min(MathF.Min(north, east), MathF.Min(south, west)));
            int lastRow = (int)MathF.Ceiling(MathF.Max(MathF.Max(north, east), MathF.Max(south, west)));
            // Score the entire projected quad, including points outside the
            // art rectangle. Cropped opaque art is not evidence of a full roof.
            for (int row = firstRow; row < lastRow; row++)
            for (int column = (width >> 1) - 22; column < (width >> 1) + 22; column++)
            {
                N2 pixel = new(column + .5f - (width >> 1), row + .5f - height + 44);
                if (!TryInverseRoof(pixel, gx, gy, lift, out N3 p)) return default;
                if (p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1) continue;
                projected++;
                bool solid = row >= 0 && row < height && column >= 0 && column < width
                    && pixels[row * width + column] != 0;
                if (solid) intersection++;
                if (p.X > .025f && p.X < .975f && p.Y > .025f && p.Y < .975f)
                { interior++; if (!solid) interiorHoles++; }
            }
            float score = intersection / (float)Math.Max(1, opaque + projected - intersection);
            float coverage = intersection / (float)Math.Max(1, projected);
            // Require a complete opaque interior; roof holes need their own
            // alpha geometry, rather than making an inferred opaque patch.
            bool qualified = projected >= 128 && interior >= 100 && interiorHoles == 0 && score >= .90f && coverage >= .98f;
            if (score > best.IntersectionOverUnion)
            { runnerUp = best.IntersectionOverUnion; best = new(qualified, lift, score, coverage); }
            else runnerUp = MathF.Max(runnerUp, score);
        }
        // A flat score plateau does not establish which physical plane owns
        // these pixels. Keep that recipe visibly unsupported.
        if (best.IntersectionOverUnion - runnerUp < .005f) best = best with { Qualified = false };
        return best;
    }

    // pixel=(column-W/2,row-H+44), before instance placement. Returned Z has
    // the apparent art lift removed; X/Y stay on the inferred physical plane.
    internal static bool TryInverseRoof(N2 pixel, float gx, float gy, float lift, out N3 physical)
    {
        float denominator = 1 - (gx + gy) / 11f;
        physical = default;
        if (!float.IsFinite(denominator) || MathF.Abs(denominator) < .2f) return false;
        float difference = pixel.X / 22;
        float sum = (pixel.Y + 4 * lift) / 22;
        float x = (sum + difference) * .5f, y = (sum - difference) * .5f;
        float dz = (gx * x + gy * y) / denominator;
        physical = new(x + dz / 11, y + dz / 11, dz);
        return float.IsFinite(physical.X) && float.IsFinite(physical.Y) && float.IsFinite(physical.Z);
    }
}
