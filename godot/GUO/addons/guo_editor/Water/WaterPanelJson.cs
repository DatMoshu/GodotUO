// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Text;
using Godot;

internal partial class WaterPanel
{
    private ConfirmationDialog _jsonDialog;
    private TextEdit _jsonText;
    private RichTextLabel _jsonError;
    internal ConfirmationDialog JsonDialog => _jsonDialog;
    internal TextEdit JsonText => _jsonText;
    internal string JsonError => _jsonError?.Text;

    internal void OpenJson() => Try(() =>
    {
        CommitName(); CloseJson();
        var dialog = new ConfirmationDialog
        {
            Title = "Share water JSON", OkButtonText = "Load as new draft", CancelButtonText = "Close",
            DialogHideOnOk = false, Size = new Vector2I(832, 600),
            Unfocusable = GetWindow().Unfocusable || DisplayServer.GetName() == "headless"
                || DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.NoFocus, GetWindow().GetWindowId()),
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest
        };
        _jsonDialog = dialog; AddChild(dialog);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) }; dialog.AddChild(box);
        // Fixed-height text areas prevent wrapped labels from inflating the dialog's minimum height
        // before its first container layout. Long validation messages remain scrollable.
        box.AddChild(new RichTextLabel
        {
            Text = "Copy this text to share a preset, or replace it with water JSON. Loading creates a new draft; Undo restores your working draft.",
            CustomMinimumSize = new Vector2(640, 60)
        });
        _jsonText = new TextEdit
        {
            CustomMinimumSize = new Vector2(640, 300), SizeFlagsVertical = SizeFlags.ExpandFill,
            Text = Encoding.UTF8.GetString(WaterAuthoring.Serialize(_style)),
            TooltipText = "Use the text editor's Copy and Paste actions. Accepts validated guo/water@1 JSON, up to 16 KB of UTF-8 text."
        };
        box.AddChild(_jsonText);
        _jsonError = new RichTextLabel { Visible = false, CustomMinimumSize = new Vector2(640, 80) };
        box.AddChild(_jsonError);
        dialog.AddButton("Use as reference", false, "reference");
        dialog.Confirmed += () => { if (ReferenceEquals(_jsonDialog, dialog)) ApplyJson(false); };
        dialog.CustomAction += action => { if (ReferenceEquals(_jsonDialog, dialog) && action == "reference") ApplyJson(true); };
        dialog.Canceled += () => { if (ReferenceEquals(_jsonDialog, dialog)) CloseJson(); };
        if (DisplayServer.GetName() != "headless" && IsVisibleInTree()) dialog.PopupCentered();
    });

    internal bool ApplyJson(bool reference)
    {
        if (_jsonDialog == null) return false;
        try
        {
            var style = WaterAuthoring.ParseText(_jsonText.Text);
            if (reference)
            {
                SetReference(style); _compare.ButtonPressed = true; UpdateReference();
                _status.Text = "JSON preset kept as reference. Your working draft is unchanged.";
            }
            else
            {
                LoadStyle(style);
                _status.Text = "JSON loaded as a new draft. Undo restores the previous draft; Save draft creates a working copy.";
            }
            CloseJson(); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            _jsonError.Text = "Could not load this JSON: " + ex.Message + " Correct the text or Close without loading.";
            _jsonError.Visible = true; return false;
        }
    }

    internal void CloseJson()
    {
        var dialog = _jsonDialog;
        _jsonDialog = null; _jsonText = null; _jsonError = null;
        if (GodotObject.IsInstanceValid(dialog)) { dialog.Hide(); dialog.QueueFree(); }
    }
}
#endif
