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
/// One look for water. The client's built-in style is code; a pack style
/// (ADR-0037 decision 6) implements the same interface from a Store
/// component, so nothing outside this file knows where a style's numbers come
/// from.
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

    /// <summary>
    /// The style's ripple settings: how hard things push the water and how
    /// fast it calms. A style without ripples sets Strength to 0.
    /// </summary>
    void BindRipples(WaterRipples ripples);
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
        set("water_model", WaterRender.Model("uo"));
        set("water_environment", false);
        set("water_tint", new Color(0.12f, 0.24f, 0.42f, 1f));
        // speed, scale (1 / world px), wave strength, foam strength
        set("water_params", new Vector4(1.0f, 0.045f, 0.22f, 0.85f));
        // glint, foam width (cells), brightness levels, ripple strength
        set("water_params2", new Vector4(0.35f, 0.22f, 6f, 1.0f));
        // grain (world px per water pixel)
        set("water_params3", new Vector4(1f, 0f, 0f, 0f));
    }

    public void BindRipples(WaterRipples ripples)
    {
        ripples.Strength = 1f;
        ripples.Damping = 0.985f;
    }
}

/// <summary>
/// A style from a Store pack in the active deployment: a guo/water@1 file
/// (StoreWater) that picks one of the shader's built-in models and sets its
/// numbers. Its id is the component's identity, pack-id:component-id.
/// </summary>
internal sealed class PackWaterStyle : IWaterStyle
{
    private readonly GUO.Store.StoreWater _style;

    public PackWaterStyle(string identity, GUO.Store.StoreWater style)
    {
        Id = identity;
        _style = style;
    }

    public string Id { get; }
    public string DisplayName => _style.Name;

    public void Bind(Action<string, Variant> set)
    {
        set("water_model", WaterRender.Model(_style.Model));
        set("water_environment", false);
        set("water_tint", new Color(_style.Tint[0], _style.Tint[1], _style.Tint[2], 1f));
        set("water_params", new Vector4(_style.Speed, _style.Scale, _style.Wave, _style.Foam));
        set("water_params2", new Vector4(_style.Glint, _style.FoamWidth, _style.Levels, _style.RippleStrength > 0f ? 1f : 0f));
        set("water_params3", new Vector4(_style.Grain, 0f, 0f, 0f));
    }

    public void BindRipples(WaterRipples ripples)
    {
        ripples.Strength = _style.RippleStrength;
        ripples.Damping = _style.RippleDamping;
    }
}

/// <summary>Opt-in lighting-mode water; existing styles retain their original shading.</summary>
internal sealed class LightingWaterStyle : IWaterStyle
{
    public string Id => "lighting";
    public string DisplayName => "Lighting (sky, wind and light glints)";

    public void Bind(Action<string, Variant> set)
    {
        set("water_model", WaterRender.Model("realistic"));
        set("water_environment", true);
        set("water_tint", new Color(0.06f, 0.22f, 0.30f, 1f));
        set("water_params", new Vector4(0.65f, 0.025f, 0.16f, 0.72f));
        set("water_params2", new Vector4(0.28f, 0.38f, 0f, 1f));
        set("water_params3", new Vector4(1f, 0f, 0f, 0f));
    }

    public void BindRipples(WaterRipples ripples)
    {
        ripples.Strength = 0.8f;
        ripples.Damping = 0.98f;
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

    private static readonly IWaterStyle[] BuiltIn = { new UoRealtimeWaterStyle(), new LightingWaterStyle() };
    private static readonly HashSet<string> _warned = new HashSet<string>();

    /// <summary>
    /// --water-style NAME: this session's style, ahead of the profile and
    /// never saved. For probes, scenarios and the perf lab.
    /// </summary>
    public static string Override;

    // The shard session is created only after the player approves its content.
    // Water consent is separate and never inferred from approving that content.
    internal static GUO.Store.StoreWaterPolicy ShardPolicy => GUO.Host.ShardSession.Active
        && string.Equals(GUO.Host.ShardSession.Current.Host, Settings.GlobalSettings?.IP, StringComparison.OrdinalIgnoreCase)
        && GUO.Host.ShardSession.Current.Port == Settings.GlobalSettings?.Port
        ? GUO.Host.ShardSession.Current.Water : null;
    private static GUO.Host.ShardSession.Data _policySession;
    private static string _consentKey;
    internal static string ShardConsentKey
    {
        get
        {
            var session = GUO.Host.ShardSession.Current;
            if (!ReferenceEquals(session, _policySession))
            {
                _policySession = session;
                _consentKey = session?.Water?.ConsentKey((session.ContentUrl ?? "") + "\n"
                    + (session.Host ?? "").ToLowerInvariant() + ":" + session.Port);
            }
            return ShardPolicy == null ? null : _consentKey;
        }
    }

    internal static bool ShardAccepted(Profile p) => ShardConsentKey is string key && p?.ShardWaterConsent == key;
    internal static string SelectedId(Profile p) => ShardAccepted(p)
        ? ShardPolicy.Select(p.ShardWaterStyle) : p?.WaterStyle ?? Classic;

    internal static void SetPlayerChoice(Profile p, string id)
    {
        if (ShardAccepted(p)) p.ShardWaterStyle = ShardPolicy.Select(id);
        else p.WaterStyle = id;
    }

    internal static void AcceptShard(Profile p, bool accept)
    {
        if (!accept) { p.ShardWaterConsent = ""; return; }
        if (ShardConsentKey is not string key) return;
        if (p.ShardWaterConsent != key) p.ShardWaterStyle = ShardPolicy.Default;
        p.ShardWaterConsent = key;
    }

    internal static string ShardDescription => ShardPolicy is { } policy
        ? $"Water set by shard: {policy.Default}. " + (policy.Lock ? "Style choices restricted after consent." : "Players may override.")
        : "";

#if TOOLS
    // L3 water authoring, scoped to the editor's exact world instance. Never
    // persisted to a player profile or compiled into an exported client.
    internal static World EditorWorld;
    internal static IWaterStyle EditorStyle;
    internal static float EditorSeconds;
    internal static bool EditorPaused;
    private static bool _lightingTargetBound;
    private static bool InEditorPreview => EditorWorld != null && ReferenceEquals(EditorWorld, Client.Game?.UO?.World);

    /// <summary>Read-only scene-thread diagnostics for authoring and private interaction proofs.</summary>
    internal static object Inspect(World world)
    {
        if (world?.Player == null) return null;
        var player = world.Player;
        bool wet = _mask.WaterAt(player.X, player.Y, out int z);
        return new
        {
            selectedStyle = Resolve(ProfileManager.CurrentProfile)?.Id ?? Classic,
            boundStyle = _bound?.Id ?? Classic, active = _styled,
            playerWet = wet, surfaceZ = wet ? (int?)z : null,
            playerDisturbs = wet && player.Z <= z + 1,
            rippleStamps = _ripples.Stamps, rippleSteps = _ripples.Steps,
            rainStamps = _ripples.RainStamps,
            lightTargetBound = _lightingTargetBound,
            daylight = Daylight(world, ProfileManager.CurrentProfile),
            wind = Wind(world, ProfileManager.CurrentProfile),
            lastUpdateMs = _ripples.LastUpdateMs
        };
    }
#endif

    private static IWaterStyle _bound;
    private static bool _boundOnce;
    private static readonly WaterMask _mask = new WaterMask();
    private static readonly WaterRipples _ripples = new WaterRipples();
    private static long _lastTicks;
    private static int _lastMap = -1;
    private static Vector4 _styleParams2;
    private static bool _ripplesEnabled;

    // ADR-0048: called after the current speed-mode light target is ready.
    // Bounds are target pixels (origin x/y, width/height), not world tiles.
    // The core must clear this binding on every canvas/fallback frame.
    internal static void BindLightTarget(UltimaBatcher2D batcher, Texture2D texture, Vector4 bounds, Vector3 ambient)
    {
        if (_bound is not LightingWaterStyle || !_styled) return;
        batcher.SetWaterUniform("water_light_target", texture);
        batcher.SetWaterUniform("water_light_bounds", bounds);
        batcher.SetWaterUniform("water_light_ambient", ambient);
        batcher.SetWaterUniform("water_has_light_target", true);
#if TOOLS
        _lightingTargetBound = true;
#endif
    }

    internal static void ClearLightTarget(UltimaBatcher2D batcher)
    {
        if (_bound is not LightingWaterStyle) return;
        batcher.SetWaterUniform("water_has_light_target", false);
        batcher.SetWaterUniform("water_light_target", default(Variant));
        batcher.SetWaterUniform("water_has_reflections", false);
        batcher.SetWaterUniform("water_surface_buffer", default(Variant));
        batcher.SetWaterUniform("water_scene_color", default(Variant));
#if TOOLS
        _lightingTargetBound = false;
#endif
    }

    // Same-frame, nearest source buffers for ADR-0048 geometric reflections.
    // The caller validates the borrowed SpeedModeBindings.Current ticket.
    internal static void BindReflectionTargets(UltimaBatcher2D batcher, Texture2D surface,
        Texture2D color, Vector2I size, Vector2I origin, float artScale, bool enabled)
    {
        if (_bound is not LightingWaterStyle || !_styled) return;
        bool valid = enabled && surface != null && color != null && size.X > 0 && size.Y > 0
            && surface.GetWidth() == size.X && surface.GetHeight() == size.Y
            && color.GetWidth() == size.X && color.GetHeight() == size.Y;
        batcher.SetWaterUniform("water_has_reflections", valid);
        batcher.SetWaterUniform("water_surface_buffer", valid ? surface : default(Variant));
        batcher.SetWaterUniform("water_scene_color", valid ? color : default(Variant));
        if (!valid) return;
        batcher.SetWaterUniform("water_reflection_target_size", new Vector2(size.X, size.Y));
        batcher.SetWaterUniform("water_reflection_origin", new Vector2(origin.X, origin.Y));
        batcher.SetWaterUniform("water_reflection_art_scale", float.IsFinite(artScale) ? Math.Clamp(artScale, 0.125f, 8f) : 1f);
    }

    internal static bool RipplesEnabled(Profile profile) =>
        IsStyled(profile) && UoPlusOptions.Enabled(profile, UoPlusFeature.WaterRipples);

    /// <summary>The shader's water_model for a model name (uo_water.gdshaderinc); 0 is Classic.</summary>
    public static int Model(string model) => model switch
    {
        "uo" => 1,
        "pixel" => 2,
        "cel" => 3,
        "realistic" => 4,
        "classicplus" => 5,
        _ => 0
    };

    private static GUO.Store.StoreRuntimeContent _packSource;
    private static IWaterStyle[] _packStyles = Array.Empty<IWaterStyle>();

    /// <summary>
    /// The styles of the active deployment's water components, read once per
    /// mounted content (it changes only on a restart). Tests set it directly.
    /// </summary>
    internal static IWaterStyle[] PackStyles
    {
        get
        {
            if (ProbePackStyles != null)
            {
                return ProbePackStyles;
            }
            var content = Client.Game?.UO?.FileManager?.Content;
            if (content != _packSource)
            {
                _packSource = content;
                var styles = new List<IWaterStyle>();
                foreach ((string identity, GUO.Store.StoreWater style) in content?.WaterStyles
                    ?? (IReadOnlyList<(string, GUO.Store.StoreWater)>)Array.Empty<(string, GUO.Store.StoreWater)>())
                {
                    styles.Add(new PackWaterStyle(identity, style));
                }
                _packStyles = styles.ToArray();
            }
            return _packStyles;
        }
    }

    /// <summary>WaterProbe's stand-in for a mounted deployment's styles; null in the client.</summary>
    internal static IWaterStyle[] ProbePackStyles;

    /// <summary>Built-in styles, then the deployment's pack styles.</summary>
    private static IEnumerable<IWaterStyle> Styles()
    {
        foreach (IWaterStyle s in BuiltIn)
        {
            yield return s;
        }
        foreach (IWaterStyle s in PackStyles)
        {
            yield return s;
        }
    }

    /// <summary>Every style Options can offer, Classic first, as (id, name).</summary>
    public static IEnumerable<(string Id, string Name)> Choices()
    {
        yield return (Classic, "Classic tiles");
        foreach (IWaterStyle s in Styles())
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

    internal static (string[] Ids, string[] Names) PlayerChoiceLists(Profile profile)
    {
        if (!ShardAccepted(profile) || !ShardPolicy.Lock) return ChoiceLists();
        var ids = new List<string>();
        var names = new List<string>();
        foreach (var choice in Choices())
            if (ShardPolicy.Allows(choice.Id)) { ids.Add(choice.Id); names.Add(choice.Name); }
        // Missing packs never download automatically; Classic is the safe fallback.
        if (ids.Count == 0) { ids.Add(Classic); names.Add("Classic tiles (shard style unavailable)"); }
        return (ids.ToArray(), names.ToArray());
    }

    internal static int PlayerChoiceIndex(Profile p)
    {
        int index = Array.FindIndex(PlayerChoiceLists(p).Ids, id => string.Equals(id, SelectedId(p), StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, index);
    }

    internal static string PlayerChoiceId(Profile p, int index)
    {
        var ids = PlayerChoiceLists(p).Ids;
        return index >= 0 && index < ids.Length ? ids[index] : Classic;
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
#if TOOLS
        if (InEditorPreview) return EditorStyle;
#endif
        string id = Override ?? SelectedId(profile);
        if (string.IsNullOrEmpty(id) || string.Equals(id, Classic, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Every frame: two array walks, no iterator to allocate.
        foreach (IWaterStyle s in BuiltIn)
        {
            if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        foreach (IWaterStyle s in PackStyles)
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

    internal static bool EnvironmentEnabled(Profile profile) => Resolve(profile) is LightingWaterStyle;

    internal static float Wind(World world, Profile profile)
    {
        float weather = (world?.Weather.Wind ?? 0) / 4f;
        return weather + (profile?.WindStrength ?? 0f) * (weather == 0f ? 0.4f : weather);
    }

    // The shard's daylight and the player's explicit light override are the
    // clock available to the renderer; never infer sky time from the PC clock.
    internal static float Daylight(World world, Profile profile)
    {
        if (world == null) return 1f;
        int overall = world.Light.Overall;
        if (profile?.UseCustomLightLevel == true)
            overall = profile.LightLevelType == 1 ? Math.Min(overall, profile.LightLevel) : profile.LightLevel;
        return Math.Clamp(Math.Max(world.Light.Personal, 32 - overall) / 32f, 0f, 1f);
    }

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

    internal static Vector2 SurfacePoint(int x, int y, float bx, float by)
        => new(x + (bx + by - 22f) / 44f, y + (by - bx + 22f) / 44f);

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
            ClearLightTarget(batcher);
            _bound = style;
            if (style == null)
            {
                batcher.SetWaterUniform("water_model", 0);
                _ripples.Reset();
                _ripplesEnabled = false;
                _lastTicks = 0;
                return;
            }
            style.Bind((name, value) =>
            {
                if (name == "water_params2") _styleParams2 = value.AsVector4();
                batcher.SetWaterUniform(name, value);
            });
            style.BindRipples(_ripples);
            _ripples.Reset();
            batcher.SetWaterUniform("water_debug", int.TryParse(System.Environment.GetEnvironmentVariable("GUO_WATER_DEBUG"), out int debug) ? debug : 0);
            _mask.Invalidate();
        }

        if (style == null)
        {
            if (_ripplesEnabled) _ripples.Reset();
            _ripplesEnabled = false;
            _lastTicks = 0;
            return;
        }

        bool environment = style is LightingWaterStyle;
        batcher.SetWaterUniform("water_environment", environment);
        if (environment)
        {
            if (world != null) SpeedMode.Weather.SpeedModeWeather.Observe(world, ProfileManager.CurrentProfile);
            float daylight = Daylight(world, ProfileManager.CurrentProfile);
            batcher.SetWaterUniform("water_daylight", daylight);
            // Weather.Wind also drives the rain angle. Calm weather still has
            // a gentle swell; storms steepen and advect the same wave field.
            batcher.SetWaterUniform("water_wind", Wind(world, ProfileManager.CurrentProfile));
            ClearLightTarget(batcher);
        }

        bool ripplesEnabled = RipplesEnabled(ProfileManager.CurrentProfile);
        var parameters = _styleParams2;
        if (!ripplesEnabled) parameters.W = 0f;
        batcher.SetWaterUniform("water_params2", parameters);
        if (ripplesEnabled != _ripplesEnabled)
        {
            _ripples.Reset();
            _lastTicks = 0;
            _ripplesEnabled = ripplesEnabled;
        }

        batcher.SetWaterUniform("water_time", (float)(Time.Ticks % 3_600_000L) / 1000f);
#if TOOLS
        if (InEditorPreview) batcher.SetWaterUniform("water_time", EditorSeconds);
#endif

        // Canvas-local = inverse(view) * target; world = local + offset +
        // 22, the half tile UpdateRealScreenPosition takes off for the
        // top-left corner. The view has no rotation or skew, only zoom and
        // translation, so a scale and an offset per axis carry it.
        Transform2D inv = view.AffineInverse();
        batcher.SetWaterUniform(
            "water_target_to_world",
            new Vector4(inv.X.X, inv.Y.Y, inv.Origin.X + offsetX + 22, inv.Origin.Y + offsetY + 22));

        if (world?.Player == null)
        {
            return;
        }

        if (_mask.Update(world, world.Player.X, world.Player.Y))
        {
            batcher.SetWaterUniform("water_mask", _mask.Texture);
            batcher.SetWaterUniform("water_mask_origin", new Vector2(_mask.OriginX, _mask.OriginY));
        }

        // Off means no object scan, stamps or wave simulation; the style still draws.
        if (!ripplesEnabled) return;
        style.BindRipples(_ripples);
        _ripples.Strength *= UoPlusOptions.Density(ProfileManager.CurrentProfile, UoPlusFeature.WaterRipples) / 100f;

        if (world.Map.Index != _lastMap)
        {
            _lastMap = world.Map.Index;
            _ripples.Reset();
        }
        float delta = _lastTicks == 0 ? 0f : Math.Clamp((Time.Ticks - _lastTicks) / 1000f, 0f, 0.25f);
        _lastTicks = Time.Ticks;
#if TOOLS
        if (InEditorPreview && EditorPaused) return;
#endif
        if (_ripples.Update(world, _mask, delta))
        {
            batcher.SetWaterUniform("water_ripples", _ripples.Texture);
            batcher.SetWaterUniform("water_ripples_origin", new Vector2(_ripples.OriginX, _ripples.OriginY));
        }
    }

    /// <summary>The ripple field, for probes (stamps, steps, cost).</summary>
    internal static WaterRipples Ripples => _ripples;

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

    /// <summary>Whether the last build saw water at a cell, and its height. False outside the window.</summary>
    public bool WaterAt(int x, int y, out int z)
    {
        z = 0;
        if (_texture == null || x < OriginX || y < OriginY || x >= OriginX + Size || y >= OriginY + Size)
        {
            return false;
        }
        int at = (((y & (Size - 1)) * Size) + (x & (Size - 1))) * 4;
        if (_pixels[at] == 0)
        {
            return false;
        }
        z = _pixels[at + 1] - 128;
        return true;
    }

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
                    int dryZ = int.MinValue;
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
                        else if (o is Land || o is Static)
                        {
                            dryZ = Math.Max(dryZ, o.Z);
                        }
                    }

                    // G is the water's height, or for a dry cell its ground's,
                    // so a style can tell a beach from a cliff.
                    int at = (((wy & (Size - 1)) * Size) + (wx & (Size - 1))) * 4;
                    _pixels[at] = wet ? (byte)255 : (byte)0;
                    _pixels[at + 1] = wet ? (byte)Math.Clamp(z + 128, 0, 255)
                        : dryZ == int.MinValue ? (byte)0 : (byte)Math.Clamp(dryZ + 128, 0, 255);
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
