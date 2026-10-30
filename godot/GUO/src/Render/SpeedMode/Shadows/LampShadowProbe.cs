// SPDX-License-Identifier: BSD-2-Clause
// Synthetic wall DDA oracle; never used by the client renderer.
using System;
using System.IO;
using Godot;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class LampShadowProbe : Node
{
    public override void _Ready()
    {
        try { Run(Array.IndexOf(OS.GetCmdlineUserArgs(),"--gpu")>=0); GetTree().Quit(0); }
        catch (Exception error) { GD.PrintErr("[lamp-shadow-probe] FAIL "+error); GetTree().Quit(1); }
    }

    internal static void Run(bool gpu=false)
    {
        var near=Fixture(0,0); var far=Fixture(5000,6000);
        ShadowLamp lamp=new(new N3(.5f,.5f,10),8,1);
        ShadowLamp translated=lamp with { Position=lamp.Position+new N3(5000,6000,0) };
        float[] row=gpu ? Gpu(near,lamp) : Oracle(near,lamp);
        float[] shifted=gpu ? Gpu(far,translated) : Oracle(far,translated);
        float[] oracle=Oracle(near,lamp);
        int blocked=0;
        for (int i=0;i<256;i++)
        {
            Check(row[i*2]==shifted[i*2] && row[i*2+1]==shifted[i*2+1],"camera translation invariance");
            Check(MathF.Abs(row[i*2]-oracle[i*2])<.01f && row[i*2+1]==oracle[i*2+1],"DDA matches independent segment oracle");
            if (row[i*2]<100) { blocked++; Check(row[i*2+1]==30,"wall top Z"); }
        }
        Check(blocked is >=36 and <=40,"one wall angular span");
        Check(MathF.Abs(row[128*2]-3)<.02f,"wall plane distance");
        Check(row[0]>60000,"no mobile/tree polar wedge");
        var filtered=new WallShadowGrid(); filtered.Begin(0,0,2,2);
        Check(!filtered.AddBlocker(0,0,30,GUO.Assets.TileFlag.Foliage | GUO.Assets.TileFlag.Impassable,30,"",false),"tree excluded before blocker aggregation");
        Check(!filtered.AddBlocker(0,0,30,GUO.Assets.TileFlag.Bridge | GUO.Assets.TileFlag.Wall,30,"",false),"stairs excluded before blocker aggregation");
        Check(!filtered.AddBlocker(0,0,30,GUO.Assets.TileFlag.Impassable,30,"oak tree trunk",false),"bare tree trunk name excluded");
        Check(!filtered.AddBlocker(0,0,30,GUO.Assets.TileFlag.Impassable,30,"wood",true),"bare trunk with foliage on tile excluded");
        Check(filtered.AddBlocker(0,0,30,GUO.Assets.TileFlag.Impassable,10,"crate",false),"solid blocker retained");
        Check(WallShadowGrid.TreeRule(GUO.Assets.TileFlag.None,"TREE",false)=="tree-trunk-name" &&
            WallShadowGrid.TreeRule(GUO.Assets.TileFlag.None,"wood",true)=="foliage-on-tile","tree rejection rule labelled");
        var line=new WallShadowGrid(); line.Begin(-2,-2,8,8); line.AddEdge(0,0,false,.75f,20);
        var edge=Oracle(line,lamp);
        Check(MathF.Abs(edge[128*2]-.25f)<.001f,"emitter-cell edge retains its sub-tile plane");
        Span<int> selected=stackalloc int[10];
        ShadowLamp[] lamps={ lamp,new(new N3(2.5f,.5f,10),8,2),new(new N3(4.5f,.5f,10),8,3) };
        Check(PolarShadowGeometry.Select(lamps,N3.Zero,2,selected)==2 && selected[0]==0 && selected[1]==1,"nearest rows");
        Check(PolarShadowGeometry.Select(lamps,N3.Zero,0,selected)==0,"zero rows");
        Span<MobileLampShadow> projections=stackalloc MobileLampShadow[4];
        Check(MobileLampProjection.Select(lamps,new N3(3,1,0),projections)==2,"two nearest mobile lamp silhouettes");
        Check(projections[0].Offer==1 && projections[0].Strength>projections[1].Strength,"mobile distance fade");
        if (gpu) Save(row);
        GD.Print($"[lamp-shadow-probe] PASS backend={(gpu ? "gpu-compute" : "oracle")} rays=256 wall-span={blocked} translation=5000,6000 mobile-lamps=2 tree-rules=foliage-flag,tree-trunk-name,foliage-on-tile");
    }

    private static WallShadowGrid Fixture(int x,int y)
    {
        var grid=new WallShadowGrid(); grid.Begin(x-8,y-8,18,18);
        for (int yy=-1;yy<=1;yy++) grid.AddEdge(x+3,y+yy,false,.5f,30);
        return grid;
    }

    private static float[] Gpu(WallShadowGrid grid,ShadowLamp lamp)
    {
        RenderingDevice rd=RenderingServer.CreateLocalRenderingDevice();
        Check(rd!=null,"local RenderingDevice available");
        var map=new PolarShadowMapRd();
        try
        {
            Check(map.Render(rd,grid,new[] { lamp },false),map.Failure ?? "compute dispatch");
            rd.Submit(); rd.Sync();
            byte[] data=rd.TextureGetData(map.Atlas,0);
            Check(data.Length==256*64*4,"RG16F atlas readback");
            var row=new float[512];
            for (int i=0;i<row.Length;i++) row[i]=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data,i*2));
            Check(map.Render(rd,grid,new[] { lamp },false),"reuse unchanged grid");
            rd.Submit(); rd.Sync();
            Check(map.GridUploads==1,"stable grid avoids upload");
            var empty=new WallShadowGrid(); empty.Begin(grid.OriginX,grid.OriginY,grid.Width,grid.Height);
            Check(map.Render(rd,empty,new[] { lamp },false),"changed grid dispatch");
            rd.Submit(); rd.Sync();
            data=rd.TextureGetData(map.Atlas,0);
            for (int i=0;i<256;i++) Check((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data,i*4))>60000,"removed wall leaves no stale shadow");
            Check(map.GridUploads==2,"changed grid uploads once");
            return row;
        }
        finally { map.Dispose(); map.Texture.Dispose(); rd.Free(); }
    }

    private static float[] Oracle(WallShadowGrid grid,ShadowLamp lamp)
    {
        var result=new float[512];
        for (int angle=0;angle<256;angle++)
        {
            float theta=(angle+.5f)/256*MathF.Tau-MathF.PI;
            float dx=MathF.Cos(theta),dy=MathF.Sin(theta),hit=65504,top=-32768;
            for (int y=grid.OriginY;y<grid.OriginY+grid.Height;y++)
            for (int x=grid.OriginX;x<grid.OriginX+grid.Width;x++)
            {
                var cell=grid.Cell(x,y);
                // Fixture oracle deliberately intersects explicit wall segments,
                // independently of the GPU's tile-boundary DDA traversal.
                if (cell.Horizontal>WallShadowGrid.NoBlock && MathF.Abs(dy)>1e-6f)
                {
                    float t=(y+cell.OffsetY-lamp.Position.Y)/dy;
                    float xx=lamp.Position.X+t*dx;
                    if (t>.001f && t<=lamp.Radius && t<hit && xx>=x && xx<=x+1) { hit=t; top=cell.Horizontal; }
                }
                if (cell.Vertical>WallShadowGrid.NoBlock && MathF.Abs(dx)>1e-6f)
                {
                    float t=(x+cell.OffsetX-lamp.Position.X)/dx;
                    float yy=lamp.Position.Y+t*dy;
                    if (t>.001f && t<=lamp.Radius && t<hit && yy>=y && yy<=y+1) { hit=t; top=cell.Vertical; }
                }
                Check(cell.Solid==WallShadowGrid.NoBlock,"fixture uses exact wall lines");
            }
            result[angle*2]=hit; result[angle*2+1]=top;
        }
        return result;
    }

    private static void Save(float[] row)
    {
        string folder=System.Environment.GetEnvironmentVariable("GUO_SHADOW_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var image=Image.CreateEmpty(256,64,false,Image.Format.Rgba8);
        for (int x=0;x<256;x++) for (int y=0;y<64;y++)
        {
            float d=row[x*2]; image.SetPixel(x,y,d>100 ? Colors.Black : new Color(1-d/8,row[x*2+1]/40,0));
        }
        Check(image.SavePng(Path.Combine(folder,"lamp-wall-compute-gpu.png"))==Error.Ok,"fixture PNG");
    }
    private static void Check(bool ok,string reason) { if (!ok) throw new InvalidOperationException(reason); }
}
