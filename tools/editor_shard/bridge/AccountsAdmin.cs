// The Admin tab's Accounts (AD5): the server's accounts listed, and an account
// made, given an access level, given a new password, banned or unbanned.
// AccountRules.cs decides what is allowed; this file does it to ModernUO's
// accounts. Game thread only.
//
// Requests (each with an optional "req" echoed back):
//   {"op":"admin_accounts"}                                            every account, by name
//   {"op":"admin_account","action":"create","account":"..","password":"..","access":"Player"}
//   {"op":"admin_account","action":"access","account":"..","access":"GameMaster"}
//   {"op":"admin_account","action":"password","account":"..","password":".."}
//   {"op":"admin_account","action":"ban"|"unban","account":".."}
// Replies: admin_accounts {"accounts":[{"name","access","created","last_login","characters":[{"name","online"}],
// "online","banned","protected"}],"count","truncated"}; admin_account {"account","action","row"} with the account's
// row as it is now, or {"ok":false,"error":".."} in plain words.
//
// The password travels only in the request's "password" field, which the audit
// log masks by its name (ADR-0035); no reply, log line or audit entry holds it.
// The editor makes a generated one itself, so the server never sends one back.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using Server;
using Server.Accounting;
using Server.Misc;
using Server.Network;

namespace GUO.EditorBridge;

internal static class AccountsAdmin
{
    private static string Utc(DateTime t) => t == DateTime.MinValue ? null : t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static bool IsProtected(Account a) =>
        ServerAccess.ServerAccessConfiguration?.ProtectedAccounts.Contains(a.Username.ToLowerInvariant()) == true;

    /// <summary>One account as the tab lists it.</summary>
    private static JsonObject Row(Account a)
    {
        var characters = new JsonArray();
        bool online = false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is { Deleted: false } m)
            {
                bool on = m.NetState != null;
                online |= on;
                characters.Add(new JsonObject { ["name"] = m.RawName, ["online"] = on });
            }
        }

        return new JsonObject
        {
            ["name"] = a.Username,
            ["access"] = a.AccessLevel.ToString(),
            ["created"] = Utc(a.Created),
            ["last_login"] = Utc(a.LastLogin),
            ["characters"] = characters,
            ["online"] = online,
            ["banned"] = a.Banned,
            // Reset to Owner by ModernUO at its next login with the right password (server-access.json).
            ["protected"] = IsProtected(a),
        };
    }

    /// <summary>admin_accounts: every account, by name (up to AccountRules.MaxListed).</summary>
    public static void List(JsonObject reply)
    {
        var rows = new JsonArray();
        int count = 0;
        foreach (Account a in Accounts.GetAccounts().OfType<Account>().OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase))
        {
            if (count++ < AccountRules.MaxListed)
            {
                rows.Add(Row(a));
            }
        }

        reply["accounts"] = rows;
        reply["count"] = count;
        reply["truncated"] = count > AccountRules.MaxListed;
    }

    /// <summary>admin_account: one change to one account. False, with the refusal in reply["error"], when it is refused.</summary>
    public static bool Act(JsonNode msg, JsonObject reply, AdminLevel held, out string done)
    {
        done = null;
        string action = (string)msg["action"];
        string name = ((string)msg["account"] ?? "").Trim();
        reply["action"] = action;
        reply["account"] = name;
        string error = action switch
        {
            "create" => Create(name, (string)msg["password"], (string)msg["access"], held, reply, out done),
            "access" => SetAccess(name, (string)msg["access"], held, reply, out done),
            "password" => SetPassword(name, (string)msg["password"], held, reply, out done),
            "ban" or "unban" => SetBanned(name, action == "ban", held, reply, out done),
            _ => $"admin_account needs an action: {string.Join(", ", AccountRules.Actions)}",
        };
        if (error != null)
        {
            reply["ok"] = false;
            reply["error"] = error;
            return false;
        }

        return true;
    }

    // The account to change, or null with the refusal: it must exist and be below the connection's level.
    private static Account Target(string name, AdminLevel held, out string error)
    {
        if (string.IsNullOrEmpty(name))
        {
            error = "name the account";
            return null;
        }

        if (Accounts.GetAccount(name) is not Account a)
        {
            error = $"there is no account '{name}'";
            return null;
        }

        error = AccountRules.CheckTarget(held, (AdminLevel)(int)a.AccessLevel);
        return error == null ? a : null;
    }

    private static string Create(string name, string password, string access, AdminLevel held, JsonObject reply, out string done)
    {
        done = null;
        AdminLevel level = AdminLevel.Player;
        if (!string.IsNullOrEmpty(access))
        {
            if (AccountRules.ParseLevel(access) is not { } parsed)
            {
                return $"'{access}' is not an access level";
            }

            level = parsed;
        }

        string error = AccountRules.CheckName(name) ?? AccountRules.CheckPassword(name, password)
            ?? (level == AdminLevel.Player ? null : AccountRules.CheckLevel(held, level));
        if (error != null)
        {
            return error;
        }

        if (Accounts.GetAccount(name) != null)
        {
            return $"there is already an account '{name}'";
        }

        var a = new Account(name, password) { AccessLevel = (AccessLevel)(int)level };
        reply["row"] = Row(a);
        done = $"made account '{a.Username}' ({level})";
        return null;
    }

    private static string SetAccess(string name, string access, AdminLevel held, JsonObject reply, out string done)
    {
        done = null;
        Account a = Target(name, held, out string error);
        if (a == null)
        {
            return error;
        }

        if (AccountRules.ParseLevel(access) is not { } level)
        {
            return $"'{access}' is not an access level";
        }

        if (AccountRules.CheckLevel(held, level) is { } refused)
        {
            return refused;
        }

        AccessLevel was = a.AccessLevel;
        a.AccessLevel = (AccessLevel)(int)level;
        // A character takes its account's level only when it is made (CharacterCreation), so the account's
        // characters are set too: otherwise a raised account keeps player characters, and a lowered one staff ones.
        int characters = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is { Deleted: false } m && m.AccessLevel != a.AccessLevel)
            {
                m.AccessLevel = a.AccessLevel;
                characters++;
            }
        }

        reply["characters_changed"] = characters;
        reply["row"] = Row(a);
        done = $"account '{a.Username}' {was} -> {a.AccessLevel} ({characters} character(s) too)";
        return null;
    }

    private static string SetPassword(string name, string password, AdminLevel held, JsonObject reply, out string done)
    {
        done = null;
        Account a = Target(name, held, out string error);
        if (a == null)
        {
            return error;
        }

        if (AccountRules.CheckPassword(a.Username, password) is { } refused)
        {
            return refused;
        }

        a.SetPassword(password);
        reply["row"] = Row(a);
        done = $"new password for account '{a.Username}'";
        return null;
    }

    private static string SetBanned(string name, bool ban, AdminLevel held, JsonObject reply, out string done)
    {
        done = null;
        Account a = Target(name, held, out string error);
        if (a == null)
        {
            return error;
        }

        // As the admin gump: no ban time or duration (a ban until it is lifted), and no ban dealer (the bridge has no mobile).
        a.SetUnspecifiedBan(null);
        a.Banned = ban;
        int kicked = 0;
        if (ban)
        {
            foreach (NetState ns in NetState.Instances.Where(ns => ns.Account == a).ToList())
            {
                ns.Disconnect("Banned by the Admin tab.");
                kicked++;
            }
        }

        reply["disconnected"] = kicked;
        reply["row"] = Row(a);
        done = ban ? $"banned account '{a.Username}' ({kicked} connection(s) closed)" : $"unbanned account '{a.Username}'";
        return null;
    }
}
