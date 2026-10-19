// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using GUO.Configuration;
using GUO.Utility;

namespace GUO.Network.Mods;

/// <summary>Packet mods inside the Godot process, without a shard or a UO install (ADR-0038):
/// the sample mod loads through the client's loader, and a packet goes through the real
/// PacketsTable and PacketHandlers seams, with the option off and on.
/// Needs the sample built first: dotnet build docs/modding/samples/announce/Announce.csproj.</summary>
public partial class ModsProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    private const ClientVersion Version = ClientVersion.CV_7090;
    private const byte Probe = 0xF8; // no client handler; the probe installs one for the run

    internal static bool Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "guo-mods-probe-" + Guid.NewGuid().ToString("N"));
        string ip = Settings.GlobalSettings.IP;
        bool loadMods = Settings.GlobalSettings.LoadMods;
        try
        {
            string sample = SampleFolder();
            Require(sample != null, "the sample mod is not built; run: dotnet build docs/modding/samples/announce/Announce.csproj");
            string mods = Path.Combine(temporary, "Mods");
            string announce = Path.Combine(mods, "announce");
            Directory.CreateDirectory(announce);
            File.Copy(Path.Combine(sample, "Announce.dll"), Path.Combine(announce, "Announce.dll"));
            File.Copy(Path.Combine(SampleSource(), "mod.json"), Path.Combine(announce, "mod.json"));

            Require(!new Settings().LoadMods, "a new settings.json must have Load mods off");
            Settings.GlobalSettings.IP = "127.0.0.1";
            NetClient.Socket.Load(Version, 0);
            short clientLength = new PacketsTable(Version).GetPacketLength(0xF9);
            var seen = new List<byte[]>();
            PacketHandlers.Handler.Add(Probe, (GUO.Game.World _, ref GUO.IO.StackDataReader p) => seen.Add(p.Buffer.ToArray()));
            ClientMods.ProbeMessages = new List<(string, ushort)>();
            short probeLength = NetClient.Socket.PacketsTable.GetPacketLength(Probe);
            byte[] after = new byte[probeLength == -1 ? 3 : probeLength];
            after[0] = Probe;
            if (probeLength == -1) after[2] = 3;
            byte[] packet = Announce("Server restart in 5 minutes", 0x35);

            // Off: nothing loads, and the client frames 0xF9 by its own table.
            ClientMods.Load(Version, mods, enabled: false);
            Require(PacketModRegistry.Current == null && ClientMods.Results.Count == 0, "off: no mod may load");
            Require(NetClient.Socket.PacketsTable.GetPacketLength(0xF9) == clientLength, "off: 0xF9 must keep the client's length");

            // On: the sample loads in this process, with the client's copy of the ABI.
            ClientMods.Load(Version, mods, enabled: true);
            Require(ClientMods.Results.Count == 1 && ClientMods.Results[0].Loaded, "on: the sample must load: " + string.Join("; ", ClientMods.Results.Select(r => r.Message)));
            Require(NetClient.Socket.PacketsTable.GetPacketLength(0xF9) == -1, "on: 0xF9 must take the mod's dynamic length");

            PacketHandlers.Handler.Append(packet.Concat(after).ToArray(), true);
            PacketHandlers.Handler.ParsePackets(NetClient.Socket, null, Span<byte>.Empty);
            Require(ClientMods.ProbeMessages.Count == 1 && ClientMods.ProbeMessages[0] == ("Server restart in 5 minutes", 0x35), "on: the mod must print the announcement");
            Require(seen.Count == 1 && seen[0].SequenceEqual(after), "on: the packet after the mod's must reach the client's handler intact");

            // Another shard: the same client parses as if no mod were installed.
            Settings.GlobalSettings.IP = "play.example.org";
            Require(NetClient.Socket.PacketsTable.GetPacketLength(0xF9) == clientLength, "other shard: the mod must not apply");

            // A broken mod.json is refused whole, with a reason a user can act on.
            File.WriteAllText(Path.Combine(announce, "mod.json"), File.ReadAllText(Path.Combine(SampleSource(), "mod.json")).Replace("\"entry\"", "\"entyr\""));
            Settings.GlobalSettings.IP = "127.0.0.1";
            ClientMods.Load(Version, mods, enabled: true);
            Require(PacketModRegistry.Current == null && ClientMods.Results.Count == 1 && !ClientMods.Results[0].Loaded
                && ClientMods.Results[0].Message.Contains("unknown field \"entyr\""), "a typo in mod.json must refuse the mod: " + ClientMods.Results.FirstOrDefault().Message);

            GD.Print("[mods] PASS: off by default, off parses as the client, sample loads in-process, length and handler seams, shard guard, strict mod.json");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr("[mods] FAIL: " + ex.Message);
            return false;
        }
        finally
        {
            PacketModRegistry.Current = null;
            ClientMods.ProbeMessages = null;
            PacketHandlers.Handler.Add(Probe, null);
            Settings.GlobalSettings.IP = ip;
            Settings.GlobalSettings.LoadMods = loadMods;
            try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] Announce(string text, ushort hue)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] packet = new byte[5 + body.Length];
        packet[0] = 0xF9;
        packet[1] = (byte)(packet.Length >> 8);
        packet[2] = (byte)packet.Length;
        packet[3] = (byte)(hue >> 8);
        packet[4] = (byte)hue;
        body.CopyTo(packet, 5);
        return packet;
    }

    private static string SampleSource() =>
        Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "docs", "modding", "samples", "announce"));

    private static string SampleFolder() =>
        new[] { "Debug", "Release" }.Select(c => Path.Combine(SampleSource(), "bin", c, "net8.0"))
            .FirstOrDefault(f => File.Exists(Path.Combine(f, "Announce.dll")));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
