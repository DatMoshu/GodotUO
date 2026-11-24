// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.InteropServices;
using Godot;

namespace GUO.Labs.HarborWater;

internal static class RustRipples
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int Step(float* now, float* previous, int size, float speed2, float damping);

    private static readonly Step Kernel = Load();
    private static bool reported;

    private static Step Load()
    {
        if (System.Environment.GetEnvironmentVariable("GUO_RUST_RIPPLES") != "1") return null;
        string name = OperatingSystem.IsWindows() ? "guo_harbor_water.dll"
            : OperatingSystem.IsMacOS() ? "libguo_harbor_water.dylib" : "libguo_harbor_water.so";
        string path = ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/" + name);
        if (NativeLibrary.TryLoad(path, out var handle))
        {
            if (NativeLibrary.TryGetExport(handle, "guo_water_step_v1", out var fn))
                return Marshal.GetDelegateForFunctionPointer<Step>(fn);
            NativeLibrary.Free(handle);
        }
        GD.PushWarning("Using managed ripples. Run launchers/dev/harbor_water_doctor.");
        return null;
    }

    internal static unsafe bool TryStep(float[] now, float[] previous, int size, float speed2, float damping)
    {
        if (Kernel == null) return false;
        fixed (float* current = now, before = previous)
        {
            if (Kernel(current, before, size, speed2, damping) != 1) return false;
        }
        if (!reported)
        {
            GD.Print("[harbor] native Rust ripple step active");
            reported = true;
        }
        return true;
    }
}
