// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
namespace GUO.Labs;
internal static partial class LabsRegistration
{
    static partial void RegisterFeatures(Context context)
    {
        void Toggle()
        {
            GpuRenderer.OrderedWorldCapture.Enabled = !GpuRenderer.OrderedWorldCapture.Enabled;
            Godot.GD.Print("[rd-world] " + (GpuRenderer.OrderedWorldCapture.Enabled ? "enabled (canvas rollback retained)" : "disabled"));
        }
        context.Command("rdworld", _ => Toggle());
        context.Option("Ordered GPU world prototype", Toggle);
    }
}
#endif
