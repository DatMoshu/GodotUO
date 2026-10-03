#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The UO Store main-screen tab's button. Like <see cref="GuoAssetsPlugin"/>: an editor plugin owns one
/// main screen, so the store tab is a third tiny plugin, enabled by its own plugin.cfg under
/// addons/guo_editor_store. The view is built, fed and torn down by <see cref="GuoEditorPlugin"/>.
/// </summary>
[Tool]
public partial class GuoStorePlugin : EditorPlugin
{
    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => StoreView.TabName;

    public override Texture2D _GetPluginIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("Load", "EditorIcons");

    public override void _MakeVisible(bool visible)
    {
        if (GuoEditorPlugin.StoreMain != null)
        {
            GuoEditorPlugin.StoreMain.Visible = visible;
        }
    }
}
#endif
