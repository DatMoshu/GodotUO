#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// The bottom-panel "Logs" dock: server, clients, the client's file logs, the Godot log and any file you
/// add, each tailed read only (<see cref="LogsPanel"/>). Owns worker tasks, which <see cref="Shutdown"/>
/// cancels before an assembly reload.
/// </summary>
[Tool]
public partial class LogsDock : EditorDock
{
    private LogsPanel _panel;
    private bool _ready;

    public LogsPanel Panel => _panel;

    public LogsDock()
    {
        Name = "UOLogs";
        Title = "Logs";
        LayoutKey = "guo_logs";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Vertical | DockLayout.Floating;
        IconName = "Script";
    }

    public override void _Ready()
    {
        if (_ready)
        {
            return;
        }

        _ready = true;
        _panel = new LogsPanel();
        _panel.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_panel);
    }

    /// <summary>Brings a source forward: "server", "client1".."client4", "clientfiles", "godot". False if it does not exist.</summary>
    public bool ShowSource(string key)
    {
        MakeVisible();
        return _panel != null && _panel.Show(key);
    }

    /// <summary>The F3 command "Logs: add file": the dock opens and asks for a file.</summary>
    public void AddFile()
    {
        MakeVisible();
        _panel?.ChooseFile();
    }

    public void Shutdown() => _panel?.Shutdown();
}
#endif
