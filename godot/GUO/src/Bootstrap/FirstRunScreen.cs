namespace GUO.Host;

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The first-run screen (ADR-0021, G2): shown when no valid UO data was
/// found. It says GUO needs the player's own UO install and why none was
/// usable. It offers Choose folder (the platform's native dialog where there
/// is one, Godot's own otherwise), checks the chosen folder live against the
/// required set (DataRequirements), and Continue hands the folder back to the
/// caller, which saves it and goes on to login in the same run.
/// </summary>
/// <remarks>
/// GUO-owned; not in ClassicUO. Godot controls in the Store window's card
/// style (charcoal and gold), like Input/Touch/WindowMenu. It adds no
/// textures and leaves the project's nearest-neighbour default alone.
///
/// Re-opened from Options ("Change UO folder…", a marked PORT DEVIATION in
/// OptionsGump) through <see cref="OpenChange"/>: the same screen in change
/// mode, which saves the folder into upstream's settings and says it applies
/// on the next start.
///
/// On Android (G2a) Choose folder opens the system's folder picker (the
/// Storage Access Framework, through Godot's native dialog). The folder is
/// checked through the grant it returns, and Continue copies its game data
/// into GUO's own data folder, with progress, before going on (SafFolder).
///
/// On the web (ADR-0008, ADR-0021) Choose folder opens the browser's own
/// picker through tools/web/guo_data.js: the player's folder is read in place
/// by a worker and mounted at /uo_picked, and this screen checks it like any
/// other folder. Nothing is uploaded.
/// </remarks>
internal sealed partial class FirstRunScreen : CanvasLayer
{
    private static readonly Color Gold = new("dfbb77"), Text = new("eeeade"), Muted = new("abb5ac"),
        Good = new("8fc79a"), Bad = new("e08a7a"), Back = new("0b0e0d");

    private Action<string> _chosen;
    private bool _change;
    private Label _folder;
    private VBoxContainer _checks;
    private Label _verdict;
    private Button _continue;
    private string _picked;

    /// <summary>
    /// Shows the screen over the host. <paramref name="configured"/> names a
    /// configured install that was not valid (it keeps precedence on later
    /// runs, so the screen says so). <paramref name="probeFolder"/> scripts it:
    /// photographs, picks that folder without a dialog, photographs, continues.
    /// </summary>
    public static FirstRunScreen Open(Node host, DataSources.Result data, string configured, string probeFolder,
                                      string shotDir, Action<string> chosen, bool change = false)
    {
        var screen = new FirstRunScreen { _chosen = chosen, _change = change, Layer = 128 };
        host.AddChild(screen);
        screen.Build(data, configured);
        GD.Print($"[GUO] first run     : screen shown{(change ? " (change from Options)" : "")}");
        if (!string.IsNullOrWhiteSpace(probeFolder))
        {
            _ = screen.Probe(probeFolder, shotDir);
        }

        return screen;
    }

    /// <summary>
    /// Options' "Change UO folder…": the screen in change mode over the running
    /// client. Save stores the folder in upstream's settings.json
    /// (ultimaonlinedirectory); the client keeps its loaded data until the next
    /// start. <paramref name="probeFolder"/> scripts it as in <see cref="Open"/>.
    /// </summary>
    public static FirstRunScreen OpenChange(string probeFolder = null, string shotDir = null)
    {
        var host = ((SceneTree)Engine.GetMainLoop()).Root;
        string current = Configuration.Settings.GlobalSettings.UltimaOnlineDirectory;
        var data = new DataSources.Result();
        data.Notes.Add($"current folder {current}");
        return Open(host, data, null, probeFolder, shotDir, folder =>
        {
            Configuration.Settings.GlobalSettings.UltimaOnlineDirectory = folder;
            Configuration.Settings.GlobalSettings.Save();
            GD.Print($"[GUO] first run     : UO folder changed to {folder}; applies on the next start");
        }, change: true);
    }

    private void Build(DataSources.Result data, string configured)
    {
        var theme = new Theme { DefaultFontSize = 15 };
        var root = new Control { Theme = theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        root.AddChild(new ColorRect { Color = Back, AnchorRight = 1, AnchorBottom = 1 });

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(center);

        var card = new PanelContainer { CustomMinimumSize = new Vector2(560, 0) };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.078f, 0.098f, 0.090f, 1f),
            BorderColor = new Color(Gold, 0.45f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
            ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 10, ShadowOffset = new Vector2(0, 3),
            ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 20, ContentMarginBottom = 20,
        });
        center.AddChild(card);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        card.AddChild(col);

        col.AddChild(Label(_change ? "Change UO folder" : "Welcome to GUO", 24, Gold));
        col.AddChild(Label(
            "GUO plays Ultima Online with the game files from your own copy of the Classic client. "
            + "They are not included. Choose the folder where Ultima Online Classic is installed.", 15, Text, wrap: true));
        col.AddChild(new HSeparator { Modulate = new Color(Gold, 0.35f) });
        col.AddChild(Label(
            _change
                ? $"Now using: {Configuration.Settings.GlobalSettings.UltimaOnlineDirectory}. The folder you choose is used the next time GUO starts."
                : "Why this screen: " + data.Reason,
            13, Muted, wrap: true));
        if (configured != null)
        {
            col.AddChild(Label(
                $"{configured} is set and takes precedence on later runs; clear it for the folder you choose here to be used next time.",
                13, Muted, wrap: true));
        }

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        col.AddChild(row);
        Button choose = GoldButton("Choose folder…");
        choose.Pressed += ChooseFolder;
        row.AddChild(choose);
        _folder = Label("No folder chosen yet.", 14, Muted, wrap: true);
        _folder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _folder.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_folder);

        _checks = new VBoxContainer();
        col.AddChild(_checks);
        _verdict = Label("", 15, Muted, wrap: true);
        col.AddChild(_verdict);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        buttons.AddThemeConstantOverride("separation", 10);
        col.AddChild(buttons);
        if (_change)
        {
            Button cancel = GoldButton("Cancel");
            cancel.Pressed += QueueFree;
            buttons.AddChild(cancel);
        }

        _continue = GoldButton(_change ? "Save" : "Continue");
        _continue.Disabled = true;
        _continue.Pressed += () => Finish();
        buttons.AddChild(_continue);
    }

    private static Label Label(string text, int size, Color color, bool wrap = false)
    {
        var l = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static Button GoldButton(string text)
    {
        static StyleBoxFlat Box(string bg, string border) => new()
        {
            BgColor = new Color(bg), BorderColor = new Color(border),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8,
        };

        var b = new Button { Text = text, CustomMinimumSize = new Vector2(150, 40) };
        b.AddThemeStyleboxOverride("normal", Box("b58e42", "dfbb77"));
        b.AddThemeStyleboxOverride("hover", Box("c79d4d", "f0d08a"));
        b.AddThemeStyleboxOverride("pressed", Box("9e7a36", "dfbb77"));
        b.AddThemeStyleboxOverride("disabled", Box("3a3f39", "56604f"));
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color" })
        {
            b.AddThemeColorOverride(c, new Color("141917"));
        }

        b.AddThemeColorOverride("font_disabled_color", Muted);
        b.AddThemeFontSizeOverride("font_size", 16);
        return b;
    }

    private static bool Web => OS.HasFeature("web");

    private static bool Android => OS.GetName() == "Android";

    private List<string> _treeFiles;

    private int _webPicks;

    public override void _Process(double delta)
    {
        // A pick made in the page (the folder dialog, or a test filling the
        // folder input) is checked as soon as it is mounted.
        if (!Web)
        {
            return;
        }

        Variant count = JavaScriptBridge.Eval("(window.guoWeb && window.guoWeb.pickCount) || 0");
        int picks = count.VariantType is Variant.Type.Int or Variant.Type.Float ? (int)count.AsDouble() : 0;
        if (picks != _webPicks)
        {
            _webPicks = picks;
            string root = (string)JavaScriptBridge.Eval("window.guoWeb.pickRoot");
            string label = (string)JavaScriptBridge.Eval("window.guoWeb.picked ? window.guoWeb.picked.label : ''");
            Pick(root);
            _folder.Text = $"{label} (read in this browser; nothing is uploaded)";
        }
    }

    private void ChooseFolder()
    {
        if (Web)
        {
            // Inside the click's user activation, which the browser's picker needs.
            JavaScriptBridge.Eval("window.guoWeb && window.guoWeb.pickFolder()");
            return;
        }

        string start = Android ? "" : _picked ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        if (DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile))
        {
            DisplayServer.FileDialogShow("Choose your Ultima Online Classic folder", start ?? "", "", false,
                DisplayServer.FileDialogMode.OpenDir, Array.Empty<string>(),
                Callable.From<bool, string[], int>((ok, paths, _) =>
                {
                    GD.Print($"[GUO] first run     : folder dialog {(ok ? "returned " + string.Join(", ", paths) : "cancelled")}");
                    if (ok && paths.Length > 0)
                    {
                        if (SafFolder.IsTree(paths[0]))
                        {
                            PickTree(paths[0]);
                        }
                        else
                        {
                            Pick(paths[0]);
                        }
                    }
                }));
            return;
        }

        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Choose your Ultima Online Classic folder",
            CurrentDir = start ?? "",
            UseNativeDialog = false,
        };
        dialog.DirSelected += dir =>
        {
            Pick(dir);
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(720, 480));
    }

    /// <summary>Checks a folder live and enables Continue when it is valid.</summary>
    public void Pick(string folder)
    {
        _picked = folder;
        _folder.Text = folder;
        _folder.AddThemeColorOverride("font_color", Text);
        foreach (Node n in _checks.GetChildren())
        {
            n.QueueFree();
        }

        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        _checks.AddChild(grid);
        var check = DataSources.Check(folder);
        foreach ((string key, string form) in check)
        {
            grid.AddChild(Label((form != null ? "✓ " : "✗ ") + key + (form != null ? $" ({form})" : ""), 13, form != null ? Good : Bad));
        }

        int missing = check.Count(c => c.Form == null);
        bool ok = Directory.Exists(folder) && missing == 0;
        _verdict.Text = ok
            ? $"Found: all {check.Count} required files are there."
            : !Directory.Exists(folder)
                ? "That folder does not exist."
                : $"Missing {missing} of {check.Count} required files. This does not look like an Ultima Online Classic folder.";
        _verdict.AddThemeColorOverride("font_color", ok ? Good : Bad);
        _continue.Disabled = !ok;
        GD.Print($"[GUO] first run     : picked {folder}: {(ok ? "valid" : $"{missing} missing")}");
    }

    /// <summary>Android: checks a folder picked through the Storage Access Framework.</summary>
    public void PickTree(string tree)
    {
        _picked = tree;
        _folder.Text = SafFolder.Label(tree);
        _folder.AddThemeColorOverride("font_color", Text);
        foreach (Node n in _checks.GetChildren())
        {
            n.QueueFree();
        }

        List<string> files = SafFolder.Files(tree, out bool listed);
        _treeFiles = files.Where(SafFolder.IsData).ToList();
        var check = SafFolder.Check(files);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        _checks.AddChild(grid);
        foreach ((string key, string form) in check)
        {
            grid.AddChild(Label((form != null ? "✓ " : "✗ ") + key + (form != null ? $" ({form})" : ""), 13, form != null ? Good : Bad));
        }

        int missing = check.Count(c => c.Form == null);
        bool ok = missing == 0;
        _verdict.Text = ok
            ? $"Found: all {check.Count} required files are there. Continue copies the game data ({_treeFiles.Count} files) "
              + "into GUO's own folder once; your folder is only read."
            : $"Missing {missing} of {check.Count} required files. This does not look like an Ultima Online Classic folder.";
        _verdict.AddThemeColorOverride("font_color", ok ? Good : Bad);
        _continue.Disabled = !ok;
        GD.Print($"[GUO] first run     : picked {tree} ({(listed ? $"listed, {files.Count} files, {_treeFiles.Count} of them data" : "not listable; required names probed")}): "
                 + (ok ? "valid" : $"{missing} missing"));
    }

    private async Task FinishTree()
    {
        string dest = DataSources.PlatformDefaults().FirstOrDefault();
        if (dest == null)
        {
            return;
        }

        _continue.Disabled = true;
        _verdict.AddThemeColorOverride("font_color", Text);
        string failed = await SafFolder.Copy(_picked, _treeFiles, dest, (done, total, name) =>
        {
            _verdict.Text = $"Copying the game data: {done / (1 << 20)} of {total / (1 << 20)} MB ({(total > 0 ? done * 100 / total : 0)}%), {name}";
        });

        if (failed != null || DataSources.Validate(dest) != null)
        {
            string why = failed ?? DataSources.Validate(dest);
            _verdict.Text = $"The copy did not finish: {why}. Choose the folder again to retry.";
            _verdict.AddThemeColorOverride("font_color", Bad);
            GD.PrintErr($"[GUO] first run     : copy into {dest} failed: {why}");
            return;
        }

        SafFolder.Release(_picked);
        QueueFree();
        _chosen?.Invoke(dest);
    }

    private void Finish()
    {
        if (SafFolder.IsTree(_picked))
        {
            _ = FinishTree();
            return;
        }

        if (_picked == null || DataSources.Validate(_picked) != null)
        {
            return;
        }

        string folder = _picked;
        QueueFree();
        _chosen?.Invoke(folder);
    }

    private async Task Probe(string folder, string shotDir)
    {
        if (folder == "web")
        {
            await ProbeWeb(shotDir);
            return;
        }

        string dir = string.IsNullOrWhiteSpace(shotDir) ? ProjectSettings.GlobalizePath("user://") : shotDir;
        Directory.CreateDirectory(dir);
        await Shot(Path.Combine(dir, "first_run_1_screen.png"));
        Pick(Path.Combine(Path.GetTempPath(), "guo-not-a-uo-folder"));
        await Shot(Path.Combine(dir, "first_run_2_bad_pick.png"));
        Pick(folder);
        await Shot(Path.Combine(dir, "first_run_3_good_pick.png"));
        GD.Print($"[GUO] first run     : probe pressing {_continue.Text} ({(_continue.Disabled ? "disabled" : "enabled")})");
        _continue.EmitSignal(BaseButton.SignalName.Pressed);
    }

    /// <summary>
    /// The web's probe: the screen, then a folder picked from outside (a test
    /// fills the page's folder input), checked, and Continue pressed.
    /// </summary>
    private async Task ProbeWeb(string shotDir)
    {
        string dir = string.IsNullOrWhiteSpace(shotDir) ? ProjectSettings.GlobalizePath("user://") : shotDir;
        Directory.CreateDirectory(dir);
        GD.Print("[GUO] first run     : web probe waiting for a picked folder");
        for (int i = 0; i < 20 * 60 && _webPicks == 0; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await Shot(Path.Combine(dir, "first_run_web_picked.png"));
        GD.Print($"[GUO] first run     : web probe pressing {_continue.Text} ({(_continue.Disabled ? "disabled" : "enabled")})");
        if (!_continue.Disabled)
        {
            _continue.EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    private async Task Shot(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }

        Image frame = GetViewport().GetTexture().GetImage();
        frame.SavePng(path);
        GD.Print($"[GUO] first run     : screenshot {path}");
    }
}
