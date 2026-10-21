// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): temporary lights for spell effects, under the Realtime lights option.

using System;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The spell-effect light hook: a coloured light at a world position for a
    /// while. The Realtime lights field adds every live one to the frame's
    /// sources, through the same walls and roofs as any other light. With the
    /// option off nothing reads them; they expire on their own.
    /// </summary>
    /// <remarks>
    /// Game-loop thread only, as the rest of the scene. A full table drops the
    /// oldest light. Time is the client's <c>Time.Ticks</c> (milliseconds).
    /// </remarks>
    internal static class RealtimeLightEffects
    {
        /// <summary>Most live temporary lights.</summary>
        public const int MaxLights = 64;

        /// <summary>UO+ spell bursts admitted at once (and thus per frame).</summary>
        public const int MaxSpellLights = 16;

        /// <summary>World offers plus temporary sources may never exceed the classic cap.</summary>
        public const int FrameLightCap = GUO.Assets.LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT;

        /// <summary>Longest a temporary light may last, ms.</summary>
        public const uint MaxDurationMs = 60_000;

        /// <summary>Share of the duration, at its end, over which the light fades out.</summary>
        public const float FadeShare = 0.3f;

        private struct Entry
        {
            public int Handle;
            public int X, Y, Z, Radius;
            public Color Color;
            public float Energy;
            public LightCurve Curve;
            public bool Fade;
            public bool Spell;
            public uint Start, End;
        }

        private static readonly Entry[] _entries = new Entry[MaxLights];
        private static int _count;
        private static int _nextHandle = 1;

        /// <summary>Live temporary lights (expired ones are dropped when the field reads them).</summary>
        public static int Count => _count;

        /// <summary>
        /// Light (x, y, z) for <paramref name="durationMs"/> from now. Returns a
        /// handle for <see cref="Remove"/>. Radius is in tiles (1..8), clamped.
        /// </summary>
        public static int Add(int x, int y, int z, int radius, Color color, uint durationMs,
            LightCurve curve = LightCurve.E, float energy = RealtimeLightField.Energy, bool fadeOut = true)
            => Add(GUO.Time.Ticks, x, y, z, radius, color, durationMs, curve, energy, fadeOut);

        /// <summary><see cref="Add(int,int,int,int,Color,uint,LightCurve,float,bool)"/> at an explicit time (probes).</summary>
        public static int Add(uint now, int x, int y, int z, int radius, Color color, uint durationMs,
            LightCurve curve = LightCurve.E, float energy = RealtimeLightField.Energy, bool fadeOut = true)
        {
            if (durationMs == 0) return 0;
            durationMs = Math.Min(durationMs, MaxDurationMs);
            Prune(now);

            if (_count == MaxLights)
            {
                // Drop the oldest.
                Array.Copy(_entries, 1, _entries, 0, MaxLights - 1);
                _count--;
            }

            int handle = _nextHandle++;
            if (_nextHandle == int.MaxValue) _nextHandle = 1;

            _entries[_count++] = new Entry
            {
                Handle = handle,
                X = x,
                Y = y,
                Z = z,
                Radius = Math.Clamp(radius, 1, RealtimeLightField.MaxRadius),
                Color = color,
                Energy = Math.Clamp(energy, 0f, 8f),
                Curve = curve == LightCurve.Custom ? LightCurve.E : curve,
                Fade = fadeOut,
                Start = now,
                End = now + durationMs,
            };
            return handle;
        }

        /// <summary>Put a light out early. False when it had already gone.</summary>
        public static bool Remove(int handle)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Handle != handle) continue;
                Array.Copy(_entries, i + 1, _entries, i, _count - i - 1);
                _count--;
                return true;
            }

            return false;
        }

        /// <summary>Put every temporary light out (a map change, a logout).</summary>
        public static void Clear() => _count = 0;

        /// <summary>
        /// A bounded UO+ spell burst; for example AddSpell(now, x, y, z, 3, Colors.Orange, 600).
        /// At saturation the oldest spell burst goes, preserving non-spell hooks.
        /// </summary>
        internal static int AddSpell(uint now, int x, int y, int z, int radius, Color color,
            uint durationMs, LightCurve curve = LightCurve.E, int spellBudget = MaxSpellLights)
        {
            spellBudget = Math.Clamp(spellBudget, 0, MaxSpellLights);
            if (durationMs == 0 || spellBudget == 0) return 0;
            Prune(now);
            int count = 0, oldest = 0;
            for (int i = 0; i < _count; i++)
            {
                if (!_entries[i].Spell) continue;
                if (count++ == 0) oldest = _entries[i].Handle;
            }
            // A density reduction may leave several previously admitted bursts.
            while (count >= spellBudget || _count == MaxLights)
            {
                if (count == 0) return 0; // Generic hooks already own every slot.
                Remove(oldest);
                count--;
                for (int i = 0; i < _count; i++)
                    if (_entries[i].Spell) { oldest = _entries[i].Handle; break; }
            }
            int handle = Add(now, x, y, z, radius, color, durationMs, curve);
            _entries[_count - 1].Spell = true;
            return handle;
        }

        /// <summary>Clear only UO+ bursts on disabling, map change or logout.</summary>
        internal static void ClearSpells()
        {
            int w = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Spell) continue;
                _entries[w++] = _entries[i];
            }
            _count = w;
        }

        /// <summary>Apply a lower density immediately, retaining the newest spell bursts.</summary>
        internal static void LimitSpells(int budget)
        {
            budget = Math.Clamp(budget, 0, MaxSpellLights);
            int retained = 0;
            for (int i = _count - 1; i >= 0; i--)
                if (_entries[i].Spell && ++retained > budget) Remove(_entries[i].Handle);
        }

        /// <summary>
        /// The live lights as field sources into <paramref name="into"/>, from
        /// <paramref name="start"/>; returns how many were written. Expired ones go.
        /// </summary>
        internal static int Collect(uint now, RealtimeLightField.Source[] into, int start, bool includeSpells = true)
        {
            Prune(now);
            if (!includeSpells) ClearSpells();
            int n = 0;
            int end = Math.Min(into.Length, FrameLightCap);
            for (int i = 0; i < _count && start + n < end; i++)
            {
                ref Entry e = ref _entries[i];
                float energy = e.Energy * Strength(in e, now);
                if (energy <= 0f) continue;
                into[start + n++] = new RealtimeLightField.Source(e.X, e.Y, e.Z, e.Radius, e.Color, curve: e.Curve, energy: energy);
            }

            return n;
        }

        private static float Strength(in Entry e, uint now)
        {
            if (!e.Fade) return 1f;
            float total = e.End - e.Start;
            float left = (int)(e.End - now);
            float fade = total * FadeShare;
            return fade <= 0f ? 1f : Math.Clamp(left / fade, 0f, 1f);
        }

        private static void Prune(uint now)
        {
            int w = 0;
            for (int i = 0; i < _count; i++)
            {
                // Wrap-safe: expired when End is at or before now.
                if ((int)(_entries[i].End - now) <= 0) continue;
                if (w != i) _entries[w] = _entries[i];
                w++;
            }

            _count = w;
        }
    }
}
