using System.Runtime.CompilerServices;
using CentrED.MapGen.Data;

namespace CentrED.MapGen.Tests;

// Locates the repo from this source file's compile-time path, so tests find presets and
// the transition table even when the build output lives outside the repo
// (dotnet test --artifacts-path <temp>), where walking up from the binary finds nothing.
internal static class TestRepo
{
    public static string Root { get; } = FindRoot();

    public static string Path(string repoRelative) =>
        System.IO.Path.Combine(Root, repoRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// The transition table the tests use: GUO's committed table (transitions.guo.json). Tests never read a
    /// user's resolved table or Dragon import: they get an empty data folder of their own.
    /// </summary>
    public static string BrushTable => Path(GuoTransitionTable.RelativePath);

    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RepoRootResolver.RepoRootEnvVar)))
            Environment.SetEnvironmentVariable(RepoRootResolver.RepoRootEnvVar, Root);
        // Tests never read or write a user's mined data: give them an empty data folder of their own.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RepoRootResolver.DataDirEnvVar)))
            Environment.SetEnvironmentVariable(RepoRootResolver.DataDirEnvVar,
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guo-mapgen-tests", Environment.ProcessId.ToString()));
    }

    private static string FindRoot([CallerFilePath] string here = "")
    {
        // .../tools/mapgen/MapGen.Tests/TestRepo.cs -> repo root is three levels above the folder.
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(here)!);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "tools", "mapgen", "MapGen", "presets")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Environment.CurrentDirectory;
    }
}
