// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using Godot;
using GUO.Store;

internal partial class WaterPanel
{
    private VBoxContainer _libraryVisual;
    private Label _libraryVisualInfo;
    private WaterPreview _libraryPreview;
    private StoreWater _libraryPreviewStyle;
    internal OptionButton LibraryScene { get; private set; }
    internal OptionButton LibraryLight { get; private set; }
    internal WaterPreview LibraryPreview => _libraryPreview;
    internal StoreWater LibraryPreviewStyle => _libraryPreviewStyle;

    private void BuildLibraryPreview()
    {
        _libraryVisual = new VBoxContainer { Visible = false };
        _library.AddChild(_libraryVisual);
        var context = new HBoxContainer { TooltipText = "Selected preset: splash + hull paused at 1.50 seconds. Your draft stays unchanged." };
        _libraryVisual.AddChild(context);
        LibraryScene = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Selected preset scene; splash + hull paused at 1.50 s. Kept while browsing; does not change the working preview or World." };
        foreach (string scene in new[] { "Open water", "Low beach", "High cliff" }) LibraryScene.AddItem(scene);
        LibraryScene.Select(1);
        context.AddChild(LibraryScene);
        LibraryLight = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Selected preset lighting; splash + hull paused at 1.50 s. Kept while browsing; does not change the working preview or World." };
        foreach (string light in new[] { "Daylight", "Dusk", "Night" }) LibraryLight.AddItem(light);
        context.AddChild(LibraryLight);
        // Use the same guarded disk read as a new selection, including error recovery.
        LibraryScene.ItemSelected += _ => LibrarySelected();
        LibraryLight.ItemSelected += _ => LibrarySelected();
        _libraryVisualInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _libraryVisual.AddChild(_libraryVisualInfo);
    }

    private StoreWater PreviewLibraryEntry(LibraryEntry entry)
    {
        if (!_library.IsVisibleInTree()) { ClearLibraryPreview(); return entry.Style; }
        // Selection is an audition, never a document load or a change to the working preview.
        // Read saved files again so the picture and actions cannot silently use a stale row.
        var style = entry.Path == null ? entry.Style : WaterAuthoring.Read(entry.Path);
        _libraryPreviewStyle = style with { Tint = (float[])style.Tint.Clone() };
        if (_libraryPreview == null)
        {
            _libraryPreview = new WaterPreview { CustomMinimumSize = new Vector2(240, 120),
                MouseFilter = MouseFilterEnum.Ignore };
            _libraryVisual.AddChild(_libraryPreview);
            // WaterPreview sets its default minimum during _Ready; the browser uses a compact pose.
            _libraryPreview.CustomMinimumSize = new Vector2(240, 120);
            _libraryVisual.MoveChild(_libraryPreview, 1);
        }
        _libraryPreview.SetStyle(_libraryPreviewStyle);
        _libraryPreview.SetScene(LibraryScene.Selected);
        _libraryPreview.SetBrightness(LibraryLight.Selected == 0 ? 1 : LibraryLight.Selected == 1 ? .55f : .2f);
        _libraryPreview.Replay(2, 1.5f);
        _libraryVisualInfo.Text = $"{style.Name}\n{LibraryScene.GetItemText(LibraryScene.Selected)} · {LibraryLight.GetItemText(LibraryLight.Selected)} · splash + hull at 1.50 s. Synthetic water; your draft is unchanged.";
        _libraryVisual.Show();
        return style;
    }

    internal void CloseLibrary()
    {
        _library.Hide();
        ClearLibraryPreview();
    }

    private void ClearLibraryPreview()
    {
        _libraryPreviewStyle = null;
        var preview = _libraryPreview; _libraryPreview = null;
        if (_libraryVisual != null) _libraryVisual.Hide();
        if (GodotObject.IsInstanceValid(preview)) { preview.Shutdown(); preview.QueueFree(); }
    }
}
#endif
