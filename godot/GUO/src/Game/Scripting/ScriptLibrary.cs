// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Linq;

namespace GUO.Game.Scripting;

/// <summary>Small per-character script library. Names cannot escape its directory.</summary>
internal sealed class ScriptLibrary
{
    private readonly string _directory;
    public ScriptLibrary(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) throw new IOException("No character profile is loaded.");
        _directory = Path.Combine(profilePath, "scripts");
    }

    private string FileFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            name.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' ||
                           c >= '0' && c <= '9' || c == '-' || c == '_')))
            throw new IOException("Use 1-64 letters, digits, hyphens or underscores for the name.");
        // Prefix also prevents Windows device names (CON, AUX, COM1, ...).
        return Path.Combine(_directory, "script-" + name + ".razor");
    }

    public string[] Names() => !Directory.Exists(_directory) ? Array.Empty<string>() :
        Directory.GetFiles(_directory, "script-*.razor")
            .Select(p => Path.GetFileNameWithoutExtension(p).Substring(7))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public string Read(string name)
    {
        string path = FileFor(name);
        if (new FileInfo(path).Length > ScriptRunner.MaximumLength * 4L)
            throw new IOException("Script file is too large.");
        string text = File.ReadAllText(path);
        if (text.Length > ScriptRunner.MaximumLength) throw new IOException("Script exceeds 64K characters.");
        return text;
    }

    public void Save(string name, string source)
    {
        string path = FileFor(name);
        if (source.Length > ScriptRunner.MaximumLength) throw new IOException("Script exceeds 64K characters.");
        Directory.CreateDirectory(_directory);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, source);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string AddStarter(StarterScript starter)
        => AddCopy(starter.Id, starter.Source);

    /// <summary>Create a personal copy without replacing edits. Optional store attribution
    /// travels beside the source and survives pack updates/removal.</summary>
    public string AddCopy(string suggestedName, string source, string attribution = null)
    {
        FileFor(suggestedName);
        if (source.Length > ScriptRunner.MaximumLength) throw new IOException("Script exceeds 64K characters.");
        var names = new System.Collections.Generic.HashSet<string>(Names(), StringComparer.OrdinalIgnoreCase);
        string name = suggestedName;
        for (int suffix = 2; names.Contains(name) || File.Exists(FileFor(name) + ".LICENSE.txt"); suffix++)
        {
            string ending = "-" + suffix;
            name = suggestedName.Substring(0, Math.Min(suggestedName.Length, 64 - ending.Length)) + ending;
        }
        Directory.CreateDirectory(_directory);
        string destination = FileFor(name), temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string notice = destination + ".LICENSE.txt", temporaryNotice = temporary + ".LICENSE.txt";
        bool noticeCreated = false, committed = false;
        try
        {
            File.WriteAllText(temporary, source);
            if (attribution != null)
            {
                File.WriteAllText(temporaryNotice, attribution);
                File.Move(temporaryNotice, notice); // Never replace a pre-existing sidecar.
                noticeCreated = true;
            }
            File.Move(temporary, destination); // Never replace a concurrent writer's file.
            committed = true;
            return name;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporaryNotice)) File.Delete(temporaryNotice);
            if (!committed && noticeCreated) File.Delete(notice);
        }
    }
}
