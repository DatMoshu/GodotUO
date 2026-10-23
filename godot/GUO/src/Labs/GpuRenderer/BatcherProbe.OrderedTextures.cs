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
    private async Task VerifyOrderedTextures(UltimaBatcher2D batcher, SubViewport viewport, Texture2D palette)
    {
        ImageTexture Pattern(int width, int height, int seed)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                pixels[i * 4] = (byte)(seed + i * 17);
                pixels[i * 4 + 1] = (byte)(seed * 3 + i * 53);
                pixels[i * 4 + 2] = (byte)(seed * 7 + i * 29);
                pixels[i * 4 + 3] = 255;
            }
            using var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
            return ImageTexture.CreateFromImage(image);
        }
        using var a = Pattern(2, 3, 31);
        using var b = Pattern(7, 5, 63);
        using var c = Pattern(1, 4, 97);
        using var d = Pattern(5, 1, 131);
        using var e = Pattern(3, 2, 163);
        using var alternatePalette = BuildSolid(new byte[] { 27, 99, 180 });
        using var black = BuildSolid(new byte[] { 0, 0, 0 });
        Texture2D[] textures = { a, b, c, d, e };
        int Differences(byte[] x, byte[] y)
        {
            int n = 0;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) n++;
            return n;
        }
        async Task<byte[]> Render(bool rd, int[] sequence, bool alternate = false, bool present = true)
        {
            OrderedWorldCapture.Enabled = rd;
            batcher.HueTexture = batcher.LightTexture = alternate ? alternatePalette : palette;
            batcher.BeginFrame();
            batcher.Begin(Transform2D.Identity);
            batcher.SetBrightlight(0.3f);
            batcher.SetCircleOfTransparencyRadius(0);
            batcher.Draw(black, new Rectangle(-32,-32,64,64), new Rectangle(0,0,1,1),new Vector3(0,0,1),0);
            batcher.BeginOrderedWorld(true, viewport.Size);
            foreach (int index in sequence)
            {
                Texture2D texture = textures[index];
                // Identical geometry/UVs under different original views, all
                // overlapping and translucent: sorting or wrong slots changes pixels.
                batcher.Draw(texture,new Rectangle(0,0,viewport.Size.X,viewport.Size.Y),
                    new Rectangle(0,0,texture.GetWidth(),texture.GetHeight()),new Vector3(33,alternate ? 1 : 0,0.5f),0);
            }
            batcher.EndOrderedWorld(); batcher.End();
            if (!present) return Array.Empty<byte>();
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using Image image = viewport.GetTexture().GetImage();
            return image.GetData();
        }
        async Task Parity(string label, int[] sequence, int draws, bool alternate = false)
        {
            byte[] reference = await Render(false,sequence,alternate);
            long frames = OrderedWorldCapture.FramesDrawn, calls = OrderedWorldCapture.DrawCalls;
            byte[] actual = await Render(true,sequence,alternate);
            bool fallback = OrderedWorldCapture.InjectFailure != 0 || OrderedWorldCapture.InjectInvalidTextureSlot;
            CheckThat(label + " pixels",Differences(reference,actual)==0,"ordered texture pixels differ");
            CheckThat(label + " backend",OrderedWorldCapture.FramesDrawn==frames+(fallback ? 0 : 1),OrderedWorldCapture.LastFallback);
            if (!fallback) CheckThat(label + " draws",OrderedWorldCapture.DrawCalls-calls==draws,$"draws={OrderedWorldCapture.DrawCalls-calls}");
        }
        try
        {
            await Parity("RD texture A/B/A alpha order",new[] {0,1,0},1);
            CheckThat("RD compiled shader layout and literal bindings",OrderedWorldShaderContract.ValidatedPrograms>0,"emitted shader contract not checked");
            byte[] reference = await Render(false,new[] {0,1,0});
            long frames = OrderedWorldCapture.FramesDrawn;
            OrderedWorldCapture.InjectTextureSlotFault = true;
            byte[] wrong = await Render(true,new[] {0,1,0});
            CheckThat("RD texture oracle catches wrong slot",Differences(reference,wrong)>0
                && OrderedWorldCapture.FramesDrawn==frames+1,"slot negative control did not change active RD pixels");
            OrderedWorldCapture.InjectTextureSlotFault = false;
            OrderedWorldCapture.InjectInvalidTextureSlot = true;
            await Parity("RD texture out-of-range selector falls back",new[] {0,1,0},1);
            OrderedWorldCapture.InjectInvalidTextureSlot = false;
            await Parity("RD texture all four slots and size/edge UVs",new[] {0,1,2,3,0,2,1,3},1);
            await Parity("RD texture binding tuple baseline",new[] {0,1,2,3},1);
            await Parity("RD texture binding tuple reordered",new[] {3,2,1,0},1);
            await Parity("RD texture binding tuple reused",new[] {0,1,2,3},1);
            await Parity("RD texture fifth view starts second batch",new[] {0,1,2,3,4,0,4},2);
            await Parity("RD texture repeated capacity flushes",new[] {0,1,2,3,4,0,1,2,3},3);

            int[] initial = {0,1,2,0}, changed = {0,1,2,1};
            await Parity("RD texture selector baseline",initial,1);
            long state = OrderedWorldDevice.StateBytes, writes = OrderedWorldDevice.StateUploadCalls;
            long geometry = OrderedWorldDevice.GeometryBytes, order = OrderedWorldDevice.OrderBytes;
            await Parity("RD texture selector-only change",changed,1);
            CheckThat("RD texture selector changes only state",OrderedWorldDevice.StateBytes-state==128
                && OrderedWorldDevice.StateUploadCalls-writes==1 && OrderedWorldDevice.GeometryBytes==geometry
                && OrderedWorldDevice.OrderBytes==order,"selector contaminated geometry/order or upload count");
            state = OrderedWorldDevice.StateBytes; writes = OrderedWorldDevice.StateUploadCalls;
            await Parity("RD texture unchanged selector stream",changed,1);
            CheckThat("RD texture unchanged selector uploads zero",OrderedWorldDevice.StateBytes==state
                && OrderedWorldDevice.StateUploadCalls==writes,"unchanged selector rewrote state");
            await Parity("RD texture palette replacement",changed,1,alternate:true);
            await Parity("RD texture original palette restored",changed,1);

            OrderedWorldCapture.InjectFailure = 2;
            await Parity("RD texture post-record failure replay",new[] {0,1,2,3,4,0},2);
            OrderedWorldCapture.InjectFailure = 0;
            await Parity("RD texture identical retry",new[] {0,1,2,3,4,0},2);
            reference = await Render(false,changed);
            frames = OrderedWorldCapture.FramesDrawn;
            await Render(true,new[] {4,3,2,1,0},present:false);
            byte[] replacement = await Render(true,changed);
            CheckThat("RD texture canceled bindings replaced",Differences(reference,replacement)==0
                && OrderedWorldCapture.FramesDrawn==frames+1,"old bindings leaked from canceled ticket");
            viewport.Size = new Vector2I(12,12);
            await Parity("RD texture resize restores bindings",new[] {4,3,2,1,0},2);
            await Parity("RD texture empty clears target",Array.Empty<int>(),0);
            await Parity("RD texture regrowth and off/on",initial,1);
        }
        finally
        {
            OrderedWorldCapture.InjectFailure = 0;
            OrderedWorldCapture.InjectTextureSlotFault = false;
            OrderedWorldCapture.InjectInvalidTextureSlot = false;
            batcher.HueTexture = batcher.LightTexture = palette;
            viewport.Size = new Vector2I(8,8);
        }
    }
}
#endif
