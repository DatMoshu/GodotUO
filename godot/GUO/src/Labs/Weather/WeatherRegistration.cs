// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
namespace GUO.Labs;
internal static partial class LabsRegistration
{
    static partial void RegisterWeather(Context context)
    {
        context.Command("uopweather", args =>
        {
            if (args.Length == 1 && args[0] == "proof") Weather.WeatherLiveProof.Start(context.World);
            else Weather.WeatherLab.Open(context.World);
        });
    }
}
#endif
