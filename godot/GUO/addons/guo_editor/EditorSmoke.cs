#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly WorldView _world;
    private readonly Dictionary<string, object> _worldReport = new();
    private readonly Dictionary<string, object> _report = new();
    private readonly Dictionary<string, object> _panels = new();
    private readonly List<string> _failures = new();

    private double _elapsed;
    private int _frames;
    private int _stage;
    private int _panel;
    private bool _reloadTest;
    private bool _afterReload;

    public EditorSmoke() : this(null, null, null, null, null)
    {
    }

    public EditorSmoke(string outDir, EditorData data, AssetsDock assets, InspectorDock inspector, WorldView world)
    {
        _world = world;
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
                    _stage = 6;
                    _frames = 0;
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

            case 6:
                // The World tab, reached the way a user reaches it: from the
                // Maps panel's "Show in UO World".
                StartWorld();
                _stage = 7;
                _frames = 0;
                break;

            case 7:
                if (_frames >= (Headless ? 5 : SettleFrames))
                {
                    CheckWorld();
                    _world.ForcedMouse = new Vector2I((int)_world.Size.X / 2, (int)(_world.Size.Y / 2));
                    _stage = 8;
                    _frames = 0;
                }

                break;

            case 8:
                if (_frames >= 5)
                {
                    CheckPick();
                    _world.ForcedMouse = null;
                    _before = _world.IsBooted ? _world.Host.Scene.RenderedObjectsCount : 0;
                    PlaceMulti();
                    _stage = 11;
                    _frames = 0;
                }

                break;

            case 11:
                if (_frames >= (Headless ? 5 : SettleFrames))
                {
                    CheckMulti();

                    // The server's delete-object path (0x1D) removes it again,
                    // so the overlay check below has the block to itself.
                    _world.Host.RemoveServerObject(0x4000_0064);
                    StartOverlay();
                    _stage = 12;
                    _frames = 0;
                }

                break;

            case 12:
                if (_frames >= (Headless ? 5 : SettleFrames))
                {
                    CheckOverlay();
                    CloseOverlay();
                    _stage = 13;
                    _frames = 0;
                }

                break;

            case 13:
                if (_frames >= 5)
                {
                    CheckRestored();
                    _stage = 9;
                }

                break;

            case 9:
                _report["panels"] = _panels;
                _report["world"] = _worldReport;
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

    private void StartWorld()
    {
        MapPanel maps = _assets.Panel<MapPanel>();
        if (maps == null || _world == null)
        {
            WorldFail("no Maps panel or no World tab");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        maps.RequestJump(0, 1496, 1628);
        _worldReport["jump_ms"] = sw.ElapsedMilliseconds;
        _worldReport["tab_visible"] = _world.Visible;
        if (!_world.Visible)
        {
            WorldFail("Show in UO World did not bring the World tab forward");
        }
    }

    private void CheckWorld()
    {
        WorldHost host = _world.Host;
        _worldReport["booted"] = host.IsBooted;
        _worldReport["boot_ms"] = host.BootMilliseconds;
        if (!host.IsBooted)
        {
            WorldFail($"the world did not start: {host.Error}");
            return;
        }

        // The client writes default tables under CUOEnviroment.ExecutablePath;
        // the world view must have pointed that away from the project.
        bool litter = Directory.Exists(Path.Combine(ProjectSettings.GlobalizePath("res://"), "Data"));
        _worldReport["project_folder_clean"] = !litter;
        if (litter)
        {
            WorldFail("booting the world wrote a Data folder into the Godot project");
        }

        _worldReport["position"] = new[] { host.Facet, host.X, host.Y, host.Z };
        _worldReport["in_game"] = host.World.InGame;
        _worldReport["rendered_objects"] = host.Scene.RenderedObjectsCount;
        if (host.Facet != 0 || host.X != 1496 || host.Y != 1628)
        {
            WorldFail($"the view is at map{host.Facet} {host.X},{host.Y}, not map0 1496,1628");
        }

        if (host.Scene.RenderedObjectsCount <= 0)
        {
            WorldFail("GameScene drew no objects");
        }

        Image frame = _world.Capture();
        if (frame != null && !frame.IsEmpty())
        {
            Directory.CreateDirectory(_out);
            string path = Path.Combine(_out, $"world{Suffix}.png");
            frame.SavePng(path);
            _worldReport["png"] = path;
            _worldReport["image_size"] = new[] { frame.GetWidth(), frame.GetHeight() };
            int colours = Colours(frame);
            _worldReport["distinct_colours"] = colours;
            if (colours < 64)
            {
                WorldFail($"the world frame has only {colours} colours; it is not the map");
            }

            Image editor = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage();
            if (editor != null)
            {
                string shot = Path.Combine(_out, $"editor_world{Suffix}.png");
                editor.SavePng(shot);
                _worldReport["screenshot"] = shot;
            }
        }
    }

    private void CheckPick()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        Inspection picked = _world.InspectPicked();
        _worldReport["picked"] = picked?.Text.Split('\n')[0];
        if (picked == null)
        {
            WorldFail("the game's picking found nothing at the centre of the view");
        }
        else if (_inspector.Current != picked)
        {
            WorldFail("the picked object did not reach the UO Inspector");
        }
    }

    private int _before;

    private void PlaceMulti()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        // Multi 0x0064 (a small house) a few cells south of the view centre,
        // through the same calls a server's world-object packet makes.
        var item = _world.Host.PlaceServerMulti(0x4000_0064, 0x0064, 1500, 1634, 10);
        _worldReport["multi_placed"] = item != null;
        if (item == null)
        {
            WorldFail("could not place a multi through the server path");
        }
    }

    private void CheckMulti()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        int after = _world.Host.Scene.RenderedObjectsCount;
        _worldReport["objects_before_multi"] = _before;
        _worldReport["objects_with_multi"] = after;
        bool house = _world.Host.World.HouseManager.TryGetHouse(0x4000_0064, out var h) && h.Components.Count > 0;
        _worldReport["multi_components"] = house ? h.Components.Count : 0;
        if (!house)
        {
            WorldFail("the placed multi has no components in HouseManager");
        }
        else if (after <= _before)
        {
            WorldFail($"placing the multi did not add to what GameScene draws ({_before} -> {after})");
        }

        Image frame = _world.Capture();
        if (frame != null && !frame.IsEmpty())
        {
            string path = Path.Combine(_out, $"world_multi{Suffix}.png");
            frame.SavePng(path);
            _worldReport["multi_png"] = path;
        }
    }

    // Block 187,203 holds the view centre, 1496,1628. The overlay puts three
    // trees (static 0x0CCA) on it and turns all its land to water (0x00A8),
    // which a screenshot shows at a glance and a chunk walk can count.
    private const int OverlayBx = 187, OverlayBy = 203;
    private const ushort OverlayTree = 0x0CCA, OverlayWater = 0x00A8;
    private readonly Dictionary<string, object> _overlayReport = new();
    private Dictionary<string, DateTime> _installStamp;

    private Dictionary<string, DateTime> InstallStamp()
    {
        var d = new Dictionary<string, DateTime>();
        foreach (string f in Directory.GetFiles(_data.ClientData))
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            if (n.StartsWith("map") || n.StartsWith("statics") || n.StartsWith("staidx"))
            {
                d[n] = File.GetLastWriteTimeUtc(f);
            }
        }

        return d;
    }

    private void StartOverlay()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        _worldReport["overlay"] = _overlayReport;
        _installStamp = InstallStamp();
        WorldHost host = _world.Host;
        // A project of its own per pass: the second pass after a reload must
        // not open the first pass's blocks and count them as the install's.
        string root = Path.Combine(_out, $"world_project{Suffix}");

        try
        {
            WorldProject project = host.OpenProject(root);
            WorldBlock b = WorldProject.Capture(Client.Game.UO.FileManager.Maps, 0, OverlayBx, OverlayBy);
            _overlayReport["base_statics"] = b.Statics.Count;
            _overlayReport["base_trees"] = CountInChunk(OverlayTree, statics: true);
            for (int i = 0; i < 3; i++)
            {
                int c = 1 + i * 2;
                b.Statics.Add(new WorldStatic { Id = OverlayTree, X = (byte)c, Y = (byte)c, Z = b.LandZ[c * 8 + c] });
            }

            for (int i = 0; i < 64; i++)
            {
                b.LandId[i] = OverlayWater;
            }

            string path = project.WriteBlock(b);
            _overlayReport["block_file"] = path;

            // Round trip through disk: a fresh project object reads the JSON.
            host.OpenProject(root);
            int changed = host.ApplyOverlay();
            _overlayReport["blocks_applied"] = changed;
            _overlayReport["blocks_in_project"] = host.Project.Blocks(0).Count;
        }
        catch (Exception ex)
        {
            OverlayFail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private int CountInChunk(ushort graphic, bool statics)
    {
        var chunk = _world.Host.World.Map.GetChunk2(OverlayBx, OverlayBy, load: true);
        int n = 0;
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (var o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                {
                    if (o.Graphic == graphic && (statics ? o is GUO.Game.GameObjects.Static : o is GUO.Game.GameObjects.Land))
                    {
                        n++;
                    }
                }
            }
        }

        return n;
    }

    private void CheckOverlay()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        int trees = CountInChunk(OverlayTree, statics: true);
        int water = CountInChunk(OverlayWater, statics: false);
        _overlayReport["trees_in_chunk"] = trees;
        _overlayReport["water_in_chunk"] = water;
        int baseTrees = _overlayReport.TryGetValue("base_trees", out object bt) ? (int)bt : 0;
        if (trees != baseTrees + 3)
        {
            OverlayFail($"the chunk has {trees} trees of 0x{OverlayTree:X4}, expected {baseTrees + 3}");
        }

        if (water != 64)
        {
            OverlayFail($"the chunk has {water} water cells, expected 64");
        }

        Image frame = _world.Capture();
        if (frame != null && !frame.IsEmpty())
        {
            string path = Path.Combine(_out, $"world_overlay{Suffix}.png");
            frame.SavePng(path);
            _overlayReport["png"] = path;
        }
    }

    private void CloseOverlay()
    {
        if (_world.IsBooted)
        {
            _world.Host.CloseProject();
        }
    }

    private void CheckRestored()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        int trees = CountInChunk(OverlayTree, statics: true);
        _overlayReport["trees_after_close"] = trees;
        int baseTrees = _overlayReport.TryGetValue("base_trees", out object bt) ? (int)bt : 0;
        if (trees != baseTrees)
        {
            OverlayFail($"closing the project left {trees} trees, the install has {baseTrees}");
        }

        Dictionary<string, DateTime> after = InstallStamp();
        bool untouched = _installStamp != null && after.Count == _installStamp.Count
            && after.All(kv => _installStamp.TryGetValue(kv.Key, out DateTime t) && t == kv.Value);
        _overlayReport["install_untouched"] = untouched;
        if (!untouched)
        {
            OverlayFail("the install's map/statics files changed during the overlay check");
        }

        _overlayReport["ok"] = !_overlayReport.ContainsKey("failed");
    }

    private void OverlayFail(string why)
    {
        _overlayReport["failed"] = true;
        WorldFail($"overlay: {why}");
    }

    private void WorldFail(string why)
    {
        _worldReport["ok"] = false;
        _failures.Add($"World: {why}");
    }

    private static int Colours(Image img)
    {
        var seen = new HashSet<uint>();
        for (int y = 0; y < img.GetHeight(); y += 2)
        {
            for (int x = 0; x < img.GetWidth(); x += 2)
            {
                seen.Add(img.GetPixel(x, y).ToRgba32());
                if (seen.Count > 4096)
                {
                    return seen.Count;
                }
            }
        }

        return seen.Count;
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
