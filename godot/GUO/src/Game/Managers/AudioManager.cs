// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using GUO.Utility;
using GUO.Configuration;
using GUO.IO.Audio;
using GUO.Assets;
using GUO.Utility.Logging;
using Godot;

namespace GUO.Game.Managers
{
    internal sealed class AudioManager
    {
        const float SOUND_DELTA = 250;

        // PORT DEVIATION (GUO): upstream's SoundEffect.MasterVolume is a global
        // scalar over every playing sound. Godot's equivalent is the volume of
        // bus 0, which every AudioStreamPlayer routes through by default.
        const int MasterBus = 0;

        private bool _canReproduceAudio = true;
        // PORT DEVIATION (GUO): Sound, not UOSound: verified content-pack
        // music (StoreMusic) plays through the base API and is tracked here.
        // Every use below only touches base members (Name, IsPlaying, Volume,
        // Stop), so the wider element type changes nothing for UOSound.
        private readonly LinkedList<Sound> _currentSounds = new LinkedList<Sound>();
        private readonly UOMusic[] _currentMusic = { null, null };
        private readonly int[] _currentMusicIndices = { 0, 0 };
        // PORT DEVIATION (GUO): music zones duck the base music instead of
        // layering over it. The duck is a sticky factor (1 = full, 0 =
        // silent), not a one-shot volume poke: AudioManager.Update runs after
        // the zone tick every frame and recomputes volumes from the profile,
        // so a poke applied by ZoneAudio is wiped before it is ever heard.
        // Every writer below (PlayMusic, Update, UpdateCurrentMusicVolume)
        // multiplies by this factor, so entering a zone silences base music
        // even when it restarts mid-zone and exiting restores it.
        private float _musicDuckFactor = 1f;
        public int LoginMusicIndex { get; private set; }
        public int DeathMusicIndex { get; } = 42;

        /// <summary>
        /// What is playing right now, named, for the input probe.
        /// </summary>
        /// <remarks>
        /// GUO-only, and diagnostics only -- nothing in the client reads it.
        /// Whether sound reaches a speaker is not something a screenshot or a
        /// log line can show, and the alternative was reflection into private
        /// fields from the probe, which breaks silently when this file
        /// changes. This is a rewrite-tier file; there is no upstream text
        /// here to keep faithful to.
        /// </remarks>
        internal string NowPlaying
        {
            get
            {
                var playing = new List<string>();

                foreach (UOMusic music in _currentMusic)
                {
                    if (music != null && music.IsPlaying(Time.Ticks))
                    {
                        playing.Add($"music:{music.Name}");
                    }
                }

                foreach (Sound sound in _currentSounds)
                {
                    if (sound != null && sound.IsPlaying(Time.Ticks))
                    {
                        playing.Add($"sound:{sound.Name}");
                    }
                }

                return playing.Count == 0 ? "nothing" : string.Join(", ", playing);
            }
        }

        public void Initialize()
        {
            // PORT DEVIATION (GUO): upstream constructs and immediately disposes a
            // DynamicSoundEffectInstance here, purely to find out whether FNA can
            // open an audio device -- it catches NoAudioHardwareException and turns
            // sound off for the session. Godot has no equivalent probe and no
            // equivalent throw: when no device can be opened it falls back to a
            // dummy driver and keeps running, which is the behaviour this flag
            // exists to produce. So the flag stays and starts true.
            _canReproduceAudio = true;

            LoginMusicIndex = Client.Game.UO.Version switch
            {
                >= ClientVersion.CV_7000 => 78, // LoginLoop
                > ClientVersion.CV_308Z => 0,
                _ => 8 // stones2
            };

            Client.Game.Activated += OnWindowActivated;
            Client.Game.Deactivated += OnWindowDeactivated;
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            if (!_canReproduceAudio || ProfileManager.CurrentProfile == null || ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            {
                return;
            }

            AudioServer.SetBusVolumeLinear(MasterBus, 0f);
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
            if (!_canReproduceAudio || ProfileManager.CurrentProfile == null || ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            {
                return;
            }

            AudioServer.SetBusVolumeLinear(MasterBus, 1f);
        }

        public void PlaySound(int index)
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            if (!_canReproduceAudio || currentProfile == null)
            {
                return;
            }

            float volume = currentProfile.SoundVolume / SOUND_DELTA;

            if (Client.Game.IsActive)
            {
                if (!currentProfile.ReproduceSoundsInBackground)
                {
                    volume = currentProfile.SoundVolume / SOUND_DELTA;
                }
            }
            else if (!currentProfile.ReproduceSoundsInBackground)
            {
                volume = 0;
            }

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            if (!currentProfile.EnableSound || !Client.Game.IsActive && !currentProfile.ReproduceSoundsInBackground)
            {
                volume = 0;
            }

            UOSound sound = (UOSound) Client.Game.UO.Sounds.GetSound(index);

            if (sound != null && sound.Play(Time.Ticks, volume))
            {
                sound.X = -1;
                sound.Y = -1;
                sound.CalculateByDistance = false;

                _currentSounds.AddLast(sound);
            }
        }

        public void PlaySoundWithDistance(World world, int index, int x, int y)
        {
            if (!_canReproduceAudio || !world.InGame)
            {
                return;
            }

            int distX = Math.Abs(x - world.Player.X);
            int distY = Math.Abs(y - world.Player.Y);
            int distance = Math.Max(distX, distY);

            Profile currentProfile = ProfileManager.CurrentProfile;
            float volume = currentProfile.SoundVolume / SOUND_DELTA;
            float distanceFactor = 0.0f;

            if (distance >= 1)
            {
                float volumeByDist = volume / (world.ClientViewRange + 1);
                distanceFactor = volumeByDist * distance;
            }

            if (distance > world.ClientViewRange)
            {
                volume = 0;
            }

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            if (currentProfile == null || !currentProfile.EnableSound || !Client.Game.IsActive && !currentProfile.ReproduceSoundsInBackground)
            {
                volume = 0;
            }

            UOSound sound = (UOSound)Client.Game.UO.Sounds.GetSound(index);

            if (sound != null && sound.Play(Time.Ticks, volume, distanceFactor))
            {
                sound.X = x;
                sound.Y = y;
                sound.CalculateByDistance = true;

                _currentSounds.AddLast(sound);
            }
        }

        public void PlayMusic(int music, bool iswarmode = false, bool is_login = false)
        {
            if (!_canReproduceAudio)
            {
                return;
            }

            if (music >= Constants.MAX_MUSIC_DATA_INDEX_COUNT)
            {
                return;
            }

            float volume;

            if (is_login)
            {
                volume = Settings.GlobalSettings.LoginMusic ? Settings.GlobalSettings.LoginMusicVolume / SOUND_DELTA : 0;
            }
            else
            {
                Profile currentProfile = ProfileManager.CurrentProfile;

                if (currentProfile == null || !currentProfile.EnableMusic)
                {
                    volume = 0;
                }
                else
                {
                    volume = currentProfile.MusicVolume / SOUND_DELTA;
                }

                if (currentProfile != null && !currentProfile.EnableCombatMusic && iswarmode)
                {
                    return;
                }
            }


            if (volume < -1 || volume > 1f)
            {
                return;
            }

            Sound m = Client.Game.UO.Sounds.GetMusic(music);

            if (m == null && _currentMusic[0] != null)
            {
                StopMusic();
            }
            else if (m is UOMusic um && (um != _currentMusic[0] || iswarmode))
            {
                StopMusic();

                int idx = iswarmode ? 1 : 0;
                _currentMusicIndices[idx] = music;
                _currentMusic[idx] = um;

                _currentMusic[idx].Play(Time.Ticks, volume * _musicDuckFactor);
            }
            else if (m != null && m is not UOMusic)
            {
                // PORT DEVIATION (GUO): verified content-pack music is a plain
                // Sound, not a UOMusic, so the cast above would throw. It
                // plays through the base API and is tracked with the sounds,
                // which is also what NowPlaying reads. It is still base
                // music, so it starts ducked like the UOMusic path; plain
                // SFX added by PlaySound never pass through here.
                if (m.Play(Time.Ticks, volume * _musicDuckFactor))
                {
                    _currentSounds.AddLast(m);
                }
            }
        }

        public void UpdateCurrentMusicVolume(bool isLogin = false)
        {
            if (!_canReproduceAudio)
            {
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null)
                {
                    float volume;

                    if (isLogin)
                    {
                        volume = Settings.GlobalSettings.LoginMusic ? Settings.GlobalSettings.LoginMusicVolume / SOUND_DELTA : 0;
                    }
                    else
                    {
                        Profile currentProfile = ProfileManager.CurrentProfile;

                        volume = currentProfile == null || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA;
                    }


                    if (volume < -1 || volume > 1f)
                    {
                        return;
                    }

                    _currentMusic[i].Volume = i == 0 && _currentMusic[1] != null ? 0 : volume * _musicDuckFactor;
                }
            }

            // Content-pack base music rides in _currentSounds; keep it ducked
            // too. Plain SFX are never StoreMusic, so they stay layered.
            foreach (Sound s in _currentSounds)
            {
                if (s is Store.StoreMusic)
                {
                    Profile currentProfile = ProfileManager.CurrentProfile;
                    float baseVolume = currentProfile == null || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA;
                    s.Volume = baseVolume * _musicDuckFactor;
                }
            }
        }

        public void UpdateCurrentSoundsVolume()
        {
            if (!_canReproduceAudio)
            {
                return;
            }

            Profile currentProfile = ProfileManager.CurrentProfile;

            float volume = currentProfile == null || !currentProfile.EnableSound ? 0 : currentProfile.SoundVolume / SOUND_DELTA;

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            for (LinkedListNode<Sound> soundNode = _currentSounds.First; soundNode != null; soundNode = soundNode.Next)
            {
                // Content-pack base music rides here as StoreMusic: it ducks
                // with the zone fade. Plain SFX stay at full volume (layered).
                if (soundNode.Value is Store.StoreMusic)
                {
                    float baseVolume = currentProfile == null || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA;
                    soundNode.Value.Volume = baseVolume * _musicDuckFactor;
                }
                else
                {
                    soundNode.Value.Volume = volume;
                }
            }
        }

        /// <summary>
        /// PORT DEVIATION (GUO): music zones fade the base music under their
        /// own tracks instead of layering over it. 1 is full volume, 0 is
        /// silent; the factor is sticky so the per-frame Update (which runs
        /// after the zone tick and recomputes volumes from the profile) and
        /// any mid-zone PlayMusic restart keep the duck until the zone
        /// restores it with 1. SFX are never touched here.
        /// </summary>
        public void SetMusicDuck(float factor)
        {
            if (!_canReproduceAudio)
            {
                return;
            }

            Profile currentProfile = ProfileManager.CurrentProfile;
            float volume = currentProfile == null || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA;
            if (volume < -1 || volume > 1f)
            {
                return;
            }

            factor = factor < 0f ? 0f : factor > 1f ? 1f : factor;
            _musicDuckFactor = factor;
            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null)
                {
                    _currentMusic[i].Volume = i == 0 && _currentMusic[1] != null ? 0 : volume * factor;
                }
            }
            foreach (Sound s in _currentSounds)
            {
                if (s is Store.StoreMusic)
                {
                    s.Volume = volume * factor;
                }
            }
        }

        public void StopMusic()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null)
                {
                    _currentMusic[i].Stop();
                    _currentMusic[i].Dispose();
                    _currentMusic[i] = null;
                }
            }
        }

        public void StopWarMusic()
        {
            PlayMusic(_currentMusicIndices[0]);
        }

        public void StopSounds()
        {
            LinkedListNode<Sound> first = _currentSounds.First;

            while (first != null)
            {
                LinkedListNode<Sound> next = first.Next;

                first.Value.Stop();

                _currentSounds.Remove(first);

                first = next;
            }
        }

        public void Update()
        {
            if (!_canReproduceAudio)
            {
                return;
            }

            bool runninWarMusic = _currentMusic[1] != null;
            Profile currentProfile = ProfileManager.CurrentProfile;

            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null && currentProfile != null)
                {
                    if (Client.Game.IsActive)
                    {
                        if (!currentProfile.ReproduceSoundsInBackground)
                        {
                            _currentMusic[i].Volume = i == 0 && runninWarMusic || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA * _musicDuckFactor;
                        }
                    }
                    else if (!currentProfile.ReproduceSoundsInBackground && _currentMusic[i].Volume != 0.0f)
                    {
                        _currentMusic[i].Volume = 0;
                    }
                }

                _currentMusic[i]?.Update();
            }


            LinkedListNode<Sound> first = _currentSounds.First;

            while (first != null)
            {
                LinkedListNode<Sound> next = first.Next;

                if (!first.Value.IsPlaying(Time.Ticks))
                {
                    first.Value.Stop();
                    _currentSounds.Remove(first);
                }

                first = next;
            }
        }

        public UOMusic GetCurrentMusic()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null && _currentMusic[i].IsPlaying(Time.Ticks))
                {
                    return _currentMusic[i];
                }
            }
            return null;
        }
    }
}