#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GUO.Workspace;

internal sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Backend { get; set; } = "custom";
    public string Name { get; set; } = "New server";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2593;
    public string Executable { get; set; } = "";
    public string ServerDirectory { get; set; } = "";
    public string ServerProject { get; set; } = "";
    /// <summary>The client a run starts with by default (a client id in clients.json), or "".</summary>
    public string DefaultClient { get; set; } = "";
    /// <summary>The client version the server expects (a dotted version), or "". A different client only warns.</summary>
    public string ExpectedClientVersion { get; set; } = "";
    public string ContentLock { get; set; } = "";
    public string ContentStore { get; set; } = "";
    public string[] Arguments { get; set; } = Array.Empty<string>();
}

// Local workstation state only: never checked in, and never changes a server's own configuration.
// Lives in the per-user workspace (ADR-0032, docs/data_formats.md section 30).
internal sealed class ServerProfiles
{
    // Keep collectible addon types out of System.Text.Json's process-wide default cache.
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly Regex VersionPattern = new(@"^[0-9]+(\.[0-9]+){0,3}[a-z]?$", RegexOptions.CultureInvariant);
    public string Selected { get; set; }
    /// <summary>The client chosen in the run bar (an override of the server's default), or null.</summary>
    public string SelectedClient { get; set; }
    public List<ServerProfile> Servers { get; set; } = new();
    public static ServerProfiles Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Server list exceeds 1 MB");
        var list = JsonSerializer.Deserialize<ServerProfiles>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Invalid server list");
        Validate(list); return list;
    }
    /// <summary>The effective client of a server: the run bar's choice if it names a client, else the server's default.</summary>
    public string ClientFor(ServerProfile server) => !string.IsNullOrEmpty(SelectedClient) && Workspace.IsId(SelectedClient) ? SelectedClient : server?.DefaultClient;
    public static void Validate(ServerProfiles list)
    {
        if (list.Servers == null || list.Servers.Count > 64) throw new InvalidDataException("At most 64 server profiles are supported");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list.Servers)
        {
            if (s == null || !Guid.TryParseExact(s.Id, "N", out _) || !ids.Add(s.Id) || string.IsNullOrWhiteSpace(s.Name)
                || s.Name.Length > 100 || string.IsNullOrWhiteSpace(s.Host) || Uri.CheckHostName(s.Host) == UriHostNameType.Unknown
                || s.Port < 1 || s.Port > 65535 || s.Arguments == null || s.Arguments.Length > 32)
                throw new InvalidDataException("Invalid server name, identity, address, port or arguments");
            if (s.DefaultClient == null || s.DefaultClient.Length > 0 && !Workspace.IsId(s.DefaultClient)
                || s.ExpectedClientVersion == null || s.ExpectedClientVersion.Length > 0 && !VersionPattern.IsMatch(s.ExpectedClientVersion))
                throw new InvalidDataException("Invalid default client or expected client version");
            foreach (string path in new[] { s.Executable, s.ServerDirectory, s.ServerProject, s.ContentLock, s.ContentStore })
                if (!string.IsNullOrEmpty(path) && !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Choose absolute paths for server and client files");
        }
        if (list.SelectedClient != null && list.SelectedClient.Length > 0 && !Workspace.IsId(list.SelectedClient))
            throw new InvalidDataException("Invalid selected client");
    }
    /// <summary>
    /// ADR-0032: the one-time move of the earlier per-checkout profiles file into the workspace. Each server's
    /// ClientProject / ClientData pair becomes a client profile (the same pair, one client); servers merge by id;
    /// the old file is renamed .migrated. Returns whether anything was migrated. Nothing is moved on failure.
    /// </summary>
    public static bool Migrate(string legacyPath, ClientRegistry clients)
    {
        if (string.IsNullOrEmpty(legacyPath) || !File.Exists(legacyPath)) return false;
        if (new FileInfo(legacyPath).Length > 1024 * 1024) throw new InvalidDataException("Old server list exceeds 1 MB");
        var root = JsonNode.Parse(File.ReadAllBytes(legacyPath)) as JsonObject ?? throw new InvalidDataException("Invalid old server list");
        var servers = File.Exists(Workspace.ServersFile) ? Load(Workspace.ServersFile) : new ServerProfiles();
        string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string s) ? s : "";
        string Abs(string path) => !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) ? path : "";
        var lenient = new JsonSerializerOptions(Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip };
        foreach (var node in root["Servers"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject o) continue;
            if (servers.Servers.Any(s => s.Id == Text(o, "Id"))) continue;
            var profile = JsonSerializer.Deserialize<ServerProfile>(o.ToJsonString(), lenient) ?? throw new InvalidDataException("Invalid old server profile");
            string project = Abs(Text(o, "ClientProject")), data = Abs(Text(o, "ClientData"));
            if (project.Length > 0 || data.Length > 0)
                profile.DefaultClient = clients.FindOrAddProject(profile.Name, project, data, "", "", null, "migrated").Id;
            servers.Servers.Add(profile);
        }
        string selected = Text(root, "Selected");
        if (string.IsNullOrEmpty(servers.Selected) && servers.Servers.Any(s => s.Id == selected)) servers.Selected = selected;
        Validate(servers);
        clients.Save(); servers.Save(Workspace.ServersFile);
        string backup = legacyPath + ".migrated";
        if (File.Exists(backup)) backup += "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        File.Move(legacyPath, backup);
        return true;
    }
    /// <summary>The workspace's server list, after migrating an earlier per-checkout file when there is one.</summary>
    public static ServerProfiles LoadWorkspace(string legacyPath, ClientRegistry clients)
    {
        Migrate(legacyPath, clients);
        return Load(Workspace.ServersFile);
    }
    public void Save(string path)
    {
        Validate(this);
        Workspace.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));
    }
}

internal sealed class ManagedServerProcess
{
    public int Pid { get; set; }
    public long Started { get; set; }
    public string Executable { get; set; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
    private static string ExecutableOf(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    string image = new FileInfo($"/proc/{process.Id}/exe").ResolveLinkTarget(false)?.FullName;
                    if (!string.IsNullOrEmpty(image)) return Path.GetFullPath(image);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return Path.GetFullPath(process.MainModule?.FileName ?? throw new InvalidOperationException("Process image is unavailable"));
        }
        // MainModule can be null between CreateProcess returning and the loader initializing the
        // module list (reproduced by the console fixture's rapid restarts). The kernel image query
        // is available immediately and needs only PROCESS_QUERY_LIMITED_INFORMATION, not VM reads.
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var name = new StringBuilder(32768);
        int length = name.Capacity;
        if (!QueryFullProcessImageName(handle, 0, name, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetFullPath(name.ToString());
    }
    private static Process Owned(string state)
    {
        if (!File.Exists(state)) return null;
        var saved = JsonSerializer.Deserialize<ManagedServerProcess>(File.ReadAllBytes(state), ServerProfiles.Json);
        if (saved == null) return null;
        Process process = null;
        try
        {
            process = Process.GetProcessById(saved.Pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == saved.Started
                && string.Equals(ExecutableOf(process), saved.Executable, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            { var owned = process; process = null; return owned; }
            return null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        finally { process?.Dispose(); }
    }
    public static bool Running(string state) { using var p = Owned(state); return p != null; }
    public static void Start(ServerProfile profile, string state)
    {
        if (!File.Exists(profile.Executable) || !Directory.Exists(profile.ServerDirectory)) throw new InvalidDataException("Choose an existing server executable and working directory");
        if (!(profile.Host == "localhost" || System.Net.IPAddress.TryParse(profile.Host, out var address) && System.Net.IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("Remote profiles are connect-only; start their server on its host");
        var info = new ProcessStartInfo(profile.Executable) { WorkingDirectory = profile.ServerDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in profile.Arguments) info.ArgumentList.Add(arg);
        // Do not accidentally apply a development probe or another server's deployment.
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("UO_", StringComparison.Ordinal) && (k.EndsWith("_PROBE", StringComparison.Ordinal) || k == "UO_SERVER_CONTENT")).ToArray()) info.Environment.Remove(key);
        Start(ConsoleStartInfo(info, LogSources.ServerConsole(profile.Id)), state);
    }
    /// <summary>
    /// The OS shell owns the console file and waits for the server. No reader, callback or file handle in
    /// the collectible addon survives a reload; the recorded shell identity still owns the whole tree.
    /// </summary>
    internal static ProcessStartInfo ConsoleStartInfo(ProcessStartInfo info, string console)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(console));
        var shell = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
            { WorkingDirectory = info.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true };
        shell.Environment.Clear();
        foreach (var pair in info.Environment) shell.Environment[pair.Key] = pair.Value;
        string key = "GUO_CONSOLE_" + Guid.NewGuid().ToString("N");
        shell.Environment[key + "_FILE"] = console;
        if (OperatingSystem.IsWindows())
        {
            // Delayed expansion happens AFTER cmd parses operators/percent expansions. Insert the native
            // command's CRT-quoted arguments then, so &, %, ^, ! and embedded quotes stay argument text.
            shell.Environment[key + "_EXE"] = info.FileName;
            shell.Environment[key + "_ARGS"] = string.Join(" ", info.ArgumentList.Select(WindowsArgument));
            shell.Arguments = $"/d /v:on /s /c \"\"!{key}_EXE!\" !{key}_ARGS! >> \"!{key}_FILE!\" 2>&1\"";
        }
        else
        {
            shell.ArgumentList.Add("-c");
            // A following command keeps shells from replacing themselves with the final program:
            // process.json must continue to name the wrapper executable, not an exec'd server.
            shell.ArgumentList.Add($"\"$@\" >> \"${key}_FILE\" 2>&1; status=$?; exit \"$status\"");
            shell.ArgumentList.Add("guo-server-console");
            shell.ArgumentList.Add(info.FileName);
            foreach (string arg in info.ArgumentList) shell.ArgumentList.Add(arg);
        }
        return shell;
    }
    private static string WindowsArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(c); slashes = 0;
        }
        quoted.Append('\\', slashes * 2); quoted.Append('"');
        return quoted.ToString();
    }
    /// <summary>
    /// Starts a program and records its exact identity (PID, start time, executable) in the state file. Refused while
    /// the recorded process still runs. A client (ADR-0032) is tracked the same way as a server.
    /// </summary>
    public static void Start(ProcessStartInfo info, string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(state));
        using var gate = new FileStream(state + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var previous = Owned(state);
        if (previous != null) throw new InvalidOperationException("This server is already managed and running");
        using var process = Process.Start(info) ?? throw new IOException("Process did not start");
        try
        {
            var saved = new ManagedServerProcess { Pid = process.Id, Started = process.StartTime.ToUniversalTime().Ticks, Executable = ExecutableOf(process) };
            File.WriteAllBytes(state, JsonSerializer.SerializeToUtf8Bytes(saved, ServerProfiles.Json));
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    // Explicit stop of this manager's exact process only. A recycled PID or an external server is never stopped.
    public static void Stop(string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(state));
        using var gate = new FileStream(state + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var process = Owned(state);
        if (process != null) { process.Kill(true); if (!process.WaitForExit(10000)) throw new IOException("Server has not stopped"); }
        if (File.Exists(state)) File.Delete(state);
    }
}
#endif
