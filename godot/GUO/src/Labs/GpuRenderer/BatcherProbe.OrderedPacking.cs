// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Runtime.InteropServices;
using Godot;
using GUO.Labs.GpuRenderer;
using GUO.Renderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PreviousPackedVertex
    {
        internal Vector4 PositionUv, PackedHue, Normal;
    }

    private void VerifyOrderedPacking()
    {
        // Frozen RD9 packing expression. The oracle retains original GodotSharp
        // constructors/operators, including positive-zero addition and UNORM8.
        PreviousPackedVertex Previous(Vector2 p, Vector2 uv, Color color, Vector3 normal, bool mesh, bool rect)
        {
            float Byte(float value) => Math.Clamp((int)((double)value * 255.0), 0, 255) / 255f;
            if (mesh) color = new Color(Byte(color.R), Byte(color.G), Byte(color.B), Byte(color.A));
            p += Vector2.Zero;
            return new PreviousPackedVertex
            {
                PositionUv = new Vector4(p.X,p.Y,uv.X,uv.Y),
                PackedHue = new Vector4(color.R,color.G,color.B,color.A),
                Normal = new Vector4(normal.X,normal.Y,normal.Z,rect ? 1 : 0)
            };
        }
        bool Same(ref PreviousPackedVertex old, ref OrderedWorldDevice.Vertex actual) =>
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref old,1)).SequenceEqual(
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual,1)));
        var random = new Random(91873);
        float Raw() => BitConverter.Int32BitsToSingle(unchecked((int)random.NextInt64(0,1L<<32)));
        float[] boundary = { 0f, -0f, float.Epsilon, -float.Epsilon, float.MaxValue, -float.MaxValue,
            float.PositiveInfinity, float.NegativeInfinity, BitConverter.Int32BitsToSingle(unchecked((int)0x7fc01234)),
            BitConverter.Int32BitsToSingle(unchecked((int)0xffc05678)), 0.5f, 64/255f, 137/255f };
        bool exact = true;
        int cases = 0;
        PreviousPackedVertex lastOld = default;
        OrderedWorldDevice.Vertex lastNew = default;
        for (int i=0;i<4096;i++)
        {
            var p = new Vector2(i<boundary.Length ? boundary[i] : Raw(),Raw());
            var uv = new Vector2(Raw(),Raw());
            var normal = new Vector3(Raw(),Raw(),Raw());
            var hue = new Vector3(i%65536,i%31, i%4==0 ? 2f : i%4==1 ? 0.5f : (i%256)/255f);
            Color color = UltimaBatcher2D.Encode(hue);
            foreach(bool mesh in new[]{false,true})
                foreach(bool rect in new[]{false,true})
                {
                    lastOld=Previous(p,uv,color,normal,mesh,rect);
                    lastNew=OrderedWorldCapture.PackVertex(p,uv,color,normal,mesh,rect);
                    exact &= Same(ref lastOld,ref lastNew); cases++;
                }
        }
        float[] hueEdges = { -1, 0, 255, 256, 4095, 4096, 65535, 65536 };
        float[] alphaEdges = { -0f, 0f, 1/255f, 64/255f, 0.5f, 137/255f,
            MathF.BitDecrement(1f), 1f, MathF.BitIncrement(1f), 2f,
            BitConverter.Int32BitsToSingle(unchecked((int)0x7fc01234)), float.PositiveInfinity, float.NegativeInfinity };
        foreach(float index in hueEdges)
            foreach(float alpha in alphaEdges)
                foreach(int mode in new[]{0,30})
                    foreach(bool mesh in new[]{false,true})
                        foreach(bool rect in new[]{false,true})
                        {
                            var p = new Vector2(-0f,0f);
                            var uv = new Vector2(1,0);
                            var normal = new Vector3(0,1,1);
                            Color color = UltimaBatcher2D.Encode(new Vector3(index,mode,alpha));
                            lastOld=Previous(p,uv,color,normal,mesh,rect);
                            lastNew=OrderedWorldCapture.PackVertex(p,uv,color,normal,mesh,rect);
                            exact &= Same(ref lastOld,ref lastNew); cases++;
                        }
        CheckThat("RD packing preserves exact transport bytes",exact && cases==17216,$"packing mismatch across {cases} cases");
        lastNew.Normal.W = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(lastNew.Normal.W)^1);
        CheckThat("RD packing byte oracle catches changed bit",!Same(ref lastOld,ref lastNew),"byte oracle ignored a changed field");
        CheckThat("RD plain transport ABI",Marshal.SizeOf<OrderedWorldDevice.Float4>()==16
            && Marshal.SizeOf<OrderedWorldDevice.Vertex>()==48
            && Marshal.OffsetOf<OrderedWorldDevice.Vertex>(nameof(OrderedWorldDevice.Vertex.PackedHue)).ToInt32()==16
            && Marshal.OffsetOf<OrderedWorldDevice.Vertex>(nameof(OrderedWorldDevice.Vertex.Normal)).ToInt32()==32,"transport ABI changed");
    }
}
#endif
