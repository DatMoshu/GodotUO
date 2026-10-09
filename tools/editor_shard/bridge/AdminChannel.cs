// The bridge's admin channel (ADR-0035): who may run an admin op, at what
// access level, and the audit log of every one.
//
// Map editing (block, object, multi, mobiles, ...) stays as ADR-0012 left it:
// a loopback connection that has said hello. An admin op needs more: the
// per-server admin token, sent once in that hello as "admin_token". The token
// comes from the environment the shard is started with
// (GUO_BRIDGE_ADMIN_TOKEN; tools/editor_shard passes the one in the user's
// workspace secrets file). A server started without one offers no admin ops
// at all.
//
// Every admin op names the access level it runs at; a connection holds the
// level the token grants (GUO_BRIDGE_ADMIN_ACCESS, Administrator by default)
// and runs only ops at or below it. Every admin op, and every refused admin
// hello, is written to the audit log: who, what, when, the outcome. Fields
// whose name says password, token or secret are masked before anything is
// written, so neither the log nor the tab ever holds one.
//
// Plain .NET, no ModernUO types: tests/ compiles this file on its own.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GUO.EditorBridge;

/// <summary>ModernUO's AccessLevel, by value (Server.AccessLevel); kept here so this file needs no server.</summary>
public enum AdminLevel
{
    Player,
    Counselor,
    GameMaster,
    Seer,
    Administrator,
    Developer,
    Owner,
}

public sealed class AdminChannel
{
    /// <summary>Refused admin hellos a connection may make before the bridge closes it.</summary>
    public const int MaxRefusals = 3;

    /// <summary>The pause after a refused token, so a connection cannot try them quickly.</summary>
    public const int RefusalDelayMs = 1000;

    /// <summary>
    /// Every admin op and the access level it runs at. An op not listed here is
    /// not an admin op. AD2-AD6 add theirs (god view, accounts, ...).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AdminLevel> Ops = new Dictionary<string, AdminLevel>
    {
        // Who this connection is to the admin channel: its level and the ops it may run.
        ["admin_whoami"] = AdminLevel.Counselor,
        // The last audit entries (newest last), for the tab's log.
        ["admin_audit"] = AdminLevel.Administrator,
        // AD1, the Admin tab's Health panel: uptime, who is online, world size, memory, last save, version.
        ["admin_status"] = AdminLevel.Counselor,
        // AD1, Save now (and the save before a Restart): a world save, answered when it is on disk.
        ["admin_save"] = AdminLevel.Administrator,
    };

    private readonly byte[] _tokenHash;

    public AdminLevel Grants { get; }

    public AuditLog Audit { get; }

    /// <summary>True when the server was started with a token: admin ops exist.</summary>
    public bool Enabled => _tokenHash != null;

    public AdminChannel(string token, AdminLevel grants, AuditLog audit)
    {
        // Compared as hashes, in fixed time: neither the length nor a prefix of
        // the token leaks through how fast a wrong one is refused.
        _tokenHash = string.IsNullOrEmpty(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
        Grants = grants;
        Audit = audit;
    }

    /// <summary>The channel as the shard's environment sets it up.</summary>
    public static AdminChannel FromEnvironment(string auditPath)
    {
        string token = Environment.GetEnvironmentVariable("GUO_BRIDGE_ADMIN_TOKEN");
        AdminLevel grants = Enum.TryParse(Environment.GetEnvironmentVariable("GUO_BRIDGE_ADMIN_ACCESS"), true, out AdminLevel l)
            ? l
            : AdminLevel.Administrator;
        return new AdminChannel(token, grants, new AuditLog(auditPath));
    }

    public static bool IsAdminOp(string op) => op != null && Ops.ContainsKey(op);

    /// <summary>
    /// The admin part of a hello. Returns the level granted, or null with the
    /// reason in <paramref name="refused"/> (null too when no token was offered:
    /// a plain map-editing hello is not a refusal).
    /// </summary>
    public AdminLevel? CheckHello(string offered, out string refused)
    {
        refused = null;
        if (string.IsNullOrEmpty(offered))
        {
            return null;
        }

        if (!Enabled)
        {
            refused = "this server has no admin token";
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(offered));
        if (!CryptographicOperations.FixedTimeEquals(hash, _tokenHash))
        {
            refused = "admin token refused";
            return null;
        }

        return Grants;
    }

    /// <summary>Refusal text if a connection holding <paramref name="held"/> may not run the admin op, or null.</summary>
    public string Authorise(string op, AdminLevel? held)
    {
        if (!Ops.TryGetValue(op, out AdminLevel needs))
        {
            return $"'{op}' is not an admin op";
        }

        if (!Enabled)
        {
            return "this server has no admin token";
        }

        if (held == null)
        {
            return "admin op without the admin token: send admin_token in hello";
        }

        if (held.Value < needs)
        {
            return $"'{op}' needs {needs}; this connection holds {held.Value}";
        }

        return null;
    }

    /// <summary>The admin ops a connection holding <paramref name="held"/> may run.</summary>
    public JsonArray OpsFor(AdminLevel held)
    {
        var ops = new JsonArray();
        foreach (var (op, needs) in Ops)
        {
            if (held >= needs)
            {
                ops.Add(op);
            }
        }

        return ops;
    }
}

/// <summary>
/// The admin audit log: one JSON object per line, appended, and the last
/// entries in memory for the tab. Values are masked before they are kept.
/// </summary>
public sealed class AuditLog
{
    public const int Keep = 200;

    private static readonly string[] SecretWords = { "password", "token", "secret", "passphrase" };

    private readonly string _path;
    private readonly LinkedList<JsonObject> _recent = new();
    private readonly object _lock = new();

    public AuditLog(string path) => _path = path;

    public string Path => _path;

    /// <summary>True when a field of this name may hold a secret.</summary>
    public static bool IsSecretName(string name)
    {
        foreach (string w in SecretWords)
        {
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A deep copy of the node with every secret-named field's value replaced by "***".</summary>
    public static JsonNode Mask(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                {
                    copy[k] = IsSecretName(k) ? "***" : Mask(v);
                }

                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                foreach (JsonNode v in a)
                {
                    copy.Add(Mask(v));
                }

                return copy;
            }
            default:
                return node == null ? null : JsonNode.Parse(node.ToJsonString());
        }
    }

    /// <summary>
    /// Records one admin op: when (UTC), the editor's name, the op, the level it
    /// ran at, whether it ran, and the request's fields (masked) or the refusal.
    /// </summary>
    public JsonObject Record(string editor, string op, AdminLevel? level, bool ok, JsonNode request, string error = null)
    {
        var args = new JsonObject();
        if (request is JsonObject r)
        {
            foreach (var (k, v) in r)
            {
                if (k != "op")
                {
                    args[k] = IsSecretName(k) ? "***" : Mask(v);
                }
            }
        }

        var entry = new JsonObject
        {
            ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["editor"] = editor,
            ["op"] = op,
            ["level"] = level?.ToString(),
            ["ok"] = ok,
            ["args"] = args,
        };
        if (error != null)
        {
            entry["error"] = error;
        }

        lock (_lock)
        {
            _recent.AddLast(entry);
            while (_recent.Count > Keep)
            {
                _recent.RemoveFirst();
            }

            if (_path != null)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
                    File.AppendAllText(_path, entry.ToJsonString() + "\n", new UTF8Encoding(false));
                }
                catch (IOException)
                {
                    // The in-memory log still has it; the tab shows it.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return entry;
    }

    /// <summary>The last <paramref name="count"/> entries, oldest first (copies).</summary>
    public JsonArray Recent(int count)
    {
        var list = new JsonArray();
        lock (_lock)
        {
            int skip = Math.Max(0, _recent.Count - count);
            foreach (JsonObject e in _recent)
            {
                if (skip-- > 0)
                {
                    continue;
                }

                list.Add(JsonNode.Parse(e.ToJsonString()));
            }
        }

        return list;
    }
}
