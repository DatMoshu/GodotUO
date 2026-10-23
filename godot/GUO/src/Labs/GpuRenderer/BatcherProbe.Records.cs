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
    private async Task VerifyOrderedRecords(UltimaBatcher2D batcher, SubViewport viewport, Texture2D palette)
    {
        bool oldEnabled = OrderedWorldRecords.Enabled;
        OrderedWorldRecords.Enabled = true;
        using var owner = new MeshLayerDisposable();
        MeshLayer layer = owner.Layer;
        using var black = BuildSolid(new byte[] { 0, 0, 0 });
        using var art = BuildSolid(new byte[] { 153, 121, 87 });
        using var other = BuildSolid(new byte[] { 47, 173, 211 });
        layer.Count = 2;
        layer.WriteQuadAtHd(0, art, new Rectangle(0, 0, 1, 1), 1, 1, 7, 8, new Vector3(0, 0, 0.5f), 0);
        layer.WriteQuadAtHd(1, other, new Rectangle(0, 0, 1, 1), 0, 0, 5, 6, new Vector3(0, 5, 0.6f), 0);
        layer.SetVisible(0, 0); layer.SetVisible(1, 0);
        long first = layer.QuadRecordVersion(0), second = layer.QuadRecordVersion(1);
        layer.ResetVisibility(); layer.SetVisible(0, 0); layer.SetVisible(1, 0);
        CheckThat("source visibility leaves content revisions unchanged", layer.QuadRecordVersion(0) == first && layer.QuadRecordVersion(1) == second, "order is independent");
        layer.SetHue(0, 33, 2);
        CheckThat("source hue invalidates only its quad", layer.QuadRecordVersion(0) != first && layer.QuadRecordVersion(1) == second, "per-index dirty stamp");
        first = layer.QuadRecordVersion(0);
        layer.SetHue(0, 33, 2);
        CheckThat("unchanged source hue retains revision", layer.QuadRecordVersion(0) == first, "no redundant invalidation");
        layer.SetVisible(0, 120, true);
        first = layer.QuadRecordVersion(0);
        layer.ResetAlpha();
        CheckThat("alpha reset invalidates its source record", layer.QuadRecordVersion(0) != first && layer.QuadRecordVersion(1) == second, "circle and fade reset");
        layer.SetVisible(0, 128);
        layer.BuildVisibleIndices();
        batcher.HueTexture = batcher.LightTexture = palette;
        try
        {
            async Task<byte[]> Render(bool rd, int offset = 0, bool mutate = false, bool clip = false)
            {
                if (mutate) layer.SetHue(0, 0, 0);
                OrderedWorldCapture.Enabled = rd;
                batcher.BeginFrame();
                batcher.Begin(new Transform2D(new Vector2(0.75f, 0), new Vector2(0, 0.75f), new Vector2(0.375f, 0.625f)));
                batcher.SetBrightlight(0.3f);
                batcher.Draw(black, new Rectangle(-20, -20, 40, 40), new Rectangle(0, 0, 1, 1), new Vector3(0, 0, 1), 0);
                batcher.BeginOrderedWorld(true, viewport.Size);
                batcher.DrawStaticMeshSprite(layer, 0, offset, -offset);
                if (mutate) layer.SetHue(0, 33, 2);
                batcher.DrawMeshSprite(layer, 1, offset, -offset);
                // Every ordered occurrence survives even when it reuses a record.
                batcher.DrawStaticMeshSprite(layer, 0, offset, -offset);
                batcher.DrawMeshSprite(layer, 1, offset, -offset);
                if (clip)
                {
                    batcher.ClipBegin(1, 1, 3, 3);
                    batcher.Draw(other, new Rectangle(0, 0, 4, 4), new Rectangle(0, 0, 1, 1), new Vector3(0, 0, 0.5f), 0);
                    batcher.ClipEnd();
                }
                batcher.EndOrderedWorld(); batcher.End();
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using Image image = viewport.GetTexture().GetImage();
                return image.GetData();
            }
            async Task Parity(string label, int offset = 0, bool mutate = false, bool clip = false)
            {
                byte[] reference = await Render(false, offset, mutate, clip);
                long drawn = OrderedWorldCapture.FramesDrawn;
                byte[] actual = await Render(true, offset, mutate, clip);
                CheckThat(label + " pixels", reference.AsSpan().SequenceEqual(actual), "exact canvas pixels including repeated alpha");
                bool fallback = clip || OrderedWorldCapture.InjectFailure != 0;
                CheckThat(label + " backend", OrderedWorldCapture.FramesDrawn == drawn + (fallback ? 0 : 1), "active RD or required replay");
            }
            await Parity("source records retain rectangle and covering mesh arithmetic");
            long packed = OrderedWorldRecords.Packed, hits = OrderedWorldRecords.Hits, bytes = OrderedWorldDevice.GeometryBytes;
            await Render(true);
            CheckThat("unchanged source draws pack and upload no geometry", OrderedWorldRecords.Packed == packed && OrderedWorldDevice.GeometryBytes == bytes && OrderedWorldRecords.Hits == hits + 4, "four occurrences reused");
            layer.ResetAlpha(); layer.SetVisible(0, 128); layer.BuildVisibleIndices();
            await Render(true);
            CheckThat("transient alpha reset retains identical final record", OrderedWorldRecords.Packed == packed && OrderedWorldDevice.GeometryBytes == bytes, "dirty source checked without repacking or reupload");
            layer.SetHue(0, 37, 2);
            await Parity("source hue refresh");
            CheckThat("one source mutation packs only its record", OrderedWorldRecords.Packed == packed + 1, "cover record stayed resident");
            layer.SetVisible(1, 90, true); layer.BuildVisibleIndices();
            await Parity("source circle and alpha refresh");
            layer.Vertices[1].Normal1 = new Vector3(0, -1, 1);
            layer.Vertices[1].Position1.Y += 1;
            layer.InvalidateQuadRecord(1);
            await Parity("source direct stretched geometry refresh");
            await Parity("source camera subtraction", offset: 2);
            await Parity("source immutable revisions within one frame", mutate: true);
            await Parity("source early prefix replay", clip: true);
            OrderedWorldCapture.InjectFailure = 2;
            await Parity("source late whole-frame replay");
            OrderedWorldCapture.InjectFailure = 0;
            await Parity("source recovery after failed pass");
            var q0 = layer.Vertices[0]; var q1 = layer.Vertices[1];
            layer.SoftReset(); layer.Count = 2;
            q0.Position0.X += 1; q0.Position1.X += 1; q0.Position2.X += 1; q0.Position3.X += 1;
            layer.Vertices[0] = q0; layer.Vertices[1] = q1;
            layer.Textures[0] = other; layer.Textures[1] = art;
            layer.SetVisible(0, 0); layer.SetVisible(1, 0); layer.BuildVisibleIndices();
            await Parity("source pooled layer replacement");
            OrderedWorldRecords.Enabled = false;
            await Parity("source diagnostic content-cache control");
        }
        finally
        {
            OrderedWorldRecords.Enabled = oldEnabled;
            OrderedWorldCapture.InjectFailure = 0;
        }
    }
}
#endif
