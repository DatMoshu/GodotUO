// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace GUO.Assets
{
    /// <summary>
    /// One 3D gaussian from a PLY splat (TripoSplat/SplatToFile3D layout):
    /// centre, DC colour, opacity logit, log-scale and rotation quaternion.
    /// Plain floats: parsing stays headless-testable, like the .mul readers.
    /// </summary>
    public struct SplatGaussian
    {
        public float X, Y, Z;
        public float Dc0, Dc1, Dc2;
        public float Opacity;
        public float Scale0, Scale1, Scale2;
        public float Rot0, Rot1, Rot2, Rot3;
    }

    /// <summary>A parsed splat level: gaussians plus the lod0 bounds the client culls by.</summary>
    public sealed class SplatSet
    {
        public SplatGaussian[] Gaussians = Array.Empty<SplatGaussian>();
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float OpacityMass;
    }

    /// <summary>
    /// Reads binary_little_endian gaussian PLYs (what ComfyUI's SplatToFile3D and
    /// tools/comfy/splatlod.py write): a single `element vertex` block carrying
    /// x/y/z, f_dc_0..2, opacity, scale_0..2, rot_0..3 and optional f_rest_*.
    /// Extra properties of any scalar width count toward the stride (so a
    /// `property uchar red` no longer shifts every later offset); `list`
    /// properties and unknown scalar types are rejected instead of misread.
    /// Rotations are (w, x, y, z) with rot_0 = w, per the training convention.
    /// </summary>
    public static class SplatPlyParser
    {
        private static readonly string[] Wanted =
        {
            "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
            "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3",
        };

        public static SplatSet Parse(string path) => Parse(File.ReadAllBytes(path), path);

        public static SplatSet Parse(byte[] raw, string name = "")
        {
            int head = FindHeaderEnd(raw);
            string[] lines = System.Text.Encoding.ASCII.GetString(raw, 0, head).Split('\n');
            if (lines.Length < 2 || lines[0].Trim() != "ply" || !lines[1].Contains("binary_little_endian"))
            {
                throw new InvalidDataException($"{name}: only binary_little_endian PLY supported");
            }

            var props = new List<(string Name, int Size)>();
            int count = 0;
            bool inVertex = false;
            foreach (string line in lines)
            {
                string[] p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0)
                {
                    continue;
                }

                if (p[0] == "element")
                {
                    inVertex = p.Length > 2 && p[1] == "vertex";
                    if (inVertex)
                    {
                        count = int.Parse(p[2]);
                    }
                }
                else if (p[0] == "property" && inVertex)
                {
                    if (p.Length == 3 && ScalarWidth(p[1]) is int w)
                    {
                        props.Add((p[2], w));
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"{name}: unsupported vertex property '{line.Trim()}' (scalar uchar/ushort/float/double only)");
                    }
                }
            }

            if (count <= 0 || props.Count == 0)
            {
                throw new InvalidDataException($"{name}: no vertex element found");
            }

            var want = new Dictionary<string, int>();
            foreach (string w in Wanted)
            {
                want[w] = -1;
            }

            var sizes = new Dictionary<string, int>();
            int stride = 0;
            for (int i = 0; i < props.Count; i++)
            {
                if (want.ContainsKey(props[i].Name) && want[props[i].Name] < 0)
                {
                    want[props[i].Name] = stride;
                    sizes[props[i].Name] = props[i].Size;
                }

                stride += props[i].Size;
            }

            foreach (string w in Wanted)
            {
                if (want[w] < 0)
                {
                    throw new InvalidDataException($"{name}: missing required property {w}");
                }

                if (sizes[w] != 4)
                {
                    throw new InvalidDataException($"{name}: required property {w} is not 4 bytes");
                }
            }

            if (raw.Length < head + count * stride)
            {
                throw new InvalidDataException($"{name}: truncated body");
            }

            var set = new SplatSet { Gaussians = new SplatGaussian[count] };
            for (int i = 0; i < count; i++)
            {
                int at = head + i * stride;
                set.Gaussians[i] = new SplatGaussian
                {
                    X = BitConverter.ToSingle(raw, at + want["x"]),
                    Y = BitConverter.ToSingle(raw, at + want["y"]),
                    Z = BitConverter.ToSingle(raw, at + want["z"]),
                    Dc0 = BitConverter.ToSingle(raw, at + want["f_dc_0"]),
                    Dc1 = BitConverter.ToSingle(raw, at + want["f_dc_1"]),
                    Dc2 = BitConverter.ToSingle(raw, at + want["f_dc_2"]),
                    Opacity = BitConverter.ToSingle(raw, at + want["opacity"]),
                    Scale0 = BitConverter.ToSingle(raw, at + want["scale_0"]),
                    Scale1 = BitConverter.ToSingle(raw, at + want["scale_1"]),
                    Scale2 = BitConverter.ToSingle(raw, at + want["scale_2"]),
                    Rot0 = BitConverter.ToSingle(raw, at + want["rot_0"]),
                    Rot1 = BitConverter.ToSingle(raw, at + want["rot_1"]),
                    Rot2 = BitConverter.ToSingle(raw, at + want["rot_2"]),
                    Rot3 = BitConverter.ToSingle(raw, at + want["rot_3"]),
                };
            }

            Normalize(set);
            return set;
        }

        /// <summary>
        /// Mirror of the reference loader: fit positions into a 2-unit cube
        /// centered on origin (Y negated: pipeline files arrive Y-down, the
        /// renderer is Y-up) and scale sigmas the same way, clamped. Sigmas
        /// are stored LINEAR after this (the draw squares them).
        /// </summary>
        public static void Normalize(SplatSet set)
        {
            SplatGaussian[] g = set.Gaussians;
            if (g.Length == 0)
            {
                return;
            }

            float minX = g[0].X, minY = g[0].Y, minZ = g[0].Z;
            float maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 1; i < g.Length; i++)
            {
                if (g[i].X < minX) minX = g[i].X; else if (g[i].X > maxX) maxX = g[i].X;
                if (g[i].Y < minY) minY = g[i].Y; else if (g[i].Y > maxY) maxY = g[i].Y;
                if (g[i].Z < minZ) minZ = g[i].Z; else if (g[i].Z > maxZ) maxZ = g[i].Z;
            }

            float span = maxX - minX;
            if (maxY - minY > span) span = maxY - minY;
            if (maxZ - minZ > span) span = maxZ - minZ;
            float s = span > 1e-6f ? 2f / span : 1f;
            float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            float mass = 0;
            for (int i = 0; i < g.Length; i++)
            {
                SplatGaussian e = g[i];
                e.X = (e.X - cx) * s;
                e.Y = -(e.Y - cy) * s;
                e.Z = (e.Z - cz) * s;
                e.Scale0 = ClampSigma(MathF.Exp(ClampLog(e.Scale0)) * s);
                e.Scale1 = ClampSigma(MathF.Exp(ClampLog(e.Scale1)) * s);
                e.Scale2 = ClampSigma(MathF.Exp(ClampLog(e.Scale2)) * s);
                g[i] = e;
                min.X = Math.Min(min.X, e.X);
                min.Y = Math.Min(min.Y, e.Y);
                min.Z = Math.Min(min.Z, e.Z);
                max.X = Math.Max(max.X, e.X);
                max.Y = Math.Max(max.Y, e.Y);
                max.Z = Math.Max(max.Z, e.Z);
                mass += 1f / (1f + MathF.Exp(-e.Opacity));
            }

            set.BoundsMin = min;
            set.BoundsMax = max;
            set.OpacityMass = mass;
        }

        /// <summary>Byte width of a PLY scalar, or null when not a plain scalar.</summary>
        internal static int? ScalarWidth(string type) => type switch
        {
            "char" or "uchar" or "int8" or "uint8" => 1,
            "short" or "ushort" or "int16" or "uint16" => 2,
            "int" or "uint" or "float" or "int32" or "uint32" or "float32" => 4,
            "double" or "float64" => 8,
            _ => null,
        };

        private static float ClampLog(float v) => v > 3f ? 3f : v < -8f ? -8f : v;

        private static float ClampSigma(float v) => v < 0.0005f ? 0.0005f : v > 0.35f ? 0.35f : v;

        private static int FindHeaderEnd(byte[] raw)
        {
            // "end_header\n" on one line, as every writer in this pipeline emits.
            byte[] needle = System.Text.Encoding.ASCII.GetBytes("end_header\n");
            for (int i = 0; i + needle.Length <= raw.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (raw[i + j] != needle[j])
                    {
                        hit = false;
                        break;
                    }
                }

                if (hit)
                {
                    return i + needle.Length;
                }
            }

            throw new InvalidDataException("no end_header");
        }
    }
}
