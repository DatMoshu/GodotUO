#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

/// <summary>
/// The addon's own health check, driven by tools/editor_smoke. Present only
/// when the editor was started with <c>-- --guo-editor-smoke &lt;dir&gt;</c>:
/// it waits for the client data, then walks every tab of the UO Assets dock,
/// searches it for a known id, checks the UO Inspector received what that
/// panel should produce, saves the image and (when there is a window) a
/// capture of the editor, writes <c>report.json</c> and quits.
/// </summary>
/// <remarks>
/// With <c>--guo-editor-smoke-reload</c> it also proves the addon survives an
/// assembly reload: after the first pass it writes <c>reload.request</c> and
/// waits for tools/editor_smoke to rebuild the C# and answer with
/// <c>reload.go</c>, then sends the editor the focus-in notification GodotTools
/// checks for a changed assembly on. The reload recreates the plugin, the
/// plugin builds a new smoke node, and that one finds <c>before_reload.json</c>
/// and walks the panels a second time against the reloaded docks.
/// </remarks>
[Tool]
public partial class EditorSmoke : Node
{
    public const string Flag = "--guo-editor-smoke";
    public const string ArtFlag = "--guo-editor-smoke-art";
    public const string ReloadFlag = "--guo-editor-smoke-reload";

    private const double TimeoutSeconds = 180;
    private const int SettleFrames = 45;

    private readonly string _out;
    private readonly EditorData _data;
    private readonly AssetsDock _assets;
    private readonly InspectorDock _inspector;
    private readonly Dictionary<string, object> _report = new();
    private readonly Dictionary<string, object> _panels = new();
    private readonly List<string> _failures = new();

    private double _elapsed;
    private int _frames;
    private int _stage;
    private int _panel;
    private bool _reloadTest;
    private bool _afterReload;

    public EditorSmoke() : this(null, null, null, null)
    {
    }

    public EditorSmoke(string outDir, EditorData data, AssetsDock assets, InspectorDock inspector)
    {
        _out = outDir;
        _data = data;
        _assets = assets;
        _inspector = inspector;
        Name = "GuoEditorSmoke";

        if (_out != null)
        {
            _reloadTest = Array.IndexOf(OS.GetCmdlineUserArgs(), ReloadFlag) >= 0;
            _afterReload = _reloadTest
                && File.Exists(Path.Combine(_out, "before_reload.json"))
                && File.Exists(Path.Combine(_out, "reload.go"));
        }
    }

    /// <summary>The output directory from the command line, or null when this is not a smoke run.</summary>
    public static string OutDirFromArgs() => ArgValue(Flag);

    private static string ArgValue(string flag)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
            {
                return args[i].Substring(flag.Length + 1);
            }
        }

        return null;
    }

    private static bool Headless => DisplayServer.GetName() == "headless";

    private string Suffix => _afterReload ? "_after_reload" : "";

    public override void _Process(double delta)
    {
        if (_out == null)
        {
            return;
        }

        _elapsed += delta;
        _frames++;

        switch (_stage)
        {
            case 0:
                if (_data.IsLoaded || _data.Error != null)
                {
                    CheckLoaded();
                    _stage = _failures.Count == 0 ? 1 : 9;
                    _frames = 0;
                }
                else if (_elapsed > TimeoutSeconds)
                {
                    _failures.Add($"client data did not load within {TimeoutSeconds} s");
                    _stage = 9;
                }

                break;

            case 1:
                // Bring the next tab to the front and let it lay out.
                if (_panel >= _assets.Panels.Count)
                {
                    _stage = 9;
                    break;
                }

                _assets.ShowPanel(_assets.Panels[_panel]);
                _stage = 2;
                _frames = 0;
                break;

            case 2:
                if (_frames >= 3)
                {
                    CheckPanel(_assets.Panels[_panel]);
                    _stage = 3;
                    _frames = 0;
                }

                break;

            case 3:
                // Let the inspector draw what it was given, then capture.
                if (Headless || _frames >= SettleFrames)
                {
                    Capture(_assets.Panels[_panel]);
                    _panel++;
                    _stage = 1;
                }

                break;

            case 9:
                _report["panels"] = _panels;
                if (_reloadTest && !_afterReload && _failures.Count == 0)
                {
                    RequestReload();
                    _stage = 4;
                    _elapsed = 0;
                }
                else
                {
                    Finish();
                }

                break;

            case 4:
                if (File.Exists(Path.Combine(_out, "reload.go")))
                {
                    // What GodotTools does on focus-in: compare the assembly on
                    // disk with the loaded one, and reload if it changed.
                    GD.Print("[GUO editor] smoke: assembly rebuilt, asking the editor to reload");
                    GetTree().Root.PropagateNotification((int)NotificationApplicationFocusIn);
                    _stage = 5;
                    _elapsed = 0;
                }
                else if (_elapsed > TimeoutSeconds)
                {
                    _failures.Add("tools/editor_smoke never answered reload.request");
                    Finish();
                }

                break;

            case 5:
                // A reload frees this node; still being here means none happened.
                if (_elapsed > 30)
                {
                    _failures.Add("the editor did not reload the rebuilt assembly");
                    Finish();
                }

                break;
        }
    }

    private void CheckLoaded()
    {
        _report["client_data"] = _data.ClientData;
        _report["client_version"] = _data.ClientVersion;
        _report["load_ms"] = _data.LoadMilliseconds;
        _report["data_loaded"] = _data.IsLoaded;
        _report["assets_dock_in_tree"] = _assets.IsInsideTree();
        _report["inspector_dock_in_tree"] = _inspector.IsInsideTree();
        _report["panel_count"] = _assets.Panels.Count;

        if (!_data.IsLoaded)
        {
            _failures.Add(_data.Error ?? "client data not loaded");
        }

        if (!_assets.IsInsideTree())
        {
            _failures.Add("UO Assets dock is not in the editor tree");
        }

        if (!_inspector.IsInsideTree())
        {
            _failures.Add("UO Inspector dock is not in the editor tree");
        }

        _assets.MakeVisible();
        _inspector.MakeVisible();
    }

    private void CheckPanel(AssetPanel panel)
    {
        string name = panel.Name;
        var result = new Dictionary<string, object>();
        _panels[name] = result;
        var failures = new List<string>();

        if (panel is ParityPanel { Unavailable: string why })
        {
            // guoasset is optional (tools/guoasset/README.md): no UOWW, no parity.
            result["skipped"] = why;
            result["ok"] = true;
            result["failures"] = failures;
            return;
        }

        string query = name == "Art" ? ArgValue(ArtFlag) ?? panel.SmokeQuery : panel.SmokeQuery;
        result["query"] = query;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int? selected;
        try
        {
            selected = panel.Search(query);
        }
        catch (Exception ex)
        {
            selected = null;
            failures.Add($"search threw {ex.GetType().Name}: {ex.Message}");
        }

        result["ms"] = sw.ElapsedMilliseconds;
        result["selected"] = selected;
        if (selected == null && failures.Count == 0)
        {
            failures.Add($"search '{query}' selected nothing");
        }

        Inspection shown = _inspector.Current;
        string source = name switch
        {
            "Anims" => "Animations",
            _ => name,
        };

        if (selected != null && (shown == null || shown.Source != source))
        {
            failures.Add($"the inspector did not receive the {name} selection");
        }
        else if (shown != null && selected != null)
        {
            result["id"] = shown.Id;
            result["frames"] = shown.Frames.Length;
            result["text_chars"] = shown.Text.Length;

            Image img = shown.Image;
            if (img != null)
            {
                int opaque = Opaque(img);
                result["image_size"] = new[] { img.GetWidth(), img.GetHeight() };
                result["opaque_pixels"] = opaque;
                if (opaque == 0)
                {
                    failures.Add("the image is fully transparent");
                }

                Directory.CreateDirectory(_out);
                string path = Path.Combine(_out, $"{name.ToLowerInvariant()}{Suffix}.png");
                img.SavePng(path);
                result["png"] = path;

                if (_inspector.Texture == null)
                {
                    failures.Add("the inspector preview has no texture");
                }
            }
            else if (panel.SmokeNeedsImage)
            {
                failures.Add("the inspection has no image");
            }

            if (shown.Text.Length == 0)
            {
                failures.Add("the inspection has no text");
            }
        }

        if (panel is SoundPanel sounds && selected != null)
        {
            string played = sounds.SmokePlay();
            result["played"] = played == null;
            if (played != null)
            {
                failures.Add(played);
            }
        }

        result["ok"] = failures.Count == 0;
        result["failures"] = failures;
        foreach (string f in failures)
        {
            _failures.Add($"{name}: {f}");
        }
    }

    private static int Opaque(Image img)
    {
        int opaque = 0;
        for (int y = 0; y < img.GetHeight(); y++)
        {
            for (int x = 0; x < img.GetWidth(); x++)
            {
                if (img.GetPixel(x, y).A > 0)
                {
                    opaque++;
                }
            }
        }

        return opaque;
    }

    private void Capture(AssetPanel panel)
    {
        var result = (Dictionary<string, object>)_panels[panel.Name.ToString()];
        if (Headless)
        {
            result["screenshot"] = null;
            return;
        }

        Image frame = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage();
        if (frame == null || frame.IsEmpty())
        {
            _failures.Add($"{panel.Name}: could not capture the editor window");
            return;
        }

        Directory.CreateDirectory(_out);
        string path = Path.Combine(_out, $"editor_{panel.Name.ToString().ToLowerInvariant()}{Suffix}.png");
        frame.SavePng(path);
        result["screenshot"] = path;
    }

    private void RequestReload()
    {
        Directory.CreateDirectory(_out);
        _report["ok"] = true;
        File.WriteAllText(Path.Combine(_out, "before_reload.json"), JsonSerializer.Serialize(_report, JsonOptions));
        File.WriteAllText(Path.Combine(_out, "reload.request"), "");
        GD.Print("[GUO editor] smoke: first pass OK, waiting for a rebuilt assembly");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void Finish()
    {
        _report["ok"] = _failures.Count == 0;
        _report["failures"] = _failures;
        _report["display"] = DisplayServer.GetName();
        _report["elapsed_s"] = Math.Round(_elapsed, 2);
        if (_afterReload)
        {
            _report["reloaded"] = true;
            _report["before_reload"] = JsonSerializer.Deserialize<JsonElement>(
                File.ReadAllText(Path.Combine(_out, "before_reload.json"))
            );
        }

        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "report.json"), JsonSerializer.Serialize(_report, JsonOptions));

        GD.Print($"[GUO editor] smoke {(_failures.Count == 0 ? "OK" : "FAILED")}: {string.Join("; ", _failures)}");
        _stage = 10;
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
#endif
