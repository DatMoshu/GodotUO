#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// "Deploy to private shard" (ADR-0031, phase 2): write the multi to the stage, then let <c>tools/multi prove</c> place
/// it on the private shard (<c>build/shard_private</c>, its own port and bridge) and walk it. The target must be a
/// run-bar profile that is that private shard on this machine; port 2593 (the shared shard) and any other server are
/// refused. A confirm dialog comes first, and nothing here runs in the smoke stage.
/// </summary>
public partial class MultiEditView
{
    private ConfirmationDialog _deployDialog;
    private OptionButton _deployTarget;
    private Label _deployLog;
    private Process _deployProc;
    private string _deployPlan;

    /// <summary>A deploy is waiting for its confirmation.</summary>
    public bool DeployPending => _deployPlan != null;

    public static string PrivateHome => Path.Combine(EditorData.RepoRoot, "build", "shard_private");

    private static string ProfilesPath => Path.Combine(EditorData.RepoRoot, "build", "editor_servers", "profiles.json");

    /// <summary>
    /// Why a server may not receive a deployed multi, or null when it may: loopback only, never 2593, and it has to be the
    /// private shard whose home and port the editor shard tool wrote to <c>state.json</c>.
    /// </summary>
    public static string RefuseReason(string host, int port, string serverDirectory, string home, int? statePort)
    {
        if (port == 2593)
        {
            return "port 2593 is the shared shard: a deployment never goes there";
        }

        if (host != "127.0.0.1" && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{host} is not this machine";
        }

        if (string.IsNullOrEmpty(serverDirectory) || !string.Equals(Path.GetFullPath(serverDirectory).TrimEnd('\\', '/'), Path.GetFullPath(home).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return "it is not the private shard (build/shard_private)";
        }

        if (statePort == null)
        {
            return "the private shard is not set up (python tools/editor_shard/run.py setup)";
        }

        return statePort == port ? null : $"the private shard listens on {statePort}, not {port}";
    }

    private static int? StatePort(string home)
    {
        try
        {
            string file = Path.Combine(home, "state.json");
            if (!File.Exists(file))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.GetProperty("port").GetInt32();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every run-bar profile with the reason it is refused (null when it can take a deployment).</summary>
    internal List<(string Name, int Port, string Refused)> DeployTargets(string profilesPath = null, string home = null)
    {
        home ??= PrivateHome;
        var list = new List<(string, int, string)>();
        try
        {
            ServerProfiles profiles = ServerProfiles.Load(profilesPath ?? ProfilesPath);
            int? state = StatePort(home);
            foreach (ServerProfile s in profiles.Servers)
            {
                list.Add((s.Name, s.Port, RefuseReason(s.Host, s.Port, s.ServerDirectory, home, state)));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] multi deploy: profiles: {ex.Message}");
        }

        return list;
    }

    private void BuildDeploy(Control save)
    {
        save.AddChild(new HSeparator());
        save.AddChild(new Label { Text = "Private shard" });
        _deployTarget = new OptionButton { TooltipText = "Run-bar profiles that are this checkout's private shard" };
        save.AddChild(_deployTarget);
        save.AddChild(Tip(Btn("Deploy to private shard...", () => RequestDeploy()), "Write to the stage, then place the multi on the private shard and walk it (asks first)"));
        _deployLog = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
        save.AddChild(_deployLog);
        RefreshDeployTargets();
    }

    private void RefreshDeployTargets()
    {
        if (_deployTarget == null)
        {
            return;
        }

        _deployTarget.Clear();
        foreach ((string name, int port, string why) in DeployTargets().Where(t => t.Refused == null))
        {
            _deployTarget.AddItem($"{name} (127.0.0.1:{port})");
        }

        _deployTarget.Disabled = _deployTarget.ItemCount == 0;
    }

    /// <summary>
    /// Asks for a confirmation to deploy to the first eligible private-shard profile (or the one named). Returns the plan
    /// text, or null with the reason in the status. Starts nothing.
    /// </summary>
    public string RequestDeploy(string profileName = null)
    {
        RefreshDeployTargets();
        var targets = DeployTargets();
        var ok = targets.Where(t => t.Refused == null && (profileName == null || t.Name == profileName)).ToList();
        if (ok.Count == 0)
        {
            string why = targets.Count == 0 ? "no server profiles" : string.Join("; ", targets.Select(t => $"{t.Name}: {t.Refused}"));
            _deployLog.Text = $"no private shard to deploy to: {why}";
            _status.Text = "deploy refused";
            return null;
        }

        ValidateNow();
        if (_result.HasErrors)
        {
            _deployLog.Text = "fix the errors first";
            return null;
        }

        string plan = $"Write '{_name.Text.Trim()}' ({_doc.Parts.Count} components) to the stage and place it on {ok[0].Name} (127.0.0.1:{ok[0].Port})?\n" +
                      "This starts the private shard and a client if they are not running.";
        _deployPlan = plan;
        if (IsInsideTree())
        {
            if (_deployDialog == null)
            {
                _deployDialog = new ConfirmationDialog { Title = "Deploy to private shard", OkButtonText = "Deploy" };
                _deployDialog.Confirmed += () => _ = DeployAsync();
                _deployDialog.Canceled += () => _deployPlan = null;
                AddChild(_deployDialog);
            }

            _deployDialog.DialogText = plan;
            _deployDialog.PopupCentered();
        }

        return plan;
    }

    /// <summary>The confirmed deploy: stage write, then <c>run.py prove</c> in the background (log under build/multi).</summary>
    public async Task<bool> DeployAsync()
    {
        if (_deployPlan == null)
        {
            return false;
        }

        _deployPlan = null;
        SaveResult r = await SaveToStageAsync();
        if (!r.Ok)
        {
            _deployLog.Text = r.Error ?? "the stage write failed";
            return false;
        }

        string python = QueueClient.FindPython();
        string script = Path.Combine(EditorData.RepoRoot, "tools", "multi", "run.py");
        if (python == null || !File.Exists(script))
        {
            _deployLog.Text = "python or tools/multi/run.py not found";
            return false;
        }

        string log = Path.Combine(EditorData.RepoRoot, "build", "multi", $"deploy_{MultiStore.SafeName(r.Name)}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log));
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in new[] { script, "prove", r.Name, "--stage", StageDir })
        {
            psi.ArgumentList.Add(a);
        }

        _deployProc?.Dispose();
        _deployProc = Process.Start(psi);
        Process proc = _deployProc;
        _deployLog.Text = $"placing {r.Name} on the private shard; the log is build/multi/{Path.GetFileName(log)}";
        _ = Task.Run(async () =>
        {
            try
            {
                string text = await proc.StandardOutput.ReadToEndAsync() + await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                File.WriteAllText(log, text);
                int code = proc.ExitCode;
                Callable.From(() =>
                {
                    if (IsInsideTree() && _deployLog != null)
                    {
                        _deployLog.Text = code == 0 ? $"{r.Name}: placed and walked on the private shard" : $"{r.Name}: the proof stopped ({code}); see {Path.GetFileName(log)}";
                    }
                }).CallDeferred();
            }
            catch (Exception)
            {
                // The view was torn down (a reload) while the proof ran; the log file is all that is left to write.
            }
        });
        return true;
    }

    /// <summary>A deploy process was started by this view.</summary>
    public bool DeployRunning => _deployProc != null;

    /// <summary>Drops a deployment that is waiting for its confirmation.</summary>
    public void CancelDeploy()
    {
        _deployPlan = null;
        _deployDialog?.Hide();
    }

    private void ReleaseDeploy()
    {
        _deployPlan = null;
        _deployProc?.Dispose();
        _deployProc = null;
    }
}
#endif
