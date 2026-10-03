#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// UO Store, section 3 (pack authors): verify a pack ZIP, publish it to the local store, and prepare
/// a pull request for the official catalogue. The checks are tools/asset_store's (`run.py check`),
/// the same code the command line and the catalogue's CI use. The editor makes no GitHub call and
/// never handles a signing key: the official catalogue is signed by its own CI after review.
/// </summary>
[Tool]
public partial class StorePublishTab : VBoxContainer, IStoreSection
{
    private StoreView _view;
    private LineEdit _zip, _store, _urls, _out;
    private TextEdit _provenance;
    private RichTextLabel _report;
    private TextEdit _log;
    private Button _publish, _prepare;
    private FileDialog _picker;
    private JsonNode _last;
    private string _lastZip;
    private bool _busy;

    internal void Attach(StoreView view) => _view = view;

    private StoreBench Bench => _view.Bench;

    public void Shown()
    {
    }

    // ---- what the smoke check reads ------------------------------------------------------------------------

    public string Verdict => StoreBench.VerdictLine(_last);

    public string Log => _log?.Text ?? "";

    public string ReportText => _report?.GetParsedText() ?? "";

    public bool IsBusy => _busy;

    public bool CanPublish => _publish != null && !_publish.Disabled;

    public void SetStoreFolder(string folder) => _store.Text = folder;

    public override void _Ready()
    {
        AddChild(new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "Check a pack ZIP, put it in a local store (any web server or the Store tab's catalogue list can serve that folder), "
                + "and prepare the listing file for a pull request to the official catalogue (DatMoshu/GodotUO-packs). "
                + "This editor never handles a signing key and makes no GitHub call: the official catalogue is signed by its own CI after review, "
                + "and you run the printed gh commands yourself.",
        });
        var row = new HBoxContainer();
        AddChild(row);
        row.AddChild(new Label { Text = "Pack ZIP" });
        _zip = new LineEdit { PlaceholderText = "path to my-pack-1.0.0.zip", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_zip);
        var browse = new Button { Text = "Browse..." };
        browse.Pressed += BrowseZip;
        row.AddChild(browse);
        var verify = new Button { Text = "Verify", TooltipText = "Schema, every file's hash, the dependencies, and the automatic content-policy checks. Writes nothing." };
        verify.Pressed += () => _ = VerifyAsync(_zip.Text);
        row.AddChild(verify);

        _report = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 170), SelectionEnabled = true };
        AddChild(_report);

        var pub = new HBoxContainer();
        AddChild(pub);
        pub.AddChild(new Label { Text = "Local store folder" });
        _store = new LineEdit { PlaceholderText = "blank: the configured store (UO_STORE_DIR, default build/store_cdn)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pub.AddChild(_store);
        _publish = new Button { Text = "Publish to the local store", Disabled = true, TooltipText = "run.py publish: verifies again, then copies the ZIP into the store folder and rewrites its index. Published versions never change." };
        _publish.Pressed += () => _ = PublishAsync();
        pub.AddChild(_publish);

        AddChild(new HSeparator());
        AddChild(new Label { Text = "Official catalogue: host the ZIP over HTTPS (a release asset on your own repository works), then prepare the listing." });
        var urls = new HBoxContainer();
        AddChild(urls);
        urls.AddChild(new Label { Text = "ZIP address(es)" });
        _urls = new LineEdit { PlaceholderText = "https://github.com/you/your-pack/releases/download/v1.0.0/my-pack-1.0.0.zip   (separate mirrors with spaces)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        urls.AddChild(_urls);
        var prov = new HBoxContainer();
        AddChild(prov);
        prov.AddChild(new Label { Text = "How it was made" });
        _provenance = new TextEdit { PlaceholderText = "Provenance, at most 500 characters: \"Drawn in Aseprite; no client art used\"", CustomMinimumSize = new Vector2(0, 54), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        prov.AddChild(_provenance);
        var outRow = new HBoxContainer();
        AddChild(outRow);
        outRow.AddChild(new Label { Text = "Write the listing into" });
        _out = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "blank: build/editor_store/pull_requests/<pack id>" };
        outRow.AddChild(_out);
        _prepare = new Button { Text = "Prepare a catalogue PR", Disabled = true };
        _prepare.Pressed += () => _ = PrepareAsync();
        outRow.AddChild(_prepare);
        var copy = new Button { Text = "Copy the log" };
        copy.Pressed += () => DisplayServer.ClipboardSet(_log.Text);
        outRow.AddChild(copy);

        _log = new TextEdit { Editable = false, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 150), PlaceholderText = "Results and the gh commands appear here." };
        AddChild(_log);
    }

    private void BrowseZip()
    {
        _picker ??= new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Choose a pack ZIP",
            Filters = new[] { "*.zip ; Pack ZIP" },
        };
        if (_picker.GetParent() == null)
        {
            AddChild(_picker);
            _picker.FileSelected += path =>
            {
                _zip.Text = path;
                _ = VerifyAsync(path);
            };
        }

        _picker.PopupCenteredRatio(0.5f);
    }

    public async Task VerifyAsync(string zip)
    {
        zip = (zip ?? "").Trim().Trim('"');
        if (_busy || zip.Length == 0)
        {
            return;
        }

        _busy = true;
        _publish.Disabled = true;
        _prepare.Disabled = true;
        _report.Text = "Checking...";
        try
        {
            var args = new System.Collections.Generic.List<string>();
            if (_store.Text.Trim().Length > 0)
            {
                args.AddRange(new[] { "--store-dir", _store.Text.Trim() });
            }

            args.AddRange(new[] { "check", "--json", zip });
            var (code, text) = await Bench.StoreToolAsync(args);
            JsonNode node = null;
            try
            {
                node = JsonNode.Parse(text.Split('\n').Last(l => l.TrimStart().StartsWith('{')));
            }
            catch (Exception)
            {
                node = new JsonObject { ["ok"] = false, ["error"] = text };
            }

            _last = node;
            _lastZip = zip;
            if (!IsInsideTree())
            {
                return;
            }

            _report.Text = Describe(node);
            bool ok = node["ok"]?.GetValue<bool>() == true && (string)node["policy"]?["verdict"] != "refused";
            _publish.Disabled = !ok;
            _prepare.Disabled = !ok;
        }
        finally
        {
            _busy = false;
        }
    }

    private static string Describe(JsonNode n)
    {
        if (n["ok"]?.GetValue<bool>() != true)
        {
            return "[b]Does not verify[/b]\n" + ((string)n["error"] ?? "").Replace("[", "[lb]") + "\n\nNothing can be published until the pack verifies.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[b]{(string)n["title"]}[/b]  {(string)n["id"]} v{(string)n["version"]}");
        sb.AppendLine($"Schema {(string)n["schema"]}   kind {(string)n["kind"]}" + ((string)n["target"] is { Length: > 0 } t ? $"   target {t}" : "") + $"   licence {(string)n["licence"]}   {(int)n["files"]} files, {(long)n["size"] / 1024.0:0} KB");
        sb.AppendLine("Hashes: " + (string)n["hashes"]);
        sb.AppendLine("SHA-256 of the ZIP: " + (string)n["sha256"]);
        if (n["components"] is JsonArray comps && comps.Count > 0)
        {
            sb.AppendLine("Components: " + string.Join(", ", comps.Select(c => $"{(string)c["id"]} ({(string)c["type"]}, {(string)c["target"]})")));
        }

        if (n["dependencies"] is JsonObject deps && deps.Count > 0)
        {
            sb.AppendLine("Dependencies: " + string.Join(", ", deps.Select(d => $"{d.Key}@{(string)d.Value["version"]} ({(string)d.Value["status"]})")));
        }
        else
        {
            sb.AppendLine("Dependencies: none");
        }

        string verdict = (string)n["policy"]["verdict"];
        sb.AppendLine($"Content policy (automatic): [b]{verdict}[/b]" + (verdict == "refused" ? "  - cannot be published or listed" : verdict == "review" ? "  - a reviewer looks at the flagged items" : ""));
        foreach (JsonNode f in (JsonArray)n["policy"]["findings"])
        {
            sb.AppendLine($"  {(string)f["level"]}: {((string)f["text"]).Replace("[", "[lb]")}");
        }

        sb.AppendLine((string)n["policy"]["note"]);
        return sb.ToString();
    }

    public async Task PublishAsync()
    {
        if (_busy || _last == null)
        {
            return;
        }

        _busy = true;
        _publish.Disabled = true;
        try
        {
            var args = new System.Collections.Generic.List<string>();
            if (_store.Text.Trim().Length > 0)
            {
                args.AddRange(new[] { "--store-dir", _store.Text.Trim() });
            }

            args.AddRange(new[] { "publish", _lastZip });
            var (code, text) = await Bench.StoreToolAsync(args);
            if (IsInsideTree())
            {
                _log.Text = $"run.py publish exit {code}\n{text}\n" + (code == 0 ? "Serve that folder (python tools/asset_store/run.py serve) and add its address on the Browse tab; the catalogue is signed when UO_STORE_SIGNING_KEY is set for that tool, which this editor never sets." : "");
            }
        }
        finally
        {
            _busy = false;
            if (IsInsideTree())
            {
                _publish.Disabled = false;
            }
        }
    }

    public async Task PrepareAsync(string urls, string provenance, string outFolder)
    {
        _urls.Text = urls;
        _provenance.Text = provenance;
        _out.Text = outFolder;
        await PrepareAsync();
    }

    public async Task PrepareAsync()
    {
        if (_busy || _last == null)
        {
            return;
        }

        _busy = true;
        try
        {
            string id = (string)_last["id"];
            string folder = _out.Text.Trim().Length > 0 ? _out.Text.Trim() : Path.Combine(Bench.Root, "pull_requests", id);
            var args = new System.Collections.Generic.List<string> { "prepare-listing", _lastZip, "--provenance", _provenance.Text.Trim(), "--out", folder };
            foreach (string u in _urls.Text.Split(new[] { ' ', ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                args.AddRange(new[] { "--url", u });
            }

            var (code, text) = await Bench.StoreToolAsync(args);
            if (IsInsideTree())
            {
                _log.Text = (code == 0 ? "" : "Not prepared:\n") + text;
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
#endif
