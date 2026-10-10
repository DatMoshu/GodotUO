// The Admin tab's Accounts (AD5): which names, passwords and access levels the
// bridge takes, and which accounts a connection may change. AccountsAdmin.cs
// does it to the server's accounts; this file only decides.
//
// The levels follow ModernUO's own admin gump: a connection sets an account to
// a level below its own (an Owner to any), and changes nothing on an account
// at its own level or above. Names and passwords fit the login gump, whose two
// boxes hold 16 characters each: an account the tab makes can be typed in.
//
// Plain .NET, no ModernUO types: tests/ compiles this file on its own.

using System;

namespace GUO.EditorBridge;

public static class AccountRules
{
    /// <summary>The login gump's account and password boxes hold this many characters (LoginGump.cs, as upstream).</summary>
    public const int MaxLength = 16;

    /// <summary>The shortest password the tab sets.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>The most accounts one admin_accounts reply lists.</summary>
    public const int MaxListed = 5000;

    /// <summary>admin_account's actions.</summary>
    public static readonly string[] Actions = { "create", "access", "password", "ban", "unban" };

    // ModernUO's AccountHandler.ForbiddenChars.
    private const string ForbiddenChars = "<>:\"/\\|?*";

    /// <summary>Refusal text if <paramref name="name"/> cannot be an account name, or null.</summary>
    public static string CheckName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "an account needs a name";
        }

        if (name.Length > MaxLength)
        {
            return $"an account name has at most {MaxLength} characters (the login screen's box)";
        }

        // ModernUO's AccountHandler.IsValidUsername.
        if (name.StartsWith(' ') || name.EndsWith(' ') || name.EndsWith('.'))
        {
            return "an account name may not start or end with a space, or end with a dot";
        }

        if (name.IndexOfAny(ForbiddenChars.ToCharArray()) >= 0)
        {
            return $"an account name may not hold any of {ForbiddenChars}";
        }

        foreach (char c in name)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return "an account name holds letters, digits, spaces and plain punctuation only";
            }
        }

        return null;
    }

    /// <summary>Refusal text if <paramref name="password"/> may not be the password of <paramref name="name"/>, or null.</summary>
    public static string CheckPassword(string name, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "a password is needed";
        }

        if (password.Length < MinPasswordLength || password.Length > MaxLength)
        {
            return $"a password has {MinPasswordLength} to {MaxLength} characters (the login screen's box holds {MaxLength})";
        }

        foreach (char c in password)
        {
            if (c <= 0x20 || c > 0x7E)
            {
                return "a password holds letters, digits and plain punctuation only, no spaces";
            }
        }

        // Passwords anyone can read in GUO's history (the 0001 patch refuses the same ones for staff).
        if (string.Equals(password, name, StringComparison.OrdinalIgnoreCase) || string.Equals(password, "guoprobe", StringComparison.OrdinalIgnoreCase))
        {
            return "that password is published (the account's own name or an old default); pick another";
        }

        return null;
    }

    /// <summary>
    /// Refusal text if a connection holding <paramref name="held"/> may not change an account now at
    /// <paramref name="current"/>, or null. An Owner may change any; anyone else only accounts below their own level.
    /// </summary>
    public static string CheckTarget(AdminLevel held, AdminLevel current)
    {
        return held == AdminLevel.Owner || current < held
            ? null
            : $"that account is {current}; this connection ({held}) changes only accounts below its own level";
    }

    /// <summary>
    /// Refusal text if a connection holding <paramref name="held"/> may not set an account to <paramref name="wanted"/>,
    /// or null. As ModernUO's admin gump: a level below the connection's own, or any for an Owner.
    /// </summary>
    public static string CheckLevel(AdminLevel held, AdminLevel wanted)
    {
        return held == AdminLevel.Owner || wanted < held
            ? null
            : $"this connection ({held}) can give an account a level below its own only, not {wanted}";
    }

    /// <summary>The level named, by ModernUO's names (any case), or null.</summary>
    public static AdminLevel? ParseLevel(string name) =>
        !string.IsNullOrEmpty(name) && !int.TryParse(name, out _) && Enum.TryParse(name, true, out AdminLevel l) && Enum.IsDefined(l) ? l : null;
}
