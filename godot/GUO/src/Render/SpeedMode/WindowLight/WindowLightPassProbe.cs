// SPDX-License-Identifier: BSD-2-Clause
// Synthetic relative-origin surface-buffer fixture, no UO art or shard.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;
using N2 = System.Numerics.Vector2;
using N4 = System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.WindowLight;

public partial class WindowLightPassProbe : Node
{
    public override void _Ready()
    {
        try
        {
            Run(Array.IndexOf(OS.GetCmdlineUserArgs(),"--gpu") >= 0);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("[window-light-pass-probe] FAIL " + error);
            GetTree().Quit(1);
        }
    }

    internal static void Run(bool gpu = false)
    {
        WindowOpening window = WindowLightTable.BuiltIn[0xF03D];
        Check(Marshal.SizeOf<WindowLightPatch>() == WindowLightPatch.Bytes,"GPU record layout");
        Check(WindowLightPatch.TryCreate(window,new N2(5000,6000),0,N2.UnitX,N2.UnitY,N2.UnitY,
            true,true,1,new N4(0,0,128,128),new N4(1,.8f,.5f,1),out var patch),"covered window by day");
        Check(patch.Rows0.X == 2 && patch.Rows0.Y == 1,"pane rows preserved");
        foreach (var gate in new[] { (false,true,1f),(true,false,1f),(true,true,.5f) })
            Check(!WindowLightPatch.TryCreate(window,N2.Zero,0,N2.UnitX,N2.UnitY,N2.UnitY,
                gate.Item1,gate.Item2,gate.Item3,N4.One,N4.One,out _),"day/inside/outside gate");
        if (gpu) Gpu(patch);
        GD.Print($"[window-light-pass-probe] PASS backend={(gpu ? "gpu" : "geometry")} origin=5000,6000 panes=2,1 covered=required upright=lower-only");
    }

    private static void Gpu(WindowLightPatch patch)
    {
        RD rd = RenderingServer.CreateLocalRenderingDevice()
            ?? throw new NotSupportedException("GPU fixture needs Forward+/Mobile and a Godot slot.");
        var pass = new WindowLightPassRd();
        Rid target = default, surface = default, framebuffer = default;
        try
        {
            const int size = 128;
            float[] gbuffer = new float[size*size*4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int at = (y*size+x)*4;
                gbuffer[at] = (x+.5f)/size*1.14f;
                gbuffer[at+1] = (y+.5f)/size*2.5f;
                gbuffer[at+2] = x == 15 ? 5 : x == 20 ? 2 : 0;
                gbuffer[at+3] = x == 5 ? 18 : x == 10 ? 1 : x is 15 or 20 ? 20 : 17;
            }
            using var surfaceFormat = new RDTextureFormat { Format=RD.DataFormat.R32G32B32A32Sfloat,
                Width=size,Height=size,Depth=1,ArrayLayers=1,Mipmaps=1,TextureType=RD.TextureType.Type2D,
                Samples=RD.TextureSamples.Samples1,UsageBits=RD.TextureUsageBits.SamplingBit };
            using var view = new RDTextureView();
            surface = rd.TextureCreate(surfaceFormat,view,new Godot.Collections.Array<byte[]> { MemoryMarshal.AsBytes(gbuffer.AsSpan()).ToArray() });
            using var targetFormat = new RDTextureFormat { Format=RD.DataFormat.R16G16B16A16Sfloat,
                Width=size,Height=size,Depth=1,ArrayLayers=1,Mipmaps=1,TextureType=RD.TextureType.Type2D,
                Samples=RD.TextureSamples.Samples1,UsageBits=RD.TextureUsageBits.ColorAttachmentBit|RD.TextureUsageBits.CanCopyFromBit };
            target = rd.TextureCreate(targetFormat,view,new Godot.Collections.Array<byte[]>());
            framebuffer = rd.FramebufferCreate(new Godot.Collections.Array<Rid> { target },RD.InvalidId,1);
            Check(surface.IsValid && target.IsValid && framebuffer.IsValid,"fixture targets");
            long clear = rd.DrawListBegin(framebuffer,RD.DrawFlags.ClearColor0,new Godot.Color[] { new(0,0,0,0) },1,0,null,0);
            Check(clear != RD.InvalidId,"clear target"); rd.DrawListEnd();
            Check(pass.Draw(rd,framebuffer,surface,new N2(5000,6000),size,size,new[] { patch }),pass.Failure ?? "window GPU pass");
            rd.Submit(); rd.Sync();
            byte[] bytes = rd.TextureGetData(target,0);
            float Red(int x,int y) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes,(y*size+x)*8));
            Check(Red(64,32) > .9f,"single lower pane lit");
            Check(Red(64,96) < .01f,"upper pane bar cut-out");
            Check(Red(32,96) > .9f,"upper side pane lit");
            Check(Red(5,32) < .01f,"wall rejected");
            Check(Red(10,32) < .01f,"uncovered floor rejected");
            Check(Red(15,32) < .01f,"upper card rejected");
            Check(Red(20,32) > .9f,"lower upright lit");
            string folder = System.Environment.GetEnvironmentVariable("GUO_SHADOW_PROBE_OUT");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
                using Image image = Image.CreateFromData(size,size,false,Image.Format.Rgbah,bytes);
                image.Convert(Image.Format.Rgba8);
                Check(image.SavePng(Path.Combine(folder,"window-light-gpu.png")) == Error.Ok,"window GPU PNG");
            }
        }
        finally
        {
            pass.Dispose();
            if (framebuffer.IsValid) rd.FreeRid(framebuffer);
            if (target.IsValid) rd.FreeRid(target);
            if (surface.IsValid) rd.FreeRid(surface);
            rd.Free();
        }
    }

    private static void Check(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
}
