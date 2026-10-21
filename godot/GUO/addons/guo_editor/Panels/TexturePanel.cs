#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Sloped-land textures, decoded by the game's loader with effective TexTerr.def remaps.</summary>
[Tool]
public partial class TexturePanel : GridPanel
{
    private List<int> _ids;
    private Dictionary<int, List<(int Id, string Name)>> _land;
    private OptionButton _size;
    public override string SmokeQuery => "0x0003";
    protected override string Placeholder => "Texture ID (0x0001, 1) or referring land name — Enter";
    protected override int IconSize => 64;
    protected override int PageSize => 120;

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _size = new OptionButton { AccessibilityName = "Texture dimensions", TooltipText = "Filter native texture dimensions" };
        foreach (string name in new[] { "All sizes", "64 × 64", "128 × 128" }) _size.AddItem(name);
        _size.ItemSelected += _ => Refresh(); bar.AddChild(_size);
    }
    internal void FilterSize(int size)
    {
        EnsureUi(); _size.Select(size switch { 64 => 1, 128 => 2, _ => 0 }); Refresh();
    }
    protected override IEnumerable<int> Ids()
    {
        if (_ids == null)
            _ids = Enumerable.Range(0, Math.Min(Data.Files.Texmaps.File.Entries.Length,
                GUO.Assets.TexmapsLoader.MAX_LAND_TEXTURES_DATA_INDEX_COUNT)).Where(id => Data.TextureSize(id) > 0).ToList();
        return _ids;
    }
    protected override void ForgetIds() { _ids = null; _land = null; }
    protected override bool Include(int id) => _size.Selected == 0 || Data.TextureSize(id) == (_size.Selected == 1 ? 64 : 128);
    protected override string Caption(int id) => $"{id:X4}";
    private List<(int Id, string Name)> Land(int id)
    {
        if (_land == null)
        {
            _land = new(); var tiles = Data.Files.TileData.LandData;
            for (int i = 0; i < tiles.Length; i++)
            {
                int texture = tiles[i].TexID;
                if (!_land.TryGetValue(texture, out var uses)) _land[texture] = uses = new();
                uses.Add((i, tiles[i].Name ?? ""));
            }
        }
        return _land.TryGetValue(id, out var names) ? names : new();
    }
    internal (int Id, string Name)[] LandReferences(int id) => Land(id).ToArray();
    protected override bool Matches(int id, string query) => Land(id).Any(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    protected override string Tooltip(int id) => $"Texture 0x{id:X4} · {Data.TextureSize(id)} × {Data.TextureSize(id)}\n"
        + string.Join(", ", Land(id).Select(t => t.Name).Where(n => n.Length > 0).Distinct().Take(8));
    protected override Image Icon(int id) => Data.TextureImage(id);
    protected override Inspection Describe(int id)
    {
        Image image = Data.TextureImage(id); var uses = Land(id);
        var inspection = Inspection.Still("Textures", $"0x{id:X4}", image,
            $"[b]Texture 0x{id:X4}[/b] ({id})\nsize {Data.TextureSize(id)} × {Data.TextureSize(id)}\n"
            + $"{uses.Count} land tile reference(s)\n" + string.Join("\n", uses.Take(16).Select(t => $"0x{t.Id:X4} · {t.Name}"))
            + (uses.Count > 16 ? $"\n… {uses.Count - 16} more" : ""));
        inspection.Title = "Texture"; inspection.Origin = "Active texture library · read-only";
        inspection.Fields.Add(("Dimensions", $"{Data.TextureSize(id)} × {Data.TextureSize(id)}"));
        inspection.Fields.Add(("Land references", uses.Count.ToString()));
        inspection.Fields.Add(("Land tiles", uses.Count == 0 ? "None" : string.Join("\n", uses.Take(16).Select(t => $"0x{t.Id:X4} · {t.Name}"))
            + (uses.Count > 16 ? $"\n… {uses.Count - 16} more" : "")));
        inspection.Fields.Add(("Source", "texmaps.mul / texidx.mul"));
        if (image != null) inspection.Actions.Add(new("export-texture-png", "Export texture PNG…", "Save", () =>
            AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile, $"texture_0x{id:X4}.png", path => AssetActions.ExportPng(image, path),
                owner: this, title: "Export texture PNG")));
        return inspection;
    }
}
#endif
