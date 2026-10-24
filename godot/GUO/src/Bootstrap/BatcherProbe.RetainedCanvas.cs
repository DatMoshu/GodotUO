// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;
using Command = GUO.Renderer.RetainedCanvasCommands.Command;
using Kind = GUO.Renderer.RetainedCanvasCommands.Kind;

public partial class BatcherProbe
{
    private sealed class CanvasOracle : RetainedCanvasCommands.ISink
    {
        internal readonly List<Command> Stream = new();
        internal int Clears, Emits;
        public void Clear(Rid item) { Stream.Clear(); Clears++; }
        public void Emit(Rid item, in Command command) { Stream.Add(command); Emits++; }
        internal bool Matches(Command[] expected)
        {
            if (Stream.Count != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (!Stream[i].Same(expected[i])) return false;
            return true;
        }
    }

    private async Task VerifyRetainedCanvas(UltimaBatcher2D batcher, SubViewport viewport)
    {
        VerifyRetainedStreams();
        bool previous = UltimaBatcher2D.RetainCanvas;
        bool oldBatched = UltimaBatcher2D.BatchedWorld;
        using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        using var texture = ImageTexture.CreateFromImage(image);
        var frames = new List<byte[]>();
        try
        {
            // Repeat changes on the SAME batcher and SAME texture RID. This
            // exercises pool reuse, shorter streams, empty frames and mode changes.
            foreach (bool retained in new[] { false, true, false, true })
            {
                UltimaBatcher2D.RetainCanvas = retained;
                for (int step = 0; step < 12; step++)
                {
                    image.Fill(step < 5 ? Colors.Red : Colors.Green);
                    texture.Update(image);
                    batcher.BeginFrame();
                    if (step != 8)
                    {
                        batcher.Begin();
                        var hue = ShaderHueTranslator.GetHueVector(step == 3 ? 2 : 0);
                        if (step != 7)
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 8, 8), null, hue, 0);
                        if (step == 1 || step == 2)
                        {
                            // An unchanged rectangle prefix, an actual raw
                            // triangle run, then another ordinary rectangle.
                            UltimaBatcher2D.BatchedWorld = true;
                            batcher.Draw(texture, new Compat.Rectangle(2, 2, 4, 4), null, new Vector3(0, 0, 0.5f), 0);
                            UltimaBatcher2D.BatchedWorld = false;
                            batcher.Draw(texture, new Compat.Rectangle(3, 3, 2, 2), null, hue, 0);
                        }
                        if (step == 10)
                        {
                            batcher.SetBlendState(new BlendState { ColorSourceBlend = Blend.DestinationColor,
                                ColorDestinationBlend = Blend.InverseSourceAlpha });
                            batcher.Draw(texture, new Compat.Rectangle(1, 1, 5, 5), null, hue, 0);
                            batcher.SetBlendState(null);
                        }
                        if (step < 5 || step > 8)
                        {
                            batcher.PushUiTransform(new Transform2D(0f, new Vector2(step == 4 ? 3 : 1, 1)));
                            batcher.ClipBegin(0, 0, 4, 4);
                            hue.Z = 0.5f;
                            batcher.Draw(texture, new Vector2(1, 1), null, hue, 0.2f,
                                Vector2.Zero, 2, SpriteEffects.FlipHorizontally, 0);
                            batcher.ClipEnd();
                            batcher.ClipEnd();
                            batcher.Draw(texture, new Compat.Rectangle(6, 6, 1, 1), null, hue, 0);
                        }
                        batcher.End();
                    }
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    using Image pixels = viewport.GetTexture().GetImage();
                    byte[] actual = pixels.GetData();
                    if (frames.Count < 12) frames.Add(actual);
                    else CheckThat($"retained canvas mode {retained} transition {step}",
                        frames[step].AsSpan().SequenceEqual(actual), "exact pixels, including empty/shorter pools and texture updates");
                }
            }
            await VerifyRetainedMeshes(batcher, viewport, texture);
        }
        finally
        {
            UltimaBatcher2D.RetainCanvas = previous;
            UltimaBatcher2D.BatchedWorld = oldBatched;
            batcher.BeginFrame();
        }
    }

    private async Task VerifyRetainedMeshes(UltimaBatcher2D batcher, SubViewport viewport, Texture2D texture)
    {
        var reference = new List<byte[]>();
        foreach (bool retained in new[] { false, true })
        {
            UltimaBatcher2D.RetainCanvas = retained;
            var mesh = new ArrayMesh();
            using Image source = texture.GetImage();
            var activeTexture = ImageTexture.CreateFromImage(source);
            try
            {
                Rid original = mesh.GetRid();
                for (int step = 0; step < 8; step++)
                {
                    if (step == 5)
                    {
                        // Hide commands holding a resource, then release it.
                        batcher.BeginFrame();
                        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                        mesh.Dispose();
                        mesh = new ArrayMesh();
                        activeTexture.Dispose();
                        activeTexture = ImageTexture.CreateFromImage(source);
                    }
                    if (step != 1)
                    {
                        mesh.ClearSurfaces();
                        if (step != 4)
                        {
                            using var arrays = new Godot.Collections.Array();
                            arrays.Resize((int)Mesh.ArrayType.Max);
                            float x = step < 2 ? 0 : 3;
                            arrays[(int)Mesh.ArrayType.Vertex] = new Vector2[] { new(x, 0), new(7, 0), new(x, 7), new(7, 7) };
                            arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[] { Vector2.Zero, Vector2.Right, Vector2.Down, Vector2.One };
                            Color color = new(1, 1, 1, step == 3 ? 0.5f : 1);
                            arrays[(int)Mesh.ArrayType.Color] = new[] { color, color, color, color };
                            arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 1, 3, 2 };
                            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                        }
                    }
                    long submitted = RetainedCanvasCommands.Submitted;
                    batcher.BeginFrame(); batcher.Begin();
                    batcher.DrawLayerMesh(mesh, activeTexture);
                    batcher.End();
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    using Image pixels = viewport.GetTexture().GetImage();
                    byte[] actual = pixels.GetData();
                    if (!retained) reference.Add(actual);
                    else
                    {
                        CheckThat($"retained native mesh mutation {step}", reference[step].AsSpan().SequenceEqual(actual),
                            "same RID surface/bounds/color changes, empty surface and disposed/replaced resource");
                        if (step is 1 or 2 or 3 or 4)
                            CheckThat("retained mesh update needs no command resubmission", mesh.GetRid() == original
                                && RetainedCanvasCommands.Submitted == submitted, "native command references live mesh");
                    }
                }
            }
            finally { batcher.BeginFrame(); mesh.Dispose(); activeTexture.Dispose(); }
        }
        CheckThat("mesh fixture changes visible geometry", !reference[0].AsSpan().SequenceEqual(reference[2]), "bounds update is observable");
        CheckThat("mesh fixture changes visible alpha", !reference[2].AsSpan().SequenceEqual(reference[3]), "color update is observable");
    }

    private void VerifyRetainedStreams()
    {
        VerifyRetainedRecovery();
        var sink = new CanvasOracle();
        var budget = new RetainedCanvasCommands.Budget();
        var cache = new RetainedCanvasCommands(budget, sink, default);
        Command Make(int value, Kind kind = Kind.Rect) => new(kind, default, default,
            new Rect2(value, 2, 3, 4), new Rect2(5, 6, 7, 8), new Color(value / 32f, 0, 0, 1),
            new Transform2D(0f, new Vector2(value, 0)));
        Command a = Make(1), b = Make(2, Kind.Affine), c = Make(3, Kind.Mesh), d = Make(4);
        void Frame(params Command[] commands)
        {
            cache.Begin();
            foreach (Command command in commands) cache.Add(command);
            cache.End();
            CheckThat("retained native stream matches requested order", sink.Matches(commands), $"{commands.Length} commands");
        }
        Frame(a, b, c);
        int emits = sink.Emits, clears = sink.Clears;
        // No params arrays or assertion strings inside the allocation window.
        for (int i = 0; i < 10; i++) { cache.Begin(); cache.Add(a); cache.Add(b); cache.Add(c); cache.End(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { cache.Begin(); cache.Add(a); cache.Add(b); cache.Add(c); cache.End(); }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        CheckThat("unchanged retained stream makes no native calls", sink.Emits == emits && sink.Clears == clears, "1010 warm frames");
        CheckThat("unchanged retained stream allocates nothing", allocated == 0, $"{allocated} bytes");
        Frame(a, b, d); // late mismatch must replay the old matching prefix
        Frame(a, b, d, c); // append
        Frame(a, b); // remove suffix
        Frame(); // remove all
        Frame(c, b, a); // different command kinds/order
        cache.Begin(); cache.Add(c); cache.Bypass(); sink.Emit(default, d); cache.Add(a); cache.End();
        CheckThat("unsupported command preserves prefix and following order", sink.Matches(new[] { c, d, a }) && !cache.HasCommands, "uncached frame");
        sink.Clear(default); cache.Reset(); Frame(a, c);

        var random = new Random(2317);
        var pool = new[] { a, b, c, d };
        bool exact = true;
        for (int frame = 0; frame < 512; frame++)
        {
            var expected = new Command[random.Next(0, 24)];
            cache.Begin();
            for (int i = 0; i < expected.Length; i++) cache.Add(expected[i] = pool[random.Next(pool.Length)]);
            cache.End();
            exact &= sink.Matches(expected);
        }
        CheckThat("512 changing retained streams preserve painter order", exact, "deterministic independent native-stream oracle");
        var full = new RetainedCanvasCommands.Budget { Capacity = RetainedCanvasCommands.Budget.Limit };
        var boundedSink = new CanvasOracle();
        var bounded = new RetainedCanvasCommands(full, boundedSink, default);
        bounded.Begin(); bounded.Add(a); bounded.Add(b); bounded.End();
        CheckThat("exhausted retained budget keeps every draw", boundedSink.Matches(new[] { a, b }) && !bounded.HasCommands
            && full.Capacity == RetainedCanvasCommands.Budget.Limit, "native fallback");
        boundedSink.Clear(default);
        var large = new RetainedCanvasCommands(new RetainedCanvasCommands.Budget(), boundedSink, default);
        large.Begin();
        for (int i = 0; i < 8194; i++) large.Add(pool[i % 4]);
        large.End();
        CheckThat("per-item retained limit preserves its entire suffix", boundedSink.Stream.Count == 8194
            && boundedSink.Stream[8193].Same(b) && !large.HasCommands, "8192 cached commands then native fallback");

        Command zero = Make(0);
        Command minusZero = new(Kind.Rect, default, default, zero.Destination, zero.Source,
            new Color(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 0, 0, 1), default);
        CheckThat("retained comparison keeps signed-zero bits", !zero.Same(minusZero), "no approximate or hash-only identity");
        Command nan1 = new(Kind.Rect, default, default, zero.Destination, zero.Source,
            new Color(BitConverter.Int32BitsToSingle(0x7fc00001), 0, 0, 1), default);
        Command nan2 = new(Kind.Rect, default, default, zero.Destination, zero.Source,
            new Color(BitConverter.Int32BitsToSingle(0x7fc00002), 0, 0, 1), default);
        CheckThat("retained comparison keeps distinct NaN payloads", nan1.Same(nan1) && !nan1.Same(nan2), "bitwise float identity");
    }
}
