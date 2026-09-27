#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The GUO editor addon: turns the Godot editor into the UO workbench
/// (docs/editor_plan.md). Registers the UO docks and owns the one
/// <see cref="EditorData"/> they share.
/// </summary>
/// <remarks>
/// <para>
/// Everything under addons/guo_editor is compiled into the game assembly but
/// only in the Debug configuration, which is the only one that defines
/// TOOLS: an exported client (ExportDebug / ExportRelease) carries none of
/// it. See docs/architecture/ADR-0010-editor-addon-shape.md.
/// </para>
/// <para>
/// When the C# is rebuilt with the editor open, Godot unloads the assembly
/// and recreates every managed object with its parameterless constructor.
/// It does not run <c>_EnterTree</c> again, so a plugin that set itself up
/// there comes back with empty fields and docks nobody drives. Hence
/// <see cref="ISerializationListener"/>: the docks and the open client data
/// are torn down before the reload and built again after it.
/// </para>
/// </remarks>
[Tool]
public partial class GuoEditorPlugin : EditorPlugin, ISerializationListener
{
    private EditorData _data;
    private AssetsDock _assets;
    private InspectorDock _inspector;
    private EditorSmoke _smoke;
    private WorldView _world;

    // Whether the World tab was on screen when an assembly reload began.
    // A bool field survives the reload (Godot serializes it), and the editor
    // does not call _MakeVisible again for a tab that is already current, so
    // without this the rebuilt view would stay hidden behind its own button.
    private bool _worldWasVisible;

    public const string WorldTabName = "UO World";

    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => WorldTabName;

    public override Texture2D _GetPluginIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("WorldEnvironment", "EditorIcons");

    public override void _MakeVisible(bool visible)
    {
        if (_world != null)
        {
            _world.Visible = visible;
        }
    }

    public override void _EnterTree() => Build();

    public override void _ExitTree() => TearDown();

    public void OnBeforeSerialize()
    {
        GD.Print("[GUO editor] assembly reload: closing docks and client data");
        TearDown();
    }

    public void OnAfterDeserialize()
    {
        GD.Print("[GUO editor] assembly reloaded: rebuilding docks");
        Callable.From(Build).CallDeferred();
    }

    private void Build()
    {
        if (_data != null)
        {
            return;
        }

        _data = new EditorData();
        _assets = new AssetsDock(_data);
        _inspector = new InspectorDock();
        _assets.Inspect += _inspector.ShowInspection;

        AddDock(_assets);
        AddDock(_inspector);

        // The World tab: the game's renderer, read only (ADR-0015). It starts
        // the world the first time it is shown, not here.
        _world = new WorldView(_data);
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_world);
        _world.Visible = _worldWasVisible;
        _worldWasVisible = false;
        _world.Inspect += _inspector.ShowInspection;
        MapPanel maps = _assets.Panel<MapPanel>();
        if (maps != null)
        {
            maps.JumpToWorld += ShowInWorld;

            // While the world runs, the radar reads the world's map (with the
            // world project over it) and repaints the blocks an edit touches.
            maps.MapSource = () => _world != null && _world.IsBooted ? Client.Game?.UO.FileManager.Maps : null;
            _world.Host.OverlayChanged += maps.RefreshBlocks;
        }

        string smokeOut = EditorSmoke.OutDirFromArgs();
        if (smokeOut != null)
        {
            _smoke = new EditorSmoke(smokeOut, _data, _assets, _inspector, _world);
            AddChild(_smoke);
        }

        // The headless editor is used to import and to build solutions
        // (launchers\dev\smoke.bat, build.bat); opening the install there is
        // time spent for nobody. The smoke check asks for it explicitly.
        if (smokeOut != null || DisplayServer.GetName() != "headless")
        {
            _data.LoadAsync();
        }

        GD.Print("[GUO editor] plugin entered");
    }

    /// <summary>Brings the World tab forward at a cell.</summary>
    public void ShowInWorld(int facet, int x, int y)
    {
        EditorInterface.Singleton.SetMainScreenEditor(WorldTabName);
        if (_world != null)
        {
            // Already the current tab (after a reload, say): the editor does
            // not call _MakeVisible for it, so show it here.
            _world.Visible = true;
            _world.GoTo(facet, x, y);
        }
    }

    private void TearDown()
    {
        if (_world != null)
        {
            // Frees the embedded controller's render resources and releases
            // Client.Game, so an assembly reload finds nothing held.
            _worldWasVisible = _world.Visible;
            _world.Shutdown();
            _world.GetParent()?.RemoveChild(_world);
            _world.QueueFree();
            _world = null;
        }

        if (_assets != null)
        {
            _assets.Inspect -= _inspector.ShowInspection;
            RemoveDock(_assets);
            _assets.QueueFree();
            _assets = null;
        }

        if (_inspector != null)
        {
            RemoveDock(_inspector);
            _inspector.QueueFree();
            _inspector = null;
        }

        if (_smoke != null)
        {
            RemoveChild(_smoke);
            _smoke.QueueFree();
            _smoke = null;
        }

        // The loaders map the install's files: close them rather than leave
        // the handles to the finalizers of an assembly being unloaded.
        _data?.Dispose();
        _data = null;
    }
}
#endif
