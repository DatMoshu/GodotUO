#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The UO Assets main-screen tab's button. An editor plugin owns one main
/// screen, and <see cref="GuoEditorPlugin"/> already owns UO World, so the
/// assets tab is a second (tiny) plugin, enabled by its own plugin.cfg under
/// addons/guo_editor_assets. The view itself is built, fed and torn down by
/// the main plugin (it holds the client data); this one only shows and hides
/// it with its tab.
/// </summary>
[Tool]
public partial class GuoAssetsPlugin : EditorPlugin
{
    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => AssetsView.TabName;

    public override Texture2D _GetPluginIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("ImageTexture", "EditorIcons");

    public override void _MakeVisible(bool visible)
    {
        if (GuoEditorPlugin.AssetsMain != null)
        {
            GuoEditorPlugin.AssetsMain.Visible = visible;
        }
    }
}
#endif
