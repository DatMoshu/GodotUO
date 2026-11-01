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
            if (SpeedModeBindings.CameraOrigin(1434,1699,0,0,0,0,2)!=new Vector2I(1434,1699)
                || SpeedModeBindings.CameraOrigin(1434,1699,-11,-11,0,0,2)!=new Vector2I(1433,1699)
                || SpeedModeBindings.CameraOrigin(1434,1699,0,0,22,0,2)!=new Vector2I(1435,1698)
                || SpeedModeBindings.CameraOrigin(1434,1699,21.9f,21.9f,0,0,2)!=new Vector2I(1434,1699)
                || SpeedModeBindings.CameraOrigin(1434,1699,22,22,0,0,2)!=new Vector2I(1435,1699))
                throw new InvalidOperationException("Camera-centre origin drifted from walking/peek projection.");
            var cpuOrigin=new Vector2I(1434,1699);
            SpeedModeBindings.PublishSurface(device,device.SurfaceTexture,cpuOrigin,new Vector2I(1280,720));
            var cpuSnapshot=SpeedModeBindings.Current;
            SpeedModeBindings.PublishLight(device,device.Presenter,cpuSnapshot.TargetPixelBounds,new Vector3(0.1f,0.2f,0.3f));
            if (SpeedModeBindings.Current.Origin!=cpuOrigin || cpuSnapshot.LightTarget!=null)
                throw new InvalidOperationException("Light hook changed the origin or previous snapshot.");
            bool rejected=false;
            try { SpeedModeBindings.PublishLight(new object(),device.Presenter,default,default); }
            catch(InvalidOperationException) { rejected=true; }
            if (!rejected) throw new InvalidOperationException("Cross-frame owner accepted for light target.");
            SpeedModeBindings.Clear(device);
            if (SpeedModeBindings.Current.Surface!=null || SpeedModeBindings.Current.LightTarget!=null
                || SpeedModeBindings.Current.Origin!=default || device.HasSurfaceResources)
                throw new InvalidOperationException("Off did not clear the binding without GPU allocation.");
            CheckReachCache();
            if (DisplayServer.GetName() == "headless")
            {
                GD.Print("[lighting-speed-probe] CONTRACT PASS defaults=off stride=8 camera-origin=pass binding-lifetime=pass reach-cache=pass; GPU NOT RUN (headless dummy renderer)");
                exit=0; return;
            }
            using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8); image.Fill(Colors.White); image.SetPixel(0,0,Colors.Transparent);
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
                    if (device.HasSurfaceResources) throw new InvalidOperationException("Surface resources created while mode is off.");
                    const int cameraX=-5910,cameraY=68826;
                    var origin=new Vector2I(1444,1698);
                    var resident = new DepthWorldResident();
                    var textures = new OrderedWorldDevice.TextureSet(new OrderedWorldDevice.Textures(art.GetRid(), art.GetRid(), art.GetRid()));
                    for (int i = 0; i < 6; i++)
                    {
                        int x = 1434 + i * 3, y = 1699 - i * 3;
                        float left = (x - y) * 22 - 30, top = (x + y) * 22 + 22 - 90;
                        var hue = UltimaBatcher2D.Encode(new Vector3(0, 0, 1));
                        OrderedWorldDevice.Vertex V(float px, float py, float u, float v) => new() {
                            PositionUv = new(px, py, u, v), PackedHue = new(hue.R,hue.G,hue.B,hue.A), Normal = new(0,0,1,1) };
                        var row = new SurfaceRow(x,y,0,20,i+1,i == 5 ? 3 : 0);
                        resident.Quads[i] = new DepthWorldResident.Quad {
                            A=V(left,top,0,0), B=V(left+60,top,1,0), C=V(left,top+90,0,1), D=V(left+60,top+90,1,1),
                            Aux=new OrderedWorldDevice.Float4(x+y+1.77f,0,0,BitConverter.UInt32BitsToSingle(row.Data)) };
                        if(i==0) {
                            resident.Quads[i].A.Normal.W=0; resident.Quads[i].B.Normal.W=0;
                            resident.Quads[i].C.Normal.W=0; resident.Quads[i].D.Normal.W=0;
                        }
                        resident.Merged[i]=(uint)i;
                    }
                    resident.QuadCount=resident.MergedCount=6;
                    resident.Draws.Add(new DepthWorldResident.Draw { Count=6, Land=false, Textures=textures });
                    var push = new OrderedWorldDevice.Push { Row0=new Vector4(1,0,0,1), Row1=new Vector4(0,1,0,0), Screen=new Vector4(960,240,0,0) };
                    // Reuse one dynamic geometry slot twice, with different state and surface rows.
                    // Separate runs catch draw-local row indexing on the second occurrence.
                    var quad = new OrderedWorldResidency.Quad { A=resident.Quads[3].A, B=resident.Quads[3].B,
                        C=resident.Quads[3].C, D=resident.Quads[3].D };
                    quad.A.PositionUv.X+=400; quad.B.PositionUv.X+=400;
                    quad.C.PositionUv.X+=400; quad.D.PositionUv.X+=400;
                    var occurrences = new[] { new OrderedWorldResidency.Occurrence(0), new OrderedWorldResidency.Occurrence(0),
                        new OrderedWorldResidency.Occurrence(0) };
                    var geometry = new[] { quad };
                    var states = new[] {
                        new OrderedWorldDevice.State { Xx=1,Yy=1,OriginX=-cameraX,OriginY=-cameraY,Pad1=3135 },
                        new OrderedWorldDevice.State { Xx=1,Yy=1,OriginX=-cameraX+74,OriginY=-cameraY,Pad1=3136 },
                        new OrderedWorldDevice.State { Xx=1,Yy=1,OriginX=-cameraX,OriginY=-cameraY,Pad1=3134 } };
                    var segments = new[] { new OrderedWorldDevice.Segment(0,6,textures), new OrderedWorldDevice.Segment(6,6,textures),new OrderedWorldDevice.Segment(12,6,textures) };
                    var rows = new[] { new SurfaceRow(1453,1680,0,20,4), new SurfaceRow(1454,1679,0,0,6,3),new SurfaceRow(1453,1680,0,20,3) };
                    device.RenderRT(occurrences,geometry,states,segments,3,push,true,resident,cameraX,cameraY);
                    byte[] before = device.ReadColorRT();
                    device.RenderSurfaceRT(rows,segments,push,resident,cameraX,cameraY,127,0,origin);
                    byte[] data=device.ReadSurfaceRT();
                    int ReadClass(int x,int y) => (int)(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data,(y*960+x)*8+6)) & 15;
                    float ReadValue(int x,int y,int channel) => (float)BitConverter.UInt16BitsToHalf(
                        BitConverter.ToUInt16(data,(y*960+x)*8+channel*2));
                    // A flat rectangular land image still uses the colour mesh diagonal.
                    // Its world position must lie on the floor plane, not a stretched diamond.
                    float expectedX=(100.5f+cameraX+40.5f+cameraY+22)/44f-origin.X;
                    float expectedY=(40.5f+cameraY+22-(100.5f+cameraX))/44f-origin.Y;
                    if (Math.Abs(ReadValue(100,40,0)-expectedX)>0.01f || Math.Abs(ReadValue(100,40,1)-expectedY)>0.01f
                        || ReadValue(100,40,2)!=0) throw new InvalidOperationException("Flat-land surface plane is incorrect.");
                    if (ReadClass(60,40)!=0 || ReadClass(100,40)!=1)
                        throw new InvalidOperationException("Surface art-alpha cutout does not match colour.");
                    if (ReadClass(876,80)!=4 || ReadClass(945,80)!=6)
                        throw new InvalidOperationException("Dynamic occurrence rows do not follow ordered state indices.");
                    var binding=SpeedModeBindings.Current;
                    if(binding.Origin!=origin || !ReferenceEquals(binding.Surface,device.SurfaceTexture)
                        || binding.LightTarget!=null || binding.Sequence<=0)
                        throw new InvalidOperationException("Surface origin is not bound to the matching frame texture.");
                    SpeedModeBindings.PublishLight(device,art,binding.TargetPixelBounds,new Vector3(0.1f,0.2f,0.3f));
                    if (SpeedModeBindings.Current.Origin!=origin || !ReferenceEquals(SpeedModeBindings.Current.LightTarget,art))
                        throw new InvalidOperationException("Light-target hook lost the surface origin.");
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
                    File.WriteAllText(Path.Combine(output,"surface-frame.json"),System.Text.Json.JsonSerializer.Serialize(new {
                        origin=new[] { origin.X,origin.Y },size=new[] {960,240},format="RGBA16F",
                        position="camera-relative XY, world Z",light_hook="synthetic binding only; no light pass" }));
                    CheckGpuLight(device,origin,data,output);
                    // A new origin is a new surface ticket, even when geometry is reused.
                    var nextOrigin=origin+new Vector2I(1,-1);
                    device.RenderSurfaceRT(rows,segments,push,resident,cameraX,cameraY,127,0,nextOrigin);
                    byte[] moved=device.ReadSurfaceRT();
                    float movedX=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(moved,(40*960+100)*8));
                    float movedY=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(moved,(40*960+100)*8+2));
                    if (Math.Abs((movedX+nextOrigin.X)-(expectedX+origin.X))>0.01f
                        || Math.Abs((movedY+nextOrigin.Y)-(expectedY+origin.Y))>0.01f
                        || binding.Origin!=origin || SpeedModeBindings.Current.Origin!=nextOrigin)
                        throw new InvalidOperationException("Origin shift changed world position or mutated the old frame snapshot.");
                    device.DisableSurfaceRT();
                    if(SpeedModeBindings.Current.Surface!=null || SpeedModeBindings.Current.LightTarget!=null
                        || SpeedModeBindings.Current.Origin!=default)
                        throw new InvalidOperationException("Off retained a stale surface/light/origin binding.");
                    if (device.HasSurfaceResources) throw new InvalidOperationException("Disabled surface resources remain allocated.");
                    device.RenderRT(occurrences,geometry,states,segments,3,push,true,resident,cameraX,cameraY);
                    if (device.HasSurfaceResources || !before.AsSpan().SequenceEqual(device.ReadColorRT()))
                        throw new InvalidOperationException("Off-state colour or resource lifetime changed.");
                    GD.Print("[lighting-speed-probe] PASS classes="+string.Join(",",counts)+" colour-diff=0 dynamic-rows=3 alpha=pass depth=pass off-resources=0 relative-origin=pass");
                }
                catch(Exception e) { failure=e; }
                finally { device.DisposeRT(); }
            }));
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if(failure!=null) throw failure;
            await CheckCompose(output);
            exit=0;
        }
        catch(Exception e) { GD.PrintErr("[lighting-speed-probe] FAIL "+e); }
        finally { SpeedModeBindings.Clear(device); art?.Dispose(); device.Presenter.Dispose(); device.SurfaceTexture.Dispose(); device.SpeedLightTexture.Dispose(); GetTree().Quit(exit); }
    }

    private static void CheckReachCache()
    {
        var field=new GUO.Renderer.Lights.RealtimeLightField();
        field.Begin(0,0,16,16);
        var source=new GUO.Renderer.Lights.RealtimeLightField.Source(2,8,10,6,Colors.White);
        field.BeginSpeedReach();
        var reach=field.GetSpeedReach(in source);
        static int At(GUO.Renderer.Lights.RealtimeLightField.SpeedReach r,float x,float y)
            => ((int)((y-r.Y)*r.Scale)*r.Side+(int)((x-r.X)*r.Scale))*2;
        int far=At(reach,6.5f,8.5f);
        if(reach.Pixels[far]!=255 || field.LastComputed!=1)
            throw new InvalidOperationException("Open reach map failed.");
        int revision=reach.Revision;
        field.BeginSpeedReach();
        if(!ReferenceEquals(reach,field.GetSpeedReach(in source)) || reach.Revision!=revision || field.LastComputed!=0)
            throw new InvalidOperationException("Unchanged blocker grid recomputed its reach map.");
        for(int y=0;y<16;y++) field.AddEdge(5,y,false,0,32);
        field.BeginSpeedReach();
        reach=field.GetSpeedReach(in source);
        int edge=At(reach,5.125f,8.5f);
        if(reach.Revision==revision || field.LastComputed!=1 || reach.Pixels[far]!=0
            || reach.Pixels[far+1]!=0 || reach.Pixels[edge]!=0 || reach.Pixels[edge+1]!=255)
            throw new InvalidOperationException("Reach invalidation, wall blocking or one-tile dilation failed.");
    }

    private static void CheckGpuLight(OrderedWorldDevice device,Vector2I origin,byte[] surface,string output)
    {
        static float Component(byte[] bytes,int pixel,int channel)
            => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes,pixel*8+channel*2));
        var frame=new SpeedLightFrame { Origin=origin,Ambient=new Vector3(0.1f,0.2f,0.3f),RoofShare=0.55f };
        device.RenderLightsRT(frame);
        byte[] ambient=device.ReadSpeedLightRT();
        int floor=40*960+100,covered=-1;
        for(int i=0;i<960*240;i++)
            if(((int)Component(surface,i,3)&16)!=0) { covered=i; break; }
        if(covered<0 || Math.Abs(Component(ambient,floor,0)-0.1f)>0.001f
            || Math.Abs(Component(ambient,covered,0)-0.055f)>0.001f)
            throw new InvalidOperationException($"GPU ambient/cover dim failed: covered={covered} floor={Component(ambient,floor,0)} cover={(covered>=0 ? Component(ambient,covered,0) : -1)}.");
        int x=(int)MathF.Floor(Component(surface,floor,0)+origin.X);
        int y=(int)MathF.Floor(Component(surface,floor,1)+origin.Y);
        var field=new GUO.Renderer.Lights.RealtimeLightField();
        field.Begin(x-10,y-10,21,21); field.BeginSpeedReach();
        var source=new GUO.Renderer.Lights.RealtimeLightField.Source(x,y,-10,8,new Color(1,0.5f,0.2f));
        frame.Count=1;
        frame.Lights[0]=new SpeedLightFrame.Entry { Source=source,Reach=field.GetSpeedReach(in source),
            PixelCentre=new Vector2(100,40),PixelRadius=400,Flicker=1 };
        device.RenderLightsRT(frame);
        long uploads=OrderedWorldDevice.SpeedReachUploads;
        byte[] lit=device.ReadSpeedLightRT();
        if(Component(lit,floor,0)<1f || Component(lit,floor,0)<=Component(lit,floor,1)
            || Math.Abs(Component(lit,40*960+900,0)-0.1f)>0.001f)
            throw new InvalidOperationException("GPU bounded additive light failed.");
        device.RenderLightsRT(frame);
        if(OrderedWorldDevice.SpeedReachUploads!=uploads || !device.SpeedLightReady
            || SpeedModeBindings.Current.LightTarget!=device.SpeedLightTexture || SpeedModeBindings.Current.Origin!=origin)
            throw new InvalidOperationException("GPU reach upload cache or light binding failed.");
        var rgba=new byte[960*240*4];
        for(int i=0;i<960*240;i++) { for(int c=0;c<3;c++) rgba[i*4+c]=(byte)(Math.Clamp(Component(lit,i,c),0,1)*255); rgba[i*4+3]=255; }
        using var image=Image.CreateFromData(960,240,false,Image.Format.Rgba8,rgba);
        if(image.SavePng(Path.Combine(output,"light-target.png"))!=Error.Ok) throw new IOException("Cannot save light target.");
        File.WriteAllBytes(Path.Combine(output,"light-target.rgba16f"),lit);
        CheckShadowInputs(device,frame,surface,lit,floor,covered);
        frame.Soften=0; device.RenderSoftenRT(frame);
        if(!lit.AsSpan().SequenceEqual(device.ReadSpeedLightRT()))
            throw new InvalidOperationException("Zero soften changed the light attachment.");
        foreach(int steps in new[] {1,3,10})
        {
            frame.Soften=steps; device.RenderSoftenRT(frame);
            byte[] softened=device.ReadSpeedLightRT();
            bool changed=false;
            for(int i=0;i<960*240;i++)
            {
                float value=Component(softened,i,0);
                if(!float.IsFinite(value)||value<0) throw new InvalidOperationException("Soften produced invalid HDR.");
                int kind=(int)Component(surface,i,3)&15;
                if(kind==0 && Math.Abs(value-Component(lit,i,0))>0.001f)
                    throw new InvalidOperationException("Soften crossed a class boundary into empty space.");
                if(kind==1 && Math.Abs(value-Component(lit,i,0))>0.001f) changed=true;
            }
            if(!changed) throw new InvalidOperationException("Nonzero soften did no work.");
            if(steps==3)
            {
                for(int i=0;i<960*240;i++) for(int c=0;c<3;c++)
                    rgba[i*4+c]=(byte)(Math.Clamp(Component(softened,i,c),0,1)*255);
                using var softImage=Image.CreateFromData(960,240,false,Image.Format.Rgba8,rgba);
                if(softImage.SavePng(Path.Combine(output,"light-soften-3.png"))!=Error.Ok) throw new IOException("Cannot save softened light.");
            }
        }
        GD.Print("[lighting-speed-probe] SOFTEN PASS steps=0,1,3,10 zero-diff=0 class-boundary=pass finite-hdr=pass");
        GD.Print("[lighting-speed-probe] LIGHT PASS ambient-cover=pass bounded-additive=pass reach-upload-cache=pass same-origin=pass");
    }
}

