using System.Globalization;
using System.Text;
using System.Text.Json;
using CentrED.MapGen.IR;

namespace GuoMapGen;

/// <summary>
/// Writes a generated map as a world project's replaced blocks (docs/data_formats.md §9), laid at
/// an origin on one facet, so the editor's World tab opens it over the install. The block text
/// matches the editor's own WorldProject.WriteBlock byte for byte: one land row and one static
/// per line, statics sorted by y, x, z, id. project.json is left to the editor (it fingerprints
/// the install when the project is first opened).
/// </summary>
public static class WorldProjectWriter
{
    public static Dictionary<string, object?> Write(GenIR ir, string root, int facet, int originX, int originY, string hash, string runDir)
    {
        if (originX % 8 != 0 || originY % 8 != 0) throw new CliError("--origin-x and --origin-y must be multiples of 8");
        OutputFolder.PrepareFresh(root);
        var ci = CultureInfo.InvariantCulture;
        var dir = Path.Combine(root, "blocks", facet.ToString(ci));
        Directory.CreateDirectory(dir);

        var statics = new Dictionary<(int, int), List<StaticOp>>();
        foreach (var op in ir.StaticOps)
        {
            if (op.X >= ir.Width || op.Y >= ir.Height) continue;
            var key = (op.X / 8, op.Y / 8);
            if (!statics.TryGetValue(key, out var list)) statics[key] = list = new List<StaticOp>();
            if (op.Kind == StaticOpKind.Add) list.Add(op);
            else
            {
                int i = list.FindIndex(s => s.X == op.X && s.Y == op.Y && s.Z == op.Z && s.Id == op.Id);
                if (i >= 0) list.RemoveAt(i);
            }
        }

        int bw = ir.Width / 8, bh = ir.Height / 8, written = 0;
        var sb = new StringBuilder();
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int obx = bx + originX / 8, oby = by + originY / 8;
            sb.Clear();
            sb.Append("{\n");
            sb.Append("  \"format\": 1,\n");
            sb.Append($"  \"facet\": {facet},\n");
            sb.Append($"  \"block\": [{obx}, {oby}],\n");
            sb.Append("  \"land\": [\n");
            for (int y = 0; y < 8; y++)
            {
                sb.Append("    \"");
                for (int x = 0; x < 8; x++)
                {
                    if (x > 0) sb.Append(' ');
                    int i = (by * 8 + y) * ir.Width + bx * 8 + x;
                    sb.Append(ir.LandId![i].ToString("X4", ci)).Append(':').Append(ir.Height_Z![i].ToString(ci));
                }
                sb.Append(y < 7 ? "\",\n" : "\"\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"statics\": [");
            var list = statics.TryGetValue((bx, by), out var l)
                ? l.OrderBy(s => s.Y % 8).ThenBy(s => s.X % 8).ThenBy(s => s.Z).ThenBy(s => s.Id).ToList()
                : new List<StaticOp>();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{\"id\": \"0x{s.Id:X4}\", \"x\": {s.X % 8}, \"y\": {s.Y % 8}, \"z\": {s.Z.ToString(ci)}, \"hue\": \"0x{s.Hue:X4}\"}}");
            }
            sb.Append(list.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(dir, $"{obx}_{oby}.json"), sb.ToString());
            written++;
        }

        var marker = new Dictionary<string, object?>
        {
            ["schema"] = "guo.mapgen.generated/1",
            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", ci),
            ["hash"] = hash,
            ["run"] = Path.GetFullPath(runDir),
            ["facet"] = facet,
            ["origin"] = new[] { originX, originY },
            ["size"] = new[] { (int)ir.Width, (int)ir.Height },
            ["blocks"] = written,
        };
        File.WriteAllText(Path.Combine(root, "generated.json"), JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        return marker;
    }
}
