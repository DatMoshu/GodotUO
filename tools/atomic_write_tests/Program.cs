using System.Text;
using GUO.Editor;
using GUO.Workspace;

// SF8: world project files are written by temporary file and move; a failed write leaves the old file whole.
// Run: dotnet run --project tools/atomic_write_tests/AtomicWriteTests.csproj

string home = Path.Combine(Path.GetTempPath(), "guo-atomic-" + Guid.NewGuid().ToString("N"));
try
{
    AtomicWriteTests.Run(home);
}
finally
{
    Directory.Delete(home, true);
}

internal static class AtomicWriteTests
{
    public static void Run(string home)
    {
        string dir = Path.Combine(home, "atomic");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "block.json");
        const string old = "{\"old\": true}\n";
        File.WriteAllText(target, old);

        // A write that dies halfway: the old file is untouched and no temporary is left behind.
        bool threw = false;
        try
        {
            Workspace.WriteAtomic(target, stream =>
            {
                stream.Write(Encoding.UTF8.GetBytes("{\"new\": tr"));
                throw new IOException("simulated interruption");
            });
        }
        catch (IOException)
        {
            threw = true;
        }

        Require(threw, "the interrupted write did not surface its error");
        Require(File.ReadAllText(target) == old, "an interrupted write changed the old file");
        Require(Directory.GetFiles(dir).Length == 1, "an interrupted write left a temporary file");

        // The text overload writes the same bytes File.WriteAllText did (UTF-8, no byte order mark).
        string text = "{\"name\": \"Trinsic é\"}\n";
        Workspace.WriteAtomic(target, text);
        string reference = Path.Combine(home, "reference.json");
        File.WriteAllText(reference, text);
        Require(File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(reference)), "text bytes differ from File.WriteAllText");
        Require(Directory.GetFiles(dir).Length == 1, "a good write left a temporary file");

        // A real project writer: ShardObjects.Save goes through the helper.
        string project = Path.Combine(home, "project");
        var objects = new ShardObjects(project);
        objects.Items.Add(new ShardItem { Map = "Felucca", X = 1496, Y = 1628, Z = 10, ItemId = 0x0E76 });
        objects.Save();
        string saved = File.ReadAllText(objects.Path);
        Require(new ShardObjects(project).Items.Count == 1, "objects.json did not round-trip");

        // A target another process holds open cannot be replaced on Windows: the save fails, the old file stays whole.
        if (OperatingSystem.IsWindows())
        {
            objects.Items.Add(new ShardItem { Map = "Felucca", X = 1497, Y = 1628, Z = 10, ItemId = 0x0E77 });
            bool refused = false;
            using (new FileStream(objects.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    objects.Save();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    refused = true;
                }
            }

            Require(refused, "a save over a locked file did not fail");
            Require(File.ReadAllText(objects.Path) == saved, "a failed save changed objects.json");
            Require(Directory.GetFiles(Path.GetDirectoryName(objects.Path)).Length == 1, "a failed save left a temporary file");
        }

        Require(false, "CI-01 planted failure");
        Console.WriteLine("PASS: atomic project writes (interrupted write and failed save leave the old file whole, no temporaries, same bytes)");
    }

    private static void Require(bool ok, string what)
    {
        if (!ok)
        {
            throw new Exception(what);
        }
    }
}
