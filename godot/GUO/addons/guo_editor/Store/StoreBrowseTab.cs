#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GUO.Store;

/// <summary>
/// UO Store, section 1: catalogues, search, pack details, install and remove, into the editor's own
/// store. The catalogue code is the client's: <see cref="StoreClient"/> verifies signatures, hashes
/// and expiry, and <see cref="StoreTrust"/> asks for approval of a key exactly as the game's Store
/// window does (the fingerprint, then a yes).
/// </summary>
[Tool]
public partial class StoreBrowseTab : VBoxContainer, IStoreSection
{
    private static readonly string[] Kinds = { "", "background", "theme", "sound", "profile-preset", "screensaver", "postfx", "razor-script", "content" };

    private StoreView _view;
    private OptionButton _source, _kind;
    private LineEdit _address, _search;
    private Button _remove, _install, _removePack, _refresh;
    private VBoxContainer _approvals;
    private ItemList _list;
    private RichTextLabel _details;
    private TextureRect _preview;
    private Label _status;
    private bool _firstShown, _busy;
    private StoreEntry[] _entries = Array.Empty<StoreEntry>();
    private readonly List<StoreEntry> _shown = new();
    private readonly List<StoreCatalogueRecord> _sources = new();
    private readonly List<StoreTrustRequired> _pending = new();
    private readonly Dictionary<string, JsonNode> _verdicts = new();
    private readonly List<string> _log = new();

    internal void Attach(StoreView view) => _view = view;

    private StoreBench Bench => _view.Bench;

    // ---- what the smoke check reads ------------------------------------------------------------------------

    public int EntryCount => _entries.Length;

    public int ShownCount => _shown.Count;

    public string Status => _status?.Text ?? "";

    public int PendingApprovals => _pending.Count;

    public string Details => _details?.GetParsedText() ?? "";

    public string PendingFingerprint => _pending.FirstOrDefault()?.Fingerprint;

    public bool IsBusy => _busy;

    public void Shown()
    {
        if (_firstShown)
        {
            return;
        }

        _firstShown = true;
        _ = RefreshAsync();
    }

    public override void _Ready()
    {
        var top = new HBoxContainer();
        AddChild(top);
        top.AddChild(new Label { Text = "Catalogue" });
        _source = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        _source.ItemSelected += _ => Render();
        top.AddChild(_source);
        _address = new LineEdit { PlaceholderText = "Add a catalogue: https://packs.example.com/", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddChild(_address);
        var add = new Button { Text = "Add" };
        add.Pressed += AddCatalogue;
        top.AddChild(add);
        _remove = new Button { Text = "Remove catalogue", Disabled = true };
        _remove.Pressed += RemoveCatalogue;
        top.AddChild(_remove);
        _refresh = new Button { Text = "Refresh" };
        _refresh.Pressed += () => _ = RefreshAsync();
        top.AddChild(_refresh);

        _approvals = new VBoxContainer();
        AddChild(_approvals);

        var filters = new HBoxContainer();
        AddChild(filters);
        _search = new LineEdit { PlaceholderText = "Search packs, creators or ids", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => Render();
        filters.AddChild(_search);
        _kind = new OptionButton();
        foreach (string k in new[] { "All kinds", "Backgrounds", "Themes", "Sounds", "Profile presets", "Screensavers", "Screen effects", "Razor scripts", "Game content" })
        {
            _kind.AddItem(k);
        }

        _kind.ItemSelected += _ => Render();
        filters.AddChild(_kind);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _list = new ItemList { CustomMinimumSize = new Vector2(420, 0), TextureFilter = TextureFilterEnum.Nearest, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.ItemSelected += i => _ = ShowDetails((int)i);
        split.AddChild(_list);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(380, 0) };
        split.AddChild(right);
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 150),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        right.AddChild(_preview);
        _details = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true, FitContent = false };
        right.AddChild(_details);
        var row = new HBoxContainer();
        right.AddChild(row);
        _install = new Button { Text = "Install", Disabled = true, TooltipText = "Installs the pack and the exact versions it depends on into the editor's store. Installing is inert: nothing is applied." };
        _install.Pressed += () => _ = InstallSelectedAsync();
        row.AddChild(_install);
        _removePack = new Button { Text = "Remove", Disabled = true };
        _removePack.Pressed += () => _ = RemoveSelectedAsync();
        row.AddChild(_removePack);

        _status = new Label { Text = "Open this tab to load the catalogues.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_status);
        AddChild(new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "Installed packs sit in the editor's own store as inert files. UO Assets and UO World read the client's files plus the world "
                + "project's overlay; the game's content mount is startup-only and needs a deployment lock, so an installed pack does not show "
                + "there live. To see a pack in a running client, deploy it on the Server content tab and use it for Start clients.",
        });
    }

    // ---- catalogues ---------------------------------------------------------------------------------------

    public async Task RefreshAsync()
    {
        if (_busy || _view?.Bench == null)
        {
            return;
        }

        _busy = true;
        try
        {
            _entries = Array.Empty<StoreEntry>();
            _pending.Clear();
            foreach (Node child in _approvals.GetChildren())
            {
                _approvals.RemoveChild(child);
                child.QueueFree();
            }

            int picked = _source.Selected;
            _sources.Clear();
            _sources.AddRange(Bench.Sources());
            _source.Clear();
            _source.AddItem("All catalogues");
            foreach (StoreCatalogueRecord s in _sources)
            {
                _source.AddItem(s.Title ?? new Uri(s.Url).Host);
            }

            _source.Selected = picked >= 0 && picked < _source.ItemCount ? picked : 0;
            _status.Text = $"Connecting to {_sources.Count} catalogue(s)...";
            Render();
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(Bench.Cancel.Token);
            connection.CancelAfter(TimeSpan.FromSeconds(15));
            StoreBench bench = Bench;
            var fetches = _sources.Select(async s =>
            {
                using StoreClient client = bench.NewClient(s.Url);
                try
                {
                    return (s, (IReadOnlyList<StoreEntry>)await client.FetchIndex(connection.Token), (Exception)null, client.CatalogueSigned);
                }
                catch (Exception e)
                {
                    return (s, (IReadOnlyList<StoreEntry>)Array.Empty<StoreEntry>(), e, false);
                }
            }).ToArray();
            var results = await Task.WhenAll(fetches);
            if (!IsInsideTree())
            {
                return;
            }

            var merged = new List<StoreEntry>();
            var lines = new List<string>();
            foreach (var (s, entries, error, signed) in results)
            {
                string title = s.Title ?? new Uri(s.Url).Host;
                if (error is StoreTrustRequired trust)
                {
                    Approval(trust);
                    lines.Add(title + ": waiting for your approval");
                    continue;
                }

                if (error != null)
                {
                    lines.Add(title + ": " + (error is System.Net.Http.HttpRequestException ? "unreachable" : error is TaskCanceledException ? "timed out" : error.Message));
                    continue;
                }

                foreach (StoreEntry e in entries)
                {
                    if (!merged.Any(m => m.Manifest.Id == e.Manifest.Id && m.Manifest.Version == e.Manifest.Version))
                    {
                        merged.Add(e);
                    }
                }

                lines.Add($"{title}: {entries.Count} pack(s), {(signed ? "signed" : "unsigned")}");
            }

            _entries = merged.ToArray();
            _status.Text = $"{_entries.Length} packs, {InstalledNow().Count} installed.  " + string.Join("  |  ", lines);
        }
        catch (Exception e)
        {
            if (IsInsideTree())
            {
                _status.Text = "Could not load the catalogues: " + e.Message;
            }
        }
        finally
        {
            _busy = false;
            if (IsInsideTree())
            {
                Render();
            }
        }
    }

    private IReadOnlyList<StoreManifest> InstalledNow()
    {
        using StoreClient client = Bench.NewClient("http://127.0.0.1:1/");
        return client.Installed();
    }

    private void Approval(StoreTrustRequired trust)
    {
        _pending.Add(trust);
        var card = new PanelContainer();
        _approvals.AddChild(card);
        var col = new VBoxContainer();
        card.AddChild(col);
        col.AddChild(new Label { Text = trust.KeyChanged ? "This catalogue's key changed" : "Approve a new catalogue?", ThemeTypeVariation = "HeaderSmall" });
        col.AddChild(new Label { Text = $"\"{trust.Pending.Title}\" at {trust.Pending.Url}", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        col.AddChild(new Label
        {
            Text = "Key fingerprint " + trust.Fingerprint + (trust.KeyChanged
                ? ". Approve only if the catalogue's owner announced a new key; otherwise someone else may be answering at this address."
                : ". Compare it with the one the catalogue's owner publishes. Approving lists its packs; nothing installs on its own."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var row = new HBoxContainer();
        col.AddChild(row);
        var approve = new Button { Text = trust.KeyChanged ? "Approve the new key" : "Approve" };
        approve.Pressed += () => ApproveAndRefresh(trust);
        row.AddChild(approve);
        var dismiss = new Button { Text = "Not now" };
        dismiss.Pressed += () =>
        {
            _pending.Remove(trust);
            _approvals.RemoveChild(card);
            card.QueueFree();
        };
        row.AddChild(dismiss);
    }

    private void ApproveAndRefresh(StoreTrustRequired trust)
    {
        try
        {
            Bench.Trust.Approve(trust.Pending);
            _ = RefreshAsync();
        }
        catch (Exception e)
        {
            _status.Text = "Could not approve: " + e.Message;
        }
    }

    /// <summary>The same yes the Approve button gives, for the first pending key (the smoke check).</summary>
    public void ApproveFirstPending()
    {
        if (_pending.Count > 0)
        {
            ApproveAndRefresh(_pending[0]);
        }
    }

    public void AddCatalogueUrl(string url)
    {
        try
        {
            string added = Bench.Trust.Add(url);
            _address.Text = "";
            _status.Text = "Added " + added + ". Its packs are listed once you approve its key.";
            _ = RefreshAsync();
        }
        catch (Exception e)
        {
            _status.Text = "Could not add that catalogue: " + e.Message;
        }
    }

    private void AddCatalogue() => AddCatalogueUrl(_address.Text);

    private void RemoveCatalogue()
    {
        int index = _source.Selected - 1;
        if (index < 0 || index >= _sources.Count || _sources[index].Official)
        {
            return;
        }

        try
        {
            Bench.Trust.Remove(_sources[index].Url);
            _source.Selected = 0;
            _ = RefreshAsync();
        }
        catch (Exception e)
        {
            _status.Text = "Could not remove that catalogue: " + e.Message;
        }
    }

    // ---- the list and details ---------------------------------------------------------------------------

    public void SetSearch(string text, int kindIndex = 0)
    {
        _search.Text = text;
        _kind.Selected = kindIndex;
        Render();
    }

    private void Render()
    {
        if (_list == null || _view?.Bench == null)
        {
            return;
        }

        IReadOnlyList<StoreManifest> installed = InstalledNow();
        string query = _search.Text ?? "";
        int sourceIndex = _source.Selected - 1;
        string only = sourceIndex >= 0 && sourceIndex < _sources.Count ? _sources[sourceIndex].Url : null;
        _remove.Disabled = only == null || _sources[sourceIndex].Official;
        _shown.Clear();
        _list.Clear();
        IEnumerable<StoreEntry> query2 = _entries.Where(p =>
            (only == null || p.CatalogueUrl == only)
            && (_kind.Selected == 0 || p.Manifest.Kind == Kinds[_kind.Selected])
            && (p.Manifest.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Manifest.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => installed.Any(m => m.Id == p.Manifest.Id && m.Version == p.Manifest.Version))
            .ThenBy(p => p.Manifest.Title);
        foreach (StoreEntry e in query2)
        {
            _shown.Add(e);
            bool present = installed.Any(m => m.Id == e.Manifest.Id && m.Version == e.Manifest.Version);
            _list.AddItem($"{(present ? "[installed] " : "")}{e.Manifest.Title}  v{e.Manifest.Version}  ({e.Manifest.Kind}, {(e.Signed ? "signed" : "unsigned")})");
        }

        // Installed packs stay removable even when no catalogue lists them now.
        foreach (StoreManifest m in installed.Where(m => !_entries.Any(p => p.Manifest.Id == m.Id && p.Manifest.Version == m.Version)
            && (query.Length == 0 || m.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            _shown.Add(new StoreEntry { Manifest = m });
            _list.AddItem($"[installed] {m.Title}  v{m.Version}  ({m.Kind}, not in the catalogues)");
        }

        _install.Disabled = true;
        _removePack.Disabled = true;
        _details.Text = _shown.Count == 0 ? "No matching packs. Try another search or kind, or refresh." : "Pick a pack to see what it is, what it needs and whether it passes the content policy.";
    }

    public bool Select(string id)
    {
        int i = _shown.FindIndex(e => e.Manifest.Id == id);
        if (i < 0)
        {
            return false;
        }

        _list.Select(i);
        _ = ShowDetails(i);
        return true;
    }

    private string Key(StoreManifest m) => m.Id + "@" + m.Version;

    private async Task ShowDetails(int index)
    {
        if (index < 0 || index >= _shown.Count)
        {
            return;
        }

        StoreEntry e = _shown[index];
        StoreManifest m = e.Manifest;
        IReadOnlyList<StoreManifest> installed = InstalledNow();
        bool present = installed.Any(x => x.Id == m.Id && x.Version == m.Version);
        if (present && !_verdicts.ContainsKey(Key(m)))
        {
            _verdicts[Key(m)] = await Bench.InspectInstalledAsync(m.Id, m.Version);
        }

        if (!IsInsideTree() || index >= _shown.Count || _shown[index] != e)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[b]{Esc(m.Title)}[/b]  v{Esc(m.Version)}");
        sb.AppendLine($"Kind: {m.Kind}" + (m.Kind == "content" ? $"   Target: {m.Target}   (installs inert; deployed on the Server content tab)" : ""));
        sb.AppendLine($"Author: {Esc(m.Author)}   Licence: {m.Licence}   Needs GUO profile {m.MinProfileVersion}+");
        sb.AppendLine(e.CatalogueTitle == null ? "Source: installed here" : $"Listed by {Esc(e.CatalogueTitle)}: {(e.Signed ? "catalogue signature verified, pack hash pinned by the signed index" : "unsigned catalogue (this computer or LAN only)")}"
            + (string.IsNullOrEmpty(e.Provenance) ? "" : $"\nProvenance: {Esc(e.Provenance)}"));
        if (e.Size > 0)
        {
            sb.AppendLine($"Size: {e.Size / 1024.0 / 1024.0:0.0} MB");
        }

        if (m.Dependencies is { Count: > 0 })
        {
            sb.AppendLine("Depends on: " + string.Join(", ", m.Dependencies.Select(d =>
                $"{d.Key}@{d.Value}" + (installed.Any(x => x.Id == d.Key && x.Version == d.Value) ? " (installed)" : _entries.Any(x => x.Manifest.Id == d.Key && x.Manifest.Version == d.Value) ? " (in the catalogue)" : " (NOT AVAILABLE)"))));
        }
        else
        {
            sb.AppendLine("Depends on: nothing");
        }

        if (m.Components is { Count: > 0 })
        {
            sb.AppendLine("Components: " + string.Join(", ", m.Components.Select(c => $"{c.Id} ({c.Type}, {c.Target})")));
        }

        if (present)
        {
            sb.AppendLine("Installed: yes, files verified against their hashes");
            JsonNode v = _verdicts.GetValueOrDefault(Key(m));
            sb.AppendLine("Content policy (automatic): [b]" + StoreBench.VerdictLine(v) + "[/b]");
            if (v?["policy"]?["findings"] is JsonArray findings)
            {
                foreach (JsonNode f in findings)
                {
                    sb.AppendLine($"  {f["level"]}: {Esc((string)f["text"])}");
                }

                sb.AppendLine((string)v["policy"]["note"]);
            }
        }
        else
        {
            sb.AppendLine("Content policy (automatic): checked when installed. It cannot see whether art came from the client's files; the catalogue's reviewers do.");
        }

        _details.Text = sb.ToString();
        _install.Disabled = _busy || present || m.MinProfileVersion > GUO.Configuration.PlatformDefaults.CurrentVersion || e.CatalogueUrl == null;
        _removePack.Disabled = _busy || !present;
        _ = ShowPreview(e);
    }

    private static string Esc(string s) => (s ?? "").Replace("[", "[lb]");

    private async Task ShowPreview(StoreEntry e)
    {
        _preview.Texture = null;
        if (e.CatalogueUrl == null || e.PreviewUri == null)
        {
            return;
        }

        try
        {
            using StoreClient client = Bench.NewClient(e.CatalogueUrl);
            byte[] bytes = await client.FetchPreview(e, Bench.Cancel.Token);
            if (!IsInsideTree())
            {
                return;
            }

            using var image = new Image();
            string ext = System.IO.Path.GetExtension(e.Manifest.Preview).ToLowerInvariant();
            Error err = ext == ".png" ? image.LoadPngFromBuffer(bytes) : ext == ".webp" ? image.LoadWebpFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
            if (err == Error.Ok && image.GetWidth() <= 4096 && image.GetHeight() <= 4096)
            {
                _preview.Texture = ImageTexture.CreateFromImage(image);
            }
        }
        catch (Exception)
        {
            // A missing preview never blocks an install.
        }
    }

    private int SelectedIndex => _list.GetSelectedItems().FirstOrDefault(-1);

    public async Task InstallSelectedAsync()
    {
        int i = SelectedIndex;
        if (_busy || i < 0 || i >= _shown.Count || _shown[i].CatalogueUrl == null)
        {
            return;
        }

        StoreEntry entry = _shown[i];
        _busy = true;
        _install.Disabled = true;
        try
        {
            _status.Text = "Installing " + entry.Manifest.Title + "...";
            using StoreClient client = Bench.NewClient(entry.CatalogueUrl);
            await client.InstallWithDependencies(entry, _entries, Bench.Cancel.Token);
            Bench.RememberSource(entry.Manifest.Id, entry.Manifest.Version, entry.CatalogueUrl);
            foreach (KeyValuePair<string, string> d in entry.Manifest.Dependencies ?? new())
            {
                StoreEntry dep = _entries.FirstOrDefault(x => x.Manifest.Id == d.Key && x.Manifest.Version == d.Value);
                if (dep != null)
                {
                    Bench.RememberSource(d.Key, d.Value, dep.CatalogueUrl);
                }
            }

            _verdicts.Remove(Key(entry.Manifest));
            if (IsInsideTree())
            {
                _status.Text = $"Installed and verified {entry.Manifest.Title} {entry.Manifest.Version} (and what it needs) into the editor's store.";
            }
        }
        catch (Exception e)
        {
            if (IsInsideTree())
            {
                _status.Text = "Install failed: " + e.Message;
            }
        }
        finally
        {
            _busy = false;
            if (IsInsideTree())
            {
                int keep = i;
                Render();
                if (keep < _list.ItemCount)
                {
                    _list.Select(keep);
                    await ShowDetails(keep);
                }
            }
        }
    }

    public async Task RemoveSelectedAsync()
    {
        int i = SelectedIndex;
        if (_busy || i < 0 || i >= _shown.Count)
        {
            return;
        }

        StoreManifest m = _shown[i].Manifest;
        try
        {
            using StoreClient client = Bench.NewClient("http://127.0.0.1:1/");
            client.Uninstall(m.Id, m.Version);
            _verdicts.Remove(Key(m));
            _status.Text = $"Removed {m.Title} {m.Version} from the editor's store.";
        }
        catch (Exception e)
        {
            _status.Text = "Could not remove: " + e.Message;
        }

        Render();
        await Task.CompletedTask;
    }

    public bool IsInstalled(string id) => InstalledNow().Any(m => m.Id == id);

    internal IReadOnlyList<StoreManifest> Installed() => InstalledNow();
}
#endif
