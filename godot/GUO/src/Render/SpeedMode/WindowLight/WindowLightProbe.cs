// SPDX-License-Identifier: BSD-2-Clause
// Synthetic contract; no client install or shard required.
using System;
using System.IO;
using TileVector = System.Numerics.Vector2;
using Godot;

namespace GUO.Renderer.SpeedMode.WindowLight;

public partial class WindowLightProbe : Node
{
    public override void _Ready()
    {
        try
        {
            var table = WindowLightTable.BuiltIn;
            Check(table.Count == 60, "60 built-in graphics");
            WindowOpening transom = table[0xF03D];
            Check(!transom.Contains(.5f, .75f) && transom.Contains(.5f, .25f), "two panes over one");
            Check(!transom.Contains(.25f, .5f), "transom bar");
            Check(transom.TryProject(TileVector.Zero, TileVector.UnitX, TileVector.UnitY, new TileVector(1, 1),
                out TileVector width, out TileVector depth), "inward ray");
            Check(TileVector.Distance(width, new TileVector(1.14f, 0)) < .0001f &&
                TileVector.Distance(depth, new TileVector(2.5f, 2.5f)) < .0001f, "floor parallelogram in tiles");
            Check(!transom.TryProject(TileVector.Zero, TileVector.UnitX, TileVector.UnitY, -TileVector.UnitY,
                out _, out _), "outward ray rejected");
            Check(!table[0xE7D5].Contains(.5f, .5f), "ragged hole explicitly excluded");
            foreach (ushort graphic in new ushort[] { 0xBAEA, 0xBAF9, 0x9959 })
            {
                Check(table[graphic].Contains(.5f, .95f), "crown centre open");
                Check(!table[graphic].Contains(.05f, .95f), "crown corners closed");
            }
            // C# rejects malformed pack data independently of the Python tool.
            bool rejected = false;
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                    "{\"schema\":\"guo/window-light@1\",\"windows\":[]}"));
                WindowLightTable.Parse(stream);
            }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "empty pack rejected");
            GD.Print("[window-light-probe] PASS table=60 projection=tile-space shapes=5");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("[window-light-probe] FAIL " + error);
            GetTree().Quit(1);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
