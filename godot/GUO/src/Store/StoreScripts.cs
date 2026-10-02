// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GUO.Game.Scripting;

namespace GUO.Store;

internal sealed record StoreScript(StoreManifest Pack, string Path)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Category => $"Store: {Pack.Title} v{Pack.Version}";
}

/// <summary>Offline discovery and verified personal-copy import. Never executes scripts.</summary>
internal static class StoreScripts
{
    public static IReadOnlyList<StoreScript> List(StoreClient client) => client.Installed()
        .Where(m => m.Kind == "razor-script")
        .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(m => StorePack.Version(m.Version))
        .SelectMany(m => m.Files.Keys.Where(StorePack.IsScript).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new StoreScript(m, p))).ToArray();

    public static string Read(StoreClient client, StoreScript script)
    {
        StorePack.Require(script.Pack.Kind == "razor-script" && StorePack.IsScript(script.Path), "Select a declared Razor script");
        return StorePack.ScriptText(client.ReadVerifiedPayload(script.Pack, script.Path, StorePack.MaxScriptBytes));
    }

    public static string Import(StoreClient client, StoreScript script, ScriptLibrary library)
    {
        string source = Read(client, script);
        string licence = "";
        if (script.Pack.Files.ContainsKey("LICENSE.txt"))
        {
            byte[] bytes = client.ReadVerifiedPayload(script.Pack, "LICENSE.txt", StorePack.MaxManifest);
            try { licence = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Pack LICENSE.txt is not valid UTF-8"); }
        }
        string attribution = $"Pack: {script.Pack.Title}\nID: {script.Pack.Id}\nVersion: {script.Pack.Version}\n" +
            $"Author: {script.Pack.Author}\nLicence: {script.Pack.Licence}\nSource: {script.Path}\n" +
            $"Original SHA-256: {script.Pack.Files[script.Path]}\n\n{licence}";
        string name = script.Pack.Id + "-" + script.Title;
        name = new string(name.Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' ? c : '-').ToArray());
        return library.AddCopy(name.Substring(0, Math.Min(name.Length, 64)), source, attribution);
    }
}
