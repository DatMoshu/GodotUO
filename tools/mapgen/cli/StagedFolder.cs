namespace GuoMapGen;

// Keep unfinished writes away from the public paths. A process killed before publication can
// leave a staging folder, but a retry always starts in a different one.
internal sealed class StagedFolder : IDisposable
{
    public string Destination { get; }
    public string WorkingPath { get; }

    public StagedFolder(string destination)
    {
        Destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        OutputFolder.RequireFresh(Destination);
        WorkingPath = Path.Combine(Path.GetDirectoryName(Destination)!,
            $".{Path.GetFileName(Destination)}-mapgen-{Guid.NewGuid():N}");
        OutputFolder.PrepareFresh(WorkingPath);
    }

    public void Publish()
    {
        if (Directory.Exists(Destination))
            Directory.Delete(Destination); // only an empty destination is replaceable
        // Windows readers can briefly hold a freshly written file without delete sharing.
        // Retry only that rename, and never replace another publisher's result.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(WorkingPath, Destination);
                return;
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && attempt < 5 &&
                e is IOException or UnauthorizedAccessException &&
                (e.HResult & 0xffff) is 5 or 32 or 33 && !Directory.Exists(Destination))
            {
                Thread.Sleep(50 << attempt);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(WorkingPath))
            Directory.Delete(WorkingPath, recursive: true);
    }
}
