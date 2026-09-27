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
/// it waits for the client data, searches the Assets dock for an art id,
/// checks the inspector decoded real pixels, captures the editor window when
/// there is one, writes <c>report.json</c> to the directory and quits.
/// </summary>
/// <remarks>
/// With <c>--guo-editor-smoke-reload</c> it also proves the addon survives an
/// assembly reload: after the first pass it writes <c>reload.request</c> and
/// waits for tools/editor_smoke to rebuild the C# and answer with
/// <c>reload.go</c>, then sends the editor the focus-in notification GodotTools
/// checks for a changed assembly on. The reload recreates the plugin, the
/// plugin builds a new smoke node, and that one finds <c>before_reload.json</c>
/// and runs the checks a second time against the reloaded docks.
/// </remarks>
[Tool]
public partial class EditorSmoke : Node
{
    public const string Flag = "--guo-editor-smoke";
    public const string ArtFlag = "--guo-editor-smoke-art";
    public const string ReloadFlag = "--guo-editor-smoke-reload";

    private const double TimeoutSeconds = 180;
    private const int SettleFrames = 90;

    private readonly string _out;
    private readonly EditorData _data;
    private readonly AssetsDock _assets;
    private readonly ArtInspectorDock _inspector;
    private readonly Dictionary<string, object> _report = new();
    private readonly List<string> _failures = new();

    private double _elapsed;
    private int _frames;
    private int _stage;
    private bool _reloadTest;
    private bool _afterReload;

    public EditorSmoke() : this(null, null, null, null)
    {
    }

    public EditorSmoke(string outDir, EditorData data, AssetsDock assets, ArtInspectorDock inspector)
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
                    _stage = 1;
                    _frames = 0;
                }
                else if (_elapsed > TimeoutSeconds)
                {
                    _failures.Add($"client data did not load within {TimeoutSeconds} s");
                    Finish();
                }

                break;

            case 1:
                // Let the docks lay out and draw the page once.
                if (_frames >= 5)
                {
                    CheckArt();
                    _stage = 2;
                    _frames = 0;
                }

                break;

            case 2:
                if (DisplayServer.GetName() == "headless" || _frames >= SettleFrames)
                {
                    Capture();
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

    private void CheckArt()
    {
        if (!_data.IsLoaded)
        {
            return;
        }

        string art = ArgValue(ArtFlag) ?? "0x0E75";
        _report["art_query"] = art;

        uint? index = _assets.Search(art);
        _report["art_index"] = index.HasValue ? $"0x{index.Value:X5}" : null;
        if (index == null)
        {
            _failures.Add($"Assets dock search '{art}' selected nothing");
            return;
        }

        if (_inspector.Current != index)
        {
            _failures.Add("selecting in the Assets dock did not reach the inspector");
        }

        Image img = _inspector.CurrentImage;
        if (img == null)
        {
            _failures.Add($"inspector has no image for {art}");
            return;
        }

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

        _report["art_size"] = new[] { img.GetWidth(), img.GetHeight() };
        _report["art_opaque_pixels"] = opaque;
        if (opaque == 0)
        {
            _failures.Add($"art {art} decoded to a fully transparent image");
        }

        _report["inspector_has_texture"] = _inspector.Texture != null;
        if (_inspector.Texture == null)
        {
            _failures.Add("inspector preview has no texture");
        }

        Directory.CreateDirectory(_out);
        string path = Path.Combine(_out, "art.png");
        img.SavePng(path);
        _report["art_png"] = path;
    }

    private void Capture()
    {
        if (DisplayServer.GetName() == "headless")
        {
            _report["screenshot"] = null;
            _report["screenshot_note"] = "headless editor: nothing is rendered, so there is no frame to capture";
            return;
        }

        Image frame = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage();
        if (frame == null || frame.IsEmpty())
        {
            _failures.Add("could not capture the editor window");
            return;
        }

        Directory.CreateDirectory(_out);
        string path = Path.Combine(_out, _afterReload ? "editor_after_reload.png" : "editor.png");
        frame.SavePng(path);
        _report["screenshot"] = path;
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
        File.WriteAllText(
            Path.Combine(_out, "report.json"),
            JsonSerializer.Serialize(_report, JsonOptions)
        );

        GD.Print($"[GUO editor] smoke {(_failures.Count == 0 ? "OK" : "FAILED")}: {string.Join("; ", _failures)}");
        _stage = 3;
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
#endif
