// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;

internal partial class WaterPanel
{
    private Label _previewExportStatus;
    private void BuildPreviewExport(Node body)
    {
        var row = new HFlowContainer(); body.AddChild(row);
        var button = AddButton(row, "Save preview PNG…", PreviewExportDialog);
        button.Disabled = DisplayServer.GetName() == "headless";
        button.TooltipText = "Save the most recently drawn synthetic preview at its original resolution, including Compare or Classic. Freeze first to hold a pose. Excludes World artwork, controls and preset metadata. Requires a renderer.";
        _previewExportStatus = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        body.AddChild(_previewExportStatus);
    }

    private void PreviewExportDialog()
    {
        var dialog = Dialog("Save synthetic water preview", FileDialog.FileModeEnum.SaveFile, "*.png ; Synthetic water preview");
        dialog.CurrentFile = "water-preview.png";
        dialog.FileSelected += path => { SavePreviewPath(path); dialog.QueueFree(); };
        dialog.PopupCenteredRatio(.65f);
    }

    internal bool SavePreviewPath(string path)
    {
        try
        {
            WaterAuthoring.CheckPreviewDestination(path);
            if (DisplayServer.GetName() == "headless")
                throw new InvalidDataException("PNG export needs a rendered synthetic preview. Open the editor with a renderer and try again.");
            using var image = _preview.Capture();
            WaterAuthoring.SavePreview(image, path);
            _previewExportStatus.Text = $"Saved {Path.GetFileName(path)} ({image.GetWidth()} × {image.GetHeight()})";
            _previewExportStatus.TooltipText = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _previewExportStatus.Text = "Could not save preview: " + ex.Message;
            _previewExportStatus.TooltipText = "";
            return false;
        }
        finally { _previewExportStatus.Visible = true; }
    }
    internal string PreviewExportStatus => _previewExportStatus.Text;
}
#endif
