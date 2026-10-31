// SPDX-License-Identifier: BSD-2-Clause
// Synthetic GPU shelter proof: real RGBA16F receiver input and R8/D32 readbacks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using RD = Godot.RenderingDevice;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class StructuralShelterProbe : Node
{
    private readonly List<object> _rows = new();
    private int _checks;
    private ulong _generation;
    private readonly record struct Receiver(string Name, N3 World, int Packed, byte Expected);
    public override void _Ready()
    {
        try { Run(); Save(null); GD.Print($"[structural-shelter] PASS checks={_checks}; backend=gpu-raster-compute"); GetTree().Quit(0); }
        catch (Exception error) { Save(error.ToString()); GD.PrintErr("[structural-shelter] FAIL " + error); GetTree().Quit(1); }
    }
    private void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); _checks++; }

    private PhysicalWorldBuilder Builder()
    {
        var b = new PhysicalWorldBuilder(new PhysicalSnapshotKey(43, ++_generation, 0), new PhysicalRect(-4, -4, 8, 8));
        for (int y = -4; y < 4; y++) for (int x = -4; x < 4; x++) b.MarkComplete(x, y);
        b.MarkUnknown(3, 0, PhysicalUnknownReason.UnsupportedStructure);
        return b;
    }
    private static void Plane(PhysicalWorldBuilder b, float x, float y, float side, float z,
        PhysicalMaterial material = PhysicalMaterial.OpaqueStructure)
    {
        var owner = new PhysicalOwnerKey(PhysicalSourceKind.MapStatic, (ulong)(uint)z);
        b.AddTriangle(new(new(x,y,z),new(x+side,y,z),new(x+side,y+side,z),owner,material,PhysicalProvenance.NativeFloor));
        b.AddTriangle(new(new(x,y,z),new(x+side,y+side,z),new(x,y+side,z),owner,material,PhysicalProvenance.NativeFloor));
    }

    private void Run()
    {
        RD rd = RenderingServer.CreateLocalRenderingDevice();
        Check(rd != null, "local raster/compute RenderingDevice available");
        var pass = new PhysicalShelterPassRd();
        Rid surface = default;
        try
        {
            Receiver[] receivers = {
                new("ground-below-two-layers",new(.5f,.5f,0),257,255),
                new("upper-floor-below-roof",new(.5f,.5f,20),257,255),
                new("roof-own-surface",new(.5f,.5f,40),261,128),
                new("above-roof",new(.5f,.5f,60),257,128),
                new("terrain-is-not-weather-ceiling",new(2.5f,.5f,0),257,128),
                new("unknown-empty-column",new(3.5f,.5f,0),257,0),
                new("outside-capture",new(20.5f,.5f,0),257,0),
                new("unqualified-receiver",new(.5f,.5f,0),1,0),
                new("translucent-receiver",new(.5f,.5f,0),257|128,0),
                new("above-native-z-domain",new(.5f,.5f,383),257,0)
            };
            var bounds = new Vector2I(receivers.Length,1);
            var origin = new Vector2I(2,2);
            surface = Surface(rd, receivers, origin);
            var layered = Builder(); Plane(layered,0,0,2,20); Plane(layered,0,0,2,40);
            Plane(layered,2,0,1,70,PhysicalMaterial.Terrain);
            var snapshot = layered.Seal();
            Render(rd,pass,snapshot,surface,origin,bounds,receivers,"stacked-native-layers");
            DepthWitness(rd,pass,snapshot,.5f,.5f,40,"highest-weather-roof");
            DepthWitness(rd,pass,snapshot,2.5f,.5f,null,"terrain-filtered-from-weather-depth");
            int uploads=pass.GeometryUploads, draws=pass.DepthDraws;
            Render(rd,pass,snapshot,surface,origin,bounds,receivers,"identical-ticket-reuse");
            Check(pass.GeometryUploads==uploads && pass.DepthDraws==draws,"immutable shelter geometry/depth reuse");

            // Cutaway changes WHICH native receiver is visible, never physical geometry.
            // Replace a visible roof receiver by its ground pixel on the same surface RID.
            Receiver[] cutaway=(Receiver[])receivers.Clone();
            cutaway[2]=new("cutaway-reveals-ground-but-retains-roof-shelter",new(.5f,.5f,0),257,255);
            UpdateSurface(rd,surface,cutaway,origin);
            Render(rd,pass,snapshot,surface,origin,bounds,cutaway,"roof-cutaway");
            Check(pass.GeometryUploads==uploads && pass.DepthDraws==draws,"cutaway never rebuilds or removes physical roof");

            // Origin moves while native XYZ and geometry stay fixed.
            origin=new Vector2I(-2,-3);
            UpdateSurface(rd,surface,receivers,origin);
            Render(rd,pass,snapshot,surface,origin,bounds,receivers,"surface-origin-moved");
            Check(pass.GeometryUploads==uploads && pass.DepthDraws==draws,"surface origin does not move world shelter");

            var floorOnly=Builder(); Plane(floorOnly,0,0,2,20);
            var noRoof=floorOnly.Seal();
            Receiver[] removed=(Receiver[])receivers.Clone();
            removed[1]=removed[1] with { Name="first-roof-removal-exposes-upper-floor", Expected=128 };
            UpdateSurface(rd,surface,removed,origin);
            Render(rd,pass,noRoof,surface,origin,bounds,removed,"roof-removed-first-generation");
            Check(pass.SnapshotKey==noRoof.Key && pass.GeometryUploads==uploads+1 && pass.DepthDraws==draws+1,
                "roof removal clears previous depth immediately");
            DepthWitness(rd,pass,noRoof,.5f,.5f,20,"remaining-floor-is-next-ceiling");

            var empty=Builder().Seal();
            removed[0]=removed[0] with { Name="last-ceiling-removal-exposes-ground",Expected=128 };
            UpdateSurface(rd,surface,removed,origin);
            Render(rd,pass,empty,surface,origin,bounds,removed,"all-ceilings-removed");
            DepthWitness(rd,pass,empty,.5f,.5f,null,"empty-depth-cleared");

            var unknownRoof=Builder(); Plane(unknownRoof,0,0,2,40);
            unknownRoof.MarkUnknown(0,0,PhysicalUnknownReason.UnsupportedStructure);
            var unknownSnapshot=unknownRoof.Seal();
            Receiver[] uncertain=(Receiver[])receivers.Clone();
            uncertain[2]=uncertain[2] with { Expected=0 };
            uncertain[3]=uncertain[3] with { Expected=0 };
            UpdateSurface(rd,surface,uncertain,origin);
            Render(rd,pass,unknownSnapshot,surface,origin,bounds,uncertain,"positive-hit-survives-unknown-column");

            uploads=pass.GeometryUploads;draws=pass.DepthDraws;
            Receiver[] smaller={uncertain[0],uncertain[3]};
            Rid replacement=Surface(rd,smaller,origin);
            rd.FreeRid(surface);surface=replacement;bounds=new Vector2I(smaller.Length,1);uncertain=smaller;
            Render(rd,pass,unknownSnapshot,surface,origin,bounds,uncertain,"resized-status-first-ticket");
            Check(pass.GeometryUploads==uploads && pass.DepthDraws==draws,
                "status resize and receiver replacement reuse immutable depth while immediately reclassifying pixels");

            pass.Dispose();
            Check(!pass.Ready && !pass.StatusRid.IsValid && !pass.DepthRid.IsValid && pass.SnapshotKey==default
                && (pass.Texture==null || !pass.Texture.TextureRdRid.IsValid),"dispose revokes depth/status/publication");
            pass.Dispose();
            Render(rd,pass,unknownSnapshot,surface,origin,bounds,uncertain,"recreate-after-retirement");
            var invalid=Builder(); Plane(invalid,0,0,2,500);
            bool rejected=false;
            try { pass.Render(rd,invalid.Seal(),surface,origin,bounds,publish:false); }
            catch (InvalidOperationException) { rejected=true; }
            Check(rejected && !pass.Ready && !pass.StatusRid.IsValid && !pass.DepthRid.IsValid,
                "out-of-native-caster-range is rejected, never clipped into a clear result");
        }
        finally { pass.Dispose(); if(surface.IsValid)rd.FreeRid(surface); rd.Free(); }
    }

    private Rid Surface(RD rd, Receiver[] receivers, Vector2I origin)
    {
        using var spec=new RDTextureFormat { Width=(uint)receivers.Length,Height=1,Depth=1,ArrayLayers=1,Mipmaps=1,
            TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R16G16B16A16Sfloat,Samples=RD.TextureSamples.Samples1,
            UsageBits=RD.TextureUsageBits.SamplingBit|RD.TextureUsageBits.CanUpdateBit|RD.TextureUsageBits.CanCopyFromBit };
        using var view=new RDTextureView();
        Rid surface=rd.TextureCreate(spec,view,new Godot.Collections.Array<byte[]> { SurfaceBytes(receivers,origin) });
        Check(surface.IsValid,"actual native-format RGBA16F fixture surface created");
        return surface;
    }
    private void UpdateSurface(RD rd,Rid surface,Receiver[] receivers,Vector2I origin)
        => Check(rd.TextureUpdate(surface,0,SurfaceBytes(receivers,origin))==Error.Ok,"native receiver surface update succeeds");
    private static byte[] SurfaceBytes(Receiver[] receivers,Vector2I origin)
    {
        byte[] bytes=new byte[receivers.Length*8];
        Span<float> values=stackalloc float[4];
        for(int i=0;i<receivers.Length;i++)
        {
            var r=receivers[i];
            values[0]=r.World.X-origin.X;values[1]=r.World.Y-origin.Y;values[2]=r.World.Z;values[3]=r.Packed;
            for(int j=0;j<4;j++) BitConverter.TryWriteBytes(bytes.AsSpan(i*8+j*2,2),BitConverter.HalfToUInt16Bits((Half)values[j]));
        }
        return bytes;
    }
    private void Render(RD rd,PhysicalShelterPassRd pass,PhysicalWorldSnapshot snapshot,Rid surface,
        Vector2I origin,Vector2I size,Receiver[] receivers,string stage)
    {
        pass.Render(rd,snapshot,surface,origin,size,publish:false);rd.Submit();rd.Sync();
        Check(pass.Ready && pass.SnapshotKey==snapshot.Key,"shelter output belongs to admitted physical snapshot");
        byte[] status=rd.TextureGetData(pass.StatusRid,0);
        Check(status.Length==receivers.Length,"actual R8 GPU status readback size");
        for(int i=0;i<receivers.Length;i++)
        {
            _rows.Add(new {stage,receiver=receivers[i].Name,actual=status[i],expected=receivers[i].Expected,generation=snapshot.Key.Generation});
            Check(status[i]==receivers[i].Expected,"GPU shelter classification "+stage+": "+receivers[i].Name);
        }
    }
    private void DepthWitness(RD rd,PhysicalShelterPassRd pass,PhysicalWorldSnapshot snapshot,float x,float y,float? expectedZ,string name)
    {
        byte[] depth=rd.TextureGetData(pass.DepthRid,0);
        int res=PhysicalShelterPassRd.Resolution;
        Check(depth.Length==res*res*4,"actual vertical D32 GPU readback size");
        int px=(int)MathF.Floor((x-snapshot.Bounds.X)/snapshot.Bounds.Width*res);
        int py=(int)MathF.Floor((y-snapshot.Bounds.Y)/snapshot.Bounds.Height*res);
        float actual=BitConverter.ToSingle(depth,(py*res+px)*4);
        float expected=expectedZ.HasValue ? (PhysicalShelterPassRd.MaximumZ-expectedZ.Value)/(PhysicalShelterPassRd.MaximumZ-PhysicalShelterPassRd.MinimumZ) : 1;
        _rows.Add(new {depth=name,actual,expected});
        Check(float.IsFinite(actual)&&MathF.Abs(actual-expected)<.00001f,"GPU physical ceiling depth "+name);
    }
    private void Save(string failure)
    {
        string root=System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_WORLD_OUT");
        if(string.IsNullOrWhiteSpace(root))return;
        root=Path.GetFullPath(root);string project=Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        if(root.StartsWith(project,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("GPU fixture evidence must stay outside project.");
        string folder=Path.Combine(root,"data");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"structural-shelter-gpu-results.json"),JsonSerializer.Serialize(
            new {backend="gpu-raster-compute",checks=_checks,rows=_rows,failure},new JsonSerializerOptions {WriteIndented=true}));
    }
}
