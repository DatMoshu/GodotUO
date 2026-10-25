// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Godot;
using GUO.Network;
using GUO.Renderer;
using GUO.Utility;

namespace GUO.Editor;

/// <summary>Bounded packet/counter correctness checks; no shard, account or FPS sampling.
/// Run the matching scene with godot-console --headless --path godot/GUO.</summary>
public partial class DiagnosticsFunctionalProbe : Node
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly List<string> Trace = new();
    private static PacketHandlers _parser;
    private static int _checks;

    public override void _Ready()
    {
        var game = new GameController(embedded: true);
        try
        {
            Require(Client.Game == null && Plugin.Plugins.Count == 0, "isolated process");
            Client.AttachEmbedded(game);
            string off = Exercise(false);
            string on = Exercise(true);
            Require(off == on, "counter enablement preserves packet bytes, callback order and dispatch counts");
            GD.Print($"[diagnostics functional] PASS: {_checks} checks; fragmented/compressed loopback, plugin mutation/veto/injection, queued send, reconnect, scope reset; no FPS sampling");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[diagnostics functional] FAIL: " + ex);
            GetTree().Quit(1);
        }
        finally
        {
            PerfStages.Enabled = false;
            Array.Clear(PerfStages.Ms);
            Array.Clear(PerfStages.Drawn);
            Plugin.Plugins.Clear();
            Client.DetachEmbedded(game);
            game.Free();
        }
    }

    private static string Exercise(bool enabled)
    {
        Trace.Clear();
        Array.Clear(PerfStages.Ms);
        Array.Clear(PerfStages.Drawn);
        PerfStages.Enabled = enabled;
        _parser = new PacketHandlers();
        _parser.Add(0x03, (GUO.Game.World _, ref GUO.IO.StackDataReader p) => Trace.Add("dispatch:" + Convert.ToHexString(p.Buffer)));
        _parser.Add(0x04, (GUO.Game.World _, ref GUO.IO.StackDataReader p) => Trace.Add("injected:" + Convert.ToHexString(p.Buffer)));
        AddPlugin(nameof(FirstPlugin));
        AddPlugin(nameof(SecondPlugin));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var socket = new NetClient();
        socket.Load(ClientVersion.CV_7090, 0);
        try
        {
            socket.Connect("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
            using var peer = listener.AcceptTcpClient();
            peer.ReceiveTimeout = 3000;
            var stream = peer.GetStream();
            socket.EnableCompression();
            byte[] wire = Compress(new byte[] { 3, 0, 4, 1, 3, 0, 4, 2, 3, 0, 4, 3 });
            int dispatched = 0;
            // Every compressed byte arrives separately, including partial Huffman codes/headers.
            foreach (byte b in wire)
            {
                stream.WriteByte(b);
                WaitReadable(socket);
                byte[] bytes;
                using (new PerfStages.Scope(PerfStages.Stage.NetworkReceive))
                    bytes = socket.CollectAvailableData().ToArray();
                using (new PerfStages.Scope(PerfStages.Stage.NetworkParse))
                    dispatched += _parser.ParsePackets(socket, null, bytes);
            }
            Require(dispatched == 3, "two accepted packets and one plugin-injected packet");
            Require(Trace.SequenceEqual(new[] { "first:1", "second:9", "dispatch:03000409", "first:2", "second:2", "first:3", "second:3", "dispatch:03000403", "injected:0407" }), "mutation, veto still calls next plugin, injection bypass/order");
            byte[] outgoing = Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray();
            socket.Send(outgoing, ignorePlugin: true, skipEncryption: true);
            Require(!peer.Client.Poll(1000, SelectMode.SelectRead), "send stays queued before Flush");
            using (new PerfStages.Scope(PerfStages.Stage.NetworkFlush)) socket.Flush();
            var received = new byte[outgoing.Length];
            stream.ReadExactly(received);
            Require(received.SequenceEqual(outgoing), "Flush retains queued byte order across sending-buffer boundaries");
            Require(socket.Statistics.TotalBytesSent == outgoing.Length, "send statistics");
            socket.Disconnect();
            socket.Connect("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
            using var reconnected = listener.AcceptTcpClient();
            Require(socket.IsConnected && socket.Statistics.TotalBytesSent == 0, "reconnect resets statistics");
            reconnected.GetStream().Write(new byte[] { 4, 8 });
            WaitReadable(socket);
            // Connect resets compression state via the preceding Disconnect.
            Require(socket.CollectAvailableData().SequenceEqual(new byte[] { 4, 8 }), "reconnect reads plain traffic");
            var totals = new double[(int)PerfStages.Stage.Count];
            var counts = new int[totals.Length];
            PerfStages.Take(totals, counts);
            foreach (var stage in new[] { PerfStages.Stage.NetworkReceive, PerfStages.Stage.NetworkDecompress, PerfStages.Stage.NetworkParse, PerfStages.Stage.PluginReceive, PerfStages.Stage.NetworkDispatch, PerfStages.Stage.NetworkFlush })
                Require(enabled ? totals[(int)stage] > 0 : totals[(int)stage] == 0, "stage enablement: " + stage);
            Require(PerfStages.Ms.All(x => x == 0) && counts.All(x => x == 0), "Take clears stages without inventing draw counts");
            return string.Join("|", Trace);
        }
        finally
        {
            if (socket.IsConnected) socket.Disconnect();
            listener.Stop();
            Plugin.Plugins.Clear();
        }
    }

    private static bool FirstPlugin(byte[] data, ref int length)
    {
        Trace.Add("first:" + data[3]);
        if (data[3] == 1) data[3] = 9;
        if (data[3] == 3) _parser.Append(new byte[] { 4, 7 }, true);
        return data[3] != 2;
    }

    private static bool SecondPlugin(byte[] data, ref int length)
    {
        Trace.Add("second:" + data[3]);
        return true;
    }

    private static void AddPlugin(string method)
    {
        // Probe-only reflection avoids adding a callback injection API to the port.
        var plugin = (Plugin)Activator.CreateInstance(typeof(Plugin), Private, null, new object[] { "functional-probe" }, null);
        var field = typeof(Plugin).GetField("_onRecv_new", Private);
        field.SetValue(plugin, Delegate.CreateDelegate(field.FieldType, typeof(DiagnosticsFunctionalProbe).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)));
        Plugin.Plugins.Add(plugin);
    }

    private static void WaitReadable(NetClient socket)
    {
        var wrapper = typeof(NetClient).GetField("_socket", Private).GetValue(socket);
        var tcp = (TcpClient)wrapper.GetType().GetField("_socket", Private).GetValue(wrapper);
        Require(SpinWait.SpinUntil(() => tcp.Available > 0, 3000), "loopback bytes arrived");
    }

    private static byte[] Compress(byte[] plain)
    {
        // Invert the production decoder's code tree for this synthetic fixture.
        int[] tree = (int[])typeof(Huffman).GetField("_decTree", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var codes = new Dictionary<int, string>();
        void Walk(int node, string bits)
        {
            for (int edge = 0; edge < 2; edge++)
            {
                int child = tree[node * 2 + edge];
                string code = bits + (edge == 0 ? "1" : "0");
                if (child <= 0) codes[-child] = code;
                else Walk(child, code);
            }
        }
        Walk(0, "");
        string encoded = string.Concat(plain.Select(b => codes[b])) + codes[256];
        encoded = encoded.PadRight((encoded.Length + 7) / 8 * 8, '0');
        return Enumerable.Range(0, encoded.Length / 8).Select(i => Convert.ToByte(encoded.Substring(i * 8, 8), 2)).ToArray();
    }

    private static void Require(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }
}
#endif
