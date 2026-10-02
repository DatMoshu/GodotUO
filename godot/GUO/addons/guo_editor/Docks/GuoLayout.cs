#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using Godot;

/// <summary>
/// The default GUO editor layout, applied on the first run and whenever
/// "Reset GUO layout" is chosen, never otherwise: a layout the user changed
/// stays theirs. Whether it was applied is remembered in the editor's project
/// metadata (under .godot, not in tracked files).
/// </summary>
/// <remarks>
/// The arrangement: UO Assets is a main-screen tab (the whole centre); the UO
/// Inspector has the full height of the right dock column in front of Godot's
/// Inspector; Scene and FileSystem share one narrow tabbed dock on the left;
/// the UO Shard dock sits in the bottom panel beside Output.
/// </remarks>
public static class GuoLayout
{
    private const string Section = "guo_editor";
    private const string Key = "layout_applied_v1";

    /// <summary>True if the default has been applied before (on this project, in this editor's metadata).</summary>
    public static bool WasApplied() =>
        EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(Section, Key, false).AsBool();

    /// <summary>Applies the default only if it never was. True if it did.</summary>
    public static bool ApplyIfFirstRun(InspectorDock inspector)
    {
        if (WasApplied())
        {
            return false;
        }

        Apply(inspector);
        return true;
    }

    /// <summary>Applies the default layout now.</summary>
    public static void Apply(InspectorDock inspector)
    {
        Node root = EditorInterface.Singleton.GetBaseControl();

        // Scene and FileSystem share the upper-right slot of the left side.
        TabContainer scene = FindSlot(root, "DockSlotLeftUR");
        Control fs = EditorInterface.Singleton.GetFileSystemDock();
        if (scene != null && fs != null && fs.GetParent() is Node from && from != scene)
        {
            string title = fs.Name;
            fs.Reparent(scene, false);
            int at = scene.GetChildCount() - 1;
            scene.SetTabTitle(at, title);
            scene.CurrentTab = 0;
        }

        // The UO Inspector first in the right column, Godot's Inspector behind it
        // (wherever an earlier layout left it: it is moved to Godot's slot).
        TabContainer right = null;
        for (Node n = EditorInterface.Singleton.GetInspector(); n != null; n = n.GetParent())
        {
            if (n is TabContainer t)
            {
                right = t;
                break;
            }
        }

        if (inspector != null && right != null)
        {
            if (inspector.GetParent() != right)
            {
                string title = inspector.Title;
                inspector.Reparent(right, false);
                right.SetTabTitle(right.GetChildCount() - 1, title);
            }

            // Wide enough for the preview and the details: the column was Godot's narrow default.
            right.CustomMinimumSize = new Vector2(400 * EditorInterface.Singleton.GetEditorScale(), 0);
            right.MoveChild(inspector, 0);
            right.CurrentTab = 0;
        }

        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata(Section, Key, true);
        GD.Print("[GUO editor] default layout applied");
    }

    private static TabContainer FindSlot(Node root, string name)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n is TabContainer t && n.Name == name)
            {
                return t;
            }

            foreach (Node c in n.GetChildren())
            {
                stack.Push(c);
            }
        }

        return null;
    }
}
#endif
