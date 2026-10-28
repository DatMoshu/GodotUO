// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
namespace GUO.Labs;
internal static partial class LabsRegistration
{
    static partial void RegisterMobileDebug(Context context)
    {
        context.Command("mobiledebug", _ => MobileDebug.MobileInspector.Open(context.World));
        context.Option("Mobile frame inspector", () => MobileDebug.MobileInspector.Open(context.World));
    }
}
#endif
