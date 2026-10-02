// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Store;

internal sealed class StoreMusic : GUO.IO.Audio.Sound
{
    private readonly byte[] _pcm;
    private AudioStreamWav _stream;
    public StoreMusic(int id, byte[] pcm) : base("pack-music-" + id, id) { _pcm = pcm; Delay = 0; }
    protected override AudioStream GetStream() => _stream ??= new AudioStreamWav
    {
        Data = _pcm, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = 22050,
        Stereo = false, LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = _pcm.Length / 2
    };
}
