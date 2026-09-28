// GUO addition, not a port: upstream ClassicUO remembers one account, in
// settings.json. GUO keeps accounts per server in servers.json, with their
// passwords in the operating system's keystore (SecretStore).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Godot;

namespace GUO.Input.Touch.Pregame.Accounts;

/// <summary>One account saved for a server: its name, and where its password is kept, if anywhere.</summary>
internal sealed class SavedAccount
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Null when the player chose not to keep the password.</summary>
    [JsonPropertyName("secret")] public Secret Secret { get; set; }

    [JsonPropertyName("last_used")] public DateTime? LastUsed { get; set; }

    /// <summary>A dev account (a dev build's dev shard only): one click logs in as it (DevLogin).</summary>
    [JsonPropertyName("dev")] public bool Dev { get; set; }

    [JsonIgnore] public bool HasPassword => Secret != null && Secret.Store != SecretStore.None;
}

/// <summary>
/// The accounts of each server, kept on its <see cref="ServerEntry"/> in
/// servers.json. The classic login gump's "Save account" box and upstream's
/// settings.json fields are never read or written here.
/// </summary>
internal static class AccountBook
{
    /// <summary>A server's accounts, the last used first.</summary>
    public static IReadOnlyList<SavedAccount> For(ServerEntry e) =>
        (ServerBook.Holding(e)?.Accounts ?? new List<SavedAccount>())
        .OrderByDescending(a => a.LastUsed ?? DateTime.MinValue)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The binding a secret is made for: a copy moved onto another entry doesn't decrypt.</summary>
    public static string Binding(ServerEntry e, string name) => $"{e.Host?.Trim().ToLowerInvariant()}:{e.Port}:{name}";

    /// <summary>
    /// Saves an account for a server, with its password when <paramref name="keepPassword"/>
    /// and the store can. Null with the reason when it can't be added at all; a
    /// note in <paramref name="why"/> when it was added without its password.
    /// </summary>
    public static SavedAccount Add(ServerEntry e, string name, string password, bool keepPassword, out string why, bool dev = false)
    {
        why = null;
        name = name?.Trim() ?? "";

        if (name.Length == 0)
        {
            why = "Type the account name.";
            return null;
        }

        ServerEntry kept = ServerBook.Hold(e);
        kept.Accounts ??= new List<SavedAccount>();
        SavedAccount a = kept.Accounts.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

        if (a == null)
        {
            a = new SavedAccount { Name = name };
            kept.Accounts.Add(a);
        }
        else
        {
            SecretStore.Current.Forget(Binding(kept, name), a.Secret);
        }

        a.Secret = null;
        a.Dev = dev && e.Dev;

        if (keepPassword && !string.IsNullOrEmpty(password))
        {
            a.Secret = SecretStore.Current.Protect(Binding(kept, name), password, out why);
        }

        ServerBook.Save();
        GD.Print($"[GUO] accounts: saved an account for \"{kept.Name}\" ({(a.HasPassword ? "password in " + a.Secret.Store : "no password kept")})");

        return a;
    }

    /// <summary>The password, from the store; null with the reason when it can't be read.</summary>
    public static string Password(ServerEntry e, SavedAccount a, out string why)
    {
        why = null;

        if (!a.HasPassword)
        {
            why = "no password is saved for it";
            return null;
        }

        return SecretStore.Current.Unprotect(Binding(ServerBook.Holding(e) ?? e, a.Name), a.Secret, out why);
    }

    /// <summary>Marks it the last used (it comes first next time).</summary>
    public static void Touch(ServerEntry e, SavedAccount a)
    {
        a.LastUsed = DateTime.UtcNow;
        ServerBook.Save();
    }

    public static void Forget(ServerEntry e, SavedAccount a)
    {
        ServerEntry kept = ServerBook.Holding(e);

        if (kept?.Accounts == null || !kept.Accounts.Remove(a))
        {
            return;
        }

        SecretStore.Current.Forget(Binding(kept, a.Name), a.Secret);

        if (kept.Accounts.Count == 0)
        {
            kept.Accounts = null;
        }

        ServerBook.Save();
        GD.Print($"[GUO] accounts: forgot an account for \"{kept.Name}\"");
    }

    /// <summary>Every account of every server.</summary>
    public static void ForgetAll()
    {
        foreach (ServerEntry e in ServerBook.WithAccounts.ToList())
        {
            foreach (SavedAccount a in e.Accounts.ToList())
            {
                Forget(e, a);
            }
        }
    }
}
