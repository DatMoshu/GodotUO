// GUO addition, not a port: upstream ClassicUO keeps one password in
// settings.json behind Crypter, an XOR mask. On Linux and the Steam Deck
// GUO's accounts manager keeps passwords in the Secret Service keyring.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GUO.Input.Touch.Pregame.Accounts;

/// <summary>
/// Linux and the Steam Deck: the password itself goes into the Secret
/// Service keyring (GNOME Keyring, KWallet) through libsecret, under the
/// schema <see cref="SchemaName"/> with one attribute, the entry's
/// host:port:name. servers.json only records that the keyring has it, so a
/// copy moved onto another entry finds nothing. Reached by P/Invoke of
/// libsecret's non-variadic calls (the <c>…v_sync</c> ones: P/Invoke can't
/// pass C varargs) with a GHashTable of the attributes. No library, or no
/// keyring running, and the store isn't available: there's no plaintext
/// fallback. An unlocked keyring hands the password to any process of this
/// user, as DPAPI does on Windows.
/// </summary>
internal sealed class LibsecretStore : ISecretStore
{
    public const string SchemaName = "org.guo.Account";
    private const string Attribute = "binding";
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";

    private static IntPtr _schema;
    private static IntPtr _strHash, _strEqual;

    private readonly string _unavailable;

    public LibsecretStore()
    {
        if (!OperatingSystem.IsLinux())
        {
            _unavailable = "there's no keyring here";
            return;
        }

        if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(LibGlib, out IntPtr glib) ||
            !NativeLibrary.TryGetExport(glib, "g_str_hash", out _strHash) || !NativeLibrary.TryGetExport(glib, "g_str_equal", out _strEqual))
        {
            _unavailable = "libsecret isn't installed";
            return;
        }

        try
        {
            // A lookup of nothing: it only fails when no keyring answers.
            _ = Lookup("guo:probe:" + Guid.NewGuid().ToString("N"), out string why);

            if (why != null)
            {
                _unavailable = why.Contains("dbus", StringComparison.OrdinalIgnoreCase) ? "no keyring is running" : $"the keyring refused it ({why})";
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = "libsecret isn't installed";
        }
    }

    /// <summary>Why the store can't be used, or null when it can.</summary>
    public string Unavailable => _unavailable;

    public string Kind => SecretStore.Libsecret;
    public bool Available => _unavailable == null;

    public Secret Protect(string binding, string password, out string why)
    {
        if (!Available)
        {
            why = _unavailable;
            return null;
        }

        byte[] plain = Utf8Z(password ?? "");
        GCHandle pin = GCHandle.Alloc(plain, GCHandleType.Pinned);

        try
        {
            bool ok = WithAttributes(binding, attributes =>
            {
                IntPtr error = IntPtr.Zero;
                bool stored = 0 != secret_password_storev_sync(Schema(), attributes, IntPtr.Zero, "GUO account " + binding, pin.AddrOfPinnedObject(), IntPtr.Zero, ref error);
                return Done(error, stored);
            }, out why);

            return ok ? new Secret { Store = Kind } : null;
        }
        finally
        {
            Array.Clear(plain);
            pin.Free();
        }
    }

    public string Unprotect(string binding, Secret secret, out string why)
    {
        if (!Available)
        {
            why = _unavailable;
            return null;
        }

        if (secret?.Store != Kind)
        {
            why = "it was saved on another system";
            return null;
        }

        string password = Lookup(binding, out why);

        if (password == null && why == null)
        {
            why = "the keyring has no password for it (it belongs to another entry, or the keyring was cleared)";
        }

        return password;
    }

    public void Forget(string binding, Secret secret)
    {
        if (!Available || secret?.Store != Kind)
        {
            return;
        }

        WithAttributes(binding, attributes =>
        {
            IntPtr error = IntPtr.Zero;
            // False when there was nothing to clear, which is fine.
            secret_password_clearv_sync(Schema(), attributes, IntPtr.Zero, ref error);
            return Done(error, true);
        }, out _);
    }

    /// <summary>The password kept for <paramref name="binding"/>; null with no reason when there is none.</summary>
    private static string Lookup(string binding, out string why)
    {
        string found = null;

        WithAttributes(binding, attributes =>
        {
            IntPtr error = IntPtr.Zero;
            IntPtr p = secret_password_lookupv_sync(Schema(), attributes, IntPtr.Zero, ref error);

            if (!Done(error, true))
            {
                return false;
            }

            if (p != IntPtr.Zero)
            {
                int n = 0;

                while (Marshal.ReadByte(p, n) != 0)
                {
                    n++;
                }

                byte[] plain = new byte[n];
                Marshal.Copy(p, plain, 0, n);
                found = Encoding.UTF8.GetString(plain);
                Array.Clear(plain);

                // Wipes the keyring's copy before freeing it.
                secret_password_free(p);
            }

            return true;
        }, out why);

        return found;
    }

    /// <summary>
    /// Runs <paramref name="call"/> with a GHashTable of the one attribute;
    /// <paramref name="why"/> is the GError's domain and code when it failed
    /// (never its message, which could carry data).
    /// </summary>
    private static bool WithAttributes(string binding, Func<IntPtr, bool> call, out string why)
    {
        IntPtr key = Marshal.StringToCoTaskMemUTF8(Attribute);
        IntPtr value = Marshal.StringToCoTaskMemUTF8(binding ?? "");
        IntPtr table = g_hash_table_new(_strHash, _strEqual);
        _why = null;

        try
        {
            g_hash_table_insert(table, key, value);
            bool ok = call(table);
            why = ok ? null : _why ?? "the keyring refused it";
            return ok;
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(value);
        }
    }

    [ThreadStatic] private static string _why;

    /// <summary>Whether a call went through; its GError's domain and code into <see cref="_why"/> when not.</summary>
    private static bool Done(IntPtr error, bool ok)
    {
        if (error == IntPtr.Zero)
        {
            return ok;
        }

        uint domain = (uint) Marshal.ReadInt32(error);
        int code = Marshal.ReadInt32(error, 4);
        _why = $"{Marshal.PtrToStringUTF8(g_quark_to_string(domain))} {code}";
        g_error_free(error);

        return false;
    }

    /// <summary>The schema, made once: one string attribute, the entry's host:port:name.</summary>
    private static IntPtr Schema()
    {
        if (_schema != IntPtr.Zero)
        {
            return _schema;
        }

        IntPtr key = Marshal.StringToCoTaskMemUTF8(Attribute);
        IntPtr types = g_hash_table_new(_strHash, _strEqual);

        try
        {
            // SECRET_SCHEMA_ATTRIBUTE_STRING is 0, stored as the value pointer.
            g_hash_table_insert(types, key, IntPtr.Zero);
            _schema = secret_schema_newv(SchemaName, 0, types);
        }
        finally
        {
            g_hash_table_unref(types);
            Marshal.FreeCoTaskMem(key);
        }

        return _schema;
    }

    private static byte[] Utf8Z(string s)
    {
        byte[] z = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, z, 0);
        return z;
    }

    // gboolean is a C int: the calls that return one are declared int.
    [DllImport(LibSecret)]
    private static extern IntPtr secret_schema_newv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, IntPtr attributeNamesAndTypes);

    [DllImport(LibSecret)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
                                                           [MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr password,
                                                           IntPtr cancellable, ref IntPtr error);

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

    [DllImport(LibSecret)]
    private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(LibGlib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

    [DllImport(LibGlib)]
    private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGlib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGlib)]
    private static extern IntPtr g_quark_to_string(uint quark);

    [DllImport(LibGlib)]
    private static extern void g_error_free(IntPtr error);
}
