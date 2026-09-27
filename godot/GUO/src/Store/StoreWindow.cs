// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;

namespace GUO.Store;

/// <summary>Store UI lives on its own layer; no renderer or bootstrap hooks.</summary>
internal sealed partial class StoreWindow : CanvasLayer
{
    private static StoreWindow _open;
    private StoreClient _client;
    private VBoxContainer _list;
    private Label _status;
    private LineEdit _search;
    private OptionButton _kind;
    private StoreEntry[] _entries = Array.Empty<StoreEntry>();
    private bool _busy;
    private readonly CancellationTokenSource _cancel = new();
    private readonly string[] _kinds = { "", "background", "theme", "sound", "profile-preset" };

    public static void Open()
    {
        if (GodotObject.IsInstanceValid(_open)) return;
        _open = new StoreWindow();
        Client.Game.AddChild(_open);
    }

    public override void _Ready()
    {
        Layer = 100;
        _client = StoreOptions.CreateClient();
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, .8f), MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(backdrop); backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer(); backdrop.AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
        var column = new VBoxContainer(); margin.AddChild(column);
        var bar = new HBoxContainer(); column.AddChild(bar);
        bar.AddChild(new Label { Text = "GUO Asset Store", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var refresh = new Button { Text = "Refresh" }; bar.AddChild(refresh); refresh.Pressed += () => _ = Refresh();
        var close = new Button { Text = "Close" }; bar.AddChild(close); close.Pressed += QueueFree;
        column.AddChild(new Label { Text = "Install packs here. Reopen Options to refresh background choices.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        _status = new Label { Text = "Loading collection…", AutowrapMode = TextServer.AutowrapMode.WordSmart }; column.AddChild(_status);
        var filters = new HBoxContainer(); column.AddChild(filters);
        _search = new LineEdit { PlaceholderText = "Search packs", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; filters.AddChild(_search);
        _kind = new OptionButton(); foreach (string title in new[] { "All kinds", "Backgrounds", "Themes", "Sounds", "Profile presets" }) _kind.AddItem(title);
        filters.AddChild(_kind); _search.TextChanged += _ => Render(); _kind.ItemSelected += _ => Render();
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; column.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_list);
        _ = Refresh();
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _entries = (await _client.FetchIndex(_cancel.Token)).ToArray();
            if (!IsInsideTree()) return;
            _status.Text = $"{_entries.Length} packs · {StoreOptions.Url}";
        }
        catch (Exception e) { if (IsInsideTree()) _status.Text = "Could not load collection: " + e.Message; }
        finally { _busy = false; if (IsInsideTree()) Render(); }
    }

    private void Render()
    {
        foreach (Node child in _list.GetChildren()) { _list.RemoveChild(child); child.QueueFree(); }
        var installed = _client.Installed();
        string query = _search.Text;
        foreach (var entry in _entries.Where(p => (_kind.Selected == 0 || p.Manifest.Kind == _kinds[_kind.Selected]) &&
            (p.Manifest.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Manifest.Author.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Manifest.Title).ThenByDescending(p => StorePack.Version(p.Manifest.Version)))
        {
            var m = entry.Manifest;
            var row = new HBoxContainer(); _list.AddChild(row);
            row.AddChild(new Label { Text = $"{m.Title} · {m.Version}\n{m.Kind} · {m.Author} · {m.Licence}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
            bool present = installed.Any(p => p.Id == m.Id && p.Version == m.Version);
            bool compatible = m.MinProfileVersion <= PlatformDefaults.CurrentVersion;
            bool update = installed.Any(p => p.Id == m.Id && StorePack.Version(p.Version) < StorePack.Version(m.Version));
            var action = new Button { Text = present ? "Uninstall" : !compatible ? "Needs newer GUO" : update ? "Update" : "Install", Disabled = _busy || (!present && !compatible) };
            row.AddChild(action); action.Pressed += () => _ = Change(entry, present);
        }
        // Installed packs remain removable even if a publisher delists them.
        foreach (var m in installed.Where(m => !_entries.Any(p => p.Manifest.Id == m.Id && p.Manifest.Version == m.Version)))
        {
            var button = new Button { Text = $"Uninstall {m.Title} {m.Version} (not in collection)", Disabled = _busy };
            _list.AddChild(button); button.Pressed += () => _ = Change(new StoreEntry { Manifest = m }, true);
        }
    }

    private async Task Change(StoreEntry entry, bool remove)
    {
        if (_busy) return;
        _busy = true; Render();
        try
        {
            _status.Text = (remove ? "Removing " : "Installing ") + entry.Manifest.Title + "…";
            if (remove) _client.Uninstall(entry.Manifest.Id, entry.Manifest.Version);
            else await _client.Install(entry, _cancel.Token);
            if (IsInsideTree()) _status.Text = remove ? "Pack removed. Reopen Options to refresh backgrounds." : "Installed and verified. Reopen Options and select the background, then Apply.";
        }
        catch (Exception e) { if (IsInsideTree()) _status.Text = e.Message; }
        finally { _busy = false; if (IsInsideTree()) Render(); }
    }

    public override void _ExitTree()
    {
        _cancel.Cancel(); _client?.Dispose();
        if (_open == this) _open = null;
    }
}
