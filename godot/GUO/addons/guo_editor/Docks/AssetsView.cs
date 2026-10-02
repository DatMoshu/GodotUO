#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// The UO Assets main-screen tab (docs/editor_plan.md §4.6): the whole centre
/// of the editor, one tab per kind of client asset, a bigger grid (cell size
/// S/M/L) and the search at the top of each tab. Each tab is an
/// <see cref="AssetPanel"/>; whatever a panel picks goes to the UO Inspector
/// through <see cref="Inspect"/>. The tab button itself comes from
/// <see cref="GuoAssetsPlugin"/>.
/// </summary>
[Tool]
public partial class AssetsView : VBoxContainer
{
    public const string TabName = "UO Assets";

    private readonly EditorData _data;
    private TabContainer _tabs;
    private OptionButton _cells;
    private readonly List<AssetPanel> _panels = new();

    /// <summary>Raised with what a panel picked.</summary>
    public event Action<Inspection> Inspect;

    public IReadOnlyList<AssetPanel> Panels => _panels;

    public AssetsView() : this(null)
    {
    }

    public AssetsView(EditorData data)
    {
        _data = data;
        Name = "UOAssets";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    /// <summary>Cell size of the grids: 0 small, 1 medium, 2 large.</summary>
    public int CellSizeIndex
    {
        get => _cells?.Selected ?? 1;
        set
        {
            _cells?.Select(value);
            ApplyCellSize();
        }
    }

    public override void _Ready()
    {
        if (_tabs != null || _data == null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        bar.AddChild(new Label { Text = "Cell size" });
        _cells = new OptionButton { TooltipText = "Size of the icons in the grids" };
        foreach (string c in new[] { "S", "M", "L" })
        {
            _cells.AddItem(c);
        }

        _cells.Selected = 1;
        _cells.ItemSelected += _ => ApplyCellSize();
        bar.AddChild(_cells);
        bar.AddChild(new Label
        {
            Text = "  Pick an asset; the UO Inspector (right) shows it. Search is at the top of each tab.",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        });

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_tabs);

        Add("Art", new ArtPanel());
        Add("Gumps", new GumpPanel());
        Add("Anims", new AnimationPanel());
        Add("Hues", new HuePanel());
        Add("Multis", new MultiPanel());
        Add("Maps", new MapPanel());
        Add("Cliloc", new ClilocPanel());
        Add("Sounds", new SoundPanel());
        Add("Bulk", new BulkPanel());
        Add("Parity", new ParityPanel());
        ApplyCellSize();

        if (_data.IsLoaded || _data.Error != null)
        {
            OnDataLoaded();
        }
        else
        {
            _data.Loaded += OnDataLoaded;
        }
    }

    private void ApplyCellSize()
    {
        int px = CellSizeIndex switch { 0 => 44, 2 => 120, _ => 76 };
        px = (int)(px * EditorInterface.Singleton.GetEditorScale());
        foreach (AssetPanel panel in _panels)
        {
            (panel as GridPanel)?.SetCellSize(px);
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

    /// <summary>Brings the UO Assets tab to the front of the main screen.</summary>
    public void MakeVisible()
    {
        EditorInterface.Singleton.SetMainScreenEditor(TabName);
        Visible = true;
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
