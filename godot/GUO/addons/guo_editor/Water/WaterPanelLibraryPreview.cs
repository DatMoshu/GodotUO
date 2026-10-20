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
    internal WaterPreview LibraryPreview => _libraryPreview;
    internal StoreWater LibraryPreviewStyle => _libraryPreviewStyle;

    private void BuildLibraryPreview()
    {
        _libraryVisual = new VBoxContainer { Visible = false };
        _library.AddChild(_libraryVisual);
        _libraryVisual.AddChild(new Label { Text = "Selection · fixed 1.50 s test",
            TooltipText = "Low beach, daylight, splash + hull. A separate synthetic preview; selecting a preset keeps your draft unchanged." });
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
        _libraryPreview.SetScene(1);
        _libraryPreview.SetBrightness(1);
        _libraryPreview.Replay(2, 1.5f);
        _libraryVisualInfo.Text = $"{style.Name}\nLow beach · daylight · splash + hull at 1.50 s. Synthetic water; your draft is unchanged.";
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
