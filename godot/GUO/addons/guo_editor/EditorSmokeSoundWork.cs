#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using GUO.Assets;
using GUO.IO;

public partial class EditorSmoke
{
    private static UOFile SoundArchive(SoundsLoader loader) =>
        (UOFile)typeof(SoundsLoader).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(loader);

    private void MeasureSoundMetadataWork()
    {
        if (!_data.IsLoaded) return;
        CheckSyntheticSoundMetadata();
        var passes = new List<object>();
        _report["sound_metadata_work"] = passes;
        ulong? firstSignature = null;
        for (int pass = 0; pass < 2; pass++)
        {
            int attempted = 0, found = 0, nonempty = 0, packReferences = 0, largest = 0, parityChecks = 0;
            long returnedBytes = 0, callAllocated = 0, callTicks = 0;
            ulong signature = 14695981039346656037UL;
            void Mix(ulong value) { unchecked { signature = (signature ^ value) * 1099511628211UL; } }
            for (int id = 0; id < 0x1000 && returnedBytes < 512L * 1024 * 1024; id++)
            {
                bool originalFound = false;
                string originalName = null;
                int originalLength = 0;
                long originalPosition = 0, originalStreamPosition = 0;
                UOFile archive = SoundArchive(_data.Files.Sounds);
                if (pass == 0)
                {
                    originalFound = _data.Files.Sounds.TryGetSound(id, out byte[] original, out originalName);
                    originalLength = original?.Length ?? 0;
                    originalPosition = archive.Position;
                    originalStreamPosition = archive.Reader.BaseStream.Position;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                bool exists = _data.Files.Sounds.TryGetSoundMetadata(id, out string name, out int length);
                callTicks += Stopwatch.GetTimestamp() - start;
                callAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (pass == 0)
                {
                    bool same = exists == originalFound && name == originalName && length == originalLength
                        && archive.Position == originalPosition && archive.Reader.BaseStream.Position == originalStreamPosition;
                    if (!same) _failures.Add("Sound metadata mismatch at ID " + id);
                    else parityChecks++;
                }
                attempted++;
                Mix((ulong)id);
                Mix(exists ? 1UL : 0UL);
                if (!exists) continue;
                found++;
                if (length > 0) nonempty++;
                returnedBytes += length;
                largest = Math.Max(largest, length);
                Mix((ulong)length);
                if (name != null) foreach (char c in name) Mix(c);
                if (_data.Files.Content != null && _data.Files.Content.TrySound(id, out _)) packReferences++;
            }
            bool complete = attempted == 0x1000;
            bool sameMetadata = !firstSignature.HasValue || signature == firstSignature.Value;
            firstSignature ??= signature;
            passes.Add(new Dictionary<string, object>
            {
                ["pass"] = pass, ["attempted"] = attempted, ["found"] = found,
                ["nonempty"] = nonempty, ["described_body_bytes"] = returnedBytes,
                ["call_allocated_bytes"] = callAllocated, ["call_ms"] = callTicks * 1000.0 / Stopwatch.Frequency,
                ["pack_entries"] = packReferences, ["largest_body_bytes"] = largest,
                ["original_parity_checks"] = parityChecks,
                ["metadata_signature"] = signature.ToString("X16"), ["same_metadata"] = sameMetadata,
                ["complete"] = complete,
            });
            if (!sameMetadata) _failures.Add("Sound metadata measurement changed between consecutive scans");
        }
    }
}
#endif
