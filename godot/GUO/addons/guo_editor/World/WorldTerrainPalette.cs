#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using Godot;

public partial class WorldView
{
    private OptionButton _terrainPalette;

    private void PickTerrainPalette()
    {
        var picker = new EditorFileDialog
        {
            Title = "Open Landscaper terrain palette",
            FileMode = EditorFileDialog.FileModeEnum.OpenFile,
            Access = EditorFileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.xml ; Landscaper terrain palette" }
        };
        AddChild(picker);
        picker.FileSelected += path => { LoadTerrainPalette(path); picker.QueueFree(); };
        picker.Canceled += picker.QueueFree;
        picker.PopupCenteredRatio();
    }

    internal bool LoadTerrainPalette(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { MaxCharactersInDocument = 2_000_000 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "Terrains")
                throw new InvalidDataException("Choose Data/System/Terrain.xml with a Terrains root.");
            var entries = new List<(string Name, ushort Tile)>();
            foreach (var row in root.Elements())
            {
                string name = ((string)row.Attribute("Name"))?.Trim();
                if (row.Name != "Terrain" || string.IsNullOrEmpty(name) ||
                    !ushort.TryParse((string)row.Attribute("TileID"), NumberStyles.None, CultureInfo.InvariantCulture, out ushort tile) ||
                    tile >= EditorData.LandCount)
                    throw new InvalidDataException($"Terrain row {entries.Count + 1} needs a name and a decimal land TileID.");
                entries.Add((name, tile));
            }
            if (entries.Count == 0) throw new InvalidDataException("The terrain palette is empty.");
            _terrainPalette.Clear();
            _terrainPalette.AddItem("Choose a terrain…");
            foreach (var entry in entries)
            {
                _terrainPalette.AddItem($"{entry.Name}  0x{entry.Tile:X4}");
                _terrainPalette.SetItemMetadata(_terrainPalette.ItemCount - 1, (int)entry.Tile);
            }
            _terrainPalette.Select(0);
            _terrainPalette.Visible = true;
            _status.Text = $"Loaded {entries.Count} terrain tiles. Choose one to paint.";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or XmlException)
        {
            _status.Text = $"Cannot open terrain palette. {ex.Message}";
            return false;
        }
    }

    internal void SelectPaletteTerrain(long index)
    {
        if (index <= 0 || index >= _terrainPalette.ItemCount) return;
        uint tile = (uint)_terrainPalette.GetItemMetadata((int)index).AsInt32();
        if (_data?.IsLoaded != true || !_data.HasArt(tile))
        {
            _terrainPalette.Select(0);
            _status.Text = $"Land art 0x{tile:X4} is unavailable in the loaded client.";
            return;
        }
        QuickRecipe(true, "Paint", 5, 100, 1);
        _edges.Text = _recipe.Edges = "";
        UseFavorite(tile);
        _status.Text = $"Painting {_terrainPalette.GetItemText((int)index)}";
    }
}
#endif
