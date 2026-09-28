namespace GUO.Host;

using System;
using System.IO;
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
/// Re-opening from Options (later): call <see cref="Open"/> with a Result
/// whose Reason says "change the UO folder" and a callback that saves and asks
/// for a restart; the Options gump is ported code, so that button is a marked
/// PORT DEVIATION when it is added. The Android folder picker (SAF) and the web
/// are not handled here.
/// </remarks>
internal sealed partial class FirstRunScreen : CanvasLayer
{
    private static readonly Color Gold = new("dfbb77"), Text = new("eeeade"), Muted = new("abb5ac"),
        Good = new("8fc79a"), Bad = new("e08a7a"), Back = new("0b0e0d");

    private Action<string> _chosen;
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
                                      string shotDir, Action<string> chosen)
    {
        var screen = new FirstRunScreen { _chosen = chosen, Layer = 128 };
        host.AddChild(screen);
        screen.Build(data, configured);
        GD.Print("[GUO] first run     : screen shown");
        if (!string.IsNullOrWhiteSpace(probeFolder))
        {
            _ = screen.Probe(probeFolder, shotDir);
        }

        return screen;
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

        col.AddChild(Label("Welcome to GUO", 24, Gold));
        col.AddChild(Label(
            "GUO plays Ultima Online with the game files from your own copy of the Classic client. "
            + "They are not included. Choose the folder where Ultima Online Classic is installed.", 15, Text, wrap: true));
        col.AddChild(new HSeparator { Modulate = new Color(Gold, 0.35f) });
        col.AddChild(Label("Why this screen: " + data.Reason, 13, Muted, wrap: true));
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

        _continue = GoldButton("Continue");
        _continue.Disabled = true;
        _continue.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _continue.Pressed += () => Finish();
        col.AddChild(_continue);
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

    private void ChooseFolder()
    {
        string start = _picked ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        if (DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile))
        {
            DisplayServer.FileDialogShow("Choose your Ultima Online Classic folder", start ?? "", "", false,
                DisplayServer.FileDialogMode.OpenDir, Array.Empty<string>(),
                Callable.From<bool, string[], int>((ok, paths, _) =>
                {
                    if (ok && paths.Length > 0)
                    {
                        Pick(paths[0]);
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

    private void Finish()
    {
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
        string dir = string.IsNullOrWhiteSpace(shotDir) ? ProjectSettings.GlobalizePath("user://") : shotDir;
        Directory.CreateDirectory(dir);
        await Shot(Path.Combine(dir, "first_run_1_screen.png"));
        Pick(Path.Combine(Path.GetTempPath(), "guo-not-a-uo-folder"));
        await Shot(Path.Combine(dir, "first_run_2_bad_pick.png"));
        Pick(folder);
        await Shot(Path.Combine(dir, "first_run_3_good_pick.png"));
        GD.Print($"[GUO] first run     : probe pressing Continue ({(_continue.Disabled ? "disabled" : "enabled")})");
        _continue.EmitSignal(BaseButton.SignalName.Pressed);
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
