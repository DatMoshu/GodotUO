// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GUO.Store;

/// <summary>Immutable manifest snapshot; bounded payload reads recheck the snapshot hash.</summary>
internal sealed class StoreVerifiedPack
{
    private readonly byte[] _manifest;
    private readonly string _directory;
    private readonly Dictionary<string, string> _files;
    public string Id { get; }
    public string Version { get; }
    public string IdentityHash { get; }
    public StoreManifest Manifest => StorePack.Parse((byte[])_manifest.Clone());

    internal StoreVerifiedPack(string directory, StoreManifest manifest)
    {
        _directory = directory;
        // Canonicalize object keys recursively, retaining array order.
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(manifest));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Canonical(writer, document.RootElement);
        _manifest = buffer.ToArray();
        using var bytes = new MemoryStream(_manifest);
        IdentityHash = StorePack.Hash(bytes);
        Id = manifest.Id; Version = manifest.Version;
        _files = new(manifest.Files, StringComparer.Ordinal);
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(p.Name); Canonical(writer, p.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Canonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    public byte[] ReadPayload(string name, int maximumBytes = 16 * 1024 * 1024)
    {
        StorePack.Require(maximumBytes > 0 && maximumBytes <= StorePack.MaxFile, "Invalid read limit");
        StorePack.Require(name != null && _files.TryGetValue(name, out _), "Undeclared payload");
        string path = Path.Combine(_directory, name.Replace('/', Path.DirectorySeparatorChar));
        StoreClient.NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        StorePack.Require(stream.Length <= maximumBytes, "Payload exceeds consumer limit");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[65536];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            StorePack.Require(buffer.Length + read <= maximumBytes, "Payload exceeds consumer limit");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        StorePack.Require(StorePack.Hash(buffer) == _files[name], "Payload changed after verification");
        return buffer.ToArray();
    }
}

internal sealed class StoreVerifiedContent
{
    public IReadOnlyDictionary<string, StoreVerifiedPack> Packs { get; }
    public string IdentityHash { get; }

    internal StoreVerifiedContent(Dictionary<string, StoreVerifiedPack> packs)
    {
        Packs = new ReadOnlyDictionary<string, StoreVerifiedPack>(new Dictionary<string, StoreVerifiedPack>(packs, StringComparer.Ordinal));
        string identity = string.Join("\n", packs.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "@" + p.Value.Version + ":" + p.Value.IdentityHash));
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(identity));
        IdentityHash = StorePack.Hash(bytes);
    }
}
