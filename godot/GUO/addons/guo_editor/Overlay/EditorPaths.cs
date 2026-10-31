#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>Resolve directory links before applying the editor's read-only install boundary.</summary>
internal static class EditorPaths
{
    private static readonly HashSet<string> ReadOnlySources = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal static void ProtectSource(string path)
    { string source = Real(path); lock (ReadOnlySources) ReadOnlySources.Add(source); }
    internal static string Real(string path)
    {
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full);
        foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null) current = info.ResolveLinkTarget(true)?.FullName
                ?? throw new IOException("Could not resolve editor output path.");
        }
        return Path.GetFullPath(current);
    }

    internal static string OutsideInstall(string path, string install)
    {
        string target = Real(path), source = Real(install).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(source, comparison) || target.StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Writing into the client installation is refused.");
        lock (ReadOnlySources)
            if (ReadOnlySources.Contains(target)) throw new IOException("A source document is read-only. Choose a different project/export destination.");
        return target;
    }

    internal static string ProjectFile(string project, string install, params string[] relative)
    {
        string root = Real(project);
        string target = OutsideInstall(Path.Combine(root, Path.Combine(relative)), install);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Document must stay inside the selected project.");
        return target;
    }
}
#endif
