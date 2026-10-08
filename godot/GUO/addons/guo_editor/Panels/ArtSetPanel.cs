#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Builds the extracted art set (ADR-0034, data_formats section 36) from the UO
/// Assets dock: export, then verify, both <c>tools/art_extract/run.py</c>'s.
/// The panel runs the tool as a subprocess and shows its lines as progress.
/// No AI is involved, so this panel does not use the AI gate.
/// </summary>
/// <remarks>
/// The tool writes only under <c>UO_ART_EXTRACT_DIR</c> and refuses any other
/// output folder; the verbs are fixed (<c>export</c>, <c>verify</c>, <c>where</c>),
/// and only the class list comes from the check boxes. The install is never written.
/// </remarks>
[Tool]
public partial class ArtSetPanel : AssetPanel
{
    private static readonly string[] Classes = { "art", "gumps", "texmaps", "lights", "anim" };

    private readonly Dictionary<string, CheckBox> _what = new();
    private readonly List<Button> _buttons = new();
    private Label _where;
    private Label _status;
    private RichTextLabel _log;
    private bool _busy;

    public override string SmokeQuery => "";

    public override bool SmokeNeedsImage => false;

    public override void _Ready() => EnsureUi();

    private void EnsureUi()
    {
        if (_status != null)
        {
            return;
        }

        AddChild(new Label
        {
            Text = "Extracted art set: your install's art as atlas pages on this machine. The client reads it only when started with --art-set.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _where = new Label { Text = "folder: ..." };
        AddChild(_where);

        var row = new HBoxContainer();
        AddChild(row);
        foreach (string c in Classes)
        {
            var box = new CheckBox { Text = c, ButtonPressed = true };
            _what[c] = box;
            row.AddChild(box);
        }

        var buttons = new HBoxContainer();
        AddChild(buttons);
        Button("Export + verify", buttons, () => RunAsync(export: true));
        Button("Verify only", buttons, () => RunAsync(export: false));
        Button("Open folder", buttons, OpenFolder);

        _status = new Label { Text = "tools/art_extract/run.py", AutowrapMode = TextServer.AutowrapMode.WordSmart };
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

    private void Button(string text, Node parent, Action run)
    {
        var b = new Button { Text = text };
        b.Pressed += run;
        parent.AddChild(b);
        _buttons.Add(b);
    }

    public override void OnDataLoaded()
    {
        EnsureUi();
        _where.Text = $"folder: {SetFolder()}";
    }

    public override int? Search(string text) => null;

    private static string Tool() => Path.Combine(EditorData.RepoRoot, "tools", "art_extract", "run.py");

    /// <summary>UO_ART_EXTRACT_DIR as the tool resolves it (it owns the default), else the editor's own setting.</summary>
    private static string SetFolder()
    {
        var lines = new List<string>();
        (int code, _) = Run(new List<string> { Tool(), "where" }, lines.Add, 30_000);
        const string key = "UO_ART_EXTRACT_DIR = ";
        if (code == 0)
        {
            foreach (string l in lines)
            {
                if (l.StartsWith(key, StringComparison.Ordinal))
                {
                    return l.Substring(key.Length).Trim();
                }
            }
        }

        return EditorData.Setting("UO_ART_EXTRACT_DIR", "");
    }

    private void OpenFolder()
    {
        string dir = SetFolder();
        if (dir.Length > 0 && Directory.Exists(dir))
        {
            OS.ShellOpen(dir);
        }
        else
        {
            _status.Text = $"no set yet: {dir}";
        }
    }

    private string WhatList()
    {
        var picked = new List<string>();
        foreach (string c in Classes)
        {
            if (_what[c].ButtonPressed)
            {
                picked.Add(c);
            }
        }

        return string.Join(",", picked);
    }

    private void RunAsync(bool export)
    {
        if (_busy)
        {
            _status.Text = "already running";
            return;
        }

        string what = WhatList();
        if (what.Length == 0)
        {
            _status.Text = "tick at least one class";
            return;
        }

        SetBusy(true);
        _log.Clear();
        Task.Run(() =>
        {
            int code = 0;
            string error = null;
            string step = "export";
            if (export)
            {
                Status("export...");
                (code, error) = Run(new List<string> { Tool(), "export", "--what", what }, Append, 60 * 60_000);
            }

            if (code == 0 && error == null)
            {
                step = "verify";
                Status("verify...");
                (code, error) = Run(new List<string> { Tool(), "verify", "--what", what }, Append, 60 * 60_000);
            }

            Callable.From(() =>
            {
                SetBusy(false);
                _status.Text = error ?? (code == 0 ? "done: set written and verified (exit 0)" : $"{step}: FAILED (exit {code}); see the lines above");
                _where.Text = $"folder: {SetFolder()}";
            }).CallDeferred();
        });
    }

    private void Append(string line) => Callable.From(() => _log.AppendText(line + "\n")).CallDeferred();

    private void Status(string text) => Callable.From(() => _status.Text = text).CallDeferred();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Button b in _buttons)
        {
            b.Disabled = busy;
        }
    }

    private static (int Code, string Error) Run(List<string> args, Action<string> line, int timeoutMs)
    {
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
            if (!p.WaitForExit(timeoutMs))
            {
                p.Kill(true);
                return (-1, $"timed out after {timeoutMs / 60_000} minutes");
            }

            p.WaitForExit();
            return (p.ExitCode, null);
        }
        catch (Exception ex)
        {
            return (-1, $"could not run python: {ex.Message}");
        }
    }
}
#endif
