#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private async Task VerifyCoast(WorldProject project, Action<string> chooseFile, Action<int> chooseSwatch)
    {
        const int width = 24, height = 16;
        const ushort shore = 3, water = 168, outside = 22;
        var cells = Enumerable.Range(0, width * height).Select(i => (X: i % width, Y: i / width)).ToArray();
        ushort Initial(int x, int y)
        {
            if (x <= 0 || x >= width - 1 || y <= 0 || y >= height - 1) return outside;
            bool inlet = x is >= 2 and <= 8 && y is >= 4 and <= 9;
            bool channel = x is >= 8 and <= 12 && y is >= 7 and <= 8;
            bool bay = x is >= 11 and <= 22 && y is >= 2 and <= 13;
            bool island = x is >= 14 and <= 19 && y is >= 5 and <= 10;
            return inlet || channel || (bay && !island) ? water : shore;
        }
        sbyte HeightAt(int x, int y) => (sbyte)((x + y) % 9 - 5);
        // Read the 3x3 as a grid, independently of the planner's compass walk.
        int Mask(int x, int y)
        {
            int[] bits = { 128, 1, 2, 64, 0, 4, 32, 16, 8 };
            int mask = 0;
            for (int i = 0; i < bits.Length; i++)
                if (Initial(x + i % 3 - 1, y + i / 3 - 1) == water) mask |= bits[i];
            return mask;
        }
        int[] masks = cells.Where(c => Initial(c.X, c.Y) == shore).Select(c => Mask(c.X,c.Y))
            .Where(m => m != 0).Distinct().Order().ToArray();
        // The pinned upstream writer fixture assigns consecutive marker IDs to sorted masks.
        var byMask = masks.Select((mask, index) => (mask, id: 32 + index)).ToDictionary(row => row.mask, row => row.id);
        int[] standard = { 131, 14, 56, 224, 1 << 1, 1 << 3, 1 << 5, 1 << 7, 143, 62, 248, 227 };
        if (standard.Except(masks).Any()) throw new InvalidOperationException("Coast fixture lost a straight or corner");
        string path = Path.Combine(EditorData.RepoRoot, "tools", "editor_smoke", "fixtures", "centred-coast-brushes.json");
        var patches = new Dictionary<(int X,int Y), Action<WorldBlock>>();
        for (int bx = 0; bx < 3; bx++)
        for (int by = 0; by < 2; by++)
            patches[(OverlayBx + bx, OverlayBy + by)] = b =>
            {
                b.Statics.Clear();
                for (int i = 0; i < 64; i++)
                {
                    int x = (b.Bx - OverlayBx) * 8 + i % 8, y = (b.By - OverlayBy) * 8 + i / 8;
                    b.LandId[i] = Initial(x,y); b.LandZ[i] = HeightAt(x,y);
                }
            };
        string[] Snapshot() => patches.Keys.Select(k => project.BlockText(0,k.X,k.Y)).ToArray();
        void SaveWitness(string name)
        {
            using var witness = WorldProject.OpenOrCreate(Path.Combine(_out, name),
                EditorData.Setting("UO_CLIENT_DATA", ""), EditorData.Setting("UO_CLIENT_VERSION", ""));
            foreach (var k in patches.Keys)
                witness.WriteBlock(WorldProject.ReadBlock(project.BlockPath(0, k.X, k.Y)));
        }
        var original = Snapshot();
        int undo = _world.Editor.UndoCount;
        bool locked = _world.TerrainLocked;
        uint previous = _data.CurrentArt;
        try
        {
            _world.QuickRecipeForSmoke("Single");
            chooseFile(path);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            chooseSwatch(1);
            _world.TerrainLocked = false;
            if (!_world.Editor.EditBatch(0, patches, "Coast test inlet")) throw new InvalidOperationException("Could not stage inlet");
            void Stroke(bool top) => _world.PlaceForSmoke(cells.Where(c => (c.Y < 8) == top)
                .Select(c => (OverlayBx * 8 + c.X, OverlayBy * 8 + c.Y)));
            var baseline = Snapshot();
            SaveWitness("coast-before");
            Stroke(true);
            var halfway = Snapshot();
            _world.SavePresetForSmoke("Inlet bank");
            _world.QuickRecipeForSmoke("Single");
            var list = _world.FindChildren("*", "ItemList", true, false).OfType<ItemList>()
                .Single(l => Enumerable.Range(0, l.ItemCount).Any(i => l.GetItemText(i).StartsWith("Inlet bank")));
            list.EmitSignal(ItemList.SignalName.ItemActivated,
                Enumerable.Range(0, list.ItemCount).Single(i => list.GetItemText(i).StartsWith("Inlet bank")));
            string invalid = Path.Combine(_out, "coast-chance.json");
            File.WriteAllText(invalid, """{"Bank":{"Tiles":[3],"Chance":10,"To":{}}}""");
            chooseFile(invalid);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_world.BrushStatus.Contains("Chance")) throw new InvalidOperationException("Expected a diagnostic for Chance");
            Stroke(false);
            if (_world.Editor.UndoCount != undo + 3) throw new InvalidOperationException("Expected one undo per stroke: " + _world.BrushStatus);
            SaveWitness("coast-after");
            using var opened = WorldProject.OpenOrCreate(project.Root, "", "");
            var blocks = opened.Blocks(0).ToDictionary(b => (b.Bx,b.By));
            var after = new List<ushort>();
            foreach (var c in cells)
            {
                var b = blocks[(OverlayBx + c.X / 8, OverlayBy + c.Y / 8)];
                int loc = (c.Y % 8) * 8 + c.X % 8;
                int id = Initial(c.X,c.Y);
                int mask = Mask(c.X, c.Y);
                if (id == shore && mask != 0) id = byMask[mask];
                if (b.LandId[loc] != id || b.LandZ[loc] != HeightAt(c.X,c.Y))
                    throw new InvalidOperationException($"Incorrect coast at {c}: expected {id}/{HeightAt(c.X,c.Y)}, got {b.LandId[loc]}/{b.LandZ[loc]}");
                after.Add(b.LandId[loc]);
            }
            _world.Editor.Undo();
            if (!Snapshot().SequenceEqual(halfway)) throw new InvalidOperationException("Undo did not restore preceding coast stroke");
            _world.Editor.Undo();
            if (!Snapshot().SequenceEqual(baseline)) throw new InvalidOperationException("Undo did not restore inlet");
            File.WriteAllText(Path.Combine(_out,"coast-inlet.json"), JsonSerializer.Serialize(new
            {
                width, height, origin = new[] { OverlayBx * 8, OverlayBy * 8 }, byMask,
                before = cells.Select(c => Initial(c.X,c.Y)), after, z = cells.Select(c => HeightAt(c.X,c.Y)).ToArray()
            }));
            _overlayReport["coast_cells_verified"] = cells.Length;
            _overlayReport["coast_distinct_masks"] = masks;
            _overlayReport["coast_preceding_stroke_restored"] = true;
            _overlayReport["coast_preset_and_invalid_import_recovery"] = true;
        }
        finally
        {
            while (_world.Editor.UndoCount > undo && _world.Editor.Undo()) { }
            _world.QuickRecipeForSmoke("Single");
            _data.CurrentArt = previous;
            _world.TerrainLocked = locked;
        }
        if (!Snapshot().SequenceEqual(original)) throw new InvalidOperationException("Coast fixture cleanup failed");
    }
}
#endif
