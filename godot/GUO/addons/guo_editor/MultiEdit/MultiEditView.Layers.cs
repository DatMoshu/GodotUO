#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using Godot;

public partial class MultiEditView
{
    private ItemList _layersList;
    private LineEdit _layerName;
    private SpinBox _layerDx, _layerDy, _layerDz, _layerTile, _layerReplacement;
    private int _layerId;
    private Label _layerMessage;

    private void BuildLayersTab()
    {
        var box = new VBoxContainer { Name = "Layers" };
        _tabs.AddChild(box);
        box.AddChild(new Label { Text = "Layers organize edits; UO controls draw order." });
        _layersList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(280, 180) };
        box.AddChild(_layersList);
        _layersList.ItemSelected += i =>
        {
            _layerId = (int)_layersList.GetItemMetadata((int)i);
            _layerName.Text = _doc.Layers.First(l => l.Id == _layerId).Name;
        };
        _layerName = new LineEdit { PlaceholderText = "Layer name", MaxLength = 80 };
        box.AddChild(_layerName);
        var names = new HBoxContainer(); box.AddChild(names);
        names.AddChild(Btn("New", () => LayerAction("create")));
        names.AddChild(Btn("Rename", () => LayerAction("rename")));
        var actions = new HBoxContainer(); box.AddChild(actions);
        actions.AddChild(Btn("Draw here", () => LayerAction("activate")));
        actions.AddChild(Btn("Select", () => LayerAction("select")));
        actions.AddChild(Btn("Assign selection", () => LayerAction("assign")));
        var toggles = new HBoxContainer(); box.AddChild(toggles);
        toggles.AddChild(Btn("Show / hide", () => LayerAction("visible")));
        toggles.AddChild(Btn("Lock / unlock", () => LayerAction("lock")));
        box.AddChild(new Label { Text = "Move layer (X / Y / Z)" });
        var move = new HBoxContainer(); box.AddChild(move);
        _layerDx = LayerNumber(move, -1000, 1000);
        _layerDy = LayerNumber(move, -1000, 1000);
        _layerDz = LayerNumber(move, -128, 127);
        move.AddChild(Btn("Move", () => LayerAction("move")));
        box.AddChild(new Label { Text = "Replace tile ID → tile ID in this layer" });
        var material = new HBoxContainer(); box.AddChild(material);
        _layerTile = LayerNumber(material, 0, 65535);
        _layerReplacement = LayerNumber(material, 0, 65535);
        material.AddChild(Btn("Replace", () => LayerAction("replace")));
        _layerMessage = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 45) };
        box.AddChild(_layerMessage);
        RefreshLayers();
    }

    private static SpinBox LayerNumber(Control parent, int min, int max)
    {
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new Vector2(70, 0) };
        parent.AddChild(spin); return spin;
    }

    private void LayerAction(string action)
    {
        try
        {
            var layer = _doc.Layers.FirstOrDefault(l => l.Id == _layerId);
            _doc.EditLayer(_doc.Revision, action, _layerId, _layerName.Text,
                (int)_layerDx.Value, (int)_layerDy.Value, (int)_layerDz.Value,
                (int)_layerTile.Value, (int)_layerReplacement.Value,
                action == "visible" ? !(layer?.Visible ?? true) : !(layer?.Locked ?? false));
            if (action == "create") _layerId = _doc.ActiveLayerId;
            _layerMessage.Text = $"{action}: applied";
            RefreshLayers();
            _canvas.Touch();
        }
        catch (Exception ex) { _layerMessage.Text = ex.Message; }
    }

    private void RefreshLayers()
    {
        if (_layersList == null) return;
        if (!_doc.Layers.Any(l => l.Id == _layerId)) _layerId = _doc.Layers[0].Id;
        _layersList.Clear();
        foreach (var layer in _doc.Layers)
        {
            int i = _layersList.AddItem($"{(layer.Id == _doc.ActiveLayerId ? "● " : "")}{layer.Name}  ({_doc.Parts.Count(p => p.LayerId == layer.Id)}){(!layer.Visible ? "  hidden" : "")}{(layer.Locked ? "  locked" : "")}");
            _layersList.SetItemMetadata(i, layer.Id);
            if (layer.Id == _layerId) _layersList.Select(i);
        }
    }
}
#endif
