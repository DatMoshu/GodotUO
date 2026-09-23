// A managed test plugin, shaped like Razor: a .NET Framework assembly with a
// public static Assistant.Engine.Install(PluginHeader*), no native export,
// built against upstream's cuoapi.dll. GUO cannot call it directly; it only
// loads through the ported bootstrap in tools/plugin_host, which is the path
// this exists to exercise.
//
// It registers for packets both ways, position changes and the lifecycle
// callbacks, asks the client where the player is, and logs what it sees for
// tools/plugin_probe/run.py. Every packet is passed on untouched, except one
// ping each way once the player is in the world: that one is grown the way
// Razor's filters can grow a packet, by handing back a longer array, and the
// host must drop it rather than write past the client's buffer.

using System;
using System.IO;
using System.Runtime.InteropServices;
using CUO_API;

namespace Assistant
{
    public static class Engine
    {
        // Held here so the GC never collects a delegate the host still calls.
        private static OnPacketSendRecv _onRecv, _onSend;
        private static OnUpdatePlayerPosition _onPosition;
        private static OnInitialize _onInitialize;
        private static OnConnected _onConnected;
        private static OnDisconnected _onDisconnected;
        private static OnClientClose _onClose;
        private static OnTick _onTick;

        private static OnGetPlayerPosition _getPlayerPosition;
        private static OnGetPacketLength _getPacketLength;

        private static StreamWriter _log;
        private static long _recv, _send, _positions, _ticks;
        private static bool _grewRecv, _grewSend;

        // The ping (0x73) is sent every second in the world and echoed back,
        // so losing one of each is harmless to the session.
        private const byte GrowId = 0x73;
        private const int GrowBy = 64;

        public static unsafe void Install(PluginHeader* plugin)
        {
            var dir = Environment.GetEnvironmentVariable("GUO_PLUGIN_PROBE_LOG_DIR");
            var path = string.IsNullOrEmpty(dir) ? "guo_probe_managed.log" : Path.Combine(dir, "managed.log");
            _log = new StreamWriter(path, false) { AutoFlush = true };

            _getPlayerPosition = Marshal.GetDelegateForFunctionPointer<OnGetPlayerPosition>(plugin->GetPlayerPosition);
            _getPacketLength = Marshal.GetDelegateForFunctionPointer<OnGetPacketLength>(plugin->GetPacketLength);

            Log(
                $"install clr={Environment.Version} client_version=0x{plugin->ClientVersion:X8} "
                + $"hwnd={plugin->HWND} is_window={(plugin->HWND != IntPtr.Zero && IsWindow(plugin->HWND))} "
                + $"uo_path={Marshal.GetDelegateForFunctionPointer<OnGetUOFilePath>(plugin->GetUOFilePath)()}"
            );

            _onRecv = OnRecv;
            _onSend = OnSend;
            _onPosition = OnPosition;
            _onInitialize = () => Log("initialize");
            _onConnected = () => Log("connected");
            _onDisconnected = () => Log("disconnected");
            _onClose = () =>
            {
                Log($"totals recv={_recv} send={_send} positions={_positions} ticks={_ticks}");
                Log("closing");
            };
            _onTick = () => _ticks++;

            plugin->OnRecv = Marshal.GetFunctionPointerForDelegate(_onRecv);
            plugin->OnSend = Marshal.GetFunctionPointerForDelegate(_onSend);
            plugin->OnPlayerPositionChanged = Marshal.GetFunctionPointerForDelegate(_onPosition);
            plugin->OnInitialize = Marshal.GetFunctionPointerForDelegate(_onInitialize);
            plugin->OnConnected = Marshal.GetFunctionPointerForDelegate(_onConnected);
            plugin->OnDisconnected = Marshal.GetFunctionPointerForDelegate(_onDisconnected);
            plugin->OnClientClosing = Marshal.GetFunctionPointerForDelegate(_onClose);
            plugin->Tick = Marshal.GetFunctionPointerForDelegate(_onTick);
        }

        private static bool OnRecv(ref byte[] data, ref int length)
        {
            if (++_recv <= 10)
                Log($"recv id=0x{data[0]:X2} len={length} table_len={_getPacketLength(data[0])}");

            if (!_grewRecv && _positions > 0 && data[0] == GrowId)
            {
                _grewRecv = true;
                Grow("recv", ref data, ref length);
            }

            return true;
        }

        private static bool OnSend(ref byte[] data, ref int length)
        {
            if (++_send <= 10)
                Log($"send id=0x{data[0]:X2} len={length}");

            if (!_grewSend && _positions > 0 && data[0] == GrowId)
            {
                _grewSend = true;
                Grow("send", ref data, ref length);
            }

            return true;
        }

        private static void OnPosition(int x, int y, int z)
        {
            if (++_positions <= 20)
            {
                var ok = _getPlayerPosition(out var px, out var py, out var pz);
                Log($"position x={x} y={y} z={z} get_player_position ok={(ok ? 1 : 0)} x={px} y={py} z={pz} recv={_recv} send={_send}");
            }
        }

        private static void Grow(string way, ref byte[] data, ref int length)
        {
            var grown = new byte[length + GrowBy];
            Array.Copy(data, grown, length);
            Log($"grow {way} id=0x{data[0]:X2} len={length} to={length + GrowBy}");
            data = grown;
            length += GrowBy;
        }

        private static void Log(string line)
        {
            _log?.WriteLine(line);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
    }
}
