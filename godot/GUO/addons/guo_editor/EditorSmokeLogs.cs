#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using GUO.Workspace;

/// <summary>
/// The smoke stage for the Logs dock. Nothing here touches a real server, client or log: the fixture
/// logs are written under the smoke output folder, a second <see cref="LogsPanel"/> follows them (with
/// the real dock left alone), and its settings file is in the same folder. It proves: a file that does
/// not exist yet and then does, appended lines read while another handle holds the file open, follow
/// and pause, the text filter and the level filter, level colours, find, secrets hidden, truncation and
/// rotation, the line cap, a huge file entered at its end, UTC lines with local time beside them, the
/// remembered extra files, "clear view" leaving the file alone, and every worker task gone after
/// shutdown.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _logsReport = new();
    private Task _logsTask;
    private readonly Stopwatch _logsClock = new();

    /// <summary>The Logs dock the plugin made, for the checks.</summary>
    public LogsDock Logs { get; set; }

    private void LogsFail(string why)
    {
        _logsReport["ok"] = false;
        _failures.Add($"Logs: {why}");
        GD.Print($"[GUO editor] smoke Logs FAIL: {why}");
    }

    private void LogsCheck(string name, bool ok, string detail = "")
    {
        _logsReport[name] = ok;
        if (!ok)
        {
            LogsFail($"{name} {detail}".Trim());
        }
    }

    /// <summary>One tick of the stage; true when finished.</summary>
    private bool StepLogs()
    {
        if (System.Environment.GetEnvironmentVariable("GUO_LOGS_SKIP") != null || Logs == null)
        {
            return true;
        }

        if (_logsTask == null)
        {
            _logsReport["ok"] = true;
            _logsTask = RunLogsAsync();
            _logsClock.Restart();
        }

        if (_logsTask.IsCompleted)
        {
            if (_logsTask.IsFaulted)
            {
                LogsFail($"threw {_logsTask.Exception?.GetBaseException().GetType().Name}: {_logsTask.Exception?.GetBaseException().Message}");
                _logsReport["exception_stack"] = _logsTask.Exception?.GetBaseException().StackTrace;
                GD.PrintErr(_logsTask.Exception?.GetBaseException().StackTrace);
            }

            _report["logs"] = _logsReport;
            return true;
        }

        if (_logsClock.Elapsed.TotalSeconds > 90)
        {
            LogsFail("the stage did not finish within 90 s");
            _report["logs"] = _logsReport;
            return true;
        }

        return false;
    }

    private static bool LogHas(LogView v, string text) => v.Lines.Any(l => l.Text.Contains(text, StringComparison.Ordinal));

    private async Task<bool> LogWait(Func<bool> done, double seconds = 6) => await Until(done, seconds);

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static void LogAppend(FileStream writer, params string[] lines)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
    }

    private async Task RunLogsAsync()
    {
        string dir = Path.Combine(_out, "logs");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);

        // The real dock first: it exists, is in the tree and found its sources by itself.
        LogsCheck("dock_in_tree", Logs.IsInsideTree() && Logs.Panel != null);
        Logs.Panel?.Refresh();
        LogsCheck("dock_sources", Logs.Panel != null && Logs.Panel.View("server") != null && Logs.Panel.View("godot") != null && Logs.Panel.View("clientfiles") != null,
            string.Join(",", Logs.Panel?.Views.Keys ?? Enumerable.Empty<string>()));
        LogsCheck("dock_server_console", Logs.Panel?.View("serverconsole") != null);

        await CheckManagedConsoleAsync(dir);

        int baseline = LogTailer.Live;
        var panel = new LogsPanel { AutoDiscover = false, SettingsPath = Path.Combine(dir, "sources.json"), PollMilliseconds = 50 };
        AddChild(panel);
        await Delay(0.1);

        // --- a file that does not exist yet, then does -----------------------------------------------------------
        string late = Path.Combine(dir, "late.log");
        LogView lateView = panel.AddSource("late", "late", new LogTailer(late) { PollMilliseconds = 50 });
        await Delay(0.4);
        LogsCheck("missing_waits", lateView.Tailer.State.StartsWith("waiting", StringComparison.Ordinal) && lateView.BufferCount == 0, lateView.Tailer.State);
        File.WriteAllText(late, "first line\n");
        LogsCheck("missing_then_created", await LogWait(() => LogHas(lateView, "first line")) && lateView.Tailer.State == "following");

        // --- follow, append while another handle holds the file open ---------------------------------------------
        string serverLog = Path.Combine(dir, "server.log");
        File.WriteAllText(serverLog, "2026-10-02 12:00:00 server started\nWARNING: disk low\nERROR: boom happened\n");
        using var writer = new FileStream(serverLog, FileMode.Append, System.IO.FileAccess.Write, FileShare.ReadWrite);
        LogView v = panel.AddSource("server", "server", new LogTailer(serverLog) { PollMilliseconds = 50 });
        LogsCheck("initial_lines", await LogWait(() => v.BufferCount >= 3), $"{v.BufferCount} lines");
        LogAppend(writer, "saved world", "player joined");
        LogsCheck("append_read_while_open", await LogWait(() => LogHas(v, "player joined")));
        var texts = v.Lines.Select(l => l.Text).ToList();
        LogsCheck("order_kept", texts.IndexOf("saved world") == texts.IndexOf("ERROR: boom happened") + 1 && texts.Last() == "player joined");

        v.SetFollow(true);
        LogsCheck("follow_on", v.FollowOn && v.BodyFollowing);
        v.SetPaused(true);
        LogAppend(writer, "while paused");
        await Delay(0.5);
        LogsCheck("pause_holds_view", !LogHas(v, "while paused") && v.StatusText.Contains("paused", StringComparison.Ordinal), v.StatusText);
        v.SetPaused(false);
        LogsCheck("resume_delivers", await LogWait(() => LogHas(v, "while paused")));

        // --- level colouring and filters --------------------------------------------------------------------------
        LogLine err = v.Lines.First(l => l.Text.StartsWith("ERROR", StringComparison.Ordinal));
        LogLine warn = v.Lines.First(l => l.Text.StartsWith("WARNING", StringComparison.Ordinal));
        LogLine info = v.Lines.First(l => l.Text == "saved world");
        LogsCheck("levels_classified", err.Level == LogLevel.Error && warn.Level == LogLevel.Warn && info.Level == LogLevel.Info);
        LogsCheck("level_colours_differ", LogView.ColorFor(LogLevel.Error) != LogView.ColorFor(LogLevel.Warn) && LogView.ColorFor(LogLevel.Warn) != LogView.ColorFor(LogLevel.Info));

        v.SetFilter("boom");
        LogsCheck("text_filter", v.ShownCount == 1 && v.BodyText.Contains("boom", StringComparison.Ordinal) && !v.BodyText.Contains("saved world", StringComparison.Ordinal), $"{v.ShownCount} shown");
        v.SetFilter("");
        v.SetMinLevel(LogLevel.Warn);
        LogsCheck("level_filter_warn", v.ShownCount == 2 && !v.BodyText.Contains("saved world", StringComparison.Ordinal), $"{v.ShownCount} shown");
        v.SetMinLevel(LogLevel.Error);
        LogsCheck("level_filter_error", v.ShownCount == 1);
        v.SetMinLevel(LogLevel.Info);
        LogsCheck("filters_reset", v.ShownCount == v.BufferCount);

        v.SetFind("disk");
        LogsCheck("find", v.FindMatches == 1 && v.FindStep(1) >= 0, $"{v.FindMatches} matches");
        v.SetFind("");

        // --- secrets ----------------------------------------------------------------------------------------------
        LogAppend(writer, "login ok password=hunter2abc user=bob", "Authorization: Bearer abcdefghijklmnop1234567890",
            "key is sk-abcdefghijklmnopqrstuvwxyz0123", "{\"api_key\": \"AbCdEf123456\"}");
        LogsCheck("secrets_arrive", await LogWait(() => LogHas(v, "login ok")));
        string all = string.Join("\n", v.Lines.Select(l => l.Text)) + "\n" + v.BodyText;
        LogsCheck("secrets_hidden", !all.Contains("hunter2abc") && !all.Contains("abcdefghijklmnop1234567890") && !all.Contains("sk-abcdef") && !all.Contains("AbCdEf123456") && all.Contains("[redacted]") && all.Contains("user=bob"));
        LogsCheck("redact_helper", LogText.Redact("token: abcd1234efgh") == "token: [redacted]" && LogText.Redact("nothing to hide") == "nothing to hide");

        // --- clear view leaves the file alone ----------------------------------------------------------------------
        byte[] before = ReadShared(serverLog);
        v.ClearView();
        await Delay(0.3);
        LogsCheck("clear_view_keeps_file", v.BufferCount == 0 && ReadShared(serverLog).SequenceEqual(before));

        // --- truncation and rotation -------------------------------------------------------------------------------
        using (var cut = new FileStream(serverLog, FileMode.Truncate, System.IO.FileAccess.Write, FileShare.ReadWrite))
        {
            LogAppend(cut, "after truncation");
        }

        LogsCheck("truncation_followed", await LogWait(() => LogHas(v, "after truncation")) && LogHas(v, "truncated or replaced"));
        writer.Dispose();
        string rotated = serverLog + ".1";
        File.Move(serverLog, rotated, true);
        File.WriteAllText(serverLog, "new log\n");
        LogsCheck("rotation_followed", await LogWait(() => LogHas(v, "new log")), v.StatusText);
        LogsCheck("no_duplicate_after_rotation", v.Lines.Count(l => l.Text == "after truncation") == 1);

        // --- the line cap ------------------------------------------------------------------------------------------
        string busy = Path.Combine(dir, "busy.log");
        File.WriteAllText(busy, "");
        LogView capView = panel.AddSource("busy", "busy", new LogTailer(busy) { PollMilliseconds = 50 });
        capView.SetCap(100);
        File.WriteAllLines(busy, Enumerable.Range(0, 400).Select(i => $"line {i}"));
        LogsCheck("cap_last_line_arrives", await LogWait(() => LogHas(capView, "line 399")));
        LogsCheck("cap_keeps_newest", capView.BufferCount == 100 && capView.Lines.First().Text == "line 300" && capView.Lines.Last().Text == "line 399" && capView.DroppedLines >= 300,
            $"{capView.BufferCount} kept, {capView.DroppedLines} dropped");
        int bodyLines = capView.BodyText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        LogsCheck("cap_body_matches", bodyLines == 100, $"{bodyLines} lines in the body");

        // --- a huge file is entered at its end ---------------------------------------------------------------------
        string huge = Path.Combine(dir, "huge.log");
        using (var h = new StreamWriter(huge, false, new UTF8Encoding(false)))
        {
            for (int i = 0; i < 15000; i++)
            {
                h.Write($"entry {i:D6} " + new string('x', 90) + "\n");
            }
        }

        LogView hugeView = panel.AddSource("huge", "huge", new LogTailer(huge) { PollMilliseconds = 50 });
        hugeView.SetCap(30000);
        LogsCheck("huge_tail_only", await LogWait(() => LogHas(hugeView, "entry 014999")) && hugeView.BufferCount < 7000 && hugeView.Lines.First().Notice && !LogHas(hugeView, "entry 000000"),
            $"{hugeView.BufferCount} lines");

        // --- UTC logs show local time beside the line --------------------------------------------------------------
        string utcLog = Path.Combine(dir, "client.log");
        File.WriteAllText(utcLog, "2026-10-02 12:00:00 | Info | connected\nno stamp here\n");
        LogView utc = panel.AddSource("utc", "utc", new LogTailer(utcLog) { PollMilliseconds = 50 }, utc: true);
        LogsCheck("utc_local_time", await LogWait(() => utc.BufferCount >= 2) && utc.Lines.First().Text.Contains(" local] 2026-10-02 12:00:00", StringComparison.Ordinal) && utc.Lines.Last().Text == "no stamp here" && utc.StatusText.Contains("UTC", StringComparison.Ordinal), utc.Lines.FirstOrDefault()?.Text);

        // --- the user's extra files are remembered -----------------------------------------------------------------
        string extra = Path.Combine(dir, "extra.log");
        File.WriteAllText(extra, "from the extra file\n");
        LogView added = panel.AddFile(extra);
        LogsCheck("add_file", added != null && await LogWait(() => LogHas(added, "from the extra file")));
        LogsCheck("add_file_relative_refused", panel.AddFile("relative.log") == null);
        LogsCheck("add_file_remembered", File.Exists(panel.SettingsPath) && JsonNode.Parse(File.ReadAllText(panel.SettingsPath))?["files"]?.AsArray().Any(n => (string)n["path"] == extra) == true);
        var second = new LogsPanel { AutoDiscover = false, SettingsPath = panel.SettingsPath, PollMilliseconds = 50 };
        AddChild(second);
        await Delay(0.2);
        LogsCheck("add_file_reloaded", second.View("file:" + extra) != null && await LogWait(() => LogHas(second.View("file:" + extra), "from the extra file")));
        second.Shutdown();
        second.QueueFree();

        // --- teardown ----------------------------------------------------------------------------------------------
        int during = LogTailer.Live;
        List<LogTailer> tailers = panel.Views.Values.Select(x => x.Tailer).ToList();
        panel.Shutdown();
        panel.QueueFree();
        await Delay(0.2);
        LogsCheck("tailers_started", during > baseline, $"{during} live, baseline {baseline}");
        LogsCheck("disposed_cleanly", LogTailer.Live == baseline && tailers.All(t => !t.Running), $"{LogTailer.Live} live, baseline {baseline}");
        _logsReport["views"] = string.Join(",", Logs.Panel?.Views.Keys ?? Enumerable.Empty<string>());
    }

    private static bool ProcessGone(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static bool ConsoleReleased(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    private async Task CheckManagedConsoleAsync(string dir)
    {
        string previousRoot = Workspace.RootOverride;
        string root = Path.Combine(_out, "logs_workspace");
        const string id = "81d9cf226a1646efb6a8ab498741afe9";
        string home = Path.Combine(root, "servers", id);
        string state = Path.Combine(home, "process.json");
        string childFile = Path.Combine(home, "fixture.pid");
        LogsPanel panel = null;
        bool leaveForReload = false;
        try
        {
            Workspace.RootOverride = root;
            Directory.CreateDirectory(home);
            string console = LogSources.ServerConsole(id);
            LogsCheck("server_console_workspace_path", console == Path.Combine(home, "server.console.log") && LogSources.ServerConsole("../bad") == "");
            if (_afterReload)
            {
                LogsCheck("server_console_reload_running", ManagedServerProcess.Running(state));
                int oldChild = int.Parse(File.ReadAllText(childFile));
                ManagedServerProcess.Stop(state);
                LogsCheck("server_console_reload_stopped", await LogWait(() => ProcessGone(oldChild) && ConsoleReleased(console)) && !File.Exists(state));
            }

            // A script only: it prints both streams and its PID, sleeps, then exits on its own as a
            // last-resort fixture fuse. It opens no socket and never starts a shard.
            string payload = "spaces & %PATH% ^ ! \"quotes\" trailing\\";
            var profile = new ServerProfile { Id = id, Name = "Console fixture", Host = "127.0.0.1", Port = 2619, ServerDirectory = home };
            string script = Path.Combine(home, "console fixture.py");
            File.WriteAllText(script, "import os, sys, time\nfrom pathlib import Path\n"
                + "Path(__file__).with_name('fixture.pid').write_text(str(os.getpid()))\n"
                + "print('fixture stdout ready', flush=True)\nprint('fixture stderr ready', file=sys.stderr, flush=True)\n"
                + "print('password=fixture-only-value', flush=True)\nprint('argument=' + sys.argv[1], flush=True)\ntime.sleep(0.3 if len(sys.argv) > 2 else 180)\n");
            string python = EditorData.Setting("UO_PYTHON", OperatingSystem.IsWindows() ? "python" : "python3");
            profile.Executable = Path.IsPathFullyQualified(python) ? python : (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), python + (OperatingSystem.IsWindows() && !python.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : "")))
                .FirstOrDefault(File.Exists) ?? python;
            profile.Arguments = new[] { "-u", script, payload };
            string profiles = Path.Combine(dir, "fixture-profiles.json");
            new ServerProfiles { Selected = id, Servers = new() { profile } }.Save(profiles);
            panel = new LogsPanel { ServersFile = profiles, SettingsPath = Path.Combine(dir, "fixture-sources.json"), PollMilliseconds = 50 };
            AddChild(panel);
            await Delay(0.1);
            LogView view = panel.View("serverconsole");
            ManagedServerProcess.Start(profile, state);
            LogsCheck("server_console_streams", await LogWait(() => LogHas(view, "fixture stdout ready") && LogHas(view, "fixture stderr ready")));
            LogsCheck("server_console_redacted", await LogWait(() => LogHas(view, "password=[redacted]")) && !view.BodyText.Contains("fixture-only-value", StringComparison.Ordinal));
            LogsCheck("server_console_arguments", await LogWait(() => LogHas(view, "argument=" + payload)), view.BodyText);
            LogsCheck("server_console_managed", ManagedServerProcess.Running(state));
            bool duplicateRefused = false;
            try { ManagedServerProcess.Start(profile, state); }
            catch (InvalidOperationException) { duplicateRefused = true; }
            LogsCheck("server_console_duplicate_refused", duplicateRefused);
            int child = int.Parse(File.ReadAllText(childFile));
            ManagedServerProcess.Stop(state);
            LogsCheck("server_console_stop_cleanup", await LogWait(() => ProcessGone(child) && ConsoleReleased(console)) && !File.Exists(state));
            byte[] first = ReadShared(console);
            ManagedServerProcess.Start(profile, state);
            LogsCheck("server_console_restart", await LogWait(() => File.Exists(childFile) && File.ReadAllText(childFile) != child.ToString() && ReadShared(console).Length > first.Length));
            int restarted = int.Parse(File.ReadAllText(childFile));
            ManagedServerProcess.Stop(state);
            LogsCheck("server_console_restart_cleanup", await LogWait(() => ProcessGone(restarted) && ConsoleReleased(console)) && !ManagedServerProcess.Running(state));
            LogsCheck("server_console_append", ReadShared(console).Take(first.Length).SequenceEqual(first));
            // A server that exits by itself must also relinquish the wrapper and output handle.
            string[] arguments = profile.Arguments;
            profile.Arguments = arguments.Concat(new[] { "exit" }).ToArray();
            ManagedServerProcess.Start(profile, state);
            LogsCheck("server_console_natural_exit", await LogWait(() => !ManagedServerProcess.Running(state) && ConsoleReleased(console)));
            ManagedServerProcess.Stop(state);
            LogsCheck("server_console_natural_exit_state_removed", !File.Exists(state));
            profile.Arguments = arguments;
            // The second assembly must find and stop this very same wrapper and child. Its state/PID
            // are files, not static addon objects, and all tailers below are shut down before reload.
            if (_reloadTest && !_afterReload && _failures.Count == 0)
            {
                string previousChild = File.ReadAllText(childFile);
                ManagedServerProcess.Start(profile, state);
                LogsCheck("server_console_reload_fixture_ready", await LogWait(() => File.ReadAllText(childFile) != previousChild));
                leaveForReload = _failures.Count == 0;
            }
            _logsReport["server_console_path"] = console;
            File.WriteAllText(Path.Combine(dir, "server_console_redacted.txt"), string.Join("\n", view.Lines.Select(l => l.Text)));
        }
        finally
        {
            if (!leaveForReload) ManagedServerProcess.Stop(state);
            panel?.Shutdown(); panel?.QueueFree();
            Workspace.RootOverride = previousRoot;
        }
    }
}
#endif
