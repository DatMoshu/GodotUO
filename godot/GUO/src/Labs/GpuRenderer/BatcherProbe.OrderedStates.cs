// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Threading.Tasks;
using Godot;
using GUO.Labs.GpuRenderer;
using GUO.Renderer;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Host;

public partial class BatcherProbe
{
    private async Task VerifyOrderedStates(UltimaBatcher2D batcher, SubViewport viewport, Texture2D palette)
    {
        using var a = BuildSolid(new byte[] { 177, 103, 51 });
        using var b = BuildSolid(new byte[] { 30, 140, 210 });
        using var black = BuildSolid(new byte[] { 0, 0, 0 });
        using var slope = new MeshLayerDisposable();
        slope.Layer.Count = 1;
        slope.Layer.WriteQuadAt(0,a,new Rectangle(0,0,1,1),0,0,new Vector3(0,5,0.5f),0);
        MeshQuad land = slope.Layer.Vertices[0];
        land.Position0 = Vector2.Zero; land.Position1 = new Vector2(5,0);
        land.Position2 = new Vector2(0,5); land.Position3 = new Vector2(5,5);
        land.Normal0 = land.Normal1 = land.Normal2 = land.Normal3 = new Vector3(0,1,1);
        slope.Layer.Vertices[0] = land;
        slope.Layer.SetVisible(0,128); slope.Layer.BuildVisibleIndices();
        batcher.HueTexture = batcher.LightTexture = palette;
        int Differences(byte[] x, byte[] y)
        {
            int count = 0;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) count++;
            return count;
        }
        async Task<byte[]> Render(bool rd, int count = 3, bool aba = false, int change = 0,
            bool camera = false, float brightness = 0.3f, float radius = 0, bool present = true,
            bool unsupported = false, bool lighting = false)
        {
            OrderedWorldCapture.Enabled = rd;
            batcher.BeginFrame();
            batcher.Begin(new Transform2D(new Vector2(0.75f,0), new Vector2(0,0.75f),
                camera ? new Vector2(0.625f,0.375f) : new Vector2(0.375f,0.625f)));
            batcher.SetBrightlight(brightness);
            batcher.SetCircleOfTransparencyRadius(radius);
            batcher.CircleOfTransparencyCenter = new Vector2(4,4);
            batcher.Draw(black, new Rectangle(-16384,-16384,32768,32768), new Rectangle(0,0,1,1),new Vector3(0,0,1),0);
            batcher.BeginOrderedWorld(true, viewport.Size);
            for (int i = 0; i < count; i++)
            {
                int offset = change == 1 && i == 0 || change == 2 && (i == 0 || i == count - 1) ? 1 : 0;
                batcher.SetWorldOffset((i % 3) * 2 + offset, i % 2);
                Texture2D texture = aba && i % 3 == 1 ? b : a;
                // Repeated identical geometry under independent offsets and
                // local matrices. Alpha makes the original order observable.
                var hue = new Vector3(0, lighting ? 5 : 0, lighting ? 0.5f : 1.5f);
                if (lighting)
                {
                    if (batcher.OrderedWorldRecording) batcher.DrawOrderedLand(slope.Layer);
                    else batcher.DrawMeshLayer(slope.Layer);
                }
                else if (i % 3 == 0)
                    batcher.Draw(texture,new Rectangle(0,0,5,5),new Rectangle(0,0,1,1),hue,0);
                else
                    batcher.Draw(texture,Vector2.Zero,new Rectangle(0,0,1,1),hue,
                        i % 3 == 1 ? 0.2f : -0.15f,Vector2.Zero,i % 3 == 1 ? 4f : -4f,
                        SpriteEffects.FlipHorizontally,0);
            }
            if (unsupported)
            {
                batcher.ClipBegin(1,1,4,4);
                batcher.Draw(b,new Rectangle(0,0,6,6),new Rectangle(0,0,1,1),new Vector3(0,0,0.5f),0);
                batcher.ClipEnd();
            }
            batcher.EndOrderedWorld();
            batcher.End();
            if (!present) return Array.Empty<byte>();
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using Image image = viewport.GetTexture().GetImage();
            return image.GetData();
        }
        async Task Parity(string label, int count = 3, bool aba = false, int change = 0,
            bool camera = false, float brightness = 0.3f, float radius = 0, bool unsupported = false, bool lighting = false)
        {
            byte[] reference = await Render(false,count,aba,change,camera,brightness,radius,unsupported:unsupported,lighting:lighting);
            long drawn = OrderedWorldCapture.FramesDrawn, calls = OrderedWorldCapture.DrawCalls;
            byte[] actual = await Render(true,count,aba,change,camera,brightness,radius,unsupported:unsupported,lighting:lighting);
            int differences = Differences(reference, actual);
            bool fallback = OrderedWorldCapture.InjectFailure != 0 || unsupported;
            CheckThat(label + " pixels", differences == 0, $"{differences} differing channels");
            CheckThat(label + " backend", OrderedWorldCapture.FramesDrawn == drawn + (fallback ? 0 : 1),
                $"RD delta={OrderedWorldCapture.FramesDrawn-drawn}, fallback={OrderedWorldCapture.LastFallback}");
            if (!fallback)
                CheckThat(label + " draws", OrderedWorldCapture.DrawCalls - calls == (count == 0 ? 0 : aba ? count : 1),
                    $"draws={OrderedWorldCapture.DrawCalls-calls}, occurrences={count}");
        }
        try
        {
            await Parity("RD occurrence transforms merge adjacent texture");
            await Parity("RD occurrence A/B/A retains alpha order",aba:true);
            byte[] reference = await Render(false,aba:true);
            long drawn = OrderedWorldCapture.FramesDrawn;
            OrderedWorldCapture.InjectStateIndexFault = true;
            byte[] wrong = await Render(true,aba:true);
            CheckThat("RD occurrence oracle catches draw-local index", Differences(reference,wrong) > 0
                && OrderedWorldCapture.FramesDrawn == drawn + 1,"negative control did not expose indexing error on active RD");
            OrderedWorldCapture.InjectStateIndexFault = false;
            await Parity("RD occurrence absolute index restored",aba:true);
            long bytes = OrderedWorldDevice.StateBytes, uploads = OrderedWorldDevice.StateUploadCalls;
            await Parity("RD occurrence unchanged stream",aba:true);
            CheckThat("RD occurrence unchanged uploads zero",OrderedWorldDevice.StateBytes == bytes && OrderedWorldDevice.StateUploadCalls == uploads,"redundant state write");
            long geometry = OrderedWorldDevice.GeometryBytes, order = OrderedWorldDevice.OrderBytes;
            await Parity("RD occurrence one transformed record",aba:true,change:1);
            CheckThat("RD occurrence one change bulk upload",OrderedWorldDevice.StateBytes - bytes == 96
                && OrderedWorldDevice.StateUploadCalls - uploads == 1 && OrderedWorldDevice.GeometryBytes == geometry && OrderedWorldDevice.OrderBytes == order,"state-only upload mismatch");
            await Parity("RD occurrence baseline before distant changes",aba:true);
            bytes = OrderedWorldDevice.StateBytes; uploads = OrderedWorldDevice.StateUploadCalls;
            await Parity("RD occurrence distant transforms",aba:true,change:2);
            CheckThat("RD occurrence distant changes one upload",OrderedWorldDevice.StateBytes - bytes == 96
                && OrderedWorldDevice.StateUploadCalls - uploads == 1 && OrderedWorldDevice.GeometryBytes == geometry && OrderedWorldDevice.OrderBytes == order,"state-only bulk mismatch");
            bytes = OrderedWorldDevice.StateBytes;
            await Parity("RD occurrence camera-only change",aba:true,change:2,camera:true);
            CheckThat("RD occurrence camera changes no geometry",OrderedWorldDevice.StateBytes - bytes == 96
                && OrderedWorldDevice.GeometryBytes == geometry && OrderedWorldDevice.OrderBytes == order,"camera invalidated resident geometry/order");
            await Parity("RD occurrence brightness baseline",lighting:true);
            bytes = OrderedWorldDevice.StateBytes;
            byte[] dim = await Render(true,lighting:true);
            await Parity("RD occurrence brightness remains global",brightness:0.7f,lighting:true);
            byte[] lit = await Render(true,brightness:0.7f,lighting:true);
            CheckThat("RD occurrence brightness changes pixels only",Differences(dim,lit)>0 && OrderedWorldDevice.StateBytes == bytes,"brightness lost or state rewritten");
            await Parity("RD occurrence circle baseline");
            bytes = OrderedWorldDevice.StateBytes;
            byte[] solid = await Render(true);
            await Parity("RD occurrence radius remains global",radius:4);
            byte[] circle = await Render(true,radius:4);
            CheckThat("RD occurrence circle changes pixels only",Differences(solid,circle)>0 && OrderedWorldDevice.StateBytes == bytes,"radius lost or state rewritten");

            // Resize starts small state/order allocations. 129 repeats need a
            // larger state buffer while geometry and order capacities still fit.
            viewport.Size = new Vector2I(12,12);
            await Parity("RD occurrence resize restores state");
            long growth = OrderedWorldDevice.BufferGrowths, stateGrowth = OrderedWorldDevice.StateBufferGrowths;
            geometry = OrderedWorldDevice.GeometryBytes;
            await Parity("RD occurrence state-only capacity growth",count:129);
            CheckThat("RD occurrence only state buffer grew",OrderedWorldDevice.BufferGrowths-growth == 1
                && OrderedWorldDevice.StateBufferGrowths-stateGrowth == 1 && OrderedWorldDevice.GeometryBytes == geometry,"growth changed unrelated buffers/geometry");
            await Parity("RD occurrence shrinks without stale tail");
            bytes = OrderedWorldDevice.StateBytes; uploads = OrderedWorldDevice.StateUploadCalls;
            await Parity("RD occurrence empty clears target",count:0);
            CheckThat("RD occurrence empty has no upload",OrderedWorldDevice.StateBytes == bytes && OrderedWorldDevice.StateUploadCalls == uploads
                && OrderedWorldDevice.ActiveStateBytes == 0,"empty state issued upload or retained active bytes");
            await Parity("RD occurrence regrowth uploads active rows");
            CheckThat("RD occurrence regrowth upload",OrderedWorldDevice.StateBytes-bytes == 96,"regrowth trusted stale active length");
            foreach (int failure in new[] { 5, 6 })
            {
                OrderedWorldCapture.InjectFailure = failure;
                await Parity("RD occurrence failed state upload " + failure,change:failure==5 ? 1 : 2);
                OrderedWorldCapture.InjectFailure = 0;
                bytes = OrderedWorldDevice.StateBytes;
                await Parity("RD occurrence identical retry " + failure,change:failure==5 ? 1 : 2);
                CheckThat("RD occurrence retry uploads invalid state " + failure,OrderedWorldDevice.StateBytes-bytes == 96,"failed upload falsely applied");
            }
            await Parity("RD occurrence early rejection preserves suffix",aba:true,unsupported:true);
            reference = await Render(false,aba:true,change:2);
            drawn = OrderedWorldCapture.FramesDrawn;
            await Render(true,change:1,present:false);
            byte[] replacement = await Render(true,aba:true,change:2);
            CheckThat("RD occurrence canceled ticket replaced exactly",Differences(reference,replacement)==0
                && OrderedWorldCapture.FramesDrawn==drawn+1,"canceled transform or texture stream submitted");
            await Parity("RD occurrence off/on current state",aba:true);
        }
        finally
        {
            OrderedWorldCapture.InjectFailure = 0;
            OrderedWorldCapture.InjectStateIndexFault = false;
            viewport.Size = new Vector2I(8,8);
        }
    }
}
#endif
