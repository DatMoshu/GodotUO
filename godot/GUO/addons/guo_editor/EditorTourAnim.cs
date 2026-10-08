#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

/// <summary>S10 shown through the exchange folder: the sheet Pixelorama would open, a scripted save, the watcher.</summary>
public partial class EditorTour
{
    private async System.Threading.Tasks.Task AnimRoundtripSeg()
    {
        const int body = 0x0190;
        var anims = _assets.Panel<AnimationPanel>();
        await ShowTab(anims);
        // Action 0, direction 1: the clip exported below. The Animations segment leaves direction 3.
        var spins = All<SpinBox>(anims).ToList();
        if (spins.Count >= 2)
        {
            spins[0].Value = 0;
            spins[1].Value = 1;
        }

        _overlay.SetDetail(null);
        _overlay.ClearMarks();
        AssetOverlay assets = _data.Assets ?? _data.OpenAssets(_projectRoot);
        string savedExchange = System.Environment.GetEnvironmentVariable("UO_ART_EXCHANGE");
        System.Environment.SetEnvironmentVariable("UO_ART_EXCHANGE", Path.Combine(_out, "art_exchange"));
        try
        {
            Say("Animations go to Pixelorama and back. 'Edit in Pixelorama' writes the clip as a sheet with a sidecar: "
                + "every frame in a foot-aligned cell, with its original rectangle, centre and the clip's frame rate.");
            int? id = await Query(anims, "0x0190", 1);
            Check(id == body && InspectorHas("Animations", true), "body 0x0190 is inspected");
            MarkControl(All<Button>(_inspector).FirstOrDefault(b => b.Text == "Edit in Pixelorama"), "Edit in Pixelorama");
            await Shot(4);

            // What the button does, minus starting Pixelorama: the tour never opens another program.
            OverlayAnimationClip clip = anims.ExchangeClip(body, 0, 1);
            string png = ArtExchange.ExportAnimation(_data, body, clip);
            ArtSidecar side = ArtSidecar.FromJson(JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(png, ".json"))));
            JsonArray frames = side.Animation?["frames"]?.AsArray();
            Check(File.Exists(png) && frames?.Count == clip.Frames.Length, $"the clip is exported as a sheet of {clip.Frames.Length} cells");
            Image sheet = Image.LoadFromFile(png);
            _overlay.ClearMarks();
            _inspector.ShowInspection(Inspection.Still("Exchange", side.Stem, sheet,
                $"sheet  {sheet.GetWidth()}x{sheet.GetHeight()}, {(int)side.Animation["columns"]} columns\n"
                + $"frames {frames?.Count}, fps {(double)side.Animation["fps"]}\n"
                + "rects and centres fixed; edits stay inside each rect"));
            await Frames(6);
            Say("This is the sheet Pixelorama opens: its GUO menu turns the cells into timeline frames at the recorded rate. "
                + "Frame count, order, cell size and centres stay fixed for the round trip.");
            MarkControl(_inspector, "exchange sheet");
            await Shot(6);

            // The scripted save: what the GUO extension writes when the artist saves, here one green pixel per frame.
            JsonArray first = frames[0]["rect"].AsArray();
            int columns = (int)side.Animation["columns"];
            JsonArray cell = side.Animation["cell_size"].AsArray();
            for (int i = 0; i < frames.Count; i++)
            {
                JsonArray rect = frames[i]["rect"].AsArray();
                int x = i % columns * (int)cell[0] + (int)rect[0] + (int)rect[2] / 2;
                int y = i / columns * (int)cell[1] + (int)rect[1] + 1;
                sheet.SetPixel(x, y, Colors.Green);
            }

            side.Provenance.Tool = "pixelorama";
            string incoming = Path.Combine(ArtExchange.Sub("in"), Path.GetFileName(png));
            File.WriteAllText(Path.ChangeExtension(incoming, ".json"), side.ToJson().ToJsonString());
            sheet.SavePng(incoming);
            var outcomes = ArtExchange.Poll(_data);
            Check(outcomes.Any(o => o.Stem == side.Stem && o.Imported), "the watcher imports the saved sheet into the overlay");
            OverlayAnimationClip back = assets.LoadAnimation(body, 0, 1, out _);
            Check(back != null && back.Frames.Length == clip.Frames.Length && back.Fps == clip.Fps
                && back.Centers.SequenceEqual(clip.Centers), "frame count, frame rate and centres survive the round trip");
            Check((int)first[2] > 0 && back?.Frames[0].GetPixel((int)first[2] / 2, 1) == Colors.Green, "the edited pixel is in the imported frame");

            int? again = anims.Search("0x0190");
            await Frames(8);
            Check(again == body && _inspector.Current?.Text.Contains("editor overlay") == true, "the Animations panel now plays the overlay clip");
            Say("Saved in Pixelorama (scripted here), picked up by the watcher, and checked: same frames, centres and rate, "
                + "edits inside each frame. The panel now plays the overlay clip; the install is untouched.");
            MarkControl(_inspector, "overlay clip playing");
            await Burst(7, 0.125);

            assets.Revert(AssetKind.Animation, body);
            new AssetProvenance(assets).Remove(assets.RelativePathOf(AssetKind.Animation, body));
            anims.Search("0x0190");
            await Frames(8);
            Check(_inspector.Current?.Text.Contains("editor overlay") != true, "Revert brings the client's animation back");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("UO_ART_EXCHANGE", savedExchange);
            _overlay.ClearMarks();
        }
    }
}
#endif
