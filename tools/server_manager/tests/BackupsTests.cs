using GUO.Workspace;

/// <summary>
/// The Admin tab's backups without the engine or a server (AD6): a snapshot of a save folder, its manifest, the
/// list newest first, keep N, names that cannot leave the folder, and a restore that sets the staged save aside
/// and puts the previous save back when it cannot finish.
/// </summary>
internal static class BackupsTests
{
    private static void Require(bool ok, string what) { if (!ok) throw new Exception("backups: " + what); }

    public static void Run(string home)
    {
        string server = Path.Combine(home, "backups server"), saves = Path.Combine(server, "Saves");
        string root = ShardBackups.Root(Path.Combine(server, "Backups"));
        Directory.CreateDirectory(Path.Combine(saves, "Accounts"));
        File.WriteAllText(Path.Combine(saves, "Accounts", "Accounts.bin"), "world A");
        File.WriteAllText(Path.Combine(saves, "Items.bin"), "items A");

        // A snapshot: the files, a manifest without a path, a name from the time.
        var t0 = new DateTime(2026, 10, 10, 3, 15, 0, DateTimeKind.Utc);
        ShardBackups.Snapshot a = ShardBackups.Take(saves, root, t0, "manual", "Admin tab (test)");
        Require(a.Name == "2026-10-10_031500Z" && a.Files == 2 && a.Bytes == 14, $"snapshot {a}");
        string manifest = File.ReadAllText(Path.Combine(root, a.Name, ShardBackups.Manifest));
        Require(!manifest.Contains(home) && !manifest.Contains(":\\") && manifest.Contains("\"reason\": \"manual\""), "the manifest holds a path or no reason");
        Require(File.ReadAllText(Path.Combine(root, a.Name, "Accounts", "Accounts.bin")) == "world A", "the snapshot's files");
        Require(!Directory.EnumerateDirectories(root, "*.part").Any(), "a part folder was left");
        ShardBackups.Snapshot same = ShardBackups.Take(saves, root, t0, "manual", "x");
        Require(same.Name == a.Name + "_2", "a second snapshot in the same second: " + same.Name);
        Directory.Delete(Path.Combine(root, same.Name), true);
        Console.WriteLine("PASS: a snapshot copies the save folder whole, with a manifest naming no path");

        // An empty save folder is refused; so is a missing one.
        string empty = Path.Combine(server, "Empty"); Directory.CreateDirectory(empty);
        Require(Refused(() => ShardBackups.Take(empty, root, t0, "manual", "x")) && Refused(() => ShardBackups.Take(Path.Combine(server, "None"), root, t0, "manual", "x")),
            "an empty or missing save folder was backed up");

        // The list, newest first; a folder without a manifest or with a foreign name is not a snapshot.
        File.WriteAllText(Path.Combine(saves, "Items.bin"), "items B, longer");
        ShardBackups.Snapshot b = ShardBackups.Take(saves, root, t0.AddMinutes(5), "manual", "x");
        ShardBackups.Snapshot before = ShardBackups.Take(saves, root, t0.AddMinutes(6), ShardBackups.BeforeRestore, "x");
        Require(before.Name == "2026-10-10_032100Z_before-restore", "before-restore name " + before.Name);
        Directory.CreateDirectory(Path.Combine(root, "2026-10-10_040000Z"));
        Directory.CreateDirectory(Path.Combine(root, "not a backup"));
        File.WriteAllText(Path.Combine(root, "not a backup", ShardBackups.Manifest), "{\"at\":\"2030-01-01T00:00:00Z\"}");
        List<ShardBackups.Snapshot> list = ShardBackups.List(root);
        Require(list.Select(s => s.Name).SequenceEqual(new[] { before.Name, b.Name, a.Name }), "list: " + string.Join(",", list.Select(s => s.Name)));
        Console.WriteLine("PASS: the list is newest first and holds only whole snapshots");

        // Names that could leave the folder are never looked up.
        foreach (string bad in new[] { "..", "../Saves", "2026-10-10_031500Z/..", "2026-10-10_031500Z\\x", "", null, "C:\\Windows", "2026-10-10_031500" })
        {
            Require(!ShardBackups.ValidName(bad) && ShardBackups.Find(root, bad) == null, "a bad name was accepted: " + bad);
        }

        Require(ShardBackups.Keep(null) == ShardBackups.DefaultKeep && ShardBackups.Keep(0) == 1 && ShardBackups.Keep(1000) == ShardBackups.MaxKeep, "keep bounds");

        // Keep N: the oldest go, never a spared one.
        List<string> removed = ShardBackups.Prune(root, 1, a.Name);
        Require(removed.SequenceEqual(new[] { b.Name }) && ShardBackups.List(root).Select(s => s.Name).SequenceEqual(new[] { before.Name, a.Name }), "prune kept the wrong ones: " + string.Join(",", removed));
        Require(ShardBackups.Prune(root, 5).Count == 0, "prune under the limit removed something");
        // Snapshots in one second keep their order: the newest is the last taken, whatever its name's suffix.
        var t1 = t0.AddMinutes(10);
        string s1 = ShardBackups.Take(saves, root, t1, "manual", "x").Name;
        string s2 = ShardBackups.Take(saves, root, t1.AddMilliseconds(300), ShardBackups.BeforeRestore, "x").Name;
        string s3 = ShardBackups.Take(saves, root, t1.AddMilliseconds(600), "manual", "x").Name;
        Require(ShardBackups.List(root).Take(3).Select(s => s.Name).SequenceEqual(new[] { s3, s2, s1 }), "same-second order");
        List<string> sameSecond = ShardBackups.Prune(root, 1, s3);
        Require(ShardBackups.List(root).Select(s => s.Name).SequenceEqual(new[] { s3 }) && sameSecond.Contains(s2) && sameSecond.Contains(s1),
            "keep 1 in one second kept the wrong one");
        // The restore below brings back a snapshot of world A, taken again (the prune removed the first).
        File.WriteAllText(Path.Combine(saves, "Items.bin"), "items A");
        a = ShardBackups.Take(saves, root, t0, "manual", "Admin tab (test)");
        Console.WriteLine("PASS: keep N removes the oldest snapshots and spares the named ones");

        // Restore: the snapshot's files replace the save, the manifest stays out, a staged save is set aside.
        File.WriteAllText(Path.Combine(saves, "Items.bin"), "items C");
        File.WriteAllText(Path.Combine(saves, "Extra.bin"), "only in C");
        string staged = saves + ".next"; Directory.CreateDirectory(staged); File.WriteAllText(Path.Combine(staged, "Items.bin"), "staged");
        ShardBackups.Restore(root, a.Name, saves);
        Require(File.ReadAllText(Path.Combine(saves, "Items.bin")) == "items A" && File.ReadAllText(Path.Combine(saves, "Accounts", "Accounts.bin")) == "world A",
            "restore did not bring the snapshot back");
        Require(!File.Exists(Path.Combine(saves, "Extra.bin")) && !File.Exists(Path.Combine(saves, ShardBackups.Manifest)), "restore left a newer file or the manifest");
        Require(!Directory.Exists(staged) && !Directory.Exists(saves + ".guo-replaced") && !Directory.Exists(saves + ".guo-restore"), "restore left a staged save or its own folders");
        Require(ShardBackups.Find(root, a.Name) != null, "restore used up the snapshot");
        Console.WriteLine("PASS: restore replaces the save with the snapshot and sets a staged save aside");

        // A restore that cannot finish puts the previous save back.
        Require(Refused(() => ShardBackups.Restore(root, "2026-01-01_000000Z", saves)), "a missing snapshot was restored");
        if (OperatingSystem.IsWindows())
        {
            // Windows will not move a folder holding an open file: the swap fails half way.
            File.WriteAllText(Path.Combine(saves, "Items.bin"), "items D");
            using (File.Open(Path.Combine(saves, "Items.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Require(Refused(() => ShardBackups.Restore(root, a.Name, saves)), "a restore over a locked save reported success");
            }

            Require(File.ReadAllText(Path.Combine(saves, "Items.bin")) == "items D", "the previous save was not kept after a failed restore");
            Require(!Directory.Exists(saves + ".guo-restore"), "a failed restore left its copy");
        }

        Console.WriteLine("PASS: a restore that cannot finish leaves the previous save in place");
    }

    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { return true; }
    }
}
