// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Store;

public partial class StoreAudioProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "guo-audio-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var profile = new Profile();
            StorePack.Require(!profile.ContentAudioEnabled, "New profile audio must be off");
            var old = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(!old.ContentAudioEnabled, "Existing profile audio must stay off");
            profile.ContentAudioEnabled = true;
            string saved = JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(JsonSerializer.Deserialize(saved, ProfileJsonContext.DefaultToUse.Profile).ContentAudioEnabled, "Audio opt-in must persist");
            StorePack.Require(!IO.Audio.PackVoice.Speak("Test", 400, "hello", 0), "No profile must not play audio");

            byte[] wave = Wave();
            var components = new List<StoreComponent> {
                new() { Id = "music", Type = "music", Target = "client", Entry = "music.wav" },
                new() { Id = "sound", Type = "sound", Target = "client", Entry = "sound.wav" },
                new() { Id = "body", Type = "voice", Target = "client", Entry = "body.json", Body = 400, Wav = new() { "body.wav" } },
                new() { Id = "named", Type = "voice", Target = "client", Entry = "named.json", Name = "Test NPC", Wav = new() { "named.wav" } },
                new() { Id = "region", Type = "region", Target = "client", Entry = "region.json", References = new() { "audio-test:music", "audio-test:sound" } }
            };
            var payload = new Dictionary<string, byte[]> {
                ["music.wav"] = wave, ["sound.wav"] = wave, ["body.wav"] = wave, ["named.wav"] = Wave(2),
                ["body.json"] = Encoding.UTF8.GetBytes("{\"schema\":\"guo/voice@1\",\"clips\":{\"hello\":\"body.wav\"}}"),
                ["named.json"] = Encoding.UTF8.GetBytes("{\"schema\":\"guo/voice@1\",\"clips\":{\"hello\":\"named.wav\"}}"),
                ["region.json"] = Encoding.UTF8.GetBytes("{\"name\":\"Test region\",\"facet\":0,\"priority\":50,\"areas\":[{\"x\":10,\"y\":20,\"z\":-5,\"width\":4,\"height\":3,\"depth\":10}],\"music\":[\"audio-test:music\"],\"sound\":[\"audio-test:sound\"]}"),
                ["preview.png"] = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==")
            };
            var manifest = new StoreManifest { Schema = "guo/store-pack@2", Id = "audio-test", Version = "1.0.0", Kind = "content",
                Title = "Synthetic audio test", Author = "GUO tests", Licence = "CC0-1.0", MinProfileVersion = 11, Preview = "preview.png",
                Target = "client", Dependencies = new(), Components = components,
                Files = payload.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value)).ToLowerInvariant()) };
            string zip = Path.Combine(temporary, "fixture.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var (entry, data) in payload.Append(new KeyValuePair<string, byte[]>("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest))))
                { using var stream = archive.CreateEntry(entry).Open(); stream.Write(data); }
            }
            string installed = Path.Combine(temporary, "store", manifest.Id, manifest.Version);
            Directory.CreateDirectory(installed);
            StorePack.Extract(zip, installed);
            using var store = new StoreClient("http://127.0.0.1:18865", Path.Combine(temporary, "store"), int.MaxValue);
            var snapshot = store.VerifyContent(manifest.Id, manifest.Version);
            var contentLock = new StoreContentLock { Pack = manifest.Id, Version = manifest.Version, IdentityHash = snapshot.IdentityHash,
                Bindings = new() { ["audio-test:music"] = new() { Type = "music", Id = 1 }, ["audio-test:sound"] = new() { Type = "sound", Id = 1 } } };
            using var content = StoreRuntimeContent.Load(null, "enu", contentLock, store, apply: false);
            StorePack.Require(content.Audio.Music.Count == 1 && content.Audio.Sound.Count == 1, "Independent layers not mounted");
            var region = content.Audio.Music[0];
            StorePack.Require(region.Contains(0, 10, 20, -5) && region.Contains(0, 13, 22, 4)
                && !region.Contains(1, 10, 20, 0) && !region.Contains(0, 14, 20, 0) && !region.Contains(0, 10, 20, 5), "Region boundaries wrong");
            StorePack.Require(content.Audio.TryVoice("test npc", 400, " hello ", out var named) && named.Length == 4, "Named clip must override body");
            StorePack.Require(content.Audio.TryVoice("Other", 400, "hello", out var body) && body.Length == 2
                && !content.Audio.TryVoice("Other", 401, "hello", out _) && !content.Audio.TryVoice("Other", 400, "missing", out _), "Body binding or silent fallback wrong");
            using var streamAudio = IO.Audio.PackVoice.Stream(body);
            StorePack.Require(streamAudio.MixRate == 22050 && !streamAudio.Stereo && streamAudio.Data.SequenceEqual(body), "Playback stream format wrong");
            Refuses(() => StoreRuntimeContent.DecodeWave(new byte[44]), "Malformed WAV accepted");
            Refuses(() => new StoreAudioContent().AddVoice(components[2], Encoding.UTF8.GetBytes("{\"schema\":\"guo/voice@1\",\"clips\":{\"hello\":\"undeclared.wav\"}}"), _ => body), "Undeclared voice WAV accepted");
            components[4].References.Clear();
            Refuses(() => new StoreAudioContent().AddRegion("test:region", components[4], payload["region.json"]), "Undeclared region reference accepted");
            GD.Print("[audio packs] PASS: installed PCM, independent region layers/bounds, named/body voice, default-off persistence and malformed refusal");
            return true;
        }
        catch (Exception ex) { GD.PrintErr("[audio packs] FAIL: " + ex); return false; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static void Refuses(Action action, string message)
    { bool refused = false; try { action(); } catch (InvalidDataException) { refused = true; } StorePack.Require(refused, message); }

    private static byte[] Wave(int samples = 1)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(22050); writer.Write(44100); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); writer.Write(new byte[samples * 2]);
        return memory.ToArray();
    }
}
