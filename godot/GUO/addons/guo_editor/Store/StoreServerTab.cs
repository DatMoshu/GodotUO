#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using GUO.Store;

/// <summary>
/// UO Store, section 2 (shard owners): pick one of the run bar's server profiles, pick an installed
/// content pack, give each client component a numeric slot, and deploy. The deployment is
/// tools/shard_content's: the pack and what it needs are installed from its catalogue with the
/// client's own installer, locked with these bindings, and written to the shard as
/// <c>guo/server-content@1</c> (with its identity_hash) and the public descriptor
/// <c>guo/shard-content@1</c>, through the profile's backend adapter. A deployment is one pack plus
/// the exact packs it depends on. The tab shows the tool's log and what each profile has deployed.
/// </summary>
[Tool]
public partial class StoreServerTab : VBoxContainer, IStoreSection
{
    private static readonly string[] Backends = { "modernuo", "servuo", "runuo", "pol", "sphere", "uox3" };

    private StoreView _view;
    private ServerProfiles _profiles;
    private OptionButton _profile, _scripts;
    private Label _profileInfo, _packInfo;
    private ItemList _packs;
    private LineEdit _catalogue, _name, _slots, _sphere;
    private VBoxContainer _rows;
    private HBoxContainer _extra;
    private TextEdit _log;
    private RichTextLabel _deployed;
    private Button _build, _deploy, _roll, _use;
    private bool _busy;
    private readonly List<StoreManifest> _installed = new();
    private readonly Dictionary<string, LineEdit> _bindings = new();
    private readonly Dictionary<string, (string Lock, string Store)> _lastDeploy = new();

    internal void Attach(StoreView view) => _view = view;

    private StoreBench Bench => _view.Bench;

    /// <summary>A profile list to use instead of the run bar's file (the smoke check's scratch shard).</summary>
    internal ServerProfiles ProfilesOverride { get; set; }

    private ServerProfile Current => _profiles?.Servers.ElementAtOrDefault(_profile?.Selected ?? -1);

    private StoreManifest Pack => _packs?.GetSelectedItems().Length > 0 ? _installed.ElementAtOrDefault(_packs.GetSelectedItems()[0]) : null;

    // ---- what the smoke check reads ------------------------------------------------------------------------

    public string Log => _log?.Text ?? "";

    public string DeployedText => _deployed?.GetParsedText() ?? "";

    public int ProfileCount => _profiles?.Servers.Count ?? 0;

    public int BindingCount => _bindings.Count;

    public bool IsBusy => _busy;

    public void Shown()
    {
        if (_profile == null)
        {
            return;
        }

        LoadProfiles();
        LoadPacks();
        _ = RefreshStatusAsync();
    }

    public override void _Ready()
    {
        AddChild(new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "Deploy a content pack to one of your servers. Choose the server profile (the same list as the run bar's), an installed pack and the numeric slots its client components take; "
                + "then build and deploy. The shard's own backend adapter writes native files; stop the server and back up its saves first, and restart it to load the deployment.",
        });
        var row = new HBoxContainer();
        AddChild(row);
        row.AddChild(new Label { Text = "Server profile" });
        _profile = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        _profile.ItemSelected += _ => ProfileChanged();
        row.AddChild(_profile);
        _profileInfo = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        row.AddChild(_profileInfo);
        var reload = new Button { Text = "Reload profiles" };
        reload.Pressed += Shown;
        row.AddChild(reload);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        split.AddChild(left);
        left.AddChild(new Label { Text = "Installed content packs (Browse and install adds them)" });
        _packs = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, TextureFilter = TextureFilterEnum.Nearest };
        _packs.ItemSelected += _ => PackChanged();
        left.AddChild(_packs);
        _packInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        left.AddChild(_packInfo);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(right);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 150), SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(scroll);
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(form);
        form.AddChild(Field("Shard name (players see it)", out _name));
        form.AddChild(Field("Catalogue the pack comes from (URL, or URL=ed25519:key)", out _catalogue));
        var scriptRow = new HBoxContainer();
        form.AddChild(scriptRow);
        scriptRow.AddChild(new Label { Text = "Script packs on this shard" });
        _scripts = new OptionButton();
        _scripts.AddItem("allowed");
        _scripts.AddItem("forbidden");
        scriptRow.AddChild(_scripts);
        _extra = new HBoxContainer();
        form.AddChild(_extra);
        form.AddChild(new Label { Text = "Numeric slots for the client components (a slot the shard does not already use; the tool refuses collisions and out-of-range ids)" });
        _rows = new VBoxContainer();
        form.AddChild(_rows);

        var buttons = new HBoxContainer();
        right.AddChild(buttons);
        _build = new Button { Text = "Build (dry run)", TooltipText = "Installs, locks, exports and checks everything, and writes nothing into the shard." };
        _build.Pressed += () => _ = DeployAsync(true);
        buttons.AddChild(_build);
        _deploy = new Button { Text = "Deploy to the profile", TooltipText = "Writes the deployment into the profile's server folder. Restart the server afterwards." };
        _deploy.Pressed += () => _ = DeployAsync(false);
        buttons.AddChild(_deploy);
        _roll = new Button { Text = "Roll back to previous", TooltipText = "ModernUO: swaps the previous deployment back in (and again to undo). Native adapters: deploy the older pack version again." };
        _roll.Pressed += () => _ = RollbackAsync();
        buttons.AddChild(_roll);
        _use = new Button { Text = "Use for Start clients", TooltipText = "Puts this deployment's lock and store on the profile, so the run bar's Start clients runs the game with the pack mounted." };
        _use.Pressed += UseForClients;
        buttons.AddChild(_use);

        _log = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(0, 120), SizeFlagsVertical = SizeFlags.ExpandFill, PlaceholderText = "The deployment log and its result appear here." };
        right.AddChild(_log);

        AddChild(new Label { Text = "Deployed on each profile" });
        _deployed = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(0, 110), SelectionEnabled = true };
        AddChild(_deployed);
        AddChild(new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "Rolling back: ModernUO keeps the replaced deployment in Data/GUO/previous; Roll back swaps it in, then restart the server. "
                + "ServUO, RunUO, POL, Sphere and UOX3 keep every replaced file in Data/GUO/revisions; deploy the older pack version again to roll back, "
                + "which updates the server files and the players' descriptor together. Saves are never touched; restore a save backup if a deployment changed persistent objects.",
        });
        Shown();
    }

    private static Control Field(string label, out LineEdit edit)
    {
        var box = new HBoxContainer();
        box.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(260, 0) });
        edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(edit);
        return box;
    }

    // ---- profiles and packs ---------------------------------------------------------------------------

    private void LoadProfiles()
    {
        if (_view?.Bench == null)
        {
            return;
        }

        string keep = Current?.Id;
        try
        {
            _profiles = ProfilesOverride ?? ServerProfiles.Load(Bench.ProfilesPath);
        }
        catch (Exception e)
        {
            _profiles = new ServerProfiles();
            _log.Text = "Server profiles could not be read: " + e.Message;
        }

        _profile.Clear();
        foreach (ServerProfile p in _profiles.Servers)
        {
            _profile.AddItem($"{p.Name} ({p.Backend})");
        }

        int index = _profiles.Servers.FindIndex(p => p.Id == (keep ?? _profiles.Selected));
        if (index >= 0)
        {
            _profile.Selected = index;
        }

        ProfileChanged();
    }

    public bool SelectProfile(string name)
    {
        int i = _profiles?.Servers.FindIndex(p => p.Name == name) ?? -1;
        if (i < 0)
        {
            return false;
        }

        _profile.Selected = i;
        ProfileChanged();
        return true;
    }

    private void ProfileChanged()
    {
        ServerProfile p = Current;
        if (p == null)
        {
            _profileInfo.Text = "No server profiles yet: open Manage servers on the run bar and add one (or Add backend starters).";
            return;
        }

        bool known = Backends.Contains(p.Backend);
        _profileInfo.Text = $"{p.Host}:{p.Port}   folder: {(string.IsNullOrEmpty(p.ServerDirectory) ? "(not set)" : p.ServerDirectory)}"
            + (known ? "" : $"   backend '{p.Backend}' has no content adapter");
        if (_name.Text.Length == 0 || _name.HasMeta("auto"))
        {
            _name.Text = p.Name;
            _name.SetMeta("auto", true);
        }

        foreach (Node c in _extra.GetChildren())
        {
            _extra.RemoveChild(c);
            c.QueueFree();
        }

        _slots = null;
        _sphere = null;
        if (p.Backend == "pol")
        {
            _extra.AddChild(new Label { Text = "POL object types file (JSON)" });
            _slots = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "{\"pack:item\": 327680}" };
            _extra.AddChild(_slots);
        }
        else if (p.Backend == "sphere")
        {
            _extra.AddChild(new Label { Text = "Existing Sphere graphics to reuse (comma separated)" });
            _sphere = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _extra.AddChild(_sphere);
        }

        _deploy.Disabled = !known;
        _build.Disabled = !known;
        _use.Disabled = !_lastDeploy.ContainsKey(p.Id);
        _ = RefreshStatusAsync();
    }

    private void LoadPacks()
    {
        if (_view?.Bench == null)
        {
            return;
        }

        string keep = Pack?.Id;
        _installed.Clear();
        _packs.Clear();
        using StoreClient client = Bench.NewClient("http://127.0.0.1:1/");
        foreach (StoreManifest m in client.Installed().Where(m => m.Kind == "content").OrderBy(m => m.Id))
        {
            _installed.Add(m);
            _packs.AddItem($"{m.Title}  v{m.Version}  ({m.Target})");
        }

        _packInfo.Text = _installed.Count == 0 ? "No content packs installed. Install one on the Browse and install tab." : "";
        int i = keep == null ? -1 : _installed.FindIndex(m => m.Id == keep);
        if (i >= 0)
        {
            _packs.Select(i);
        }

        PackChanged();
    }

    public bool SelectPack(string id)
    {
        int i = _installed.FindIndex(m => m.Id == id);
        if (i < 0)
        {
            return false;
        }

        _packs.Select(i);
        PackChanged();
        return true;
    }

    private static int Suggest(string type, int n) => type switch
    {
        "static" or "tiledata" or "light" or "font" or "map" => 0x6001 + n,
        "land" or "texmap" or "hue" => 0x3000 + n,
        "gump" => 0xF000 + n,
        "sound" or "music" => 0xE000 + n,
        "animation" => 2000 + n,
        "multi" => 0x2000 + n,
        _ => 0x7000 + n,
    };

    private void PackChanged()
    {
        _bindings.Clear();
        foreach (Node c in _rows.GetChildren())
        {
            _rows.RemoveChild(c);
            c.QueueFree();
        }

        StoreManifest m = Pack;
        if (m == null)
        {
            return;
        }

        try
        {
            using StoreClient client = Bench.NewClient("http://127.0.0.1:1/");
            StoreVerifiedContent closure = client.VerifyContent(m.Id, m.Version);
            var counters = new Dictionary<string, int>();
            foreach (StoreVerifiedPack pack in closure.Packs.Values.OrderBy(p => p.Id))
            {
                foreach (StoreComponent c in pack.Manifest.Components ?? new())
                {
                    // The same components the game's own deployment window asks a slot for.
                    if (c.Target == "server" || c.Type is "translation" or "wearable" or "script" or "item" or "region" or "decoration" or "loot" or "creature")
                    {
                        continue;
                    }

                    counters[c.Type] = counters.GetValueOrDefault(c.Type);
                    var line = new HBoxContainer();
                    line.AddChild(new Label { Text = $"{pack.Id}:{c.Id}  ({c.Type})", CustomMinimumSize = new Vector2(320, 0) });
                    var edit = new LineEdit { Text = Suggest(c.Type, counters[c.Type]++).ToString(), CustomMinimumSize = new Vector2(100, 0) };
                    line.AddChild(edit);
                    _rows.AddChild(line);
                    _bindings[$"{pack.Id}:{c.Id}={c.Type}"] = edit;
                }
            }

            _packInfo.Text = $"{m.Title} {m.Version}: {closure.Packs.Count} pack(s) in the deployment ({string.Join(", ", closure.Packs.Keys)}), identity {closure.IdentityHash[..16]}";
            string source = Bench.SourceOf(m.Id, m.Version);
            if (source != null)
            {
                StoreCatalogueRecord rec = Bench.Trust.Catalogues().FirstOrDefault(r => r.Url == source);
                _catalogue.Text = rec?.Key != null ? $"{source}={rec.Key}" : source;
            }
        }
        catch (Exception e)
        {
            _packInfo.Text = "This pack's files do not verify: " + e.Message;
        }
    }

    public void SetBinding(string identity, int id)
    {
        foreach (var (key, edit) in _bindings)
        {
            if (key.StartsWith(identity + "=", StringComparison.Ordinal))
            {
                edit.Text = id.ToString();
            }
        }
    }

    public void SetCatalogue(string text) => _catalogue.Text = text;

    // ---- the deployment ---------------------------------------------------------------------------------

    private List<string> DeployArgs(ServerProfile p, StoreManifest m, bool dry)
    {
        var args = new List<string>
        {
            "deploy", "--backend", p.Backend, "--name", _name.Text.Trim().Length > 0 ? _name.Text.Trim() : p.Name,
            "--catalogue", _catalogue.Text.Trim(), "--pack", m.Id, "--version", m.Version,
            "--scripts", _scripts.GetItemText(_scripts.Selected), "--shard-dir", p.ServerDirectory,
            "--host", p.Host, "--port", p.Port.ToString(), "--work", Path.Combine(Bench.DeployRoot, p.Id),
        };
        foreach (var (key, edit) in _bindings)
        {
            string identity = key[..key.IndexOf('=')];
            string type = key[(key.IndexOf('=') + 1)..];
            args.AddRange(new[] { "--bind", $"{identity}={type}:{edit.Text.Trim()}" });
        }

        if (_slots != null && _slots.Text.Trim().Length > 0)
        {
            args.AddRange(new[] { "--adapter-slots", _slots.Text.Trim() });
        }

        if (_sphere != null)
        {
            foreach (string g in _sphere.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                args.AddRange(new[] { "--sphere-existing-graphic", g });
            }
        }

        if (dry)
        {
            args.Add("--dry-run");
        }

        return args;
    }

    public async Task DeployAsync(bool dry)
    {
        ServerProfile p = Current;
        StoreManifest m = Pack;
        if (_busy)
        {
            return;
        }

        string why = p == null ? "Choose a server profile." : m == null ? "Choose an installed content pack." : !Backends.Contains(p.Backend) ? $"Backend '{p.Backend}' has no content adapter."
            : string.IsNullOrEmpty(p.ServerDirectory) ? "The profile has no server folder: set it in Manage servers." : _catalogue.Text.Trim().Length == 0 ? "Name the catalogue the pack comes from." : null;
        if (why != null)
        {
            _log.Text = why;
            return;
        }

        _busy = true;
        SetButtons(false);
        _log.Text = $"{(dry ? "Dry run" : "Deploying")} {m.Id} {m.Version} for {p.Name} ({p.Backend}) in {p.ServerDirectory}\n";
        try
        {
            var (code, text) = await Bench.ShardToolAsync(DeployArgs(p, m, dry));
            if (!IsInsideTree())
            {
                return;
            }

            _log.Text += text + $"\n\nResult: {(code == 0 ? (dry ? "built and checked; nothing written" : "deployed; restart the server to load it") : "FAILED, exit " + code + "; the shard folder was not changed by a failed deployment")}\n";
            if (code == 0 && !dry)
            {
                RememberDeployment(p);
            }
        }
        finally
        {
            _busy = false;
            if (IsInsideTree())
            {
                SetButtons(true);
                await RefreshStatusAsync();
            }
        }
    }

    private void RememberDeployment(ServerProfile p)
    {
        string work = Path.Combine(Bench.DeployRoot, p.Id);
        if (!Directory.Exists(work))
        {
            return;
        }

        string newest = Directory.GetDirectories(work, "deploy-*").OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
        if (newest != null && File.Exists(Path.Combine(newest, "lock.json")))
        {
            _lastDeploy[p.Id] = (Path.Combine(newest, "lock.json"), Path.Combine(newest, "store"));
            _use.Disabled = false;
        }
    }

    private void SetButtons(bool on)
    {
        _build.Disabled = !on;
        _deploy.Disabled = !on;
        _roll.Disabled = !on;
    }

    public async Task RollbackAsync()
    {
        ServerProfile p = Current;
        if (_busy || p == null || string.IsNullOrEmpty(p.ServerDirectory))
        {
            return;
        }

        _busy = true;
        try
        {
            var (code, text) = await Bench.ShardToolAsync(new[] { "rollback", "--shard-dir", p.ServerDirectory });
            if (IsInsideTree())
            {
                _log.Text = $"Roll back {p.Name}\n{text}\nexit {code}";
            }
        }
        finally
        {
            _busy = false;
            if (IsInsideTree())
            {
                await RefreshStatusAsync();
            }
        }
    }

    private void UseForClients()
    {
        ServerProfile p = Current;
        if (p == null || !_lastDeploy.TryGetValue(p.Id, out var d))
        {
            return;
        }

        try
        {
            p.ContentLock = d.Lock;
            p.ContentStore = d.Store;
            _profiles.Save(Bench.ProfilesPath);
            Reload();
            _log.Text += $"\nProfile {p.Name} now starts clients with this deployment mounted (lock {d.Lock}). Clear the content lock in Manage servers to go back.\n";
        }
        catch (Exception e)
        {
            _log.Text += "\nCould not update the profile: " + e.Message + "\n";
        }
    }

    private void Reload() => _view.Run?.ReloadProfiles();

    // ---- what each profile has -----------------------------------------------------------------------------

    public async Task RefreshStatusAsync()
    {
        if (_view?.Bench == null || _profiles == null || _deployed == null)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (ServerProfile p in _profiles.Servers)
        {
            if (string.IsNullOrEmpty(p.ServerDirectory) || !Directory.Exists(p.ServerDirectory))
            {
                sb.AppendLine($"[b]{p.Name}[/b] ({p.Backend}): no local server folder, nothing to read");
                continue;
            }

            var (code, text) = await Bench.ShardToolAsync(new[] { "status", "--json", "--shard-dir", p.ServerDirectory });
            if (!IsInsideTree())
            {
                return;
            }

            try
            {
                JsonNode n = JsonNode.Parse(text.Split('\n').Last(l => l.TrimStart().StartsWith('{')));
                JsonNode d = n["deployed"];
                JsonNode prev = n["previous"];
                sb.AppendLine($"[b]{p.Name}[/b] ({p.Backend}): " + (d == null ? "nothing deployed" :
                    $"{(string)d["pack"]} {(string)d["version"]}, identity {((string)d["identity_hash"])[..16]}, scripts {(string)d["scripts"]}"
                    + (prev == null ? "; no previous kept" : $"; previous {(string)prev["pack"]} {(string)prev["version"]}")
                    + (_profiles.Servers.Count > 0 && _lastDeploy.ContainsKey(p.Id) ? "; used for Start clients" : "")));
            }
            catch (Exception)
            {
                sb.AppendLine($"[b]{p.Name}[/b]: status unavailable ({text.Replace("[", "[lb]")})");
            }
        }

        _deployed.Text = sb.Length == 0 ? "No server profiles." : sb.ToString();
    }
}
#endif
