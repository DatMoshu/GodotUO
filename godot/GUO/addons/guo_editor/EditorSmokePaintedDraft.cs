#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private async Task CheckPaintedDraftAsync()
    {
        Directory.CreateDirectory(_out);
        const int width = 128, height = 64;
        string terrain = Path.Combine(_out, "inlet.terrain.png");
        string altitude = Path.Combine(_out, "inlet.altitude.png");
        using var colors = Image.CreateEmpty(width, height, false, Image.Format.Rgb8);
        using var heights = Image.CreateEmpty(width, height, false, Image.Format.L8);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool water = x < 6 || y < 6 || y > 57 || x > 121 || (x < 32 && y < 18);
            bool peak = !water && x > 83 && y > 36;
            colors.SetPixel(x,y, x == 56 && y == 24 ? Colors.Magenta : water ? Color.Color8(0,0,128)
                : peak ? Color.Color8(128,128,128) : x > 45 && x < 79 && y < 42
                ? Color.Color8(32,128,32) : Color.Color8(96,192,64));
            byte z = peak ? (byte)255 : water ? (byte)0 : (byte)58;
            heights.SetPixel(x,y, Color.Color8(z,z,z));
        }
        colors.SavePng(terrain);
        heights.SavePng(altitude);
        var view = GuoEditorPlugin.MapGenMain;
        EditorInterface.Singleton.SetMainScreenEditor(MapGenView.TabName);
        async Task Wait(Func<bool> ready, string action)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.Elapsed.TotalSeconds > 90) throw new IOException("Timed out waiting for " + action);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        await Wait(() => view.PassGroupCount > 0, "MapGen schema");
        Button ButtonWith(string label) => view.FindChildren("*","Button",true,false).OfType<Button>().Single(b => b.Text == label);
        void Press(Button b) => b.EmitSignal(BaseButton.SignalName.Pressed);
        var choose = ButtonWith("Use painted draft…");
        var generate = ButtonWith("Generate");
        var export = ButtonWith("Export map files");
        async Task Generate()
        {
            Press(generate);
            await Wait(() => !generate.Disabled, "Generate");
        }
        Press(choose);
        var dialog = view.GetChildren().OfType<FileDialog>().Single(d => d.Visible);
        dialog.CurrentPath = terrain;
        dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
        if (!view.BuildRunArgs("trial").Contains("--painted-draft")) throw new IOException("Picker did not select the pair");
        view.FindChildren("*","LineEdit",true,false).OfType<LineEdit>()
            .Single(l => l.TooltipText.StartsWith("Signed 64-bit")).Text = "528";
        await Generate();
        string first = view.LastRun ?? throw new IOException("Initial draft failed");
        var spins = view.FindChildren("*","SpinBox",true,false).OfType<SpinBox>().ToArray();
        spins.Single(s => s.TooltipText.Contains("Coast Terrace.TerraceBandWidth")).Value = 12;
        spins.Single(s => s.TooltipText.Contains("Forest Scatter.DensityMultiplier")).Value = 1.8;
        await Generate();
        string tuned = view.LastRun ?? throw new IOException("Tuned draft failed");
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(tuned,"run.json")));
        using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(first,"run.json")));
        if (run.RootElement.GetProperty("hash").GetString() == original.RootElement.GetProperty("hash").GetString())
            throw new IOException("Coast and scatter tuning did not affect the map");
        var log = view.FindChildren("*","RichTextLabel",true,false).OfType<RichTextLabel>().Single(l => l.Name == "Log");
        if (!log.GetParsedText().Contains("(56, 24)")) throw new IOException("Missing located palette warning");
        using var wrong = Image.CreateEmpty(width, height+8, false, Image.Format.L8);
        wrong.SavePng(altitude);
        await Generate();
        if (view.LastRun != null || !export.Disabled ||
            !view.FindChildren("*","Label",true,false).OfType<Label>().Any(l => l.Text.Contains("dimensions differ")))
            throw new IOException("Mismatched altitude did not produce a recoverable error");
        Press(ButtonWith("Show previous completed run"));
        if (view.LastRun != tuned) throw new IOException("Restored the wrong draft");
        Press(export);
        await Wait(() => !generate.Disabled, "verified export");
        var open = ButtonWith("Open in UO World");
        if (open.Disabled) throw new IOException("Could not export the retained pair. " + log.GetParsedText());
        Press(open);
        await Wait(() => _world.IsBooted && _world.Host.Project != null, "World project");
        string world = _world.Host.Project.Root;
        using var generated = JsonDocument.Parse(File.ReadAllText(Path.Combine(world,"generated.json")));
        if (generated.RootElement.GetProperty("hash").GetString() != run.RootElement.GetProperty("hash").GetString())
            throw new IOException("World opened a different result");
        var data = new WorldData(_world.Host);
        var witnesses = new[] { (X:18,Y:12), (X:103,Y:49) }.Select(c => new
            { x=c.X,y=c.Y, id=data.LandId(c.X,c.Y), z=data.LandZ(c.X,c.Y) }).ToArray();
        using var dump = new BinaryReader(File.OpenRead(Path.Combine(tuned,"map.bin")));
        foreach (var c in witnesses)
        {
            dump.BaseStream.Position = 8 + 4*(c.y*width+c.x);
            if (dump.ReadUInt16() != c.id || dump.ReadSByte() != c.z) throw new IOException($"World differs at {c.x},{c.y}");
        }
        if (witnesses[0].z >= witnesses[1].z) throw new IOException("SE high point lost");
        EditorInterface.Singleton.SetMainScreenEditor(MapGenView.TabName);
        var inspect = ButtonWith("Inspect draft warning");
        if (!inspect.Visible) throw new IOException("Missing warning action after export");
        Press(inspect);
        await Wait(() => _world.Host.X == 56 && _world.Host.Y == 24, "World at draft warning");
        heights.SavePng(altitude);
        _report["painted_draft"] = new { first, tuned, world, witnesses, mismatch_recovered=true,
            inspected_warning = new[] { _world.Host.X, _world.Host.Y } };
    }
}
#endif
