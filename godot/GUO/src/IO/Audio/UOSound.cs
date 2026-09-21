// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.IO.Audio
{
    public class UOSound : Sound
    {
        // What SoundsLoader hands over: the .mul path strips a 40-byte name header
        // and the loose-.wav path rejects anything that is not exactly this.
        private const int FREQUENCY = 22050;
        private const bool STEREO = false;

        private readonly byte[] _waveBuffer;
        private AudioStreamWav _stream;

        public UOSound(string name, int index, byte[] buffer) : base(name, index)
        {
            _waveBuffer = buffer;

            // Upstream's expression, kept. It is wrong in two ways -- 88.2 bytes
            // per millisecond is 22050Hz *stereo* and this data is mono, and the
            // header it subtracts is 40 bytes, not 32 -- so the spam gate is about
            // half as long as it reads. It is upstream's behaviour and a bug to
            // note, not to fix mid-port. Project rule 2.
            Delay = (uint) ((buffer.Length - 32) / 88.2f);
        }

        public bool CalculateByDistance { get; set; }
        public int X, Y;

        /// <summary>
        ///     The whole effect, as one stream.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         PORT DEVIATION — see ADR-0005. Replaces upstream's
        ///         <c>GetBuffer()</c> and its empty <c>OnBufferNeeded</c>.
        ///         <c>audio_probe.bat</c> measured that <c>AudioStreamWav</c> takes
        ///         these bytes as they are, with no RIFF container, and reports the
        ///         exact duration <c>Sound.Play</c> needs for
        ///         <c>DurationTime</c>.
        ///     </para>
        ///     <para>
        ///         Built once. The buffer never changes and
        ///         <c>Renderer.Sounds.Sound</c> caches the UOSound for the life of
        ///         the client.
        ///     </para>
        ///     <para>
        ///         Upstream's <c>OnBufferNeeded</c> body is a commented-out block
        ///         that attenuates by distance from the player, using
        ///         <see cref="CalculateByDistance" />, <see cref="X" /> and
        ///         <see cref="Y" /> — which is why those three exist and why nothing
        ///         reads them. It is left in upstream and left out here, rather than
        ///         quietly dropped: the fields still record the intent, and whatever
        ///         meets it will not hang off a refill callback that no longer
        ///         exists.
        ///     </para>
        /// </remarks>
        protected override AudioStream GetStream()
        {
            if (_waveBuffer == null || _waveBuffer.Length == 0)
            {
                return null;
            }

            return _stream ??= new AudioStreamWav
            {
                Data = _waveBuffer,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = FREQUENCY,
                Stereo = STEREO,
                LoopMode = AudioStreamWav.LoopModeEnum.Disabled,
            };
        }
    }
}
