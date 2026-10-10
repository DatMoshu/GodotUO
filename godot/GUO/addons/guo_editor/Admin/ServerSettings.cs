#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port. The Admin tab's Settings form (AD4) without Godot: the per-backend schema, the server's
// configuration files as the form sees them, validation, the diff and the write. Plain .NET, so
// tools/server_manager/tests compiles and tests this file as it stands (it defines TOOLS).
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>One setting on the form: where it lives (a file of the schema and a path in it), its plain label and its type.</summary>
internal sealed class SettingsField
{
    public string Group { get; init; } = "";
    public string File { get; init; } = "";
    /// <summary>The path in the file's JSON, segments split by '/' (a ModernUO setting's own name has dots).</summary>
    public string Path { get; init; } = "";
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";
    /// <summary>bool, int, number, timespan, string, enum, email, url, list, listeners, folders, secret or expansion.</summary>
    public string Type { get; init; } = "string";
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string MinSpan { get; init; }
    public string MaxSpan { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public bool Optional { get; init; }
    public string[] Options { get; init; } = Array.Empty<string>();
    public string[] OptionLabels { get; init; } = Array.Empty<string>();
    /// <summary>For a secret: its key in the workspace secrets file (UO_SHARD_EMAIL_PASSWORD).</summary>
    public string SecretKey { get; init; }
    /// <summary>True for a line of the "other" group: no plain label yet, shown by its own name.</summary>
    public bool IsOther { get; init; }

    /// <summary>The field's id on the form and in the audit: "file:path".</summary>
    public string Id => File + ":" + Path;

    public bool IsSecret => Type == "secret";

    public bool IsList => Type is "list" or "listeners" or "folders";

    public string OptionLabel(string value)
    {
        int i = Array.IndexOf(Options, value);
        return i >= 0 && i < OptionLabels.Length ? OptionLabels[i] : value;
    }
}

/// <summary>A group of fields under one heading.</summary>
internal sealed class SettingsGroup
{
    public string Name { get; init; } = "";
    public string Help { get; init; } = "";
    public List<SettingsField> Fields { get; } = new();
}

/// <summary>
/// The per-backend schema (Admin/Schemas/&lt;backend&gt;.settings.json; docs/data_formats.md section 36): which
/// configuration files the form covers, its groups and plain labels, and where the lines with no label yet come from.
/// </summary>
internal sealed class SettingsSchema
{
    public string Backend { get; private set; } = "";
    public string Title { get; private set; } = "";
    /// <summary>File id to its path under the server's folder.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    /// <summary>The server's own table of expansions, under its folder (ModernUO's Data/expansions.json), or null.</summary>
    public string ExpansionTable { get; private set; }
    public List<SettingsGroup> Groups { get; } = new();
    public string OtherFile { get; private set; }
    public string OtherPath { get; private set; }
    public string OtherGroup { get; private set; } = "Other settings";
    public string OtherHelp { get; private set; } = "";

    public static SettingsSchema Parse(string json)
    {
        JsonNode root = JsonNode.Parse(json) ?? throw new InvalidDataException("empty settings schema");
        var s = new SettingsSchema
        {
            Backend = (string)root["backend"] ?? throw new InvalidDataException("settings schema without a backend"),
            Title = (string)root["title"] ?? (string)root["backend"],
            ExpansionTable = (string)root["expansion_table"],
        };
        foreach (var (id, path) in root["files"]?.AsObject() ?? new JsonObject())
        {
            s.Files[id] = (string)path;
        }

        if (root["other"] is JsonObject other)
        {
            s.OtherFile = (string)other["file"];
            s.OtherPath = (string)other["path"];
            s.OtherGroup = (string)other["group"] ?? s.OtherGroup;
            s.OtherHelp = (string)other["help"] ?? "";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode g in root["groups"]?.AsArray() ?? new JsonArray())
        {
            var group = new SettingsGroup { Name = (string)g["name"] ?? "", Help = (string)g["help"] ?? "" };
            foreach (JsonNode f in g["fields"]?.AsArray() ?? new JsonArray())
            {
                string type = (string)f["type"] ?? "string";
                string min = f["min"]?.ToJsonString(), max = f["max"]?.ToJsonString();
                var field = new SettingsField
                {
                    Group = group.Name,
                    File = (string)f["file"] ?? throw new InvalidDataException($"a field of '{group.Name}' names no file"),
                    Path = (string)f["path"] ?? throw new InvalidDataException($"a field of '{group.Name}' has no path"),
                    Label = (string)f["label"] ?? (string)f["path"],
                    Help = (string)f["help"] ?? "",
                    Type = type,
                    Min = type == "timespan" ? null : f["min"] is JsonValue a ? a.GetValue<double>() : null,
                    Max = type == "timespan" ? null : f["max"] is JsonValue b ? b.GetValue<double>() : null,
                    MinSpan = type == "timespan" ? (string)f["min"] : null,
                    MaxSpan = type == "timespan" ? (string)f["max"] : null,
                    MinLength = (int?)f["min_length"],
                    MaxLength = (int?)f["max_length"],
                    Optional = (bool?)f["optional"] ?? false,
                    Options = f["options"]?.AsArray().Select(o => (string)o).ToArray() ?? Array.Empty<string>(),
                    OptionLabels = f["option_labels"]?.AsArray().Select(o => (string)o).ToArray() ?? Array.Empty<string>(),
                    SecretKey = (string)f["secret_key"],
                };
                if (!s.Files.ContainsKey(field.File))
                {
                    throw new InvalidDataException($"'{field.Label}' names file '{field.File}', which the schema does not list");
                }

                if (field.IsSecret && string.IsNullOrEmpty(field.SecretKey))
                {
                    throw new InvalidDataException($"'{field.Label}' is a secret without a secret_key");
                }

                if (!seen.Add(field.Id))
                {
                    throw new InvalidDataException($"'{field.Id}' is in the schema twice");
                }

                group.Fields.Add(field);
            }

            s.Groups.Add(group);
        }

        return s;
    }

    /// <summary>The schema's file for a backend, beside this class (res://addons/guo_editor/Admin/Schemas), or null.</summary>
    public static string FileName(string backend) => string.IsNullOrEmpty(backend) ? null : backend.ToLowerInvariant() + ".settings.json";

    /// <summary>
    /// Which schema a server folder takes: the profile's backend when it names one, else what the folder holds
    /// (ModernUO keeps Configuration/modernuo.json). Null when no schema fits yet.
    /// </summary>
    public static string BackendFor(string backend, string serverDirectory)
    {
        if (string.Equals(backend, "modernuo", StringComparison.OrdinalIgnoreCase))
        {
            return "modernuo";
        }

        return !string.IsNullOrEmpty(serverDirectory) && System.IO.File.Exists(System.IO.Path.Combine(serverDirectory, "Configuration", "modernuo.json"))
            ? "modernuo"
            : null;
    }
}

/// <summary>One line of the diff.</summary>
internal sealed record SettingsChange(SettingsField Field, string From, string To)
{
    /// <summary>The change in plain words; a secret's value is never in it.</summary>
    public string Describe()
    {
        string name = Field.IsOther ? Field.Path.Split('/').Last() : $"{Field.Group}: {Field.Label}";
        if (Field.IsSecret)
        {
            return To == null ? $"{name}: cleared" : $"{name}: changed (kept in your secrets file)";
        }

        return $"{name}: {Shown(From)} -> {Shown(To)}";

        string Shown(string v) => Field.Type switch
        {
            _ when v == null => "(not set)",
            "bool" => v == "True" ? "on" : "off",
            "enum" or "expansion" => Field.OptionLabel(v),
            _ when Field.IsList => v.Length == 0 ? "(none)" : string.Join(", ", v.Split('\n')),
            _ => v.Length == 0 ? "(empty)" : v,
        };
    }
}

/// <summary>What a write did.</summary>
internal sealed record SettingsWriteResult(IReadOnlyList<string> Files, string PreviousFolder, IReadOnlyList<string> SecretsWritten);

/// <summary>
/// A server's configuration as the Settings form edits it: loaded from the files the schema names, values held as
/// text, checked, compared and written back. A secret's value is read only to compare it with the secrets file; it is
/// never handed to the form, the diff, a log or the audit.
/// </summary>
internal sealed class ServerSettings
{
    /// <summary>Kept sets of previous files, oldest removed first.</summary>
    public const int KeepPrevious = 10;

    /// <summary>The folder, under Configuration, that keeps the files a save replaced.</summary>
    public const string PreviousFolderName = "GUO-previous";

    /// <summary>What a kept copy holds instead of a secret's value.</summary>
    public const string Masked = "***";

    private static readonly string[] SecretWords = { "password", "token", "secret", "passphrase", "webhook" };

    private readonly Dictionary<string, JsonNode> _docs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secretEdits = new(StringComparer.Ordinal);
    private readonly Func<string, string> _secretSource;

    public SettingsSchema Schema { get; }
    public string ServerDirectory { get; }
    public List<SettingsGroup> Groups { get; } = new();
    public IEnumerable<SettingsField> Fields => Groups.SelectMany(g => g.Fields);
    /// <summary>Files of the schema the server folder does not have (their fields are shown as missing).</summary>
    public List<string> MissingFiles { get; } = new();

    /// <param name="secretSource">A secret's value in the secrets file by its key (ShardSecrets.ReadFile), or null.</param>
    private ServerSettings(SettingsSchema schema, string serverDirectory, Func<string, string> secretSource)
    {
        Schema = schema;
        ServerDirectory = serverDirectory;
        _secretSource = secretSource ?? (_ => null);
    }

    /// <summary>True when a setting of this name may hold a secret (the audit masks the same words, and webhooks).</summary>
    public static bool LooksSecret(string name) => SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static ServerSettings Load(SettingsSchema schema, string serverDirectory, Func<string, string> secretSource = null)
    {
        var s = new ServerSettings(schema, serverDirectory, secretSource);
        foreach (var (id, rel) in schema.Files)
        {
            string path = s.FullPath(id);
            if (!System.IO.File.Exists(path))
            {
                s.MissingFiles.Add(rel);
                continue;
            }

            try
            {
                s._docs[id] = JsonNode.Parse(System.IO.File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"{rel} is not valid JSON ({e.Message}); fix or delete it first", e);
            }
        }

        var labelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingsGroup g in schema.Groups)
        {
            var copy = new SettingsGroup { Name = g.Name, Help = g.Help };
            copy.Fields.AddRange(g.Fields);
            s.Groups.Add(copy);
            foreach (SettingsField f in g.Fields)
            {
                labelled.Add(f.Id);
            }
        }

        // Every other line of the "other" object, by its own name, so the form covers the whole file.
        if (schema.OtherFile != null && s.Node(schema.OtherFile, schema.OtherPath) is JsonObject other)
        {
            var group = new SettingsGroup { Name = schema.OtherGroup, Help = schema.OtherHelp };
            foreach (var (key, value) in other.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                string path = schema.OtherPath + "/" + key;
                if (labelled.Contains(schema.OtherFile + ":" + path))
                {
                    continue;
                }

                string text = Text(value);
                bool secret = LooksSecret(key);
                group.Fields.Add(new SettingsField
                {
                    Group = group.Name, File = schema.OtherFile, Path = path, Label = key, IsOther = true, Optional = true,
                    Type = secret ? "secret" : text is "True" or "False" ? "bool"
                        : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? "int"
                        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "number"
                        : text != null && Regex.IsMatch(text, @"^-?(\d+\.)?\d{1,2}:\d\d:\d\d(\.\d+)?$") ? "timespan"
                        : "string",
                    SecretKey = secret ? "UO_SHARD_SETTING_" + Regex.Replace(key.ToUpperInvariant(), "[^A-Z0-9]", "_") : null,
                });
            }

            if (group.Fields.Count > 0)
            {
                s.Groups.Add(group);
            }
        }

        foreach (SettingsField f in s.Fields)
        {
            string v = f.IsSecret ? null : s.Read(f);
            s._loaded[f.Id] = v;
            s._values[f.Id] = v;
        }

        return s;
    }

    public string FullPath(string fileId) => System.IO.Path.GetFullPath(System.IO.Path.Combine(ServerDirectory, Schema.Files[fileId]));

    public SettingsField Field(string id) => Fields.FirstOrDefault(f => f.Id == id);

    /// <summary>True when the server's file has the field at all (else the server uses its built-in default).</summary>
    public bool Present(SettingsField f) => _docs.TryGetValue(f.File, out JsonNode d) && Exists(d, f.Path);

    /// <summary>The field's value as the form shows it; never a secret's (null for those).</summary>
    public string Get(SettingsField f) => f.IsSecret ? null : _values.GetValueOrDefault(f.Id);

    /// <summary>The value as loaded from the file.</summary>
    public string Loaded(SettingsField f) => f.IsSecret ? null : _loaded.GetValueOrDefault(f.Id);

    public void Set(SettingsField f, string value)
    {
        if (f.IsSecret)
        {
            throw new InvalidOperationException("a secret is set with SetSecret");
        }

        _values[f.Id] = f.IsList ? NormaliseList(value) : value;
    }

    /// <summary>A new value for a secret ("" clears it); null forgets an edit.</summary>
    public void SetSecret(SettingsField f, string value)
    {
        if (value == null)
        {
            _secretEdits.Remove(f.Id);
        }
        else
        {
            _secretEdits[f.Id] = value;
        }
    }

    /// <summary>Whether the server's file holds a value for the secret (the form shows "set", never the value).</summary>
    public bool SecretSet(SettingsField f) => !string.IsNullOrEmpty(Text(Node(f.File, f.Path)));

    /// <summary>Whether the secrets file holds this secret.</summary>
    public bool SecretKept(SettingsField f) => !string.IsNullOrEmpty(_secretSource(f.SecretKey));

    public bool SecretEdited(SettingsField f) => _secretEdits.ContainsKey(f.Id);

    // What the server's file should hold for a secret after a save: the form's new value, else the secrets file's
    // (it is where GUO keeps the value), else what the server already has.
    private string SecretTarget(SettingsField f, out bool fromForm)
    {
        fromForm = _secretEdits.TryGetValue(f.Id, out string edit);
        if (fromForm)
        {
            return edit.Length == 0 ? null : edit;
        }

        string kept = _secretSource(f.SecretKey);
        return !string.IsNullOrEmpty(kept) ? kept : Text(Node(f.File, f.Path));
    }

    /// <summary>What changed, in schema order. A secret appears without its value.</summary>
    public List<SettingsChange> Diff()
    {
        var list = new List<SettingsChange>();
        foreach (SettingsField f in Fields)
        {
            if (f.IsSecret)
            {
                string now = Text(Node(f.File, f.Path));
                string target = SecretTarget(f, out _);
                if (!string.Equals(now ?? "", target ?? "", StringComparison.Ordinal))
                {
                    list.Add(new SettingsChange(f, null, target));
                }

                continue;
            }

            string from = _loaded.GetValueOrDefault(f.Id), to = _values.GetValueOrDefault(f.Id);
            if (!string.Equals(from ?? "", to ?? "", StringComparison.Ordinal))
            {
                list.Add(new SettingsChange(f, from, to));
            }
        }

        return list;
    }

    /// <summary>Problems with the changed values, by field id, in plain words. Empty when the form may be saved.</summary>
    public Dictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SettingsChange c in Diff())
        {
            string why = c.Field.IsSecret ? CheckSecret(_secretEdits.GetValueOrDefault(c.Field.Id)) : Check(c.Field, c.To);
            if (why != null)
            {
                errors[c.Field.Id] = why;
            }
        }

        return errors;
    }

    /// <summary>Warnings that do not stop a save (a server opened beyond this computer).</summary>
    public List<string> Warnings()
    {
        var warn = new List<string>();
        foreach (SettingsChange c in Diff().Where(c => c.Field.Type == "listeners" && c.To != null))
        {
            foreach (string line in c.To.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryEndPoint(line, out IPEndPoint ep) && !IPAddress.IsLoopback(ep.Address))
                {
                    warn.Add($"{line} is reachable from other computers, not only this one.");
                }
            }
        }

        return warn;
    }

    // A secret travels in a .bat line: no quote, percent sign, caret, exclamation mark or line break.
    private static string CheckSecret(string value) =>
        value == null || value.Length == 0 ? null
        : value.IndexOfAny(new[] { '"', '%', '^', '!', '\r', '\n' }) >= 0 ? "may not hold \" % ^ ! or a line break (the secrets file is a .bat)"
        : value.Length > 1024 ? "is longer than 1024 characters"
        : null;

    /// <summary>Why a value does not fit the field, or null.</summary>
    public static string Check(SettingsField f, string v)
    {
        v ??= "";
        if (v.Length == 0)
        {
            return f.Optional || f.IsList && f.Type == "list" ? null : "may not be empty";
        }

        var inv = CultureInfo.InvariantCulture;
        switch (f.Type)
        {
            case "bool":
                return v is "True" or "False" ? null : "must be on or off";
            case "int":
                if (!long.TryParse(v, NumberStyles.Integer, inv, out long n))
                {
                    return "must be a whole number";
                }

                return Range(n);
            case "number":
                if (!double.TryParse(v, NumberStyles.Float, inv, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                {
                    return "must be a number (a dot for decimals)";
                }

                return Range(d);
            case "timespan":
                if (!TimeSpan.TryParse(v, inv, out TimeSpan t))
                {
                    return "must be a time: hh:mm:ss, or d.hh:mm:ss for days";
                }

                if (f.MinSpan != null && t < TimeSpan.Parse(f.MinSpan, inv))
                {
                    return $"must be at least {f.MinSpan}";
                }

                return f.MaxSpan != null && t > TimeSpan.Parse(f.MaxSpan, inv) ? $"must be at most {f.MaxSpan}" : null;
            case "enum":
            case "expansion":
                return f.Options.Contains(v) ? null : "must be one of the choices";
            case "email":
                return Regex.IsMatch(v, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") ? null : "must be an email address (name@example.com)";
            case "url":
                return Uri.TryCreate(v, UriKind.Absolute, out Uri u) && (u.Scheme == "http" || u.Scheme == "https") ? null : "must be an http:// or https:// address";
            case "listeners":
                foreach (string line in v.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryEndPoint(line, out _))
                    {
                        return $"'{line}' is not an address and port (127.0.0.1:2593)";
                    }
                }

                return null;
            case "folders":
                foreach (string line in v.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!System.IO.Path.IsPathFullyQualified(line) || !Directory.Exists(line))
                    {
                        return $"'{line}' is not a folder on this computer";
                    }
                }

                return null;
            case "list":
                return null;
            default:
                if (f.MinLength is int lo && v.Length < lo)
                {
                    return $"must be at least {lo} characters";
                }

                return f.MaxLength is int hi && v.Length > hi ? $"must be at most {hi} characters" : null;
        }

        string Range(double x) =>
            f.Min is double min && x < min ? $"must be at least {min.ToString(inv)}"
            : f.Max is double max && x > max ? $"must be at most {max.ToString(inv)}"
            : null;
    }

    private static bool TryEndPoint(string text, out IPEndPoint ep)
    {
        ep = null;
        return IPEndPoint.TryParse(text.Trim(), out ep) && ep.Port is > 0 and <= 65535 && text.Contains(':');
    }

    /// <summary>
    /// Writes the changes: each file that changes is first copied to Configuration/GUO-previous/&lt;UTC time&gt;/ with
    /// every secret masked, then replaced (a temporary file, then a move). A secret goes into the secrets file and the
    /// server's own file, nowhere else. The caller stops the server first: ModernUO writes some of these files from
    /// memory while it runs, which would undo a change made under it.
    /// </summary>
    /// <param name="writeSecret">Writes one key of the secrets file (ShardSecrets.Write); null value removes it.</param>
    public SettingsWriteResult Write(Action<string, string> writeSecret, DateTime utcNow)
    {
        if (Validate() is { Count: > 0 } errors)
        {
            throw new InvalidDataException("the form has problems: " + string.Join("; ", errors.Select(e => $"{Field(e.Key)?.Label}: {e.Value}")));
        }

        List<SettingsChange> changes = Diff();
        if (changes.Count == 0)
        {
            return new SettingsWriteResult(Array.Empty<string>(), null, Array.Empty<string>());
        }

        // Apply to copies of the documents; the loaded ones stay as the files were until every write is done.
        var docs = _docs.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone(), StringComparer.Ordinal);
        var touched = new List<string>();
        var secrets = new List<(string Key, string Value)>();

        // The expansion first: it replaces the whole expansion file from the server's table, then the facet lines apply.
        foreach (SettingsChange c in changes.Where(c => c.Field.Type == "expansion"))
        {
            docs[c.Field.File] = ExpansionDoc(c.To, docs.GetValueOrDefault(c.Field.File));
            touched.Add(c.Field.File);
        }

        foreach (SettingsChange c in changes.Where(c => c.Field.Type != "expansion"))
        {
            SettingsField f = c.Field;
            if (!docs.TryGetValue(f.File, out JsonNode doc))
            {
                throw new InvalidDataException($"{Schema.Files[f.File]} is missing, so '{f.Label}' cannot be saved; start the server once to make it");
            }

            if (f.IsSecret)
            {
                string target = SecretTarget(f, out bool fromForm);
                SetNode(doc, f.Path, target == null ? null : JsonValue.Create(target));
                if (fromForm)
                {
                    secrets.Add((f.SecretKey, target));
                }
            }
            else
            {
                SetNode(doc, f.Path, ToNode(f, Node(doc, f.Path), c.To));
            }

            if (!touched.Contains(f.File))
            {
                touched.Add(f.File);
            }
        }

        string stamp = utcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z";
        string configDir = System.IO.Path.GetDirectoryName(FullPath(touched[0]));
        string previous = System.IO.Path.Combine(configDir, PreviousFolderName, stamp);
        Directory.CreateDirectory(previous);
        foreach (string id in touched)
        {
            if (_docs.TryGetValue(id, out JsonNode before))
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(previous, System.IO.Path.GetFileName(FullPath(id))), Serialise(MaskSecrets(id, before)), new UTF8Encoding(false));
            }
        }

        PrunePrevious(System.IO.Path.Combine(configDir, PreviousFolderName));

        // Secrets first: if the secrets file cannot be written, the server's files stay as they were.
        foreach (var (key, value) in secrets)
        {
            writeSecret?.Invoke(key, value);
        }

        var written = new List<string>();
        foreach (string id in touched)
        {
            string path = FullPath(id);
            string tmp = path + ".guo-tmp";
            System.IO.File.WriteAllText(tmp, Serialise(docs[id]), new UTF8Encoding(false));
            System.IO.File.Move(tmp, path, overwrite: true);
            written.Add(Schema.Files[id]);
            _docs[id] = docs[id];
        }

        // The form now starts from what was written.
        foreach (SettingsField f in Fields)
        {
            _loaded[f.Id] = f.IsSecret ? null : Read(f);
            _values[f.Id] = _loaded[f.Id];
        }

        _secretEdits.Clear();
        return new SettingsWriteResult(written, previous, secrets.Select(s => s.Key).ToList());
    }

    /// <summary>The changes as the audit gets them: values of plain settings, never a secret's.</summary>
    public JsonArray AuditChanges()
    {
        var list = new JsonArray();
        foreach (SettingsChange c in Diff())
        {
            var o = new JsonObject { ["file"] = Schema.Files[c.Field.File], ["key"] = c.Field.Path };
            if (c.Field.IsSecret)
            {
                o["secret"] = true;
                o["cleared"] = c.To == null;
            }
            else
            {
                o["from"] = c.From;
                o["to"] = c.To;
            }

            list.Add(o);
        }

        return list;
    }

    // The new expansion file: the server's own entry for that expansion, with the facets the server has now.
    private JsonNode ExpansionDoc(string name, JsonNode current)
    {
        SettingsField f = Fields.First(x => x.Type == "expansion");
        int id = Array.IndexOf(f.Options, name);
        string table = Schema.ExpansionTable == null ? null : System.IO.Path.Combine(ServerDirectory, Schema.ExpansionTable);
        if (id < 0 || table == null || !System.IO.File.Exists(table))
        {
            throw new InvalidDataException($"the server's table of expansions ({Schema.ExpansionTable}) is missing, so the expansion cannot change");
        }

        JsonNode entry = JsonNode.Parse(System.IO.File.ReadAllText(table))?.AsArray().FirstOrDefault(e => (int?)e?["Id"] == id)
            ?? throw new InvalidDataException($"the server's table of expansions has no entry {id} ({name})");
        JsonNode doc = entry.DeepClone();
        if (current?["MapSelectionFlags"] is JsonNode maps)
        {
            doc["MapSelectionFlags"] = maps.DeepClone();
        }

        return doc;
    }

    // A copy of a file's document with every secret's value replaced by Masked: the schema's secrets and any line
    // whose name looks like one.
    private JsonNode MaskSecrets(string fileId, JsonNode doc)
    {
        JsonNode copy = doc.DeepClone();
        foreach (SettingsField f in Fields.Where(f => f.IsSecret && f.File == fileId))
        {
            if (!string.IsNullOrEmpty(Text(Node(copy, f.Path))))
            {
                SetNode(copy, f.Path, JsonValue.Create(Masked));
            }
        }

        MaskByName(copy);
        return copy;

        static void MaskByName(JsonNode n)
        {
            if (n is JsonObject o)
            {
                foreach (string k in o.Select(kv => kv.Key).ToList())
                {
                    if (LooksSecret(k) && o[k] is JsonValue v && !string.IsNullOrEmpty(Text(v)))
                    {
                        o[k] = Masked;
                    }
                    else
                    {
                        MaskByName(o[k]);
                    }
                }
            }
            else if (n is JsonArray a)
            {
                foreach (JsonNode x in a)
                {
                    MaskByName(x);
                }
            }
        }
    }

    private static void PrunePrevious(string root)
    {
        var sets = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToList();
        foreach (string old in sets.Take(Math.Max(0, sets.Count - KeepPrevious)))
        {
            try
            {
                Directory.Delete(old, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ModernUO's own layout: two spaces, LF, a newline at the end.
    private static string Serialise(JsonNode doc)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            doc.WriteTo(w);
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private string Read(SettingsField f)
    {
        if (f.Type == "expansion")
        {
            return Node(f.File, f.Path) is JsonValue v && v.TryGetValue(out int id) && id >= 0 && id < f.Options.Length ? f.Options[id] : null;
        }

        JsonNode n = Node(f.File, f.Path);
        return f.IsList
            ? n is JsonArray a ? string.Join("\n", a.Select(x => Text(x))) : n == null ? null : Text(n)
            : Text(n);
    }

    // A JSON value as the form's text: strings as they are, booleans as True/False (ModernUO's own spelling), numbers invariant.
    private static string Text(JsonNode n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string s) => s,
        JsonValue v when v.TryGetValue(out bool b) => b ? "True" : "False",
        JsonValue v => v.ToJsonString(),
        _ => n.ToJsonString(),
    };

    // The form's text as JSON of the same kind as the file had (ModernUO's settings are all strings; the other files
    // use real booleans and numbers).
    private static JsonNode ToNode(SettingsField f, JsonNode old, string text)
    {
        if (f.IsList)
        {
            var a = new JsonArray();
            foreach (string line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                a.Add(line.Trim());
            }

            return a;
        }

        if (f.Type == "expansion")
        {
            return JsonValue.Create(Array.IndexOf(f.Options, text));
        }

        if (string.IsNullOrEmpty(text) && f.Optional && (old == null || old.GetValueKind() == JsonValueKind.Null))
        {
            return null;
        }

        JsonValueKind kind = old?.GetValueKind() ?? (f.Path.StartsWith("settings/", StringComparison.Ordinal) ? JsonValueKind.String : f.Type switch
        {
            "bool" => JsonValueKind.True,
            "int" or "number" => JsonValueKind.Number,
            _ => JsonValueKind.String,
        });
        var inv = CultureInfo.InvariantCulture;
        return kind switch
        {
            JsonValueKind.True or JsonValueKind.False => JsonValue.Create(text == "True"),
            JsonValueKind.Number when long.TryParse(text, NumberStyles.Integer, inv, out long n) => JsonValue.Create(n),
            JsonValueKind.Number when double.TryParse(text, NumberStyles.Float, inv, out double d) => JsonValue.Create(d),
            _ when string.IsNullOrEmpty(text) && f.Optional => null,
            _ => JsonValue.Create(text),
        };
    }

    private static string NormaliseList(string text) =>
        string.Join("\n", (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private JsonNode Node(string fileId, string path) => _docs.TryGetValue(fileId, out JsonNode doc) ? Node(doc, path) : null;

    private static JsonNode Node(JsonNode doc, string path)
    {
        JsonNode n = doc;
        foreach (string seg in path.Split('/'))
        {
            if (n is not JsonObject o || !o.TryGetPropertyValue(seg, out n))
            {
                return null;
            }
        }

        return n;
    }

    private static bool Exists(JsonNode doc, string path)
    {
        string[] segs = path.Split('/');
        JsonNode n = Node(doc, string.Join("/", segs.Take(segs.Length - 1)));
        return segs.Length == 1 ? doc is JsonObject r && r.ContainsKey(segs[0]) : n is JsonObject o && o.ContainsKey(segs[^1]);
    }

    private static void SetNode(JsonNode doc, string path, JsonNode value)
    {
        string[] segs = path.Split('/');
        JsonObject o = doc.AsObject();
        foreach (string seg in segs.Take(segs.Length - 1))
        {
            if (o[seg] is not JsonObject next)
            {
                next = new JsonObject();
                o[seg] = next;
            }

            o = next;
        }

        o[segs[^1]] = value;
    }
}
#endif
