using System.Runtime.CompilerServices;
using CentrED.MapGen.Data;

namespace CentrED.MapGen.Tests;

// Locates the repo from this source file's compile-time path, so tests find presets and
// the brush table even when the build output lives outside the repo
// (dotnet test --artifacts-path <temp>), where walking up from the binary finds nothing.
internal static class TestRepo
{
    public static string Root { get; } = FindRoot();

    public static string Path(string repoRelative) =>
        System.IO.Path.Combine(Root, repoRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// The Dragon brush table the tests use, or null. GUO does not ship it (docs/upstream/mapgen.md): it is
    /// MAPGEN_BRUSH_TABLE, else the user's import in their generator data folder, else a copy in the repo.
    /// Tests that need it are <see cref="BrushFactAttribute"/>/<see cref="BrushTheoryAttribute"/> and skip without it.
    /// </summary>
    public static string? BrushTable { get; private set; }

    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RepoRootResolver.RepoRootEnvVar)))
            Environment.SetEnvironmentVariable(RepoRootResolver.RepoRootEnvVar, Root);
        BrushTable = new[]
        {
            Environment.GetEnvironmentVariable("MAPGEN_BRUSH_TABLE"),
            System.IO.Path.Combine(RepoRootResolver.DataDir(), "landbrush.dragon.json"),
            Path(LandBrushTable.DefaultJsonRelativePath),
        }.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        // Tests never read or write a user's mined data: give them an empty data folder of their own.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RepoRootResolver.DataDirEnvVar)))
            Environment.SetEnvironmentVariable(RepoRootResolver.DataDirEnvVar,
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guo-mapgen-tests", Environment.ProcessId.ToString()));
        // The pipeline finds the table where a user's would be: in the (test) data folder.
        if (BrushTable is not null)
        {
            string dest = RepoRootResolver.Resolve(LandBrushTable.DataJsonRelativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            if (!string.Equals(System.IO.Path.GetFullPath(dest), System.IO.Path.GetFullPath(BrushTable), StringComparison.OrdinalIgnoreCase))
                File.Copy(BrushTable, dest, overwrite: true);
        }
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

/// <summary>A fact that needs the Dragon brush table; skipped, with the reason, when there is none.</summary>
public sealed class BrushFactAttribute : FactAttribute
{
    public BrushFactAttribute()
    {
        if (TestRepo.BrushTable is null) Skip = BrushSkip.Reason;
    }
}

/// <summary>A theory that needs the Dragon brush table; skipped, with the reason, when there is none.</summary>
public sealed class BrushTheoryAttribute : TheoryAttribute
{
    public BrushTheoryAttribute()
    {
        if (TestRepo.BrushTable is null) Skip = BrushSkip.Reason;
    }
}

internal static class BrushSkip
{
    public const string Reason = "no Dragon brush table: run 'guo-mapgen prepare --dragon DIR' or set MAPGEN_BRUSH_TABLE";
}
