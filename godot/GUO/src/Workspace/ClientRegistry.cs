// GUO addition, not a port. ADR-0032: client profiles. Plain .NET (no Godot types).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUO.Workspace;

internal static class ClientKinds
{
    public const string GuoProject = "guo-project";
    public const string GuoBuild = "guo-build";
    public const string External = "external";

    public static readonly string[] All = { GuoProject, GuoBuild, External };

    public static bool IsValid(string kind) => Array.IndexOf(All, kind) >= 0;

    /// <summary>A program GUO starts (not a data set the running GUO reads): desktop only, never Android or web.</summary>
    public static bool NeedsDesktop(string kind) => kind != GuoProject;

    public static bool DesktopHost => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
}

/// <summary>Client.json: what a client is, apart from how it is launched.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ClientMeta
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = ClientKinds.GuoProject;
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("encryption")] public int? Encryption { get; set; }
    [JsonPropertyName("base_fingerprint")] public string BaseFingerprint { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";
}

/// <summary>One entry of clients.json.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ClientProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = Workspace.NewId();
    [JsonPropertyName("name")] public string Name { get; set; } = "New client";
    [JsonPropertyName("kind")] public string Kind { get; set; } = ClientKinds.GuoProject;
    [JsonPropertyName("program")] public string Program { get; set; } = "";
    [JsonPropertyName("arguments")] public string[] Arguments { get; set; } = Array.Empty<string>();
    [JsonPropertyName("working_dir")] public string WorkingDir { get; set; } = "";
    [JsonPropertyName("base_data")] public string BaseData { get; set; } = "";
    [JsonPropertyName("overlay")] public string Overlay { get; set; } = "";
    [JsonPropertyName("plugins")] public string[] Plugins { get; set; } = Array.Empty<string>();

    [JsonIgnore] public ClientMeta Meta { get; set; } = new();

    public ClientProfile Copy() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Program = Program, Arguments = (string[]) Arguments.Clone(), WorkingDir = WorkingDir,
        BaseData = BaseData, Overlay = Overlay, Plugins = (string[]) Plugins.Clone(),
        Meta = new ClientMeta { Kind = Meta.Kind, Version = Meta.Version, Encryption = Meta.Encryption, BaseFingerprint = Meta.BaseFingerprint, Source = Meta.Source },
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ClientList
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("clients")] public List<ClientProfile> Clients { get; set; } = new();
}

/// <summary>
/// The client profiles of the workspace, shared by the editor's run bar and the pregame Servers tab. It
/// reads and writes <c>profiles/clients.json</c> and each <c>clients/id/client.json</c>, strictly: an
/// unreadable or invalid file is reported and never overwritten.
/// </summary>
internal sealed class ClientRegistry
{
    public const int MaxClients = 64;
    public const long MaxBytes = 1024 * 1024;

    // A private options instance: the editor reloads this assembly, and a collectible type must not
    // enter System.Text.Json's process-wide default cache.
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static ClientRegistry _current;

    public List<ClientProfile> Clients { get; private set; } = new();

    /// <summary>Why the files could not be read; Save then refuses so they are not overwritten.</summary>
    public string LoadError { get; private set; }

    /// <summary>The workspace's registry, read once; <see cref="Reset"/> after the folder changes.</summary>
    public static ClientRegistry Current => _current ??= LoadOrEmpty();

    public static void Reset() => _current = null;

    public static ClientRegistry LoadOrEmpty()
    {
        try
        {
            return Load();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            return new ClientRegistry { LoadError = e.Message };
        }
    }

    public static ClientRegistry Load()
    {
        var registry = new ClientRegistry();
        string path = Workspace.ClientsFile;

        if (!File.Exists(path))
        {
            return registry;
        }

        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new InvalidDataException("clients.json exceeds 1 MB");
        }

        ClientList list = JsonSerializer.Deserialize<ClientList>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Invalid client list");

        if (list.Version != 1)
        {
            throw new InvalidDataException("clients.json is a newer version than this build understands");
        }

        registry.Clients = list.Clients ?? new();
        Validate(registry.Clients);

        foreach (ClientProfile c in registry.Clients)
        {
            string meta = Workspace.ClientFile(c.Id);

            if (File.Exists(meta))
            {
                if (new FileInfo(meta).Length > MaxBytes)
                {
                    throw new InvalidDataException("client.json exceeds 1 MB");
                }

                c.Meta = JsonSerializer.Deserialize<ClientMeta>(File.ReadAllBytes(meta), Json) ?? throw new InvalidDataException("Invalid client.json");
            }

            c.Meta.Kind = c.Kind;
        }

        return registry;
    }

    private static bool Absolute(string path) => string.IsNullOrEmpty(path) || Path.IsPathFullyQualified(path);

    public static void Validate(IReadOnlyCollection<ClientProfile> clients)
    {
        if (clients == null || clients.Count > MaxClients)
        {
            throw new InvalidDataException("At most 64 client profiles are supported");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (ClientProfile c in clients)
        {
            if (c == null || !Workspace.IsId(c.Id) || !ids.Add(c.Id))
            {
                throw new InvalidDataException("Invalid or repeated client identity");
            }

            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100)
            {
                throw new InvalidDataException("A client needs a name of up to 100 characters");
            }

            if (!ClientKinds.IsValid(c.Kind))
            {
                throw new InvalidDataException("Client kind must be guo-project, guo-build or external");
            }

            if (c.Kind != ClientKinds.GuoProject && string.IsNullOrWhiteSpace(c.Program))
            {
                throw new InvalidDataException("A " + c.Kind + " client needs its program");
            }

            if (c.Arguments == null || c.Arguments.Length > 32 || c.Arguments.Any(a => a == null || a.Length > 2048)
                || c.Plugins == null || c.Plugins.Length > 16)
            {
                throw new InvalidDataException("Too many client arguments or plugins");
            }

            foreach (string path in new[] { c.Program, c.WorkingDir, c.BaseData, c.Overlay }.Concat(c.Plugins))
            {
                if (!Absolute(path))
                {
                    throw new InvalidDataException("Choose absolute paths for client files");
                }
            }
        }
    }

    public void Validate() => Validate(Clients);

    public void Save()
    {
        if (LoadError != null)
        {
            throw new InvalidDataException("Repair clients.json before saving; it was not overwritten (" + LoadError + ")");
        }

        Validate();

        foreach (ClientProfile c in Clients)
        {
            c.Meta.Kind = c.Kind;
            Workspace.WriteAtomic(Workspace.ClientFile(c.Id), JsonSerializer.SerializeToUtf8Bytes(c.Meta, Json));
        }

        Workspace.WriteAtomic(Workspace.ClientsFile, JsonSerializer.SerializeToUtf8Bytes(new ClientList { Clients = Clients }, Json));
    }

    public ClientProfile Find(string id) => string.IsNullOrEmpty(id) ? null : Clients.FirstOrDefault(c => c.Id == id);

    public ClientProfile Add(ClientProfile profile)
    {
        var next = Clients.Where(c => c.Id != profile.Id).Append(profile).ToList();
        Validate(next);
        Clients = next;
        return profile;
    }

    /// <summary>Forgets a client; its files in the workspace are kept (the person may want its overlay).</summary>
    public bool Remove(string id) => Clients.RemoveAll(c => c.Id == id) > 0;

    /// <summary>A copy with a new identity; the overlay folder is not copied, the original's overlay path is kept if it was explicit.</summary>
    public ClientProfile Duplicate(string id)
    {
        ClientProfile original = Find(id) ?? throw new InvalidOperationException("No such client");
        ClientProfile copy = original.Copy();
        copy.Id = Workspace.NewId();
        copy.Name = original.Name.Length > 90 ? original.Name[..90] + " copy" : original.Name + " copy";
        copy.Meta.Source = "manual";
        return Add(copy);
    }

    /// <summary>
    /// The profile for a folder the pregame picked: a client with this base data (or overlay), made if there
    /// is none, so servers that use the same files share one client. The folder is read in place.
    /// </summary>
    public ClientProfile FindOrAddFolder(string name, string baseData, string overlay, string version, int? encryption, string source) =>
        FindOrAddProject(name, "", baseData, overlay, version, encryption, source);

    /// <summary>As <see cref="FindOrAddFolder"/> for a guo-project client at an explicit project folder ("" is the hosting project).</summary>
    public ClientProfile FindOrAddProject(string name, string program, string baseData, string overlay, string version, int? encryption, string source)
    {
        baseData ??= "";
        overlay ??= "";
        program ??= "";
        ClientProfile same = Clients.FirstOrDefault(c => c.Kind == ClientKinds.GuoProject && c.Program == program && c.BaseData == baseData
            && c.Overlay == overlay && c.Arguments.Length == 0 && c.Plugins.Length == 0);

        if (same != null)
        {
            return same;
        }

        string label = string.IsNullOrWhiteSpace(name) ? "Client" : name.Trim();
        var profile = new ClientProfile { Name = label.Length > 100 ? label[..100] : label, Program = program, BaseData = baseData, Overlay = overlay };
        profile.Meta = new ClientMeta
        {
            Version = version?.Trim() ?? "", Encryption = encryption, Source = source,
            BaseFingerprint = string.IsNullOrEmpty(baseData) ? "" : Fingerprint(baseData),
        };
        return Add(profile);
    }

    /// <summary>The overlay folder in force: the profile's own, else the workspace's when it exists, else null.</summary>
    public static string OverlayFolder(ClientProfile c)
    {
        if (!string.IsNullOrEmpty(c.Overlay))
        {
            return c.Overlay;
        }

        string folder = Workspace.ClientOverride(c.Id);
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// What the running GUO reads for a client: a custom (overlay) folder and an install folder, either null.
    /// Nothing for a program kind that is not GUO's own data (external, guo-build) or off the desktop.
    /// </summary>
    public bool Files(string id, out string custom, out string install)
    {
        custom = install = null;
        ClientProfile c = Find(id);

        if (c == null || c.Kind != ClientKinds.GuoProject)
        {
            return false;
        }

        custom = OverlayFolder(c);
        install = string.IsNullOrWhiteSpace(c.BaseData) ? null : c.BaseData;
        return custom != null || install != null;
    }

    /// <summary>SHA-256 over the sorted "name|length" lines of the install's top level .mul/.uop/.idx files; "" when none.</summary>
    public static string Fingerprint(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return "";
            }

            string[] lines = new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => f.Extension.Equals(".mul", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".uop", StringComparison.OrdinalIgnoreCase)
                    || f.Extension.Equals(".idx", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Name.ToLowerInvariant() + "|" + f.Length)
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();

            return lines.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Records the install's present fingerprint on a client (after the person edits or accepts it).</summary>
    public static void Refresh(ClientProfile c) => c.Meta.BaseFingerprint = Fingerprint(c.BaseData);

    /// <summary>
    /// The warning for starting <paramref name="c"/> against a server that expects <paramref name="expectedVersion"/>, or
    /// null when they agree or nothing is known to compare. Never blocks a launch.
    /// </summary>
    public static string Mismatch(string expectedVersion, ClientProfile c)
    {
        if (c == null)
        {
            return null;
        }

        var warnings = new List<string>();
        string have = c.Meta.Version?.Trim() ?? "";
        string want = expectedVersion?.Trim() ?? "";

        if (want.Length > 0 && have.Length > 0 && want != have)
        {
            warnings.Add($"server expects client {want}, this client is {have}");
        }

        string recorded = c.Meta.BaseFingerprint ?? "";

        if (recorded.Length > 0 && !string.IsNullOrEmpty(c.BaseData))
        {
            string now = Fingerprint(c.BaseData);

            if (now.Length > 0 && now != recorded)
            {
                warnings.Add("the client's data folder changed since the profile was saved");
            }
        }

        return warnings.Count == 0 ? null : string.Join("; ", warnings);
    }
}
