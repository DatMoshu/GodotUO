#if TOOLS
namespace GUO.Editor;
using Godot;

[Tool]
public partial class GuoGumpsPlugin : EditorPlugin
{
    public override bool _HasMainScreen() => true;
    public override string _GetPluginName() => "Gumps";
    public override Texture2D _GetPluginIcon() => EditorInterface.Singleton.GetEditorTheme().GetIcon("Control", "EditorIcons");
    public override void _MakeVisible(bool visible)
    {
        if (GuoEditorPlugin.GumpsMain != null) GuoEditorPlugin.GumpsMain.Visible = visible;
    }
}
#endif
