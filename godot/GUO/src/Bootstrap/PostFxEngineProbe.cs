// GUO-owned: synthetic R1 regression checks, no shard or client art.
using System;
using System.Reflection;
using Godot;
using GUO.Renderer;
using GUO.Renderer.PostFx;

namespace GUO.Host;

public partial class PostFxEngineProbe : Node
{
    private int _failures;

    public override async void _Ready()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        PostFxStack.RunOverride = "off";
        using var shader = new Shader
        {
            Code = "shader_type canvas_item; uniform int taps : hint_range(3, 7, 1) = 4; void fragment() { COLOR = vec4(float(taps)); }",
        };
        PostFxLibrary.Register("r1-probe", shader);
        Check("integer round then clamp", () =>
        {
            Require(PostFxStack.Coerce(shader, "taps", -20f).AsInt32() == 3, "below minimum");
            Require(PostFxStack.Coerce(shader, "taps", 20f).AsInt32() == 7, "above maximum");
            Require(PostFxStack.Coerce(shader, "taps", 6.5f).AsInt32() == 6, "midpoint rounding changed");
        });
        Check("integer slider persists an integer", () =>
        {
            var stack = PostFxStack.Instance;
            stack.Use(Preset("r1-probe"), remember: false);
            using var menu = new PostFxMenu();
            var knob = typeof(PostFxMenu).GetMethod("Knob", BindingFlags.NonPublic | BindingFlags.Instance);
            using var row = (Control)knob.Invoke(menu, new object[] { 0, stack.Preset.Passes[0], PostFxUniforms.Find(shader, "taps") });
            var slider = row.GetChild<HSlider>(1);
            slider.EmitSignal(Godot.Range.SignalName.ValueChanged, 20.0);
            Require(stack.Preset.Passes[0].Params["taps"].GetValue<int>() == 7, "slider did not clamp/store int");
            Require(PostFxStack.ToVariant(stack.Preset.Passes[0].Params["taps"]).AsSingle() == 7f, "typed slider JSON cannot reach a live material");
            slider.EmitSignal(Godot.Range.SignalName.ValueChanged, -20.0);
            Require(stack.Preset.Passes[0].Params["taps"].GetValue<int>() == 3, "slider minimum");
            row.Free();
            menu.Free();
        });

        var fx = new PostFxStack();
        using var world = new RenderTarget2D(this, 16, 16);
        fx.EnsureLoaded();
        Check("Classic texture identity", () => Require(fx.Process(world, null) == world.Texture, "Classic changed texture"));
        fx.Use(Preset("r1-probe"), remember: false);
        Check("rebuild frame uses world", () =>
        {
            Require(fx.Process(world, null) == world.Texture, "new viewport exposed before rendering");
            Require(fx.Process(world, null) == world.Texture, "second call in build frame exposed viewport");
        });
        Check("live material accepts integer slider JSON", () =>
        {
            fx.SetParam(0, "taps", System.Text.Json.Nodes.JsonValue.Create(7));
            Require(fx.Preset.Passes[0].Params["taps"].GetValue<int>() == 7, "live parameter lost int");
        });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("next frame uses processed output", () => Require(fx.Process(world, null) != world.Texture, "effect never became active"));
        Check("toggle rebuild uses world", () =>
        {
            fx.Rebuild();
            Require(fx.Process(world, null) == world.Texture, "toggle exposed new viewport");
        });
        using var resized = new RenderTarget2D(this, 24, 20);
        Check("resize uses new world", () => Require(fx.Process(resized, null) == resized.Texture, "resize exposed stale output"));
        fx.Use(Preset("r1-missing-shader"), remember: false);
        Check("missing shader uses world", () => Require(fx.Process(world, null) == world.Texture, "missing shader output"));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("missing shader stays on world", () => Require(fx.Process(world, null) == world.Texture, "empty chain exposed next frame"));
        fx.Use(PostFxPreset.Classic(), remember: false);
        Check("off after active keeps identity", () => Require(fx.Process(world, null) == world.Texture, "off changed texture"));
        Check("disposed target is not dereferenced", () =>
        {
            resized.Dispose();
            Require(fx.Process(resized, null) == null, "disposed target returned");
        });
        Check("unparented world uses world texture", () =>
        {
            using var parent = new Node();
            using var detached = new RenderTarget2D(parent, 8, 8);
            var viewport = parent.GetChild(0);
            parent.RemoveChild(viewport);
            fx.Use(Preset("r1-probe"), remember: false);
            Require(fx.Process(detached, null) == detached.Texture, "no-host build did not fall back");
            detached.Dispose();
            parent.Free();
        });
        Check("freed viewport rebuild uses world", () =>
        {
            fx.Rebuild();
            fx.Process(world, null);
            var field = typeof(PostFxStack).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance);
            ((SubViewport)field.GetValue(fx)).Free();
            Require(fx.Process(world, null) == world.Texture, "freed viewport not recovered");
        });
        fx.Use(PostFxPreset.Classic(), remember: false);
        Check("cleanup after viewport recovery", () => fx.Process(world, null));
        PostFxStack.Instance.Use(PostFxPreset.Classic(), remember: false);
        GD.Print($"[postfx engine] {(_failures == 0 ? "PASS" : "FAIL")}: {_failures} failure(s); lifecycle/selection only, no rendered-pixel claim");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static PostFxPreset Preset(string shader) => new() { Name = "R1 synthetic", Passes = { new PostFxPass { Shader = shader } } };
    private void Check(string name, Action test)
    {
        try { test(); GD.Print("[postfx engine] PASS " + name); }
        catch (Exception e) { _failures++; GD.PrintErr("[postfx engine] FAIL " + name + ": " + e); }
    }
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
