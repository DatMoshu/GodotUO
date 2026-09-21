// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Reflection;

namespace GUO.Renderer
{
    /// <summary>
    ///     The eight UI fonts the client compiles into itself.
    /// </summary>
    /// <remarks>
    ///     PORT DEVIATION (GUO): upstream declares these as partial methods filled
    ///     in by the <c>FileEmbed</c> source generator, the same arrangement
    ///     <see cref="GUO.Resources.Loader" /> replaces for the two embedded PNGs.
    ///     GUO does not take that dependency and reads the bytes out of the
    ///     assembly manifest instead. The method names and signatures are
    ///     unchanged, so <see cref="Fonts" /> ports without edits.
    /// </remarks>
    public partial class FontResources
    {
        private static readonly byte[][] _cache = new byte[8][];

        public static ReadOnlySpan<byte> GetRegularFont() => Read(0, "regular_font.xnb");

        public static ReadOnlySpan<byte> GetBoldFont() => Read(1, "bold_font.xnb");

        public static ReadOnlySpan<byte> GetMap1Font() => Read(2, "map1_font.xnb");

        public static ReadOnlySpan<byte> GetMap2Font() => Read(3, "map2_font.xnb");

        public static ReadOnlySpan<byte> GetMap3Font() => Read(4, "map3_font.xnb");

        public static ReadOnlySpan<byte> GetMap4Font() => Read(5, "map4_font.xnb");

        public static ReadOnlySpan<byte> GetMap5Font() => Read(6, "map5_font.xnb");

        public static ReadOnlySpan<byte> GetMap6Font() => Read(7, "map6_font.xnb");

        /// <summary>
        ///     Reads one embedded font. Unlike the decorative PNGs, a missing font
        ///     is not survivable -- every piece of UI text goes through one of
        ///     these -- so this throws rather than returning nothing and letting
        ///     the client come up silently mute.
        /// </summary>
        private static byte[] Read(int slot, string name)
        {
            if (_cache[slot] != null)
            {
                return _cache[slot];
            }

            Assembly asm = typeof(FontResources).Assembly;

            // The logical name is set in GUO.csproj. Look it up rather than
            // assuming a root-namespace prefix, which differs between SDKs.
            using Stream stream = asm.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException($"embedded font missing: {name}");

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            return _cache[slot] = buffer.ToArray();
        }
    }

    public static class Fonts
    {
        /// <remarks>
        ///     PORT DEVIATION (GUO): upstream takes a <c>GraphicsDevice</c> and
        ///     passes it to every <c>SpriteFont.Create</c>. Godot has no device.
        /// </remarks>
        public static void Initialize()
        {
            Regular = SpriteFont.Create(FontResources.GetRegularFont());
            Bold = SpriteFont.Create(FontResources.GetBoldFont());
            Map1 = SpriteFont.Create(FontResources.GetMap1Font());
            Map2 = SpriteFont.Create(FontResources.GetMap2Font());
            Map3 = SpriteFont.Create(FontResources.GetMap3Font());
            Map4 = SpriteFont.Create(FontResources.GetMap4Font());
            Map5 = SpriteFont.Create(FontResources.GetMap5Font());
            Map6 = SpriteFont.Create(FontResources.GetMap6Font());
        }

        public static SpriteFont Regular { get; private set; }
        public static SpriteFont Bold { get; private set; }
        public static SpriteFont Map1 { get; private set; }
        public static SpriteFont Map2 { get; private set; }
        public static SpriteFont Map3 { get; private set; }
        public static SpriteFont Map4 { get; private set; }
        public static SpriteFont Map5 { get; private set; }
        public static SpriteFont Map6 { get; private set; }
    }
}
