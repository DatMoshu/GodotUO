// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Host;

/// <summary>
/// Writes what the client holds of the world around the player: every item
/// and mobile within <see cref="Range"/> tiles, as the server sent them.
/// </summary>
/// <remarks>
/// The machine-checkable half of "the client shows it" for the editor's world
/// objects (ADR-0014): tools/editor_objects_proof looks for the exported
/// decoration and the spawner's creature here, and keeps the frame beside it.
/// </remarks>
internal static class ObjectsDump
{
    public const int Range = 12;

    public static void Write(string path)
    {
        World world = Client.Game?.UO?.World;
        var items = new List<Dictionary<string, object>>();
        var mobiles = new List<Dictionary<string, object>>();
        if (world?.Player != null)
        {
            int px = world.Player.X, py = world.Player.Y;
            foreach (Item i in world.Items.Values)
            {
                if (i.OnGround && System.Math.Abs(i.X - px) <= Range && System.Math.Abs(i.Y - py) <= Range)
                {
                    items.Add(new() { ["serial"] = i.Serial, ["graphic"] = $"0x{i.Graphic:X4}", ["hue"] = $"0x{i.Hue:X4}",
                        ["x"] = i.X, ["y"] = i.Y, ["z"] = i.Z, ["name"] = i.Name ?? "" });
                }
            }

            foreach (Mobile m in world.Mobiles.Values)
            {
                if (m != world.Player && System.Math.Abs(m.X - px) <= Range && System.Math.Abs(m.Y - py) <= Range)
                {
                    mobiles.Add(new() { ["serial"] = m.Serial, ["body"] = $"0x{m.Graphic:X4}",
                        ["x"] = m.X, ["y"] = m.Y, ["z"] = m.Z, ["name"] = m.Name ?? "" });
                }
            }
        }

        var report = new Dictionary<string, object>
        {
            ["map"] = world?.MapIndex ?? -1,
            ["player"] = world?.Player != null ? new[] { (int)world.Player.X, world.Player.Y, world.Player.Z } : null,
            ["items"] = items,
            ["mobiles"] = mobiles,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Godot.GD.Print($"[GUO] objects dump: {items.Count} item(s), {mobiles.Count} mobile(s) near the player -> {path}");
    }
}
