#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using System;
using Godot;

public partial class GuoEditorPlugin
{
    private readonly Dictionary<string, (Control View, HSplitContainer Shell, VBoxContainer Inspector)> _uoShells = new();
    private bool _uoLayoutActive, _previousDistraction;
    private bool _layoutTearingDown;

    private void BuildUoLayout()
    {
        _layoutTearingDown = false;
        WrapUoView(AssetsView.TabName, _assets);
        WrapUoView(StoreView.TabName, _store);
        WrapUoView(MapGenView.TabName, _mapgen);
        WrapUoView(MultiEditView.TabName, _multiedit);
        WrapUoView("Gumps", _gumps);
        MainScreenChanged += ChangeUoLayout;
        _world.VisibilityChanged += () => { if (_world.Visible) ChangeUoLayout(WorldTabName); };
        if (_world.Visible) ChangeUoLayout(WorldTabName);
        else foreach (var entry in _uoShells) if (entry.Value.View.Visible) { ChangeUoLayout(entry.Key); break; }
    }

    private void WrapUoView(string name, Control view)
    {
        var shell = new HSplitContainer { Name = name + "Workspace", Visible = view.Visible, SplitOffsets = new[] { 440 },
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        shell.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(shell);
        var inspector = new VBoxContainer { CustomMinimumSize = new Vector2(270, 0) };
        bool assets = name == AssetsView.TabName;
        if (!assets) { inspector.AddChild(new Label { Text = "UO Inspector" }); shell.AddChild(inspector); }
        view.Reparent(shell, false);
        if (assets)
        {
            inspector.CustomMinimumSize = new Vector2(300, 0) * EditorInterface.Singleton.GetEditorScale();
            shell.AddChild(inspector);
            var preferences = EditorInterface.Singleton.GetEditorSettings();
            float scale = EditorInterface.Singleton.GetEditorScale();
            int width = preferences.GetProjectMetadata("guo_assets", "inspector_width", 330).AsInt32();
            void Fit() => shell.SplitOffsets = new[] { Math.Max(120, (int)(shell.Size.X - Math.Max(300, width) * scale)) };
            shell.Resized += Fit;
            shell.Dragged += _ => { width = Math.Max(300, (int)(inspector.Size.X / scale)); preferences.SetProjectMetadata("guo_assets", "inspector_width", width); };
            Callable.From(Fit).CallDeferred();
        }
        view.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        view.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _uoShells[name] = (view, shell, inspector);
        view.VisibilityChanged += () =>
        {
            if (_layoutTearingDown) return;
            if (shell.Visible != view.Visible) shell.Visible = view.Visible;
            if (view.Visible) ChangeUoLayout(name);
        };
    }

    private void ChangeUoLayout(string name)
    {
        if (_layoutTearingDown) return;
        bool uo = name == WorldTabName || _uoShells.ContainsKey(name);
        if (uo)
        {
            if (name == AssetsView.TabName) HideBottomPanel();
            if (!_uoLayoutActive) _previousDistraction = EditorInterface.Singleton.IsDistractionFreeModeEnabled();
            _uoLayoutActive = true;
            EditorInterface.Singleton.SetDistractionFreeMode(true);
            _inspector.MountContent(name == WorldTabName ? _world.InspectorHost : _uoShells[name].Inspector);
        }
        else if (_uoLayoutActive)
        {
            _inspector.MountContent(_inspector);
            EditorInterface.Singleton.SetDistractionFreeMode(_previousDistraction);
            _uoLayoutActive = false;
        }
    }

    private void RemoveUoLayout()
    {
        _layoutTearingDown = true;
        MainScreenChanged -= ChangeUoLayout;
        _inspector?.MountContent(_inspector);
        foreach (var entry in _uoShells.Values)
        {
            entry.View.Reparent(EditorInterface.Singleton.GetEditorMainScreen(), false);
            entry.Shell.GetParent()?.RemoveChild(entry.Shell);
            entry.Shell.QueueFree();
        }
        _uoShells.Clear();
        if (_uoLayoutActive) EditorInterface.Singleton.SetDistractionFreeMode(_previousDistraction);
        _uoLayoutActive = false;
    }
}
#endif
