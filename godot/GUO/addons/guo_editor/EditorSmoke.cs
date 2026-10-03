#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// <c>facet,x,y,w,h</c>: skip the checks, show the World tab at that cell
    /// in a viewport of exactly w x h, and save <c>world_shot.png</c>. What
    /// tools/world_parity compares with a logged-in client's frame.
    /// </summary>
    public const string WorldShotFlag = "--guo-editor-world-shot";

    /// <summary>
    /// <c>send</c> or <c>follow</c>: the live tier check (tools/editor_live).
    /// Both open the World tab at the wilderness block and connect to the
    /// shard's bridge (<c>--guo-editor-live-port</c>). <c>send</c> waits for a
    /// <c>go</c> file, stamps a tree, and records the shard's acknowledgement
    /// and a GM command's reply (<c>--guo-editor-live-as</c>); <c>follow</c>
    /// waits for that block to arrive from the other editor.
    /// </summary>
    public const string LiveFlag = "--guo-editor-live";

    private const double TimeoutSeconds = 180;
    private const int SettleFrames = 45;

    private readonly string _out;
    private readonly EditorData _data;
    private readonly AssetsView _assets;
    private readonly InspectorDock _inspector;
    private readonly WorldView _world;
    private readonly ShardDock _shard;
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

    /// <summary>The F3 popup the plugin made, for the search checks.</summary>
    public SearchPopup Search { get; set; }

    /// <summary>The AI dock the plugin made, for the AI checks (ADR-0028).</summary>
    public AiDock Ai { get; set; }

    public EditorSmoke() : this(null, null, null, null, null, null)
    {
    }

    public EditorSmoke(string outDir, EditorData data, AssetsView assets, InspectorDock inspector, WorldView world, ShardDock shard)
    {
        _world = world;
        _shard = shard;
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

    internal static string ArgValue(string flag)
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
                    _stage = _failures.Count > 0 ? 9
                        : ArgValue(LiveFlag) != null ? 40
                        : ArgValue(WorldShotFlag) != null ? 30 : 1;
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
                    // Phase 4b: the F3 search, on the index the editor built.
                    _stage = 50;
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

            case 50:
                if (StepSearch())
                {
                    _stage = 60;
                    _frames = 0;
                }

                break;

            case 60:
                // The AI hub (ADR-0028): ACP, Ollama and queue, against stubs.
                if (StepAi())
                {
                    // Phase 5: the asset overlay, before the World tab boots
                    // so the world's own loaders get it too.
                    RunAssets();
                    _stage = 6;
                    _frames = 0;
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
                    CheckWorldAssets();
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

                    // Phase 6: the world-objects layer (ADR-0014).
                    RunObjects();
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
                    BuildEditScript();
                    _stage = 14;
                    _frames = 0;
                }

                break;

            case 14:
                // The edit script: each step waits its frames, then runs.
                if (_step >= _steps.Count)
                {
                    _world.ForcedMouse = null;
                    _world.Tool = WorldTool.Select;
                    _editReport["ok"] = !_editReport.ContainsKey("failed");
                    _stage = 9;
                }
                else if (_frames >= _steps[_step].Wait)
                {
                    try
                    {
                        _steps[_step].Run();
                    }
                    catch (Exception ex)
                    {
                        EditFail($"step {_step} threw {ex.GetType().Name}: {ex.Message}");
                    }

                    _step++;
                    _frames = 0;
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

            case 40:
                StartLive();
                _stage = 41;
                _frames = 0;
                _elapsed = 0;
                break;

            case 41:
                StepLive();
                break;

            case 30:
                StartWorldShot();
                _stage = 31;
                _frames = 0;
                break;

            case 31:
                if (_frames >= 90)
                {
                    SaveWorldShot();
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

        // --guo-editor-multi-dump 0x64,0x1000: also write those multis'
        // composites (what the panel and inspector draw) to <out>/multi_XXXX.png.
        if (panel is MultiPanel && ArgValue("--guo-editor-multi-dump") is string dump)
        {
            Directory.CreateDirectory(_out);
            foreach (string tok in dump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int mid = tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(tok[2..], 16) : int.Parse(tok);
                Image composite = MultiPanel.CompositeOf(_data, mid);
                composite?.SavePng(Path.Combine(_out, $"multi_{mid:X4}{Suffix}.png"));
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

    // --- live tier -------------------------------------------------------

    private readonly Dictionary<string, object> _live = new();
    private string _liveRole;
    private int _livePhase;
    private long _liveSent;

    // A cell on the wilderness block that a client standing at 1164,1668 sees
    // clear of its paperdoll (about 110 px right of centre).
    private const int LiveX = 1167, LiveY = 1666;

    /// <summary>The facet the live check edits (--guo-editor-live-facet; map0 by default).</summary>
    private int _liveFacet;

    private void StartLive()
    {
        _liveRole = ArgValue(LiveFlag);
        _liveFacet = int.TryParse(ArgValue("--guo-editor-live-facet"), out int lf) ? lf : 0;
        _live["facet"] = _liveFacet;
        _report["live"] = _live;
        _live["role"] = _liveRole;
        EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        _world.Visible = true;
        _world.OpenProject(Path.Combine(_out, $"world_project_{_liveRole}"));
        _world.GoTo(_liveFacet, EditX, EditY);
        _world.Guides.Blocks = false;

        int port = int.TryParse(ArgValue("--guo-editor-live-port"), out int p) ? p : 2595;
        if (!_shard.Connect("127.0.0.1", port, $"editor-{_liveRole}"))
        {
            _failures.Add($"live: could not connect to the bridge on {port}");
            Finish();
            return;
        }

        _live["port"] = port;
        File.WriteAllText(Path.Combine(_out, $"{_liveRole}.ready"), "");
    }

    private void StepLive()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_elapsed > 300)
        {
            _failures.Add($"live {_liveRole}: timed out in phase {_livePhase}");
            Finish();
            return;
        }

        if (_liveRole == "objects")
        {
            StepLiveObjects();
            return;
        }

        if (_liveRole == "follow")
        {
            if (_shard.LastRemote is { } r && r.Facet == _liveFacet && r.Bx == EditBx && r.By == EditBy)
            {
                _live["received_from"] = r.From;
                _live["received_ms"] = r.ReceivedMs;
                _live["latency_ms"] = r.LatencyMs;
                bool there = false;
                var chunk = _world.Host.World.Map.GetChunk2(EditBx, EditBy, load: true);
                for (var o = chunk?.GetHeadObject(LiveX & 7, LiveY & 7); o != null; o = o.TNext)
                {
                    there |= o is GUO.Game.GameObjects.Static && o.Graphic == 0x0CE3;
                }

                _live["tree_in_this_world"] = there;
                if (!there)
                {
                    _failures.Add("live follow: the other editor's tree is not in this world");
                }

                Finish();
            }

            return;
        }

        switch (_livePhase)
        {
            case 0:
                if (File.Exists(Path.Combine(_out, "go")))
                {
                    _data.CurrentArt = EditorData.LandCount + 0x0CE3;
                    _liveSent = now;
                    _live["sent_ms"] = now;
                    bool ok = _world.Editor.Stamp(_liveFacet, LiveX, LiveY, _world.Host.World.Map.GetTileZ(LiveX, LiveY), 0x0CE3, 0);
                    _live["stamped"] = ok;
                    _livePhase = 1;
                }

                break;

            case 1:
                if (_shard.LastAck is { } a)
                {
                    _live["ack_ms"] = a.AckMs;
                    _live["round_trip_ms"] = a.AckMs - _liveSent;
                    _live["pushed_to_clients"] = a.Clients;
                    _live["relayed_to_editors"] = a.Editors;
                    string who = ArgValue("--guo-editor-live-as");
                    if (!string.IsNullOrEmpty(who))
                    {
                        _shard.RunCommand(who, "[where");
                        _livePhase = 2;
                    }
                    else
                    {
                        _livePhase = 3;
                        _frames = 0;
                    }
                }

                break;

            case 2:
                if (_shard.LastCommand is JsonNode c)
                {
                    _live["command_ok"] = (bool)c["ok"];
                    _live["command"] = c.ToJsonString();
                    _livePhase = 3;
                    _frames = 0;
                }

                break;

            case 3:
                if (Headless || _frames >= SettleFrames)
                {
                    Image frame = _world.Capture();
                    if (frame != null && !frame.IsEmpty())
                    {
                        string path = Path.Combine(_out, $"world_live_{_liveRole}.png");
                        frame.SavePng(path);
                        _live["png"] = path;
                    }

                    Finish();
                }

                break;
        }
    }

    private void StartWorldShot()
    {
        string[] p = ArgValue(WorldShotFlag).Split(',');
        int facet = int.Parse(p[0]), x = int.Parse(p[1]), y = int.Parse(p[2]);
        var size = new Vector2I(int.Parse(p[3]), int.Parse(p[4]));
        EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        _world.Visible = true;
        _world.SetFixedSize(size);

        // The game's frame only: no editor guides in a parity shot.
        _world.Guides.Grid = false;
        _world.Guides.Altitude = false;
        _world.Guides.Blocks = false;
        if (!_world.GoTo(facet, x, y))
        {
            _failures.Add($"world shot: could not go to map{facet} {x},{y}: {_world.Error}");
        }

        // An optional sixth field: the season the shard gives this map.
        if (p.Length > 5 && Enum.TryParse(p[5], ignoreCase: true, out GUO.Game.Managers.Season season))
        {
            _world.Season = season;
        }

        _report["world_shot"] = new Dictionary<string, object>
        {
            ["facet"] = facet, ["x"] = x, ["y"] = y, ["size"] = new[] { size.X, size.Y }, ["season"] = _world.Season.ToString(),
        };
    }

    private void SaveWorldShot()
    {
        var shot = (Dictionary<string, object>)_report["world_shot"];
        Image frame = _world.Capture();
        if (frame == null || frame.IsEmpty())
        {
            _failures.Add("world shot: no frame (headless?)");
            return;
        }

        Directory.CreateDirectory(_out);
        string path = Path.Combine(_out, "world_shot.png");
        frame.SavePng(path);
        shot["png"] = path;
        shot["frame_size"] = new[] { frame.GetWidth(), frame.GetHeight() };
        shot["z"] = _world.Host.Z;
        shot["objects"] = _world.Host.Scene.RenderedObjectsCount;
        shot["project"] = _world.Host.Project?.Root;
        shot["project_blocks"] = _world.Host.Project?.Blocks(_world.Host.Facet).Count ?? 0;
        var chunk = _world.Host.World.Map.GetChunk(_world.Host.X, _world.Host.Y, load: true);
        var statics = new List<string>();
        for (int cx = 0; cx < 8; cx++)
        {
            for (int cy = 0; cy < 8; cy++)
            {
                for (var o = chunk?.GetHeadObject(cx, cy); o != null; o = o.TNext)
                {
                    if (o is GUO.Game.GameObjects.Static)
                    {
                        statics.Add($"0x{o.Graphic:X4}@{o.X},{o.Y},{o.Z}{(o.AllowedToDraw ? "" : " hidden")}");
                    }
                }
            }
        }

        shot["centre_chunk_statics"] = statics;
    }

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
        else
        {
            // The drawn total can fall as the house hides what is behind it,
            // so check the house itself: its components are in the map's
            // tiles and drawable.
            int inTiles = 0, drawable = 0;
            foreach (var m in h.Components)
            {
                var chunk = _world.Host.World.Map.GetChunk(m.X, m.Y, load: true);
                for (var o = chunk?.GetHeadObject(m.X % 8, m.Y % 8); o != null; o = o.TNext)
                {
                    if (ReferenceEquals(o, m))
                    {
                        inTiles++;
                        if (m.AllowedToDraw)
                        {
                            drawable++;
                        }

                        break;
                    }
                }
            }

            _worldReport["multi_components_in_tiles"] = inTiles;
            _worldReport["multi_components_drawable"] = drawable;
            if (inTiles != h.Components.Count || drawable == 0)
            {
                WorldFail($"the house's components are not in the map ({inTiles} of {h.Components.Count} in tiles, {drawable} drawable)");
            }
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
            WorldProject project = _world.OpenProject(root);
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
            _world.OpenProject(root);
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

    // --- phase 3: tools, undo, layers, guides, radar ------------------------

    // A wilderness block west of Britain (flat grass, no statics in the
    // install; tools/world found it), away from where probe characters stand.
    private const int EditX = 1164, EditY = 1668;
    private const int EditBx = EditX >> 3, EditBy = EditY >> 3;
    // A large crate: solid over the cell's centre, so a pick at the view's
    // centre lands on it. (A bare tree does not: the pixel falls between its
    // branches and the game's picking rightly returns the land.)
    private const ushort Tree = 0x0E3D, RedHue = 0x0021;
    private readonly Dictionary<string, object> _editReport = new();
    private readonly List<(int Wait, Action Run)> _steps = new();
    private int _step;
    private Color _radarBefore;

    private void EditFail(string why)
    {
        _editReport["failed"] = true;
        _failures.Add($"World edit: {why}");
    }

    private WorldBlock ProjectBlock() =>
        _world.Host.Project.BlockText(0, EditBx, EditBy) is null
            ? null
            : WorldProject.ReadBlock(_world.Host.Project.BlockPath(0, EditBx, EditBy));

    private int ChunkStatics(bool visibleOnly)
    {
        var chunk = _world.Host.World.Map.GetChunk2(EditBx, EditBy, load: true);
        int n = 0;
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (var o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                {
                    if (o is GUO.Game.GameObjects.Static && (!visibleOnly || o.AllowedToDraw))
                    {
                        n++;
                    }
                }
            }
        }

        return n;
    }

    private void Expect(bool ok, string what)
    {
        _editReport[what] = ok;
        if (!ok)
        {
            EditFail(what);
        }
    }

    /// <summary>
    /// Points the forced mouse at a static on the edit block, the way a user
    /// would aim at it: its art's lower middle, projected with the game's own
    /// camera. False when there is no such static.
    /// </summary>
    private bool AimAt(ushort id)
    {
        Vector2I? screen = WorldAim.ScreenOf(_world, _data, EditBx, EditBy, id);
        if (screen is not { } at)
        {
            return false;
        }

        _world.ForcedMouse = at;
        _editReport[$"aim_{id:X4}"] = new[] { at.X, at.Y };
        return true;
    }

    private Color RadarAt(int x, int y)
    {
        Image radar = _assets.Panel<MapPanel>()?.RadarImage;
        return radar == null ? new Color(0, 0, 0, 0) : radar.GetPixel(x / 4, y / 4);
    }

    private void BuildEditScript()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        _worldReport["edit"] = _editReport;
        int wait = Headless ? 3 : 20;
        string root = Path.Combine(_out, $"world_project_tools{Suffix}");
        string picked = null;

        _steps.Add((1, () =>
        {
            _world.OpenProject(root);
            _world.GoTo(0, EditX, EditY);
            _world.ForcedMouse = new Vector2I((int)_world.Size.X / 2, (int)(_world.Size.Y / 2));
            _data.CurrentArt = EditorData.LandCount + Tree;
            _radarBefore = RadarAt(EditX, EditY);
            _editReport["install_statics"] = ChunkStatics(false);
        }));

        // Stamp through the click path, on what the game picked.
        _steps.Add((wait, () =>
        {
            _world.Tool = WorldTool.Stamp;
            picked = _world.ApplyTool();
            _editReport["stamp"] = picked;
            WorldBlock b = ProjectBlock();
            Expect(b != null && b.Statics.Count(s => s.Id == Tree) == 1, "stamp_in_project");
        }));
        _steps.Add((wait, () => Expect(ChunkStatics(false) == (int)_editReport["install_statics"] + 1, "stamp_in_chunk")));

        // Hue on the stamped crate: aim at it, let a frame pick, then apply.
        _steps.Add((1, () => Expect(AimAt(Tree), "aim_at_stamp")));
        _steps.Add((wait, () =>
        {
            _world.Tool = WorldTool.Hue;
            _world.BrushHue = RedHue;
            _editReport["hue"] = _world.ApplyTool();
            Expect(ProjectBlock()?.Statics.Any(s => s.Id == Tree && s.Hue == RedHue) == true, "hue_in_project");
        }));

        // Raise the land under it.
        _steps.Add((wait, () =>
        {
            WorldBlock before = ProjectBlock();
            _world.Tool = WorldTool.Raise;
            _editReport["raise"] = _world.ApplyTool();
            WorldBlock after = ProjectBlock();
            int changed = 0;
            for (int i = 0; i < 64; i++)
            {
                if (after.LandZ[i] != before.LandZ[i])
                {
                    changed += after.LandZ[i] - before.LandZ[i];
                }
            }

            Expect(changed == 1, "raise_one_cell_by_one");
        }));

        // Erase the crate (aim again: raising the land moved it).
        _steps.Add((wait, () => AimAt(Tree)));
        _steps.Add((wait, () =>
        {
            _world.Tool = WorldTool.Erase;
            _editReport["erase"] = _world.ApplyTool();
            Expect(ProjectBlock()?.Statics.Count(s => s.Id == Tree) == 0, "erase_in_project");
        }));

        // Undo all four: the block is the install's again (no file).
        _steps.Add((wait, () =>
        {
            _editReport["undo_depth"] = _world.Editor.UndoCount;
            for (int i = 0; i < 4; i++)
            {
                _world.Editor.Undo();
            }

            Expect(ProjectBlock() == null, "undo_back_to_install");
        }));
        _steps.Add((wait, () => Expect(ChunkStatics(false) == (int)_editReport["install_statics"], "undo_chunk_is_install")));

        // Redo the stamp.
        _steps.Add((1, () =>
        {
            _world.Editor.Redo();
            Expect(ProjectBlock()?.Statics.Count(s => s.Id == Tree) == 1, "redo_stamp");
        }));

        // The radar: a direct stamp on the cell the 1:4 radar samples, then its pixel.
        _steps.Add((wait, () =>
        {
            _world.Editor.Stamp(0, EditX, EditY, 0, 0x0CE3, 0);

            // And one clear of the view's centre (about 154 px right of it),
            // so a logged-in client's frame shows it beside its paperdoll:
            // tools/world_parity's export proof looks for it there.
            _world.Editor.Stamp(0, EditX + 3, EditY - 4, 0, 0x0CE3, 0);
            Color after = RadarAt(EditX, EditY);
            _editReport["radar_before"] = _radarBefore.ToHtml();
            _editReport["radar_after"] = after.ToHtml();
            Expect(after != _radarBefore, "radar_shows_overlay");
        }));

        // Layers: statics off, then on.
        _steps.Add((1, () => _world.Host.ShowStatics = false));
        _steps.Add((wait, () =>
        {
            _editReport["statics_visible_when_off"] = ChunkStatics(true);
            Expect(ChunkStatics(true) == 0, "statics_layer_off");
            _world.Host.ShowStatics = true;
        }));
        _steps.Add((wait, () => Expect(ChunkStatics(true) == ChunkStatics(false), "statics_layer_on")));

        // Guides: grid, altitude and blocks on, then a frame.
        _steps.Add((1, () =>
        {
            _world.Guides.Grid = true;
            _world.Guides.Altitude = true;
            _world.Guides.Blocks = true;
        }));
        _steps.Add((wait, () =>
        {
            _editReport["guide_cells"] = _world.Guides.CellsDrawn;
            Expect(_world.Guides.CellsDrawn > 0, "guides_drawn");
            Image frame = _world.Capture();
            if (frame != null && !frame.IsEmpty())
            {
                string path = Path.Combine(_out, $"world_edit{Suffix}.png");
                frame.SavePng(path);
                _editReport["png"] = path;
            }

            _world.Guides.Grid = false;
            _world.Guides.Altitude = false;
            _editReport["block_file"] = _world.Host.Project.BlockPath(0, EditBx, EditBy);
        }));
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
