// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
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
    private GridContainer _list;
    private Label _status;
    private LineEdit _search;
    private OptionButton _kind;
    private StoreEntry[] _entries = Array.Empty<StoreEntry>();
    private bool _busy;
    private readonly Dictionary<string, Task<byte[]>> _previews = new();
    private static readonly Color Gold = new("dfbb77"), Muted = new("abb5ac");
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
        var backdrop = new ColorRect { Color = new Color("141917"), MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(backdrop); backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer(); backdrop.AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 28);
        margin.Theme = BuildTheme();
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 14); margin.AddChild(column);
        var bar = new HBoxContainer(); column.AddChild(bar);
        var brand = Text("GODOTUO  /  COMMUNITY COLLECTION", 13, Gold); brand.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; bar.AddChild(brand);
        var refresh = new Button { Text = "Refresh" }; bar.AddChild(refresh); refresh.Pressed += () => _ = Refresh();
        var close = new Button { Text = "Close" }; bar.AddChild(close); close.Pressed += QueueFree;
        column.AddChild(Text("Make the world your own.", 32, new Color("eeeade")));
        column.AddChild(Text("Backgrounds, sounds, themes and presets for your next adventure.", 15, Muted));
        var filters = new HBoxContainer(); column.AddChild(filters);
        _search = new LineEdit { PlaceholderText = "Search packs or creators…", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 42) }; filters.AddChild(_search);
        _kind = new OptionButton(); foreach (string title in new[] { "All kinds", "Backgrounds", "Themes", "Sounds", "Profile presets" }) _kind.AddItem(title);
        filters.AddChild(_kind); _search.TextChanged += _ => Render(); _kind.ItemSelected += _ => Render();
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; column.AddChild(scroll);
        _list = new GridContainer { Columns = GetViewport().GetVisibleRect().Size.X >= 950 ? 2 : 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("h_separation", 14); _list.AddThemeConstantOverride("v_separation", 14); scroll.AddChild(_list);
        _status = Text("Loading collection…", 13, Muted); column.AddChild(_status);
        column.AddChild(Text("After installing: reopen Options, choose your background, then Apply.", 12, Muted));
        _ = Refresh();
    }

    private static Label Text(string value, int size, Color color)
    {
        var label = new Label { Text = value, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static StyleBoxFlat Box(string color, string border, int padding = 12)
    {
        return new StyleBoxFlat { BgColor = new Color(color), BorderColor = new Color(border),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding };
    }

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 15 };
        foreach (string control in new[] { "Button", "OptionButton", "LineEdit" })
        {
            theme.SetStylebox("normal", control, Box("202922", "455342"));
            theme.SetStylebox("hover", control, Box("303d2e", "b4a16b"));
            theme.SetStylebox("pressed", control, Box("3f4931", "dfbb77"));
            theme.SetStylebox("focus", control, new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = Gold, BorderWidthBottom = 2 });
            theme.SetColor("font_color", control, new Color("eeeade"));
        }
        return theme;
    }

    private async Task Preview(StoreEntry entry, TextureRect target)
    {
        try
        {
            string key = entry.Manifest.Id + "/" + entry.Manifest.Version;
            if (!_previews.TryGetValue(key, out var task)) _previews[key] = task = _client.FetchPreview(entry, _cancel.Token);
            byte[] bytes = await task;
            if (!IsInsideTree() || !GodotObject.IsInstanceValid(target) || !target.IsInsideTree()) return;
            using var image = new Image();
            string extension = System.IO.Path.GetExtension(entry.Manifest.Preview).ToLowerInvariant();
            Error error = extension == ".png" ? image.LoadPngFromBuffer(bytes) : extension == ".webp" ? image.LoadWebpFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
            if (error == Error.Ok && image.GetWidth() <= 4096 && image.GetHeight() <= 4096) target.Texture = ImageTexture.CreateFromImage(image);
        }
        catch (Exception) { /* A missing preview must not prevent installation. */ }
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _entries = (await _client.FetchIndex(_cancel.Token)).ToArray();
            if (!IsInsideTree()) return;
            _status.Text = $"{_entries.Length} packs in the collection · {_client.Installed().Count} installed";
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
            .OrderByDescending(p => installed.Any(m => m.Id == p.Manifest.Id && m.Version == p.Manifest.Version))
            .ThenBy(p => p.Manifest.Title).ThenByDescending(p => StorePack.Version(p.Manifest.Version)))
        {
            var m = entry.Manifest;
            var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; card.AddThemeStyleboxOverride("panel", Box("202922", "354039", 16)); _list.AddChild(card);
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 18); card.AddChild(row);
            var preview = new TextureRect { CustomMinimumSize = new Vector2(144, 140), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
            row.AddChild(preview); _ = Preview(entry, preview);
            var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(info);
            info.AddChild(Text(m.Kind.ToUpperInvariant().Replace('-', ' ') + "  ·  " + m.Licence, 11, Gold));
            info.AddChild(Text(m.Title, 22, new Color("eeeade")));
            info.AddChild(Text("By " + m.Author, 12, Muted));
            bool present = installed.Any(p => p.Id == m.Id && p.Version == m.Version);
            bool compatible = m.MinProfileVersion <= PlatformDefaults.CurrentVersion;
            bool update = installed.Any(p => p.Id == m.Id && StorePack.Version(p.Version) < StorePack.Version(m.Version));
            info.AddChild(Text("v" + m.Version + (present ? "  ·  Installed ✓" : update ? "  ·  Update available" : $"  ·  {entry.Size / 1048576.0:0.0} MB"), 12, present ? new Color("b5d69b") : Muted));
            var action = new Button { Text = present ? "Uninstall" : !compatible ? "Needs newer GUO" : update ? "Update" : "Install", Disabled = _busy || (!present && !compatible) };
            action.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            info.AddChild(action); action.Pressed += () => _ = Change(entry, present);
        }
        // Installed packs remain removable even if a publisher delists them.
        foreach (var m in installed.Where(m => !_entries.Any(p => p.Manifest.Id == m.Id && p.Manifest.Version == m.Version)))
        {
            var button = new Button { Text = $"Uninstall {m.Title} {m.Version} (not in collection)", Disabled = _busy };
            _list.AddChild(button); button.Pressed += () => _ = Change(new StoreEntry { Manifest = m }, true);
        }
        if (_list.GetChildCount() == 0) _list.AddChild(Text("No matching packs. Try another search or kind.", 18, Muted));
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
