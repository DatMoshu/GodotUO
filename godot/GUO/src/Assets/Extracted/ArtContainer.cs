// GUO addition, not a port. AX6 / docs/data_formats.md section 36: the encrypted art container (<shard>.guoart).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GUO.Assets.Extracted;

/// <summary>A container that cannot be used. Messages never carry key material.</summary>
internal sealed class ArtContainerException : Exception
{
    public ArtContainerException(string message) : base(message) { }
}

/// <summary>
/// Reads one piece of an encrypted art container at a time: AES-256-GCM, a fresh nonce per chunk, the header and the
/// chunk's name as associated data. Plaintext exists only in the returned array; nothing is written to disk. The file
/// is opened for each read, so nothing stays locked. Tools/art_extract/art_container.py writes it.
/// </summary>
internal sealed class ArtContainer
{
    private static readonly byte[] Magic = { (byte)'G', (byte)'U', (byte)'O', (byte)'A', (byte)'R', (byte)'T', 1, 0 };
    private static readonly byte[] End = { (byte)'G', (byte)'U', (byte)'O', (byte)'E', (byte)'N', (byte)'D', 1, 0 };
    internal const string Toc = "\0toc";
    private const int NonceBytes = 12, TagBytes = 16, KeyBytes = 32, MaxHeader = 1 << 16;

    private readonly string _path;
    private readonly byte[] _key;
    private readonly byte[] _headerAad; // header_len || header, the front of every chunk's associated data
    private readonly long _length;
    private readonly Dictionary<string, (long Offset, int Length)> _entries = new();

    public string ShardId { get; }
    public string SetId { get; }

    private ArtContainer(string path, byte[] key, byte[] headerAad, long length, string shardId, string setId)
    {
        _path = path; _key = key; _headerAad = headerAad; _length = length; ShardId = shardId; SetId = setId;
    }

    /// <summary>A short one-way tag of a key, the same the exporter writes in the header.</summary>
    internal static string KeyId(byte[] key)
    {
        byte[] prefix = Encoding.ASCII.GetBytes("guo/art_key_id\0");
        byte[] all = new byte[prefix.Length + key.Length];
        prefix.CopyTo(all, 0); key.CopyTo(all, prefix.Length);
        return Convert.ToHexString(SHA256.HashData(all)).ToLowerInvariant().Substring(0, 16);
    }

    internal static bool Supported => AesGcm.IsSupported;

    /// <summary>Opens the container for <paramref name="shardId"/> with <paramref name="key"/>; throws ArtContainerException.</summary>
    internal static ArtContainer Open(string path, string shardId, byte[] key)
    {
        if (!AesGcm.IsSupported) throw new ArtContainerException("this platform has no AES-GCM");
        if (key == null || key.Length != KeyBytes) throw new ArtContainerException("the key is not 32 bytes");
        try
        {
            using var h = File.OpenHandle(path);
            long length = RandomAccess.GetLength(h);
            byte[] front = new byte[Magic.Length + 4];
            if (length < front.Length + End.Length + 8 || RandomAccess.Read(h, front, 0) != front.Length || !Same(front, 0, Magic))
                throw new ArtContainerException("not an art container");
            int hlen = BitConverter.ToInt32(front, Magic.Length);
            if (hlen <= 0 || hlen > MaxHeader || front.Length + hlen > length) throw new ArtContainerException("the container header is damaged");
            byte[] header = new byte[hlen];
            if (RandomAccess.Read(h, header, front.Length) != hlen) throw new ArtContainerException("the container header is damaged");
            string schema, cipher, shard, keyId, setId;
            int version;
            try
            {
                using var doc = JsonDocument.Parse(header);
                var r = doc.RootElement;
                schema = r.GetProperty("schema").GetString(); version = r.GetProperty("version").GetInt32();
                cipher = r.GetProperty("cipher").GetString(); shard = r.GetProperty("shard_id").GetString();
                keyId = r.GetProperty("key_id").GetString(); setId = r.GetProperty("set_id").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new ArtContainerException("the container header is damaged");
            }
            if (schema != "guo/art_container@1" || version != 1 || cipher != "aes-256-gcm")
                throw new ArtContainerException("the container is a format version this client does not know");
            if (shard != shardId) throw new ArtContainerException($"the container is for shard {shard}");
            if (!string.Equals(keyId, KeyId(key), StringComparison.Ordinal))
                throw new ArtContainerException("the key for this shard is not the one the container was sealed with");

            byte[] footer = new byte[8 + End.Length];
            if (RandomAccess.Read(h, footer, length - footer.Length) != footer.Length || !Same(footer, 8, End))
                throw new ArtContainerException("the container is cut short");
            long tocAt = BitConverter.ToInt64(footer, 0);

            byte[] aad = new byte[4 + hlen];
            BitConverter.GetBytes(hlen).CopyTo(aad, 0); header.CopyTo(aad, 4);
            var box = new ArtContainer(path, (byte[])key.Clone(), aad, length, shard, setId);
            byte[] toc = box.ReadChunk(h, tocAt, Toc);
            try
            {
                using var doc = JsonDocument.Parse(toc);
                foreach (var e in doc.RootElement.GetProperty("entries").EnumerateObject())
                {
                    long off = e.Value[0].GetInt64(); int len = e.Value[1].GetInt32();
                    if (off < 0 || len < TagBytes || off >= length) throw new ArtContainerException("the container's table of contents is damaged");
                    box._entries[e.Name] = (off, len);
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
            {
                throw new ArtContainerException("the container's table of contents is damaged");
            }
            return box;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtContainerException(ex.Message);
        }
    }

    public bool Has(string name) => _entries.ContainsKey(name);

    /// <summary>The plaintext of one named piece; throws ArtContainerException when absent or when its tag fails.</summary>
    public byte[] Read(string name)
    {
        if (!_entries.TryGetValue(name, out var e)) throw new ArtContainerException($"{name} is not in the container");
        try
        {
            using var h = File.OpenHandle(_path);
            return ReadChunk(h, e.Offset, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtContainerException(ex.Message);
        }
    }

    private byte[] ReadChunk(Microsoft.Win32.SafeHandles.SafeFileHandle h, long offset, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] head = new byte[2 + nameBytes.Length + NonceBytes + 4];
        if (offset < 0 || offset + head.Length > _length || RandomAccess.Read(h, head, offset) != head.Length)
            throw new ArtContainerException("the container is damaged");
        if (BitConverter.ToUInt16(head, 0) != nameBytes.Length || !Same(head, 2, nameBytes))
            throw new ArtContainerException("the container is damaged");
        int sealedLength = BitConverter.ToInt32(head, head.Length - 4);
        if (sealedLength < TagBytes || offset + head.Length + sealedLength > _length)
            throw new ArtContainerException("the container is damaged");
        byte[] sealedBytes = new byte[sealedLength];
        if (RandomAccess.Read(h, sealedBytes, offset + head.Length) != sealedLength) throw new ArtContainerException("the container is damaged");
        byte[] aad = new byte[_headerAad.Length + nameBytes.Length];
        _headerAad.CopyTo(aad, 0); nameBytes.CopyTo(aad, _headerAad.Length);
        byte[] plain = new byte[sealedLength - TagBytes];
        try
        {
            using var gcm = new AesGcm(_key, TagBytes);
            gcm.Decrypt(head.AsSpan(2 + nameBytes.Length, NonceBytes), sealedBytes.AsSpan(0, plain.Length),
                sealedBytes.AsSpan(plain.Length, TagBytes), plain, aad);
        }
        catch (CryptographicException)
        {
            throw new ArtContainerException($"{(name == Toc ? "the table of contents" : name)} failed its integrity check (wrong key, or the file was changed)");
        }
        return plain;
    }

    private static bool Same(byte[] a, int at, byte[] b)
    {
        if (at + b.Length > a.Length) return false;
        for (int i = 0; i < b.Length; i++) if (a[at + i] != b[i]) return false;
        return true;
    }

    /// <summary>The key a player's profile holds for a shard: 64 hex characters in art_keys/&lt;shard&gt;.key, or null.</summary>
    internal static byte[] LoadKey(string keyDir, string shardId)
    {
        string path = Path.Combine(keyDir, shardId + ".key");
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            if (text.Length != KeyBytes * 2) return null;
            return Convert.FromHexString(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }
}
