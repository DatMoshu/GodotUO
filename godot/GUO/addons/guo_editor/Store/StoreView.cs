#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The UO Store main-screen tab (ADR-0026 section 8, "admin windows"): the store for shard owners
/// and pack authors, in the editor where they work. Three sections, one tab each: Browse and
/// install, Server content (deploy to a server profile) and Publish. The tab button itself comes
/// from <see cref="GuoStorePlugin"/>, like UO Assets'.
/// </summary>
/// <remarks>
/// Players use the game client's Store (Options, Video, Store); this is not that window. Nothing
/// here writes to the client install, and the editor never handles a signing key.
/// </remarks>
[Tool]
public partial class StoreView : VBoxContainer
{
    public const string TabName = "UO Store";

    private TabContainer _tabs;
    private StoreBench _bench;

    public StoreView()
    {
        Name = "UOStore";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    internal StoreBench Bench => _bench;

    public StoreBrowseTab Browse { get; private set; }

    public StoreServerTab Server { get; private set; }

    public StorePublishTab Publish { get; private set; }

    /// <summary>The run bar, so a deployment can be put on a profile and the profile list reloaded.</summary>
    public RunBar Run { get; set; }

    /// <summary>The smoke check points the store at a scratch folder and away from the internet.</summary>
    internal void UseBench(StoreBench bench)
    {
        _bench = bench;
    }

    public override void _Ready()
    {
        if (_tabs != null)
        {
            return;
        }

        _bench ??= new StoreBench();
        AddChild(new Label
        {
            Text = "  For shard owners and pack authors. Players install packs in the game client: Options, Video, Store. "
                + "Installed here: " + _bench.Root,
            ClipText = true,
            TooltipText = "Packs installed in this tab live in the editor's own store, not in your game profile, and nothing here writes to the UO client folder.",
        });
        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_tabs);
        Browse = new StoreBrowseTab { Name = "Browse and install" };
        Server = new StoreServerTab { Name = "Server content" };
        Publish = new StorePublishTab { Name = "Publish" };
        Browse.Attach(this);
        Server.Attach(this);
        Publish.Attach(this);
        _tabs.AddChild(Browse);
        _tabs.AddChild(Server);
        _tabs.AddChild(Publish);
        _tabs.TabChanged += _ => ShownTab();
        VisibilityChanged += ShownTab;
    }

    // Catalogues are fetched when the tab is first shown, not when the editor starts.
    private void ShownTab()
    {
        if (!IsVisibleInTree() || _tabs == null)
        {
            return;
        }

        (_tabs.GetCurrentTabControl() as IStoreSection)?.Shown();
    }

    public void ShowSection(int index)
    {
        MakeVisible();
        _tabs.CurrentTab = index;
        ShownTab();
    }

    /// <summary>Brings the UO Store tab to the front of the main screen.</summary>
    public void MakeVisible()
    {
        EditorInterface.Singleton.SetMainScreenEditor(TabName);
        Visible = true;
    }

    public void Shutdown()
    {
        _bench?.Dispose();
        _bench = null;
    }

    public override void _ExitTree() => Shutdown();

    internal static async Task<T> WithTimeout<T>(Task<T> task, double seconds, Func<T> fallback)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) == task)
        {
            return await task;
        }

        return fallback();
    }
}

/// <summary>A section that loads its data when first shown.</summary>
internal interface IStoreSection
{
    void Shown();
}
#endif
