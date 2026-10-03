#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Bulk import/export (Epic H, H2): unpacks client assets to PNG plus JSON
/// sidecars, and packs an edited folder into a staged data set (ADR-0022).
/// The work is <c>tools/uopack/run.py</c>'s. This panel runs it as a
/// subprocess and shows its <c>[uopack]</c> lines. Exit 0 means every record
/// was decoded back equal to its PNGs and the install is unchanged.
/// </summary>
/// <remarks>
/// Nothing here writes to <c>UO_CLIENT_DATA</c>. An output folder or stage
/// inside it is refused before the tool runs, and the tools refuse it again.
/// The verbs are fixed (<c>unpack</c>, <c>pack</c>, and uodata_write's
/// <c>verify</c>); only the ids and folders come from input.
/// </remarks>
[Tool]
public partial class BulkPanel : AssetPanel
{
    private static readonly string[] Kinds = { "art", "land", "gumps", "anim", "tiledata" };

    private OptionButton _kind;
    private LineEdit _ids;
    private LineEdit _unpackOut;
    private LineEdit _packIn;
    private LineEdit _stage;
    private readonly List<Button> _buttons = new();
    private RichTextLabel _log;
    private Label _status;
    private bool _busy;

    public override string SmokeQuery => "0x0E75";

    public override bool SmokeNeedsImage => true;

    public override void _Ready() => EnsureUi();

    private void EnsureUi()
    {
        if (_kind != null)
        {
            return;
        }

        string build = Path.Combine(EditorData.RepoRoot, "build");

        AddChild(new Label { Text = "Unpack: client assets to PNG + JSON sidecars" });
        var row = new HBoxContainer();
        AddChild(row);
        _kind = new OptionButton();
        foreach (string k in Kinds)
        {
            _kind.AddItem(k);
        }

        row.AddChild(_kind);
        _ids = new LineEdit { PlaceholderText = "ids: 0x1B74,50581,400-410", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_ids);
        _unpackOut = Folder(Path.Combine(build, "uopack", "work"));
        AddChild(_unpackOut);
        Button("Unpack", this, () => RunAsync("unpack", UnpackArgs(_ids.Text), _unpackOut.Text));

        AddChild(new HSeparator());
        AddChild(new Label { Text = "Pack: an edited folder into a staged data set (the install is never written)" });
        _packIn = Folder(Path.Combine(build, "uopack", "work"));
        AddChild(_packIn);
        _stage = Folder(Path.Combine(build, "uodata", "moshu"));
        AddChild(_stage);
        var packRow = new HBoxContainer();
        AddChild(packRow);
        Button("Pack into stage", packRow, () => RunAsync("pack", PackArgs(), _stage.Text));
        Button("Verify stage", packRow, () => RunAsync("verify", VerifyArgs(), _stage.Text));
        Button("Open folder", packRow, () => OS.ShellOpen(_stage.Text));

        _status = new Label { Text = "uopack: tools/uopack/run.py", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_status);
        _log = new RichTextLabel
        {
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 160),
        };
        AddChild(_log);
    }

    /// <summary>What the Verify stage button does (F3 runs it on Enter).</summary>
    public void RunVerify()
    {
        EnsureUi();
        RunAsync("verify", VerifyArgs(), _stage.Text);
    }

    private static LineEdit Folder(string path) =>
        new() { Text = path, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "a folder" };

    private void Button(string text, Node parent, Action run)
    {
        var b = new Button { Text = text };
        b.Pressed += run;
        parent.AddChild(b);
        _buttons.Add(b);
    }

    public override void OnDataLoaded() => EnsureUi();

    private List<string> UnpackArgs(string ids) =>
        new() { Tool("uopack"), "unpack", "--what", Kinds[_kind.Selected], "--ids", ids.Trim(), "--out", _unpackOut.Text };

    private List<string> PackArgs() => new() { Tool("uopack"), "pack", _packIn.Text, "--stage", _stage.Text };

    private List<string> VerifyArgs() => new() { Tool("uodata_write"), "verify", "--stage", _stage.Text };

    private static string Tool(string name) => Path.Combine(EditorData.RepoRoot, "tools", name, "run.py");

    /// <summary>Why a folder cannot be written, or null: it must not be inside the install.</summary>
    private static string Refuse(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "no folder given";
        }

        string install = EditorData.Setting("UO_CLIENT_DATA", "");
        if (install.Length == 0)
        {
            return null;
        }

        string a = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string b = Path.GetFullPath(install).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? $"refused: {folder} is inside the UO install" : null;
    }

    private void RunAsync(string what, List<string> args, string writes)
    {
        if (_busy)
        {
            _status.Text = "already running";
            return;
        }

        if (what == "unpack" && _ids.Text.Trim().Length == 0)
        {
            _status.Text = "type ids to unpack";
            return;
        }

        if (Refuse(writes) is string why)
        {
            _status.Text = why;
            return;
        }

        SetBusy(true);
        _log.Clear();
        _status.Text = $"{what}...";
        Task.Run(() =>
        {
            (int code, string error) = Run(args, line => Callable.From(() => _log.AppendText(line + "\n")).CallDeferred());
            Callable.From(() =>
            {
                SetBusy(false);
                _status.Text = error ?? (code == 0 ? $"{what}: done (exit 0)" : $"{what}: FAILED (exit {code}); see the lines above");
            }).CallDeferred();
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Button b in _buttons)
        {
            b.Disabled = busy;
        }
    }

    /// <summary>Runs python with the arguments; each output line goes to <paramref name="line"/>.</summary>
    private static (int Code, string Error) Run(List<string> args, Action<string> line)
    {
        // UO_PYTHON is config.bat's key; most Linux distributions ship only
        // "python3", so that is the default there.
        var psi = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", OperatingSystem.IsWindows() ? "python" : "python3"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = EditorData.RepoRoot,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process p = new() { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    line(e.Data);
                }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    line(e.Data);
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(30 * 60_000))
            {
                p.Kill();
                return (-1, "timed out after 30 minutes");
            }

            p.WaitForExit();
            return (p.ExitCode, null);
        }
        catch (Exception ex)
        {
            return (-1, $"could not run python: {ex.Message}");
        }
    }

    /// <summary>
    /// For the smoke check: unpacks one static synchronously into
    /// build/editor_bulk and inspects the PNG it wrote. Returns the id, or null.
    /// </summary>
    public override int? Search(string text)
    {
        EnsureUi();
        if (!TryParseId(text, out int id))
        {
            return null;
        }

        string outDir = Path.Combine(EditorData.RepoRoot, "build", "editor_bulk", "smoke");
        _kind.Selected = 0;
        _ids.Text = text;
        _unpackOut.Text = outDir;
        var lines = new List<string>();
        (int code, string error) = Run(UnpackArgs(text), lines.Add);
        _log.Clear();
        _log.AppendText(string.Join("\n", lines));
        string png = Directory.Exists(Path.Combine(outDir, "art"))
            ? Directory.GetFiles(Path.Combine(outDir, "art"), $"static_0x{id:x4}.png").FirstOrDefault()
            : null;
        if (code != 0 || png == null)
        {
            _status.Text = error ?? $"unpack FAILED (exit {code})";
            return null;
        }

        _status.Text = "unpack: done (exit 0)";
        Raise(Inspection.Still("Bulk", $"0x{id:X4}", Image.LoadFromFile(png),
            $"unpacked by tools/uopack to {png}\n{string.Join("\n", lines.TakeLast(4))}"));
        return id;
    }
}
#endif
