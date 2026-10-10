#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The Admin main-screen tab's button. Like <see cref="GuoStorePlugin"/>: an editor plugin owns one main screen,
/// so the Admin tab is its own tiny plugin, enabled by its own plugin.cfg under addons/guo_editor_admin. The view
/// is built, fed and torn down by <see cref="GuoEditorPlugin"/>.
/// </summary>
[Tool]
public partial class GuoAdminPlugin : EditorPlugin
{
    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => AdminView.TabName;

    public override Texture2D _GetPluginIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("Tools", "EditorIcons");

    public override void _MakeVisible(bool visible)
    {
        if (GuoEditorPlugin.AdminMain != null)
        {
            GuoEditorPlugin.AdminMain.Visible = visible;
        }
    }
}
#endif
