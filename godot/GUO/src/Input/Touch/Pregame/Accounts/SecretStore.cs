// GUO addition, not a port: upstream ClassicUO keeps one password in
// settings.json behind Crypter, an XOR mask. GUO's accounts manager keeps
// passwords in the operating system's own keystore instead
// (docs/ui/second_screen_pregame.md, Accounts).

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GUO.Input.Touch.Pregame.Accounts;

/// <summary>
/// What servers.json keeps of a password: which store has it, and for a store
/// that hands back ciphertext, that ciphertext. Never the password.
/// </summary>
internal sealed class Secret
{
    [System.Text.Json.Serialization.JsonPropertyName("store")] public string Store { get; set; } = SecretStore.None;
    [System.Text.Json.Serialization.JsonPropertyName("iv")] public string Iv { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("blob")] public string Blob { get; set; }
}

/// <summary>A keystore of the operating system. One per platform, chosen at start.</summary>
internal interface ISecretStore
{
    /// <summary>The name servers.json records (<see cref="SecretStore.Dpapi"/> and so on).</summary>
    string Kind { get; }

    /// <summary>Whether a password can be kept here at all.</summary>
    bool Available { get; }

    /// <summary>Keeps <paramref name="password"/> for <paramref name="binding"/> (host:port:name); null with the reason when it can't.</summary>
    Secret Protect(string binding, string password, out string why);

    /// <summary>The password back; null with the reason when it can't be read.</summary>
    string Unprotect(string binding, Secret secret, out string why);

    /// <summary>Forgets it where the store holds it itself (a keyring); a no-op for ciphertext in the file.</summary>
    void Forget(string binding, Secret secret);
}

internal static class SecretStore
{
    public const string Dpapi = "dpapi";
    public const string AndroidKeystore = "android-keystore";
    public const string Libsecret = "libsecret";
    public const string None = "none";

    private static ISecretStore _current;

    /// <summary>This platform's store; one that keeps nothing where there is none.</summary>
    public static ISecretStore Current => _current ??= Pick();

    /// <summary>For the probe: another store, or null for this platform's.</summary>
    public static void SetForProbe(ISecretStore store) => _current = store;

    private static ISecretStore Pick()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiStore();
        }

        if (OperatingSystem.IsAndroid())
        {
            var android = new AndroidKeystoreStore();

            return android.Available ? android : new NoStore($"Passwords aren't saved here: {android.Unavailable}. You'll type it at login.");
        }

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            var linux = new LibsecretStore();

            return linux.Available ? linux : new NoStore($"Passwords aren't saved here: {linux.Unavailable}. You'll type it at login.");
        }

        return new NoStore(OperatingSystem.IsBrowser()
            ? "Passwords are never saved in the browser."
            : "Passwords aren't saved on this system. You'll type it at login.");
    }
}

/// <summary>Web, or a system without a keystore: nothing is kept, and the UI says why.</summary>
internal sealed class NoStore : ISecretStore
{
    public NoStore(string why) => Why = why;

    public string Why { get; }
    public string Kind => SecretStore.None;
    public bool Available => false;

    public Secret Protect(string binding, string password, out string why)
    {
        why = Why;
        return null;
    }

    public string Unprotect(string binding, Secret secret, out string why)
    {
        why = Why;
        return null;
    }

    public void Forget(string binding, Secret secret)
    {
    }
}

/// <summary>
/// Windows: DPAPI, CurrentUser scope. The entropy is GUO's own plus the
/// entry's host:port:name, so a blob moved onto another entry doesn't decrypt.
/// It protects the file off this Windows account; any process running as the
/// same user can still decrypt it.
/// </summary>
internal sealed class DpapiStore : ISecretStore
{
    private const string Entropy = "GUO accounts v1\n";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public string Kind => SecretStore.Dpapi;
    public bool Available => OperatingSystem.IsWindows();

    public Secret Protect(string binding, string password, out string why)
    {
        byte[] plain = Encoding.UTF8.GetBytes(password ?? "");

        try
        {
            byte[] blob = Call(true, plain, binding, out why);
            return blob == null ? null : new Secret { Store = Kind, Blob = Convert.ToBase64String(blob) };
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public string Unprotect(string binding, Secret secret, out string why)
    {
        why = null;

        if (secret?.Store != Kind || string.IsNullOrEmpty(secret.Blob))
        {
            why = "it was saved on another system";
            return null;
        }

        byte[] blob;

        try
        {
            blob = Convert.FromBase64String(secret.Blob);
        }
        catch (FormatException)
        {
            why = "the saved copy is damaged";
            return null;
        }

        byte[] plain = Call(false, blob, binding, out why);

        if (plain == null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public void Forget(string binding, Secret secret)
    {
    }

    private static byte[] Call(bool protect, byte[] data, string binding, out string why)
    {
        why = null;
        byte[] entropy = Encoding.UTF8.GetBytes(Entropy + binding);
        GCHandle dataPin = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new DataBlob();

        try
        {
            var input = new DataBlob { cbData = data.Length, pbData = dataPin.AddrOfPinnedObject() };
            var extra = new DataBlob { cbData = entropy.Length, pbData = entropyPin.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "GUO account", ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                why = protect ? $"Windows couldn't encrypt it (error {error})" : "Windows couldn't decrypt it (another Windows account, or it belongs to another entry)";
                return null;
            }

            byte[] result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);

            if (!protect)
            {
                // The plaintext's own copy, cleared before it is freed.
                Marshal.Copy(new byte[output.cbData], 0, output.pbData, output.cbData);
            }

            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = "DPAPI isn't available";
            return null;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }

            dataPin.Free();
            entropyPin.Free();
            Array.Clear(entropy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy,
                                                IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
                                                  IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
