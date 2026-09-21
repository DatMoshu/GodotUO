// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using System;
using static System.String;

namespace GUO.IO.Audio
{
    /// <summary>
    ///     A playable UO sound or music track.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         PORT DEVIATION — see ADR-0005. The public surface is upstream's, line
    ///         for line: the <see cref="_lastPlayedTime" /> spam gate, the volume
    ///         clamp, <c>Max(value - VolumeFactor, 0)</c>, and the order of
    ///         operations in <see cref="Play" /> and <see cref="Stop" />. Only what
    ///         they operate on changed.
    ///     </para>
    ///     <para>
    ///         Upstream's <c>SoundInstance</c> is an FNA
    ///         <c>DynamicSoundEffectInstance</c>: the subclass hands over a PCM
    ///         buffer through <c>GetBuffer()</c> and refills it on a
    ///         <c>BufferNeeded</c> event. Godot has no such type, and it already
    ///         does both jobs that protocol exists for — a complete buffer is an
    ///         <c>AudioStreamWav</c>, and a streamed MP3 is an
    ///         <c>AudioStreamMP3</c>. So the pair becomes one abstract
    ///         <see cref="GetStream" />, and <c>Channels</c> and <c>Frequency</c>
    ///         move down to <see cref="UOSound" />, the only thing that still has to
    ///         know the PCM format.
    ///     </para>
    /// </remarks>
    public abstract class Sound : IComparable<Sound>, IDisposable
    {
        private uint _lastPlayedTime;
        private string m_Name;
        private float m_volume = 1.0f;
        private float m_volumeFactor;


        protected Sound(string name, int index)
        {
            Name = name;
            Index = index;
        }

        public string Name
        {
            get => m_Name;
            private set
            {
                if (!IsNullOrEmpty(value))
                {
                    m_Name = value.Replace(".mp3", "");
                }
                else
                {
                    m_Name = Empty;
                }
            }
        }

        public int Index { get; }
        public double DurationTime { get; private set; }

        public float Volume
        {
            get => m_volume;
            set
            {
                if (value < 0.0f)
                {
                    value = 0f;
                }
                else if (value > 1f)
                {
                    value = 1f;
                }

                m_volume = value;

                float instanceVolume = Math.Max(value - VolumeFactor, 0.0f);

                if (Alive)
                {
                    // VolumeLinear, not VolumeDb: upstream's volume is a linear
                    // 0..1 and audio_probe.bat confirms this property round-trips
                    // one. Going through decibels would need a conversion here and
                    // its inverse nowhere.
                    SoundInstance.VolumeLinear = instanceVolume;
                }
            }
        }

        public float VolumeFactor
        {
            get => m_volumeFactor;
            set
            {
                m_volumeFactor = value;
                Volume = m_volume;
            }
        }

        public bool IsPlaying(uint curTime) => Alive && SoundInstance.Playing && DurationTime > curTime;

        public int CompareTo(Sound other)
        {
            return other == null ? -1 : Index.CompareTo(other.Index);
        }

        public void Dispose()
        {
            if (SoundInstance != null)
            {
                if (Alive)
                {
                    SoundInstance.Stop();
                    SoundInstance.QueueFree();
                }

                SoundInstance = null;
            }
        }

        protected AudioStreamPlayer SoundInstance;
        protected uint Delay = 250;

        /// <summary>
        ///     Replaces upstream's <c>GetBuffer()</c>. Returns null when there is
        ///     nothing to play, which is what an empty <c>ArraySegment</c> meant.
        /// </summary>
        protected abstract AudioStream GetStream();

        protected virtual void AfterStop()
        {
        }

        protected virtual void BeforePlay()
        {
        }

        /// <summary>
        ///     Whether the player exists and Godot has not freed it. Stands in for
        ///     upstream's <c>SoundInstance != null &amp;&amp;
        ///     !SoundInstance.IsDisposed</c>.
        /// </summary>
        private bool Alive => GodotObject.IsInstanceValid(SoundInstance);

        /// <summary>
        ///     Plays the effect.
        /// </summary>
        /// <param name="asEffect">Set to false for music, true for sound effects.</param>
        public bool Play(uint curTime, float volume = 1.0f, float volumeFactor = 0.0f, bool spamCheck = false)
        {
            if (_lastPlayedTime > curTime)
            {
                return false;
            }

            BeforePlay();

            if (Alive)
            {
                SoundInstance.Stop();
            }
            else
            {
                SoundInstance = AudioHost.CreatePlayer();

                if (SoundInstance == null)
                {
                    return false;
                }
            }

            AudioStream stream = GetStream();

            if (stream != null)
            {
                _lastPlayedTime = curTime + Delay;

                SoundInstance.Stream = stream;
                VolumeFactor = volumeFactor;
                Volume = volume;

                // Upstream asks the instance for the duration of the buffer it just
                // submitted. The stream knows its own length, in seconds.
                DurationTime = curTime + stream.GetLength() * 1000.0;

                SoundInstance.Play();

                return true;
            }

            return false;
        }

        public void Stop()
        {
            if (Alive)
            {
                SoundInstance.Stop();
            }

            AfterStop();
        }
    }
}
