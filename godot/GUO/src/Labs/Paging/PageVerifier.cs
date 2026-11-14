// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS || GUO_PAGING_TESTS
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace GUO.Labs.Paging;

/// <summary>
/// Checks one page's files against its manifest entry: exact MUL sizes for the
/// declared dimensions, static index entries inside the statics file, and the
/// three SHA-256 hashes. Reads the files; never writes them.
/// </summary>
internal static class PageVerifier
{
    /// <summary>
    /// Size and modification time of the three files. A verified result is
    /// reused only while this is unchanged; a replaced or truncated file is
    /// verified again.
    /// </summary>
    internal static string Fingerprint(PageEntry page)
    {
        return $"{Stamp(page.MapPath)};{Stamp(page.StaidxPath)};{Stamp(page.StaticsPath)}";

        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}@{info.LastWriteTimeUtc.Ticks}" : "missing";
        }
    }

    /// <summary>Returns null when the page is usable, else a plain reason.</summary>
    internal static string Verify(PageEntry page)
    {
        string where = $"page '{page.Id}' (facet {page.Facet})";
        long blocks = page.BlockCount;

        foreach (var (path, label) in new[] { (page.MapPath, "map"), (page.StaidxPath, "staidx"), (page.StaticsPath, "statics") })
        {
            if (!File.Exists(path)) return $"{where}: {label} file is missing: {path}";
        }

        long mapLength = new FileInfo(page.MapPath).Length;
        if (mapLength != blocks * PageCatalog.MapBlockBytes)
            return $"{where}: map file is {mapLength} bytes; {page.Width}x{page.Height} needs {blocks * PageCatalog.MapBlockBytes}.";

        long idxLength = new FileInfo(page.StaidxPath).Length;
        if (idxLength != blocks * PageCatalog.StaidxEntryBytes)
            return $"{where}: staidx file is {idxLength} bytes; {page.Width}x{page.Height} needs {blocks * PageCatalog.StaidxEntryBytes}.";

        long staticsLength = new FileInfo(page.StaticsPath).Length;
        if (staticsLength % PageCatalog.StaticEntryBytes != 0)
            return $"{where}: statics file is {staticsLength} bytes, not a whole number of {PageCatalog.StaticEntryBytes}-byte records.";

        string idxProblem = CheckIndex(page.StaidxPath, blocks, staticsLength, where, out string idxHash);
        if (idxProblem != null) return idxProblem;

        if (!Same(HashFile(page.MapPath), page.MapSha256)) return $"{where}: map file hash does not match the manifest.";
        if (!Same(idxHash, page.StaidxSha256)) return $"{where}: staidx file hash does not match the manifest.";
        if (!Same(HashFile(page.StaticsPath), page.StaticsSha256)) return $"{where}: statics file hash does not match the manifest.";
        return null;
    }

    private static string CheckIndex(string path, long blocks, long staticsLength, string where, out string hash)
    {
        hash = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[PageCatalog.StaidxEntryBytes * 4096];
        long block = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            if (read % PageCatalog.StaidxEntryBytes != 0) return $"{where}: staidx file changed while it was read.";
            for (int o = 0; o < read; o += PageCatalog.StaidxEntryBytes, block++)
            {
                uint position = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(o));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(o + 4));
                // The two forms MapLoader treats as an empty block.
                if (position == 0xFFFF_FFFF || size == 0) continue;
                if (size % PageCatalog.StaticEntryBytes != 0 || (long)position + size > staticsLength)
                    return $"{where}: staidx block {block} points outside the statics file.";
            }
        }

        if (block != blocks) return $"{where}: staidx has {block} entries; expected {blocks}.";
        hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        return null;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool Same(string actual, string expected) => string.Equals(actual, expected, StringComparison.Ordinal);
}
#endif
