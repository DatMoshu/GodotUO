#if TOOLS
namespace GUO.Editor;

using System;
using System.Threading.Tasks;
using Godot;
using GUO.Game.UI.Gumps;

public partial class EditorTour
{
    private async Task JarJarClientUi()
    {
        (GetParent() as EditorPlugin)?.HideBottomPanel();
        EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        _world.GoTo(0, 1496, 1628); await Frames(30);
        Check(WorldUp, "embedded World booted for offline client control previews");
        if (!WorldUp) return;
        var world = _world.Host.World;
        var cases = new (string Title, Func<Gump> Create, string Caption)[]
        {
            ("Audio Zones: music", () => new MusicZoneGump(world), "In-game -audiozones: select/define a zone, playlist and fade, preview tracks, and generate from a prompt. These are real client controls rendered offline; login, walk-in/out and generation are not tested here."),
            ("Audio Zones: SFX", () => new MusicZoneGump(world, sfx: true), "In-game -sfxzone: the separate SFX layer uses the same zone form, with its own manifest and fades. This preview does not save a zone or submit a ComfyUI prompt."),
            ("Repaint", () => new RepaintGump(world), "In-game -repaint: target a static, choose the model/backend, tune the sampler, scheduler, steps and guidance, then review/save a themed replacement. No target or generation is simulated in this preview."),
            ("Splat Placer", () => new SplatPlacerGump(world), "In-game -splat: staged 3D models, target/selection, before and after images, prompt, generation and placement controls. Empty model lists accurately show the local setup state; no 3D result is fabricated."),
            ("Voice", () => new VoiceGump(world), "In-game -voice: target a mobile, choose or design a voice, assign it, test it, or set a default. This is the real empty-state dialog. TTS models and actual spoken playback still need separate verification."),
        };
        foreach (var entry in cases)
        {
            Gump gump = entry.Create();
            var canvas = new JarJarGumpPreview { Position = new Vector2(900, 420), Scale = new Vector2(1.5f, 1.5f) };
            EditorInterface.Singleton.GetBaseControl().AddChild(canvas);
            try
            {
                canvas.SetGump(gump);
                await Frames(12);
                Check(canvas.Commands > 0, entry.Title + " rendered actual client drawing commands");
                Check(gump.Width > 0 && gump.Height > 0, entry.Title + " has a nonempty layout");
                var bounds = new Rect2(new Vector2(900, 420), new Vector2(gump.Width, gump.Height) * 1.5f);
                Check(EditorInterface.Singleton.GetBaseControl().GetViewportRect().Encloses(bounds), entry.Title + " preview fits within the window below the caption");
                Say(entry.Caption, top: true, title: entry.Title + " (offline preview)");
                await Shot(8, 8); await Secs(8);
            }
            finally { canvas.QueueFree(); await Frames(3); }
        }
    }
}

/// <summary>Renders a real gump without registering input, network state or UIManager ownership.</summary>
[Tool]
public partial class JarJarGumpPreview : Node2D
{
    private Gump _gump;
    private Transform2D _previewTransform;
    private GUO.Renderer.UltimaBatcher2D _batch;
    private readonly GUO.Game.Scenes.RenderLists _lists = new();
    internal int Commands => _batch?.Commands ?? 0;
    internal void SetGump(Gump gump)
    {
        _previewTransform = Transform;
        _gump = gump; gump.X = 0; gump.Y = 0;
        _batch = new GUO.Renderer.UltimaBatcher2D(GetCanvasItem());
        // Read the loaded palette from the embedded client; do not create a second client or palette loader.
        var palette = typeof(GUO.GameController).GetField("_hueTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _batch.HueTexture = palette?.GetValue(GUO.Client.Game) as Texture2D;
    }
    public override void _Process(double delta)
    {
        if (_gump == null || _batch == null) return;
        _gump.Update(); _lists.Clear(); float depth = -10000;
        _gump.AddToRenderLists(_lists, 0, 0, ref depth);
        _batch.BeginFrame(); _batch.Begin(); _batch.PushUiTransform(_previewTransform);
        _lists.DrawRenderLists(_batch, sbyte.MaxValue); _batch.ClipEnd(); _batch.End();
    }
    public override void _ExitTree() { _gump?.Dispose(); _gump = null; _batch?.Dispose(); _batch = null; }
}
#endif
