// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): water render modes (ADR-0037). Not upstream's.
using System;
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Utility.Logging;
using Godot;

namespace GUO.Renderer.Water;

/// <summary>
/// One look for water. The client's built-in styles are code; a later pack
/// style (ADR-0037 decision 6, WATER-04) implements the same interface from
/// a Store component, so nothing outside this file knows where a style's
/// numbers -- or, if the owner allows it, its shader -- come from.
/// </summary>
internal interface IWaterStyle
{
    /// <summary>The value Profile.WaterStyle holds for this style.</summary>
    string Id { get; }

    /// <summary>The name Options shows.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Pushes this style's uniforms. <paramref name="set"/> reaches every
    /// world material (UltimaBatcher2D.SetOnAllMaterials). Called when the
    /// style becomes active, not every frame.
    /// </summary>
    void Bind(Action<string, Variant> set);
}

/// <summary>
/// The built-in "uo" model: the tiles' own colours, crossing swells lit from
/// UO's light direction, a few brightness levels so it stays pixel art, and
/// surf where water meets a dry cell.
/// </summary>
internal sealed class UoRealtimeWaterStyle : IWaterStyle
{
    public string Id => "uo";
    public string DisplayName => "Realtime (UO colours)";

    public void Bind(Action<string, Variant> set)
    {
        set("water_model", 1);
        set("water_tint", new Color(0.12f, 0.24f, 0.42f, 1f));
        // speed, scale (1 / world px), wave strength, foam strength
        set("water_params", new Vector4(1.0f, 0.045f, 0.22f, 0.85f));
        // glint, foam width (cells), brightness levels, unused
        set("water_params2", new Vector4(0.35f, 0.22f, 6f, 0f));
    }
}

/// <summary>
/// The water styles the client knows, the profile's choice, and the per-frame
/// work: the mask of wet cells and the uniforms that place water in the world.
/// Classic is no style at all: nothing is bound, no tile carries the water
/// bit, and the world draws as it does without this file.
/// </summary>
internal static class WaterRender
{
    public const string Classic = "classic";

    /// <summary>Bit 6 of the shader mode byte: a Wet tile under a water style (uo_water.gdshaderinc).</summary>
    public const int ModeBit = 0x40;

    private static readonly IWaterStyle[] BuiltIn = { new UoRealtimeWaterStyle() };
    private static readonly HashSet<string> _warned = new HashSet<string>();

    /// <summary>
    /// --water-style NAME: this session's style, ahead of the profile and
    /// never saved. For probes, scenarios and the perf lab.
    /// </summary>
    public static string Override;

    private static IWaterStyle _bound;
    private static bool _boundOnce;
    private static readonly WaterMask _mask = new WaterMask();

    /// <summary>Every style Options can offer, Classic first, as (id, name).</summary>
    public static IEnumerable<(string Id, string Name)> Choices()
    {
        yield return (Classic, "Classic tiles");
        foreach (IWaterStyle s in BuiltIn)
        {
            yield return (s.Id, s.DisplayName);
        }
    }

    /// <summary>Choices() as two lists, for a combo box: ids and names, Classic at 0.</summary>
    public static (string[] Ids, string[] Names) ChoiceLists()
    {
        var ids = new List<string>();
        var names = new List<string>();
        foreach ((string id, string name) in Choices())
        {
            ids.Add(id);
            names.Add(name);
        }
        return (ids.ToArray(), names.ToArray());
    }

    /// <summary>Where a profile's style sits in ChoiceLists(); 0 (Classic) when it is not offered.</summary>
    public static int ChoiceIndex(string id)
    {
        string[] ids = ChoiceLists().Ids;
        for (int i = 0; i < ids.Length; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>The id at a ChoiceLists() index, Classic when out of range.</summary>
    public static string ChoiceId(int index)
    {
        string[] ids = ChoiceLists().Ids;
        return index >= 0 && index < ids.Length ? ids[index] : Classic;
    }

    /// <summary>
    /// The style a profile asks for, or null for Classic. An id the client
    /// does not know (a removed pack, a typo in the file) is Classic, logged once.
    /// </summary>
    public static IWaterStyle Resolve(Profile profile)
    {
        string id = Override ?? profile?.WaterStyle;
        if (string.IsNullOrEmpty(id) || string.Equals(id, Classic, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (IWaterStyle s in BuiltIn)
        {
            if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        if (_warned.Add(id))
        {
            Log.Warn($"[water] unknown water style '{id}'; drawing classic tiles");
        }
        return null;
    }

    /// <summary>Whether Wet tiles carry the water bit for this profile.</summary>
    public static bool IsStyled(Profile profile) => Resolve(profile) != null;

    /// <summary>
    /// Upstream's Animated water effect, which a water style replaces
    /// (ADR-0037 decision 4): on only when the player asked for it and the
    /// water is Classic.
    /// </summary>
    public static bool UpstreamWobble(Profile profile) =>
        profile != null && profile.AnimatedWaterEffect && !IsStyled(profile);

    // Whether this frame draws a style: set once in PrepareFrame, so the
    // per-tile Flag below is a field read, not a lookup by name.
    private static bool _styled;

    /// <summary>The mode value to add to a tile's hue vector this frame: the bit, or 0.</summary>
    public static float Flag(bool wet) => wet && _styled ? ModeBit : 0f;

    /// <summary>
    /// After Profile.WaterStyle changes: Wet land is baked into the chunk
    /// meshes with or without the bit, so every loaded mesh rebuilds.
    /// </summary>
    public static void Changed(World world)
    {
        _mask.Invalidate();
        if (world?.Map == null)
        {
            return;
        }

        foreach (var chunk in world.Map.GetUsedChunks())
        {
            if (chunk?.Mesh != null)
            {
                chunk.Mesh.IsDirty = true;
            }
        }
    }

    /// <summary>
    /// Once per world draw, before any tile is drawn. Binds the style when it
    /// changes, keeps the mask around the player and maps the world target's
    /// pixels back to world pixels: <paramref name="view"/> is the batcher's
    /// view transform and <paramref name="offsetX"/>/<paramref name="offsetY"/>
    /// the offset UpdateRealScreenPosition subtracts.
    /// </summary>
    public static void PrepareFrame(World world, UltimaBatcher2D batcher, Transform2D view, int offsetX, int offsetY)
    {
        IWaterStyle style = Resolve(ProfileManager.CurrentProfile);
        _styled = style != null;
        if (!_boundOnce || style != _bound)
        {
            // Whatever changed the profile (Options, the editor, a probe),
            // the meshes baked under the old style rebuild here.
            if (_boundOnce)
            {
                Changed(world);
            }
            _boundOnce = true;
            _bound = style;
            if (style == null)
            {
                batcher.SetWaterUniform("water_model", 0);
                return;
            }
            style.Bind(batcher.SetWaterUniform);
            batcher.SetWaterUniform("water_debug", System.Environment.GetEnvironmentVariable("GUO_WATER_DEBUG") == "1" ? 1 : 0);
            _mask.Invalidate();
        }

        if (style == null)
        {
            return;
        }

        batcher.SetWaterUniform("water_time", (float)(Time.Ticks % 3_600_000L) / 1000f);

        // Canvas-local = inverse(view) * target; world = local + offset +
        // 22, the half tile UpdateRealScreenPosition takes off for the
        // top-left corner. The view has no rotation or skew, only zoom and
        // translation, so a scale and an offset per axis carry it.
        Transform2D inv = view.AffineInverse();
        batcher.SetWaterUniform(
            "water_target_to_world",
            new Vector4(inv.X.X, inv.Y.Y, inv.Origin.X + offsetX + 22, inv.Origin.Y + offsetY + 22));

        if (world?.Player != null && _mask.Update(world, world.Player.X, world.Player.Y))
        {
            batcher.SetWaterUniform("water_mask", _mask.Texture);
            batcher.SetWaterUniform("water_mask_origin", new Vector2(_mask.OriginX, _mask.OriginY));
        }
    }

    /// <summary>For the probe: how long the last mask rebuild took, and how many.</summary>
    public static (double Ms, int Rebuilds) MaskStats => (_mask.LastBuildMs, _mask.Rebuilds);
}

/// <summary>
/// Which cells around the player hold water, and at what height, as a
/// 128 x 128 ring texture in world cells (uo_water.gdshaderinc, THE MASK).
/// Rebuilt when the player has moved a few cells or the loaded chunks change,
/// never every frame.
/// </summary>
internal sealed class WaterMask
{
    public const int Size = 128;
    private const int Recentre = 8;

    private readonly byte[] _pixels = new byte[Size * Size * 4];
    private Image _image;
    private ImageTexture _texture;
    private int _centreX = -100_000, _centreY = -100_000, _chunks = -1, _map = -1;
    private long _nextCount;

    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public double LastBuildMs { get; private set; }
    public int Rebuilds { get; private set; }
    public Texture2D Texture => _texture;

    public void Invalidate()
    {
        _chunks = -1;
        _nextCount = 0;
    }

    /// <summary>Rebuilds when needed; true when the texture or origin changed.</summary>
    public bool Update(World world, int x, int y)
    {
        bool moved = Math.Abs(x - _centreX) >= Recentre || Math.Abs(y - _centreY) >= Recentre;
        // Counting the loaded chunks walks the map's list; four times a
        // second is soon enough to see new ground arrive.
        if (_texture != null && !moved && world.Map.Index == _map && Time.Ticks < _nextCount)
        {
            return false;
        }
        _nextCount = Time.Ticks + 250;

        int chunks = 0;
        foreach (var _ in world.Map.GetUsedChunks())
        {
            chunks++;
        }

        if (_texture != null && chunks == _chunks && world.Map.Index == _map && !moved)
        {
            return false;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _centreX = x;
        _centreY = y;
        _chunks = chunks;
        _map = world.Map.Index;
        OriginX = x - Size / 2;
        OriginY = y - Size / 2;

        // Unknown cells read as water at the usual sea height: no foam.
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = 255;
            _pixels[i + 1] = 123;
            _pixels[i + 2] = 0;
            _pixels[i + 3] = 255;
        }

        foreach (var chunk in world.Map.GetUsedChunks())
        {
            if (chunk == null)
            {
                continue;
            }

            for (int cx = 0; cx < 8; cx++)
            {
                for (int cy = 0; cy < 8; cy++)
                {
                    int wx = (chunk.X << 3) + cx;
                    int wy = (chunk.Y << 3) + cy;
                    if (wx < OriginX || wy < OriginY || wx >= OriginX + Size || wy >= OriginY + Size)
                    {
                        continue;
                    }

                    bool wet = false;
                    int z = int.MinValue;
                    for (GameObject o = chunk.GetHeadObject(cx, cy); o != null; o = o.TNext)
                    {
                        bool w = o switch
                        {
                            Land land => land.TileData.IsWet,
                            Static s => s.ItemData.IsWet,
                            _ => false
                        };
                        if (w)
                        {
                            wet = true;
                            z = Math.Max(z, o.Z);
                        }
                    }

                    int at = (((wy & (Size - 1)) * Size) + (wx & (Size - 1))) * 4;
                    _pixels[at] = wet ? (byte)255 : (byte)0;
                    _pixels[at + 1] = wet ? (byte)Math.Clamp(z + 128, 0, 255) : (byte)0;
                }
            }
        }

        if (_image == null)
        {
            _image = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, _pixels);
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(Size, Size, false, Image.Format.Rgba8, _pixels);
            _texture.Update(_image);
        }

        Rebuilds++;
        LastBuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return true;
    }
}
