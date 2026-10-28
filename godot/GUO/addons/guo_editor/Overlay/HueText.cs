#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

internal enum HueTextFormat { Fiddler1252, Utf8 }
internal sealed record HueTextEntry(string Name, ushort Start, ushort End, ushort[] Colors);

/// <summary>Bounded 35-line Fiddler palette exchange; explicit encoding, complete validation.</summary>
internal static class HueText
{
    internal const int MaxBytes = 65536;
    internal static byte[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new IOException("Hue text must fit 64 KiB.");
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Hue text changed while reading. Choose the file again.");
        return bytes;
    }
    private static Encoding EncodingFor(HueTextFormat format)
    {
        if (format == HueTextFormat.Utf8) return new UTF8Encoding(false, true);
        if (format != HueTextFormat.Fiddler1252) throw new IOException("Choose Windows-1252 or UTF-8 hue text.");
        var encoding = (Encoding)CodePagesEncodingProvider.Instance.GetEncoding(1252).Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback; return encoding;
    }
    internal static void Validate(HueTextEntry entry)
    {
        if (entry == null || entry.Colors == null || entry.Colors.Length != 32) throw new IOException("Hue text needs exactly 32 colors.");
        if (entry.Name == null || entry.Name.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 || Encoding.UTF8.GetByteCount(entry.Name) > 4096)
            throw new IOException("Hue name must fit 4,096 UTF-8 bytes without NUL or line breaks.");
    }
    internal static HueTextEntry Decode(byte[] bytes, HueTextFormat format)
    {
        if (bytes == null || bytes.Length > MaxBytes) throw new IOException("Hue text must fit 64 KiB.");
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        if (offset > 0 && format != HueTextFormat.Utf8) throw new IOException("This file has a UTF-8 BOM. Choose UTF-8.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) throw new IOException("UTF-16 hue text is unsupported. Choose a UTF-8 or Windows-1252 file.");
        string text;
        try { text = EncodingFor(format).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new IOException("Invalid UTF-8. Choose Windows-1252 only if that is the file's declared encoding."); }
        var lines = new List<string>(); using var reader = new StringReader(text);
        while (reader.ReadLine() is string line) { if (lines.Count == 35) throw new IOException("Hue text has extra lines; expected name, two bounds and exactly 32 colors."); lines.Add(line); }
        if (lines.Count != 35) throw new IOException($"Hue text has {lines.Count} lines; expected exactly 35.");
        ushort Number(int index) => ushort.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort value) ? value
            : throw new IOException($"Line {index + 1} must be decimal 0–65535.");
        var entry = new HueTextEntry(lines[0], Number(1), Number(2), Enumerable.Range(3, 32).Select(Number).ToArray()); Validate(entry); return entry;
    }
    internal static byte[] Encode(HueTextEntry entry, HueTextFormat format)
    {
        Validate(entry); string text = entry.Name + "\r\n" + entry.Start.ToString(CultureInfo.InvariantCulture) + "\r\n" + entry.End.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + string.Concat(entry.Colors.Select(value => value.ToString(CultureInfo.InvariantCulture) + "\r\n"));
        try { byte[] bytes = EncodingFor(format).GetBytes(text); if (bytes.Length > MaxBytes) throw new IOException("Hue text exceeds 64 KiB."); return bytes; }
        catch (EncoderFallbackException) { throw new IOException("The hue name cannot be represented in Windows-1252. Choose UTF-8 to preserve it."); }
    }
    internal static byte[] EncodeNames(IEnumerable<(int Id, string Name)> rows)
    {
        var text = new StringBuilder(); int previous = 0;
        foreach (var row in rows.OrderBy(row => row.Id))
        {
            if (row.Id <= previous || row.Id > 65535) throw new IOException("Hue names require unique one-based IDs 1–65535.");
            Validate(new(row.Name, 0, 0, new ushort[32])); previous = row.Id;
            text.Append("0x").Append((row.Id - 1).ToString("X", CultureInfo.InvariantCulture)).Append(' ').Append(row.Name).Append("\r\n");
            if (text.Length > 16 * 1024 * 1024) throw new IOException("Hue name list exceeds 16 MiB.");
        }
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text.ToString());
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Hue name list exceeds 16 MiB."); return bytes;
    }
    internal static void Write(string path, string install, byte[] bytes, Action guard)
    {
        string destination = EditorPaths.OutsideInstall(path, install); AssetActions.EnsureExportDestination(destination); guard();
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); guard(); EditorPaths.OutsideInstall(destination, install); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void Export(HueDocument document, string path, HueTextFormat format)
    {
        string token = document.ReviewToken;
        void Guard() { if (document.Pending || document.RecoveryError != null || document.SourceError.Length > 0 || document.ReviewToken != token)
            throw new IOException(document.RecoveryError ?? (document.SourceError.Length > 0 ? document.SourceError : "Hue working fields changed or are pending. Commit or undo them, then export again.")); }
        Guard(); byte[] bytes = Encode(new(document.Name, document.Start, document.End, document.Colors()), format); Write(path, document.Install, bytes, Guard);
    }
}

internal sealed class HueTextReviewPlan
{
    private readonly HueDocument _document;
    private readonly byte[] _bytes;
    private readonly string _token;
    private HueTextEntry _candidate;
    internal HueTextFormat Format { get; private set; }
    internal string Error { get; private set; }
    internal HueTextEntry Candidate => _candidate == null ? null : new(_candidate.Name, _candidate.Start, _candidate.End, (ushort[])_candidate.Colors.Clone());
    internal string ReadinessError => Error.Length > 0 ? Error : _document.RecoveryError ?? (_document.SourceError.Length > 0 ? _document.SourceError
        : _document.Pending ? "Commit or undo pending hue fields before importing." : _document.ReviewToken != _token ? "The working hue changed. Close this review and choose the text again." : "");
    internal bool CanApply => _candidate != null && ReadinessError.Length == 0;
    internal HueTextReviewPlan(HueDocument document, byte[] bytes, HueTextFormat format)
    {
        if (bytes == null || bytes.Length > HueText.MaxBytes) throw new IOException("Hue text must fit 64 KiB.");
        _document = document; _bytes = (byte[])bytes.Clone(); _token = document.ReviewToken; Choose(format);
    }
    internal void Choose(HueTextFormat format)
    {
        Format = format; _candidate = null;
        try { _candidate = HueText.Decode(_bytes, format); Error = ""; } catch (Exception e) { Error = e.Message; }
    }
    internal void Apply()
    {
        if (!CanApply) throw new IOException(ReadinessError.Length > 0 ? ReadinessError : "Choose valid hue text before importing.");
        _document.ImportText(HueText.Decode(_bytes, Format), _token);
    }
}
#endif
