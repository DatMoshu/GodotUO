// GUO addition: AndroidX WindowManager through Godot's Java bridge.
using System;
using System.Collections.Concurrent;
using Godot;

namespace GUO.Platform.Android;

internal sealed class FoldObserver : IDisposable
{
    internal readonly record struct Feature(LayoutRect Bounds, bool Separating, bool HalfOpen);
    private readonly ConcurrentQueue<Feature> _updates = new();
    private GodotObject _adapter, _consumer, _activity;
    private Callable _callback, _start;
    public string Status { get; private set; } = "Window size fallback";

    public void Start()
    {
        if (!OperatingSystem.IsAndroid() || !Engine.HasSingleton("AndroidRuntime")) return;
        var runtime = Engine.GetSingleton("AndroidRuntime");
        _activity = runtime.Call("getActivity").AsGodotObject();
        _start = Callable.From(StartOnUiThread);
        _activity.Call("runOnUiThread", runtime.Call("createRunnableFromGodotCallable", _start));
    }

    private void StartOnUiThread()
    {
        try
        {
            var trackerClass = JavaClassWrapper.Wrap("androidx.window.layout.WindowInfoTracker");
            if (trackerClass == null) { Status = "AndroidX unavailable; window size fallback"; return; }
            var tracker = trackerClass.Call("getOrCreate", _activity).AsGodotObject();
            var adapterClass = JavaClassWrapper.Wrap("androidx.window.java.layout.WindowInfoTrackerCallbackAdapter");
            _adapter = adapterClass.Call("WindowInfoTrackerCallbackAdapter", tracker).AsGodotObject();
            _callback = Callable.From<GodotObject>(OnLayout);
            _consumer = JavaClassWrapper.CreateSamCallback("androidx.core.util.Consumer", _callback);
            if (_adapter == null || _consumer == null) throw new InvalidOperationException("No WindowManager callback");
            var executor = _activity.Call("getMainExecutor").AsGodotObject();
            _adapter.Call("addWindowLayoutInfoListener", _activity, executor, _consumer);
            Status = "AndroidX posture listener active";
        }
        catch (Exception e) { Status = "Window size fallback: " + e.Message; }
        GD.Print("[GUO] posture: " + Status);
    }

    private void OnLayout(GodotObject info)
    {
        try
        {
            var features = info.Call("getDisplayFeatures").AsGodotObject();
            int n = features.Call("size").AsInt32();
            Feature result = default;
            for (int i = 0; i < n; i++)
            {
                var feature = features.Call("get", i).AsGodotObject();
                string name = feature.Call("getClass").AsGodotObject().Call("getName").AsString();
                if (!name.Contains("FoldingFeature")) continue;
                var bounds = feature.Call("getBounds").AsGodotObject();
                int w = bounds.Call("width").AsInt32(), h = bounds.Call("height").AsInt32();
                int x = bounds.Call("centerX").AsInt32() - w / 2;
                int y = bounds.Call("centerY").AsInt32() - h / 2;
                bool half = feature.Call("getState").AsGodotObject().Call("toString").AsString().Contains("HALF_OPENED");
                bool full = feature.Call("getOcclusionType").AsGodotObject().Call("toString").AsString().Contains("FULL");
                result = new(new(x, y, w, h), feature.Call("isSeparating").AsBool() || full, half);
                break;
            }
            _updates.Enqueue(result); // UI thread never touches client state.
        }
        catch (Exception e) { GD.PrintErr("[GUO] posture update: " + e.Message); }
    }

    public bool Poll(out Feature feature)
    {
        bool changed = false;
        feature = default;
        while (_updates.TryDequeue(out Feature next)) { feature = next; changed = true; }
        return changed;
    }

    public void Dispose()
    {
        if (_adapter != null && _consumer != null)
            _adapter.Call("removeWindowLayoutInfoListener", _consumer);
        _adapter = _consumer = null;
    }
}
