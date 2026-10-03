#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>One agent session found on this machine (the Sessions tab, ADR-0028).</summary>
public sealed record SessionInfo(string Source, string Project, string Id, string Path, DateTime Last, string First, string QueueName, bool IsFolder);

/// <summary>
/// Lists the AI sessions that already exist on this machine, read only and metadata only: the
/// project, the last write time, and the first user line of each transcript. It looks at exactly
/// these places and nothing else: <c>~/.claude/projects/*/*.jsonl</c>, <c>~/.codex/sessions/**.jsonl</c>
/// and <c>~/.codex/session_index.jsonl</c>, and the Cursor project folders. It never opens a
/// credentials file (<c>auth.json</c>, <c>.credentials*</c>, <c>.env</c>, <c>config.toml</c>): those
/// are not on the list of places, and a transcript is read only up to <see cref="HeadBytes"/>.
/// Nothing here touches a Godot type, so it runs on a worker thread.
/// </summary>
public sealed class SessionScanner
{
    /// <summary>Most bytes read from any one file.</summary>
    public const int HeadBytes = 32 * 1024;

    /// <summary>Newest transcripts kept per Claude project, and in all.</summary>
    public int PerProject { get; set; } = 25;

    public int Total { get; set; } = 300;

    private readonly string _home;
    private readonly string _appData;
    private readonly Action<string> _opened;

    /// <param name="home">The user's home folder (a temporary one in the smoke).</param>
    /// <param name="appData">The roaming application data folder, for Cursor.</param>
    /// <param name="opened">Called with the path of every file this scanner opens (the smoke asserts on it).</param>
    public SessionScanner(string home = null, string appData = null, Action<string> opened = null)
    {
        _home = home ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        _appData = appData ?? (home != null ? Path.Combine(home, "AppData", "Roaming")
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData));
        _opened = opened;
    }

    public string Home => _home;

    /// <summary>The override the dock honours (smoke and tour): a folder to treat as the home.</summary>
    public static string HomeOverride => System.Environment.GetEnvironmentVariable("GUO_AI_HOME");

    public List<SessionInfo> Scan()
    {
        var all = new List<SessionInfo>();
        Safe(() => ScanClaude(all));
        Safe(() => ScanCodex(all));
        Safe(() => ScanCursor(all));
        return all.OrderByDescending(s => s.Last).Take(Total).ToList();
    }

    private static void Safe(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // --- Claude Code -----------------------------------------------------------------------------

    private void ScanClaude(List<SessionInfo> into)
    {
        string root = Path.Combine(_home, ".claude", "projects");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            var files = new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(PerProject);
            foreach (FileInfo f in files)
            {
                string cwd = null;
                string first = null;
                foreach (JsonNode line in HeadLines(f.FullName))
                {
                    cwd ??= (string)line?["cwd"];
                    if ((string)line?["type"] == "user" && (bool?)line["isMeta"] != true && first == null)
                    {
                        string t = Plain(line["message"]?["content"]);
                        if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith('<') && !t.StartsWith("Caveat:", StringComparison.Ordinal))
                        {
                            first = t;
                        }
                    }

                    if (cwd != null && first != null)
                    {
                        break;
                    }
                }

                string id = Path.GetFileNameWithoutExtension(f.Name);
                string project = !string.IsNullOrEmpty(cwd) ? Leaf(cwd) : Path.GetFileName(dir);
                into.Add(new SessionInfo("Claude", project, id, f.FullName, f.LastWriteTime, Clean(first), QueueName(project, id), false));
            }
        }
    }

    // --- Codex -----------------------------------------------------------------------------------

    private void ScanCodex(List<SessionInfo> into)
    {
        string root = Path.Combine(_home, ".codex");
        string sessions = Path.Combine(root, "sessions");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(sessions))
        {
            var files = new DirectoryInfo(sessions).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(Total);
            foreach (FileInfo f in files)
            {
                string cwd = null;
                string id = null;
                string first = null;
                foreach (JsonNode line in HeadLines(f.FullName))
                {
                    JsonNode p = line?["payload"];
                    if ((string)line?["type"] == "session_meta")
                    {
                        cwd ??= (string)p?["cwd"];
                        id ??= (string)p?["id"];
                    }
                    else if ((string)p?["type"] == "user_message" && first == null)
                    {
                        first = (string)p?["message"];
                    }

                    if (cwd != null && first != null)
                    {
                        break;
                    }
                }

                id ??= Path.GetFileNameWithoutExtension(f.Name);
                seen.Add(id);
                string project = cwd != null ? Leaf(cwd) : "codex";
                into.Add(new SessionInfo("Codex", project, id, f.FullName, f.LastWriteTime, Clean(first), QueueName(project, id), false));
            }
        }

        // The index names threads that may have no file left (or none under sessions).
        string index = Path.Combine(root, "session_index.jsonl");
        if (File.Exists(index))
        {
            DateTime when = File.GetLastWriteTime(index);
            foreach (JsonNode line in HeadLines(index))
            {
                string id = (string)line?["id"];
                if (string.IsNullOrEmpty(id) || seen.Contains(id))
                {
                    continue;
                }

                DateTime last = DateTime.TryParse((string)line["updated_at"], out DateTime u) ? u.ToLocalTime() : when;
                into.Add(new SessionInfo("Codex", "codex", id, null, last, Clean((string)line["thread_name"]), QueueName("codex", id), false));
            }
        }
    }

    // --- Cursor ----------------------------------------------------------------------------------

    private void ScanCursor(List<SessionInfo> into)
    {
        string projects = Path.Combine(_home, ".cursor", "projects");
        if (Directory.Exists(projects))
        {
            foreach (string dir in Directory.EnumerateDirectories(projects))
            {
                string name = Path.GetFileName(dir);
                into.Add(new SessionInfo("Cursor", name, name, dir, Directory.GetLastWriteTime(dir), "", QueueName(name, name), true));
            }
        }

        string storage = Path.Combine(_appData, "Cursor", "User", "workspaceStorage");
        if (Directory.Exists(storage))
        {
            foreach (string dir in Directory.EnumerateDirectories(storage))
            {
                string meta = Path.Combine(dir, "workspace.json");
                if (!File.Exists(meta))
                {
                    continue;
                }

                string folder = null;
                foreach (JsonNode j in HeadLines(meta, wholeFileIsOneObject: true))
                {
                    folder = (string)j?["folder"] ?? (string)j?["workspace"];
                }

                if (string.IsNullOrEmpty(folder))
                {
                    continue;
                }

                string decoded = Uri.UnescapeDataString(folder);
                string project = Leaf(decoded);
                into.Add(new SessionInfo("Cursor", project, Path.GetFileName(dir), dir, Directory.GetLastWriteTime(dir), decoded, QueueName(project, Path.GetFileName(dir)), true));
            }
        }
    }

    // --- reading ---------------------------------------------------------------------------------

    /// <summary>The JSON lines in the first <see cref="HeadBytes"/> of a file; a cut last line is dropped.</summary>
    private IEnumerable<JsonNode> HeadLines(string path, bool wholeFileIsOneObject = false)
    {
        string text;
        bool cut;
        _opened?.Invoke(path);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var buf = new byte[HeadBytes];
            int n = 0;
            while (n < buf.Length)
            {
                int r = fs.Read(buf, n, buf.Length - n);
                if (r <= 0)
                {
                    break;
                }

                n += r;
            }

            cut = fs.Position < fs.Length;
            text = Encoding.UTF8.GetString(buf, 0, n);
        }

        if (wholeFileIsOneObject)
        {
            JsonNode one = TryParse(text);
            if (one != null)
            {
                yield return one;
            }

            yield break;
        }

        string[] lines = text.Split('\n');
        int count = cut ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < count; i++)
        {
            string l = lines[i].Trim();
            if (l.Length == 0)
            {
                continue;
            }

            JsonNode j = TryParse(l);
            if (j != null)
            {
                yield return j;
            }
        }
    }

    private static JsonNode TryParse(string s)
    {
        try
        {
            return JsonNode.Parse(s);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Plain(JsonNode content)
    {
        if (content is JsonValue v)
        {
            return (string)v;
        }

        if (content is JsonArray a)
        {
            foreach (JsonNode part in a)
            {
                if ((string)part?["type"] == "text" && (string)part["text"] is string t)
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static string Clean(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 120 ? s[..120] + "..." : s;
    }

    private static string Leaf(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        int i = p.LastIndexOf('/');
        return i >= 0 ? p[(i + 1)..] : p;
    }

    /// <summary>A recipient name the queue accepts: 1-40 of letters, digits, underscore, dot, dash.</summary>
    public static string QueueName(string project, string id)
    {
        string Safe(string s) => new string((s ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray()).Trim('-');
        string p = Safe(project);
        string i = Safe(id);
        string tail = i.Length > 8 ? i[..8] : i;
        string name = p.Length > 0 && tail.Length > 0 ? $"{p}-{tail}" : p + tail;
        if (name.Length > 40)
        {
            name = name[..40];
        }

        return name.Length > 0 ? name : "session";
    }
}
#endif
