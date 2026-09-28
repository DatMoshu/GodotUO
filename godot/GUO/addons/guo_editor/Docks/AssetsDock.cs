#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// The UO Assets dock (docs/editor_plan.md §4.6): one tab per kind of client
/// asset, in the plan's priority order. Each tab is an <see cref="AssetPanel"/>;
/// whatever a panel picks goes to the UO Inspector through
/// <see cref="Inspect"/>.
/// </summary>
[Tool]
public partial class AssetsDock : EditorDock
{
    private readonly EditorData _data;
    private TabContainer _tabs;
    private readonly List<AssetPanel> _panels = new();

    /// <summary>Raised with what a panel picked.</summary>
    public event Action<Inspection> Inspect;

    public IReadOnlyList<AssetPanel> Panels => _panels;

    public AssetsDock() : this(null)
    {
    }

    public AssetsDock(EditorData data)
    {
        _data = data;
        Name = "UOAssets";
        Title = "UO Assets";
        LayoutKey = "guo_assets";
        DefaultSlot = DockSlot.LeftUl;
        AvailableLayouts = DockLayout.Vertical | DockLayout.Floating;
        IconName = "ImageTexture";
    }

    public override void _Ready()
    {
        if (_tabs != null || _data == null)
        {
            return;
        }

        _tabs = new TabContainer();
        _tabs.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_tabs);

        Add("Art", new ArtPanel());
        Add("Gumps", new GumpPanel());
        Add("Anims", new AnimationPanel());
        Add("Hues", new HuePanel());
        Add("Multis", new MultiPanel());
        Add("Cliloc", new ClilocPanel());
        Add("Sounds", new SoundPanel());
        Add("Maps", new MapPanel());
        Add("Parity", new ParityPanel());
        Add("Bulk", new BulkPanel());

        if (_data.IsLoaded || _data.Error != null)
        {
            OnDataLoaded();
        }
        else
        {
            _data.Loaded += OnDataLoaded;
        }
    }

    private void Add(string title, AssetPanel panel)
    {
        panel.Name = title;
        panel.Attach(_data);
        panel.Inspect += i => Inspect?.Invoke(i);
        _tabs.AddChild(panel);
        _panels.Add(panel);
    }

    private void OnDataLoaded()
    {
        if (!IsInstanceValid(this))
        {
            return;
        }

        if (!_data.IsLoaded)
        {
            GD.PrintErr($"[GUO editor] UO Assets: {_data.Error}");
            return;
        }

        _data.AssetsApplied -= OnAssetsApplied;
        _data.AssetsApplied += OnAssetsApplied;
        foreach (AssetPanel panel in _panels)
        {
            try
            {
                panel.OnDataLoaded();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO editor] {panel.Name} panel failed to load: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>An import or revert changed the overlay: every grid redraws what it shows.</summary>
    private void OnAssetsApplied()
    {
        if (!IsInstanceValid(this))
        {
            return;
        }

        foreach (AssetPanel panel in _panels)
        {
            if (panel is GridPanel grid && (panel is ArtPanel || panel is GumpPanel || panel is HuePanel))
            {
                grid.OnAssetsChanged();
            }
        }
    }

    /// <summary>Brings a tab to the front.</summary>
    public void ShowPanel(AssetPanel panel)
    {
        _tabs.CurrentTab = _panels.IndexOf(panel);
    }

    public T Panel<T>() where T : AssetPanel => _panels.Find(p => p is T) as T;

    public override void _ExitTree()
    {
        if (_data != null)
        {
            _data.Loaded -= OnDataLoaded;
            _data.AssetsApplied -= OnAssetsApplied;
        }

        foreach (AssetPanel panel in _panels)
        {
            panel.Shutdown();
        }
    }
}
#endif
