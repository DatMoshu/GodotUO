// GUO addition, not a port. ADR-0035: the shard's generated secrets as the editor reads them. Plain .NET (no
// Godot types) so tools/server_manager/tests compiles this file as it stands.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GUO.Workspace;

/// <summary>
/// The per-user secrets file, <c>&lt;workspace&gt;/shard/secrets.bat</c> (tools/guo/shard_secrets.py writes it):
/// the dev shard's passwords and the editor bridge's admin token. Read the way tools/guo/config.py resolves a
/// setting: the environment, then <see cref="Workspace.Setting"/> (config.local.bat, config.bat), then this file.
/// Nothing here prints or logs a value.
/// </summary>
internal static class ShardSecrets
{
    public const string AdminTokenKey = "UO_BRIDGE_ADMIN_TOKEN";

    // `if not defined KEY set "KEY=value"` or `set "KEY=value"`, as shard_secrets.py writes them.
    private static readonly Regex SetLine = new(@"^\s*(?:if\s+not\s+defined\s+\S+\s+)?set\s+""(?<key>[A-Za-z0-9_]+)=(?<val>[^""]*)""\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string FilePath => Path.Combine(Workspace.Root, "shard", "secrets.bat");

    /// <summary>One key of the secrets file itself, or null.</summary>
    public static string ReadFile(string key, string path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            Match m = SetLine.Match(line);
            if (m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase) && m.Groups["val"].Value.Length > 0)
            {
                return m.Groups["val"].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets one key of the secrets file (the Admin tab's Settings form, AD4: a server setting that is a secret, such as
    /// a mail password, is kept here); a null or empty value removes the key. Other lines are kept as they are, the
    /// file is replaced through a temporary file, and on Linux and macOS it stays readable by its owner only.
    /// </summary>
    public static void Write(string key, string value, string path = null)
    {
        path ??= FilePath;
        if (!Regex.IsMatch(key ?? "", "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException("not a secrets file key", nameof(key));
        }

        if (value != null && value.IndexOfAny(new[] { '"', '%', '^', '!', '\r', '\n' }) >= 0)
        {
            throw new ArgumentException("a secret in the secrets file may not hold \" % ^ ! or a line break", nameof(value));
        }

        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>
        {
            "@echo off",
            "REM The local dev shard's secrets (tools\\guo\\shard_secrets.py, the Admin tab's Settings).",
            "REM Per user, outside the repository; never commit or share this file.",
        };
        lines.RemoveAll(l => SetLine.Match(l) is { Success: true } m && string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(value))
        {
            lines.Add($"if not defined {key} set \"{key}={value}\"");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join("\r\n", lines) + "\r\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A secret resolved as every setting is: environment, config files, then the secrets file. Null when unset.</summary>
    public static string Resolve(string key)
    {
        string env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        string configured = Workspace.Setting?.Invoke(key);
        return !string.IsNullOrEmpty(configured) ? configured : ReadFile(key);
    }

    /// <summary>The editor bridge's admin token (ADR-0035), or null.</summary>
    public static string BridgeAdminToken() => Resolve(AdminTokenKey);

    /// <summary>
    /// What a ModernUO server needs in its environment for its editor bridge, when its folder lists the bridge
    /// (Data/assemblies.json names GUO.EditorBridge.dll): the admin token, and, for a private instance that
    /// tools/editor_shard set up (its state.json), the bridge port, shard name and maps that tool starts it with.
    /// Empty for any other server. Lets the run bar start or restart that server as tools/editor_shard would.
    /// </summary>
    public static Dictionary<string, string> BridgeEnvironment(string serverDirectory)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(serverDirectory))
        {
            return env;
        }

        string listed = Path.Combine(serverDirectory, "Data", "assemblies.json");
        try
        {
            if (!File.Exists(listed) || !File.ReadAllText(listed).Contains("GUO.EditorBridge.dll", StringComparison.OrdinalIgnoreCase))
            {
                return env;
            }

            string token = BridgeAdminToken();
            if (!string.IsNullOrEmpty(token))
            {
                env["GUO_BRIDGE_ADMIN_TOKEN"] = token;
            }

            string state = Path.Combine(serverDirectory, "state.json");
            if (File.Exists(state))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(state));
                JsonElement root = doc.RootElement;
                string Text(string name, string fallback) =>
                    root.TryGetProperty(name, out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : fallback : fallback;
                env["GUO_BRIDGE_PORT"] = Text("bridge_port", "2595");
                env["GUO_BRIDGE_SHARD"] = Text("bridge_shard", "GUO-Editor-Private");
                env["GUO_BRIDGE_MAPS"] = Text("bridge_maps", "0,1,2,3,4,5");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A server whose files cannot be read starts as it would without the bridge's settings.
        }

        return env;
    }
}
