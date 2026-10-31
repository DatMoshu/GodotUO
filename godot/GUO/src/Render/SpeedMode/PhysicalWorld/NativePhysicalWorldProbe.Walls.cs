// SPDX-License-Identifier: BSD-2-Clause
// Actual native wall receiver pixels checked against their owner's source mesh.
using System;
using System.Linq;
using System.Threading.Tasks;
using GUO.Game.GameObjects;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private readonly record struct WallWitness(uint Serial, ushort Graphic, int X, int Y, int Z, int Height);

    private ushort FindReceiverWall(bool alongX)
    {
        ref var tiles = ref _game.UO.FileManager.TileData.StaticData;
        int candidates = 0;
        for (ushort id = 1; id < Math.Min(tiles.Length, 0x4000) && candidates < 256; id++) {
            var tile = tiles[id];
            if (!tile.IsWall || tile.IsWindow || tile.IsDoor || tile.IsAnimated || tile.IsTransparent
                || tile.IsTranslucent || tile.Height < 16 || tile.Height > 40) continue;
            candidates++;
            if (_game.UO.Arts.GetArt(id).Texture == null) continue;
            var item = Place(id, X, Y, _z);
            bool admitted = PhysicalReceiverRecipe.Wall(item, out bool axis, out byte offsetCode) && axis == alongX;
            item.Destroy();
            if (admitted) {
                _rows.Add(new { test = "native-wall-receiver-selection", graphic = id, alongX, offsetCode,
                    height = tile.Height, candidates });
                return id;
            }
        }
        throw new InvalidOperationException("No qualified native " + (alongX ? "X" : "Y")
            + " wall receiver among " + candidates + " bounded candidates.");
    }

    private static bool OnOwnerTriangle(N3 point, PhysicalTriangle triangle)
    {
        // Bounds and native-unit tolerance are independent of the shader's
        // packed metadata. The additional barycentric test checks an actual
        // emitted triangle, rather than merely its plane or bounding rectangle.
        N3 minimum = N3.Min(triangle.A, N3.Min(triangle.B, triangle.C));
        N3 maximum = N3.Max(triangle.A, N3.Max(triangle.B, triangle.C));
        if (point.X < minimum.X - .02f || point.X > maximum.X + .02f
            || point.Y < minimum.Y - .02f || point.Y > maximum.Y + .02f
            || point.Z < minimum.Z - .13f || point.Z > maximum.Z + .13f) return false;
        N3 Metric(N3 p) => new(p.X, p.Y, p.Z * .125f);
        N3 a = Metric(triangle.A), ab = Metric(triangle.B) - a, ac = Metric(triangle.C) - a;
        N3 normal = N3.Cross(ab, ac); float length = normal.Length();
        if (length < 1e-6f) return false;
        normal /= length;
        N3 relative = Metric(point) - a;
        float distance = N3.Dot(relative, normal);
        if (MathF.Abs(distance) > .02f) return false;
        relative -= normal * distance;
        float aa = N3.Dot(ab, ab), bb = N3.Dot(ac, ac), cross = N3.Dot(ab, ac);
        float denominator = aa * bb - cross * cross;
        if (MathF.Abs(denominator) < 1e-8f) return false;
        float first = (bb * N3.Dot(relative, ab) - cross * N3.Dot(relative, ac)) / denominator;
        float second = (aa * N3.Dot(relative, ac) - cross * N3.Dot(relative, ab)) / denominator;
        return first >= -.015f && second >= -.015f && first + second <= 1.015f;
    }

    private int CheckNativeWallPixels(Shot shot, WallWitness wall, bool alongX, bool removed = false)
    {
        var triangles = shot.Snapshot.Triangles.ToArray()
            .Where(t => t.Owner.Kind == PhysicalSourceKind.Item && t.Owner.Identity == wall.Serial).ToArray();
        int count = 0, matched = 0, wrongAxis = 0;
        for (int y = 0; y < shot.Surface.GetHeight(); y++) for (int x = 0; x < shot.Surface.GetWidth(); x++) {
            var pixel = shot.Surface.GetPixel(x, y); int packed = (int)MathF.Round(pixel.A), kind = packed & 15;
            if ((kind != 2 && kind != 3) || (packed & 256) == 0 || (packed & 128) != 0) continue;
            N3 point = new(pixel.R + shot.Origin.X, pixel.G + shot.Origin.Y, pixel.B);
            if (point.X < wall.X - .025f || point.X > wall.X + 1.025f
                || point.Y < wall.Y - .025f || point.Y > wall.Y + 1.025f
                || point.Z < wall.Z - .13f || point.Z > wall.Z + wall.Height + .13f) continue;
            count++;
            if (kind != (alongX ? 2 : 3)) wrongAxis++;
            if (triangles.Any(t => OnOwnerTriangle(point, t))) matched++;
        }
        if (removed) Check(triangles.Length == 0 && count == 0, "removed native wall has no source or qualified receiver pixels " + alongX);
        else {
            Check(triangles.Length > 0 && count >= 3, "actual native wall has visible qualified receiver pixels " + alongX);
            Check(matched == count && wrongAxis == 0, "every actual wall receiver lies on its emitted owner triangle " + alongX);
        }
        _rows.Add(new { test = "native-wall-pixel-triangle", graphic = wall.Graphic, serial = wall.Serial,
            alongX, removed, sourceTriangles = triangles.Length, count, matched, wrongAxis,
            xyTolerance = .02f, nativeZTolerance = .13f, generation = shot.Snapshot.Key.Generation }); Save();
        return count;
    }

    private async Task RunWallReceiverCases()
    {
#if DEBUG
        int savedZ = _z;
        try {
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            int highestGround = _z;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                _game.UO.World.Map.GetMapZ(X + x, Y + y, out sbyte landZ, out _);
                highestGround = Math.Max(highestGround, landZ);
            }
            _z = highestGround + 8;
            _game.UO.World.Player.SetInWorldTile(X, Y - 2, (sbyte)_z);
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) Place(0x0495, X + x, Y + y, _z);
            ushort xGraphic = FindReceiverWall(true), yGraphic = FindReceiverWall(false);
            var xWall = Place(xGraphic, X - 1, Y + 1, _z);
            var yWall = Place(yGraphic, X + 1, Y - 1, _z); Mutated();
            // Destroy clears native Graphic; preserve the ownership/geometry
            // receipt before removal so the negative checks cover the same region.
            WallWitness Witness(Item item) => new(item.Serial, item.Graphic, item.X, item.Y, item.Z,
                _game.UO.FileManager.TileData.StaticData[item.Graphic].Height);
            var xWitness = Witness(xWall); var yWitness = Witness(yWall);
            using var walls = await Capture("native-wall-receiver-axes-first-published-frame");
            CheckNativeWallPixels(walls, xWitness, true); CheckNativeWallPixels(walls, yWitness, false);
            xWall.Destroy(); yWall.Destroy(); Mutated();
            using var removed = await Capture("native-wall-receiver-axes-removed-first-published-frame");
            Check(removed.Snapshot.Key.Generation > walls.Snapshot.Key.Generation,
                "wall receiver removal advances first bound source generation");
            CheckNativeWallPixels(removed, xWitness, true, true); CheckNativeWallPixels(removed, yWitness, false, true);
        } finally { _z = savedZ; }
#endif
    }
}
