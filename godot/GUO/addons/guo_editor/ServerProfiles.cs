#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    public string ClientProject { get; set; } = "";
    public string ClientData { get; set; } = "";
    public string ContentLock { get; set; } = "";
    public string ContentStore { get; set; } = "";
    public string[] Arguments { get; set; } = Array.Empty<string>();
}

// Local workstation state only: never checked in, and never changes a server's own configuration.
internal sealed class ServerProfiles
{
    // Keep collectible addon types out of System.Text.Json's process-wide default cache.
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    public string Selected { get; set; }
    public List<ServerProfile> Servers { get; set; } = new();
    public static ServerProfiles Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Server list exceeds 1 MB");
        var list = JsonSerializer.Deserialize<ServerProfiles>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Invalid server list");
        Validate(list); return list;
    }
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
            foreach (string path in new[] { s.Executable, s.ServerDirectory, s.ServerProject, s.ClientProject, s.ClientData, s.ContentLock, s.ContentStore })
                if (!string.IsNullOrEmpty(path) && !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Choose absolute paths for server and client files");
        }
    }
    public void Save(string path)
    {
        Validate(this); Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class ManagedServerProcess
{
    public int Pid { get; set; }
    public long Started { get; set; }
    public string Executable { get; set; }
    private static Process Owned(string state)
    {
        if (!File.Exists(state)) return null;
        var saved = JsonSerializer.Deserialize<ManagedServerProcess>(File.ReadAllBytes(state), ServerProfiles.Json);
        if (saved == null) return null;
        try
        {
            var process = Process.GetProcessById(saved.Pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == saved.Started
                && string.Equals(Path.GetFullPath(process.MainModule.FileName), saved.Executable, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return process;
            process.Dispose(); return null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
    public static bool Running(string state) { using var p = Owned(state); return p != null; }
    public static void Start(ServerProfile profile, string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(state));
        using var gate = new FileStream(state + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var previous = Owned(state);
        if (previous != null) throw new InvalidOperationException("This server is already managed and running");
        if (!File.Exists(profile.Executable) || !Directory.Exists(profile.ServerDirectory)) throw new InvalidDataException("Choose an existing server executable and working directory");
        if (!(profile.Host == "localhost" || System.Net.IPAddress.TryParse(profile.Host, out var address) && System.Net.IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("Remote profiles are connect-only; start their server on its host");
        var info = new ProcessStartInfo(profile.Executable) { WorkingDirectory = profile.ServerDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in profile.Arguments) info.ArgumentList.Add(arg);
        // Do not accidentally apply a development probe or another server's deployment.
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("UO_", StringComparison.Ordinal) && (k.EndsWith("_PROBE", StringComparison.Ordinal) || k == "UO_SERVER_CONTENT")).ToArray()) info.Environment.Remove(key);
        Directory.CreateDirectory(Path.GetDirectoryName(state));
        using var process = Process.Start(info) ?? throw new IOException("Server did not start");
        try
        {
            var saved = new ManagedServerProcess { Pid = process.Id, Started = process.StartTime.ToUniversalTime().Ticks, Executable = Path.GetFullPath(process.MainModule.FileName) };
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
