// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using GUO.Game;
using GUO.Game.Scenes;
using GUO.Renderer;
using UiControl = GUO.Game.UI.Controls.Control;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Host;

public partial class BatcherProbe
{
    private sealed class UiTextureOwner : UiControl
    {
        internal UiTextureOwner(int width = 0, int height = 0) { Width = width; Height = height; }
    }

    // Exact pre-patch field types/order. The running target measures both;
    // do not replace this oracle with an architecture-specific size constant.
    private readonly struct LegacyUiCommandLayout
    {
        public readonly RenderedText Text;
        public readonly int X;
        public readonly int Y;
        public readonly float LayerDepth;
        public readonly float Alpha;
        public readonly ushort Hue;
        public readonly Func<UltimaBatcher2D, bool> Callback;

        internal LegacyUiCommandLayout(RenderedText text, int x, int y, float depth,
            float alpha, ushort hue, Func<UltimaBatcher2D, bool> callback)
        { Text = text; X = x; Y = y; LayerDepth = depth; Alpha = alpha; Hue = hue; Callback = callback; }
    }

    private static void QueueLegacyUiTexture(RenderLists lists, UiControl owner, Texture2D texture,
        int x, int y, Rectangle source, Vector3 hue, float depth, bool tiled)
    {
        // Separate method so the typed caller cannot acquire this display class.
        if (tiled)
            lists.AddGumpWithAtlas(b => { b.DrawTiled(texture, new Rectangle(x, y, owner.Width, owner.Height), source, hue, depth); return true; });
        else
            lists.AddGumpWithAtlas(b => { b.Draw(texture, new Rectangle(x, y, owner.Width, owner.Height), source, hue, depth); return true; });
    }

    private static void QueueUiTexture(bool typed, RenderLists lists, UiControl owner, Texture2D texture,
        int x, int y, Rectangle source, Vector3 hue, float depth, bool tiled = false)
    {
        if (typed) lists.AddGumpWithAtlas(owner, texture, x, y, source, hue, depth, tiled);
        else QueueLegacyUiTexture(lists, owner, texture, x, y, source, hue, depth, tiled);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueueUiTextureLifetime(RenderLists lists, Texture2D texture)
    {
        var owner = new UiTextureOwner();
        lists.AddGumpWithAtlas(owner, texture, 1, 2, new Rectangle(0, 0, 1, 1), Vector3.Zero, 3);
        return new WeakReference(owner);
    }

    private void VerifyUiTextureStorage(Texture2D texture)
    {
        int beforeSize = Unsafe.SizeOf<LegacyUiCommandLayout>();
        int afterSize = Unsafe.SizeOf<NoAtlasGumpCommand>();
        int payloadSize = Unsafe.SizeOf<GumpTextureCommand>();
        GD.Print($"[batcher-probe] UI command managed bytes: old={beforeSize} mixed={afterSize} payload={payloadSize}");
        CheckThat("UI mixed command footprint unchanged", beforeSize == afterSize, "Unsafe.SizeOf on running target");

        var lists = new RenderLists();
        var owner = new UiTextureOwner(3, 4);
        var emptyText = new RenderedText();
        lists.AddGumpNoAtlas(static _ => false);
        lists.AddGumpNoAtlas(emptyText, 0, 0, 0, 1, ushort.MaxValue);
        CheckThat("fallback/text-only lists allocate no texture storage", !lists.HasGumpTextureStorage,
            "including text hue equal to texture tag");
        CheckThat("default and old commands cannot alias texture tags", !default(NoAtlasGumpCommand).IsTexture
            && !lists.PeekGumpText(0).IsTexture && !lists.PeekGumpText(1).IsTexture, "both references and tag discriminate");

        Rectangle source = new(1, 2, 3, 4);
        Vector3 hue = new(5, 6, 0.75f);
        lists.AddGumpWithAtlas(owner, texture, 7, 8, source, hue, -4);
        lists.AddGumpWithAtlas(owner, texture, 9, 10, source, hue, 99, tiled: true);
        NoAtlasGumpCommand rect = lists.PeekGumpText(2), tiled = lists.PeekGumpText(3);
        CheckThat("mixed texture indices preserve insertion positions", rect.IsTexture && rect.TextureIndex == 0
            && !rect.TextureTiled && tiled.IsTexture && tiled.TextureIndex == 1 && tiled.TextureTiled,
            "one mixed entry per payload, no extra pass");
        GumpTextureCommand payload = lists.PeekGumpTexture(rect.TextureIndex);
        source.X = 50; hue.X = 80; owner.X = 100; owner.Y = 200;
        owner.Width = 11; owner.Height = 12; owner.Dispose();
        CheckThat("typed UI keeps snapshots and live disposed-owner size", ReferenceEquals(payload.SizeOwner, owner)
            && ReferenceEquals(payload.Texture, texture) && payload.Source == new Rectangle(1, 2, 3, 4)
            && payload.HueVector == new Vector3(5, 6, 0.75f) && rect.X == 7 && rect.LayerDepth == -4
            && payload.Destination(rect.X) == new Rectangle(7, 8, 11, 12),
            "XY/texture/source/hue/depth snapshots; only Width/Height live; no disposal gate");
        int capacity = lists.GumpTextureCapacity;
        lists.Clear();
        CheckThat("UI payload Clear keeps capacity and drops logical entries", lists.GumpTextsCount == 0
            && lists.GumpTextureCount == 0 && lists.GumpTextureCapacity == capacity, "both lists clear together");
        lists.AddGumpWithAtlas(owner, texture, 0, 0, new Rectangle(0, 0, 1, 1), Vector3.Zero, 0);
        CheckThat("UI payload indices restart after Clear", lists.PeekGumpText(0).TextureIndex == 0, "no stale side index");
        lists.Clear();

        WeakReference lifetime = QueueUiTextureLifetime(lists, texture);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        CheckThat("queued UI command retains dimension owner", lifetime.IsAlive, "strong reference until Clear");
        lists.Clear();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        CheckThat("UI payload Clear releases dimension owner", !lifetime.IsAlive, "List<T>.Clear clears struct references");

        // Warm both arrays to the full measured size. No callbacks, formatting,
        // assertions or shader/resource lookup in the allocation window.
        for (int pass = 0; pass < 10; pass++)
        {
            lists.Clear();
            for (int i = 0; i < 32; i++)
                lists.AddGumpWithAtlas(owner, texture, i, i, new Rectangle(0, 0, 1, 1), Vector3.Zero, i, (i & 1) != 0);
        }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 1000; pass++)
        {
            lists.Clear();
            for (int i = 0; i < 32; i++)
                lists.AddGumpWithAtlas(owner, texture, i, i, new Rectangle(0, 0, 1, 1), Vector3.Zero, i, (i & 1) != 0);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        CheckThat("warmed typed UI enqueue allocates nothing", allocated == 0, $"{allocated} bytes / 32000 commands");
        lists.Clear();

        // Fresh child lists cannot amortize side-array growth. Report, do not
        // assume savings at every population or hide regressions in an average.
        foreach (int count in new[] { 0, 1, 2, 3, 8, 20, 32 })
        {
            long legacy = MeasureFreshUiTextureLists(false, count, owner, texture);
            long typed = MeasureFreshUiTextureLists(true, count, owner, texture);
            GD.Print($"[batcher-probe] fresh UI lists count={count} legacy={legacy} typed={typed} bytes/100 lists");
        }
    }

    private static long MeasureFreshUiTextureLists(bool typed, int count, UiControl owner, Texture2D texture)
    {
        // Warm the called paths before measurement, including legacy delegate JIT.
        var warm = new RenderLists();
        QueueUiTexture(typed, warm, owner, texture, 0, 0, new Rectangle(0, 0, 1, 1), Vector3.Zero, 0);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 100; pass++)
        {
            var child = new RenderLists();
            for (int i = 0; i < count; i++)
                QueueUiTexture(typed, child, owner, texture, i, i, new Rectangle(0, 0, 1, 1), Vector3.Zero, i);
            GC.KeepAlive(child);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    // Parent registration: await VerifyUiTextureCommands(batcher, viewport).
    private async Task VerifyUiTextureCommands(UltimaBatcher2D batcher, SubViewport viewport)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Blue);
        image.SetPixel(1, 1, Colors.Red); image.SetPixel(2, 1, Colors.Green);
        image.SetPixel(1, 2, Colors.White); image.SetPixel(2, 2, Colors.Yellow);
        using var texture = ImageTexture.CreateFromImage(image);
        VerifyUiTextureStorage(texture);
        bool oldRetain = UltimaBatcher2D.RetainCanvas, oldBatched = UltimaBatcher2D.BatchedWorld;
        var reference = new List<byte[]>();
        var returns = new List<int>();
        try
        {
            UltimaBatcher2D.RetainCanvas = false;
            UltimaBatcher2D.BatchedWorld = false;
            foreach (bool typed in new[] { false, true })
            {
                var lists = new RenderLists();
                for (int step = 0; step < 10; step++)
                {
                    lists.Clear();
                    var owner = new UiTextureOwner(5, 3);
                    Rectangle source = new(1, 1, 2, 2);
                    Vector3 hue = ShaderHueTranslator.GetHueVector(step == 9 ? 2 : 0, step == 9, step == 9 ? 0.5f : 1, true);
                    int witness = 0;
                    var sentinel = new InvalidOperationException("UI callback fixture");
                    if (step == 6)
                        lists.AddGumpNoAtlas(b => { b.PushUiTransform(new Transform2D(0, new Vector2(1, 1))); b.ClipBegin(0, 0, 5, 5); return true; });
                    lists.AddGumpNoAtlas(_ => { witness = witness * 10 + 1; return false; });
                    if (step == 1)
                        lists.AddGumpNoAtlas(_ => { owner.Width = 2; owner.Height = 6; owner.X = 70; owner.Y = 80; return false; });
                    if (step == 3) lists.AddGumpNoAtlas(_ => { owner.Dispose(); return false; });
                    if (step is 4 or 5) owner.Width = step == 4 ? 0 : -1;
                    QueueUiTexture(typed, lists, owner, texture, 0, 0, source, hue, 100);
                    lists.AddGumpNoAtlas(new RenderedText(), 0, 0, 0, 1, ushort.MaxValue);
                    lists.AddGumpNoAtlas(_ => { witness = witness * 10 + 2; return true; });
                    if (step == 2) lists.AddGumpNoAtlas(_ => { owner.Width = 3; owner.Height = 5; return false; });
                    if (step == 8) lists.AddGumpNoAtlas(_ => throw sentinel);
                    QueueUiTexture(typed, lists, owner, texture, 2, 2, source, hue, -100, tiled: true);
                    if (step == 6) lists.AddGumpNoAtlas(b => { b.ClipEnd(); b.ClipEnd(); return true; });
                    if (step == 7)
                        lists.AddGumpNoAtlas(b =>
                        {
                            var child = new RenderLists();
                            bool clipped = b.ClipBegin(0, 0, 4, 4);
                            if (clipped)
                            {
                                QueueUiTexture(typed, child, owner, texture, 1, 1, source, hue, 0, tiled: true);
                                child.DrawRenderLists(b, sbyte.MaxValue);
                                b.ClipEnd();
                            }
                            return true;
                        });
                    lists.AddGumpWithAtlas(b =>
                    {
                        witness = witness * 10 + 3;
                        b.Draw(texture, new Rectangle(6, 6, 1, 1), source, hue, 0);
                        return true;
                    });
                    batcher.BeginFrame(); batcher.Begin();
                    int done = -1;
                    Exception observed = null;
                    try { done = lists.DrawRenderLists(batcher, sbyte.MaxValue); }
                    catch (Exception ex) { observed = ex; }
                    finally { batcher.End(); }
                    CheckThat($"UI fallback effects and exception order {typed}/{step}",
                        witness == (step == 8 ? 12 : 123)
                        && (step == 8 ? ReferenceEquals(observed, sentinel) : observed == null),
                        "false/true callbacks keep effects, throwing callback stops later texture/callback");
                    if (step is 4 or 5)
                        CheckThat("UI no-geometry texture callbacks still count true", done == 4,
                            "two texture commands plus two true fallbacks, empty text skipped");
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    using Image pixels = viewport.GetTexture().GetImage();
                    byte[] actual = pixels.GetData();
                    if (!typed) { reference.Add(actual); returns.Add(done); }
                    else
                    {
                        CheckThat($"typed UI exact pixels {step}", reference[step].AsSpan().SequenceEqual(actual),
                            "mixed order/depth, live size, disposal, partial tiles, atlas UV, clip/transform, child list, hue/alpha");
                        CheckThat($"typed UI return parity {step}", returns[step] == done, "same callback/text return semantics");
                    }
                    if (step == 0)
                    {
                        witness = 0;
                        batcher.BeginFrame(); batcher.Begin();
                        int repeated = lists.DrawRenderLists(batcher, sbyte.MaxValue);
                        batcher.End();
                        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                        using Image again = viewport.GetTexture().GetImage();
                        CheckThat($"queued UI list can flush again {typed}", repeated == done && witness == 123
                            && actual.AsSpan().SequenceEqual(again.GetData()), "payload lifetime lasts until Clear, not first flush");
                    }
                }
                lists.Clear();
            }
        }
        finally
        {
            UltimaBatcher2D.RetainCanvas = false;
            batcher.BeginFrame(); // clear native test commands before texture disposal
            UltimaBatcher2D.RetainCanvas = oldRetain;
            UltimaBatcher2D.BatchedWorld = oldBatched;
        }
    }
}
