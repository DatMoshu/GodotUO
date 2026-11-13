// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;

namespace GUO.Renderer
{
    /// <summary>
    /// Relative references beneath the explicitly selected layer root. The root
    /// and its ancestors are trusted (and may redirect); descendants may not.
    /// Checks precede opens, but cannot prevent concurrent replacement or detect
    /// hard-link aliases. This is not a hostile-local-process sandbox.
    /// </summary>
    internal static class TerrainLayerPaths
    {
        public static bool TryNormalize(string name, out string relative, out string reason)
        {
            relative = null;
            reason = "Unsafe layer-relative path.";
            // Do this before GetFullPath or any candidate filesystem inspection.
            if (string.IsNullOrEmpty(name) || name[0] == '/' || name[0] == '\\')
                return false;
            foreach (char c in name)
            {
                if (char.IsControl(c) || "<>:\"|?*".IndexOf(c) >= 0)
                    return false;
            }

            string normalized = name.Replace('\\', '/');
            foreach (string part in normalized.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".."
                    || part.EndsWith('.') || part.EndsWith(' '))
                    return false;
                string stem = part.Split('.')[0].ToUpperInvariant();
                // Win32 also recognizes spaces before an extension and the
                // superscript digits in COM/LPT device aliases.
                stem = stem.TrimEnd(' ');
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                    || stem == "CONIN$" || stem == "CONOUT$" || stem == "CLOCK$"
                    || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                        && "123456789\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0))
                    return false;
            }

            relative = normalized;
            reason = "";
            return true;
        }

        private static StringComparison Comparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static string CanonicalRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        private static string Boundary(string root) => Path.EndsInDirectorySeparator(root)
            ? root : root + Path.DirectorySeparatorChar;

        /// <summary>Path algebra only; explicit outside picker sources are allowed.</summary>
        public static bool IsInside(string root, string full, out string relative)
        {
            string boundary = Boundary(CanonicalRoot(root));
            full = Path.GetFullPath(full);
            bool inside = full.StartsWith(boundary, Comparison);
            relative = inside ? full.Substring(boundary.Length).Replace(Path.DirectorySeparatorChar, '/') : null;
            return inside;
        }

        public static bool TryResolve(string root, string name, out string path, out string relative, out string reason)
        {
            path = null;
            if (!TryNormalize(name, out relative, out reason))
                return false;
            try
            {
                string canonical = CanonicalRoot(root);
                string candidate = Path.GetFullPath(Path.Combine(canonical, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(Boundary(canonical), Comparison))
                {
                    reason = "Layer path leaves the selected root.";
                    return false;
                }

                string current = canonical;
                string[] parts = relative.Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    current = Path.Combine(current, parts[i]);
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(current);
                    }
                    catch (FileNotFoundException) { break; }
                    catch (DirectoryNotFoundException) { break; }
                    // Do not use Exists: it hides access errors and dangling links.
                    // Each parent is checked before inspecting the next child.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reason = "Layer descendants cannot be links or reparse points.";
                        return false;
                    }
                    if (i < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                    {
                        reason = "A layer parent is not a directory.";
                        return false;
                    }
                }

                path = candidate;
                reason = "";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Could not inspect layer path: {ex.Message}";
                return false;
            }
        }

        public static string Resolve(string root, string name, out string relative)
        {
            if (!TryResolve(root, name, out string path, out relative, out string reason))
                throw new IOException(reason);
            return path;
        }

        public static string CreateDirectory(string root, string name)
        {
            string path = Resolve(root, name, out _);
            Directory.CreateDirectory(path);
            return Resolve(root, name, out _);
        }
    }
}
