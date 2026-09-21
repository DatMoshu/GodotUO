namespace GUO.Bootstrap;

using System;
using Godot;
using GUO.Resources;

/// <summary>
/// Checks that the resources compiled into the assembly can actually be found
/// at runtime.
/// </summary>
/// <remarks>
/// This exists because the two things it checks are exactly the kind that
/// compile perfectly and fail in front of a user. The localized strings are
/// looked up by a name baked into a string literal, and the images by a
/// manifest name set in the .csproj; neither is checked by the compiler, and
/// both break silently -- a missing string comes back null, a missing image as
/// zero bytes. Running this in the offline health check turns both into a
/// build failure instead of a blank login screen.
/// </remarks>
public static class ResourceProbe
{
    /// <summary>Runs every check and prints what failed. True if all passed.</summary>
    public static bool Verify()
    {
        bool ok = true;

        ok &= CheckString("ResGumps", ResGumps.Accept);
        ok &= CheckString("ResGeneral", ResGeneral.Alliance0);
        ok &= CheckString("ResErrorMessages", ResErrorMessages.CharacterAlreadyExists);

        ok &= CheckImage("cuologo.png", Loader.GetCuoLogo().Length);
        ok &= CheckImage("game-background.png", Loader.GetBackgroundImage().Length);

        GD.Print(ok
            ? "[GUO] embedded resources OK."
            : "[GUO] embedded resources FAILED.");

        return ok;
    }

    private static bool CheckString(string set, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Almost always a logical-name mismatch between GUO.csproj and the
            // literal in Res*.Designer.cs. See the comment in the .csproj.
            GD.PrintErr($"[GUO] resource set '{set}' resolved to nothing.");
            return false;
        }

        return true;
    }

    private static bool CheckImage(string name, int byteCount)
    {
        if (byteCount == 0)
        {
            GD.PrintErr($"[GUO] embedded image '{name}' is missing or empty.");
            return false;
        }

        return true;
    }
}
