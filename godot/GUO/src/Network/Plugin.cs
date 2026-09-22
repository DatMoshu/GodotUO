// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.IO;
using GUO.Assets;
using GUO.Utility.Logging;
using GUO.Utility.Platforms;
using CUO_API;
using GUO.Platform.Sdl;

// PORT DEVIATION (GUO): upstream also imports ClassicUO.Renderer,
// ClassicUO.Renderer.Batching and the two XNA namespaces. Every use of them is
// inside HandleCmdList, which is a PORT GAP here -- see its remarks.

namespace GUO.Network
{
    internal unsafe class Plugin
    {
        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnCastSpell _castSpell;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnDrawCmdList _draw_cmd_list;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetCliloc _get_cliloc;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetStaticData _get_static_data;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetTileData _get_tile_data;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetPacketLength _getPacketLength;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetPlayerPosition _getPlayerPosition;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetStaticImage _getStaticImage;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnGetUOFilePath _getUoFilePath;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnWndProc _on_wnd_proc;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnClientClose _onClientClose;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnConnected _onConnected;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnDisconnected _onDisconnected;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnFocusGained _onFocusGained;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnFocusLost _onFocusLost;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnHotkey _onHotkeyPressed;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnInitialize _onInitialize;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnMouse _onMouse;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnPacketSendRecv_new _onRecv_new,
            _onSend_new;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnUpdatePlayerPosition _onUpdatePlayerPosition;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnPacketSendRecv _recv,
            _send,
            _onRecv,
            _onSend;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnPacketSendRecv_new_intptr _recv_new,
            _send_new;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private RequestMove _requestMove;

        // PORT DEVIATION (GUO): upstream's value type is XNA's
        // GraphicsResource. Same substitution, for the same reason, as
        // IPluginHost.GfxResources in src/Client/PluginHost.cs.
        private readonly Dictionary<IntPtr, Godot.Resource> _resources =
            new Dictionary<IntPtr, Godot.Resource>();

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnSetTitle _setTitle;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        private OnTick _tick;

        private Plugin(string path)
        {
            PluginPath = path;
        }

        public static List<Plugin> Plugins { get; } = new List<Plugin>();

        public string PluginPath { get; }

        public bool IsValid { get; private set; }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFile(string name);

        public static Plugin Create(string path)
        {
            path = Path.GetFullPath(
                Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Plugins", path)
            );

            if (!File.Exists(path))
            {
                Log.Error($"Plugin '{path}' not found.");

                return null;
            }

            Log.Trace($"Loading plugin: {path}");

            Plugin p = new Plugin(path);
            p.Load();

            if (!p.IsValid)
            {
                Log.Warn($"Invalid plugin: {path}");

                return null;
            }

            Log.Trace($"Plugin: {path} loaded.");
            Plugins.Add(p);

            return p;
        }

        public void Load()
        {
            _recv = OnPluginRecv;
            _send = OnPluginSend;
            _recv_new = OnPluginRecv_new;
            _send_new = OnPluginSend_new;
            _getPacketLength = OnGetPacketLength;
            _getPlayerPosition = GetPlayerPosition;
            _castSpell = GameActions.CastSpell;
            _getStaticImage = GetStaticImage;
            _getUoFilePath = GetUOFilePath;
            _requestMove = RequestMove;
            _setTitle = SetWindowTitle;
            _get_static_data = GetStaticData;
            _get_tile_data = GetTileData;
            _get_cliloc = GetCliloc;

            // PORT DEVIATION (GUO): upstream asks SDL for the HWND behind its
            // window. Godot's window is not an SDL window; the game window's
            // Handle is already the HWND on Windows (DisplayServer's
            // WindowHandle), which is what a plugin wants it for.
            IntPtr hwnd = IntPtr.Zero;
            if (CUOEnviroment.IsWindows)
            {
                hwnd = Client.Game.Window.Handle;
            }

            PluginHeader header = new PluginHeader
            {
                ClientVersion = (int)Client.Game.UO.Version,
                Recv = Marshal.GetFunctionPointerForDelegate(_recv),
                Send = Marshal.GetFunctionPointerForDelegate(_send),
                GetPacketLength = Marshal.GetFunctionPointerForDelegate(_getPacketLength),
                GetPlayerPosition = Marshal.GetFunctionPointerForDelegate(_getPlayerPosition),
                CastSpell = Marshal.GetFunctionPointerForDelegate(_castSpell),
                GetStaticImage = Marshal.GetFunctionPointerForDelegate(_getStaticImage),
                HWND = hwnd,
                GetUOFilePath = Marshal.GetFunctionPointerForDelegate(_getUoFilePath),
                RequestMove = Marshal.GetFunctionPointerForDelegate(_requestMove),
                SetTitle = Marshal.GetFunctionPointerForDelegate(_setTitle),
                Recv_new = Marshal.GetFunctionPointerForDelegate(_recv_new),
                Send_new = Marshal.GetFunctionPointerForDelegate(_send_new),
                // PORT DEVIATION (GUO): upstream hands over its SDL_Window*.
                // There is no SDL window in a Godot process, and a plugin
                // that passed any other pointer to SDL would crash inside it;
                // zero is what SDL itself reports as "no window".
                SDL_Window = IntPtr.Zero,
                GetStaticData = Marshal.GetFunctionPointerForDelegate(_get_static_data),
                GetTileData = Marshal.GetFunctionPointerForDelegate(_get_tile_data),
                GetCliloc = Marshal.GetFunctionPointerForDelegate(_get_cliloc)
            };

            void* func = &header;

            if (
                Environment.OSVersion.Platform != PlatformID.Unix
                && Environment.OSVersion.Platform != PlatformID.MacOSX
            )
            {
                UnblockPath(Path.GetDirectoryName(PluginPath));
            }

            try
            {
                var assptr = Native.LoadLibrary(PluginPath);

                Log.Trace($"assembly: {assptr}");

                if (assptr == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error().ToString();
                    throw new Exception("Invalid Assembly, Attempting managed load.");
                }

                Log.Trace($"Searching for 'Install' entry point  -  {assptr}");

                var installPtr = Native.GetProcessAddress(assptr, "Install");

                Log.Trace($"Entry point: {installPtr}");

                if (installPtr == IntPtr.Zero)
                {
                    Native.FreeLibrary(assptr);
                    Console.WriteLine("free lib done");
                    throw new Exception("Invalid Entry Point, Attempting managed load.");
                }

                Marshal.GetDelegateForFunctionPointer<OnInstall>(installPtr)(func);

                Console.WriteLine(">>> ADDRESS {0}", header.OnInitialize);
            }
            catch
            {
                try
                {
                    Client.Game.PluginHost?.LoadPlugin(PluginPath);

                    //Client.Game.AssistantHost.OnSocketConnected += (o, e) => {
                    //    Client.Game.AssistantHost.PluginInitialize(PluginPath);
                    //};
                    //Client.Game.AssistantHost.Connect("127.0.0.1", 7777);

                    //Assembly asm = Assembly.LoadFile(PluginPath);
                    //Type type = asm.GetType("Assistant.Engine");

                    //if (type == null)
                    //{
                    //    Log.Error(
                    //        "Unable to find Plugin Type, API requires the public class Engine in namespace Assistant."
                    //    );

                    //    return;
                    //}

                    //MethodInfo meth = type.GetMethod(
                    //    "Install",
                    //    BindingFlags.Public | BindingFlags.Static
                    //);

                    //if (meth == null)
                    //{
                    //    Log.Error(
                    //        "Engine class missing public static Install method Needs 'public static unsafe void Install(PluginHeader *plugin)' "
                    //    );

                    //    return;
                    //}

                    //meth.Invoke(null, new object[] { (IntPtr)func });
                }
                catch (Exception err)
                {
                    Log.Error(
                        $"Plugin threw an error during Initialization. {err.Message} {err.StackTrace} {err.InnerException?.Message} {err.InnerException?.StackTrace}"
                    );

                    return;
                }
            }

            if (header.OnRecv != IntPtr.Zero)
            {
                _onRecv = Marshal.GetDelegateForFunctionPointer<OnPacketSendRecv>(header.OnRecv);
            }

            if (header.OnSend != IntPtr.Zero)
            {
                _onSend = Marshal.GetDelegateForFunctionPointer<OnPacketSendRecv>(header.OnSend);
            }

            if (header.OnHotkeyPressed != IntPtr.Zero)
            {
                _onHotkeyPressed = Marshal.GetDelegateForFunctionPointer<OnHotkey>(
                    header.OnHotkeyPressed
                );
            }

            if (header.OnMouse != IntPtr.Zero)
            {
                _onMouse = Marshal.GetDelegateForFunctionPointer<OnMouse>(header.OnMouse);
            }

            if (header.OnPlayerPositionChanged != IntPtr.Zero)
            {
                _onUpdatePlayerPosition =
                    Marshal.GetDelegateForFunctionPointer<OnUpdatePlayerPosition>(
                        header.OnPlayerPositionChanged
                    );
            }

            if (header.OnClientClosing != IntPtr.Zero)
            {
                _onClientClose = Marshal.GetDelegateForFunctionPointer<OnClientClose>(
                    header.OnClientClosing
                );
            }

            if (header.OnInitialize != IntPtr.Zero)
            {
                _onInitialize = Marshal.GetDelegateForFunctionPointer<OnInitialize>(
                    header.OnInitialize
                );
            }

            if (header.OnConnected != IntPtr.Zero)
            {
                _onConnected = Marshal.GetDelegateForFunctionPointer<OnConnected>(
                    header.OnConnected
                );
            }

            if (header.OnDisconnected != IntPtr.Zero)
            {
                _onDisconnected = Marshal.GetDelegateForFunctionPointer<OnDisconnected>(
                    header.OnDisconnected
                );
            }

            if (header.OnFocusGained != IntPtr.Zero)
            {
                _onFocusGained = Marshal.GetDelegateForFunctionPointer<OnFocusGained>(
                    header.OnFocusGained
                );
            }

            if (header.OnFocusLost != IntPtr.Zero)
            {
                _onFocusLost = Marshal.GetDelegateForFunctionPointer<OnFocusLost>(
                    header.OnFocusLost
                );
            }

            if (header.Tick != IntPtr.Zero)
            {
                _tick = Marshal.GetDelegateForFunctionPointer<OnTick>(header.Tick);
            }

            if (header.OnRecv_new != IntPtr.Zero)
            {
                _onRecv_new = Marshal.GetDelegateForFunctionPointer<OnPacketSendRecv_new>(
                    header.OnRecv_new
                );
            }

            if (header.OnSend_new != IntPtr.Zero)
            {
                _onSend_new = Marshal.GetDelegateForFunctionPointer<OnPacketSendRecv_new>(
                    header.OnSend_new
                );
            }

            if (header.OnDrawCmdList != IntPtr.Zero)
            {
                _draw_cmd_list = Marshal.GetDelegateForFunctionPointer<OnDrawCmdList>(
                    header.OnDrawCmdList
                );
            }

            if (header.OnWndProc != IntPtr.Zero)
            {
                _on_wnd_proc = Marshal.GetDelegateForFunctionPointer<OnWndProc>(header.OnWndProc);
            }

            IsValid = true;

            if (_onInitialize != null)
            {
                _onInitialize();
            }
        }

        private static string GetUOFilePath()
        {
            return Settings.GlobalSettings.UltimaOnlineDirectory;
        }

        private static void SetWindowTitle(string str)
        {
            Client.Game.SetWindowTitle(str);
        }

        private static bool GetStaticData(
            int index,
            ref ulong flags,
            ref byte weight,
            ref byte layer,
            ref int count,
            ref ushort animid,
            ref ushort lightidx,
            ref byte height,
            ref string name
        )
        {
            if (index >= 0 && index < ArtLoader.MAX_STATIC_DATA_INDEX_COUNT)
            {
                ref StaticTiles st = ref Client.Game.UO.FileManager.TileData.StaticData[index];

                flags = (ulong)st.Flags;
                weight = st.Weight;
                layer = st.Layer;
                count = st.Count;
                animid = st.AnimID;
                lightidx = st.LightIndex;
                height = st.Height;
                name = st.Name;

                return true;
            }

            return false;
        }

        private static bool GetTileData(
            int index,
            ref ulong flags,
            ref ushort textid,
            ref string name
        )
        {
            if (index >= 0 && index < ArtLoader.MAX_STATIC_DATA_INDEX_COUNT)
            {
                ref LandTiles st = ref Client.Game.UO.FileManager.TileData.LandData[index];

                flags = (ulong)st.Flags;
                textid = st.TexID;
                name = st.Name;

                return true;
            }

            return false;
        }

        private static bool GetCliloc(int cliloc, string args, bool capitalize, out string buffer)
        {
            buffer = Client.Game.UO.FileManager.Clilocs.Translate(cliloc, args, capitalize);

            return buffer != null;
        }

        private static void GetStaticImage(ushort g, ref CUO_API.ArtInfo info)
        {
            //Client.Game.UO.FileManager.Arts.TryGetEntryInfo(g, out long address, out long size, out long compressedsize);
            //info.Address = address;
            //info.Size = size;
            //info.CompressedSize = compressedsize;
        }

        internal static bool RequestMove(int dir, bool run)
        {
            return Client.Game.UO.World.Player.Walk((Direction)dir, run);
        }

        internal static bool GetPlayerPosition(out int x, out int y, out int z)
        {
            if (Client.Game.UO.World.Player != null)
            {
                x = Client.Game.UO.World.Player.X;
                y = Client.Game.UO.World.Player.Y;
                z = Client.Game.UO.World.Player.Z;

                return true;
            }

            x = y = z = 0;

            return false;
        }

        internal static void Tick()
        {
            Client.Game.PluginHost?.Tick();

            foreach (Plugin t in Plugins)
            {
                if (t._tick != null)
                {
                    t._tick();
                }
            }
        }

        internal static bool ProcessRecvPacket(byte[] data, ref int length)
        {
            bool result = Client.Game.PluginHost?.PacketIn(new ArraySegment<byte>(data, 0, length)) ?? true;

            foreach (Plugin plugin in Plugins)
            {
                if (plugin._onRecv_new != null)
                {
                    byte[] tmp = new byte[length];
                    Array.Copy(data, tmp, length);

                    if (!plugin._onRecv_new(tmp, ref length))
                    {
                        result = false;
                    }

                    Array.Copy(tmp, data, length);
                }
                else if (plugin._onRecv != null)
                {
                    byte[] tmp = new byte[length];
                    Array.Copy(data, tmp, length);

                    if (!plugin._onRecv(ref tmp, ref length))
                    {
                        result = false;
                    }

                    Array.Copy(tmp, data, length);
                }
            }

            return result;
        }

        internal static bool ProcessSendPacket(ref Span<byte> message)
        {
            bool result = Client.Game.PluginHost?.PacketOut(message) ?? true;

            foreach (Plugin plugin in Plugins)
            {
                if (plugin._onSend_new != null)
                {
                    var tmp = message.ToArray();
                    var length = tmp.Length;

                    if (!plugin._onSend_new(tmp, ref length))
                    {
                        result = false;
                    }

                    message = message.Slice(0, length);
                    tmp.AsSpan(0, length).CopyTo(message);
                }
                else if (plugin._onSend != null)
                {
                    var tmp = message.ToArray();
                    var length = tmp.Length;

                    if (!plugin._onSend(ref tmp, ref length))
                    {
                        result = false;
                    }

                    message = message.Slice(0, length);
                    tmp.AsSpan(0, length).CopyTo(message);
                }
            }

            return result;
        }

        internal static void OnClosing()
        {
            Client.Game.PluginHost?.Closing();

            for (int i = 0; i < Plugins.Count; i++)
            {
                if (Plugins[i]._onClientClose != null)
                {
                    Plugins[i]._onClientClose();
                }
            }

            Plugins.Clear();
        }

        internal static void OnFocusGained()
        {
            Client.Game.PluginHost?.FocusGained();

            foreach (Plugin t in Plugins)
            {
                if (t._onFocusGained != null)
                {
                    t._onFocusGained();
                }
            }
        }

        internal static void OnFocusLost()
        {
            Client.Game.PluginHost?.FocusLost();

            foreach (Plugin t in Plugins)
            {
                if (t._onFocusLost != null)
                {
                    t._onFocusLost();
                }
            }
        }

        internal static void OnConnected()
        {
            Client.Game.PluginHost?.Connected();

            foreach (Plugin t in Plugins)
            {
                if (t._onConnected != null)
                {
                    t._onConnected();
                }
            }
        }

        internal static void OnDisconnected()
        {
            Client.Game.PluginHost?.Disconnected();

            foreach (Plugin t in Plugins)
            {
                if (t._onDisconnected != null)
                {
                    t._onDisconnected();
                }
            }
        }

        internal static bool ProcessHotkeys(int key, int mod, bool ispressed)
        {
            if ((!Client.Game.UO.World?.InGame ?? false) || UIManager.SystemChat != null && (
                        ProfileManager.CurrentProfile != null
                            && ProfileManager.CurrentProfile.ActivateChatAfterEnter
                            && UIManager.SystemChat.IsActive
                        || UIManager.KeyboardFocusControl != UIManager.SystemChat.TextBoxControl
                    )
            )
            {
                return true;
            }

            var ok = Client.Game.PluginHost?.Hotkey(key, mod, ispressed);

            bool result = ok ?? true;

            foreach (Plugin plugin in Plugins)
            {
                if (
                    plugin._onHotkeyPressed != null && !plugin._onHotkeyPressed(key, mod, ispressed)
                )
                {
                    result = false;
                }
            }

            return result;
        }

        internal static void ProcessMouse(int button, int wheel)
        {
            Client.Game.PluginHost?.Mouse(button, wheel);

            foreach (Plugin plugin in Plugins)
            {
                plugin._onMouse?.Invoke(button, wheel);
            }
        }

        // PORT DEVIATION (GUO): upstream takes the GraphicsDevice to draw the
        // command lists onto. There is no device; see HandleCmdList.
        internal static void ProcessDrawCmdList()
        {
            IntPtr cmdList = IntPtr.Zero;
            var len = 0;
            Client.Game.PluginHost?.GetCommandList(out cmdList, out len);
            if (Client.Game.PluginHost != null && len != 0 && cmdList != IntPtr.Zero)
            {
                HandleCmdList(cmdList, len, Client.Game.PluginHost.GfxResources);
            }

            foreach (Plugin plugin in Plugins)
            {
                if (plugin._draw_cmd_list != null)
                {
                    len = 0;
                    plugin._draw_cmd_list.Invoke(out cmdList, ref len);

                    if (len != 0 && cmdList != IntPtr.Zero)
                    {
                        HandleCmdList(cmdList, len, plugin._resources);
                    }
                }
            }
        }

        internal static int ProcessWndProc(SDL.SDL_Event* e)
        {
            var result = Client.Game.PluginHost?.SdlEvent(e) ?? 0;

            foreach (Plugin plugin in Plugins)
            {
                if (plugin._on_wnd_proc != null)
                {
                    result |= plugin._on_wnd_proc(e);
                }
            }

            return result;
        }

        internal static void UpdatePlayerPosition(int x, int y, int z)
        {
            Client.Game.PluginHost?.UpdatePlayerPosition(x, y, z);

            foreach (Plugin plugin in Plugins)
            {
                try
                {
                    // TODO: need fixed on razor side
                    // if you quick entry (0.5-1 sec after start, without razor window loaded) - breaks CUO.
                    // With this fix - the razor does not work, but client does not crashed.
                    if (plugin._onUpdatePlayerPosition != null)
                    {
                        plugin._onUpdatePlayerPosition(x, y, z);
                    }
                }
                catch
                {
                    Log.Error("Plugin initialization failed, please re login");
                }
            }
        }

        internal static short OnGetPacketLength(int id)
        {
            return NetClient.Socket.PacketsTable.GetPacketLength(id);
        }

        internal static bool OnPluginRecv(ref byte[] data, ref int length)
        {
            lock (PacketHandlers.Handler)
            {
                PacketHandlers.Handler.Append(data.AsSpan(0, length), true);
            }

            return true;
        }

        internal static bool OnPluginSend(ref byte[] data, ref int length)
        {
            if (NetClient.Socket.IsConnected)
            {
                NetClient.Socket.Send(data.AsSpan(0, length), true);
            }

            return true;
        }

        internal static bool OnPluginRecv_new(IntPtr buffer, ref int length)
        {
            if (buffer != IntPtr.Zero && length > 0)
            {
                lock (PacketHandlers.Handler)
                {
                    PacketHandlers.Handler.Append(new Span<byte>(buffer.ToPointer(), length), true);
                }
            }

            return true;
        }

        internal static bool OnPluginSend_new(IntPtr buffer, ref int length)
        {
            if (buffer != IntPtr.Zero && length > 0)
            {
                NetClient.Socket.Send(new Span<byte>((void*)buffer, length), true);
            }

            return true;
        }

        //Code from https://stackoverflow.com/questions/6374673/unblock-file-from-within-net-4-c-sharp
        private static void UnblockPath(string path)
        {
            string[] files = Directory.GetFiles(path);
            string[] dirs = Directory.GetDirectories(path);

            foreach (string file in files)
            {
                if (file.EndsWith("dll") || file.EndsWith("exe"))
                {
                    UnblockFile(file);
                }
            }

            foreach (string dir in dirs)
            {
                UnblockPath(dir);
            }
        }

        private static bool UnblockFile(string fileName)
        {
            return DeleteFile(fileName + ":Zone.Identifier");
        }

        private static bool _cmdListWarned;

        /// <summary>
        /// Would draw a plugin's command list. Does nothing.
        /// </summary>
        /// <remarks>
        /// PORT GAP (GUO). Upstream's HandleCmdList is ~500 lines that replay a
        /// plugin's command stream -- vertex and index buffers, BasicEffect,
        /// render targets, blend, depth, stencil and sampler state -- straight
        /// onto FNA's GraphicsDevice. That is a second renderer, rewrite tier
        /// against ADR-0002, and a large one.
        ///
        /// Only a plugin that fills PluginHeader.OnDrawCmdList, or a hosted
        /// managed plugin whose host returns a list, ever produces one; the
        /// assistants run their own WinForms/WPF windows.
        /// Everything else in this file -- loading, packets both ways, hotkeys,
        /// mouse, position, focus, connect and disconnect -- is ported.
        ///
        /// So a command list is fetched exactly as upstream fetches it, so the
        /// plugin sees the same calls, and then dropped. The first time one
        /// arrives, that is said out loud once.
        /// </remarks>
        private static void HandleCmdList(
            IntPtr ptr,
            int length,
            IDictionary<IntPtr, Godot.Resource> resources
        )
        {
            if (ptr == IntPtr.Zero || length <= 0)
            {
                return;
            }

            if (!_cmdListWarned)
            {
                _cmdListWarned = true;

                Log.Warn(
                    "a plugin sent a draw command list; drawing plugin command lists is not ported (PORT GAP, see Plugin.HandleCmdList)"
                );
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnInstall(void* header);

        [return: MarshalAs(UnmanagedType.I1)]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool OnPacketSendRecv_new(byte[] data, ref int length);

        [return: MarshalAs(UnmanagedType.I1)]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool OnPacketSendRecv_new_intptr(IntPtr data, ref int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OnDrawCmdList([Out] out IntPtr cmdlist, ref int size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OnWndProc(SDL.SDL_Event* ev);

        [return: MarshalAs(UnmanagedType.I1)]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool OnGetStaticData(
            int index,
            ref ulong flags,
            ref byte weight,
            ref byte layer,
            ref int count,
            ref ushort animid,
            ref ushort lightidx,
            ref byte height,
            ref string name
        );

        [return: MarshalAs(UnmanagedType.I1)]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool OnGetTileData(
            int index,
            ref ulong flags,
            ref ushort textid,
            ref string name
        );

        [return: MarshalAs(UnmanagedType.I1)]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool OnGetCliloc(
            int cliloc,
            [MarshalAs(UnmanagedType.LPStr)] string args,
            bool capitalize,
            [Out] [MarshalAs(UnmanagedType.LPStr)] out string buffer
        );

        private struct PluginHeader
        {
            public int ClientVersion;
            public IntPtr HWND;
            public IntPtr OnRecv;
            public IntPtr OnSend;
            public IntPtr OnHotkeyPressed;
            public IntPtr OnMouse;
            public IntPtr OnPlayerPositionChanged;
            public IntPtr OnClientClosing;
            public IntPtr OnInitialize;
            public IntPtr OnConnected;
            public IntPtr OnDisconnected;
            public IntPtr OnFocusGained;
            public IntPtr OnFocusLost;
            public IntPtr GetUOFilePath;
            public IntPtr Recv;
            public IntPtr Send;
            public IntPtr GetPacketLength;
            public IntPtr GetPlayerPosition;
            public IntPtr CastSpell;
            public IntPtr GetStaticImage;
            public IntPtr Tick;
            public IntPtr RequestMove;
            public IntPtr SetTitle;

            public IntPtr OnRecv_new,
                OnSend_new,
                Recv_new,
                Send_new;

            public IntPtr OnDrawCmdList;
            public IntPtr SDL_Window;
            public IntPtr OnWndProc;
            public IntPtr GetStaticData;
            public IntPtr GetTileData;
            public IntPtr GetCliloc;
        }
    }
}
