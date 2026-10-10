// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has no gamepad, so it has no
// pad bindings either.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Input.Gamepad
{
    /// <summary>A job a pad input can be bound to (the "Set controls" order).</summary>
    internal enum PadCommand
    {
        Use,
        Cancel,
        AttackLast,
        TargetLast,
        WarMode,
        NextHostile,
        AlwaysRun,
        MacroRow,
        MenuWheel,
        InteractRadar,
        Options,
        Drawer
    }

    /// <summary>A window the menu wheel can open.</summary>
    internal enum WheelWindow
    {
        Backpack,
        Paperdoll,
        Journal,
        Skills,
        Spellbook,
        WorldMap,
        Macros,
        Options,
        Status,
        Party
    }

    /// <summary>
    /// One pad input: a button by its printed label (A is the printed A,
    /// whatever the pad's layout sends), or one half of an axis (a trigger, or
    /// a stick pushed one way).
    /// </summary>
    internal readonly record struct PadInput(bool IsAxis, int Index, int Sign)
    {
        public static PadInput Button(JoyButton b) => new(false, (int) b, 0);

        public static PadInput Axis(JoyAxis a, int sign) => new(true, (int) a, Math.Sign(sign));

        /// <summary>"b0" for a button, "a4+" / "a0-" for half an axis: the saved form.</summary>
        public string Id => IsAxis ? $"a{Index}{(Sign < 0 ? "-" : "+")}" : $"b{Index}";

        public static PadInput? Parse(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2)
            {
                return null;
            }

            if (id[0] == 'b' && int.TryParse(id.AsSpan(1), out int b) && b >= 0 && b < (int) JoyButton.Max)
            {
                return new PadInput(false, b, 0);
            }

            if (id[0] == 'a' && id.Length >= 3 && (id[^1] == '+' || id[^1] == '-')
                && int.TryParse(id.AsSpan(1, id.Length - 2), out int a) && a >= 0 && a < (int) JoyAxis.Max)
            {
                return new PadInput(true, a, id[^1] == '-' ? -1 : 1);
            }

            return null;
        }

        /// <summary>What a player calls it, by the printed labels (Xbox names; the glyphs show the family's).</summary>
        public string Label
        {
            get
            {
                if (IsAxis)
                {
                    bool neg = Sign < 0;

                    return (JoyAxis) Index switch
                    {
                        JoyAxis.LeftX => neg ? "Left stick left" : "Left stick right",
                        JoyAxis.LeftY => neg ? "Left stick up" : "Left stick down",
                        JoyAxis.RightX => neg ? "Right stick left" : "Right stick right",
                        JoyAxis.RightY => neg ? "Right stick up" : "Right stick down",
                        JoyAxis.TriggerLeft => "LT",
                        JoyAxis.TriggerRight => "RT",
                        _ => $"Axis {Index}{(neg ? "-" : "+")}",
                    };
                }

                return (JoyButton) Index switch
                {
                    JoyButton.A => "A",
                    JoyButton.B => "B",
                    JoyButton.X => "X",
                    JoyButton.Y => "Y",
                    JoyButton.LeftShoulder => "LB",
                    JoyButton.RightShoulder => "RB",
                    JoyButton.Back => "Back",
                    JoyButton.Start => "Start",
                    JoyButton.Guide => "Guide",
                    JoyButton.LeftStick => "L3",
                    JoyButton.RightStick => "R3",
                    JoyButton.DpadUp => "D-pad up",
                    JoyButton.DpadDown => "D-pad down",
                    JoyButton.DpadLeft => "D-pad left",
                    JoyButton.DpadRight => "D-pad right",
                    _ => $"Button {Index}",
                };
            }
        }

        /// <summary>The printed button, for a glyph; null for an axis.</summary>
        public JoyButton? AsButton => IsAxis ? null : (JoyButton) Index;
    }

    /// <summary>
    /// The pad's rebindable action map (the owner's design of 2026-10-03,
    /// docs/wiki/Controller.md): which input does each <see cref="PadCommand"/>,
    /// which window each of the menu wheel's eight slices opens, and how far
    /// the interact radar reaches. GamepadInput reads every one of these jobs
    /// from here; "Set controls" (<see cref="PadWizard"/>) writes it.
    /// </summary>
    /// <remarks>
    /// Saved as <c>padbindings.json</c> beside settings.json, as
    /// <c>pregame3d.json</c> is: one file per client home, read once, written
    /// whenever it changes. The JSON is read and written by hand, not by a
    /// typed serialiser (the client keeps its assembly free of reflection
    /// JSON). One input belongs to one job: binding it clears it from any other.
    /// </remarks>
    internal static class PadBindings
    {
        public const float AxisOn = 0.6f, AxisOff = 0.3f;
        public const int DefaultRadarRange = 10;

        // Reserved, not bound, not in Set controls (Controller.md, note for Moshu):
        // open swapper, move across paperdolls, change auto behavior, control, spectate.
        // One UO connection is one character. Do not treat these as a world pass.

        /// <summary>The order "Set controls" asks in.</summary>
        public static readonly PadCommand[] Order =
        {
            PadCommand.Use, PadCommand.Cancel, PadCommand.AttackLast, PadCommand.TargetLast,
            PadCommand.WarMode, PadCommand.NextHostile, PadCommand.AlwaysRun, PadCommand.MacroRow,
            PadCommand.MenuWheel, PadCommand.InteractRadar, PadCommand.Options, PadCommand.Drawer,
        };

        public static string Name(PadCommand c) => c switch
        {
            PadCommand.Use => "Use / click",
            PadCommand.Cancel => "Cancel",
            PadCommand.AttackLast => "Attack last",
            PadCommand.TargetLast => "Target last",
            PadCommand.WarMode => "Toggle war mode",
            PadCommand.NextHostile => "Next hostile",
            PadCommand.AlwaysRun => "Always run",
            PadCommand.MacroRow => "Macro row",
            PadCommand.MenuWheel => "Menu wheel",
            PadCommand.InteractRadar => "Interact radar",
            PadCommand.Options => "Options",
            PadCommand.Drawer => "Drawer",
            _ => c.ToString(),
        };

        public static string Name(WheelWindow w) => w switch
        {
            WheelWindow.WorldMap => "World map",
            _ => w.ToString(),
        };

        /// <summary>The layout the owner approved (2026-10-03); the pad's older buttons stay where they were.</summary>
        public static Dictionary<PadCommand, PadInput> Defaults() => new()
        {
            [PadCommand.Use] = PadInput.Button(JoyButton.A),
            [PadCommand.Cancel] = PadInput.Button(JoyButton.B),
            [PadCommand.AttackLast] = PadInput.Button(JoyButton.X),
            [PadCommand.TargetLast] = PadInput.Button(JoyButton.LeftShoulder),
            [PadCommand.WarMode] = PadInput.Button(JoyButton.RightStick),
            [PadCommand.NextHostile] = PadInput.Button(JoyButton.RightShoulder),
            [PadCommand.AlwaysRun] = PadInput.Button(JoyButton.LeftStick),
            [PadCommand.MacroRow] = PadInput.Button(JoyButton.Y),
            [PadCommand.MenuWheel] = PadInput.Axis(JoyAxis.TriggerLeft, 1),
            [PadCommand.InteractRadar] = PadInput.Axis(JoyAxis.TriggerRight, 1),
            [PadCommand.Options] = PadInput.Button(JoyButton.Start),
            [PadCommand.Drawer] = PadInput.Button(JoyButton.Back),
        };

        /// <summary>The wheel, clockwise from the top.</summary>
        public static WheelWindow[] DefaultWheel() =>
        new[]
        {
            WheelWindow.Backpack, WheelWindow.Paperdoll, WheelWindow.Journal, WheelWindow.Skills,
            WheelWindow.Spellbook, WheelWindow.WorldMap, WheelWindow.Macros, WheelWindow.Options,
        };

        private static Dictionary<PadCommand, PadInput> _map;
        private static WheelWindow[] _wheel;
        private static int _radarRange = DefaultRadarRange;
        private static bool _offered;

        /// <summary>For probes: a file other than the one beside settings.json.</summary>
        public static string PathOverride { get; set; }

        public static string FilePath => PathOverride ??
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Settings.GetSettingsFilepath()) ?? "", "padbindings.json");

        /// <summary>Raised after the map changes (a wizard applied, defaults restored).</summary>
        public static event Action Changed;

        private static void EnsureLoaded()
        {
            if (_map == null)
            {
                Load();
            }
        }

        public static IReadOnlyDictionary<PadCommand, PadInput> Map
        {
            get
            {
                EnsureLoaded();
                return _map;
            }
        }

        public static IReadOnlyList<WheelWindow> Wheel
        {
            get
            {
                EnsureLoaded();
                return _wheel;
            }
        }

        /// <summary>Tiles the interact radar reaches (1..24).</summary>
        public static int RadarRange
        {
            get
            {
                EnsureLoaded();
                return _radarRange;
            }
            set
            {
                EnsureLoaded();
                _radarRange = Math.Clamp(value, 1, 24);
                Save();
            }
        }

        /// <summary>Whether "Set controls" has been offered after a first login on a pad.</summary>
        public static bool WizardOffered
        {
            get
            {
                EnsureLoaded();
                return _offered;
            }
            set
            {
                EnsureLoaded();
                _offered = value;
                Save();
            }
        }

        /// <summary>The input bound to <paramref name="c"/>, or null.</summary>
        public static PadInput? For(PadCommand c) => Map.TryGetValue(c, out PadInput i) ? i : null;

        /// <summary>The job <paramref name="input"/> is bound to, or null.</summary>
        public static PadCommand? CommandFor(PadInput input)
        {
            foreach (KeyValuePair<PadCommand, PadInput> kv in Map)
            {
                if (kv.Value == input)
                {
                    return kv.Key;
                }
            }

            return null;
        }

        /// <summary>
        /// Apply a set of choices together (the end of "Set controls"): each
        /// input is cleared from any other job first, then bound. Saved once.
        /// </summary>
        public static void Apply(IReadOnlyDictionary<PadCommand, PadInput> chosen, IReadOnlyList<WheelWindow> wheel)
        {
            EnsureLoaded();

            foreach (PadCommand c in Order)
            {
                if (!chosen.TryGetValue(c, out PadInput input))
                {
                    continue;
                }

                foreach (PadCommand other in Order)
                {
                    if (other != c && _map.TryGetValue(other, out PadInput held) && held == input)
                    {
                        _map.Remove(other);
                    }
                }

                _map[c] = input;
            }

            if (wheel != null && wheel.Count == 8)
            {
                _wheel = new WheelWindow[8];

                for (int i = 0; i < 8; i++)
                {
                    _wheel[i] = wheel[i];
                }
            }

            Save();
            Changed?.Invoke();
        }

        public static void ResetToDefaults()
        {
            EnsureLoaded();
            _map = Defaults();
            _wheel = DefaultWheel();
            _radarRange = DefaultRadarRange;
            Save();
            Changed?.Invoke();
        }

        /// <summary>Read the file again (or the defaults, when there is none).</summary>
        public static void Load()
        {
            _map = Defaults();
            _wheel = DefaultWheel();
            _radarRange = DefaultRadarRange;
            _offered = false;
            string path = FilePath;

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("bindings", out JsonElement b) && b.ValueKind == JsonValueKind.Object)
                {
                    var map = new Dictionary<PadCommand, PadInput>();

                    foreach (JsonProperty p in b.EnumerateObject())
                    {
                        if (Enum.TryParse(p.Name, out PadCommand c) && p.Value.ValueKind == JsonValueKind.String
                            && PadInput.Parse(p.Value.GetString()) is PadInput input)
                        {
                            map[c] = input;
                        }
                    }

                    // A file names every bound job; one it leaves out is unbound.
                    _map = map;
                }

                if (root.TryGetProperty("wheel", out JsonElement w) && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() == 8)
                {
                    var wheel = DefaultWheel();
                    int i = 0;

                    foreach (JsonElement e in w.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String && Enum.TryParse(e.GetString(), out WheelWindow ww))
                        {
                            wheel[i] = ww;
                        }

                        i++;
                    }

                    _wheel = wheel;
                }

                if (root.TryGetProperty("radarRange", out JsonElement r) && r.TryGetInt32(out int range))
                {
                    _radarRange = Math.Clamp(range, 1, 24);
                }

                if (root.TryGetProperty("wizardOffered", out JsonElement o) && (o.ValueKind == JsonValueKind.True || o.ValueKind == JsonValueKind.False))
                {
                    _offered = o.GetBoolean();
                }

                GD.Print($"[GUO] pad bindings: {path}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO] pad bindings: {path} unreadable, using the defaults: {ex.Message}");
            }
        }

        public static void Save()
        {
            string path = FilePath;

            try
            {
                using var stream = new MemoryStream();

                using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    json.WriteStartObject();
                    json.WriteNumber("version", 1);
                    json.WriteStartObject("bindings");

                    foreach (PadCommand c in Order)
                    {
                        if (_map.TryGetValue(c, out PadInput input))
                        {
                            json.WriteString(c.ToString(), input.Id);
                        }
                    }

                    json.WriteEndObject();
                    json.WriteStartArray("wheel");

                    foreach (WheelWindow w in _wheel)
                    {
                        json.WriteStringValue(w.ToString());
                    }

                    json.WriteEndArray();
                    json.WriteNumber("radarRange", _radarRange);
                    json.WriteBoolean("wizardOffered", _offered);
                    json.WriteEndObject();
                }

                string dir = System.IO.Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllBytes(path, stream.ToArray());
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO] pad bindings: could not write {path}: {ex.Message}");
            }
        }
    }
}
