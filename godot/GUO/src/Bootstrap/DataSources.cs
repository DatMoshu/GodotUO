namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

/// <summary>
/// Where the client's data comes from (ADR-0021), the runtime half of
/// tools/guo/datasources.py. The order: a custom data folder
/// (<c>--custom-data</c>, then <c>UO_CUSTOM_DATA</c>, then <c>guo_data/</c>
/// beside the executable), then the UO install (<c>--client-data</c> or
/// <c>UO_CLIENT_DATA</c>, else settings.json <c>ultimaonlinedirectory</c>, else
/// the platform defaults), else the first-run wizard.
/// </summary>
/// <remarks>
/// A folder is valid when every entry of <see cref="DataRequirements"/> is
/// satisfied: the table is generated from tools/guo/formats.py, so this and the
/// tools judge a folder the same way. The first configured install is the only
/// one tried; a broken one is reported, never replaced by another install.
/// Reads only, apart from the override file a layered folder needs.
/// </remarks>
internal static class DataSources
{
    public const string Manifest = "guo_data.json";
    public const string ManifestFormat = "guo/data-folder@1";

    /// <summary>What the client should read, or why the wizard is needed.</summary>
    public sealed class Result
    {
        /// <summary>custom, install, install+custom, or wizard.</summary>
        public string Source = "wizard";

        /// <summary>The folder opened as the install; null for the wizard.</summary>
        public string ClientData;

        /// <summary>custom, flag, environment, setting or default.</summary>
        public string Origin = "";

        public string Custom;

        /// <summary>Install file name (lowercase) to its replacement, for a layered folder.</summary>
        public Dictionary<string, string> Overrides = new();

        public bool LocalOnly;

        /// <summary>Every candidate passed over, and why, in the order tried.</summary>
        public List<string> Notes = new();

        public bool Ok => Source != "wizard";

        public string Reason => Notes.Count > 0 ? string.Join("; ", Notes) : "no UO data configured and none found";
    }

    /// <summary>The inputs, gathered by the caller, so the order can be tested without a machine.</summary>
    public sealed class Inputs
    {
        public string CustomFlag = "";
        public string CustomEnv = "";
        public string ShippedFolder = "";
        public string InstallConfigured = "";
        public string InstallConfiguredOrigin = "environment";
        public string SettingsFile = "";
        public IReadOnlyList<string> Defaults = Array.Empty<string>();
    }

    public static Result Resolve(Inputs inputs)
    {
        var r = new Result();
        (string folder, Dictionary<string, JsonElement> manifest)? layered = null;

        // Only the first custom folder that is set is considered, as in the tools.
        // A shipped guo_data/ that is not there is simply not configured.
        string custom = new[] { inputs.CustomFlag, inputs.CustomEnv }.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
            ?? (!string.IsNullOrWhiteSpace(inputs.ShippedFolder) && Directory.Exists(inputs.ShippedFolder) ? inputs.ShippedFolder : null);
        if (custom != null)
        {
            (Dictionary<string, JsonElement> m, string why) = ReadManifest(custom);
            if (m == null)
            {
                r.Notes.Add($"custom {custom}: {why}");
            }
            else if (m["mode"].GetString() == "complete")
            {
                string missing = Validate(custom);
                if (missing == null)
                {
                    r.Source = "custom";
                    r.ClientData = custom;
                    r.Origin = "custom";
                    r.Custom = custom;
                    r.LocalOnly = m["contains_ea_data"].GetBoolean();
                    return r;
                }

                r.Notes.Add($"custom {custom} (complete): {missing}");
            }
            else
            {
                layered = (custom, m);
            }
        }

        var installs = new List<(string Origin, string Path)>();
        string saved = SavedSetting(inputs.SettingsFile);
        if (!string.IsNullOrWhiteSpace(inputs.InstallConfigured))
        {
            installs.Add((inputs.InstallConfiguredOrigin, inputs.InstallConfigured));
        }
        else if (!string.IsNullOrWhiteSpace(saved))
        {
            installs.Add(("setting", saved));
        }
        else
        {
            installs.AddRange(inputs.Defaults.Select(d => ("default", d)));
        }

        foreach ((string origin, string path) in installs)
        {
            string missing = Validate(path);
            if (missing != null)
            {
                r.Notes.Add($"install ({origin}) {path}: {missing}");
                continue;
            }

            r.Source = "install";
            r.ClientData = path;
            r.Origin = origin;
            if (layered is { } l)
            {
                foreach (string name in l.manifest["files"].EnumerateObject().Select(p => p.Name))
                {
                    r.Overrides[name.ToLowerInvariant()] = Path.Combine(l.folder, name);
                }

                if (r.Overrides.Count > 0)
                {
                    r.Source = "install+custom";
                    r.Custom = l.folder;
                    r.LocalOnly = l.manifest["contains_ea_data"].GetBoolean();
                }
                else
                {
                    r.Notes.Add($"custom {l.folder} (layered): its manifest lists no files");
                }
            }

            return r;
        }

        if (layered is { } alone)
        {
            r.Notes.Add($"custom {alone.folder} (layered) needs a valid UO install beneath it");
        }

        return r;
    }

    /// <summary>Null when every required entry is satisfied, else why not.</summary>
    public static string Validate(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "not set";
        }

        if (!Directory.Exists(folder))
        {
            return $"not a folder: {folder}";
        }

        var found = new HashSet<string>(
            Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f).ToLowerInvariant())
        );
        bool Has(string[] names) => names.Any(n => found.Contains(n.ToLowerInvariant()));

        var missing = new List<string>();
        foreach (DataRequirements.Entry e in DataRequirements.Required)
        {
            // As formats.DataFile.is_satisfied: the UOP form, or the MUL form
            // with its index; a MUL without its index does not count.
            bool ok = Has(e.Uop) || (Has(e.Mul) && (e.Index.Length == 0 || Has(e.Index)));
            if (!ok)
            {
                missing.Add(e.Key);
            }
        }

        return missing.Count == 0 ? null : $"missing required data: {string.Join(", ", missing)}";
    }

    /// <summary>The folder's guo_data.json, checked as the tools check it, or null and why not.</summary>
    public static (Dictionary<string, JsonElement> Manifest, string Why) ReadManifest(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return (null, "not a folder");
        }

        string path = Path.Combine(folder, Manifest);
        if (!File.Exists(path))
        {
            return (null, $"no {Manifest}");
        }

        Dictionary<string, JsonElement> m;
        try
        {
            m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return (null, $"{Manifest} unreadable: {ex.Message}");
        }

        if (m == null || !m.TryGetValue("format", out JsonElement f) || f.ValueKind != JsonValueKind.String || f.GetString() != ManifestFormat)
        {
            return (null, $"{Manifest} format is not {ManifestFormat}");
        }

        if (!m.TryGetValue("mode", out JsonElement mode) || mode.ValueKind != JsonValueKind.String
            || (mode.GetString() != "complete" && mode.GetString() != "layered"))
        {
            return (null, $"{Manifest} mode must be complete or layered");
        }

        if (!m.TryGetValue("files", out JsonElement files))
        {
            m["files"] = files = JsonDocument.Parse("{}").RootElement;
        }

        if (files.ValueKind != JsonValueKind.Object)
        {
            return (null, $"{Manifest} files must be an object");
        }

        foreach (JsonProperty p in files.EnumerateObject())
        {
            if (Path.GetFileName(p.Name) != p.Name || !File.Exists(Path.Combine(folder, p.Name)))
            {
                return (null, $"{Manifest} names '{p.Name}', which is not a file in the folder");
            }
        }

        if (!m.TryGetValue("contains_ea_data", out JsonElement ea) || (ea.ValueKind != JsonValueKind.True && ea.ValueKind != JsonValueKind.False))
        {
            return (null, $"{Manifest} must declare contains_ea_data (true or false)");
        }

        return (m, "");
    }

    /// <summary>upstream's settings.json ultimaonlinedirectory, which the wizard writes; "" if none.</summary>
    public static string SavedSetting(string settingsFile)
    {
        if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile))
        {
            return "";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
            return doc.RootElement.TryGetProperty("ultimaonlinedirectory", out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return "";
        }
    }

    /// <summary>Where a UO install usually is on this platform, in the order tried.</summary>
    public static List<string> PlatformDefaults()
    {
        var out_ = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Measured: the EA installer writes this key (ADR-0021).
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Electronic Arts\EA Games\Ultima Online Classic"
                );
                if (key?.GetValue("InstallDir") is string dir && dir.Length > 0)
                {
                    out_.Add(dir);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }

            foreach (string env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string pf = System.Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(pf))
                {
                    out_.Add(Path.Combine(pf, "Electronic Arts", "Ultima Online Classic"));
                }
            }
        }
        else if (OperatingSystem.IsAndroid())
        {
            // The app's external files folder, where tools\android push puts the
            // data. Godot's user dir is /data/user/0/<package>/files; the package
            // is its parent's name. Not yet verified on a device.
            string package = Path.GetFileName(Path.GetDirectoryName(OS.GetUserDataDir().TrimEnd('/')) ?? "");
            if (package.Length > 0)
            {
                out_.Add($"/sdcard/Android/data/{package}/files/uo");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            string home = System.Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                out_.Add(Path.Combine(home, "UO"));
            }
        }

        return out_;
    }

    /// <summary>Writes a layered folder's overrides as upstream's files_override file.</summary>
    public static string WriteOverride(Result r, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, r.Overrides.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return path;
    }
}
