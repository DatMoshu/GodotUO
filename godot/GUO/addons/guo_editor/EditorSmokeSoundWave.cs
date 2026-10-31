#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Godot;

public partial class EditorSmoke
{
    private void SoundWaveCheck(string name, bool ok)
    { _assetReport["sound_wave_" + name] = ok; if (!ok) AssetFail("Sound WAV: " + name); }

    private static bool SoundWaveRefused(Action action)
    { try { action(); return false; } catch (Exception e) when (e is ArgumentException or IOException) { return true; } }

    private bool IndependentWave(string wave, string pcm, bool expectValid = true)
    {
        const string script = "import pathlib,sys,wave; expected=pathlib.Path(sys.argv[2]).read_bytes(); f=wave.open(sys.argv[1],'rb'); assert (f.getnchannels(),f.getsampwidth(),f.getframerate(),f.getnframes(),f.getcomptype())==(1,2,22050,len(expected)//2,'NONE'); assert f.readframes(f.getnframes())==expected; f.close(); print('PCM exact')";
        var start = new ProcessStartInfo(EditorData.Setting("PYTHON", "python"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-c", script, wave, pcm }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start);
        if (!child.WaitForExit(10_000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); return false; }
        string output = child.StandardOutput.ReadToEnd(), error = child.StandardError.ReadToEnd();
        if (expectValid && child.ExitCode != 0) _assetReport["sound_wave_oracle_error"] = error;
        return expectValid ? child.ExitCode == 0 && output.Contains("PCM exact") : child.ExitCode != 0;
    }

    private void CheckSoundWaveExport()
    {
        string root = Path.Combine(_out, "sound-wave" + Suffix + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        byte[] pcm = { 0, 128, 255, 127, 0, 0, 255, 255, 1, 0, 0x34, 0x12 };
        byte[] original = (byte[])pcm.Clone(); string raw = Path.Combine(root, "fixture.pcm"), path = Path.Combine(root, "fixture.wav");
        File.WriteAllBytes(raw, pcm); SoundWave.Export(pcm, path);
        SoundWaveCheck("independent_pcm_exact", IndependentWave(path, raw) && pcm.AsSpan().SequenceEqual(original));
        var engine = AudioStreamWav.LoadFromFile(path);
        SoundWaveCheck("engine_duration_and_format", engine != null && !engine.Stereo && engine.MixRate == 22050
            && engine.Format == AudioStreamWav.FormatEnum.Format16Bits && Math.Abs(engine.GetLength() - pcm.Length / 2.0 / 22050) < 0.000001);
        engine?.Dispose();
        byte[] changed = File.ReadAllBytes(path); changed[24] ^= 1; string damaged = Path.Combine(root, "wrong-rate.wav"); File.WriteAllBytes(damaged, changed);
        SoundWaveCheck("oracle_catches_header_corruption", IndependentWave(damaged, raw, false));
        SoundWaveCheck("invalid_pcm_refused", SoundWaveRefused(() => SoundWave.Encode(null)) && SoundWaveRefused(() => SoundWave.Encode(Array.Empty<byte>()))
            && SoundWaveRefused(() => SoundWave.Encode(new byte[3])) && SoundWaveRefused(() => SoundWave.ValidateLength(SoundWave.MaximumBytes + 2)));
        byte[] prior = File.ReadAllBytes(path);
        SoundWaveCheck("invalid_export_preserves_existing_file", SoundWaveRefused(() => SoundWave.Export(new byte[3], path)) && File.ReadAllBytes(path).AsSpan().SequenceEqual(prior));
        string install = _data.ClientData;
        SoundWaveCheck("install_export_refused", SoundWaveRefused(() => SoundWave.Export(pcm, Path.Combine(install, "guo-sound-refusal.wav")))
            && !File.Exists(Path.Combine(install, "guo-sound-refusal.wav")));
        string target = Path.Combine(root, "backing.wav"), hardlink = Path.Combine(root, "output.wav"); File.WriteAllBytes(target, prior);
        var link = new ProcessStartInfo(EditorData.Setting("PYTHON", "python")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-c", "import os,sys; os.link(sys.argv[1],sys.argv[2])", target, hardlink }) link.ArgumentList.Add(argument);
        using (var child = Process.Start(link)) { if (!child.WaitForExit(10_000) || child.ExitCode != 0) throw new IOException("Could not create isolated WAV hardlink fixture."); }
        SoundWave.Export(new byte[] { 0, 0 }, hardlink);
        SoundWaveCheck("hardlink_backing_unchanged", File.ReadAllBytes(target).AsSpan().SequenceEqual(prior) && File.ReadAllBytes(hardlink).Length == 46);

        var panel = _assets.Panel<SoundPanel>(); _assets.ShowPanel(panel); panel.SelectKind(false);
        int id = panel.Search(panel.SmokeQuery) ?? -1;
        SoundWaveCheck("typed_effect_actions", id >= 0 && _inspector.Current.Actions.Exists(action => action.Id == "export-wav")
            && _inspector.Current.Actions.Count(action => action.Id is "play-sound" or "stop-sound") == 2);
        string Hash(string file) { using var source = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(source)); }
        var stamps = Directory.GetFiles(install).Where(file => Path.GetFileName(file).StartsWith("sound", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file, file => (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file), Hash(file)));
        _data.Files.Sounds.TryGetSound(id, out byte[] active, out _);
        string actualRaw = Path.Combine(root, "active.pcm"), actualWav = Path.Combine(root, "active.wav"); File.WriteAllBytes(actualRaw, active);
        var dialog = panel.OpenWavExport(id);
        SoundWaveCheck("native_wav_picker", dialog.GetParent() == panel && dialog.FileMode == EditorFileDialog.FileModeEnum.SaveFile
            && dialog.Filters.Length == 1 && dialog.Filters[0].Contains("*.wav"));
        dialog.EmitSignal(EditorFileDialog.SignalName.Canceled);
        SoundWaveCheck("picker_cancel_writes_nothing", !File.Exists(actualWav));
        dialog = panel.OpenWavExport(id); dialog.EmitSignal(EditorFileDialog.SignalName.FileSelected, actualWav);
        SoundWaveCheck("native_picker_export_pcm_exact", File.Exists(actualWav) && IndependentWave(actualWav, actualRaw));
        SoundWaveCheck("source_archives_unchanged", stamps.All(row => row.Value == (new FileInfo(row.Key).Length, File.GetLastWriteTimeUtc(row.Key), Hash(row.Key))));
        _assetReport["sound_wave_source_sha256"] = stamps.ToDictionary(row => Path.GetFileName(row.Key), row => row.Value.Item3);
        _assetReport["sound_wave_active_id"] = id; _assetReport["sound_wave_sample_count"] = active.Length / 2;
        _assetReport["sound_wave_pcm_sha256"] = Convert.ToHexString(SHA256.HashData(active)).ToLowerInvariant();
        panel.SelectKind(true);
        int? music = panel.Search(panel.SmokeQuery);
        if (music.HasValue) SoundWaveCheck("music_has_no_pcm_export", !_inspector.Current.Actions.Exists(action => action.Id == "export-wav"));
        else _assetReport["sound_wave_music_skip"] = "No music entry in this dataset.";
        panel.SelectKind(false); panel.Search(id.ToString());
    }
}
#endif
