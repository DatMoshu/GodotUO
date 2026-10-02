#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using GUO.Assets;

/// <summary>
/// Art &amp; tiledata (plan §4.6 panel 1): land and static art through
/// <see cref="ArtLoader.GetArt"/>, with the tiledata entry for each.
/// </summary>
[Tool]
public partial class ArtPanel : GridPanel
{
    private OptionButton _kind;
    private readonly List<int>[] _ids = new List<int>[2];

    public override string SmokeQuery => "0x0E75";

    private bool Land => _kind != null && _kind.Selected == 1;

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _kind = new OptionButton();
        _kind.AddItem("Statics");
        _kind.AddItem("Land");
        _kind.ItemSelected += _ => Refresh();
        bar.AddChild(_kind);
    }

    protected override void ForgetIds()
    {
        _ids[0] = null;
        _ids[1] = null;
    }

    /// <summary>Switches between Statics and Land (F3 opens an id in the right one).</summary>
    public void SelectKind(bool land)
    {
        EnsureUi();
        _kind.Selected = land ? 1 : 0;
    }

    private uint Index(int id) => Land ? (uint)id : EditorData.LandCount + (uint)id;

    protected override IEnumerable<int> Ids()
    {
        int k = Land ? 1 : 0;
        if (_ids[k] == null)
        {
            var list = new List<int>();
            int count = Land
                ? (int)EditorData.LandCount
                : Math.Min(Data.Files.Arts.File.Entries.Length - (int)EditorData.LandCount, 0x10000);
            for (int id = 0; id < count; id++)
            {
                if (Data.HasArt(Index(id)))
                {
                    list.Add(id);
                }
            }

            _ids[k] = list;
        }

        return _ids[k];
    }

    protected override string Caption(int id) => $"{id:X4}";

    protected override string Tooltip(int id) => $"0x{id:X4} {Data.NameOf(Index(id))}";

    protected override bool Matches(int id, string query) =>
        Data.NameOf(Index(id)).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Image Icon(int id) => Data.ArtImage(Index(id));

    protected override Inspection Describe(int id)
    {
        uint index = Index(id);
        Data.CurrentArt = index;
        Image img = Data.ArtImage(index);

        var sb = new StringBuilder();
        sb.Append($"[b]{(Land ? "Land" : "Static")} 0x{id:X4}[/b] ({id})\n");
        sb.Append(img != null ? $"size {img.GetWidth()} x {img.GetHeight()}\n" : "no art\n");

        if (Land)
        {
            LandTiles[] tiles = Data.Files.TileData.LandData;
            if (id < tiles.Length)
            {
                LandTiles t = tiles[id];
                sb.Append($"name   {t.Name}\n");
                sb.Append($"texmap 0x{t.TexID:X4}\n");
                sb.Append($"flags  {Flags((ulong)t.Flags)}\n");
            }
        }
        else
        {
            StaticTiles[] tiles = Data.Files.TileData.StaticData;
            if (id < tiles.Length)
            {
                StaticTiles t = tiles[id];
                sb.Append($"name   {t.Name}\n");
                sb.Append($"height {t.Height}   weight {t.Weight}   layer {t.Layer}\n");
                sb.Append($"anim   0x{t.AnimID:X4}   hue {t.Hue}   light {t.LightIndex}   count {t.Count}\n");
                sb.Append($"flags  {Flags((ulong)t.Flags)}\n");
            }
        }

        Inspection ins = Inspection.Still("Art", $"0x{id:X4}", img, sb.ToString());
        AssetActions.Add(ins, Data, Land ? AssetKind.Land : AssetKind.Static, id, img);
        return ins;
    }

    public static string Flags(ulong flags)
    {
        if (flags == 0)
        {
            return "none";
        }

        var names = new List<string>();
        foreach (TileFlag f in Enum.GetValues<TileFlag>())
        {
            ulong v = (ulong)f;
            if (v != 0 && (v & (v - 1)) == 0 && (flags & v) != 0)
            {
                names.Add(f.ToString());
            }
        }

        return string.Join(", ", names);
    }
}
#endif
