// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
namespace GUO.Labs;
internal static partial class LabsRegistration
{
    static partial void RegisterWeather(Context context)
    {
        context.Command("uopweather", args =>
        {
            if (args.Length == 2 && args[1] == "proof") Weather.WeatherLiveProof.Start(context.World);
            else Game.UI.Gumps.UoPlusOptionsGump.Open(context.World);
        });
    }
}
#endif
