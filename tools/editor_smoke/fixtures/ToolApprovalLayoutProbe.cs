using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Editor;

public partial class ToolApprovalLayoutProbe : Node
{
    public override async void _Ready()
    {
        GetTree().Root.GuiEmbedSubwindows = true;
        var results = new List<object>();
        int failures = 0;
        string request = "world_patch_cells\n" + string.Join("\n", Enumerable.Repeat(
            "{\"cells\":[{\"x\":5187,\"y\":570,\"before\":{\"land\":{\"id\":580,\"z\":0}},\"after\":{\"land\":{\"id\":580,\"z\":-20}}}]}", 600))
            + "\n[b]literal untrusted markup[/b]\n" + new string('x', 4096);
        try
        {
            foreach (Vector2I viewport in new[] { new Vector2I(640, 480), new Vector2I(800, 600), new Vector2I(1280, 720) })
            foreach (float scale in new[] { 1f, 2f })
            {
                var host = new SubViewport { Size = viewport, GuiEmbedSubwindows = true };
                AddChild(host);
                await Frames(3);
                var dialog = AiToolApprovalDialog.Create(request);
                dialog.Theme = new Theme { DefaultFontSize = (int)(16 * scale) };
                host.AddChild(dialog);
                AiToolApprovalDialog.Popup(dialog, scale);
                await Frames(8);
                var details = dialog.GetNode<RichTextLabel>("RequestText");
                Rect2 bounds = new(Vector2.Zero, dialog.GetVisibleRect().Size);
                Rect2 allow = dialog.GetOkButton().GetGlobalRect();
                Rect2 refuse = dialog.GetCancelButton().GetGlobalRect();
                bool bounded = dialog.Size.X <= viewport.X * 0.81f && dialog.Size.Y <= viewport.Y * 0.81f;
                bool footerVisible = bounds.Encloses(allow) && bounds.Encloses(refuse)
                    && allow.Position.Y >= details.GetGlobalRect().End.Y;
                bool scrolls = details.GetVScrollBar().MaxValue > details.GetVScrollBar().Page && details.Size.Y >= 80 * scale;
                bool intact = details.Text == request && !details.BbcodeEnabled;
                bool ok = bounded && footerVisible && scrolls && intact;
                if (!ok) failures++;
                results.Add(new { viewport = viewport.ToString(), scale, bounded, footerVisible, scrolls, intact,
                    embedded = dialog.IsEmbedded(), available = host.GetVisibleRect().Size.ToString(),
                    dialog = dialog.Size.ToString(), allow = allow.ToString(), refuse = refuse.ToString(), ok });
                dialog.Hide(); dialog.QueueFree();
                await Frames(3);
                host.QueueFree();
            }
        }
        catch (Exception ex) { failures++; results.Add(new { error = ex.ToString() }); }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://report.json"), JsonSerializer.Serialize(
            new { failures, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"Tool approval layout: {results.Count} cases, {failures} failures");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
