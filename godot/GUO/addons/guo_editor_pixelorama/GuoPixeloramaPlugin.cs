#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>Editor-only native-window host. No Pixelorama code enters the game assembly.</summary>
[Tool]
public partial class GuoPixeloramaPlugin : EditorPlugin, ISerializationListener
{
    public static PixeloramaView Main { get; private set; }
    private bool _visible;
    public override bool _HasMainScreen() => true;
    public override string _GetPluginName() => "Pixelorama";
    public override Texture2D _GetPluginIcon() => EditorInterface.Singleton.GetEditorTheme().GetIcon("Image", "EditorIcons");
    public override void _EnterTree()
    {
        Build();
        // Probe launch belongs to initial entry, never to assembly deserialization.
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--guo-pixelorama-probe") >= 0)
            Callable.From(() =>
            {
                if (Main == null) return;
                EditorInterface.Singleton.GetBaseControl().GetWindow().Title = "GUO Pixelorama probe";
                EditorInterface.Singleton.SetMainScreenEditor("Pixelorama");
                Main.OpenImage(null);
            }).CallDeferred();
    }
    public override void _ExitTree() => TearDown();
    public override void _MakeVisible(bool visible)
    {
        _visible = visible;
        if (Main != null) Main.Visible = visible;
        var screen = EditorInterface.Singleton.GetEditorMainScreen();
        if (visible && !screen.HasMeta("guo_pixelorama_auto_opened") && DisplayServer.GetName() != "headless")
        {
            screen.SetMeta("guo_pixelorama_auto_opened", true);
            Callable.From(() => { if (Main != null && !Main.HasSession) Main.OpenImage(null); }).CallDeferred();
        }
    }
    public void OnBeforeSerialize() => TearDown();
    public void OnAfterDeserialize() => Callable.From(Build).CallDeferred();
    private void Build()
    {
        if (Main != null) return;
        Main = new PixeloramaView
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(Main);
        Main.Visible = _visible;
    }
    private void TearDown()
    {
        if (Main == null) return;
        // Unsaved artwork belongs to the artist: detach rather than terminate on reload/exit.
        Main.Detach();
        Main.GetParent()?.RemoveChild(Main);
        Main.QueueFree();
        Main = null;
    }
}
#endif
