// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Buffers.Binary;
using Godot;

namespace GUO.Store;

/// <summary>Verified pack-owned HD pixels, uploaded once on the scene thread.</summary>
internal sealed class StoreHdAsset : IDisposable
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int NativeWidth { get; private init; }
    public int NativeHeight { get; private init; }
    public double Scale { get; private init; }
    private byte[] _png;
    private Texture2D _texture;

    public static StoreHdAsset Decode(StoreComponent component, byte[] png, ref long pixels)
    {
        StorePack.Require(png.Length >= 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "HD art requires PNG");
        long width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        double expectedW = component.NativeWidth.Value * component.Scale.Value, expectedH = component.NativeHeight.Value * component.Scale.Value;
        StorePack.Require(expectedW == Math.Truncate(expectedW) && expectedH == Math.Truncate(expectedH)
            && width == expectedW && height == expectedH && width is > 0 and <= 8192 && height is > 0 and <= 8192,
            "HD dimensions must match native canvas times scale (maximum 8192)");
        pixels += width * height;
        StorePack.Require(pixels <= 64 * 1024 * 1024, "Content image memory budget exceeded");
        using var image = new Image();
        StorePack.Require(image.LoadPngFromBuffer(png) == Error.Ok && image.GetWidth() == width && image.GetHeight() == height, "Invalid HD PNG");
        return new StoreHdAsset { Width = (int)width, Height = (int)height, NativeWidth = component.NativeWidth.Value,
            NativeHeight = component.NativeHeight.Value, Scale = component.Scale.Value, _png = png };
    }

    public Texture2D Texture()
    {
        if (_texture != null) return _texture;
        using var image = new Image();
        StorePack.Require(_png != null && image.LoadPngFromBuffer(_png) == Error.Ok, "HD content was disposed");
        image.Convert(Image.Format.Rgba8);
        image.GenerateMipmaps();
        return _texture = ImageTexture.CreateFromImage(image);
    }

    public void Dispose() { if (_texture != null) { Renderer.Arts.Art.ReleaseHd(_texture); _texture.Dispose(); } _texture = null; _png = null; }
}
