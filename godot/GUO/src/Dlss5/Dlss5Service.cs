using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Godot.Collections;

namespace GUO.Dlss5;

/// <summary>
/// C# bridge to the native DLSS5 GDExtension (addons/dlss5).
///
/// The extension registers a native ClassDB class ("DLSS5") which C# cannot
/// call directly (it is not in GodotSharp's generated bindings), so this
/// service instantiates it through a tiny GDScript bridge. All heavy
/// lifting (ORT session, scaffolding math, CUDA/CPU fallback) lives native.
///
/// Provider order is cuda -&gt; dml -&gt; cpu. This native build offers cuda
/// then cpu (no DML execution provider is compiled into the GDExtension, so
/// the dml slot always falls through); <see cref="Provider"/> surfaces
/// whichever one the native session actually picked ("cuda", "cpu", or ""
/// when nothing is loaded yet).
/// </summary>
public sealed class Dlss5Service
{
    /// <summary>The tile the networks run at (still and temporal alike).</summary>
    public const int WorkSize = 320;

    /// <summary>Native temporal default (matches the GDExtension DEFVAL).</summary>
    public const float DefaultBlendScale = 0.73974609375f;

    private static Dlss5Service _shared;
    private static readonly object SharedSync = new object();

    public static Dlss5Service Shared
    {
        get
        {
            if (_shared == null)
            {
                lock (SharedSync)
                {
                    _shared ??= new Dlss5Service();
                }
            }

            return _shared;
        }
    }

    private readonly object _sync = new object();
    private GodotObject _dlss;
    private bool _loaded;
    private string _provider = "";
    private double _lastMs;
    private string _lastError = "";
    private bool _disposed;

    public bool Loaded => _loaded;
    public string Provider => _provider;
    public double LastMs => _lastMs;
    public string LastError => _lastError;
    public bool Disposed => _disposed;

    /// <summary>
    /// Frees the native DLSS5/ORT session. Call only after the frame and
    /// bake workers have been joined (Dlss5Frame.Shutdown), so no thread
    /// is inside EnhanceTemporal/EnhanceStill when the native instance
    /// goes away. After this every entry point is a safe no-op (null /
    /// false) guarded by the disposed flag + lock; never throws.
    /// </summary>
    public void Shutdown()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                GodotObject dlss = _dlss;
                _dlss = null;
                _loaded = false;
                try
                {
                    if (dlss != null && GodotObject.IsInstanceValid(dlss))
                    {
                        try { dlss.Dispose(); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        try
        {
            lock (ZeroSync)
            {
                try { _zeroMotion?.Dispose(); } catch { }
                _zeroMotion = null;
            }
        }
        catch { }
    }

    /// <summary>True while the fp32 temporal graph is the loaded one.</summary>
    public bool IsTemporal => _loaded && string.Equals(_currentGraph, TemporalGraph(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when screenshot enhancement should run (opt-in).</summary>
    public static bool EnhanceScreenshotsEnabled =>
        System.Environment.GetEnvironmentVariable("GUO_DLSS_ENHANCE") == "1"
        || OS.GetCmdlineUserArgs().Contains("--dlss-enhance-screenshots");

    private static string Resolved(string relative)
    {
        return ProjectSettings.GlobalizePath(relative);
    }

    /// <summary>
    /// Loads a model. graphPath may be null (default: fp16 still graph).
    /// Safe to call repeatedly; reloads only when the graph path changes.
    /// Missing dll/model returns false with LastError; never throws.
    /// </summary>
    public bool EnsureLoaded(string graphPath = null)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }
            if (_loaded && (_currentGraph == graphPath))
            {
                if (_dlss == null || !GodotObject.IsInstanceValid(_dlss))
                {
                    _loaded = false;
                }
                else
                {
                    return true;
                }
            }

            _currentGraph = graphPath ?? DefaultStillGraph();
            _loaded = false;

            try
            {
                if (_dlss == null)
                {
                    var bridge = ResourceLoader.Load<GDScript>("res://addons/dlss5/dlss5_bridge.gd");
                    if (bridge == null)
                    {
                        _lastError = "addons/dlss5/dlss5_bridge.gd not found";
                        return false;
                    }

                    // The bridge only constructs the native object; every call
                    // below goes to the DLSS5 instance itself, so temporal
                    // history (has_history_/sequence_) survives across frames
                    // instead of resetting per call.
                    GodotObject made = null;
                    try
                    {
                        made = bridge.New().AsGodotObject()?.Call("make").AsGodotObject();
                    }
                    catch (Exception e)
                    {
                        _lastError = e.ToString();
                        return false;
                    }
                    if (made == null)
                    {
                        _lastError = "DLSS5 native class unavailable (GDExtension not loaded)";
                        return false;
                    }
                    _dlss = made;
                }

                if (_disposed || _dlss == null || !GodotObject.IsInstanceValid(_dlss))
                {
                    _dlss = null;
                    _loaded = false;
                    return false;
                }

                Variant rc = _dlss.Call("load_model",
                    Resolved(_currentGraph),
                    Resolved("res://addons/dlss5/models/dlss_tables.bin"),
                    Resolved("res://addons/dlss5/bin"));
                if (rc.AsInt32() != 0)
                {
                    try
                    {
                        if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                        {
                            _lastError = (string)(_dlss.Call("get_error").AsString());
                        }
                    }
                    catch { }
                    return false;
                }

                try
                {
                    if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                    {
                        _provider = (string)(_dlss.Call("get_provider").AsString());
                    }
                }
                catch { }
                _loaded = true;
                return true;
            }
            catch (Exception e)
            {
                _lastError = e.ToString();
                return false;
            }
        }
    }

    private string _currentGraph;

    private static string DefaultStillGraph()
    {
        return "res://addons/dlss5/models/dlssnr-core-320-fp16-vendorsm.onnx";
    }

    private static string TemporalGraph()
    {
        return "res://addons/dlss5/models/dlssnr-core-320-vendorsm.onnx";
    }

    /// <summary>
    /// Loads the fp32 temporal graph (default) for the frame hook. Reloads
    /// only when the graph path changes; a switch from another graph resets
    /// native history (it belongs to the previous graph). Missing dll/model
    /// returns false with LastError; never throws.
    /// </summary>
    public bool EnsureLoadedTemporal(string graphPath = null)
    {
        string want = graphPath ?? TemporalGraph();
        bool wasCurrent;
        lock (_sync)
        {
            wasCurrent = _loaded && string.Equals(_currentGraph, want, StringComparison.OrdinalIgnoreCase);
        }
        if (!EnsureLoaded(want))
        {
            return false;
        }
        if (!wasCurrent)
        {
            ResetTemporal();
        }
        return true;
    }

    /// <summary>
    /// Clears native temporal history (scene cuts, toggle flips). No-op
    /// until the first load. Never throws.
    /// </summary>
    public void ResetTemporal()
    {
        try
        {
            lock (_sync)
            {
                try
                {
                    if (_disposed || _dlss == null || !GodotObject.IsInstanceValid(_dlss))
                    {
                        return;
                    }
                    _dlss.Call("temporal_reset");
                }
                catch (Exception e)
                {
                    _lastError = e.ToString();
                }
            }
        }
        catch (Exception e)
        {
            _lastError = e.ToString();
        }
    }

    /// <summary>
    /// Enhances a 320x320 RGB8 image through the still path. Returns null
    /// (with LastError) on failure. Input is converted if needed.
    /// </summary>
    public Image EnhanceStill(Image image, float intensity = 1.0f, int profile = 0)
    {
        if (image == null || !GodotObject.IsInstanceValid(image))
        {
            _lastError = "EnhanceStill: null or freed image";
            return null;
        }
        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }
            if (!EnsureLoaded())
            {
                return null;
            }
            if (_disposed || _dlss == null || !GodotObject.IsInstanceValid(_dlss))
            {
                return null;
            }
            if (!GodotObject.IsInstanceValid(image))
            {
                _lastError = "EnhanceStill: image freed mid-call";
                return null;
            }

            try
            {
                var d = new Dictionary
                {
                    ["profile"] = profile,
                    ["intensity"] = intensity,
                    ["detail_strength"] = 1.0f,
                    ["colour_strength"] = 1.0f,
                    ["detail_radius"] = 4.0f,
                    ["frame_index"] = 0,
                };
                _dlss.Call("set_params", d);

                Variant outv = _dlss.Call("enhance", image);
                var result = outv.AsGodotObject() as Image;
                if (result == null)
                {
                    try
                    {
                        if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                        {
                            _lastError = (string)(_dlss.Call("get_error").AsString());
                        }
                    }
                    catch { }
                    return null;
                }

                try
                {
                    if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                    {
                        _lastMs = _dlss.Call("get_last_ms").AsDouble();
                    }
                }
                catch { }
                return result;
            }
            catch (Exception e)
            {
                _lastError = e.ToString();
                return null;
            }
        }
    }

    /// <summary>
    /// Enhances one 320x320 RGB8 tile through the temporal path, advancing
    /// native history. motionRgf must be 320x320 FORMAT_RGF, pixel motion
    /// current-&gt;previous (see <see cref="ZeroMotion320"/> for a static
    /// frame). Returns null (with LastError) on failure; the fp16 graph
    /// rejects temporal natively. Never throws.
    /// </summary>
    public Image EnhanceTemporal(Image image320, Image motionRgf, float intensity = 1.0f, int profile = 0, float blendScale = DefaultBlendScale)
    {
        if (image320 == null || motionRgf == null
            || !GodotObject.IsInstanceValid(image320) || !GodotObject.IsInstanceValid(motionRgf))
        {
            _lastError = "EnhanceTemporal: null or freed image/motion";
            return null;
        }

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return null;
                }
                if (!EnsureLoadedTemporal())
                {
                    return null;
                }
                if (_disposed || _dlss == null || !GodotObject.IsInstanceValid(_dlss))
                {
                    return null;
                }
                if (!GodotObject.IsInstanceValid(image320) || !GodotObject.IsInstanceValid(motionRgf))
                {
                    _lastError = "EnhanceTemporal: image freed mid-call";
                    return null;
                }

                var d = new Dictionary
                {
                    ["profile"] = profile,
                    ["intensity"] = intensity,
                    ["detail_strength"] = 1.0f,
                    ["colour_strength"] = 1.0f,
                    ["detail_radius"] = 4.0f,
                    ["frame_index"] = 0,
                };
                _dlss.Call("set_params", d);

                if (_disposed || _dlss == null || !GodotObject.IsInstanceValid(_dlss)
                    || !GodotObject.IsInstanceValid(image320) || !GodotObject.IsInstanceValid(motionRgf))
                {
                    _lastError = "EnhanceTemporal: freed during params";
                    return null;
                }

                Variant outv = _dlss.Call("enhance_temporal", image320, motionRgf, blendScale);
                var result = outv.AsGodotObject() as Image;
                if (result == null)
                {
                    try
                    {
                        if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                        {
                            _lastError = (string)(_dlss.Call("get_error").AsString());
                        }
                    }
                    catch (Exception e)
                    {
                        _lastError = e.ToString();
                    }
                    return null;
                }

                try
                {
                    if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                    {
                        _lastMs = _dlss.Call("get_last_ms").AsDouble();
                    }
                }
                catch { }
                try
                {
                    if (_dlss != null && GodotObject.IsInstanceValid(_dlss))
                    {
                        _provider = (string)(_dlss.Call("get_provider").AsString());
                    }
                }
                catch { }
                return result;
            }
        }
        catch (Exception e)
        {
            _lastError = e.ToString();
            return null;
        }
    }

    /// <summary>
    /// Frame-hook convenience: crops any viewport frame to the 320 tile and
    /// runs it through <see cref="EnhanceTemporal"/>. A null motion uses a
    /// shared zero-motion tile (static frame). Returns null (with LastError)
    /// on failure. Never throws.
    /// </summary>
    public Image EnhanceFrame(Image fullFrame, Image motionRgfOrNull = null, float intensity = 1.0f, int profile = 0, float blendScale = DefaultBlendScale)
    {
        if (fullFrame == null)
        {
            _lastError = "EnhanceFrame: null frame";
            return null;
        }

        Image square;
        try
        {
            square = PrepareSquare320(fullFrame);
        }
        catch (Exception e)
        {
            _lastError = e.ToString();
            return null;
        }
        if (square == null)
        {
            _lastError = "EnhanceFrame: crop failed";
            return null;
        }

        Image motion = motionRgfOrNull ?? ZeroMotion320();
        if (motion == null)
        {
            _lastError = "EnhanceFrame: motion unavailable";
            return null;
        }

        return EnhanceTemporal(square, motion, intensity, profile, blendScale);
    }

    private static Image _zeroMotion;
    private static readonly object ZeroSync = new object();

    /// <summary>
    /// Shared zero-motion tile (320x320 FORMAT_RGF, current-&gt;previous = 0)
    /// for static frames. Read-only after creation; the native path only
    /// reads it. Null when the image cannot be built. Never throws.
    /// </summary>
    public static Image ZeroMotion320()
    {
        lock (ZeroSync)
        {
            if (_zeroMotion != null && GodotObject.IsInstanceValid(_zeroMotion))
            {
                return _zeroMotion;
            }

            try
            {
                var zeros = new byte[WorkSize * WorkSize * 2 * sizeof(float)];
                Image made = Image.CreateFromData(WorkSize, WorkSize, false, Image.Format.Rgf, zeros);
                if (made == null)
                {
                    return null;
                }
                _zeroMotion = made;
                return _zeroMotion;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Center-crops a screenshot to a square and scales it to 320x320
    /// (the size the network runs at), ready for EnhanceStill.
    /// </summary>
    public static Image PrepareSquare320(Image source)
    {
        int w = source.GetWidth();
        int h = source.GetHeight();
        int side = Math.Min(w, h);
        var crop = source.GetRegion(new Rect2I((w - side) / 2, (h - side) / 2, side, side));
        crop.Resize(320, 320, Image.Interpolation.Bilinear);
        crop.Convert(Image.Format.Rgb8);
        return crop;
    }
}
