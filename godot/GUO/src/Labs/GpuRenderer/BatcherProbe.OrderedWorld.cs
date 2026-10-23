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
    private async Task VerifyOrderedWorld(UltimaBatcher2D batcher, SubViewport viewport, Texture2D palette)
    {
        VerifyOrderedResidency();
        bool old = OrderedWorldCapture.Enabled;
        using var background = BuildSolid(new byte[] { 3, 80, 137 });
        using var black = BuildSolid(new byte[] { 0, 0, 0 });
        using var gray = BuildSolid(new byte[] { 123, 123, 123 });
        using var colored = BuildSolid(new byte[] { 177, 103, 51 });
        byte[] pixels = new byte[2048*2048*4];
        for(int i=0;i<2048*2048;i++) { int x=i%2048,y=i/2048; pixels[i*4]=(byte)(x*23+y*47); pixels[i*4+1]=(byte)(x*137+y*13); pixels[i*4+2]=(byte)(x*47+y*173); pixels[i*4+3]=255; }
        using var patternImage = Image.CreateFromData(2048,2048,false,Image.Format.Rgba8,pixels);
        using var pattern = ImageTexture.CreateFromImage(patternImage);
        using var land = new MeshLayerDisposable();
        MeshLayer layer = land.Layer;
        layer.Count = 1;
        layer.WriteQuadAt(0, colored, new Rectangle(0,0,1,1), 0,0, new Vector3(0,5,1),0);
        MeshQuad q = layer.Vertices[0];
        q.Position0 = new Vector2(0,0); q.Position1 = new Vector2(8,1);
        q.Position2 = new Vector2(1,7); q.Position3 = new Vector2(8,8);
        q.Normal0 = new Vector3(0,1,1); q.Normal1 = new Vector3(0,-1,1);
        q.Normal2 = new Vector3(1,0,0.2f); q.Normal3 = new Vector3(0,0,1);
        layer.Vertices[0] = q;
        layer.SetVisible(0,137); layer.BuildVisibleIndices();
        batcher.HueTexture = batcher.LightTexture = palette;
        try
        {
            async Task<byte[]> Render(bool rd, int mode, bool slope = false, bool unsupported = false, bool empty = false, bool fractional = false, bool present = true,
                int residentCount = 0, int residentSeed = 0, bool reverse = false, int poolItems = 0)
            {
                OrderedWorldCapture.Enabled = rd;
                batcher.BeginFrame();
                batcher.Begin(mode==99 ? new Transform2D(new Vector2(0.5f,0),new Vector2(0,0.5f),Vector2.Zero)
                    : fractional ? new Transform2D(new Vector2(0.75f,0),new Vector2(0,0.75f),new Vector2(0.375f,0.625f)) : Transform2D.Identity);
                batcher.SetBrightlight(0.3f);
                batcher.SetCircleOfTransparencyRadius(mode == 128 ? 4 : 0);
                batcher.CircleOfTransparencyCenter = new Vector2(4,4);
                // The live WorldRenderTarget clears opaque black; this probe's default clear is grey.
                batcher.Draw(black,new Rectangle(-16384,-16384,32768,32768),new Rectangle(0,0,1,1),new Vector3(0,0,1),0);
                batcher.BeginOrderedWorld(true, viewport.Size);
                if (!empty)
                {
                    batcher.Draw(background, new Rectangle(-4,-4,24,24), new Rectangle(0,0,1,1), new Vector3(0,0,1), 0);
                    if (residentCount > 0)
                    {
                        for (int n = 0; n < residentCount; n++)
                        {
                            int i = reverse ? residentCount - 1 - n : n;
                            // Overlapping translucent quads make order observable. Additional
                            // offscreen quads force storage growth without changing the fixture.
                            batcher.Draw(i % 2 == 0 ? colored : gray,
                                new Rectangle(i, residentSeed, 7, 8), new Rectangle(0,0,1,1),
                                new Vector3(0,0,0.5f), 0);
                        }
                    }
                    else if (mode == 104)
                    {
                        batcher.SetSampler(SamplerState.LinearClamp);
                        batcher.Draw(pattern,new Rectangle(0,0,8,8),new Rectangle(19,71,6,6),new Vector3(0,0,1),0);
                    }
                    else if (mode == 99)
                        batcher.Draw(pattern,new Rectangle(1,3,101,133),new Rectangle(1117,139,101,133),new Vector3(0,0,1),0);
                    else if (mode == 100)
                        batcher.DrawShadow(pattern, new Vector2(0,6), new Rectangle(1117,139,8,8), true, 0);
                    else if (mode == 101)
                        batcher.Draw(pattern, new Vector2(1,1), new Rectangle(19,71,6,6), new Vector3(0,0,0.5f),
                            0.33f, Vector2.Zero, 0.9f, SpriteEffects.FlipHorizontally, 0);
                    else if (slope)
                    {
                        if (batcher.OrderedWorldRecording) batcher.DrawOrderedLand(layer);
                        else batcher.DrawMeshLayer(layer);
                    }
                    else
                    {
                        batcher.Draw(mode == 0 ? black : gray, new Rectangle(0,0,7,8), new Rectangle(0,0,1,1),
                            new Vector3(33, mode == 128 ? 0 : mode, mode == 128 ? 2 : 64 / 255f), 0);
                        batcher.Draw(black, new Rectangle(2,1,6,7), new Rectangle(0,0,1,1), new Vector3(0,0,64 / 255f), 0);
                    }
                    if (unsupported)
                    {
                        batcher.ClipBegin(1,1,4,4);
                        batcher.Draw(colored,new Rectangle(0,0,8,8),new Rectangle(0,0,1,1),new Vector3(0,0,1), 0);
                        batcher.ClipEnd();
                    }
                    if (mode == 103) batcher.SetBrightlight(0.7f);
                }
                for (int i = 0; i < poolItems; i++)
                {
                    batcher.SetWorldOffset(i % 3, i % 2);
                    batcher.Draw(colored, new Rectangle(i % 5, i % 7, 3, 3), new Rectangle(0,0,1,1), new Vector3(0,0,0.5f), 0);
                }
                batcher.EndOrderedWorld();
                batcher.End();
                // Synchronously build the next frame without yielding to native FramePreDraw.
                if (!present) return Array.Empty<byte>();
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using Image image = viewport.GetTexture().GetImage();
                return image.GetData();
            }
            async Task Parity(string label,int mode,bool slope=false,bool unsupported=false,bool empty=false,bool fractional=false)
            {
                byte[] reference = await Render(false,mode,slope,unsupported,empty,fractional);
                long before = OrderedWorldCapture.FramesDrawn;
                long replayBefore = OrderedWorldCanvas.CommandsDrawn;
                byte[] actual = await Render(true,mode,slope,unsupported,empty,fractional);
                int differences = 0;
                for(int i=0;i<reference.Length;i++) if(reference[i]!=actual[i]) differences++;
                CheckThat(label,differences==0,$"{differences} differing channels; fallback={OrderedWorldCapture.LastFallback}");
                bool expectFallback = unsupported || mode is 102 or 103 or 104 || OrderedWorldCapture.InjectFailure != 0;
                CheckThat(label+" backend",expectFallback ? OrderedWorldCapture.FramesDrawn==before : OrderedWorldCapture.FramesDrawn==before+1,
                    $"RD delta={OrderedWorldCapture.FramesDrawn-before}, fallback={OrderedWorldCapture.LastFallback}");
                if (!expectFallback)
                    CheckThat(label+" no duplicate canvas", batcher.Commands == 1 && OrderedWorldCanvas.CommandsDrawn == replayBefore,
                        $"canvas commands={batcher.Commands} (only backdrop expected), replay={OrderedWorldCanvas.CommandsDrawn-replayBefore}");
            }
            foreach(int mode in new[]{0,1,2,3,4,7,8,9,10,20,21,22,128}) await Parity("RD mode "+mode,mode);
            await Parity("RD slopes and alpha",0,slope:true);
            layer.SetVisible(0,128); // Marks the layer dirty; then exercise exact 0.5, not 128/255.
            layer.Vertices[0].Hue0.Z = layer.Vertices[0].Hue1.Z = layer.Vertices[0].Hue2.Z = layer.Vertices[0].Hue3.Z = 0.5f;
            layer.BuildVisibleIndices();
            await Parity("RD exact half-alpha mesh quantization",0,slope:true);
            await Parity("RD fractional camera",1,fractional:true);
            await Parity("RD nearest texture at half scale",99);
            await Parity("RD affine mirrored shadow",100,fractional:true);
            await Parity("RD rotated mirrored sprite",101,fractional:true);
            viewport.Size=new Vector2I(2560,1440);
            await Parity("RD atlas at full target half scale",99);
            viewport.Size=new Vector2I(8,8);
            await Parity("RD whole-frame unsupported clip rollback",0,unsupported:true);
            await Parity("RD shared material mutation replay",103,slope:true);
            await Parity("RD sampler mutation preserves prefix and suffix",104);
            OrderedWorldCapture.InjectFailure=1;
            await Parity("RD rollback before upload",0);
            OrderedWorldCapture.InjectFailure=2;
            await Parity("RD rollback after recorded pass",0);
            await Parity("RD deferred mesh replay",0,slope:true,fractional:true);
            await Parity("RD deferred affine shadow replay",100,fractional:true);
            await Parity("RD deferred rotated sprite replay",101,fractional:true);
            await Parity("RD deferred full atlas replay",99);
            OrderedWorldCapture.InjectFailure=0;
            // A partial land prefix must not be replayed and then drawn again by the old all-land path.
            layer.Count = 2;
            layer.WriteQuadAt(1,colored,new Rectangle(0,0,1,1),0,0,new Vector3(0,11,0.5f),0);
            MeshQuad second = layer.Vertices[0];
            second.Hue0.Y = second.Hue1.Y = second.Hue2.Y = second.Hue3.Y = 11;
            layer.Vertices[1] = second;
            layer.SetVisible(1,128); layer.BuildVisibleIndices();
            await Parity("RD reject midway through land",102,slope:true);
            layer.Count = 1; layer.SetVisible(1,0); layer.BuildVisibleIndices();
            byte[] afterFailureReference = await Render(false,1);
            await Render(true,4,unsupported:true);
            byte[] afterFailure = await Render(false,1);
            int stale = 0;
            for (int i=0;i<afterFailure.Length;i++) if(afterFailure[i]!=afterFailureReference[i]) stale++;
            CheckThat("RD fallback children hidden after toggle off", stale==0, $"{stale} stale channels after pool reuse");
            await Parity("RD recovery uses current frame",2);
            // Enlarge the native pool with visible commands, then stop using
            // its tail. Every previous active item must be cleared exactly once.
            byte[] poolReference = await Render(false, 1);
            async Task CheckPoolFrame(string label, bool rd, int expectedClears, bool exact = true)
            {
                long clears = UltimaBatcher2D.OrderedCanvasItemsCleared;
                long drawn = OrderedWorldCapture.FramesDrawn;
                byte[] actual = await Render(rd, 1);
                int differences = 0;
                for (int i = 0; i < actual.Length; i++) if (actual[i] != poolReference[i]) differences++;
                long cleared = UltimaBatcher2D.OrderedCanvasItemsCleared - clears;
                CheckThat(label + " pixels", differences == 0, $"{differences} stale channels");
                CheckThat(label + " native clears", exact ? cleared == expectedClears : cleared >= expectedClears,
                    $"cleared={cleared}, expected{(exact ? "=" : ">=")}{expectedClears}");
                CheckThat(label + " backend", OrderedWorldCapture.FramesDrawn == drawn + (rd ? 1 : 0),
                    $"RD delta={OrderedWorldCapture.FramesDrawn-drawn}");
            }
            await Render(false, 4, poolItems:256);
            int busyItems = batcher.FlushesDone;
            await CheckPoolFrame("RD clears previous busy canvas", true, busyItems);
            int smallItems = batcher.FlushesDone;
            CheckThat("RD pool fixture has an unused tail", busyItems > smallItems + 200,
                $"busy={busyItems}, small={smallItems}");
            await CheckPoolFrame("RD does not reclear idle tail", true, smallItems);
            await CheckPoolFrame("RD toggle off keeps ordinary pool clearing", false, busyItems, exact:false);
            await Render(true, 4, unsupported:true, poolItems:256);
            await CheckPoolFrame("RD clears rejected native suffix and replay children", true, batcher.FlushesDone);
            await Render(true, 4, present:false, poolItems:256);
            await CheckPoolFrame("RD cancelled ticket clears previous active items", true, batcher.FlushesDone);
            await Parity("RD empty world",0,empty:true);
            viewport.Size=new Vector2I(16,12);
            await Parity("RD resize",1);
            viewport.Size=new Vector2I(8,8);
            await Parity("RD resize back",2);
            foreach (bool resumeRd in new[] { false, true })
            {
                byte[] reference = await Render(false, 1);
                long before = OrderedWorldCapture.FramesDrawn;
                await Render(true, 4, present:false);
                byte[] actual = await Render(resumeRd, 1);
                int differences = 0;
                for (int i = 0; i < reference.Length; i++) if (reference[i] != actual[i]) differences++;
                string label = "RD skipped pre-draw resumes " + (resumeRd ? "RD" : "canvas");
                CheckThat(label, differences == 0, $"{differences} differing channels after pool reuse");
                CheckThat(label + " backend", OrderedWorldCapture.FramesDrawn == before + (resumeRd ? 1 : 0),
                    $"RD delta={OrderedWorldCapture.FramesDrawn-before}; stale ticket must never submit");
            }
            async Task ResidentParity(string label, int count, int seed = 0, bool reverse = false)
            {
                byte[] reference = await Render(false, 0, residentCount:count, residentSeed:seed, reverse:reverse);
                long before = OrderedWorldCapture.FramesDrawn;
                byte[] actual = await Render(true, 0, residentCount:count, residentSeed:seed, reverse:reverse);
                int differences = 0;
                for (int i=0;i<reference.Length;i++) if (reference[i]!=actual[i]) differences++;
                CheckThat(label, differences == 0, $"{differences} differing channels");
                CheckThat(label + " backend", OrderedWorldCapture.FramesDrawn == before + (OrderedWorldCapture.InjectFailure == 0 ? 1 : 0),
                    $"RD delta={OrderedWorldCapture.FramesDrawn-before}; fallback={OrderedWorldCapture.LastFallback}");
            }
            await ResidentParity("RD resident warm", 4);
            long geometry = OrderedWorldDevice.GeometryBytes, order = OrderedWorldDevice.OrderBytes;
            await ResidentParity("RD resident unchanged", 4);
            CheckThat("RD resident unchanged uploads zero", OrderedWorldDevice.GeometryBytes == geometry && OrderedWorldDevice.OrderBytes == order,
                $"geometry={OrderedWorldDevice.GeometryBytes-geometry}, order={OrderedWorldDevice.OrderBytes-order}");
            await ResidentParity("RD resident alpha order reversal", 4, reverse:true);
            CheckThat("RD resident order-only upload", OrderedWorldDevice.GeometryBytes == geometry && OrderedWorldDevice.OrderBytes > order,
                $"geometry={OrderedWorldDevice.GeometryBytes-geometry}, order={OrderedWorldDevice.OrderBytes-order}");
            geometry = OrderedWorldDevice.GeometryBytes;
            await ResidentParity("RD resident one new quad", 5, reverse:true);
            CheckThat("RD resident one dirty quad", OrderedWorldDevice.GeometryBytes-geometry == 192,
                $"geometry={OrderedWorldDevice.GeometryBytes-geometry}");
            long growths = OrderedWorldDevice.BufferGrowths;
            await ResidentParity("RD resident storage growth", 512);
            CheckThat("RD resident storage grew", OrderedWorldDevice.BufferGrowths > growths, $"growths={OrderedWorldDevice.BufferGrowths-growths}");
            await ResidentParity("RD resident old slots after growth", 4);
            OrderedWorldCapture.InjectFailure = 3;
            await ResidentParity("RD resident failed partial geometry upload", 4, seed:1);
            OrderedWorldCapture.InjectFailure = 0;
            await ResidentParity("RD resident retries identical IDs after geometry failure", 4, seed:1);
            OrderedWorldCapture.InjectFailure = 4;
            await ResidentParity("RD resident failed order upload", 4, seed:1, reverse:true);
            OrderedWorldCapture.InjectFailure = 0;
            await ResidentParity("RD resident retries identical IDs after order failure", 4, seed:1, reverse:true);
            viewport.Size = new Vector2I(12,10);
            geometry = OrderedWorldDevice.GeometryBytes; order = OrderedWorldDevice.OrderBytes;
            await ResidentParity("RD resident resize with identical IDs", 4, seed:1, reverse:true);
            CheckThat("RD resident resize restores both buffers", OrderedWorldDevice.GeometryBytes > geometry && OrderedWorldDevice.OrderBytes > order,
                $"geometry={OrderedWorldDevice.GeometryBytes-geometry}, order={OrderedWorldDevice.OrderBytes-order}");
            viewport.Size = new Vector2I(8,8);
            await VerifyOrderedStates(batcher, viewport, palette);
        }
        finally { OrderedWorldCapture.Enabled=old; OrderedWorldCapture.InjectFailure=0; }
    }

    private sealed class MeshLayerDisposable : IDisposable
    {
        internal readonly MeshLayer Layer = new();
        public void Dispose() => Layer.Dispose();
    }
}
#endif
