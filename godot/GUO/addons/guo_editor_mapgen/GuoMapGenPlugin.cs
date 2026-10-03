#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The Map Generator main-screen tab's button (ADR-0030). Like <see cref="GuoAssetsPlugin"/>, a
/// second tiny plugin, because an editor plugin owns one main screen. The view is built and torn
/// down by <see cref="GuoEditorPlugin"/>; this one only shows and hides it with its tab.
/// </summary>
[Tool]
public partial class GuoMapGenPlugin : EditorPlugin
{
    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => MapGenView.TabName;

    public override Texture2D _GetPluginIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("GridMap", "EditorIcons");

    public override void _MakeVisible(bool visible)
    {
        if (GuoEditorPlugin.MapGenMain != null)
        {
            GuoEditorPlugin.MapGenMain.Visible = visible;
        }
    }
}
#endif
