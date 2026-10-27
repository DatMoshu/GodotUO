// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Linq;
using Godot;
using GUO.Labs.GpuRenderer;
using GUO.Renderer;
using Rectangle = GUO.Compat.Rectangle;
namespace GUO.Host;
public partial class BatcherProbe
{
    private void VerifyDepthWorldResidentOrder()
    {
        using var red = BuildSolid(new byte[] { 255, 0, 0 });
        using var green = BuildSolid(new byte[] { 0, 255, 0 });
        using var first = new MeshLayerDisposable();
        using var middle = new MeshLayerDisposable();
        using var last = new MeshLayerDisposable();
        using var ground = new MeshLayerDisposable();
        void Init(MeshLayer layer, Texture2D texture, int marker)
        {
            layer.Count = 1;
            layer.WriteQuadAt(0, texture, new Rectangle(0,0,1,1), marker,0,new Vector3(0,0,1),100f);
            layer.SetVisible(0,255); layer.BuildVisibleIndices();
        }
        Init(first.Layer, red, 1); Init(middle.Layer, green, 2); Init(last.Layer, red, 3); Init(ground.Layer,green,4);
        var resident = new DepthWorldResident();
        void Frame(string name, int[] expected, int draws, params MeshLayer[] layers)
        {
            resident.BeginFrame();
            foreach(var layer in layers)
                CheckThat(name+" admission",resident.Add(layer,ReferenceEquals(layer,ground.Layer),red.GetRid(),red.GetRid(),out string why),why);
            resident.Merge();
            int[] actual = new int[resident.MergedCount];
            int at=0;
            // Match the device's category passes, then each draw's primitive order.
            for(int pass=0;pass<2;pass++)
                foreach(var draw in resident.Draws)
                    if(draw.Land==(pass==0))
                        for(uint i=0;i<draw.Count;i++) actual[at++]=(int)resident.Quads[resident.Merged[draw.VisibleBase+i]].A.PositionUv.X;
            CheckThat(name+" order",actual.SequenceEqual(expected),"expected "+string.Join(",",expected)+" got "+string.Join(",",actual));
            CheckThat(name+" contiguous runs",resident.Draws.Count==draws,"expected "+draws+" got "+resident.Draws.Count);
        }
        Frame("depth resident A B A",new[]{1,2,3},3,first.Layer,middle.Layer,last.Layer);
        resident.Uploaded();
        Frame("depth resident unchanged",new[]{1,2,3},3,first.Layer,middle.Layer,last.Layer);
        CheckThat("depth resident unchanged reuses merge",!resident.MergedDirty,"unchanged frame repacked");
        Frame("depth resident changed traversal",new[]{2,1,3},2,middle.Layer,first.Layer,last.Layer);
        Frame("depth resident adjacent A A B",new[]{1,3,2},2,first.Layer,last.Layer,middle.Layer);
        middle.Layer.ResetVisibility(); middle.Layer.BuildVisibleIndices();
        Frame("depth resident hidden middle",new[]{1,3},1,first.Layer,middle.Layer,last.Layer);
        middle.Layer.ResetVisibility(); middle.Layer.SetVisible(0,255); middle.Layer.BuildVisibleIndices();
        Frame("depth resident restored middle",new[]{1,2,3},3,first.Layer,middle.Layer,last.Layer);
        Frame("depth resident category order",new[]{4,1,2,3},4,first.Layer,ground.Layer,middle.Layer,last.Layer);
        resident.Uploaded();
        // ChunkMesh writes stretched land directly, then calls this seam.
        ground.Layer.Vertices[0].Position0.X = 40;
        ground.Layer.InvalidateQuadRecord(0);
        Frame("depth resident direct land write",new[]{40},1,ground.Layer);
        CheckThat("depth resident direct write uploaded",resident.DirtyQuads.Count>0,"no dirty geometry");
        resident.Uploaded();
        // A pooled chunk can rebuild the same 64-quad count entirely through
        // direct stretched-land writes. No WriteQuadAt stamp change occurs.
        void RefillLand(int marker)
        {
            ground.Layer.Count = 1;
            ground.Layer.Vertices[0].Position0.X = marker;
            ground.Layer.Textures[0] = red;
            ground.Layer.Depths[0] = marker;
            ground.Layer.ResetVisibility(); ground.Layer.SetVisible(0,255);
            ground.Layer.BuildVisibleIndices();
        }
        ground.Layer.SoftReset(); RefillLand(80);
        Frame("depth resident pooled same-count land",new[]{80},1,ground.Layer);
        CheckThat("depth resident pooled texture refreshed",resident.Draws[0].Textures.Art0==red.GetRid(),"stale texture binding");
        CheckThat("depth resident pooled depth refreshed",resident.Quads[resident.Merged[0]].Aux.X==80,"stale depth");
        resident.Uploaded();
        ground.Layer.Reset(); RefillLand(120);
        Frame("depth resident same-count rebuild",new[]{120},1,ground.Layer);
        resident.Uploaded();
        Frame("depth resident rebuilt steady frame",new[]{120},1,ground.Layer);
        CheckThat("depth resident rebuilt steady frame reuses geometry",!resident.MergedDirty && resident.DirtyQuads.Count==0,"unchanged rebuilt frame uploads");

    }
}
#endif
