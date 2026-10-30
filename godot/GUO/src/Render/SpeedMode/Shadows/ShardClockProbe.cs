// SPDX-License-Identifier: BSD-2-Clause
// Offline real-packet framing and World.Clear lifecycle; no socket connection.
using System;
using System.IO;
using Godot;
using GUO.Game;
using GUO.Network;
using GUO.Utility;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class ShardClockProbe : Node
{
    public override void _Ready()
    {
        // World constructs ContainerManager, which may create its default file.
        // Match SpellLightingProbe: preserve existing data and retire only this
        // disconnected fixture's newly generated file and empty directories.
        string dataDir=Path.Combine(CUOEnviroment.ExecutablePath,"Data");
        string clientDir=Path.Combine(dataDir,"Client");
        string containerFile=Path.Combine(clientDir,"containers.txt");
        bool hadDataDir=Directory.Exists(dataDir),hadClientDir=Directory.Exists(clientDir);
        bool hadContainerFile=File.Exists(containerFile);
        try {
            var world=new World();
            var socket=new NetClient(); socket.Load(ClientVersion.CV_7090,0);
            Check(!socket.IsConnected && !world.ShardClock.Valid,"cold world and disconnected fixture");
            var handler=PacketHandlers.Handler;
            Check(socket.PacketsTable.GetPacketLength(0x5B)==4,"unchanged wire length");
            handler.Append(new byte[] { 0x5B,12 },true);
            Check(handler.ParsePackets(socket,world,Span<byte>.Empty)==0 && !world.ShardClock.Valid,"fragment waits for complete packet");
            handler.Append(new byte[] { 34,56 },true);
            Check(handler.ParsePackets(socket,world,Span<byte>.Empty)==1 && world.ShardClock.Valid &&
                world.ShardClock.Hour==12 && world.ShardClock.Minute==34 && world.ShardClock.Second==56,"real 0x5B retains time");
            handler.Append(new byte[] { 0x5B,24,0,0 },true);
            Check(handler.ParsePackets(socket,world,Span<byte>.Empty)==1 && !world.ShardClock.Valid,"invalid hour clears stamp");
            handler.Append(new byte[] { 0x5B,8,60,0,0x5B,9,1,2 },true);
            Check(handler.ParsePackets(socket,world,Span<byte>.Empty)==2 && world.ShardClock.Hour==9 &&
                world.ShardClock.Minute==1 && world.ShardClock.Second==2,"invalid minute does not desynchronise next packet");
            world.Clear();
            Check(!world.ShardClock.Valid && !socket.IsConnected,"logout clears stamp without connecting");
            GD.Print("[shard-clock-probe] PASS framing=4 fragment=pass validation=pass next-packet=pass logout-clear=pass socket=disconnected");
            GetTree().Quit(0);
        }
        catch(Exception error) { GD.PrintErr("[shard-clock-probe] FAIL "+error); GetTree().Quit(1); }
        finally {
            if(!hadContainerFile && File.Exists(containerFile)) File.Delete(containerFile);
            if(!hadClientDir && Directory.Exists(clientDir) && Directory.GetFileSystemEntries(clientDir).Length==0)
                Directory.Delete(clientDir);
            if(!hadDataDir && Directory.Exists(dataDir) && Directory.GetFileSystemEntries(dataDir).Length==0)
                Directory.Delete(dataDir);
        }
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
}
