#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Godot;

public partial class WorldView
{
    private OptionButton _terrainPalette;
    private List<WorldCoastPair> _coastPairs;

    private void PickTerrainPalette()
    {
        var picker = new EditorFileDialog
        {
            Title = "Open terrain palette or coast brushes",
            FileMode = EditorFileDialog.FileModeEnum.OpenFile,
            Access = EditorFileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.xml,*.json ; Terrain palettes and coast brushes", "*.xml ; Landscaper terrain palette", "*.json ; CentrED# land brushes" }
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
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (stream.Length > 2_000_000) throw new InvalidDataException("Brush file exceeds 2 MB.");
                using var text = new StreamReader(stream);
                var pairs = WorldCoastPair.Read(text.ReadToEnd());
                _terrainPalette.Clear();
                _terrainPalette.AddItem("Choose a coast brush pair…");
                foreach (var pair in pairs) _terrainPalette.AddItem(pair.Label);
                _coastPairs = pairs;
                _terrainPalette.Select(0);
                _terrainPalette.Visible = true;
                _status.Text = $"Loaded {pairs.Count} directional pairs. Choose the shore to paint.";
                return true;
            }
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { MaxCharactersInDocument = 2_000_000 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name == "TilesBrush")
                throw new InvalidDataException("Import TilesBrush.xml in CentrED# and open its land_brushes JSON value here. Its XML converter drops base Chance and recognizes six edge types.");
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
            _coastPairs = null;
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
        catch (JsonException)
        {
            _status.Text = "Cannot open coast brushes. Choose the land_brushes JSON value with Tiles, To, decimal TileID and Direction (0–255). Check for unsupported fields such as Chance.";
            return false;
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
        if (_coastPairs != null)
        {
            var pair = _coastPairs[(int)index - 1];
            if (pair.Check(_data) is string error)
            {
                _terrainPalette.Select(0);
                _status.Text = error;
                return;
            }
            _brushArt.SelectKind(true);
            UseFavorite(pair.BaseTile);
            _recipe.Coast = pair;
            _operation.Select(_operation.ItemCount - 1);
            _recipe.Operation = WorldCoastPair.Operation;
            _status.Text = $"Brush over the {pair.Label} boundary to paint its edges.";
            return;
        }
        uint tile = (uint)_terrainPalette.GetItemMetadata((int)index).AsInt32();
        if (_data?.IsLoaded != true || !_data.HasArt(tile))
        {
            _terrainPalette.Select(0);
            _status.Text = $"Land art 0x{tile:X4} is unavailable in the loaded client.";
            return;
        }
        _operation.Select(0);
        _recipe.Operation = "Paint";
        _brushArt.SelectKind(true);
        // A palette entry supplies one base tile. Keep the stroke settings the painter chose.
        _weights.Text = _recipe.Variants = "";
        RebuildVariants();
        _edges.Text = _recipe.Edges = "";
        UseFavorite(tile);
        _status.Text = $"Painting {_terrainPalette.GetItemText((int)index)}";
    }
}
#endif
