// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;
using RD = Godot.RenderingDevice;

namespace GUO.Host;

public partial class LightingSpeedProbe : Node
{
    public static void Run(Node parent) { parent.AddChild(new LightingSpeedProbe()); }
    public override async void _Ready()
    {
        int exit = 1;
        var device = new OrderedWorldDevice();
        ImageTexture art = null;
        try
        {
            string output = null;
            foreach (var arg in OS.GetCmdlineUserArgs())
                if (arg.StartsWith("--lighting-speed-out=")) output = arg.Substring("--lighting-speed-out=".Length);
            if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Pass --lighting-speed-out=ABSOLUTE_EVIDENCE_FOLDER");
            Directory.CreateDirectory(output);
            var profile = new Configuration.Profile();
            if (profile.LightingSpeedMode || profile.SpeedModeNightFloor != 3 || profile.SpeedModeSoften != 3)
                throw new InvalidOperationException("Speed mode defaults changed.");
            if (Marshal.SizeOf<SurfaceRow>() != 8) throw new InvalidOperationException("Surface row stride changed.");
            var saved=System.Text.Json.JsonSerializer.Deserialize<Configuration.Profile>(
                System.Text.Json.JsonSerializer.Serialize(new Configuration.Profile { LightingSpeedMode=true }));
            if (!saved.LightingSpeedMode) throw new InvalidOperationException("Mode persistence lost.");
            for(int kind=1;kind<=6;kind++) for(int z=-128;z<=127;z++) {
                var row=new SurfaceRow(1434,1699,z,20,kind,15);
                if ((row.Tile & 65535)!=1434 || (row.Tile>>16)!=1699 || (row.Data & 15)!=kind
                    || ((row.Data>>4)&15)!=15 || (int)((row.Data>>12)&255)-128!=z)
                    throw new InvalidOperationException("Surface transport roundtrip failed.");
            }
            if (DisplayServer.GetName() == "headless")
            {
                GD.Print("[lighting-speed-probe] CONTRACT PASS defaults=off stride=8; GPU NOT RUN (headless dummy renderer)");
                exit=0; return;
            }
            using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8); image.Fill(Colors.White);
            art = ImageTexture.CreateFromImage(image);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Exception failure = null;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                try
                {
                    device.InitializeRT(new Vector2I(960, 240), OrderedWorldCapture.ShaderSource("resident_world_vertex.glsl.txt"),
                        OrderedWorldCapture.ShaderSource("ordered_world_fragment.glsl.txt"),
                        OrderedWorldCapture.ShaderSource("depth_world_resident_vertex.glsl.txt"));
                    var resident = new DepthWorldResident();
                    var textures = new OrderedWorldDevice.TextureSet(new OrderedWorldDevice.Textures(art.GetRid(), art.GetRid(), art.GetRid()));
                    for (int i = 0; i < 6; i++)
                    {
                        int x = 100 + i * 3, y = 100 - i * 3;
                        float left = (x - y) * 22 - 30, top = (x + y) * 22 + 22 - 90;
                        var hue = UltimaBatcher2D.Encode(new Vector3(0, 0, 1));
                        OrderedWorldDevice.Vertex V(float px, float py, float u, float v) => new() {
                            PositionUv = new(px, py, u, v), PackedHue = new(hue.R,hue.G,hue.B,hue.A), Normal = new(0,0,1,1) };
                        var row = new SurfaceRow(x,y,0,20,i+1,i == 5 ? 3 : 0);
                        resident.Quads[i] = new DepthWorldResident.Quad {
                            A=V(left,top,0,0), B=V(left+60,top,1,0), C=V(left,top+90,0,1), D=V(left+60,top+90,1,1),
                            Aux=new OrderedWorldDevice.Float4(x+y+1.77f,0,0,BitConverter.UInt32BitsToSingle(row.Data)) };
                        resident.Merged[i]=(uint)i;
                    }
                    resident.QuadCount=resident.MergedCount=6;
                    resident.Draws.Add(new DepthWorldResident.Draw { Count=6, Land=false, Textures=textures });
                    var push = new OrderedWorldDevice.Push { Row0=new Vector4(1,0,0,1), Row1=new Vector4(0,1,0,0), Screen=new Vector4(960,240,0,0) };
                    device.RenderRT(Array.Empty<OrderedWorldResidency.Occurrence>(), Array.Empty<OrderedWorldResidency.Quad>(),
                        Array.Empty<OrderedWorldDevice.State>(), Array.Empty<OrderedWorldDevice.Segment>(),0,push,true,resident,-80,4300);
                    byte[] before = device.ReadColorRT();
                    device.RenderSurfaceRT(Array.Empty<SurfaceRow>(),Array.Empty<OrderedWorldDevice.Segment>(),push,resident,-80,4300,127,0);
                    byte[] data=device.ReadSurfaceRT();
                    int[] counts=new int[7];
                    byte[] debug=new byte[960*240*4];
                    for (int n=0;n<960*240;n++)
                    {
                        int packed=(int)(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data,n*8+6));
                        int kind=packed & 15;
                        if (kind>6) throw new InvalidOperationException("Invalid surface class " + kind);
                        counts[kind]++;
                        var color=SpeedModeDebug.ClassColor(kind,(packed/16 & 1)!=0,n%960,n/960);
                        debug[n*4]=(byte)(color.R*255); debug[n*4+1]=(byte)(color.G*255); debug[n*4+2]=(byte)(color.B*255); debug[n*4+3]=255;
                    }
                    for(int k=1;k<=6;k++) if(counts[k]==0) throw new InvalidOperationException("Missing class " + k);
                    if (!before.AsSpan().SequenceEqual(device.ReadColorRT())) throw new InvalidOperationException("Surface pass modified colour target.");
                    using var dump=Image.CreateFromData(960,240,false,Image.Format.Rgba8,debug);
                    if(dump.SavePng(Path.Combine(output,"surface-classes.png"))!=Error.Ok) throw new IOException("Cannot save surface dump.");
                    File.WriteAllBytes(Path.Combine(output,"surface.rgba16f"),data);
                    File.WriteAllText(Path.Combine(output,"surface-counts.json"),System.Text.Json.JsonSerializer.Serialize(counts));
                    GD.Print("[lighting-speed-probe] PASS classes="+string.Join(",",counts)+" colour-diff=0");
                }
                catch(Exception e) { failure=e; }
                finally { device.DisposeRT(); }
            }));
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if(failure!=null) throw failure;
            exit=0;
        }
        catch(Exception e) { GD.PrintErr("[lighting-speed-probe] FAIL "+e); }
        finally { art?.Dispose(); device.Presenter.Dispose(); device.SurfaceTexture.Dispose(); GetTree().Quit(exit); }
    }
}

