// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GUO.IO.Audio
{
    /// <summary>
    /// Location-based audio beds: tile rectangles with a playlist of
    /// generated tracks. Two independent layers share the engine (and the
    /// ComfyUI generation): music (ambient tracks, replacing the base music
    /// with a fade) and sfx (ambient beds over the base sounds, never
    /// ducking them). Each layer has its own player, manifest and fade, so
    /// neither cancels the other.
    /// </summary>
    internal static class ZoneAudio
    {
        public sealed class Zone
        {
            public string Name = "";
            public int Facet;
            public int X0, Y0, X1, Y1;
            public List<string> Tracks = new();

            public bool Contains(int facet, int x, int y) =>
                (Facet < 0 || Facet == facet)
                && x >= Math.Min(X0, X1) && x <= Math.Max(X0, X1)
                && y >= Math.Min(Y0, Y1) && y <= Math.Max(Y0, Y1);
        }

        public static string MusicDir()
        {
            string dir = Environment.GetEnvironmentVariable("GUO_MUSIC_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GUO", "music");
        }

        public static string TracksDir()
        {
            return Path.Combine(MusicDir(), "tracks");
        }

        internal sealed class Layer
        {
            private readonly string _manifest;
            private readonly string _stem;
            private readonly Func<Configuration.Profile, float> _volumeOf;
            private readonly Func<Configuration.Profile, bool> _enabledOf;
            private readonly bool _duckBase;

            private List<Zone> _zones;
            private Godot.AudioStreamPlayer _player;
            private string _currentZone;
            private int _trackIndex;
            private float _audible;
            private DateTime _lastTick = DateTime.UtcNow;

            /// <summary>Seconds for a full fade in or out (music lingers, sfx leaves faster).</summary>
            private readonly float _fadeSeconds;

            public Layer(string manifest, string stem,
                Func<Configuration.Profile, float> volumeOf,
                Func<Configuration.Profile, bool> enabledOf,
                bool duckBase,
                float fadeSeconds)
            {
                _manifest = manifest;
                _stem = stem;
                _volumeOf = volumeOf;
                _enabledOf = enabledOf;
                _duckBase = duckBase;
                _fadeSeconds = fadeSeconds <= 0f ? 2f : fadeSeconds;
            }

            public string Manifest => _manifest;
            public string Stem => _stem;
            public string Status { get; private set; } = "idle";

            public List<Zone> Zones
            {
                get
                {
                    _zones ??= Load();
                    return _zones;
                }
            }

            private List<Zone> Load()
            {
                var zones = new List<Zone>();
                try
                {
                    string path = Path.Combine(MusicDir(), _manifest + ".json");
                    if (!File.Exists(path))
                    {
                        return zones;
                    }

                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("zones", out JsonElement list))
                    {
                        return zones;
                    }

                    foreach (JsonElement z in list.EnumerateArray())
                    {
                        var zone = new Zone
                        {
                            Name = z.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                            Facet = z.TryGetProperty("facet", out JsonElement f) ? f.GetInt32() : -1,
                            X0 = z.TryGetProperty("x0", out JsonElement x0) ? x0.GetInt32() : 0,
                            Y0 = z.TryGetProperty("y0", out JsonElement y0) ? y0.GetInt32() : 0,
                            X1 = z.TryGetProperty("x1", out JsonElement x1) ? x1.GetInt32() : 0,
                            Y1 = z.TryGetProperty("y1", out JsonElement y1) ? y1.GetInt32() : 0,
                        };
                        if (z.TryGetProperty("tracks", out JsonElement tracks))
                        {
                            foreach (JsonElement t in tracks.EnumerateArray())
                            {
                                string file = t.GetString();
                                if (!string.IsNullOrWhiteSpace(file))
                                {
                                    zone.Tracks.Add(file);
                                }
                            }
                        }

                        zones.Add(zone);
                    }
                }
                catch (Exception)
                {
                }

                return zones;
            }

            public bool Save(List<Zone> zones)
            {
                try
                {
                    Directory.CreateDirectory(MusicDir());
                    using var stream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("format", 1);
                        writer.WriteStartArray("zones");
                        foreach (Zone z in zones)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", z.Name);
                            writer.WriteNumber("facet", z.Facet);
                            writer.WriteNumber("x0", z.X0);
                            writer.WriteNumber("y0", z.Y0);
                            writer.WriteNumber("x1", z.X1);
                            writer.WriteNumber("y1", z.Y1);
                            writer.WriteStartArray("tracks");
                            foreach (string t in z.Tracks)
                            {
                                writer.WriteStringValue(t);
                            }

                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    File.WriteAllText(Path.Combine(MusicDir(), _manifest + ".json"),
                        System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
                    _zones = zones;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>Staged tracks for this layer (its filename stem).</summary>
            public List<string> TrackFiles()
            {
                var files = new List<string>();
                try
                {
                    string dir = TracksDir();
                    if (!Directory.Exists(dir))
                    {
                        return files;
                    }

                    foreach (string path in Directory.EnumerateFiles(dir, _stem + "_*.*"))
                    {
                        string name = Path.GetFileName(path);
                        if (name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(name);
                        }
                    }

                    files.Sort(StringComparer.Ordinal);
                }
                catch (Exception)
                {
                }

                return files;
            }

            public string StemFor(string zoneName)
            {
                string stem = string.Concat((zoneName ?? "").ToLowerInvariant().Where(
                    c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')));
                return string.IsNullOrEmpty(stem) ? _stem : stem;
            }

            private void EnsurePlayer()
            {
                if (_player == null || !Godot.GodotObject.IsInstanceValid(_player))
                {
                    _player = AudioHost.CreatePlayer();
                }
            }

            private static float DbFor(float vol) =>
                vol >= 1f ? 0f : 20f * MathF.Log10(Math.Max(0.001f, vol));

            private void DuckBase(float audible)
            {
                if (!_duckBase)
                {
                    return;
                }

                try
                {
                    var audio = Client.Game?.Audio;
                    if (audio != null)
                    {
                        audio.SetMusicDuck(1f - audible);
                    }
                }
                catch (Exception)
                {
                }
            }

            /// <summary>Per-frame tick from the scene update: zone lookup, fades, advance, stop.</summary>
            public void Update(Game.World world)
            {
                List<Zone> zones = Zones;

                Game.GameObjects.PlayerMobile player = world?.Player;
                if (player == null || !world.InGame)
                {
                    if (_currentZone != null || _audible > 0f)
                    {
                        Stop();
                        DuckBase(0f);
                        Status = "idle";
                    }

                    _audible = 0f;
                    _lastTick = DateTime.UtcNow;
                    return;
                }

                Zone inside = null;
                foreach (Zone z in zones)
                {
                    if (z.Tracks.Count > 0 && z.Contains(world.MapIndex, player.X, player.Y))
                    {
                        inside = z;
                        break;
                    }
                }

                var profile = Configuration.ProfileManager.CurrentProfile;
                if (profile == null || !_enabledOf(profile))
                {
                    if (_currentZone != null || _audible > 0f)
                    {
                        Stop();
                        DuckBase(0f);
                        Status = "idle (off)";
                    }

                    _audible = 0f;
                    _lastTick = DateTime.UtcNow;
                    return;
                }

                DateTime now = DateTime.UtcNow;
                float dt = (float)(now - _lastTick).TotalSeconds;
                _lastTick = now;
                if (dt < 0f || dt > 1f)
                {
                    dt = 0f;
                }

                // A new zone (or none) takes over: swap the track, then ramp.
                if (_currentZone != inside?.Name)
                {
                    _currentZone = inside?.Name;
                    _trackIndex = 0;
                    _audible = 0f;
                    if (inside != null)
                    {
                        PlayTrack(inside, -60f);
                        Status = $"fading in {inside.Name}/{inside.Tracks[_trackIndex]}";
                    }
                }

                float target = inside != null ? 1f : 0f;
                if (Math.Abs(_audible - target) > 0.001f)
                {
                    _audible += Math.Sign(target - _audible) * (dt / _fadeSeconds);
                    _audible = Math.Clamp(_audible, 0f, 1f);
                    ApplyLevels(profile);
                    if (inside != null && target > 0f && _audible < target)
                    {
                        Status = $"fading in {inside.Name}/{inside.Tracks[_trackIndex]}";
                    }
                    else if (target <= 0f && _audible > 0f)
                    {
                        Status = "fading out";
                    }
                }

                if (inside == null)
                {
                    if (_audible <= 0f && _currentZone != null)
                    {
                        Stop();
                        Status = "idle";
                    }

                    return;
                }

                if (_player != null && !_player.Playing && _audible >= 1f)
                {
                    _trackIndex = (_trackIndex + 1) % inside.Tracks.Count;
                    PlayTrack(inside, DbFor(_volumeOf(profile)));
                    Status = $"playing {inside.Name}/{inside.Tracks[_trackIndex]}";
                }
                else if (_audible >= 1f && Status.StartsWith("fading in"))
                {
                    Status = $"playing {inside.Name}/{inside.Tracks[_trackIndex]}";
                }

                // The base game can restart its music mid-zone; keep it ducked.
                if (_audible > 0f)
                {
                    DuckBase(_audible);
                }
            }

            /// <summary>Push the ramp to both players: ours up, the base game's down.</summary>
            private void ApplyLevels(Configuration.Profile profile)
            {
                float vol = _volumeOf(profile) * _audible;
                try
                {
                    if (_player != null)
                    {
                        _player.VolumeDb = DbFor(vol);
                    }
                }
                catch (Exception)
                {
                }

                DuckBase(_audible);
            }

            private void PlayTrack(Zone zone, float startDb)
            {
                for (int i = 0; i < zone.Tracks.Count; i++)
                {
                    int at = (_trackIndex + i) % zone.Tracks.Count;
                    string path = Path.Combine(TracksDir(), zone.Tracks[at]);
                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(path);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    try
                    {
                        EnsurePlayer();
                        if (_player == null)
                        {
                            Status = "no audio host";
                            return;
                        }

                        Godot.AudioStream stream = zone.Tracks[at].EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                            ? (Godot.AudioStream)new Godot.AudioStreamWav { Data = bytes }
                            : new Godot.AudioStreamMP3 { Data = bytes };
                        _player.Stream = stream;
                        _player.VolumeDb = startDb;
                        _player.Play();
                        _trackIndex = at;
                        Status = $"playing {zone.Name}/{zone.Tracks[at]}";
                        return;
                    }
                    catch (Exception ex)
                    {
                        Status = $"unplayable {zone.Tracks[at]}: {ex.Message}";
                    }
                }

                Stop();
                Status = $"idle ({zone.Name}: no playable tracks)";
            }

            private void Stop()
            {
                try
                {
                    if (_player != null)
                    {
                        _player.Stop();
                    }
                }
                catch (Exception)
                {
                }

                _currentZone = null;
            }
        }

        public static readonly Layer Music = new Layer(
            "music", "track",
            p => p.MusicVolume / 250f,
            p => p.EnableMusic,
            duckBase: true,
            fadeSeconds: 2.5f);

        public static readonly Layer Sfx = new Layer(
            "sfx", "sfx",
            p => p.SoundVolume / 250f,
            p => p.EnableSound,
            duckBase: false,
            fadeSeconds: 1.5f);
    }

    /// <summary>Ambient music zones (see <see cref="ZoneAudio"/> layer Music).</summary>
    internal static class MusicZones
    {
        public static List<ZoneAudio.Zone> Zones => ZoneAudio.Music.Zones;
        public static string Status => ZoneAudio.Music.Status;
        public static bool Save(List<ZoneAudio.Zone> zones) => ZoneAudio.Music.Save(zones);
        public static List<string> TrackFiles() => ZoneAudio.Music.TrackFiles();
        public static string MusicDir() => ZoneAudio.MusicDir();
        public static string TracksDir() => ZoneAudio.TracksDir();
        public static void Update(Game.World world) => ZoneAudio.Music.Update(world);
    }

    /// <summary>Ambient sfx zones (see <see cref="ZoneAudio"/> layer Sfx).</summary>
    internal static class SfxZones
    {
        public static List<ZoneAudio.Zone> Zones => ZoneAudio.Sfx.Zones;
        public static string Status => ZoneAudio.Sfx.Status;
        public static bool Save(List<ZoneAudio.Zone> zones) => ZoneAudio.Sfx.Save(zones);
        public static List<string> TrackFiles() => ZoneAudio.Sfx.TrackFiles();
        public static void Update(Game.World world) => ZoneAudio.Sfx.Update(world);
    }
}
