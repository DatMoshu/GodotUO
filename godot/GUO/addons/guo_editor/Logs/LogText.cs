#if TOOLS
namespace GUO.Editor;

using System;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>How serious a log line looks. Ordered: a filter shows everything at or above a level.</summary>
public enum LogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}

/// <summary>
/// What the Logs tab does to a line before it is shown: hides anything that looks like a credential,
/// guesses its level from its words, and (for logs written in UTC) puts the local time beside it.
/// Pure text, no Godot, so the smoke check can drive it directly.
/// </summary>
public static class LogText
{
    // The shapes tools/agent_queue refuses as secrets (SECRET_PATTERNS in run.py), here replaced
    // rather than refused, because a log is read-only and must still show the line around them.
    private static readonly Regex[] Whole =
    {
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled),
        new(@"\bgh[pousr]_[A-Za-z0-9]{30,}", RegexOptions.Compiled),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),
        new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled),
    };

    private static readonly Regex Bearer = new(@"(?i)(\bauthorization:\s*bearer\s+)\S{8,}", RegexOptions.Compiled);

    // password=hunter22, "token": "abc...", api_key: xyz. The key stays, the value goes.
    private static readonly Regex Pair = new(
        @"(?i)(\b(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token)[""']?\s*[:=]\s*[""']?)[^\s""',;&]{4,}",
        RegexOptions.Compiled);

    // ModernUO's login lines can carry the account's password ("password hunter22").
    private static readonly Regex Login = new(@"(?i)(\bpassword\s+)\S{4,}", RegexOptions.Compiled);

    public const string Mask = "[redacted]";

    public static string Redact(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line ?? "";
        }

        foreach (Regex r in Whole)
        {
            line = r.Replace(line, Mask);
        }

        line = Bearer.Replace(line, "$1" + Mask);
        line = Pair.Replace(line, "$1" + Mask);
        return Login.Replace(line, "$1" + Mask);
    }

    private static readonly Regex ErrorWords = new(@"(?i)\b(error|errors|exception|fatal|panic|critical|crash(ed)?|failed|failure)\b|^\s*\[?E(RR)?\]", RegexOptions.Compiled);
    private static readonly Regex WarnWords = new(@"(?i)\b(warn|warning|warnings|deprecated)\b|^\s*\[?W(ARN)?\]", RegexOptions.Compiled);

    public static LogLevel Classify(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return LogLevel.Info;
        }

        if (ErrorWords.IsMatch(line))
        {
            return LogLevel.Error;
        }

        return WarnWords.IsMatch(line) ? LogLevel.Warn : LogLevel.Info;
    }

    private static readonly Regex Iso = new(@"^\[?(\d{4}-\d\d-\d\d[T ]\d\d:\d\d:\d\d)", RegexOptions.Compiled);

    /// <summary>
    /// For a UTC log: the line with its local time in front, "[14:03:22 local] ", when it starts with a
    /// timestamp this can read ("2026-10-02 12:03:22" or the client console's "10/2/2026 12:03:22 PM | ").
    /// A line with no timestamp comes back as it was.
    /// </summary>
    public static string WithLocalTime(string line)
    {
        DateTime utc = default;
        bool found = false;
        Match m = Iso.Match(line);
        if (m.Success)
        {
            found = DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
        }
        else
        {
            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            if (bar > 5 && bar <= 40)
            {
                found = DateTime.TryParse(line.AsSpan(0, bar), CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
            }
        }

        return found ? $"[{utc.ToLocalTime():HH:mm:ss} local] {line}" : line;
    }
}
#endif
