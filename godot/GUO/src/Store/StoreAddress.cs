// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;

namespace GUO.Store;

/// <summary>Per-profile sidecar; deliberately independent of profile migrations.</summary>
internal static class StoreAddress
{
    public const string Default = "http://127.0.0.1:18865";
    public static string Normalize(string value)
    {
        value = value?.Trim();
        StorePack.Require(!string.IsNullOrEmpty(value) && value.Length <= 2048 &&
            !value.Contains('\\') && !System.Linq.Enumerable.Any(value, char.IsControl) &&
            Uri.TryCreate(value, UriKind.Absolute, out var unused), "Enter an HTTP or HTTPS store address with a host and optional port.");
        var uri = new Uri(value, UriKind.Absolute);
        StorePack.Require(uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) &&
            uri.Port > 0 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment), "Store address must use HTTP or HTTPS, with no credentials, query or fragment.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public static string Load(string profileDirectory, string fallback)
    {
        string path = string.IsNullOrEmpty(profileDirectory) ? null : Path.Combine(profileDirectory, "store-address.txt");
        return path != null && File.Exists(path) ? File.ReadAllText(path).Trim() : fallback;
    }

    public static void Save(string profileDirectory, string value)
    {
        StorePack.Require(!string.IsNullOrEmpty(profileDirectory), "Load a character profile before saving a store address.");
        string normalized = Normalize(value);
        Directory.CreateDirectory(profileDirectory);
        string path = Path.Combine(profileDirectory, "store-address.txt");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, normalized + "\n"); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
