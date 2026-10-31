#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>Theme-native, keyboard-focusable editor actions shared by the asset workspace.</summary>
internal static class AssetUi
{
    public static Texture2D Icon(string name)
    {
        Theme theme = EditorInterface.Singleton.GetEditorTheme();
        if (name == "Undo" && !theme.HasIcon(name, "EditorIcons"))
            name = theme.HasIcon("UndoRedo", "EditorIcons") ? "UndoRedo" : "Reload";
        return theme.GetIcon(theme.HasIcon(name, "EditorIcons") ? name : "Tools", "EditorIcons");
    }

    public static Button Action(string icon, string label, Action run)
    {
        var button = new Button
        {
            Icon = Icon(icon), TooltipText = label, AccessibilityName = label,
            CustomMinimumSize = new Vector2(30, 30) * EditorInterface.Singleton.GetEditorScale(),
        };
        button.Pressed += () => { try { run(); } catch (Exception e) { AssetActions.ReportEdit(e.Message); } };
        return button;
    }

    public static Label Label(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        // A wrapped label measured at width zero makes a native AcceptDialog thousands of pixels tall.
        Size = new Vector2(240, 0) * EditorInterface.Singleton.GetEditorScale(),
        CustomMinimumSize = new Vector2(120, 0) * EditorInterface.Singleton.GetEditorScale(),
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
}
#endif
