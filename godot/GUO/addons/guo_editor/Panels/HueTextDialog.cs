#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;

/// <summary>Review the captured text candidate before replacing a working palette.</summary>
[Tool]
public partial class HueTextDialog : ConfirmationDialog
{
    internal HueTextReviewPlan Plan { get; private set; }
    internal OptionButton EncodingChoice { get; private set; }
    internal string Status => _status.Text;
    private Label _status;
    private VBoxContainer _preview;
    internal static HueTextDialog Open(HuePanel panel, HueDocument document, byte[] bytes, string filename, HueTextFormat format = HueTextFormat.Fiddler1252)
    {
        var dialog = new HueTextDialog { Title = $"Import palette text · hue 0x{document.Id:X4}", OkButtonText = "Import into working copy", DialogHideOnOk = false };
        dialog.Plan = new(document, bytes, format);
        var body = new VBoxContainer(); dialog.AddChild(body);
        body.AddChild(AssetUi.Label(Path.GetFileName(filename)));
        var row = new HBoxContainer(); body.AddChild(row); row.AddChild(new Label { Text = "Text encoding" });
        dialog.EncodingChoice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Hue text encoding" };
        dialog.EncodingChoice.AddItem("Fiddler · Windows-1252"); dialog.EncodingChoice.AddItem("UTF-8"); dialog.EncodingChoice.Selected = (int)format; row.AddChild(dialog.EncodingChoice);
        dialog._preview = new VBoxContainer(); body.AddChild(dialog._preview); dialog._status = AssetUi.Label(""); body.AddChild(dialog._status);
        body.AddChild(AssetUi.Label("Replaces this hue's name, both raw bounds and all 32 colors as one undo step. Save copy persists it. The installed palette stays unchanged."));
        void Refresh()
        {
            foreach (Node child in dialog._preview.GetChildren()) { dialog._preview.RemoveChild(child); child.QueueFree(); }
            HueTextEntry incoming = dialog.Plan.Candidate;
            if (incoming != null)
            {
                var names = new GridContainer { Columns = 2 }; dialog._preview.AddChild(names);
                names.AddChild(new Label { Text = "Current" }); names.AddChild(new LineEdit { Text = document.Name, Editable = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Current hue name" });
                names.AddChild(new Label { Text = "Incoming" }); names.AddChild(new LineEdit { Text = incoming.Name, Editable = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Incoming hue name" });
                dialog._preview.AddChild(AssetUi.Label($"Bounds: 0x{document.Start:X4}–0x{document.End:X4} → 0x{incoming.Start:X4}–0x{incoming.End:X4}"));
                var strips = new GridContainer { Columns = 2 }; dialog._preview.AddChild(strips);
                strips.AddChild(new Label { Text = "Current palette" }); strips.AddChild(new Label { Text = "Incoming palette" });
                strips.AddChild(Strip(document.Colors())); strips.AddChild(Strip(incoming.Colors));
            }
            dialog._status.Text = dialog.Plan.ReadinessError; dialog._status.Visible = dialog._status.Text.Length > 0;
            dialog.GetOkButton().Disabled = !dialog.Plan.CanApply || !panel.IsCurrentProject(document);
        }
        dialog.EncodingChoice.ItemSelected += index => { dialog.Plan.Choose((HueTextFormat)index); Refresh(); };
        dialog.Confirmed += () =>
        {
            try { if (!panel.IsCurrentProject(document)) throw new IOException("The selected world project changed. Reopen this import in the current project."); dialog.Plan.Apply(); dialog.Hide(); panel.OnAssetsChanged(); dialog.QueueFree(); }
            catch (Exception e) { dialog._status.Text = e.Message; dialog._status.Visible = true; dialog.GetOkButton().Disabled = true; }
        };
        dialog.Canceled += () => { dialog.Hide(); dialog.QueueFree(); }; panel.AddChild(dialog); Refresh();
        float scale = EditorInterface.Singleton.GetEditorScale(); dialog.PopupCenteredClamped((Vector2I)(new Vector2(650, 420) * scale), 0.9f); return dialog;
    }
    internal static TextureRect Strip(ushort[] colors)
    {
        using Image image = Image.CreateEmpty(32, 1, false, Image.Format.Rgba8);
        for (int i = 0; i < 32; i++) { uint c = GUO.Utility.HuesHelper.Color16To32(colors[i]); image.SetPixel(i, 0, Color.Color8((byte)c, (byte)(c >> 8), (byte)(c >> 16))); }
        return new TextureRect { Texture = ImageTexture.CreateFromImage(image), TextureFilter = CanvasItem.TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28) * EditorInterface.Singleton.GetEditorScale() };
    }
}
#endif
