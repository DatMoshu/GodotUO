#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using Godot;
using GUO.UI.Authoring;

public partial class GumpStudio
{
    internal bool InspectorFits => _inspectorScroll != null
        && _inspectorScroll.GetGlobalRect().End.X <= (GetParent() is Control parent ? parent.GetGlobalRect().End.X : GetViewportRect().End.X) + 2
        && _properties.Size.X <= _inspectorScroll.Size.X + 2;

    internal Action ShowVisualProof(GumpDocument doc)
    {
        var previous = _document;
        string saved = _saved, path = _path, status = _status.Text;
        var selection = _canvas.Selection.ToArray();
        _document = doc;
        _saved = doc.ToJson();
        _canvas.Selection.Clear();
        if (doc.Elements.Count > 1) _canvas.Selection.Add(doc.Elements[1].Id);
        Refresh();
        return () =>
        {
            _document = previous;
            _saved = saved; _path = path; _status.Text = status;
            _canvas.Selection.Clear(); foreach (string id in selection) _canvas.Selection.Add(id);
            Refresh();
        };
    }
    private void FitInspector()
    {
        if (_rightSplit == null || _rightSplit.Size.X <= 0) return;
        // Reserve the inspector at the actual available width, including editor DPI scaling.
        // A fixed canvas split offset can put the entire inspector outside the main-screen tab.
        _rightSplit.SplitOffsets = new[] { Math.Max(80, (int)(_rightSplit.Size.X - _inspectorScroll.GetCombinedMinimumSize().X - 12 * UiScale)) };
    }

    private void BuildAssetShelf(VSplitContainer work)
    {
        var shelf = new VBoxContainer { CustomMinimumSize = new Vector2(0, 175 * UiScale) };
        work.AddChild(shelf);
        var actions = new HBoxContainer(); shelf.AddChild(actions);
        var selection = new Label { Text = "UO Assets · Gumps", ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill }; actions.AddChild(selection);
        int? picked = null;
        var add = new Button { Text = "Add to layout", Disabled = true }; actions.AddChild(add);
        var use = new Button { Text = "Use for selected element", Disabled = true }; actions.AddChild(use);
        add.Pressed += () => { if (picked is int id) Guard(() => AddArt(id)); };
        use.Pressed += () => { if (picked is int id) Guard(() => AssignArt(id)); };
        _gumpAssets = new AssetsView(_data) { GumpsOnly = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        shelf.AddChild(_gumpAssets);
        _gumpAssets.CellSizeIndex = 0;
        _gumpAssets.Inspect += inspection =>
        {
            picked = _gumpAssets.Panel<GumpPanel>()?.Selected;
            add.Disabled = use.Disabled = picked == null;
            selection.Text = picked is int id ? $"Gump 0x{id:X4} · double-click to add" : "UO Assets · Gumps";
        };
        if (_gumpAssets.Panel<GumpPanel>() is GumpPanel panel)
            panel.Activated += id => Guard(() => AddArt(id));
    }

    private void AssignArt(int id)
    {
        var e = Selected().SingleOrDefault();
        if (e == null || e.Kind is not (GumpElementKind.Image or GumpElementKind.TiledImage or GumpElementKind.Panel or GumpElementKind.Button or GumpElementKind.CheckBox or GumpElementKind.Radio))
            throw new InvalidOperationException("Select one image, panel, button, checkbox or radio element first.");
        Mutate(() =>
        {
            e.Graphic = id;
            if (e.Kind == GumpElementKind.Image && Texture(id) is Texture2D texture)
            {
                e.Width = texture.GetWidth(); e.Height = texture.GetHeight();
            }
        });
        SetStatus($"Assigned 0x{id:X4} to {e.Name}");
    }

    private void PickGumpArt(Action<int> picked)
    {
        var dialog = new AcceptDialog { Title = "Choose gump art", Size = new Vector2I(820, 540), OkButtonText = "Use selected", DialogHideOnOk = false };
        AddChild(dialog);
        var panel = new GumpPanel { MinimumGridHeight = 260 };
        panel.Attach(_data); dialog.AddChild(panel);
        void Choose(int id) { dialog.Hide(); Guard(() => picked(id)); dialog.QueueFree(); }
        panel.Activated += Choose;
        dialog.Confirmed += () => { if (panel.Selected is int id) Choose(id); };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
        if (_data?.IsLoaded == true) panel.OnDataLoaded();
        else if (_data != null)
        {
            _data.Loaded += panel.OnDataLoaded;
            dialog.TreeExiting += () => _data.Loaded -= panel.OnDataLoaded;
        }
    }
}
#endif
