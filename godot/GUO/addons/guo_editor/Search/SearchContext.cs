#if TOOLS
namespace GUO.Editor;

using System;
using System.Reflection;
using Godot;

/// <summary>
/// What the providers act on: the editor's own docks and the open client
/// data, handed over once by the plugin. Providers keep no other state about
/// the editor, so a reload (which rebuilds the plugin) rebuilds them too.
/// </summary>
public sealed class SearchContext
{
    public EditorData Data;
    public AssetsView Assets;
    public InspectorDock Inspector;
    internal WorldView World;
    public ShardDock Shard;
    public RunBar Run;
    public AiDock Ai;
    public EditorPlugin Plugin;

    /// <summary>Brings the World tab forward at a cell (the plugin's own method).</summary>
    public Action<int, int, int> ShowInWorld;

    /// <summary>Brings the World tab forward where it is.</summary>
    public Action ShowWorldTab;

    /// <summary>
    /// The plugin's ResetLayout method, if it has one. When it is missing, the
    /// "Reset GUO layout" command then says it is not available yet.
    /// </summary>
    public Action ResetLayout =>
        Plugin?.GetType().GetMethod("ResetLayout", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is MethodInfo m
            ? () => m.Invoke(Plugin, null)
            : null;

    /// <summary>The plugin's one call: everything the providers act on.</summary>
    internal static SearchContext From(EditorPlugin plugin, EditorData data, AssetsView assets, InspectorDock inspector,
        WorldView world, ShardDock shard, RunBar run, Action<int, int, int> showInWorld) =>
        new()
        {
            Plugin = plugin, Data = data, Assets = assets, Inspector = inspector, World = world, Shard = shard, Run = run,
            ShowInWorld = showInWorld,
            ShowWorldTab = () =>
            {
                EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
                world.Visible = true;
            },
        };

    /// <summary>A line in the editor's toast area (bottom right): what F3 just did, or why it could not.</summary>
    public static void Toast(string message, EditorToaster.Severity severity = EditorToaster.Severity.Info)
    {
        try
        {
            EditorInterface.Singleton.GetEditorToaster()?.PushToast(message, severity);
        }
        catch (Exception)
        {
            GD.Print($"[GUO editor] {message}");
        }
    }

    /// <summary>Brings an Assets tab forward and searches it, which selects the match and fills the UO Inspector.</summary>
    public bool OpenAsset(AssetPanel panel, string search)
    {
        if (panel == null || Assets == null)
        {
            return false;
        }

        Assets.MakeVisible();
        Assets.ShowPanel(panel);
        return panel.Search(search) != null;
    }
}
#endif
