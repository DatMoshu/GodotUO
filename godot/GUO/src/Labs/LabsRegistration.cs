// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using GUO.Game;

namespace GUO.Labs;

/// <summary>One opt-in registration point for Labs commands and Options actions.</summary>
internal static partial class LabsRegistration
{
    /// <summary>
    /// Hosts provide the surface they are building. For example, CommandManager
    /// supplies its command registration delegate; Modern Options supplies an
    /// action-row delegate. A missing surface registers nothing.
    /// </summary>
    internal static void Register(World world,
        Action<string, Action<string[]>> command = null,
        Action<string, Action> option = null)
    {
        RegisterFeatures(new Context(world, command, option));
    }

    // Intentionally empty until a separately reviewed Labs feature implements it.
    // Its implementation calls each feature's registration in one place.
    static partial void RegisterFeatures(Context context);

    internal readonly struct Context
    {
        internal World World { get; }
        private readonly Action<string, Action<string[]>> _command;
        private readonly Action<string, Action> _option;

        internal Context(World world, Action<string, Action<string[]>> command,
            Action<string, Action> option)
        {
            World = world;
            _command = command;
            _option = option;
        }

        internal void Command(string name, Action<string[]> run) => _command?.Invoke(name, run);
        internal void Option(string label, Action open) => _option?.Invoke("Labs: " + label, open);
    }
}
#endif
