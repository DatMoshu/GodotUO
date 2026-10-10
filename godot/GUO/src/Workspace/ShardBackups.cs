// GUO addition, not a port. AD6: the Admin tab's backups of a ModernUO world save. Plain .NET (no Godot or ModernUO
// types): the editor bridge links this file to take a snapshot, the editor to restore one with the server stopped,
// and tools/server_manager/tests compiles it as it stands.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GUO.Workspace;

/// <summary>
/// Snapshots of a server's save folder (ModernUO's <c>Saves</c>), one folder each under
/// <c>&lt;backup folder&gt;/GUO/&lt;name&gt;</c> with a <see cref="Manifest"/> beside the copied files. ModernUO's own
/// backups (<c>Backups/Automatic</c>, rolled into <c>Archives</c>) are left alone; only this folder is pruned.
/// A name is the snapshot's UTC time, <c>2026-10-10_031500Z</c>, with a suffix for one taken before a restore.
/// </summary>
/// <remarks>
/// <see cref="Take"/> runs on the server's game thread right after a save is on disk, so no other save moves the
/// folder while it is copied. <see cref="Restore"/> runs only with the server stopped: the current save is set aside,
/// the snapshot copied in, and the set-aside copy removed once the copy is whole (put back if it is not).
/// Nothing here writes a path into a manifest or a message.
/// </remarks>
internal static class ShardBackups
{
    /// <summary>The folder under the server's backup folder that holds GUO's snapshots.</summary>
    public const string Folder = "GUO";

    /// <summary>The file in each snapshot that says what it is; never restored into the save folder.</summary>
    public const string Manifest = "guo_backup.json";

    public const int DefaultKeep = 10;
    public const int MaxKeep = 100;

    /// <summary>The reason a snapshot taken just before a restore carries (and its name's suffix).</summary>
    public const string BeforeRestore = "before-restore";

    private static readonly Regex NamePattern = new(@"^\d{4}-\d\d-\d\d_\d{6}Z(_before-restore)?(_\d{1,3})?$", RegexOptions.CultureInvariant);

    /// <summary>One snapshot as its manifest describes it.</summary>
    public sealed record Snapshot(string Name, DateTime AtUtc, string Reason, string Editor, int Files, long Bytes)
    {
        public JsonObject ToJson() => new()
        {
            ["name"] = Name, ["at"] = AtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), ["reason"] = Reason,
            ["editor"] = Editor, ["files"] = Files, ["bytes"] = Bytes,
        };
    }

    /// <summary>True for a name this class makes: no separators, no "..", nothing but the time and a suffix.</summary>
    public static bool ValidName(string name) => name != null && NamePattern.IsMatch(name);

    /// <summary>A keep count within bounds (1 to <see cref="MaxKeep"/>), <see cref="DefaultKeep"/> when unset.</summary>
    public static int Keep(int? keep) => Math.Clamp(keep ?? DefaultKeep, 1, MaxKeep);

    /// <summary>The snapshot folder of a server: <paramref name="backupFolder"/>/GUO.</summary>
    public static string Root(string backupFolder) => Path.Combine(backupFolder, Folder);

    /// <summary>
    /// Copies <paramref name="saves"/> into a new snapshot under <paramref name="root"/> and returns it. The copy is
    /// made under a temporary name and renamed when whole, so a half copy is never listed.
    /// </summary>
    public static Snapshot Take(string saves, string root, DateTime utc, string reason, string editor)
    {
        if (!Directory.Exists(saves) || !Directory.EnumerateFileSystemEntries(saves).Any())
        {
            throw new InvalidOperationException("there is no world save to back up yet");
        }

        Directory.CreateDirectory(root);
        string name = utc.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + "Z" + (reason == BeforeRestore ? "_" + BeforeRestore : "");
        for (int n = 2; Directory.Exists(Path.Combine(root, name)); n++)
        {
            name = Regex.Replace(name, @"_\d{1,3}$", "") + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        string dest = Path.Combine(root, name), part = dest + ".part";
        if (Directory.Exists(part))
        {
            Directory.Delete(part, true);
        }

        try
        {
            (int files, long bytes) = CopyTree(saves, part, skipManifest: false);
            var snap = new Snapshot(name, utc, reason, editor, files, bytes);
            File.WriteAllText(Path.Combine(part, Manifest), snap.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(part, dest);
            return snap;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Every whole snapshot under <paramref name="root"/>, newest first. A folder without a readable manifest is skipped.</summary>
    public static List<Snapshot> List(string root)
    {
        var list = new List<Snapshot>();
        if (!Directory.Exists(root))
        {
            return list;
        }

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (Read(dir) is { } s)
            {
                list.Add(s);
            }
        }

        return list.OrderByDescending(s => s.AtUtc).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>One snapshot by name, or null (an invalid name is never looked up).</summary>
    public static Snapshot Find(string root, string name) => ValidName(name) ? Read(Path.Combine(root, name)) : null;

    private static Snapshot Read(string dir)
    {
        string name = Path.GetFileName(dir), manifest = Path.Combine(dir, Manifest);
        if (!ValidName(name) || !File.Exists(manifest))
        {
            return null;
        }

        try
        {
            JsonNode m = JsonNode.Parse(File.ReadAllText(manifest));
            DateTime at = DateTime.Parse((string)m["at"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            return new Snapshot(name, at, (string)m["reason"] ?? "", (string)m["editor"] ?? "", (int?)m["files"] ?? 0, (long?)m["bytes"] ?? 0);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the oldest snapshots beyond <paramref name="keep"/>, never one named in <paramref name="spare"/>
    /// (the one just taken, the one about to be restored). Returns the names removed.
    /// </summary>
    public static List<string> Prune(string root, int keep, params string[] spare)
    {
        var removed = new List<string>();
        List<Snapshot> all = List(root);
        int kept = 0;
        foreach (Snapshot s in all)
        {
            if (spare.Contains(s.Name) || kept < keep)
            {
                kept++;
                continue;
            }

            Directory.Delete(Path.Combine(root, s.Name), true);
            removed.Add(s.Name);
        }

        return removed;
    }

    /// <summary>
    /// Puts snapshot <paramref name="name"/> in place of <paramref name="saves"/>. Server stopped only. ModernUO
    /// publishes a staged save (<c>Saves.next</c>) over <c>Saves</c> when it boots, so a staged one is set aside too.
    /// On any failure the previous save is put back and the exception rethrown.
    /// </summary>
    public static Snapshot Restore(string root, string name, string saves)
    {
        Snapshot snap = Find(root, name) ?? throw new InvalidOperationException($"there is no backup called {name}");
        string source = Path.Combine(root, name);
        string incoming = saves + ".guo-restore", aside = saves + ".guo-replaced", staged = saves + ".next";
        TryDelete(incoming);
        TryDelete(aside);
        try
        {
            CopyTree(source, incoming, skipManifest: true);
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }

        bool moved = false;
        try
        {
            Directory.CreateDirectory(aside);
            if (Directory.Exists(saves))
            {
                Directory.Move(saves, Path.Combine(aside, "Saves"));
                moved = true;
            }

            if (Directory.Exists(staged))
            {
                Directory.Move(staged, Path.Combine(aside, "Saves.next"));
            }

            Directory.Move(incoming, saves);
        }
        catch
        {
            if (moved && !Directory.Exists(saves))
            {
                Directory.Move(Path.Combine(aside, "Saves"), saves);
            }

            if (Directory.Exists(Path.Combine(aside, "Saves.next")) && !Directory.Exists(staged))
            {
                Directory.Move(Path.Combine(aside, "Saves.next"), staged);
            }

            TryDelete(incoming);
            throw;
        }

        TryDelete(aside);
        return snap;
    }

    private static (int Files, long Bytes) CopyTree(string from, string to, bool skipManifest)
    {
        int files = 0;
        long bytes = 0;
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            if (skipManifest && rel == Manifest)
            {
                continue;
            }

            File.Copy(file, Path.Combine(to, rel));
            files++;
            bytes += new FileInfo(file).Length;
        }

        return (files, bytes);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
