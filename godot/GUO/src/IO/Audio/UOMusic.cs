// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Utility.Logging;
using System;

namespace GUO.IO.Audio
{
    /// <summary>
    ///     A music track from the client's <c>Music</c> folder.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         PORT DEVIATION — see ADR-0005. Upstream decodes the MP3 itself with
    ///         MP3Sharp, reading 32 KB of PCM at a time and keeping three chunks
    ///         queued on an FNA <c>DynamicSoundEffectInstance</c>; <see cref="Update" />
    ///         is called every frame to do the topping up, and looping is a rewind
    ///         of the decoder. Godot decodes MP3, streams it and loops it, so all of
    ///         that is one <see cref="AudioStreamMP3" />, and MP3Sharp — 40 files of
    ///         vendored decoder — does not enter the port.
    ///     </para>
    ///     <para>
    ///         Two behaviours improve as a side effect, which is worth recording
    ///         because they are differences from the original client:
    ///         <c>DurationTime</c> becomes the length of the track rather than of
    ///         one chunk, and the loop seam is clean — upstream refills a partial
    ///         chunk from the rewound stream and submits it as though it were full.
    ///     </para>
    /// </remarks>
    public class UOMusic : Sound
    {
        private bool m_Playing;
        private readonly bool m_Repeat;
        private AudioStreamMP3 m_Stream;


        public UOMusic(int index, string name, bool loop, string fileName) : base(name, index)
        {
            m_Repeat = loop;
            m_Playing = false;
            Delay = 0;

            Path = fileName;
        }

        private string Path { get; }

        /// <summary>
        ///     Kept, and empty.
        /// </summary>
        /// <remarks>
        ///     Upstream's comment on this is <i>"sanity - if the buffer empties, we
        ///     will lose our sound effect"</i>, which is exactly the problem the
        ///     engine now owns. <c>AudioManager</c> calls this every frame and will
        ///     go on calling it after every upstream merge, so the method stays
        ///     rather than making that a compile error to rediscover each time.
        /// </remarks>
        public void Update()
        {
        }

        protected override AudioStream GetStream()
        {
            return m_Playing ? m_Stream : null;
        }

        protected override void BeforePlay()
        {
            if (m_Playing)
            {
                Stop();
            }

            try
            {
                m_Stream = null;

                // System.IO, not AudioStreamMP3.LoadFromFile: the music lives in
                // the user's UO install, outside res:// and user://, and it is read
                // in place.
                m_Stream = AudioStreamMP3.LoadFromBuffer(System.IO.File.ReadAllBytes(Path));

                if (m_Stream == null)
                {
                    m_Playing = false;

                    return;
                }

                m_Stream.Loop = m_Repeat;

                m_Playing = true;
            }
            catch (Exception ex)
            {
                // file in use or access denied.
                Log.Error(ex.ToString());
                m_Playing = false;
            }
        }

        protected override void AfterStop()
        {
            if (m_Playing)
            {
                m_Playing = false;
                m_Stream = null;
            }
        }
    }
}
