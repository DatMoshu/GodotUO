#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using GUO.Assets;

/// <summary>
/// Shows one piece of art as the loaders decode it, with its tiledata. The
/// "Inspector" of docs/editor_plan.md §3; in later phases it inspects map
/// cells, statics and spawners too.
/// </summary>
[Tool]
public partial class ArtInspectorDock : EditorDock
{
    private readonly EditorData _data;

    private TextureRect _preview;
    private OptionButton _zoom;
    private RichTextLabel _fields;
    private Image _image;

    /// <summary>The art index on show, or null.</summary>
    public uint? Current { get; private set; }

    /// <summary>The texture on show, as decoded; null when the id is empty.</summary>
    public Texture2D Texture => _preview?.Texture;

    public ArtInspectorDock() : this(null)
    {
    }

    public ArtInspectorDock(EditorData data)
    {
        _data = data;
        Name = "UOInspector";
        Title = "UO Inspector";
        LayoutKey = "guo_inspector";
        DefaultSlot = DockSlot.RightBl;
        AvailableLayouts = DockLayout.Vertical | DockLayout.Floating;
        IconName = "Search";
    }

    public override void _Ready()
    {
        if (_preview != null)
        {
            return;
        }

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var row = new HBoxContainer();
        root.AddChild(row);
        row.AddChild(new Label { Text = "Zoom" });
        _zoom = new OptionButton();
        foreach (int z in new[] { 1, 2, 3, 4 })
        {
            _zoom.AddItem($"{z}x");
        }

        _zoom.Selected = 1;
        _zoom.ItemSelected += _ => ApplyZoom();
        row.AddChild(_zoom);

        // A checker behind the art so transparent pixels read as transparent.
        var backdrop = new PanelContainer { CustomMinimumSize = new Vector2(0, 200) };
        root.AddChild(backdrop);

        _preview = new TextureRect
        {
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // Never filter pixel art (CLAUDE.md rule 7).
            TextureFilter = TextureFilterEnum.Nearest,
        };
        backdrop.AddChild(_preview);

        _fields = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Text = "Pick art in the UO Assets dock.",
        };
        root.AddChild(_fields);
    }

    /// <summary>Shows the art at this index (land 0..0x3FFF, statics after).</summary>
    public void ShowArt(uint index)
    {
        if (_data == null || !_data.IsLoaded)
        {
            return;
        }

        Current = index;
        _image = _data.ArtImage(index);
        _preview.Texture = _image != null ? ImageTexture.CreateFromImage(_image) : null;
        ApplyZoom();
        _fields.Text = Describe(index, _image);
    }

    /// <summary>The decoded image on show, for the smoke check.</summary>
    public Image CurrentImage => _image;

    private void ApplyZoom()
    {
        if (_image == null || _preview == null)
        {
            return;
        }

        // KeepCentered draws at native size; the zoomed copy is scaled with
        // nearest-neighbour so it stays pixel art.
        int z = _zoom.Selected + 1;
        if (z == 1)
        {
            _preview.Texture = ImageTexture.CreateFromImage(_image);
            return;
        }

        var scaled = (Image)_image.Duplicate();
        scaled.Resize(_image.GetWidth() * z, _image.GetHeight() * z, Image.Interpolation.Nearest);
        _preview.Texture = ImageTexture.CreateFromImage(scaled);
    }

    private string Describe(uint index, Image img)
    {
        var sb = new StringBuilder();
        bool land = index < EditorData.LandCount;
        uint id = land ? index : index - EditorData.LandCount;

        sb.Append($"[b]{(land ? "Land" : "Static")} 0x{id:X4}[/b] ({id})\n");
        sb.Append(img != null ? $"size {img.GetWidth()} x {img.GetHeight()}\n" : "no art\n");

        if (land)
        {
            LandTiles[] tiles = _data.Files.TileData.LandData;
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
            StaticTiles[] tiles = _data.Files.TileData.StaticData;
            if (id < tiles.Length)
            {
                StaticTiles t = tiles[id];
                sb.Append($"name   {t.Name}\n");
                sb.Append($"height {t.Height}   weight {t.Weight}   layer {t.Layer}\n");
                sb.Append($"anim   0x{t.AnimID:X4}   hue {t.Hue}   light {t.LightIndex}   count {t.Count}\n");
                sb.Append($"flags  {Flags((ulong)t.Flags)}\n");
            }
        }

        return sb.ToString();
    }

    private static string Flags(ulong flags)
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
