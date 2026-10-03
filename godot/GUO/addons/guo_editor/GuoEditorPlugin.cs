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
    private AssetsView _assets;
    private InspectorDock _inspector;
    private EditorSmoke _smoke;
    private EditorTour _tour;
    private WorldView _world;
    private ShardDock _shard;
    private RunBar _run;
    private AiDock _ai;
    private SearchPopup _search;

    // Whether the World tab was on screen when an assembly reload began.
    // A bool field survives the reload (Godot serializes it), and the editor
    // does not call _MakeVisible again for a tab that is already current, so
    // without this the rebuilt view would stay hidden behind its own button.
    private bool _worldWasVisible;
    private bool _assetsWasVisible;

    private const string ResetMenuLabel = "Reset GUO layout";

    /// <summary>The UO Assets view, for <see cref="GuoAssetsPlugin"/> to show and hide with its tab.</summary>
    public static AssetsView AssetsMain { get; private set; }

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
        _assets = new AssetsView(_data);
        AssetsMain = _assets;
        _inspector = new InspectorDock();
        _assets.Inspect += _inspector.ShowInspection;

        // UO Assets is a main-screen tab (GuoAssetsPlugin owns its button).
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_assets);
        _assets.Visible = _assetsWasVisible;
        _assetsWasVisible = false;
        AddDock(_inspector);

        // The World tab: the game's renderer, read only (ADR-0015). It starts
        // the world the first time it is shown, not here.
        _world = new WorldView(_data);
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_world);
        _world.Visible = _worldWasVisible;
        _worldWasVisible = false;
        _world.Inspect += _inspector.ShowInspection;
        // The live tier (ADR-0012): the UO Shard dock drives the World tab's edits to a shard.
        _shard = new ShardDock();
        AddDock(_shard);
        _shard.Attach(_world);

        // The AI hub (ADR-0028): chat, agents over ACP, the request queue. It owns child
        // processes (agent CLIs), which TearDown kills.
        _ai = new AiDock();
        AddDock(_ai);

        // Start server, start clients: on the toolbar, always one click away.
        _run = new RunBar();
        AddControlToContainer(CustomControlContainer.Toolbar, _run);

        MapPanel maps = _assets.Panel<MapPanel>();
        if (maps != null)
        {
            maps.JumpToWorld += ShowInWorld;
            _world.RadarSource = maps.RadarFor;
            _world.Host.OverlayChanged += (_, _) => _world.Minimap?.Invalidate();

            // While the world runs, the radar reads the world's map (with the
            // world project over it) and repaints the blocks an edit touches.
            maps.MapSource = () => _world != null && _world.IsBooted ? Client.Game?.UO.FileManager.Maps : null;
            _world.Host.OverlayChanged += maps.RefreshBlocks;
        }

        SearchContext searchContext = SearchContext.From(this, _data, _assets, _inspector, _world, _shard, _run, ShowInWorld);
        searchContext.Ai = _ai;
        _search = SearchPopup.Install(searchContext);

        string smokeOut = EditorSmoke.OutDirFromArgs();
        string tourOut = EditorTour.OutDirFromArgs();

        // A tool started this editor (the smoke flag): its window must not
        // take the keyboard or the foreground from whoever is working, as a
        // scripted game run's does not (Bootstrap/Main.cs NoFocus).
        if ((smokeOut != null || tourOut != null) && DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.AlwaysOnTop, false);
            GD.Print("[GUO editor] window: no focus (started by a tool)");
        }

        if (smokeOut != null)
        {
            _smoke = new EditorSmoke(smokeOut, _data, _assets, _inspector, _world, _shard);
            _smoke.Search = _search;
            _smoke.Ai = _ai;
            AddChild(_smoke);
        }

        if (tourOut != null)
        {
            _tour = new EditorTour(tourOut, _data, _assets, _inspector, _world, _shard, _run);
            _tour.Search = _search;
            AddChild(_tour);
        }

        // The headless editor is used to import and to build solutions
        // (launchers\dev\smoke.bat, build.bat); opening the install there is
        // time spent for nobody. The smoke check asks for it explicitly.
        if (smokeOut != null || tourOut != null || DisplayServer.GetName() != "headless")
        {
            _data.LoadAsync();
        }

        AddToolMenuItem(ResetMenuLabel, Callable.From(ResetLayout));

        // The default layout, once: never over a layout the user has changed.
        Callable.From(ApplyDefaultLayoutOnFirstRun).CallDeferred();

        GD.Print("[GUO editor] plugin entered");
    }

    private async void ApplyDefaultLayoutOnFirstRun()
    {
        // The editor restores its own layout after the plugins load.
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        if (_inspector != null && IsInstanceValid(_inspector))
        {
            GuoLayout.ApplyIfFirstRun(_inspector);
        }
    }

    /// <summary>
    /// Restores the default GUO layout: UO Assets in the centre, the UO
    /// Inspector in front on the right, Scene and FileSystem together on the
    /// left, the shard dock in the bottom panel. Public so a search popup can call it.
    /// </summary>
    public void ResetLayout()
    {
        if (_inspector != null)
        {
            GuoLayout.Apply(_inspector);
            _assets?.MakeVisible();
        }
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
        SearchPopup.Remove(_search);
        _search = null;

        if (_run != null)
        {
            RemoveControlFromContainer(CustomControlContainer.Toolbar, _run);
            _run.QueueFree();
            _run = null;
        }

        if (_ai != null)
        {
            // Kills the agent CLIs it started and stops any stream, before a reload or when the editor closes.
            _ai.Shutdown();
            RemoveDock(_ai);
            _ai.QueueFree();
            _ai = null;
        }

        if (_shard != null)
        {
            // Closes the bridge connection and its reader thread before a reload.
            _shard.Shutdown();
            RemoveDock(_shard);
            _shard.QueueFree();
            _shard = null;
        }

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

        RemoveToolMenuItem(ResetMenuLabel);

        if (_assets != null)
        {
            _assets.Inspect -= _inspector.ShowInspection;
            _assetsWasVisible = _assets.Visible;
            _assets.GetParent()?.RemoveChild(_assets);
            _assets.QueueFree();
            _assets = null;
            AssetsMain = null;
        }

        if (_inspector != null)
        {
            RemoveDock(_inspector);
            _inspector.QueueFree();
            _inspector = null;
        }

        if (_tour != null)
        {
            RemoveChild(_tour);
            _tour.QueueFree();
            _tour = null;
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
