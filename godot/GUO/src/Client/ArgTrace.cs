// GUO addition, not a port: keeps credentials out of the client's argument
// trace. Upstream's Main.cs logs every command-line value, the account name and
// password included; GUO's Main.cs logs them through this instead. Recorded in
// docs/upstream/GUO_DIVERGENCES.md.

namespace GUO
{
    internal static class ArgTrace
    {
        public const string Hidden = "<hidden>";

        // The options in Main.cs's ReadSettingsFromArgs that carry a credential.
        // The name arrives lower-cased and without its leading '-'.
        private static readonly string[] Secret = { "username", "password", "password_enc" };

        public static bool IsSecret(string cmd) => System.Array.IndexOf(Secret, cmd) >= 0;

        // The value to write after "VALUE:" for this option.
        public static string Value(string cmd, string value) => IsSecret(cmd) ? Hidden : value;
    }
}
