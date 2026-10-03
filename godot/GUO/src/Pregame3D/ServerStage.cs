// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the server list (classic ServerSelectionGump) as
// a panel of the client's own frame art over the dimmed painting.

using System.Collections.Generic;
using Godot;
using GUO.Game.Scenes;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

internal sealed class ServerStage : Stage
{
    private readonly List<IOverlayFocusable> _items = new();
    private readonly List<(Label label, ServerListEntry server)> _labels = new();
    private PanelContainer _panel;
    private ServerListEntry[] _servers;
    private double _pingAt;
    private UiFocus _current;

    public override string Hints => "A  Choose     B  Back";

    public override IEnumerable<IOverlayFocusable> OverlayItems => _items;

    public static bool ProbeServerFocused => PregameScreen.Instance?.Stage is ServerStage s && (s.D.Focus.Current as UiFocus)?.Tag is string t && t.StartsWith("server:");

    public override void Enter()
    {
        D.SetDim(0.45f);
        _servers = Login.Servers;

        if (_servers == null || _servers.Length == 0)
        {
            D.ShowMessage("The shard sent no servers.", () => Login.StepBack());
            return;
        }

        var clilocs = Client.Game.UO.FileManager.Clilocs;
        _panel = Overlay.Card(0x0DAC);
        VBoxContainer col = Overlay.Column(3);
        _panel.AddChild(col);
        col.AddChild(Overlay.Text(clilocs.GetString(1044579) ?? "Select which shard to play on:", UoTheme.Heading));

        // The servers in a scroll when there are many.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseFilter = Control.MouseFilterEnum.Ignore };
        VBoxContainer list = Overlay.Column(2);
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(list);
        col.AddChild(scroll);

        int last = Login.GetServerIndexFromSettings();
        IFocusable focus = null;
        var rows = new List<IFocusable>();

        for (int i = 0; i < _servers.Length; i++)
        {
            ServerListEntry server = _servers[i];
            HBoxContainer h = Overlay.Row(12);
            Label name = Overlay.Text(server.Name, UoTheme.Ink);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            Label ping = Overlay.Text("", UoTheme.Muted);
            h.AddChild(name);
            h.AddChild(ping);
            UiFocus row = Overlay.FrameRow(h, name, "server:" + server.Name);
            row.Pressed = () => Choose(server);
            row.Shown += on =>
            {
                if (on)
                {
                    _current = row;
                    Callable.From(() => { if (GodotObject.IsInstanceValid(scroll)) scroll.EnsureControlVisible(row.Control); }).CallDeferred();
                }
            };
            list.AddChild(row.Control);
            _items.Add(row);
            rows.Add(row);
            _labels.Add((ping, server));

            if (i == last)
            {
                focus = row;
            }
        }

        // The gump's arrows: back, and on with the chosen server.
        HBoxContainer arrows = Overlay.Row(6);
        arrows.Alignment = BoxContainer.AlignmentMode.End;
        var prev = new GumpProp(0x15A1, 0x15A3, 0x15A2, Vector2.Zero) { Tag = "prev", Activated = () => Login.StepBack() };
        var next = new GumpProp(0x15A4, 0x15A6, 0x15A5, Vector2.Zero) { Tag = "next", Activated = () => (_current ?? focus as UiFocus)?.Press() };
        arrows.AddChild(prev.Control);
        arrows.AddChild(next.Control);
        col.AddChild(arrows);
        _items.Add(prev);
        _items.Add(next);

        PadFocus.LinkColumn(rows);
        rows[^1].Down = next;
        next.Up = rows[^1];
        prev.Up = rows[^1];
        PadFocus.LinkRow(new List<IFocusable> { prev, next });

        // A scroll only when the list would not fit; otherwise the rows' own height.
        float rowHeight = UoTheme.Font.GetHeight(UoTheme.FontSize) + 12;
        float room = D.OverlayRoot.Size.Y - 150;
        float need = _servers.Length * (rowHeight + 2);
        scroll.CustomMinimumSize = new Vector2(320, Mathf.Min(need, room));
        scroll.VerticalScrollMode = need > room ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
        D.OverlayRoot.AddChild(_panel);
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Both;
        D.Focus.Set(focus ?? rows[0]);
    }

    public override void Exit()
    {
        D.Focus.Clear();
        _panel?.QueueFree();
        _panel = null;
        _items.Clear();
        _labels.Clear();
    }

    private void Choose(ServerListEntry server)
    {
        GD.Print($"[GUO] pregame3d: server \"{server.Name}\" ({server.Index})");
        Login.SelectServer((byte) server.Index);
    }

    public override void Update(double delta)
    {
        // Ping now and then and show it as the classic list does.
        _pingAt -= delta;

        if (_pingAt > 0)
        {
            return;
        }

        _pingAt = 2.0;

        foreach ((Label label, ServerListEntry server) in _labels)
        {
            server?.DoPing();
            label.Text = server != null && server.Ping >= 0 ? $"{server.Ping} ms" : "";
        }
    }

    public override bool Command(PadCmd cmd)
    {
        switch (cmd)
        {
            case PadCmd.B:
                Login.StepBack();
                return true;

            case PadCmd.Start:
                D.Focus.Current?.Press();
                return true;
        }

        return false;
    }
}
