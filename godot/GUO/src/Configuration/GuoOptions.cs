// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned options category contract (task 1136). One authoritative store per
// setting; detail panels are the only editors of the settings they own.
using System;

namespace GUO.Configuration;

/// <summary>The focused GUO detail panels. All is the legacy combined UO+ panel.</summary>
internal enum GuoSection { All = 0, Lighting = 1, Weather = 2, World = 3 }

internal static class GuoOptions
{
    /// <summary>Raised on the game-loop thread after a GUO detail panel committed to the profile.</summary>
    internal static event Action<GuoSection> Applied;
    internal static void RaiseApplied(GuoSection section) => Applied?.Invoke(section);

    /// <summary>One-line effective-state summary for the GUO page buttons.</summary>
    internal static string Summary(Profile profile, GuoSection section) => "";
}
