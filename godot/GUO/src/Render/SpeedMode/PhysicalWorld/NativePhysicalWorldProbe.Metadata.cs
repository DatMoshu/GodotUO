// SPDX-License-Identifier: BSD-2-Clause
// First-frame receiver metadata witnesses; no manually authored surface pixels.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Godot;
using GUO.Game.GameObjects;
using GUO.Game.Map;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private static bool QualifiedRow(uint data) => (data & 15) is 8 or 9 or 10 || ((data & 15) == 1 && (data & 256) != 0);

    private async Task DrawMetadataFrame()
    {
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position);
        _draw.Invoke(_game, null); await Painted();
    }

    private async Task CheckMovingReceiver(Item owner, int kind, string name)
    {
        float minimumZ = owner.Z, maximumZ = owner.Z;
        for (int state = 0; state < 3; state++) {
            bool moving = state == 1;
            owner.Offset = moving ? new Godot.Vector3(2, 0, 0) : Godot.Vector3.Zero;
            Mutated(); await DrawMetadataFrame();
            uint row = SurfaceRow.For(owner, true).Data;
            var ownerTriangles = Shadows.PhysicalWorld.Triangles.ToArray()
                .Where(t => t.Owner.Kind == PhysicalSourceKind.Item && t.Owner.Identity == owner.Serial).ToArray();
            bool sourcePresent = ownerTriangles.Length > 0;
            if (!moving && sourcePresent) {
                minimumZ = ownerTriangles.Min(t => MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)));
                maximumZ = ownerTriangles.Max(t => MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z)));
            }
            using var surface = SpeedModeBindings.Current.Surface.GetImage();
            int qualified = 0, unqualified = 0;
            var origin = SpeedModeBindings.Current.Origin;
            for (int y = 0; y < surface.GetHeight(); y++) for (int x = 0; x < surface.GetWidth(); x++) {
                var p = surface.GetPixel(x, y); int packed = (int)MathF.Round(p.A);
                if ((packed & 15) != kind) continue;
                // A qualified sloped roof has a Z interval, not owner.Z at
                // every pixel. Its unqualified fallback may use art lift and
                // tiledata height; keep that negative bounded to this roof cell
                // without incorrectly requiring it to be the physical plane.
                float zMargin = moving && kind == 5 ? 16f : .25f;
                if (p.B < minimumZ - zMargin || p.B > maximumZ + zMargin) continue;
                float wx = p.R + origin.X, wy = p.G + origin.Y;
                if (wx < owner.X + .1f || wx > owner.X + .9f || wy < owner.Y + .1f || wy > owner.Y + .9f) continue;
                if ((packed & 256) != 0) qualified++; else unqualified++;
            }
            Check(QualifiedRow(row) != moving && sourcePresent != moving, "moving native receiver and source qualification agree " + name + state);
            Check(moving ? qualified == 0 && unqualified >= 3 : qualified >= 3,
                "actual moving native pixels lose qualification and restore immediately " + name + state);
            _rows.Add(new { test = "native-moving-receiver", name, state, moving, row, sourcePresent,
                qualified, unqualified, minimumZ, maximumZ, generation = Shadows.PhysicalWorld.Key.Generation }); Save();
        }
    }

    private Static[] LoadedNativeStatics()
    {
        var result = new System.Collections.Generic.List<Static>();
        for (int y = Y - 14; y <= Y + 14; y++) for (int x = X - 14; x <= X + 14; x++)
            for (var obj = _game.UO.World.Map.GetTile(x, y, false); obj != null; obj = obj.TNext)
                if (obj is Static native && !native.IsDestroyed && native.InChunkMesh && native.MeshSpriteIndex >= 0)
                    result.Add(native);
        return result.ToArray();
    }

    private static string NativeMeshHash(ChunkMesh mesh) => Convert.ToHexString(SHA256.HashData(
        MemoryMarshal.AsBytes(mesh.Statics.Vertices.AsSpan(0, mesh.Statics.Count))));

    private async Task RunResidentReceiverCases()
    {
#if DEBUG
        var candidates = LoadedNativeStatics();
        var activation = candidates.FirstOrDefault(obj => QualifiedRow(SurfaceRow.For(obj, true).Data));
        Require(activation != null, "loaded native resident supports first-active qualification");
        var activationMesh = _game.UO.World.Map.GetChunk(activation.X, activation.Y, false).Mesh;
        _profile.LightingSpeedMode = false; activationMesh.IsDirty = true; Mutated(); await DrawMetadataFrame();
        uint offRow = activationMesh.Statics.SurfaceData[activation.MeshSpriteIndex];
        int offStamp = activationMesh.Statics.ContentStamp;
        string offVertices = NativeMeshHash(activationMesh);
        Check(!QualifiedRow(offRow), "classic resident build avoids optional art qualification");
        _profile.LightingSpeedMode = true; Mutated(); await DrawMetadataFrame();
        uint onRow = activationMesh.Statics.SurfaceData[activation.MeshSpriteIndex];
        Check(QualifiedRow(onRow) && onRow == SurfaceRow.For(activation, true).Data
            && activationMesh.Statics.ContentStamp > offStamp, "first active frame qualifies a resident built in classic mode");
        Check(NativeMeshHash(activationMesh) == offVertices, "first active metadata refresh preserves native colour vertices");
        _rows.Add(new { test = "native-resident-first-active", graphic = activation.Graphic, offRow, onRow,
            offStamp, onStamp = activationMesh.Statics.ContentStamp }); Save();

        // Find one real cached roof whose fresh row responds to a neighbouring
        // live Item. This keeps the mutation meaningful even when native art
        // cannot qualify for the high-confidence physical roof recipe.
        Static roof = null; int nx = 0, ny = 0, nz = 0;
        foreach (var candidate in candidates.Where(obj => obj.ItemData.IsRoof).Take(64)) {
            uint baseline = SurfaceRow.For(candidate, true).Data;
            foreach (var delta in new[] { (1, 0), (0, 1) }) {
                var trial = Place(candidate.Graphic, candidate.X + delta.Item1, candidate.Y + delta.Item2, candidate.Z);
                uint changed = SurfaceRow.For(candidate, true).Data; trial.Destroy();
                if (changed == baseline) continue;
                roof = candidate; nx = candidate.X + delta.Item1; ny = candidate.Y + delta.Item2; nz = candidate.Z; break;
            }
            if (roof != null) break;
        }
        Require(roof != null, "bounded loaded native roof responds to an Item neighbour");
        var mesh = _game.UO.World.Map.GetChunk(roof.X, roof.Y, false).Mesh;
        Mutated(); await DrawMetadataFrame();
        string vertices = NativeMeshHash(mesh); uint baselineRow = mesh.Statics.SurfaceData[roof.MeshSpriteIndex];
        var neighbour = Place(roof.Graphic, nx, ny, nz);
        for (int state = 0; state < 5; state++) {
            if (state == 1) neighbour.SetInWorldTile((ushort)nx, (ushort)ny, (sbyte)(nz + 3));
            if (state == 2) neighbour.Offset = new Godot.Vector3(2, 0, 0);
            if (state == 3) { neighbour.Offset = Godot.Vector3.Zero; neighbour.SetInWorldTile((ushort)nx, (ushort)ny, (sbyte)nz); }
            if (state == 4) neighbour.Destroy();
            uint before = mesh.Statics.SurfaceData[roof.MeshSpriteIndex]; int stamp = mesh.Statics.ContentStamp;
            long changes = mesh.PhysicalReceiverChanges;
            Mutated(); await DrawMetadataFrame();
            uint expected = SurfaceRow.For(roof, true).Data, actual = mesh.Statics.SurfaceData[roof.MeshSpriteIndex];
            Check(actual == expected, "cached native roof matches live Item neighbour on first frame " + state);
            Check(before == expected || (mesh.Statics.ContentStamp > stamp && mesh.PhysicalReceiverChanges > changes),
                "changed roof row advances resident upload stamp " + state);
            Check(NativeMeshHash(mesh) == vertices, "Item neighbour metadata update preserves native colour mesh " + state);
            if (state == 0) Check(actual != baselineRow, "Item addition really changes native resident roof metadata");
            if (state == 4) Check(actual == baselineRow, "Item removal restores native resident roof metadata");
            _rows.Add(new { test = "native-resident-roof-neighbour", graphic = roof.Graphic, x = roof.X, y = roof.Y,
                state, before, expected, actual, oldStamp = stamp, newStamp = mesh.Statics.ContentStamp,
                checks = mesh.PhysicalReceiverChecks, changes = mesh.PhysicalReceiverChanges }); Save();
        }
        int stableStamp = mesh.Statics.ContentStamp; mesh.RefreshPhysicalRoofReceivers();
        Check(mesh.Statics.ContentStamp == stableStamp, "unchanged roof refresh preserves resident content stamp");

        ushort originalGraphic = activation.Graphic;
        try {
            activation.Graphic = originalGraphic == 0x0495 ? (ushort)0x05E4 : (ushort)0x0495;
            Check(!PhysicalReceiverRecipe.NativePlacement(activation) && !QualifiedRow(SurfaceRow.For(activation, true).Data),
                "numeric replacement of a raw-map Static cannot claim native physical correspondence");
        } finally { activation.Graphic = originalGraphic; }
#endif
    }
}
