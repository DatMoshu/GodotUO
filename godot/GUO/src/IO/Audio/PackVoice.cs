// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Configuration;

namespace GUO.IO.Audio;

/// <summary>Scene-thread recorded clip playback. Missing lines remain silent; never synthesize.</summary>
internal static class PackVoice
{
    private static AudioStreamPlayer _player;
    public static int Played { get; private set; }
    internal static AudioStreamWav Stream(byte[] pcm) => new() {
        Data = pcm, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = 22050, Stereo = false };

    public static bool Speak(string name, ushort body, string text, int distance)
    {
        var profile = ProfileManager.CurrentProfile;
        if (profile?.ContentAudioEnabled != true || !profile.EnableSound || distance > 20) return false;
        var content = Client.Game?.UO?.FileManager?.Content?.Audio;
        if (content == null || !content.TryVoice(name, body, text, out var pcm)) return false;
        if (_player == null || !GodotObject.IsInstanceValid(_player)) _player = AudioHost.CreatePlayer();
        if (_player == null) return true; // A match must not fall through to generation.
        var previous = _player.Stream;
        _player.Stream = Stream(pcm);
        previous?.Dispose();
        float volume = Math.Clamp(profile.SoundVolume / 250f, 0f, 1f) * VoiceManager.VolumeFor(distance);
        _player.VolumeDb = volume <= 0 ? -80f : 20f * MathF.Log10(volume);
        _player.Play();
        Played++;
        return true;
    }

    public static void Update(Game.World world)
    {
        if (world?.InGame == true && ProfileManager.CurrentProfile?.ContentAudioEnabled == true
            && ProfileManager.CurrentProfile.EnableSound) return;
        if (_player != null && GodotObject.IsInstanceValid(_player))
        {
            _player.Stop();
            var stream = _player.Stream;
            _player.Stream = null;
            stream?.Dispose();
        }
    }
}
