// GUO addition, not a port. AD8: the Admin tab's Audit list without Godot. The server's audit log (ADR-0035) as
// rows (when, who, op, result), the filter the list applies, and the CSV the list exports. Plain .NET (no Godot or
// ModernUO types): tools/editor_shard/bridge/tests compiles this file and checks it on a fixture log.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GUO.Workspace;

/// <summary>
/// One audit entry as the list shows it. Every value is as the server wrote it: a secret was masked to <c>***</c>
/// before the entry was kept, so nothing here is unmasked or re-masked.
/// </summary>
internal sealed record AdminAuditRow(DateTime? At, string AtText, string Who, string Op, string Level, bool Ok, string Error, string Args)
{
    /// <summary>When, on the tab's one clock: UTC with a Z.</summary>
    public string When => At is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z" : AtText ?? "";

    /// <summary>The outcome in plain words: "ran", or "refused" with the server's reason.</summary>
    public string Result => Ok ? "ran" : string.IsNullOrEmpty(Error) ? "refused" : "refused: " + Error;
}

internal static class AdminAuditView
{
    /// <summary>The folder under the repository's build/ that exports go to.</summary>
    public const string ExportFolder = "admin_audit";

    /// <summary>The CSV's columns, in order.</summary>
    public static readonly string[] Columns = { "when_utc", "who", "op", "level", "result", "args" };

    /// <summary>The rows of an <c>admin_audit</c> reply's <c>entries</c>, oldest first as the server sends them.</summary>
    public static List<AdminAuditRow> FromEntries(JsonArray entries)
    {
        var rows = new List<AdminAuditRow>();
        if (entries == null)
        {
            return rows;
        }

        foreach (JsonNode e in entries)
        {
            if (e is JsonObject o)
            {
                rows.Add(FromEntry(o));
            }
        }

        return rows;
    }

    /// <summary>The rows of an audit file (<c>Logs/GUO/admin_audit.jsonl</c>): one object a line; a broken line is skipped.</summary>
    public static List<AdminAuditRow> FromLines(string jsonl)
    {
        var rows = new List<AdminAuditRow>();
        foreach (string line in (jsonl ?? "").Split('\n'))
        {
            string l = line.Trim();
            if (l.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(l) is JsonObject o)
                {
                    rows.Add(FromEntry(o));
                }
            }
            catch (JsonException)
            {
                // A line cut short by a crash mid-append: the rest of the log still reads.
            }
        }

        return rows;
    }

    public static AdminAuditRow FromEntry(JsonObject e)
    {
        string at = Text(e["at"]);
        DateTime? when = DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
            ? t
            : null;
        string args = e["args"] is JsonObject a && a.Count > 0 ? a.ToJsonString() : "";
        return new AdminAuditRow(when, at, Text(e["editor"]), Text(e["op"]), Text(e["level"]), e["ok"] is JsonValue ok && ok.TryGetValue(out bool b) && b,
            Text(e["error"]), args);
    }

    private static string Text(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) ? s : n?.ToJsonString() ?? "";

    /// <summary>The distinct values of one column, sorted, for the filter's pickers.</summary>
    public static List<string> Distinct(IEnumerable<AdminAuditRow> rows, Func<AdminAuditRow, string> column) =>
        rows.Select(column).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The rows the filter keeps, in their order: <paramref name="op"/> and <paramref name="who"/> match a whole value
    /// (null or empty: any), <paramref name="text"/> is found anywhere in the row (when, who, op, level, result, args),
    /// ignoring case.
    /// </summary>
    public static List<AdminAuditRow> Filter(IEnumerable<AdminAuditRow> rows, string op, string who, string text)
    {
        string needle = (text ?? "").Trim();
        return rows.Where(r =>
                (string.IsNullOrEmpty(op) || string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(who) || string.Equals(r.Who, who, StringComparison.OrdinalIgnoreCase))
                && (needle.Length == 0 || Haystack(r).Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string Haystack(AdminAuditRow r) => string.Join("\n", r.When, r.Who, r.Op, r.Level, r.Result, r.Args);

    /// <summary>The rows as CSV (RFC 4180, a header line, CRLF), in the order given (the list's: newest first).</summary>
    public static string Csv(IEnumerable<AdminAuditRow> rows)
    {
        var csv = new StringBuilder();
        csv.Append(string.Join(",", Columns)).Append("\r\n");
        foreach (AdminAuditRow r in rows)
        {
            csv.Append(string.Join(",", new[] { r.When, r.Who, r.Op, r.Level, r.Result, r.Args }.Select(Cell))).Append("\r\n");
        }

        return csv.ToString();
    }

    // Quoted when it holds a comma, a quote or a line break. A cell a spreadsheet would run as a formula (=, +, -, @)
    // gets a leading apostrophe: an editor name or a command line is typed by whoever reached the bridge.
    internal static string Cell(string value)
    {
        string v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>The export file for <paramref name="utc"/>: <c>build/admin_audit/guo_AD8_audit_YYYYMMDD-HHMM.csv</c>.</summary>
    public static string ExportPath(string repoRoot, DateTime utc) =>
        Path.Combine(repoRoot, "build", ExportFolder, $"guo_AD8_audit_{utc.ToUniversalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv");

    /// <summary>
    /// Writes <paramref name="rows"/> to the export file for <paramref name="utc"/> under <paramref name="repoRoot"/>'s
    /// build/ (a second export in the same minute is <c>_v2</c>, then <c>_v3</c>) and returns its path. Refuses any
    /// path that would leave build/.
    /// </summary>
    public static string Export(string repoRoot, IEnumerable<AdminAuditRow> rows, DateTime utc)
    {
        string build = Path.GetFullPath(Path.Combine(repoRoot, "build")) + Path.DirectorySeparatorChar;
        string first = Path.GetFullPath(ExportPath(repoRoot, utc));
        string path = first;
        for (int v = 2; File.Exists(path); v++)
        {
            path = first[..^4] + $"_v{v}.csv";
        }

        if (!path.StartsWith(build, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("the audit export goes under build/ only");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Csv(rows), new UTF8Encoding(false));
        return path;
    }
}
