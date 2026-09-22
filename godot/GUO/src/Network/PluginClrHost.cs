// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Runtime.InteropServices;
using GUO.Utility.Logging;

namespace GUO.Network
{
    /// <summary>
    /// Starts the .NET Framework runtime inside this process and has
    /// ClassicUO's plugin host fill a <see cref="HostBindings"/>, so that
    /// managed assistants -- Razor, Razor Enhanced, ClassicAssist -- load the
    /// way upstream loads them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO). Upstream ships two programs. ClassicUO.exe is
    /// ClassicUO.Bootstrap, a .NET Framework 4.7.2 exe; it loads cuo.dll, the
    /// client compiled ahead of time to native code, and calls its
    /// <c>Initialize</c> export with a HostBindings it has filled. When
    /// Plugin.Load finds a DLL with no native <c>Install</c> export -- which
    /// is every managed assistant -- it asks that host to load it, and the
    /// host loads it into .NET Framework, where those assistants were built
    /// to run.
    /// </para>
    /// <para>
    /// Here the process is Godot's, and the client is a .NET 8 assembly in it.
    /// Nothing calls <c>Initialize</c>. So this does in-process what the exe
    /// did from outside: it starts the .NET Framework CLR next to .NET 8
    /// (Windows supports both runtimes in one process), loads the ported
    /// bootstrap from <c>tools/plugin_host</c> into its default AppDomain,
    /// and calls its entry with the address of a zeroed HostBindings to fill.
    /// The result goes to <see cref="UnmanagedAssistantHost"/>, exactly as the
    /// pointer <c>Initialize</c> received would have.
    /// </para>
    /// <para>
    /// Windows only, as .NET Framework is. Elsewhere there is no host, which
    /// is what upstream has when it is launched without the bootstrap: native
    /// plugins still load, managed ones are logged as invalid.
    /// </para>
    /// </remarks>
    internal static unsafe class PluginClrHost
    {
        /// <summary>Folder next to GUO.dll that the build copies the host into.</summary>
        public const string HostFolder = "plugin_host";

        /// <summary>The ported bootstrap keeps upstream's assembly name.</summary>
        public const string HostAssembly = "ClassicUO.dll";

        private const string ClrVersion = "v4.0.30319";

        private static readonly Guid CLSID_CLRMetaHost = new Guid("9280188d-0e8e-4867-b30c-7fa83884e8de");
        private static readonly Guid IID_ICLRMetaHost = new Guid("d332db9e-b9b3-4125-8207-a14884f53216");
        private static readonly Guid IID_ICLRRuntimeInfo = new Guid("bd39d1d2-ba2f-486a-89b0-b4b0cb466891");
        private static readonly Guid CLSID_CLRRuntimeHost = new Guid("90f1a06e-7712-4762-86b5-7a5eba6bdb02");
        private static readonly Guid IID_ICLRRuntimeHost = new Guid("90f1a06c-7712-4762-86b5-7a5eba6bdb02");

        // vtable slots, counting IUnknown's three. From metahost.h and mscoree.h.
        private const int ICLRMetaHost_GetRuntime = 3;
        private const int ICLRRuntimeInfo_GetInterface = 9;
        private const int ICLRRuntimeHost_Start = 3;
        private const int ICLRRuntimeHost_ExecuteInDefaultAppDomain = 11;

        [DllImport("mscoree.dll")]
        private static extern int CLRCreateInstance(Guid* clsid, Guid* riid, IntPtr* ppInterface);

        /// <summary>
        /// Returns a host bound to the .NET Framework runtime, or null -- with
        /// the reason logged -- when there is none to be had.
        /// </summary>
        public static UnmanagedAssistantHost TryCreate()
        {
            if (!CUOEnviroment.IsWindows)
            {
                Log.Warn("managed plugins need .NET Framework, which is Windows only; only native plugins will load");

                return null;
            }

            string hostPath = FindHost();

            if (hostPath == null)
            {
                Log.Error($"managed plugin host not found: build tools/plugin_host, which the GUO build does, and look for {HostFolder}/{HostAssembly} next to GUO.dll");

                return null;
            }

            try
            {
                IntPtr runtimeHost = StartRuntime();

                var bindings = (HostBindings*)NativeMemory.AllocZeroed((nuint)sizeof(HostBindings));

                uint ret;
                int hr;

                fixed (char* asm = hostPath)
                fixed (char* type = "Program")
                fixed (char* method = "Start")
                fixed (char* arg = ((long)bindings).ToString())
                {
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, char*, char*, char*, uint*, int>)Slot(runtimeHost, ICLRRuntimeHost_ExecuteInDefaultAppDomain))(runtimeHost, asm, type, method, arg, &ret);
                }

                if (hr < 0 || ret != 0)
                {
                    Log.Error($"managed plugin host failed to start: hr=0x{hr:X8} ret={ret} ({hostPath})");
                    NativeMemory.Free(bindings);

                    return null;
                }

                // The bindings stay allocated for the life of the process, as
                // they do upstream: the host holds on to the delegates behind
                // them, and so does the client.
                Log.Trace($"managed plugin host bound: {hostPath}");

                return new UnmanagedAssistantHost(bindings);
            }
            catch (Exception e)
            {
                Log.Error($"managed plugin host failed to start: {e.Message}");

                return null;
            }
        }

        private static string FindHost()
        {
            string[] roots =
            {
                Path.GetDirectoryName(typeof(PluginClrHost).Assembly.Location),
                AppContext.BaseDirectory,
                CUOEnviroment.ExecutablePath
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                string path = Path.Combine(root, HostFolder, HostAssembly);

                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }

            return null;
        }

        private static IntPtr StartRuntime()
        {
            IntPtr metaHost, runtimeInfo, runtimeHost;

            Guid clsidMeta = CLSID_CLRMetaHost, iidMeta = IID_ICLRMetaHost;
            Guid iidInfo = IID_ICLRRuntimeInfo;
            Guid clsidHost = CLSID_CLRRuntimeHost, iidHost = IID_ICLRRuntimeHost;

            Check(CLRCreateInstance(&clsidMeta, &iidMeta, &metaHost), "CLRCreateInstance");

            fixed (char* version = ClrVersion)
            {
                Check(
                    ((delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, IntPtr*, int>)Slot(metaHost, ICLRMetaHost_GetRuntime))
                        (metaHost, version, &iidInfo, &runtimeInfo),
                    "ICLRMetaHost.GetRuntime"
                );
            }

            Check(
                ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(runtimeInfo, ICLRRuntimeInfo_GetInterface))
                    (runtimeInfo, &clsidHost, &iidHost, &runtimeHost),
                "ICLRRuntimeInfo.GetInterface"
            );

            // S_FALSE when it is already running, which is fine.
            Check(
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(runtimeHost, ICLRRuntimeHost_Start))(runtimeHost),
                "ICLRRuntimeHost.Start"
            );

            return runtimeHost;
        }

        private static IntPtr Slot(IntPtr comObject, int slot)
        {
            return (*(IntPtr**)comObject)[slot];
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
            {
                throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
            }
        }
    }
}
