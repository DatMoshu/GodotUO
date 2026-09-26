// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Network.Socket;
using GUO.Renderer;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;

namespace GUO.Host;

/// <summary>Regression checks without a UO installation or shard. See dev/README.md.</summary>
public partial class RegressionProbe : Node
{
    public override async void _Ready()
    {
        int failures = 0;
        string selected = OS.GetCmdlineUserArgs().FirstOrDefault() ?? "all";
        if (!new[] { "all", "atlas", "json", "tcp", "websocket", "video", "rendering" }.Contains(selected))
        {
            GD.PrintErr($"Unknown regression group: {selected}");
            GetTree().Quit(1);
            return;
        }
        foreach (var test in new (string Name, Func<Task> Run)[]
        {
            ("atlas", () => { TestAtlas(); return Task.CompletedTask; }),
            ("json", () => { TestJson(); return Task.CompletedTask; }),
            ("tcp", () => Task.Run(TestTcp)),
            ("websocket", () => Task.Run(TestWebSocket)),
            ("video", () => { TestVideoDefaults(); return Task.CompletedTask; }),
            ("rendering", TestRendering),
        })
        {
            if (selected != "all" && selected != test.Name)
                continue;
            if (selected == "all" && test.Name == "rendering" && DisplayServer.GetName() == "headless")
            {
                GD.Print("[regression] SKIP rendering (requires a real renderer)");
                continue;
            }
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(15));
                GD.Print($"[regression] PASS {test.Name}");
            }
            catch (Exception e)
            {
                failures++;
                GD.PrintErr($"[regression] FAIL {test.Name}: {e}");
            }
        }
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void TestVideoDefaults()
    {
        var profile = new Profile();
        var camera = new Camera(0.5f, 2.5f, 0.1f);
        Check(profile.GameWindowFullSize && profile.EnableMousewheelScaleZoom, "Full-size/scroll zoom defaults are off");
        Check((int)Math.Round((profile.DefaultScale - camera.ZoomMin) / camera.ZoomStep) == 2, "Default zoom is not slider step 2");
        Check(profile.GameWindowPosition == GUO.Compat.Point.Zero, "Viewport origin is not zero");
        Check(profile.ReduceFPSWhenInactive && !profile.WindowBorderless && !profile.GameWindowLock && !profile.RestoreScaleAfterUnpressCtrl, "Video checkbox defaults differ");
        Check(new Settings().FPS == 60 && new Settings().ScreenScale == 1f, "FPS/screen scale defaults differ");
    }

    // A synthetic ordinary sprite uses the same RenderLists path as excluded
    // tree art, without requiring an installed UO client's art and tiledata.
    private sealed class ProbeSprite(Texture2D texture) : GameEffect(null, null, 0, 0, 0, 0)
    {
        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            batcher.Draw(texture, new GUO.Compat.Rectangle(0, 0, 16, 16), new Vector3(0, 0, 1), depth);
            return true;
        }
    }

    private async Task TestRendering()
    {
        Check(DisplayServer.GetName() != "headless", "Run rendering without --headless");
        using var roofImage = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        using var treeImage = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        roofImage.Fill(Colors.Red);
        treeImage.Fill(Colors.Green);
        // A transparent corner proves we retain cutouts, not just draw order.
        roofImage.SetPixel(2, 2, Colors.Transparent);
        using var roofTexture = ImageTexture.CreateFromImage(roofImage);
        using var treeTexture = ImageTexture.CreateFromImage(treeImage);
        var viewport = new SubViewport { Size = new Vector2I(16, 16), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
        var host = new Node2D();
        viewport.AddChild(host);
        AddChild(viewport);
        using var batcher = new UltimaBatcher2D(host.GetCanvasItem());
        var layer = new MeshLayer { Count = 1 };
        layer.WriteQuadAt(0, roofTexture, new GUO.Compat.Rectangle(0, 0, 16, 16), 100, 80, new Vector3(0, 0, 1), 0);
        layer.SetVisible(0, 255);
        var roof = new Static(null) { X = 10, Y = 10, MeshSpriteIndex = 0, InChunkMesh = true };
        var tree = new ProbeSprite(treeTexture) { X = 9, Y = 10 };
        var lists = new RenderLists();
        try
        {
            async Task<Godot.Color[]> Draw(bool legacy)
            {
                batcher.BeginFrame();
                batcher.Begin();
                lists.Clear();
                lists.Add(tree);
                if (legacy)
                {
                    // Reproduce the original separate mesh-then-sprite passes.
                    layer.BuildVisibleIndices();
                    batcher.SetWorldOffset(100, 80);
                    batcher.DrawMeshLayer(layer);
                    batcher.ResetWorldOffset();
                }
                else
                    lists.AddMeshStatic(roof, layer);
                lists.DrawRenderLists(batcher, 127, new System.Collections.Generic.List<GUO.Game.Map.Chunk>(), 100, 80);
                batcher.End();
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using var image = viewport.GetTexture().GetImage();
                return new[] { image.GetPixel(8, 8), image.GetPixel(2, 2) };
            }

            var old = await Draw(true);
            Check(old[0].G > 0.9f && old[0].R < 0.1f, "Legacy passes did not reproduce tree over roof");
            var behind = await Draw(false);
            Check(behind[0].R > 0.9f && behind[0].G < 0.1f, "Tree behind roof painted over it");
            Check(behind[1].G > 0.9f, "Roof cutout did not reveal the tree");
            tree.X = 11;
            var front = await Draw(false);
            Check(front[0].G > 0.9f && front[0].R < 0.1f, "Tree in front of roof was hidden");
        }
        finally
        {
            layer.Dispose();
            viewport.QueueFree();
        }
    }

    private static void TestAtlas()
    {
        using var atlas = new TextureAtlas(4096, 4096);
        void Sprite(int width, int height, uint color)
        {
            var pixels = Enumerable.Repeat(color, width * height).ToArray();
            var texture = atlas.AddSprite(pixels, width, height, out var region);
            Check(region.Width == width && region.Height == height, "Sprite dimensions changed");
            var actual = new uint[pixels.Length];
            Check(TextureAtlas.TryReadRegion(texture, region, actual), "Texture is not tracked");
            Check(actual.SequenceEqual(pixels), "Sprite pixels changed");
            pixels[0] ^= 0xFF;
            Check(TextureAtlas.TryWriteRegion(texture, region, pixels), "Texture cannot be updated");
            TextureAtlas.TryReadRegion(texture, region, actual);
            Check(actual.SequenceEqual(pixels), "Texture update changed pixels");
        }
        Sprite(2047, 1, 0xFF0000FF); // Dedicated page before a packed page exists.
        Sprite(2046, 1, 0xFF00FF00); // Largest width that fits with padding.
        Sprite(1, 2047, 0xFFFF0000); // Dedicated page between packed sprites.
        Sprite(16, 16, 0xFFFFFFFF);
        Check(atlas.TexturesCount == 3, "Dedicated textures displaced the current packing page");
        atlas.Flush();
        using var small = new TextureAtlas(8, 8);
        small.AddSprite(new uint[36], 6, 6, out _);
        small.AddSprite(new uint[36], 6, 6, out _);
        Check(small.TexturesCount == 2, "Normal page rollover failed");
        try
        {
            small.AddSprite(new uint[1], 2047, 2, out _);
            throw new InvalidOperationException("Invalid pixel data accepted");
        }
        catch (ArgumentException) { }
        Check(small.TexturesCount == 2, "Invalid data allocated a texture");
    }

    private static void TestJson()
    {
        string path = Path.GetTempFileName();
        try
        {
            var expected = new Profile
            {
                SpellDisplayFormat = "José 日本語 \"spell\"\n\t{power}",
                WorldMapHiddenMarkerFiles = @"C:\maps\日本\markers.csv",
                WorldMapHiddenZoneFiles = @"\\server\zones\map.csv",
            };
            ConfigurationResolver.Save(expected, path, ProfileJsonContext.DefaultToUse.Profile);
            var actual = ConfigurationResolver.Load(path, ProfileJsonContext.DefaultToUse.Profile);
            Check(actual.SpellDisplayFormat == expected.SpellDisplayFormat, "Unicode/quotes/control characters did not round-trip");
            Check(actual.WorldMapHiddenMarkerFiles == expected.WorldMapHiddenMarkerFiles, "Windows path did not round-trip");
            Check(actual.WorldMapHiddenZoneFiles == expected.WorldMapHiddenZoneFiles, "UNC path did not round-trip");
        }
        finally { File.Delete(path); }
    }

    private static async Task TestTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpSocketWrapper();
        int closed = 0;
        client.OnDisconnected += (_, _) =>
        {
            Check(!client.IsConnected, "Disconnect callback saw a connected socket");
            closed++;
        };
        client.Connect(new Uri($"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"));
        using var peer = await listener.AcceptTcpClientAsync();
        var received = new byte[64];
        Check(client.Read(received) == 0 && client.IsConnected && closed == 0, "Idle socket was disconnected");
        byte[] expected = { 10, 20, 30, 40 };
        await peer.GetStream().WriteAsync(expected);
        peer.Client.Shutdown(SocketShutdown.Send);
        using var all = new MemoryStream();
        for (int attempt = 0; attempt < 200 && closed == 0; attempt++)
        {
            int count = client.Read(received);
            all.Write(received, 0, count);
            await Task.Delay(5);
        }
        Check(all.ToArray().SequenceEqual(expected), "Data preceding FIN was lost");
        Check(closed == 1 && !client.IsConnected, "FIN did not disconnect exactly once");
        Check(client.Read(received) == 0 && closed == 1, "Read after disconnect repeated callback");
    }

    private static async Task TestWebSocket()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new WebSocketWrapper();
        int errors = 0;
        client.OnError += (_, _) => Interlocked.Increment(ref errors);
        Task connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), timeout);
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        // Minimal loopback HTTP upgrade, then .NET implements the WebSocket framing.
        string header = "";
        var one = new byte[1];
        while (!header.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Check(await stream.ReadAsync(one, token) == 1, "Incomplete handshake");
            header += (char)one[0];
            Check(header.Length < 16384, "Handshake too large");
        }
        string key = header.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), token);
        using var server = WebSocket.CreateFromStream(stream, true, null, System.Threading.Timeout.InfiniteTimeSpan);
        await connect.WaitAsync(token);
        Check(client.IsConnected, "WebSocket connection failed");

        async Task Message(int size, bool emptyFinalFragment)
        {
            var expected = new byte[size];
            new Random(size).NextBytes(expected);
            // Let the first short fragment enter the receiver before a large tail.
            int first = Math.Min(2000, size);
            await server.SendAsync(expected.AsMemory(0, first), WebSocketMessageType.Binary, false, token);
            await Task.Delay(20, token);
            await server.SendAsync(expected.AsMemory(first), WebSocketMessageType.Binary, !emptyFinalFragment, token);
            if (emptyFinalFragment)
                await server.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, token);
            using var actual = new MemoryStream();
            var chunk = new byte[777];
            while (actual.Length < size)
            {
                token.ThrowIfCancellationRequested();
                Check(Volatile.Read(ref errors) == 0, "Receiver rejected a valid message");
                int count = client.Read(chunk);
                actual.Write(chunk, 0, count);
                if (count == 0) await Task.Delay(2, token);
            }
            Check(actual.ToArray().SequenceEqual(expected), $"Fragmented {size}-byte message was corrupted");
        }

        await Message(80_000, false);
        await Message(1024 * 1024, true); // Exact cap, including an empty final frame.
        await Message(17, false); // Accumulator was reset between messages.
        await server.SendAsync(new byte[1024 * 1024 + 1].AsMemory(), WebSocketMessageType.Binary, true, token);
        while (Volatile.Read(ref errors) == 0) await Task.Delay(2, token);
        Check(client.Read(new byte[32]) == 0, "Oversized message leaked partial data");
        timeout.Cancel();
        server.Abort();
    }
}
