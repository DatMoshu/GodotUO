// GUO addition, not a port: upstream ClassicUO keeps one password in
// settings.json behind Crypter, an XOR mask. On Android GUO's accounts
// manager keeps passwords encrypted by a key in the Android Keystore.

using System;
using System.Text;
using Godot;

namespace GUO.Input.Touch.Pregame.Accounts;

/// <summary>
/// Android: AES-256-GCM under a key the Android Keystore makes and keeps
/// (alias <see cref="Alias"/>, not exportable), made on the first Protect.
/// servers.json keeps the IV and the ciphertext, base64. The entry's
/// host:port:name is the associated data, so a blob moved onto another entry
/// fails its tag, as DPAPI's entropy does on Windows. Reached through
/// JavaClassWrapper and the AndroidRuntime singleton (as SafFolder is): no
/// export plugin. The key stays on this device and this install; the
/// ciphertext protects the file off it, not from code running as the app.
/// </summary>
internal sealed class AndroidKeystoreStore : ISecretStore
{
    public const string Alias = "guo.accounts.v1";
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int EncryptMode = 1;
    private const int DecryptMode = 2;
    private const int PurposeEncryptDecrypt = 1 | 2;
    private const int TagBits = 128;

    private readonly string _unavailable;

    public AndroidKeystoreStore()
    {
        if (!OperatingSystem.IsAndroid() || !Engine.HasSingleton("AndroidRuntime"))
        {
            _unavailable = "the Android Keystore isn't reachable";
        }
    }

    /// <summary>Why the store can't be used, or null when it can.</summary>
    public string Unavailable => _unavailable;

    public string Kind => SecretStore.AndroidKeystore;
    public bool Available => _unavailable == null;

    public Secret Protect(string binding, string password, out string why)
    {
        if (!Available)
        {
            why = _unavailable;
            return null;
        }

        byte[] plain = Encoding.UTF8.GetBytes(password ?? "");

        try
        {
            GodotObject key = Key(true, out why);
            GodotObject cipher = key == null ? null : Cipher(EncryptMode, key, null, binding, out why);

            if (cipher == null)
            {
                return null;
            }

            byte[] blob = Java(cipher, "doFinal", out why, plain).AsByteArray();
            byte[] iv = Java(cipher, "getIV", out string ivWhy).AsByteArray();

            if (why != null || ivWhy != null || blob == null || blob.Length == 0 || iv == null || iv.Length == 0)
            {
                why = why ?? ivWhy ?? "the Android Keystore gave nothing back";
                return null;
            }

            return new Secret { Store = Kind, Iv = Convert.ToBase64String(iv), Blob = Convert.ToBase64String(blob) };
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public string Unprotect(string binding, Secret secret, out string why)
    {
        if (!Available)
        {
            why = _unavailable;
            return null;
        }

        if (secret?.Store != Kind || string.IsNullOrEmpty(secret.Blob) || string.IsNullOrEmpty(secret.Iv))
        {
            why = "it was saved on another system";
            return null;
        }

        byte[] blob, iv;

        try
        {
            blob = Convert.FromBase64String(secret.Blob);
            iv = Convert.FromBase64String(secret.Iv);
        }
        catch (FormatException)
        {
            why = "the saved copy is damaged";
            return null;
        }

        GodotObject key = Key(false, out why);

        if (key == null)
        {
            why ??= "this device's key for it is gone (the app was reinstalled or its data cleared)";
            return null;
        }

        GodotObject cipher = Cipher(DecryptMode, key, iv, binding, out why);

        if (cipher == null)
        {
            return null;
        }

        byte[] plain = Java(cipher, "doFinal", out why, blob).AsByteArray();

        if (why != null || plain == null)
        {
            // A failed tag: another entry's copy, or a damaged one.
            why = "it belongs to another entry, or the saved copy is damaged";
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
        // The ciphertext is in servers.json; the one key serves every entry.
    }

    /// <summary>The key, made on first use when <paramref name="create"/>.</summary>
    private static GodotObject Key(bool create, out string why)
    {
        GodotObject store = Java(JavaClassWrapper.Wrap("java.security.KeyStore"), "getInstance", out why, Provider).AsGodotObject();

        if (store == null)
        {
            why ??= "no Android Keystore";
            return null;
        }

        Java(store, "load", out why, default(Variant));

        if (why != null)
        {
            return null;
        }

        if (Java(store, "containsAlias", out why, Alias).AsBool())
        {
            GodotObject entry = Java(store, "getEntry", out why, Alias, default(Variant)).AsGodotObject();
            GodotObject existing = entry == null ? null : Java(entry, "getSecretKey", out why).AsGodotObject();

            if (existing == null)
            {
                why ??= "the key couldn't be read";
            }

            return existing;
        }

        if (!create)
        {
            why = null;
            return null;
        }

        JavaClass builderClass = JavaClassWrapper.Wrap("android.security.keystore.KeyGenParameterSpec$Builder");
        GodotObject builder = Java(builderClass, "KeyGenParameterSpec$Builder", out why, Alias, PurposeEncryptDecrypt).AsGodotObject();

        if (builder == null)
        {
            // How JavaClassWrapper names an inner class's constructor varies.
            builder = Java(builderClass, "Builder", out why, Alias, PurposeEncryptDecrypt).AsGodotObject();
        }

        if (builder == null)
        {
            why ??= "the key's settings couldn't be made";
            return null;
        }

        Java(builder, "setBlockModes", out why, new[] { "GCM" });
        Java(builder, "setEncryptionPaddings", out string paddingWhy, new[] { "NoPadding" });
        Java(builder, "setKeySize", out string sizeWhy, 256);
        GodotObject spec = why == null && paddingWhy == null && sizeWhy == null ? Java(builder, "build", out why).AsGodotObject() : null;

        if (spec == null)
        {
            why = why ?? paddingWhy ?? sizeWhy ?? "the key's settings couldn't be made";
            return null;
        }

        GodotObject generator = Java(JavaClassWrapper.Wrap("javax.crypto.KeyGenerator"), "getInstance", out why, "AES", Provider).AsGodotObject();

        if (generator == null)
        {
            why ??= "no AES key generator";
            return null;
        }

        Java(generator, "init", out why, spec);
        GodotObject key = why == null ? Java(generator, "generateKey", out why).AsGodotObject() : null;

        if (key == null)
        {
            why ??= "the key couldn't be made";
            return null;
        }

        GD.Print("[GUO] accounts: made this device's key for saved passwords (Android Keystore)");

        return key;
    }

    /// <summary>A GCM cipher set up for the mode, with the entry's binding as associated data.</summary>
    private static GodotObject Cipher(int mode, GodotObject key, byte[] iv, string binding, out string why)
    {
        GodotObject cipher = Java(JavaClassWrapper.Wrap("javax.crypto.Cipher"), "getInstance", out why, Transformation).AsGodotObject();

        if (cipher == null)
        {
            why ??= "no AES-GCM";
            return null;
        }

        if (iv == null)
        {
            Java(cipher, "init", out why, mode, key);
        }
        else
        {
            GodotObject spec = Java(JavaClassWrapper.Wrap("javax.crypto.spec.GCMParameterSpec"), "GCMParameterSpec", out why, TagBits, iv).AsGodotObject();

            if (spec == null)
            {
                why ??= "the saved IV couldn't be used";
                return null;
            }

            Java(cipher, "init", out why, mode, key, spec);
        }

        if (why != null)
        {
            return null;
        }

        byte[] aad = Encoding.UTF8.GetBytes(binding ?? "");
        Java(cipher, "updateAAD", out why, aad);

        return why == null ? cipher : null;
    }

    /// <summary>
    /// One call into Java; <paramref name="why"/> is the exception's class
    /// when it threw (never its message, which could carry data), else null.
    /// </summary>
    private static Variant Java(GodotObject target, string method, out string why, params Variant[] args)
    {
        why = null;

        if (target == null)
        {
            why = $"{method}: nothing to call it on";
            return default;
        }

        Variant result = target.Call(method, args);
        GodotObject thrown = JavaClassWrapper.GetException();

        if (thrown != null)
        {
            string type = thrown.Call("getClass").AsGodotObject()?.Call("getName").AsString();
            why = $"{method}: {(string.IsNullOrEmpty(type) ? "it failed" : type)}";

            return default;
        }

        return result;
    }
}
