#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using GUO.IO;

/// <summary>
/// Gumps (plan §4.6 panel 2): every gump through
/// <see cref="GUO.Assets.GumpsLoader.GetGump"/>, and which ported UI classes
/// name the id in their source.
/// </summary>
/// <remarks>
/// "Used by" is a text search of <c>src/Game/UI</c> for the id as a hex or
/// decimal literal, done once when first asked. It finds what the code says,
/// not what a server sends: most gumps a shard shows come by packet.
/// </remarks>
[Tool]
public partial class GumpPanel : GridPanel
{
    private List<int> _ids;
    private Dictionary<int, SortedSet<string>> _usedBy;

    public override string SmokeQuery => "0x0064";

    protected override int IconSize => 64;

    // Gumps run to 800x600; a page of 240 took 2.7 s to decode.
    protected override int PageSize => 60;

    protected override IEnumerable<int> Ids()
    {
        if (_ids == null)
        {
            _ids = new List<int>();
            UOFileIndex[] entries = Data.Files.Gumps.File.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                ref UOFileIndex e = ref Data.Files.Gumps.File.GetValidRefEntry(i);
                if (e.Length > 0)
                {
                    _ids.Add(i);
                }
            }
        }

        return _ids;
    }

    protected override string Caption(int id) => $"{id:X4}";

    protected override bool Matches(int id, string query) => false;

    protected override string Placeholder => "gump id (0x0064, 100)";

    protected override Image Icon(int id) => Data.GumpImage(id);

    protected override Inspection Describe(int id)
    {
        Image img = Data.GumpImage(id);
        var sb = new StringBuilder();
        sb.Append($"[b]Gump 0x{id:X4}[/b] ({id})\n");
        sb.Append(img != null ? $"size {img.GetWidth()} x {img.GetHeight()}\n" : "no image\n");

        UsedBy().TryGetValue(id, out SortedSet<string> users);
        sb.Append(users is { Count: > 0 }
            ? $"named in src/Game/UI by: {string.Join(", ", users)}\n"
            : "not named in src/Game/UI (may still come from the server)\n");

        return Inspection.Still("Gumps", $"0x{id:X4}", img, sb.ToString());
    }

    private static readonly Regex Literal = new(@"\b0x([0-9A-Fa-f]{2,4})\b|\b(\d{2,5})\b");

    private Dictionary<int, SortedSet<string>> UsedBy()
    {
        if (_usedBy != null)
        {
            return _usedBy;
        }

        _usedBy = new Dictionary<int, SortedSet<string>>();
        string ui = ProjectSettings.GlobalizePath("res://src/Game/UI");
        if (!Directory.Exists(ui))
        {
            return _usedBy;
        }

        foreach (string file in Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            foreach (string line in File.ReadLines(file))
            {
                // Only lines that look like they hand an id to a gump control.
                if (!line.Contains("Gump", StringComparison.Ordinal) && !line.Contains("Button", StringComparison.Ordinal)
                    && !line.Contains("Pic", StringComparison.Ordinal) && !line.Contains("Tiled", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match m in Literal.Matches(line))
                {
                    int id = m.Groups[1].Success
                        ? Convert.ToInt32(m.Groups[1].Value, 16)
                        : int.Parse(m.Groups[2].Value);
                    if (!_usedBy.TryGetValue(id, out SortedSet<string> set))
                    {
                        _usedBy[id] = set = new SortedSet<string>();
                    }

                    set.Add(name);
                }
            }
        }

        return _usedBy;
    }
}
#endif
