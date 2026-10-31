// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GUO.Renderer.DepthWorld;

// Check the emitted ABI once at shader creation, not just the GLSL text.
// Opcodes/decorations: Khronos SPIRV-Headers include/spirv/unified1/spirv.json.
internal sealed class OrderedWorldShaderContract
{
    private readonly Dictionary<uint, (uint Op, uint[] Args)> _types = new();
    private readonly Dictionary<uint, (uint Type, uint Storage)> _variables = new();
    private readonly Dictionary<(uint, uint), uint> _decorations = new();
    private readonly Dictionary<(uint, uint), uint> _offsets = new();
    internal static int ValidatedPrograms;

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Ordered shader contract: " + what);
    }

    private OrderedWorldShaderContract(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length >= 20 && bytes.Length % 4 == 0, "SPIR-V header size");
        uint[] words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
        Require(words[0] == 0x07230203, "SPIR-V magic");
        bool shader = false;
        for (int cursor = 5; cursor < words.Length;)
        {
            int count = (int)(words[cursor] >> 16);
            uint op = words[cursor] & 0xffff;
            Require(count > 0 && count <= words.Length - cursor, "SPIR-V instruction bounds");
            ReadOnlySpan<uint> args = words.AsSpan(cursor + 1, count - 1);
            if (op == 17) // OpCapability: this fixed-binding program needs Shader only.
            {
                Require(args.Length == 1 && args[0] == 1, "unexpected optional capability");
                shader = true;
            }
            else if (op is 21 or 22 or 23 or 25 or 27 or 29 or 30 or 32)
            {
                Require(args.Length > 0, "missing type ID");
                _types.Add(args[0], (op, args.Slice(1).ToArray()));
            }
            else if (op == 59) // OpVariable.
            {
                Require(args.Length >= 3, "variable operands");
                _variables.Add(args[1], (args[0], args[2]));
            }
            else if (op == 71) // OpDecorate.
            {
                Require(args.Length >= 2 && args[1] != 5300, "nonuniform descriptor decoration");
                _decorations[(args[0], args[1])] = args.Length > 2 ? args[2] : 1;
            }
            else if (op == 72 && args.Length >= 3 && args[2] == 35) // Member Offset.
            {
                Require(args.Length == 4, "member offset operands");
                _offsets[(args[0], args[1])] = args[3];
            }
            cursor += count;
        }
        Require(shader, "missing Shader capability");
    }

    private uint Decoration(uint id, uint kind) => _decorations.TryGetValue((id, kind), out uint value) ? value : uint.MaxValue;
    private uint Offset(uint id, uint member) => _offsets.TryGetValue((id, member), out uint value) ? value : uint.MaxValue;
    private uint[] Type(uint id, uint op)
    {
        Require(_types.TryGetValue(id, out var type) && type.Op == op, "unexpected type shape");
        return type.Args;
    }
    private uint Pointee(uint variable, uint storage)
    {
        var value = _variables[variable];
        uint[] pointer = Type(value.Type, 32);
        Require(pointer.Length == 2 && value.Storage == storage && pointer[0] == storage, "storage class");
        return pointer[1];
    }
    private uint Binding(uint set, uint binding)
    {
        uint found = 0;
        foreach (uint id in _variables.Keys)
            if (Decoration(id, 34) == set && Decoration(id, 33) == binding)
            { Require(found == 0, "duplicate descriptor binding"); found = id; }
        Require(found != 0, "missing descriptor binding");
        return found;
    }
    private void Uint32(uint id)
    {
        uint[] type = Type(id, 21);
        Require(type.Length == 2 && type[0] == 32 && type[1] == 0, "uint32 selector");
    }
    private void FloatVector(uint id, uint count)
    {
        uint[] vector = Type(id, 23);
        Require(vector.Length == 2 && vector[1] == count, "transform vector width");
        uint[] scalar = Type(vector[0], 22);
        Require(scalar.Length == 1 && scalar[0] == 32, "float32 transform");
    }
    private void Selector(uint storage)
    {
        int found = 0;
        foreach (var variable in _variables)
            if (variable.Value.Storage == storage && Decoration(variable.Key, 30) == 3)
            {
                Require(Decoration(variable.Key, 14) == 1, "flat selector interface");
                Uint32(Pointee(variable.Key, storage)); found++;
            }
        Require(found == 1, "one selector at location3");
    }
    internal static void Validate(ReadOnlySpan<byte> vertex, ReadOnlySpan<byte> fragment)
    {
        var v = new OrderedWorldShaderContract(vertex);
        uint variable = v.Binding(0, 2);
        uint storage = v._variables[variable].Storage;
        Require(storage is 2 or 12, "state storage buffer");
        uint block = v.Pointee(variable, storage);
        uint[] members = v.Type(block, 30);
        Require(members.Length == 1 && v.Offset(block, 0) == 0, "state block layout");
        uint[] array = v.Type(members[0], 29);
        Require(array.Length == 1 && v.Decoration(members[0], 6) == 32, "state array stride32");
        uint state = array[0];
        uint[] fields = v.Type(state, 30);
        Require(fields.Length == 3 && v.Offset(state, 0) == 0 && v.Offset(state, 1) == 12
            && v.Offset(state, 2) == 16, "state member offsets0/12/16");
        v.FloatVector(fields[0], 3); v.Uint32(fields[1]); v.FloatVector(fields[2], 4);
        v.Selector(3); // Output.
        var f = new OrderedWorldShaderContract(fragment);
        f.Selector(1); // Input.
        for (uint binding = 0; binding < 6; binding++)
        {
            uint sampled = f.Pointee(f.Binding(1, binding), 0); // UniformConstant.
            uint[] image = f.Type(sampled, 27); // Direct combined image, never descriptor array.
            Require(image.Length == 1, "literal sampled image binding");
            uint[] imageType = f.Type(image[0], 25);
            Require(imageType.Length == 7 && imageType[1] == 1 && imageType[2] == 0
                && imageType[3] == 0 && imageType[4] == 0 && imageType[5] == 1
                && imageType[6] == 0, "plain sampler2D with unknown format");
            uint[] sampledScalar = f.Type(imageType[0], 22);
            Require(sampledScalar.Length == 1 && sampledScalar[0] == 32, "float32 sampled image");
        }
        ValidatedPrograms++;
    }
}
