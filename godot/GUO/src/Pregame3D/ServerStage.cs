// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the server list (classic ServerSelectionGump) as
// scrolls laid in the chest.

using System.Collections.Generic;
using Godot;
using GUO.Game.Scenes;

namespace GUO.Pregame3D;

internal sealed class ServerStage : Stage
{
    private readonly List<Hotspot> _scrolls = new();
    private readonly List<Label3D> _labels = new();
    private ServerListEntry[] _servers;
    private int _page;
    private double _pingAt;

    public override string Hints => $"A  Choose     B  Back{(Pages > 1 ? "     LB/RB  Page" : "")}";

    private int PerPage => System.Math.Max(1, D.Scene.Layout.ScrollSlots.Count);

    private int Pages => _servers == null ? 0 : (_servers.Length + PerPage - 1) / PerPage;

    public override void Enter()
    {
        D.LidTo(1f, 0.6);
        D.Frame(D.StepPose("servers"), null, 0.8); // aimed first: the labels size for where it looks from
        _servers = Login.Servers;
        int last = _servers == null || _servers.Length == 0 ? 0 : Login.GetServerIndexFromSettings();
        _page = last / PerPage;
        Build(last);
        D.Frame(D.StepPose("servers"), _scrolls, 0.8);
    }

    public override void Exit() => Clear();

    private void Build(int focusIndex)
    {
        Clear();

        if (_servers == null || _servers.Length == 0)
        {
            D.ShowMessage("The shard sent no servers.", () => Login.StepBack());
            return;
        }

        List<Placement> slots = D.Scene.Layout.ScrollSlots;
        int first = _page * PerPage;
        Hotspot focus = null;

        for (int i = first; i < _servers.Length && i < first + PerPage; i++)
        {
            ServerListEntry server = _servers[i];
            Node3D visual = D.Scene.Scroll();
            visual.Transform = slots[i - first].Transform * visual.Transform;
            D.Scene.Root.AddChild(visual);
            Hotspot h = Hotspot.Wrap(visual, "Server" + i, D.Scene.Root);
            h.Activated = () => Choose(server);
            D.AddPickable(h);
            _scrolls.Add(h);
            _labels.Add(D.TextFront(visual, server.Name));

            if (i == focusIndex)
            {
                focus = h;
            }
        }

        D.LinkBySpace(_scrolls);
        D.Focus.Set(focus ?? (_scrolls.Count > 0 ? _scrolls[0] : null));
    }

    private void Clear()
    {
        D.Focus.Clear();

        foreach (Hotspot h in _scrolls)
        {
            D.RemovePickable(h);
            h.QueueFree();
        }

        _scrolls.Clear();
        _labels.Clear();
    }

    private void Choose(ServerListEntry server)
    {
        GD.Print($"[GUO] pregame3d: server \"{server.Name}\" ({server.Index})");
        Login.SelectServer((byte) server.Index);
    }

    public override void Update(double delta)
    {
        if (_servers == null)
        {
            return;
        }

        // Ping the shown servers now and then, and show it as the classic list does.
        _pingAt -= delta;

        if (_pingAt > 0)
        {
            return;
        }

        _pingAt = 2.0;
        int first = _page * PerPage;

        for (int i = 0; i < _labels.Count && first + i < _servers.Length; i++)
        {
            ServerListEntry s = _servers[first + i];

            if (s == null)
            {
                continue;
            }

            s.DoPing();
            _labels[i].Text = s.Ping >= 0 ? $"{s.Name}  {s.Ping}ms" : s.Name;
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

            case PadCmd.LeftShoulder or PadCmd.RightShoulder when Pages > 1:
                _page = (_page + (cmd == PadCmd.LeftShoulder ? Pages - 1 : 1)) % Pages;
                Build(_page * PerPage);
                return true;
        }

        return false;
    }
}
