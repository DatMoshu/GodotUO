#if TOOLS
namespace GUO.Editor;

using Godot;
using System.Linq;

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
    private StoreView _store;
    private ArtDock _art;
    private LogsDock _logs;
    private MapGenView _mapgen;
    private GumpStudio _gumps;
    private bool _gumpsWasVisible;
    public static GumpStudio GumpsMain { get; private set; }
    private MultiEditView _multiedit;
    private SearchPopup _search;
    private Button _collapseBottomPanels;
    private EditorMcpServer _editorMcp;
    private SearchContext _searchContext;
    private EditorSettings _settings;
    private SpriteMotionDock _spriteMotion;

    // Whether the World tab was on screen when an assembly reload began.
    // A bool field survives the reload (Godot serializes it), and the editor
    // does not call _MakeVisible again for a tab that is already current, so
    // without this the rebuilt view would stay hidden behind its own button.
    private bool _worldWasVisible;
    private bool _assetsWasVisible;
    private bool _storeWasVisible;
    private bool _mapgenWasVisible;
    private bool _multieditWasVisible;

    private const string ResetMenuLabel = "Reset GUO layout";

    /// <summary>The UO Assets view, for <see cref="GuoAssetsPlugin"/> to show and hide with its tab.</summary>
    public static AssetsView AssetsMain { get; private set; }

    /// <summary>The UO Store view, for <see cref="GuoStorePlugin"/> to show and hide with its tab.</summary>
    public static StoreView StoreMain { get; private set; }

    /// <summary>The Map Generator view, for <see cref="GuoMapGenPlugin"/> to show and hide with its tab.</summary>
    public static MapGenView MapGenMain { get; private set; }

    /// <summary>The Multi Editor view, for <see cref="GuoMultiEditPlugin"/> to show and hide with its tab.</summary>
    public static MultiEditView MultiEditMain { get; private set; }

    public const string WorldTabName = "World";

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

    public override void _EnterTree()
    {
        // Only a fresh start can tell: a reload (no _EnterTree) keeps the live layout.
        _layoutLost = GuoLayout.WasApplied() && !GuoLayout.SavedLayoutExists();
        Build();
    }

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

    private bool _layoutLost;

    private void Build()
    {
        if (_data != null)
        {
            return;
        }

        AiFeatures.Apply(AiFeatures.ReadPreference());
        _settings = EditorInterface.Singleton.GetEditorSettings();
        _settings.SettingsChanged += OnEditorSettingsChanged;
        _data = new EditorData();
        _gumps = new GumpStudio(_data);
        GumpsMain = _gumps;
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_gumps);
        _gumps.Visible = _gumpsWasVisible;
        _assets = new AssetsView(_data);
        AssetsMain = _assets;
        _inspector = new InspectorDock();
        _assets.Inspect += _inspector.ShowInspection;

        // UO Assets is a main-screen tab (GuoAssetsPlugin owns its button).
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_assets);
        _assets.Visible = _assetsWasVisible;
        _assetsWasVisible = false;
        AddDock(_inspector);

        // UO Store is the third main-screen tab (GuoStorePlugin owns its button): catalogues, server
        // content and publishing for shard owners and pack authors (ADR-0026 section 8). It loads its
        // catalogues when first shown.
        _store = new StoreView();
        StoreMain = _store;
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_store);
        _store.Visible = _storeWasVisible;
        _storeWasVisible = false;

        // The World tab: the game's renderer, read only (ADR-0015). It starts
        // the world the first time it is shown, not here.
        _world = new WorldView(_data);
        _world.FocusRequested += HideBottomPanel;
        _gumps.PreviewWorld = () => _world.EnsureBooted() ? _world.Host.World : throw new System.InvalidOperationException(_world.Error);
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_world);
        _world.Visible = _worldWasVisible;
        _worldWasVisible = false;
        _world.Inspect += _inspector.ShowInspection;
        // The live tier (ADR-0012): the UO Shard dock drives the World tab's edits to a shard.
        _shard = new ShardDock();
        AddDock(_shard);
        _shard.Attach(_world);
        _spriteMotion = new SpriteMotionDock();
        AddDock(_spriteMotion);

        // The AI hub (ADR-0028): chat, agents over ACP, the request queue. It owns child
        // processes (agent CLIs), which TearDown kills.
        if (AiFeatures.Enabled)
        {
            _ai = new AiDock();
            AddDock(_ai);
        }

        // The art pipeline (ADR-0029): image services, and the watcher that imports what Pixelorama
        // and Pinta save. It owns worker tasks, which TearDown cancels.
        _art = new ArtDock();
        _art.Attach(_data, () => _inspector?.Current);
        AddDock(_art);

        // The Logs dock: the server's and the clients' logs, tailed read only. It owns worker
        // tasks, which TearDown cancels.
        _logs = new LogsDock();
        AddDock(_logs);
        Callable.From(InstallBottomPanelCollapse).CallDeferred();

        // The Map Generator (ADR-0030): a main-screen tab (GuoMapGenPlugin owns its button). It runs
        // tools/mapgen as a process and opens what it exports in the World tab.
        _mapgen = new MapGenView { OpenInWorld = OpenGeneratedWorld };
        MapGenMain = _mapgen;
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_mapgen);
        _mapgen.Visible = _mapgenWasVisible;
        _mapgenWasVisible = false;

        // The Multi Editor (ADR-0031): a main-screen tab (GuoMultiEditPlugin owns its button).
        _multiedit = new MultiEditView(_data)
        {
            AfterWrite = AfterMultiWrite,
            PreviewInWorld = PreviewMultiInWorld,
        };
        MultiEditMain = _multiedit;
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_multiedit);
        _multiedit.Visible = _multieditWasVisible;
        _multieditWasVisible = false;
        MultiPanel.EditRequested = OpenInMultiEditor;
        BuildUoLayout();
        _world.AreaToMulti = (name, parts) =>
        {
            ShowMultiEditor();
            _multiedit?.GuardUnsaved(() => _multiedit.OpenParts(name, parts));
        };

        // Start server, start clients: on the toolbar, always one click away.
        _run = new RunBar();
        AddControlToContainer(CustomControlContainer.Toolbar, _run);
        _store.Run = _run;

        MapPanel maps = _assets.Panel<MapPanel>();
        if (maps != null)
        {
            maps.JumpToWorld += ShowInWorld;
            _world.RadarSource = maps.RadarFor;
            _world.Host.OverlayChanged += (f, b) => _world.Minimap?.Invalidate(f, b);

            // While the world runs, the radar reads the world's map (with the
            // world project over it) and repaints the blocks an edit touches.
            maps.MapSource = () => _world != null && _world.IsBooted ? Client.Game?.UO.FileManager.Maps : null;
            _world.Host.OverlayChanged += maps.RefreshBlocks;
        }

        SearchContext searchContext = SearchContext.From(this, _data, _assets, _inspector, _world, _shard, _run, ShowInWorld);
        searchContext.Ai = _ai;
        searchContext.Store = _store;
        searchContext.Logs = _logs;
        searchContext.MultiEdit = _multiedit;
        _search = SearchPopup.Install(searchContext);
        AssetField.Reveal = searchContext.RevealAsset;
        _searchContext = searchContext;
        ConnectAi();

        string smokeOut = EditorSmoke.OutDirFromArgs();
        string tourOut = EditorTour.OutDirFromArgs();

        // A tool started this editor (the smoke flag): its window must not
        // take the keyboard or the foreground from whoever is working, as a
        // scripted game run's does not (Bootstrap/Main.cs NoFocus).
        if ((smokeOut != null || tourOut != null || System.Environment.GetEnvironmentVariable("GUO_EDITOR_SCRIPTED") == "1") && DisplayServer.GetName() != "headless")
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
            _smoke.Store = _store;
            _smoke.Art = _art;
            _smoke.Logs = _logs;
            _smoke.MultiEdit = _multiedit;
            AddChild(_smoke);
        }

        if (tourOut != null)
        {
            _tour = new EditorTour(tourOut, _data, _assets, _inspector, _world, _shard, _run);
            _tour.Search = _search;
            _tour.Ai = _ai;
            _tour.Store = _store;
            AddChild(_tour);
        }
        else if (!string.IsNullOrWhiteSpace(EditorData.Setting("GUO_EDITOR_MCP_PORT", "")))
        {
            // An editor driven over the editor MCP can run one tour segment at a time (tour_segment); it runs nothing by itself.
            _tour = new EditorTour(null, _data, _assets, _inspector, _world, _shard, _run);
            _tour.Search = _search;
            _tour.Ai = _ai;
            _tour.Store = _store;
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

    // Only settings callbacks touch Godot; all AI workers observe AiFeatures' snapshot.
    private void OnEditorSettingsChanged()
    {
        bool enabled = AiFeatures.ReadPreference();
        if (enabled != AiFeatures.Enabled) ApplyAiPreference(enabled);
    }

    internal void ApplyAiPreference(bool enabled)
    {
        AiFeatures.Apply(enabled); // Cancel work before detaching its UI and tool targets.
        StopAi();
        if (enabled)
        {
            _ai = new AiDock();
            AddDock(_ai);
        }
        _art?.ApplyAiFeatures();
        _searchContext.Ai = _ai;
        SearchPopup.Remove(_search);
        _search = SearchPopup.Install(_searchContext);
        ConnectAi();
        if (_smoke != null) { _smoke.Ai = _ai; _smoke.Search = _search; }
        if (_tour != null) { _tour.Ai = _ai; _tour.Search = _search; }
    }

    private void ConnectAi()
    {
        if (!AiFeatures.Enabled || _ai == null) return;
        _ai.UseTools(_searchContext, () => _search?.Index);
        EditorCapabilities.Register(_ai.Hub.Tools, _searchContext, () => _search?.Index, () => _tour);
        _editorMcp = EditorMcpServer.StartConfigured(_ai.Hub.Tools);
        EditorMcpConnection.Configure(_editorMcp != null);
        _ai.Hub.SelectionImage = () => _inspector?.Current?.Image;
        _ai.Hub.SelectionLabel = () => _inspector?.Current is Inspection i ? $"{i.Source} {i.Id}" : null;
    }

    private void StopAi()
    {
        _editorMcp?.Dispose();
        _editorMcp = null;
        EditorMcpConnection.Configure(false);
        if (_ai == null) return;
        _ai.Shutdown();
        RemoveDock(_ai);
        _ai.GetParent()?.RemoveChild(_ai);
        _ai.QueueFree();
        _ai = null;
    }

    internal bool AiRunning => _ai != null;
    internal bool AiMcpRunning => _editorMcp != null;

    private async void ApplyDefaultLayoutOnFirstRun()
    {
        // The editor restores its own layout after the plugins load.
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        if (_inspector != null && IsInstanceValid(_inspector))
        {
            GuoLayout.ApplyIfFirstRun(_inspector, _layoutLost);
            _layoutLost = false;
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

    /// <summary>Opens a generated world project in the World tab at a cell.</summary>
    private void OpenGeneratedWorld(string root, int facet, int x, int y)
    {
        ShowInWorld(facet, x, y);
        _world?.OpenProject(root);
    }

    /// <summary>Brings the Multi Editor tab forward.</summary>
    public void ShowMultiEditor()
    {
        EditorInterface.Singleton.SetMainScreenEditor(MultiEditView.TabName);
        if (_multiedit != null)
        {
            _multiedit.Visible = true;
        }
    }

    /// <summary>Opens a client multi in the Multi Editor (the Multis panel's button, F3).</summary>
    private void OpenInMultiEditor(int id)
    {
        ShowMultiEditor();
        _multiedit?.GuardUnsaved(() => _multiedit.OpenClientMulti(id));
    }

    /// <summary>After a write to a stage: the Multis panel lists the new multi without a restart.</summary>
    private void AfterMultiWrite(SaveResult result)
    {
        _assets?.Panel<MultiPanel>()?.RefreshIds();
    }

    private void PreviewMultiInWorld(int id)
    {
        if (_world == null || !_world.PreviewMulti(id))
        {
            SearchContext.Toast("Open the World tab once so it can place the multi.", EditorToaster.Severity.Warning);
            return;
        }

        EditorInterface.Singleton.SetMainScreenEditor(WorldTabName);
        _world.Visible = true;
    }

    private void InstallBottomPanelCollapse()
    {
        if (_data == null || _collapseBottomPanels != null)
        {
            return;
        }

        Node panel = GodotUi.Walk(GodotUi.Base).FirstOrDefault(n => n.GetClass() == "EditorBottomPanel");
        if (panel == null)
        {
            return;
        }

        // Use the same tab-row discovery as the F3 panel provider. Collapse through
        // Godot's API so its selection, saved layout and split sizing stay in sync.
        HBoxContainer tabs = GodotUi.All<HBoxContainer>(panel).FirstOrDefault(box =>
            box.GetChildren().OfType<Button>().Count(b => b.ToggleMode && b.Text.Length > 0) >= 2);
        if (tabs == null)
        {
            return;
        }

        _collapseBottomPanels = new Button
        {
            Name = "GuoCollapseBottomPanels",
            Text = "Collapse",
            TooltipText = "Collapse the bottom panels. Click any panel tab to open it again.",
            Flat = true,
        };
        _collapseBottomPanels.Pressed += HideBottomPanel;
        tabs.AddChild(_collapseBottomPanels);
        tabs.MoveChild(_collapseBottomPanels, 0);
    }

    private void TearDown()
    {
        RemoveUoLayout();

        if (_collapseBottomPanels != null)
        {
            _collapseBottomPanels.Pressed -= HideBottomPanel;
            _collapseBottomPanels.GetParent()?.RemoveChild(_collapseBottomPanels);
            _collapseBottomPanels.QueueFree();
            _collapseBottomPanels = null;
        }

        if (_settings != null) _settings.SettingsChanged -= OnEditorSettingsChanged;
        AiFeatures.Apply(false);
        _settings = null;
        // Save recovery before StoreView clears System.Text.Json's process-wide type caches.
        // Serializing after that cache release pins this assembly and prevents hot reload.
        if (_gumps != null)
        {
            _gumpsWasVisible = _gumps.Visible;
            _gumps.Shutdown();
            _gumps.GetParent()?.RemoveChild(_gumps);
            _gumps.QueueFree();
            _gumps = null;
            GumpsMain = null;
        }
        if (_multiedit != null)
        {
            // Frees the canvas textures and drops the loader overlay before a reload.
            _multieditWasVisible = _multiedit.Visible;
            _multiedit.Shutdown();
            MultiPanel.EditRequested = null;
            _multiedit.GetParent()?.RemoveChild(_multiedit);
            _multiedit.QueueFree();
            _multiedit = null;
            MultiEditMain = null;
        }

        if (_mapgen != null)
        {
            // Stops a generator run in progress before a reload.
            _mapgen.Shutdown();
            _mapgenWasVisible = _mapgen.Visible;
            _mapgen.GetParent()?.RemoveChild(_mapgen);
            _mapgen.QueueFree();
            _mapgen = null;
            MapGenMain = null;
        }

        StopAi();
        SearchPopup.Remove(_search);
        _searchContext = null;
        _search = null;
        AssetField.Reveal = null;

        if (_run != null)
        {
            RemoveControlFromContainer(CustomControlContainer.Toolbar, _run);
            _run.QueueFree();
            _run = null;
        }

        if (_logs != null)
        {
            // Cancels every log tailer before a reload or when the editor closes.
            _logs.Shutdown();
            RemoveDock(_logs);
            _logs.QueueFree();
            _logs = null;
        }

        if (_art != null)
        {
            _art.Shutdown();
            RemoveDock(_art);
            _art.QueueFree();
            _art = null;
        }

        if (_ai != null)
        {
            // Kills the agent CLIs it started and stops any stream, before a reload or when the editor closes.
            _ai.Shutdown();
            RemoveDock(_ai);
            _ai.QueueFree();
            _ai = null;
        }

        if (_spriteMotion != null)
        {
            _spriteMotion.Shutdown();
            RemoveDock(_spriteMotion);
            _spriteMotion.QueueFree();
            _spriteMotion = null;
        }
        if (_shard != null)
        {
            // Closes the bridge connection and its reader thread before a reload.
            _shard.Shutdown();
            RemoveDock(_shard);
            _shard.QueueFree();
            _shard = null;
        }

        if (_store != null)
        {
            // Kills any tool process it started and cancels its catalogue fetches.
            _storeWasVisible = _store.Visible;
            _store.Shutdown();
            _store.GetParent()?.RemoveChild(_store);
            _store.QueueFree();
            _store = null;
            StoreMain = null;
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
