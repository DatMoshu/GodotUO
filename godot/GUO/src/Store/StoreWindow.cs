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
    private LineEdit _address;
    private OptionButton _kind;
    private StoreEntry[] _entries = Array.Empty<StoreEntry>();
    private bool _busy;
    private float _ui = 1f;
    private ScrollContainer _scroll;
    private readonly Dictionary<string, Task<byte[]>> _previews = new();
    private static readonly Color Gold = new("dfbb77"), Muted = new("abb5ac");
    private readonly CancellationTokenSource _cancel = new();
    private readonly string[] _kinds = { "", "background", "theme", "sound", "profile-preset", "screensaver", "postfx", "content" };

    /// <summary>Whether the window is up; GameController then leaves input to its controls.</summary>
    public static bool IsOpen => GodotObject.IsInstanceValid(_open);

    public static void Open()
    {
        if (GodotObject.IsInstanceValid(_open)) return;
        _open = new StoreWindow();
        Client.Game.AddChild(_open);
    }

    public override void _Ready()
    {
        Layer = 100;
        _client = StoreOptions.CreateClient(StoreAddress.Default);
        // On a touch screen the window is drawn at the client's screen scale
        // and laid out in its logical pixels, so its text and buttons are the
        // size the game's own gumps are there, and big enough for a finger.
        _ui = GUO.Input.Touch.TouchInput.Enabled ? Math.Max(1f, Client.Game?.DpiScale ?? 1f) : 1f;
        Scale = new Vector2(_ui, _ui);
        Vector2 logical = GetViewport().GetVisibleRect().Size / _ui;
        var backdrop = new ColorRect { Color = new Color("141917"), MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(backdrop);
        if (_ui > 1f) { backdrop.Position = Vector2.Zero; backdrop.Size = logical; }
        else backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // A short window (a phone at 2x is 540 logical px tall) keeps only its
        // controls above the list: the Close/Refresh row, the address row and
        // the filters, three 48 px rows, with tighter margins. Brand, title,
        // tagline and the closing hint go, so the collection gets the height.
        bool compact = logical.Y < 720;
        var margin = new MarginContainer(); backdrop.AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, compact ? 12 : 28);
        margin.Theme = BuildTheme();
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", compact ? 8 : 14); margin.AddChild(column);
        var bar = new HBoxContainer(); column.AddChild(bar);
        var brand = Text(compact ? "" : "GODOTUO  /  COMMUNITY COLLECTION", 13, Gold); brand.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; bar.AddChild(brand);
        var refresh = Touchable(new Button { Text = "Refresh" }); bar.AddChild(refresh); refresh.Pressed += () => _ = Refresh();
        var close = Touchable(new Button { Text = "Close" }); bar.AddChild(close); close.Pressed += QueueFree;
        if (!compact)
        {
            column.AddChild(Text("Make the world your own.", 32, new Color("eeeade")));
            column.AddChild(Text("Backgrounds, sounds, themes and presets for your next adventure.", 15, Muted));
        }
        var addressRow = new HBoxContainer(); column.AddChild(addressRow);
        var addressLabel = Text("Store address", 14, Gold);
        addressLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        addressLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        addressRow.AddChild(addressLabel);
        _address = new LineEdit { PlaceholderText = "https://store.example.com/", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 48) };
        Touchable(_address); addressRow.AddChild(_address);
        var connect = Touchable(new Button { Text = "Save & connect", CustomMinimumSize = new Vector2(0, 48) }); addressRow.AddChild(connect);
        connect.Pressed += () => _ = Refresh(true); _address.TextSubmitted += text => { _ = Refresh(true); };
        var filters = new HBoxContainer(); column.AddChild(filters);
        _search = new LineEdit { PlaceholderText = "Search packs or creators…", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 42) }; Touchable(_search); filters.AddChild(_search);
        _kind = Touchable(new OptionButton()); foreach (string title in new[] { "All kinds", "Backgrounds", "Themes", "Sounds", "Profile presets", "Screensavers", "Screen effects", "Game content" }) _kind.AddItem(title);
        filters.AddChild(_kind); _search.TextChanged += _ => Render(); _kind.ItemSelected += _ => Render();
        var scroll = _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; column.AddChild(scroll);
        _list = new GridContainer { Columns = logical.X >= 950 ? 2 : 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("h_separation", 14); _list.AddThemeConstantOverride("v_separation", 14); scroll.AddChild(_list);
        _status = Text("Loading collection…", 13, Muted); column.AddChild(_status);
        if (!compact) column.AddChild(Text("After installing: reopen Options, choose your background, then Apply.", 12, Muted));
        try { _address.Text = StoreOptions.Url; _ = Refresh(); }
        catch (Exception) { _status.Text = "Could not read this profile's store address. Enter an address and Save & connect."; }
    }

    /// <summary>A control at least a finger's size on a touch screen (48 logical px tall, 7 mm on the Thor); unchanged elsewhere.</summary>
    private T Touchable<T>(T control) where T : Control
    {
        if (_ui > 1f) control.CustomMinimumSize = new Vector2(Math.Max(control.CustomMinimumSize.X, 96), Math.Max(control.CustomMinimumSize.Y, 48));
        return control;
    }

    /// <summary>
    /// A finger dragged anywhere over the list scrolls it. Godot's own
    /// ScrollContainer drag did not scroll on the Thor, so the screen drag
    /// is applied here; the event still goes on to the controls.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventScreenDrag drag && _scroll != null && _scroll.GetGlobalRect().HasPoint(drag.Position / _ui))
        {
            _scroll.ScrollVertical -= (int)Math.Round(drag.Relative.Y / _ui);
        }
    }

    /// <summary>A pack's size as the web page shows it: KB under 1 MB, else MB to one place.</summary>
    private static string SizeText(long bytes) =>
        bytes < 1048576 ? $"{Math.Max(1, (long)Math.Round(bytes / 1024.0))} KB" : $"{bytes / 1048576.0:0.0} MB";

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

    private async Task Refresh(bool saveAddress = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            string url = StoreAddress.Normalize(_address.Text);
            if (saveAddress) StoreAddress.Save(ProfileManager.CurrentProfile == null ? null : ProfileManager.ProfilePath, url);
            _client.Dispose(); _client = StoreOptions.CreateClient(url);
            _previews.Clear(); _entries = Array.Empty<StoreEntry>();
            _address.Text = url; _status.Text = "Connecting to store…";
            Render();
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            connection.CancelAfter(TimeSpan.FromSeconds(15));
            _entries = (await _client.FetchIndex(connection.Token)).ToArray();
            if (!IsInsideTree()) return;
            _status.Text = $"{_entries.Length} packs in the collection · {_client.Installed().Count} installed";
        }
        catch (System.Net.Http.HttpRequestException) { if (IsInsideTree()) _status.Text = "Store unreachable. Check the address, server and network connection, then Refresh."; }
        catch (TaskCanceledException) { if (IsInsideTree()) _status.Text = "Store connection timed out. Check the address and try Refresh."; }
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
            // Pass, not Stop: a finger dragged over a card has to reach the
            // ScrollContainer, which is what scrolls the list on a touch screen.
            var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass }; card.AddThemeStyleboxOverride("panel", Box("202922", "354039", 16)); _list.AddChild(card);
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 18); card.AddChild(row);
            var preview = new TextureRect { CustomMinimumSize = new Vector2(144, 140), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Pass };
            row.AddChild(preview); _ = Preview(entry, preview);
            var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(info);
            info.AddChild(Text(m.Kind.ToUpperInvariant().Replace('-', ' ') + "  ·  " + m.Licence, 11, Gold));
            if (m.Kind == "content")
                info.AddChild(Text(m.Target.ToUpperInvariant() + " · " + string.Join(", ", m.Components.Select(c => c.Type).Distinct()) + " · installs inactive", 11, Muted));
            info.AddChild(Text(m.Title, 22, new Color("eeeade")));
            info.AddChild(Text("By " + m.Author, 12, Muted));
            bool present = installed.Any(p => p.Id == m.Id && p.Version == m.Version);
            bool compatible = m.MinProfileVersion <= PlatformDefaults.CurrentVersion;
            bool update = installed.Any(p => p.Id == m.Id && StorePack.Version(p.Version) < StorePack.Version(m.Version));
            info.AddChild(Text("v" + m.Version + (present ? "  ·  Installed ✓" : update ? "  ·  Update available" : $"  ·  {SizeText(entry.Size)}"), 12, present ? new Color("b5d69b") : Muted));
            var action = Touchable(new Button { Text = present ? "Uninstall" : !compatible ? "Needs newer GUO" : update ? "Update" : "Install", Disabled = _busy || (!present && !compatible) });
            action.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            info.AddChild(action); action.Pressed += () => _ = Change(entry, present);
        }
        // Installed packs remain removable even if a publisher delists them.
        foreach (var m in installed.Where(m => !_entries.Any(p => p.Manifest.Id == m.Id && p.Manifest.Version == m.Version)))
        {
            var button = Touchable(new Button { Text = $"Uninstall {m.Title} {m.Version} (not in collection)", Disabled = _busy });
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
            if (IsInsideTree()) _status.Text = remove ? _client.LastUninstallMessage : "Installed and verified. Reopen Options and select the background, then Apply.";
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
