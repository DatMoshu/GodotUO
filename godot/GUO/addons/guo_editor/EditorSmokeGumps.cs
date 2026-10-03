#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.UI.Authoring;
using Classic = GUO.Game.UI.Controls;

public partial class EditorSmoke
{
    private Action _restoreGumpVisual;
    private Window _gumpProofWindow;
    private Node _gumpProofParent;
    private void StartGumpVisual()
    {
        var studio = GuoEditorPlugin.GumpsMain;
        _restoreGumpVisual = studio.ShowVisualProof(ClientGumpLibrary.Preview("StatusGumpOld", studio.PreviewWorld()));
        _gumpProofParent = studio.GetParent();
        _gumpProofWindow = new Window { Title = "Gump Studio layout verification", Size = new Vector2I(1600, 1000), Unfocusable = true, Theme = EditorInterface.Singleton.GetEditorTheme() };
        AddChild(_gumpProofWindow);
        studio.Reparent(_gumpProofWindow);
        studio.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        studio.Visible = true;
        studio.GumpAssets.Panel<GumpPanel>().Search("status");
        _gumpProofWindow.PopupCentered();
    }

    private void CaptureGumpVisual(string size)
    {
        var studio = GuoEditorPlugin.GumpsMain;
        if (!studio.InspectorFits) _failures.Add("Gump Studio: properties overflow at " + size);
        if (!studio.GumpAssets.IsVisibleInTree() || studio.GumpAssets.Size.Y < 100) _failures.Add("Gump Studio: asset shelf hidden at " + size);
        if (studio.Size.Y > _gumpProofWindow.Size.Y + 2 || studio.GumpAssets.GetGlobalRect().End.Y > _gumpProofWindow.Size.Y + 2)
            _failures.Add("Gump Studio: workspace extends below viewport at " + size);
        using var image = _gumpProofWindow.GetTexture().GetImage();
        image.SavePng(Path.Combine(_out, $"gump_studio_{size}{Suffix}.png"));
        _report["gump_layout_" + size] = new { inspector_fits = studio.InspectorFits, viewport = _gumpProofWindow.Size.ToString(), studio_size = studio.Size.ToString(), inspector = studio.PropertiesScroll.GetGlobalRect().ToString(), assets_height = studio.GumpAssets.Size.Y };
    }
    private void FinishGumpVisual()
    {
        GuoEditorPlugin.GumpsMain.Reparent(_gumpProofParent);
        _restoreGumpVisual?.Invoke(); _restoreGumpVisual = null;
        _gumpProofWindow.QueueFree(); _gumpProofWindow = null;
    }
    private sealed class CaptureControl : Classic.Control { }

    private void CheckGumpStudio()
    {
        int checks = 0;
        void Check(string name, bool ok)
        {
            checks++;
            if (!ok) throw new InvalidOperationException(name);
        }
        AuthoredGumpView view = null;
        GumpCanvas canvas = null;
        try
        {
            var d = ClassicGumpCodec.Import("{ noclose }{ page 0 }{ resizepic 0 0 5054 400 300 }{ text 20 20 0 0 }{ page 1 }{ group 7 }{ radio 10 60 210 211 1 42 }{ textentry 20 90 200 30 0 8 1 }{ button 20 140 247 248 0 2 0 }{ page 2 }{ checkbox 20 70 210 211 1 77 }{ button 20 140 247 248 1 0 9 }{ xmfhtmlgump 1 2 3 4 500000 0 1 }", new[] { "Welcome", "Ada" });
            Check("classic element count", d.Elements.Count == 9);
            var bundle = ClassicGumpCodec.Export(d);
            Check("unknown preserved", bundle.Layout.Contains("xmfhtmlgump 1 2 3 4 500000 0 1") && bundle.Layout.Contains("noclose"));
            var again = ClassicGumpCodec.Import(bundle.Layout, bundle.Texts);
            Check("text table round trip", again.Elements.Single(e => e.Kind == GumpElementKind.TextEntry).Text == "Ada");
            Check("page and group round trip", again.Elements.Single(e => e.Kind == GumpElementKind.Radio).Group == 7 && again.Elements.Last().Page == 2);
            Check("JSON round trip", GumpDocument.Parse(d.ToJson()).ToJson() == d.ToJson());
            bool invalid = false;
            try { ClassicGumpCodec.Import("{ text 1 2 0 0", Array.Empty<string>()); } catch (InvalidDataException) { invalid = true; }
            Check("unclosed command rejected", invalid);
            var malformed = ClassicGumpCodec.Import("{ button bad data }", Array.Empty<string>());
            Check("malformed command retained", malformed.Elements[0].Kind == GumpElementKind.Raw && ClassicGumpCodec.Export(malformed).Layout.Contains("button bad data"));
            var history = new GumpHistory(); history.Push(d); d.Name = "Changed";
            d = history.Undo(d); Check("undo", d.Name == "Imported classic gump");
            d = history.Redo(d); Check("redo", d.Name == "Changed");
            var bad = d.Clone(); bad.Elements[1].Id = bad.Elements[0].Id; invalid = false;
            try { bad.Validate(); } catch (InvalidDataException) { invalid = true; }
            Check("duplicate identities rejected", invalid);
            view = new AuthoredGumpView(); AddChild(view); view.Build(d);
            Check("shared page and inactive page", view.Elements[d.Elements[1].Id].Visible && !view.Elements[d.Elements[^2].Id].Visible);
            GumpReply received = null; view.Reply += r => received = r;
            view.Activate(d.Elements.Single(e => e.Kind == GumpElementKind.Button && e.PageButton));
            Check("page button does not reply", view.ActivePage == 2 && received == null);
            var entry = d.Elements.Single(e => e.Kind == GumpElementKind.TextEntry);
            ((LineEdit)view.Elements[entry.Id]).Text = "Edited";
            view.Activate(d.Elements.Single(e => e.Kind == GumpElementKind.Button && !e.PageButton));
            Check("reply ID", received.ButtonId == 9);
            Check("switches across all pages", received.Switches.SequenceEqual(new uint[] { 42, 77 }));
            Check("text entry across all pages", received.Entries.Single().Item1 == 8 && received.Entries.Single().Item2 == "Edited");
            var radioDoc = new GumpDocument { Modern = true };
            radioDoc.Elements.Add(new GumpElement { Kind = GumpElementKind.Radio, Group = 1, ReplyId = 3 });
            radioDoc.Elements.Add(new GumpElement { Kind = GumpElementKind.Radio, Group = 1, ReplyId = 4 });
            view.Build(radioDoc);
            ((CheckBox)view.Elements[radioDoc.Elements[0].Id]).ButtonPressed = true;
            ((CheckBox)view.Elements[radioDoc.Elements[1].Id]).ButtonPressed = true;
            Check("radio exclusivity", view.CollectReply(1).Switches.SequenceEqual(new uint[] { 4 }));
            var anchor = new GumpElement { Kind = GumpElementKind.Button, Anchor = "BottomRight", X = 400, Y = 400, Width = 80, Height = 40 };
            radioDoc.Elements.Add(anchor); view.Build(radioDoc); view.Size += new Vector2(100, 100);
            Check("responsive anchor", view.Elements[anchor.Id].Position == new Vector2(500, 500));
            anchor.Binding = "player.name";
            view.ApplyBindings(new System.Collections.Generic.Dictionary<string, Variant> { ["player.name"] = "Avatar" });
            Check("display bindings", ((Button)view.Elements[anchor.Id]).Text == "Avatar");
            string scenePath = Path.Combine(_out, "authored_gump.tscn");
            File.WriteAllText(scenePath, GumpStudio.SceneText(radioDoc));
            using var packed = ResourceLoader.Load<PackedScene>(scenePath, cacheMode: ResourceLoader.CacheMode.Ignore);
            var exported = packed.Instantiate<AuthoredGumpView>(); AddChild(exported);
            Check("exported scene loads and builds", exported.Elements.Count == 3 && exported.DocumentJson == radioDoc.ToJson());
            exported.QueueFree();
            Check("extended commands retained", ClassicGumpCodec.Export(ClassicGumpCodec.Import("{ gumppic 1 2 100 hue=42 }", Array.Empty<string>())).Layout.Contains("hue=42"));
            var root = new GUO.Game.UI.Gumps.Gump(null, 0, 0);
            var parent = new CaptureControl { X = 10, Y = 20, Width = 100, Height = 100 };
            var child = new CaptureControl { X = 5, Y = 6, Width = 30, Height = 40 };
            root.Add(parent); parent.Add(child);
            var captured = GumpCapture.Capture(root);
            Check("nested capture coordinates", captured.Elements[1].X == 15 && captured.Elements[1].Y == 26);
            captured.Elements[0].X += 8; captured.Elements[1].X += 8;
            GumpCapture.Apply(captured, root);
            Check("apply preserves relative coordinates", parent.X == 18 && child.X == 5);
            captured.Elements[1].SourceType = "WrongType"; captured.Elements[0].X = 900; invalid = false;
            try { GumpCapture.Apply(captured, root); } catch (InvalidDataException) { invalid = true; }
            Check("mismatch cannot partially apply", invalid && parent.X == 18);
            Check("workspace created", GuoEditorPlugin.GumpsMain?.Canvas != null);
            Check("embedded assets defaults to gumps", GuoEditorPlugin.GumpsMain.GumpAssets?.Panels.Count == 1 && GuoEditorPlugin.GumpsMain.GumpAssets.Panel<GumpPanel>() != null);
            var artBrowser = GuoEditorPlugin.GumpsMain.GumpAssets.Panel<GumpPanel>();
            Check("art picker finds names", artBrowser.Search("paperdoll") != null);
            Check("art picker finds IDs", artBrowser.Search("0x0064") == 100);
            var previewCounts = new System.Collections.Generic.Dictionary<string, int>();
            var previewWorld = GuoEditorPlugin.GumpsMain.PreviewWorld();
            foreach (string type in ClientGumpLibrary.Factories.Keys)
            {
                var preview = ClientGumpLibrary.Preview(type, previewWorld);
                Check("client preview " + type, preview.Elements.Count > 0 && preview.Width > 16 && preview.Height > 16);
                previewCounts[type] = preview.Elements.Count;
            }
            _report["client_gump_previews"] = previewCounts;
            var studio = GuoEditorPlugin.GumpsMain;
            var restore = studio.ShowVisualProof(new GumpDocument());
            try
            {
                studio.ClientGumpDialog();
                var dialog = studio.GetChildren().OfType<AcceptDialog>().Last();
                var libraryList = dialog.FindChildren("*", "ItemList", true, false).OfType<ItemList>().Single();
                int statusIndex = Enumerable.Range(0, libraryList.ItemCount).First(i => libraryList.GetItemText(i).StartsWith("StatusGumpOld ", StringComparison.Ordinal));
                libraryList.EmitSignal(ItemList.SignalName.ItemActivated, statusIndex);
                Check("client library double click loads document", studio.Document.SourceGump.EndsWith("StatusGumpOld", StringComparison.Ordinal) && studio.Document.Elements.Count == previewCounts["StatusGumpOld"]);
                int beforeArt = studio.Document.Elements.Count;
                artBrowser.Search("0x0064");
                var artList = artBrowser.FindChildren("*", "ItemList", true, false).OfType<ItemList>().Single();
                artList.EmitSignal(ItemList.SignalName.ItemActivated, artList.GetSelectedItems().Single());
                Check("asset double click inserts art", studio.Document.Elements.Count == beforeArt + 1 && studio.Document.Elements[^1].Graphic == 100);
            }
            finally { restore(); }
            var canvasDoc = new GumpDocument();
            canvasDoc.Elements.Add(new GumpElement { Kind = GumpElementKind.Label, X = 16, Y = 16, Text = "Drag" });
            canvas = new GumpCanvas { Document = canvasDoc }; AddChild(canvas); canvas.Rebuild(null, false);
            int edits = 0; canvas.BeginEdit += () => edits++;
            var overlay = (Control)canvas.GetChild(canvas.GetChildCount() - 1);
            overlay.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(20, 20) });
            overlay.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = new Vector2(39, 36) });
            overlay.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
            Check("drag snaps with one undo transaction", canvasDoc.Elements[0].X == 32 && canvasDoc.Elements[0].Y == 32 && edits == 1);
            File.WriteAllText(Path.Combine(_out, "gump_roundtrip.json"), again.ToJson());
            _report["gump_studio"] = new { ok = true, checks };
            GD.Print($"[GUO editor] Gump Studio: {checks} checks passed");
        }
        catch (Exception ex)
        {
            _report["gump_studio"] = new { ok = false, checks, error = ex.ToString() };
            _failures.Add("Gump Studio: " + ex.Message);
        }
        finally { view?.QueueFree(); canvas?.QueueFree(); }
    }
}
#endif
