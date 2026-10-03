#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

/// <summary>
/// F3 commands for the UO Store tab: "Store: ...". They open its sections and act on the run bar's
/// server profiles. Nothing here installs, deploys or publishes by itself; each one only brings the
/// right section forward (a deploy asks for its pack and slots there).
/// </summary>
public sealed class StoreProvider : SearchProvider
{
    private readonly SearchContext _ctx;
    private readonly List<SearchEntry> _entries = new();
    private bool _built;

    public StoreProvider(SearchContext ctx)
    {
        _ctx = ctx;
    }

    public override string Name => "Store";

    public override bool Done => _built;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        if (_built)
        {
            return;
        }

        _built = true;
        if (_ctx.Store == null)
        {
            return;
        }

        Add("Store: browse and install packs", "UO Store tab: catalogues, search, details, install into the editor's store", "store packs catalogue browse install shop assets", () => _ctx.Store.ShowSection(0));
        Add("Store: refresh catalogues", "UO Store tab: fetch every catalogue again", "store catalogue refresh reload", () =>
        {
            _ctx.Store.ShowSection(0);
            _ = _ctx.Store.Browse.RefreshAsync();
        });
        Add("Store: approve a catalogue key", "UO Store tab: a new catalogue asks for its key to be approved", "store catalogue key approve trust fingerprint", () => _ctx.Store.ShowSection(0));
        Add("Store: server content", "UO Store tab: choose packs and deploy them to a server profile", "store server content deploy shard profile pack adapter", () => _ctx.Store.ShowSection(1));
        Add("Store: deployed on each server", "UO Store tab: what each profile has deployed, and how to roll back", "store deployed status rollback shard profile", () =>
        {
            _ctx.Store.ShowSection(1);
            _ = _ctx.Store.Server.RefreshStatusAsync();
        });
        Add("Store: verify a pack", "UO Store tab, Publish: schema, hashes, dependencies and the content policy", "store verify check pack zip policy publish author", () => _ctx.Store.ShowSection(2));
        Add("Store: publish a pack", "UO Store tab, Publish: put a verified ZIP in the local store", "store publish pack zip local author", () => _ctx.Store.ShowSection(2));
        Add("Store: prepare a catalogue pull request", "UO Store tab, Publish: write the listing file and the gh commands for DatMoshu/GodotUO-packs", "store catalogue pull request pr listing official submit gh", () => _ctx.Store.ShowSection(2));
        Add("Store: open the editor's pack store folder", "the folder of packs installed from this editor (build/editor_store)", "store folder open packs installed explorer", () =>
        {
            string root = Path.Combine(EditorData.RepoRoot, "build", "editor_store");
            Directory.CreateDirectory(root);
            OS.ShellOpen(root);
        });

        ServerProfiles profiles;
        try
        {
            profiles = ServerProfiles.Load(Path.Combine(EditorData.RepoRoot, "build", "editor_servers", "profiles.json"));
        }
        catch (Exception)
        {
            return;
        }

        foreach (ServerProfile p in profiles.Servers)
        {
            string name = p.Name;
            Add($"Store: deploy to {name}", $"UO Store tab, Server content, with the {p.Backend} profile {name} chosen", $"store deploy server content shard profile {p.Backend} {name}", () =>
            {
                _ctx.Store.ShowSection(1);
                _ctx.Store.Server.SelectProfile(name);
            });
        }
    }

    private void Add(string title, string hint, string tags, Action run)
    {
        _entries.Add(new SearchEntry { Kind = "Store", Title = title, Hint = hint, Tags = tags, Key = "Store:" + title, Bonus = 30, Run = run }.Prepare());
    }
}
#endif
