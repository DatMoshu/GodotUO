#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The Multi Editor main-screen tab's button (ADR-0031). Like <see cref="GuoMapGenPlugin"/>, a second tiny
/// plugin, because an editor plugin owns one main screen. The view is built and torn down by
/// <see cref="GuoEditorPlugin"/>; this one only shows and hides it with its tab.
/// </summary>
[Tool]
public partial class GuoMultiEditPlugin : EditorPlugin
{
    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => MultiEditView.TabName;

    public override Texture2D _GetPluginIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("Node3D", "EditorIcons");

    public override void _MakeVisible(bool visible)
    {
        if (GuoEditorPlugin.MultiEditMain != null)
        {
            GuoEditorPlugin.MultiEditMain.Visible = visible;
        }
    }
}
#endif
