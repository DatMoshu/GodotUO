#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal sealed record SoundExportRead(string Name, byte[] Pcm, string SourceBinding);
internal sealed record SoundExportRow(int Id, string Name, int Samples, string PcmHash, string SourceBinding);
internal enum SoundExportFormat { Utf8Csv, FiddlerCsv, WaveFolder }

/// <summary>Small immutable review metadata; audio is read and checked one effect at a time.</summary>
internal sealed class SoundExportPlan
{
    private readonly Func<int, SoundExportRead> _read;
    internal IReadOnlyList<SoundExportRow> Rows { get; }
    private static string Hash(byte[] pcm) => Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant();
    internal SoundExportPlan(IEnumerable<int> ids, Func<int, SoundExportRead> read)
    {
        _read = read; int[] selection = ids.ToArray();
        if (selection.Length == 0 || selection.Length > 0x10000 || selection.Distinct().Count() != selection.Length || selection.Any(id => id < 0 || id >= 0xFFFF))
            throw new IOException("Choose a nonempty set of distinct sound effect IDs.");
        Rows = selection.Select(id =>
        {
            SoundExportRead state = read(id); SoundWave.ValidateLength(state.Pcm?.Length ?? 0);
            if (state.Name == null || state.Name.Contains('\0') || state.SourceBinding == null) throw new IOException($"Sound 0x{id:X4} has invalid export metadata.");
            return new SoundExportRow(id, state.Name, state.Pcm.Length / 2, Hash(state.Pcm), state.SourceBinding);
        }).ToArray();
    }
    internal byte[] ReadVerified(int index)
    {
        SoundExportRow row = Rows[index]; SoundExportRead current = _read(row.Id);
        SoundWave.ValidateLength(current.Pcm?.Length ?? 0);
        if (current.Name != row.Name || current.SourceBinding != row.SourceBinding || Hash(current.Pcm) != row.PcmHash)
            throw new IOException($"Sound 0x{row.Id:X4} changed after review. Close and reopen the export review.");
        return current.Pcm;
    }
    private static string CsvField(string value) => value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    internal byte[] Csv(bool legacy, int idOffset)
    {
        if (idOffset is < 0 or > 1) throw new IOException("CSV sound ID offset must be native (0) or POL (+1).");
        var text = new StringBuilder("ID;Name;Length\r\n");
        foreach (var row in Rows)
            text.Append($"0x{row.Id + idOffset:X3};").Append(CsvField(row.Name)).Append(';')
                .Append((row.Samples / (double)SoundWave.Frequency).ToString("F2", legacy ? CultureInfo.CurrentCulture : CultureInfo.InvariantCulture)).Append("\r\n");
        if (!legacy) return new UTF8Encoding(false, true).GetBytes(text.ToString());
        var encoding = (Encoding)CodePagesEncodingProvider.Instance.GetEncoding(1252).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try { return encoding.GetBytes(text.ToString()); }
        catch (EncoderFallbackException) { throw new IOException("A working name cannot be represented in Windows-1252. Choose UTF-8 CSV to preserve it."); }
    }
    internal string[] FileNames(bool includeIds)
    {
        string[] names = Rows.Select(row => FileName(row.Id, row.Name, includeIds)).ToArray();
        var collision = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (collision != null) throw new IOException($"Output filename '{collision.Key}' collides. Enable ID prefixes or rename the working copies.");
        return names;
    }
    internal static string FileName(int id, string name, bool includeId)
    {
        string safe = new((name ?? "").Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim().TrimEnd('.', ' ');
        if (safe.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) safe = safe[..^4].TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "sound";
        string device = safe.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && "123456789¹²³".Contains(device[3]))) safe = "_" + safe;
        return (includeId ? $"0x{id:X4} " : "") + safe + ".wav";
    }
}

/// <summary>Incremental standard-file export. A WAV folder appears only when every entry succeeds.</summary>
internal sealed class SoundExportJob : IDisposable
{
    private readonly SoundExportPlan _plan;
    private readonly SoundExportFormat _format;
    private readonly string _destination, _temporary;
    private readonly string[] _names;
    private readonly byte[] _csv;
    private readonly List<string> _written = new();
    internal int CompletedRows { get; private set; }
    internal bool Finished { get; private set; }
    internal bool Succeeded { get; private set; }
    internal string Error { get; private set; }
    internal SoundExportJob(SoundExportPlan plan, SoundExportFormat format, string destination, bool includeIds, int idOffset)
    {
        _plan = plan; _format = format;
        if (!Enum.IsDefined(format)) throw new IOException("Choose a supported sound export format.");
        if (string.IsNullOrWhiteSpace(destination)) throw new IOException("Choose an export destination first.");
        _destination = Path.GetFullPath(destination); AssetActions.EnsureExportDestination(_destination);
        string parent = Path.GetDirectoryName(_destination);
        if (!Directory.Exists(parent)) throw new IOException("Choose an existing parent folder for the export.");
        _csv = plan.Csv(format == SoundExportFormat.FiddlerCsv, format == SoundExportFormat.WaveFolder ? 0 : idOffset);
        if (format == SoundExportFormat.WaveFolder)
        {
            _names = plan.FileNames(includeIds);
            if (Directory.Exists(_destination) || File.Exists(_destination)) throw new IOException("WAV batch destination already exists. Choose a new folder; existing exports are not replaced.");
            _temporary = Path.Combine(parent, ".guo-sounds-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_temporary);
        }
        else
        {
            if (Directory.Exists(_destination)) throw new IOException("Choose a CSV filename, not a directory.");
            _temporary = _destination + ".tmp-" + Guid.NewGuid().ToString("N");
        }
    }
    internal void Step()
    {
        if (Finished) return;
        try
        {
            byte[] pcm = _plan.ReadVerified(CompletedRows);
            if (_format == SoundExportFormat.WaveFolder)
            {
                string path = Path.Combine(_temporary, _names[CompletedRows]); _written.Add(path); File.WriteAllBytes(path, SoundWave.Encode(pcm));
            }
            CompletedRows++;
            if (CompletedRows != _plan.Rows.Count) return;
            AssetActions.EnsureExportDestination(_destination);
            if (_format == SoundExportFormat.WaveFolder)
            {
                string manifest = Path.Combine(_temporary, "SoundList.csv"); _written.Add(manifest); File.WriteAllBytes(manifest, _csv);
                Directory.Move(_temporary, _destination);
            }
            else { File.WriteAllBytes(_temporary, _csv); File.Move(_temporary, _destination, true); }
            Finished = Succeeded = true;
        }
        catch (Exception e) { Error = e.Message; Finished = true; Cleanup(); }
    }
    internal void Cancel()
    {
        if (Finished) return;
        Finished = true; Error = "Export canceled. No completed output was published."; Cleanup();
    }
    private void Cleanup()
    {
        try
        {
            if (_format == SoundExportFormat.WaveFolder)
            {
                // Delete only the exact files this job created; never recursively delete a directory.
                foreach (string path in _written) if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(_temporary)) Directory.Delete(_temporary, false);
            }
            else if (File.Exists(_temporary)) File.Delete(_temporary);
        }
        catch (Exception e) { Error += " Temporary export cleanup needs attention: " + e.Message; }
    }
    public void Dispose() => Cancel();
}
#endif
