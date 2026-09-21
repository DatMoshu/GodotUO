using System;
using System.IO;
using System.Reflection;

namespace GUO.Resources
{
    /// <summary>
    /// The two images the client compiles into itself: the about-box logo and
    /// the login screen background.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream declares these as partial methods filled
    /// in by the <c>FileEmbed</c> source generator. GUO does not take that
    /// dependency -- a generator is a build-time package to keep in step for
    /// two files -- and reads the same bytes out of the assembly manifest
    /// instead. Both method names and both signatures are unchanged, so the
    /// two call sites port without edits.
    ///
    /// The images are embedded rather than shipped as Godot resources on
    /// purpose: they are needed before a scene tree exists, and being in the
    /// assembly means they cannot go missing from an export preset.
    /// </remarks>
    public partial class Loader
    {
        private static byte[] _cuoLogo;
        private static byte[] _backgroundImage;

        public static ReadOnlySpan<byte> GetCuoLogo() =>
            _cuoLogo ??= Read("cuologo.png");

        public static ReadOnlySpan<byte> GetBackgroundImage() =>
            _backgroundImage ??= Read("game-background.png");

        /// <summary>
        /// Reads one embedded image. Returns an empty array rather than
        /// throwing if it is absent: a missing decoration should not stop the
        /// client from starting, and both callers already handle empty bytes
        /// by drawing nothing.
        /// </summary>
        private static byte[] Read(string name)
        {
            Assembly asm = typeof(Loader).Assembly;

            // The logical name is set in GUO.csproj. Look it up rather than
            // assuming a root-namespace prefix, which differs between SDKs.
            using Stream stream = asm.GetManifestResourceStream(name);
            if (stream == null)
            {
                return Array.Empty<byte>();
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
