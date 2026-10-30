// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: opt-in lighting speed mode, ADR-0048. No art pixels in this table.
using System;
using System.Collections.Generic;
using System.IO;
using TileVector = System.Numerics.Vector2;
using System.Text.Json;

namespace GUO.Renderer.SpeedMode.WindowLight;

internal enum OpeningShape { None, Rect, RoundArch, PointedArch, Oval }

internal sealed class WindowOpening
{
    public ushort Graphic { get; }
    public OpeningShape Shape { get; }
    public float Aspect { get; }
    public TileVector PatchTiles { get; }
    private readonly int[] _rows;
    public int RowCount => _rows.Length;
    public int PanesInRow(int row) => _rows[row];

    internal WindowOpening(ushort graphic, OpeningShape shape, float aspect, TileVector patch, int[] rows)
    {
        Graphic = graphic;
        Shape = shape;
        Aspect = aspect;
        PatchTiles = patch;
        _rows = rows;
    }

    // Normalized opening coordinates; v=0 is the bottom, rows are top to bottom.
    public bool Contains(float u, float v, float bar = 0.035f)
    {
        if (Shape == OpeningShape.None || !(u > 0 && u < 1 && v > 0 && v < 1)) return false;
        float x = 2 * u - 1;
        float spring = MathF.Max(0, 1 - Aspect * 0.5f);
        if (Shape == OpeningShape.RoundArch && v > spring &&
            x*x + MathF.Pow((v-spring)/(1-spring), 2) > 1) return false;
        if (Shape == OpeningShape.PointedArch && v > 0.5f &&
            MathF.Pow(MathF.Abs(x)+1, 2) + 3*MathF.Pow(2*v-1, 2) > 4) return false;
        if (Shape == OpeningShape.Oval && x*x + MathF.Pow(2*v-1, 2) > 1) return false;
        for (int i = 1; i < _rows.Length; i++)
            if (MathF.Abs(v-(float)i/_rows.Length) < bar/2) return false;
        int row = Math.Min(_rows.Length-1, (int)((1-v)*_rows.Length));
        for (int i = 1; i < _rows[row]; i++)
            if (MathF.Abs(u-(float)i/_rows[row]) < bar/2) return false;
        return true;
    }

    // Window tangent and inward normal are orthonormal tile-space axes. Ray is
    // the horizontal sun-ray direction. An outward/grazing ray emits no patch.
    public bool TryProject(TileVector origin, TileVector tangent, TileVector inward, TileVector ray,
        out TileVector width, out TileVector depth)
    {
        width = depth = default;
        float normal = TileVector.Dot(ray, inward);
        if (Shape == OpeningShape.None || !float.IsFinite(normal) || normal <= 1e-6f) return false;
        float shear = TileVector.Dot(ray, tangent)/normal;
        if (!float.IsFinite(shear)) return false;
        width = tangent * PatchTiles.X;
        depth = (inward + tangent*shear) * PatchTiles.Y;
        return true;
    }
}

internal static class WindowLightTable
{
    // Lazy: the off-state never opens the table or creates renderer resources.
    private static readonly Lazy<IReadOnlyDictionary<ushort, WindowOpening>> Builtin = new(() =>
    {
        using Stream stream = typeof(WindowLightTable).Assembly.GetManifestResourceStream("lighting/window_light.json")
            ?? throw new InvalidDataException("Built-in window light metadata is missing.");
        return Parse(stream);
    });

    public static IReadOnlyDictionary<ushort, WindowOpening> BuiltIn => Builtin.Value;

    // Pack extension is explicit and atomic: reject duplicate entries within a
    // component; the caller decides whether to replace a built-in graphic.
    public static IReadOnlyDictionary<ushort, WindowOpening> Parse(Stream stream)
    {
        using JsonDocument doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;
        RequireKeys(root, "schema", "windows");
        if (root.GetProperty("schema").GetString() != "guo/window-light@1") Fail("schema");
        JsonElement windows = root.GetProperty("windows");
        if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() is < 1 or > 65536) Fail("windows");
        var result = new Dictionary<ushort, WindowOpening>();
        foreach (JsonElement entry in windows.EnumerateArray())
        {
            RequireKeys(entry, "graphic", "shape", "rows", "aspect", "patchTiles");
            if (!entry.GetProperty("graphic").TryGetUInt16(out ushort graphic) || result.ContainsKey(graphic)) Fail("graphic");
            OpeningShape shape = entry.GetProperty("shape").GetString() switch
            {
                "none" => OpeningShape.None, "rect" => OpeningShape.Rect,
                "round_arch" => OpeningShape.RoundArch, "pointed_arch" => OpeningShape.PointedArch,
                "oval" => OpeningShape.Oval, _ => throw new InvalidDataException("Unknown window opening shape.")
            };
            JsonElement rows = entry.GetProperty("rows"), patch = entry.GetProperty("patchTiles");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 16) Fail("rows");
            int[] panes = new int[rows.GetArrayLength()];
            for (int i = 0; i < panes.Length; i++)
                if (!rows[i].TryGetInt32(out panes[i]) || panes[i] is < 1 or > 16) Fail("rows");
            if (patch.ValueKind != JsonValueKind.Array || patch.GetArrayLength() != 2) Fail("patchTiles");
            float aspect = entry.GetProperty("aspect").GetSingle();
            TileVector size = new(patch[0].GetSingle(), patch[1].GetSingle());
            if (shape == OpeningShape.None)
            {
                if (aspect != 0 || size != TileVector.Zero || panes.Length != 1 || panes[0] != 1) Fail("none");
            }
            else if (!InRange(aspect, .01f, 8) || !InRange(size.X, .01f, 16) || !InRange(size.Y, .01f, 16)) Fail("dimensions");
            result.Add(graphic, new WindowOpening(graphic, shape, aspect, size, panes));
        }
        return result;
    }

    private static bool InRange(float n, float lo, float hi) => float.IsFinite(n) && n >= lo && n <= hi;
    private static void Fail(string field) => throw new InvalidDataException($"Invalid window light {field}.");
    private static void RequireKeys(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail("object");
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(keys, property.Name) < 0) Fail(property.Name);
            count++;
        }
        if (count != keys.Length) Fail("fields");
        foreach (string key in keys) if (!element.TryGetProperty(key, out _)) Fail(key);
    }
}
