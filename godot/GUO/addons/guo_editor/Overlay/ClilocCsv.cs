#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

internal enum ClilocCsvFormat { Fiddler, QuotedUtf8 }
internal sealed record ClilocCsvRow(int Id, string Text, byte Flag);
internal sealed record ClilocCsvChange(int Id, string Before, string After, bool Added, string Warning);

/// <summary>UTF-8 output; legacy input also detects the BOM encodings accepted by Fiddler's StreamReader.</summary>
internal static class ClilocCsv
{
    internal const int MaxBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Flag(byte value) => value switch { 0 => "Original", 1 => "Custom", 2 => "Modified", 3 => "Custom, Modified", _ => value.ToString(CultureInfo.InvariantCulture) };
    private static byte ParseFlag(string value) => value.Trim() switch
    {
        "Original" => 0, "Custom" => 1, "Modified" => 2, "Custom, Modified" => 3,
        var raw when byte.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out byte flag) => flag,
        _ => throw new IOException("Flag must be Original, Custom, Modified or a decimal byte (0–255)."),
    };
    internal static byte[] ReadFile(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxBytes) throw new IOException("Cliloc CSV exceeds 64 MiB.");
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    internal static IReadOnlyList<ClilocCsvRow> Parse(byte[] bytes, ClilocCsvFormat format)
    {
        if (!Enum.IsDefined(format) || bytes == null || bytes.Length > MaxBytes) throw new IOException("Choose a supported CSV format of at most 64 MiB.");
        string text = Decode(bytes, format);
        if (text.Contains('\0')) throw new IOException("CSV contains NUL characters; no rows were imported.");
        var result = new List<ClilocCsvRow>(); var seen = new HashSet<int>(); int record = 0;
        foreach (string[] fields in Records(text, format))
        {
            record++;
            if ((fields.Length == 1 && string.IsNullOrWhiteSpace(fields[0])) || fields[0].TrimStart().StartsWith('#')) continue;
            if (fields.SequenceEqual(new[] { "Number", "Text", "Flag" })) continue;
            if (fields.Length != 3 || !int.TryParse(fields[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                throw new IOException($"CSV record {record}: expected non-negative decimal Number;Text;Flag. No rows were imported.");
            if (!seen.Add(id)) throw new IOException($"CSV record {record}: duplicate ID {id}. Keep one row per ID.");
            byte flag;
            try { flag = ParseFlag(fields[2]); }
            catch (IOException e) { throw new IOException($"CSV record {record}: {e.Message}"); }
            result.Add(new(id, format == ClilocCsvFormat.Fiddler ? fields[1].Trim() : fields[1], flag));
            if (result.Count > 500_000) throw new IOException("CSV exceeds 500,000 records.");
        }
        if (result.Count == 0) throw new IOException("CSV contains no localized string records.");
        return result.AsReadOnly();
    }
    private static string Decode(byte[] bytes, ClilocCsvFormat format)
    {
        ReadOnlySpan<byte> input = bytes; Encoding encoding = Utf8; int offset = 0;
        if (input.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) offset = 3;
        else if (format == ClilocCsvFormat.Fiddler)
        {
            // Test four-byte markers before UTF-16 LE's shared FF FE prefix.
            if (input.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
            else if (input.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
            else if (input.StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
            else if (input.StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        }
        try { return encoding.GetString(input[offset..]); }
        catch (DecoderFallbackException)
        { throw new IOException($"CSV contains invalid {encoding.WebName} bytes. Re-export as valid UTF-8, or use a BOM-marked UTF-16/UTF-32 file with the Fiddler dialect."); }
    }
    private static IEnumerable<string[]> Records(string text, ClilocCsvFormat format)
    {
        if (format == ClilocCsvFormat.Fiddler)
        {
            using var lines = new StringReader(text);
            while (lines.ReadLine() is { } line) yield return line.Split(';');
            yield break;
        }
        var fields = new List<string>(); var field = new StringBuilder();
        bool quoted = false, closed = false, started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c is ';' or '\r' or '\n')
            {
                fields.Add(field.ToString()); field.Clear(); closed = started = false;
                if (c == ';') continue;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                yield return fields.ToArray(); fields.Clear(); continue;
            }
            if (closed) throw new IOException("Unexpected text after a closing CSV quote. No rows were imported.");
            if (c == '"')
            {
                if (started) throw new IOException("Quote inside an unquoted CSV field. Use doubled quotes inside a quoted field.");
                quoted = started = true;
            }
            else { field.Append(c); started = true; }
        }
        if (quoted) throw new IOException("CSV has an unterminated quoted field. No rows were imported.");
        if (started || closed || fields.Count > 0) { fields.Add(field.ToString()); yield return fields.ToArray(); }
    }
    internal static byte[] Encode(IEnumerable<ClilocCsvRow> rows, ClilocCsvFormat format)
    {
        if (!Enum.IsDefined(format)) throw new IOException("Choose a supported Cliloc CSV format.");
        var output = new StringBuilder("Number;Text;Flag\r\n"); var seen = new HashSet<int>();
        foreach (var row in rows)
        {
            if (row.Id < 0 || !seen.Add(row.Id) || seen.Count > 500_000 || row.Text == null || row.Text.Contains('\0'))
                throw new IOException("CSV export requires distinct non-negative IDs and text without NUL.");
            string text = row.Text;
            if (format == ClilocCsvFormat.Fiddler)
            {
                if (text.IndexOfAny(new[] { ';', '\r', '\n' }) >= 0 || text != text.Trim())
                    throw new IOException($"String {row.Id} cannot roundtrip through Fiddler's line-based importer. Choose quoted UTF-8 CSV to preserve delimiters, line breaks and surrounding spaces.");
            }
            else if (text.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0) text = "\"" + text.Replace("\"", "\"\"") + "\"";
            output.Append(row.Id.ToString(CultureInfo.InvariantCulture)).Append(';').Append(text).Append(';').Append(Flag(row.Flag)).Append("\r\n");
            if (output.Length > MaxBytes) throw new IOException("CSV export exceeds 64 MiB.");
        }
        byte[] encoded = Utf8.GetBytes(output.ToString());
        if (encoded.Length > MaxBytes) throw new IOException("CSV export exceeds 64 MiB.");
        return encoded;
    }
    internal static void Export(ClilocDocument document, IEnumerable<int> ids, string path, ClilocCsvFormat format)
    {
        document.CheckSource();
        byte[] bytes = Encode(ids.Select(id => (Id: id, Entry: document.Get(id))).Where(row => row.Entry != null)
            .Select(row => new ClilocCsvRow(row.Id, row.Entry.Text, row.Entry.Flag)), format);
        AssetActions.EnsureExportDestination(path); string target = document.WritablePath(path);
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new IOException("Choose an existing destination folder.");
        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); AssetActions.EnsureExportDestination(target); document.WritablePath(target); document.CheckSource(); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Captured immutable import review, bound to one source and working revision.</summary>
internal sealed class ClilocCsvPlan
{
    private readonly ClilocDocument _document;
    private readonly long _revision;
    private readonly Dictionary<int, string> _text;
    internal IReadOnlyList<ClilocCsvChange> Changes { get; }
    internal int Records { get; }
    internal int Unchanged => Records - Changes.Count;
    internal int FlagDifferences { get; }
    internal ClilocCsvPlan(ClilocDocument document, byte[] bytes, ClilocCsvFormat format)
    {
        document.CheckSource(); _document = document; _revision = document.Revision;
        var rows = ClilocCsv.Parse(bytes, format); Records = rows.Count;
        _text = rows.ToDictionary(row => row.Id, row => row.Text);
        document.ApplyTextBatch(_text, validateOnly: true);
        FlagDifferences = rows.Count(row => row.Flag != (document.Get(row.Id)?.Flag ?? document.Original(row.Id)?.Flag ?? 0));
        Changes = rows.Where(row => document.Get(row.Id)?.Text != row.Text).Select(row =>
            new ClilocCsvChange(row.Id, document.Get(row.Id)?.Text, row.Text, document.Get(row.Id) == null,
                document.TokenWarning(row.Id, row.Text))).ToList().AsReadOnly();
    }
    internal void Apply()
    {
        _document.CheckSource();
        if (_document.Revision != _revision) throw new IOException("The language document changed after review. Close and reopen the CSV review.");
        _document.ApplyTextBatch(_text);
    }
}
#endif
