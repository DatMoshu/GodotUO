#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using GUO.Workspace;

/// <summary>
/// The smoke stage for client profiles (ADR-0032). All in a workspace under the smoke output folder, never the
/// person's: a fixture earlier-format profiles file is migrated, the run bar's Client list follows the server and
/// can override it, a version mismatch warns, a slot path is keyed by server and client, an external client's
/// launch is planned (a dry run: nothing is started), and the Clients page duplicates a client.
/// </summary>
public partial class EditorSmoke
{
    private void CheckClientProfiles()
    {
        string workspace = Path.Combine(_out, "workspace_clients");
        string legacy = Path.Combine(_out, "legacy_servers", "profiles.json");
        RunBar bar = null;
        ServerManagerWindow window = null;
        var detail = new Dictionary<string, object>();
        try
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            if (Directory.Exists(Path.GetDirectoryName(legacy))) Directory.Delete(Path.GetDirectoryName(legacy), true);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy));
            Workspace.RootOverride = workspace; EditorWorkspace.LegacyOverride = legacy; EditorWorkspace.ForgetMigration(); ClientRegistry.Reset();

            string project = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
            string data = _data.ClientData, otherData = Path.Combine(_out, "other_uo_data");
            string idA = new string('a', 32), idB = new string('b', 32), idC = new string('c', 32);
            string Old(string id, string name, string proj, string dat) => JsonSerializer.Serialize(new Dictionary<string, object>
            { ["Id"] = id, ["Backend"] = "custom", ["Name"] = name, ["Host"] = "127.0.0.1", ["Port"] = 2599, ["Executable"] = "", ["ServerDirectory"] = "", ["ServerProject"] = "",
              ["ClientProject"] = proj, ["ClientData"] = dat, ["ContentLock"] = "", ["ContentStore"] = "", ["Arguments"] = Array.Empty<string>() });
            File.WriteAllText(legacy, "{\"Selected\":\"" + idA + "\",\"Servers\":[" + Old(idA, "Alpha", project, data) + "," + Old(idB, "Beta", project, data) + "," + Old(idC, "Gamma", project, otherData) + "]}");

            // Migration happens when the run bar loads the list.
            bar = new RunBar(); AddChild(bar);
            var reg = ClientRegistry.Current;
            bool migrated = reg.Clients.Count == 2 && !File.Exists(legacy) && File.Exists(legacy + ".migrated") && File.Exists(Workspace.ServersFile) && File.Exists(Workspace.ClientsFile);
            detail["migrated"] = migrated;
            if (!migrated) _failures.Add("Client profiles: the earlier profiles file was not migrated into the workspace");

            // The Client list follows the server; the person may override it.
            var servers = ServerProfiles.Load(Workspace.ServersFile);
            string clientAB = servers.Servers.First(s => s.Id == idA).DefaultClient, clientC = servers.Servers.First(s => s.Id == idC).DefaultClient;
            Check(clientAB == servers.Servers.First(s => s.Id == idB).DefaultClient && clientAB != clientC, "migration shares one client between identical pairs", detail);
            bar.SelectServer(idC);
            Check(bar.EffectiveClient?.Id == clientC, "choosing a server selects its default client", detail);
            bar.SelectClient(clientAB);
            Check(bar.EffectiveClient?.Id == clientAB, "the Client list overrides the server's default", detail);
            bar.SelectServer(idB);
            Check(bar.EffectiveClient?.Id == clientAB && ServerProfiles.Load(Workspace.ServersFile).SelectedClient == null, "choosing a server again returns to its default", detail);

            // A server that expects another client version warns; agreeing clears it.
            reg.Find(clientAB).Meta.Version = "7.0.2.0";
            var edited = ServerProfiles.Load(Workspace.ServersFile);
            edited.Servers.First(s => s.Id == idB).ExpectedClientVersion = "7.0.1.0";
            reg.Save(); edited.Save(Workspace.ServersFile); bar.ReloadProfiles();
            Check(bar.Warning.Contains("7.0.1.0") && bar.Warning.Contains("7.0.2.0"), "a client of another version than the server expects shows a warning", detail);
            bar.SelectServer(idA);
            Check(bar.Warning == "", "no warning when the server expects no version", detail);

            // Slot paths are keyed by server and client; an external client is planned (dry run), never started or given settings.
            var external = reg.Add(new ClientProfile { Name = "Fork", Kind = ClientKinds.External, Program = OS.GetExecutablePath(), Arguments = new[] { "--host", "{host}", "{port}" } });
            reg.Save(); bar.ReloadProfiles(); bar.SelectServer(idB); bar.SelectClient(external.Id);
            var plan = bar.PlanSlot(2);
            Check(plan.SlotDir == Workspace.RunSlot(idB, external.Id, 2) && plan.SlotDir.Replace('\\', '/').Contains($"/runs/{idB}/{external.Id}/slot-2"), "the slot folder is runs/server/client/slot-n", detail);
            Check(plan.Arguments.SequenceEqual(new[] { "--host", "127.0.0.1", "2599" }) && plan.Environment.Count == 0 && plan.Console == null, "an external client gets its arguments and nothing else", detail);
            Check(!ManagedServerProcess.Running(plan.State) && !Directory.Exists(plan.SlotDir), "the dry run started nothing and created nothing", detail);
            bar.SelectClient(clientAB);
            try
            {
                var project1 = bar.PlanSlot(1);
                Check(project1.SlotDir == Workspace.RunSlot(idB, clientAB, 1) && project1.Environment["UO_CACHE_DIR"] == Path.Combine(project1.SlotDir, "cache") && project1.Environment["UO_CLIENT_DATA"] == data, "a project client's cache and data come from its slot and profile", detail);
            }
            catch (FileNotFoundException) { detail["project_plan"] = "skipped: no console engine here"; }

            // The Clients page.
            window = new ServerManagerWindow(); AddChild(window);
            int saves = 0;
            window.Open(ServerProfiles.Load(Workspace.ServersFile), null, s => Path.Combine(_out, s.Id, "process.json"), () => OS.GetExecutablePath(), () => saves++, reg);
            int before = reg.Clients.Count;
            var buttons = window.FindChildren("*", "Button", true, false).OfType<Button>().ToList();
            buttons.First(b => b.Text == "Duplicate").EmitSignal(Button.SignalName.Pressed);
            Check(reg.Clients.Count == before + 1 && ClientRegistry.Load().Clients.Count == before + 1, "the Clients page duplicates a client and saves it", detail);
            Check(buttons.Any(b => b.Text == "New client") && buttons.Any(b => b.Text == "Remove client") && window.FindChildren("*", "TabContainer", true, false).Count == 1, "the window has Servers and Clients pages", detail);
        }
        catch (Exception e) { _failures.Add("Client profiles: " + e.Message); }
        finally
        {
            window?.QueueFree(); bar?.QueueFree();
            Workspace.RootOverride = null; EditorWorkspace.LegacyOverride = null; EditorWorkspace.ForgetMigration(); ClientRegistry.Reset();
        }
        _report["client_profiles"] = detail;
    }

    private void Check(bool ok, string what, Dictionary<string, object> into) { into[what] = ok; if (!ok) _failures.Add("Client profiles: " + what); }
}
#endif
