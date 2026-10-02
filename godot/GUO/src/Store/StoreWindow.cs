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
    private OptionButton _source;
    private Button _remove;
    private VBoxContainer _approvals;
    // The catalogues of the last refresh, in the order the source picker lists them after "All catalogues".
    private readonly List<(string Url, string Title, bool Official)> _sources = new();
    private StoreEntry[] _entries = Array.Empty<StoreEntry>();
    private bool _busy;
    private float _ui = 1f;
    private ScrollContainer _scroll;
    private readonly Dictionary<string, Task<byte[]>> _previews = new();
    private static readonly Color Gold = new("dfbb77"), Muted = new("abb5ac");
    private readonly CancellationTokenSource _cancel = new();
    private readonly string[] _kinds = { "", "background", "theme", "sound", "profile-preset", "screensaver", "postfx", "razor-script", "content" };

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
            column.AddChild(Text("Backgrounds, sounds, themes, presets and Razor scripts for your next adventure.", 15, Muted));
        }
        // Catalogues (ADR-0026): the official one, any the player added, and this
        // character's own store address. Packs from all of them are listed together.
        var addressRow = new HBoxContainer(); column.AddChild(addressRow);
        var addressLabel = Text("Catalogues", 14, Gold);
        addressLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        addressLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        addressRow.AddChild(addressLabel);
        _source = Touchable(new OptionButton { CustomMinimumSize = new Vector2(0, 48) }); addressRow.AddChild(_source);
        _source.ItemSelected += _ => Render();
        _address = new LineEdit { PlaceholderText = "Add a catalogue: https://packs.example.com/", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 48) };
        Touchable(_address); addressRow.AddChild(_address);
        var connect = Touchable(new Button { Text = "Add", CustomMinimumSize = new Vector2(0, 48) }); addressRow.AddChild(connect);
        connect.Pressed += () => _ = Add(); _address.TextSubmitted += text => { _ = Add(); };
        _remove = Touchable(new Button { Text = "Remove", CustomMinimumSize = new Vector2(0, 48), Disabled = true }); addressRow.AddChild(_remove);
        _remove.Pressed += RemoveSelected;
        _approvals = new VBoxContainer(); column.AddChild(_approvals);
        var filters = new HBoxContainer(); column.AddChild(filters);
        _search = new LineEdit { PlaceholderText = "Search packs or creators…", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 42) }; Touchable(_search); filters.AddChild(_search);
        _kind = Touchable(new OptionButton()); foreach (string title in new[] { "All kinds", "Backgrounds", "Themes", "Sounds", "Profile presets", "Screensavers", "Screen effects", "Razor scripts", "Game content" }) _kind.AddItem(title);
        filters.AddChild(_kind); _search.TextChanged += _ => Render(); _kind.ItemSelected += _ => Render();
        var scroll = _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; column.AddChild(scroll);
        _list = new GridContainer { Columns = logical.X >= 950 ? 2 : 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("h_separation", 14); _list.AddThemeConstantOverride("v_separation", 14); scroll.AddChild(_list);
        _status = Text("Loading collection…", 13, Muted); column.AddChild(_status);
        if (!compact) column.AddChild(Text("After installing: reopen Options, choose your background, then Apply.", 12, Muted));
        _ = Refresh();
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

    /// <summary>Every catalogue to list from: the trust list's (official first), then this character's own
    /// store address when it is not already one of them.</summary>
    private List<(string Url, string Title, bool Official)> Sources()
    {
        var list = _client.Trust.Catalogues().Select(r => (r.Url, r.Title ?? new Uri(r.Url).Host, r.Official)).ToList();
        try
        {
            string own = StoreAddress.Normalize(StoreOptions.Url);
            if (list.All(s => s.Url != own)) list.Add((own, own == StoreAddress.Normalize(StoreAddress.Default) ? "This computer" : new Uri(own).Host, false));
        }
        catch (Exception) { /* An unreadable saved address is simply not listed. */ }
        return list;
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _previews.Clear(); _entries = Array.Empty<StoreEntry>();
            foreach (Node child in _approvals.GetChildren()) { _approvals.RemoveChild(child); child.QueueFree(); }
            _sources.Clear(); _sources.AddRange(Sources());
            int picked = _source.Selected;
            _source.Clear(); _source.AddItem("All catalogues");
            foreach (var source in _sources) _source.AddItem(source.Title);
            _source.Selected = picked >= 0 && picked < _source.ItemCount ? picked : 0;
            _status.Text = "Connecting to " + _sources.Count + " catalogue(s)…";
            Render();
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            connection.CancelAfter(TimeSpan.FromSeconds(15));
            // All catalogues at once; each one's failure is its own line, never the whole Store's.
            var fetches = _sources.Select(async source =>
            {
                using var client = StoreOptions.CreateClient(source.Url);
                try { return (source, (IReadOnlyList<StoreEntry>)await client.FetchIndex(connection.Token), (Exception)null, client.CatalogueSigned); }
                catch (Exception e) { return (source, (IReadOnlyList<StoreEntry>)Array.Empty<StoreEntry>(), e, false); }
            }).ToArray();
            var results = await Task.WhenAll(fetches);
            if (!IsInsideTree()) return;
            var merged = new List<StoreEntry>();
            var lines = new List<string>();
            int conflicts = 0;
            foreach (var (source, entries, error, signed) in results)
            {
                if (error is StoreTrustRequired trust) { Approval(trust); lines.Add(source.Title + ": waiting for your approval"); continue; }
                if (error != null) { lines.Add(source.Title + ": " + Reason(error)); continue; }
                // A pack two catalogues list with the same bytes is one pack; different bytes under one
                // id and version cannot both install, so the first catalogue's listing wins.
                foreach (var entry in entries)
                {
                    var same = merged.FirstOrDefault(e => e.Manifest.Id == entry.Manifest.Id && e.Manifest.Version == entry.Manifest.Version);
                    if (same == null) merged.Add(entry);
                    else if (same.Sha256 != entry.Sha256) conflicts++;
                }
                lines.Add($"{source.Title}: {entries.Count} pack(s), {(signed ? "signed" : "unsigned")}");
            }
            _entries = merged.ToArray();
            _status.Text = $"{_entries.Length} packs · {_client.Installed().Count} installed  ·  " + string.Join("  ·  ", lines)
                + (conflicts > 0 ? $"  ·  {conflicts} conflicting listing(s) hidden" : "");
        }
        catch (Exception e) { if (IsInsideTree()) _status.Text = "Could not load the catalogues: " + e.Message; }
        finally { _busy = false; if (IsInsideTree()) Render(); }
    }

    private static string Reason(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException => "unreachable",
        TaskCanceledException => "timed out",
        _ => e.Message,
    };

    /// <summary>A catalogue whose key is not approved: its name, address and key fingerprint, and the choice.</summary>
    private void Approval(StoreTrustRequired trust)
    {
        var card = new PanelContainer(); card.AddThemeStyleboxOverride("panel", Box(trust.KeyChanged ? "3a1f1c" : "232b1f", trust.KeyChanged ? "c0604f" : "b4a16b", 12));
        _approvals.AddChild(card);
        var column = new VBoxContainer(); card.AddChild(column);
        column.AddChild(Text(trust.KeyChanged ? "This catalogue's key changed" : "Approve a new catalogue?", 16, trust.KeyChanged ? new Color("f0a090") : Gold));
        column.AddChild(Text($"\"{trust.Pending.Title}\" at {trust.Pending.Url}", 13, new Color("eeeade")));
        column.AddChild(Text("Key fingerprint " + trust.Fingerprint + (trust.KeyChanged
            ? ". Approve only if the catalogue's owner announced a new key; otherwise someone else may be answering at this address."
            : ". Compare it with the one the catalogue's owner publishes. Approving lists its packs; nothing installs on its own."), 12, Muted));
        var row = new HBoxContainer(); column.AddChild(row);
        var approve = Touchable(new Button { Text = trust.KeyChanged ? "Approve the new key" : "Approve" }); row.AddChild(approve);
        var dismiss = Touchable(new Button { Text = "Not now" }); row.AddChild(dismiss);
        approve.Pressed += () =>
        {
            try { _client.Trust.Approve(trust.Pending); _ = Refresh(); }
            catch (Exception e) { _status.Text = "Could not approve: " + e.Message; }
        };
        dismiss.Pressed += () => { _approvals.RemoveChild(card); card.QueueFree(); };
    }

    private async Task Add()
    {
        try
        {
            string url = _client.Trust.Add(_address.Text);
            _address.Text = "";
            _status.Text = "Added " + url + ". Its packs are listed once you approve its key.";
            await Refresh();
        }
        catch (Exception e) { _status.Text = "Could not add that catalogue: " + e.Message; }
    }

    private void RemoveSelected()
    {
        int index = _source.Selected - 1;
        if (index < 0 || index >= _sources.Count || _sources[index].Official) return;
        try { _client.Trust.Remove(_sources[index].Url); _source.Selected = 0; _ = Refresh(); }
        catch (Exception e) { _status.Text = "Could not remove that catalogue: " + e.Message; }
    }

    private void Render()
    {
        foreach (Node child in _list.GetChildren()) { _list.RemoveChild(child); child.QueueFree(); }
        var installed = _client.Installed();
        string query = _search.Text;
        int sourceIndex = _source.Selected - 1;
        string onlyFrom = sourceIndex >= 0 && sourceIndex < _sources.Count ? _sources[sourceIndex].Url : null;
        _remove.Disabled = onlyFrom == null || _sources[sourceIndex].Official;
        foreach (var entry in _entries.Where(p => (onlyFrom == null || p.CatalogueUrl == onlyFrom) && (_kind.Selected == 0 || p.Manifest.Kind == _kinds[_kind.Selected]) &&
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
            info.AddChild(Text((entry.Signed ? "Listed by " : "Unsigned, from ") + entry.CatalogueTitle
                + (string.IsNullOrEmpty(entry.Provenance) ? "" : "  ·  " + entry.Provenance), 11, entry.Signed ? Muted : new Color("d9a066")));
            bool present = installed.Any(p => p.Id == m.Id && p.Version == m.Version);
            bool compatible = m.MinProfileVersion <= PlatformDefaults.CurrentVersion;
            bool update = installed.Any(p => p.Id == m.Id && StorePack.Version(p.Version) < StorePack.Version(m.Version));
            info.AddChild(Text("v" + m.Version + (present ? "  ·  Installed ✓" : update ? "  ·  Update available" : $"  ·  {SizeText(entry.Size)}"), 12, present ? new Color("b5d69b") : Muted));
            var action = Touchable(new Button { Text = present ? "Uninstall" : !compatible ? "Needs newer GUO" : update ? "Update" : "Install", Disabled = _busy || (!present && !compatible) });
            action.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            info.AddChild(action); action.Pressed += () => _ = Change(entry, present);
            if (present && m.Kind == "content")
            {
                var deploy = Touchable(new Button { Text = "Configure deployment", Disabled = _busy });
                info.AddChild(deploy);
                deploy.Pressed += () =>
                {
                    try { StoreDeploymentWindow.Open(this, m.Id, m.Version); }
                    catch (Exception e) { _status.Text = "Could not review deployment: " + e.Message; }
                };
            }
            if (present && (m.Kind == "razor-script" || m.Components?.Any(c => c.Type == "script" && c.Target == "client") == true))
            {
                var browse = Touchable(new Button { Text = "Browse scripts", Disabled = _busy || !(Client.Game?.UO?.World?.InGame ?? false) });
                info.AddChild(browse);
                browse.Pressed += () =>
                {
                    QueueFree();
                    GUO.Input.Touch.Modern.ModernScripts.ShowStorePack(Client.Game.UO.World, m.Id, m.Version);
                };
                info.AddChild(Text("Preview and add personal copies. Nothing runs automatically.", 12, Muted));
            }
        }
        // Installed packs remain removable even if a publisher delists them.
        foreach (var m in installed.Where(m => !_entries.Any(p => p.Manifest.Id == m.Id && p.Manifest.Version == m.Version)))
        {
            var button = Touchable(new Button { Text = $"Uninstall {m.Title} {m.Version} (not in collection)", Disabled = _busy });
            _list.AddChild(button); button.Pressed += () => _ = Change(new StoreEntry { Manifest = m }, true);
            if (m.Kind == "content")
            {
                var configure = Touchable(new Button { Text = $"Configure {m.Title} {m.Version}", Disabled = _busy });
                _list.AddChild(configure);
                configure.Pressed += () =>
                {
                    try { StoreDeploymentWindow.Open(this, m.Id, m.Version); }
                    catch (Exception e) { _status.Text = "Could not review deployment: " + e.Message; }
                };
            }
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
            else await _client.InstallWithDependencies(entry, _entries, _cancel.Token);
            if (IsInsideTree()) _status.Text = entry.Manifest.Kind == "razor-script"
                ? remove ? "Script pack removed. Personal copies are kept." : "Scripts installed and verified. Log in, then Browse scripts to add personal copies."
                : remove ? _client.LastUninstallMessage : "Installed and verified. Reopen Options and select the background, then Apply.";
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
