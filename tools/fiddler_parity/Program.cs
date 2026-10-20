// SPDX-License-Identifier: BSD-2-Clause
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;

// Invoked only by run.py after its bounded container/RLE validation. No full
// installation load, settings lookup, loose-art override, GUI, or write access.
var identity = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .ToDictionary(x => x.Key, x => x.Value);
try
{
    if (string.IsNullOrEmpty(identity["OracleCommit"]) || string.IsNullOrEmpty(identity["OracleSourceHash"]))
        throw new InvalidOperationException("Unidentified build; run launchers/dev/fiddler_parity_doctor.bat --build.");
    if (args.Length == 1 && args[0] == "identity")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString() }));
        return 0;
    }
    if (args.Length != 4) throw new ArgumentException("Expected stage, packaging, client version, comma-separated IDs.");
    var stage = Path.GetFullPath(args[0]);
    if (!ClientVersionHelper.IsClientVersionValid(args[2], out var version))
        throw new ArgumentException("Invalid explicit client version.");
    var map = new UOFilesOverrideMap();
    // IsUOPInstallation checks MainMisc's existence. Only art is loaded; point
    // that probe at the explicitly selected archive, without fabricating files.
    map["mainmisc.uop"] = Path.Combine(stage, args[1] == "uop" ? "artLegacyMUL.uop" : "unused-oracle-probe");
    var files = new UOFileManager(version, stage, map);
    files.Arts.Load();
    using var archive = files.Arts.File;
    if ((archive is UOFileUop) != (args[1] == "uop")) throw new InvalidOperationException("Container selection differs.");
    var rows = new List<object>();
    foreach (var text in args[3].Split(','))
    {
        var id = int.Parse(text);
        ref var entry = ref archive.GetValidRefEntry(id + 0x4000);
        if (entry.Length <= 0) throw new InvalidDataException($"Missing static {id}.");
        archive.Seek(entry.Offset, SeekOrigin.Begin);
        var raw = new byte[entry.Length];
        archive.Read(raw);
        var image = files.Arts.GetArt((uint)(id + 0x4000));
        rows.Add(new { id, width = (int)image.Width, height = (int)image.Height,
            rgba = image.Pixels.ToArray(), raw_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { identity, runtime = Environment.Version.ToString(), records = rows }));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Static-art oracle: {e.Message}");
    return 2;
}
