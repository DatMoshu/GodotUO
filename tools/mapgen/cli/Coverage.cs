using CentrED.MapGen.Data;
using CentrED.MapGen.Pipeline;

namespace GuoMapGen;

/// <summary>
/// <c>coverage</c>: how much of each transition pair a table can draw, in counts only, for the committed
/// GUO table, the user's resolved GUO table and the user's Dragon import (each when present). Per pair:
/// the edge shapes with a tile (of 12), the neighbour masks with a tile (of 255), and the distinct ids.
/// It never prints a table's rows, so a Dragon import serves as a coverage reference without being copied.
/// </summary>
public static class CoverageCommand
{
    public static int Execute(Args a, JsonEmitter json)
    {
        var tables = new List<(string Name, string Path)>
        {
            ("guo-core", RepoRootResolver.Resolve(GuoTransitionTable.RelativePath)),
            ("guo-resolved", RepoRootResolver.Resolve(GuoTransitionTable.ResolvedRelativePath)),
            ("dragon", RepoRootResolver.Resolve(LandBrushTable.DataJsonRelativePath)),
        };
        var loaded = tables.Where(t => File.Exists(t.Path)).Select(t => (t.Name, Table: LandBrushTable.LoadOrEmpty(t.Path))).ToList();
        var pairs = new List<Dictionary<string, object?>>();
        foreach (var (owner, other) in MeasureCommand.Pairs)
        {
            var row = new Dictionary<string, object?> { ["pair"] = $"{owner}>{other}" };
            foreach (var (name, table) in loaded)
            {
                var lut = table.Lookup(owner, other);
                row[name] = new Dictionary<string, object?>
                {
                    ["shapes"] = lut is null ? 0 : EdgeShapes.All.Count(s => lut[s.Direction] is { Length: > 0 }),
                    ["masks"] = lut is null ? 0 : Enumerable.Range(1, 255).Count(m => lut[m] is { Length: > 0 }),
                    ["ids"] = lut is null ? 0 : lut.Where(c => c is not null).SelectMany(c => c!).Distinct().Count(),
                    ["via"] = table.Via.GetValueOrDefault($"{owner}>{other}"),
                    ["plain"] = table.Plain.Contains($"{owner}>{other}") ? true : null,
                };
            }
            pairs.Add(row);
        }
        json.Event("done", new()
        {
            ["schema"] = "guo.mapgen.coverage/1",
            ["tables"] = loaded.Select(t => t.Name).ToList(),
            ["missing"] = tables.Where(t => !File.Exists(t.Path)).Select(t => t.Name).ToList(),
            ["pairs"] = pairs,
        });
        return 0;
    }
}
