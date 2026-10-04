#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

/// <summary>
/// Finds and starts the outside image editors (ADR-0029): Pixelorama, GUO's pixel editor fetched by
/// tools/pixelorama, and Pinta, the photo-style editor (MIT) that the user installs themselves.
/// GUO installs neither's binary for you here; a missing Pinta gets the install hint.
/// </summary>
public static class ExternalTools
{
    public const string PintaHint = "Pinta is not installed. Install it with: winget install Pinta.Pinta (or set UO_PINTA to Pinta.exe)";

    /// <summary>When true nothing is started (the smoke), and the would-be command is returned instead.</summary>
    public static bool DryRun;

    public static string FindPinta()
    {
        string configured = EditorData.Setting("UO_PINTA", "");
        if (configured.Length > 0 && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new List<string>();
        foreach (string dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "Pinta.exe" : "pinta"));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[]
                     {
                         System.Environment.SpecialFolder.ProgramFiles,
                         System.Environment.SpecialFolder.ProgramFilesX86,
                         System.Environment.SpecialFolder.LocalApplicationData,
                     })
            {
                string root = System.Environment.GetFolderPath(folder);
                if (root.Length == 0)
                {
                    continue;
                }

                // The winget/Inno installer puts it in Pinta\bin.
                candidates.Add(Path.Combine(root, "Pinta", "bin", "Pinta.exe"));
                candidates.Add(Path.Combine(root, "Pinta", "Pinta.exe"));
                candidates.Add(Path.Combine(root, "Programs", "Pinta", "bin", "Pinta.exe"));
                candidates.Add(Path.Combine(root, "Programs", "Pinta", "Pinta.exe"));
            }
        }
        else
        {
            candidates.Add("/usr/bin/pinta");
            candidates.Add("/usr/local/bin/pinta");
        }

        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    public static string FindPixelorama()
    {
        string configured = EditorData.Setting("UO_PIXELORAMA", "");
        if (configured.Length > 0 && File.Exists(configured))
        {
            return configured;
        }

        var roots = new List<string> { EditorData.RepoRoot };
        string gitFile = Path.Combine(EditorData.RepoRoot, ".git");
        if (File.Exists(gitFile))
        {
            string line = File.ReadAllText(gitFile).Trim();
            if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                string gitDir = Path.GetFullPath(line[7..].Trim(), EditorData.RepoRoot);
                string commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile)) roots.Add(Directory.GetParent(Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir)).FullName);
            }
        }
        foreach (string root in roots)
        {
            string bin = Path.Combine(root, "tools", "pixelorama", "bin");
            if (!Directory.Exists(bin)) continue;
            string name = OperatingSystem.IsWindows() ? "Pixelorama.exe" : "Pixelorama.x86_64";
            foreach (string f in Directory.GetFiles(bin, name, SearchOption.AllDirectories))
            {
                return f;
            }
        }

        foreach (string dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "Pixelorama.exe" : "pixelorama");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>One line each, for the Art dock and the smoke log.</summary>
    public static string Describe() =>
        $"Pixelorama: {(FindPixelorama() != null ? "found" : "missing (python tools/pixelorama/run.py fetch)")}; "
        + $"Pinta: {(FindPinta() != null ? "found" : "missing (" + PintaHint + ")")}";

    /// <summary>Opens the exported PNG in Pixelorama through tools/pixelorama/run.py, which installs the extension too. Null on success, else why not.</summary>
    public static string OpenPixelorama(string png)
    {
        if (FindPixelorama() == null)
        {
            return "Pixelorama is not fetched. Run: python tools/pixelorama/run.py fetch";
        }

        if (DryRun)
        {
            return null;
        }

        string sidecar = Path.ChangeExtension(png, ".json");
        string run = Path.Combine(EditorData.RepoRoot, "tools", "pixelorama", "run.py");
        int pid = OS.CreateProcess(EditorData.Setting("UO_PYTHON", "python"), new[] { run, "open", png, "--sidecar", sidecar });
        return pid > 0 ? null : "could not start python tools/pixelorama/run.py";
    }

    public static string OpenPinta(string png)
    {
        string exe = FindPinta();
        if (exe == null)
        {
            return PintaHint;
        }

        if (DryRun)
        {
            return null;
        }

        int pid = OS.CreateProcess(exe, new[] { png });
        return pid > 0 ? null : $"could not start {Path.GetFileName(exe)}";
    }
}
#endif
